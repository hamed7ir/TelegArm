using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted "attach" (paperclip) button, drawn with GDI+ shapes (no glyph
    /// fonts) so it renders identically on 8.1/10/ARM32 — same approach as
    /// MediaControlButton. Resolves all colors through ThemeHelper, repaints on
    /// ThemeChanged, and unsubscribes in Dispose (theming correct by construction).
    ///
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI), drawn at the button's DPI (k = dpi/96, the GlyphButton
    /// pattern; the window's DPI once parented); IDpiAware SETS the 40x40 from the logical value. Identity at 96.
    /// </summary>
    public class AttachButton : Control, IDpiAware
    {
        private const int Side = 40;   // logical px

        private readonly Fade _hover;   // 0..1: the hover disc fades (Helpers.Anim)
        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public AttachButton()
        {
            _hover = new Fade(this, AnimKind.Hover, Anim.HoverInMs, Anim.HoverOutMs);
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

        protected override void OnMouseEnter(EventArgs e) { _hover.Set(true); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Set(false); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            float k = dpi / 96f;   // exactly 1 at 96
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Match the surrounding input row (inherits the themed form background).
            Color bg = Parent != null ? Parent.BackColor
                       : ThemeHelper.Palette.GlyphButtonBg;
            WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, bg);   // step 5.9: the window backdrop when it is on

            Color glyph = ThemeHelper.Palette.AccentGlyph;
            if (!Enabled)
                glyph = ThemeHelper.Palette.GlyphButtonDisabled;
            else if (_hover.T > 0f)
            {
                using (var hb = new SolidBrush(ColorMath.WithAlpha(glyph, (int)Math.Round((ThemeHelper.Palette.Dark ? 45 : 30) * _hover.T))))
                    g.FillEllipse(hb, P(2), P(2), Width - P(4), Height - P(4));
            }

            float cx = Width / 2f, cy = Height / 2f, r = 4.5f * k;
            float top = cy - 8f * k, bottom = cy + 9f * k;
            using (var pen = new Pen(glyph, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var path = new GraphicsPath())
            {
                // Paperclip body: rounded top cap, both sides, rounded bottom cap.
                path.AddArc(cx - r, top, 2 * r, 2 * r, 180, 180);          // top cap (left → right)
                path.AddLine(cx + r, top + r, cx + r, bottom - r);        // right side down
                path.AddArc(cx - r, bottom - 2 * r, 2 * r, 2 * r, 0, 180); // bottom cap (right → left)
                path.AddLine(cx - r, bottom - r, cx - r, top + r);        // left side up
                g.DrawPath(pen, path);
                // Inner clasp arm.
                g.DrawLine(pen, cx, top + r + 1 * k, cx, bottom - 2 * r);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
