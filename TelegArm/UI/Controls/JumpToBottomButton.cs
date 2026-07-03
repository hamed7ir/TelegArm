using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Floating "scroll to bottom" button (owner-painted, RT-safe) shown over the message panel when the
    /// user is scrolled up: a round chevron-down button with an optional unread-count badge. Click to
    /// jump to the latest message. Themed via <see cref="AccentColor"/> / <see cref="IsDark"/>.
    /// </summary>
    public sealed class JumpToBottomButton : Control
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        private int _count;
        /// <summary>New-message count shown in the badge (0 = no badge).</summary>
        public int UnreadCount
        {
            get { return _count; }
            set { if (_count != value) { _count = value; if (!IsDisposed) { RebuildRegion(); Invalidate(); } } }
        }

        public JumpToBottomButton()
        {
            Size = new Size(44, 44);
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            RebuildRegion();   // cached pre-handle; applied when the handle is created
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RebuildRegion();
        }

        /// <summary>Badge bounds for the given text — shared by paint and region so they can never drift.
        /// The badge overlaps OUTSIDE the circle (top-right corner), so the region must union it.</summary>
        private Rectangle BadgeRect(string t, Font bf)
        {
            int bw = Math.Max(16, TextRenderer.MeasureText(t, bf).Width + 8);
            return new Rectangle(Width - bw, 0, bw, 16);
        }

        /// <summary>SCROLLBTN-REGION: the hwnd is square, and WinForms "transparency" only copies the
        /// PARENT background — over sibling bubbles the corners rendered as an opaque box. A circular
        /// window Region (∪ the badge's rounded-rect while visible) makes the corners not exist at all.
        /// The region ellipse sits ~1px OUTSIDE the painted AA rim (and OnPaint clears with the button's
        /// own fill, never the parent background), so the aliased region edge hides in a fill-colored
        /// halo. The Region is also the CLICK boundary: Ø38px circle + badge — the square's corners were
        /// visually dead but tappable before; Ø38 still meets the ~40px touch-target bar.</summary>
        private int _regionForCount = -1;   // the count the current Region was built for (self-heal key)

        private void RebuildRegion()
        {
            // WINDING is load-bearing: the default (Alternate/even-odd) XORs overlapping figures, which
            // punched a HOLE in the region exactly where the badge pill overlaps the circle — the chat
            // background showed through the bite (field screenshot: badge visible only outside the rim).
            var path = new GraphicsPath { FillMode = FillMode.Winding };
            int d = Math.Min(Width, Height) - 6;   // painted circle is -8: region rim rides 1px outside it
            path.AddEllipse((Width - d) / 2f, (Height - d) / 2f, d, d);
            if (_count > 0)
            {
                string t = _count > 99 ? "99+" : _count.ToString();
                using (var bf = FontHelper.Ui(7.5f, FontStyle.Bold))
                {
                    var badge = BadgeRect(t, bf);
                    badge.Inflate(2, 2);   // keep the badge's own AA edge safely inside the region
                    using (var bp = DrawHelper.RoundedRect(badge, 10)) path.AddPath(bp, false);
                }
            }
            _regionForCount = _count;
            var old = Region;
            Region = new Region(path);   // Region(GraphicsPath) copies — safe to dispose the path
            path.Dispose();
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Self-heal: if the badge state changed without the Region following (field evidence:
            // a badge painted as a clipped crescent = badge ∩ ellipse-only region), rebuild NOW —
            // whatever path went stale, paint and region can never disagree for more than one frame.
            if (_regionForCount != _count) RebuildRegion();

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int d = Math.Min(Width, Height) - 8;
            var circle = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);

            Color fill = IsDark ? Color.FromArgb(54, 54, 58) : Color.White;
            // Clear with the button's OWN fill (not the parent background): with the circular Region,
            // every surviving pixel belongs to the button — background color must never show anywhere.
            g.Clear(fill);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, circle);
            using (var p = new Pen(IsDark ? Color.FromArgb(82, 82, 86) : Color.FromArgb(218, 218, 222)))
                g.DrawEllipse(p, circle);

            float cx = circle.X + d / 2f, cy = circle.Y + d / 2f;
            using (var pen = new Pen(AccentColor, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(cx - 6, cy - 3), new PointF(cx, cy + 4), new PointF(cx + 6, cy - 3) });

            if (_count > 0)
            {
                string t = _count > 99 ? "99+" : _count.ToString();
                using (var bf = FontHelper.Ui(7.5f, FontStyle.Bold))
                {
                    var badge = BadgeRect(t, bf);
                    using (var bb = new SolidBrush(AccentColor))
                    using (var pth = DrawHelper.RoundedRect(badge, 8)) g.FillPath(bb, pth);
                    TextRenderer.DrawText(g, t, bf, badge, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
