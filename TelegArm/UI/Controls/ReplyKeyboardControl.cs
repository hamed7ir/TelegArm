using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// The alternate "reply keyboard" (ReplyKeyboardMarkup) — a panel of bot-supplied buttons that sits
    /// above the input and REPLACES the on-screen keyboard (distinct from the inline buttons drawn under a
    /// message). Tapping a plain button sends its text. Has a built-in ▾ hide toggle: when hidden it shows a
    /// slim "Show keyboard" bar so the user can type normally and bring it back. Owner-painted + themed; RTL
    /// labels via TextRenderer. Height is driven by <see cref="DesiredHeight"/> (the host sets the row).
    ///
    /// Redesign step 3.2: lengths are logical (96-DPI) through Px at the control's DPI; fonts are shared FontCache fonts
    /// (never disposed — the old per-paint FontHelper.Ui leaked one per paint). DesiredHeight is PHYSICAL px at that DPI,
    /// and <see cref="DesiredHeightChanged"/> tells the host to re-sync its row after a DPI change.
    /// </summary>
    public sealed class ReplyKeyboardControl : Control, IDpiAware
    {
        public enum RKKind { Text, RequestPhone, RequestGeo, RequestPeer, RequestPoll, WebView }
        public sealed class RKButton { public string Label; public RKKind Kind; }

        private List<List<RKButton>> _rows;
        private readonly List<KeyValuePair<RKButton, Rectangle>> _rects = new List<KeyValuePair<RKButton, Rectangle>>();
        private Rectangle _hideRect, _showRect;

        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }
        public bool Collapsed { get; private set; }

        /// <summary>A button was tapped (plain → send its text; request-* → host decides).</summary>
        public event Action<RKButton> ButtonActivated;
        /// <summary>The collapse state changed → the host should re-read DesiredHeight and resize the row.</summary>
        public event EventHandler ToggleChanged;
        /// <summary>The DPI changed, so <see cref="DesiredHeight"/> did → the host should re-read it and resize the row.</summary>
        public event EventHandler DesiredHeightChanged;

        private const int RowH = 40, Gap = 6, Pad = 6, HideH = 22, BarH = 30;   // logical px

        private int _dpi;                                          // the DPI this control is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public ReplyKeyboardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Invalidate();
            DesiredHeightChanged?.Invoke(this, EventArgs.Empty);   // host SETS its row from DesiredHeight (no multiplying)
        }

        public bool HasButtons { get { return _rows != null && _rows.Count > 0; } }

        /// <summary>PHYSICAL px at the control's DPI (the same per-term rounding OnPaint lays out with).</summary>
        public int DesiredHeight
        {
            get
            {
                if (!HasButtons) return 0;
                int dpi = CurDpi;
                if (Collapsed) return Dpi.Px(BarH, dpi);
                return Dpi.Px(Pad, dpi) + Dpi.Px(HideH, dpi) + _rows.Count * (Dpi.Px(RowH, dpi) + Dpi.Px(Gap, dpi));
            }
        }

        public void SetButtons(List<List<RKButton>> rows)
        {
            _rows = rows; Collapsed = false;
            if (!IsDisposed) Invalidate();
        }

        public void Clear() { _rows = null; if (!IsDisposed) Invalidate(); }

        private void SetCollapsed(bool c)
        {
            if (Collapsed == c) return;
            Collapsed = c;
            ToggleChanged?.Invoke(this, EventArgs.Empty);   // host resizes the row
            if (!IsDisposed) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = IsDark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(236, 236, 238);
            g.Clear(bg);
            _rects.Clear(); _hideRect = Rectangle.Empty; _showRect = Rectangle.Empty;
            if (!HasButtons) return;

            Color sub = IsDark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);

            if (Collapsed)
            {
                _showRect = new Rectangle(0, 0, Width, P(BarH));
                TextRenderer.DrawText(g, "⌨  Show keyboard", FontHelper.UiAt(9f, dpi), _showRect, sub,   // shared: never dispose
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            // ▾ hide toggle along the top-right
            _hideRect = new Rectangle(Width - P(40), 0, P(40), P(HideH));
            TextRenderer.DrawText(g, "▾", FontHelper.UiAt(10f, dpi), _hideRect, sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Color cell = IsDark ? Color.FromArgb(58, 58, 62) : Color.White;
            Color text = IsDark ? Color.FromArgb(228, 228, 230) : Color.FromArgb(25, 25, 28);

            int rowH = P(RowH), gap = P(Gap), pad = P(Pad);
            int y = P(HideH);
            foreach (var row in _rows)
            {
                int n = Math.Max(1, row.Count);
                int totalGap = (n - 1) * gap + 2 * pad;
                int cw = (Width - totalGap) / n;
                int x = pad;
                for (int i = 0; i < row.Count; i++)
                {
                    var r = new Rectangle(x, y, cw, rowH);
                    _rects.Add(new KeyValuePair<RKButton, Rectangle>(row[i], r));
                    using (var b = new SolidBrush(cell))
                    using (var p = DrawHelper.RoundedRect(r, P(8)))
                        g.FillPath(b, p);
                    // Script-aware font (Vazirmatn for Persian) + inline Noto emoji, centered — bot button labels
                    // are often Persian and/or carry emoji; the old Roboto + TextRenderer showed system Persian + glyph emoji.
                    string label = row[i].Label ?? "";
                    EmojiRenderer.DrawLineCentered(g, label, FontHelper.ForAt(label, 9.5f, dpi), text, r);   // shared: never dispose
                    x += cw + gap;
                }
                y += rowH + gap;
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!HasButtons) return;
            if (Collapsed) { if (_showRect.Contains(e.Location)) SetCollapsed(false); return; }
            if (_hideRect.Contains(e.Location)) { SetCollapsed(true); return; }
            foreach (var kv in _rects)
                if (kv.Value.Contains(e.Location)) { ButtonActivated?.Invoke(kv.Key); return; }
        }
    }
}
