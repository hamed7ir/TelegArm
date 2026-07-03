using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// The swappable composer footer for non-COMPOSE states: a single centered themed BUTTON
    /// (Join / Mute / Unmute / Start / Unblock) or a centered LABEL (restricted / slow-mode). One
    /// owner-painted control (no child controls) — the proven RT-safe bar pattern. Raises
    /// <see cref="ActionClicked"/> when a button is tapped.
    /// </summary>
    public sealed class ComposerFooterBar : Control
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }
        public event EventHandler ActionClicked;

        private string _text = "";
        private bool _isButton;
        private Rectangle _btnRect;

        public ComposerFooterBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetButton(string text)
        {
            _text = text ?? ""; _isButton = true; Cursor = Cursors.Hand;
            if (!IsDisposed) Invalidate();
        }

        public void SetLabel(string text)
        {
            _text = text ?? ""; _isButton = false; Cursor = Cursors.Default;
            if (!IsDisposed) Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_isButton && e.Button == MouseButtons.Left && _btnRect.Contains(e.Location))
                ActionClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(IsDark ? Color.FromArgb(38, 38, 41) : Color.FromArgb(247, 247, 249));
            using (var p = new Pen(IsDark ? Color.FromArgb(60, 60, 63) : Color.FromArgb(228, 228, 232)))
                g.DrawLine(p, 0, 0, Width, 0);   // top separator, like the other bars

            if (string.IsNullOrEmpty(_text)) { _btnRect = Rectangle.Empty; return; }

            if (_isButton)
            {
                int textW;
                using (var mf = FontHelper.Ui(10f, FontStyle.Bold))
                    textW = TextRenderer.MeasureText(_text, mf).Width;
                int bw = Math.Min(Width - 24, Math.Max(160, textW + 60));
                int bh = Math.Min(Height - 16, 40);
                _btnRect = new Rectangle((Width - bw) / 2, (Height - bh) / 2, bw, bh);
                using (var b = new SolidBrush(AccentColor))
                using (var path = DrawHelper.RoundedRect(_btnRect, 8))
                    g.FillPath(b, path);
                using (var f = FontHelper.For(_text, 10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _text, f, _btnRect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                _btnRect = Rectangle.Empty;
                Color c = IsDark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
                var r = new Rectangle(16, 0, Width - 32, Height);
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                          | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                          | (IsRtl(_text) ? TextFormatFlags.RightToLeft : 0);
                using (var f = FontHelper.For(_text, 9.5f))
                    TextRenderer.DrawText(g, _text, f, r, c, flags);
            }
        }

        private static bool IsRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s)
                if ((ch >= 0x0590 && ch <= 0x08FF) || (ch >= 0xFB1D && ch <= 0xFDFF) || (ch >= 0xFE70 && ch <= 0xFEFF))
                    return true;
            return false;
        }
    }
}
