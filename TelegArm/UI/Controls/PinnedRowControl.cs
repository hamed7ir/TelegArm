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
    /// </summary>
    public sealed class PinnedRowControl : Control
    {
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
            Height = 54;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
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
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color baseBg = IsDark ? Color.FromArgb(36, 36, 39) : Color.White;
            Color hoverBg = IsDark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(238, 240, 244);
            g.Clear(_hover ? hoverBg : baseBg);

            using (var b = new SolidBrush(AccentColor))
                g.FillRectangle(b, 12, 9, 3, Math.Max(4, Height - 18));

            const int x = 24, rightPad = 12;
            int cw = Math.Max(0, Width - x - rightPad);

            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, 0, cw, Height));
            const TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            using (var lf = FontHelper.Ui(8.25f, FontStyle.Bold))
                TextRenderer.DrawText(g, _label, lf, new Rectangle(x, 6, cw, 15), AccentColor, baseFlags | TextFormatFlags.Left);

            Color prevColor = IsDark ? Color.FromArgb(190, 190, 195) : Color.FromArgb(80, 80, 85);
            bool rtl = IsRtl(_preview);
            var prevFlags = baseFlags | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            var prevRect = new Rectangle(x, 22, cw, Math.Max(14, Height - 22 - 6));
            using (var pf = FontHelper.For(_preview, 9f))
                TextRenderer.DrawText(g, _preview, pf, prevRect, prevColor, prevFlags);
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
