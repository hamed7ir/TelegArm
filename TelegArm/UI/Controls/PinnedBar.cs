using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted "pinned message" bar (single control, no children — the proven
    /// MiniPlayerBar/SelectionBar pattern, so it renders correctly on RT). Shows an accent left bar,
    /// a "Pinned message"/counter title, and a one-line preview. Clicking raises <see cref="BarClicked"/>.
    /// </summary>
    public sealed class PinnedBar : Control
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        private string _title = "Pinned message";
        private string _preview = "";

        /// <summary>Tapping the bar body cycles to the next pin.</summary>
        public event EventHandler BarClicked;
        /// <summary>Tapping the right-edge list icon opens the full pinned list.</summary>
        public event EventHandler ShowAllClicked;

        private const int IconBox = 34;   // right-edge hit area for the "show all pinned" list icon

        public PinnedBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public void SetContent(string title, string preview)
        {
            _title = title ?? "Pinned message";
            _preview = preview ?? "";
            if (!IsDisposed) Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.X >= Width - IconBox) ShowAllClicked?.Invoke(this, EventArgs.Empty);   // right-edge icon → list
            else BarClicked?.Invoke(this, EventArgs.Empty);                              // body → cycle next
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.Clear(IsDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(248, 248, 250));

            // Accent bar at the leading edge.
            using (var b = new SolidBrush(AccentColor))
                g.FillRectangle(b, 12, 8, 3, Math.Max(4, Height - 16));

            const int x = 24;
            int cw = Math.Max(0, Width - x - IconBox);   // reserve the right-edge list icon
            Color prevColor = IsDark ? Color.FromArgb(180, 180, 185) : Color.FromArgb(90, 90, 95);

            // "Show all pinned" list icon (stacked lines) at the right edge.
            int iconW = 16, ix = Width - IconBox + (IconBox - iconW) / 2, iy = Height / 2 - 5;
            using (var ip = new Pen(IsDark ? Color.FromArgb(170, 170, 175) : Color.FromArgb(110, 110, 115), 2f))
            {
                g.DrawLine(ip, ix, iy, ix + iconW, iy);
                g.DrawLine(ip, ix, iy + 5, ix + iconW, iy + 5);
                g.DrawLine(ip, ix, iy + 10, ix + iconW, iy + 10);
            }

            // Clip ALL text to the content area so nothing (incl. a custom-emoji box glyph) bleeds past the bar.
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, 0, cw, Height));

            // Two single-line rows, each vertically centered + ellipsized + clipped (NO second-line bleed).
            const TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            using (var tf = FontHelper.Ui(8.25f, FontStyle.Bold))
                TextRenderer.DrawText(g, _title, tf, new Rectangle(x, 4, cw, 15), AccentColor,
                    baseFlags | TextFormatFlags.Left);

            // Preview: give it the taller remaining space so Persian isn't vertically clipped; RTL right-aligned.
            var prevRect = new Rectangle(x, 18, cw, Math.Max(14, Height - 18 - 4));
            bool rtl = IsRtl(_preview);
            var prevFlags = baseFlags | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            using (var pf = FontHelper.For(_preview, 9f))
                TextRenderer.DrawText(g, _preview, pf, prevRect, prevColor, prevFlags);

            g.Clip = oldClip;

            using (var p = new Pen(IsDark ? Color.FromArgb(60, 60, 63) : Color.FromArgb(230, 230, 233)))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        /// <summary>True if the text contains Hebrew/Arabic/Persian letters (→ right-align the preview).</summary>
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
