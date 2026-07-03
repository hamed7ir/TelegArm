using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// One active-session row in Settings → Devices (owner-painted single control — the RT-safe pattern).
    /// Shows the app/device title, platform/system line, and location · last-active line. Non-current rows
    /// draw a red ✕ that raises <see cref="TerminateClicked"/>; the current session draws no ✕.
    /// </summary>
    public sealed class SessionRowControl : Control
    {
        public long Hash { get; private set; }
        public bool IsCurrent { get; set; }
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        public event Action<long> TerminateClicked;

        private readonly string _title, _line2, _line3;
        private Rectangle _xRect;

        public SessionRowControl(long hash, string title, string line2, string line3)
        {
            Hash = hash;
            _title = title ?? "";
            _line2 = line2 ?? "";
            _line3 = line3 ?? "";
            Height = 70;
            Margin = new Padding(0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!IsCurrent && e.Button == MouseButtons.Left && _xRect.Contains(e.Location))
                TerminateClicked?.Invoke(Hash);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(IsDark ? Color.FromArgb(38, 38, 41) : Color.FromArgb(248, 248, 250));

            Color titleColor = IsDark ? Color.FromArgb(230, 230, 235) : Color.FromArgb(30, 30, 34);
            Color subColor = IsDark ? Color.FromArgb(165, 165, 170) : Color.FromArgb(110, 110, 115);

            using (var ab = new SolidBrush(AccentColor))
                g.FillRectangle(ab, 0, 10, 3, Math.Max(4, Height - 20));   // accent left bar

            int x = 14;
            int reserveX = IsCurrent ? 14 : 44;       // leave room for the ✕ on non-current rows
            int cw = Math.Max(20, Width - x - reserveX);

            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            using (var tf = FontHelper.Ui(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, _title, tf, new Rectangle(x, 7, cw, 20), titleColor, flags);
            using (var f = FontHelper.Ui(8.25f))
                TextRenderer.DrawText(g, _line2, f, new Rectangle(x, 28, cw, 18), subColor, flags);
            using (var f = FontHelper.Ui(8f))
                TextRenderer.DrawText(g, _line3, f, new Rectangle(x, 46, cw, 18), subColor, flags);

            if (!IsCurrent)
            {
                _xRect = new Rectangle(Width - 40, (Height - 28) / 2, 28, 28);
                using (var pen = new Pen(IsDark ? Color.FromArgb(200, 95, 95) : Color.FromArgb(205, 70, 70), 1.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, _xRect.X + 9, _xRect.Y + 9, _xRect.Right - 9, _xRect.Bottom - 9);
                    g.DrawLine(pen, _xRect.Right - 9, _xRect.Y + 9, _xRect.X + 9, _xRect.Bottom - 9);
                }
            }

            using (var p = new Pen(IsDark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(232, 232, 236)))
                g.DrawLine(p, x, Height - 1, Width - 14, Height - 1);
        }
    }
}
