using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 5.2 — the in-app colour picker ("Custom…" for the accent and the theme bases): a
    /// saturation/value square, a hue strip, the old and new colour side by side, and a hex box. Touch-sized and themed;
    /// the native ColorDialog is light-only in dark mode and awkward on touch (plan 5.2).
    /// <paramref name="clamp"/> (optional) limits what can be chosen — a dark-theme base must stay dark — and the preview
    /// shows the colour as it will be applied. Enter = OK, Esc = Cancel.</summary>
    internal sealed class ColorPickerDialog : DialogForm
    {
        private const int W = 420, BodyH = 290;
        private readonly ColorPickerPanel _panel;

        public static Color? Pick(IWin32Window owner, string title, Color initial, Func<Color, Color> clamp = null)
        {
            using (var d = new ColorPickerDialog(title, initial, clamp))
                return d.ShowDialog(owner) == DialogResult.OK ? d._panel.Applied : (Color?)null;
        }

        internal ColorPickerDialog(string title, Color initial, Func<Color, Color> clamp) : base(title, W, 420)   // internal: the rig shoots it
        {
            _panel = new ColorPickerPanel(initial, clamp);
            UseOwnBody(_panel);
            var ok = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.OK);
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = ok;
            FocusOnShow(_panel);
            Size = new Size(Width, Bar.Height + Px(BodyH) + Px(FooterHeight) + 1);
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Size = new Size(Width, Bar.Height + Px(BodyH) + Px(FooterHeight) + 1);
        }

        /// <summary>The gallery probe drives the panel directly.</summary>
        internal static ColorPickerPanel ProbePanel(Color initial, Func<Color, Color> clamp) { return new ColorPickerPanel(initial, clamp); }
    }

    /// <summary>The picker's body. Logical layout: a 16 px margin; the SV square fills the width left of a 24 px hue strip
    /// (12 px gap), 200 px tall; under it the old|new preview and the hex box.</summary>
    internal sealed class ColorPickerPanel : Control, IDpiAware
    {
        private const int Pad = 16, SqTop = 12, SquareH = 200, StripW = 24, Gap = 12, RowY = SqTop + SquareH + 18, RowH = 40, PreviewW = 96;
        private readonly Color _old;
        private readonly Func<Color, Color> _clamp;
        private readonly ThemedTextBox _hex;
        private float _h, _s, _v;   // hue 0-360, saturation and value 0-1
        private Bitmap _sv;         // the square for the current hue, at the current pixel size
        private float _svHue = -1;
        private int _drag;          // 0 none, 1 square, 2 strip
        private bool _syncing;

        public event EventHandler ColorChanged;

        public ColorPickerPanel(Color initial, Func<Color, Color> clamp)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = "Colour";
            AccessibleRole = AccessibleRole.Grouping;
            _old = ColorMath.Rgb(initial.R, initial.G, initial.B);   // opaque
            _clamp = clamp;
            BackColor = ThemeHelper.Palette.Card;   // the hex box fills its rounded corners with its parent's BackColor
            ToHsv(_old, out _h, out _s, out _v);
            _hex = new ThemedTextBox(false) { Hint = "#RRGGBB", AccessibleName = "Hex colour" };
            _hex.TextChanged += (s, e) => OnHexTyped();
            Controls.Add(_hex);
            SyncHex();
        }

        /// <summary>The colour as chosen in the square / strip / hex box.</summary>
        public Color Chosen { get { return FromHsv(_h, _s, _v); } }

        /// <summary>The colour that will be applied: <see cref="Chosen"/> through the clamp.</summary>
        public Color Applied { get { var c = Chosen; return _clamp != null ? _clamp(c) : c; } }

        /// <summary>The hex text box (the probe types into it).</summary>
        internal ThemedTextBox HexBox { get { return _hex; } }

        private int Dpi_ { get { return Dpi.Of(this); } }
        private int P(int v) { return Dpi.Px(v, Dpi_); }

        private Rectangle Square { get { return new Rectangle(P(Pad), P(SqTop), Math.Max(1, Width - P(Pad) * 2 - P(StripW) - P(Gap)), P(SquareH)); } }
        private Rectangle Strip { get { var q = Square; return new Rectangle(q.Right + P(Gap), q.Top, P(StripW), q.Height); } }
        private Rectangle Preview { get { return new Rectangle(P(Pad), P(RowY), P(PreviewW), P(RowH)); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = P(Pad) + P(PreviewW) + P(Gap);
            _hex.SetBounds(x, P(RowY), Math.Max(P(60), Math.Min(P(160), Width - x - P(Pad))), P(RowH));
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { DropSquare(); PerformLayout(); Invalidate(); }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); DropSquare(); }

        private void DropSquare() { if (_sv != null) { _sv.Dispose(); _sv = null; } }

        // ── input ──────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (Rectangle.Inflate(Square, P(6), P(6)).Contains(e.Location)) _drag = 1;
            else if (Rectangle.Inflate(Strip, P(8), P(6)).Contains(e.Location)) _drag = 2;
            else return;
            Capture = true;
            DragTo(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag != 0) DragTo(e.Location); }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = 0; Capture = false; }

        private void DragTo(Point pt)
        {
            if (_drag == 1)
            {
                var q = Square;
                _s = Clamp01((pt.X - q.Left) / (float)Math.Max(1, q.Width - 1));
                _v = 1f - Clamp01((pt.Y - q.Top) / (float)Math.Max(1, q.Height - 1));
            }
            else if (_drag == 2)
            {
                var r = Strip;
                _h = 360f * Clamp01((pt.Y - r.Top) / (float)Math.Max(1, r.Height - 1));
            }
            Changed(true);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            float step = e.Shift ? 0.1f : 0.01f;
            switch (e.KeyCode)
            {
                case Keys.Left: _s = Clamp01(_s - step); break;
                case Keys.Right: _s = Clamp01(_s + step); break;
                case Keys.Up: _v = Clamp01(_v + step); break;
                case Keys.Down: _v = Clamp01(_v - step); break;
                case Keys.PageUp: _h = (_h + 355f) % 360f; break;
                case Keys.PageDown: _h = (_h + 5f) % 360f; break;
                default: return;
            }
            e.Handled = true;
            Changed(true);
        }

        private void OnHexTyped()
        {
            if (_syncing) return;
            var c = ThemeHelper.ParseAccent(_hex.Text);
            if (!c.HasValue) return;
            ToHsv(c.Value, out _h, out _s, out _v);
            Changed(false);
        }

        private void Changed(bool writeHex)
        {
            if (writeHex) SyncHex();
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = ColorChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void SyncHex()
        {
            _syncing = true;
            try { _hex.Text = ColorMath.Hex(Chosen); } finally { _syncing = false; }
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new PanelAccessible(this); }

        private sealed class PanelAccessible : ControlAccessibleObject
        {
            private readonly ColorPickerPanel _p;
            public PanelAccessible(ColorPickerPanel p) : base(p) { _p = p; }
            public override string Value { get { return ColorMath.Hex(_p.Applied); } set { } }
        }

        // ── paint ──────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            if (BackColor != p.Card) BackColor = p.Card;   // a theme switch while the picker is open
            g.Clear(p.Card);
            var q = Square;
            if (_sv == null || _svHue != _h || _sv.Width != q.Width || _sv.Height != q.Height) BuildSquare(q.Size);
            g.DrawImageUnscaled(_sv, q.Location);

            var strip = Strip;
            using (var lg = new LinearGradientBrush(strip, Color.Red, Color.Red, LinearGradientMode.Vertical))
            {
                var stops = new Color[7]; var pos = new float[7];
                for (int i = 0; i < 7; i++) { stops[i] = FromHsv(i * 60f % 360f, 1f, 1f); pos[i] = i / 6f; }
                lg.InterpolationColors = new ColorBlend { Colors = stops, Positions = pos };
                g.FillRectangle(lg, strip);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            // the square's marker: a white ring inside a dark one, readable on any colour
            int mx = q.Left + (int)Math.Round(_s * (q.Width - 1)), my = q.Top + (int)Math.Round((1 - _v) * (q.Height - 1)), r = P(7);
            using (var dark = new Pen(ColorMath.DarkInk, Dpi.F(3f, Dpi_))) g.DrawEllipse(dark, mx - r, my - r, 2 * r, 2 * r);
            using (var white = new Pen(Color.White, Dpi.F(1.5f, Dpi_))) g.DrawEllipse(white, mx - r, my - r, 2 * r, 2 * r);
            // the strip's marker: a bar across it
            int hy = strip.Top + (int)Math.Round(_h / 360f * (strip.Height - 1));
            var bar = new Rectangle(strip.Left - P(3), hy - P(3), strip.Width + P(6), P(6));
            using (var dark = new Pen(ColorMath.DarkInk, Dpi.F(2.5f, Dpi_))) g.DrawRectangle(dark, bar);
            using (var white = new Pen(Color.White, Dpi.F(1f, Dpi_))) g.DrawRectangle(white, bar);

            // old | new, as it will be applied
            var pv = Preview;
            var left = new Rectangle(pv.Left, pv.Top, pv.Width / 2, pv.Height);
            var right = new Rectangle(left.Right, pv.Top, pv.Width - left.Width, pv.Height);
            using (var b = new SolidBrush(_old)) g.FillRectangle(b, left);
            using (var b = new SolidBrush(Applied)) g.FillRectangle(b, right);
            using (var pen = new Pen(p.Divider)) g.DrawRectangle(pen, pv.Left, pv.Top, pv.Width - 1, pv.Height - 1);

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(p.Accent, Dpi.F(1.5f, Dpi_)) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, Rectangle.Inflate(q, P(3), P(3)));
        }

        /// <summary>The SV square for the current hue: x = saturation, y = 1 - value. Built once per hue and size.</summary>
        private void BuildSquare(Size size)
        {
            DropSquare();
            int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new int[w];
                var hue = FromHsv(_h, 1f, 1f);
                for (int y = 0; y < h; y++)
                {
                    float v = 1f - y / (float)Math.Max(1, h - 1);
                    for (int x = 0; x < w; x++)
                    {
                        float s = x / (float)Math.Max(1, w - 1);
                        // s blends white→hue, v scales toward black: the HSV square without a per-pixel HSV conversion
                        int rr = (int)((255 + (hue.R - 255) * s) * v + 0.5f), gg = (int)((255 + (hue.G - 255) * s) * v + 0.5f), bb = (int)((255 + (hue.B - 255) * s) * v + 0.5f);
                        row[x] = (rr << 16) | (gg << 8) | bb;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
                }
            }
            finally { bmp.UnlockBits(data); }
            _sv = bmp;
            _svHue = _h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropSquare();
            base.Dispose(disposing);
        }

        // ── HSV ────────────────────────────────────────────────────────────────────────────────────────────────

        private static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        internal static void ToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0) h += 360f;
        }

        internal static Color FromHsv(float h, float s, float v)
        {
            float c = v * s, x = c * (1 - Math.Abs(h / 60f % 2f - 1)), m = v - c;
            float r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return ColorMath.Rgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }
    }
}
