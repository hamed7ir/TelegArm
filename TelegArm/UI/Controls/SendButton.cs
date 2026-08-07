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
    /// ⚠ ACCENT COMES FROM ThemeHelper. MaterialSkinManager's Accent slot is an app-wide singleton and
    ///   writing it re-poisons every other form (§2d / LESSONS_LEARNED.md:163).
    /// ⚠ NO Region, DELIBERATELY — and that is the X1 rule APPLIED, not skipped. Region is for when the
    ///   parent's colour cannot be relied on; this control sits on the composer bar, whose flat background
    ///   we know, so painting its own rounded fill over that known colour is correct and avoids a Region's
    ///   unantialiased edge. A Region here would look WORSE, not safer.
    /// ⚠ THE MIC STAYS A SEPARATE CONTROL. Telegram-Android morphs mic↔send as the input empties and
    ///   fills; that is a BEHAVIOUR change (what the button does, and when), not a look change, so it is
    ///   deliberately not built here. Follow-up if wanted.</summary>
    public class SendButton : Control
    {
        private bool _hover, _pressed;

        public SendButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(40, 40);          // identical to MicButton
            Cursor = Cursors.Hand;
            TabStop = false;
            BackColor = Color.Transparent;
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

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int d = Math.Min(Width, Height) - 4;
            var disc = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            Color accent = ThemeHelper.GetWindowsAccentColor();

            if (!Enabled)
            {
                // Disabled reads as a flat grey disc rather than a hidden control: the composer still shows
                // where send WILL be, which is what stops the row shifting when it becomes available.
                using (var b = new SolidBrush(ThemeHelper.IsDark ? Color.FromArgb(58, 58, 63) : Color.FromArgb(214, 214, 220)))
                    g.FillEllipse(b, disc);
                DrawPlane(g, disc, ThemeHelper.IsDark ? Color.FromArgb(96, 96, 102) : Color.FromArgb(168, 168, 176));
                return;
            }

            // Pressed darkens, hover lightens — the same two-state feedback the other composer buttons give.
            Color fill = _pressed ? Darken(accent, 0.82f) : _hover ? Lighten(accent, 0.12f) : accent;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, disc);
            DrawPlane(g, disc, Color.White);
        }

        /// <summary>The paper plane, drawn as a filled path rather than a font glyph — the same reason the
        /// header's magnifier and ⋮ are drawn: a font/emoji glyph renders inconsistently on RT.
        /// Slightly nudged right and up so the plane reads as centred (its visual mass sits left-low).</summary>
        private static void DrawPlane(Graphics g, Rectangle disc, Color ink)
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
                using (var p = new Pen(Color.FromArgb(70, ink.R, ink.G, ink.B), Math.Max(1f, s * 0.03f)))
                    g.DrawLine(p, notch, tip);
            }
        }

        private static Color Lighten(Color c, float f)
        {
            return Color.FromArgb(c.A,
                (int)Math.Min(255, c.R + (255 - c.R) * f),
                (int)Math.Min(255, c.G + (255 - c.G) * f),
                (int)Math.Min(255, c.B + (255 - c.B) * f));
        }
        private static Color Darken(Color c, float f)
        {
            return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }
    }
}
