using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Mic button visual: idle mic, red stop (recording), or discard X (ready-to-send).</summary>
    public enum MicMode { Mic, Stop, Discard }

    /// <summary>
    /// Owner-painted microphone button (GDI+, no glyph fonts) for recording voice notes.
    /// Mic = accent mic; Stop = red square; Discard = red X. Themed via ThemeHelper.
    ///
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI), drawn at the button's DPI (Px for the int rectangles,
    /// k = dpi/96 for the float glyph — the GlyphButton pattern); IDpiAware SETS the 40x40 from the logical value.
    /// Identity at 96.
    /// </summary>
    public class MicButton : Control, IDpiAware
    {
        private const int Side = 40;   // logical px

        private bool _hover;
        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        /// <summary>Current visual/behavioural mode.</summary>
        public MicMode Mode { get; set; } = MicMode.Mic;

        public MicButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));
            Cursor = Cursors.Hand;
            TabStop = false;
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)Invalidate); } catch { }
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Size = new Size(Dpi.Px(Side, d), Dpi.Px(Side, d)); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Size = new Size(Dpi.Px(Side, newDpi), Dpi.Px(Side, newDpi));   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            float k = dpi / 96f;   // exactly 1 at 96
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color bg = Parent != null ? Parent.BackColor
                       : ThemeHelper.Palette.MicButtonBg;
            g.Clear(bg);

            float cx = Width / 2f, cy = Height / 2f;
            var red = ThemeHelper.Palette.RecordRed;

            if (Mode == MicMode.Stop)
            {
                // Red stop square — tap to stop (then review & Send).
                using (var b = new SolidBrush(red))
                using (var path = DrawHelper.RoundedRect(new Rectangle((int)cx - P(7), (int)cy - P(7), P(14), P(14)), P(3)))
                    g.FillPath(b, path);
                return;
            }

            if (Mode == MicMode.Discard)
            {
                // Red X — tap to discard the recorded clip.
                if (_hover)
                    using (var hb = new SolidBrush(ColorMath.WithAlpha(red, ThemeHelper.Palette.Dark ? 45 : 30)))
                        g.FillEllipse(hb, P(2), P(2), Width - P(4), Height - P(4));
                using (var pen = new Pen(red, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - 6 * k, cy - 6 * k, cx + 6 * k, cy + 6 * k);
                    g.DrawLine(pen, cx + 6 * k, cy - 6 * k, cx - 6 * k, cy + 6 * k);
                }
                return;
            }

            Color glyph = Enabled ? ThemeHelper.Palette.Accent
                                  : ThemeHelper.Palette.MicGlyphDisabled;
            if (Enabled && _hover)
                using (var hb = new SolidBrush(ColorMath.WithAlpha(glyph, ThemeHelper.Palette.Dark ? 45 : 30)))
                    g.FillEllipse(hb, P(2), P(2), Width - P(4), Height - P(4));

            using (var b = new SolidBrush(glyph))
            using (var pen = new Pen(glyph, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Mic capsule.
                using (var cap = DrawHelper.RoundedRect(new Rectangle((int)cx - P(5), (int)cy - P(11), P(10), P(16)), P(5)))
                    g.FillPath(b, cap);
                // Holder arc + stem + base.
                g.DrawArc(pen, cx - 9 * k, cy - 5 * k, 18 * k, 14 * k, 25, 130);
                g.DrawLine(pen, cx, cy + 9 * k, cx, cy + 13 * k);
                g.DrawLine(pen, cx - 5 * k, cy + 13 * k, cx + 5 * k, cy + 13 * k);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
