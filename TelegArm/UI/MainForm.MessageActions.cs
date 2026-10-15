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

        /// <summary>Builds + shows the per-message context menu (right-click / touch-hold).</summary>
        private void OnBubbleContextMenu(object sender, Point screenPt)
        {
            var bubble = sender as MessageBubbleControl;
            if (bubble == null) return;
            Message msg = bubble.MessageId > 0
                ? _currentChatMessages.FirstOrDefault(x => x.ID == bubble.MessageId)
                : null;
            // Retry only applies to a failed optimistic media send (we kept its source).
            var failed = bubble.Failed && _failedSends.ContainsKey(bubble) ? bubble : null;
            ShowMessageMenu(bubble, msg, screenPt, failed);
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

        /// <summary>Builds + shows the themed per-message menu shared by message and voice bubbles.</summary>
        private void ShowMessageMenu(Control ctl, Message msg, Point screenPt, MessageBubbleControl failedBubble)
        {
            if (_selectedChat == null) return;

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
                string copyText = msg?.message ?? "";
                if (!string.IsNullOrEmpty(copyText))
                    AddMenuItem(menu, "⧉   Copy text", () => { try { Clipboard.SetText(copyText); } catch { } });
            }
            if (mb != null && mb.HasSelectableText && !mb.HasTextSelection)
                AddMenuItem(menu, "✍   Select Text", () => mb.SelectAllText());
            if (msg != null)
            {
                AddMenuItem(menu, "↩   Reply", () => StartReply(msg));
                AddReactMenu(menu, msg);
                if ((msg.flags & Message.Flags.pinned) != 0)
                    AddMenuItem(menu, "📌   Unpin", () => UnpinMessage(msg));
                else
                    AddMenuItem(menu, "📌   Pin", () => PinMessage(msg));
                if (IsOut(msg) && !string.IsNullOrEmpty(msg.message))
                    AddMenuItem(menu, "✎   Edit", () => StartEdit(msg));
                // In selection mode, Forward acts on the whole selection (not just this bubble).
                if (_selectionMode && _selectedMessageIds.Count > 0)
                    AddMenuItem(menu, "⮕   Forward (" + _selectedMessageIds.Count + ")", () => ForwardSelected());
                else
                    AddMenuItem(menu, "⮕   Forward", () => ForwardMessage(msg));
                AddMenuItem(menu, "☑   Select", () => EnterSelectionMode(msg.ID));
                if (MediaClassifier.FromMessage(msg) != null)   // any media (photo/video/audio/voice/file/gif/sticker)
                {
                    AddMenuItem(menu, "💾   Save", () => { var _ = SaveMediaAsync(msg); });
                    AddMenuItem(menu, "📁   Reveal in folder", () => RevealMedia(msg));
                }
                AddMenuItem(menu, "✖   Delete", () => DeleteMessage(ctl, msg));
            }

            if (menu.Items.Count == 0) { menu.Dispose(); return; }

            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.ShowAt(screenPt);
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

        private async void DeleteMessage(Control bubble, Message msg)
        {
            // 0 = for everyone (revoke), 1 = for me only, 2 = cancel.
            int choice = ThemedDialog.Show(this, "Delete message",
                "Delete this message?", "For everyone", "Just for me", "Cancel");
            if (choice != 0 && choice != 1) return;
            bool revoke = choice == 0;

            try
            {
                await _service.DeleteMessagesAsync(_selectedChat.Peer, new[] { msg.ID }, revoke);
                _messagePanel.Controls.Remove(bubble);
                bubble.Dispose();
                _currentChatMessages.RemoveAll(x => x.ID == msg.ID);
                _shownMessageIds.Remove(msg.ID);
                if (_replyTarget != null && _replyTarget.ID == msg.ID) CancelReply();
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Delete failed", "Couldn't delete: " + ex.Message, "OK");
            }
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

        /// <summary>Forwards a message to a chat picked from a themed dialog.</summary>
        private async void ForwardMessage(Message msg)
        {
            if (_selectedChat == null) return;

            List<ChatEntry> targets;
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }
            int ok = await ForwardToTargets(_selectedChat.Peer, new[] { msg.ID }, targets);
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
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }

            var ids = _selectedMessageIds.OrderBy(x => x).ToArray();   // chronological order
            int ok = await ForwardToTargets(_selectedChat.Peer, ids, targets);
            if (ok > 0) ExitSelectionMode();
            ThemedDialog.Show(this, ok > 0 ? "Forwarded" : "Forward failed",
                ok == 0 ? "Couldn't forward — make sure your VPN is on."
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
