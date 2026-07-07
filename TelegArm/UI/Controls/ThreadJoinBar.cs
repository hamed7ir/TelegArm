using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// COMMENTS-JOIN-FLYOUT: a themed bar shown ABOVE the composer in a comment thread when the user is
    /// NOT a member of the linked discussion group. Leading message + an accent "Join" button + an optional
    /// "✕" dismiss. One owner-painted control (no child controls) — the proven RT-safe bar pattern shared
    /// with <see cref="ComposerFooterBar"/>. RTL-aware: mirrors the layout when the message text is
    /// right-to-left (Persian). Joining is OPTIONAL — posting a comment works without it.
    /// </summary>
    public sealed class ThreadJoinBar : Control
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }
        public bool ShowDismiss { get; set; } = true;
        public event EventHandler JoinClicked;
        public event EventHandler DismissClicked;

        private string _message = "Join the group to get replies as messages";
        private readonly string _joinText = "Join";
        private Rectangle _joinRect, _dismissRect;

        public ThreadJoinBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public string Message
        {
            get { return _message; }
            set { _message = value ?? ""; if (!IsDisposed) Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_joinRect.Contains(e.Location)) { JoinClicked?.Invoke(this, EventArgs.Empty); return; }
            if (ShowDismiss && _dismissRect.Contains(e.Location)) DismissClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool onBtn = _joinRect.Contains(e.Location) || (ShowDismiss && _dismissRect.Contains(e.Location));
            Cursor = onBtn ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(IsDark ? Color.FromArgb(38, 38, 41) : Color.FromArgb(247, 247, 249));
            using (var p = new Pen(IsDark ? Color.FromArgb(60, 60, 63) : Color.FromArgb(228, 228, 232)))
                g.DrawLine(p, 0, 0, Width, 0);   // top separator, like the other bars

            bool rtl = IsRtl(_message);
            const int pad = 12, gap = 8;
            int bh = Math.Min(Height - 12, 34);
            int by = (Height - bh) / 2;

            int joinW;
            using (var mf = FontHelper.Ui(10f, FontStyle.Bold))
                joinW = Math.Max(88, TextRenderer.MeasureText(_joinText, mf).Width + 44);
            int dw = ShowDismiss ? 30 : 0;

            // The Join button sits on the trailing edge; ✕ just inside it; the message fills the leading band.
            // RTL mirrors leading/trailing (Join on the left, message right-aligned).
            int textStart, textEnd;
            if (!rtl)
            {
                _joinRect = new Rectangle(Width - pad - joinW, by, joinW, bh);
                _dismissRect = ShowDismiss ? new Rectangle(_joinRect.Left - gap - dw, by, dw, bh) : Rectangle.Empty;
                textStart = pad + 2;
                textEnd = (ShowDismiss ? _dismissRect.Left : _joinRect.Left) - gap;
            }
            else
            {
                _joinRect = new Rectangle(pad, by, joinW, bh);
                _dismissRect = ShowDismiss ? new Rectangle(_joinRect.Right + gap, by, dw, bh) : Rectangle.Empty;
                textStart = (ShowDismiss ? _dismissRect.Right : _joinRect.Right) + gap;
                textEnd = Width - pad - 2;
            }

            using (var b = new SolidBrush(AccentColor))
            using (var path = DrawHelper.RoundedRect(_joinRect, 8))
                g.FillPath(b, path);
            using (var f = FontHelper.Ui(10f, FontStyle.Bold))
                TextRenderer.DrawText(g, _joinText, f, _joinRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (ShowDismiss)
            {
                Color xc = IsDark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
                using (var f = FontHelper.Ui(11f))
                    TextRenderer.DrawText(g, "✕", f, _dismissRect, xc,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tw = Math.Max(0, textEnd - textStart);
            if (tw > 0)
            {
                Color tc = IsDark ? Color.FromArgb(205, 205, 210) : Color.FromArgb(70, 70, 76);
                var tr = new Rectangle(textStart, 0, tw, Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                          | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
                using (var f = FontHelper.For(_message, 9.5f))
                    TextRenderer.DrawText(g, _message, f, tr, tc, flags);
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
