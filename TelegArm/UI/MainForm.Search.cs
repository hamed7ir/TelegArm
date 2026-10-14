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
        // ── Avatars (profile photos) — all surfaces route through AvatarStore (AVATAR-PIPELINE) ──

        /// <summary>Cached avatar for a peer id (sync), or null if not loaded yet — for picker rows to draw
        /// immediately and fall back to the initials-circle until <see cref="GetAvatarBoundedAsync"/> fills in.</summary>
        public Image GetCachedAvatar(long id)
        {
            return _avatars.GetCached(id);
        }

        /// <summary>Awaitable avatar for the pickers (memory → disk → single-flight bounded download via the
        /// store's queue). Null = no photo / failed — callers keep the letter circle. Same delegate signature
        /// the picker dialogs always took, so they are unchanged.</summary>
        public System.Threading.Tasks.Task<Image> GetAvatarBoundedAsync(long id, IPeerInfo peer)
        {
            return _avatars.GetAsync(id, peer);
        }

        /// <summary>Chat-list row hook: memory hit paints immediately; otherwise a store request is queued
        /// AHEAD of the backfill and the row repaints via <see cref="OnAvatarLoaded"/> when it lands.</summary>
        private void LoadAvatar(ChatEntry entry)
        {
            if (entry == null) return;
            _avatars.Request(entry.PeerId, entry.PeerInfo);
        }

        /// <summary>AvatarLoaded (worker thread): repaint every live surface showing this peer — the chat-list
        /// row and any open group bubbles awaiting this sender. Rows/bubbles are looked up LIVE (both get
        /// recreated constantly), never through captured references.</summary>
        private void OnAvatarLoaded(long peerId)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    var img = _avatars.GetCached(peerId);
                    if (img == null) return;
                    ApplyAvatarToRow(peerId, img);
                    RefreshStoryAvatar(peerId);   // STORIES: repaint the tray chip when its avatar lands
                    // CHANNEL-PHOTO-REFRESH: if the OPEN chat's avatar just (re)landed — e.g. its photo changed — repaint the header.
                    if (_selectedChat != null && _selectedChat.PeerId == peerId) { _headerAvatarImg = img; _headerAvatar?.Invalidate(); }
                    if (_messagePanel != null && !_messagePanel.IsDisposed)
                        foreach (Control c in _messagePanel.Controls)
                            if (c is MessageBubbleControl b && !b.IsDisposed && b.SenderPeerId == peerId && b.SenderAvatar == null)
                            { b.SenderAvatar = img; b.Invalidate(); }
                }));
            }
            catch { /* form tearing down */ }
        }

        /// <summary>Sets a freshly-arrived avatar on the current chat-list row for <paramref name="peerId"/> and
        /// repaints just that row. Looks the row up live (rows are recreated on every RenderChatList), marshals
        /// to the UI thread if the avatar arrived off-thread, and is safe against a disposed list/row.</summary>
        private void ApplyAvatarToRow(long peerId, Image img)
        {
            if (img == null) return;
            var panel = _chatListPanel;
            if (panel == null || panel.IsDisposed) return;
            if (panel.InvokeRequired) { try { panel.BeginInvoke((Action)(() => ApplyAvatarToRow(peerId, img))); } catch { } return; }
            foreach (Control c in panel.Controls)
                if (c is ChatListItemControl row && !row.IsDisposed && row.Entry != null && row.Entry.PeerId == peerId)
                { row.Avatar = img; row.Invalidate(); break; }
        }

        // ── Combined search (chats + global messages in one list) ────────────

        // SEARCH-BUILD-1: public-discovery (Contacts_Search) limit. The API has NO offset → "Show more" re-queries
        // with the expanded limit. Reset to the initial limit on every new query (each search starts collapsed).
        private const int PublicSearchLimit = 8, PublicSearchLimitExpanded = 40;
        private int _publicLimit = PublicSearchLimit;

        private void OnSearchTextChanged()
        {
            _searchDebounce.Stop();
            // INCHAT-SEARCH: while scoped to an open chat, typing searches THAT chat (not the global list). The
            // results re-render in DoInChatSearch after the debounce; here we just keep the scope chip shown.
            if (_inChatSearchEntry != null)
            {
                RenderInChatSearchChrome();
                if (!string.IsNullOrWhiteSpace(_searchBox.Text)) _searchDebounce.Start();
                return;
            }
            string q = _searchBox.Text;
            _publicLimit = PublicSearchLimit;   // SEARCH-BUILD-1: a fresh query starts with the collapsed public list
            // Instant local chat matches (folder-aware); message + public results stream in after a debounce.
            RenderChatList(q);
            if (!string.IsNullOrWhiteSpace(q)) _searchDebounce.Start();
        }

        private async void DoMessageSearch()
        {
            _searchDebounce.Stop();
            // INCHAT-SEARCH: scoped-to-open-chat search runs its own path (Messages_Search on the peer).
            if (_inChatSearchEntry != null) { await DoInChatSearch(); return; }
            var query = _searchBox.Text.Trim();
            if (query.Length == 0) return;

            // SEARCH-BUILD-2: a typed @username / t.me link resolves to a public entity — the PRIMARY result. Skip the
            // normal message/public search for it (the resolution IS the answer).
            if (TryResolveSearchLink(query)) return;

            // SEARCH-SPONSORED (PART 6.5): promoted channels/bots for this query — their own "SPONSORED" section (ToS:
            // view reported on display, click on tap). Best-effort; usually empty (no section).
            try
            {
                var sponsored = await _service.GetSponsoredPeersAsync(query);
                if (_searchBox.Text.Trim() != query) return;
                AppendSponsoredResults(sponsored);
            }
            catch { /* sponsored is best-effort */ }

            try
            {
                var results = await _service.SearchMessagesAsync(query, 50);
                // Drop stale responses (query changed meanwhile).
                if (_searchBox.Text.Trim() != query) return;
                AppendMessageResults(results);
            }
            catch (Exception ex)
            {
                _chatTitle.Text = "Search failed: " + ex.Message;
            }

            // SEARCH-BUILD-1: public discovery (channels/groups/users you're NOT in) — additive + best-effort; a
            // failure never disturbs the local + message results already shown.
            try
            {
                var found = await _service.SearchContactsAsync(query, _publicLimit);
                if (_searchBox.Text.Trim() != query) return;   // stale (query changed during the await)
                AppendPublicResults(found);
            }
            catch { /* public discovery is best-effort */ }
        }

        /// <summary>Appends global message hits below the already-shown chat matches (combined search).</summary>
        private void AppendMessageResults(Messages_MessagesBase results)
        {
            var msgs = results.Messages.OfType<Message>().Where(m => m.peer_id != null).ToList();
            if (msgs.Count == 0) return;

            _chatListPanel.SuspendLayout();
            int w = ContentWidth(_chatListPanel);
            AddSectionHeader("MESSAGES");
            foreach (var m in msgs)
            {
                string title;
                InputPeer peer;
                bool isGroup = false;
                var resolved = results.UserOrChat(m.peer_id);
                if (resolved is User u) { title = DisplayName(u); peer = u.ToInputPeer(); }
                else if (resolved is ChatBase ch) { title = ch.Title; peer = ch.ToInputPeer(); isGroup = true; }
                else continue;

                var entry = new ChatEntry
                {
                    Peer = peer,
                    PeerId = m.peer_id.ID,
                    Title = title,
                    Preview = GetDisplayText(m),
                    Date = m.date,
                    IsGroup = isGroup,
                    FocusMessageId = m.ID,
                    PeerInfo = resolved   // SEARCH-FIX-2 (BUG 2): carry the peer so the avatar can load/fetch
                };
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnSearchResultClick;
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(entry.PeerId);   // SEARCH-FIX-2 (BUG 2): cached avatar shows instantly
                if (item.Avatar == null) LoadAvatar(entry);       // else fetch on-demand (letter circle meanwhile)
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>SEARCH-BUILD-1: appends PUBLIC discovery results (Contacts_Search `results`) below the message
        /// hits — categorized CHANNELS (broadcast) / GROUPS (megagroup) / USERS. Entities you're ALREADY in are
        /// skipped (they show under CHATS). A "Show more" row re-queries with a larger limit when the limit was hit.</summary>
        private void AppendPublicResults(Contacts_Found found)
        {
            if (found == null || found.results == null || found.results.Length == 0) return;
            var known = new HashSet<long>(_allChats.Select(c => c.PeerId));
            var channels = new List<ChatEntry>();
            var groups = new List<ChatEntry>();
            var users = new List<ChatEntry>();

            foreach (var p in found.results)
            {
                if (p == null || known.Contains(p.ID)) continue;   // already in it → shown under CHATS
                var info = found.UserOrChat(p);
                if (info is Channel ch)
                    ((ch.flags & Channel.Flags.broadcast) != 0 ? channels : groups).Add(PublicChannelEntry(ch));
                else if (info is User u)
                    users.Add(PublicUserEntry(u));
                // a basic Chat can't be public (no username) — skip
            }
            if (channels.Count + groups.Count + users.Count == 0) return;

            _chatListPanel.SuspendLayout();
            AppendPublicSection("CHANNELS", channels);
            AppendPublicSection("GROUPS", groups);
            AppendPublicSection("USERS", users);
            // "Show more": the API has NO offset, so re-query with the expanded limit. Offer it only when the query
            // filled the current limit (more likely exist) and we're not already expanded.
            if (found.results.Length >= _publicLimit && _publicLimit < PublicSearchLimitExpanded)
                AddShowMoreRow();
            _chatListPanel.ResumeLayout();
        }

        private void AppendPublicSection(string header, List<ChatEntry> entries)
        {
            if (entries.Count == 0) return;   // no empty sections
            AddSectionHeader(header);
            int w = ContentWidth(_chatListPanel);
            foreach (var entry in entries)
            {
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnSearchResultClick;   // → OnSearchResultClick → OpenChat (public channel = preview + Join)
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(entry.PeerId);
                if (item.Avatar == null) LoadAvatar(entry);
            }
        }

        /// <summary>Builds a public channel/group result row: name + a "N subscribers/members" subtitle (or @username /
        /// "Channel"/"Group" when the count isn't carried). PeerInfo/Peer set so OpenChat can preview it unjoined.</summary>
        private ChatEntry PublicChannelEntry(Channel ch)
        {
            bool broadcast = (ch.flags & Channel.Flags.broadcast) != 0;
            string sub;
            if ((ch.flags & Channel.Flags.has_participants_count) != 0 && ch.participants_count > 0)
                sub = FormatMemberCount(ch.participants_count) + (broadcast ? " subscribers" : " members");
            else if (!string.IsNullOrEmpty(ch.username)) sub = "@" + ch.username;
            else sub = broadcast ? "Channel" : "Group";
            return new ChatEntry
            {
                Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.Title,
                Preview = sub, IsGroup = !broadcast, PeerInfo = ch, Date = default(DateTime)
            };
        }

        private ChatEntry PublicUserEntry(User u)
        {
            string sub = !string.IsNullOrEmpty(u.username) ? "@" + u.username
                       : ((u.flags & User.Flags.bot) != 0 ? "Bot" : "User");
            return new ChatEntry
            {
                Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u),
                Preview = sub, IsGroup = false, PeerInfo = u, Date = default(DateTime)
            };
        }

        private static string FormatMemberCount(int n)
        {
            if (n >= 1000000) return (n / 1000000f).ToString("0.#") + "M";
            if (n >= 1000) return (n / 1000f).ToString("0.#") + "K";
            return n.ToString();
        }

        /// <summary>SEARCH-BUILD-1: a clickable "Show more" row under the public sections — re-queries Contacts_Search
        /// with the EXPANDED limit (no API offset) and re-renders the whole search. One extra message re-fetch is
        /// accepted for simplicity (rare, explicit user action).</summary>
        private void AddShowMoreRow()
        {
            var lbl = new Label
            {
                Text = "Show more",
                AutoSize = false,
                Height = Px(30),   // step 3.4: logical px; shared font, never dispose
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0),
                Cursor = Cursors.Hand,
                Font = FontHelper.UiAt(9f, WindowDpiValue, FontStyle.Bold),
                ForeColor = _accent
            };
            lbl.Click += (s, e) =>
            {
                _publicLimit = PublicSearchLimitExpanded;
                RenderChatList(_searchBox.Text);   // clears + re-renders CHATS; the deferred fetch re-appends the rest
                DoMessageSearch();
            };
            _chatListPanel.Controls.Add(lbl);
        }

        // ── SEARCH-SPONSORED (PART 6.5): promoted channels/bots in search — reuses the sponsored view/click reporting
        // + the public-row style (NOT SponsoredCardControl, which is the in-channel big card) ──

        /// <summary>Renders promoted peers (Contacts_GetSponsoredPeers) as a labeled "SPONSORED" section of result
        /// rows. ToS: the VIEW is reported once per random_id on display; the CLICK on tap. Reuses the public-row
        /// build + the existing _sponsoredViewed dedup + ViewSponsoredAsync/ClickSponsoredAsync.</summary>
        private void AppendSponsoredResults(Contacts_SponsoredPeers sponsored)
        {
            if (sponsored == null || sponsored.peers == null || sponsored.peers.Length == 0) return;
            _chatListPanel.SuspendLayout();
            AddSectionHeader("SPONSORED");
            int w = ContentWidth(_chatListPanel);
            foreach (var sp in sponsored.peers)
            {
                if (sp == null || sp.peer == null) continue;
                var info = sponsored.UserOrChat(sp.peer);
                var entry = info is Channel ch ? PublicChannelEntry(ch)
                          : info is User u ? PublicUserEntry(u)
                          : (info != null ? EntryFromPeerInfo(info) : null);
                if (entry == null) continue;
                // ToS label: "Sponsored" always visible on the row (in addition to the section header).
                entry.Preview = string.IsNullOrEmpty(entry.Preview) ? "Sponsored" : "Sponsored · " + entry.Preview;
                var rid = sp.random_id;
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += (s, e) => OnSponsoredResultClick(entry, rid);
                _chatListPanel.Controls.Add(item);
                if (entry.PeerId != 0) { item.Avatar = _avatars.GetCached(entry.PeerId); if (item.Avatar == null) LoadAvatar(entry); }
                MaybeReportSponsoredView(rid);
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>ToS: report a sponsored result's VIEW once per random_id (reuses the _sponsoredViewed dedup that
        /// the in-channel sponsored card uses).</summary>
        private void MaybeReportSponsoredView(byte[] randomId)
        {
            if (randomId == null) return;
            if (!_sponsoredViewed.Add(BitConverter.ToString(randomId))) return;
            var _ = SafeSponsored(() => _service.ViewSponsoredAsync(randomId));
        }

        /// <summary>A sponsored result was tapped: report the CLICK (ToS) then open the promoted entity via the
        /// public-open path (a not-joined channel previews with Join).</summary>
        private void OnSponsoredResultClick(ChatEntry entry, byte[] randomId)
        {
            if (randomId != null) { var _ = SafeSponsored(() => _service.ClickSponsoredAsync(randomId, false)); }
            _searchBox.Text = "";
            var target = _allChats.FirstOrDefault(c => c.PeerId == entry.PeerId) ?? entry;
            var __ = OpenChat(target, 0);
        }

        // ── SEARCH-BUILD-2: link / @username resolution in the search box (reuses the in-message router) ──

        /// <summary>Detects whether the query is a resolvable @username or t.me link (reusing the router's STATIC
        /// parser <see cref="ParseTgLink"/>). If so, kicks off the async resolution (a "GO TO" result) and returns
        /// true so the caller skips the normal search. A normal search term parses as External → returns false.</summary>
        private bool TryResolveSearchLink(string query)
        {
            var kind = ParseTgLink(query, out string username, out int _, out long _, out string invite, out string _);
            if (kind == TgKind.User && !string.IsNullOrEmpty(username)) { ResolveSearchUsername(query, username); return true; }
            if (kind == TgKind.Invite && !string.IsNullOrEmpty(invite)) { ResolveSearchInvite(query, invite); return true; }
            return false;
        }

        /// <summary>Resolves an @username / t.me/name (ResolveUsernameAsync) and shows it as a "GO TO" result row
        /// (tap → the router opens it — a public channel previews with Join). Graceful "not found" on failure.</summary>
        private async void ResolveSearchUsername(string query, string username)
        {
            IPeerInfo who = null;
            try { who = await _service.ResolveUsernameAsync(username.TrimStart('@')); } catch { }
            if (IsDisposed || _searchBox.Text.Trim() != query) return;   // torn down / query changed meanwhile

            _chatListPanel.SuspendLayout();
            AddSectionHeader("GO TO");
            if (who == null) AddNotFoundRow("@" + username.TrimStart('@'));
            else
            {
                var entry = who is Channel ch ? PublicChannelEntry(ch)
                          : who is User u ? PublicUserEntry(u)
                          : EntryFromPeerInfo(who);
                if (entry != null) AddResolvedRow(entry, query); else AddNotFoundRow("@" + username.TrimStart('@'));
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Previews an invite link (CheckInviteAsync) as a "GO TO" row: already-a-member → the chat; a preview
        /// → title + member count + "tap to join". Tap → the router (OpenInvite) confirms + joins + opens.</summary>
        private async void ResolveSearchInvite(string query, string hash)
        {
            ChatInviteBase info = null;
            try { info = await _service.CheckInviteAsync(hash); } catch { }
            if (IsDisposed || _searchBox.Text.Trim() != query) return;

            _chatListPanel.SuspendLayout();
            AddSectionHeader("GO TO");
            ChatEntry entry = null;
            if (info is ChatInviteAlready already) entry = EntryFromPeerInfo(already.chat);
            else if (info is ChatInvitePeek peek) entry = EntryFromPeerInfo(peek.chat);
            else if (info is ChatInvite preview) entry = InvitePreviewEntry(preview);
            if (entry == null) AddNotFoundRow("invite link");
            else AddResolvedRow(entry, query);
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Adds a resolved "GO TO" result row (avatar + name + subtitle). Tap → the router opens the target.</summary>
        private void AddResolvedRow(ChatEntry entry, string query)
        {
            var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = ContentWidth(_chatListPanel) };
            item.Click += (s, e) => OpenResolvedTarget(query);
            _chatListPanel.Controls.Add(item);
            if (entry.PeerId != 0) { item.Avatar = _avatars.GetCached(entry.PeerId); if (item.Avatar == null) LoadAvatar(entry); }
        }

        /// <summary>A "GO TO" row was tapped: clear the box (restores the normal list) and hand the query to the
        /// in-message router — it resolves + opens (username/msgId/start-param OR invite preview→join→open).</summary>
        private void OpenResolvedTarget(string query)
        {
            _searchBox.Text = "";
            ResolveLinkAsync(query);
        }

        private ChatEntry InvitePreviewEntry(ChatInvite p)
        {
            bool broadcast = (p.flags & ChatInvite.Flags.broadcast) != 0;
            string sub = p.participants_count > 0
                ? FormatMemberCount(p.participants_count) + (broadcast ? " subscribers" : " members") + " · tap to join"
                : "tap to join";
            return new ChatEntry { PeerId = 0, Title = p.title, Preview = sub, IsGroup = !broadcast, Date = default(DateTime) };
        }

        /// <summary>A subtle themed "no results" row for a link/username that didn't resolve.</summary>
        private void AddNotFoundRow(string what)
        {
            var lbl = new Label
            {
                Text = "No results for " + what,
                AutoSize = false,
                Height = Px(34),
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Px(14), 0, 0, 0),
                Margin = new Padding(0),
                Font = FontHelper.UiAt(9f, WindowDpiValue),   // shared: never dispose
                ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(120, 120, 125)
            };
            _chatListPanel.Controls.Add(lbl);
        }

        /// <summary>Adds a small section divider label into the chat list (search groupings).</summary>
        private void AddSectionHeader(string text)
        {
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Height = Px(22),
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Px(10), 0, 0, 0),
                Margin = new Padding(0),
                Font = FontHelper.UiAt(8f, WindowDpiValue, FontStyle.Bold),   // shared: never dispose
                ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(120, 120, 125)
            };
            _chatListPanel.Controls.Add(lbl);
        }

    }
}
