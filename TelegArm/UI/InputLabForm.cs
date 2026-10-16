using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Device trip 2026-10 (alpha.2): <c>TelegArm.exe --input-lab</c> — the typing A/B with no account and none of
    /// MainForm's keyboard handling. Runs like --gallery: storage isolated, no mutex, no Telegram client, and NO
    /// TouchKeyboard.Filter unless the launcher switch below turns one on for this window.
    ///
    /// One row per kind of text box, each with live counters taken from the EDIT's own messages:
    ///   keys (WM_KEYDOWN) · chars (WM_CHAR) · native (the box changed) · dead (a typing char the box ignored) · and
    ///   where the chars came from (GetCurrentInputMessageSource: hardware = a real keyboard, injected = the touch keyboard).
    /// The rows: a plain TextBox as Finestra/DroidFinestra use it (single and multi-line), ThemedTextBox (the Plain engine),
    /// a custom control that handles WM_CHAR itself and draws colour emoji (does a non-EDIT even receive the touch
    /// keyboard's text on RT? It does: L6 typed on the device), and the EmojiComposer. "Save report" writes everything to
    /// out\ next to the exe. (Step 2.8 removed the MaterialSkin rows — L5 MaterialTextBox2 and the MaterialForm host
    /// test — with the package; the device result they were built for is in NOTES-carried §10.)
    /// Nothing here ever logs or saves what is typed — only counts.</summary>
    internal sealed class InputLabForm : AppForm
    {
        private readonly List<Probe> _probes = new List<Probe>();
        private readonly NoNativeScrollPanel _page;
        private readonly ThemedLabel _head;
        private readonly TouchKeyboard.Filter _launcher = new TouchKeyboard.Filter();
        private bool _launcherOn;
        private readonly StringBuilder _events = new StringBuilder();
        private KeyCatcher _catcher;
        private EmojiComposer _composer;
        private int _y;

        public InputLabForm()
        {
            Text = "TelegArm input lab";
            Size = new Size(Px(760), Px(720));
            LogicalMinimumSize = new Size(560, 420);

            var host = new Panel { Dock = DockStyle.Fill };
            Content.Controls.Add(host);
            _page = ScrollHost.Wrap(host, ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent);

            int x = Px(16);
            _y = Px(10);
            _head = new ThemedLabel { Role = LabelRole.Dim, Wrap = true, Bounds = new Rectangle(x, _y, Px(700), Px(60)) };
            _page.Controls.Add(_head);
            _y += Px(66);

            // ── switches and actions ──
            int bx = x;
            AddButton(ref bx, "Keyboard launcher: off", b =>
            {
                _launcherOn = !_launcherOn;
                if (_launcherOn) Application.AddMessageFilter(_launcher); else Application.RemoveMessageFilter(_launcher);
                b.Text = "Keyboard launcher: " + (_launcherOn ? "on" : "off");
                Note("launcher " + (_launcherOn ? "ON (TelegArm starts TabTip on a touch tap, as the app does)" : "OFF (Windows decides)"));
            });
            AddButton(ref bx, "Show keyboard", b => { TouchKeyboard.Show(); Note("keyboard shown by button"); });
            _y += Px(46);
            bx = x;
            AddButton(ref bx, "Scale lab", b => ScaleLab());
            AddButton(ref bx, "Clear counters", b => { foreach (var p in _probes) p.Clear(); _events.Clear(); Refresh(); });
            AddButton(ref bx, "Copy report", b => { try { Clipboard.SetText(Report()); Note("report copied"); } catch { } });
            AddButton(ref bx, "Save report", b => SaveReport());
            _y += Px(54);

            // ── the boxes ──
            var l1 = new TextBox { Font = FontHelper.UiAt(10.5f, WindowDpiValue) };
            AddRow("L1  Plain TextBox, single line (as Finestra)", l1, l1, Px(34));
            var l2 = new TextBox { Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.None, Font = FontHelper.UiAt(10.5f, WindowDpiValue) };
            AddRow("L2  Plain TextBox, multi-line (the composer's shape)", l2, l2, Px(80));
            var l3 = new ThemedTextBox(false) { Hint = "ThemedTextBox, single line", FontPoints = 10.5f };
            AddRow("L3  ThemedTextBox, single line (Plain search)", l3, l3.Inner, Px(40));
            var l4 = new ThemedTextBox(true) { Hint = "ThemedTextBox, multi-line", FontPoints = 11f };
            AddRow("L4  ThemedTextBox, multi-line (Plain composer)", l4, l4.Inner, Px(80));
            var l6 = new KeyCatcher(this) { Font = FontHelper.UiAt(11f, WindowDpiValue) };
            AddRow("L6  Custom control: handles WM_CHAR itself, draws colour emoji", l6, null, Px(80));
            l6.Probe = _probes[_probes.Count - 1];
            _catcher = l6;
            var l7 = new EmojiComposer { Hint = "Write a message…", FontPoints = 11f };
            AddRow("L7  EmojiComposer — the new composer (colour emoji, takes WM_CHAR itself)", l7, l7, Px(96));
            _composer = l7;

            UpdateHead();
            var t = new Timer { Interval = 1000 };
            t.Tick += (s, e) => UpdateHead();
            t.Start();
            FormClosed += (s, e) => { t.Stop(); if (_launcherOn) Application.RemoveMessageFilter(_launcher); };
        }

        private void AddButton(ref int x, string text, Action<RoundedButton> click)
        {
            var b = new RoundedButton { Text = text, Kind = RoundedButtonKind.Neutral, Modern = true, Bounds = new Rectangle(x, _y, Px(170), Px(38)), Font = FontHelper.UiAt(9.5f, WindowDpiValue) };
            b.Click += (s, e) => click(b);
            _page.Controls.Add(b);
            x += Px(178);
        }

        private void AddRow(string caption, Control field, Control edit, int height)
        {
            int x = Px(16);
            _page.Controls.Add(new ThemedLabel { Text = caption, Role = LabelRole.Title, Bounds = new Rectangle(x, _y, Px(700), Px(22)) });
            _y += Px(24);
            field.Bounds = new Rectangle(x, _y, Px(560), height);
            _page.Controls.Add(field);
            _y += height + Px(4);
            var status = new ThemedLabel { Role = LabelRole.Dim, Bounds = new Rectangle(x, _y, Px(700), Px(20)) };
            _page.Controls.Add(status);
            _y += Px(30);
            var probe = new Probe(caption.Substring(0, 2), edit, status);
            _probes.Add(probe);
            probe.Attach();
        }

        private void UpdateHead()
        {
            bool hw = false; try { hw = TouchKeyboard.HasHardwareKeyboard(); } catch { }
            bool tablet = false; try { tablet = TouchKeyboard.IsTabletMode(); } catch { }
            _head.Text = "Type a few words into each box, close and reopen the keyboard, then type again. "
                + "Counts only — nothing typed is recorded.\nHardware keyboard: " + (hw ? "yes" : "no")
                + " · tablet posture: " + (tablet ? "yes" : "no") + " · launcher: " + (_launcherOn ? "on" : "off")
                + " · DPI " + WindowDpiValue;
        }

        private void Note(string what)
        {
            _events.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + what);
            UpdateHead();
        }

        /// <summary>Times one DPI rescale pass (96→144→96 relative to the current DPI) on a realised, hidden design gallery —
        /// the cost a live interface-scale change would pay per window on this CPU (plan step 3.8b).</summary>
        private void ScaleLab()
        {
            try
            {
                using (var g = new DesignGalleryForm())
                {
                    g.CreateControl();
                    int d = g.WindowDpiValue, up = d * 3 / 2, count = 0;
                    var all = new Stack<Control>(); all.Push(g);
                    while (all.Count > 0) { var c = all.Pop(); count++; foreach (Control k in c.Controls) all.Push(k); }
                    var sw = Stopwatch.StartNew();
                    DpiScaler.Rescale(g, d, up);
                    long a = sw.ElapsedMilliseconds;
                    sw.Restart();
                    DpiScaler.Rescale(g, up, d);
                    long b = sw.ElapsedMilliseconds;
                    Note("scale lab: " + count + " controls, rescale " + d + "→" + up + " " + a + " ms, back " + b + " ms");
                    ThemedDialog.Show(this, "Scale lab", count + " controls\n" + d + " → " + up + " DPI: " + a + " ms\n" + up + " → " + d + " DPI: " + b + " ms", "OK");
                }
            }
            catch (Exception ex) { Note("scale lab failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TelegArm input lab — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(_head.Text.Replace("\n", " "));
            sb.AppendLine();
            foreach (var p in _probes) sb.AppendLine(p.Line());
            if (_events.Length > 0) { sb.AppendLine(); sb.AppendLine("Events:"); sb.Append(_events); }
            sb.AppendLine();
            sb.Append(DiagReport.Build());
            return sb.ToString();
        }

        private void SaveReport()
        {
            string name = "inputlab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            foreach (var dir in new[] { Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "out"),
                                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, name);
                    File.WriteAllText(path, Report(), new UTF8Encoding(true));
                    Note("report saved: " + path);
                    ThemedDialog.Show(this, "Input lab", "Saved to\n" + path, "OK");
                    return;
                }
                catch { /* not writable: try the next place */ }
            }
            ThemedDialog.Show(this, "Input lab", "Couldn't save the report. Use Copy report instead.", "OK");
        }

        // ── the gallery probe (TelegArm.exe --gallery-probe) ─────────────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]   // SendMessageW: the ANSI one mangles every non-ASCII char
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        /// <summary>Opens the lab for real, types into every box with real WM_CHAR messages and checks the counters: on this
        /// healthy dev box every typed char must be "native" and none "dead"; the custom control must receive them; the
        /// report must list every box. Proves the lab measures, before it travels.</summary>
        internal static void RunProbe(IWin32Window owner, Action<bool, string, string> check, Action<string> info)
        {
            using (var f = new InputLabForm())
            {
                f.Show(owner);
                Application.DoEvents();
                const string word = "lab ok";
                foreach (var p in f._probes)
                {
                    IntPtr h = p.EditHandle;
                    if (h == IntPtr.Zero) continue;
                    foreach (char c in word) SendMessage(h, 0x0102, (IntPtr)c, (IntPtr)1);
                    check(p.Chars == word.Length && p.Native == word.Length && p.Dead == 0,
                          "input lab " + p.Name + ": every char typed is native, none dead", p.Line());
                }
                var catcher = f._catcher;
                foreach (char c in word) SendMessage(catcher.Handle, 0x0102, (IntPtr)c, (IntPtr)1);
                check(catcher.Typed == word && catcher.Probe.Chars == word.Length, "input lab L6: the custom control receives WM_CHAR itself",
                      "text='" + catcher.Typed + "' chars=" + catcher.Probe.Chars);

                ComposerProbe(f._composer, check);

                string report = f.Report();
                check(report.Contains("L1:") && report.Contains("L6:") && report.Contains("L7:") && !report.Contains(word),
                      "input lab: the report lists every box and never what was typed", report.Length + " chars");
                info("input lab at DPI " + f.WindowDpiValue + ": " + f._probes.Count + " probed boxes");
                f.Close();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

        /// <summary>Plan E1: the new composer driven with real messages — what TabTip and a keyboard send it.</summary>
        private static void ComposerProbe(EmojiComposer c, Action<bool, string, string> check)
        {
            IntPtr h = c.Handle;
            Action<string> type = s => { foreach (char ch in s) SendMessage(h, 0x0102, (IntPtr)ch, (IntPtr)1); };
            Action<Keys, bool> key = (k, ctrl) =>
            {
                var st = new byte[256]; GetKeyboardState(st); byte old = st[0x11];
                if (ctrl) { st[0x11] = 0x80; SetKeyboardState(st); }
                try { SendMessage(h, 0x0100, (IntPtr)(int)k, (IntPtr)1); }
                finally { if (ctrl) { GetKeyboardState(st); st[0x11] = old; SetKeyboardState(st); } }
            };
            c.Focus();
            c.Text = "";
            type("hi \U0001F600x");
            check(c.Text == "hi \U0001F600x", "composer: Latin and a surrogate-pair emoji arrive as typed", "'" + c.Text + "'");
            type("\b\b");
            check(c.Text == "hi ", "composer: Backspace removes a char, then the WHOLE emoji", "'" + c.Text + "' len " + c.Text.Length);
            type("\ryo");
            check(c.Text == "hi \nyo", "composer: Shift+Enter's CR becomes a line break", "'" + c.Text.Replace("\n", "\\n") + "'");
            key(Keys.Left, false); type("Z");
            check(c.Text == "hi \nyZo", "composer: Left moves the caret one character", "'" + c.Text.Replace("\n", "\\n") + "'");
            key(Keys.Z, true);
            check(c.Text == "hi \nyo", "composer: Ctrl+Z undoes the last edit", "'" + c.Text.Replace("\n", "\\n") + "'");
            c.SelectAll(); type("q");
            check(c.Text == "q", "composer: typing replaces a selection", "'" + c.Text + "'");
            c.Text = ""; type("\u0633\u0644\u0627\u0645 \u2764\uFE0F");
            check(c.Text == "\u0633\u0644\u0627\u0645 \u2764\uFE0F" && c.SelectionStart == c.Text.Length, "composer: Persian and a BMP emoji, caret at the end", "len " + c.Text.Length + " caret " + c.SelectionStart);
            // R4: the arrows are visual - in a Persian line Right goes back in the text, Left goes on; Latin unchanged
            c.Text = "سلام"; c.SelectionStart = 2;
            key(Keys.Right, false); int rtlRight = c.SelectionStart;
            key(Keys.Left, false); key(Keys.Left, false); int rtlLeft = c.SelectionStart;
            c.Text = "abcd"; c.SelectionStart = 2;
            key(Keys.Right, false); int ltrRight = c.SelectionStart;
            check(rtlRight == 1 && rtlLeft == 3 && ltrRight == 3, "composer: arrows are visual in a right-to-left line (and unchanged in Latin)",
                  "RTL 2 -> Right " + rtlRight + " -> Left x2 " + rtlLeft + "; LTR 2 -> Right " + ltrRight);
            c.Text = "Hello \U0001F600 x";
            check(true, "composer layout (info)", c.DebugFirstLine());
            int fired = 0;
            EventHandler onGrow = (s, e) => fired++;
            c.ContentHeightChanged += onGrow;
            c.Text = "one";
            int before = c.ContentHeight;
            c.Text = "one\ntwo\nthree\nfour";
            int lh = c.HeightForLines(2) - c.HeightForLines(1);
            check(fired >= 1 && c.ContentHeight - before >= 3 * lh - 3, "composer: ContentHeightChanged fires and the content grows per line", "fired " + fired + ", " + before + " -> " + c.ContentHeight);
            check(c.HeightForLines(13) > c.HeightForLines(4), "composer: the 13-line cap is taller than 4 lines", c.HeightForLines(4) + " / " + c.HeightForLines(13));
            c.ContentHeightChanged -= onGrow;
            c.Text = "Hello \U0001F600 x";
            int one = c.ContentHeight;
            c.Text = string.Join(" ", System.Linq.Enumerable.Repeat("wrapping words", 30));
            check(c.ContentHeight > one * 2, "composer: long text wraps onto several lines", one + " -> " + c.ContentHeight);
            c.Text = "";
        }

        // ── the per-box observer ──────────────────────────────────────────────────────────────────────────────────

        private sealed class Probe : NativeWindow
        {
            private const int WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102;
            public readonly string Name;
            private readonly Control _edit;
            private readonly Control _status;
            public int Keys, Chars, Native, Dead, Hardware, Injected, Other;

            public Probe(string name, Control edit, Control status) { Name = name; _edit = edit; _status = status; }

            public void Attach()
            {
                if (_edit == null) { Show(); return; }
                if (_edit.IsHandleCreated) Assign();
                _edit.HandleCreated += (s, e) => Assign();
                _edit.HandleDestroyed += (s, e) => { try { ReleaseHandle(); } catch { } };
                Show();
            }

            private void Assign() { try { if (Handle == IntPtr.Zero) AssignHandle(_edit.Handle); } catch { } }

            public void Clear() { Keys = Chars = Native = Dead = Hardware = Injected = Other = 0; Show(); }

            /// <summary>The observed EDIT's handle (zero for the custom control, which counts for itself).</summary>
            public IntPtr EditHandle => _edit != null && _edit.IsHandleCreated ? _edit.Handle : IntPtr.Zero;

            /// <summary>Counts a WM_CHAR's origin (the custom control reports its own).</summary>
            public void Source()
            {
                switch (InputSource.Origin())
                {
                    case 1: Hardware++; break;
                    case 2: Injected++; break;
                    default: Other++; break;
                }
            }

            public string Line()
            {
                return Name + ": keys " + Keys + " · chars " + Chars + " (hardware " + Hardware + ", injected " + Injected + ", other " + Other + ")"
                    + (_edit != null ? " · native " + Native + " · DEAD " + Dead : " · received " + Chars);
            }

            public void Show() { if (_status != null && !_status.IsDisposed) _status.Text = Line(); }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_KEYDOWN) { Keys++; base.WndProc(ref m); Show(); return; }
                if (m.Msg != WM_CHAR) { base.WndProc(ref m); return; }
                Chars++; Source();
                var tb = _edit as TextBoxBase;
                char ch = (char)(m.WParam.ToInt64() & 0xFFFF);
                string before = _edit.Text ?? "";
                int s0 = tb != null ? tb.SelectionStart : 0, l0 = tb != null ? tb.SelectionLength : 0;
                base.WndProc(ref m);
                bool changed = !string.Equals(before, _edit.Text ?? "", StringComparison.Ordinal)
                    || (tb != null && l0 > 0 && tb.SelectionLength == 0 && tb.SelectionStart == s0 + 1);
                bool typing = ch == '\b' || ch >= ' ' || ((ch == '\r' || ch == '\n') && tb != null && tb.Multiline);
                bool noop = ch == '\b' && s0 == 0 && l0 == 0;
                if (changed) Native++;
                else if (typing && !noop && tb != null && !tb.ReadOnly) Dead++;
                Show();
            }
        }

        private static class InputSource
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct INPUT_MESSAGE_SOURCE { public int deviceType; public int originId; }
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);

            /// <summary>IMO_HARDWARE=1, IMO_INJECTED=2, IMO_SYSTEM=4; 0 when unavailable.</summary>
            public static int Origin()
            {
                try { INPUT_MESSAGE_SOURCE s; return GetCurrentInputMessageSource(out s) ? s.originId : 0; }
                catch { return 0; }
            }
        }

        // ── L6: a control that is not an EDIT ────────────────────────────────────────────────────────────────────

        /// <summary>Takes WM_CHAR itself and paints the text with the Noto colour emoji (EmojiRenderer), so the lab shows
        /// (a) whether the touch keyboard's characters reach a non-EDIT window on RT at all, and (b) colour emoji as they
        /// are typed. Windows does not open the keyboard for it by itself — a touch tap here opens TabTip directly, which
        /// is what a custom composer would have to do.</summary>
        private sealed class KeyCatcher : Control
        {
            private const int WM_CHAR = 0x0102;
            private readonly InputLabForm _owner;
            private readonly StringBuilder _text = new StringBuilder();
            private char _high;
            public Probe Probe;

            /// <summary>What has been typed here (the probe reads it; the report never does).</summary>
            public string Typed => _text.ToString();

            public KeyCatcher(InputLabForm owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                TabStop = true;
                AccessibleName = "Custom text field";
                AccessibleRole = AccessibleRole.Text;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                try { if (TouchKeyboard.LastInputWasTouch()) TouchKeyboard.Show(); } catch { }
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override bool IsInputKey(Keys keyData)
            {
                var k = keyData & Keys.KeyCode;
                return k == Keys.Enter || k == Keys.Back || base.IsInputKey(keyData);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0100 && Probe != null) Probe.Keys++;
                if (m.Msg == WM_CHAR)
                {
                    char ch = (char)(m.WParam.ToInt64() & 0xFFFF);
                    if (Probe != null) { Probe.Chars++; Probe.Source(); }
                    if (ch == '\b')
                    {
                        int n = _text.Length;
                        if (n > 0) _text.Length = n >= 2 && char.IsLowSurrogate(_text[n - 1]) && char.IsHighSurrogate(_text[n - 2]) ? n - 2 : n - 1;
                    }
                    else if (ch == '\r' || ch == '\n') _text.Append('\n');
                    else if (char.IsHighSurrogate(ch)) _high = ch;
                    else if (char.IsLowSurrogate(ch)) { if (_high != '\0') { _text.Append(_high).Append(ch); _high = '\0'; } }
                    else if (ch >= ' ') _text.Append(ch);
                    if (Probe != null) Probe.Show();
                    Invalidate();
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                g.Clear(p.Card);
                using (var pen = new Pen(Focused ? p.Accent : p.Divider)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                int lineH = TextRenderer.MeasureText("Ag", Font).Height, pad = 8, y = pad;
                string[] lines = _text.Length == 0 && !Focused ? new[] { "Tap and type (emoji too)" } : _text.ToString().Split('\n');
                var ink = _text.Length == 0 ? p.TextDim : p.Text;
                int lastW = 0;
                foreach (var line in lines)
                {
                    var r = new Rectangle(pad, y, Width - 2 * pad, lineH);
                    if (EmojiRenderer.ContainsEmoji(line)) EmojiRenderer.DrawLine(g, line, Font, ink, r);
                    else TextRenderer.DrawText(g, line, Font, r, ink, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    lastW = EmojiRenderer.ContainsEmoji(line) ? EmojiRenderer.MeasureLineWidth(g, line, Font) : TextRenderer.MeasureText(g, line, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
                    y += lineH;
                }
                if (Focused)
                    using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, pad + (_text.Length == 0 ? 0 : lastW) + 1, y - lineH + 2, 2, lineH - 4);
            }
        }
    }
}
