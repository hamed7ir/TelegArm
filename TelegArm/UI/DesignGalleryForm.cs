using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.3 — the design gallery: the first <see cref="AppForm"/>, and the place every Phase-1
    /// control is shown before any real window uses it (steps 1.4–1.6 add their controls here).
    ///   TelegArm.exe --gallery                 open it (storage isolated, like --shoot: no account is touched)
    ///   TelegArm.exe --gallery-probe out.txt   open it, run the chrome checks on a REAL window, write PASS/FAIL, exit
    /// Combine with TELEGARM_DPI=pmv1 to exercise per-monitor DPI, TELEGARM_ACCENT=#RRGGBB for accents, and
    /// TELEGARM_CHROME=native for the escape hatch. The --shoot rig also renders it.
    /// Step 3.2: a "Chat rows" section (the owner-drawn chat controls), which the probe measures across a DPI move.</summary>
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
        private readonly List<ChatKitRow> _chatKit = new List<ChatKitRow>();

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
            return AddChatRows(x, y);
        }

        // ── step 3.2: chat rows — the owner-drawn chat controls, for the per-window-DPI probe ─────────────────────

        /// <summary>Step 3.2 — one control of the "Chat rows" section. <c>Make</c> builds it ON THE PAGE at a location and a
        /// pixel width (ignored by a control that owns its size), at the window's current DPI; the probe calls it again for
        /// a fresh twin. <c>LogicalH</c>: the height it owns at 96 DPI, 0 when it is measured from its text. <c>Theme</c>:
        /// the palette it was built in (RichInfoLabel bakes its colours).</summary>
        private sealed class ChatKitRow
        {
            public string Name; public int LogicalH; public Func<Point, int, Control> Make; public Control Ctl; public string Theme;
        }

        /// <summary>Step 3.2 — the owner-drawn chat controls, built the way MainForm builds them (RenderChatList, CreateBubble,
        /// MakeServiceLine, the composer row, the reply-keyboard row), so the probe can measure them at every DPI.
        /// Returns the next free y.</summary>
        private int AddChatRows(int x, int y)
        {
            _page.Controls.Add(Header("Chat rows (owner-drawn, at the window's DPI)", x, y)); y += Px(30);

            // Fixed dates: the row's stamp (dd/MM/yy) and the bubbles' (HH:mm) are the same on every run of the rig.
            var date = new DateTime(2026, 3, 14, 9, 41, 0, DateTimeKind.Utc);
            var entry = new ChatEntry { PeerId = 7, Title = "Ada Lovelace", Preview = "The engine weaves algebraic patterns, as the loom weaves flowers", Date = date, UnreadCount = 3 };
            y += AddChatKit("chat row", 64, x, y, Px(360), (at, w) => OnPage(new ChatListItemControl(entry)
                { AccentColor = ThemeHelper.Palette.Accent, IsDark = ThemeHelper.Palette.Dark, Location = at, Width = w })).Height + Px(10);
            y += AddChatKit("bubble (Latin, incoming)", 0, x, y, Px(616), (at, w) => Bubble(
                "An incoming message long enough to wrap onto a second line, so the bubble's height comes from the text layout at the window's DPI.",
                "Ada Lovelace", false, date, at, w)).Height + Px(6);
            y += AddChatKit("bubble (Persian, outgoing)", 0, x, y, Px(616), (at, w) => Bubble(
                "سلام! این یک پیام فارسی است؛ اندازه‌اش باید در هر DPI درست بماند.", null, true, date, at, w)).Height + Px(6);
            y += AddChatKit("service line", 30, x, y, Px(616), (at, w) => OnPage(new ServiceLineControl("Ada Lovelace joined the group")
                { IsDark = ThemeHelper.Palette.Dark, Location = at, Width = w })).Height + Px(10);

            // The composer's 40 px cells, and the floating proxy pill: Max(34, caption + 16) high — the floor wins at 9 pt.
            int rowH = AddChatKit("attach button", 40, x, y, 0, (at, w) => OnPage(new AttachButton { Location = at })).Height;
            rowH = Math.Max(rowH, AddChatKit("mic button", 40, x + Px(48), y, 0, (at, w) => OnPage(new MicButton { Location = at })).Height);
            rowH = Math.Max(rowH, AddChatKit("send button", 40, x + Px(96), y, 0, (at, w) => OnPage(new SendButton { Location = at })).Height);
            rowH = Math.Max(rowH, AddChatKit("proxy pill (floating)", 34, x + Px(160), y, 0, (at, w) => OnPage(new ProxyStatusPill
                { IsDark = ThemeHelper.Palette.Dark, AccentColor = ThemeHelper.Palette.Accent, Location = at })).Height);
            y += rowH + Px(10);

            y += AddChatKit("reply keyboard", 74, x, y, Px(400), ReplyKeyboard).Height + Px(10);   // 74 = 6 + 22 + one row of (40 + 6)
            y += AddChatKit("rich info label", 0, x, y, Px(400), RichLabel).Height + Px(18);
            return y;
        }

        private Control AddChatKit(string name, int logicalH, int x, int y, int w, Func<Point, int, Control> make)
        {
            var k = new ChatKitRow { Name = name, LogicalH = logicalH, Make = make, Ctl = make(new Point(x, y), w), Theme = ThemeKey(ThemeHelper.Palette) };
            _chatKit.Add(k);
            return k.Ctl;
        }

        private T OnPage<T>(T c) where T : Control { _page.Controls.Add(c); return c; }

        private static string ThemeKey(Palette p) { return (p.Dark ? "dark|" : "light|") + p.Accent.ToArgb(); }

        /// <summary>As MainForm.CreateBubble, but measured AFTER it is added: the window's DPI is known only once parented.</summary>
        private Control Bubble(string text, string sender, bool outgoing, DateTime date, Point at, int w)
        {
            var p = ThemeHelper.Palette;
            var b = new MessageBubbleControl(text, sender, outgoing, date) { AccentColor = p.Accent, IsDark = p.Dark, Location = at, Width = w, Read = outgoing };
            b.SetEntities(null);   // unconditionally, as CreateBubble does (the bubble decides on the inline engine itself)
            OnPage(b);
            b.Measure();
            return b;
        }

        /// <summary>One row of two buttons. The gallery is its host: it SETS the height from DesiredHeight (as MainForm's
        /// SyncReplyKeyboardHeight sets its row) — here, after a collapse toggle, and after a DPI change (OnDpiChanged).</summary>
        private Control ReplyKeyboard(Point at, int w)
        {
            var p = ThemeHelper.Palette;
            var kb = new ReplyKeyboardControl { IsDark = p.Dark, AccentColor = p.Accent, Location = at, Width = w };
            kb.SetButtons(new List<List<ReplyKeyboardControl.RKButton>>
            {
                new List<ReplyKeyboardControl.RKButton>
                {
                    new ReplyKeyboardControl.RKButton { Label = "Yes, continue", Kind = ReplyKeyboardControl.RKKind.Text },
                    new ReplyKeyboardControl.RKButton { Label = "Not now", Kind = ReplyKeyboardControl.RKKind.Text }
                }
            });
            OnPage(kb);
            kb.Height = kb.DesiredHeight;
            kb.ToggleChanged += (s, e) => kb.Height = kb.DesiredHeight;
            return kb;
        }

        /// <summary>A profile description (ProfileForm.NewDetails): a link, a mention and a hashtag, wrapping to a second line.
        /// Its font is the SHARED one for the window's DPI — never disposed (DpiScaler swaps it on a DPI change).</summary>
        private Control RichLabel(Point at, int w)
        {
            var p = ThemeHelper.Palette;
            const string text = "TelegArm, a Telegram client for Windows RT 8.1: https://github.com/hamed7ir/TelegArm #telegarm @example. Long enough to wrap, so its height follows the font at the window's DPI.";
            var r = new RichInfoLabel(p.Dark, p.Accent, FontHelper.UiAt(9.75f, WindowDpiValue)) { Location = at, Width = w };
            OnPage(r);
            r.SetText(text, TextEntities.Detect(text), null);
            return r;
        }

        /// <summary>Step 3.2 — the chat-rows kit at <paramref name="dpi"/>: each control is as tall as a TWIN built fresh at that DPI
        /// with the same width (a moved control must equal a new one: ctor + OnParentChanged against OnDpiChanged + the host's
        /// relayout) and, when it owns a fixed height, Px(logical, dpi). Both within 1 px. One check per control.</summary>
        private void CheckChatKitAt(int dpi, string where, Action<bool, string, string> check, Action<string> info)
        {
            var seen = new StringBuilder();
            foreach (var k in _chatKit)
            {
                var c = k.Ctl;
                int th;
                try
                {
                    var twin = k.Make(c.Location, c.Width);
                    th = twin.Height;
                    _page.Controls.Remove(twin); twin.Dispose();
                }
                catch (Exception ex) { check(false, where + ": " + k.Name + " built fresh", ex.GetType().Name + ": " + ex.Message); continue; }   // a throwing ctor is the v1.9.0 crash
                int want = k.LogicalH > 0 ? Dpi.Px(k.LogicalH, dpi) : th;
                check(Math.Abs(c.Height - th) <= 1 && Math.Abs(c.Height - want) <= 1, where + ": " + k.Name + " height",
                      c.Height + " px; built fresh " + th + (k.LogicalH > 0 ? "; Px(" + k.LogicalH + ") " + want : ""));
                seen.Append(seen.Length > 0 ? ", " : "").Append(k.Name).Append(' ').Append(c.Width).Append('x').Append(c.Height);
            }
            info(where + ": " + seen);
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

        /// <summary>Step 3.2: as the chat-rows kit's HOST the gallery does what MainForm's relayout does after a DPI change —
        /// measures the bubbles again (MessageBubbleControl leaves that to its host) and SETS the reply keyboard's height from
        /// DesiredHeight. Never a ratio of what is there: DpiScaler has already scaled the bounds.</summary>
        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            foreach (var k in _chatKit)
            {
                if (k.Ctl is MessageBubbleControl b) b.Measure();
                else if (k.Ctl is ReplyKeyboardControl kb && kb.HasButtons) kb.Height = kb.DesiredHeight;
            }
        }

        private void Recolor()
        {
            var p = ThemeHelper.Palette;
            foreach (var b in _onAccent) { b.BarColor = p.TitleBar; b.Invalidate(); }
            foreach (var b in _onCard) { b.BarColor = p.Card; b.GlyphColor = p.Text; b.Invalidate(); }
            RecolorTree(Content, p);
            RecolorChatKit(p);
            foreach (var b in new[] { _pageBar }.Concat(_demoBars)) if (b != null) { b.IsDark = p.Dark; b.AccentColor = p.Accent; b.Invalidate(); }
            _anchored.BackColor = p.Card; _anchored.ForeColor = p.Text;
            _info.Text = "DPI " + WindowDpiValue + " (" + Dpi.Mode + ", system " + Dpi.SystemDpi + ")   chrome: "
                         + (CustomChrome ? "custom" : "native") + " (" + ChromeReason + ")   accent " + ColorMath.Hex(p.Accent)
                         + (ThemeHelper.AccentOverride.HasValue ? " (fixed)" : " (Windows)");
            _swatches.Invalidate();
        }

        /// <summary>Step 3.2: the chat-rows kit takes the palette the way MainForm hands it (IsDark / AccentColor); the composer
        /// buttons follow ThemeHelper by themselves. RichInfoLabel bakes its colours when built, so it is rebuilt in place, as
        /// ProfileForm rebuilds its description.</summary>
        private void RecolorChatKit(Palette p)
        {
            string theme = ThemeKey(p);
            foreach (var k in _chatKit)
            {
                var c = k.Ctl;
                if (c is RichInfoLabel)
                {
                    if (k.Theme == theme) continue;
                    k.Ctl = k.Make(c.Location, c.Width); k.Theme = theme;
                    _page.Controls.Remove(c); c.Dispose();
                    continue;
                }
                if (c is ChatListItemControl row) { row.IsDark = p.Dark; row.AccentColor = p.Accent; }
                else if (c is MessageBubbleControl b) { b.IsDark = p.Dark; b.AccentColor = p.Accent; }
                else if (c is ServiceLineControl sl) sl.IsDark = p.Dark;
                else if (c is ProxyStatusPill pill) { pill.IsDark = p.Dark; pill.AccentColor = p.Accent; }
                else if (c is ReplyKeyboardControl kb) { kb.IsDark = p.Dark; kb.AccentColor = p.Accent; }
                c.Invalidate();
            }
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

                    f.CheckChatKitAt(f.WindowDpiValue, "chat rows at " + f.WindowDpiValue + " DPI", check, s => log.AppendLine("INFO  " + s));   // step 3.2, every mode
                    ProbeDpiMove(f, check, s => log.AppendLine("INFO  " + s + ": " + where()), s => log.AppendLine("INFO  " + s));
                    ProbeLiveScale(f, check, s => log.AppendLine("INFO  " + s + ": " + where()), s => log.AppendLine("INFO  " + s));   // step 3.8b: the interface scale, live and back
                    ProbeCreateOnOtherDpi(check, s => log.AppendLine("INFO  " + s));
                    ProbeKeyboards(check);   // step 4.1: WTC 4.4.9's button types
                    check(TelegramService.CanRaiseOwnUpdates, "WTC: Client.RaiseOwnUpdates(UpdatesBase) exists (a join's own updates reach the UpdateManager)", null);   // step 4.1
                    DpiOverlay.RunProbe(f, check, s => log.AppendLine("INFO  " + s), Path.ChangeExtension(file, ".overlay.png"));   // step 3.9
                    DialogProbe.Run(f, check, s => log.AppendLine("INFO  " + s));   // step 1.4: the dialogs' keyboard tests
                    TextBoxProbe.Run(check, s => log.AppendLine("INFO  " + s));      // step 1.5: ThemedTextBox's contract
                    SettingsProbe.Run(f, check, s => log.AppendLine("INFO  " + s));  // step 2.6: Settings' apply models
                    InputLabForm.RunProbe(f, check, s => log.AppendLine("INFO  " + s)); // device trip: the input lab measures
                    ComposerEditorForm.RunProbe(f, check, s => log.AppendLine("INFO  " + s)); // step 2.7d: the editor window
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
        /// the original pixels — DpiScaler on REAL windows (step 1.1 could only test handle-less controls).
        /// Step 3.2: the chat-rows kit too — on the other monitor each control is as tall as one built fresh there (and
        /// Px(logical) when it owns a fixed height); back home, every one is on its exact pixels.
        /// <paramref name="note"/> is a plain INFO line (<paramref name="info"/> appends the anchored button's place).</summary>
        private static void ProbeDpiMove(DesignGalleryForm f, Action<bool, string, string> check, Action<string> info, Action<string> note)
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
            var kitHome = f._chatKit.Select(k => k.Ctl.Bounds).ToArray();   // step 3.2: bounds (so the bubbles' measured heights too)
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
            f.CheckChatKitAt(otherDpi, "chat rows on the " + otherDpi + "-DPI monitor", check, note);
            SetWindowPos(f.Handle, IntPtr.Zero, homeBounds.Left, homeBounds.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Pump(800);
            info("back home");
            check(f.WindowDpiValue == home, "moved back to " + home + " DPI", "window DPI " + f.WindowDpiValue);
            check(f.Bar.Height == barHome, "title bar height back exactly", f.Bar.Height + " vs " + barHome);
            check(f._anchored.Bounds == anchoredHome, "anchored button back on the same pixels", f._anchored.Bounds + " vs " + anchoredHome);
            check(f.Size == homeBounds.Size, "window size back exactly (Windows' suggested rectangles)", f.Size + " vs " + homeBounds.Size);
            for (int i = 0; i < kitHome.Length && i < f._chatKit.Count; i++)
                check(f._chatKit[i].Ctl.Bounds == kitHome[i], "chat rows back home: " + f._chatKit[i].Name + " on the same pixels",
                      f._chatKit[i].Ctl.Bounds + " vs " + kitHome[i]);
        }

        /// <summary>Step 4.1: WTelegramClient 4.4.9 rewrote the keyboard classes (one button class, the kind in its type). Every
        /// button type through both mappings, with the fields TelegArm acts on.</summary>
        private static void ProbeKeyboards(Action<bool, string, string> check)
        {
            Func<string, TL.InlineButtonType, TL.KeyboardInlineButton> ib = (text, type) => new TL.KeyboardInlineButton { text = text, type = type };
            var inline = new TL.ReplyInlineMarkup
            {
                rows = new[]
                {
                    new TL.KeyboardInlineButtonRow { buttons = new[]
                    {
                        ib("cb", new TL.InlineButtonTypeCallback { data = new byte[] { 7, 9 } }),
                        ib("url", new TL.InlineButtonTypeUrl { url = "https://t.me/x" }),
                        ib("si", new TL.InlineButtonTypeSwitchInline { query = "q", flags = TL.InlineButtonTypeSwitchInline.Flags.same_peer }),
                        null,
                    } },
                    new TL.KeyboardInlineButtonRow { buttons = null },
                    new TL.KeyboardInlineButtonRow { buttons = new[]
                    {
                        ib("auth", new TL.InlineButtonTypeUrlAuth { url = "https://a.example" }),
                        ib("game", new TL.InlineButtonTypeGame()),
                        ib("off", new TL.InlineButtonTypeDisabled()),
                    } },
                },
            };
            var rows = MainForm.InlineKeyboardRows(inline);
            var got = string.Join(" | ", rows.Select(r => string.Join(",", r.Select(v => v.Label + "=" + v.Kind))));
            check(got == "cb=Callback,url=Url,si=SwitchInline | auth=UrlAuth,game=Unsupported,off=Unsupported",
                  "inline keyboard (4.4.9): every button type maps, empty rows and null buttons skipped", got);
            var c = rows[0];
            check(c[0].Data != null && c[0].Data.Length == 2 && c[0].Data[1] == 9 && c[1].Url == "https://t.me/x" && c[2].Query == "q" && c[2].SamePeer
                  && rows[1][0].Url == "https://a.example", "inline keyboard (4.4.9): data, url, query, same_peer carried", null);

            Func<string, TL.ButtonType, TL.KeyboardButton> rb = (text, type) => new TL.KeyboardButton { text = text, type = type };
            var reply = new TL.ReplyKeyboardMarkup
            {
                rows = new[]
                {
                    new TL.KeyboardButtonRow { buttons = new[] { rb("hi", null), rb("phone", new TL.ButtonTypeRequestPhone()), rb("geo", new TL.ButtonTypeRequestGeoLocation()) } },
                    new TL.KeyboardButtonRow { buttons = new[] { rb("peer", new TL.ButtonTypeRequestPeer()), rb("poll", new TL.ButtonTypeRequestPoll()), rb("web", new TL.ButtonTypeSimpleWebView { url = "https://w" }) } },
                },
            };
            var rr = MainForm.ReplyKeyboardRows(reply);
            got = string.Join(" | ", rr.Select(r => string.Join(",", r.Select(v => v.Label + "=" + v.Kind))));
            check(got == "hi=Text,phone=RequestPhone,geo=RequestGeo | peer=RequestPeer,poll=RequestPoll,web=WebView",
                  "reply keyboard (4.4.9): a null type is text, every request type maps", got);
        }

        /// <summary>Step 3.8b: the interface scale applied LIVE and back. The window is first sized to fit the bigger scale (so
        /// the work area cannot clamp the round trip); it must rescale through the monitor-move path, the title bar and the
        /// chat-row kit with it, and come back to the same pixels.</summary>
        private static void ProbeLiveScale(DesignGalleryForm f, Action<bool, string, string> check, Action<string> info, Action<string> note)
        {
            int old = Dpi.UserScalePercent;
            var homeBounds = f.Bounds;
            try
            {
                f.Size = new Size(f.Px(560), f.Px(420));
                Pump(300);
                var wa = Screen.FromHandle(f.Handle).WorkingArea;
                int cap = Dpi.FitCap(wa, f.MonitorDpiValue);
                if (cap < 125) { check(true, "live scale skipped (this screen is capped at 100%)", null); return; }
                int target = Math.Min(150, cap);
                var before = f.Bounds; int home = f.WindowDpiValue, barHome = f.Bar.Height;
                var kitHome = f._chatKit.Select(k => k.Ctl.Bounds).ToArray();

                AppForm.ApplyInterfaceScale(target);
                Pump(600);
                int want = Dpi.EffectiveOn(Screen.FromHandle(f.Handle).WorkingArea, f.MonitorDpiValue);
                info("interface scale " + target + "% (effective " + want + " DPI)");
                check(f.WindowDpiValue == want, "interface scale " + target + "%: the window rescaled live", "dpi " + f.WindowDpiValue + ", want " + want);
                check(f.Bar.Height == Dpi.Px(f.Bar.Compact ? TitleBar.CompactHeight : TitleBar.FullHeight, want), "title bar at the new scale", f.Bar.Height + " px");
                f.CheckChatKitAt(want, "chat rows at " + target + "%", check, note);

                AppForm.ApplyInterfaceScale(old);
                Pump(600);
                check(f.WindowDpiValue == home, "back at " + old + "%: the window's DPI", f.WindowDpiValue + " vs " + home);
                check(f.Size == before.Size, "back at " + old + "%: the window size exactly", f.Size + " vs " + before.Size);
                check(f.Bar.Height == barHome, "back at " + old + "%: the title bar", f.Bar.Height + " vs " + barHome);
                for (int i = 0; i < kitHome.Length && i < f._chatKit.Count; i++)
                    check(f._chatKit[i].Ctl.Bounds == kitHome[i], "back at " + old + "%: " + f._chatKit[i].Name + " on the same pixels",
                          f._chatKit[i].Ctl.Bounds + " vs " + kitHome[i]);

                // G3: a live change while MAXIMISED — rescaled in place, still maximised; restore comes back at the old size.
                f.WindowState = FormWindowState.Maximized; Pump(400);
                AppForm.ApplyInterfaceScale(target); Pump(600);
                check(f.WindowDpiValue == want && f.WindowState == FormWindowState.Maximized, "maximised, " + target + "%: rescaled live, still maximised",
                      f.WindowDpiValue + " " + f.WindowState);
                check(f.Bar.Height == Dpi.Px(f.Bar.Compact ? TitleBar.CompactHeight : TitleBar.FullHeight, want), "maximised, " + target + "%: the title bar", f.Bar.Height + " px");
                AppForm.ApplyInterfaceScale(old); Pump(600);
                f.WindowState = FormWindowState.Normal; Pump(500);
                check(f.WindowDpiValue == home && f.Size == before.Size, "maximised round trip: restores at " + old + "% and the same size",
                      f.WindowDpiValue + " " + f.Size + " vs " + before.Size);

                // G3: a live change while MINIMISED — nothing until it is restored; then it lands at the new scale.
                f.WindowState = FormWindowState.Minimized; Pump(400);
                AppForm.ApplyInterfaceScale(target); Pump(400);
                check(f.WindowDpiValue == home, "minimised, " + target + "%: the change waits for the restore", "dpi " + f.WindowDpiValue);
                f.WindowState = FormWindowState.Normal; Pump(600);
                var scaled = new Size((int)Math.Round(before.Width * (double)want / home), (int)Math.Round(before.Height * (double)want / home));
                check(f.WindowDpiValue == want, "restored: it lands at " + target + "%", "dpi " + f.WindowDpiValue + ", want " + want);
                check(Math.Abs(f.Width - scaled.Width) <= 1 && Math.Abs(f.Height - scaled.Height) <= 1, "restored: its normal size rescaled with it (±1 px)",
                      f.Size + " vs " + scaled);
                AppForm.ApplyInterfaceScale(old); Pump(600);
                check(f.WindowDpiValue == home && Math.Abs(f.Width - before.Width) <= 1 && Math.Abs(f.Height - before.Height) <= 1,
                      "and back at " + old + "%: the same size (±1 px)", f.WindowDpiValue + " " + f.Size + " vs " + before.Size);
            }
            finally
            {
                if (Dpi.UserScalePercent != old) AppForm.ApplyInterfaceScale(old);
                f.Bounds = homeBounds;
                Pump(300);
            }
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
                g.CheckChatKitAt(d, "chat rows in a window created at " + d + " DPI", check, info);   // step 3.2: the Resync path
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
