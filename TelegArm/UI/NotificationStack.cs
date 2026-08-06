using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>BATCH-TA-27 — WHERE NOTIFICATIONS GO, AND WHETHER THEY GO AT ALL.
    ///
    /// Owns three things <see cref="NotificationWindow"/> deliberately does not: WHICH chat a window
    /// belongs to (W2), WHERE it sits (W4), and whether the user is in a state that should not be
    /// interrupted (W3). Keeping them here means the window is only ever a rendering of one notification,
    /// and the rules that span notifications live in one place.</summary>
    internal static class NotificationStack
    {
        // ── W3 — do not interrupt a fullscreen app ───────────────────────────────────────────────
        // shell32, Vista onward, so the RT 8.1 device is covered by the same call as Windows 11 — one
        // P/Invoke instead of a version table. MEASURED on the dev box: hr=0x0, state=5.
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        private const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
        private static readonly string[] StateNames =
        {
            "?", "NOT_PRESENT", "BUSY", "RUNNING_D3D_FULL_SCREEN",
            "PRESENTATION_MODE", "ACCEPTS_NOTIFICATIONS", "QUIET_TIME", "APP"
        };

        /// <summary>May we interrupt right now?
        ///
        /// ⚠ HONESTLY: THIS IS NOT FOCUS ASSIST. Modern Focus Assist / Do Not Disturb does NOT map cleanly
        /// onto this call — QUNS_QUIET_TIME is the old Windows 7 first-hour-after-login notion, and a
        /// Windows 10/11 Focus Assist session will frequently still report ACCEPTS_NOTIFICATIONS. What
        /// this DOES cover is the fullscreen and presentation cases (a game, a D3D exclusive app, a
        /// projector), which is most of the value and the part the recon listed as the real cost of
        /// building our own windows. Do not describe this as Focus Assist support.
        ///
        /// ⚠ IT FAILS **OPEN** — the OPPOSITE of the mute gate in TA-26, and for the same underlying rule:
        /// FAIL TOWARD THE SMALLER HARM. There, an unanswerable peer means SILENT, because notifying a
        /// chat the user explicitly muted is the app ignoring an instruction. Here, an unanswerable call
        /// means SHOW, because the alternative is one missing shell32 export silently disabling every
        /// notification on a device we cannot iterate on. Ignoring an explicit mute is worse than one
        /// extra notification; killing all notifications is worse than one appearing over a game.</summary>
        private static bool AcceptsNotifications(out string why)
        {
            why = null;
            try
            {
                int st;
                int hr = SHQueryUserNotificationState(out st);
                if (hr != 0) { why = "hr=0x" + hr.ToString("X"); return true; }        // fail OPEN
                if (st == QUNS_ACCEPTS_NOTIFICATIONS) return true;
                why = st >= 0 && st < StateNames.Length ? StateNames[st] : st.ToString();
                return false;
            }
            catch (Exception ex) { why = ex.GetType().Name; return true; }             // fail OPEN
        }

        // ── W2 — one window per chat, keyed on (account, peer) ───────────────────────────────────
        // ⚠ THE ACCOUNT IS PART OF THE KEY, not decoration. Two accounts can be in the SAME group, and
        //   TA-26's de-dup already notifies both on purpose. Keying on peer alone would collapse those
        //   into one window and lose whichever account clicked through.
        private static readonly Dictionary<(long, long), NotificationWindow> _byChat
            = new Dictionary<(long, long), NotificationWindow>();
        /// <summary>Creation order — index 0 is the OLDEST and sits at the bottom. See Relayout.</summary>
        private static readonly List<NotificationWindow> _live = new List<NotificationWindow>();

        private static Form _anchor;
        private static bool _hooked;

        /// <summary>Raised when a notification is CLICKED: (accountId, peerId, messageId). MainForm
        /// switches accounts if needed and opens the chat.</summary>
        public static event Action<long, long, int> Clicked;

        private const int EdgeGap = 12, StackGap = 8;

        public static void Show(Form anchor, NotifyInfo info, bool dark, Color accent)
        {
            if (info == null) return;
            if (anchor != null && !anchor.IsDisposed) _anchor = anchor;
            HookDisplayChanges();

            string why;
            if (!AcceptsNotifications(out why))
            {
                // Logged, not silent: "why did nothing appear?" must be answerable from a field log (R7).
                Logger.Diag("[NOTIFY-WIN] suppressed acct=" + info.AccountId + " peer=" + info.PeerId
                            + " msg=" + info.MessageId + " reason=user-busy state=" + why);
                return;
            }

            var key = (info.AccountId, info.PeerId);
            NotificationWindow w;
            if (_byChat.TryGetValue(key, out w) && w != null && !w.IsDisposed)
            {
                w.Update(info);                       // W2 — in place; no second window, no new slot
                Logger.Diag("[NOTIFY-WIN] update acct=" + info.AccountId + " peer=" + info.PeerId
                            + " msg=" + info.MessageId);
                Relayout();
                return;
            }

            w = new NotificationWindow(info, dark, accent);
            w.Clicked += OnClicked;
            w.Dismissed += OnDismissed;
            _byChat[key] = w;
            _live.Add(w);
            w.ShowNoActivate(SlotFor(_live.Count - 1, w.Size));
            Logger.Diag("[NOTIFY-WIN] show acct=" + info.AccountId + " peer=" + info.PeerId
                        + " msg=" + info.MessageId + " live=" + _live.Count + " at=" + w.Location);
            Relayout();
        }

        /// <summary>W4 — bottom-right of the WORKING AREA (not the screen bounds) of the monitor holding
        /// the main window, stacking upward.
        ///
        /// ⚠ WorkingArea, NOT Bounds — Bounds includes the taskbar band, so a notification anchored to it
        /// would sit UNDER the taskbar wherever the taskbar is. WorkingArea also tracks the taskbar being
        /// moved to another edge or set to auto-hide, with no special case for either. Measured on the dev
        /// box: bounds 1536x864, working area 1536x816, i.e. a 48px band this must stay clear of.
        /// ⚠ Read FRESH on every show and every relayout, so moving the main window to another monitor —
        /// or a resolution change — re-anchors the whole stack rather than leaving it on the old screen.</summary>
        private static Point SlotFor(int index, Size size)
        {
            Rectangle wa = WorkArea();
            int x = wa.Right - size.Width - EdgeGap;
            int y = wa.Bottom - size.Height - EdgeGap - index * (size.Height + StackGap);
            // Never climb off the top of the working area — on a short screen the stack simply stops
            // growing upward and the newest windows overlap the oldest rather than escaping the monitor.
            if (y < wa.Top + EdgeGap) y = wa.Top + EdgeGap;
            return new Point(x, y);
        }

        private static Rectangle WorkArea()
        {
            try
            {
                Screen s = _anchor != null && !_anchor.IsDisposed && _anchor.IsHandleCreated
                    ? Screen.FromControl(_anchor)
                    : Screen.PrimaryScreen;
                return s.WorkingArea;
            }
            catch { return Screen.PrimaryScreen.WorkingArea; }
        }

        /// <summary>Re-anchors every live window. ⚠ OLDEST STAYS AT THE BOTTOM and new ones appear ABOVE
        /// it, rather than the newest taking the bottom slot and shoving everything up. Both are defensible;
        /// this one moves N-1 fewer windows per notification, which matters on a Tegra 3 where every move
        /// is a real repaint.</summary>
        private static void Relayout()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var w = _live[i];
                if (w == null || w.IsDisposed) { _live.RemoveAt(i); continue; }
            }
            for (int i = 0; i < _live.Count; i++)
            {
                try { _live[i].MoveTo(SlotFor(i, _live[i].Size)); } catch { }
            }
        }

        private static void OnClicked(NotificationWindow w)
        {
            if (w == null) return;
            var h = Clicked;
            // ⚠ The window's OWN account/peer/message, never a shared "last notified" slot — that slot is
            //   correct only for a channel that can show one thing at a time (the balloon), and wrong the
            //   moment a second chat has a window of its own.
            if (h != null) h(w.AccountId, w.PeerId, w.MessageId);
        }

        private static void OnDismissed(NotificationWindow w)
        {
            if (w == null) return;
            _live.Remove(w);
            var key = (w.AccountId, w.PeerId);
            NotificationWindow cur;
            if (_byChat.TryGetValue(key, out cur) && ReferenceEquals(cur, w)) _byChat.Remove(key);
            Relayout();
        }

        /// <summary>Every live window closed — logout, account switch, or shutdown. A notification for an
        /// account you just left is worse than no notification: clicking it would switch you back.</summary>
        public static void CloseAll()
        {
            var copy = _live.ToArray();
            foreach (var w in copy) { try { w.Dismiss(); } catch { } }
            _live.Clear();
            _byChat.Clear();
        }

        /// <summary>Closes the windows belonging to one account (used when that account goes away).</summary>
        public static void CloseForAccount(long accountId)
        {
            var copy = _live.ToArray();
            foreach (var w in copy)
                if (w != null && w.AccountId == accountId) { try { w.Dismiss(); } catch { } }
        }

        /// <summary>W4 — a monitor change, a resolution change or a taskbar edge move while windows are up
        /// must re-anchor them, or the stack is left addressing a screen layout that no longer exists.</summary>
        private static void HookDisplayChanges()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => { try { Relayout(); } catch { } };
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    // Taskbar size/position lives under the "General"/"Desktop" preferences; relayout is
                    // cheap and idempotent, so it is not worth filtering precisely.
                    try { Relayout(); } catch { }
                };
            }
            catch { /* SystemEvents unavailable → placement is simply not live-updated */ }
        }
    }
}
