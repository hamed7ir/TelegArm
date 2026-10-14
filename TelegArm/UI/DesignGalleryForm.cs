using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.3 — the design gallery: the first <see cref="AppForm"/>, and the place every Phase-1
    /// control is shown before any real window uses it (steps 1.4–1.6 add their controls here).
    ///   TelegArm.exe --gallery                 open it (storage isolated, like --shoot: no account is touched)
    ///   TelegArm.exe --gallery-probe out.txt   open it, run the chrome checks on a REAL window, write PASS/FAIL, exit
    /// Combine with TELEGARM_DPI=pmv1 to exercise per-monitor DPI, TELEGARM_ACCENT=#RRGGBB for accents, and
    /// TELEGARM_CHROME=native for the escape hatch. The --shoot rig also renders it.</summary>
    public sealed class DesignGalleryForm : AppForm
    {
        private readonly Label _info;
        private readonly Panel _swatches;
        private readonly Panel _glyphStrip;
        private readonly Button _anchored;
        private readonly NoNativeScrollPanel _page;
        private readonly ThemedScrollBar _pageBar;
        private readonly List<ThemedScrollBar> _demoBars = new List<ThemedScrollBar>();
        private readonly List<GlyphButton> _onAccent = new List<GlyphButton>();
        private readonly List<GlyphButton> _onCard = new List<GlyphButton>();

        public DesignGalleryForm()
        {
            Text = "TelegArm design gallery";
            Size = new Size(Px(900), Px(640));
            LogicalMinimumSize = new Size(560, 420);
            Bar.LeftKind = TitleBar.LeftButton.Menu;

            // Step 1.6: the page scrolls (TelegArm's own stack: NoNativeScrollPanel + ThemedScrollBar + TouchScroller) and
            // carries every Phase-1 control; the info line and the probe's anchored button stay on Content below it.
            var cs0 = Content.ClientSize;
            var pageHost = new Panel { Bounds = new Rectangle(0, 0, cs0.Width, Math.Max(Px(100), cs0.Height - Px(AnchoredGapY) - Px(44))), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Content.Controls.Add(pageHost);
            _page = ScrollHost.Wrap(pageHost, ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent);
            foreach (Control c in pageHost.Controls) if (c is ThemedScrollBar) _pageBar = (ThemedScrollBar)c;
            var page = _page.Controls;

            int x = Px(20), y = Px(14);
            page.Add(Header("Glyph buttons (drawn, never font glyphs)", x, y));
            y += Px(26);
            _glyphStrip = new Panel { Bounds = new Rectangle(x, y, Px(52) * 10, Px(46) * 2) };
            int i = 0;
            foreach (GlyphButton.Glyph g in Enum.GetValues(typeof(GlyphButton.Glyph)))
            {
                var a = new GlyphButton(g) { Bounds = new Rectangle(i * Px(52), 0, Px(52), Px(46)), DangerHover = g == GlyphButton.Glyph.Close };
                var c = new GlyphButton(g) { Bounds = new Rectangle(i * Px(52), Px(46), Px(52), Px(46)) };
                _onAccent.Add(a); _onCard.Add(c);
                _glyphStrip.Controls.Add(a); _glyphStrip.Controls.Add(c);
                i++;
            }
            page.Add(_glyphStrip);
            y += _glyphStrip.Height + Px(18);

            page.Add(Header("Palette (ThemeHelper.Palette)", x, y));
            y += Px(26);
            _swatches = new SwatchPanel { Bounds = new Rectangle(x, y, Px(820), Px(330)) };
            page.Add(_swatches);
            y += _swatches.Height + Px(18);

            y = AddKit(x, y);

            // Placed from Content's REAL size (the gutter is 6 or 12 by touch capability, the bar 46 or 36 by screen
            // height): the anchored button sits AnchoredGap logical px from the right and bottom edges, so the probe can
            // check those gaps exactly at any DPI.
            var cs = Content.ClientSize;
            _info = new Label { AutoSize = false, Bounds = new Rectangle(x, cs.Height - Px(AnchoredGapY) - Px(26), Px(600), Px(22)), Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            Content.Controls.Add(_info);
            _anchored = new Button { Text = "Anchored", Bounds = new Rectangle(cs.Width - Px(AnchoredGapX) - Px(120), cs.Height - Px(AnchoredGapY) - Px(32), Px(120), Px(32)), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, FlatStyle = FlatStyle.Flat };
            Content.Controls.Add(_anchored);
            Recolor();
        }

        private const int AnchoredGapX = 20, AnchoredGapY = 8;

        /// <summary>Steps 1.4–1.6: one section per control, top-down on the scrolling page. Returns the next free y.</summary>
        private int AddKit(int x, int y)
        {
            var page = _page.Controls;
            int dpi = WindowDpiValue;

            // Buttons — Modern (the 2.x button) in every kind, plus the 1.x one for comparison.
            page.Add(Header("Buttons (RoundedButton, Modern)", x, y)); y += Px(30);
            int bx = x;
            foreach (RoundedButtonKind k in Enum.GetValues(typeof(RoundedButtonKind)))
            {
                page.Add(new RoundedButton { Text = k.ToString(), Kind = k, Modern = true, Bounds = new Rectangle(bx, y, Px(120), Px(38)), Font = FontHelper.UiAt(10f, dpi, FontStyle.Bold) });
                bx += Px(128);
            }
            page.Add(new RoundedButton { Text = "Disabled", Modern = true, Enabled = false, Bounds = new Rectangle(bx, y, Px(120), Px(38)), Font = FontHelper.UiAt(10f, dpi, FontStyle.Bold) });
            bx += Px(128);
            page.Add(new RoundedButton { Text = "1.x Primary", Bounds = new Rectangle(bx, y, Px(120), Px(38)) });
            y += Px(38) + Px(18);

            // Dialogs (step 1.4) — opened for real, so the keyboard can be tried by hand.
            page.Add(Header("Dialogs (DialogForm)", x, y)); y += Px(30);
            bx = x;
            AddLauncher(ref bx, y, "Confirm…", () => ConfirmDialog.Ask(this, "Apply the new settings now?", "Question"));
            AddLauncher(ref bx, y, "Delete…", () => ConfirmDialog.Ask(this, "Delete this chat for you? This cannot be undone.", "Delete chat", "Delete", "No", true));
            AddLauncher(ref bx, y, "Input…", () => InputDialog.Ask(this, "Rename", "New name", "Family group"));
            AddLauncher(ref bx, y, "Close choice…", () => { using (var d = new CloseChoiceDialog()) d.ShowDialog(this); });
            AddLauncher(ref bx, y, "Info…", () => ConfirmDialog.Info(this, "Saved.", "Info"));
            y += Px(38) + Px(18);

            // Text fields (step 1.5).
            page.Add(Header("Text fields (ThemedTextBox)", x, y)); y += Px(30);
            page.Add(new ThemedTextBox(false) { Hint = "Search", Bounds = new Rectangle(x, y, Px(300), Px(40)), FontPoints = 10.5f });
            page.Add(new ThemedTextBox(false) { Hint = "Read-only (InputEnabled = false)", InputEnabled = false, Bounds = new Rectangle(x + Px(316), y, Px(300), Px(40)), FontPoints = 10.5f });
            y += Px(40) + Px(10);
            page.Add(new ThemedTextBox(true) { Hint = "Write a message…", Bounds = new Rectangle(x, y, Px(616), Px(64)), FontPoints = 11f });
            y += Px(64) + Px(18);

            // Labels (step 1.6).
            page.Add(Header("Labels (ThemedLabel)", x, y)); y += Px(30);
            foreach (LabelRole r in Enum.GetValues(typeof(LabelRole)))
            {
                var l = new ThemedLabel { Text = r + " — the quick brown fox 😀", Role = r, Location = new Point(x, y), Width = Px(400), Height = Px(22) };
                if (r == LabelRole.Title) { l.PointSize = 12f; l.Style = FontStyle.Bold; l.Height = Px(26); }
                page.Add(l); y += l.Height + Px(4);
            }
            page.Add(new ThemedLabel { Text = "سلام، حالت چطوره؟ — Persian aligns right by itself", Location = new Point(x, y), Width = Px(400), Height = Px(24) }); y += Px(28);
            var wrap = new ThemedLabel { Text = "A wrapped label: its height follows its width, so a long Persian or English sentence never clips — resize the window to see it reflow.", Wrap = true, Role = LabelRole.Dim, Location = new Point(x, y), Width = Px(400) };
            page.Add(wrap); y += Math.Max(Px(40), wrap.Height) + Px(18);

            // Setting rows (step 1.6): DroidFinestra's rows + SettingGroup, and the promoted Segmented/Stepper.
            page.Add(Header("Setting rows (SettingRows in a SettingGroup)", x, y)); y += Px(30);
            var grp = new SettingGroup { Location = new Point(x, y), Width = Px(616) };
            grp.AddRange(
                new SectionHeader("Appearance"),
                new ToggleRow("Notifications", true),
                new ChoiceRow("Theme", new[] { "System", "Light", "Dark" }, 0),
                new SegmentedRow("Text size", new[] { "Small", "Normal", "Large" }, 1),
                new StepperRow("Chat font", 8, 20, 11, 1, " pt"),
                new TextRow("Display name", "Ada Lovelace"),
                new NoteRow("Nothing on this page is saved — it is the design gallery."),
                new OutputRow("Output", 56));
            page.Add(grp); y += grp.Height + Px(18);

            // Empty state (step 1.6).
            page.Add(Header("Empty state (EmptyState)", x, y)); y += Px(30);
            page.Add(new EmptyState { Icon = GlyphButton.Glyph.Search, Title = "No chats found", Hint = "Nothing matches your search. Try another name, or start a new chat.", ActionText = "New chat", Bounds = new Rectangle(x, y, Px(616), Px(260)) });
            y += Px(260) + Px(18);

            // Scrollbars (step 1.6): 1.x (left) and the restyle that switches on in 2.0 (right). The flag is read when a bar
            // is BUILT, so it is set only around this one construction — never left on (the rig shoots other windows too).
            page.Add(Header("Scrollbars: 1.x | restyled (ThemedScrollBar.Restyled, on in 2.0)", x, y)); y += Px(30);
            AddScrollDemo(x, y, false);
            AddScrollDemo(x + Px(316), y, true);
            y += Px(140) + Px(18);

            // Menus (step 1.6): Restyled is read when a menu opens and paints, so it is on only while this one is open.
            page.Add(Header("Menus (ThemedContextMenuStrip)", x, y)); y += Px(30);
            bx = x;
            AddLauncher(ref bx, y, "1.x menu", null).Click += (s, e) => ShowDemoMenu((Control)s, false);
            AddLauncher(ref bx, y, "Restyled menu", null).Click += (s, e) => ShowDemoMenu((Control)s, true);
            y += Px(38) + Px(18);
            return y;
        }

        /// <summary>The rig: scroll the page to its n-th screen (0 = top). False when that screen is past the end.</summary>
        internal bool ScrollPage(int n)
        {
            int view = _page.ClientSize.Height, total = _page.DisplayRectangle.Height;
            int y = n * Math.Max(1, view - Px(40));
            if (n > 0 && y >= total - Px(20)) return false;
            _page.AutoScrollPosition = new Point(0, Math.Min(y, Math.Max(0, total - view)));
            _page.PerformLayout();
            if (_pageBar != null) _pageBar.Invalidate();
            return true;
        }

        private RoundedButton AddLauncher(ref int x, int y, string text, Action open)
        {
            var b = new RoundedButton { Text = text, Kind = RoundedButtonKind.Neutral, Modern = true, Bounds = new Rectangle(x, y, Px(150), Px(38)), Font = FontHelper.UiAt(10f, WindowDpiValue) };
            if (open != null) b.Click += (s, e) => open();
            _page.Controls.Add(b);
            x += Px(158);
            return b;
        }

        private void AddScrollDemo(int x, int y, bool restyled)
        {
            var host = new Panel { Bounds = new Rectangle(x, y, Px(300), Px(140)) };
            bool was = ThemedScrollBar.Restyled;
            NoNativeScrollPanel inner;
            try
            {
                ThemedScrollBar.Restyled = restyled;
                inner = ScrollHost.Wrap(host, ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent);
            }
            finally { ThemedScrollBar.Restyled = was; }
            foreach (Control c in host.Controls) if (c is ThemedScrollBar) _demoBars.Add((ThemedScrollBar)c);
            for (int i = 0; i < 12; i++)
                inner.Controls.Add(new ThemedLabel { Text = (restyled ? "Restyled" : "1.x") + " row " + (i + 1), Location = new Point(Px(10), Px(6) + i * Px(26)), Width = Px(240), Height = Px(22) });
            _page.Controls.Add(host);
        }

        private void ShowDemoMenu(Control anchor, bool restyled)
        {
            var m = new ThemedContextMenuStrip();
            m.Items.Add("Reply"); m.Items.Add("Copy"); m.Items.Add("Forward"); m.Items.Add(new ToolStripSeparator());
            var sub = new ToolStripMenuItem("More"); sub.DropDownItems.Add("Pin"); sub.DropDownItems.Add("Select"); m.Items.Add(sub);
            m.Items.Add("Delete");
            bool was = ThemedContextMenuStrip.Restyled;
            ThemedContextMenuStrip.Restyled = restyled;
            m.Closed += (s, e) => { ThemedContextMenuStrip.Restyled = was; BeginInvoke((Action)m.Dispose); };
            m.Show(anchor, new Point(0, anchor.Height));
        }

        private Label Header(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y), Font = FontHelper.UiAt(11f, WindowDpiValue, FontStyle.Bold), Tag = "header" };
        }

        protected override void OnThemeChanged() { Recolor(); }

        private void Recolor()
        {
            var p = ThemeHelper.Palette;
            foreach (var b in _onAccent) { b.BarColor = p.TitleBar; b.Invalidate(); }
            foreach (var b in _onCard) { b.BarColor = p.Card; b.GlyphColor = p.Text; b.Invalidate(); }
            RecolorTree(Content, p);
            foreach (var b in new[] { _pageBar }.Concat(_demoBars)) if (b != null) { b.IsDark = p.Dark; b.AccentColor = p.Accent; b.Invalidate(); }
            _anchored.BackColor = p.Card; _anchored.ForeColor = p.Text;
            _info.Text = "DPI " + WindowDpiValue + " (" + Dpi.Mode + ", system " + Dpi.SystemDpi + ")   chrome: "
                         + (CustomChrome ? "custom" : "native") + " (" + ChromeReason + ")   accent " + ColorMath.Hex(p.Accent)
                         + (ThemeHelper.AccentOverride.HasValue ? " (fixed)" : " (Windows)");
            _swatches.Invalidate();
        }

        /// <summary>Headers in AccentFg, plain labels in Text, containers in Window — the kit controls colour themselves.</summary>
        private static void RecolorTree(Control parent, Palette p)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Label) { c.ForeColor = (c.Tag as string) == "header" ? p.AccentFg : p.Text; c.BackColor = p.Window; }
                else if (c is Panel && !(c is SwatchPanel) && !(c is SettingGroup)) { c.BackColor = p.Window; RecolorTree(c, p); }   // a SettingGroup is a Card and colours itself
                else if (c is ThemedTextBox) ((ThemedTextBox)c).Recolor();
            }
        }

        /// <summary>Every Color token of the current palette: chip + name, in columns.</summary>
        private sealed class SwatchPanel : Panel, IDpiAware
        {
            private static readonly PropertyInfo[] Tokens = typeof(Palette).GetProperties().Where(pp => pp.PropertyType == typeof(Color)).ToArray();
            public SwatchPanel() { DoubleBuffered = true; }
            void IDpiAware.OnDpiChanged(int o, int n) { Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                e.Graphics.Clear(p.Card);
                int dpi = Dpi.Of(this), row = Dpi.Px(20, dpi), colW = Dpi.Px(212, dpi), chip = Dpi.Px(14, dpi), pad = Dpi.Px(8, dpi);
                var font = FontHelper.UiAt(8.5f, dpi);
                int perCol = Math.Max(1, (Height - pad * 2) / row);
                for (int i = 0; i < Tokens.Length; i++)
                {
                    int cx = pad + (i / perCol) * colW, cy = pad + (i % perCol) * row;
                    var c = (Color)Tokens[i].GetValue(p, null);
                    using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, cx, cy + (row - chip) / 2, chip, chip);
                    using (var pen = new Pen(p.Divider)) e.Graphics.DrawRectangle(pen, cx, cy + (row - chip) / 2, chip, chip);
                    TextRenderer.DrawText(e.Graphics, Tokens[i].Name, font, new Rectangle(cx + chip + pad / 2, cy, colW - chip - pad, row), p.Text,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        // ── the probe: chrome checks on a real window ────────────────────────────────────────────────────────

        /// <summary>Opens the gallery, runs the checks, writes "PASS|FAIL name detail" lines to
        /// <paramref name="file"/>, returns the number of failures.</summary>
        internal static int RunProbe(string file)
        {
            var log = new StringBuilder();
            int fails = 0;
            Action<bool, string, string> check = (ok, name, detail) =>
            {
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail));
                if (!ok) fails++;
                // Written as it goes: if the process dies, the report still shows how far it got.
                try { File.WriteAllText(file, log.ToString(), new UTF8Encoding(true)); } catch { }
            };
            log.AppendLine(DiagReport.Build());
            using (var f = new DesignGalleryForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                var primary = Screen.PrimaryScreen.WorkingArea;
                f.Location = new Point(primary.Left + 40, primary.Top + 40);
                f.Show();
                Pump(400);
                Func<string> where = () => "anchored " + f._anchored.Bounds + " in content " + f.Content.ClientSize
                    + " gaps " + (f.Content.ClientSize.Width - f._anchored.Right) + "," + (f.Content.ClientSize.Height - f._anchored.Bottom)
                    + " bar " + f.Bar.Height + (f.Bar.Compact ? " (compact)" : "");
                log.AppendLine("INFO  after show: " + where());
                try
                {
                    check(f.CustomChrome, "custom chrome active", AppForm.ChromeReason);
                    RECT wr; GetWindowRect(f.Handle, out wr);
                    var cl = f.RectangleToScreen(f.ClientRectangle);
                    check(wr.Left == cl.Left && wr.Top == cl.Top && wr.Right == cl.Right && wr.Bottom == cl.Bottom, "window rect == client rect (WM_NCCALCSIZE)",
                          Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom) + " vs " + cl);

                    int W = f.ClientSize.Width, H = f.ClientSize.Height, g = f.Px(Gutter) / 2;
                    var cases = new[]
                    {
                        Tuple.Create("caption (title area)", new Point(W / 3, f.Bar.Height / 2), HTCAPTION),
                        Tuple.Create("top edge", new Point(W / 3, g), HTTOP),
                        Tuple.Create("top-left corner", new Point(1, 1), HTTOPLEFT),
                        Tuple.Create("left edge", new Point(g, H / 2), HTLEFT),
                        Tuple.Create("right edge", new Point(W - 1 - g, H / 2), HTRIGHT),
                        Tuple.Create("bottom edge", new Point(W / 2, H - 1 - g), HTBOTTOM),
                        Tuple.Create("bottom-right corner", new Point(W - 2, H - 2), HTBOTTOMRIGHT),
                        Tuple.Create("content", new Point(W / 2, H / 2), HTCLIENT),
                    };
                    foreach (var c in cases)
                    {
                        var s = f.PointToScreen(c.Item2);
                        int hit = (int)SendMessage(f.Handle, 0x0084, IntPtr.Zero, (IntPtr)((s.Y << 16) | (s.X & 0xFFFF)));
                        check(hit == c.Item3, "hit-test " + c.Item1, "got " + hit + ", want " + c.Item3);
                    }
                    int barHit = (int)SendMessage(f.Bar.Handle, 0x0084, IntPtr.Zero, IntPtr.Zero);
                    check(barHit == -1, "title bar answers HTTRANSPARENT", "got " + barHit);

                    var normal = f.Bounds;
                    f.WindowState = FormWindowState.Maximized; Pump(400);
                    var work = Screen.FromHandle(f.Handle).WorkingArea;
                    bool fits = Math.Abs(f.Bounds.Left - work.Left) <= 1 && Math.Abs(f.Bounds.Top - work.Top) <= 1
                                && Math.Abs(f.Bounds.Right - work.Right) <= 1 && Math.Abs(f.Bounds.Bottom - work.Bottom) <= 1;
                    check(fits, "maximised = work area (±1 px for an auto-hide taskbar)", f.Bounds + " vs " + work);
                    check(f.Padding == Padding.Empty, "no resize gutter while maximised", f.Padding.ToString());
                    f.WindowState = FormWindowState.Normal; Pump(400);
                    check(f.Bounds == normal, "restore returns to the same bounds", f.Bounds + " vs " + normal);
                    ProbeReview13(f, check, s => log.AppendLine("INFO  " + s));

                    var before = ThemeHelper.Palette.TitleBar;
                    ThemeHelper.SetAccent(Color.FromArgb(0x40, 0xA7, 0xE3)); Pump(100);
                    check(f.Bar.BackColor.ToArgb() == Color.FromArgb(0x40, 0xA7, 0xE3).ToArgb(), "accent change recolours the title bar live", f.Bar.BackColor.ToString());
                    ThemeHelper.SetAccent(null); Pump(100);
                    log.AppendLine("INFO  after maximise/restore: " + where());

                    ProbeDpiMove(f, check, s => log.AppendLine("INFO  " + s + ": " + where()));
                    ProbeCreateOnOtherDpi(check, s => log.AppendLine("INFO  " + s));
                    DialogProbe.Run(f, check, s => log.AppendLine("INFO  " + s));   // step 1.4: the dialogs' keyboard tests
                    TextBoxProbe.Run(check, s => log.AppendLine("INFO  " + s));      // step 1.5: ThemedTextBox's contract
                    SettingsProbe.Run(f, check, s => log.AppendLine("INFO  " + s));  // step 2.6: Settings' apply models
                    InputLabForm.RunProbe(f, check, s => log.AppendLine("INFO  " + s)); // device trip: the input lab measures
                }
                catch (Exception ex) { check(false, "probe threw", ex.ToString()); }
                f.Close();
            }
            log.AppendLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            // With a BOM: RT 8.1's Notepad reads a BOM-less file as ANSI, and this report is read on the device (G1).
            File.WriteAllText(file, log.ToString(), new UTF8Encoding(true));
            return fails;
        }

        /// <summary>Per-monitor only: move the window to a monitor with another DPI and back. Checks WM_DPICHANGED
        /// arrives, the window rescales (title bar height, the anchored button's gap), and the round trip lands on
        /// the original pixels — DpiScaler on REAL windows (step 1.1 could only test handle-less controls).</summary>
        private static void ProbeDpiMove(DesignGalleryForm f, Action<bool, string, string> check, Action<string> info)
        {
            if (!Dpi.Mode.StartsWith("per-monitor")) { check(true, "DPI move skipped (process is " + Dpi.Mode + "; run with TELEGARM_DPI=pmv1)", null); return; }
            int home = f.WindowDpiValue;
            Screen other = null; int otherDpi = 0;
            foreach (var s in Screen.AllScreens)
            {
                int d = DpiAt(s);
                if (d != home) { other = s; otherDpi = d; break; }
            }
            if (other == null) { check(true, "DPI move skipped (every monitor is at " + home + " DPI)", null); return; }

            var homeBounds = f.Bounds; var anchoredHome = f._anchored.Bounds; int barHome = f.Bar.Height;
            int gapR = f.Content.ClientSize.Width - f._anchored.Right, gapB = f.Content.ClientSize.Height - f._anchored.Bottom;
            var wa = other.WorkingArea;
            SetWindowPos(f.Handle, IntPtr.Zero, wa.Left + 60, wa.Top + 60, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Pump(800);
            info("on the " + otherDpi + "-DPI monitor");
            check(f.WindowDpiValue == otherDpi, "WM_DPICHANGED moved the window to " + otherDpi + " DPI", "window DPI " + f.WindowDpiValue);
            check(f.Bar.Height == Dpi.Px(f.Bar.Compact ? TitleBar.CompactHeight : TitleBar.FullHeight, otherDpi), "title bar height rescaled", f.Bar.Height + " px");
            int ngR = f.Content.ClientSize.Width - f._anchored.Right, ngB = f.Content.ClientSize.Height - f._anchored.Bottom;
            double r = otherDpi / (double)home;
            check(Math.Abs(ngR - gapR * r) <= 2 && Math.Abs(ngB - gapB * r) <= 2, "anchored Right|Bottom gaps rescaled",
                  gapR + "," + gapB + " -> " + ngR + "," + ngB + " (want x" + r.ToString("0.00") + ")");
            SetWindowPos(f.Handle, IntPtr.Zero, homeBounds.Left, homeBounds.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Pump(800);
            info("back home");
            check(f.WindowDpiValue == home, "moved back to " + home + " DPI", "window DPI " + f.WindowDpiValue);
            check(f.Bar.Height == barHome, "title bar height back exactly", f.Bar.Height + " vs " + barHome);
            check(f._anchored.Bounds == anchoredHome, "anchored button back on the same pixels", f._anchored.Bounds + " vs " + anchoredHome);
            check(f.Size == homeBounds.Size, "window size back exactly (Windows' suggested rectangles)", f.Size + " vs " + homeBounds.Size);
        }

        /// <summary>The review-1.3 defects, each on the real window: the gutter as the DEEPEST window sees it, restore keeping
        /// the place Windows chose, and maximise on a monitor other than the primary.</summary>
        private static void ProbeReview13(DesignGalleryForm f, Action<bool, string, string> check, Action<string> info)
        {
            // The gutter: WM_NCHITTEST goes to the deepest window under the point, so ask THAT one (a hit test sent
            // straight to the form, as above, cannot see a child stealing the edge).
            int e = f.Padding.Left, H = f.ClientSize.Height;
            check(e == f.Px(EdgeWidth), "the gutter is " + EdgeWidth + " logical px (" + (TouchCapable ? "touch-capable" : "no touch") + ")", "padding " + e + " px");
            int inEdge = DeepHit(f, new Point(e - 1, H / 2)), pastEdge = DeepHit(f, new Point(e + 1, H / 2));
            check(inEdge == HTLEFT, "the gutter's inner pixel is a resize edge for the window that is really there", "got " + inEdge);
            check(pastEdge == HTCLIENT, "one pixel past the gutter is content", "got " + pastEdge);

            // Restore keeps the place WINDOWS chose (Win+Shift+Arrow, a monitor change, a drag down from maximised).
            var normal = f.Bounds;
            f.WindowState = FormWindowState.Maximized; Pump(400);
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            GetWindowPlacement(f.Handle, ref wp);
            // Inside the work area (the window is only 48 px shorter than it here): this checks WHERE, not the
            // keep-reachable rule.
            wp.rcNormalPosition.Left += 120; wp.rcNormalPosition.Right += 120;
            wp.rcNormalPosition.Top -= 20; wp.rcNormalPosition.Bottom -= 20;
            wp.showCmd = SW_SHOWMAXIMIZED;
            SetWindowPlacement(f.Handle, ref wp); Pump(200);
            ShowWindow(f.Handle, SW_RESTORE); Pump(500);
            var want = new Point(normal.Left + 120, normal.Top - 20);
            check(f.Location == want && f.Size == normal.Size, "restore keeps the place Windows chose, and the normal size",
                  f.Bounds + " vs " + new Rectangle(want, normal.Size));
            SetWindowPos(f.Handle, IntPtr.Zero, normal.Left, normal.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); Pump(300);

            // Maximise on a monitor other than the primary: ITS work area, not the primary-sized track limit.
            Screen second = null;
            foreach (var sc in Screen.AllScreens) if (!sc.Primary) { second = sc; break; }
            if (second == null) { check(true, "secondary-monitor maximise skipped (one monitor)", null); return; }
            var home = f.Bounds;
            SetWindowPos(f.Handle, IntPtr.Zero, second.WorkingArea.Left + 60, second.WorkingArea.Top + 60, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Pump(800);
            f.WindowState = FormWindowState.Maximized; Pump(600);
            var wa = Screen.FromHandle(f.Handle).WorkingArea;
            RECT wr; GetWindowRect(f.Handle, out wr);
            bool fits = Math.Abs(wr.Left - wa.Left) <= 1 && Math.Abs(wr.Top - wa.Top) <= 1 && Math.Abs(wr.Right - wa.Right) <= 1 && Math.Abs(wr.Bottom - wa.Bottom) <= 1;
            check(fits, "maximised on the secondary monitor = ITS work area", Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom) + " vs " + wa);
            f.WindowState = FormWindowState.Normal; Pump(500);
            SetWindowPos(f.Handle, IntPtr.Zero, home.Left, home.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Pump(800);
            check(Math.Abs(f.Width - home.Width) <= 2 && Math.Abs(f.Height - home.Height) <= 2, "back home after that: the same size", f.Size + " vs " + home.Size);
            info("after the review-1.3 cases: " + f.Bounds + " dpi " + f.WindowDpiValue);
        }

        /// <summary>Per-monitor only: a window CREATED on a monitor with another DPI (the Resync path — no rectangle from
        /// Windows) comes out at its logical size there, content laid out to fit (review 1.3: it used to keep the
        /// creation-DPI pixel size, content clipped).</summary>
        private static void ProbeCreateOnOtherDpi(Action<bool, string, string> check, Action<string> info)
        {
            if (!Dpi.Mode.StartsWith("per-monitor")) { check(true, "create-on-other-DPI skipped (process is " + Dpi.Mode + ")", null); return; }
            Screen other = null; int otherDpi = 0;
            foreach (var sc in Screen.AllScreens)
            {
                int d = DpiAt(sc);
                if (d != Dpi.SystemDpi) { other = sc; otherDpi = d; break; }
            }
            if (other == null) { check(true, "create-on-other-DPI skipped (every monitor is at the system DPI)", null); return; }
            using (var g = new DesignGalleryForm())
            {
                g.StartPosition = FormStartPosition.Manual;
                g.Location = new Point(other.WorkingArea.Left + 60, other.WorkingArea.Top + 60);
                g.Show(); Pump(800);
                int d = g.WindowDpiValue;
                check(d == otherDpi, "a window created on the " + otherDpi + "-DPI monitor runs at its DPI", "window DPI " + d);
                var want = new Size(Dpi.Px(900, d), Dpi.Px(640, d));
                check(Math.Abs(g.Width - want.Width) <= 2 && Math.Abs(g.Height - want.Height) <= 2, "…and is its logical size at that DPI", g.Size + " vs " + want);
                int gapR = g.Content.ClientSize.Width - g._anchored.Right, gapB = g.Content.ClientSize.Height - g._anchored.Bottom;
                check(Math.Abs(gapR - Dpi.Px(AnchoredGapX, d)) <= 2 && Math.Abs(gapB - Dpi.Px(AnchoredGapY, d)) <= 2, "…with the anchored button at its logical gaps (content not clipped)",
                      gapR + "," + gapB + " vs " + Dpi.Px(AnchoredGapX, d) + "," + Dpi.Px(AnchoredGapY, d));
                info("created on the other monitor: " + g.Bounds + " dpi " + d);
                g.Close();
            }
        }

        /// <summary>WM_NCHITTEST as Windows really asks it: the deepest visible child under the point first, then its
        /// parents while they answer HTTRANSPARENT.</summary>
        private static int DeepHit(Control root, Point client)
        {
            var screen = root.PointToScreen(client);
            IntPtr h = root.Handle;
            while (true)
            {
                var pc = new POINT { X = screen.X, Y = screen.Y };
                ScreenToClient(h, ref pc);
                IntPtr child = ChildWindowFromPointEx(h, pc, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
                if (child == IntPtr.Zero || child == h) break;
                h = child;
            }
            var lp = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
            while (h != IntPtr.Zero)
            {
                int hit = (int)SendMessage(h, 0x0084, IntPtr.Zero, lp);
                if (hit != -1) return hit;
                if (h == root.Handle) return hit;
                h = GetParent(h);
            }
            return 0;
        }

        private static int DpiAt(Screen s)
        {
            try
            {
                var c = new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
                uint x, y;
                if (GetDpiForMonitor(MonitorFromPoint(c, 2), 0, out x, out y) == 0) return (int)x;
            }
            catch { }
            return Dpi.SystemDpi;
        }

        private static void Pump(int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount - end < 0) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }

        private const int SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int SW_SHOWMAXIMIZED = 3, SW_RESTORE = 9, CWP_SKIPINVISIBLE = 0x0001, CWP_SKIPTRANSPARENT = 0x0004;
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, int flags);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    }
}
