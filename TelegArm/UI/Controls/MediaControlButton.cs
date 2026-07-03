using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    public enum MediaButtonIcon { Play, Pause, Previous, Next, Volume, VolumeMute, Forward, Close }

    /// <summary>
    /// A media control button drawn entirely with GDI+ shapes — no fonts/glyphs —
    /// so it looks identical on Windows 8.1, 10, x86/x64 and ARM32.
    /// </summary>
    public class MediaControlButton : Control
    {
        private MediaButtonIcon _icon;
        private bool _active;
        private bool _hover;

        public MediaButtonIcon Icon
        {
            get { return _icon; }
            set { _icon = value; Invalidate(); }
        }

        /// <summary>When true the button is filled with the accent color (e.g. play/pause).</summary>
        public bool IsActive
        {
            get { return _active; }
            set { _active = value; Invalidate(); }
        }

        public MediaControlButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(36, 36);
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(28, 28, 28); // matches the control bar
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Color bg = _hover ? Color.FromArgb(80, 80, 80) : Color.FromArgb(45, 45, 48);
            if (_active) bg = ThemeHelper.GetWindowsAccentColor();
            using (var brush = new SolidBrush(bg))
                g.FillEllipse(brush, 2, 2, Width - 4, Height - 4);

            float cx = Width / 2f;
            float cy = Height / 2f;
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 2f))
            {
                switch (_icon)
                {
                    case MediaButtonIcon.Play:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 4, cy - 6), new PointF(cx + 7, cy), new PointF(cx - 4, cy + 6)
                        });
                        break;

                    case MediaButtonIcon.Pause:
                        g.FillRectangle(white, cx - 6, cy - 6, 4, 12);
                        g.FillRectangle(white, cx + 2, cy - 6, 4, 12);
                        break;

                    case MediaButtonIcon.Previous:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx + 4, cy - 6), new PointF(cx - 4, cy), new PointF(cx + 4, cy + 6)
                        });
                        g.FillRectangle(white, cx - 7, cy - 6, 3, 12);
                        break;

                    case MediaButtonIcon.Next:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 4, cy - 6), new PointF(cx + 4, cy), new PointF(cx - 4, cy + 6)
                        });
                        g.FillRectangle(white, cx + 4, cy - 6, 3, 12);
                        break;

                    case MediaButtonIcon.Volume:
                        g.FillRectangle(white, cx - 7, cy - 3, 4, 6);
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 3, cy - 5), new PointF(cx + 2, cy - 8),
                            new PointF(cx + 2, cy + 8), new PointF(cx - 3, cy + 5)
                        });
                        g.DrawArc(pen, cx + 2, cy - 5, 6, 10, -60, 120);
                        break;

                    case MediaButtonIcon.VolumeMute:
                        g.FillRectangle(white, cx - 7, cy - 3, 4, 6);
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 3, cy - 5), new PointF(cx + 2, cy - 8),
                            new PointF(cx + 2, cy + 8), new PointF(cx - 3, cy + 5)
                        });
                        using (var red = new Pen(Color.Red, 2f))
                        {
                            g.DrawLine(red, cx + 4, cy - 4, cx + 9, cy + 4);
                            g.DrawLine(red, cx + 9, cy - 4, cx + 4, cy + 4);
                        }
                        break;

                    case MediaButtonIcon.Forward:
                        // Right-pointing arrow (shaft + head).
                        using (var p2 = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            g.DrawLine(p2, cx - 7, cy, cx + 6, cy);
                            g.DrawLines(p2, new[] { new PointF(cx + 1, cy - 5), new PointF(cx + 6, cy), new PointF(cx + 1, cy + 5) });
                        }
                        break;

                    case MediaButtonIcon.Close:
                        using (var p3 = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawLine(p3, cx - 5, cy - 5, cx + 5, cy + 5);
                            g.DrawLine(p3, cx + 5, cy - 5, cx - 5, cy + 5);
                        }
                        break;
                }
            }
        }
    }

    /// <summary>A double-buffered, owner-painted seek bar (no flicker on frequent invalidation).</summary>
    public class SeekBar : Panel
    {
        public SeekBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }
}
