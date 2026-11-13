using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>How a <see cref="RoundedButton"/> is coloured (its role), so a destructive button stays red
    /// and a list-row stays subtle rather than everything becoming accent-blue.</summary>
    public enum RoundedButtonKind { Primary, Secondary, Neutral, Danger }

    /// <summary>
    /// BUTTON-CONSISTENCY: a rounded, accent-aware button (rounded corners + the app accent) that
    /// keeps STANDARD Button metrics and text casing (no uppercase, no ripple, no auto-height)
    /// so it drops into existing form layouts without shifting them. Reads the accent + dark/light from
    /// <see cref="ThemeHelper"/> and recolours live on <see cref="ThemeHelper.ThemeChanged"/>.
    ///   Primary   = accent-filled  (the main action: Save / Create / Apply / OK)
    ///   Secondary = accent-outlined (secondary action: Cancel / Check)
    ///   Neutral   = subtle surface  (list-row / low-emphasis)
    ///   Danger    = red             (destructive: Delete / Remove / Leave / Unlink — NEVER forced to accent)
    ///
    /// Redesign step 1.4: it is a Button, so it already IS an IButtonControl — AcceptButton / CancelButton and
    /// DialogResult work on it (DroidFinestra's comment "RoundedButton isn't an IButtonControl" was about its own
    /// Control-based copy). Colours now come from the cached <see cref="ThemeHelper.Palette"/> (same values, no
    /// registry read per paint). <see cref="Modern"/> opts a button into the 2.x behaviour.
    /// </summary>
    public sealed class RoundedButton : Button
    {
        private RoundedButtonKind _kind = RoundedButtonKind.Primary;
        private int _radius = 8;
        private readonly Fade _hover, _down;   // 0..1: the hover and press colours fade (Helpers.Anim)

        public RoundedButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }
        public int Radius { get { return _radius; } set { _radius = value; Invalidate(); } }

        /// <summary>Redesign step 1.4 — the 2.x button: radius and text insets in logical pixels (Px at the window's
        /// DPI), text that keeps its contrast on any accent (OnAccent / AccentFg), and a focus ring when the keyboard
        /// is in use. FALSE by default so the 17 forms that use this button today stay pixel-identical (zero diff vs
        /// B1); DialogForm and every new 2.x surface set it. The flag goes when the last 1.x form does.</summary>
        public bool Modern { get { return _modern; } set { _modern = value; Invalidate(); } }
        private bool _modern;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            _hover = new Fade(this, AnimKind.Hover, Anim.HoverInMs, Anim.HoverOutMs);
            _down = new Fade(this, AnimKind.Hover, Anim.PressInMs, Anim.PressOutMs);
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged() { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke((Action)Invalidate); } catch { } } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover.Set(true); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.Set(false); _down.Set(false); Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down.Set(true); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down.Set(false); Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var pal = ThemeHelper.Palette;
            bool dark = pal.Dark;
            Color accent = pal.Accent;
            int dpi = _modern ? Dpi.Of(this) : Dpi.Logical;
            Color pbg = Parent != null ? Parent.BackColor : pal.DialogPanelBg;
            // so the rounded corners reveal the form behind, not a square (step 5.9: the window backdrop when it is on)
            WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, pal.DialogPanelBg);

            Color fill, text; Color border = Color.Empty;
            switch (_kind)
            {
                case RoundedButtonKind.Danger:
                    fill = pal.Danger; text = _modern ? ColorMath.OnColor(fill) : Color.White; break;
                case RoundedButtonKind.Neutral:
                    if (_modern)
                    {
                        // On DroidFinestra's footer the 1.x light fill (234,234,238) vanishes into (236,236,240): a card
                        // with a hairline reads as a button on any surface, both themes.
                        fill = pal.Card; text = pal.Text; border = pal.Divider; break;
                    }
                    fill = pal.RoundedBtnNeutralFill;
                    text = pal.RoundedBtnNeutralText; break;
                case RoundedButtonKind.Secondary:
                    fill = pbg; text = _modern ? pal.AccentFg : accent; border = _modern ? pal.AccentGlyph : accent; break;
                default:   // Primary
                    fill = accent; text = pal.OnAccent; break;
            }

            if (!Enabled)
            {
                fill = pal.RoundedBtnDisabledFill;
                text = pal.RoundedBtnDisabledText;
                border = Color.Empty;
            }
            else
            {
                // The same three colours as before (rest, hover, pressed: pressed wins), now mixed by how far each has come.
                float ht = _hover.T, dt = _down.T;
                Color rest = fill;
                if (ht > 0f)
                {
                    Color over = _kind == RoundedButtonKind.Secondary ? Blend(pbg, accent, 0.12f)
                               : _modern && _kind == RoundedButtonKind.Neutral ? Blend(rest, pal.Text, 0.07f)   // a white card cannot lighten
                                                                               : Blend(rest, Color.White, dark ? 0.10f : 0.06f);
                    fill = ht >= 1f ? over : ColorMath.Mix(rest, over, ht);
                }
                if (dt > 0f)
                {
                    Color pressed = Blend(rest, Color.Black, 0.16f);
                    fill = dt >= 1f ? pressed : ColorMath.Mix(fill, pressed, dt);
                }
            }

            int radius = Dpi.Px(_radius, dpi);
            var r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = DrawHelper.RoundedRect(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty && Enabled) using (var p = new Pen(border, Dpi.F(1.4f, dpi))) g.DrawPath(p, path);
            }
            // Modern: the keyboard focus ring — inside the button, in the text colour, so it shows on every kind
            // (an accent ring would vanish around an accent-filled Primary). Only when focus cues are on (keyboard).
            if (_modern && Focused && ShowFocusCues && Enabled)
            {
                int inset = Dpi.Px(3, dpi);
                var fr = Rectangle.Inflate(r, -inset, -inset);
                if (fr.Width > 4 && fr.Height > 4)
                    using (var path = DrawHelper.RoundedRect(fr, Math.Max(1, radius - inset)))
                    using (var p = new Pen(text, Dpi.F(1.5f, dpi))) g.DrawPath(p, path);
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            bool left = TextAlign == ContentAlignment.MiddleLeft || TextAlign == ContentAlignment.TopLeft || TextAlign == ContentAlignment.BottomLeft;
            bool right = TextAlign == ContentAlignment.MiddleRight || TextAlign == ContentAlignment.TopRight || TextAlign == ContentAlignment.BottomRight;
            flags |= left ? TextFormatFlags.Left : right ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
            var tr = new Rectangle(Padding.Left + Dpi.Px(left ? 8 : 2, dpi), 0, Math.Max(1, Width - Padding.Left - Padding.Right - Dpi.Px(left ? 10 : 4, dpi)), Height);
            TextRenderer.DrawText(g, Text ?? "", Font, tr, text, flags);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return ColorMath.Rgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
