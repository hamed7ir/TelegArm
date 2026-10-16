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
    ///
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI), drawn at the button's DPI (the window's once parented); the
    /// badge font is a shared FontCache font (never disposed). IDpiAware SETS the 44x44 from the logical value and
    /// rebuilds the Region (its badge metrics are DPI-dependent; the self-heal key includes the DPI). Identity at 96.
    /// </summary>
    public sealed class JumpToBottomButton : Control, IDpiAware
    {
        private const int Side = 44;   // logical px

        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

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
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            RebuildRegion();   // cached pre-handle; applied when the handle is created
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Size = new Size(Dpi.Px(Side, d), Dpi.Px(Side, d)); RebuildRegion(); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Size = new Size(Dpi.Px(Side, newDpi), Dpi.Px(Side, newDpi));   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            RebuildRegion();   // DpiScaler's resize rebuilt it at the OLD DPI (the callback comes after the bounds)
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RebuildRegion();
        }

        /// <summary>Badge bounds for the given text — shared by paint and region so they can never drift.
        /// The badge overlaps OUTSIDE the circle (top-right corner), so the region must union it.</summary>
        private Rectangle BadgeRect(string t, Font bf, int dpi)
        {
            int bw = Math.Max(Dpi.Px(16, dpi), TextRenderer.MeasureText(t, bf).Width + Dpi.Px(8, dpi));
            return new Rectangle(Width - bw, 0, bw, Dpi.Px(16, dpi));
        }

        /// <summary>SCROLLBTN-REGION: the hwnd is square, and WinForms "transparency" only copies the
        /// PARENT background — over sibling bubbles the corners rendered as an opaque box. A circular
        /// window Region (∪ the badge's rounded-rect while visible) makes the corners not exist at all.
        /// The region ellipse sits ~1px OUTSIDE the painted AA rim (and OnPaint clears with the button's
        /// own fill, never the parent background), so the aliased region edge hides in a fill-colored
        /// halo. The Region is also the CLICK boundary: Ø38px circle + badge — the square's corners were
        /// visually dead but tappable before; Ø38 still meets the ~40px touch-target bar.</summary>
        private int _regionForCount = -1;   // the count the current Region was built for (self-heal key)
        private int _regionForDpi;          // ...and the DPI (step 3.2: the badge metrics are DPI-dependent)

        private void RebuildRegion()
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            // WINDING is load-bearing: the default (Alternate/even-odd) XORs overlapping figures, which
            // punched a HOLE in the region exactly where the badge pill overlaps the circle — the chat
            // background showed through the bite (field screenshot: badge visible only outside the rim).
            var path = new GraphicsPath { FillMode = FillMode.Winding };
            int d = Math.Min(Width, Height) - P(6);   // painted circle is -8: region rim rides 1px outside it
            path.AddEllipse((Width - d) / 2f, (Height - d) / 2f, d, d);
            if (_count > 0)
            {
                string t = _count > 99 ? "99+" : _count.ToString();
                var badge = BadgeRect(t, FontHelper.UiAt(7.5f, dpi, FontStyle.Bold), dpi);   // shared font: never dispose
                badge.Inflate(P(2), P(2));   // keep the badge's own AA edge safely inside the region
                using (var bp = DrawHelper.RoundedRect(badge, P(10))) path.AddPath(bp, false);
            }
            _regionForCount = _count;
            _regionForDpi = dpi;
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            float k = dpi / 96f;   // exactly 1 at 96
            if (_regionForCount != _count || _regionForDpi != dpi) RebuildRegion();

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int d = Math.Min(Width, Height) - P(8);
            var circle = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);

            Color fill = ThemeHelper.PaletteFor(IsDark).JumpButtonFill;
            // Clear with the button's OWN fill (not the parent background): with the circular Region,
            // every surviving pixel belongs to the button — background color must never show anywhere.
            g.Clear(fill);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, circle);
            using (var p = new Pen(ThemeHelper.PaletteFor(IsDark).JumpButtonBorder))
                g.DrawEllipse(p, circle);

            float cx = circle.X + d / 2f, cy = circle.Y + d / 2f;
            using (var pen = new Pen(ColorMath.Ink(AccentColor, fill, 3.0), 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(cx - 6 * k, cy - 3 * k), new PointF(cx, cy + 4 * k), new PointF(cx + 6 * k, cy - 3 * k) });

            if (_count > 0)
            {
                string t = _count > 99 ? "99+" : _count.ToString();
                var bf = FontHelper.UiAt(7.5f, dpi, FontStyle.Bold);   // shared: never dispose
                var badge = BadgeRect(t, bf, dpi);
                using (var bb = new SolidBrush(AccentColor))
                using (var pth = DrawHelper.RoundedRect(badge, P(8))) g.FillPath(bb, pth);
                TextRenderer.DrawText(g, t, bf, badge, ColorMath.OnColor(AccentColor),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
