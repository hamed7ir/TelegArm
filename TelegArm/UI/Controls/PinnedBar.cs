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
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI (the icon's hit box too); fonts are shared FontHelper.UiAt / ForAt fonts (never disposed). Identity
    /// at 96. The height is the host's (MainForm's pinned-bar row).
    /// </summary>
    public sealed class PinnedBar : Control, IDpiAware
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        private string _title = "Pinned message";
        private string _preview = "";

        /// <summary>Tapping the bar body cycles to the next pin.</summary>
        public event EventHandler BarClicked;
        /// <summary>Tapping the right-edge list icon opens the full pinned list.</summary>
        public event EventHandler ShowAllClicked;

        private const int IconBox = 34;   // right-edge hit area for the "show all pinned" list icon (logical px)

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public PinnedBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
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

        public void SetContent(string title, string preview)
        {
            _title = title ?? "Pinned message";
            _preview = preview ?? "";
            if (!IsDisposed) Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.X >= Width - Dpi.Px(IconBox, CurDpi)) ShowAllClicked?.Invoke(this, EventArgs.Empty);   // right-edge icon → list
            else BarClicked?.Invoke(this, EventArgs.Empty);                                              // body → cycle next
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.Clear(ThemeHelper.PaletteFor(IsDark).PinnedBarBg);

            // Accent bar at the leading edge.
            using (var b = new SolidBrush(ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).PinnedBarBg, 3.0)))
                g.FillRectangle(b, P(12), P(8), P(3), Math.Max(P(4), Height - P(16)));

            int x = P(24), iconBox = P(IconBox);
            int cw = Math.Max(0, Width - x - iconBox);   // reserve the right-edge list icon
            Color prevColor = ThemeHelper.PaletteFor(IsDark).PinnedBarPreview;

            // "Show all pinned" list icon (stacked lines) at the right edge.
            int iconW = P(16), ix = Width - iconBox + (iconBox - iconW) / 2, iy = Height / 2 - P(5);
            using (var ip = new Pen(ThemeHelper.PaletteFor(IsDark).PinnedBarIcon, Dpi.F(2f, dpi)))
            {
                g.DrawLine(ip, ix, iy, ix + iconW, iy);
                g.DrawLine(ip, ix, iy + P(5), ix + iconW, iy + P(5));
                g.DrawLine(ip, ix, iy + P(10), ix + iconW, iy + P(10));
            }

            // Clip ALL text to the content area so nothing (incl. a custom-emoji box glyph) bleeds past the bar.
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, 0, cw, Height));

            // Two single-line rows, each vertically centered + ellipsized + clipped (NO second-line bleed).
            const TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            // shared fonts: never dispose
            TextRenderer.DrawText(g, _title, FontHelper.UiAt(8.25f, dpi, FontStyle.Bold), new Rectangle(x, P(4), cw, P(15)), ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).PinnedBarBg),
                baseFlags | TextFormatFlags.Left);

            // Preview: give it the taller remaining space so Persian isn't vertically clipped; RTL right-aligned.
            var prevRect = new Rectangle(x, P(18), cw, Math.Max(P(14), Height - P(18) - P(4)));
            bool rtl = IsRtl(_preview);
            var prevFlags = baseFlags | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            TextRenderer.DrawText(g, _preview, FontHelper.ForAt(_preview, 9f, dpi), prevRect, prevColor, prevFlags);

            g.Clip = oldClip;

            using (var p = new Pen(ThemeHelper.PaletteFor(IsDark).PinnedBarDivider))
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
