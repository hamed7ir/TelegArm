using System;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign R5 — the main window's keyboard shortcuts (Telegram Desktop's set, where it makes sense here). Called
    /// first from <see cref="ProcessCmdKey"/>, which runs before the focused control's own key handling — so every key
    /// here is one the composer and the search box have no use for. Ctrl+K stays the composer's "add link" (plan E2),
    /// so search keeps Ctrl+F.
    /// </summary>
    public partial class MainForm
    {
        /// <summary>The list for Settings > Advanced > Keyboard shortcuts — kept beside the code that implements it.</summary>
        internal static readonly string[][] Shortcuts =
        {
            new[] { "Ctrl+F", "Search chats" },
            new[] { "Alt+Up / Alt+Down", "Previous / next chat" },
            new[] { "Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous folder" },
            new[] { "Ctrl+1 … Ctrl+9", "Folder 1 to 9 (1 = All)" },
            new[] { "Esc", "Clear the search, cancel the reply or edit, close the side panel, then close the chat" },
            new[] { "Ctrl+E", "Edit your last message in this chat" },
            new[] { "Ctrl+Shift+M", "Mute or unmute this chat" },
            new[] { "Enter / Shift+Enter", "Send / new line (Settings > Chats & Composer)" },
            new[] { "Ctrl+B / Ctrl+I / Ctrl+U / Ctrl+K", "Bold / italic / underline / link in the composer" },
            new[] { "Ctrl+Plus / Ctrl+Minus / Ctrl+0", "Interface scale up / down / 100%" },
            new[] { "Ctrl+W", "Hide to the tray" },
            new[] { "Ctrl+L", "Log out" },
        };

        /// <summary>R5: true when <paramref name="keyData"/> was a shortcut and has been handled.</summary>
        private bool TryShortcut(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Alt | Keys.Up: CycleChat(-1); return true;
                case Keys.Alt | Keys.Down: CycleChat(+1); return true;
                case Keys.Control | Keys.Tab: CycleFolder(+1); return true;
                case Keys.Control | Keys.Shift | Keys.Tab: CycleFolder(-1); return true;
                case Keys.Control | Keys.E: return EditLastOwnMessage();
                case Keys.Control | Keys.Shift | Keys.M:
                    if (_selectedChat == null) return false;
                    ToggleChatMute(_selectedChat);
                    return true;
            }
            if ((keyData & Keys.Modifiers) == Keys.Control)
            {
                var k = keyData & Keys.KeyCode;
                if (k >= Keys.D1 && k <= Keys.D9) return SelectFolderNumber(k - Keys.D1);
                if (k >= Keys.NumPad1 && k <= Keys.NumPad9) return SelectFolderNumber(k - Keys.NumPad1);
            }
            return false;
        }

        // ── chats ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Alt+Up / Alt+Down: the previous / next row of the list as it is shown (folder and search applied).</summary>
        private async void CycleChat(int dir)
        {
            var rows = _chatListPanel.Controls.OfType<ChatListItemControl>().Where(r => r.Visible).ToList();
            if (rows.Count == 0) return;
            int at = _selectedItem != null ? rows.IndexOf(_selectedItem) : -1;
            int next = at < 0 ? (dir > 0 ? 0 : rows.Count - 1) : at + dir;
            if (next < 0 || next >= rows.Count) return;
            var row = rows[next];
            try { _chatListPanel.ScrollControlIntoView(row); } catch { }
            if (LogOn) Logger.Diag("[KEYS] " + (dir > 0 ? "next" : "previous") + " chat → peer=" + row.Entry.PeerId);
            await OpenChat(row.Entry, 0);
        }

        /// <summary>Ctrl+E: edit my newest editable message in the open chat (what Up does in Telegram Desktop).</summary>
        private bool EditLastOwnMessage()
        {
            if (_selectedChat == null || !_messageInput.Enabled) return false;
            Message last = null;
            foreach (var m in _currentChatMessages)
                if (m != null && IsOut(m) && m.fwd_from == null && !(m.media is MessageMediaPoll) && (last == null || m.ID > last.ID)) last = m;
            if (last == null) return false;
            StartEdit(last);
            return true;
        }

        /// <summary>Esc, one layer at a time: the search box (handled before this), in-chat search, the reply or edit,
        /// selection, the side panel, and finally the open chat itself. False when there is nothing left to close.</summary>
        private bool EscapeOneLevel()
        {
            if (_inChatSearchEntry != null) { ExitInChatSearch(); return true; }
            if (_replyTarget != null || _editTarget != null) { CancelReplyByUser(); return true; }
            if (_selectionMode) { ExitSelectionMode(); return true; }
            if (_dock != null && _dock.Visible) { SetDockOpen(false); return true; }
            if (_selectedChat != null) { CloseOpenChat(); return true; }
            return false;
        }

        /// <summary>Back to "Select a chat": the draft is saved first, everything that belongs to the open chat is put
        /// away (as OpenChat/LoadHistoryAsync do when LEAVING a chat), and the composer goes back to its disabled start
        /// state.</summary>
        private void CloseOpenChat()
        {
            if (_selectedChat == null) return;
            if (LogOn) Logger.Diag("[KEYS] close chat peer=" + _selectedChat.PeerId);
            if (_editTarget != null) CancelReplyByUser();   // the text typed before an edit is the draft, not the edit's text
            PersistDraftForCurrentChat();
            CancelReply();
            CancelTyping();
            if (_selectionMode) ExitSelectionMode();
            if (_voiceState != VoiceState.None) AbortVoice();
            ClearThreadMode();
            if (_currentForumEntry != null)
            {
                _currentForumEntry = null; _forumTopics = null; _selectedTopicId = 0;
                RebuildTopicBar(); ShowTopicBar(false);
            }
            ResetBotUi();
            _selectedChat = null;
            if (_selectedItem != null && !_selectedItem.IsDisposed) { _selectedItem.Selected = false; _selectedItem.Invalidate(); }
            _selectedItem = null;
            _openLatchPeer = 0;   // reopening the same chat right away is a real open, not a touch echo
            _pinnedMessages = null; _pinnedChatId = 0;
            if (_pinnedBar != null) _pinnedBar.Visible = false;
            SetDockOpen(false);
            if (_chatSearchBtn != null) _chatSearchBtn.Visible = false;
            if (_chatMenuBtn != null) _chatMenuBtn.Visible = false;
            if (_callBtn != null) _callBtn.Visible = false;
            if (_dockBtn != null) _dockBtn.Visible = false;
            ClearMessagePanel();
            _currentChatMessages.Clear();
            _shownMessageIds.Clear();
            _albumBubbles.Clear();
            _chatTitle.Text = "Select a chat";
            SetHeaderAvatar(null);
            UpdateHeaderStatus();
            // the composer as it starts: shown, empty and disabled
            if (_footerBar != null) _footerBar.Visible = false;
            if (_composerBar != null) _composerBar.Visible = true;
            _messageInput.Text = "";
            _messageInput.Enabled = false;
            _sendButton.Enabled = false;
            _attachButton.Enabled = false; _attachButton.Invalidate();
            _micButton.Enabled = false; _micButton.Invalidate();
            _emojiButton.Enabled = false; _emojiButton.Invalidate();
            if (_dateFlyout != null) { _dateFlyout.Visible = false; if (_dateFlyoutTimer != null) _dateFlyoutTimer.Stop(); }   // the day pill of the old chat
            try { ActiveControl = null; } catch { }   // a read-only composer that kept the focus would still show its caret
            _messagePanel.Invalidate();   // the "Select a chat to start messaging" pill
            UpdateTrayTooltip();
            ShowColumn(true);             // one column: back to the list
        }

        // ── folders ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The folder strip's order: All, the user's folders, Archive (when anything is archived).</summary>
        private int FolderCount { get { return 1 + _folders.Count(f => f != null) + (_allChats.Any(c => c.Archived) ? 1 : 0); } }

        private int CurrentFolderIndex()
        {
            if (_showArchive) return FolderCount - 1;
            if (_activeFolder == null) return 0;
            int i = 1;
            foreach (var f in _folders) { if (f == null) continue; if (ReferenceEquals(f, _activeFolder)) return i; i++; }
            return 0;
        }

        private bool SelectFolderNumber(int index)
        {
            if (index < 0 || index >= FolderCount) return false;
            int n = 1 + _folders.Count(f => f != null);
            if (index == 0) SetActiveFolder(null);
            else if (index < n) SetActiveFolder(_folders.Where(f => f != null).ElementAt(index - 1));
            else SetArchive();
            return true;
        }

        private void CycleFolder(int dir)
        {
            int count = FolderCount;
            if (count <= 1) return;
            SelectFolderNumber(((CurrentFolderIndex() + dir) % count + count) % count);
        }
    }
}
