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
        private async void SendCurrentMessage()
        {
            if (_thread != null) { SendThreadComment(); return; }   // COMMENTS-POST: route to the discussion-thread send
            if (_footerKind != ComposerKind.Compose) return;   // gated: composer isn't shown in non-compose states
            var raw = _messageInput.Text.Trim();
            if (raw.Length == 0 || _selectedChat == null) return;

            var chat = _selectedChat;

            // Editing an existing message takes precedence over sending a new one.
            if (_editTarget != null)
            {
                var target = _editTarget;
                _messageInput.Text = "";
                CancelReply();          // also clears _editTarget + hides the strip
                await ApplyEdit(chat, target, raw);   // ApplyEdit parses the markdown itself
                return;
            }

            // SEND-ENTITIES: markdown → plain text + entities (no markers → entities null → plain send, unchanged).
            TL.MessageEntity[] entities;
            string text = MarkdownEntities.Parse(raw, out entities);
            if (text.Length == 0) return;   // the message was only markers

            int replyId = _replyTarget?.ID ?? 0;       // capture before clearing the composer
            string replyPreview = null;
            if (_replyTarget != null)
            {
                replyPreview = GetDisplayText(_replyTarget);
                if (replyPreview.Length > 60) replyPreview = replyPreview.Substring(0, 60) + "…";
            }

            _messageInput.Text = "";
            CancelReply();
            ClearDraftAfterSend(chat);   // DRAFTS: the composer just emptied on send → clear any draft for this chat

            // Optimistic bubble — clock while sending, then ✓ on confirm. Carry the reply quote.
            var bubble = CreateBubble(text, null, true, DateTime.UtcNow, 0, entities);
            bubble.Pending = true;
            if (replyPreview != null) { bubble.ReplyPreview = replyPreview; bubble.Measure(); }
            _messagePanel.Controls.Add(bubble);
            ScrollMessagesToBottom();

            try
            {
                var sent = await _service.SendTextAsync(chat.Peer, text, replyId, entities);
                if (sent != null)
                {
                    _shownMessageIds.Add(sent.ID);     // dedupe a possible echo update
                    bubble.MessageId = sent.ID;        // make the bubble actionable (reply/forward/delete)
                    bubble.Pending = false;            // → single check (✓)
                    bubble.Invalidate();
                    if (!_currentChatMessages.Any(x => x.ID == sent.ID)) _currentChatMessages.Add(sent);
                    UpdateChatListForMessage(chat.PeerId, sent, true);   // update the chat-list row now
                }
            }
            catch
            {
                bubble.Pending = false;
                bubble.Failed = true;                  // → red failed mark on the bubble
                bubble.Invalidate();
            }
        }

        // ── SEND-ENTITIES: composer formatting helpers — wrap the selection in markdown; MarkdownEntities.Parse
        //    turns it into MessageEntity[] on send. Uses only public SelectionStart/SelectionLength/Text (no internals).
        // Step 2.7d: the bodies moved to ComposerFormat (UI/ComposerEditorForm.cs), shared with the editor window.
        private void WrapComposerSelection(string open, string close) { ComposerFormat.Wrap(_messageInput, open, close); }

        private void InsertComposerLink() { ComposerFormat.InsertLink(_messageInput); }

        // ── Step 2.7d (plan R8): the composer's editor window ──────────────────────────────────────────────────────
        private ComposerEditorForm _composerEditor;
        private ChatEntry _composerEditorChat;                  // what the window was opened for: its text belongs there only
        private object _composerEditorEdit, _composerEditorThread;
        private long _composerEditorAccount;

        /// <summary>True while the editor window is open AND MainForm still shows the chat (and edit, thread and account) it
        /// was opened for. Only then does the window hold this composer's live text.</summary>
        private bool EditorOwnsComposer()
        {
            var ed = _composerEditor;
            return ed != null && !ed.IsDisposed && ReferenceEquals(_selectedChat, _composerEditorChat)
                && ReferenceEquals(_editTarget, _composerEditorEdit) && ReferenceEquals(_thread, _composerEditorThread)
                && AccountContext.ActiveId == _composerEditorAccount;
        }

        /// <summary>The composer's text, or the editor window's while it owns the composer (see EditorOwnsComposer). The
        /// draft paths read it through here, so a tray Exit or a chat switch under the window saves what was typed — and
        /// never into another chat's draft (review 2.7d F1).</summary>
        private string CurrentComposerText()
        {
            if (EditorOwnsComposer()) return _composerEditor.EditorText;
            return _messageInput != null ? _messageInput.Text : "";
        }

        /// <summary>The chat or account is about to change under the editor window (a notification click; the modal blocks
        /// every other door). PersistDraftForCurrentChat has just saved the window's text as the chat's draft through the
        /// OUTGOING account, so close the window now — it must not outlive its chat (review 2.7d F1/F2/F3).</summary>
        private void CloseComposerEditorForSwitch()
        {
            var ed = _composerEditor;
            if (ed == null || ed.IsDisposed) return;
            Logger.Diag("[EDITOR] the chat or account is changing under the window: its text was saved as the draft; closing it");
            ed.CloseKeepingText();
        }

        /// <summary>Opens the editor window on the composer's text — modal (see ComposerEditorForm). On close the text comes
        /// back into the composer, unless the chat changed under the window (a notification click): then it is kept as
        /// that chat's draft and never written into another chat's composer. Send goes through SendCurrentMessage.</summary>
        private void OpenComposerEditor()
        {
            if (_composerEditor != null || _messageInput == null || !_messageInput.Enabled || _selectedChat == null) return;
            if (_recordingBar != null && _recordingBar.Visible) return;   // a voice note owns the composer cell
            var chat = _selectedChat;
            var edit = _editTarget;
            var thread = _thread;
            long account = AccountContext.ActiveId;
            string title = edit != null ? "Edit message" : thread != null ? "Comment" : (chat.Title ?? "Compose");
            string text;
            bool send;
            using (var f = new ComposerEditorForm(title, _messageInput.Text, _service))
            {
                // A sticker goes to the window's chat only (review 2.7d F3): never to whatever MainForm moved to meanwhile.
                f.StickerPicked += doc =>
                {
                    if (EditorOwnsComposer()) SendDocument(doc);
                    else Logger.Diag("[EDITOR] sticker dropped: the chat changed under the window");
                };
                _composerEditor = f;
                _composerEditorChat = chat; _composerEditorEdit = edit; _composerEditorThread = thread; _composerEditorAccount = account;
                try { f.ShowDialog(this); }
                finally { _composerEditor = null; _composerEditorChat = null; _composerEditorEdit = null; _composerEditorThread = null; }
                text = f.EditorText ?? "";
                send = f.SendRequested;
            }
            if (IsDisposed || _messageInput == null) return;
            if (AccountContext.ActiveId != account)
            {
                // The switch saved the window's text through the outgoing account and closed it; _service is the other
                // account's now, so nothing may be sent or saved from here (review 2.7d F2).
                Logger.Diag("[EDITOR] the account changed under the window: its text was saved as the draft by the switch");
                return;
            }
            if (!ReferenceEquals(_selectedChat, chat) || !ReferenceEquals(_editTarget, edit) || !ReferenceEquals(_thread, thread))
            {
                if (edit == null && thread == null) SaveDraftFor(chat, text);   // same account: _service is still the right one
                Logger.Diag("[EDITOR] the chat changed under the window: the text was kept as its draft, not sent");
                return;
            }
            // The window keeps "\n"; a native multi-line EDIT (the Plain engine) breaks lines on CRLF only (review 2.7d F4).
            if (_messageInput.Plain != null) text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (text != _messageInput.Text) _messageInput.Text = text;
            _messageInput.SelectionStart = text.Length;
            if (send) { SendPathLog("editor"); SendCurrentMessage(); }
            else _messageInput.Focus();
        }

        private void PasteIntoComposer()
        {
            var tb = _messageInput; if (tb == null || !tb.Enabled) return;   // review 1.5: never write into a disabled composer
            try
            {
                if (!Clipboard.ContainsText()) return;
                string clip = Clipboard.GetText();
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                tb.Text = t.Substring(0, start) + clip + t.Substring(start + len);
                tb.SelectionStart = start + clip.Length;
                tb.Focus();
            }
            catch { }
        }

        /// <summary>COMMENTS-POST: post the composer text as a comment in the open discussion thread. Direct post is
        /// the PRIMARY path (Telegram allows commenting without the group appearing in your chat list); join is only a
        /// FALLBACK if the server rejects for membership. Optimistic bubble → raw thread-send → live-append on success.</summary>
        private async void SendThreadComment()
        {
            var thread = _thread;
            if (thread == null || _messageInput == null) return;
            var raw = _messageInput.Text.Trim();
            if (raw.Length == 0) return;

            _messageInput.Text = "";
            CancelReply();

            // SEND-ENTITIES: markdown → plain text + entities for the comment too.
            TL.MessageEntity[] entities;
            string text = MarkdownEntities.Parse(raw, out entities);
            if (text.Length == 0) return;

            var bubble = CreateBubble(text, null, true, DateTime.UtcNow, 0, entities);   // outgoing, optimistic
            bubble.Pending = true;
            _messagePanel.Controls.Add(bubble);
            ScrollMessagesToBottom();

            await PostThreadComment(thread, text, entities, bubble, allowJoinRetry: true);
        }

        /// <summary>The send + membership-fallback core. Posts via the GROUP-scoped thread (GroupPeer + GroupRootId) —
        /// the same thread reads show, and the form a non-admin can post through (channel-peer sends are admin-only).
        /// On success: confirm the optimistic bubble + cache the (group-side) message. On a membership rejection
        /// (CHAT_WRITE_FORBIDDEN): join the group, then retry ONCE. Any other error (or a failed join): drop the
        /// optimistic bubble and restore the text so it isn't lost.</summary>
        private async System.Threading.Tasks.Task PostThreadComment(ThreadCtx thread, string text, TL.MessageEntity[] entities, MessageBubbleControl bubble, bool allowJoinRetry)
        {
            try
            {
                var sent = await _service.SendThreadCommentAsync(thread.GroupPeer, thread.GroupRootId, text, entities);
                if (sent != null)
                {
                    _shownMessageIds.Add(sent.ID);                       // dedupe the echo update
                    if (bubble != null) { bubble.MessageId = sent.ID; bubble.Pending = false; bubble.Invalidate(); }
                    if (!_currentChatMessages.Any(x => x.ID == sent.ID)) _currentChatMessages.Add(sent);
                }
                else if (bubble != null) { bubble.Pending = false; bubble.Invalidate(); }   // sent, but no full message echoed
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post ok root=" + thread.GroupRootId + " post=" + thread.PostMsgId);
            }
            catch (Exception ex)
            {
                if (allowJoinRetry && IsMembershipError(ex) && _selectedChat != null && _selectedChat.Peer is InputPeerChannel ipc)
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post rejected (membership) → join + retry");
                    try
                    {
                        await _service.JoinChannelAsync(ipc);
                        if (_selectedChat.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + _selectedChat.PeerId + " ok");
                        await PostThreadComment(thread, text, entities, bubble, allowJoinRetry: false);   // retry ONCE, no further join
                        return;
                    }
                    catch (Exception jex)
                    {
                        FailThreadPost(bubble, text);
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join fail: " + jex.Message);
                        ThemedDialog.Show(this, "Comments", "Couldn't join to comment:\n" + jex.Message, "OK");
                        return;
                    }
                }
                FailThreadPost(bubble, text);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post fail: " + ex.Message);
                ThemedDialog.Show(this, "Comments", FriendlyPostError(ex), "OK");
            }
        }

        /// <summary>COMMENTS-NAV-FIX 2.1: a human message for a failed comment post. CHAT_ADMIN_REQUIRED = the channel
        /// restricts commenting (join can't fix it — never a join prompt); the rest map common send errors.</summary>
        private static string FriendlyPostError(Exception ex)
        {
            string m = ex?.Message ?? "";
            if (m.IndexOf("CHAT_ADMIN_REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Commenting is restricted on this channel — you can't comment here.";
            if (m.IndexOf("MSG_TOO_LONG", StringComparison.OrdinalIgnoreCase) >= 0) return "That comment is too long.";
            if (m.IndexOf("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) >= 0)
                return "You're commenting too fast — wait a moment and try again.";
            return "Couldn't post the comment:\n" + m;
        }

        /// <summary>COMMENTS-POST 2.2: a comment send failed — drop the optimistic bubble and put the text back in the
        /// composer so the user doesn't lose it.</summary>
        private void FailThreadPost(MessageBubbleControl bubble, string text)
        {
            // Unconditional remove+dispose (Controls.Remove is a safe no-op if it isn't a child) so a rejected post
            // NEVER leaves a phantom optimistic bubble, even if the panel was reconciled during the await.
            try { if (bubble != null) { _messagePanel.Controls.Remove(bubble); bubble.Dispose(); } } catch { }
            if (string.IsNullOrEmpty(_messageInput.Text)) _messageInput.Text = text;
        }

        /// <summary>True when a send was rejected because the discussion group requires membership (the ONLY case that
        /// triggers the join fallback — never on FLOOD_WAIT / too-long / network errors).</summary>
        private static bool IsMembershipError(Exception ex)
        {
            string m = ex?.Message ?? "";
            return m.IndexOf("CHAT_WRITE_FORBIDDEN", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("CHANNEL_PRIVATE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Edits a message server-side, then rebuilds its bubble in place with the new text.</summary>
        private async System.Threading.Tasks.Task ApplyEdit(ChatEntry chat, Message target, string newText)
        {
            try
            {
                // SEND-ENTITIES: the edited text is markdown too → plain text + entities.
                TL.MessageEntity[] entities;
                string plain = MarkdownEntities.Parse(newText, out entities);
                await _service.EditMessageAsync(chat.Peer, target.ID, plain, entities);
                if (_selectedChat != chat) return;     // user switched chats meanwhile
                var msg = _currentChatMessages.FirstOrDefault(x => x.ID == target.ID) ?? target;
                msg.message = plain;                    // TL Message.message is settable
                msg.entities = entities;                // so the rebuilt bubble shows the new formatting
                msg.edit_date = DateTime.UtcNow;        // so the rebuilt bubble shows "edited"
                msg.flags |= Message.Flags.has_edit_date;
                RebuildBubble(target.ID, msg);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Edit", "Couldn't edit: " + ex.Message, "OK");
            }
        }

        /// <summary>Replaces the bubble for <paramref name="messageId"/> with a fresh one (same position),
        /// re-wiring entities/reactions and re-folding any media/card. Preserves scroll (or stays pinned to
        /// the bottom if the view was at the bottom, so a card popping in on the newest message stays visible).</summary>
        private void RebuildBubble(int messageId, Message msg)
        {
            for (int i = 0; i < _messagePanel.Controls.Count; i++)
            {
                if (_messagePanel.Controls[i] is MessageBubbleControl old && old.MessageId == messageId)
                {
                    // Preserve the group sender name (MakeMessageBubble needs it; the cache has it).
                    string sender = null;
                    if (_selectedChat != null && _selectedChat.IsGroup && !IsOut(msg) && msg.from_id is PeerUser pu)
                        _peerNames.TryGetValue(pu.user_id, out sender);

                    bool atBottom = -_messagePanel.AutoScrollPosition.Y + _messagePanel.ClientSize.Height
                                    >= _messagePanel.DisplayRectangle.Height - Px(8);
                    int scrollY = -_messagePanel.AutoScrollPosition.Y;

                    _messagePanel.SuspendLayout();
                    var fresh = MakeMessageBubble(_selectedChat, msg, sender) as MessageBubbleControl;
                    if (fresh == null) { _messagePanel.ResumeLayout(); return; }
                    ApplyReactions(fresh, msg);
                    ApplyEntities(fresh, msg);   // re-wires inline links + the card-tap (LinkClicked) path
                    _messagePanel.Controls.Add(fresh);
                    _messagePanel.Controls.SetChildIndex(fresh, i);
                    _messagePanel.Controls.Remove(old);
                    old.Dispose();
                    _messagePanel.ResumeLayout(true);

                    if (atBottom) ScrollMessagesToBottom();
                    else _messagePanel.AutoScrollPosition = new Point(0, scrollY);
                    return;
                }
            }
        }

        // ── Emoji picker ─────────────────────────────────────────────────────────

        private void OpenEmojiPicker()
        {
            if (_selectedChat == null) return;
            // Step 3.1: a popup takes the DPI of WHERE IT OPENS (the anchor), not this window's; it is built at that DPI.
            var p = _emojiButton.PointToScreen(Point.Empty);
            int dpi = Dpi.ForPopup(p);
            var picker = new EmojiPicker(_service, _dark, _accent, dpi: dpi);
            picker.Picked += InsertEmoji;
            picker.DocumentPicked += SendDocument;
            picker.Location = new Point(p.X - picker.Width + _emojiButton.Width, p.Y - picker.Height - Dpi.Px(4, dpi)); // above the button (logical gap at the popup's DPI)
            picker.Show(this);
            picker.Activate();
        }

        /// <summary>Sends a picked sticker/GIF document to the open chat (optimistic bubble + chat list).</summary>
        private async void SendDocument(Document doc)
        {
            if (_selectedChat == null || _service.Client == null || doc == null) return;
            var chat = _selectedChat;
            try
            {
                var msg = await _service.SendDocumentAsync(chat.Peer, doc);
                if (msg != null)
                {
                    if (_selectedChat == chat && _shownMessageIds.Add(msg.ID))
                    {
                        _currentChatMessages.Add(msg);
                        _messagePanel.Controls.Add(MakeMessageBubble(chat, msg, null));
                        ScrollMessagesToBottom();
                    }
                    UpdateChatListForMessage(chat.PeerId, msg, true);
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Send", "Couldn't send: " + ex.Message, "OK");
            }
        }

        private void InsertEmoji(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return;
            int caret = _messageInput.SelectionStart;
            string t = _messageInput.Text ?? "";
            if (caret < 0 || caret > t.Length) caret = t.Length;
            _messageInput.Text = t.Insert(caret, emoji);
            _messageInput.SelectionStart = caret + emoji.Length;
            _messageInput.Focus();
        }

        // ── Voice recording ──────────────────────────────────────────────────────

        /// <summary>Mic-button click: start → (record) → stop-to-ready → (ready) → discard.</summary>
        private void OnMicClick()
        {
            switch (_voiceState)
            {
                case VoiceState.None: StartRecording(); break;
                case VoiceState.Recording: StopToReady(); break;
                case VoiceState.Ready: DiscardVoice(); break;
            }
        }

        private void StartRecording()
        {
            if (_selectedChat == null || _service.Client == null) return;
            _recorder = new VoiceRecorder();
            _recorder.Finished += OnVoiceFinished;
            try
            {
                _recorder.Start();
            }
            catch (Exception ex)
            {
                _recorder = null;
                ThemedDialog.Show(this, "Microphone", "Couldn't start recording:\n" + ex.Message, "OK");
                return;
            }
            _voiceState = VoiceState.Recording;
            _recordingBar.DotColor = ThemeHelper.Palette.RecordingDot;
            _recordingBar.Caption = "Recording…   0:00";
            ApplyVoiceUi();
            _recordTimer.Start();
        }

        /// <summary>Stops recording and moves to the Ready state — does NOT send (Send button does).</summary>
        private void StopToReady()
        {
            _recordTimer.Stop();
            _recorder?.Stop();   // Finished fires (bg) → OnVoiceFinished sets Ready (or None if too short)
        }

        private void DiscardVoice()
        {
            TryDeleteFile(_pendingVoicePath);
            _pendingVoicePath = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
        }

        /// <summary>Aborts any in-progress recording/ready clip (chat switch, close).</summary>
        private void AbortVoice()
        {
            _recordTimer.Stop();
            if (_recorder != null && _recorder.IsRecording) _recorder.Cancel();
            TryDeleteFile(_pendingVoicePath);
            _pendingVoicePath = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
        }

        private void OnVoiceFinished(string path, int durationSec, byte[] waveform)
        {
            _recorder = null;
            if (IsDisposed) { TryDeleteFile(path); return; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (string.IsNullOrEmpty(path)) { _voiceState = VoiceState.None; ApplyVoiceUi(); return; }
                    _pendingVoicePath = path;
                    _pendingVoiceDur = durationSec;
                    _pendingVoiceWave = waveform;
                    _voiceState = VoiceState.Ready;
                    _recordingBar.DotColor = _accent;
                    _recordingBar.Caption = "Voice  " + FormatDuration(durationSec) + "   — tap Send";
                    ApplyVoiceUi();
                }));
            }
            catch { TryDeleteFile(path); }
        }

        /// <summary>Reflects the current voice state across the input-row controls.</summary>
        private void ApplyVoiceUi()
        {
            bool none = _voiceState == VoiceState.None;
            if (_recordingBar != null) _recordingBar.Visible = !none;
            if (_messageInput != null) _messageInput.Visible = none;
            if (_attachButton != null) { _attachButton.Enabled = none && _selectedChat != null; _attachButton.Invalidate(); }
            if (_micButton != null)
            {
                _micButton.Mode = _voiceState == VoiceState.Recording ? MicMode.Stop
                                : _voiceState == VoiceState.Ready ? MicMode.Discard
                                : MicMode.Mic;
                _micButton.Invalidate();
            }
            // Send is usable except while actively recording (nothing finalized yet).
            if (_sendButton != null) _sendButton.Enabled = _voiceState != VoiceState.Recording && _selectedChat != null;
        }

        /// <summary>Send button while a recorded clip is ready → sends it as a voice note.</summary>
        private void SendPendingVoice()
        {
            if (_voiceState != VoiceState.Ready || string.IsNullOrEmpty(_pendingVoicePath)) return;
            string path = _pendingVoicePath;
            int dur = _pendingVoiceDur;
            byte[] wave = _pendingVoiceWave;
            _pendingVoicePath = null;
            _pendingVoiceWave = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
            _ = SendVoice(path, dur, wave);
        }

        private async System.Threading.Tasks.Task SendVoice(string path, int durationSec, byte[] waveform)
        {
            if (string.IsNullOrEmpty(path)) return;          // cancelled / too short
            if (_selectedChat == null || _service.Client == null) { TryDeleteFile(path); return; }

            var chat = _selectedChat;
            try
            {
                var msg = await MediaSender.SendVoiceAsync(_service.Client, chat.Peer, path, durationSec, waveform,
                    System.Threading.CancellationToken.None);
                if (msg != null)
                {
                    // Pre-seed the audio cache so the bubble can play instantly (no re-download).
                    try
                    {
                        var d = (msg.media as MessageMediaDocument)?.document as Document;
                        if (d != null)
                        {
                            MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                            File.Copy(path, AudioCachePath(d, false, null), true);
                        }
                    }
                    catch { }

                    if (_selectedChat == chat && _shownMessageIds.Add(msg.ID))
                    {
                        _currentChatMessages.Add(msg);
                        _messagePanel.Controls.Add(MakeMessageBubble(chat, msg, null));
                        ScrollMessagesToBottom();
                    }
                    UpdateChatListForMessage(chat.PeerId, msg, true);
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Voice message", "Couldn't send: " + ex.Message, "OK");
            }
            finally { TryDeleteFile(path); }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── Reply composer ───────────────────────────────────────────────────────

        private void StartReply(Message msg)
        {
            _editTarget = null;       // reply and edit are mutually exclusive
            _replyTarget = msg;
            UpdateReplyStrip();
            _messageInput.Focus();
        }

        /// <summary>Begins editing my message: fill the input with its text, show the edit strip.</summary>
        private void StartEdit(Message msg)
        {
            _replyTarget = null;
            _editTarget = msg;
            _messageInput.Text = msg.message ?? "";
            UpdateReplyStrip();
            _messageInput.Focus();
            try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
        }

        private void CancelReply()
        {
            _replyTarget = null;
            _editTarget = null;
            UpdateReplyStrip();
        }

        /// <summary>Shows/hides + (re)themes the reply/edit strip above the input.</summary>
        private void UpdateReplyStrip()
        {
            if (_replyStrip == null) return;
            bool active = _replyTarget != null || _editTarget != null;

            // Toggling the strip's row resizes the message panel, which resets AutoScroll;
            // preserve the offset like UpdateMiniBar does.
            int scrollY = _messagePanel != null ? -_messagePanel.AutoScrollPosition.Y : 0;

            if (active)
            {
                if (_editTarget != null)
                {
                    _replyEditing = true;
                    _replyPreview = "Editing message";
                }
                else
                {
                    string preview = GetDisplayText(_replyTarget);
                    if (preview.Length > 90) preview = preview.Substring(0, 90) + "…";
                    _replyEditing = false;
                    _replyPreview = preview;
                }

                _replyStrip.BackColor = ThemeHelper.PaletteFor(_dark).ReplyStripBg;
                _replyStrip.Invalidate();   // owner-drawn (DrawReplyStrip) — repaint with the new glyph/preview
                _replyCancelBtn.BackColor = _replyStrip.BackColor;
                _replyCancelBtn.ForeColor = ThemeHelper.PaletteFor(_dark).ReplyCancelFg;
                _replyCancelBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            }

            _replyStrip.Visible = active;
            SetRowPx(_rightLayout, 6, active ? 40 : 0);   // FORUM-TOPICS: reply strip row 5→6 (step 3.4: logical px)
            if (_messagePanel != null)
                _messagePanel.AutoScrollPosition = new Point(0, scrollY);
        }

        // ── Attachments ─────────────────────────────────────────────────────────
        // Pending-bubble visual kind (chosen from the SendMediaDialog mode).

        private enum AttachKind { Image, Video, Audio, Document }

        private void OpenAttachmentDialog()
        {
            if (_selectedChat == null) return;
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Attach files", Filter = "All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                // Same real send path as drag-and-drop: SendMediaDialog (compress / file).
                OnFilesDropped(dlg.FileNames);
            }
        }

        /// <summary>
        /// Creates an optimistic, PENDING outgoing bubble for a local file — mirroring the
        /// text-send path (add bubble, mark sending, reserve a temp id in _shownMessageIds).
        /// The drag-drop send path swaps these via _pendingBubbles on the server-confirmed id.
        /// Returns the bubble so callers can track it for the swap.
        /// </summary>
        private MessageBubbleControl AddPendingBubble(string path, AttachKind kind, string caption)
        {
            string name = Path.GetFileName(path);
            long size = SafeFileSize(path);
            // The caption is rendered like a real media caption (under the thumbnail / as
            // bubble text), so the optimistic bubble matches the post-reload appearance.
            // No caption → empty: the thumbnail/tile/card already conveys the media type.
            string cap = caption ?? "";

            int tempId = _nextTempMessageId--;          // -1, -2, -3, …
            _shownMessageIds.Add(tempId);               // dedupe path treats it uniformly

            MessageBubbleControl bubble;
            switch (kind)
            {
                case AttachKind.Image:
                {
                    var thumb = LoadDownscaledThumb(path, 480, 480, out int sw, out int sh);
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    if (thumb != null)
                    {
                        _attachmentThumbs.Add(thumb); // we own these (the photo cache doesn't)
                        bubble.ConfigurePhoto(sw, sh, MessageBubbleControl.PhotoState.Loaded);
                        bubble.SetImage(thumb);
                    }
                    else
                    {
                        // Unreadable image → fall back to a file card.
                        bubble.IsFile = true; bubble.FileName = name;
                        bubble.FileSizeText = DrawHelper.FormatSize(size); bubble.Measure();
                    }
                    break;
                }

                case AttachKind.Video:
                    // Generic video tile (gray area + play overlay). No VLC / frame extraction.
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    bubble.IsVideoThumb = true;
                    bubble.ConfigurePhoto(320, 200, MessageBubbleControl.PhotoState.Placeholder);
                    break;

                case AttachKind.Audio:
                default: // Audio + Document → file card (name + size; ext-colored GDI+ icon).
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    bubble.IsFile = true;
                    bubble.FileName = name;
                    bubble.FileSizeText = DrawHelper.FormatSize(size);
                    bubble.Measure();
                    break;
            }

            bubble.Pending = true;
            _messagePanel.Controls.Add(bubble);
            _pendingBubbles[tempId] = bubble;
            return bubble;
        }

        // ── Drag-and-drop send (Phase 4 Part 2) ─────────────────────────────────

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = (_selectedChat != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            if (_selectedChat == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                OnFilesDropped(paths);
        }

        /// <summary>BATCH-TA-36 — Ctrl+V in the composer with a FILE or an IMAGE on the clipboard opens the
        /// same upload dialog a drag-and-drop does, so the user still chooses whether and how to send.
        /// Returns false for anything else (text, nothing) so ordinary paste is untouched.
        ///
        /// ⚠ IT REUSES OnFilesDropped RATHER THAN SENDING ANYTHING ITSELF. That path already owns the
        ///   optimistic bubbles, the album grouping, the progress tokens and the failure handling; a second
        ///   send path would be a second set of those bugs. A pasted bitmap is written to a temp PNG first,
        ///   because the dialog works in file paths — CopyFromScreen-style clipboard images have no path.
        /// ⚠ Clipboard access is wrapped: it is shared machine state and another app holding it open makes
        ///   these calls throw, which must never take the composer down.</summary>
        private bool TryPasteAttachment()
        {
            if (_selectedChat == null || _service?.Client == null) return false;
            try
            {
                // 1. Real files copied in Explorer — the common case, and the cheapest to detect.
                if (Clipboard.ContainsFileDropList())
                {
                    var list = Clipboard.GetFileDropList();
                    var paths = new List<string>();
                    foreach (string p in list)
                        if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) paths.Add(p);
                    if (paths.Count > 0) { OnFilesDropped(paths.ToArray()); return true; }
                    return false;   // a folder-only drop list: nothing we can upload
                }

                // 2. A bitmap (screenshot, "copy image" from a browser). Materialise it as a PNG so the
                //    dialog has a path to work with, in the app's own temp area.
                if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    {
                        if (img == null) return false;
                        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TelegArm-paste");
                        System.IO.Directory.CreateDirectory(dir);
                        string file = System.IO.Path.Combine(dir,
                            "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                        img.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        OnFilesDropped(new[] { file });
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PASTE] attachment paste failed: " + ex.Message);
            }
            return false;   // text or nothing → let the TextBox paste normally
        }

        private void OnFilesDropped(string[] paths)
        {
            if (_selectedChat == null || _service.Client == null || paths == null || paths.Length == 0)
                return;

            var dlg = new SendMediaDialog(_service.Client, _selectedChat.Peer, paths);
            var byToken = new Dictionary<int, MessageBubbleControl>();
            var infoByToken = new Dictionary<int, (string path, SendMode mode, string caption)>();
            var albumPendingByToken = new Dictionary<int, MessageBubbleControl>();

            // Optimistic bubble per file (negative tempId), swapped to the real id on success.
            dlg.FileStarting += (token, path, mode, caption) =>
            {
                var kind = mode == SendMode.Photo ? AttachKind.Image
                         : mode == SendMode.Video ? AttachKind.Video
                         : AttachKind.Document;
                var bubble = AddPendingBubble(path, kind, caption);
                byToken[token] = bubble;
                infoByToken[token] = (path, mode, caption);
                ScrollMessagesToBottom();
            };
            dlg.FileSucceeded += (token, msg) =>
            {
                if (!byToken.TryGetValue(token, out var b)) return;
                // If the live echo already reconciled this bubble (TrySwapPendingBubble),
                // its id is now the real (positive) one — nothing left to do.
                if (b.MessageId >= 0) return;

                int tempId = b.MessageId;                 // negative placeholder id
                _pendingBubbles.Remove(tempId);
                _shownMessageIds.Remove(tempId);
                if (msg != null)
                {
                    _shownMessageIds.Add(msg.ID);         // dedupe the (possibly later) echo
                    b.MessageId = msg.ID;
                    if (!_currentChatMessages.Any(x => x.ID == msg.ID))
                        _currentChatMessages.Add(msg);    // so the viewer/playlist see it pre-reload
                    if (_selectedChat != null)
                        UpdateChatListForMessage(_selectedChat.PeerId, msg, true); // refresh the row now
                }
                else b.MessageId = 0;
                b.Pending = false;
                b.Invalidate();
            };
            dlg.FileFailed += (token, err) =>
            {
                if (!byToken.TryGetValue(token, out var b)) return;
                _pendingBubbles.Remove(b.MessageId);
                b.Pending = false;
                b.Failed = true;
                b.Invalidate();
                if (infoByToken.TryGetValue(token, out var info))
                    _failedSends[b] = info;   // remember the source so Retry can re-upload
            };

            // Album send: one pending album bubble while uploading; resolved to the real album on send.
            dlg.AlbumSending += (token0, apaths, caption) =>
            {
                var bubble = CreateBubble(caption ?? "", null, true, DateTime.UtcNow, 0);
                bubble.Pending = true;
                bubble.BeginAlbum(false);
                for (int i = 1; i < apaths.Count; i++) bubble.AddAlbumItem(-(1000000 + token0 * 100 + i), false);   // gray placeholder tiles
                _messagePanel.Controls.Add(bubble);
                albumPendingByToken[token0] = bubble;
                ScrollMessagesToBottom();
            };
            dlg.AlbumSent += (token0, msgs) =>
            {
                MessageBubbleControl pend;
                if (albumPendingByToken.TryGetValue(token0, out pend) && pend != null && !pend.IsDisposed)
                { _messagePanel.Controls.Remove(pend); pend.Dispose(); }
                albumPendingByToken.Remove(token0);
                if (msgs == null || _selectedChat == null) return;
                foreach (var msg in msgs)
                {
                    MessageBubbleControl created;
                    HandleAlbumItem(_selectedChat, msg, null, null, out created);   // builds/merges the real album bubble
                    if (created != null) _messagePanel.Controls.Add(created);
                    if (!_currentChatMessages.Any(x => x.ID == msg.ID)) _currentChatMessages.Add(msg);
                }
                if (msgs.Length > 0) UpdateChatListForMessage(_selectedChat.PeerId, msgs[msgs.Length - 1], true);
                ScrollMessagesToBottom();
            };
            dlg.AlbumFailed += (token0, err) =>
            {
                MessageBubbleControl pend;
                if (albumPendingByToken.TryGetValue(token0, out pend) && pend != null && !pend.IsDisposed)
                { pend.Pending = false; pend.Failed = true; pend.Invalidate(); }
            };

            using (dlg) dlg.ShowDialog(this);
        }

        private static long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        /// <summary>
        /// Loads a downscaled thumbnail from a local image file. Only the small bitmap is
        /// retained — the full-resolution source is decoded transiently and disposed
        /// immediately (no full-res image kept in memory; ARM32-safe). Returns null on failure.
        /// </summary>
        private static Image LoadDownscaledThumb(string path, int maxW, int maxH, out int srcW, out int srcH)
        {
            srcW = srcH = 0;
            try
            {
                using (var src = Image.FromFile(path))
                {
                    srcW = src.Width; srcH = src.Height;
                    double scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
                    if (scale > 1) scale = 1; // never upscale the source
                    int w = Math.Max(1, (int)(src.Width * scale));
                    int h = Math.Max(1, (int)(src.Height * scale));
                    var thumb = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(thumb))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                    return thumb;
                }
            }
            catch { return null; }
        }

        private static IPeerInfo SenderInfo(Messages_MessagesBase history, Message m)
        {
            return m.from_id != null ? history.UserOrChat(m.from_id) : null;
        }

    }
}
