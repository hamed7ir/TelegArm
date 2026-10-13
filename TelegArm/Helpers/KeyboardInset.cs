using System;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.4 — the touch keyboard's screen rectangle, for windows other than MainForm.
    ///
    /// ⚠ THIS IS NOT A DETECTOR. MainForm's Part B poller (MainForm.Keyboard.cs, OnKbTick) is the ONE keyboard
    /// detector in the app (plan §2.2: DroidFinestra's KeyboardInset was a second poller, mirrored from Part B; we do
    /// not take it). Part B publishes here only while something listens — a DialogForm with a focused text field is
    /// the active window — so with no listener the live app's keyboard handling is exactly what it was.
    ///
    /// The rectangle is in screen pixels; Rectangle.Empty means "no keyboard" (gone, hardware keyboard attached, the
    /// "Resize for keyboard" setting off, or no text field focused). Raised on the UI thread, only on a change.</summary>
    public static class KeyboardInset
    {
        private static Action<Rectangle> _handlers;
        private static Rectangle _last;

        public static event Action<Rectangle> KeyboardRectChanged
        {
            add { _handlers += value; }
            remove
            {
                _handlers -= value;
                if (_handlers == null) _last = Rectangle.Empty;   // the next listener starts from "no keyboard"
            }
        }

        /// <summary>True while at least one window listens; Part B skips the work entirely otherwise.</summary>
        public static bool Listening { get { return _handlers != null; } }

        /// <summary>Part B's side: the keyboard rectangle as it stands now (Empty = none). Raises on a change only.</summary>
        public static void Publish(Rectangle keyboard)
        {
            // Nothing is remembered without a listener: a dialog that starts listening while the keyboard is already up
            // must still be told (SelfTest caught the first version recording the rect here and never raising it).
            var h = _handlers;
            if (h == null || keyboard == _last) return;
            _last = keyboard;
            foreach (Action<Rectangle> one in h.GetInvocationList())
            {
                try { one(keyboard); }
                catch (Exception ex) { Logger.Diag("[KBD] inset listener threw: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}
