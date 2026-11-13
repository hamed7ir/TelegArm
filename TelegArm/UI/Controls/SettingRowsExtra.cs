using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 2.6 — a row that DOES something: tapping anywhere on it (or Enter / Space) raises Click.
    /// Without <paramref name="actionText"/> it shows a drawn chevron (it opens something: Proxy, Display scaling);
    /// with one it shows that word in the accent ink at the right (Copy, Clear, Run). Subtitle as every row.</summary>
    public sealed class ActionRow : SettingRow
    {
        private string _action;

        public ActionRow(string caption, string actionText = null) : base(caption, true)
        {
            _action = string.IsNullOrEmpty(actionText) ? null : actionText;
            AccessibleRole = AccessibleRole.PushButton;   // R6
            Refit();
        }

        /// <summary>The word at the right; null = a chevron.</summary>
        public string ActionText
        {
            get { return _action; }
            set { _action = string.IsNullOrEmpty(value) ? null : value; Invalidate(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; if (Enabled) OnClick(EventArgs.Empty); }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.Enter && (keyData & (Keys.Alt | Keys.Control)) == 0 || base.IsInputKey(keyData);
        }

        protected override string AccessibleAction { get { return _action ?? "Open"; } }
        protected override void AccessibleInvoke() { OnClick(EventArgs.Empty); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            int right = Width - padX;
            if (_action == null)
            {
                float cx = right - Dpi.F(5f, dpi), cy = Height / 2f, u = Dpi.F(4f, dpi);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Dimmed ? p.TextDisabled : p.TextDim, Dpi.F(1.6f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - u / 2, cy - u, cx + u / 2, cy);
                    g.DrawLine(pen, cx + u / 2, cy, cx - u / 2, cy + u);
                }
                g.SmoothingMode = old;
                right -= Dpi.Px(20, dpi);
            }
            else
            {
                var f = FontHelper.UiAt(10f, dpi, FontStyle.Bold);
                int w = TextRenderer.MeasureText(_action, f, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
                DrawTextLine(g, _action, f, Dimmed ? p.TextDisabled : p.AccentFg, new Rectangle(right - w, 0, w, Height), true, false);
                right -= w + Dpi.Px(12, dpi);
            }
            DrawCaption(g, p, new Rectangle(padX, 0, Math.Max(1, right - padX), Height), dpi);
            int inset = Dpi.Px(4, dpi);
            PaintFocusRing(g, p, new Rectangle(inset, inset, Width - 2 * inset, Height - 2 * inset), Dpi.Px(6, dpi), dpi);
        }
    }

    /// <summary>Redesign step 2.6 / 5.2 — a row of colour swatches under a caption: the FIRST swatch is "Follow Windows"
    /// (the accent: drawn in Windows' accent with a ring inside) or "Default" (a theme base), then fixed presets, then
    /// "Custom…" (a small colour wheel, or the custom colour once one is chosen). Tap, or Left/Right, picks; tapping Custom
    /// raises <see cref="CustomRequested"/> — the page opens the picker and calls <see cref="SetCustom"/>, which raises
    /// <see cref="Changed"/>. <see cref="SelectedAccent"/> is null for the first swatch.</summary>
    public sealed class SwatchRow : SettingRow
    {
        /// <summary>The fixed accents after "Follow Windows" (Telegram's blue first).</summary>
        public static Color[] Accents
        {
            get
            {
                var p = ThemeHelper.Palette;
                return new[]
                {
                    p.AccentSwatchBlue,   // Telegram blue
                    p.AccentSwatchViolet,   // violet
                    p.AccentSwatchMagenta,   // magenta
                    p.AccentSwatchPink,   // pink
                    p.AccentSwatchRed,   // red
                    p.AccentSwatchOrange,   // orange
                    p.AccentSwatchGreen,   // green
                    p.AccentSwatchTeal,   // teal
                };
            }
        }

        private const int Diameter = 28, Gap = 12, Ring = 3, Height2 = 92;
        private readonly Color[] _presets;
        private readonly string[] _names;
        private readonly bool _follow;     // first swatch = Follow Windows (accent) rather than Default (a base)
        private bool _ringFirst;           // the first swatch draws the "follows something" ring (accent, bubble)
        private string _firstName;         // its accessible name
        private readonly Color _default;   // the first swatch's colour for a base row
        private int _index;                // 0 = first, 1..n = presets, n+1 = custom
        private Color? _custom;
        private Color _windows;            // the Windows accent, read when built (never in paint)
        public event EventHandler Changed;

        /// <summary>Raised when Custom is tapped (or chosen from the keyboard with no custom colour yet).</summary>
        public event EventHandler CustomRequested;

        /// <summary>The accent row: Follow Windows, the fixed accents, Custom.</summary>
        public SwatchRow(string caption, Color? current) : this(caption, current, true, Color.Empty, Accents, null) { }

        /// <summary>A theme-base row: Default, the mode's presets, Custom.</summary>
        public static SwatchRow ForBase(string caption, bool dark, Color? current)
        {
            return new SwatchRow(caption, current, false, dark ? Palette.DefaultDarkBase : Palette.DefaultLightBase,
                                 dark ? Palette.DarkBasePresets : Palette.LightBasePresets, dark ? Palette.DarkBaseNames : Palette.LightBaseNames);
        }

        private SwatchRow(string caption, Color? current, bool follow, Color dflt, Color[] presets, string[] names) : base(caption, true)
        {
            _follow = follow;
            _default = dflt;
            _presets = presets;
            _names = names;
            AccessibleRole = AccessibleRole.RadioButton;   // R6: one of several swatches
            if (follow) _windows = ThemeHelper.ReadWindowsAccent();
            _index = 0;
            if (current.HasValue && !(!follow && current.Value.ToArgb() == dflt.ToArgb()))
            {
                for (int i = 0; i < _presets.Length; i++) if (_presets[i].ToArgb() == current.Value.ToArgb()) _index = i + 1;
                if (_index == 0) { _custom = current; _index = CustomIndex; }   // a custom colour (picker, env, settings)
            }
            Refit();
        }

        /// <summary>Step 5.7 — the outgoing-bubble row: "From accent" (the accent, with the ring), presets, Custom.</summary>
        public static SwatchRow ForBubble(string caption, Color? current)
        {
            var row = new SwatchRow(caption, current, false, ThemeHelper.Palette.Accent, Palette.OutBubblePresets, Palette.OutBubbleNames);
            row._ringFirst = true;
            row._firstName = "From accent";
            return row;
        }

        /// <summary>Shows <paramref name="c"/> (null = the first swatch) WITHOUT raising Changed — after a reset or an import.</summary>
        public void Select(Color? c)
        {
            _index = 0;
            if (c.HasValue && !(!_follow && !_ringFirst && c.Value.ToArgb() == _default.ToArgb()))
            {
                for (int i = 0; i < _presets.Length; i++) if (_presets[i].ToArgb() == c.Value.ToArgb()) _index = i + 1;
                if (_index == 0) { _custom = c; _index = CustomIndex; }
            }
            Invalidate();
        }

        /// <summary>The chosen colour, or null for the first swatch (Follow Windows / Default).</summary>
        public Color? SelectedAccent
        {
            get { return _index <= 0 ? (Color?)null : _index == CustomIndex ? _custom : _presets[_index - 1]; }
        }

        /// <summary>The custom colour last chosen (null until one is).</summary>
        public Color? CustomColor { get { return _custom; } }

        /// <summary>Selects Custom with <paramref name="c"/> and raises <see cref="Changed"/>.</summary>
        public void SetCustom(Color c)
        {
            _custom = c;
            _index = -1;   // force Pick to see a change even when Custom was already selected
            Pick(CustomIndex);
        }

        /// <summary>Re-reads the Windows accent (after Windows changed it).</summary>
        public void RefreshWindowsAccent() { if (_follow) { _windows = ThemeHelper.ReadWindowsAccent(); Invalidate(); } }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(Height2, dpi); }

        private int CustomIndex { get { return _presets.Length + 1; } }
        private int Count { get { return _presets.Length + 2; } }

        private Rectangle Swatch(int i, int dpi)
        {
            int d = Dpi.Px(Diameter, dpi), step = d + Dpi.Px(Gap, dpi);
            int top = Height - Dpi.Px(14, dpi) - d;
            return new Rectangle(Dpi.Px(PadX, dpi) + Dpi.Px(Ring, dpi) + i * step, top, d, d);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int dpi = Dpi.Of(this);
            for (int i = 0; i < Count; i++)
                if (Rectangle.Inflate(Swatch(i, dpi), Dpi.Px(4, dpi), Dpi.Px(4, dpi)).Contains(e.Location))
                {
                    if (i == CustomIndex) RequestCustom(); else Pick(i);
                    return;
                }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return ((keyData & (Keys.Alt | Keys.Control)) == 0 && (k == Keys.Left || k == Keys.Right)) || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { Pick(Math.Max(0, _index - 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right)
            {
                int next = Math.Min(CustomIndex, _index + 1);
                if (next == CustomIndex && !_custom.HasValue) RequestCustom(); else Pick(next);
                e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _index == CustomIndex) { RequestCustom(); e.Handled = true; }
        }

        private void RequestCustom()
        {
            var h = CustomRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void Pick(int i)
        {
            if (i == _index) return;
            _index = i;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override string AccessibleValueText
        {
            get
            {
                if (_index == 0) return _firstName ?? (_follow ? "Follow Windows" : "Default");
                if (_index == CustomIndex) return "Custom " + (_custom.HasValue ? ColorMath.Hex(_custom.Value) : "");
                return _names != null && _index - 1 < _names.Length ? _names[_index - 1] : ColorMath.Hex(_presets[_index - 1]);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            var back = Backdrop(p);
            int capH = Dpi.Px(SettingRow.DefaultHeight, dpi) - Dpi.Px(14, dpi);
            DrawCaption(g, p, new Rectangle(padX, Dpi.Px(4, dpi), Math.Max(1, Width - 2 * padX), capH), dpi);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Count; i++)
            {
                var r = Swatch(i, dpi);
                if (i == CustomIndex && !_custom.HasValue) DrawWheel(g, r);
                else
                {
                    Color c = i == 0 ? (_follow ? _windows : _ringFirst ? ThemeHelper.Palette.Accent : _default) : i == CustomIndex ? _custom.Value : _presets[i - 1];
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
                    // a swatch close to the card's colour (white on white, black on black) gets an outline
                    if (ColorMath.Contrast(c, back) < 1.4)
                        using (var pen = new Pen(p.Divider, Dpi.F(1f, dpi))) g.DrawEllipse(pen, r);
                    if (i == 0 && (_follow || _ringFirst))
                    {
                        // "Follow Windows": a thin ring inside the swatch marks it as the system's colour, not a fixed one.
                        var inner = Rectangle.Inflate(r, -Dpi.Px(6, dpi), -Dpi.Px(6, dpi));
                        using (var pen = new Pen(ColorMath.OnColor(c), Dpi.F(1.5f, dpi))) g.DrawEllipse(pen, inner);
                    }
                    if (i == CustomIndex)
                    {
                        // a chosen custom colour keeps a small wheel badge, so it still reads as "Custom…"
                        int bd = Dpi.Px(12, dpi);
                        DrawWheel(g, new Rectangle(r.Right - bd + Dpi.Px(2, dpi), r.Bottom - bd + Dpi.Px(2, dpi), bd, bd));
                    }
                }
                if (i == _index)
                {
                    var sel = Rectangle.Inflate(r, Dpi.Px(Ring, dpi), Dpi.Px(Ring, dpi));
                    using (var pen = new Pen(p.Text, Dpi.F(2f, dpi))) g.DrawEllipse(pen, sel);
                }
            }
            if (Focused && ShowFocusCues && _index >= 0)
            {
                var r = Rectangle.Inflate(Swatch(_index, dpi), Dpi.Px(Ring + 3, dpi), Dpi.Px(Ring + 3, dpi));
                using (var pen = new Pen(p.AccentGlyph, Dpi.F(1.5f, dpi)) { DashStyle = DashStyle.Dot }) g.DrawEllipse(pen, r);
            }
        }

        /// <summary>A small colour wheel: hues around the rim, white at the centre.</summary>
        private static void DrawWheel(Graphics g, Rectangle r)
        {
            const int n = 24;
            var pts = new PointF[n];
            var cols = new Color[n];
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, rad = r.Width / 2f;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * rad), cy + (float)(Math.Sin(a) * rad));
                cols[i] = ColorPickerPanelHue(360f * i / n);
            }
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var pb = new PathGradientBrush(pts) { CenterColor = Color.White, SurroundColors = cols })
                {
                    var state = g.Save();   // (g.Clip hands out a new Region on every read: one leaked per paint)
                    g.SetClip(path, CombineMode.Intersect);
                    g.FillEllipse(pb, Rectangle.Inflate(r, 1, 1));
                    g.Restore(state);
                }
            }
        }

        private static Color ColorPickerPanelHue(float h) { return TelegArm.UI.ColorPickerPanel.FromHsv(h, 1f, 1f); }
    }
}
