using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// An owner-drawn vertical list whose scrollbar is DARK on every Windows version — including RT 8.1, where
    /// the native scrollbar can't be themed. It hosts a <see cref="NoNativeScrollPanel"/> (AutoScroll, native
    /// bars suppressed) over a tall double-buffered canvas, driven by the owner-painted <see cref="ThemedScrollBar"/>.
    /// The caller supplies the row count + a row painter + a click handler — rows stay fully owner-drawn
    /// (RTL via TextRenderer). Drop-in replacement for a bare WinForms ListBox in the themed dialogs.
    /// </summary>
    public sealed class ThemedListBox : Panel
    {
        private readonly NoNativeScrollPanel _scroll;
        private readonly Panel _canvas;
        private readonly ThemedScrollBar _bar;

        public int RowHeight { get; set; } = 54;
        public int ItemCount { get; private set; }

        /// <summary>Paint one row: (graphics, index, row bounds within the canvas).</summary>
        public event Action<Graphics, int, Rectangle> DrawRow;
        /// <summary>A row was clicked (index).</summary>
        public event Action<int> ItemClicked;
        /// <summary>Raised when the user scrolls near the bottom — hook this to page in more rows.</summary>
        public event Action ReachedEnd;

        public ThemedListBox(bool dark, Color accent)
        {
            _scroll = new NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
            _canvas = new Panel { Left = 0, Top = 0, BackColor = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250) };
            SetDoubleBuffered(_canvas);
            _canvas.Paint += OnCanvasPaint;
            _canvas.MouseClick += OnCanvasClick;
            _scroll.Controls.Add(_canvas);

            _bar = new ThemedScrollBar(_scroll, dark, accent) { Dock = DockStyle.Right };

            Controls.Add(_scroll);   // Fill — added first so it docks last and fills the leftover space
            Controls.Add(_bar);      // Right — added last so it docks first and claims the right edge
            _scroll.ClientSizeChanged += (s, e) => SyncCanvasWidth();
            _scroll.Scroll += (s, e) => MaybeReachedEnd();

            // Finger-pan scrolling (RT touch) — the shared chat-list touch pattern, so EVERY ThemedListBox
            // (contacts / forward / country / members / invite links) is touch-scrollable for free. A tap
            // synthesizes a click on the canvas (→ ItemClicked); a drag pans the surface. Touch sets
            // AutoScrollPosition directly (no Scroll event), so route TouchScroller.Scrolled → MaybeReachedEnd
            // for lazy paging on touch too. Unhook on dispose (static event) so closed lists don't leak.
            TouchScroller.Enable(_scroll, false);
            Action<System.Windows.Forms.ScrollableControl> onTouchScroll = sc => { if (ReferenceEquals(sc, _scroll)) MaybeReachedEnd(); };
            TouchScroller.Scrolled += onTouchScroll;
            Disposed += (s, e) => TouchScroller.Scrolled -= onTouchScroll;
        }

        private void MaybeReachedEnd()
        {
            if (ReachedEnd == null || _canvas.Height <= 0) return;
            int offset = -_scroll.AutoScrollPosition.Y;
            if (offset + _scroll.ClientSize.Height >= _canvas.Height - RowHeight) ReachedEnd();
        }

        public Color CanvasBackColor { get { return _canvas.BackColor; } set { _canvas.BackColor = value; } }

        /// <summary>Set the item count and repaint from the top (call after the data / filter changes).</summary>
        public void SetItems(int count)
        {
            ItemCount = Math.Max(0, count);
            SyncCanvasWidth();
            _canvas.Height = ItemCount * RowHeight;
            _scroll.AutoScrollPosition = new Point(0, 0);
            _canvas.Invalidate();
        }

        public void InvalidateRow(int index)
        {
            if (index < 0) return;
            _canvas.Invalidate(new Rectangle(0, index * RowHeight, _canvas.Width, RowHeight));
        }

        private void SyncCanvasWidth()
        {
            int w = Math.Max(0, _scroll.ClientSize.Width);
            if (_canvas.Width != w) _canvas.Width = w;
        }

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            var h = DrawRow;
            if (h == null || ItemCount == 0) return;
            int first = Math.Max(0, e.ClipRectangle.Top / RowHeight);
            int last = Math.Min(ItemCount - 1, e.ClipRectangle.Bottom / RowHeight);
            for (int i = first; i <= last; i++)
                h(e.Graphics, i, new Rectangle(0, i * RowHeight, _canvas.Width, RowHeight));
        }

        private void OnCanvasClick(object sender, MouseEventArgs e)
        {
            int i = e.Y / RowHeight;
            if (i >= 0 && i < ItemCount && ItemClicked != null) ItemClicked(i);
        }

        private static void SetDoubleBuffered(Control c)
        {
            try
            {
                var p = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (p != null) p.SetValue(c, true, null);
            }
            catch { }
        }
    }
}
