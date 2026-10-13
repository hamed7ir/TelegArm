using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.4 — the dialogs' keyboard tests, run by <c>TelegArm.exe --gallery-probe</c> on REAL modal
    /// windows. Keys are POSTED as WM_KEYDOWN, so they take WinForms' ordinary path (PreProcessMessage → ProcessCmdKey →
    /// ProcessDialogKey → AcceptButton / CancelButton), exactly like a keystroke. A guard timer aborts a dialog that
    /// ignored its key, so a failure is reported instead of hanging the probe.</summary>
    internal static class DialogProbe
    {
        private const int GuardMs = 4000;

        internal static void Run(IWin32Window owner, Action<bool, string, string> check, Action<string> info)
        {
            // ConfirmDialog: Enter = Yes, Esc = No, ✕ = not Yes.
            Expect(check, "confirm: Enter answers Yes", DialogResult.Yes, ShowNew(owner, () => Confirm(false), d => Key(d, Keys.Enter)));
            Expect(check, "confirm: Esc answers No", DialogResult.No, ShowNew(owner, () => Confirm(false), d => Key(d, Keys.Escape)));
            var closed = ShowNew(owner, () => Confirm(false), d => d.Close());
            check(closed != DialogResult.Yes && closed != DialogResult.Abort, "confirm: closing it (the ✕ path) is not Yes", closed.ToString());

            // A danger question: Enter must NOT destroy anything.
            Expect(check, "confirm (danger): Enter answers No", DialogResult.No, ShowNew(owner, () => Confirm(true), d => Key(d, Keys.Enter)));
            Expect(check, "confirm (danger): Esc answers No", DialogResult.No, ShowNew(owner, () => Confirm(true), d => Key(d, Keys.Escape)));

            // Info: one button; Enter and Esc both close it.
            Expect(check, "info: Enter closes", DialogResult.Yes, ShowNew(owner, () => ConfirmDialog.Create("Info", "Saved.", "OK", null, false), d => Key(d, Keys.Enter)));
            Expect(check, "info: Esc closes", DialogResult.Yes, ShowNew(owner, () => ConfirmDialog.Create("Info", "Saved.", "OK", null, false), d => Key(d, Keys.Escape)));

            // InputDialog: Enter = OK with the trimmed text, Esc = Cancel, blank = null.
            using (var d = InputDialog.Create("Rename", "New name", "old"))
            {
                var r = Show(owner, d, x => { d.Field.Text = "  new name  "; Key(x, Keys.Enter); });
                check(r == DialogResult.OK && d.Value == "new name", "input: Enter = OK, value trimmed", r + " \"" + d.Value + "\"");
            }
            using (var d = InputDialog.Create("Rename", "New name", "old"))
                Expect(check, "input: Esc = Cancel", DialogResult.Cancel, Show(owner, d, x => Key(x, Keys.Escape)));
            using (var d = InputDialog.Create("Rename", "New name", "old"))
            {
                var r = Show(owner, d, x => { d.Field.Text = "   "; Key(x, Keys.Enter); });
                check(r == DialogResult.OK && d.Value == null, "input: a blank answer is null", r + " " + (d.Value ?? "null"));
            }
            using (var d = InputDialog.Create("Rename", "New name", "old"))
            {
                // Enter typed INTO the focused field still reaches AcceptButton (a single-line box does not eat it).
                var r = Show(owner, d, x => { d.Field.Focus(); d.Field.Text = "typed"; Key(x, Keys.Enter); });
                check(r == DialogResult.OK && d.Value == "typed", "input: Enter in the focused field = OK", r + " " + d.ActiveControl);
            }

            // CloseChoiceDialog: Enter = minimise (the safe default), Esc = neither, Space toggles "Remember".
            using (var d = new CloseChoiceDialog())
            {
                var r = Show(owner, d, x => Key(x, Keys.Enter));
                check(r == DialogResult.OK && d.Choice == CloseAction.MinimizeToTray && !d.Remember, "close choice: Enter = minimise to tray, not remembered", r + " " + d.Choice + " " + d.Remember);
            }
            using (var d = new CloseChoiceDialog())
                Expect(check, "close choice: Esc = neither (Cancel)", DialogResult.Cancel, Show(owner, d, x => Key(x, Keys.Escape)));
            using (var d = new CloseChoiceDialog())
            {
                bool toggled = false; string tabbed = "";
                var r = Show(owner, d, x =>
                {
                    // Timers, not nested BeginInvoke: a BeginInvoke callback can run before a keystroke posted ahead
                    // of it has been processed (measured: the first version of this case read the state too early).
                    var check1 = FindCheckRow(x);
                    check1.Focus();
                    Key(x, Keys.Space);
                    After(250, () =>
                    {
                        toggled = check1.Checked;
                        var before = x.ActiveControl;
                        Key(x, Keys.Tab);
                        After(250, () => { tabbed = (before == null ? "null" : before.Text) + " -> " + (x.ActiveControl == null ? "null" : x.ActiveControl.Text); Key(x, Keys.Escape); });
                    });
                });
                check(toggled && d.Remember, "close choice: Space toggles Remember", "remember=" + d.Remember);
                check(tabbed.StartsWith("Remember my choice -> ") && !tabbed.EndsWith("-> Remember my choice"), "close choice: Tab moves focus on", tabbed);
            }

            // The touch-keyboard shift (fed by KeyboardInset as MainForm's Part B would): up above the keyboard, then back
            // to the EXACT bounds.
            using (var d = InputDialog.Create("Keyboard", "Type here", ""))
            {
                Rectangle before = Rectangle.Empty, shifted = Rectangle.Empty, after = Rectangle.Empty, kb = Rectangle.Empty;
                var r = Show(owner, d, x =>
                {
                    d.Field.Focus();
                    before = x.Bounds;
                    var screen = Screen.FromControl(x).Bounds;
                    kb = Rectangle.FromLTRB(screen.Left, before.Bottom - before.Height / 2, screen.Right, screen.Bottom);
                    KeyboardInset.Publish(kb);
                    shifted = x.Bounds;
                    KeyboardInset.Publish(Rectangle.Empty);
                    after = x.Bounds;
                    Key(x, Keys.Escape);
                });
                check(r == DialogResult.Cancel, "keyboard: the dialog still closed normally", r.ToString());
                check(shifted.Bottom <= kb.Top && shifted.Width == before.Width, "keyboard: the dialog moves above the keyboard", before + " -> " + shifted + " (keyboard top " + kb.Top + ")");
                check(after == before, "keyboard: the dialog returns to the exact bounds", after + " vs " + before);
                check(!KeyboardInset.Listening, "keyboard: a closed dialog stops listening (Part B goes back to idle)", "listening=" + KeyboardInset.Listening);
            }

            ShowNew(owner, () => Confirm(false), x => { info("dialog at DPI " + ((AppForm)x).WindowDpiValue + ": confirm " + x.Size); Key(x, Keys.Escape); });

            // Step 2.1: the ThemedDialog facade (157 callers) on DialogForm keeps 1.x's contract.
            Expect(check, "ThemedDialog: Esc = -1", -1, LegacyResult(owner, x => Key(x, Keys.Escape)));
            Expect(check, "ThemedDialog: Enter = the LAST button (1.x's initial focus: Cancel, not Delete)", 1, LegacyResult(owner, x => Key(x, Keys.Enter)));
            Expect(check, "ThemedDialog: the leftmost button is index 0", 0, LegacyResult(owner, x =>
            {
                Control left = null;
                foreach (var b in FindAll<Controls.RoundedButton>(x)) if (left == null || b.Left < left.Left) left = b;
                ((Button)left).PerformClick();
            }));
            Expect(check, "ThemedDialog: closing it (✕) = -1", -1, LegacyResult(owner, x => x.Close()));
            string copied = null;
            var saved = ThemedDialog.CopyToClipboard;
            ThemedDialog.CopyToClipboard = t => { copied = t; return true; };
            try { LegacyResult(owner, x => { PostCtrl(x, Keys.C); x.BeginInvoke((Action)(() => Key(x, Keys.Escape))); }); }
            finally { ThemedDialog.CopyToClipboard = saved; }
            check(copied == LegacyMessage, "ThemedDialog: Ctrl+C copies the WHOLE message", copied == null ? "nothing copied" : "\"" + copied + "\"");
            using (var d = NewLegacy())
            {
                var kinds = "";
                foreach (var b in FindAll<Controls.RoundedButton>(d)) kinds += b.Text + "=" + b.Kind + " ";
                check(kinds.Contains("Delete=Danger") && kinds.Contains("Cancel=Neutral"), "ThemedDialog: a destructive first action is Danger-red", kinds.Trim());
            }
        }

        private const string LegacyMessage = "Delete this chat for everyone? This can't be undone.";

        /// <summary>The facade's private dialog, built as the rig builds it (reflection), with ("Delete", "Cancel").</summary>
        private static Form NewLegacy()
        {
            var t = typeof(ThemedDialog).GetNestedType("Dialog", System.Reflection.BindingFlags.NonPublic);
            return (Form)Activator.CreateInstance(t, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { "Delete chat", LegacyMessage, new[] { "Delete", "Cancel" } }, null);
        }

        /// <summary>Shows the facade's dialog, runs <paramref name="act"/>, returns its Result (the index Show would return).</summary>
        private static int LegacyResult(IWin32Window owner, Action<Form> act)
        {
            using (var d = NewLegacy())
            {
                var r = Show(owner, d, act);
                if (r == DialogResult.Abort) return -99;   // the guard fired: the key was ignored
                return (int)d.GetType().GetField("Result").GetValue(d);
            }
        }

        private static System.Collections.Generic.List<T> FindAll<T>(Control root) where T : Control
        {
            var list = new System.Collections.Generic.List<T>();
            foreach (Control c in root.Controls) { if (c is T) list.Add((T)c); list.AddRange(FindAll<T>(c)); }
            return list;
        }

        /// <summary>Ctrl+key as a keystroke: the key's WM_KEYDOWN with the Control state set for the posted message.</summary>
        private static void PostCtrl(Form d, Keys k)
        {
            var target = d.ActiveControl != null && d.ActiveControl.IsHandleCreated ? d.ActiveControl.Handle : d.Handle;
            var state = new byte[256];
            GetKeyboardState(state);
            byte old = state[0x11];
            state[0x11] = 0x80; SetKeyboardState(state);
            try { PostMessage(target, WM_KEYDOWN, (IntPtr)(int)k, IntPtr.Zero); Application.DoEvents(); }
            finally { GetKeyboardState(state); state[0x11] = old; SetKeyboardState(state); }
        }

        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

        private static void Expect(Action<bool, string, string> check, string name, int want, int got)
        {
            check(got == want, name, "got " + got + (got == -99 ? " (the key was ignored: guard timeout)" : ""));
        }

        private static ConfirmDialog Confirm(bool danger)
        {
            return ConfirmDialog.Create(danger ? "Delete chat" : "Question",
                danger ? "Delete this chat for you? This cannot be undone." : "Apply the new settings now?",
                danger ? "Delete" : "Yes", "No", danger);
        }

        private static void Expect(Action<bool, string, string> check, string name, DialogResult want, DialogResult got)
        {
            check(got == want, name, "got " + got + (got == DialogResult.Abort ? " (the key was ignored: guard timeout)" : ""));
        }

        /// <summary>Builds a dialog, shows it with <see cref="Show"/>, disposes it.</summary>
        private static DialogResult ShowNew(IWin32Window owner, Func<Form> make, Action<Form> act)
        {
            using (var d = make()) return Show(owner, d, act);
        }

        /// <summary>Shows <paramref name="d"/> modally, runs <paramref name="act"/> once it is up, and returns the result.
        /// The caller disposes the dialog.</summary>
        private static DialogResult Show(IWin32Window owner, Form d, Action<Form> act)
        {
            var guard = new Timer { Interval = GuardMs };
            guard.Tick += (s, e) => { guard.Stop(); try { d.DialogResult = DialogResult.Abort; } catch { } };
            d.Shown += (s, e) =>
            {
                guard.Start();
                try { d.BeginInvoke((Action)(() => { try { act(d); } catch (Exception ex) { Logger.Diag("[PROBE] dialog action threw: " + ex); } })); } catch { }
            };
            try { return d.ShowDialog(owner); }
            finally { guard.Stop(); guard.Dispose(); }
        }

        private static void After(int ms, Action a)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        private static Controls.CheckRow FindCheckRow(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Controls.CheckRow r) return r;
                var inner = FindCheckRow(c);
                if (inner != null) return inner;
            }
            return null;
        }

        private static void Key(Form d, Keys k)
        {
            var target = d.ActiveControl != null && d.ActiveControl.IsHandleCreated ? d.ActiveControl.Handle : d.Handle;
            PostMessage(target, WM_KEYDOWN, (IntPtr)(int)k, IntPtr.Zero);
            PostMessage(target, WM_KEYUP, (IntPtr)(int)k, (IntPtr)unchecked((int)0xC0000001));
        }

        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
