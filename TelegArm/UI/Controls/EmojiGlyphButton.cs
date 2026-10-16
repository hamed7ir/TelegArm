using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Owner-painted smiley button (GDI+) that opens the emoji picker. Themed.
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI), drawn at the button's DPI (k = dpi/96, the GlyphButton
    /// pattern; the window's DPI once parented); IDpiAware SETS the 40x40 from the logical value. Identity at 96.</summary>
    public class EmojiGlyphButton : Control, IDpiAware
    {
        private const int Side = 40;   // logical px

        private bool _hover;
        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public EmojiGlyphButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));
            Cursor = Cursors.Hand;
            TabStop = false;
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged() { if (!IsDisposed) try { BeginInvoke((Action)Invalidate); } catch { } }

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
                       : ThemeHelper.Palette.GlyphButtonBg;
            g.Clear(bg);

            Color c = Enabled ? ThemeHelper.Palette.Accent
                              : ThemeHelper.Palette.GlyphButtonDisabled;
            if (Enabled && _hover)
                using (var hb = new SolidBrush(ColorMath.WithAlpha(c, ThemeHelper.Palette.Dark ? 45 : 30)))
                    g.FillEllipse(hb, P(2), P(2), Width - P(4), Height - P(4));

            float cx = Width / 2f, cy = Height / 2f, r = 9f * k;
            using (var pen = new Pen(c, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var b = new SolidBrush(c))
            {
                g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);                     // face
                g.FillEllipse(b, cx - 4.5f * k, cy - 3.5f * k, 2.2f * k, 2.2f * k);   // eyes
                g.FillEllipse(b, cx + 2.3f * k, cy - 3.5f * k, 2.2f * k, 2.2f * k);
                g.DrawArc(pen, cx - 5 * k, cy - 3 * k, 10 * k, 8 * k, 20, 140);        // smile
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
