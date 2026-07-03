using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Owner-painted smiley button (GDI+) that opens the emoji picker. Themed.</summary>
    public class EmojiGlyphButton : Control
    {
        private bool _hover;

        public EmojiGlyphButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(40, 40);
            Cursor = Cursors.Hand;
            TabStop = false;
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged() { if (!IsDisposed) try { BeginInvoke((Action)Invalidate); } catch { } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Parent != null ? Parent.BackColor
                       : (ThemeHelper.IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(250, 250, 250));
            g.Clear(bg);

            Color c = Enabled ? ThemeHelper.GetWindowsAccentColor()
                              : (ThemeHelper.IsDark ? Color.FromArgb(90, 90, 94) : Color.FromArgb(190, 190, 196));
            if (Enabled && _hover)
                using (var hb = new SolidBrush(Color.FromArgb(ThemeHelper.IsDark ? 45 : 30, c)))
                    g.FillEllipse(hb, 2, 2, Width - 4, Height - 4);

            float cx = Width / 2f, cy = Height / 2f, r = 9f;
            using (var pen = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var b = new SolidBrush(c))
            {
                g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);     // face
                g.FillEllipse(b, cx - 4.5f, cy - 3.5f, 2.2f, 2.2f);   // eyes
                g.FillEllipse(b, cx + 2.3f, cy - 3.5f, 2.2f, 2.2f);
                g.DrawArc(pen, cx - 5, cy - 3, 10, 8, 20, 140);        // smile
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
