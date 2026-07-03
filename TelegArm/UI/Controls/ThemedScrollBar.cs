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
    /// </summary>
    public sealed class ThemedScrollBar : Control
    {
        private const int Thickness = 11;

        private readonly ScrollableControl _target;
        private readonly bool _horz;
        public bool IsDark { get; set; }
        public Color AccentColor { get; set; }

        private bool _dragging;
        private int _dragStart;
        private int _dragStartValue;
        private bool _hoverThumb;

        public ThemedScrollBar(ScrollableControl target, bool dark, Color accent, bool horizontal = false)
        {
            _target = target;
            _horz = horizontal;
            IsDark = dark;
            AccentColor = accent;
            if (_horz) Height = Thickness; else Width = Thickness;
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
        }

        private void OnSurfaceScrolled(ScrollableControl s)
        {
            if (ReferenceEquals(s, _target) && !IsDisposed) Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            // Static event → a leaked subscription would keep this bar (and its target) alive. Dialog
            // scrollbars (Settings/Profile/ScrollHost) are created and destroyed per dialog, so unhook.
            if (disposing) TouchScroller.Scrolled -= OnSurfaceScrolled;
            base.Dispose(disposing);
        }

        private void Metrics(out int total, out int view, out int value, out int maxVal)
        {
            var sp = _horz ? (ScrollProperties)_target.HorizontalScroll : _target.VerticalScroll;
            total = sp.Maximum + 1;
            view = sp.LargeChange;
            value = sp.Value;
            maxVal = Math.Max(1, sp.Maximum - sp.LargeChange + 1);
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
            int thumb = Math.Max(28, (int)((long)len * view / total));
            if (thumb >= len) return Rectangle.Empty;
            int off = (int)((long)(len - thumb) * value / maxVal);
            return _horz ? new Rectangle(off, 2, thumb, Height - 4)
                         : new Rectangle(2, off, Width - 4, thumb);
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245));
            if (!Active) return;

            var t = ThumbRect();
            if (t == Rectangle.Empty) return;

            Color thumb = _dragging || _hoverThumb
                ? (IsDark ? Lighten(AccentColor) : AccentColor)
                : (IsDark ? Color.FromArgb(95, 95, 100) : Color.FromArgb(190, 190, 196));
            int radius = (Thickness - 4) / 2;
            using (var b = new SolidBrush(thumb))
            using (var path = DrawHelper.RoundedRect(t, radius))
                g.FillPath(b, path);
        }

        private static Color Lighten(Color c)
            => Color.FromArgb(Math.Min(255, c.R + 40), Math.Min(255, c.G + 40), Math.Min(255, c.B + 40));
    }
}
