using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 1.3 — a flat title-bar button whose glyph is DRAWN with GDI+ lines, ported from
    /// DroidFinestra's GlyphButton (Menu/Close/Back) and extended. No font, no emoji: astral and symbol glyphs come
    /// out as tofu boxes on RT 8.1 (e6f7108, 2fafcce), and a line drawing looks the same everywhere.
    ///
    /// It paints its own background in <see cref="BarColor"/> (normally the accent title bar) with a lighter wash on
    /// hover and a stronger one pressed; <see cref="DangerHover"/> makes it go red on hover (window Close). The glyph
    /// is <see cref="GlyphColor"/>, or the readable colour on the bar (ColorMath.OnColor) when that is Empty.
    /// Sizes are logical (96-DPI) and scaled to the DPI of its window at paint time.</summary>
    public sealed class GlyphButton : Control, IDpiAware
    {
        public enum Glyph { Menu, Close, Back, Minimize, Maximize, Restore, Search, More, Plus, Check }

        private Glyph _glyph;
        private readonly Fade _hover, _down;   // 0..1: the hover and press colours fade (Helpers.Anim)

        public Color BarColor { get; set; } = ThemeHelper.Palette.GlyphButtonBar;
        public Color GlyphColor { get; set; } = Color.Empty;
        public bool DangerHover { get; set; }

        public Glyph Kind
        {
            get { return _glyph; }
            set { if (_glyph == value) return; _glyph = value; AccessibleName = NameOf(value); Invalidate(); }
        }

        public GlyphButton(Glyph glyph)
        {
            _glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // two quick taps are two clicks, not a double-click
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = NameOf(glyph);
            Cursor = Cursors.Hand;
            TabStop = false;
            _hover = new Fade(this, AnimKind.Hover, Anim.HoverInMs, Anim.HoverOutMs);
            _down = new Fade(this, AnimKind.Hover, Anim.PressInMs, Anim.PressOutMs);
        }

        private static string NameOf(Glyph g)
        {
            switch (g)
            {
                case Glyph.Menu: return "Menu";
                case Glyph.Close: return "Close";
                case Glyph.Back: return "Back";
                case Glyph.Minimize: return "Minimize";
                case Glyph.Maximize: return "Maximize";
                case Glyph.Restore: return "Restore";
                case Glyph.Search: return "Search";
                case Glyph.More: return "More options";
                case Glyph.Plus: return "Add";
                default: return "Done";
            }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { _hover.Set(true); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Set(false); _down.Set(false); Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down.Set(true); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down.Set(false); Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg = BarColor;
            Color fg = GlyphColor.IsEmpty ? ColorMath.OnColor(BarColor) : GlyphColor;
            // The same colours as before at rest, hovered and pressed (pressed wins), mixed by how far each has come.
            float ht = _hover.T, dt = _down.T;
            if (DangerHover && (ht > 0f || dt > 0f))
            {
                Color danger = ThemeHelper.Palette.Danger;
                float any = Math.Max(ht, dt);
                bg = ColorMath.Mix(ColorMath.Mix(BarColor, danger, any), ColorMath.Mix(danger, Color.Black, 0.15f), dt);
                fg = ColorMath.Mix(fg, Color.White, any);
            }
            else
            {
                Color ink = fg;
                if (ht > 0f) bg = ColorMath.Mix(BarColor, ColorMath.Mix(BarColor, ink, 0.16f), ht);
                if (dt > 0f) bg = ColorMath.Mix(bg, ColorMath.Mix(BarColor, ink, 0.28f), dt);
            }
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);

            int dpi = Dpi.Of(this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f;
            Func<float, float> P = v => Dpi.F(v, dpi);
            using (var pen = new Pen(fg, P(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (_glyph)
                {
                    case Glyph.Menu:
                        g.DrawLine(pen, cx - P(9), cy - P(6), cx + P(9), cy - P(6));
                        g.DrawLine(pen, cx - P(9), cy, cx + P(9), cy);
                        g.DrawLine(pen, cx - P(9), cy + P(6), cx + P(9), cy + P(6));
                        break;
                    case Glyph.Close:
                        g.DrawLine(pen, cx - P(5.5f), cy - P(5.5f), cx + P(5.5f), cy + P(5.5f));
                        g.DrawLine(pen, cx + P(5.5f), cy - P(5.5f), cx - P(5.5f), cy + P(5.5f));
                        break;
                    case Glyph.Back:
                        g.DrawLine(pen, cx + P(5), cy - P(6), cx - P(4), cy);
                        g.DrawLine(pen, cx - P(4), cy, cx + P(5), cy + P(6));
                        break;
                    case Glyph.Minimize:
                        g.DrawLine(pen, cx - P(6), cy, cx + P(6), cy);
                        break;
                    case Glyph.Maximize:
                        g.DrawRectangle(pen, cx - P(5.5f), cy - P(5.5f), P(11), P(11));
                        break;
                    case Glyph.Restore:
                        g.DrawRectangle(pen, cx - P(6), cy - P(3), P(9), P(9));
                        g.DrawLine(pen, cx - P(3), cy - P(3), cx - P(3), cy - P(6));
                        g.DrawLine(pen, cx - P(3), cy - P(6), cx + P(6), cy - P(6));
                        g.DrawLine(pen, cx + P(6), cy - P(6), cx + P(6), cy + P(3));
                        g.DrawLine(pen, cx + P(6), cy + P(3), cx + P(3), cy + P(3));
                        break;
                    case Glyph.Search:
                        g.DrawEllipse(pen, cx - P(7), cy - P(7), P(10.5f), P(10.5f));
                        g.DrawLine(pen, cx + P(2.2f), cy + P(2.2f), cx + P(7), cy + P(7));
                        break;
                    case Glyph.More:
                        using (var dot = new SolidBrush(fg))
                            for (int i = -1; i <= 1; i++)
                                g.FillEllipse(dot, cx - P(1.8f), cy + i * P(6) - P(1.8f), P(3.6f), P(3.6f));
                        break;
                    case Glyph.Plus:
                        g.DrawLine(pen, cx - P(7), cy, cx + P(7), cy);
                        g.DrawLine(pen, cx, cy - P(7), cx, cy + P(7));
                        break;
                    case Glyph.Check:
                        g.DrawLine(pen, cx - P(7), cy, cx - P(2), cy + P(5));
                        g.DrawLine(pen, cx - P(2), cy + P(5), cx + P(7), cy - P(5));
                        break;
                }
            }
            if (Focused && ShowFocusCues)
                using (var fp = new Pen(ColorMath.WithAlpha(fg, 160), 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(fp, 2, 2, Width - 5, Height - 5);
        }
    }
}
