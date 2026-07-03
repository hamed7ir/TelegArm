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
    /// </summary>
    public sealed class ReplyKeyboardControl : Control
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

        private const int RowH = 40, Gap = 6, Pad = 6, HideH = 22, BarH = 30;

        public ReplyKeyboardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public bool HasButtons { get { return _rows != null && _rows.Count > 0; } }

        public int DesiredHeight
        {
            get
            {
                if (!HasButtons) return 0;
                if (Collapsed) return BarH;
                return Pad + HideH + _rows.Count * (RowH + Gap);
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = IsDark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(236, 236, 238);
            g.Clear(bg);
            _rects.Clear(); _hideRect = Rectangle.Empty; _showRect = Rectangle.Empty;
            if (!HasButtons) return;

            Color sub = IsDark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);

            if (Collapsed)
            {
                _showRect = new Rectangle(0, 0, Width, BarH);
                TextRenderer.DrawText(g, "⌨  Show keyboard", FontHelper.Ui(9f), _showRect, sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            // ▾ hide toggle along the top-right
            _hideRect = new Rectangle(Width - 40, 0, 40, HideH);
            TextRenderer.DrawText(g, "▾", FontHelper.Ui(10f), _hideRect, sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Color cell = IsDark ? Color.FromArgb(58, 58, 62) : Color.White;
            Color text = IsDark ? Color.FromArgb(228, 228, 230) : Color.FromArgb(25, 25, 28);

            int y = HideH;
            foreach (var row in _rows)
            {
                int n = Math.Max(1, row.Count);
                int totalGap = (n - 1) * Gap + 2 * Pad;
                int cw = (Width - totalGap) / n;
                int x = Pad;
                for (int i = 0; i < row.Count; i++)
                {
                    var r = new Rectangle(x, y, cw, RowH);
                    _rects.Add(new KeyValuePair<RKButton, Rectangle>(row[i], r));
                    using (var b = new SolidBrush(cell))
                    using (var p = DrawHelper.RoundedRect(r, 8))
                        g.FillPath(b, p);
                    // Script-aware font (Vazirmatn for Persian) + inline Noto emoji, centered — bot button labels
                    // are often Persian and/or carry emoji; the old Roboto + TextRenderer showed system Persian + glyph emoji.
                    string label = row[i].Label ?? "";
                    using (var font = FontHelper.For(label, 9.5f))
                        EmojiRenderer.DrawLineCentered(g, label, font, text, r);
                    x += cw + Gap;
                }
                y += RowH + Gap;
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
