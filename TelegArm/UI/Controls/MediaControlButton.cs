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
    ///
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI), drawn at the button's DPI (k = dpi/96, the GlyphButton
    /// pattern; the window's DPI once parented); IDpiAware SETS the 36x36 from the logical value (every host also
    /// sizes it Px(36), so the two agree). Identity at 96.
    /// </summary>
    public class MediaControlButton : Control, IDpiAware
    {
        private const int Side = 36;   // logical px

        private MediaButtonIcon _icon;
        private bool _active;
        private bool _hover;
        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

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
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(28, 28, 28); // matches the control bar
            TabStop = false;
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
            g.Clear(BackColor);

            Color bg = _hover ? Color.FromArgb(80, 80, 80) : Color.FromArgb(45, 45, 48);
            if (_active) bg = ThemeHelper.GetWindowsAccentColor();
            using (var brush = new SolidBrush(bg))
                g.FillEllipse(brush, P(2), P(2), Width - P(4), Height - P(4));

            float cx = Width / 2f;
            float cy = Height / 2f;
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 2f * k))
            {
                switch (_icon)
                {
                    case MediaButtonIcon.Play:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 4 * k, cy - 6 * k), new PointF(cx + 7 * k, cy), new PointF(cx - 4 * k, cy + 6 * k)
                        });
                        break;

                    case MediaButtonIcon.Pause:
                        g.FillRectangle(white, cx - 6 * k, cy - 6 * k, 4 * k, 12 * k);
                        g.FillRectangle(white, cx + 2 * k, cy - 6 * k, 4 * k, 12 * k);
                        break;

                    case MediaButtonIcon.Previous:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx + 4 * k, cy - 6 * k), new PointF(cx - 4 * k, cy), new PointF(cx + 4 * k, cy + 6 * k)
                        });
                        g.FillRectangle(white, cx - 7 * k, cy - 6 * k, 3 * k, 12 * k);
                        break;

                    case MediaButtonIcon.Next:
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 4 * k, cy - 6 * k), new PointF(cx + 4 * k, cy), new PointF(cx - 4 * k, cy + 6 * k)
                        });
                        g.FillRectangle(white, cx + 4 * k, cy - 6 * k, 3 * k, 12 * k);
                        break;

                    case MediaButtonIcon.Volume:
                        g.FillRectangle(white, cx - 7 * k, cy - 3 * k, 4 * k, 6 * k);
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 3 * k, cy - 5 * k), new PointF(cx + 2 * k, cy - 8 * k),
                            new PointF(cx + 2 * k, cy + 8 * k), new PointF(cx - 3 * k, cy + 5 * k)
                        });
                        g.DrawArc(pen, cx + 2 * k, cy - 5 * k, 6 * k, 10 * k, -60, 120);
                        break;

                    case MediaButtonIcon.VolumeMute:
                        g.FillRectangle(white, cx - 7 * k, cy - 3 * k, 4 * k, 6 * k);
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx - 3 * k, cy - 5 * k), new PointF(cx + 2 * k, cy - 8 * k),
                            new PointF(cx + 2 * k, cy + 8 * k), new PointF(cx - 3 * k, cy + 5 * k)
                        });
                        using (var red = new Pen(Color.Red, 2f * k))
                        {
                            g.DrawLine(red, cx + 4 * k, cy - 4 * k, cx + 9 * k, cy + 4 * k);
                            g.DrawLine(red, cx + 9 * k, cy - 4 * k, cx + 4 * k, cy + 4 * k);
                        }
                        break;

                    case MediaButtonIcon.Forward:
                        // Right-pointing arrow (shaft + head).
                        using (var p2 = new Pen(Color.White, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            g.DrawLine(p2, cx - 7 * k, cy, cx + 6 * k, cy);
                            g.DrawLines(p2, new[] { new PointF(cx + 1 * k, cy - 5 * k), new PointF(cx + 6 * k, cy), new PointF(cx + 1 * k, cy + 5 * k) });
                        }
                        break;

                    case MediaButtonIcon.Close:
                        using (var p3 = new Pen(Color.White, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawLine(p3, cx - 5 * k, cy - 5 * k, cx + 5 * k, cy + 5 * k);
                            g.DrawLine(p3, cx + 5 * k, cy - 5 * k, cx - 5 * k, cy + 5 * k);
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
