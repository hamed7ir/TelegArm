using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    // Redesign step 1.6 — the settings row kit, ported from DroidFinestra's SettingRows (SectionHeader, ToggleRow,
    // ChoiceRow, TextRow, NoteRow, OutputRow) and extended with SettingGroup (the card a settings page is a column of)
    // and SegmentedRow / StepperRow (TelegArm's private Segmented3 and Stepper from SettingsForm, promoted).
    //
    // WHAT IS DIFFERENT FROM DF, ON PURPOSE
    //  · Colours are Palette tokens read from the cached ThemeHelper.Palette at paint time — never the registry, never a
    //    literal. DF read ThemeHelper.IsDark / GetWindowsAccentColor() in every paint.
    //  · Every length is LOGICAL (96 dpi) and becomes pixels at the DPI of the row's window when it is laid out or
    //    painted. A row never stores a font in Font: DpiScaler's generic pass rescales an explicit Font, so a font kept
    //    there would be scaled twice. Fonts are resolved per paint from FontHelper's shared cache (never disposed).
    //  · Nothing is drawn with a font symbol (tofu on RT 8.1): the switch, the chevron, the -/+ and the focus ring are
    //    GDI+ shapes.
    //  · No row reads Control.Visible to decide a layout (false for every child of a form that is not shown yet).
    //    SettingRow.RowVisible is the row's own flag, and SettingGroup stacks by it.
    //  · A row never carries Control.Font or ForeColor: those are ignored. Caption, ink and size are the row's.

    /// <summary>The base of every settings row: an opaque, owner-drawn strip with a caption, a logical height, the
    /// shared horizontal padding (<see cref="PadX"/>) and the palette background. It keeps its height right by itself:
    /// <see cref="HeightFor"/> says what the row needs at a width and DPI, and the row refits when it is parented, when
    /// its width changes, when its caption changes and when the window's DPI changes (IDpiAware, after DpiScaler's
    /// generic pass has scaled its bounds — so the exact height is re-derived, never scaled again).
    ///
    /// ★ BACKGROUND. A row clears to the nearest opaque BackColor above it (a <see cref="SettingGroup"/> keeps its own
    ///   BackColor equal to Palette.Card), or Palette.Window with no parent. The first and last row of a group paint
    ///   their slice of the card's rounded outline instead, so the rounded corners are not squared off by the rows.
    /// ★ KEYBOARD. Interactive rows are Selectable tab stops and draw an accent focus ring (only when Windows asks for
    ///   focus cues, i.e. after the keyboard was used). They take Space, and the arrow keys where that makes sense;
    ///   Enter is left alone so a dialog's AcceptButton keeps it.
    /// ★ TOUCH. StandardDoubleClick is off: two quick taps are two clicks. Otherwise the second tap of a fast +/+ on a
    ///   stepper arrives as a DoubleClick and is lost (the old private Stepper had that flaw).
    /// ★ ACCESSIBILITY. AccessibleName follows the caption (unless the caller set its own); AccessibleRole is set per
    ///   row; the accessible object exposes the row's value and checked state and a default action where there is one.
    /// ★ THEME. A row listens to ThemeHelper.ThemeChanged only while it has a window handle (a row that is built but
    ///   never shown is not pinned by a static event) and unhooks when the handle goes and in Dispose.</summary>
    public abstract class SettingRow : Control, IDpiAware
    {
        /// <summary>The height of an ordinary row, in logical pixels.</summary>
        public const int DefaultHeight = 52;

        /// <summary>The horizontal padding of the row's content, in logical pixels (captions, controls, dividers).</summary>
        public const int PadX = 16;

        /// <summary>The size of a row's caption and value text, in logical points.</summary>
        protected const float CaptionPt = 10.5f;

        // NoPadding: GDI otherwise insets text by a sixth of an em on each side, which makes measure and draw disagree
        // with the shapes drawn beside it. Measure and draw use the same flags everywhere in this file.
        protected const TextFormatFlags BaseFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private string _caption, _autoName;
        private int _fitWidth = -1;
        private bool _hooked, _rowVisible = true;

        /// <summary>Set by SettingGroup: this row is the first or last one shown, so it paints the rounded outline.</summary>
        internal bool CardEnd;

        protected SettingRow(string caption, bool interactive)
        {
            _caption = caption ?? "";
            _autoName = _caption;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // two quick taps are two clicks (see the class notes)
            SetStyle(ControlStyles.Selectable, interactive);
            TabStop = interactive;
            if (interactive) Cursor = Cursors.Hand;
            AccessibleName = _caption;
            // ⚠ Nothing virtual is called here: a derived class has not run its constructor yet. Each derived
            //   constructor ends with Refit(), which sets the real height once its own fields exist.
        }

        // A 96-dpi starting size; the real height comes from Refit() once the row has a parent (and so a DPI), and the
        // width from whoever stacks the row (SettingGroup, DialogForm.Restack, a docked page).
        private const int DefaultWidth = 300;
        protected override Size DefaultSize { get { return new Size(DefaultWidth, DefaultHeight); } }

        // ── the caption ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The caption. Changing it re-measures the row (NoteRow wraps), repaints and updates AccessibleName.</summary>
        public override string Text
        {
            get { return _caption; }
            set
            {
                value = value ?? "";
                if (value == _caption) return;
                _caption = value;
                if (string.IsNullOrEmpty(AccessibleName) || AccessibleName == _autoName) { _autoName = value; AccessibleName = value; }
                OnCaptionChanged();
                OnTextChanged(EventArgs.Empty);
            }
        }

        /// <summary>The caption (or, for NoteRow, the text) changed: refit and repaint. Derived rows must stay null-safe —
        /// this can run before their own fields are set only if they call the setter from their constructor.</summary>
        protected virtual void OnCaptionChanged()
        {
            _fitWidth = -1;
            if (!Dpi.Busy) Refit();
            Invalidate();
        }

        // ── size ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The height this row needs at outer width <paramref name="width"/> on a <paramref name="dpi"/> display.
        /// Fixed rows return their logical height in pixels; rows whose height follows their width (NoteRow, a stacked
        /// SegmentedRow) measure. Also lets a page size a dialog before it is shown.</summary>
        public virtual int HeightFor(int width, int dpi) { return Dpi.Px(DefaultHeight, dpi); }

        /// <summary>Sets Height to <see cref="HeightFor"/> at the DPI of the row's window. SettingGroup calls it on every
        /// stack; a row calls it itself when it is parented, resized, re-captioned or its DPI changes.</summary>
        internal void Refit() { RefitAt(Dpi.Of(this)); }

        private void RefitAt(int dpi)
        {
            _fitWidth = Width;
            int h = HeightFor(Width, dpi);
            if (h > 0 && Height != h) Height = h;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Skipped while DpiScaler runs (Dpi.Busy): the bounds are interim and IDpiAware.OnDpiChanged fits once at the
            // end. _fitWidth also ends the loop a Height change would otherwise start.
            if (Width != _fitWidth && !Dpi.Busy) Refit();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            _fitWidth = -1;
            Refit();   // the window, and so the DPI, may be a different one now
            Invalidate();
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _fitWidth = -1;
            OnDpiRescaled(oldDpi, newDpi);
            RefitAt(newDpi);
            PerformLayout();
            Invalidate();
        }

        /// <summary>The window's DPI changed. Bounds, Padding and any explicit Font of this row and its children have
        /// ALREADY been rescaled by DpiScaler's generic pass — re-derive only what the row computes itself (an inner
        /// text box's font, say). The height is refitted after this returns.</summary>
        protected virtual void OnDpiRescaled(int oldDpi, int newDpi) { }

        // ── visibility, as the row's own flag ───────────────────────────────────────────────────────────────

        /// <summary>Show or hide the row INSIDE a SettingGroup. Use this, not Visible: Visible reads false for every child
        /// of a form that is not shown yet, so a group could never tell "hidden" from "not shown yet". The group stacks
        /// by this flag and closes the gap.</summary>
        public bool RowVisible
        {
            get { return _rowVisible; }
            set
            {
                if (_rowVisible == value) return;
                _rowVisible = value;
                Visible = value;
                var g = Parent as SettingGroup;
                if (g != null) g.RequestRestack();
            }
        }

        // ── painting helpers ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The colour the row sits on: the nearest opaque BackColor above it, else the palette's window colour.</summary>
        protected Color Backdrop(Palette p)
        {
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return p.Window;
        }

        /// <summary>Clears the row. The first and last row of a SettingGroup paint their slice of the card instead
        /// (outer background, rounded card fill and outline) so the corners stay round.</summary>
        protected void PaintBack(Graphics g, Palette p)
        {
            var grp = Parent as SettingGroup;
            if (grp != null && CardEnd) grp.PaintSlice(g, this, p);
            else g.Clear(Backdrop(p));
        }

        /// <summary>True when the row's text and controls are drawn dimmed. TextRow overrides it: it is never really
        /// disabled (see there).</summary>
        protected virtual bool Dimmed { get { return !Enabled; } }

        protected static int LineHeight(Font font)
        {
            return TextRenderer.MeasureText("Ag", font, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Height;
        }

        /// <summary>One line of text in <paramref name="r"/>, vertically centred. Colour emoji go through EmojiRenderer
        /// (the Noto images — the same on Windows 11 and RT 8.1, which has no astral glyphs; clipped, not ellipsized);
        /// everything else through TextRenderer. Persian text is read right-to-left, without touching RightToLeft or
        /// RightToLeftLayout (which mirror every child and have broken RT input before).</summary>
        protected static void DrawTextLine(Graphics g, string text, Font font, Color ink, Rectangle r, bool alignRight, bool ellipsis)
        {
            if (string.IsNullOrEmpty(text) || r.Width < 1 || r.Height < 1) return;
            if (EmojiRenderer.ContainsEmoji(text))
            {
                try
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    int x = r.X;
                    if (alignRight)
                    {
                        int w = EmojiRenderer.MeasureLineWidth(g, text, font);
                        if (w < r.Width) x = r.Right - w;
                    }
                    EmojiRenderer.DrawLine(g, text, font, ink, new Rectangle(x, r.Y, r.Right - x, r.Height));
                    return;
                }
                catch (Exception) { /* fall through to plain text rather than leave the row blank */ }
            }
            var flags = BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                        | (alignRight ? TextFormatFlags.Right : TextFormatFlags.Left);
            if (ellipsis) flags |= TextFormatFlags.EndEllipsis;
            if (FontHelper.IsPersian(text)) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(g, text, font, r, ink, flags);
        }

        /// <summary>The caption, left-aligned and vertically centred in <paramref name="r"/>.</summary>
        protected void DrawCaption(Graphics g, Palette p, Rectangle r, int dpi)
        {
            DrawTextLine(g, Text, FontHelper.ForAt(Text, CaptionPt, dpi), Dimmed ? p.TextDisabled : p.Text, r, false, true);
        }

        /// <summary>The accent focus ring: a rounded outline a few pixels outside <paramref name="target"/>, drawn only
        /// while the row has focus AND Windows wants focus cues (after the keyboard was used).</summary>
        protected void PaintFocusRing(Graphics g, Palette p, Rectangle target, int radius, int dpi)
        {
            if (!Focused || !ShowFocusCues) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int grow = Dpi.Px(3, dpi);
            var r = Rectangle.Inflate(target, grow, grow);
            using (var path = DrawHelper.RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius + grow))
            using (var pen = new Pen(p.Accent, Dpi.F(1.5f, dpi)))
                g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // ── theme ────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_hooked) { _hooked = true; ThemeHelper.ThemeChanged += OnThemeChangedCore; }
            _fitWidth = -1;
            Refit();   // the window, and so the DPI, is known now
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Unhook();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Unhook();
            base.Dispose(disposing);
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            ThemeHelper.ThemeChanged -= OnThemeChangedCore;
        }

        private void OnThemeChangedCore()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!InvokeRequired) { OnThemeApplied(); return; }
            try { BeginInvoke((Action)OnThemeApplied); } catch (InvalidOperationException) { }   // the window went away meanwhile
        }

        /// <summary>The palette changed (it is already rebuilt). Colours are read at paint time, so the default only
        /// repaints; rows that host a text box also recolour it.</summary>
        protected virtual void OnThemeApplied() { if (!IsDisposed) Invalidate(); }

        // ── accessibility ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The value a screen reader announces after the name (the selected option, the number); null = none.</summary>
        protected virtual string AccessibleValueText { get { return null; } }

        /// <summary>The checked state of a switch; null = not a checkable row.</summary>
        protected virtual bool? AccessibleChecked { get { return null; } }

        /// <summary>The verb of the default action ("Toggle", "Open"); null = the row has none.</summary>
        protected virtual string AccessibleAction { get { return null; } }

        /// <summary>Performs the default action (what a screen reader's "click" does).</summary>
        protected virtual void AccessibleInvoke() { }

        protected override AccessibleObject CreateAccessibilityInstance() { return new RowAccessible(this); }

        private sealed class RowAccessible : Control.ControlAccessibleObject
        {
            private readonly SettingRow _row;
            public RowAccessible(SettingRow row) : base(row) { _row = row; }

            public override string Value
            {
                get { return _row.AccessibleValueText ?? base.Value; }
                set { base.Value = value; }
            }

            public override AccessibleStates State
            {
                get
                {
                    var s = base.State;
                    if (_row.AccessibleChecked == true) s |= AccessibleStates.Checked;
                    return s;
                }
            }

            public override string DefaultAction { get { return _row.AccessibleAction ?? base.DefaultAction; } }

            public override void DoDefaultAction()
            {
                if (_row.AccessibleAction != null) { if (_row.Enabled) _row.AccessibleInvoke(); }
                else base.DoDefaultAction();
            }
        }
    }

    /// <summary>A section title that groups the cards under it. Bold, in the accent ink (readable on the page), aligned
    /// with the captions inside the cards below, bottom-aligned so the gap above it separates it from the previous
    /// section. <see cref="Underline"/> adds DroidFinestra's hairline for pages that have no cards.</summary>
    public sealed class SectionHeader : SettingRow
    {
        private const int HeaderHeight = 36, BandHeight = 24, BottomPad = 6;
        private bool _underline;

        public SectionHeader(string caption) : base(caption, false)
        {
            AccessibleRole = AccessibleRole.StaticText;
            Refit();
        }

        /// <summary>A Divider-coloured hairline under the text (DF's look). Off by default: a card below is edge enough.</summary>
        public bool Underline
        {
            get { return _underline; }
            set { if (_underline == value) return; _underline = value; Invalidate(); }
        }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(HeaderHeight, dpi); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            g.Clear(Backdrop(p));
            int band = Dpi.Px(BandHeight, dpi);
            var r = new Rectangle(padX, Height - Dpi.Px(BottomPad, dpi) - band, Math.Max(1, Width - 2 * padX), band);
            DrawTextLine(g, Text, FontHelper.ForAt(Text, 11f, dpi, FontStyle.Bold), p.AccentFg, r, false, true);
            if (_underline)
            {
                int y = Height - Dpi.Px(3, dpi);
                using (var pen = new Pen(p.Divider, 1f)) g.DrawLine(pen, padX, y, Width - padX, y);
            }
        }
    }

    /// <summary>A caption and an on/off switch: an accent track with a round knob, drawn with GDI+ (a font glyph would be
    /// tofu on RT 8.1). The whole row is the touch target; Space toggles it from the keyboard and a ring marks the
    /// focus. Maps to a boolean setting.</summary>
    public sealed class ToggleRow : SettingRow
    {
        private const int TrackW = 46, TrackH = 24, KnobInset = 3, Gap = 12;
        private bool _on;

        /// <summary>Raised when <see cref="On"/> changes, by the user or by code.</summary>
        public event EventHandler Changed;

        public ToggleRow(string caption, bool on = false) : base(caption, true)
        {
            _on = on;
            AccessibleRole = AccessibleRole.CheckButton;
            Refit();
        }

        public bool On
        {
            get { return _on; }
            set
            {
                if (_on == value) return;
                _on = value;
                Invalidate();
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void Toggle() { On = !On; }

        private Rectangle Track(int dpi)
        {
            int w = Dpi.Px(TrackW, dpi), h = Dpi.Px(TrackH, dpi);
            return new Rectangle(Width - Dpi.Px(PadX, dpi) - w, (Height - h) / 2, w, h);
        }

        protected override string AccessibleValueText { get { return _on ? "On" : "Off"; } }
        protected override bool? AccessibleChecked { get { return _on; } }
        protected override string AccessibleAction { get { return "Toggle"; } }
        protected override void AccessibleInvoke() { Toggle(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            Focus();
            Toggle();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Space || !Enabled) return;
            Toggle();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            PaintBack(g, p);
            var back = Backdrop(p);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var tr = Track(dpi);
            DrawCaption(g, p, new Rectangle(Dpi.Px(PadX, dpi), 0, Math.Max(1, tr.X - Dpi.Px(PadX, dpi) - Dpi.Px(Gap, dpi)), Height), dpi);

            // Off track: the backdrop leaned toward the text ink, so it is a quiet grey on a light card and a lighter
            // well on a dark one (DF's two literals, derived from tokens instead).
            Color track = _on ? p.Accent : ColorMath.Mix(back, p.Text, p.Dark ? 0.22f : 0.20f);
            Color knob = _on ? p.OnAccent : Color.White;
            if (!Enabled) { track = ColorMath.Mix(track, back, 0.55f); knob = ColorMath.Mix(knob, back, 0.35f); }

            using (var b = new SolidBrush(track))
            using (var path = DrawHelper.RoundedRect(tr, tr.Height / 2))
                g.FillPath(b, path);
            int inset = Dpi.Px(KnobInset, dpi), d = tr.Height - 2 * inset;
            int kx = _on ? tr.Right - d - inset : tr.X + inset;
            using (var kb = new SolidBrush(knob))
                g.FillEllipse(kb, new Rectangle(kx, tr.Y + inset, d, d));

            PaintFocusRing(g, p, tr, tr.Height / 2, dpi);
        }
    }

    /// <summary>A caption and a value that opens the app's themed menu (<see cref="ThemedContextMenuStrip"/>) of choices;
    /// the current one carries a check. Maps to an enum / list setting. Click or tap, Space, F4 or Alt+Down opens it.
    /// The menu is built per opening and disposed after it closes (DF leaked one per click: each stayed subscribed to
    /// ThemeChanged).</summary>
    public sealed class ChoiceRow : SettingRow
    {
        private const int DefaultValueWidth = 168, ChevronW = 12, Gap = 8;
        private readonly string[] _opts;
        private int _idx, _valueWidth = DefaultValueWidth;
        private ThemedContextMenuStrip _menu;

        /// <summary>Raised when <see cref="SelectedIndex"/> changes, by the user or by code.</summary>
        public event EventHandler Changed;

        public ChoiceRow(string caption, string[] options, int index = 0) : base(caption, true)
        {
            _opts = options ?? new string[0];
            _idx = Math.Max(0, Math.Min(index, _opts.Length - 1));
            AccessibleRole = AccessibleRole.ComboBox;
            Refit();
        }

        public int SelectedIndex
        {
            get { return _idx; }
            set
            {
                if (value < 0 || value >= _opts.Length || _idx == value) return;
                _idx = value;
                Invalidate();
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public string SelectedText { get { return _idx >= 0 && _idx < _opts.Length ? _opts[_idx] : ""; } }

        /// <summary>Width of the value area, in logical pixels. Widen it for options whose text carries a trade-off, so the
        /// collapsed row shows the choice in full instead of ellipsizing it. Capped at 60% of the row so the caption
        /// always keeps some room.</summary>
        public int ValueWidth
        {
            get { return _valueWidth; }
            set { value = Math.Max(40, value); if (_valueWidth == value) return; _valueWidth = value; Invalidate(); }
        }

        private struct Geo { public Rectangle Caption, Value, Chevron; }

        private Geo Arrange(int dpi)
        {
            var geo = new Geo();
            int padX = Dpi.Px(PadX, dpi), chevW = Dpi.Px(ChevronW, dpi), gap = Dpi.Px(Gap, dpi);
            geo.Chevron = new Rectangle(Width - padX - chevW, 0, chevW, Height);
            int avail = Math.Max(1, geo.Chevron.X - gap - padX);
            int vw = Math.Min(Dpi.Px(_valueWidth, dpi), avail * 3 / 5);
            geo.Value = new Rectangle(geo.Chevron.X - gap - vw, 0, vw, Height);
            geo.Caption = new Rectangle(padX, 0, Math.Max(1, geo.Value.X - gap - padX), Height);
            return geo;
        }

        protected override string AccessibleValueText { get { return SelectedText; } }
        protected override string AccessibleAction { get { return "Open"; } }
        protected override void AccessibleInvoke() { ShowMenu(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            Focus();
            ShowMenu();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.F4 || (e.KeyCode == Keys.Down && e.Alt))
            {
                ShowMenu();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void ShowMenu()
        {
            if (!Enabled || _opts.Length == 0 || _menu != null) return;
            int dpi = Dpi.Of(this);
            var menu = new ThemedContextMenuStrip();
            menu.Font = FontHelper.UiAt(10f, dpi);   // shared: Control never disposes its Font
            for (int i = 0; i < _opts.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(_opts[i]) { Checked = i == _idx };
                item.Click += (s, ev) => SelectedIndex = index;
                menu.Items.Add(item);
            }
            var geo = Arrange(dpi);
            menu.MinimumSize = new Size(geo.Value.Width + geo.Chevron.Width, 0);
            menu.Closed += (s, ev) =>
            {
                var m = s as ToolStripDropDown;
                if (ReferenceEquals(_menu, m)) _menu = null;
                if (m == null) return;
                // After the item's own Click has run; disposing inside Closed can cut the click short.
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke((Action)(() => m.Dispose())); return; } catch (InvalidOperationException) { }
                }
                m.Dispose();
            };
            _menu = menu;
            menu.Show(this, new Point(geo.Value.X, Height - Dpi.Px(6, dpi)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _menu != null)
            {
                var m = _menu;
                _menu = null;
                try { m.Close(); m.Dispose(); } catch (Exception) { }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            PaintBack(g, p);
            var geo = Arrange(dpi);
            DrawCaption(g, p, geo.Caption, dpi);

            string text = SelectedText;
            var font = FontHelper.ForAt(text, CaptionPt, dpi);
            Color ink = Enabled ? p.AccentFg : p.TextDisabled;
            DrawTextLine(g, text, font, ink, geo.Value, true, true);

            // The chevron: two round-capped strokes, not a font arrow.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = Dpi.F(1f, dpi), cx = geo.Chevron.X + geo.Chevron.Width / 2f, cy = Height / 2f;
            using (var pen = new Pen(Enabled ? p.TextDim : p.TextDisabled, Dpi.F(1.7f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - 5 * u, cy - 2 * u), new PointF(cx, cy + 3 * u), new PointF(cx + 5 * u, cy - 2 * u) });

            if (Focused && ShowFocusCues)
            {
                // Around the value and the chevron, as wide as the text actually is.
                int tw = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
                int left = Math.Max(geo.Value.X, geo.Chevron.X - Dpi.Px(Gap, dpi) - tw - Dpi.Px(4, dpi));
                int h = Dpi.Px(30, dpi);
                PaintFocusRing(g, p, new Rectangle(left, (Height - h) / 2, geo.Chevron.Right - left, h), Dpi.Px(6, dpi), dpi);
            }
        }
    }

    /// <summary>A caption over a single-line themed text field (<see cref="ThemedTextBox"/>); optionally digits-only or
    /// masked. Maps to a string / number setting.
    ///
    /// ★ NEVER Enabled = false ON THE TEXT BOX: on RT 8.1 that is how the EDIT loses its input state. So
    ///   <see cref="InputEnabled"/> = false makes the field read-only, takes it out of the tab order and dims it —
    ///   and <c>Enabled</c> on this row is HIDDEN (<c>new</c>) to mean exactly that, so <c>row.Enabled = false</c> can
    ///   never disable the EDIT. ⚠ Do not disable a SettingGroup or page that holds a TextRow either: Control.Enabled on
    ///   a parent cascades to the EDIT. Disable the rows' own switches instead.
    /// ★ <paramref name="numeric"/> and <paramref name="password"/> are fixed at construction (the EDIT's window style
    ///   must not change once it exists). Numeric accepts digits only, also when pasted, and turns Persian and Arabic-
    ///   Indic digits into ASCII ones, which int.Parse understands.
    /// ★ The field's font is set on the EDIT itself (explicitly, so DpiScaler rescales it once) and re-asserted when the
    ///   window's DPI is known and when it changes; the row never reads Control.Font.</summary>
    public sealed class TextRow : SettingRow
    {
        private const float CaptionSmallPt = 9.5f, FieldPt = 10.5f;
        private const int TopPad = 8, Gap = 6, BottomPad = 10;
        private readonly ThemedTextBox _box;
        private readonly bool _numeric;
        private bool _inputEnabled = true, _sanitizing;

        /// <summary>Raised when the text changes, typed or set by code.</summary>
        public event EventHandler Changed;

        public TextRow(string caption, string value, bool numeric = false, bool password = false) : base(caption, false)
        {
            _numeric = numeric;
            _box = new ThemedTextBox(false) { AccessibleName = caption ?? "" };
            _box.Inner.AccessibleName = caption ?? "";
            if (password) _box.Inner.UseSystemPasswordChar = true;
            EnsureFont(Dpi.SystemDpi);
            _box.Text = numeric ? DigitsOnly(value ?? "") : (value ?? "");   // before subscribing: no Changed for the initial value
            _box.TextChanged += OnBoxTextChanged;
            if (numeric) _box.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            AccessibleRole = AccessibleRole.Grouping;
            Controls.Add(_box);
            Refit();
        }

        /// <summary>The text.</summary>
        public string Value
        {
            get { return _box.Text; }
            set { _box.Text = _numeric ? DigitsOnly(value ?? "") : (value ?? ""); }
        }

        /// <summary>The placeholder, shown while the field is empty and unfocused.</summary>
        public string Hint { get { return _box.Hint; } set { _box.Hint = value; } }

        public int MaxLength { get { return _box.MaxLength; } set { _box.MaxLength = value; } }

        /// <summary>The field itself (focus it, select its text, hook its events).</summary>
        public ThemedTextBox Field { get { return _box; } }

        /// <summary>Focuses the EDIT (the row is never the focus).</summary>
        public new bool Focus() { return _box.Focus(); }

        /// <summary>False = read-only, not a tab stop, dimmed. NEVER Enabled = false on the EDIT (see the class notes).</summary>
        public bool InputEnabled
        {
            get { return _inputEnabled; }
            set
            {
                if (_inputEnabled == value) return;
                _inputEnabled = value;
                _box.InputEnabled = value;
                Invalidate();
            }
        }

        /// <summary>Same as <see cref="InputEnabled"/>. Hidden on purpose — see the class notes.</summary>
        public new bool Enabled { get { return _inputEnabled; } set { InputEnabled = value; } }

        protected override bool Dimmed { get { return !_inputEnabled; } }

        private void OnBoxTextChanged(object sender, EventArgs e)
        {
            if (_numeric && !_sanitizing)
            {
                string t = _box.Text, f = DigitsOnly(t);
                if (f != t)
                {
                    _sanitizing = true;
                    try
                    {
                        int caret = _box.SelectionStart;
                        _box.Text = f;
                        _box.SelectionStart = Math.Min(f.Length, Math.Max(0, caret - (t.Length - f.Length)));
                    }
                    finally { _sanitizing = false; }
                    return;   // the assignment above raised TextChanged again with clean text; Changed fires from that
                }
            }
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>ASCII digits of <paramref name="s"/>, with Arabic-Indic (U+0660..) and Persian (U+06F0..) digits mapped
        /// to ASCII and everything else dropped.</summary>
        private static string DigitsOnly(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));
                else if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));
            }
            return sb.ToString();
        }

        protected override void OnCaptionChanged()
        {
            if (_box != null) { _box.AccessibleName = Text; _box.Inner.AccessibleName = Text; }
            base.OnCaptionChanged();
            PerformLayout();
        }

        private void EnsureFont(int dpi)
        {
            _box.FontPoints = FieldPt;   // ThemedTextBox resolves it at the window's DPI; the EDIT ignores direct font writes
        }

        private int CaptionBand(int dpi)
        {
            if (Text.Length == 0) return 0;
            return LineHeight(FontHelper.ForAt(Text, CaptionSmallPt, dpi)) + Dpi.Px(Gap, dpi);
        }

        public override int HeightFor(int width, int dpi)
        {
            if (_box == null) return Dpi.Px(DefaultHeight, dpi);
            EnsureFont(dpi);
            return Dpi.Px(TopPad, dpi) + CaptionBand(dpi) + _box.PreferredFieldHeight + Dpi.Px(BottomPad, dpi);
        }

        protected override void OnDpiRescaled(int oldDpi, int newDpi) { EnsureFont(newDpi); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureFont(Dpi.Of(this));
            _box.Recolor();
            PerformLayout();
        }

        protected override void OnThemeApplied()
        {
            _box.Recolor();
            base.OnThemeApplied();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_box == null) return;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            _box.SetBounds(padX, Dpi.Px(TopPad, dpi) + CaptionBand(dpi), Math.Max(1, Width - 2 * padX), _box.PreferredFieldHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            if (Text.Length == 0) return;
            var font = FontHelper.ForAt(Text, CaptionSmallPt, dpi);
            var r = new Rectangle(padX, Dpi.Px(TopPad, dpi), Math.Max(1, Width - 2 * padX), LineHeight(font));
            DrawTextLine(g, Text, font, Dimmed ? p.TextDisabled : p.TextDim, r, false, true);
        }
    }

    /// <summary>A short explanatory note: wrapped text in a quiet ink. Its height follows its width — set the width (a
    /// SettingGroup or DialogForm.Restack does) and the row is as tall as the wrapped text needs, never less than the
    /// requested number of lines. <see cref="Role"/> picks the ink (Dim by default; Danger for an error line).
    /// <see cref="PathLine"/> shows one line shortened in the middle instead (for a file path). Wrapped text with emoji is
    /// drawn through the font (monochrome on Windows 10+, boxes on RT 8.1) — keep emoji out of notes, as ThemedLabel
    /// does when wrapping.</summary>
    public sealed class NoteRow : SettingRow
    {
        private const float NotePt = 9.5f;
        private const int PadY = 6;
        private readonly int _minLines;
        private LabelRole _role = LabelRole.Dim;
        private bool _pathLine;

        public NoteRow(string text, int lines = 1) : base(text, false)
        {
            _minLines = Math.Max(1, lines);
            AccessibleRole = AccessibleRole.StaticText;
            Refit();
        }

        /// <summary>Which palette ink the note uses (Title is bold body ink). Dim by default.</summary>
        public LabelRole Role
        {
            get { return _role; }
            set { if (_role == value) return; _role = value; Invalidate(); }
        }

        /// <summary>One line, shortened in the middle (C:\...\older) instead of wrapped — for paths.</summary>
        public bool PathLine
        {
            get { return _pathLine; }
            set { if (_pathLine == value) return; _pathLine = value; Refit(); Invalidate(); }
        }

        private Color Ink(Palette p)
        {
            if (!Enabled) return p.TextDisabled;
            switch (_role)
            {
                case LabelRole.Body:
                case LabelRole.Title: return p.Text;
                case LabelRole.Faint: return p.TextFaint;
                case LabelRole.Accent: return p.AccentFg;
                case LabelRole.Danger: return p.Danger;
                default: return p.TextDim;
            }
        }

        private Font NoteFont(int dpi)
        {
            return FontHelper.ForAt(Text, NotePt, dpi, _role == LabelRole.Title ? FontStyle.Bold : FontStyle.Regular);
        }

        private TextFormatFlags Flags()
        {
            if (_pathLine) return BaseFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis;
            var f = BaseFlags | TextFormatFlags.WordBreak | TextFormatFlags.Top | TextFormatFlags.EndEllipsis;
            return FontHelper.IsPersian(Text) ? f | TextFormatFlags.Right | TextFormatFlags.RightToLeft : f | TextFormatFlags.Left;
        }

        public override int HeightFor(int width, int dpi)
        {
            var font = NoteFont(dpi);
            int padY = Dpi.Px(PadY, dpi), line = LineHeight(font);
            if (_pathLine) return line + 2 * padY;
            int w = Math.Max(1, width - 2 * Dpi.Px(PadX, dpi));
            int textH = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, font, new Size(w, 0), Flags()).Height;
            return 2 * padY + Math.Max(_minLines * line, textH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
            PaintBack(g, p);
            if (Text.Length == 0) return;
            var r = new Rectangle(padX, padY, Math.Max(1, Width - 2 * padX), Math.Max(1, Height - 2 * padY));
            TextRenderer.DrawText(g, Text, NoteFont(dpi), r, Ink(p), Flags());
        }
    }

    /// <summary>A read-only, selectable, multi-line box for a tool's own output (adb's messages are shown verbatim, never
    /// replaced by a generic error), under an optional caption. Built on <see cref="ThemedTextBox"/> so it is themed
    /// like <see cref="TextRow"/>. <c>height</c> is the whole row's logical height.
    /// ⚠ A ThemedTextBox multi-line field has NO scroll bar (a native bar cannot be themed — it is white on RT), so long
    ///   output scrolls with the wheel, the keyboard (Page Up/Down, Ctrl+End) or a touch drag on the EDIT, with no visible
    ///   bar. Give it enough height for the usual case.</summary>
    public sealed class OutputRow : SettingRow
    {
        private const float CaptionSmallPt = 9.5f, BoxPt = 9.5f;
        private const int TopPad = 8, Gap = 4, BottomPad = 10, MinBox = 24;
        private readonly ThemedTextBox _box;
        private readonly int _logicalHeight;

        public OutputRow(string caption, int height) : base(caption, false)
        {
            _logicalHeight = Math.Max(60, height);
            _box = new ThemedTextBox(true) { AccessibleName = caption ?? "" };
            _box.Inner.ReadOnly = true;                 // selectable and copyable, but not editable; the EDIT keeps its handle
            _box.Inner.AccessibleName = caption ?? "";
            EnsureFont(Dpi.SystemDpi);
            AccessibleRole = AccessibleRole.Grouping;
            Controls.Add(_box);
            Refit();
        }

        /// <summary>The output text; line breaks are normalised to CR LF for the EDIT.</summary>
        public string Value
        {
            get { return _box.Text; }
            set { _box.Text = (value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }
        }

        /// <summary>The field itself (copy from it, scroll it).</summary>
        public ThemedTextBox Field { get { return _box; } }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(_logicalHeight, dpi); }

        private void EnsureFont(int dpi)
        {
            _box.FontPoints = BoxPt;   // ThemedTextBox resolves it at the window's DPI; the EDIT ignores direct font writes
        }

        private int BoxTop(int dpi)
        {
            int top = Dpi.Px(TopPad, dpi);
            if (Text.Length > 0) top += LineHeight(FontHelper.ForAt(Text, CaptionSmallPt, dpi)) + Dpi.Px(Gap, dpi);
            return top;
        }

        protected override void OnCaptionChanged()
        {
            if (_box != null) { _box.AccessibleName = Text; _box.Inner.AccessibleName = Text; }
            base.OnCaptionChanged();
            PerformLayout();
        }

        protected override void OnDpiRescaled(int oldDpi, int newDpi) { EnsureFont(newDpi); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureFont(Dpi.Of(this));
            _box.Recolor();
            PerformLayout();
        }

        protected override void OnThemeApplied()
        {
            _box.Recolor();
            base.OnThemeApplied();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_box == null) return;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), top = BoxTop(dpi);
            _box.SetBounds(padX, top, Math.Max(1, Width - 2 * padX), Math.Max(Dpi.Px(MinBox, dpi), Height - top - Dpi.Px(BottomPad, dpi)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            if (Text.Length == 0) return;
            var font = FontHelper.ForAt(Text, CaptionSmallPt, dpi);
            DrawTextLine(g, Text, font, p.TextDim, new Rectangle(padX, Dpi.Px(TopPad, dpi), Math.Max(1, Width - 2 * padX), LineHeight(font)), false, true);
        }
    }

    /// <summary>A caption and a touch-friendly segmented selector of 2..N options (Everybody / My Contacts / Nobody):
    /// the chosen segment is an accent block with accent-readable text. Promoted from SettingsForm's private Segmented3 —
    /// the same behaviour: a tap anywhere in a segment selects it (segment = strip width / option count, the index
    /// clamped), -1 means "nothing selected yet", and a disabled row is dimmed and ignores input (used until a value
    /// loads from the server). Added: Left / Right move the selection from the keyboard.
    ///
    /// ★ LAYOUT. Caption on the left, strip on the right; the strip is as wide as its widest option needs. When the
    ///   caption would not fit beside it (its text, up to 45% of the row, never under 120 logical px) the row STACKS
    ///   (caption above, strip below at full width) and its height follows. The hit area is the strip's width and
    ///   the strip's height plus 6 logical px above and below (a finger is bigger than 32 px).
    /// ★ EVENTS. <see cref="SelectedIndexChanged"/> fires on every change, user or code — use
    ///   <see cref="SetSelected"/> with raise = false to apply a server value without it (Segmented3.SetSelected).
    ///   <see cref="Applied"/> is the last server-confirmed index, for reverting a failed change (it was a field there).</summary>
    public sealed class SegmentedRow : SettingRow
    {
        private const int StripH = 32, MinSeg = 64, SegPad = 14, CaptionMin = 120, Gap = 12, Radius = 6;
        private const float SegPt = 9f;
        private string[] _options;
        private int _sel;

        /// <summary>The last value the server confirmed (-1 = none yet); the page reads it to revert a failed change.</summary>
        public int Applied = -1;

        public event EventHandler SelectedIndexChanged;

        public SegmentedRow(string caption, string[] options, int selectedIndex = -1) : base(caption, true)
        {
            _options = options ?? new string[0];
            _sel = Math.Max(-1, Math.Min(selectedIndex, _options.Length - 1));
            AccessibleRole = AccessibleRole.PageTabList;
            Refit();
        }

        public string[] Options
        {
            get { return _options; }
            set
            {
                _options = value ?? new string[0];
                if (_sel >= _options.Length) _sel = _options.Length - 1;
                Refit();
                Invalidate();
            }
        }

        /// <summary>The selected option, or -1 for none. Raises <see cref="SelectedIndexChanged"/> when it changes.</summary>
        public int SelectedIndex
        {
            get { return _sel; }
            set { SetSelected(value, true); }
        }

        public string SelectedText { get { return _sel >= 0 && _sel < _options.Length ? _options[_sel] : ""; } }

        /// <summary>Selects an option (clamped to -1..n-1); raise = false applies it without the event.</summary>
        public void SetSelected(int index, bool raise)
        {
            index = Math.Max(-1, Math.Min(index, _options.Length - 1));
            if (index == _sel) return;
            _sel = index;
            if (!IsDisposed) Invalidate();
            if (!raise) return;
            var h = SelectedIndexChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private struct Geo { public Rectangle Caption, Strip; public int SegW, Height; public bool Stacked; }

        private Geo Measure(int width, int dpi)
        {
            var geo = new Geo();
            int n = _options.Length;
            int padX = Dpi.Px(PadX, dpi), stripH = Dpi.Px(StripH, dpi), gap = Dpi.Px(Gap, dpi);
            int avail = Math.Max(1, width - 2 * padX);

            int segW = Dpi.Px(MinSeg, dpi);
            for (int i = 0; i < n; i++)
            {
                var f = FontHelper.ForAt(_options[i], SegPt, dpi, FontStyle.Bold);
                int tw = TextRenderer.MeasureText(_options[i], f, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
                segW = Math.Max(segW, tw + 2 * Dpi.Px(SegPad, dpi));
            }
            // The caption keeps its whole text (up to 45% of the row, never under CaptionMin); if the strip and that do
            // not fit side by side the row stacks instead of ellipsizing the caption.
            int capNeed = Dpi.Px(CaptionMin, dpi);
            if (Text.Length > 0)
            {
                int capW = TextRenderer.MeasureText(Text, FontHelper.ForAt(Text, CaptionPt, dpi), Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
                capNeed = Math.Max(capNeed, Math.Min(capW, avail * 45 / 100));
            }
            geo.Stacked = n > 0 && segW * n + gap + capNeed > avail;
            if (geo.Stacked) segW = Math.Max(1, avail / n);
            geo.SegW = segW;

            int stripW = segW * n;
            if (!geo.Stacked)
            {
                geo.Height = Dpi.Px(DefaultHeight, dpi);
                geo.Strip = new Rectangle(width - padX - stripW, (geo.Height - stripH) / 2, stripW, stripH);
                geo.Caption = new Rectangle(padX, 0, Math.Max(1, geo.Strip.X - gap - padX), geo.Height);
            }
            else
            {
                int top = Dpi.Px(12, dpi), capH = 0, capGap = 0;
                if (Text.Length > 0) { capH = LineHeight(FontHelper.ForAt(Text, CaptionPt, dpi)); capGap = Dpi.Px(8, dpi); }
                geo.Caption = new Rectangle(padX, top, avail, capH);
                geo.Strip = new Rectangle(padX, top + capH + capGap, stripW, stripH);
                geo.Height = geo.Strip.Bottom + Dpi.Px(12, dpi);
            }
            return geo;
        }

        public override int HeightFor(int width, int dpi) { return Measure(width, dpi).Height; }

        protected override string AccessibleValueText { get { return SelectedText; } }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & (Keys.Alt | Keys.Control)) == 0)
            {
                var k = keyData & Keys.KeyCode;
                if (k == Keys.Left || k == Keys.Right) return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled || _options.Length == 0) return;
            if (e.KeyCode == Keys.Left) SetSelected(_sel < 0 ? _options.Length - 1 : _sel - 1, true);
            else if (e.KeyCode == Keys.Right) SetSelected(_sel < 0 ? 0 : _sel + 1, true);
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || !Enabled || _options.Length == 0) return;
            int dpi = Dpi.Of(this);
            var geo = Measure(Width, dpi);
            if (e.X < geo.Strip.X || e.X >= geo.Strip.Right) return;
            if (e.Y < geo.Strip.Y - Dpi.Px(6, dpi) || e.Y >= geo.Strip.Bottom + Dpi.Px(6, dpi)) return;
            int idx = (e.X - geo.Strip.X) / Math.Max(1, geo.SegW);
            if (idx < 0) idx = 0; else if (idx >= _options.Length) idx = _options.Length - 1;
            Focus();
            if (idx != _sel) SetSelected(idx, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            PaintBack(g, p);
            var geo = Measure(Width, dpi);
            DrawCaption(g, p, geo.Caption, dpi);
            int n = _options.Length;
            if (n == 0) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var s = geo.Strip;
            var frame = new Rectangle(s.X, s.Y, s.Width - 1, s.Height - 1);
            Color well = p.Window;   // the recessed track the segments sit in
            Color accent = Enabled ? p.Accent : ColorMath.Mix(p.Accent, well, 0.5f);
            int radius = Dpi.Px(Radius, dpi), inset = Dpi.Px(2, dpi);

            using (var b = new SolidBrush(well))
            using (var path = DrawHelper.RoundedRect(frame, radius))
                g.FillPath(b, path);

            for (int i = 0; i < n; i++)
            {
                var cell = new Rectangle(s.X + i * geo.SegW, s.Y, geo.SegW, s.Height);
                bool sel = i == _sel;
                if (sel)
                {
                    using (var ab = new SolidBrush(accent))
                    using (var ap = DrawHelper.RoundedRect(new Rectangle(cell.X + inset, cell.Y + inset, cell.Width - 2 * inset, cell.Height - 2 * inset), Math.Max(1, radius - inset)))
                        g.FillPath(ab, ap);
                }
                else if (i > 0 && i - 1 != _sel)
                {
                    using (var sp = new Pen(p.Divider, 1f))
                        g.DrawLine(sp, cell.X, cell.Y + Dpi.Px(6, dpi), cell.X, cell.Bottom - Dpi.Px(6, dpi));
                }
                Color ink = sel ? ColorMath.OnColor(accent) : (Enabled ? p.Text : p.TextDisabled);
                var font = FontHelper.ForAt(_options[i], SegPt, dpi, FontStyle.Bold);
                var flags = BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                if (FontHelper.IsPersian(_options[i])) flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, _options[i], font, Rectangle.Inflate(cell, -Dpi.Px(4, dpi), 0), ink, flags);
            }

            using (var pen = new Pen(p.Divider, 1f))
            using (var path = DrawHelper.RoundedRect(frame, radius))
                g.DrawPath(pen, path);

            PaintFocusRing(g, p, s, radius, dpi);
        }
    }

    /// <summary>A caption and a compact themed - / value / + stepper (replaces the bare native NumericUpDown). Promoted
    /// from SettingsForm's private Stepper — the same behaviour: an integer <see cref="Value"/> kept within
    /// [<see cref="Minimum"/>, <see cref="Maximum"/>], moved by <see cref="Step"/>; the 34 px end zones are the buttons
    /// (the middle does nothing); an end dims when the value is at its limit; the value shows an optional
    /// <see cref="Suffix"/> (" MB"). The -/+ marks are drawn lines, not font glyphs.
    /// Added: the buttons are as tall as the row (not just the 32 px strip), and the keyboard works — Left / Down / -
    /// step down, Right / Up / + step up, Page Up / Down move by ten steps, Home / End jump to the limits.</summary>
    public sealed class StepperRow : SettingRow
    {
        private const int StripW = 122, StripH = 32, ButtonW = 34, Radius = 6;
        private const float ValuePt = 9.5f;
        private int _min, _max, _value, _step;
        private string _suffix;

        /// <summary>Raised when <see cref="Value"/> changes, by the user or by code.</summary>
        public event EventHandler ValueChanged;

        public StepperRow(string caption, int minimum, int maximum, int value, int step = 1, string suffix = "") : base(caption, true)
        {
            _min = minimum;
            _max = Math.Max(minimum, maximum);
            _step = Math.Max(1, step);
            _suffix = suffix ?? "";
            _value = Clamp(value);
            AccessibleRole = AccessibleRole.SpinButton;
            Refit();
        }

        private int Clamp(int v) { return v < _min ? _min : (v > _max ? _max : v); }

        public int Minimum { get { return _min; } set { _min = value; if (_max < _min) _max = _min; Reclamp(); } }
        public int Maximum { get { return _max; } set { _max = value; if (_min > _max) _min = _max; Reclamp(); } }
        public int Step { get { return _step; } set { _step = Math.Max(1, value); } }
        public string Suffix { get { return _suffix; } set { _suffix = value ?? ""; Invalidate(); } }

        /// <summary>The value, clamped to [Minimum, Maximum].</summary>
        public int Value { get { return _value; } set { Set(value); } }

        private void Reclamp() { Set(_value); Invalidate(); }

        private void Set(int v)
        {
            v = Clamp(v);
            if (v == _value) return;
            _value = v;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private Rectangle Strip(int dpi)
        {
            int w = Dpi.Px(StripW, dpi), h = Dpi.Px(StripH, dpi);
            return new Rectangle(Width - Dpi.Px(PadX, dpi) - w, (Height - h) / 2, w, h);
        }

        protected override string AccessibleValueText { get { return _value + _suffix; } }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & (Keys.Alt | Keys.Control)) == 0)
            {
                switch (keyData & Keys.KeyCode)
                {
                    case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                    case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                        return true;
                }
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: case Keys.Subtract: case Keys.OemMinus: Set(_value - _step); break;
                case Keys.Right: case Keys.Up: case Keys.Add: case Keys.Oemplus: Set(_value + _step); break;
                case Keys.PageDown: Set(_value - 10 * _step); break;
                case Keys.PageUp: Set(_value + 10 * _step); break;
                case Keys.Home: Set(_min); break;
                case Keys.End: Set(_max); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            int dpi = Dpi.Of(this);
            var s = Strip(dpi);
            int bw = Dpi.Px(ButtonW, dpi);
            if (e.X >= s.X && e.X < s.X + bw) { Focus(); Set(_value - _step); }
            else if (e.X >= s.Right - bw && e.X < s.Right) { Focus(); Set(_value + _step); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            PaintBack(g, p);
            var s = Strip(dpi);
            DrawCaption(g, p, new Rectangle(Dpi.Px(PadX, dpi), 0, Math.Max(1, s.X - Dpi.Px(PadX, dpi) - Dpi.Px(12, dpi)), Height), dpi);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int bw = Dpi.Px(ButtonW, dpi), radius = Dpi.Px(Radius, dpi);
            var frame = new Rectangle(s.X, s.Y, s.Width - 1, s.Height - 1);
            using (var b = new SolidBrush(p.Window))
            using (var path = DrawHelper.RoundedRect(frame, radius))
                g.FillPath(b, path);

            // The two marks: an end dims at its limit (and both when the row is disabled).
            Color down = Enabled && _value > _min ? p.AccentFg : p.TextDisabled;
            Color up = Enabled && _value < _max ? p.AccentFg : p.TextDisabled;
            float u = Dpi.F(1f, dpi), mx = s.X + bw / 2f, px = s.Right - bw / 2f, cy = s.Y + s.Height / 2f;
            using (var pen = new Pen(down, Dpi.F(1.8f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, mx - 5 * u, cy, mx + 5 * u, cy);
            using (var pen = new Pen(up, Dpi.F(1.8f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, px - 5 * u, cy, px + 5 * u, cy);
                g.DrawLine(pen, px, cy - 5 * u, px, cy + 5 * u);
            }

            string text = _value + _suffix;
            TextRenderer.DrawText(g, text, FontHelper.ForAt(text, ValuePt, dpi, FontStyle.Bold),
                new Rectangle(s.X + bw, s.Y, s.Width - 2 * bw, s.Height), Enabled ? p.Text : p.TextDisabled,
                BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            g.SmoothingMode = SmoothingMode.None;   // the two inner dividers and the outline: crisp 1 px
            using (var pen = new Pen(p.Divider, 1f))
            {
                g.DrawLine(pen, s.X + bw, s.Y + Dpi.Px(5, dpi), s.X + bw, s.Bottom - 1 - Dpi.Px(5, dpi));
                g.DrawLine(pen, s.Right - 1 - bw, s.Y + Dpi.Px(5, dpi), s.Right - 1 - bw, s.Bottom - 1 - Dpi.Px(5, dpi));
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(p.Divider, 1f))
            using (var path = DrawHelper.RoundedRect(frame, radius))
                g.DrawPath(pen, path);

            PaintFocusRing(g, p, s, radius, dpi);
        }
    }

    /// <summary>A card: a rounded panel (radius 10 logical) filled with Palette.Card and outlined by a 1 px Divider
    /// hairline, which stacks the rows added to it top-down with a 1 px Divider hairline between neighbouring rows. Its
    /// Height follows the rows (set its width — Dock = Top, an Anchor, or a Width — and it is as tall as they need), so a
    /// settings page is simply a column of <see cref="SectionHeader"/> + SettingGroup.
    ///
    /// ★ ROWS FILL THE CARD. Each shown row is inset 1 px at left and right (the card's outline lives there), as wide as
    ///   the card, and as tall as <see cref="SettingRow.HeightFor"/> says. The FIRST and LAST shown row repaint their slice of
    ///   the rounded outline themselves (SettingRow.CardEnd) — otherwise their square corners would cut the card's round
    ///   ones. So keep a <see cref="SettingRow"/> at both ends: any other control placed there paints square corners.
    /// ★ ORDER = THE ORDER ADDED. Hide a row with <see cref="SettingRow.RowVisible"/>, never Visible: the group stacks
    ///   by that flag (Visible is false for everything on a form that is not shown yet).
    /// ★ BackColor is kept equal to Palette.Card, so rows and the text boxes inside TextRow clear to the right colour.
    ///   The outer corners are cleared to the nearest opaque BackColor above the group (the page), else Palette.Window.
    /// ★ A group does not scale anything itself: DpiScaler's generic pass scales bounds, then OnDpiChanged re-stacks the
    ///   rows from their exact heights.</summary>
    public sealed class SettingGroup : Panel, IDpiAware
    {
        /// <summary>The card's corner radius, in logical pixels.</summary>
        public const int Radius = 10;

        private readonly List<Control> _items = new List<Control>();
        private bool _hooked, _restacking, _restackAgain;
        private int _stackWidth = -1;

        public SettingGroup()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = ThemeHelper.Palette.Card;
            AccessibleRole = AccessibleRole.Grouping;
            Restack();   // no rows yet: Height 0
        }

        /// <summary>Adds a row at the end and returns it (so <c>var t = group.Add(new ToggleRow(...))</c> reads well).</summary>
        public T Add<T>(T row) where T : Control
        {
            Controls.Add(row);
            return row;
        }

        public void AddRange(params Control[] rows)
        {
            foreach (var r in rows) Controls.Add(r);
        }

        // ── stacking ─────────────────────────────────────────────────────────────────────────────────────────

        internal void RequestRestack() { if (!Dpi.Busy) Restack(); }

        private static bool IsShown(Control c)
        {
            var r = c as SettingRow;
            return r == null || r.RowVisible;
        }

        /// <summary>Stretches every shown row to the card's width, stacks them with a 1 px gap between neighbours and sets
        /// the group's Height. Re-entrant by design: a row's height can change when its width is set (a wrapped
        /// NoteRow), whose SizeChanged lands back here — that nested call only asks for another pass, and the rows
        /// are refitted explicitly in each pass, so it settles.</summary>
        private void Restack()
        {
            if (_restacking) { _restackAgain = true; return; }
            _restacking = true;
            try
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    _restackAgain = false;
                    _stackWidth = Width;
                    int inner = Math.Max(1, Width - 2), y = 0;
                    Control first = null, last = null;
                    foreach (var c in _items)
                    {
                        if (!IsShown(c)) continue;
                        if (first == null) first = c; else y += 1;   // the hairline between rows lives in this 1 px gap
                        last = c;
                        if (c.Left != 1 || c.Top != y || c.Width != inner) c.SetBounds(1, y, inner, c.Height);
                        var sr = c as SettingRow;
                        if (sr != null) sr.Refit();
                        y += c.Height;
                    }
                    foreach (var c in _items)
                    {
                        var sr = c as SettingRow;
                        if (sr == null) continue;
                        bool end = IsShown(c) && (ReferenceEquals(c, first) || ReferenceEquals(c, last));
                        if (sr.CardEnd != end) { sr.CardEnd = end; sr.Invalidate(); }
                    }
                    if (Height != y) Height = y;
                    if (!_restackAgain) break;
                }
            }
            finally { _restacking = false; }
            Invalidate();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (!_items.Contains(e.Control)) { _items.Add(e.Control); e.Control.SizeChanged += OnChildSizeChanged; }
            if (!Dpi.Busy) Restack();
        }

        protected override void OnControlRemoved(ControlEventArgs e)
        {
            base.OnControlRemoved(e);
            if (_items.Remove(e.Control)) e.Control.SizeChanged -= OnChildSizeChanged;
            var sr = e.Control as SettingRow;
            if (sr != null) sr.CardEnd = false;
            if (!Dpi.Busy) Restack();
        }

        private void OnChildSizeChanged(object sender, EventArgs e) { if (!Dpi.Busy) Restack(); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width != _stackWidth && !Dpi.Busy) Restack();
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (!Dpi.Busy) Restack(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Dpi.Busy) Restack(); }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            // Called after the rows' own OnDpiChanged (children first): their heights are exact, so stack from them.
            // Not skipped for Dpi.Busy — the callbacks run inside the pass.
            _stackWidth = -1;
            Restack();
        }

        // ── theme ────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_hooked) { _hooked = true; ThemeHelper.ThemeChanged += OnThemeChangedCore; }
            ApplyTheme();
            if (!Dpi.Busy) Restack();   // the window, and so the DPI, is known now
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Unhook();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Unhook();
            base.Dispose(disposing);
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            ThemeHelper.ThemeChanged -= OnThemeChangedCore;
        }

        private void OnThemeChangedCore()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!InvokeRequired) { ApplyTheme(); return; }
            try { BeginInvoke((Action)ApplyTheme); } catch (InvalidOperationException) { }
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            var card = ThemeHelper.Palette.Card;
            if (BackColor != card) BackColor = card;   // children with an ambient BackColor follow and repaint
            Invalidate(true);
        }

        // ── painting ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The colour behind the card's rounded corners: the nearest opaque BackColor above the group.</summary>
        private Color OuterBackdrop(Palette p)
        {
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return p.Window;
        }

        private Rectangle CardRect { get { return new Rectangle(Point.Empty, new Size(Math.Max(1, Width - 1), Math.Max(1, Height - 1))); } }

        /// <summary>A first / last row's whole background: the outer colour, then the card (fill + outline) as it falls
        /// inside the row — drawn from the group's own geometry shifted into the row's coordinates, so the pixels are
        /// exactly the ones OnPaint puts there.</summary>
        internal void PaintSlice(Graphics g, Control row, Palette p)
        {
            g.Clear(OuterBackdrop(p));
            if (Width < 3 || Height < 3) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dpi = Dpi.Of(this);
            var card = CardRect;
            card.Offset(-row.Left, -row.Top);
            using (var path = DrawHelper.RoundedRect(card, Dpi.Px(Radius, dpi)))
            {
                using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);
                using (var pen = new Pen(p.Divider, 1f)) g.DrawPath(pen, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            g.Clear(OuterBackdrop(p));
            if (Width < 3 || Height < 3) return;
            int dpi = Dpi.Of(this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DrawHelper.RoundedRect(CardRect, Dpi.Px(Radius, dpi)))
            {
                using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);

                // The hairlines between rows (the rows cover everything else), inset like the captions.
                g.SmoothingMode = SmoothingMode.None;
                int padX = Dpi.Px(SettingRow.PadX, dpi);
                bool firstDone = false;
                using (var pen = new Pen(p.Divider, 1f))
                    foreach (var c in _items)
                    {
                        if (!IsShown(c)) continue;
                        if (firstDone) g.DrawLine(pen, padX, c.Top - 1, Width - 1 - padX, c.Top - 1);
                        firstDone = true;
                    }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(p.Divider, 1f)) g.DrawPath(pen, path);
            }
        }
    }
}
