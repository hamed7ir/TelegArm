using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Core.Calls;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── Voice chats (2026-10-16): tgcalls' group engine, the MTProto side in Core\Calls\GroupCall (GroupCallManager) ──
        // As the 1:1 calls: one manager per account service; switching accounts leaves a live voice chat. The chat header's
        // call button (MainForm.Calls) doubles as the voice chat button in groups and channels: shown while a voice chat is
        // on there, or for an admin who may start one. Not both at once: a 1:1 call blocks a voice chat and the reverse.
        private GroupCallManager _groupCalls;
        private GroupCallWindow _groupWindow;

        private GroupCallManager GroupCalls
        {
            get
            {
                if (_service == null) return null;
                if (_groupCalls == null || !ReferenceEquals(_groupCalls.Service, _service))
                {
                    var old = _groupCalls;
                    if (old != null)
                    {
                        old.Changed -= OnGroupCallChanged;
                        old.InfoChanged -= OnGroupCallInfoChanged;
                        if (old.Busy) { var t = old.LeaveAsync(); }
                    }
                    _groupCalls = new GroupCallManager(_service, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
                    _groupCalls.OtherBusy = () => _calls != null && _calls.Busy;
                    _groupCalls.Changed += OnGroupCallChanged;
                    _groupCalls.InfoChanged += OnGroupCallInfoChanged;
                }
                return _groupCalls;
            }
        }

        private bool InVoiceChat { get { return _groupCalls != null && _groupCalls.Busy; } }

        /// <summary>updateGroupCall / updateGroupCallParticipants / updateGroupCallConnection (UI thread).</summary>
        private bool TryHandleGroupCallUpdate(Update update)
        {
            if (!(update is UpdateGroupCall) && !(update is UpdateGroupCallParticipants) && !(update is UpdateGroupCallConnection)) return false;
            if (GroupCallManager.Available) { var gc = GroupCalls; if (gc != null) gc.OnUpdate(update); }
            return true;
        }

        private void OnGroupCallChanged(GroupCallSession s)
        {
            if (!s.Ended)
            {
                try
                {
                    if (_groupWindow == null || _groupWindow.IsDisposed) ShowGroupCallWindow(s);
                    // a new voice chat while the window still shows the last one ending: rebind it (no Activate - this runs on
                    // every change)
                    else if (!ReferenceEquals(_groupWindow.Session, s)) _groupWindow.Bind(GroupCalls, s);
                }
                catch (Exception ex)
                {
                    // never a voice chat without its window (no way to see it, mute it or leave it)
                    Logger.Diag("[GCALL] window: " + ex);
                    var gc = _groupCalls; if (gc != null && ReferenceEquals(gc.Current, s)) { var t = gc.LeaveAsync(); }
                }
            }
            OnGroupCallInfoChanged(s.PeerId);
        }

        private void OnGroupCallInfoChanged(long peerId)
        {
            var e = _selectedChat;
            if (_callBtn == null || e == null || e.PeerId != peerId) return;
            bool show = CanCall(e) || CanVoiceChat(e);
            if (_callBtn.Visible != show) { _callBtn.Visible = show; if (show) PlaceCallButton(); }
            _callBtn.Invalidate();
        }

        private void ShowGroupCallWindow(GroupCallSession s)
        {
            if (_groupWindow == null || _groupWindow.IsDisposed)
            {
                _groupWindow = new GroupCallWindow(GroupCalls, s, id => GetCachedAvatar(id));
                _groupWindow.StartPosition = FormStartPosition.Manual;
                var wa = Screen.FromControl(this).WorkingArea;
                _groupWindow.Height = Math.Min(_groupWindow.Height, wa.Height - Px(16));
                _groupWindow.Location = new Point(Math.Max(wa.Left, wa.Right - _groupWindow.Width - Px(24)),
                                                  Math.Max(wa.Top, Math.Min(wa.Top + Px(24), wa.Bottom - _groupWindow.Height)));
                _groupWindow.Show(this);
            }
            else _groupWindow.Bind(GroupCalls, s);
            try { _groupWindow.Activate(); } catch { }
        }

        /// <summary>The chat behind an entry (an entry opened from search may carry no PeerInfo).</summary>
        private ChatBase ChatOf(ChatEntry e)
        {
            var c = e.PeerInfo as ChatBase;
            if (c != null) return c;
            if (!(e.Peer is InputPeerChannel) && !(e.Peer is InputPeerChat)) return null;
            var chats = _service != null && _service.Updates != null ? _service.Updates.Chats : null;
            if (chats != null && chats.TryGetValue(e.PeerId, out c)) return c;
            return null;
        }

        /// <summary>A group or channel with a voice chat on now (<paramref name="active"/>), or where we may start one.</summary>
        private bool VoiceChatState(ChatEntry e, out bool active, out bool canManage)
        {
            active = canManage = false;
            if (e == null || !GroupCallManager.Available || _service == null) return false;
            var c = ChatOf(e);
            bool flag;
            var ch = c as Channel;
            var chat = c as Chat;
            if (ch != null)
            {
                flag = (ch.flags & Channel.Flags.call_active) != 0;
                canManage = (ch.flags & Channel.Flags.creator) != 0 || (ch.admin_rights != null && (ch.admin_rights.flags & ChatAdminRights.Flags.manage_call) != 0);
            }
            else if (chat != null)
            {
                flag = (chat.flags & Chat.Flags.call_active) != 0;
                canManage = (chat.flags & Chat.Flags.creator) != 0 || (chat.admin_rights != null && (chat.admin_rights.flags & ChatAdminRights.Flags.manage_call) != 0);
            }
            else return false;
            var gc = GroupCalls;
            active = (gc != null && gc.InfoFor(e.PeerId) != null) || (flag && (gc == null || !gc.KnownEnded(e.PeerId)))
                     || (gc != null && gc.Busy && gc.Current.PeerId == e.PeerId);
            return active || canManage;
        }

        private bool CanVoiceChat(ChatEntry e) { bool a, m; return VoiceChatState(e, out a, out m); }

        /// <summary>A group or a channel (else a private chat): picks the header button's icon and action.</summary>
        private bool IsGroupOrChannel(ChatEntry e)
        {
            return e != null && (e.PeerInfo is ChatBase || e.Peer is InputPeerChannel || e.Peer is InputPeerChat);
        }

        /// <summary>The header's call button: a call with a person, else the voice chat.</summary>
        private void OnCallButton()
        {
            var e = _selectedChat;
            if (IsGroupOrChannel(e)) JoinVoiceChat(e);
            else StartCallWith(e);
        }

        /// <summary>Joins the chat's voice chat (muted), or starts one (an admin, asked first).</summary>
        private async void JoinVoiceChat(ChatEntry e)
        {
            var gc = GroupCalls;
            if (gc == null || e == null) return;
            if (gc.Busy) { ShowGroupCallWindow(gc.Current); return; }
            if (_calls != null && _calls.Busy) { ShowCallWindow(_calls.Current); return; }
            if (!GroupCallManager.Available)
            {
                ThemedDialog.Show(this, "Voice chat", "Voice chats are not available on this computer.\n\n" + TgCalls.Status, "OK");
                return;
            }
            bool active, canManage;
            if (!VoiceChatState(e, out active, out canManage)) return;
            try
            {
                if (active) await gc.JoinAsync(e.Peer, e.PeerId, e.Title, canManage);   // the window opens on its first change
                else
                {
                    if (ThemedDialog.Show(this, "Start voice chat", "Start a voice chat in " + (e.Title ?? "this chat") + "?", "Start", "Cancel") != 0) return;
                    await gc.StartAsync(e.Peer, e.PeerId, e.Title);
                }
            }
            catch (Exception ex)
            {
                Logger.Diag("[GCALL] " + ex.Message);
                ThemedDialog.Show(this, "Voice chat", ex is InvalidOperationException ? ex.Message : "Couldn't open the voice chat: " + ex.Message, "OK");
            }
        }

        private void AddVoiceChatMenuItems(ContextMenuStrip menu, ChatEntry e)
        {
            bool active, canManage;
            if (!VoiceChatState(e, out active, out canManage)) return;
            AddMenuItem(menu, active ? "Join voice chat" : "Start voice chat", () => JoinVoiceChat(e));
        }

        /// <summary>Before the active account changes (switch, add, log out): a live voice chat is left and a 1:1 call hung
        /// up on the OUTGOING account's client while it is still connected - else that account stays in the call, its mic
        /// live, with no window to leave from (review 2026-10-16). Bounded by <paramref name="waitMs"/> (0 = do not wait:
        /// both end here at once, only their goodbyes go out in the background).</summary>
        private async System.Threading.Tasks.Task LeaveCallsForAccountChangeAsync(int waitMs)
        {
            var waits = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
            var gc = _groupCalls; if (gc != null && gc.Busy) waits.Add(gc.LeaveAsync());
            var c = _calls; if (c != null && c.Busy) waits.Add(c.HangUpAsync());
            if (waits.Count == 0) return;
            Logger.Diag("[GCALL] account change: leaving " + waits.Count + " call(s) first");
            if (waitMs > 0)
                await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.WhenAll(waits), System.Threading.Tasks.Task.Delay(waitMs));
        }

        private void ShutdownGroupCalls()
        {
            var gc = _groupCalls;
            if (gc != null) try { gc.Shutdown(3000); } catch (Exception ex) { Logger.Diag("[GCALL] shutdown: " + ex.Message); }
        }

        private static void DrawVoiceChatGlyph(Graphics g, Rectangle area, Color color)
        {
            float s = HeaderGlyphBox(area) * 1.2f;   // the outline spans about 20 of its 24 units
            GroupCallWindow.FillIcon(g, GroupCallWindow.VoiceChatPath, new RectangleF(area.X + (area.Width - s) / 2f, area.Y + (area.Height - s) / 2f, s, s), color);
        }
    }
}
