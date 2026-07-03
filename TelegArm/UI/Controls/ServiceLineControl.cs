using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// A centered system/service line (e.g. "X joined the group") — not a left/right bubble. Owner-painted:
    /// a subtle themed translucent rounded pill with small centered grey text, RTL-aware (Persian renders
    /// centered/right-correct). Folds into the message FlowLayoutPanel like any row.
    /// </summary>
    public sealed class ServiceLineControl : Control
    {
        private readonly string _text;
        public bool IsDark { get; set; }

        public ServiceLineControl(string text)
        {
            _text = text ?? "";
            Height = 30;
            Margin = new Padding(0, 3, 0, 3);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245));
            if (string.IsNullOrEmpty(_text) || Width < 40) return;

            using (var f = FontHelper.For(_text, 7.5f))
            {
                int maxTextW = Width - 48;
                var sz = TextRenderer.MeasureText(g, _text, f, new Size(maxTextW, Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                int pillW = Math.Min(Width - 24, sz.Width + 22);
                int pillH = Math.Min(Height - 4, sz.Height + 8);
                var pill = new Rectangle((Width - pillW) / 2, (Height - pillH) / 2, pillW, pillH);

                using (var b = new SolidBrush(IsDark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(26, 0, 0, 0)))
                using (var p = DrawHelper.RoundedRect(pill, pillH / 2))
                    g.FillPath(b, p);

                Color tc = IsDark ? Color.FromArgb(190, 190, 196) : Color.FromArgb(105, 105, 110);
                TextRenderer.DrawText(g, _text, f, pill, tc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
