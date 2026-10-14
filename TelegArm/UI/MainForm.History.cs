using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── Message view ─────────────────────────────────────────────────────

        /// <summary>COMMENTS-THREAD source-swap: in thread mode page via GetReplies(GroupPeer, GroupRootId) — the linked
        /// discussion group + the thread root's GROUP-side id, which scopes to the ONE post's comments; else the normal
        /// GetHistory. Identical offset-id semantics, so the island pager (initial / older / newer) is reused unchanged.</summary>
        private System.Threading.Tasks.Task<Messages_MessagesBase> LoadWindowAsync(ChatEntry entry, int limit, int offsetId, int addOffset)
            => _thread != null
                ? _service.GetRepliesAsync(_thread.GroupPeer, _thread.GroupRootId, limit, offsetId, addOffset)
                : _service.GetHistoryAsync(entry.Peer, limit, offsetId, addOffset);

        private async System.Threading.Tasks.Task LoadHistoryAsync(ChatEntry entry, int focusMessageId = 0)
        {
            _chatTitle.Text = entry.Title;
            SetHeaderAvatar(entry);   // BORDERLESS-CAPTION: peer photo in the accent header
            MarkMentionsReactionsRead(entry);   // MENTION-REACTION: opening = seen → clear @ badge + heart glyph
            CancelReply();                 // a pending reply belongs to the chat we're leaving
            CancelTyping();                // stop any "typing…" we were showing in the chat we're leaving (QUICKWINS-1)
            if (_selectionMode) ExitSelectionMode();   // selection belongs to the old chat too
            if (_voiceState != VoiceState.None) AbortVoice();
            _readOutboxMaxId = entry.ReadOutboxMaxId;   // so outgoing bubbles render ✓✓ correctly
            _shownMessageIds.Clear();
            _albumBubbles.Clear();   // album grouping is per-open-chat
            _currentChatMessages.Clear();
            _repliesSourceCache.Clear();   // REPLIES-INBOX: source groups are per-open (rebuilt from the history dict)
            _groupAdminRoles = null;   // CHANNEL-META-EXTRAS: admin-role cache is per-open (refilled below for megagroups)
            _oldestMessageId = 0;
            _newestMessageId = 0;
            _hasMoreHistory = true;
            _loadingOlder = false;
            _loadingNewer = false;
            _atLiveTail = true;
            _sponsoredCard = null;   // card belongs to the chat we're leaving (panel is cleared below)

            _messagePanel.SuspendLayout();
            ClearMessagePanel();
            _messagePanel.ResumeLayout();

            try
            {
                // When focusing a specific message, load a window centred on it.
                var history = focusMessageId > 0
                    ? await LoadWindowAsync(entry, 50, focusMessageId, -25)
                    : await LoadWindowAsync(entry, 50, 0, 0);
                if (_selectedChat != entry) return; // selection changed while loading

                var ordered = history.Messages.OrderBy(MsgDate).ToList();   // include service events, chronological

                _messagePanel.SuspendLayout();
                foreach (var mb in ordered)
                {
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fu) _peerNames[fu.id] = DisplayName(fu);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        AddMessageBubble(entry, m, ResolveSender(entry, m, from), from);
                        _currentChatMessages.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        CacheServiceNames(history.UserOrChat, svc);
                        AddMessageBubble(entry, svc, null, null);
                    }
                }
                _messagePanel.ResumeLayout();

                // REPLIES-INBOX: decorate each reply entry with its SOURCE discussion + a "View in chat" affordance
                // (repurposes the reply-quote band). Uses the history dict (reliable) to resolve the source group.
                if (entry.PeerId == RepliesPeerId)
                    foreach (var b in _messagePanel.Controls.OfType<MessageBubbleControl>())
                    {
                        var msg = _currentChatMessages.FirstOrDefault(x => x.ID == b.MessageId);
                        if (msg != null) { IngestRepliesSource(msg, history); DecorateRepliesBubble(b, msg); }
                    }

                UpdateAudioPlaylist();

                if (ordered.Count > 0)
                {
                    _oldestMessageId = ordered[0].ID;
                    _newestMessageId = ordered.Max(mb => mb.ID);
                    // A focused jump loads an island; we're only at the live tail if it reached the chat's
                    // latest message. A plain bottom-load (focusMessageId==0) is always the tail. When the
                    // chat's latest id is unknown (TopMessageId==0), treat a focused load as an island so
                    // downward paging runs (it stops itself on a short page).
                    _atLiveTail = focusMessageId == 0
                                  || (entry.TopMessageId > 0 && _newestMessageId >= entry.TopMessageId);
                    // Open at the FIRST UNREAD message (not the bottom) and DON'T blanket-read on open:
                    // reading happens progressively when the user actually reaches the bottom (MarkCaughtUp).
                    bool openUnread = focusMessageId == 0 && entry.UnreadCount > 0 && entry.ReadInboxMaxId > 0;
                    if (focusMessageId > 0) ScrollToMessage(focusMessageId);
                    else if (openUnread && ScrollToFirstUnread(entry)) _jumpUnread = entry.UnreadCount;
                    else ScrollMessagesToBottom();
                    OnScrollPositionChanged();   // if we ended at the bottom (nothing unread) → read; else show the button
                }
                else
                {
                    _hasMoreHistory = false;
                }

                LoadSponsoredAsync(entry);   // Telegram ads (channels) — required for API ToS compliance
                LoadGroupAdminRolesAsync(entry);   // CHANNEL-META-EXTRAS (3): megagroup admin roles (async; re-applies)
            }
            catch (Exception ex)
            {
                AddBubble("Failed to load history: " + ex.Message, null, false, DateTime.UtcNow);
            }
        }

        // ── Sponsored messages (Telegram ads — required for API ToS 3.3 compliance) ──
        // Channel sponsored messages: fetched per open channel (5-min cache), rendered as ONE distinct
        // card BELOW the last post (NOT history — excluded from paging/read-state/jump/reply), with the
        // mandatory view-on-display + click-on-interaction telemetry and the report flow.

        private readonly Dictionary<long, (DateTime at, Messages_SponsoredMessages res)> _sponsoredCache
            = new Dictionary<long, (DateTime, Messages_SponsoredMessages)>();
        private SponsoredCardControl _sponsoredCard;        // the appended card (null = none)
        private SponsoredMessage _sponsoredMsg;             // the current sponsored message
        private Messages_SponsoredMessages _sponsoredRes;   // holds chats/users for sponsor name/photo
        private readonly HashSet<string> _sponsoredViewed = new HashSet<string>();   // random_id hex → view sent

        /// <summary>Fetches a channel's sponsored message (5-min cache) and renders the card below the
        /// last post. Channel-only; empty results (region/Premium/account-dependent) show nothing.</summary>
        private async void LoadSponsoredAsync(ChatEntry entry)
        {
            _sponsoredCard = null;
            _sponsoredMsg = null;
            _sponsoredRes = null;
            if (entry?.Peer == null || !(entry.Peer is InputPeerChannel)) return;   // ads only in channels

            long key = entry.PeerId;
            Messages_SponsoredMessages res = null;
            if (_sponsoredCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.at) < TimeSpan.FromMinutes(5))
                res = cached.res;                                  // reuse within 5 minutes (spec-mandated cache)
            else
            {
                try { res = await _service.GetSponsoredMessagesAsync(entry.Peer); }
                catch { return; }                                  // no ad / not allowed → silent
                if (res != null) _sponsoredCache[key] = (DateTime.UtcNow, res);
            }
            if (_selectedChat != entry) return;                    // chat changed while awaiting
            if (res?.messages == null || res.messages.Length == 0) return;   // empty = no card, no error

            _sponsoredRes = res;
            _sponsoredMsg = res.messages[0];   // one card below the last post (posts_between insertion is out of scope)
            BuildSponsoredCard(entry, res, _sponsoredMsg);
        }

        /// <summary>Builds the sponsored card, appends it as the LAST panel item, and lazily loads its art.</summary>
        private void BuildSponsoredCard(ChatEntry entry, Messages_SponsoredMessages res, SponsoredMessage sm)
        {
            bool recommended = (sm.flags & SponsoredMessage.Flags.recommended) != 0;
            bool hasMedia = (sm.flags & SponsoredMessage.Flags.has_media) != 0 && sm.media != null;

            var card = new SponsoredCardControl(recommended ? "Recommended" : "Sponsored", sm.title, sm.message,
                sm.entities, sm.button_text, hasMedia, ResolveCustomEmoji)
            {
                LayoutDpi = Dpi.Of(_messagePanel),   // step 3.2: measured below, BEFORE it is added — at the window's DPI
                AccentColor = SponsoredAccent(sm),
                IsDark = _dark,
                Width = ContentWidth(_messagePanel)
            };
            card.LinkClicked += url => OnSponsoredLink(sm, url);
            card.ButtonClicked += () => OnSponsoredOpen(sm, false);
            card.MediaClicked += () => OnSponsoredOpen(sm, true);
            card.SponsorClicked += () => OnSponsoredOpen(sm, false);
            card.MenuRequested += pt => ShowSponsoredMenu(sm, pt);
            card.Measure();

            _sponsoredCard = card;
            _messagePanel.Controls.Add(card);
            KeepSponsoredLast();

            if ((sm.flags & SponsoredMessage.Flags.has_photo) != 0 && sm.photo is Photo ph)
                LoadSponsoredImage(card, ph, false);
            if (hasMedia && sm.media is MessageMediaPhoto mmp && mmp.photo is Photo mph)
                LoadSponsoredImage(card, mph, true);   // photo media → thumbnail (doc/video media shows a placeholder)

            OnScrollPositionChanged();   // re-check the view trigger now the card is present
        }

        /// <summary>Maps a sponsored PeerColor base index to an accent; falls back to the theme accent.</summary>
        private Color SponsoredAccent(SponsoredMessage sm)
        {
            if ((sm.flags & SponsoredMessage.Flags.has_color) != 0 && sm.color is PeerColor pc)
                switch (pc.color)   // base palette 0..6
                {
                    case 0: return Color.FromArgb(229, 57, 53);
                    case 1: return Color.FromArgb(245, 124, 0);
                    case 2: return Color.FromArgb(142, 36, 170);
                    case 3: return Color.FromArgb(67, 160, 71);
                    case 4: return Color.FromArgb(0, 172, 193);
                    case 5: return Color.FromArgb(30, 136, 229);
                    case 6: return Color.FromArgb(216, 27, 96);
                }
            return _accent;
        }

        private async void LoadSponsoredImage(SponsoredCardControl card, Photo photo, bool asMedia)
        {
            try
            {
                var bytes = await _service.DownloadPhotoThumbAsync(photo);
                if (bytes == null || bytes.Length == 0 || card.IsDisposed) return;
                var img = await ToBitmapAsync(bytes);
                if (asMedia) card.SetMediaThumb(img); else card.SetPhoto(img);
            }
            catch { /* art is best-effort */ }
        }

        /// <summary>Keeps the sponsored card pinned to the bottom (below any newly appended real posts).</summary>
        private void KeepSponsoredLast()
        {
            if (_sponsoredCard != null && !_sponsoredCard.IsDisposed && _messagePanel.Controls.Contains(_sponsoredCard))
                _messagePanel.Controls.SetChildIndex(_sponsoredCard, _messagePanel.Controls.Count - 1);
        }

        private void RemoveSponsoredCard()
        {
            if (_sponsoredCard != null && !_sponsoredCard.IsDisposed)
            {
                _messagePanel.Controls.Remove(_sponsoredCard);
                _sponsoredCard.Dispose();
            }
            _sponsoredCard = null;
        }

        // ── Mandatory telemetry: view on display, click on interaction ──────

        /// <summary>Fires viewSponsoredMessage ONCE, when the ad's full text band is within the viewport.</summary>
        private void MaybeViewSponsored()
        {
            var card = _sponsoredCard;
            if (card == null || card.IsDisposed || _sponsoredMsg == null) return;
            string key = BitConverter.ToString(_sponsoredMsg.random_id);
            if (_sponsoredViewed.Contains(key)) return;

            int vTop = -_messagePanel.AutoScrollPosition.Y;
            int vBottom = vTop + _messagePanel.ClientSize.Height;
            int tTop = card.Top + card.ViewTextTop, tBottom = card.Top + card.ViewTextBottom;
            // Full text shown: its bottom is visible AND (its top is visible OR it's taller than the viewport).
            bool full = tBottom <= vBottom && (tTop >= vTop || (tBottom - tTop) >= (vBottom - vTop));
            if (!full) return;

            _sponsoredViewed.Add(key);
            var rid = _sponsoredMsg.random_id;
            var _ = SafeSponsored(() => _service.ViewSponsoredAsync(rid));
        }

        private async System.Threading.Tasks.Task SafeSponsored(Func<System.Threading.Tasks.Task> call)
        { try { await call(); } catch { /* telemetry is best-effort once attempted */ } }

        private void OnSponsoredLink(SponsoredMessage sm, string url)
        {
            var _ = SafeSponsored(() => _service.ClickSponsoredAsync(sm.random_id, false));
            ResolveLinkAsync(url);                     // link in the ad text → in-app if t.me, else browser
        }

        /// <summary>Button / sponsor name / photo / media click → ClickSponsored(+media) then route the url
        /// through the in-app resolver (so a t.me "VIEW CHANNEL" opens in-app; external urls go to browser).</summary>
        private void OnSponsoredOpen(SponsoredMessage sm, bool media)
        {
            var _ = SafeSponsored(() => _service.ClickSponsoredAsync(sm.random_id, media));
            ResolveLinkAsync(sm.url);
        }

        private static bool IsTelegramDomain(string url)
        {
            try
            {
                var host = new Uri(NormalizeUrl(url), UriKind.Absolute).Host;
                return System.Text.RegularExpressions.Regex.IsMatch(host,
                    @"(^|\.)(telegram\.(org|me|dog)|t\.me|te\.?legra\.ph|graph\.org|fragment\.com|telesco\.pe)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        // ── In-app link / mention router (Part A) ────────────────────────────
        private enum TgKind { External, User, PrivateChannel, Invite }

        /// <summary>Single router every link/mention/sponsored-button click goes through: opens t.me/tg://
        /// targets IN-APP, sends everything else to the browser (with the Telegram-domain confirm gate).</summary>
        private async void ResolveLinkAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var kind = ParseTgLink(url, out string username, out int msgId, out long channelId, out string invite, out string startParam);
            bool startGroup = url.IndexOf("startgroup", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                switch (kind)
                {
                    case TgKind.User:
                        await OpenByUsername(username, msgId);
                        if (startGroup) ThemedDialog.Show(this, "Add to group", "Adding a bot to a group isn't supported yet.", "OK");
                        else if (!string.IsNullOrEmpty(startParam)) await StartBotIfPossible(startParam);   // /start <param> handshake
                        break;
                    case TgKind.PrivateChannel: await OpenByChannelId(channelId, msgId); break;
                    case TgKind.Invite: await OpenInvite(invite); break;
                    default: OpenExternalUrl(url); break;
                }
            }
            catch { OpenExternalUrl(url); }   // any resolution failure → graceful browser fallback
        }

        /// <summary>Opens an external url; confirms first UNLESS the host is one of Telegram's own domains.</summary>
        private void OpenExternalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            // BATCH-TA-18 — a proxy link is OURS to handle; it must never reach a browser. This is the one
            // funnel every link seam ends at (message text, link-preview card, inline KeyboardButtonUrl,
            // UrlAuth, callback answers, sponsored ads, profile bio, the search box), which is why the
            // interception sits HERE rather than at each caller.
            // ⚠ Note what it fixes: t.me/proxy matches IsTelegramDomain below, so before this the link
            //   skipped even the confirm dialog and went straight to Process.Start.
            if (TryHandleProxyLink(url)) return;
            if (!IsTelegramDomain(url))
            {
                int c = ThemedDialog.Show(this, "Open link", "Open this link?\n\n" + url, "Open", "Cancel");
                // After the modal closes, don't let focus fall through to the search box (WinForms
                // re-focuses the last text field on reactivation) — clear the active control.
                try { ActiveControl = null; } catch { }
                if (c != 0) return;
            }
            try { System.Diagnostics.Process.Start(NormalizeUrl(url)); } catch { }   // scheme-less ("site.com") needs https://
        }

        /// <summary>BATCH-TA-18 — is this an MTProxy link, and if so, deal with it instead of shelling out?
        /// Returns TRUE for "handled, do not open a browser" — INCLUDING the malformed case, because a link
        /// that is unmistakably a proxy link but broken should say why, not silently become a web page.
        ///
        /// ⚠ ONE PARSER. The shape test is <see cref="ProxyUrl.IsProxyLink"/>; the actual parse is
        /// <see cref="ProxyUrl.TryNormalize"/> — the identical call the paste box in ProxyForm makes, so the
        /// 17-case harness covers this path too and a link cannot be accepted by one and rejected by the
        /// other. Writing a second parser here is exactly the divergence that harness exists to prevent.</summary>
        private bool TryHandleProxyLink(string url)
        {
            if (!ProxyUrl.IsProxyLink(url)) return false;

            string norm, err;
            if (!ProxyUrl.TryNormalize(url, out norm, out err))
            {
                Logger.Diag("[PROXYLINK] rejected " + ProxyUrl.SafeForLog(url));   // host:port only, never the secret
                ThemedDialog.Show(this, "Can't use that proxy link", err, "OK");   // err never quotes the secret
                try { ActiveControl = null; } catch { }
                return true;
            }

            ShowProxyLinkSheet(norm);
            return true;
        }

        /// <summary>BATCH-TA-18 — the confirmation sheet, then whatever the user chose.
        /// NEVER auto-connects: the link came from a channel, and silently moving every byte this app sends
        /// onto a stranger's server because a finger landed on a button is not acceptable (TA-18/L3).
        /// Dedupe is by exact normalised link: an already-saved proxy is SELECTED, never duplicated.</summary>
        private async void ShowProxyLinkSheet(string norm)
        {
            try
            {
                var s = AppSettings.Instance;
                if (s.ProxyList == null) s.ProxyList = new List<string>();
                int existing = s.ProxyList.FindIndex(u => string.Equals(u, norm, StringComparison.OrdinalIgnoreCase));
                Logger.Diag("[PROXYLINK] tapped " + ProxyUrl.SafeForLog(norm) + (existing >= 0 ? " (already saved)" : " (new)"));

                ProxyLinkAction action;
                using (var dlg = new ProxyLinkForm(norm, existing >= 0))
                {
                    dlg.ShowDialog(this);
                    action = dlg.Action;
                }
                // Same reason as OpenExternalUrl's confirm: don't let focus fall through to the search box.
                try { ActiveControl = null; } catch { }

                if (action == ProxyLinkAction.Cancel) { Logger.Diag("[PROXYLINK] dismissed"); return; }

                int idx = existing;
                if (idx < 0) { s.ProxyList.Add(norm); idx = s.ProxyList.Count - 1; }

                if (action == ProxyLinkAction.AddOnly)
                {
                    // Deliberately does NOT touch ProxyEnabled/ProxyActive: "collect now, test later" must
                    // not move the user off a connection that is currently working.
                    try { s.Save(); } catch { }
                    Logger.Diag("[PROXYLINK] added " + ProxyUrl.SafeForLog(norm) + " without switching ("
                                + s.ProxyList.Count + " total)");
                    ShowToast("Proxy added to your list");
                    return;
                }

                s.ProxyActive = idx;
                s.ProxyEnabled = true;
                try { s.Save(); } catch { }
                Logger.Diag("[PROXYLINK] connect → " + ProxyUrl.SafeForLog(norm) + " (index " + idx + ")");

                // EVERY DOOR MUST APPLY WHAT IT PERSISTED (§2d — the settings-door bug the device log caught).
                // This is a fourth door into the proxy setting, so it applies live like the other three:
                // warm pool down and awaited, active client reconnected, pool re-warmed. No restart.
                RefreshProxyPill();
                await ApplyProxyChangeAsync();
            }
            catch (Exception ex)
            {
                Logger.Diag("[PROXYLINK] failed: " + ex.Message);   // never echoes a link
            }
        }

        /// <summary>Adds an https:// scheme to a scheme-less link so Uri parsing + Process.Start work.</summary>
        private static string NormalizeUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return u;
            u = u.Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(u, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:") ? u : "https://" + u;
        }

        /// <summary>Classifies a url/@username/tg:// link; out-params carry the resolved target.</summary>
        private static TgKind ParseTgLink(string url, out string username, out int msgId, out long channelId, out string invite, out string startParam)
        {
            username = null; msgId = 0; channelId = 0; invite = null; startParam = null;
            string u = (url ?? "").Trim();
            if (u.Length == 0) return TgKind.External;

            if (u[0] == '@')   // bare @username
            {
                username = u.Substring(1).Split('?', '/')[0];
                return string.IsNullOrEmpty(username) ? TgKind.External : TgKind.User;
            }

            if (u.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            {
                var q = ParseQuery(u);
                if (u.StartsWith("tg://resolve", StringComparison.OrdinalIgnoreCase) && q.TryGetValue("domain", out var dom) && !string.IsNullOrEmpty(dom))
                {
                    username = dom;
                    if (q.TryGetValue("post", out var post)) int.TryParse(post, out msgId);
                    if (q.TryGetValue("start", out var sp) && !string.IsNullOrEmpty(sp)) startParam = sp;   // /start deep-link
                    return TgKind.User;
                }
                if (u.StartsWith("tg://join", StringComparison.OrdinalIgnoreCase) && q.TryGetValue("invite", out var inv) && !string.IsNullOrEmpty(inv))
                { invite = inv; return TgKind.Invite; }
                return TgKind.External;
            }

            Uri uri;
            // Message links are often scheme-less ("t.me/Channel") — absolute-Uri parsing needs a scheme.
            if (!Uri.TryCreate(NormalizeUrl(u), UriKind.Absolute, out uri)) return TgKind.External;
            string host = uri.Host.ToLowerInvariant();
            if (host != "t.me" && host != "telegram.me" && host != "telegram.dog") return TgKind.External;

            var segs = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return TgKind.External;
            string first = segs[0];

            if (first.StartsWith("+")) { invite = first.Substring(1); return string.IsNullOrEmpty(invite) ? TgKind.External : TgKind.Invite; }
            if (first.Equals("joinchat", StringComparison.OrdinalIgnoreCase))
            { invite = segs.Length > 1 ? segs[1] : null; return string.IsNullOrEmpty(invite) ? TgKind.External : TgKind.Invite; }
            if (first.Equals("c", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length >= 2 && long.TryParse(segs[1], out channelId))
                {
                    if (segs.Length >= 3) int.TryParse(segs[2], out msgId);
                    return TgKind.PrivateChannel;
                }
                return TgKind.External;
            }
            // non-username reserved first segments → browser
            if (first.Equals("share", StringComparison.OrdinalIgnoreCase) || first.Equals("addstickers", StringComparison.OrdinalIgnoreCase)
                || first.Equals("addemoji", StringComparison.OrdinalIgnoreCase) || first.Equals("setlanguage", StringComparison.OrdinalIgnoreCase)
                || first.Equals("proxy", StringComparison.OrdinalIgnoreCase) || first.Equals("socks", StringComparison.OrdinalIgnoreCase)
                || first.Equals("login", StringComparison.OrdinalIgnoreCase) || first.Equals("bg", StringComparison.OrdinalIgnoreCase))
                return TgKind.External;

            int postIdx = 1;
            if (first.Equals("s", StringComparison.OrdinalIgnoreCase) && segs.Length >= 2) { first = segs[1]; postIdx = 2; }   // t.me/s/<channel>

            username = first;
            if (segs.Length > postIdx) int.TryParse(segs[postIdx], out msgId);
            var query = ParseQuery(url);
            if (query.TryGetValue("start", out var qsp) && !string.IsNullOrEmpty(qsp)) startParam = qsp;   // t.me/<bot>?start=<param>
            return TgKind.User;
        }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int qi = url.IndexOf('?');
            if (qi < 0) return d;
            foreach (var pair in url.Substring(qi + 1).Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) try { d[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]); } catch { }
            }
            return d;
        }

        /// <summary>Resolves a @username/domain → opens that chat in-app (focused on msgId when given).</summary>
        private async System.Threading.Tasks.Task OpenByUsername(string username, int msgId)
        {
            if (string.IsNullOrEmpty(username)) return;
            var who = await _service.ResolveUsernameAsync(username.TrimStart('@'));
            if (IsDisposed) return;
            var entry = EntryFromPeerInfo(who);
            if (entry == null) { OpenExternalUrl("https://t.me/" + username); return; }   // unresolved → browser
            await OpenChat(entry, msgId);
            if (msgId > 0) ScrollToAndFlash(msgId);
        }

        /// <summary>t.me/c/&lt;id&gt;/&lt;msg&gt;: opens the private channel IF we're a member; else browser.</summary>
        private async System.Threading.Tasks.Task OpenByChannelId(long channelId, int msgId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == channelId);
            if (entry == null) { OpenExternalUrl("https://t.me/c/" + channelId + (msgId > 0 ? "/" + msgId : "")); return; }
            await OpenChat(entry, msgId);
            if (msgId > 0) ScrollToAndFlash(msgId);
        }

        /// <summary>Invite link: open if already a member/peekable; otherwise confirm + join, then open.</summary>
        private async System.Threading.Tasks.Task OpenInvite(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            ChatInviteBase info;
            try { info = await _service.CheckInviteAsync(hash); }
            catch { OpenExternalUrl("https://t.me/+" + hash); return; }

            if (info is ChatInviteAlready already) { await OpenInvitedChat(already.chat); return; }
            if (info is ChatInvitePeek peek) { await OpenInvitedChat(peek.chat); return; }
            if (info is ChatInvite preview)
            {
                int c = ThemedDialog.Show(this, "Join",
                    "Join \"" + preview.title + "\"?", "Join", "Cancel");
                if (c != 0) return;
                try
                {
                    await _service.JoinInviteAsync(hash);
                    var after = await _service.CheckInviteAsync(hash);   // now resolves to the joined chat
                    if (after is ChatInviteAlready joined) await OpenInvitedChat(joined.chat);
                    else if (after is ChatInvitePeek peeked) await OpenInvitedChat(peeked.chat);
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Join failed", ex.Message, "OK"); }
                return;
            }
            OpenExternalUrl("https://t.me/+" + hash);
        }

        private async System.Threading.Tasks.Task OpenInvitedChat(ChatBase chat)
        {
            var entry = EntryFromPeerInfo(chat);
            if (entry != null) await OpenChat(entry, 0);
        }

        /// <summary>A ChatEntry for a resolved peer — the existing dialog-list row if present, else a fresh one.</summary>
        private ChatEntry EntryFromPeerInfo(IPeerInfo info)
        {
            if (info is User u)
                return _allChats.FirstOrDefault(c => c.PeerId == u.id)
                    ?? new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u), IsGroup = false, PeerInfo = u };
            if (info is ChatBase cb)
                return _allChats.FirstOrDefault(c => c.PeerId == cb.ID)
                    ?? new ChatEntry { Peer = cb.ToInputPeer(), PeerId = cb.ID, Title = cb.Title, IsGroup = true, PeerInfo = cb };
            return null;
        }

        // ── Sponsor info + report flow (card menu) ──────────────────────────

        private void ShowSponsoredMenu(SponsoredMessage sm, Point screenPt)
        {
            var menu = new ThemedContextMenuStrip();
            bool hasInfo = (sm.flags & SponsoredMessage.Flags.has_sponsor_info) != 0
                        || (sm.flags & SponsoredMessage.Flags.has_additional_info) != 0;
            if (hasInfo) AddMenuItem(menu, "ⓘ   Sponsor info", () => ShowSponsorInfo(sm));
            if ((sm.flags & SponsoredMessage.Flags.can_report) != 0)
                AddMenuItem(menu, "⚑   About this ad", () => ReportSponsored(sm, ""));
            if (menu.Items.Count == 0) { menu.Dispose(); return; }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        private void ShowSponsorInfo(SponsoredMessage sm)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(sm.sponsor_info)) sb.AppendLine(sm.sponsor_info);
            if (!string.IsNullOrEmpty(sm.additional_info)) { if (sb.Length > 0) sb.AppendLine(); sb.Append(sm.additional_info); }
            ThemedDialog.Show(this, "Sponsor info", sb.ToString().Trim(), "OK");
        }

        /// <summary>The ad report flow: empty option → ChooseOption picker → re-report; handles AdsHidden /
        /// Reported / AD_EXPIRED / PREMIUM_ACCOUNT_REQUIRED.</summary>
        private async void ReportSponsored(SponsoredMessage sm, string option)
        {
            try
            {
                var result = await _service.ReportSponsoredAsync(sm.random_id, option);
                if (result is Channels_SponsoredMessageReportResultChooseOption choose)
                {
                    string picked = PickReportOption(choose);
                    if (picked != null) ReportSponsored(sm, picked);     // re-call with the chosen option
                }
                else if (result is Channels_SponsoredMessageReportResultAdsHidden)
                {
                    ThemedDialog.Show(this, "Ads hidden", "Sponsored messages are now hidden.", "OK");
                    RemoveSponsoredCard();
                }
                else // Channels_SponsoredMessageReportResultReported
                {
                    ThemedDialog.Show(this, "Reported", "Thank you — this ad has been reported.", "OK");
                    RemoveSponsoredCard();
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message != null && ex.Message.Contains("AD_EXPIRED") ? "This ad has expired."
                    : ex.Message != null && ex.Message.Contains("PREMIUM_ACCOUNT_REQUIRED") ? "Reporting ads requires Telegram Premium."
                    : ex.Message;
                ThemedDialog.Show(this, "Report", msg, "OK");
            }
        }

        private string PickReportOption(Channels_SponsoredMessageReportResultChooseOption choose)
        {
            if (choose.options == null || choose.options.Length == 0) return null;
            var labels = new List<string>();
            foreach (var o in choose.options) labels.Add(o.text);
            labels.Add("Cancel");
            int idx = ThemedDialog.Show(this, string.IsNullOrEmpty(choose.title) ? "Report ad" : choose.title, "", labels.ToArray());
            return (idx >= 0 && idx < choose.options.Length) ? choose.options[idx].option : null;
        }

        private void ScrollToMessage(int messageId)
        {
            _messagePanel.PerformLayout();
            var c = FindMessageControl(messageId);
            if (c != null) { _messagePanel.ScrollControlIntoView(c); return; }
            ScrollMessagesToBottom(); // target not in the loaded window
        }

        private async System.Threading.Tasks.Task LoadOlderMessages()
        {
            if (_loadingOlder || !_hasMoreHistory || _selectedChat == null || _oldestMessageId == 0)
                return;
            // TOUCH-FREEZE: a pan parked near the top re-fires this at pan-tick rate (85/s captured live).
            // The in-flight guard only spaces FETCHES — completions must be spaced too, or back-to-back
            // fetch+merge+reflow cycles saturate the UI thread on slow hardware (the RT/SB2 hard freeze).
            if (Environment.TickCount - _lastLoadOlderDoneTick < 300) return;

            _loadingOlder = true;                                                  // in-flight guard (reset in finally)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] in-flight guard set");
            try
            {
                var entry = _selectedChat;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] load-older requested (offset id=" + _oldestMessageId + ")");
                var history = await LoadWindowAsync(entry, 50, _oldestMessageId, 0);
                if (_selectedChat != entry) return;

                var raw = history.Messages ?? new MessageBase[0];
                var older = raw
                    .Where(mb => !_shownMessageIds.Contains(mb.ID))
                    .OrderBy(MsgDate)
                    .ToList();   // MessageBase (messages + service events)

                if (older.Count == 0)
                {
                    // Latch "end of chat" ONLY when the SERVER genuinely returned no older messages. A
                    // transient/aborted fetch throws (caught below, retryable) — and a page that deduped to
                    // zero (overlap/race) is NOT the top: advance past it and keep paging open.
                    if (raw.Length == 0)
                    {
                        _hasMoreHistory = false;
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] reached real top (server: no more)");
                    }
                    else
                    {
                        int minRaw = raw.Min(mb => mb.ID);
                        if (minRaw < _oldestMessageId) _oldestMessageId = minRaw;   // step past the overlap
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] older page all-deduped; advanced offset, NOT latching");
                    }
                    return;
                }

                // Anchor on the current top message so the view doesn't jump.
                var anchor = _messagePanel.Controls.Count > 0 ? _messagePanel.Controls[0] : null;

                _messagePanel.SuspendLayout();
                int idx = 0;
                var addedMsgs = new List<Message>();
                foreach (var mb in older)
                {
                    Control ctl;
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fuo) _peerNames[fuo.id] = DisplayName(fuo);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        // CHAT-RENDER-INTEGRITY: album items GROUP here too. The old direct-MakeMessageBubble
                        // bypass gave every grouped item its OWN full bubble and re-measured any existing album
                        // it should have merged into — the pin-window corruption trigger. ONE bubble per album,
                        // dedup via HandleAlbumItem's own shownIds bookkeeping (albums counted consistently).
                        if (m.grouped_id != 0)
                        {
                            MessageBubbleControl created;
                            int handled = HandleAlbumItem(entry, m, ResolveSender(entry, m, from), from, out created);
                            if (handled == 1)
                            {
                                addedMsgs.Add(m);
                                if (created != null)
                                {
                                    _messagePanel.Controls.Add(created);
                                    _messagePanel.Controls.SetChildIndex(created, idx++);
                                }
                            }
                            continue;   // merged into an existing bubble → no new control to position
                        }
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        ctl = MakeMessageBubble(entry, m, ResolveSender(entry, m, from));
                        if (ctl is MessageBubbleControl mbo)
                        {
                            if (entry.IsGroup && !IsOut(m) && from is User fuo2) WireSenderAvatar(mbo, fuo2);
                            ApplyReactions(mbo, m);
                            ApplyEntities(mbo, m);
                            if (entry.PeerId == RepliesPeerId) { IngestRepliesSource(m, history); DecorateRepliesBubble(mbo, m); }
                        }
                        addedMsgs.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        CacheServiceNames(history.UserOrChat, svc); ctl = MakeServiceLine(svc);
                    }
                    else continue;
                    if (ctl == null) continue;
                    _messagePanel.Controls.Add(ctl);
                    _messagePanel.Controls.SetChildIndex(ctl, idx++);
                }
                _oldestMessageId = older[0].ID;
                _currentChatMessages.InsertRange(0, addedMsgs);
                _messagePanel.ResumeLayout();

                if (ReconcileWindowOrHeal(entry)) return;   // 1.4: overlap detected → self-healing reload

                if (anchor != null) _messagePanel.ScrollControlIntoView(anchor);
            }
            catch (Exception ex)
            {
                // Transient (reconnect / aborted): do NOT latch _hasMoreHistory — stays retryable next scroll.
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] load-older failed (retryable): " + ex.Message);
            }
            finally
            {
                _loadingOlder = false;                                             // reset so the next scroll can retry
                _lastLoadOlderDoneTick = Environment.TickCount;                    // TOUCH-FREEZE: space completions
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] in-flight guard reset");
            }
        }

        /// <summary>
        /// DOWNWARD paging (Part 3): after a focused jump the loaded window is an island; when the user
        /// scrolls near its bottom and the newest loaded id isn't the chat's latest, fetch the page of
        /// messages NEWER than the newest loaded id and append them — repeating until the live tail is
        /// reached, at which point normal new-message appending resumes.
        /// </summary>
        private async System.Threading.Tasks.Task LoadNewerMessages()
        {
            if (_loadingNewer || _atLiveTail || _selectedChat == null || _newestMessageId == 0)
                return;

            _loadingNewer = true;
            try
            {
                var entry = _selectedChat;
                // Messages newer than _newestMessageId: anchor at it, move back (toward newer) by one page.
                var history = await LoadWindowAsync(entry, 50, _newestMessageId, -50);
                if (_selectedChat != entry) return;

                var newer = history.Messages
                    .Where(mb => mb.ID > _newestMessageId && !_shownMessageIds.Contains(mb.ID))
                    .OrderBy(MsgDate)
                    .ToList();   // strictly newer than what we hold, chronological

                if (newer.Count == 0) { _atLiveTail = true; OnScrollPositionChanged(); return; }

                int keepY = -_messagePanel.AutoScrollPosition.Y;
                bool wasAtBottom = AtBottom(AtBottomThreshold);

                _messagePanel.SuspendLayout();
                var addedMsgs = new List<Message>();
                foreach (var mb in newer)
                {
                    Control ctl;
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fu) _peerNames[fu.id] = DisplayName(fu);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        // CHAT-RENDER-INTEGRITY: same album-grouping fix as the older-page merge.
                        if (m.grouped_id != 0)
                        {
                            MessageBubbleControl created;
                            int handled = HandleAlbumItem(entry, m, ResolveSender(entry, m, from), from, out created);
                            if (handled == 1)
                            {
                                addedMsgs.Add(m);
                                if (created != null) _messagePanel.Controls.Add(created);
                            }
                            continue;
                        }
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        ctl = MakeMessageBubble(entry, m, ResolveSender(entry, m, from));
                        if (ctl is MessageBubbleControl mb2)
                        {
                            if (entry.IsGroup && !IsOut(m) && from is User fu2) WireSenderAvatar(mb2, fu2);
                            ApplyReactions(mb2, m);
                            ApplyEntities(mb2, m);
                        }
                        addedMsgs.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        CacheServiceNames(history.UserOrChat, svc); ctl = MakeServiceLine(svc);
                    }
                    else continue;
                    if (ctl == null) continue;
                    _messagePanel.Controls.Add(ctl);   // append at the bottom (this is the newer page)
                }
                _currentChatMessages.AddRange(addedMsgs);   // newer than all loaded → already in order
                _newestMessageId = Math.Max(_newestMessageId, newer.Max(mb => mb.ID));
                KeepSponsoredLast();                        // the ad card stays below the newly appended posts
                _messagePanel.ResumeLayout();
                if (ReconcileWindowOrHeal(entry)) return;   // 1.4: overlap detected → self-healing reload
                UpdateAudioPlaylist();

                // A short page (or reaching the chat's latest) means the island just met the live tail.
                if (newer.Count < 50 || _newestMessageId >= entry.TopMessageId) _atLiveTail = true;

                if (!wasAtBottom) _messagePanel.AutoScrollPosition = new Point(0, keepY);   // don't yank a mid-island reader
                OnScrollPositionChanged();
            }
            catch { /* transient — retried on next scroll */ }
            finally { _loadingNewer = false; }
        }

        /// <summary>CHAT-RENDER-INTEGRITY 1.4: cheap post-mutation insurance — after a window merge the panel
        /// must lay out as one strictly descending column (each child at/below the previous one's bottom).
        /// Any overlap (this bug class's symptom) logs the state triplet and SELF-HEALS by reloading the
        /// window through the proven full-load path, anchored at the current top message. Returns true when
        /// healing was triggered — callers must stop touching the panel.</summary>
        private bool ReconcileWindowOrHeal(ChatEntry entry)
        {
            bool overlap = false;
            int prevBottom = int.MinValue;
            Control prev = null, bad = null;
            foreach (Control c in _messagePanel.Controls)
            {
                if (!c.Visible) continue;
                // prev==null guard: prevBottom-2 would UNDERFLOW int.MinValue and flag the first control
                if (prev != null && c.Top < prevBottom - 2) { overlap = true; bad = c; break; }   // 2px slack for margins/rounding
                if (c.Bottom > prevBottom) prevBottom = c.Bottom;
                prev = c;
            }
            if (!overlap) return false;
            // TOUCH-FREEZE conviction (131 heals in one session log): healing DURING a live gesture
            // disposes the control under the finger — the WM_TOUCH stream dies mid-gesture (the stuck-
            // _active incident) and the reloaded window re-arms the near-top trigger → an unbounded
            // fetch-merge-heal livelock that reads as a hard freeze on slow hardware. Defer while a
            // finger/coast drives the panel, and never heal more than once per 3s — every subsequent
            // pager merge re-checks anyway, so a real persistent overlap still gets healed.
            if (TouchScroller.GestureOrCoastActive)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch — heal DEFERRED (gesture active)");
                return false;
            }
            if (Environment.TickCount - _lastHealTick < 3000)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch — heal SUPPRESSED (rate limit)");
                return false;
            }
            _lastHealTick = Environment.TickCount;
            if (LogOn)
            {
                System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch model=" + _currentChatMessages.Count
                    + " panel=" + _messagePanel.Controls.Count + " shown=" + _shownMessageIds.Count + " → full re-render");
                if (bad != null) System.Diagnostics.Debug.WriteLine("[RENDER]   pair: prev=" + (prev == null ? "?" : prev.GetType().Name)
                    + " id=" + (prev == null ? 0 : BubbleMsgId(prev)) + " bounds=" + (prev == null ? Rectangle.Empty : prev.Bounds)
                    + " | bad=" + bad.GetType().Name + " id=" + BubbleMsgId(bad) + " bounds=" + bad.Bounds
                    + " idx=" + _messagePanel.Controls.IndexOf(bad));
            }
            int anchorId = 0;
            foreach (Control c in _messagePanel.Controls) { anchorId = BubbleMsgId(c); if (anchorId > 0) break; }
            var _ = LoadHistoryAsync(entry, anchorId);   // async self-heal through the healthy path (resets the triplet)
            return true;
        }

        private async void MessagePanel_Scroll(object sender, ScrollEventArgs e)
        {
            if (e.ScrollOrientation != ScrollOrientation.VerticalScroll) return;
            OnScrollPositionChanged();
            if (e.NewValue <= 0)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] scroll-event near-top → load older");
                await LoadOlderMessages();
            }
        }

    }
}
