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
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI; fonts are shared FontHelper.UiAt / ForAt fonts (never disposed). Identity at 96. The height is the
    /// host's (MainForm's thread-join row).
    /// </summary>
    public sealed class ThreadJoinBar : Control, IDpiAware
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }
        public bool ShowDismiss { get; set; } = true;
        public event EventHandler JoinClicked;
        public event EventHandler DismissClicked;

        private string _message = "Join the group to get replies as messages";
        private readonly string _joinText = "Join";
        private Rectangle _joinRect, _dismissRect;

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public ThreadJoinBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
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
            _dpi = newDpi;   // no size of its own: DpiScaler scaled the bounds; every length is derived at paint
            Invalidate();
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(ThemeHelper.PaletteFor(IsDark).FooterBarBg);
            using (var p = new Pen(ThemeHelper.PaletteFor(IsDark).FooterBarLine))
                g.DrawLine(p, 0, 0, Width, 0);   // top separator, like the other bars (a 1 px hairline at every DPI)

            bool rtl = IsRtl(_message);
            int pad = P(12), gap = P(8);
            int bh = Math.Min(Height - P(12), P(34));
            int by = (Height - bh) / 2;

            var joinFont = FontHelper.UiAt(10f, dpi, FontStyle.Bold);   // shared: never dispose
            int joinW = Math.Max(P(88), TextRenderer.MeasureText(_joinText, joinFont).Width + P(44));
            int dw = ShowDismiss ? P(30) : 0;

            // The Join button sits on the trailing edge; ✕ just inside it; the message fills the leading band.
            // RTL mirrors leading/trailing (Join on the left, message right-aligned).
            int textStart, textEnd;
            if (!rtl)
            {
                _joinRect = new Rectangle(Width - pad - joinW, by, joinW, bh);
                _dismissRect = ShowDismiss ? new Rectangle(_joinRect.Left - gap - dw, by, dw, bh) : Rectangle.Empty;
                textStart = pad + P(2);
                textEnd = (ShowDismiss ? _dismissRect.Left : _joinRect.Left) - gap;
            }
            else
            {
                _joinRect = new Rectangle(pad, by, joinW, bh);
                _dismissRect = ShowDismiss ? new Rectangle(_joinRect.Right + gap, by, dw, bh) : Rectangle.Empty;
                textStart = (ShowDismiss ? _dismissRect.Right : _joinRect.Right) + gap;
                textEnd = Width - pad - P(2);
            }

            using (var b = new SolidBrush(AccentColor))
            using (var path = DrawHelper.RoundedRect(_joinRect, P(8)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, _joinText, joinFont, _joinRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (ShowDismiss)
            {
                Color xc = ThemeHelper.PaletteFor(IsDark).FooterBarGlyph;
                TextRenderer.DrawText(g, "✕", FontHelper.UiAt(11f, dpi), _dismissRect, xc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tw = Math.Max(0, textEnd - textStart);
            if (tw > 0)
            {
                Color tc = ThemeHelper.PaletteFor(IsDark).FooterBarText;
                var tr = new Rectangle(textStart, 0, tw, Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                          | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
                TextRenderer.DrawText(g, _message, FontHelper.ForAt(_message, 9.5f, dpi), tr, tc, flags);
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
