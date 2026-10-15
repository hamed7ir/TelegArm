using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 3.8b — a caption, its value ("125%") and an owner-drawn slider with detents (the interface
    /// scale: 50-200% in 25% steps). Owner-drawn on purpose: the native TrackBar is light-only on RT 8.1.
    ///
    /// ★ DRAG PREVIEWS, RELEASE COMMITS. While the thumb is dragged only <see cref="Previewing"/> fires (the value text
    ///   follows the finger); <see cref="ValueChanged"/> fires once, on release or on a key. Rescaling every window for
    ///   each detent crossed would be far too slow on a Tegra 3.
    /// ★ A LIMIT (the per-screen fit cap, plan 3.8b): detents above <see cref="Limit"/> are drawn dim and the UI cannot
    ///   pick them; what the setting holds is the page's business.
    /// Keyboard: Left / Down one detent down, Right / Up one up, Home / End the ends. Accessible role: Slider.</summary>
    public sealed class SliderRow : SettingRow
    {
        private const int TrackArea = 40, TrackH = 4, ThumbD = 20, TickD = 6, ValueGap = 12;
        private readonly int _min, _max, _step;
        private int _value, _preview = -1, _limit;
        private bool _dragging;

        /// <summary>The committed value changed (a release, a key, or code with raise = true).</summary>
        public event EventHandler ValueChanged;

        /// <summary>The thumb is being dragged over a new detent (<see cref="ShownValue"/>); nothing is committed yet.</summary>
        public event EventHandler Previewing;

        public SliderRow(string caption, int min, int max, int step, int value) : base(caption, true)
        {
            _min = min;
            _max = Math.Max(min, max);
            _step = Math.Max(1, step);
            _value = Clamp(Snap(value));
            AccessibleRole = AccessibleRole.Slider;
            Refit();
        }

        /// <summary>How a value is shown; default "N%".</summary>
        public Func<int, string> Format { get; set; }

        public int Minimum { get { return _min; } }
        public int Maximum { get { return _max; } }
        public int Step { get { return _step; } }

        /// <summary>The committed value. Setting it raises <see cref="ValueChanged"/> when it changes.</summary>
        public int Value { get { return _value; } set { SetValue(value, true); } }

        /// <summary>The value on screen: the dragged one during a drag, else <see cref="Value"/>.</summary>
        public int ShownValue { get { return _preview >= 0 ? _preview : _value; } }

        /// <summary>The highest value the UI may pick (0 = no limit); detents above it are drawn dim.</summary>
        public int Limit
        {
            get { return _limit; }
            set { if (_limit == value) return; _limit = value; Invalidate(); }
        }

        private int UiMax { get { return _limit > 0 ? Math.Max(_min, Math.Min(_max, _min + (_limit - _min) / _step * _step)) : _max; } }

        public void SetValue(int value, bool raise)
        {
            value = Math.Max(_min, Math.Min(_max, Snap(value)));
            if (value == _value) return;
            _value = value;
            if (!IsDisposed) Invalidate();
            if (!raise) return;
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private int Snap(int v) { return _min + (int)Math.Round((v - _min) / (double)_step, MidpointRounding.AwayFromZero) * _step; }
        private int Clamp(int v) { return Math.Max(_min, Math.Min(UiMax, v)); }

        private string TextOf(int v) { var f = Format; return f != null ? f(v) : v + "%"; }

        public override int HeightFor(int width, int dpi) { return RowHeight(dpi) + Dpi.Px(TrackArea, dpi); }

        protected override string AccessibleValueText { get { return TextOf(ShownValue); } }

        // ── geometry ──────────────────────────────────────────────────────────────────────────────────────────────

        private int TrackY(int dpi) { return RowHeight(dpi) + Dpi.Px(TrackArea, dpi) / 2 - Dpi.Px(4, dpi); }
        private int X0(int dpi) { return Dpi.Px(PadX, dpi) + Dpi.Px(ThumbD, dpi) / 2; }
        private int X1(int dpi) { return Width - Dpi.Px(PadX, dpi) - Dpi.Px(ThumbD, dpi) / 2; }

        private int XOf(int v, int dpi)
        {
            int x0 = X0(dpi), x1 = Math.Max(x0 + 1, X1(dpi));
            return x0 + (int)Math.Round((v - _min) * (double)(x1 - x0) / Math.Max(1, _max - _min));
        }

        private int ValueAt(int x, int dpi)
        {
            int x0 = X0(dpi), x1 = Math.Max(x0 + 1, X1(dpi));
            double t = (x - x0) / (double)(x1 - x0);
            return Clamp(Snap(_min + (int)Math.Round(t * (_max - _min))));
        }

        // ── input ─────────────────────────────────────────────────────────────────────────────────────────────────

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & (Keys.Alt | Keys.Control)) == 0)
            {
                var k = keyData & Keys.KeyCode;
                if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End) return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            int v = _value;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: v = _value - _step; break;
                case Keys.Right: case Keys.Up: v = _value + _step; break;
                case Keys.Home: v = _min; break;
                case Keys.End: v = UiMax; break;
                default: return;
            }
            SetValue(Clamp(v), true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            int dpi = Dpi.Of(this);
            int ty = TrackY(dpi), half = Dpi.Px(TrackArea, dpi) / 2 + Dpi.Px(4, dpi);
            if (e.Y < ty - half || e.Y > ty + half) return;   // the caption line is not the slider
            Focus();
            _dragging = true;
            Capture = true;
            MoveTo(ValueAt(e.X, dpi));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) MoveTo(ValueAt(e.X, Dpi.Of(this)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging) Commit();
        }

        /// <summary>A capture lost mid-drag (a dialog, Alt+Tab) commits what is shown: the user saw it as chosen.</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging && !Capture) Commit();
        }

        private void MoveTo(int v)
        {
            if (v == ShownValue && _preview >= 0) return;
            _preview = v;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = Previewing;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void Commit()
        {
            _dragging = false;
            if (Capture) Capture = false;
            int v = _preview;
            _preview = -1;
            if (v >= 0) SetValue(v, true);
            Invalidate();
        }

        // ── paint ─────────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            PaintBack(g, p);

            int padX = Dpi.Px(PadX, dpi), rowH = RowHeight(dpi);
            string valueText = TextOf(ShownValue);
            var valueFont = FontHelper.UiAt(CaptionPt, dpi, FontStyle.Bold);   // shared: never dispose
            int valueW = TextRenderer.MeasureText(valueText, valueFont, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
            DrawCaption(g, p, new Rectangle(padX, 0, Math.Max(1, Width - 2 * padX - valueW - Dpi.Px(ValueGap, dpi)), rowH), dpi);
            DrawTextLine(g, valueText, valueFont, Dimmed ? p.TextDisabled : p.Text,
                         new Rectangle(Width - padX - valueW, 0, valueW, rowH), true, false);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int ty = TrackY(dpi), th = Math.Max(2, Dpi.Px(TrackH, dpi)), x0 = X0(dpi), x1 = X1(dpi);
            int xs = XOf(ShownValue, dpi);
            Color accent = Enabled ? p.Accent : ColorMath.Mix(p.Accent, p.Window, 0.5f);
            using (var track = new SolidBrush(p.Divider))
            using (var path = DrawHelper.RoundedRect(new Rectangle(x0, ty - th / 2, Math.Max(1, x1 - x0), th), th / 2))
                g.FillPath(track, path);
            using (var fill = new SolidBrush(accent))
            using (var path = DrawHelper.RoundedRect(new Rectangle(x0, ty - th / 2, Math.Max(1, xs - x0), th), th / 2))
                g.FillPath(fill, path);

            // the detents: accent up to the value, track colour after it, faint beyond the limit
            int tick = Math.Max(3, Dpi.Px(TickD, dpi));
            for (int v = _min; v <= _max; v += _step)
            {
                int x = XOf(v, dpi);
                Color c = v <= ShownValue ? accent : v > UiMax ? ColorMath.Mix(p.Divider, p.Window, 0.6f) : p.Divider;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, x - tick / 2, ty - tick / 2, tick, tick);
            }

            // the thumb: an accent disc with the card colour inside it (reads on light and dark)
            int d = Dpi.Px(ThumbD, dpi), inner = Math.Max(2, d / 3);
            var thumb = new Rectangle(xs - d / 2, ty - d / 2, d, d);
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, thumb);
            using (var b = new SolidBrush(p.Card)) g.FillEllipse(b, xs - inner / 2, ty - inner / 2, inner, inner);
            g.SmoothingMode = SmoothingMode.None;
            PaintFocusRing(g, p, thumb, d / 2, dpi);   // (the limit shows as faint detents; the page explains it in the subtitle)
        }
    }
}
