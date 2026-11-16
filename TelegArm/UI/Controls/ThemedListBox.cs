using System;
using System.Drawing;
using System.Reflection;
using TelegArm.Helpers;
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

        private readonly bool _dark;
        private int _cursor = -1;   // R6: the row the keyboard points at (-1 none)
        private Control _searchBox;

        public ThemedListBox(bool dark, Color accent)
        {
            _dark = dark;
            // R6: reachable with Tab, then the arrows move a focus ring and Space (or Enter) acts as a click. A click or a
            // tap lands on the canvas, which cannot take the focus, so touching a row never moves the focus (the touch
            // keyboard stays as it was).
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            _scroll = new NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
            _canvas = new Panel { Left = 0, Top = 0, BackColor = ThemeHelper.PaletteFor(dark).ListBoxBg };
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
        public void SetItems(int count) { SetItems(count, false); }

        /// <summary>As <see cref="SetItems(int)"/>; <paramref name="keepPlace"/>: more rows arrived at the end (a page loaded on
        /// reaching the bottom), so the scroll position and the keyboard's row stay put. (From the top, the list jumped back
        /// to its first row after every page.)</summary>
        public void SetItems(int count, bool keepPlace)
        {
            ItemCount = Math.Max(0, count);
            if (!keepPlace) _cursor = ItemCount > 0 && Focused ? 0 : -1;
            else if (_cursor >= ItemCount) _cursor = ItemCount - 1;
            SyncCanvasWidth();
            int offset = keepPlace ? -_scroll.AutoScrollPosition.Y : 0;
            _canvas.Height = ItemCount * RowHeight;
            _scroll.AutoScrollPosition = new Point(0, offset);
            _canvas.Invalidate();
            _bar.Invalidate();
        }

        /// <summary>R6: the dialog's search box. Down in it moves into the list, Up on the list's first row goes back.</summary>
        public void AttachSearchBox(Control box)
        {
            _searchBox = box;
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Down || e.Modifiers != Keys.None || ItemCount == 0) return;
                e.Handled = e.SuppressKeyPress = true;
                Focus();
            };
        }

        /// <summary>R6: the row the keyboard points at, -1 for none.</summary>
        public int KeyCursor { get { return _cursor; } }

        // ── keyboard (R6) ──────────────────────────────────────────────────────────────────────────────────────
        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Space:
                    return true;
                case Keys.Enter:   // a dialog's own Enter (its AcceptButton: Forward, Done...) wins over clicking the row
                    var f = FindForm();
                    return f == null || f.AcceptButton == null;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != Keys.None || ItemCount == 0) return;
            int page = Math.Max(1, _scroll.ClientSize.Height / Math.Max(1, RowHeight) - 1);
            switch (e.KeyCode)
            {
                case Keys.Up:
                    if (_cursor <= 0 && _searchBox != null && _searchBox.CanFocus) { _searchBox.Focus(); e.Handled = true; return; }
                    MoveCursor(_cursor - 1); break;
                case Keys.Down: MoveCursor(_cursor + 1); break;
                case Keys.PageUp: MoveCursor(_cursor - page); break;
                case Keys.PageDown: MoveCursor(_cursor + page); break;
                case Keys.Home: MoveCursor(0); break;
                case Keys.End: MoveCursor(ItemCount - 1); break;
                case Keys.Space:
                case Keys.Enter:
                    if (_cursor >= 0 && _cursor < ItemCount && ItemClicked != null) ItemClicked(_cursor);
                    break;
                default: return;
            }
            e.Handled = e.SuppressKeyPress = true;
        }

        private void MoveCursor(int to)
        {
            to = Math.Max(0, Math.Min(ItemCount - 1, to));
            if (to == _cursor) return;
            InvalidateRow(_cursor);
            _cursor = to;
            InvalidateRow(_cursor);
            // into view
            int top = to * RowHeight, offset = -_scroll.AutoScrollPosition.Y, h = _scroll.ClientSize.Height;
            if (top < offset) offset = top;
            else if (top + RowHeight > offset + h) offset = top + RowHeight - h;
            else return;
            _scroll.AutoScrollPosition = new Point(0, Math.Max(0, offset));
            _bar.Invalidate();
            MaybeReachedEnd();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (ItemCount == 0) { _cursor = -1; return; }
            int keep = Math.Max(0, Math.Min(ItemCount - 1, _cursor));
            _cursor = -1; MoveCursor(keep);   // repaint the ring and bring its row into view
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            InvalidateRow(_cursor);
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
            if (Focused && _cursor >= first && _cursor <= last)   // R6: the keyboard's row (only while the list has the focus)
            {
                int dpi = Dpi.Of(this);
                var g = e.Graphics;
                var old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(ThemeHelper.PaletteFor(_dark).AccentGlyph, Dpi.Px(2, dpi)))
                using (var ring = DrawHelper.RoundedRect(new Rectangle(Dpi.Px(2, dpi), _cursor * RowHeight + Dpi.Px(2, dpi), _canvas.Width - Dpi.Px(4, dpi) - 1, RowHeight - Dpi.Px(4, dpi) - 1), Dpi.Px(6, dpi)))
                    g.DrawPath(pen, ring);
                g.SmoothingMode = old;
            }
        }

        private void OnCanvasClick(object sender, MouseEventArgs e)
        {
            int i = e.Y / RowHeight;
            if (i < 0 || i >= ItemCount) return;
            if (i != _cursor) { InvalidateRow(_cursor); _cursor = i; InvalidateRow(_cursor); }   // the keyboard goes on from here
            if (ItemClicked != null) ItemClicked(i);
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
