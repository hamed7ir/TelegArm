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
        // Quick reactions offered in the per-message context menu.
        private static readonly string[] QuickReactions = { "👍", "❤", "🔥", "🎉", "😁", "😢", "👎" };

        /// <summary>Adds a "React ▸" submenu of quick reactions to the message context menu.</summary>
        private void AddReactMenu(ContextMenuStrip menu, Message msg)
        {
            var react = new ToolStripMenuItem("☺   React");
            foreach (var em in QuickReactions)
            {
                var e2 = em;
                var it = new ToolStripMenuItem(em) { Font = FontHelper.UiAt(11f, WindowDpiValue) };   // shared: never disposed
                it.Click += (s, e) => BeginInvoke((Action)(() => ToggleReaction(msg, e2)));
                react.DropDownItems.Add(it);
            }
            menu.Items.Add(react);
            // Step 3.1: the 11 pt at the MENU's DPI (its open point's). The menu's own Opening ran first and set MenuDpi.
            var themed = menu as ThemedContextMenuStrip;
            if (themed != null)
                themed.Opening += (s, e) =>
                {
                    var f = FontHelper.UiAt(11f, themed.MenuDpi > 0 ? themed.MenuDpi : WindowDpiValue);
                    foreach (ToolStripItem ri in react.DropDownItems) ri.Font = f;
                };
        }

        /// <summary>Reads a message's reactions into bubble chips (skips custom-emoji reactions).</summary>
        private static List<MessageBubbleControl.ReactionChip> ExtractReactions(Message m)
        {
            var results = m?.reactions?.results;
            if (results == null || results.Length == 0) return null;
            var list = new List<MessageBubbleControl.ReactionChip>();
            foreach (var rc in results)
            {
                string emo = (rc.reaction as ReactionEmoji)?.emoticon;
                if (string.IsNullOrEmpty(emo)) continue;   // ReactionCustomEmoji has no static glyph here
                list.Add(new MessageBubbleControl.ReactionChip
                {
                    Emoji = emo,
                    Count = rc.count,
                    Chosen = (rc.flags & ReactionCount.Flags.has_chosen_order) != 0
                });
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>Wires a bubble's reactions: shows existing chips and routes taps to toggle.</summary>
        /// <summary>Wires inline entities (links/mentions/emoji): colors+hit-tests them and routes taps.</summary>
        private void ApplyEntities(MessageBubbleControl mb, Message m)
        {
            mb.CustomEmojiResolver = ResolveCustomEmoji;   // before SetEntities so the engine gets it
            mb.SetEntities(m.entities);
            // Defer out of the bubble's mouse-click handler so the (modal) confirm can't tangle with the
            // originating click. t.me/tg:// → in-app (no confirm); external → browser (with confirm).
            mb.LinkClicked += url => BeginInvoke((Action)(() => ResolveLinkAsync(url)));
            mb.MentionClicked += (username, userId) => BeginInvoke((Action)(() =>
            {
                if (!string.IsNullOrEmpty(username)) ResolveLinkAsync("@" + username.TrimStart('@'));  // open the chat in-app
                else OpenMention(null, userId);   // mention-by-id (no username) → existing best-effort path
            }));
            mb.HashtagClicked += tag => { if (!string.IsNullOrEmpty(tag)) _searchBox.Text = tag; };   // → combined search
            mb.BotCommandClicked += cmd =>
            {
                if (!string.IsNullOrEmpty(cmd) && _messageInput.Enabled) { _messageInput.Text = cmd; _messageInput.Focus(); }
            };
            ApplyInlineKeyboard(mb, m);   // bot inline-keyboard grid under the message (any bubble type)
        }

        // ── Inline custom emoji (static first frame, batched + cached) ────────
        private Image ResolveCustomEmoji(long id)
        {
            if (_customEmojiCache.TryGetValue(id, out var img)) return img;
            QueueCustomEmoji(id);
            return null;   // fallback char shown until it loads
        }

        private void QueueCustomEmoji(long id)
        {
            if (id == 0 || _customEmojiPending.Contains(id) || _customEmojiCache.ContainsKey(id)) return;
            if (!_customEmojiQueue.Contains(id)) _customEmojiQueue.Add(id);
            if (_customEmojiTimer == null)
            {
                _customEmojiTimer = new System.Windows.Forms.Timer { Interval = 150 };
                _customEmojiTimer.Tick += (s, e) => FlushCustomEmoji();
            }
            _customEmojiTimer.Stop(); _customEmojiTimer.Start();   // debounce so layout passes batch together
        }

        private async void FlushCustomEmoji()
        {
            _customEmojiTimer.Stop();
            var ids = _customEmojiQueue
                .Where(i => !_customEmojiPending.Contains(i) && !_customEmojiCache.ContainsKey(i))
                .Distinct().Take(100).ToArray();
            _customEmojiQueue.Clear();
            if (ids.Length == 0) return;
            foreach (var i in ids) _customEmojiPending.Add(i);
            try
            {
                var docs = await _service.GetCustomEmojiDocsAsync(ids);
                foreach (var doc in docs)
                {
                    var img = await DecodeCustomEmojiAsync(doc);
                    if (img != null) _customEmojiCache[doc.id] = img;
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:FlushCustomEmoji", ex); }
            finally { foreach (var i in ids) _customEmojiPending.Remove(i); }
            if (!IsDisposed) RefreshVisibleRich();
        }

        private async System.Threading.Tasks.Task<Image> DecodeCustomEmojiAsync(Document doc)
        {
            string path = MediaCache.ThumbPath("customemoji_" + doc.id + ".png");
            try { if (File.Exists(path)) using (var fs = File.OpenRead(path)) using (var t = Image.FromStream(fs)) return new Bitmap(t); } catch { }
            try
            {
                Image im = null;
                if (doc.mime_type == "image/webp")
                {
                    var b = await _service.DownloadDocBytesAsync(doc);
                    if (b != null) im = ImageDecoder.DecodeAny(b);
                }
                else if (doc.mime_type == "application/x-tgsticker" && RLottie.Available)
                {
                    var b = await _service.DownloadDocBytesAsync(doc);
                    if (b != null) using (var clip = RLottie.OpenTgs(b)) if (clip != null) im = clip.RenderFrame(0, 64);
                }
                // webm / unknown → null → fallback char stays. (Animated inline deferred.)
                if (im != null)
                    try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); im.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                return im;
            }
            catch { return null; }
        }

        private void RefreshVisibleRich()
        {
            foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>()) mb.RefreshRich();
        }

        /// <summary>Opens a tapped @mention: resolves the username → profile (user) or chat (channel/group).</summary>
        private async void OpenMention(string username, long userId)
        {
            try
            {
                if (string.IsNullOrEmpty(username)) return;   // mention-by-id only → best-effort skip
                var who = await _service.ResolveUsernameAsync(username.TrimStart('@'));
                if (IsDisposed || who == null) return;
                if (who is User u) { OpenUserProfile(u); return; }
                if (who is ChatBase ch)
                {
                    var entry = new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.ID, Title = ch.Title, IsGroup = true, PeerInfo = ch };
                    await OpenChat(entry, 0);
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:OpenMention", ex); }
        }

        // ── Polls ───────────────────────────────────────────────────────────

        /// <summary>Builds the poll view-model from MessageMediaPoll and wires vote / retract / voter-list.</summary>
        private void ApplyPoll(MessageBubbleControl mb, MessageMediaPoll mmp)
        {
            var poll = mmp.poll;
            var results = mmp.results;
            if (poll == null) return;
            bool quiz = (poll.flags & Poll.Flags.quiz) != 0;
            bool multiple = (poll.flags & Poll.Flags.multiple_choice) != 0;
            bool publicV = (poll.flags & Poll.Flags.public_voters) != 0;
            bool closed = (poll.flags & Poll.Flags.closed) != 0;

            var voters = results != null ? results.results : null;   // PollAnswerVoters[]
            int total = results != null ? results.total_voters : 0;
            bool anyChosen = false;
            var opts = new List<MessageBubbleControl.PollOptionVM>();
            foreach (var ans in poll.answers.OfType<PollAnswer>())
            {
                var vm = new MessageBubbleControl.PollOptionVM { Option = ans.option, Text = ans.text != null ? ans.text.text : "" };
                if (voters != null)
                {
                    var av = voters.FirstOrDefault(v => v.option == ans.option);
                    if (av != null)
                    {
                        vm.Voters = av.voters;
                        vm.Chosen = (av.flags & PollAnswerVoters.Flags.chosen) != 0;
                        vm.Correct = (av.flags & PollAnswerVoters.Flags.correct) != 0;
                        if (vm.Chosen) anyChosen = true;
                    }
                }
                opts.Add(vm);
            }
            bool resultsVisible = closed || anyChosen;   // Telegram: bars appear once you've voted, or when closed
            string solution = results != null ? results.solution : null;
            mb.SetPoll(poll.question != null ? poll.question.text : "", opts, total, closed, publicV, multiple, quiz, solution, resultsVisible);

            int msgId = mb.MessageId;
            mb.PollOptionTapped += option => SubmitVote(msgId, new[] { option });
            mb.PollVoteSubmit += options => SubmitVote(msgId, options);
            mb.PollRetract += () => SubmitVote(msgId, new string[0]);
            mb.PollVotersRequested += option => ShowPollVoters(msgId, option, FindOptionText(poll, option));
            System.Diagnostics.Debug.WriteLine("[POLL] rendered msg=" + msgId + " opts=" + opts.Count + " total=" + total + " quiz=" + quiz + " results=" + resultsVisible);
        }

        private static string FindOptionText(Poll poll, string option)
        {
            foreach (var a in poll.answers.OfType<PollAnswer>())
                if (a.option == option) return a.text != null ? a.text.text : option;
            return option;
        }

        /// <summary>Casts our vote (or retracts with an empty array) and refreshes from the returned updates.</summary>
        private async void SubmitVote(int msgId, string[] options)
        {
            if (_selectedChat == null) return;
            try
            {
                System.Diagnostics.Debug.WriteLine("[POLL] SendVote msg=" + msgId + " options=" + options.Length);
                var updates = await _service.SendVoteAsync(_selectedChat.Peer, msgId, options);
                if (updates != null)
                    foreach (var u in updates.UpdateList)
                        if (u is UpdateMessagePoll p) HandlePollUpdate(p);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[POLL] vote err: " + ex.Message); }
        }

        /// <summary>Live poll refresh (UpdateMessagePoll): splice new results into the cached message + rebuild in place.</summary>
        private void HandlePollUpdate(UpdateMessagePoll p)
        {
            if (_selectedChat == null || p == null) return;
            foreach (var m in _currentChatMessages)
            {
                if (m.media is MessageMediaPoll mp && mp.poll != null && mp.poll.id == p.poll_id)
                {
                    if (p.poll != null) mp.poll = p.poll;
                    if (p.results != null) mp.results = MergePollResults(mp.results, p.results);
                    if (_messagePanel.Controls.OfType<MessageBubbleControl>().Any(b => b.MessageId == m.ID))
                        RebuildBubble(m.ID, m);   // re-fold height + re-render (bars/counts/total)
                    System.Diagnostics.Debug.WriteLine("[POLL] live refresh poll_id=" + p.poll_id);
                    return;
                }
            }
        }

        /// <summary>A "min" results update omits our personal chosen/correct flags → carry them over from the old results.</summary>
        private static PollResults MergePollResults(PollResults old, PollResults fresh)
        {
            if (fresh == null) return old;
            bool freshMin = (fresh.flags & PollResults.Flags.min) != 0;
            if (freshMin && old != null && old.results != null && fresh.results != null)
                foreach (var fr in fresh.results)
                {
                    var o = old.results.FirstOrDefault(x => x.option == fr.option);
                    if (o == null) continue;
                    if ((o.flags & PollAnswerVoters.Flags.chosen) != 0) fr.flags |= PollAnswerVoters.Flags.chosen;
                    if ((o.flags & PollAnswerVoters.Flags.correct) != 0) fr.flags |= PollAnswerVoters.Flags.correct;
                }
            return fresh;
        }

        /// <summary>Public poll: shows who voted for one option (Messages_GetPollVotes, paged).</summary>
        private void ShowPollVoters(int msgId, string option, string optionText)
        {
            if (_selectedChat == null) return;
            using (var dlg = new PollVotersForm(_service, _selectedChat.Peer, msgId, option, optionText, _dark, _accent, Dpi.Of(this)))   // step 3.3: laid out at its owner's DPI
                dlg.ShowDialog(this);
        }

        // ── Inline keyboards (bot buttons) ──────────────────────────────────

        /// <summary>Renders message.reply_markup (ReplyInlineMarkup) as a button grid under the bubble.</summary>
        private void ApplyInlineKeyboard(MessageBubbleControl mb, Message m)
        {
            var markup = m.reply_markup as ReplyInlineMarkup;
            if (markup == null || markup.rows == null || markup.rows.Length == 0) return;
            var rows = new List<List<MessageBubbleControl.KbButtonVM>>();
            foreach (var row in markup.rows)
            {
                if (row == null || row.buttons == null) continue;
                var cells = new List<MessageBubbleControl.KbButtonVM>();
                foreach (var btn in row.buttons)
                {
                    var fld = btn.GetType().GetField("text");
                    var vm = new MessageBubbleControl.KbButtonVM { Label = fld != null ? fld.GetValue(btn) as string : "" };
                    if (btn is KeyboardButtonCallback cb) { vm.Kind = MessageBubbleControl.KbKind.Callback; vm.Data = cb.data; }
                    else if (btn is KeyboardButtonUrl ub) { vm.Kind = MessageBubbleControl.KbKind.Url; vm.Url = ub.url; }
                    else if (btn is KeyboardButtonSwitchInline si) { vm.Kind = MessageBubbleControl.KbKind.SwitchInline; vm.Query = si.query; vm.SamePeer = (si.flags & KeyboardButtonSwitchInline.Flags.same_peer) != 0; }
                    else if (btn is KeyboardButtonUrlAuth ua) { vm.Kind = MessageBubbleControl.KbKind.UrlAuth; vm.Url = ua.url; }
                    else vm.Kind = MessageBubbleControl.KbKind.Unsupported;
                    cells.Add(vm);
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count == 0) return;
            mb.KbButtonTapped += vm => OnKbButtonTapped(m, vm);
            mb.SetInlineKeyboard(rows);
            System.Diagnostics.Debug.WriteLine("[BTN] inline keyboard rows=" + rows.Count + " msg=" + m.ID);
        }

        private async void OnKbButtonTapped(Message m, MessageBubbleControl.KbButtonVM vm)
        {
            if (vm == null || _selectedChat == null) return;
            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>().FirstOrDefault(b => b.MessageId == m.ID);
            if (vm.Kind == MessageBubbleControl.KbKind.Url || vm.Kind == MessageBubbleControl.KbKind.UrlAuth)
            {
                System.Diagnostics.Debug.WriteLine("[BTN] url → router: " + vm.Url);
                ResolveLinkAsync(vm.Url);   // UrlAuth: confirm+open via the router (no full SRP auth handshake yet)
                return;
            }
            if (vm.Kind == MessageBubbleControl.KbKind.SwitchInline)
            {
                if (vm.SamePeer && _messageInput.Enabled)
                {
                    string bot = (_selectedChat.PeerInfo as User)?.username;
                    _messageInput.Text = (string.IsNullOrEmpty(bot) ? "" : "@" + bot + " ") + (vm.Query ?? "");
                    _messageInput.Focus();
                    try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
                    System.Diagnostics.Debug.WriteLine("[BTN] switch-inline (same peer) prefill");
                }
                else ThemedDialog.Show(this, "Switch to inline", "Choosing another chat for an inline query isn't supported yet.", "OK");
                return;
            }
            if (vm.Kind == MessageBubbleControl.KbKind.Callback)
            {
                if (mb != null) mb.SetKbLoading(vm);
                try
                {
                    var ans = await _service.GetCallbackAnswerAsync(_selectedChat.Peer, m.ID, vm.Data);
                    if (ans != null)
                    {
                        if (!string.IsNullOrEmpty(ans.url)) { ResolveLinkAsync(ans.url); }
                        else if (!string.IsNullOrEmpty(ans.message))
                        {
                            if ((ans.flags & Messages_BotCallbackAnswer.Flags.alert) != 0) ThemedDialog.Show(this, "", ans.message, "OK");
                            else ShowToast(ans.message);
                        }
                        System.Diagnostics.Debug.WriteLine("[BTN] callback answer alert=" + ((ans.flags & Messages_BotCallbackAnswer.Flags.alert) != 0) + " url=" + (ans.url != null));
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BTN] callback err: " + ex.Message); }
                finally { if (mb != null) mb.SetKbLoading(null); }
                return;
            }
            ThemedDialog.Show(this, "Not supported", "This button type isn't supported yet.", "OK");   // Buy / Game / WebView
        }

        /// <summary>Brief auto-dismissing themed toast near the bottom of the window (non-alert callback answers).</summary>
        private void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false, BackColor = _dark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(55, 55, 58)
                };
                // Step 3.4: sized and placed in LOGICAL px at this window's DPI; the font is shared (never dispose).
                var lbl = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Text = text, Font = FontHelper.UiAt(9f, WindowDpiValue) };
                f.Controls.Add(lbl);
                var sz = TextRenderer.MeasureText(text, lbl.Font, new Size(Px(360), 0), TextFormatFlags.WordBreak);
                f.Size = new Size(Math.Min(Px(400), Math.Max(Px(120), sz.Width + Px(40))), Math.Max(Px(40), sz.Height + Px(24)));
                f.Location = PointToScreen(new Point((ClientSize.Width - f.Width) / 2, ClientSize.Height - f.Height - Px(90)));
                var t = new System.Windows.Forms.Timer { Interval = 2600 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!f.IsDisposed) f.Close(); };
                f.Shown += (s, e) => t.Start();
                f.Show(this);
            }
            catch { }
        }

        /// <summary>Attach (+) menu: send media, or create a poll.</summary>
        private void ShowAttachMenu()
        {
            if (_selectedChat == null) { OpenAttachmentDialog(); return; }
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, "▣   Photo or File", () => OpenAttachmentDialog());
            AddMenuItem(menu, "⏺   Round Video", () => OpenRoundRecorder());
            AddMenuItem(menu, "📊   Create Poll", () => OpenCreatePoll());
            menu.PrepareAt(_attachButton.PointToScreen(Point.Empty));   // step 3.1: measured as it opens (font, rows, icons)
            menu.ShowAt(_attachButton, new Point(0, -menu.PreferredSize.Height));
        }

        private void OpenRoundRecorder()
        {
            if (_selectedChat == null) return;
            using (var f = new RoundRecorderForm(_service, _selectedChat.Peer))
            {
                f.ShowDialog(this);
                // Own-sent round video appears LIVE (the recorder sends directly with no optimistic bubble).
                // AddMessageBubble registers the id in _shownMessageIds, so the later UpdateManager echo of the
                // same message is de-duped (shown once). The received-round-video path is untouched.
                if (f.SentMessage != null)
                {
                    System.Diagnostics.Debug.WriteLine("[ROUND] append own-sent round video live id=" + f.SentMessage.ID);
                    HandleIncomingMessage(f.SentMessage);
                }
            }
        }

        private async void OpenCreatePoll()
        {
            if (_selectedChat == null) return;
            using (var dlg = new CreatePollForm(_dark, _accent, Dpi.Of(this)))   // step 3.3: laid out at its owner's DPI
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    await _service.SendPollAsync(_selectedChat.Peer, dlg.Question, dlg.Options, dlg.Anonymous, dlg.Multiple);
                    System.Diagnostics.Debug.WriteLine("[POLL] created q='" + dlg.Question + "' opts=" + dlg.Options.Length + " anon=" + dlg.Anonymous + " multi=" + dlg.Multiple);
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Poll", "Couldn't create the poll:\n" + ex.Message, "OK"); }
            }
        }

        private void ApplyReactions(MessageBubbleControl mb, Message m)
        {
            var chips = ExtractReactions(m);
            if (chips != null) mb.SetReactions(chips);
            mb.ReactionToggled += (s, emoji) => ToggleReaction(m, emoji);
            ApplyComments(mb, m);   // COMMENTS-INDICATOR: discussion-comments footer (called wherever a Message bubble is enriched)
            ApplyChannelMeta(mb, m);   // CHANNEL-META-EXTRAS: view count + post_author + group admin role
        }

        // CHANNEL-META-EXTRAS (3): senderId → role (owner/admin/custom rank) for the OPEN megagroup; filled async on
        // open (LoadGroupAdminRolesAsync), cleared on every chat switch. Bubble-build reads it (NO per-bubble RPC).
        private Dictionary<long, string> _groupAdminRoles;

        /// <summary>CHANNEL-META-EXTRAS: sets the flag-gated view count (channel posts) + post_author (signed channels)
        /// off the Message, and the sender's group admin/owner/custom-rank role from the per-open admin cache (or the
        /// per-message from_rank fallback). All no-ops when the fields/flags are absent.</summary>
        private void ApplyChannelMeta(MessageBubbleControl mb, Message m)
        {
            if ((m.flags & Message.Flags.has_views) != 0) mb.SetViews(m.views);
            if ((m.flags & Message.Flags.has_post_author) != 0) mb.SetPostAuthor(m.post_author);
            string role = null;
            if (_groupAdminRoles != null && m.from_id is PeerUser pu && _groupAdminRoles.TryGetValue(pu.user_id, out var r)) role = r;
            else if ((m.flags2 & Message.Flags2.has_from_rank) != 0 && !string.IsNullOrEmpty(m.from_rank)) role = m.from_rank;
            if (role != null) mb.SetAdminRole(role);
        }

        /// <summary>CHANNEL-META-EXTRAS (3): fetch the open megagroup's admins ONCE and cache senderId→role, then
        /// re-apply to the already-built bubbles (they render before this async fetch returns). No-op for non-megagroups;
        /// guarded against a chat switch mid-fetch. Cache-per-open (admin changes reflect on the next open — acceptable).</summary>
        private async void LoadGroupAdminRolesAsync(ChatEntry entry)
        {
            if (entry == null || !(entry.PeerInfo is Channel ch) || (ch.flags & Channel.Flags.megagroup) == 0) return;
            long peerId = entry.PeerId;
            try
            {
                var res = await _service.GetParticipantsAsync(ch, new ChannelParticipantsAdmins(), 0, 100);
                if (res == null || res.participants == null || _selectedChat == null || _selectedChat.PeerId != peerId) return;
                var map = new Dictionary<long, string>();
                foreach (var p in res.participants)
                {
                    if (p is ChannelParticipantCreator cc) map[cc.user_id] = string.IsNullOrEmpty(cc.rank) ? "owner" : cc.rank;
                    else if (p is ChannelParticipantAdmin ca) map[ca.user_id] = string.IsNullOrEmpty(ca.rank) ? "admin" : ca.rank;
                }
                _groupAdminRoles = map;
                // Re-apply to bubbles already built before the fetch returned (match by message id → sender).
                foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>())
                {
                    var msg = _currentChatMessages.FirstOrDefault(x => x.ID == mb.MessageId);
                    if (msg != null && msg.from_id is PeerUser pu && map.TryGetValue(pu.user_id, out var role))
                    { mb.SetAdminRole(role); mb.Measure(); mb.Invalidate(); }
                }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[ADMIN] roles cached=" + map.Count + " for peer=" + peerId);
            }
            catch (Exception ex) { if (LogOn) System.Diagnostics.Debug.WriteLine("[ADMIN] roles fetch failed: " + ex.Message); }
        }

        /// <summary>CHANNEL-META-EXTRAS (1): live view-count bump (UpdateChannelMessageViews). Updates the cached Message
        /// + the loaded/visible bubble; off-window messages no-op. Runs on the UI thread (dispatched), no RPC.</summary>
        private void HandleChannelViews(long channelId, int msgId, int views)
        {
            var msg = _currentChatMessages.FirstOrDefault(x => x.ID == msgId);
            if (msg != null) { msg.views = views; msg.flags |= Message.Flags.has_views; }
            if (_selectedChat == null || _selectedChat.PeerId != channelId) return;
            foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>())
                if (mb.MessageId == msgId) { mb.SetViews(views); mb.Measure(); mb.Invalidate(); break; }
        }

        /// <summary>COMMENTS-INDICATOR (display + tap only): if this is a broadcast-channel post with a linked
        /// discussion group (Message.replies carrying the comments flag), show the "N comments / Leave a comment"
        /// footer and route a tap to the stub. The comments flag is set ONLY on broadcast posts with a linked group,
        /// so it self-selects — a megagroup's in-reply thread carries comments=false and is skipped.</summary>
        private void ApplyComments(MessageBubbleControl mb, Message m)
        {
            if (!(m.replies is MessageReplies mr) || !mr.flags.HasFlag(MessageReplies.Flags.comments)) return;
            mb.SetComments(mr.replies, mr.channel_id);
            mb.CommentsClicked += (postId, linkedChatId) => OpenComments(postId, linkedChatId);
        }

        /// <summary>COMMENTS-THREAD: open a channel post's discussion thread (READ-ONLY this batch). Resolves the linked
        /// group + thread via GetDiscussionMessage, then pages it through the reused history view via GetReplies. The
        /// header becomes a "‹ back" affordance; the composer is hidden (posting is the next batch).</summary>
        private async void OpenComments(int postMsgId, long linkedChatId)
        {
            if (_thread != null || _selectedChat == null) return;
            var channelEntry = _selectedChat;
            var originalPost = _currentChatMessages.FirstOrDefault(x => x.ID == postMsgId);   // the tapped post (channel view still loaded)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] open post=" + postMsgId + " linked=" + linkedChatId);
            try
            {
                var disc = await _service.GetDiscussionMessageAsync(channelEntry.Peer, postMsgId);
                if (disc == null || disc.messages == null || disc.messages.Length == 0)
                { ThemedDialog.Show(this, "Comments", "This discussion isn't available.", "OK"); return; }

                // Build the linked-group entry (sender/avatar resolution + IsGroup rendering) from the returned dicts.
                ChatEntry groupEntry = null;
                if (disc.chats != null && disc.chats.TryGetValue(linkedChatId, out var gcb)) groupEntry = EntryFromPeerInfo(gcb);
                if (groupEntry == null)
                { ThemedDialog.Show(this, "Comments", "Couldn't resolve the discussion group.", "OK"); return; }

                int anchor = TopVisibleMessageId();   // remember the channel position for back (before we switch views)
                // COMMENTS-THREAD-SCOPE: GetReplies must target the DISCUSSION GROUP + the thread root's id IN THAT GROUP
                // (the auto-forwarded post, disc.messages) — NOT the broadcast channel + channel-post id, which does not
                // scope and pulls the group's whole history. disc.messages is non-empty here (checked above).
                int groupRootId = disc.messages[disc.messages.Length - 1].ID;
                _thread = new ThreadCtx { ChannelPeer = channelEntry.Peer, PostMsgId = postMsgId,
                                          GroupPeer = groupEntry.Peer, GroupRootId = groupRootId,
                                          ReturnTo = channelEntry, ReturnAnchorId = anchor,
                                          GroupEntry = groupEntry };   // COMMENTS-JOIN-FLYOUT: join source (Peer + 'left' flag)
                groupEntry.UnreadCount = 0;           // open the thread at the bottom (latest comments)
                UpdateTrayTooltip();     // TA-6b/B: the tray gap at this site — unread dropped to 0 unannounced
                RefreshFolderBadges();   // TA-6b/G1 (DOWN): opening a comment thread reads the discussion group
                _selectedChat = groupEntry;           // paging / scroll / updates target the thread from here
                await LoadHistoryAsync(groupEntry, 0);   // pages via GetReplies (thread mode, source-swapped)
                if (_thread == null) return;             // back was hit while loading
                if (originalPost != null) PrependThreadRootPost(channelEntry, originalPost);   // COMMENTS-THREAD-v2: post at TOP
                _chatTitle.Text = "‹ Comments";
                _chatStatus.Text = channelEntry.Title;
                // COMMENTS-NAV-FIX Bug 1: ALWAYS show the composer in the thread (member or not) — posting is direct;
                // join is only a fallback if the server rejects it (PostThreadComment). Never force a join to comment.
                ShowComposeFooter();
                _joinBarDismissed = false;   // COMMENTS-JOIN-FLYOUT: fresh thread — allow the join bar again
                UpdateThreadJoinBar();        // shows the bar iff we're NOT a member of the linked group
                try { var _ = _service.ReadDiscussionAsync(channelEntry.Peer, postMsgId, disc.read_inbox_max_id); } catch { }
            }
            catch (Exception ex)
            {
                _thread = null;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] open failed: " + ex.Message);
                ThemedDialog.Show(this, "Comments", "Couldn't open comments:\n" + ex.Message, "OK");
            }
        }

        /// <summary>COMMENTS-THREAD: leave the thread and return to the channel at the prior position (reload focusing
        /// the remembered anchor — the channel view was never mutated in place, so this restores it cleanly).</summary>
        private void CloseThread()
        {
            if (_thread == null) return;
            var ret = _thread.ReturnTo; int anchor = _thread.ReturnAnchorId;
            _thread = null;
            UpdateThreadJoinBar();   // COMMENTS-JOIN-FLYOUT: leaving the thread → hide the join bar
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] back to channel peer=" + ret.PeerId);
            var _ = OpenChat(ret, anchor);
        }

        /// <summary>COMMENTS-NAV-FIX Bug 2: exit thread mode (idempotent). Clearing _thread is sufficient — the three
        /// history loaders fall back to GetHistory, ResolveAndApplyComposer resolves the normal footer, and the
        /// LoadHistoryAsync that every open path runs right after clears the panel + _currentChatMessages (dropping the
        /// root-post-at-top). Called at the top of OpenChat so NO conversation-open path can leave a stale thread
        /// routing the paging/composer/send — that was the wrong-destination send bug.</summary>
        private void ClearThreadMode()
        {
            if (_thread == null) return;
            _thread = null;
            UpdateThreadJoinBar();   // COMMENTS-JOIN-FLYOUT: leaving the thread → hide the join bar
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] thread cleared (switched conversation)");
        }

        /// <summary>COMMENTS-THREAD-v2: render the original channel post as the root at the TOP of the thread (context),
        /// reusing the bubble pipeline. Its OWN comments footer is suppressed (no ApplyComments → no recursion).
        /// ReconcileWindowOrHeal is overlap-only, so this extra top bubble (correctly laid out, no overlap) is tolerated;
        /// it's added to the model at index 0 (oldest) so a heal rebuild keeps it. NOTE: on a heavy thread, paging older
        /// comments (InsertRange at 0) can slot them above the post — cosmetic, not corrupting; common threads (≤1 page)
        /// keep it pinned at top.</summary>
        private void PrependThreadRootPost(ChatEntry channelEntry, Message post)
        {
            try
            {
                if (_currentChatMessages.Any(x => x.ID == post.ID)) return;   // already present (channel/group id collision guard)
                var ctl = MakeMessageBubble(channelEntry, post, null) as MessageBubbleControl;
                if (ctl == null) return;
                ctl.MessageId = post.ID;
                ApplyEntities(ctl, post);       // links / mentions — but NOT ApplyComments (would recurse the footer)
                _messagePanel.Controls.Add(ctl);
                _messagePanel.Controls.SetChildIndex(ctl, 0);
                _currentChatMessages.Insert(0, post);
                _shownMessageIds.Add(post.ID);
            }
            catch { }
        }

        /// <summary>The message id nearest the top of the current message view (for restoring position on thread-back).</summary>
        private int TopVisibleMessageId()
        {
            try
            {
                MessageBubbleControl best = null;
                foreach (var c in _messagePanel.Controls)
                    if (c is MessageBubbleControl b && b.MessageId != 0 && b.Bottom > 0 && (best == null || b.Top < best.Top))
                        best = b;
                return best?.MessageId ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Header tap: in a comment thread it's the "‹ back" affordance; otherwise it opens the profile.</summary>
        private void OnHeaderClick()
        {
            if (_thread != null) CloseThread();
            else OpenSelectedProfile();
        }

        /// <summary>
        /// Toggles our reaction on a message: tapping the chosen one removes it, tapping another
        /// switches to it. Updates the bubble optimistically, then sends to the server.
        /// </summary>
        private async void ToggleReaction(Message m, string emoji)
        {
            if (_selectedChat == null || m == null || string.IsNullOrEmpty(emoji)) return;

            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>()
                                  .FirstOrDefault(b => b.MessageId == m.ID);

            // Work off the bubble's current chips so repeated taps stay consistent.
            var chips = (mb?.Reactions ?? (IReadOnlyList<MessageBubbleControl.ReactionChip>)new List<MessageBubbleControl.ReactionChip>())
                .Select(c => new MessageBubbleControl.ReactionChip { Emoji = c.Emoji, Count = c.Count, Chosen = c.Chosen })
                .ToList();

            var tapped = chips.FirstOrDefault(c => c.Emoji == emoji);
            bool wasChosen = tapped != null && tapped.Chosen;

            // Telegram allows one reaction here: clear whatever was chosen first.
            foreach (var c in chips.ToList())
                if (c.Chosen)
                {
                    c.Chosen = false;
                    c.Count = Math.Max(0, c.Count - 1);
                    if (c.Count == 0) chips.Remove(c);
                }

            if (!wasChosen)   // add the tapped reaction
            {
                var t = chips.FirstOrDefault(c => c.Emoji == emoji);
                if (t == null) { t = new MessageBubbleControl.ReactionChip { Emoji = emoji, Count = 0 }; chips.Add(t); }
                t.Chosen = true;
                t.Count += 1;
            }

            mb?.SetReactions(chips.Count > 0 ? chips : null);

            await _service.SendReactionAsync(_selectedChat.Peer, m.ID, wasChosen ? "" : emoji);
        }

        /// <summary>QUICKWINS-1 PART 2: others' reaction changes arrive live (we render + send, but used to DROP this
        /// update → stale until reload). If the message is loaded in the OPEN chat, refresh its pills from the echoed
        /// MessageReactions. No RPC, no fetch when off-window (same as the other live handlers). Runs on the UI thread
        /// (the update pump already marshals here). Reconciles our own optimistic send to the server truth — no stacking.</summary>
        private void HandleReactionsUpdate(long peerId, int msgId, MessageReactions reactions)
        {
            if (_selectedChat == null || _selectedChat.PeerId != peerId) return;    // not the open chat → no-op (never fetch)
            var m = _currentChatMessages.FirstOrDefault(x => x.ID == msgId);
            if (m == null) return;                                                  // not in the loaded window → no-op
            m.reactions = reactions;                                                // keep the cache current (re-render consistent)
            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>().FirstOrDefault(b => b.MessageId == msgId);
            if (mb != null)
            {
                mb.SetReactions(ExtractReactions(m));   // null when all cleared → pills disappear
                if (LogOn) System.Diagnostics.Debug.WriteLine("[REACT] live update peer=" + peerId + " msg=" + msgId);
            }
        }

        /// <summary>MENTION-REACTION: light a row's passive heart glyph when a reaction to YOUR message arrives unread
        /// (MessagePeerReaction.unread — the clean "reacted to me, unseen" signal; no group false-positives). The OPEN
        /// chat is skipped (you're seeing it → cleared on open). Count-agnostic: the glyph is on/off (exact count
        /// re-syncs on the next dialog load). NO notification — indicator only.</summary>
        private void HandleReactionIndicator(long peerId, MessageReactions reactions)
        {
            if (peerId == 0 || reactions?.recent_reactions == null) return;
            if (_selectedChat != null && _selectedChat.PeerId == peerId) return;   // open → seen (ReadReactions on open)
            bool unreadToMe = reactions.recent_reactions.Any(r => (r.flags & MessagePeerReaction.Flags.unread) != 0);
            if (!unreadToMe) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry == null || entry.UnreadReactions > 0) return;
            entry.UnreadReactions = 1;                     // on/off glyph — exact count comes from the next dialog load
            FindChatItem(peerId)?.Invalidate();
        }

        /// <summary>MENTION-REACTION: clear a channel/megagroup row's "@" badge when its message contents (incl. the
        /// mention) were read on ANOTHER device (UpdateChannelReadMessagesContents carries channel_id). Best-effort —
        /// the exact count re-syncs on the next dialog load; basic-group/DM reads (no channel_id) refresh on reload.</summary>
        private void HandleMentionsReadElsewhere(long channelId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == channelId);
            if (entry == null || entry.UnreadMentions == 0) return;
            entry.UnreadMentions = 0;
            FindChatItem(channelId)?.Invalidate();
        }

        private void ShowProfile()
        {
            using (var dlg = new ProfileForm(_service))   // editable self-profile
            {
                dlg.ShowDialog(this);
                // BATCH-TA-18 — this was MISSING: only OpenSelectedProfile routed pending links, so a link
                // tapped in your OWN profile (bio, or the shared-links gallery) was silently dropped. It has
                // to be here or the proxy-link interception has a hole exactly where the router doesn't run.
                RouteProfilePending(dlg);
            }
        }

        /// <summary>Opens the read-only profile of the currently selected chat (header click).</summary>
        private void OpenSelectedProfile()
        {
            if (_selectedChat == null) return;
            var av = _avatars.GetCached(_selectedChat.PeerId);
            using (var dlg = new ProfileForm(_service, _selectedChat, av))
            {
                dlg.Avatars = _avatars;   // PROFILE-MEMBERS: member rows use the shared store
                dlg.ForwardRequested += ForwardFromProfile;
                dlg.ShowInChatRequested += ShowInChatFromProfile;
                dlg.ShowDialog(this);
                RouteProfilePending(dlg);
            }
        }

        /// <summary>Routes a rich-info link/@mention/#hashtag tapped in a (now-closed) profile to the existing
        /// in-app resolvers — same handlers chat bubbles use.</summary>
        private void RouteProfilePending(ProfileForm dlg)
        {
            if (dlg.PendingLink != null) ResolveLinkAsync(dlg.PendingLink);
            else if (!string.IsNullOrEmpty(dlg.PendingMentionUser) || dlg.PendingMentionId != 0) OpenMention(dlg.PendingMentionUser, dlg.PendingMentionId);
            else if (dlg.PendingHashtag != null && _searchBox != null) _searchBox.Text = dlg.PendingHashtag;
            else if (dlg.PendingOpenChannel != null) OpenPersonalChannel(dlg.PendingOpenChannel);   // PROFILE-CHANNEL
        }

        /// <summary>PROFILE-CHANNEL: open a user's attached personal channel (tapped in their profile) via the normal
        /// channel-open path — reuse an existing dialog-list entry if we follow it, else build one from the Channel.</summary>
        private async void OpenPersonalChannel(Channel ch)
        {
            if (ch == null) return;
            try
            {
                var entry = _allChats.FirstOrDefault(c => c.PeerId == ch.id) ?? EntryFromPeerInfo(ch);
                if (entry != null) await OpenChat(entry, 0);
            }
            catch (Exception ex) { if (LogOn) System.Diagnostics.Debug.WriteLine("[PROFILE-CHANNEL] open failed: " + ex.Message); }
        }

        /// <summary>Handles a profile-gallery Forward request via the existing picker → forward flow.</summary>
        private async void ForwardFromProfile(InputPeer src, int msgId)
        {
            var owner = (IWin32Window)Form.ActiveForm ?? this;   // the topmost modal (gallery) owns the picker
            List<ChatEntry> targets;
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(owner) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }
            int ok = await ForwardToTargets(src, new[] { msgId }, targets);
            ThemedDialog.Show(owner, ok > 0 ? "Forwarded" : "Forward failed", ForwardResultText(ok, targets.Count), "OK");
        }

        /// <summary>Forwards ids to each selected target (bounded per target), routing EACH target's result
        /// through <see cref="ApplySendResult"/> — the single chat-list refresh — so forwarded chats update
        /// (preview + re-order) live, exactly like a received/sent message. Returns how many succeeded.</summary>
        private async System.Threading.Tasks.Task<int> ForwardToTargets(InputPeer src, int[] ids, List<ChatEntry> targets)
        {
            int ok = 0;
            foreach (var t in targets)
            {
                if (t == null || t.Peer == null) continue;
                var result = await _service.ForwardToAsync(src, ids, t.Peer);
                if (result == null) continue;
                ApplySendResult(result);   // → ProcessSingleUpdate → HandleIncomingMessage → UpdateChatListForMessage
                System.Diagnostics.Debug.WriteLine("[UPDATE] forward applied → peer=" + t.PeerId);
                ok++;
            }
            return ok;
        }

        private static string ForwardResultText(int ok, int total)
        {
            if (ok == 0) return "Couldn't forward — make sure your VPN is on.";
            if (ok == total) return "Forwarded to " + ok + (ok == 1 ? " chat." : " chats.");
            return "Forwarded to " + ok + " of " + total + " chats (the rest couldn't be reached).";
        }

        /// <summary>Jumps the conversation to a profile-gallery item's message (scroll-into-view + accent flash).</summary>
        private void ShowInChatFromProfile(InputPeer peer, int messageId)
        {
            long id = PeerIdOf(peer);
            // Defer so this runs AFTER the gallery + profile have closed and the message panel is the active view.
            BeginInvoke((Action)(async () =>
            {
                // Prefer the real dialog-list entry; otherwise build a minimal one from the InputPeer so the
                // jump still lands (e.g. a group member's media when you've never opened a 1:1 with them).
                var entry = _allChats.FirstOrDefault(c => c.PeerId == id) ?? BuildMinimalChatEntry(peer, id);
                if (entry == null) return;   // unresolvable peer → graceful no-op

                bool sameChat = _selectedChat != null && _selectedChat.PeerId == entry.PeerId;
                if (sameChat && ScrollToAndFlash(messageId))   // already open + loaded → no reload
                    return;

                await OpenChat(entry, messageId);              // different chat or far-back → focused reload
                ScrollToAndFlash(messageId);                   // flashes whichever row kind it lands on
            }));
        }

        /// <summary>Builds a throwaway ChatEntry from an InputPeer for chats not in the dialog list, so a
        /// Show-in-chat jump can still open them focused on the message. Name resolves from the peer cache.</summary>
        private ChatEntry BuildMinimalChatEntry(InputPeer peer, long id)
        {
            if (peer == null) return null;
            bool isGroup = peer is InputPeerChat || peer is InputPeerChannel;
            string title = _peerNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)
                ? n : (isGroup ? "Group" : "Chat");
            return new ChatEntry { Peer = peer, PeerId = id, Title = title, IsGroup = isGroup };
        }

        private void ShowAbout()
        {
            using (var f = new AboutForm(_dark, _accent))
            {
                f.ShowDialog(this);
                // A Telegram chat/group tapped in About (e.g. @WTelegramClient) → open it IN-APP after the
                // dialog closes, via the same router the drawer channel row uses (off-thread, graceful).
                if (!string.IsNullOrEmpty(f.PendingInAppLink)) ResolveLinkAsync(f.PendingInAppLink);
            }
        }

        /// <summary>Opens the official TelegArm channel in the chat view so the user can browse + join it.
        /// Reuses the in-app link router: resolves the handle OFF the UI thread and, if it can't (offline /
        /// VPN down / bad handle), quietly falls back to the browser — never hangs, never crashes.</summary>
        private void OpenTelegArmChannel()
        {
            string handle = (CHANNEL_USERNAME ?? "").TrimStart('@');
            if (string.IsNullOrEmpty(handle)) return;
            ResolveLinkAsync("https://t.me/" + handle);
        }

    }
}
