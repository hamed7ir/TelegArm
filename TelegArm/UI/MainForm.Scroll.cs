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
        private int DistanceFromBottom()
        {
            int max = Math.Max(0, _messagePanel.DisplayRectangle.Height - _messagePanel.ClientSize.Height);
            return Math.Max(0, max - (-_messagePanel.AutoScrollPosition.Y));
        }
        private bool AtBottom(int threshold) => DistanceFromBottom() <= threshold;

        private void PositionJumpButton()
        {
            if (_jumpBtn == null || _msgHost == null) return;
            // Clear of the themed scrollbar's lane at THIS DPI (step 3.4 review: a fixed 20 px let the button cover the lane
            // at 192+ DPI). 12 + 8 = the old 14 + 6 at 96.
            int x = _msgHost.ClientSize.Width - _jumpBtn.Width - ThemedScrollBar.LaneWidth(WindowDpiValue) - Px(8);
            int y = _msgHost.ClientSize.Height - _jumpBtn.Height - Px(10);
            _jumpBtn.Location = new Point(Math.Max(0, x), Math.Max(0, y));
            // T2: the "@" and heart buttons stack above it (above its spot when it is hidden)
            int top = _jumpBtn.Visible ? y - Px(8) : y + _jumpBtn.Height;
            foreach (var b in new Control[] { _mentionBtn, _reactionBtn })
            {
                if (b == null || !b.Visible) continue;
                top -= b.Height;
                b.Location = new Point(Math.Max(0, x), Math.Max(0, top));
                top -= Px(8);
            }
        }

        /// <summary>BUBBLE-DATETIME (C): the topmost visible message bubble, or null. Its display Top is the scroll
        /// signal (changes with touch/wheel/scrollbar alike) that distinguishes a genuine scroll from the idle watchdog.</summary>
        private MessageBubbleControl TopVisibleBubble()
        {
            try
            {
                MessageBubbleControl best = null;
                foreach (var c in _messagePanel.Controls)
                    if (c is MessageBubbleControl b && b.MessageId != 0 && b.Bottom > 0 && (best == null || b.Top < best.Top))
                        best = b;
                return best;
            }
            catch { return null; }
        }

        /// <summary>BUBBLE-DATETIME (C): the local DateTime of the topmost visible bubble (default if none).</summary>
        private DateTime TopVisibleDate()
        {
            var b = TopVisibleBubble();
            return b != null && b.Date != default(DateTime) ? b.Date.ToLocalTime() : default(DateTime);
        }

        /// <summary>BUBBLE-DATETIME (C): Telegram-style day label — Today / Yesterday / "MMMM d" (+ year when older).</summary>
        private static string FormatDayLabel(DateTime local)
        {
            var today = DateTime.Now.Date;
            if (local.Date == today) return "Today";
            if (local.Date == today.AddDays(-1)) return "Yesterday";
            return local.Year == today.Year ? local.ToString("MMMM d") : local.ToString("MMMM d, yyyy");
        }

        /// <summary>BUBBLE-DATETIME (C): show/refresh the floating day pill for the topmost visible message and keep
        /// the fade timer armed. The topmost-scan is throttled (~80ms) so per-pan-tick calls (~100Hz on touch) stay
        /// cheap — freeze-era discipline; only the fade tick is written every call. Overlay only; never touches paging.</summary>
        private void UpdateDateFlyout()
        {
            if (_dateFlyout == null || _msgHost == null || _selectedChat == null) return;
            int now = Environment.TickCount;
            // DATE-FLYOUT-TUNE (auto-hide fix): the 200ms _scrollWatch watchdog ALSO calls here on every tick — without
            // this gate it reset _dateFlyoutScrollTick forever, so the idle fade never fired ("always shown"). Count it as
            // scroll activity ONLY when the top-visible bubble ACTUALLY moved (reflects touch/wheel/scrollbar equally; it
            // reads the RESULT of scrolling, touching NO scroll code). Idle ticks now fall through → the ~5s fade runs.
            var topBubble = TopVisibleBubble();
            int sig = topBubble != null ? topBubble.Top : int.MinValue;
            if (sig == _dateFlyoutTopSig) return;                          // no real scroll (idle watchdog tick) → leave the pill + fade clock alone
            _dateFlyoutTopSig = sig;
            _dateFlyoutScrollTick = now;                                    // genuine scroll → (re)start the ~5s idle clock (DateFlyoutTick reads this)
            if (_dateFlyoutTimer != null && !_dateFlyoutTimer.Enabled) _dateFlyoutTimer.Start();
            if (_dateFlyout.Visible && unchecked(now - _dateFlyoutCalcTick) < 80) return;   // throttle the control scan
            _dateFlyoutCalcTick = now;
            var d = TopVisibleDate();
            if (d == default(DateTime)) return;
            string label = FormatDayLabel(d);
            if (_dateFlyout.Text != label)
            {
                _dateFlyout.Text = label;
                var sz = TextRenderer.MeasureText(label, _dateFlyout.Font);
                _dateFlyout.Size = new Size(sz.Width + Px(24), Math.Max(Px(24), sz.Height + Px(6)));   // 24 x 24 at 96, as before
            }
            _dateFlyout.Left = Math.Max(0, (_msgHost.ClientSize.Width - _dateFlyout.Width) / 2);
            _dateFlyout.Top = DateFlyoutTopOffset + Px(8);   // DATE-FLYOUT-TUNE: offset-able so a future top TOPIC bar can push it below itself
            _dateFlyout.Alpha = 255;
            if (!_dateFlyout.Visible) { _dateFlyout.Visible = true; _dateFlyout.BringToFront(); }
            else _dateFlyout.Invalidate();
        }

        // Idle → fade the day pill out (single timer; runs only while shown/fading, then stops).
        private void DateFlyoutTick(object sender, EventArgs e)
        {
            if (_dateFlyout == null || !_dateFlyout.Visible) { _dateFlyoutTimer?.Stop(); return; }
            if (unchecked(Environment.TickCount - _dateFlyoutScrollTick) < DateFlyoutHoldMs) return;   // DATE-FLYOUT-TUNE: hold ~5s after the last scroll, then fade (a scroll resets _dateFlyoutScrollTick)
            _dateFlyout.Alpha -= 45;
            if (_dateFlyout.Alpha <= 0) { _dateFlyout.Visible = false; _dateFlyoutTimer.Stop(); }
            else _dateFlyout.Invalidate();
        }

        /// <summary>BUBBLE-DATETIME (C): the floating day pill — a rounded translucent chip with centered text and a
        /// settable Alpha for the fade. Owner-drawn; a host child, never a scroll child.
        /// Redesign step 3.2: DPI-neutral by construction — it paints in its own bounds (radius = Height / 2) with its
        /// explicit Control.Font, and DpiScaler rescales both; its size, offset and font are set by the host
        /// (UpdateDateFlyout, BuildRightPanel), which step 3.4 converts.</summary>
        private sealed class DateFlyoutPill : Control
        {
            public bool IsDark { get; set; }
            public Color Accent { get; set; } = ThemeHelper.Palette.PillDefaultAccent;   // accent-driven fill (set live)
            public int Alpha = 255;
            public DateFlyoutPill()
            {
                TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int a = Math.Max(0, Math.Min(255, Alpha));
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Color fill = ColorMath.WithAlpha(Accent, a);   // accent-driven pill (Alpha = fade)
                using (var b = new SolidBrush(fill))
                using (var p = DrawHelper.RoundedRect(r, Height / 2))
                    g.FillPath(b, p);
                TextRenderer.DrawText(g, Text, Font, r, ColorMath.WithAlpha(ColorMath.OnColor(Accent), a),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                // Clip the control to the pill shape so the corners are truly ROUNDED — the transparent square
                // corners were showing the background through. Region matches the FillPath capsule (radius Height/2).
                if (Width > 0 && Height > 0)
                    using (var p = DrawHelper.RoundedRect(new Rectangle(0, 0, Width, Height), Height / 2))
                        Region = new Region(p);
            }
        }

        /// <summary>Updates the floating button's visibility/count and triggers the read when at the bottom.</summary>
        private void OnScrollPositionChanged()
        {
            if (_jumpBtn == null) return;
            bool hasMsgs = _selectedChat != null && _messagePanel.Controls.Count > 0;
            if (!hasMsgs) { if (_jumpBtn.Visible) _jumpBtn.Visible = false; return; }

            UpdateDateFlyout();   // BUBBLE-DATETIME (C): floating day pill (throttled scan; overlay only)
            CheckInlineVisibility();   // stop the playing inline video (WebM/GIF) if it scrolled out of view
            MaybeViewSponsored();   // fire viewSponsoredMessage once when the ad's full text scrolls into view

            // From a focused island, page newer toward the live tail before the user hits the bottom edge.
            if (!_atLiveTail && !_loadingNewer && DistanceFromBottom() <= 300)
                _ = LoadNewerMessages();

            if (_atLiveTail && AtBottom(40))   // at the REAL bottom → caught up
            {
                _jumpUnread = 0;
                if (_jumpBtn.Visible) { _jumpBtn.Visible = false; PositionJumpButton(); }   // T2: the stack moves down
                MarkCaughtUp();
            }
            else                // scrolled up, OR sitting at an island edge that isn't the true tail → show button
            {
                _jumpBtn.UnreadCount = _jumpUnread;
                if (!_jumpBtn.Visible) { _jumpBtn.Visible = true; PositionJumpButton(); _jumpBtn.BringToFront(); }
            }
        }

        private void JumpToBottomClicked()
        {
            _jumpUnread = 0;
            // From an island, a plain scroll only reaches the island edge — reload the bottom window so the
            // jump lands on the real latest message (simpler + guaranteed to reach the tail).
            if (!_atLiveTail && _selectedChat != null) { var _ = LoadHistoryAsync(_selectedChat); return; }
            ScrollMessagesToBottom();
            if (_jumpBtn != null) _jumpBtn.Visible = false;
            MarkCaughtUp();
        }

        /// <summary>Reads the open chat up to the latest loaded message — only when there's something newer
        /// (so it can't spam ReadHistory). This is the "caught up" trigger (reaching the bottom).</summary>
        private void MarkCaughtUp()
        {
            if (_selectedChat == null) return;
            int latest = _currentChatMessages.Count > 0
                ? _currentChatMessages[_currentChatMessages.Count - 1].ID
                : _selectedChat.TopMessageId;
            if (latest <= 0 || latest <= _selectedChat.ReadInboxMaxId) return;
            _selectedChat.ReadInboxMaxId = latest;
            _selectedChat.UnreadCount = 0;
            var _ = SafeReadHistory(_selectedChat.Peer, latest);
            FindChatItem(_selectedChat.PeerId)?.Invalidate();
            // TA-33/A4 — the user reached the bottom of this chat, so its Action Center entries are stale.
            ShellNotify.RemoveGroup(_selectedChat.PeerId);
            UpdateTrayTooltip();
            RefreshFolderBadges();   // TA-6b/G1 (DOWN): THE TA-6 GATE FAILURE — reading a chat in TelegArm
        }

        private async System.Threading.Tasks.Task SafeReadHistory(InputPeer peer, int maxId)
        {
            try { await _service.ReadHistoryAsync(peer, maxId); } catch { }
        }

        /// <summary>MENTION-REACTION: on opening a chat, clear its unread @mentions + reactions-to-you (server +
        /// local + row), so the "@" badge and heart glyph disappear once seen. Best-effort RPCs (fire-and-forget).</summary>
        private void MarkMentionsReactionsRead(ChatEntry entry)
        {
            if (entry == null) return;
            // T2: a DIFFERENT chat → the previous chat's jump lists go (a re-load of the same chat keeps them)
            if (!ReferenceEquals(_jumpListChat, entry)) { _jumpListChat = entry; _mentionIds.Clear(); _reactionIds.Clear(); UpdateMentionButtons(); }
            if (entry.UnreadMentions > 0)
            {
                int n = entry.UnreadMentions;
                entry.UnreadMentions = 0;
                var _ = CollectThenRead(entry, true, n);
            }
            if (entry.UnreadReactions > 0)
            {
                int n = entry.UnreadReactions;
                entry.UnreadReactions = 0;
                var _ = CollectThenRead(entry, false, n);
            }
            FindChatItem(entry.PeerId)?.Invalidate();
        }

        // ── T2: jump to the unread mentions / reactions ─────────────────────────────────────────────────────────────
        private ChatEntry _jumpListChat;
        private readonly List<int> _mentionIds = new List<int>(), _reactionIds = new List<int>();

        /// <summary>Redesign T2 — fetch the ids FIRST (the read below clears them on the server), keep them for the open
        /// chat's "@" / heart buttons, then mark them read as before (opening = seen: the list badge clears at once).
        /// Capped at the newest 100, the server's page.</summary>
        private async System.Threading.Tasks.Task CollectThenRead(ChatEntry entry, bool mentions, int count)
        {
            List<int> ids = null;
            try
            {
                ids = mentions ? await _service.GetUnreadMentionIdsAsync(entry.Peer, Math.Min(100, count))
                               : await _service.GetUnreadReactionIdsAsync(entry.Peer, Math.Min(100, count));
            }
            catch (Exception ex) { Logger.Diag("[JUMP] unread " + (mentions ? "mentions" : "reactions") + " fetch failed: " + ex.Message); }
            if (!IsDisposed && ids != null && ids.Count > 0 && ReferenceEquals(_selectedChat, entry) && ReferenceEquals(_jumpListChat, entry))
            {
                var list = mentions ? _mentionIds : _reactionIds;
                list.Clear();
                list.AddRange(ids);
                if (LogOn) Logger.Diag("[JUMP] " + ids.Count + " unread " + (mentions ? "mentions" : "reactions") + " to jump to");
                UpdateMentionButtons();
            }
            await SafeRead(() => mentions ? _service.ReadMentionsAsync(entry.Peer) : _service.ReadReactionsAsync(entry.Peer));
        }

        /// <summary>The "@" / heart buttons follow their lists (count badge, shown while anything is left).</summary>
        private void UpdateMentionButtons()
        {
            if (_mentionBtn == null || _reactionBtn == null) return;
            bool open = _selectedChat != null && ReferenceEquals(_jumpListChat, _selectedChat);
            _mentionBtn.UnreadCount = _mentionIds.Count;
            _reactionBtn.UnreadCount = _reactionIds.Count;
            _mentionBtn.Visible = open && _mentionIds.Count > 0;
            _reactionBtn.Visible = open && _reactionIds.Count > 0;
            PositionJumpButton();
            if (_mentionBtn.Visible) _mentionBtn.BringToFront();
            if (_reactionBtn.Visible) _reactionBtn.BringToFront();
        }

        /// <summary>A click: the OLDEST id left — scrolled to and flashed when it is loaded, else a history load centred on
        /// it (the reply-quote jump's path).</summary>
        private void JumpToNextUnread(bool mentions)
        {
            var list = mentions ? _mentionIds : _reactionIds;
            if (list.Count == 0 || _selectedChat == null) return;
            int id = list[0];
            list.RemoveAt(0);
            UpdateMentionButtons();
            if (LogOn) Logger.Diag("[JUMP] to unread " + (mentions ? "mention" : "reaction") + " id=" + id + " (" + list.Count + " left)");
            if (ScrollToAndFlash(id)) return;
            var chat = _selectedChat;
            BeginInvoke((Action)(async () =>
            {
                await OpenChat(chat, id);   // focused load centred on it (same chat: the lists are kept)
                ScrollToAndFlash(id);
            }));
        }

        private static async System.Threading.Tasks.Task SafeRead(Func<System.Threading.Tasks.Task> op)
        {
            try { await op(); } catch { }
        }

        private static int BubbleMsgId(Control c)
        {
            if (c is MessageBubbleControl b) return b.MessageId;
            if (c is VoiceBubbleControl v) return v.MessageId;
            return 0;
        }

        /// <summary>Finds the rendered row for a message id — any kind (text/photo/file/voice/audio).</summary>
        private Control FindMessageControl(int messageId)
        {
            foreach (Control c in _messagePanel.Controls)
            {
                if (BubbleMsgId(c) == messageId) return c;
                var mb = c as MessageBubbleControl;
                if (mb != null && mb.IsAlbum && mb.ContainsMessageId(messageId)) return mb;   // any album item → the album
            }
            return null;
        }

        /// <summary>Scrolls a message into view and flashes it via IFlashable (works for voice/audio too).</summary>
        private bool ScrollToAndFlash(int messageId)
        {
            TouchScroller.StopMomentum();   // 3.5: jump-to-message wins over a coast
            _messagePanel.PerformLayout();
            var c = FindMessageControl(messageId);
            if (c == null) return false;
            _messagePanel.ScrollControlIntoView(c);
            (c as IFlashable)?.Flash();
            return true;
        }

        /// <summary>Reply-quote tap (Part B): jump to the replied-to message in the OPEN chat and flash it.
        /// If it's loaded, scroll+flash immediately; if it's older than the loaded window, do a focused
        /// history load centered on it (same path as Show-in-chat / search / pinned-jump), then flash.</summary>
        private void JumpToReply(int replyToMsgId)
        {
            if (replyToMsgId <= 0) return;
            // REPLIES-INBOX: the reply-quote band is repurposed as a "View in chat" affordance — replyToMsgId is the
            // ENTRY's OWN id. Open the source discussion thread instead of an in-chat scroll.
            if (IsRepliesInbox) { OpenRepliesEntryThread(replyToMsgId); return; }
            if (ScrollToAndFlash(replyToMsgId)) return;     // already in the loaded window (thread OR normal)
            var chat = _selectedChat;
            if (chat == null) return;
            if (_thread != null)
            {
                // COMMENTS Option C: the target comment is older than the loaded thread window. STAY IN THE
                // THREAD — reload the thread island CENTERED on it (LoadHistoryAsync in thread mode swaps to
                // GetReplies via LoadWindowAsync, so this is scoped to the thread, not the whole group), then
                // flash. NEVER OpenChat here — that would exit the thread and open the full discussion group.
                BeginInvoke((Action)(async () =>
                {
                    await LoadHistoryAsync(chat, replyToMsgId);   // _thread!=null → GetReplies focused island
                    if (_thread != null) ScrollToAndFlash(replyToMsgId);   // add the highlight (best-effort if deleted)
                }));
                return;
            }
            BeginInvoke((Action)(async () =>
            {
                await OpenChat(chat, replyToMsgId);          // focused load centered on the target (same chat)
                ScrollToAndFlash(replyToMsgId);
            }));
        }

        // ── REPLIES-INBOX: entry source decoration + "View in chat" jump ─────────────────────────────

        /// <summary>Caches the SOURCE discussion group of a reply entry (from the history dict, keyed by group id) so
        /// "View in chat" can build an InputPeerChannel later without depending on the manager cache. No-op unless the
        /// message is a comment-reply (carries reply_to_peer_id).</summary>
        private void IngestRepliesSource(Message m, Messages_MessagesBase history)
        {
            var rh = m?.reply_to as MessageReplyHeader;
            if (rh == null || rh.reply_to_peer_id == null || history == null) return;
            var g = history.UserOrChat(rh.reply_to_peer_id);
            if (g == null) return;
            _repliesSourceCache[rh.reply_to_peer_id.ID] = g;
            if (g is ChatBase gc) _peerNames[gc.ID] = gc.Title;   // name cache for the header + future resolves
        }

        /// <summary>Restructures a Replies-inbox entry into Telegram's card layout: a QUOTED SOURCE block (reuses the
        /// reply-quote band — the source group name + who replied) at the top, the reply text/media as the body, then a
        /// distinct bottom "View in chat ›" ROW (a real hit region → <see cref="OpenRepliesEntryThread"/>). The stray
        /// "Forwarded from…" header is suppressed (the quoted block replaces it). Non-reply entries stay plain.</summary>
        private void DecorateRepliesBubble(MessageBubbleControl b, Message m)
        {
            var rh = m?.reply_to as MessageReplyHeader;
            if (b == null || rh == null || rh.reply_to_peer_id == null) return;

            // Source discussion group — the quoted block's bold header.
            string src = null;
            IPeerInfo g;
            if (_repliesSourceCache.TryGetValue(rh.reply_to_peer_id.ID, out g) && g != null)
                src = (g as ChatBase)?.Title ?? ((g as User) != null ? DisplayName((User)g) : null);
            if (string.IsNullOrEmpty(src) && _peerNames.TryGetValue(rh.reply_to_peer_id.ID, out var nm)) src = nm;

            // Who replied — the quoted block's second line (resolved from the entry's sender).
            string replier = null;
            if (m.from_id != null)
            {
                if (_peerNames.TryGetValue(m.from_id.ID, out var rn)) replier = rn;
                else { var ri = ResolvePeer(m.from_id); replier = ri is User ru ? DisplayName(ru) : (ri as ChatBase)?.Title; }
            }

            b.ForwardedFrom = null;                 // suppress the stray "Forwarded from…" — the quoted block replaces it
            b.ReplyToMsgId = m.ID;                  // own id → the quoted block is ALSO tappable (the jump lookup key)
            b.ReplySender = src ?? "Discussion";    // the SOURCE discussion group (bold, top of the quote)
            b.ReplyPreview = !string.IsNullOrEmpty(replier) ? replier : "replied to your comment";   // 2nd quote line
            b.ShowViewInChat = true;                // the distinct bottom "View in chat ›" row (the primary affordance)
            b.Measure();
        }

        /// <summary>Tap of a Replies entry's "View in chat" band: resolve the source group + thread root off the
        /// message and open the discussion thread (reuses the comment thread view, entered from the group side).</summary>
        private void OpenRepliesEntryThread(int entryMsgId)
        {
            var m = _currentChatMessages.FirstOrDefault(x => x.ID == entryMsgId);
            var rh = m?.reply_to as MessageReplyHeader;
            if (rh == null || rh.reply_to_peer_id == null)
            { ThemedDialog.Show(this, "Replies", "This entry has no source thread to open.", "OK"); return; }
            if (LogOn) System.Diagnostics.Debug.WriteLine("[REPLIES] viewinchat peer=" + rh.reply_to_peer_id.ID
                + " top=" + rh.reply_to_top_id + " msg=" + rh.reply_to_msg_id);

            IPeerInfo groupInfo;
            if (!_repliesSourceCache.TryGetValue(rh.reply_to_peer_id.ID, out groupInfo) || groupInfo == null)
                groupInfo = ResolvePeer(rh.reply_to_peer_id);
            if (groupInfo == null)
            { ThemedDialog.Show(this, "Replies", "Couldn't resolve the source group.", "OK"); return; }

            var groupEntry = EntryFromPeerInfo(groupInfo);
            if (groupEntry == null || !(groupEntry.Peer is InputPeerChannel))
            { ThemedDialog.Show(this, "Replies", "The source thread isn't available.", "OK"); return; }

            int rootId = rh.reply_to_top_id != 0 ? rh.reply_to_top_id : rh.reply_to_msg_id;
            var returnTo = _selectedChat;   // the Replies inbox — "‹ back" returns here
            var _ = EnterThreadFromGroup(groupEntry, rootId, rh.reply_to_msg_id, returnTo);
        }

        /// <summary>Opens a comment thread DIRECTLY from the discussion group + thread root (no channel post needed —
        /// the Replies entry already carries them). Mirrors <see cref="OpenComments"/> minus GetDiscussionMessage;
        /// ThreadCtx.ChannelPeer is left null (verified never read) and ReadDiscussion is skipped. Reuses the whole
        /// thread view (scoped GetReplies reads, posting via GroupPeer+GroupRootId, the join flyout, nav teardown).</summary>
        private async System.Threading.Tasks.Task EnterThreadFromGroup(ChatEntry groupEntry, int groupRootId, int focusMsgId, ChatEntry returnTo)
        {
            if (_thread != null || groupEntry == null) return;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] view-in-chat group=" + groupEntry.PeerId + " root=" + groupRootId + " focus=" + focusMsgId);
            try
            {
                _thread = new ThreadCtx
                {
                    ChannelPeer = null,      // unknown from the Replies inbox — the field is never read (verified)
                    PostMsgId = 0,
                    GroupPeer = groupEntry.Peer,
                    GroupRootId = groupRootId,
                    GroupEntry = groupEntry,
                    ReturnTo = returnTo,
                    ReturnAnchorId = 0
                };
                groupEntry.UnreadCount = 0;
                // TA-6b/A+B: this site did NOT publish the change at all — no row repaint, no tray, no
                // badges. Same shape as the other six read sites now.
                FindChatItem(groupEntry.PeerId)?.Invalidate();
                UpdateTrayTooltip();
                RefreshFolderBadges();   // (DOWN): entering a thread from the group reads the group
                _selectedChat = groupEntry;                 // paging / scroll / updates target the thread from here
                await LoadHistoryAsync(groupEntry, focusMsgId);   // thread mode → GetReplies focused island
                if (_thread == null) return;                // back hit while loading
                _chatTitle.Text = "‹ Comments";
                _chatStatus.Text = groupEntry.Title;
                ShowComposeFooter();
                _joinBarDismissed = false;
                UpdateThreadJoinBar();                      // offer Join if not a member of the source group
                if (focusMsgId > 0) ScrollToAndFlash(focusMsgId);   // land on the replied-to comment + flash
            }
            catch (Exception ex)
            {
                _thread = null;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] view-in-chat failed: " + ex.Message);
                ThemedDialog.Show(this, "Replies", "Couldn't open the thread:\n" + ex.Message, "OK");
            }
        }

        /// <summary>Scrolls the first message newer than read_inbox_max_id to the TOP of the viewport.
        /// Returns false if no such message is in the loaded window (caller falls back to the bottom).</summary>
        private bool ScrollToFirstUnread(ChatEntry entry)
        {
            TouchScroller.StopMomentum();   // 3.5: open-at-unread positioning wins over a coast
            _messagePanel.PerformLayout();
            foreach (Control c in _messagePanel.Controls)
            {
                int id = BubbleMsgId(c);
                if (id > 0 && id > entry.ReadInboxMaxId)
                {
                    _messagePanel.AutoScrollPosition = new Point(0, Math.Max(0, c.Top));
                    return true;
                }
            }
            return false;
        }

        private void ScrollMessagesToBottom()
        {
            TouchScroller.StopMomentum();   // 3.5: programmatic scroll (send / open-at-bottom) wins over a coast
            if (_messagePanel.Controls.Count == 0) return;
            // Make sure the scroll range reflects the just-added bubble…
            _messagePanel.PerformLayout();
            // …then scroll fully to the bottom so the panel's bottom padding is
            // revealed and the last bubble clears the input bar. (ScrollControlIntoView
            // would stop flush against the bar, leaving the bubble looking hidden.)
            _messagePanel.AutoScrollPosition = new Point(0, _messagePanel.VerticalScroll.Maximum);
        }

    }
}
