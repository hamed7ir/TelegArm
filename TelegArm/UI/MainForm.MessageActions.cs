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
        // ── Message actions (context menu: copy / reply / delete) ────────────────

        /// <summary>The message menu on the screen now (null when none): what the shot rig reads to check its items.</summary>
        private ThemedContextMenuStrip _messageMenu;

        /// <summary>Builds + shows the per-message context menu (right-click / touch-hold).</summary>
        private void OnBubbleContextMenu(object sender, Point screenPt)
        {
            var bubble = sender as MessageBubbleControl;
            if (bubble == null) return;
            Message msg = bubble.MessageId > 0
                ? _currentChatMessages.FirstOrDefault(x => x.ID == bubble.MessageId)
                : null;
            // A GROUP (album), as Telegram Desktop: on one of its items (a photo, a file row) the menu acts on THAT item; on
            // the rest of the bubble (the caption, the margins) on the whole group. It always acted on the first item before -
            // Save, Reply or Delete on the third file hit the first (owner 2026-11-13).
            Message textMsg = null;
            int[] groupIds = null;
            if (bubble.IsAlbum)
            {
                int capId = bubble.AlbumCaptionMessageId;
                textMsg = capId != 0 ? _currentChatMessages.FirstOrDefault(x => x.ID == capId) : null;
                int itemId = bubble.AlbumMessageIdAt(bubble.PointToClient(screenPt));
                var item = itemId != 0 ? _currentChatMessages.FirstOrDefault(x => x.ID == itemId) : null;
                if (item != null) msg = item;
                else
                {
                    msg = textMsg ?? msg;
                    if (bubble.AlbumCount > 1) groupIds = bubble.AlbumMessageIds;
                }
            }
            // Retry only applies to a failed optimistic media send (we kept its source).
            var failed = bubble.Failed && _failedSends.ContainsKey(bubble) ? bubble : null;
            ShowMessageMenu(bubble, msg, screenPt, failed, textMsg, groupIds);
        }

        private void OnVoiceContextMenu(object sender, Point screenPt)
        {
            var vb = sender as VoiceBubbleControl;
            if (vb == null) return;
            Message msg = vb.MessageId > 0
                ? _currentChatMessages.FirstOrDefault(x => x.ID == vb.MessageId)
                : null;
            ShowMessageMenu(vb, msg, screenPt, null);
        }

        /// <summary>Builds + shows the themed per-message menu shared by message and voice bubbles. For an album:
        /// <paramref name="textMsg"/> is the item that carries the caption (the text actions use it when <paramref name="msg"/>
        /// has no text of its own), and <paramref name="groupIds"/> is set when the menu is for the WHOLE group.</summary>
        private void ShowMessageMenu(Control ctl, Message msg, Point screenPt, MessageBubbleControl failedBubble,
                                     Message textMsg = null, int[] groupIds = null)
        {
            if (_selectedChat == null) return;
            // the message the text actions (copy, translate, edit) are about
            Message tm = msg != null && !string.IsNullOrEmpty(msg.message) ? msg : (textMsg ?? msg);

            var menu = new ThemedContextMenuStrip();
            if (failedBubble != null)
                AddMenuItem(menu, "⟳   Retry", () => RetrySend(failedBubble));

            // DOWNLOAD-UX 3.3: ring-tap now PAUSES; explicit cancel lives here (and in the manager panel).
            {
                var mdoc = (msg?.media as MessageMediaDocument)?.document as Document;
                if (mdoc != null)
                {
                    var live = _service.GetDownload(mdoc.id);
                    var ti = live == null ? _service.GetTransfer(mdoc.id) : null;
                    bool pausedRow = ti != null && ti.Handle != null && ti.Handle.State == DownloadHandle.DState.Paused;
                    if (live != null || pausedRow)
                        AddMenuItem(menu, "✕   Cancel download", () => _service.CancelTransfer(mdoc.id));
                }
            }

            var mb = ctl as MessageBubbleControl;
            if (mb != null && mb.HasTextSelection)
                AddMenuItem(menu, "⧉   Copy Selected Text", () =>
                {
                    try { Clipboard.SetText(mb.GetSelectedText() ?? ""); } catch { }
                    mb.ClearTextSelection();
                });
            else
            {
                string copyText = tm?.message ?? "";
                if (!string.IsNullOrEmpty(copyText))
                    AddMenuItem(menu, "⧉   Copy text", () => { try { Clipboard.SetText(copyText); } catch { } });
            }
            if (mb != null && mb.HasSelectableText && !mb.HasTextSelection)
                AddMenuItem(menu, "✍   Select Text", () => mb.SelectAllText());
            if (msg != null)
            {
                AddMenuItem(menu, "↩   Reply", () => StartReply(msg));
                AddReactMenu(menu, msg);
                // T9: who reacted — groups only (a channel's reactions are anonymous)
                int reacted = ReactionTotal(msg);
                if (reacted > 0 && _selectedChat.IsGroup)
                    AddMenuItem(menu, "👥   Who reacted (" + reacted + ")", () => ShowReactors(_selectedChat.Peer, msg.ID, reacted));
                // T16: who has seen MY message — small groups only (the server refuses big ones and old messages)
                if (IsOut(msg) && _selectedChat.IsGroup && !(_selectedChat.PeerInfo is Channel bch && (bch.flags & Channel.Flags.broadcast) != 0)
                    && (DateTime.UtcNow - msg.date).TotalDays < 7)
                    AddMenuItem(menu, "👁   Seen by…", () => ShowSeenBy(_selectedChat.Peer, msg.ID));
                // T17: translate (Telegram decides whether it's available to this account)
                if (!string.IsNullOrWhiteSpace(tm.message))
                    AddMenuItem(menu, "🌐   Translate", () => TranslateMessage(_selectedChat.Peer, tm.ID));
                if ((msg.flags & Message.Flags.pinned) != 0)
                    AddMenuItem(menu, "📌   Unpin", () => UnpinMessage(msg));
                else
                    AddMenuItem(menu, "📌   Pin", () => PinMessage(msg));
                if (IsOut(tm) && !string.IsNullOrEmpty(tm.message))
                    AddMenuItem(menu, "✎   Edit", () => StartEdit(tm));
                // In selection mode, Forward acts on the whole selection (not just this bubble).
                if (_selectionMode && _selectedMessageIds.Count > 0)
                    AddMenuItem(menu, "⮕   Forward (" + _selectedMessageIds.Count + ")", () => ForwardSelected());
                else if (groupIds != null)
                    AddMenuItem(menu, "⮕   Forward (" + groupIds.Length + ")", () => ForwardMessages(groupIds));
                else
                    AddMenuItem(menu, "⮕   Forward", () => ForwardMessages(new[] { msg.ID }));
                // selection is per bubble: an album is selected as one (its own id), whichever item was clicked
                int selectId = ctl is MessageBubbleControl ab && ab.IsAlbum ? ab.MessageId : msg.ID;
                AddMenuItem(menu, "☑   Select", () => EnterSelectionMode(selectId));
                // T3: a link to the message (channels and supergroups only: the only chats with message links)
                var linkChannel = InputChannelOf(msg);
                if (linkChannel != null)
                    AddMenuItem(menu, "🔗   Copy link", () => CopyMessageLink(linkChannel, msg.ID));
                // T3: report someone else's message (Telegram's own option list)
                if (!IsOut(msg) && !(_selectedChat.PeerInfo is User su && (su.flags & User.Flags.self) != 0))
                    AddMenuItem(menu, "⚑   Report", () => ReportMessage(_selectedChat.Peer, msg.ID, "", null));
                if (groupIds != null)   // the whole group: every item into the save folder
                    AddMenuItem(menu, "💾   Save (" + groupIds.Length + ")", () => { var _ = SaveGroupAsync(groupIds); });
                else if (MediaClassifier.FromMessage(msg) != null)   // any media (photo/video/audio/voice/file/gif/sticker)
                {
                    AddMenuItem(menu, "💾   Save", () => { var _ = SaveMediaAsync(msg); });
                    AddMenuItem(menu, "📁   Reveal in folder", () => RevealMedia(msg));
                }
                var gifDoc = GifDocumentOf(msg);   // T6: keep it in the GIF panel
                if (gifDoc != null) AddMenuItem(menu, "🎞   Save to GIFs", () => SaveGif(gifDoc));
                if (groupIds != null) AddMenuItem(menu, "✖   Delete (" + groupIds.Length + ")", () => DeleteMessages(groupIds));
                else AddMenuItem(menu, "✖   Delete", () => DeleteMessages(new[] { msg.ID }));
            }

            if (menu.Items.Count == 0) { menu.Dispose(); return; }

            menu.Closed += (s, e) =>
            {
                if (ReferenceEquals(_messageMenu, menu)) _messageMenu = null;
                BeginInvoke((Action)menu.Dispose);
            };
            _messageMenu = menu;
            menu.ShowAt(screenPt);
        }

        // ── T9 / T16 / T17 ─────────────────────────────────────────────────────────────────────────────────────────

        private void ShowReactors(InputPeer peer, int msgId, int total)
        {
            Func<string, System.Threading.Tasks.Task<PeopleListForm.Page>> load = async offset =>
            {
                var r = await _service.GetReactionsListAsync(peer, msgId, 50, offset);
                var page = new PeopleListForm.Page { NextOffset = r.next_offset };
                foreach (var pr in r.reactions)
                {
                    var re = pr.reaction as ReactionEmoji;
                    page.Rows.Add(new PeopleListForm.Row
                    {
                        Name = NameIn(pr.peer_id, r.users, r.chats), PeerId = pr.peer_id != null ? pr.peer_id.ID : 0,
                        Trailing = re != null ? re.emoticon : "\u2B50", TrailingIsEmoji = true,   // a custom/paid reaction: a star
                    });
                }
                return page;
            };
            using (var f = new PeopleListForm("Reactions (" + total + ")", load)) f.ShowDialog(this);
        }

        private void ShowSeenBy(InputPeer peer, int msgId)
        {
            Func<string, System.Threading.Tasks.Task<PeopleListForm.Page>> load = async offset =>
            {
                var list = await _service.GetReadParticipantsAsync(peer, msgId);
                var page = new PeopleListForm.Page();
                foreach (var rp in list.OrderByDescending(x => x.date))
                    page.Rows.Add(new PeopleListForm.Row
                    {
                        Name = NameOf(new PeerUser { user_id = rp.user_id }), PeerId = rp.user_id,
                        Trailing = SeenTime(rp.date),
                    });
                return page;
            };
            using (var f = new PeopleListForm("Seen by", load, "No one has seen it yet.")) f.ShowDialog(this);
        }

        private static string SeenTime(DateTime utc)
        {
            var local = utc.ToLocalTime();
            return local.Date == DateTime.Now.Date ? local.ToString("t") : local.ToString("ddd t");
        }

        /// <summary>A person's name from a result's own users/chats (a result carries the peers it names).</summary>
        private static string NameIn(Peer peer, Dictionary<long, User> users, Dictionary<long, ChatBase> chats)
        {
            if (peer == null) return "Someone";
            User u; ChatBase c;
            if (peer is PeerUser && users != null && users.TryGetValue(peer.ID, out u)) return DisplayName(u);
            if (chats != null && chats.TryGetValue(peer.ID, out c)) return c.Title;
            return "Someone";
        }

        private async void TranslateMessage(InputPeer peer, int msgId)
        {
            string lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (string.IsNullOrEmpty(lang) || lang == "iv") lang = "en";
            try
            {
                string text = await _service.TranslateMessageAsync(peer, msgId, lang);
                if (string.IsNullOrEmpty(text)) { ThemedDialog.Show(this, "Translate", "Telegram returned no translation.", "OK"); return; }
                if (ThemedDialog.Show(this, "Translation", text, "Copy", "Close") == 0) { try { Clipboard.SetText(text); } catch { } }
            }
            catch (Exception ex)
            {
                string m = ex.Message ?? "";
                string why = m.IndexOf("PREMIUM", StringComparison.OrdinalIgnoreCase) >= 0 ? "Translation needs Telegram Premium on this account."
                           : m.IndexOf("QUOTA", StringComparison.OrdinalIgnoreCase) >= 0 ? "The translation limit is reached for now. Try again later."
                           : m.IndexOf("DISABLED", StringComparison.OrdinalIgnoreCase) >= 0 ? "Translation isn't available here."
                           : "Couldn't translate: " + m;
                ThemedDialog.Show(this, "Translate", why, "OK");
            }
        }

        /// <summary>T9: how many reactions a message carries (all kinds together).</summary>
        private static int ReactionTotal(Message m)
        {
            int n = 0;
            if (m != null && m.reactions != null && m.reactions.results != null)
                foreach (var rc in m.reactions.results) n += rc.count;
            return n;
        }

        // ── T6: save a GIF ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The message's GIF (an animation: DocumentAttributeAnimated, or a raw image/gif — MediaClassifier's rule).</summary>
        private static Document GifDocumentOf(Message msg)
        {
            var d = (msg != null ? msg.media as MessageMediaDocument : null)?.document as Document;
            if (d == null || d.attributes == null) return null;
            bool animated = d.attributes.OfType<DocumentAttributeAnimated>().Any() || d.mime_type == "image/gif";
            bool sticker = d.attributes.OfType<DocumentAttributeSticker>().Any();
            return animated && !sticker ? d : null;
        }

        private async void SaveGif(Document doc)
        {
            try { await _service.SaveGifAsync(doc); ShowToast("Saved to GIFs"); }
            catch (Exception ex) { ThemedDialog.Show(this, "Save GIF", "Couldn't save the GIF: " + ex.Message, "OK"); }
        }

        // ── T3: message link + report ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The channel a message lives in (a broadcast channel, a supergroup, or the discussion group of an open
        /// comment thread), or null — a private chat or a basic group has no message links.</summary>
        private InputChannelBase InputChannelOf(Message msg)
        {
            var pc = msg != null ? msg.peer_id as PeerChannel : null;
            if (pc == null) return null;
            var ch = ResolvePeer(pc) as Channel;
            return ch != null ? new InputChannel(ch.id, ch.access_hash) : null;
        }

        private async void CopyMessageLink(InputChannelBase channel, int id)
        {
            try
            {
                string link = await _service.ExportMessageLinkAsync(channel, id);
                if (string.IsNullOrEmpty(link)) { ThemedDialog.Show(this, "Copy link", "Telegram returned no link for this message.", "OK"); return; }
                try { Clipboard.SetText(link); } catch { }
                ShowToast("Link copied");
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Copy link", "Couldn't get the link: " + ex.Message, "OK"); }
        }

        /// <summary>T3 — the report flow, one server round per step: choose a reason (as a menu at the pointer, since the
        /// list is long), sometimes add a comment, then "Reported". Same shape as the ad report (ReportSponsored).</summary>
        private async void ReportMessage(InputPeer peer, int id, string option, string comment)
        {
            try
            {
                var result = await _service.ReportMessagesAsync(peer, new[] { id }, option, comment);
                if (IsDisposed) return;
                var choose = result as ReportResultChooseOption;
                var add = result as ReportResultAddComment;
                if (choose != null)
                {
                    if (choose.options == null || choose.options.Length == 0) return;
                    var menu = new ThemedContextMenuStrip();
                    var title = new ToolStripMenuItem(string.IsNullOrEmpty(choose.title) ? "Report" : choose.title) { Enabled = false };
                    menu.Items.Add(title);
                    menu.Items.Add(new ToolStripSeparator());
                    foreach (var o in choose.options)
                    {
                        string opt = o.option;
                        AddMenuItem(menu, o.text, () => ReportMessage(peer, id, opt, null));
                    }
                    menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
                    menu.ShowAt(Cursor.Position);
                }
                else if (add != null)
                {
                    bool optional = (add.flags & ReportResultAddComment.Flags.optional) != 0;
                    string text = InputDialog.Ask(this, "Report", optional ? "Add details (optional)" : "Describe the problem", "");
                    if (text == null && !optional) return;   // cancelled
                    ReportMessage(peer, id, add.option, text ?? "");
                }
                else
                {
                    if (LogOn) Logger.Diag("[REPORT] message " + id + " reported");
                    ShowToast("Reported. Thank you.");
                }
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Report", "Couldn't report: " + ex.Message, "OK"); }
        }

        /// <summary>Pins a message after asking who should see it (everyone vs. just me).</summary>
        private async void PinMessage(Message msg)
        {
            if (_selectedChat == null) return;
            // 0 = everyone, 1 = just me (pm_oneside), 2 = cancel.
            int choice = ThemedDialog.Show(this, "Pin message",
                "Who should see this message pinned?", "Everyone", "Just me", "Cancel");
            if (choice != 0 && choice != 1) return;
            try { await _service.PinMessageAsync(_selectedChat.Peer, msg.ID, choice == 1); LoadPinnedAsync(_selectedChat); }
            catch (Exception ex) { ThemedDialog.Show(this, "Pin failed", ex.Message, "OK"); }
        }

        private async void UnpinMessage(Message msg)
        {
            if (_selectedChat == null) return;
            try { await _service.UnpinMessageAsync(_selectedChat.Peer, msg.ID); LoadPinnedAsync(_selectedChat); }
            catch (Exception ex) { ThemedDialog.Show(this, "Unpin failed", ex.Message, "OK"); }
        }

        // ── Pinned-messages bar ──────────────────────────────────────────────

        /// <summary>Fetches the chat's pinned messages and refreshes the bar (best-effort, non-blocking).</summary>
        private async void LoadPinnedAsync(ChatEntry entry)
        {
            // ROOT CAUSE of the loop-at-3rd-pin: this runs again whenever a pinned jump triggers a focused
            // OpenChat (for an off-window pin) — and it used to reset _pinnedIndex=0, clobbering the cycle.
            // Only reset when switching to a DIFFERENT chat; on a same-chat reload, keep the cycle position.
            bool sameChat = entry != null && _pinnedChatId == entry.PeerId && _pinnedMessages != null;
            if (!sameChat)
            {
                _pinnedMessages = null;
                _pinnedIndex = 0;
                _pinnedChatId = entry?.PeerId ?? 0;
                UpdatePinnedBar();
            }
            if (entry == null) return;
            try
            {
                var pins = await _service.GetPinnedMessagesAsync(entry.Peer);
                if (IsDisposed || _selectedChat != entry) return;
                _pinnedMessages = new List<Message>(pins);
                _pinnedChatId = entry.PeerId;
                if (_pinnedIndex >= _pinnedMessages.Count) _pinnedIndex = 0;   // clamp; do NOT reset on same-chat reload
                System.Diagnostics.Debug.WriteLine("[PIN] open '" + entry.Title + "' count=" + _pinnedMessages.Count
                    + " ids=[" + string.Join(",", _pinnedMessages.ConvertAll(p => p.ID)) + "] index=" + _pinnedIndex);
                UpdatePinnedBar();
            }
            catch { /* pinned bar is best-effort */ }
        }

        private void UpdatePinnedBar()
        {
            bool has = _pinnedMessages != null && _pinnedMessages.Count > 0;
            SetRowPx(_rightLayout, 2, has ? 46 : 0);   // step 3.4: logical px
            if (_pinnedListForm != null && !_pinnedListForm.IsDisposed)
            {
                if (!has) _pinnedListForm.Close();                       // no pins left → close the popup
                else _pinnedListForm.SetPins(BuildPinnedRows());         // keep it in sync after (un)pins
            }
            if (_pinnedBar == null) return;
            _pinnedBar.Visible = has;
            if (!has) return;
            if (_pinnedIndex >= _pinnedMessages.Count) _pinnedIndex = 0;
            var m = _pinnedMessages[_pinnedIndex];
            string title = _pinnedMessages.Count > 1
                ? "Pinned · " + (_pinnedIndex + 1) + "/" + _pinnedMessages.Count
                : "Pinned message";
            _pinnedBar.SetContent(title, GetDisplayText(m));
            _pinnedBar.Invalidate();
        }

        /// <summary>Tap the bar: jump to the current pinned message (highlight it), then advance to the next.</summary>
        private void OnPinnedBarClicked()
        {
            if (_pinnedMessages == null || _pinnedMessages.Count == 0) return;
            var m = _pinnedMessages[_pinnedIndex];
            System.Diagnostics.Debug.WriteLine("[PIN] click index=" + _pinnedIndex + "/" + _pinnedMessages.Count + " jump id=" + m.ID);
            _pinnedIndex = (_pinnedIndex + 1) % _pinnedMessages.Count;   // advance over the FULL stored list; wrap only after the last
            UpdatePinnedBar();
            ScrollToAndHighlight(m.ID);
        }

        private void ScrollToAndHighlight(int msgId)
        {
            if (ScrollToAndFlash(msgId)) return;                                            // in the loaded window
            if (_selectedChat != null) { var _ = OpenChat(_selectedChat, msgId); }          // else load around it
        }

        // ── Pinned-messages list (Part 2) ────────────────────────────────────

        /// <summary>Builds the ordered (newest→oldest) rows for the pinned-list popup.</summary>
        private List<(int id, string label, string preview)> BuildPinnedRows()
        {
            var rows = new List<(int, string, string)>();
            if (_pinnedMessages == null) return rows;
            int n = _pinnedMessages.Count;
            for (int i = 0; i < n; i++)
            {
                var m = _pinnedMessages[i];
                rows.Add((m.ID, "Pinned #" + (i + 1) + " of " + n, GetDisplayText(m)));
            }
            return rows;
        }

        /// <summary>Opens the "show all pinned" popup, positioned under the pinned bar.</summary>
        private void ShowPinnedList()
        {
            if (_pinnedMessages == null || _pinnedMessages.Count == 0) return;
            if (_pinnedListForm != null && !_pinnedListForm.IsDisposed) { _pinnedListForm.Activate(); return; }

            // Step 3.3: the popup is built at the DPI of WHERE IT OPENS: under the pinned bar's right end, else this window's
            // top centre — clamped into `scr`, as the popup itself is below (the right edge is exclusive, hence the - 1).
            var scr = Screen.FromControl(this).WorkingArea;   // physical screen maths: stays raw
            var at = _pinnedBar != null && _pinnedBar.Visible
                ? _pinnedBar.PointToScreen(new Point(_pinnedBar.Width - 1, _pinnedBar.Height))
                : PointToScreen(new Point(Width / 2, Px(80)));
            var form = new PinnedListForm(_dark, _accent,
                Dpi.ForPopup(new Point(Math.Max(scr.Left, Math.Min(at.X, scr.Right - 1)), Math.Max(scr.Top, Math.Min(at.Y, scr.Bottom - 1)))));
            _pinnedListForm = form;
            form.JumpRequested += id => { ScrollToAndHighlight(id); if (!form.IsDisposed) form.Close(); };
            form.ContextRequested += OnPinnedRowContext;
            form.FormClosed += (s, e) => { if (_pinnedListForm == form) _pinnedListForm = null; };
            form.SetPins(BuildPinnedRows());

            // Anchor the popup under the pinned bar's right edge (where the list icon is).
            var anchor = _pinnedBar != null && _pinnedBar.Visible
                ? _pinnedBar.PointToScreen(new Point(_pinnedBar.Width - form.Width, _pinnedBar.Height))
                : PointToScreen(new Point((Width - form.Width) / 2, Px(80)));
            int fx = Math.Max(scr.Left, Math.Min(anchor.X, scr.Right - form.Width));
            int fy = Math.Max(scr.Top, Math.Min(anchor.Y, scr.Bottom - form.Height));
            form.Location = new Point(fx, fy);
            form.Show(this);
        }

        /// <summary>Right-click / long-press on a pinned row → themed Jump / Unpin menu.</summary>
        private void OnPinnedRowContext(int msgId, Point screenPt)
        {
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, "↧   Jump to message", () => { ScrollToAndHighlight(msgId); _pinnedListForm?.Close(); });
            AddMenuItem(menu, "📌   Unpin", () => UnpinFromList(msgId));
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.ShowAt(screenPt);
        }

        /// <summary>Unpin from the list: for-everyone where the peer allows it, else just on our side.</summary>
        private async void UnpinFromList(int msgId)
        {
            var chat = _selectedChat;
            if (chat == null) return;

            // Private chats let you choose; in groups/channels unpin needs pin rights (the RPC enforces it).
            bool isPrivate = chat.Peer is InputPeerUser;
            bool forEveryone;
            if (isPrivate)
            {
                int choice = ThemedDialog.Show(this, "Unpin message",
                    "Unpin this message?", "Unpin for everyone", "Unpin for me", "Cancel");
                if (choice != 0 && choice != 1) return;
                forEveryone = choice == 0;
            }
            else
            {
                int choice = ThemedDialog.Show(this, "Unpin message",
                    "Unpin this message for everyone?", "Unpin", "Cancel");
                if (choice != 0) return;
                forEveryone = true;
            }

            try
            {
                await _service.UnpinMessageAsync(chat.Peer, msgId, forEveryone);
                if (_selectedChat != chat) return;
                RemovePinLocally(msgId);
                if (_pinnedListForm != null && !_pinnedListForm.IsDisposed) _pinnedListForm.SetPins(BuildPinnedRows());
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Unpin failed", ex.Message, "OK"); }
        }

        /// <summary>Removes a pin from the local set and keeps the bar/index in sync after an unpin.</summary>
        private void RemovePinLocally(int msgId)
        {
            if (_pinnedMessages == null) return;
            int idx = _pinnedMessages.FindIndex(m => m.ID == msgId);
            if (idx < 0) return;
            _pinnedMessages.RemoveAt(idx);
            if (_pinnedMessages.Count == 0) _pinnedIndex = 0;
            else
            {
                if (_pinnedIndex > idx) _pinnedIndex--;          // keep pointing at the same logical pin
                _pinnedIndex %= _pinnedMessages.Count;           // and if it was the last/current, wrap to a valid one
            }
            UpdatePinnedBar();
        }

        /// <summary>Deletes one message, or a whole group's (the menu on an album's caption), after asking; then takes exactly
        /// those out of the view (an album loses only the deleted items: <see cref="RemoveMessagesFromView"/>).</summary>
        private async void DeleteMessages(int[] ids)
        {
            if (ids == null || ids.Length == 0 || _selectedChat == null) return;
            // 0 = for everyone (revoke), 1 = for me only, 2 = cancel.
            int choice = ThemedDialog.Show(this, ids.Length == 1 ? "Delete message" : "Delete messages",
                ids.Length == 1 ? "Delete this message?" : "Delete these " + ids.Length + " messages?", "For everyone", "Just for me", "Cancel");
            if (choice != 0 && choice != 1) return;
            bool revoke = choice == 0;
            var chat = _selectedChat;

            try
            {
                await _service.DeleteMessagesAsync(chat.Peer, ids, revoke);
                if (!ReferenceEquals(chat, _selectedChat)) return;   // another chat is open by now: its view was built without them
                RemoveMessagesFromView(new HashSet<int>(ids));
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Delete failed", "Couldn't delete: " + ex.Message, "OK");
            }
        }

        /// <summary>Takes deleted messages out of the OPEN chat's view (our own delete, or one from elsewhere). A bubble goes
        /// with its message; an ALBUM loses only the deleted items - a tile or a row each - and goes when none is left. One
        /// deleted photo used to take the whole group off the screen, or (when it was not the first item) nothing at all.
        /// Returns how many messages were taken out.</summary>
        private int RemoveMessagesFromView(HashSet<int> ids)
        {
            int removed = 0;
            foreach (var c in _messagePanel.Controls.Cast<Control>().ToArray())
            {
                var album = c as MessageBubbleControl;
                if (album != null && album.IsAlbum)
                {
                    int before = removed, key = album.MessageId;
                    foreach (int id in album.AlbumMessageIds)
                        if (ids.Contains(id) && album.RemoveAlbumItem(id)) removed++;
                    if (removed == before) continue;   // untouched
                    if (album.AlbumCount > 0)          // it still has items
                    {
                        if (album.MessageId != key)
                        {
                            // it answers to its next item now (its first one went): a selection that held the old id follows
                            // it, and the reactions under it are that item's (they were the deleted one's)
                            if (_selectedMessageIds.Remove(key)) _selectedMessageIds.Add(album.MessageId);
                            var first = _currentChatMessages.FirstOrDefault(x => x.ID == album.MessageId);
                            if (first != null) album.SetReactions(ExtractReactions(first));
                        }
                        continue;
                    }
                }
                else
                {
                    int id = BubbleMsgId(c);
                    if (id == 0 || !ids.Contains(id)) continue;
                    removed++;
                }
                _messagePanel.Controls.Remove(c);
                c.Dispose();   // MessageBubbleControl.Dispose → _rich.Dispose → text bitmap freed; VoiceBubble cancels its download
            }
            _currentChatMessages.RemoveAll(x => ids.Contains(x.ID));
            foreach (var id in ids) _shownMessageIds.Remove(id);
            if (_replyTarget != null && ids.Contains(_replyTarget.ID)) CancelReply();
            return removed;
        }

        /// <summary>Re-uploads a previously failed media send, reusing the same (failed) bubble.</summary>
        private async void RetrySend(MessageBubbleControl bubble)
        {
            if (_selectedChat == null || _service.Client == null) return;
            if (!_failedSends.TryGetValue(bubble, out var info)) return;

            _failedSends.Remove(bubble);
            int tempId = bubble.MessageId;                      // still the negative placeholder id
            if (tempId < 0) _pendingBubbles[tempId] = bubble;   // re-track for echo reconciliation
            bubble.Failed = false;
            bubble.Pending = true;
            bubble.Invalidate();

            try
            {
                var msg = await MediaSender.SendAsync(_service.Client, _selectedChat.Peer,
                    info.path, info.mode, info.caption, null, System.Threading.CancellationToken.None);

                // Swap in place unless the live echo already reconciled it (TrySwapPendingBubble).
                if (bubble.MessageId < 0)
                {
                    _pendingBubbles.Remove(tempId);
                    _shownMessageIds.Remove(tempId);
                    if (msg != null)
                    {
                        _shownMessageIds.Add(msg.ID);
                        bubble.MessageId = msg.ID;
                        if (!_currentChatMessages.Any(x => x.ID == msg.ID)) _currentChatMessages.Add(msg);
                    }
                    bubble.Pending = false;
                    bubble.Invalidate();
                }
            }
            catch (Exception)
            {
                if (bubble.MessageId < 0) _pendingBubbles.Remove(bubble.MessageId);
                bubble.Pending = false;
                bubble.Failed = true;
                _failedSends[bubble] = info;
                bubble.Invalidate();
            }
        }

        /// <summary>Forwards one message, or a whole group's items together (the menu on an album's caption), to a chat
        /// picked from a themed dialog.</summary>
        private async void ForwardMessages(int[] ids)
        {
            if (_selectedChat == null || ids == null || ids.Length == 0) return;

            List<ChatEntry> targets;
            bool hide, drop;   // T4: the picker's forward options
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats; hide = picker.HideSender; drop = picker.DropCaptions;   // T4
            }
            int ok = await ForwardToTargets(_selectedChat.Peer, ids, targets, hide, drop);
            ThemedDialog.Show(this, ok > 0 ? "Forwarded" : "Forward failed", ForwardResultText(ok, targets.Count), "OK");
        }

        // ── Multi-select ─────────────────────────────────────────────────────────

        private void OnBubbleSelectionToggled(object sender, EventArgs e)
        {
            var b = (MessageBubbleControl)sender;
            ToggleSelection(b.MessageId, b);
        }

        private void OnVoiceSelectionToggled(object sender, EventArgs e)
        {
            var v = (VoiceBubbleControl)sender;
            ToggleSelection(v.MessageId, v);
        }

        /// <summary>Enters selection mode and selects the message the menu was opened on.</summary>
        private void EnterSelectionMode(int firstId)
        {
            _selectionMode = true;
            _selectedMessageIds.Clear();
            if (firstId > 0) _selectedMessageIds.Add(firstId);

            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.SelectionMode = true; b.Selected = _selectedMessageIds.Contains(b.MessageId); b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.SelectionMode = true; v.Selected = _selectedMessageIds.Contains(v.MessageId); v.Invalidate(); }
            }

            ShowSelectionBar(true);
            UpdateSelectionCount();
        }

        private void ExitSelectionMode()
        {
            _selectionMode = false;
            _selectedMessageIds.Clear();
            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.SelectionMode = false; b.Selected = false; b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.SelectionMode = false; v.Selected = false; v.Invalidate(); }
            }
            ShowSelectionBar(false);
        }

        private void ToggleSelection(int messageId, Control ctl)
        {
            if (messageId <= 0) return;
            bool nowSelected;
            if (_selectedMessageIds.Contains(messageId)) { _selectedMessageIds.Remove(messageId); nowSelected = false; }
            else { _selectedMessageIds.Add(messageId); nowSelected = true; }

            if (ctl is MessageBubbleControl b) b.Selected = nowSelected;
            else if (ctl is VoiceBubbleControl v) v.Selected = nowSelected;
            ctl.Invalidate();

            if (_selectedMessageIds.Count == 0) { ExitSelectionMode(); return; }
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            if (_selectionBar != null) _selectionBar.Count = _selectedMessageIds.Count;
        }

        private void ShowSelectionBar(bool show)
        {
            if (_selectionBar == null) return;
            int scrollY = _messagePanel != null ? -_messagePanel.AutoScrollPosition.Y : 0;
            _selectionBar.Visible = show;
            SetRowPx(_rightLayout, 3, show ? 48 : 0);   // step 3.4: logical px
            if (_messagePanel != null) _messagePanel.AutoScrollPosition = new Point(0, scrollY);
        }

        private void ThemeSelectionBar()
        {
            if (_selectionBar != null) { _selectionBar.AccentColor = _accent; _selectionBar.IsDark = _dark; }
            if (_recordingBar != null) _recordingBar.IsDark = _dark;
        }

        /// <summary>Forwards all selected messages to a chat picked from the themed picker.</summary>
        private async void ForwardSelected()
        {
            if (_selectedChat == null || _selectedMessageIds.Count == 0) return;

            List<ChatEntry> targets;
            bool hide, drop;   // T4: the picker's forward options
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats; hide = picker.HideSender; drop = picker.DropCaptions;   // T4
            }

            // chronological order; a selected ALBUM goes whole (its bubble is selected by its first item's id, and only that
            // item used to be forwarded)
            var all = new HashSet<int>(_selectedMessageIds);
            foreach (Control c in _messagePanel.Controls)
            {
                var ab = c as MessageBubbleControl;
                if (ab != null && ab.IsAlbum && _selectedMessageIds.Contains(ab.MessageId))
                    foreach (int id in ab.AlbumMessageIds) all.Add(id);
            }
            var ids = all.OrderBy(x => x).ToArray();
            int ok = await ForwardToTargets(_selectedChat.Peer, ids, targets, hide, drop);
            if (ok > 0) ExitSelectionMode();
            ThemedDialog.Show(this, ok > 0 ? "Forwarded" : "Forward failed",
                ok == 0 ? "Couldn't forward. Make sure your VPN is on."
                        : (ids.Length + (ids.Length == 1 ? " message" : " messages") + " forwarded to " + ok + (ok == 1 ? " chat." : " chats.")), "OK");
        }

        // ── Display text / media placeholders ────────────────────────────────

        private static string GetDisplayText(Message m)
        {
            if (!string.IsNullOrEmpty(m.message)) return m.message; // text or media caption
            return MediaPlaceholder(m.media);
        }

        /// <summary>Preview text for any message union — service events render their system text.</summary>
        private string GetDisplayText(MessageBase mb)
        {
            if (mb is MessageService svc) return ServiceText(svc) ?? "";
            return mb is Message m ? GetDisplayText(m) : "";
        }

        private static DateTime MsgDate(MessageBase mb)
            => mb is Message m ? m.date : (mb is MessageService s ? s.date : default(DateTime));

        private static string MediaPlaceholder(MessageMedia media)
        {
            switch (media)
            {
                case null: return "";
                case MessageMediaPhoto _: return "📷 Photo";
                case MessageMediaGeoLive _: return "📍 Live location"; // derives from Geo — keep first
                case MessageMediaVenue _: return "📍 Venue";           // derives from Geo — keep first
                case MessageMediaGeo _: return "📍 Location";
                case MessageMediaContact _: return "👤 Contact";
                case MessageMediaPoll _: return "📊 Poll";
                case MessageMediaDice _: return "🎲 Dice";
                case MessageMediaDocument mdoc: return DocumentPlaceholder(mdoc.document as Document);
                default: return "[media]";
            }
        }

        private static string DocumentPlaceholder(Document doc)
        {
            if (doc == null) return "📎 File";
            var attrs = doc.attributes ?? new DocumentAttribute[0];

            if (attrs.Any(a => a is DocumentAttributeSticker)) return "🎭 Sticker";
            if (attrs.Any(a => a is DocumentAttributeAnimated)) return "▷ GIF";

            var audio = attrs.OfType<DocumentAttributeAudio>().FirstOrDefault();
            if (audio != null)
                return (audio.flags & DocumentAttributeAudio.Flags.voice) != 0 ? "🎤 Voice message" : "🎵 Audio";

            if (attrs.Any(a => a is DocumentAttributeVideo)) return "🎥 Video";

            var fn = attrs.OfType<DocumentAttributeFilename>().FirstOrDefault();
            return fn != null ? "📎 File: " + fn.file_name : "📎 File";
        }

        private static string DisplayName(User user)
        {
            var name = string.Join(" ", new[] { user.first_name, user.last_name }
                .Where(s => !string.IsNullOrEmpty(s))).Trim();
            if (!string.IsNullOrEmpty(name)) return name;
            if (!string.IsNullOrEmpty(user.MainUsername)) return "@" + user.MainUsername;
            return "User " + user.id;
        }

        private void OpenSettings() { OpenSettings(false); }

        /// <summary>Redesign 2.7b: the drawer's "Appearance…" row.</summary>
        private void OpenAppearanceSettings() { OpenSettings(true); }

        private void OpenSettings(bool appearance)
        {
            // SettingsForm edits and persists AppSettings.Instance directly on OK; the service powers Devices.
            bool proxyChanged = false;
            using (var dlg = new SettingsForm(_service))
            {
                if (appearance) dlg.ShowAppearancePage();
                dlg.ProxyChangeApplied += () => proxyChanged = true;
                dlg.ShowDialog(this);
            }
            // A proxy switched from Settings must take effect like one switched from the pill — the
            // device log caught this door persisting a change with no APPLY-LIVE behind it. Deferred
            // until the dialog is CLOSED so the teardown/reconnect can't race a modal still on screen.
            if (proxyChanged) { RefreshProxyPill(); var _ = ApplyProxyChangeAsync(); }
        }

    }
}
