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
    /// </summary>
    public class AttachButton : Control
    {
        private bool _hover;

        public AttachButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(40, 40);
            Cursor = Cursors.Hand;
            TabStop = false;
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)Invalidate); } catch { }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Match the surrounding input row (inherits the themed form background).
            Color bg = Parent != null ? Parent.BackColor
                       : (ThemeHelper.IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(250, 250, 250));
            g.Clear(bg);

            Color glyph = ThemeHelper.GetWindowsAccentColor();
            if (!Enabled)
                glyph = ThemeHelper.IsDark ? Color.FromArgb(90, 90, 94) : Color.FromArgb(190, 190, 196);
            else if (_hover)
            {
                using (var hb = new SolidBrush(Color.FromArgb(ThemeHelper.IsDark ? 45 : 30, glyph)))
                    g.FillEllipse(hb, 2, 2, Width - 4, Height - 4);
            }

            float cx = Width / 2f, cy = Height / 2f, r = 4.5f;
            float top = cy - 8f, bottom = cy + 9f;
            using (var pen = new Pen(glyph, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var path = new GraphicsPath())
            {
                // Paperclip body: rounded top cap, both sides, rounded bottom cap.
                path.AddArc(cx - r, top, 2 * r, 2 * r, 180, 180);          // top cap (left → right)
                path.AddLine(cx + r, top + r, cx + r, bottom - r);        // right side down
                path.AddArc(cx - r, bottom - 2 * r, 2 * r, 2 * r, 0, 180); // bottom cap (right → left)
                path.AddLine(cx - r, bottom - r, cx - r, top + r);        // left side up
                g.DrawPath(pen, path);
                // Inner clasp arm.
                g.DrawLine(pen, cx, top + r + 1, cx, bottom - 2 * r);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
