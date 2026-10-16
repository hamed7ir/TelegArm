using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>BATCH-TA-34/A — the composer's send button: a round accent disc with a DRAWN paper-plane,
    /// Telegram-Android style, replacing a MaterialButton that said "Send".
    ///
    /// ⚠ EVERY METRIC IS COPIED FROM <see cref="MicButton"/>, NOT INVENTED — the same 40x40, the same
    ///   ControlStyles set, the same Cursors.Hand, the same TabStop=false, the same ThemeChanged →
    ///   Invalidate hook. It sits in the same composer row as the mic, the emoji and the attach buttons,
    ///   and two adjacent buttons that disagree by two pixels look like a bug. This is the same discipline
    ///   the header ⋮ used when it copied the magnifier.
    /// ⚠ ACCENT COMES FROM ThemeHelper.Palette.Accent (the user's accent, else Windows') — never a hard-coded colour, never a
    ///   registry read in paint.
    /// ⚠ NO Region, DELIBERATELY — and that is the X1 rule APPLIED, not skipped. Region is for when the
    ///   parent's colour cannot be relied on; this control sits on the composer bar, whose flat background
    ///   we know, so painting its own rounded fill over that known colour is correct and avoids a Region's
    ///   unantialiased edge. A Region here would look WORSE, not safer.
    /// ⚠ THE MIC STAYS A SEPARATE CONTROL. Telegram-Android morphs mic↔send as the input empties and
    ///   fills; that is a BEHAVIOUR change (what the button does, and when), not a look change, so it is
    ///   deliberately not built here. Follow-up if wanted.
    /// Redesign step 3.2: lengths are LOGICAL (96-DPI) and drawn through Px at the button's DPI (the window's, once
    ///   parented); IDpiAware SETS the 40x40 from the logical value, never multiplies. Identity at 96.</summary>
    public class SendButton : Control, IDpiAware
    {
        private const int Side = 40;   // logical px — identical to MicButton

        private bool _hover, _pressed;
        private int _dpi;                                          // the DPI this button is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public SendButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));   // identical to MicButton
            Cursor = Cursors.Hand;
            TabStop = false;
            // ⚠⚠ DO NOT SET `BackColor = Color.Transparent` HERE. A plain Control REJECTS it —
            //    "Control does not support transparent background colors" — and it throws in the CTOR,
            //    so the app does not start at all. It shipped in v1.9.0 and crashed TelegArm on launch
            //    on the RT device (crash.log, 2026-08-06 16:28).
            //    MicButton — the control every metric here was supposed to be copied from — does not set
            //    BackColor at all; it INHERITS the composer bar's colour, which is what makes the area
            //    outside the drawn disc match. Adding that one line was the deviation, and it was the bug.
            //    If a genuinely transparent background is ever needed, it requires
            //    SetStyle(ControlStyles.SupportsTransparentBackColor, true) — but it is not needed here.
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)Invalidate); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;   // static event → unsubscribe
            base.Dispose(disposing);
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Size = new Size(Dpi.Px(Side, d), Dpi.Px(Side, d)); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Size = new Size(Dpi.Px(Side, newDpi), Dpi.Px(Side, newDpi));   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            float k = dpi / 96f;   // exactly 1 at 96
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ⚠ THE DISC IS SMALLER THAN THE CONTROL, AND THAT IS THE WHOLE POINT.
            //   The control is 40x40 to match MicButton's CELL, but the neighbours draw thin LINE ART
            //   inside that cell — a filled disc spanning the same 36px reads far heavier than a mic or a
            //   paperclip outline and made the composer look unbalanced. 30px is the filled-shape
            //   equivalent of their optical weight: the ink stops well short of the cell edge, the way
            //   theirs does. Same lesson as the header glyph box (TA-24) — matching the BOX is not the
            //   same as matching the WEIGHT.
            int d = Math.Min(P(30), Math.Min(Width, Height) - P(6));
            var disc = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            Color accent = ThemeHelper.Palette.Accent;

            if (!Enabled)
            {
                // Disabled reads as a flat grey disc rather than a hidden control: the composer still shows
                // where send WILL be, which is what stops the row shifting when it becomes available.
                using (var b = new SolidBrush(ThemeHelper.Palette.SendDisabledFill))
                    g.FillEllipse(b, disc);
                DrawPlane(g, disc, ThemeHelper.Palette.SendDisabledGlyph, k);
                return;
            }

            // Pressed darkens, hover lightens — the same two-state feedback the other composer buttons give.
            Color fill = _pressed ? Darken(accent, 0.82f) : _hover ? Lighten(accent, 0.12f) : accent;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, disc);
            DrawPlane(g, disc, ThemeHelper.Palette.OnAccent, k);   // step 5.1: dark ink on a light accent (was white)
        }

        /// <summary>The paper plane, drawn as a filled path rather than a font glyph — the same reason the
        /// header's magnifier and ⋮ are drawn: a font/emoji glyph renders inconsistently on RT.
        /// Slightly nudged right and up so the plane reads as centred (its visual mass sits left-low).
        /// Its geometry scales with the disc (already Px-sized); <paramref name="k"/> (dpi/96) only scales the crease's
        /// 1 px floor.</summary>
        private static void DrawPlane(Graphics g, Rectangle disc, Color ink, float k)
        {
            float s = disc.Width;
            float cx = disc.X + s / 2f + s * 0.02f;
            float cy = disc.Y + s / 2f;
            float w = s * 0.46f;                 // half-width of the plane

            using (var path = new GraphicsPath())
            {
                // A swept triangle with a notched tail — the classic Telegram send mark.
                PointF tip = new PointF(cx + w, cy);
                PointF top = new PointF(cx - w, cy - w * 0.78f);
                PointF bottom = new PointF(cx - w, cy + w * 0.78f);
                PointF notch = new PointF(cx - w * 0.42f, cy);

                path.AddPolygon(new[] { top, tip, bottom, notch });
                using (var b = new SolidBrush(ink)) g.FillPath(b, path);

                // The centre crease: a hairline back to the notch, which is what makes it read as folded
                // paper rather than as a plain arrow.
                using (var p = new Pen(ColorMath.WithAlpha(ink, 70), Math.Max(1f * k, s * 0.03f)))
                    g.DrawLine(p, notch, tip);
            }
        }

        private static Color Lighten(Color c, float f)
        {
            return ColorMath.Argb(c.A,
                (int)Math.Min(255, c.R + (255 - c.R) * f),
                (int)Math.Min(255, c.G + (255 - c.G) * f),
                (int)Math.Min(255, c.B + (255 - c.B) * f));
        }
        private static Color Darken(Color c, float f)
        {
            return ColorMath.Argb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }
    }
}
