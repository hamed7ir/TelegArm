using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.5 — ThemedTextBox's contract, checked on a REAL window by <c>TelegArm.exe --gallery-probe</c>.
    /// Keys go in as posted WM_KEYDOWN / WM_CHAR, the ordinary WinForms path. What this CANNOT check is the RT part of the
    /// contract (dead input, the touch keyboard, TSF): that is device gate G1.</summary>
    internal static class TextBoxProbe
    {
        internal static void Run(Action<bool, string, string> check, Action<string> info)
        {
            using (var f = new Form { Text = "ThemedTextBox probe", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.FixedToolWindow })
            {
                int dpi = Dpi.SystemDpi;
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.Bounds = new Rectangle(wa.Left + 60, wa.Top + 60, Dpi.Px(420, dpi), Dpi.Px(220, dpi));
                var multi = new ThemedTextBox(true) { Bounds = new Rectangle(Dpi.Px(12, dpi), Dpi.Px(12, dpi), Dpi.Px(380, dpi), Dpi.Px(64, dpi)), FontPoints = 11f, Hint = "Write a message…" };
                var single = new ThemedTextBox(false) { Bounds = new Rectangle(Dpi.Px(12, dpi), Dpi.Px(96, dpi), Dpi.Px(380, dpi), Dpi.Px(40, dpi)), FontPoints = 10.5f, Hint = "Search" };
                f.Controls.Add(multi);
                f.Controls.Add(single);
                f.Show();
                Pump(300);
                try
                {
                    // Events are Control's own, so a subscriber holding the field as a plain Control hears them
                    // (MaterialTextBox2 hides them — the reason InputBox exists).
                    Control asControl = multi;
                    int textChanged = 0; KeyEventArgs seen = null;
                    asControl.TextChanged += (s, e) => textChanged++;
                    asControl.KeyDown += (s, e) => { seen = e; if (e.KeyCode == Keys.Enter && _suppressEnter) e.SuppressKeyPress = true; };

                    multi.Focus(); Pump(100);
                    Char(multi.Inner, 'h'); Char(multi.Inner, 'i'); Pump(100);
                    check(multi.Text == "hi" && textChanged >= 2, "textbox: typing reaches Text; TextChanged heard through a Control reference", "\"" + multi.Text + "\" changed=" + textChanged);

                    _suppressEnter = true;
                    Key(multi.Inner, Keys.Enter); Pump(100);   // TranslateMessage makes the WM_CHAR from the posted key
                    check(seen != null && seen.KeyCode == Keys.Enter && multi.Text == "hi", "textbox: KeyDown heard through a Control reference; SuppressKeyPress stops the newline (Enter-to-send)", "\"" + Esc(multi.Text) + "\"");
                    _suppressEnter = false;
                    Key(multi.Inner, Keys.Enter); Pump(100);
                    check(multi.Text == "hi\r\n", "textbox: an unsuppressed Enter inserts ONE newline (multiline, AcceptsReturn)", "\"" + Esc(multi.Text) + "\"");

                    // "Disabled" = read-only. Never Enabled=false, and no handle re-creation for any of these.
                    IntPtr h = multi.Inner.Handle;
                    string before = multi.Text;
                    multi.InputEnabled = false;
                    Char(multi.Inner, 'x'); Pump(100);
                    check(multi.Text == before && multi.Inner.ReadOnly && multi.Inner.Enabled && multi.Enabled, "textbox: InputEnabled=false is read-only (typing blocked), never Enabled=false",
                          "text \"" + Esc(multi.Text) + "\" ReadOnly=" + multi.Inner.ReadOnly + " inner.Enabled=" + multi.Inner.Enabled);
                    // Review 1.5: disabled REALLY means disabled — no click focus, no Focus(), a held focus is released.
                    PostMessage(multi.Inner.Handle, WM_LBUTTONDOWN, (IntPtr)1, MakeLParam(4, 4));
                    PostMessage(multi.Inner.Handle, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(4, 4));
                    Pump(120);
                    bool refused = !multi.Focus();
                    check(!multi.Inner.Focused && refused, "textbox: a disabled field takes no click focus and refuses Focus()", "focused=" + multi.Inner.Focused + " Focus() refused=" + refused);
                    multi.InputEnabled = true;
                    f.Activate(); multi.Focus(); Pump(100);
                    if (multi.Inner.Focused)
                    {
                        multi.InputEnabled = false; Pump(100);
                        check(!multi.Inner.Focused, "textbox: disabling a focused field releases the focus", "focused=" + multi.Inner.Focused);
                        multi.InputEnabled = true;
                    }
                    else info("textbox: the probe window could not take focus here — the release-on-disable check was skipped");

                    // Review 1.5: a multiline EDIT is never shorter than one line, whatever the host height.
                    var tall = multi.Size;
                    multi.Height = Dpi.Px(26, dpi); Pump(80);
                    check(multi.Inner.Height >= multi.Inner.PreferredHeight, "textbox: the multiline EDIT is never shorter than one line",
                          "host " + multi.Height + " EDIT " + multi.Inner.Height + " line " + multi.Inner.PreferredHeight);
                    multi.Size = tall; Pump(80);

                    multi.Hint = "Reply…"; multi.FontPoints = 12f; multi.Recolor(); ThemeHelper.SetAccent(Color.FromArgb(0x40, 0xA7, 0xE3)); Pump(150);
                    ThemeHelper.SetAccent(null); Pump(100);
                    check(multi.Inner.Handle == h && multi.Inner.Multiline && multi.Inner.BorderStyle == BorderStyle.None && multi.Inner.ScrollBars == ScrollBars.None,
                          "textbox: InputEnabled / Hint / Font / theme changes never recreate the EDIT", "handle " + (multi.Inner.Handle == h ? "same" : "RECREATED"));

                    int margins = (int)(SendMessage(multi.Inner.Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xFFFF);
                    check(margins == Dpi.Px(2, dpi), "textbox: the text inset is EM_SETMARGINS, at this DPI", "left margin " + margins);

                    // A tap on the frame, outside the EDIT, focuses the EDIT.
                    single.Inner.Text = ""; f.Activate(); multi.Focus(); Pump(100);
                    if (!multi.Inner.Focused)
                        info("textbox: the probe window could not take focus here (Windows gives focus to the foreground app) — the frame-tap check was skipped");
                    else
                    {
                        PostMessage(single.Handle, WM_LBUTTONDOWN, (IntPtr)1, MakeLParam(2, 2));
                        PostMessage(single.Handle, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(2, 2));
                        Pump(150);
                        check(single.Inner.Focused, "textbox: a tap on the frame focuses the EDIT", "focused=" + single.Inner.Focused);
                    }

                    // The cue: drawn while empty AND unfocused, never while focused. Read back from the window's own
                    // surface (PrintWindow, full content), so another window on top cannot fake it.
                    multi.Text = ""; multi.Hint = "Write a message…"; single.Focus(); Pump(200);
                    multi.Inner.Invalidate(); multi.Inner.Update(); Pump(100);   // a fresh WM_PAINT, not whatever was last composed
                    int unfocused = CueInk(multi);
                    multi.Focus(); Pump(200);
                    multi.Inner.Invalidate(); multi.Inner.Update(); Pump(100);
                    bool gotFocus = multi.Inner.Focused;
                    int focusedInk = CueInk(multi);
                    if (unfocused < 0) info("textbox cue: PrintWindow unavailable here — not checked");
                    else
                    {
                        check(unfocused > 20, "textbox: the cue shows on an empty, unfocused field", unfocused + " cue pixels");
                        // Focus is the OS's to give: a probe running behind other windows may not get it. Say so, not FAIL.
                        if (gotFocus) check(focusedInk == 0, "textbox: nothing is drawn over a focused field", focusedInk + " cue pixels");
                        else info("textbox: the probe window could not take focus here — the focused-cue check was skipped");
                    }
                    // InputBox's LEGACY half binds MaterialTextBox2's own (hiding) events, exactly as 1.x's typed fields did.
                    var legacy = new MaterialSkin.Controls.MaterialTextBox2 { Bounds = new Rectangle(Dpi.Px(12, dpi), Dpi.Px(150, dpi), Dpi.Px(380, dpi), Dpi.Px(48, dpi)) };
                    f.Controls.Add(legacy); Pump(150);
                    var box = InputBox.CreateLegacy(legacy);
                    int viaAdapter = 0, viaControl = 0, keysViaAdapter = 0;
                    box.TextChanged += (s, e) => viaAdapter++;
                    ((Control)legacy).TextChanged += (s, e) => viaControl++;
                    box.KeyDown += (s, e) => keysViaAdapter++;
                    box.Text = "abc"; Pump(100);
                    bool textOk = box.Text == "abc";   // read before the key below types an 'a'
                    var edit = legacy.GetType().GetField("baseTextBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(legacy) as Control;
                    if (edit != null) { Key(edit, Keys.A); Pump(100); }
                    check(viaAdapter >= 1 && keysViaAdapter >= 1 && textOk, "textbox: InputBox's Legacy half hears MaterialTextBox2's TextChanged and KeyDown",
                          "TextChanged " + viaAdapter + ", KeyDown " + keysViaAdapter + ", Text round-trip " + textOk);
                    info("textbox: the same MaterialTextBox2 held as a plain Control heard TextChanged " + viaControl + " time(s) — the hiding trap InputBox exists for");

                    ProbeMaterialReskin(check, dpi);

                    info("textbox: multi " + multi.Size + " inner " + multi.Inner.Bounds + ", single " + single.Size + " inner " + single.Inner.Bounds + " (preferred " + single.PreferredFieldHeight + ")");
                }
                catch (Exception ex) { check(false, "textbox probe threw", ex.ToString()); }
                f.Close();
            }
        }

        private static bool _suppressEnter;

        /// <summary>Review 1.5: MaterialSkin re-skins every control of a managed form (BackColor, ForeColor, Font = Roboto)
        /// whenever a dialog or Settings sets its theme. The Plain field's EDIT must come through with OUR colours and font.</summary>
        private static void ProbeMaterialReskin(Action<bool, string, string> check, int dpi)
        {
            var skin = MaterialSkin.MaterialSkinManager.Instance;
            using (var m = new MaterialSkin.Controls.MaterialForm { Text = "reskin probe", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(80, 80), Size = new Size(Dpi.Px(420, dpi), Dpi.Px(200, dpi)) })
            {
                var panel = new Panel { Dock = DockStyle.Fill };
                var box = new ThemedTextBox(false) { FontPoints = 10.5f, Bounds = new Rectangle(Dpi.Px(12, dpi), Dpi.Px(80, dpi), Dpi.Px(300, dpi), Dpi.Px(40, dpi)) };
                panel.Controls.Add(box);
                m.Controls.Add(panel);
                skin.AddFormToManage(m);
                try
                {
                    m.Show(); Pump(150);
                    var theme = skin.Theme;
                    skin.Theme = theme == MaterialSkin.MaterialSkinManager.Themes.DARK ? MaterialSkin.MaterialSkinManager.Themes.LIGHT : MaterialSkin.MaterialSkinManager.Themes.DARK;
                    skin.Theme = theme;   // what every ThemedDialog / Settings constructor does: re-apply the theme
                    Pump(150);
                    var p = ThemeHelper.Palette;
                    var wantFont = FontHelper.UiAt(10.5f, Dpi.Of(box));
                    bool ok = box.Inner.BackColor.ToArgb() == p.Card.ToArgb() && box.Inner.ForeColor.ToArgb() == p.Text.ToArgb() && ReferenceEquals(box.Inner.Font, wantFont);
                    check(ok, "textbox: MaterialSkin's re-skin does not touch the EDIT's colours or font",
                          "back " + box.Inner.BackColor + " fore " + box.Inner.ForeColor + " font " + box.Inner.Font.Name + " " + box.Inner.Font.Size);
                }
                finally { try { skin.RemoveFormToManage(m); } catch { } m.Close(); }
            }
        }

        /// <summary>Pixels of the cue colour inside the EDIT, from a full-content PrintWindow of the field; -1 if unavailable.</summary>
        private static int CueInk(ThemedTextBox box)
        {
            var r = box.Inner.ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return -1;
            using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try { ok = PrintWindow(box.Inner.Handle, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                    if (!ok) return -1;
                }
                var cue = ThemeHelper.Palette.TextFaint; int n = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (Math.Abs(c.R - cue.R) + Math.Abs(c.G - cue.G) + Math.Abs(c.B - cue.B) < 24) n++;
                    }
                return n;
            }
        }

        private static string Esc(string s) { return (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n"); }

        private static void Pump(int ms)
        {
            var until = Environment.TickCount + ms;
            while (unchecked(Environment.TickCount - until) < 0) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }

        private static void Key(Control c, Keys k) { PostMessage(c.Handle, WM_KEYDOWN, (IntPtr)(int)k, (IntPtr)1); }
        private static void Char(Control c, char ch) { PostMessage(c.Handle, WM_CHAR, (IntPtr)ch, (IntPtr)1); }
        private static IntPtr MakeLParam(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }

        private const int WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, EM_GETMARGINS = 0x00D4;
        private const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    }
}
