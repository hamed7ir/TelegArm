using System;
using System.Drawing;
using System.Linq;
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
        // ── Telegram calls (2026-10-16): tgcalls through native\tgcalls_native, the MTProto side in Core\Calls\CallManager ──
        // One manager per account service: its calls ride that account's updates. A call belongs to the active account;
        // switching accounts mid-call hangs it up (the other account's updates would no longer reach it).
        private CallManager _calls;
        private CallWindow _callWindow;
        private Button _callBtn;   // the chat header's phone (private chats with a person)

        private CallManager Calls
        {
            get
            {
                if (_service == null) return null;
                if (_calls == null || !ReferenceEquals(_calls.Service, _service))
                {
                    var old = _calls;
                    if (old != null)
                    {
                        old.Incoming -= OnIncomingCall;
                        old.Changed -= OnCallChanged;
                        if (old.Busy) { var t = old.HangUpAsync(); }
                    }
                    _calls = new CallManager(_service, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
                    _calls.Incoming += OnIncomingCall;
                    _calls.Changed += OnCallChanged;
                }
                return _calls;
            }
        }

        /// <summary>updatePhoneCall / updatePhoneCallSignalingData, from the update handler (UI thread).</summary>
        private bool TryHandleCallUpdate(Update update)
        {
            if (!(update is UpdatePhoneCall) && !(update is UpdatePhoneCallSignalingData)) return false;
            if (!CallManager.Available)
            {
                // No call library here (e.g. Windows 7): an incoming call cannot be answered, so it is left to ring on the
                // person's other devices rather than declined.
                if (update is UpdatePhoneCall up && up.phone_call is PhoneCallRequested) Logger.Diag("[CALL] incoming call ignored: " + TgCalls.Status);
                return true;
            }
            var calls = Calls;
            if (calls != null) calls.OnUpdate(update);
            return true;
        }

        private void OnIncomingCall(CallSession s)
        {
            ShowCallWindow(s);
            try { if (WindowState == FormWindowState.Minimized || !Visible || !ContainsFocus) FlashTaskbar(); } catch { }
        }

        private void OnCallChanged(CallSession s)
        {
            if (_callBtn != null) _callBtn.Invalidate();
        }

        private void ShowCallWindow(CallSession s)
        {
            if (_callWindow == null || _callWindow.IsDisposed)
            {
                _callWindow = new CallWindow(Calls, s, id => GetCachedAvatar(id), CallPeerName);
                _callWindow.StartPosition = FormStartPosition.Manual;
                var wa = Screen.FromControl(this).WorkingArea;
                _callWindow.Location = new Point(wa.Right - _callWindow.Width - Px(24), wa.Top + Px(24));
                _callWindow.Show(this);
            }
            else _callWindow.Bind(s);
            try { _callWindow.Activate(); } catch { }
        }

        private string CallPeerName(CallSession s)
        {
            var u = s.Peer;
            if (u == null)
            {
                var e = _allChats.FirstOrDefault(c => c.PeerId == s.PeerId);
                if (e != null) return e.Title ?? "Unknown";
                return "Unknown";
            }
            string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            return n.Length > 0 ? n : (u.username ?? "Deleted account");
        }

        /// <summary>The phone leftmost of the header's buttons: [phone] [🔍] [⋮] [dock]. MEASURED (live rig, 2026-10-16): the
        /// order these Dock.Right buttons end up in is the order they were FIRST SHOWN (a button's window is created when it
        /// is first made visible, and that moves it in the child order), and a higher child index sits further right. So the
        /// order is set each time the phone is shown, not once at build (a group chat opened first would otherwise leave the
        /// phone outermost right).</summary>
        private void PlaceCallButton()
        {
            if (_callBtn == null || _chatSearchBtn == null || _callBtn.Parent == null || _callBtn.Parent != _chatSearchBtn.Parent) return;
            var kids = _callBtn.Parent.Controls;
            int search = kids.GetChildIndex(_chatSearchBtn), call = kids.GetChildIndex(_callBtn);
            if (call > search) kids.SetChildIndex(_callBtn, search);
        }

        /// <summary>The header's phone: a private chat with a person (not yourself, not a bot).</summary>
        private bool CanCall(ChatEntry e)
        {
            if (e == null || !(e.Peer is InputPeerUser)) return false;
            var u = e.PeerInfo as User;
            if (u == null || u.IsBot) return false;
            var me = _service != null ? _service.Me : null;
            return me == null || u.id != me.id;
        }

        private async void StartCallWith(ChatEntry e)
        {
            var calls = Calls;
            if (calls == null || !CanCall(e)) return;
            if (calls.Busy) { ShowCallWindow(calls.Current); return; }
            if (!CallManager.Available)
            {
                ThemedDialog.Show(this, "Call", "Calls are not available on this computer.\n\n" + TgCalls.Status, "OK");
                return;
            }
            var task = calls.StartAsync((User)e.PeerInfo);
            if (calls.Current != null) ShowCallWindow(calls.Current);   // StartAsync sets Current before its first await
            await task;
        }

        /// <summary>Loads the call library off the UI thread during idle startup, as the media engine is.</summary>
        private void WarmUpCalls()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                try { ok = TgCalls.Available; } catch { }
                Logger.Diag("[CALL] warm-up " + (ok ? "ready" : "unavailable (" + TgCalls.Status + ")") + " in " + sw.ElapsedMilliseconds + "ms");
            });
        }

        /// <summary>Flashes the taskbar button until the window is brought forward (FLASHW_ALL | FLASHW_TIMERNOFG).</summary>
        private void FlashTaskbar()
        {
            var fi = new FLASHWINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = Handle, dwFlags = 3 | 12, uCount = uint.MaxValue };
            FlashWindowEx(ref fi);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO info);

        /// <summary>The handset of Google's Material Design "call" icon (Apache-2.0; THIRD-PARTY-NOTICES), filled from its own
        /// outline (24 x 24 viewBox) rather than a font glyph (tofu on RT 8.1). The outline fills about 18 of its 24 units,
        /// so the box is 4/3 of the shared glyph box: the handset comes out the size of the other header glyphs.</summary>
        private const string PhoneIconPath =
            "M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20" +
            "c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z";

        private static void DrawPhoneGlyph(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = HeaderGlyphBox(area) * 4f / 3f;
            var box = new RectangleF(area.X + (area.Width - s) / 2f, area.Y + (area.Height - s) / 2f, s, s);
            using (var path = SvgPath.Parse(PhoneIconPath, 24f, box))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }
    }
}
