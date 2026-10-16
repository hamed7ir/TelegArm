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
    ///
    /// Redesign step 3.2: lengths are logical (96-DPI) through Px at the row's DPI; the font is the shared FontCache one
    /// (never disposed). Height and Margin are SET from the logical values on parenting and on a DPI change, never multiplied.
    /// </summary>
    public sealed class ServiceLineControl : Control, IDpiAware
    {
        private const int RowH = 30, VGap = 3;   // logical px

        private readonly string _text;
        public bool IsDark { get; set; }

        private int _dpi;                                          // the DPI this row is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public ServiceLineControl(string text)
        {
            TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
            _text = text ?? "";
            ApplySize(CurDpi);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private void ApplySize(int dpi)
        {
            Height = Dpi.Px(RowH, dpi);
            Margin = new Padding(0, Dpi.Px(VGap, dpi), 0, Dpi.Px(VGap, dpi));
        }

        /// <summary>Rows are built before they are added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; ApplySize(d); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            ApplySize(newDpi);   // SET from the logical values: DpiScaler already ratio-scaled the bounds and Margin
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var pal = ThemeHelper.PaletteFor(IsDark);
            ChatBackdrop.PaintRow(g, this, e.ClipRectangle, IsDark, pal.ChatBg, null);   // step 5.4
            if (string.IsNullOrEmpty(_text) || Width < P(40)) return;
            bool scrim = ChatBackdrop.PillsNeedScrim(IsDark);   // over a background: a dark scrim with white text

            var f = FontHelper.ForAt(_text, 7.5f, dpi);   // shared: never dispose
            int maxTextW = Width - P(48);
            var sz = TextRenderer.MeasureText(g, _text, f, new Size(maxTextW, Height),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int pillW = Math.Min(Width - P(24), sz.Width + P(22));
            int pillH = Math.Min(Height - P(4), sz.Height + P(8));
            var pill = new Rectangle((Width - pillW) / 2, (Height - pillH) / 2, pillW, pillH);

            using (var b = new SolidBrush(scrim ? pal.ChatPillScrim : pal.ServicePillBg))
            using (var p = DrawHelper.RoundedRect(pill, pillH / 2))
                g.FillPath(b, p);

            Color tc = scrim ? pal.ChatPillText : pal.ServicePillText;
            TextRenderer.DrawText(g, _text, f, pill, tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
