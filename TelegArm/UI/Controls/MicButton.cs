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
    /// </summary>
    public class MicButton : Control
    {
        private bool _hover;

        /// <summary>Current visual/behavioural mode.</summary>
        public MicMode Mode { get; set; } = MicMode.Mic;

        public MicButton()
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

            Color bg = Parent != null ? Parent.BackColor
                       : (ThemeHelper.IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(250, 250, 250));
            g.Clear(bg);

            float cx = Width / 2f, cy = Height / 2f;
            var red = Color.FromArgb(229, 57, 53);

            if (Mode == MicMode.Stop)
            {
                // Red stop square — tap to stop (then review & Send).
                using (var b = new SolidBrush(red))
                using (var path = DrawHelper.RoundedRect(new Rectangle((int)cx - 7, (int)cy - 7, 14, 14), 3))
                    g.FillPath(b, path);
                return;
            }

            if (Mode == MicMode.Discard)
            {
                // Red X — tap to discard the recorded clip.
                if (_hover)
                    using (var hb = new SolidBrush(Color.FromArgb(ThemeHelper.IsDark ? 45 : 30, red)))
                        g.FillEllipse(hb, 2, 2, Width - 4, Height - 4);
                using (var pen = new Pen(red, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                    g.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
                }
                return;
            }

            Color glyph = Enabled ? ThemeHelper.GetWindowsAccentColor()
                                  : (ThemeHelper.IsDark ? Color.FromArgb(90, 90, 94) : Color.FromArgb(190, 190, 196));
            if (Enabled && _hover)
                using (var hb = new SolidBrush(Color.FromArgb(ThemeHelper.IsDark ? 45 : 30, glyph)))
                    g.FillEllipse(hb, 2, 2, Width - 4, Height - 4);

            using (var b = new SolidBrush(glyph))
            using (var pen = new Pen(glyph, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Mic capsule.
                using (var cap = DrawHelper.RoundedRect(new Rectangle((int)cx - 5, (int)cy - 11, 10, 16), 5))
                    g.FillPath(b, cap);
                // Holder arc + stem + base.
                g.DrawArc(pen, cx - 9, cy - 5, 18, 14, 25, 130);
                g.DrawLine(pen, cx, cy + 9, cx, cy + 13);
                g.DrawLine(pen, cx - 5, cy + 13, cx + 5, cy + 13);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }
}
