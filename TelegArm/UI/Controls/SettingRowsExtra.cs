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

    /// <summary>Redesign step 2.6 — the accent picker of Settings → Appearance: "Follow Windows" (the first swatch, drawn in
    /// the accent Windows has right now, with a ring inside it) and a row of fixed accents. Tap, or Left/Right, picks one
    /// and raises <see cref="Changed"/>; <see cref="SelectedAccent"/> is null for "Follow Windows". The custom colour picker
    /// is step 5.2. The caption sits above the swatches.</summary>
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
        private int _index;               // 0 = follow Windows
        private Color _windows;           // the Windows accent, read when built (never in paint)
        public event EventHandler Changed;

        public SwatchRow(string caption, Color? current) : base(caption, true)
        {
            _windows = ThemeHelper.ReadWindowsAccent();
            _index = 0;
            if (current.HasValue)
            {
                for (int i = 0; i < Accents.Length; i++) if (Accents[i].ToArgb() == current.Value.ToArgb()) _index = i + 1;
                if (_index == 0) _index = -1;   // a custom accent (env / settings): none of the swatches is lit
            }
            Refit();
        }

        public Color? SelectedAccent { get { return _index <= 0 ? (Color?)null : Accents[_index - 1]; } }

        /// <summary>Re-reads the Windows accent (after Windows changed it).</summary>
        public void RefreshWindowsAccent() { _windows = ThemeHelper.ReadWindowsAccent(); Invalidate(); }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(Height2, dpi); }

        private Rectangle Swatch(int i, int dpi)
        {
            int d = Dpi.Px(Diameter, dpi), step = d + Dpi.Px(Gap, dpi);
            int top = Height - Dpi.Px(14, dpi) - d;
            return new Rectangle(Dpi.Px(PadX, dpi) + Dpi.Px(Ring, dpi) + i * step, top, d, d);
        }

        private int Count { get { return Accents.Length + 1; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int dpi = Dpi.Of(this);
            for (int i = 0; i < Count; i++)
                if (Rectangle.Inflate(Swatch(i, dpi), Dpi.Px(4, dpi), Dpi.Px(4, dpi)).Contains(e.Location)) { Pick(i); return; }
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
            else if (e.KeyCode == Keys.Right) { Pick(Math.Min(Count - 1, _index + 1)); e.Handled = true; }
        }

        private void Pick(int i)
        {
            if (i == _index) return;
            _index = i;
            Invalidate();
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override string AccessibleValueText { get { return _index == 0 ? "Follow Windows" : _index < 0 ? "Custom" : ColorMath.Hex(Accents[_index - 1]); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            int capH = Dpi.Px(SettingRow.DefaultHeight, dpi) - Dpi.Px(14, dpi);
            DrawCaption(g, p, new Rectangle(padX, Dpi.Px(4, dpi), Math.Max(1, Width - 2 * padX), capH), dpi);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Count; i++)
            {
                var r = Swatch(i, dpi);
                Color c = i == 0 ? _windows : Accents[i - 1];
                using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
                if (i == 0)
                {
                    // "Follow Windows": a thin ring inside the swatch marks it as the system's colour, not a fixed one.
                    var inner = Rectangle.Inflate(r, -Dpi.Px(6, dpi), -Dpi.Px(6, dpi));
                    using (var pen = new Pen(ColorMath.OnColor(c), Dpi.F(1.5f, dpi))) g.DrawEllipse(pen, inner);
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
                using (var pen = new Pen(p.Accent, Dpi.F(1.5f, dpi)) { DashStyle = DashStyle.Dot }) g.DrawEllipse(pen, r);
            }
        }
    }
}
