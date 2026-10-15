using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// A slim, owner-painted scrollbar (vertical or horizontal) that gives an AutoScroll control a
    /// themed scrollbar identical on every Windows version — including Windows RT 8.1, which has no
    /// dark scrollbar visual style (so the OS scrollbars stay white there). It drives the target's
    /// <c>AutoScrollPosition</c>. The target's NATIVE bars are suppressed structurally by the
    /// host panel (see <c>NoNativeScrollPanel</c>/<c>NoNativeScrollFlowPanel</c>) — not by this control.
    ///
    /// Vertical: dock the target <see cref="DockStyle.Fill"/> in a host Panel, add this docked
    /// <see cref="DockStyle.Right"/>. Horizontal: add it docked <see cref="DockStyle.Bottom"/>.
    /// (Add the target first so it docks last and fills the leftover space.)
    ///
    /// ★ TWO LOOKS, ONE BEHAVIOUR (redesign 2.x). <see cref="Restyled"/> OFF (the default) is the 1.x bar, pixel for
    ///   pixel — the chat list and the message panel use it and a zero-diff screenshot check guards it. ON gives the
    ///   2.x bar: a 12-logical-px lane with an 8-px pill thumb centred in it, coloured from the cached palette and
    ///   scaled to the window's DPI. Only geometry and colour differ: Metrics, drag, click-to-page, hover tracking,
    ///   the wheel and the TouchScroller.Scrolled follow are the same code in both.
    /// </summary>
    public sealed class ThemedScrollBar : Control, IDpiAware
    {
        private const int Thickness = 11;

        // Restyled geometry, all LOGICAL (96-dpi) px; converted with Dpi.Px at layout/paint time.
        private const int RestyledLane = 12;     // the bar's own width (vertical) or height (horizontal)
        private const int RestyledThumb = 8;     // the pill's cross-axis size; (12 - 8) / 2 = 2 px of lane each side
        private const int MinThumb = 28;         // shortest thumb along the track (1.x: 28 px, unscaled)

        /// <summary>Selects the 2.x look for every bar CREATED AFTER it is set. ON since redesign step 2.0 (built in 1.6,
        /// off until 1.7's zero-diff check had run against the 1.x bar). It is read once, in the constructor, and kept for
        /// the life of the bar — so a bar's lane width and its painting can never disagree, and the gallery shows both
        /// looks side by side by flipping the flag around one construction.
        /// When on, colours come from <see cref="ThemeHelper.Palette"/> (and the bar repaints itself on
        /// ThemeChanged), so <see cref="IsDark"/> and <see cref="AccentColor"/> are no longer consulted.</summary>
        public static bool Restyled { get; set; } = true;

        /// <summary>The width (height, horizontal) a bar created NOW takes at <paramref name="dpi"/> — for layout that
        /// must leave room for one (the Info dock hands ProfileForm its width minus the bar). 1.x: 11, unscaled.</summary>
        public static int LaneWidth(int dpi) { return Restyled ? Dpi.Px(RestyledLane, dpi) : Thickness; }

        private readonly ScrollableControl _target;
        private readonly bool _horz;
        private readonly bool _restyled;
        /// <summary>1.x look only — the Restyled look reads the palette instead.</summary>
        public bool IsDark { get; set; }
        /// <summary>1.x look only — the Restyled look reads the palette instead.</summary>
        public Color AccentColor { get; set; }

        private bool _dragging;
        private int _dragStart;
        private int _dragStartValue;
        private bool _hoverThumb;

        public ThemedScrollBar(ScrollableControl target, bool dark, Color accent, bool horizontal = false)
        {
            _target = target;
            _horz = horizontal;
            _restyled = Restyled;
            IsDark = dark;
            AccentColor = accent;
            if (_restyled) ApplyLane(Dpi.Of(this));   // refined again once parented / shown, when the window's DPI is known
            else if (_horz) Height = Thickness; else Width = Thickness;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // Track the target so the thumb stays in sync (native bars are suppressed by the host panel).
            _target.Scroll += (s, e) => Invalidate();
            _target.ClientSizeChanged += (s, e) => Invalidate();
            _target.Layout += (s, e) => Invalidate();
            _target.ControlAdded += (s, e) => Invalidate();
            _target.ControlRemoved += (s, e) => Invalidate();
            // …but touch pans/momentum set AutoScrollPosition DIRECTLY (no Scroll event), and the mouse
            // wheel is redirected by TouchScroller — neither raises Scroll, so the thumb used to freeze
            // during wheel/touch scrolling. BOTH funnel through TouchScroller.Scrolled → follow it.
            TouchScroller.Scrolled += OnSurfaceScrolled;
            // The Restyled bar paints from the palette, so it must follow a live theme/accent change itself
            // (the 1.x bar is recoloured by its owner through IsDark/AccentColor and is left exactly as it was).
            if (_restyled) ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnSurfaceScrolled(ScrollableControl s)
        {
            if (ReferenceEquals(s, _target) && !IsDisposed) Invalidate();
        }

        private void OnThemeChanged()
        {
            if (!IsDisposed && IsHandleCreated) { try { BeginInvoke((Action)Invalidate); } catch { } }
        }

        protected override void Dispose(bool disposing)
        {
            // Static event → a leaked subscription would keep this bar (and its target) alive. Dialog
            // scrollbars (Settings/Profile/ScrollHost) are created and destroyed per dialog, so unhook.
            if (disposing)
            {
                TouchScroller.Scrolled -= OnSurfaceScrolled;
                if (_restyled) ThemeHelper.ThemeChanged -= OnThemeChanged;
            }
            base.Dispose(disposing);
        }

        // ── Restyled lane (DPI) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Sets the lane thickness to <see cref="RestyledLane"/> logical px at <paramref name="dpi"/>. An
        /// ABSOLUTE assignment, not a multiplication, so it is safe after DpiScaler's generic pass has already scaled
        /// Bounds (it only snaps the lane to the exact value, with no rounding drift over repeated DPI changes).
        /// Never called for a 1.x bar — its 11 px stays unscaled, as it has always been.</summary>
        private void ApplyLane(int dpi)
        {
            int t = Dpi.Px(RestyledLane, dpi);
            if (_horz) { if (Height != t) Height = t; }
            else { if (Width != t) Width = t; }
        }

        // The window's DPI is unknown while the bar is parentless (Dpi.Of falls back to the system DPI), so the lane
        // is re-derived when it gains a parent and when its handle is created inside its window.
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (_restyled) ApplyLane(Dpi.Of(this));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_restyled) ApplyLane(Dpi.Of(this));
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            if (_restyled) ApplyLane(newDpi);
            Invalidate();
        }

        /// <summary>⚠ DERIVED FROM THE TARGET'S ACTUAL GEOMETRY, NOT FROM ScrollProperties.
        /// This used to read <c>sp.Maximum / sp.LargeChange / sp.Value</c>. Those go STALE when the content
        /// SHRINKS: after a rebuild that removes rows, WinForms leaves the old Maximum in place until
        /// something re-runs the scrollbar calculation, so this bar kept reporting the OLD content size —
        /// <see cref="Active"/> stayed true and a full-height thumb was painted over a list that no longer
        /// scrolls at all. Measured: content really 300 px while Metrics still said 680, and it did NOT
        /// self-heal on PerformLayout() or a host resize.
        /// DisplayRectangle (the content box) and ClientRectangle (the viewport) are recomputed by the
        /// framework itself and cannot drift from what is on screen; AutoScrollPosition is likewise the
        /// live value rather than a cached one. When content fits, DisplayRectangle == ClientRectangle, so
        /// total == view and the bar correctly reports itself inactive.
        /// NOTE this control is SHARED — the chat list and message panel use it too — so the change is
        /// deliberately equivalence-preserving in the healthy case and only differs where the old values
        /// were wrong.</summary>
        private void Metrics(out int total, out int view, out int value, out int maxVal)
        {
            var disp = _target.DisplayRectangle;
            var cli = _target.ClientRectangle;
            total = _horz ? disp.Width : disp.Height;
            view = _horz ? cli.Width : cli.Height;
            // AutoScrollPosition READS negative and is assigned positive — the asymmetry this file has
            // always handled at the ScrollTo end; negate here so `value` is a plain positive offset.
            value = _horz ? -_target.AutoScrollPosition.X : -_target.AutoScrollPosition.Y;
            maxVal = Math.Max(1, total - view);
            if (value < 0) value = 0; else if (value > maxVal) value = maxVal;
        }

        private bool Active
        {
            get { Metrics(out int total, out int view, out _, out _); return total > view && view > 0; }
        }

        private int Length => _horz ? Width : Height;

        private Rectangle ThumbRect()
        {
            Metrics(out int total, out int view, out int value, out int maxVal);
            if (total <= view || view <= 0) return Rectangle.Empty;
            int len = Length;
            int minThumb = _restyled ? Dpi.Px(MinThumb, this) : MinThumb;
            int thumb = Math.Max(minThumb, (int)((long)len * view / total));
            if (thumb >= len) return Rectangle.Empty;
            int off = (int)((long)(len - thumb) * value / maxVal);
            if (_restyled) return RestyledThumbRect(off, thumb);
            // Step 3.3: the 1.x 2 px inset on each side, at the bar's DPI (2 at 96). Twice ONE rounded inset, not Px(4), so
            // both margins stay equal at every DPI (125%: 3 + 3, where Px(4) = 5 would leave 3 + 2).
            int inset = Dpi.Px(2, this);
            return _horz ? new Rectangle(off, inset, thumb, Height - 2 * inset)
                         : new Rectangle(inset, off, Width - 2 * inset, thumb);
        }

        /// <summary>The 2.x thumb: <see cref="RestyledThumb"/> logical px across the lane, centred in it, running
        /// <paramref name="thumb"/> px along the track from <paramref name="off"/>. Its cross-axis size is nudged by one
        /// pixel when the lane has the other parity (e.g. 15 px lane at 125 %), so the margins on both sides are whole,
        /// equal pixels — a half-pixel offset would anti-alias one edge of the pill and not the other.
        /// Drag and hit-testing use this same rectangle, exactly as they use the 1.x one.</summary>
        private Rectangle RestyledThumbRect(int off, int thumb)
        {
            int cross = _horz ? Height : Width;
            int w = Math.Min(Dpi.Px(RestyledThumb, this), cross);
            if (w < 1) return Rectangle.Empty;
            if (((cross - w) & 1) != 0) w += w < cross ? 1 : -1;
            int c = (cross - w) / 2;
            return _horz ? new Rectangle(off, c, thumb, w)
                         : new Rectangle(c, off, w, thumb);
        }

        private void ScrollTo(int value)
        {
            Metrics(out _, out _, out _, out int maxVal);
            if (value < 0) value = 0; else if (value > maxVal) value = maxVal;
            int ox = -_target.AutoScrollPosition.X, oy = -_target.AutoScrollPosition.Y;
            _target.AutoScrollPosition = _horz ? new Point(value, oy) : new Point(ox, value);
            Invalidate();
        }

        private int Pos(MouseEventArgs e) => _horz ? e.X : e.Y;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Active) return;
            var t = ThumbRect();
            Metrics(out _, out int view, out int value, out _);
            if (t.Contains(e.Location))
            {
                _dragging = true;
                _dragStart = Pos(e);
                _dragStartValue = value;
                Capture = true;
            }
            else
            {
                bool before = _horz ? e.X < t.X : e.Y < t.Y;
                ScrollTo(value + (before ? -view : view));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                var t = ThumbRect();
                Metrics(out _, out _, out _, out int maxVal);
                int denom = Math.Max(1, Length - (_horz ? t.Width : t.Height));
                int dv = (int)((long)(Pos(e) - _dragStart) * maxVal / denom);
                ScrollTo(_dragStartValue + dv);
            }
            else
            {
                bool h = ThumbRect().Contains(e.Location);
                if (h != _hoverThumb) { _hoverThumb = h; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverThumb) { _hoverThumb = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Metrics(out _, out int view, out int value, out _);
            ScrollTo(value - Math.Sign(e.Delta) * Math.Max(1, view / 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_restyled) { PaintRestyled(e.Graphics); return; }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(ThemeHelper.PaletteFor(IsDark).ChatBg);
            if (!Active) return;

            var t = ThumbRect();
            if (t == Rectangle.Empty) return;

            Color thumb = _dragging || _hoverThumb
                ? (IsDark ? Lighten(AccentColor) : AccentColor)
                : (ThemeHelper.PaletteFor(IsDark).ScrollThumb);
            int radius = (Thickness - 4) / 2;
            using (var b = new SolidBrush(thumb))
            using (var path = DrawHelper.RoundedRect(t, radius))
                g.FillPath(b, path);
        }

        private static Color Lighten(Color c)
            => Color.FromArgb(Math.Min(255, c.R + 40), Math.Min(255, c.G + 40), Math.Min(255, c.B + 40));

        // ── Restyled painting ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The 2.x paint: the lane in the colour of the content it scrolls, and — when there is something
        /// to scroll — a pill thumb in <c>ScrollThumb</c>, or the accent while hovered or dragged. Everything is read
        /// from the cached palette (no registry in a paint).
        /// Lane colour: 1.x cleared to a fixed grey (ChatBg), which is only right next to a chat surface; the new bar
        /// sits beside dialog and settings bodies, so it clears to the TARGET's own BackColor and the lane melts into
        /// the content instead of showing as a stripe. If that is not opaque (a transparent panel) it falls back to
        /// the bar's parent, then to <c>Window</c>.</summary>
        private void PaintRestyled(Graphics g)
        {
            var p = ThemeHelper.Palette;
            g.Clear(LaneColor(p));
            if (!Active) return;

            var t = ThumbRect();
            if (t == Rectangle.Empty) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color thumb = _dragging || _hoverThumb ? p.Accent : p.ScrollThumb;
            using (var b = new SolidBrush(thumb))
            using (var path = Pill(t))
                g.FillPath(b, path);
        }

        private Color LaneColor(Palette p)
        {
            Color c = _target.BackColor;
            if (c.A == 255) return c;
            c = Parent != null ? Parent.BackColor : Color.Empty;
            return c.A == 255 ? c : p.Window;
        }

        /// <summary>A stadium: the shorter side is the cap diameter, so the ends are fully round at ANY size.
        /// (DrawHelper.RoundedRect takes an integer radius, which leaves a flat sliver on an odd-width thumb.)</summary>
        private static GraphicsPath Pill(Rectangle r)
        {
            var path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return path;
            float d = Math.Min(r.Width, r.Height);
            if (r.Width > r.Height)
            {
                path.AddArc(r.X, r.Y, d, d, 90, 180);                 // left cap: bottom → left → top
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);        // right cap: top → right → bottom
            }
            else
            {
                path.AddArc(r.X, r.Y, d, d, 180, 180);                // top cap: left → top → right
                path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);         // bottom cap: right → bottom → left
            }
            path.CloseFigure();
            return path;
        }
    }
}
