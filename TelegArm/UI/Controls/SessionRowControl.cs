using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// One active-session row in Settings → Devices: the app/device title, the platform/system line, and the
    /// location · last-active line. Non-current rows draw a red ✕ that raises <see cref="TerminateClicked"/> (also Delete,
    /// and the accessible default action); the current session draws an accent bar and no ✕.
    /// Step 2.6: a <see cref="SettingRow"/> now — palette inks, logical sizes (was 70 px and fixed RGB), and it sits in a
    /// SettingGroup card with the rounded ends like every other row.
    /// </summary>
    public sealed class SessionRowControl : SettingRow
    {
        public long Hash { get; private set; }

        private bool _current;
        /// <summary>The session of this device: no ✕, not a tab stop.</summary>
        public bool IsCurrent
        {
            get { return _current; }
            set
            {
                _current = value;
                SetStyle(ControlStyles.Selectable, !value);
                TabStop = !value;
                Invalidate();
            }
        }

        // Kept for callers' initializers; the row paints from ThemeHelper.Palette so a live theme change recolours it.
        public Color AccentColor { get; set; }
        public bool IsDark { get; set; }

        public event Action<long> TerminateClicked;

        private readonly string _line2, _line3;
        private const int RowH = 76, XSize = 32;

        public SessionRowControl(long hash, string title, string line2, string line3) : base(title ?? "", true)
        {
            Hash = hash;
            _line2 = line2 ?? "";
            _line3 = line3 ?? "";
            Cursor = Cursors.Default;   // only the ✕ is a hand
            AccessibleRole = AccessibleRole.ListItem;
            AccessibleDescription = (_line2 + " " + _line3).Trim();
            Refit();
        }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(RowH, dpi); }

        private Rectangle XRect(int dpi)
        {
            int s = Dpi.Px(XSize, dpi);
            return new Rectangle(Width - Dpi.Px(PadX, dpi) - s + Dpi.Px(6, dpi), (Height - s) / 2, s, s);
        }

        private void Terminate()
        {
            if (_current || !Enabled) return;
            var h = TerminateClicked;
            if (h != null) h(Hash);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var c = !_current && XRect(Dpi.Of(this)).Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (Cursor != c) Cursor = c;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && XRect(Dpi.Of(this)).Contains(e.Location)) Terminate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Delete) { e.Handled = true; Terminate(); }
        }

        protected override string AccessibleAction { get { return _current ? null : "Terminate"; } }
        protected override void AccessibleInvoke() { Terminate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);

            if (_current)
                using (var ab = new SolidBrush(p.Accent))
                    g.FillRectangle(ab, Dpi.Px(4, dpi), Dpi.Px(14, dpi), Dpi.Px(3, dpi), Math.Max(4, Height - Dpi.Px(28, dpi)));

            var x = XRect(dpi);
            int right = _current ? Width - padX : x.X - Dpi.Px(8, dpi);
            int w = Math.Max(1, right - padX);
            var tf = FontHelper.ForAt(Text, CaptionPt, dpi, FontStyle.Bold);
            var f2 = FontHelper.ForAt(_line2, 8.75f, dpi);
            var f3 = FontHelper.ForAt(_line3, 8.5f, dpi);
            int h1 = LineHeight(tf), h2 = LineHeight(f2), h3 = LineHeight(f3), gap = Dpi.Px(2, dpi);
            int top = Math.Max(0, (Height - h1 - h2 - h3 - 2 * gap) / 2);
            Color dim = Enabled ? p.TextDim : p.TextDisabled;
            DrawTextLine(g, Text, tf, Enabled ? p.Text : p.TextDisabled, new Rectangle(padX, top, w, h1), false, true);
            DrawTextLine(g, _line2, f2, dim, new Rectangle(padX, top + h1 + gap, w, h2), false, true);
            DrawTextLine(g, _line3, f3, dim, new Rectangle(padX, top + h1 + h2 + 2 * gap, w, h3), false, true);

            if (!_current)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int u = Dpi.Px(9, dpi);
                using (var pen = new Pen(Enabled ? p.Danger : p.TextDisabled, Dpi.F(1.8f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, x.X + u, x.Y + u, x.Right - u, x.Bottom - u);
                    g.DrawLine(pen, x.Right - u, x.Y + u, x.X + u, x.Bottom - u);
                }
                g.SmoothingMode = old;
                PaintFocusRing(g, p, Rectangle.Inflate(x, -Dpi.Px(2, dpi), -Dpi.Px(2, dpi)), Dpi.Px(6, dpi), dpi);
            }
        }
    }
}
