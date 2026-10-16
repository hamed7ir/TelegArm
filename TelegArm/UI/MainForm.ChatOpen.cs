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
        // ── INCHAT-SEARCH: search within the currently-open chat (the LEFT panel becomes scoped results) ──────
        private ChatEntry _inChatSearchEntry;   // non-null ⇔ IN-CHAT search mode (scoped to this open chat)
        private string _inChatQuery;
        private int _inChatOffsetId;             // oldest result id shown → offset for the next (older) page
        private int _inChatTotal;                // total match count ("N messages found")
        private bool _inChatPaging;
        private int _inChatFilter;               // T19: 0 all, 1 photos, 2 videos, 3 files, 4 links, 5 voice
        private bool _inChatFromMe;              // T19: only my own messages

        private static readonly string[] InChatFilterNames = { "All", "Photos", "Videos", "Files", "Links", "Voice" };

        private static MessagesFilter InChatFilterOf(int i)
        {
            switch (i)
            {
                case 1: return new InputMessagesFilterPhotos();
                case 2: return new InputMessagesFilterVideo();
                case 3: return new InputMessagesFilterDocument();
                case 4: return new InputMessagesFilterUrl();
                case 5: return new InputMessagesFilterVoice();
                default: return null;
            }
        }

        private bool InChatFiltered { get { return _inChatFilter != 0 || _inChatFromMe; } }

        /// <summary>Enters in-chat search for the open chat: the left panel becomes the scoped results view.</summary>
        private void EnterInChatSearch()
        {
            if (_selectedChat == null) return;
            _inChatSearchEntry = _selectedChat;
            _inChatQuery = null; _inChatOffsetId = 0; _inChatTotal = 0;
            _inChatFilter = 0; _inChatFromMe = false;   // T19
            _searchBox.Text = "";                 // TextChanged (in-chat mode) shows the chip; explicit call covers no-change
            RenderInChatSearchChrome();
            ShowColumn(true);                     // one column: the search box and its results are on the list side
            try { _searchBox.Focus(); } catch { }
        }

        /// <summary>Leaves in-chat search: restores the normal chat list (the chat stays open on the right).</summary>
        private void ExitInChatSearch()
        {
            if (_inChatSearchEntry == null) return;
            _inChatSearchEntry = null; _inChatQuery = null; _inChatOffsetId = 0; _inChatTotal = 0;
            _searchBox.Text = "";
            RenderChatList("");                   // guard is off now → the normal list rebuilds
            if (_selectedChat != null) ShowColumn(false);   // one column: back to the chat that was searched
        }

        /// <summary>Renders the scope chip only (rows come from DoInChatSearch). Used on enter + while typing.</summary>
        private void RenderInChatSearchChrome()
        {
            if (_inChatSearchEntry == null) return;
            _chatListPanel.SuspendLayout();
            // BATCH-TA-5/C2: dispose before clearing — same shape as RenderChatListCore (:5725) and the
            // folder bar (:6373). This path runs on EVERY keystroke while in-chat search is open, so the
            // undisposed rows it left behind leaked one HWND + two GDI fonts each, per keystroke.
            // _selectedItem MUST be nulled: it may point at a row we are about to dispose, and
            // OnChatItemClick later does `_selectedItem.Selected = false`, whose setter calls
            // Invalidate() → ObjectDisposedException on a disposed control. Harmless before this change
            // (Clear left a live orphan), a real fault after it.
            foreach (var c in _chatListPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
            _chatListPanel.Controls.Clear();
            _selectedItem = null;
            AddInChatScopeChip();
            _chatListPanel.ResumeLayout();
        }

        /// <summary>The scope chip: "×  Searching in: NAME" — tap anywhere on it to exit in-chat search.</summary>
        private void AddInChatScopeChip()
        {
            string name = _inChatSearchEntry != null ? (_inChatSearchEntry.Title ?? "this chat") : "this chat";
            var chip = new Label
            {
                Text = "×   Searching in: " + name,
                AutoSize = false, Height = Px(40), Width = ContentWidth(_chatListPanel),   // step 3.4: logical px
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Px(12), 0, 0, 0), Margin = new Padding(0),
                Font = FontHelper.UiAt(9.5f, WindowDpiValue, FontStyle.Bold), Cursor = Cursors.Hand, ForeColor = ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).PanelPlaceholderBg, 4.5),   // shared: never dispose
                BackColor = ThemeHelper.PaletteFor(_dark).PanelPlaceholderBg
            };
            chip.Click += (s, e) => ExitInChatSearch();
            _chatListPanel.Controls.Add(chip);
            AddInChatFilterChips();
        }

        /// <summary>T19: what to search for (a media kind) and whose messages (mine) — chips under the scope chip. Picking one
        /// searches at once, even with the box empty ("all the photos in this chat").</summary>
        private void AddInChatFilterChips()
        {
            var row = new FlowLayoutPanel { Width = ContentWidth(_chatListPanel), Height = Px(44), WrapContents = false, AutoScroll = false,
                                            Margin = Padding.Empty, Padding = new Padding(Px(6), Px(4), 0, 0), BackColor = ThemeHelper.PaletteFor(_dark).ListBg };
            for (int i = 0; i < InChatFilterNames.Length; i++)
            {
                int k = i;
                var c = new FolderTabItem(InChatFilterNames[i], 0, _inChatFilter == i, _dark, _accent, WindowDpiValue);
                c.Clicked += () => { _inChatFilter = k; RerunInChatSearch(); };
                row.Controls.Add(c);
            }
            var me = new FolderTabItem("From me", 0, _inChatFromMe, _dark, _accent, WindowDpiValue);
            me.Clicked += () => { _inChatFromMe = !_inChatFromMe; RerunInChatSearch(); };
            row.Controls.Add(me);
            _chatListPanel.Controls.Add(row);
        }

        private async void RerunInChatSearch()
        {
            if (_inChatSearchEntry == null) return;
            if (string.IsNullOrWhiteSpace(_searchBox.Text) && !InChatFiltered) { _inChatQuery = null; RenderInChatSearchChrome(); return; }
            await DoInChatSearch();
        }

        /// <summary>Runs the scoped search (Messages_Search on the open peer) → renders results, newest-first.</summary>
        private async System.Threading.Tasks.Task DoInChatSearch()
        {
            if (_inChatSearchEntry == null) return;
            var q = _searchBox.Text.Trim();
            if (q.Length == 0 && !InChatFiltered) { _inChatQuery = null; RenderInChatSearchChrome(); return; }
            _inChatQuery = q; _inChatOffsetId = 0;
            int filterAt = _inChatFilter; bool meAt = _inChatFromMe;
            try
            {
                var res = await _service.SearchInChatAsync(_inChatSearchEntry.Peer, q, 0, 40, InChatFilterOf(filterAt), meAt ? new InputPeerSelf() : null);
                if (_inChatSearchEntry == null || _searchBox.Text.Trim() != q || filterAt != _inChatFilter || meAt != _inChatFromMe) return;   // stale
                RenderInChatResults(res, replace: true);
            }
            catch { /* best-effort; leave the chip up */ }
        }

        /// <summary>Renders (replace) or appends (paging) scoped result rows: sender avatar + name + snippet + date;
        /// tap → jump to that message in the open chat. Next-page offset_id = the oldest id shown.</summary>
        private void RenderInChatResults(Messages_MessagesBase res, bool replace)
        {
            if (_inChatSearchEntry == null || res == null) return;
            var msgs = res.Messages != null ? res.Messages.OfType<Message>().ToList() : new List<Message>();
            _chatListPanel.SuspendLayout();
            if (replace)
            {
                // BATCH-TA-5/C2: dispose before clearing (same shape as :5725 / :6373). This is the
                // heavier of the two in-chat sites — it builds up to 40 real ChatListItemControl HWNDs
                // per query (SearchInChatAsync requests 40) and replace:true fires on every new query.
                // Only the replace branch clears; the append/paging path below must NOT dispose.
                // _selectedItem nulled for the same disposed-control reason as RenderInChatSearchChrome.
                foreach (var c in _chatListPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
                _chatListPanel.Controls.Clear();
                _selectedItem = null;
                AddInChatScopeChip();
                _inChatTotal = (res as Messages_MessagesSlice)?.count ?? (res as Messages_ChannelMessages)?.count ?? msgs.Count;
                AddSectionHeader(_inChatTotal + (_inChatTotal == 1 ? " MESSAGE FOUND" : " MESSAGES FOUND"));
            }
            int w = ContentWidth(_chatListPanel);
            foreach (var m in msgs)
            {
                var sender = m.from_id != null ? res.UserOrChat(m.from_id) : (m.peer_id != null ? res.UserOrChat(m.peer_id) : null);
                long sid = m.from_id?.ID ?? (m.peer_id?.ID ?? 0);
                string name = sender is User su ? DisplayName(su) : ((sender as ChatBase)?.Title ?? _inChatSearchEntry.Title);
                var entry = new ChatEntry
                {
                    Peer = _inChatSearchEntry.Peer, PeerId = sid, Title = name,
                    Preview = GetDisplayText(m), Date = m.date, FocusMessageId = m.ID, PeerInfo = sender
                };
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnInChatResultClick;
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(sid);
                if (item.Avatar == null && sender != null) LoadAvatar(entry);
            }
            if (msgs.Count > 0) _inChatOffsetId = msgs.Min(m => m.ID);   // oldest shown → next-page offset
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Tap a scoped result → jump to that message in the OPEN chat (load a window around it if it isn't in
        /// the current view), scroll + flash. The in-chat search stays up so you can step through matches.</summary>
        private async void OnInChatResultClick(object sender, EventArgs e)
        {
            var result = ((ChatListItemControl)sender).Entry;
            int id = result.FocusMessageId;
            if (_selectedChat == null || id <= 0) return;
            ShowColumn(false);                    // one column: the chat, at the match (back shows the results again)
            if (!ScrollToAndFlash(id))            // not in the loaded window → reload around it, then flash
            {
                await LoadHistoryAsync(_selectedChat, id);
                ScrollToAndFlash(id);
            }
        }

        /// <summary>Pages OLDER scoped matches when the results list nears the bottom (Messages_Search offset_id).</summary>
        private async void CheckInChatPaging()
        {
            if (_inChatPaging || _inChatSearchEntry == null || _inChatQuery == null || _inChatOffsetId == 0) return;
            int pos = -_chatListPanel.AutoScrollPosition.Y, viewport = _chatListPanel.ClientSize.Height, content = _chatListPanel.DisplayRectangle.Height;
            if (!(content > viewport && pos + viewport >= content - Px(200))) return;
            _inChatPaging = true;
            try
            {
                var q = _inChatQuery; int off = _inChatOffsetId;
                var res = await _service.SearchInChatAsync(_inChatSearchEntry.Peer, q, off, 40, InChatFilterOf(_inChatFilter), _inChatFromMe ? new InputPeerSelf() : null);
                if (_inChatSearchEntry == null || _inChatQuery != q) return;   // exited / query changed mid-fetch
                if (res != null && res.Messages != null && res.Messages.OfType<Message>().Any())
                    RenderInChatResults(res, replace: false);
            }
            catch { }
            finally { _inChatPaging = false; }
        }

        private async void OnChatItemClick(object sender, EventArgs e)
        {
            if (LogOn) Logger.Diag("[OPEN] row click peer=" + ((ChatListItemControl)sender).Entry.PeerId);   // R7 (BATCH-TA-6/P1)
            await OpenChat(((ChatListItemControl)sender).Entry, 0);
        }

        // OPENCHAT-ONCE: a touch-synthesized tap (posted DOWN+UP pair) raises the row's Click TWICE
        // ~44ms apart (mouse clicks raise once — log-convicted); no duplicate wiring exists to delete,
        // so a per-peer latch absorbs the echo. Focused opens (pin jumps / search) are exempt —
        // rapid same-chat re-entry with a focus id is legitimate.
        private long _openLatchPeer;
        private int _openLatchTick;

        /// <summary>Selects a chat (highlighting its row if present) and loads its history.</summary>
        private async System.Threading.Tasks.Task OpenChat(ChatEntry entry, int focusMessageId)
        {
            if (focusMessageId == 0 && entry.PeerId == _openLatchPeer && Environment.TickCount - _openLatchTick < 500)
            {
                if (LogOn) Logger.Diag("[OPEN] duplicate open suppressed peer=" + entry.PeerId);   // R7 (BATCH-TA-6/P1)
                return;
            }
            _openLatchPeer = entry.PeerId; _openLatchTick = Environment.TickCount;
            if (_inChatSearchEntry != null) ExitInChatSearch();   // INCHAT-SEARCH: opening a (different) chat leaves in-chat search
            if (LogOn) Logger.Diag("[OPEN] OpenChat peer=" + entry.PeerId + " focus=" + focusMessageId);   // R7 (BATCH-TA-6/P1)
            if (_editTarget != null) CancelReplyByUser();   // R5: leaving mid-edit keeps what was typed BEFORE the edit as the draft
            PersistDraftForCurrentChat();   // DRAFTS: save the chat we're LEAVING (its composer text → server draft) before switching
            ClearThreadMode();        // COMMENTS-NAV-FIX Bug 2: leaving ANY comment thread — clear _thread so the loaders,
                                      // composer, and send target the NEW chat (LoadHistoryAsync below then resets the panel).
            _selectedChat = entry;
            ShowColumn(false);   // one column: the chat comes to the front (before its history loads, at its final width)
            if (_chatSearchBtn != null) _chatSearchBtn.Visible = true;   // INCHAT-SEARCH: the header magnifier appears with an open chat
            if (_chatMenuBtn != null) _chatMenuBtn.Visible = true;        // TA-21/S1a: the ⋮ appears with it
            if (_callBtn != null)   // 2026-10-16: the phone, for a person; a group's or channel's voice chat (MainForm.GroupCalls)
            {
                bool person = CanCall(entry);
                _callBtn.Visible = person || CanVoiceChat(entry);
                _callBtn.AccessibleName = IsGroupOrChannel(entry) ? "Voice chat" : "Call";
                _callBtn.Invalidate();
                PlaceCallButton();
            }
            if (_dockBtn != null) _dockBtn.Visible = true;                // TA-23/D1c: …and the dock toggle
            // TA-24: re-target the Info pane at the newly-opened chat, but only if it is actually on screen
            // — building it runs ProfileForm.LoadDetails, i.e. network, and a closed dock must not pay that.
            if (_dock != null && _dock.Visible && _dockPane == DockPane.Info) EnsureDockProfile();
            else DropDockProfile();
            // FORUM-TOPICS: a forum group → show the topic bar + fetch its topics (async); any other chat → hide + clear.
            // Opening still loads the flat all-topics history below (FORUM-GROUPS-FIX preserved) — the bar is additive.
            if (entry.PeerInfo is Channel fch && (fch.flags & Channel.Flags.forum) != 0)
            {
                _currentForumEntry = entry; _selectedTopicId = 0; _forumTopics = null;
                RebuildTopicBar(); ShowTopicBar(true);   // shown now; chips populate when the async fetch lands
                var _ft = LoadForumTopicsAsync(entry);
            }
            else if (_currentForumEntry != null)
            {
                _currentForumEntry = null; _forumTopics = null; _selectedTopicId = 0;
                RebuildTopicBar(); ShowTopicBar(false);
            }
            ResetBotUi();             // clear any previous bot Menu button / reply keyboard
            LoadPinnedAsync(entry);   // refresh the pinned-messages bar for this chat

            var row = FindChatItem(entry.PeerId);
            if (_selectedItem != null && _selectedItem != row) _selectedItem.Selected = false;
            _selectedItem = row;
            if (row != null)
            {
                row.Selected = true;
                // NB: do NOT clear the unread badge on open — it clears only when the user reaches the
                // bottom (MarkCaughtUp), so opening at the first-unread message keeps the badge meaningful.
                row.Invalidate();
                UpdateTrayTooltip();
            }

            ShowComposeFooter();   // default to the normal composer; the state machine refines it below
            _typing = false; _typingTimer?.Stop();
            UpdateHeaderStatus();
            // PEER-PRESENTATION + PRESENCE: groups take the presence refresh (ONE fetch = members+online,
            // 3.3's per-open refresh); users/broadcasts keep the count ladder (dialog-cached → ≤1 lazy fetch).
            if (entry.PeerInfo is Chat || (entry.PeerInfo is Channel mgc && (mgc.flags & Channel.Flags.broadcast) == 0))
                RefreshOpenGroupPresence();
            else
                MaybeFetchChatCount(entry);
            await LoadHistoryAsync(entry, focusMessageId);
            if (_selectedChat == entry)
            {
                ResolveAndApplyComposer(entry);   // history loaded → resolve footer state
                LoadBotMenuAsync(entry);          // bot? → fetch commands + reveal the Menu button
                ApplyInitialReplyKeyboard();      // restore the latest reply-keyboard markup in view
                LoadDraftIntoComposer(entry);     // DRAFTS: restore this chat's saved draft (or clear the previous chat's text)
            }
        }

        // ── Composer footer state machine (Batch CF1) ───────────────────────

        /// <summary>Shows the normal "Write a message" composer (the COMPOSE state).</summary>
        private void ShowComposeFooter()
        {
            _footerKind = ComposerKind.Compose;
            UpdateDockSources();   // TA-23/D1d — the dock follows the composer, live (no chat switch needed)
            if (_footerBar != null) _footerBar.Visible = false;
            if (_composerBar != null) { _composerBar.Visible = true; _composerBar.BringToFront(); }
            _messageInput.Enabled = true;
            _sendButton.Enabled = true;
            _attachButton.Enabled = true; _attachButton.Invalidate();
            _micButton.Enabled = true; _micButton.Invalidate();
            _emojiButton.Enabled = true; _emojiButton.Invalidate();
        }

        /// <summary>Renders the footer matching a resolved state — composer, a centered button, or a label.</summary>
        private void ApplyComposerState(ComposerState st)
        {
            _footerKind = st.Kind;
            if (st.Kind == ComposerKind.Compose) { ShowComposeFooter(); return; }   // that path updates the dock
            UpdateDockSources();   // TA-23/D1d — every non-compose state hides the Emoji source

            // Non-compose: hide + gate the composer, show the footer bar.
            if (_composerBar != null) _composerBar.Visible = false;
            _messageInput.Enabled = false;
            _sendButton.Enabled = false;

            string label;
            bool isButton;
            switch (st.Kind)
            {
                case ComposerKind.Join: label = "Join"; isButton = true; break;
                case ComposerKind.MuteUnmute: label = st.Muted ? "Unmute" : "Mute"; isButton = true; break;
                case ComposerKind.BotStart: label = "Start"; isButton = true; break;
                case ComposerKind.Blocked: label = "Unblock"; isButton = true; break;
                case ComposerKind.Restricted:
                    label = st.HasCountdown ? "You can send again in " + FormatComposerUntil(st.Until)
                                            : "You can't send messages here";
                    isButton = false; break;
                case ComposerKind.SlowmodeWait:
                    label = "Slow mode — wait " + FormatComposerUntil(st.Until);
                    isButton = false; break;
                default: ShowComposeFooter(); return;
            }
            if (_footerBar == null) return;
            if (isButton) _footerBar.SetButton(label); else _footerBar.SetLabel(label);
            _footerBar.Visible = true;
            _footerBar.BringToFront();
            StartComposerCountdown(st);   // T7: the label counts down; at zero the composer comes back
        }

        // ── T7: the footer's countdown (slow mode, a timed restriction) ─────────────────────────────────────────────
        private System.Windows.Forms.Timer _composerCountdown;
        private ComposerState _countdownState;
        private ChatEntry _countdownChat;

        private void StartComposerCountdown(ComposerState st)
        {
            bool counting = (st.Kind == ComposerKind.SlowmodeWait || st.Kind == ComposerKind.Restricted) && st.HasCountdown;
            if (!counting) { if (_composerCountdown != null) _composerCountdown.Stop(); return; }
            _countdownState = st;
            _countdownChat = _selectedChat;
            if (_composerCountdown == null)
            {
                _composerCountdown = new System.Windows.Forms.Timer { Interval = 1000 };
                _composerCountdown.Tick += (s, e) => ComposerCountdownTick();
            }
            _composerCountdown.Start();
        }

        private void ComposerCountdownTick()
        {
            if (IsDisposed || _selectedChat == null || !ReferenceEquals(_selectedChat, _countdownChat) || _footerKind != _countdownState.Kind)
            { _composerCountdown.Stop(); return; }
            if (_countdownState.Until <= DateTime.UtcNow)
            {
                _composerCountdown.Stop();
                ResolveAndApplyComposer(_selectedChat);   // the wait is over: re-resolve (normally back to the composer)
                return;
            }
            if (_footerBar == null) return;
            _footerBar.SetLabel(_countdownState.Kind == ComposerKind.SlowmodeWait
                ? "Slow mode — wait " + FormatComposerUntil(_countdownState.Until)
                : "You can send again in " + FormatComposerUntil(_countdownState.Until));
        }

        /// <summary>Gathers the inputs (fetching UserFull for the blocked flag) and applies the footer state.</summary>
        private async void ResolveAndApplyComposer(ChatEntry entry)
        {
            if (entry == null || entry != _selectedChat) return;
            // COMMENTS-NAV-FIX Bug 1: in a comment thread ALWAYS show the composer so a NON-member can post directly
            // (Telegram allows commenting without joining). Never gate it behind a "Join" footer — join is only a
            // FALLBACK if the server actually rejects the post (PostThreadComment), never a prerequisite to type.
            if (_thread != null) { ShowComposeFooter(); return; }
            UserFull uf = null;
            if (entry.PeerInfo is User u && entry.Peer is InputPeerUser)
            {
                try { uf = await _service.GetUserFullAsync(u); } catch { }
                if (entry != _selectedChat) return;   // chat switched while fetching
            }
            // T7: slow-mode timing is the only full-only field the resolver needs — fetched only for a supergroup that
            // HAS slow mode and where it applies to us (not the creator, not an admin): one bounded call, most chats none.
            ChannelFull cf = null;
            if (entry.PeerInfo is Channel sch && (sch.flags & Channel.Flags.slowmode_enabled) != 0
                && (sch.flags & (Channel.Flags.broadcast | Channel.Flags.creator)) == 0 && sch.admin_rights == null)
            {
                try { var full = await _service.GetChannelFullAsync(sch); cf = full != null ? full.full_chat as ChannelFull : null; } catch { }
                if (entry != _selectedChat) return;
            }
            var st = ComposerResolver.Resolve(entry.PeerInfo, cf, uf, entry.Muted, _currentChatMessages.Count == 0);
            ApplyComposerState(st);
        }

        private static string FormatComposerUntil(DateTime until)
        {
            var ts = until - DateTime.UtcNow;
            if (ts.TotalHours >= 1) return (int)ts.TotalHours + "h " + ts.Minutes + "m";
            if (ts.TotalMinutes >= 1) return (int)ts.TotalMinutes + "m " + ts.Seconds.ToString("00") + "s";   // T7: it ticks
            return Math.Max(1, (int)Math.Ceiling(ts.TotalSeconds)) + "s";
        }

        /// <summary>The footer button was tapped — dispatch by the current state.</summary>
        private void OnFooterAction()
        {
            var entry = _selectedChat;
            if (entry == null) return;
            switch (_footerKind)
            {
                case ComposerKind.Join: DoJoin(entry); break;
                case ComposerKind.MuteUnmute: ToggleChatMute(entry); break;   // re-resolves the label at its end
                case ComposerKind.BotStart: DoBotStart(entry); break;
                case ComposerKind.Blocked: DoUnblock(entry); break;
            }
        }

        private async void DoJoin(ChatEntry entry)
        {
            if (!(entry.Peer is InputPeerChannel ipc)) return;
            try { await _service.JoinChannelAsync(ipc); }
            catch (Exception ex) { ThemedDialog.Show(this, "Join", "Couldn't join: " + ex.Message, "OK"); return; }
            if (entry.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;   // reflect membership locally
            if (entry == _selectedChat) ResolveAndApplyComposer(entry);          // → COMPOSE, or Mute for a broadcast
        }

        // ── COMMENTS-JOIN-FLYOUT: the thread "Join the group" bar (docked above the composer, row 7) ──

        /// <summary>Single source of truth for the join bar's visibility: shows it iff we're in a comment thread,
        /// the linked group's Channel still carries the 'left' flag (not a member), and it wasn't dismissed this
        /// thread. Toggles row 7 to zero height when hidden so it never affects the composer/list layout.</summary>
        private void UpdateThreadJoinBar()
        {
            bool show = _thread != null && !_joinBarDismissed
                        && _thread.GroupEntry != null
                        && _thread.GroupEntry.Peer is InputPeerChannel
                        && _thread.GroupEntry.PeerInfo is Channel ch && (ch.flags & Channel.Flags.left) != 0;
            if (_threadJoinBar != null)
            {
                if (show) { _threadJoinBar.AccentColor = _accent; _threadJoinBar.IsDark = _dark; }
                _threadJoinBar.Visible = show;
                _threadJoinBar.Invalidate();
            }
            SetRowPx(_rightLayout, 8, show ? 48 : 0);   // FORUM-TOPICS: thread join bar row 7→8 (step 3.4: logical px)
        }

        /// <summary>✕ on the join bar — hide it for the rest of this thread session. Posting still works unjoined
        /// (membership is optional); a fresh thread open clears the latch and re-offers the bar.</summary>
        private void DismissThreadJoinBar()
        {
            _joinBarDismissed = true;
            UpdateThreadJoinBar();
        }

        /// <summary>Join the linked discussion group (optional). Reuses <see cref="TelegramService.JoinChannelAsync"/>.
        /// On success the bar hides (membership reflected locally so replies now arrive as normal group messages);
        /// on failure (private / invite-only / FLOOD_WAIT) a themed message shows and the bar stays for retry.</summary>
        private async void DoThreadJoin()
        {
            var t = _thread;
            var ge = t != null ? t.GroupEntry : null;
            if (ge == null || !(ge.Peer is InputPeerChannel ipc)) return;
            try
            {
                await _service.JoinChannelAsync(ipc);
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + ge.PeerId + " fail: " + ex.Message);
                ThemedDialog.Show(this, "Join", "Couldn't join the group:\n" + ex.Message, "OK");
                return;   // bar stays so the user can retry or dismiss
            }
            if (ge.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;   // reflect membership locally (same as DoJoin)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + ge.PeerId + " ok");
            if (_thread == t) UpdateThreadJoinBar();   // now a member → the bar hides itself
        }

        private async void DoBotStart(ChatEntry entry)
        {
            try { await _service.SendTextAsync(entry.Peer, "/start"); }
            catch (Exception ex) { ThemedDialog.Show(this, "Start", "Couldn't start the bot: " + ex.Message, "OK"); return; }
            if (entry == _selectedChat) ApplyComposerState(new ComposerState { Kind = ComposerKind.Compose });
        }

        // ── Bot menu + commands + reply keyboard (Batch BOT-2) ──────────────

        /// <summary>Resets all bot UI on chat switch (hide the Menu button + clear any reply keyboard).</summary>
        private void ResetBotUi()
        {
            _currentBotInfo = null;
            _replyKbSingleUse = false;
            if (_botMenuButton != null) _botMenuButton.Visible = false;
            SetColPx(_composerColumns, 0, 0);
            if (_replyKb != null) _replyKb.Clear();
            if (_messageInput != null) _messageInput.Hint = "Write a message…";
            SyncReplyKeyboardHeight();
        }

        /// <summary>For a bot chat, fetch BotInfo (commands + menu_button) and reveal the composer Menu button.</summary>
        private async void LoadBotMenuAsync(ChatEntry entry)
        {
            if (entry == null || !(entry.PeerInfo is User u) || (u.flags & User.Flags.bot) == 0) return;
            try
            {
                var full = await _service.GetUserFullAsync(u);
                if (entry != _selectedChat) return;
                _currentBotInfo = full != null ? full.bot_info : null;
                bool hasCommands = _currentBotInfo != null && _currentBotInfo.commands != null && _currentBotInfo.commands.Length > 0;
                bool webAppMenu = _currentBotInfo != null && _currentBotInfo.menu_button is BotMenuButton;
                bool show = hasCommands || webAppMenu;
                if (_botMenuButton != null)
                {
                    bool webMenu = webAppMenu && !hasCommands;   // web-app → "Menu" text; else the drawn "≡" icon
                    _botMenuIcon = !webMenu;
                    _botMenuButton.Text = webMenu ? "Menu" : "";
                    _botMenuButton.Visible = show;
                    _botMenuButton.Invalidate();
                }
                SetColPx(_composerColumns, 0, show ? 48 : 0);   // step 3.4: logical px
                System.Diagnostics.Debug.WriteLine("[BOT] menu loaded cmds=" + (hasCommands ? _currentBotInfo.commands.Length : 0) + " webapp=" + webAppMenu);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] menu load err: " + ex.Message); }
        }

        /// <summary>The composer Menu button: a WebApp menu → "not supported" notice; otherwise the command list.</summary>
        private void ShowBotMenu()
        {
            if (_currentBotInfo == null) return;
            bool hasCommands = _currentBotInfo.commands != null && _currentBotInfo.commands.Length > 0;
            if (_currentBotInfo.menu_button is BotMenuButton webapp && !hasCommands)
            {
                ThemedDialog.Show(this, string.IsNullOrEmpty(webapp.text) ? "Menu" : webapp.text,
                    "This bot's menu opens a web app, which isn't supported yet.", "OK");
                return;
            }
            if (!hasCommands) { ThemedDialog.Show(this, "Bot", "This bot has no command list.", "OK"); return; }
            var menu = new ThemedContextMenuStrip();
            foreach (var c in _currentBotInfo.commands)
            {
                string cmd = c.command;
                string label = "/" + cmd + (string.IsNullOrEmpty(c.description) ? "" : "   —   " + c.description);
                AddMenuItem(menu, label, () => SendBotCommand(cmd));
            }
            if (_botMenuButton != null && _botMenuButton.Visible)
            {
                menu.PrepareAt(_botMenuButton.PointToScreen(Point.Empty));   // step 3.1: measured as it opens (font, rows, icons)
                menu.ShowAt(_botMenuButton, new Point(0, -menu.PreferredSize.Height));
            }
            else menu.ShowAt(Cursor.Position);
        }

        private async void SendBotCommand(string command)
        {
            if (_selectedChat == null || string.IsNullOrEmpty(command)) return;
            try { await _service.SendTextAsync(_selectedChat.Peer, "/" + command.TrimStart('/')); System.Diagnostics.Debug.WriteLine("[BOT] command sent /" + command); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] command err: " + ex.Message); }
        }

        private bool _replyKbDpiHooked;

        /// <summary>Sets the reply-keyboard row height from the control's desired height (and its host visibility).</summary>
        private void SyncReplyKeyboardHeight()
        {
            if (_replyKb == null || _rightLayout == null) return;
            // Step 3.4: the control re-measures itself on a DPI change and says so; the row is then SET again from it.
            if (!_replyKbDpiHooked) { _replyKbDpiHooked = true; _replyKb.DesiredHeightChanged += (s, e) => SyncReplyKeyboardHeight(); }
            // DesiredHeight is PHYSICAL px at the control's DPI (step 3.4) — written as is, never through Px/SetRowPx.
            int h = _replyKb.HasButtons ? _replyKb.DesiredHeight : 0;
            _rightLayout.RowStyles[7].Height = h;   // FORUM-TOPICS: reply keyboard row 6→7
            if (_replyKbHost != null) _replyKbHost.Visible = h > 0;
        }

        /// <summary>Applies a message's reply markup to the chat: show/replace the reply keyboard, hide it, or force a reply.</summary>
        /// <summary>The rows of a reply keyboard (pure; the gallery probe feeds it every WTC 4.4.9 button type).</summary>
        internal static List<List<ReplyKeyboardControl.RKButton>> ReplyKeyboardRows(ReplyKeyboardMarkup rk)
        {
            var rows = new List<List<ReplyKeyboardControl.RKButton>>();
            if (rk == null || rk.rows == null) return rows;
            foreach (var row in rk.rows)
            {
                if (row == null || row.buttons == null) continue;
                var cells = new List<ReplyKeyboardControl.RKButton>();
                foreach (var btn in row.buttons)
                {
                    if (btn == null) continue;
                    // WTC 4.4.9: one KeyboardButton { text, type }; type == null is a plain text button.
                    var b = new ReplyKeyboardControl.RKButton { Label = btn.text ?? "" };
                    var t = btn.type;
                    if (t is ButtonTypeRequestPhone) b.Kind = ReplyKeyboardControl.RKKind.RequestPhone;
                    else if (t is ButtonTypeRequestGeoLocation) b.Kind = ReplyKeyboardControl.RKKind.RequestGeo;
                    else if (t is ButtonTypeRequestPeer) b.Kind = ReplyKeyboardControl.RKKind.RequestPeer;
                    else if (t is ButtonTypeRequestPoll) b.Kind = ReplyKeyboardControl.RKKind.RequestPoll;
                    else if (t is ButtonTypeSimpleWebView) b.Kind = ReplyKeyboardControl.RKKind.WebView;
                    else b.Kind = ReplyKeyboardControl.RKKind.Text;
                    cells.Add(b);
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            return rows;
        }

        private void ApplyChatReplyMarkup(ReplyMarkup markup)
        {
            if (markup is ReplyKeyboardMarkup rk && rk.rows != null)
            {
                var rows = ReplyKeyboardRows(rk);
                _replyKb.SetButtons(rows);
                _replyKbSingleUse = (rk.flags & ReplyKeyboardMarkup.Flags.single_use) != 0;
                if ((rk.flags & ReplyKeyboardMarkup.Flags.has_placeholder) != 0 && !string.IsNullOrEmpty(rk.placeholder))
                    _messageInput.Hint = rk.placeholder;
                SyncReplyKeyboardHeight();
                System.Diagnostics.Debug.WriteLine("[BOT] reply keyboard shown rows=" + rows.Count + " single_use=" + _replyKbSingleUse);
            }
            else if (markup is ReplyKeyboardHide)
            {
                _replyKb.Clear(); SyncReplyKeyboardHeight();
                _messageInput.Hint = "Write a message…";
                System.Diagnostics.Debug.WriteLine("[BOT] reply keyboard hidden");
            }
            else if (markup is ReplyKeyboardForceReply fr)
            {
                _replyKb.Clear(); SyncReplyKeyboardHeight();
                if ((fr.flags & ReplyKeyboardForceReply.Flags.has_placeholder) != 0 && !string.IsNullOrEmpty(fr.placeholder))
                    _messageInput.Hint = fr.placeholder;
                if (_messageInput.Enabled) { try { _messageInput.Focus(); } catch { } }
                System.Diagnostics.Debug.WriteLine("[BOT] force reply");
            }
        }

        /// <summary>On chat open: apply the latest reply-keyboard markup found in the loaded history (newest wins).</summary>
        private void ApplyInitialReplyKeyboard()
        {
            for (int i = _currentChatMessages.Count - 1; i >= 0; i--)
            {
                var mk = _currentChatMessages[i].reply_markup;
                if (mk is ReplyKeyboardMarkup || mk is ReplyKeyboardHide || mk is ReplyKeyboardForceReply)
                { ApplyChatReplyMarkup(mk); return; }
            }
        }

        private async void OnReplyKeyboardButton(ReplyKeyboardControl.RKButton b)
        {
            if (b == null || _selectedChat == null) return;
            if (b.Kind == ReplyKeyboardControl.RKKind.Text)
            {
                try { await _service.SendTextAsync(_selectedChat.Peer, b.Label ?? ""); System.Diagnostics.Debug.WriteLine("[BOT] reply-kbd text sent: " + b.Label); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] reply-kbd send err: " + ex.Message); }
                if (_replyKbSingleUse) { _replyKb.Clear(); SyncReplyKeyboardHeight(); }
                return;
            }
            if (b.Kind == ReplyKeyboardControl.RKKind.RequestPhone)
            {
                int c = ThemedDialog.Show(this, "Share phone number", "Send your phone number to this bot?", "Share", "Cancel");
                try { ActiveControl = null; } catch { }
                if (c != 0) return;
                var me = _service.Me;
                if (me != null) { try { await _service.SendContactAsync(_selectedChat.Peer, me.phone, me.first_name, me.last_name); } catch { } }
                if (_replyKbSingleUse) { _replyKb.Clear(); SyncReplyKeyboardHeight(); }
                return;
            }
            ThemedDialog.Show(this, "Not supported", "This button type (location / peer / poll request) isn't supported yet.", "OK");
        }

        private async System.Threading.Tasks.Task StartBotIfPossible(string startParam)
        {
            var u = _selectedChat != null ? _selectedChat.PeerInfo as User : null;
            if (u == null || (u.flags & User.Flags.bot) == 0) return;
            try { await _service.StartBotAsync(u, startParam); System.Diagnostics.Debug.WriteLine("[BOT] start deep-link param=" + startParam); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] start err: " + ex.Message); }
        }

        private async void DoUnblock(ChatEntry entry)
        {
            try { await _service.SetBlockedAsync(entry.Peer, false); }
            catch (Exception ex) { ThemedDialog.Show(this, "Unblock", "Couldn't unblock: " + ex.Message, "OK"); return; }
            if (entry == _selectedChat) ApplyComposerState(new ComposerState { Kind = ComposerKind.Compose });
        }

    }
}
