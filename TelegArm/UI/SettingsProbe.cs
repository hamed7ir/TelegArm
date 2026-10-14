using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 2.6 — the rewritten Settings keeps 1.x's two apply models, checked on a REAL modal SettingsForm
    /// by <c>TelegArm.exe --gallery-probe</c> (isolated storage — the probe's AppSettings are a throwaway copy):
    /// an on-OK row is discarded by Esc and applied by OK; a refused cache folder mutates NOTHING and keeps the dialog
    /// open on Data &amp; Storage (BATCH-TA-34/B1); the rail pages with the keyboard.</summary>
    internal static class SettingsProbe
    {
        private const int GuardMs = 6000;

        internal static void Run(IWin32Window owner, Action<bool, string, string> check, Action<string> info)
        {
            var s = AppSettings.Instance;
            bool before = s.AutoDownloadPhotos;

            // Esc discards an on-OK row.
            var r = Show(owner, f => { Row<ToggleRow>(f, "_photos").On = !before; Key(f, Keys.Escape); });
            check(r == DialogResult.Cancel && s.AutoDownloadPhotos == before, "settings: Esc discards an on-OK switch",
                  r + " photos=" + s.AutoDownloadPhotos + " (was " + before + ")");

            // OK applies it.
            r = Show(owner, f => { Row<ToggleRow>(f, "_photos").On = !before; OkButton(f).PerformClick(); });
            check(r == DialogResult.OK && s.AutoDownloadPhotos == !before, "settings: OK applies an on-OK switch",
                  r + " photos=" + s.AutoDownloadPhotos + " (was " + before + ")");
            s.AutoDownloadPhotos = before;
            s.Save();

            // A cache folder that holds session files is refused BEFORE anything is assigned: the switch flipped in the same
            // visit stays unapplied, the folder is unchanged, the dialog stays open on Data & Storage.
            string cacheBefore = s.MediaCacheFolder;
            bool stillOpen = false, onData = false;
            string refused = "";
            r = Show(owner, f =>
            {
                Row<ToggleRow>(f, "_photos").On = !before;
                Row<TextRow>(f, "_cacheBox").Value = AccountContext.AccountsRoot;
                // The refusal is a nested modal ThemedDialog: close it from a timer, then look at the settings form.
                After(400, () =>
                {
                    // Not Form.ActiveForm: that is null whenever another app is in front (measured — the dialog was up).
                    Form nested = null;
                    foreach (Form o in Application.OpenForms) if (o != f && o.Visible && o.Modal) nested = o;   // the settings form is modal too: excluded
                    refused = nested == null ? "none" : nested.Text;
                    if (nested != null && nested != f) Key(nested, Keys.Escape);
                    After(400, () =>
                    {
                        stillOpen = f.Visible && f.DialogResult == DialogResult.None;
                        var pages = (Panel[])Field(f, "_pages");
                        onData = pages[4].Parent.Visible;
                        Key(f, Keys.Escape);
                    });
                });
                OkButton(f).PerformClick();
            });
            check(refused == "Cache folder", "settings: a cache folder over the accounts root is refused with a dialog", "nested=" + refused);
            check(stillOpen && onData, "settings: the refusal keeps Settings open on Data & Storage", "open=" + stillOpen + " onData=" + onData);
            check(s.AutoDownloadPhotos == before && s.MediaCacheFolder == cacheBefore, "settings: the refusal mutates NOTHING (B1)",
                  "photos=" + s.AutoDownloadPhotos + " cache=" + s.MediaCacheFolder);

            // The rail: Down moves to the next page and shows it.
            int sel = -1; bool shown = false;
            Show(owner, f =>
            {
                var rail = (Control)Field(f, "_rail");
                rail.Focus();
                Key(f, Keys.Down);
                After(300, () =>
                {
                    sel = (int)rail.GetType().GetProperty("Selected").GetValue(rail);
                    shown = ((Panel[])Field(f, "_pages"))[1].Parent.Visible;
                    Key(f, Keys.Escape);
                });
            });
            check(sel == 1 && shown, "settings: Down on the rail opens the next page", "selected=" + sel + " shown=" + shown);

            // Device trip: the "Typing and touch keyboard" switches write the settings InputPolicy reads (live).
            Show(owner, f =>
            {
                try
                {
                    var shim = FindRow<SegmentedRow>(f, "Typing workaround");
                    var dismiss = FindRow<ToggleRow>(f, "Keep it closed after closing it");
                    shim.SetSelected(1, true);
                    bool observed = InputPolicy.Shim == InputPolicy.ShimMode.Observe && s.InputShim == "Observe";
                    dismiss.On = false;
                    bool dismissOff = !InputPolicy.DismissGuard && s.KeyboardOff.Contains("dismiss");
                    shim.SetSelected(0, true); dismiss.On = true;
                    bool restored = InputPolicy.Shim == InputPolicy.ShimMode.On && InputPolicy.DismissGuard && s.InputShim == "" && s.KeyboardOff == "";
                    check(observed && dismissOff && restored, "settings: the device-test switches drive InputPolicy live",
                          "observe=" + observed + " dismissOff=" + dismissOff + " restored=" + restored);
                }
                catch (Exception ex) { check(false, "settings: the device-test switches drive InputPolicy live", ex.GetType().Name + ": " + ex.Message); }
                Key(f, Keys.Escape);
            });

            using (var f = new SettingsForm()) info("settings at DPI " + f.WindowDpiValue + ": " + f.Size);
        }

        private static T Row<T>(Form f, string name) where T : class { return (T)Field(f, name); }

        /// <summary>The first row of type T whose caption is <paramref name="caption"/>, anywhere in the form.</summary>
        private static T FindRow<T>(Control root, string caption) where T : Control
        {
            T hit = null;
            Find(root, c => { if (hit == null && c is T && c.Text == caption) hit = (T)c; });
            if (hit == null) throw new InvalidOperationException("no row '" + caption + "'");
            return hit;
        }

        private static object Field(object o, string name)
        {
            var fi = typeof(SettingsForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null) throw new MissingFieldException("SettingsForm", name);
            return fi.GetValue(o);
        }

        private static Button OkButton(Form f)
        {
            Button ok = null;
            Find(f, c => { var b = c as RoundedButton; if (b != null && b.Text == "OK") ok = b; });
            if (ok == null) throw new InvalidOperationException("no OK button");
            return ok;
        }

        private static void Find(Control root, Action<Control> visit)
        {
            foreach (Control c in root.Controls) { visit(c); Find(c, visit); }
        }

        /// <summary>Shows a new SettingsForm modally, runs <paramref name="act"/> once it is up, returns the result (Abort = the
        /// guard fired).</summary>
        private static DialogResult Show(IWin32Window owner, Action<Form> act)
        {
            using (var d = new SettingsForm())
            {
                var guard = new Timer { Interval = GuardMs };
                guard.Tick += (s, e) => { guard.Stop(); try { d.DialogResult = DialogResult.Abort; } catch { } };
                d.Shown += (s, e) =>
                {
                    guard.Start();
                    try { d.BeginInvoke((Action)(() => { try { act(d); } catch (Exception ex) { Logger.Diag("[PROBE] settings action threw: " + ex); } })); } catch { }
                };
                try { return d.ShowDialog(owner); }
                finally { guard.Stop(); guard.Dispose(); }
            }
        }

        private static void After(int ms, Action a)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
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
