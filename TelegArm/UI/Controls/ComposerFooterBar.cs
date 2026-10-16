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
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI; fonts are shared FontHelper.UiAt / ForAt fonts (never disposed). Identity at 96. The height is the
    /// host's (MainForm's input row, which it shares with the composer).
    /// </summary>
    public sealed class ComposerFooterBar : Control, IDpiAware
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }
        public event EventHandler ActionClicked;

        private string _text = "";
        private bool _isButton;
        private Rectangle _btnRect;

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public ComposerFooterBar()
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(ThemeHelper.PaletteFor(IsDark).FooterBarBg);
            using (var p = new Pen(ThemeHelper.PaletteFor(IsDark).FooterBarLine))
                g.DrawLine(p, 0, 0, Width, 0);   // top separator, like the other bars (a 1 px hairline at every DPI)

            if (string.IsNullOrEmpty(_text)) { _btnRect = Rectangle.Empty; return; }

            if (_isButton)
            {
                // shared fonts: never dispose
                int textW = TextRenderer.MeasureText(_text, FontHelper.UiAt(10f, dpi, FontStyle.Bold)).Width;
                int bw = Math.Min(Width - P(24), Math.Max(P(160), textW + P(60)));
                int bh = Math.Min(Height - P(16), P(40));
                _btnRect = new Rectangle((Width - bw) / 2, (Height - bh) / 2, bw, bh);
                using (var b = new SolidBrush(AccentColor))
                using (var path = DrawHelper.RoundedRect(_btnRect, P(8)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, _text, FontHelper.ForAt(_text, 10f, dpi, FontStyle.Bold), _btnRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                _btnRect = Rectangle.Empty;
                Color c = ThemeHelper.PaletteFor(IsDark).FooterBarGlyph;
                var r = new Rectangle(P(16), 0, Width - P(32), Height);
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                          | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                          | (IsRtl(_text) ? TextFormatFlags.RightToLeft : 0);
                TextRenderer.DrawText(g, _text, FontHelper.ForAt(_text, 9.5f, dpi), r, c, flags);
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
