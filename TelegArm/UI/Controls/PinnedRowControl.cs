using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// One row in the pinned-messages list (owner-painted single control — the proven RT-safe pattern).
    /// Shows a small accent label + the message preview (RTL-aware, ellipsized). Left-click jumps to the
    /// message; right-click / touch long-press raises the context menu (Jump / Unpin).
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// row's DPI; fonts are shared FontHelper.UiAt / ForAt fonts (never disposed). Identity at 96. The DPI is the
    /// window's once parented (rows are built unparented, then added); IDpiAware SETS the height, never multiplies.
    /// </summary>
    public sealed class PinnedRowControl : Control, IDpiAware
    {
        private const int RowH = 54;   // logical px

        private int _dpi;                                          // the DPI this row is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public int MessageId { get; }
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        public event Action<int> Clicked;
        public event Action<int, Point> ContextRequested;

        private readonly string _label;
        private readonly string _preview;
        private bool _hover;

        private const int WM_CONTEXTMENU = 0x007B;

        public PinnedRowControl(int messageId, string label, string preview)
        {
            MessageId = messageId;
            _label = label ?? "Pinned message";
            _preview = preview ?? "";
            Height = Dpi.Px(RowH, CurDpi);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Rows are built before they are added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Height = Dpi.Px(RowH, d); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Height = Dpi.Px(RowH, newDpi);   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CONTEXTMENU)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1
                    ? PointToScreen(new Point(Width / 2, Height / 2))
                    : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                ContextRequested?.Invoke(MessageId, pt);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) Clicked?.Invoke(MessageId);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; if (!IsDisposed) Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; if (!IsDisposed) Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color baseBg = IsDark ? Color.FromArgb(36, 36, 39) : Color.White;
            Color hoverBg = IsDark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(238, 240, 244);
            g.Clear(_hover ? hoverBg : baseBg);

            using (var b = new SolidBrush(AccentColor))
                g.FillRectangle(b, P(12), P(9), P(3), Math.Max(P(4), Height - P(18)));

            int x = P(24), rightPad = P(12);
            int cw = Math.Max(0, Width - x - rightPad);

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, 0, cw, Height));
            const TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            // shared fonts: never dispose
            TextRenderer.DrawText(g, _label, FontHelper.UiAt(8.25f, dpi, FontStyle.Bold), new Rectangle(x, P(6), cw, P(15)), AccentColor, baseFlags | TextFormatFlags.Left);

            Color prevColor = IsDark ? Color.FromArgb(190, 190, 195) : Color.FromArgb(80, 80, 85);
            bool rtl = IsRtl(_preview);
            var prevFlags = baseFlags | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            var prevRect = new Rectangle(x, P(22), cw, Math.Max(P(14), Height - P(22) - P(6)));
            TextRenderer.DrawText(g, _preview, FontHelper.ForAt(_preview, 9f, dpi), prevRect, prevColor, prevFlags);
            g.Clip = oldClip;

            using (var p = new Pen(IsDark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(232, 232, 236)))
                g.DrawLine(p, x, Height - 1, Width, Height - 1);
        }

        private static bool IsRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if ((c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF))
                    return true;
            return false;
        }
    }
}
