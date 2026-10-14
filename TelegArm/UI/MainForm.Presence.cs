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
        // ── PRESENCE (v0.9.0): our own online status + peer presence surfaces ─

        /// <summary>Member lists subscribe for live row updates (unsubscribe on dispose — E-3).
        /// Raised on the UI thread from the UpdateUserStatus handler.</summary>
        public static event Action<long, UserStatus> UserStatusChanged;

        /// <summary>RELEASE-FIXES-V11 (H1): raised after WE rename a peer (admin channel/group rename in EditChatInfoForm,
        /// or a contact rename in ProfileForm) → MainForm updates the ChatEntry title + repaints the row + header live so
        /// the new name shows WITHOUT an app reload (same instant-refresh spirit as the channel-photo fix).</summary>
        public static event Action<long, string> PeerTitleChanged;

        /// <summary>Raise the rename event from OUTSIDE MainForm (a C# event can only be .Invoke'd inside its
        /// declaring type) — EditChatInfoForm / ProfileForm call this after a rename.</summary>
        public static void RaisePeerTitleChanged(long peerId, string title) { PeerTitleChanged?.Invoke(peerId, title); }

        private void OnPeerTitleChanged(long peerId, string title)
        {
            if (IsDisposed || string.IsNullOrEmpty(title)) return;
            try { if (InvokeRequired) BeginInvoke((Action)(() => ApplyPeerTitle(peerId, title))); else ApplyPeerTitle(peerId, title); }
            catch { }
        }

        private void ApplyPeerTitle(long peerId, string title)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry != null)
            {
                entry.Title = title;
                if (entry.PeerInfo is Channel ch) ch.title = title;          // keep the shared TL entity consistent
                else if (entry.PeerInfo is Chat bc) bc.title = title;
                FindChatItem(peerId)?.Invalidate();                          // repaint the chat-list row
            }
            if (_selectedChat != null && _selectedChat.PeerId == peerId)     // the OPEN chat → header
            {
                if (_selectedChat.PeerInfo is Channel sch) sch.title = title;
                else if (_selectedChat.PeerInfo is Chat sbc) sbc.title = title;
                if (_chatTitle != null) _chatTitle.Text = title;
                _headerAvatarTitle = title;                                  // initials fallback source
                _headerAvatar?.Invalidate();
            }
        }

        private Timer _presenceTimer;             // ONE 15s host: idle/offline transitions + dot-expiry sweep + 60s group refresh
        private int _lastActivityTick, _lastPresenceSentTick, _bgSinceTick, _presenceTicks;
        private int _lastGroupRefreshTick;        // dedupes the double-running open path (one RPC per open)
        private long _lastGroupRefreshPeer;
        private bool _presenceOnline;             // the state we last TOLD the server

        private void StartPresenceEngine()
        {
            _lastActivityTick = Environment.TickCount;
            Application.AddMessageFilter(new ActivityFilter(this));   // app-lifetime, like the wheel filter
            // PRESENCE-TUNE A.3: 5s cadence — the tick only reads cached fields (sweep work only on an
            // actual expiry), so the faster interval is near-zero cost; offline now lands ≤10s.
            _presenceTimer = new Timer { Interval = 5000 };
            _presenceTimer.Tick += (s, e) => PresenceTick();
            _presenceTimer.Start();
        }

        /// <summary>PRESENCE 1.1: activity = REAL input only (key/click/wheel; touch taps arrive as the
        /// synthesized posted click, touch pans via TouchScroller.Scrolled) — never our own timers.</summary>
        private sealed class ActivityFilter : IMessageFilter
        {
            private readonly MainForm _f;
            public ActivityFilter(MainForm f) { _f = f; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                // WM_KEYDOWN / WM_LBUTTONDOWN / WM_RBUTTONDOWN / WM_MOUSEWHEEL
                if (m.Msg == 0x0100 || m.Msg == 0x0201 || m.Msg == 0x0204 || m.Msg == 0x020A) _f.NoteActivity();
                return false;
            }
        }

        internal void NoteActivity()
        {
            _lastActivityTick = Environment.TickCount;
            if (!_appActive || IsDisposed) return;
            if (!_presenceOnline || Environment.TickCount - _lastPresenceSentTick >= 25000)
                SendPresence(false);
        }

        /// <summary>Single presence sender: state + throttle stamps set SYNCHRONOUSLY before the await,
        /// so a burst of activity can't double-send. ≥25s between online refreshes (1.1).</summary>
        private async void SendPresence(bool offline)
        {
            if (_service?.Client == null || TelegramService.TearingDown) return;
            _lastPresenceSentTick = Environment.TickCount;
            _presenceOnline = !offline;
            try
            {
                bool ok = await _service.UpdateStatusAsync(offline);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] sent " + (offline ? "offline" : "online") + " ok=" + ok);
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] send failed: " + ex.Message);
            }
        }

        private void PresenceTick()
        {
            if (IsDisposed) return;
            int now = Environment.TickCount;
            // PRESENCE-TUNE A.2 (amended): offline after the 5s deactivation grace (tray-hide rides it),
            // or after 5 MINUTES of true input idle while foreground — passive READING has no input, and
            // the old 60s bar flipped the user offline mid-read. Return does NOT send here —
            // activation/next input does (A.1); reconnects never auto-send.
            if (_presenceOnline)
            {
                bool bgLong = !_appActive && _bgSinceTick != 0 && now - _bgSinceTick > 5000;
                bool idleLong = _appActive && now - _lastActivityTick > 300000;
                if (bgLong || idleLong) SendPresence(true);
            }
            // 2.3: dot-expiry sweep over VISIBLE rows — dots die even without a server update.
            // (No suitable existing 30s-class timer existed; this ONE timer hosts all presence cadence.)
            try
            {
                var utc = DateTime.UtcNow;
                foreach (Control c in _chatListPanel.Controls)
                    if (c is ChatListItemControl ci && ci.Entry.OnlineUntil != default(DateTime) && ci.Entry.OnlineUntil <= utc)
                    {
                        ci.Entry.OnlineUntil = default(DateTime);
                        ci.Invalidate();
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] dot expired peer=" + ci.Entry.PeerId + " (sweep)");
                    }
            }
            catch { /* rebuild race — next tick */ }
            // Every 12th tick (60s at the 5s cadence), foreground only:
            _presenceTicks++;
            if (_presenceTicks % 12 == 0 && _appActive)
            {
                // PRESENCE keep-alive (official-client cadence): the server's online window is shorter
                // than a reading session, and reading produces ZERO input — refresh online every ~60s
                // while foreground and not truly idle, so the session stays visibly online on other
                // devices (Settings→Devices) without any input.
                if (_presenceOnline && now - _lastActivityTick < 300000) SendPresence(false);
                RefreshOpenGroupPresence();   // 3.3: open-group member/online counts
            }
        }

        /// <summary>PRESENCE 3.3: ONE bounded full-chat RPC for the OPEN group's member/online counts —
        /// at open and every 60s while it stays open and foreground. A switch mid-flight discards the
        /// result (entry comparison — E-3). Broadcasts never come here (subscribers only, no online).</summary>
        private async void RefreshOpenGroupPresence()
        {
            var entry = _selectedChat;
            if (entry?.PeerInfo == null || entry.PeerInfo is User) return;
            if (entry.PeerInfo is Channel bch && (bch.flags & Channel.Flags.broadcast) != 0) return;
            // The open path can run twice per row click (same double the [PIN] fetch shows) — a short
            // window guard keeps it to ONE RPC per open without touching the 60s cadence.
            if (entry.PeerId == _lastGroupRefreshPeer && Environment.TickCount - _lastGroupRefreshTick < 5000) return;
            _lastGroupRefreshPeer = entry.PeerId;
            _lastGroupRefreshTick = Environment.TickCount;
            try
            {
                var d = await _service.GetPeerDetailsAsync(entry.Peer, entry.PeerInfo);
                if (_selectedChat != entry || IsDisposed) return;   // switched away → discard
                if (d.members > 0) entry.ParticipantsCount = d.members;
                entry.OnlineCount = d.online;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] group refresh peer=" + entry.PeerId
                    + " members=" + d.members + " online=" + d.online);
                UpdateHeaderStatus();
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] group refresh failed: " + ex.Message);
            }
        }

        // ── Online / typing status ───────────────────────────────────────────

        internal static string StatusText(User u)
        {
            if (u == null) return "";
            switch (u.status)
            {
                case UserStatusOnline _: return "online";
                case UserStatusOffline off: return "last seen " + DrawHelper.ShortStamp(off.was_online);
                case UserStatusRecently _: return "last seen recently";
                case UserStatusLastWeek _: return "last seen within a week";
                case UserStatusLastMonth _: return "last seen within a month";
                default: return "";
            }
        }

        /// <summary>Refreshes the header subtitle (unless typing): users → presence/last-seen;
        /// channels/groups → subscriber/member count (PEER-PRESENTATION; empty until known).</summary>
        private void UpdateHeaderStatus()
        {
            if (_chatStatus == null || _typing) return;
            var entry = _selectedChat;
            var u = entry?.PeerInfo as User;
            if (u == null && entry?.PeerInfo != null)
            {
                int n = entry.ParticipantsCount;
                bool bcast = entry.PeerInfo is Channel bch && (bch.flags & Channel.Flags.broadcast) != 0;
                string txt = n > 0
                    ? n.ToString("N0") + (bcast ? (n == 1 ? " subscriber" : " subscribers")
                                                : (n == 1 ? " member" : " members"))
                    : "";   // count unknown → EMPTY, never "0 subscribers"
                // PRESENCE 3.1: groups append ", M online" when known; broadcasts never show online.
                if (!bcast && txt.Length > 0 && entry.OnlineCount >= 1)
                    txt += ", " + entry.OnlineCount.ToString("N0") + " online";
                _chatStatus.Text = txt;
                _chatStatus.Role = LabelRole.Dim;
                return;
            }
            _chatStatus.Text = u != null ? StatusText(u) : "";
            bool online = u?.status is UserStatusOnline;
            _chatStatus.Role = online ? LabelRole.Accent : LabelRole.Dim;
        }

        /// <summary>Owner-draws the header peer avatar (circular) on the accent strip, or a deterministic initials
        /// circle until the image loads — mirrors the chat-row avatar rendering.</summary>
        private void HeaderAvatar_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var p = (Panel)sender;
            g.Clear(p.BackColor);   // blend with the header's theme background
            if (_headerAvatarPeerId == 0) return;   // empty state (no chat) → just the background
            const int d = 40;
            var rect = new Rectangle((p.Width - d) / 2, (p.Height - d) / 2, d, d);
            var img = _headerAvatarImg;   // snapshot: another path can null it between the check and the draw
            if (img != null)
            {
                try
                {
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        clip.AddEllipse(rect);
                        g.SetClip(clip);
                        g.DrawImage(img, rect);
                        g.ResetClip();
                    }
                    return;
                }
                catch
                {
                    // ACCOUNT-RECOVERY-SAFETY (Bug 3): the cached bitmap was disposed out from under us (an account switch
                    // reset the avatar store mid-paint) → DrawImage throws "Parameter is not valid", and in a Paint handler
                    // that CRASHES the app (ThreadException). Drop the dangling reference and fall through to the initials.
                    try { g.ResetClip(); } catch { }
                    _headerAvatarImg = null;
                }
            }
            using (var b = new SolidBrush(DrawHelper.AvatarColor(_headerAvatarPeerId)))
                g.FillEllipse(b, rect);
            string letter = string.IsNullOrEmpty(_headerAvatarTitle) ? "?" : _headerAvatarTitle.Substring(0, 1).ToUpper();
            using (var af = FontHelper.For(_headerAvatarTitle ?? "", 15f, FontStyle.Bold))
                TextRenderer.DrawText(g, letter, af, rect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Points the header avatar at <paramref name="entry"/>'s peer (cache hit paints immediately;
        /// otherwise the bounded loader fills it in, guarded against a stale load for a chat we've since left).
        /// Pass null for the empty "Select a chat" state.</summary>
        private void SetHeaderAvatar(ChatEntry entry)
        {
            if (_messagePanel != null) _messagePanel.Invalidate();   // 2.7b: the empty-state pill comes and goes with the chat
            if (_headerAvatar == null) return;
            if (entry == null)
            {
                _headerAvatarImg = null; _headerAvatarTitle = null; _headerAvatarPeerId = 0;
                _headerAvatar.Invalidate();
                return;
            }
            _headerAvatarTitle = entry.Title;
            _headerAvatarPeerId = entry.PeerId;
            _headerAvatarImg = GetCachedAvatar(entry.PeerId);
            _headerAvatar.Invalidate();
            if (_headerAvatarImg == null && entry.PeerInfo != null)
            {
                long pid = entry.PeerId;
                GetAvatarBoundedAsync(entry.PeerId, entry.PeerInfo).ContinueWith(t =>
                {
                    if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || t.Result == null) return;
                    try { BeginInvoke((Action)(() => { if (_headerAvatarPeerId == pid) { _headerAvatarImg = t.Result; _headerAvatar.Invalidate(); } })); }
                    catch { }
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        /// <summary>ONE lazy count resolution per chat open (PEER-PRESENTATION 2.1): dialog-cached
        /// counts first (basic Chat always carries one; Channel sometimes does); else a single
        /// full-chat fetch per entry EVER (CountFetchTried latches even on a missing result). The
        /// header updates via BeginInvoke when the fetch lands — zero per-paint/per-update RPCs.</summary>
        private async void MaybeFetchChatCount(ChatEntry entry)
        {
            if (entry == null || entry.PeerInfo == null || entry.PeerInfo is User) return;
            if (entry.ParticipantsCount > 0) { UpdateHeaderStatus(); return; }   // cached — zero RPCs
            if (entry.PeerInfo is Chat c && c.participants_count > 0) entry.ParticipantsCount = c.participants_count;
            else if (entry.PeerInfo is Channel ch && ch.participants_count > 0) entry.ParticipantsCount = ch.participants_count;
            if (entry.ParticipantsCount > 0)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] subtitle count peer=" + entry.PeerId + " n=" + entry.ParticipantsCount + " src=dialog");
                UpdateHeaderStatus();
                return;
            }
            if (entry.CountFetchTried) return;   // the one lazy fetch already ran — missing stays missing quietly
            entry.CountFetchTried = true;
            try
            {
                var details = await _service.GetPeerDetailsAsync(entry.Peer, entry.PeerInfo);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] subtitle count peer=" + entry.PeerId + " n=" + details.members + " src=full-chat fetch");
                if (details.members > 0)
                {
                    entry.ParticipantsCount = details.members;
                    if (_selectedChat == entry && !IsDisposed)
                        try { BeginInvoke((Action)UpdateHeaderStatus); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] count fetch failed: " + ex.Message);
            }
        }

        private void ShowTypingFor(SendMessageAction action, string name = null)
        {
            if (action is SendMessageCancelAction)
            {
                _typing = false; _typingTimer.Stop(); UpdateHeaderStatus();
                return;
            }
            _typing = true;
            _chatStatus.Text = string.IsNullOrEmpty(name) ? "typing…" : name + " is typing…";
            _chatStatus.Role = LabelRole.Accent;
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        /// <summary>QUICKWINS-1 PART 1: on genuine composer input, tell the open chat's peer we're typing — throttled to
        /// ~once per TypingThrottleMs (the server keeps "typing…" alive ~6s). Empty text (cleared / just sent) cancels.
        /// Inert for a disabled composer (broadcast we can't post to). Fire-and-forget; never blocks or surfaces.</summary>
        private bool _suppressComposerChange;   // DRAFTS: true while a draft is loaded programmatically → don't broadcast "typing"

        private void OnComposerTextChanged(object sender, EventArgs e)
        {
            if (_suppressComposerChange) return;
            try
            {
                if (_selectedChat == null || _messageInput == null || !_messageInput.Enabled) return;
                if (string.IsNullOrEmpty(_messageInput.Text)) { CancelTyping(); return; }   // cleared / sent → stop typing
                int now = Environment.TickCount;
                if (_typingPeer == null || unchecked(now - _lastTypingTick) >= TypingThrottleMs)
                {
                    _lastTypingTick = now;
                    _typingPeer = _selectedChat.Peer;
                    var _ = _service.SendTypingAsync(_typingPeer, true);   // fire-and-forget
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[TYPING] sent typing peer=" + _selectedChat.PeerId);
                }
            }
            catch { /* typing is best-effort */ }
        }

        // ── DRAFTS ──────────────────────────────────────────────────────────

        /// <summary>Saves the CURRENT chat's composer text as its draft (empty text → CLEARS the draft), updates the
        /// local model + the row indicator + float, and pushes it to the server. No-op if unchanged (no SaveDraft spam),
        /// in a comment thread, or while editing. Returns the save Task (or null) so a close can bound-wait it.</summary>
        private void PersistDraftForCurrentChat()
        {
            try
            {
                var chat = _selectedChat;
                if (chat != null && _thread == null && _editTarget == null && _messageInput != null)
                    SaveDraftFor(chat, CurrentComposerText());   // 2.7d: the editor window's text while it owns the composer
            }
            catch { }
            // 2.7d: both callers (OpenChat, SwitchAccountAsync) are about to change the chat: the editor window closes now.
            CloseComposerEditorForSwitch();
        }

        /// <summary>Saves <paramref name="raw"/> as <paramref name="chat"/>'s draft (local model, list indicator, server).
        /// Also used by the editor window when the chat changed under it.</summary>
        private void SaveDraftFor(ChatEntry chat, string raw)
        {
            try
            {
                if (chat == null) return;
                string text = (raw ?? "").Trim();
                if (text == (chat.DraftText ?? "")) return;   // unchanged → don't spam SaveDraft
                chat.DraftText = string.IsNullOrEmpty(text) ? null : text;
                chat.DraftDate = string.IsNullOrEmpty(text) ? DateTime.MinValue : DateTime.UtcNow;
                var _ = _service?.SaveDraftAsync(chat.Peer, text);   // fire-and-forget (SaveDraftAsync never throws)
                if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");   // float + "Draft:" indicator, live
                if (LogOn) System.Diagnostics.Debug.WriteLine("[DRAFT] save peer=" + chat.PeerId + " len=" + text.Length);
            }
            catch { }
        }

        /// <summary>Loads a chat's saved draft into the composer on open (or clears the previous chat's text when there is
        /// no draft). Suppresses the "typing" broadcast for the programmatic set. Skipped in a thread / mid-edit.</summary>
        private void LoadDraftIntoComposer(ChatEntry entry)
        {
            if (_messageInput == null || _thread != null || _editTarget != null) return;
            _suppressComposerChange = true;
            try
            {
                _messageInput.Text = entry?.DraftText ?? "";
                try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
            }
            finally { _suppressComposerChange = false; }
        }

        /// <summary>SEND emptied the composer → clear this chat's draft too (local + server) so no stale "Draft:" lingers.
        /// Only fires the clear RPC + re-render when the chat actually HAD a draft.</summary>
        private void ClearDraftAfterSend(ChatEntry chat)
        {
            if (chat == null || !chat.HasDraft) { if (chat != null) { chat.DraftText = null; chat.DraftDate = DateTime.MinValue; } return; }
            chat.DraftText = null; chat.DraftDate = DateTime.MinValue;
            var _ = _service?.SaveDraftAsync(chat.Peer, "");   // idempotent if the server already cleared the draft on send
            if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");
        }

        /// <summary>DRAFTS cross-device sync: an UpdateDraftMessage (a draft changed here or on another device). Update the
        /// row's draft + float + preview; if that chat is OPEN and the user hasn't diverged from the last-known draft,
        /// mirror the remote text into the composer too (never clobbering active composing).</summary>
        private void HandleDraftUpdate(Peer peer, DraftMessageBase draft)
        {
            if (peer == null) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peer.ID);
            if (entry == null) return;
            string oldText = entry.DraftText;
            var dm = draft as DraftMessage;
            string text = (dm != null && !string.IsNullOrEmpty(dm.message)) ? dm.message : null;
            entry.DraftText = text;
            entry.DraftDate = text != null ? dm.date : DateTime.MinValue;

            if (_selectedChat == entry && _thread == null && _editTarget == null && _messageInput != null
                && (_messageInput.Text ?? "").Trim() == (oldText ?? "").Trim())   // user hasn't typed past the known draft
            {
                _suppressComposerChange = true;
                try { _messageInput.Text = text ?? ""; try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { } }
                finally { _suppressComposerChange = false; }
            }
            if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");
        }

        /// <summary>QUICKWINS-1 PART 1: stop the "typing…" we last showed (message sent, composer cleared, or chat
        /// switched). Cancels for the peer we actually told — so a chat switch stops typing in the chat we LEFT.</summary>
        private void CancelTyping()
        {
            if (_typingPeer == null) return;
            var peer = _typingPeer;
            _typingPeer = null; _lastTypingTick = 0;
            try { var _ = _service.SendTypingAsync(peer, false); } catch { }
            if (LogOn) System.Diagnostics.Debug.WriteLine("[TYPING] cancel");
        }

    }
}
