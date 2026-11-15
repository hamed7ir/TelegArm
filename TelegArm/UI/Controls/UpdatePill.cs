using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner 2026-11-15: "update button can appear like the proxy pill on titlebar". A pill in the title bar, drawn the way
    /// the proxy pill is on the accent (a lighter rounded fill, the bar's ink): a download arrow and "Update"; while the
    /// installer downloads, "Updating 45%" with the part done a shade lighter. <see cref="OnAccent"/> false = the native
    /// chrome fallback, an accent pill floating over the chat list.
    /// </summary>
    public sealed class UpdatePill : Control
    {
        private int _progress = -1;   // -1 = idle ("Update"), 0..100 = downloading
        private bool _hover, _pressed;
        private const int PillH = 24;  // logical: the title-bar pill height (fits the 32 px compact bar)

        public UpdatePill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Update TelegArm";
            Visible = false;
        }

        /// <summary>On the accent title bar (true) or floating over the chat list (false).</summary>
        public bool OnAccent { get; set; } = true;

        /// <summary>The new version, for the tooltip-free accessible name ("Update TelegArm to 2.1.0").</summary>
        public string Version
        {
            set { AccessibleName = "Update TelegArm to " + value; }
        }

        /// <summary>-1 idle; 0..100 while the installer downloads.</summary>
        public int Progress
        {
            get { return _progress; }
            set
            {
                int v = Math.Max(-1, Math.Min(100, value));
                if (v == _progress) return;
                _progress = v;
                Fit();
                Invalidate();
            }
        }

        private string Caption() { return _progress < 0 ? "Update" : "Updating " + _progress + "%"; }

        private Font CaptionFont(int dpi) { return FontHelper.UiAt(9f, dpi, FontStyle.Bold); }   // shared: never dispose

        /// <summary>Sizes the pill to its text at the window's DPI (the widest progress text, so it does not jump).</summary>
        public void Fit()
        {
            int dpi = Dpi.Of(this);
            string widest = _progress < 0 ? "Update" : "Updating 100%";
            var t = TextRenderer.MeasureText(widest, CaptionFont(dpi));
            int h = Math.Max(Dpi.Px(PillH, dpi), t.Height + Dpi.Px(6, dpi));
            int w = h + t.Width + Dpi.Px(12, dpi);   // the icon in a square of the height, then the text
            var want = new Size(w, h);
            if (Size != want) Size = want;
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Fit(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pal = ThemeHelper.Palette;
            int dpi = Dpi.Of(this);
            Color back = OnAccent ? pal.TitleBar : (Parent != null ? Parent.BackColor : pal.ListBg);
            Color ink = OnAccent ? pal.OnAccent : ColorMath.OnColor(pal.Accent);
            Color baseFill = OnAccent ? pal.TitleBar : pal.Accent;
            float lift = _pressed ? 0.30f : _hover ? 0.24f : 0.16f;
            Color fill = OnAccent ? ColorMath.Mix(baseFill, ink, lift) : ColorMath.Mix(baseFill, ink, lift - 0.16f);
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = DrawHelper.RoundedRect(body, body.Height / 2))
            {
                using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
                if (_progress >= 0)
                {
                    var state = g.Save();
                    g.SetClip(path);
                    int done = body.Width * _progress / 100;
                    using (var pb = new SolidBrush(ColorMath.Mix(fill, ink, 0.18f))) g.FillRectangle(pb, body.X, body.Y, done, body.Height + 1);
                    g.Restore(state);
                }
            }

            // the download arrow: a stem, a chevron and a tray, in the left square
            float s = body.Height * 0.5f, cx = body.Height / 2f + Dpi.Px(2, dpi), cy = body.Height / 2f;
            using (var pen = new Pen(ink, Math.Max(1.4f, s * 0.14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, cx, cy - s * 0.48f, cx, cy + s * 0.12f);
                g.DrawLines(pen, new[] { new PointF(cx - s * 0.30f, cy - s * 0.16f), new PointF(cx, cy + s * 0.14f), new PointF(cx + s * 0.30f, cy - s * 0.16f) });
                g.DrawLines(pen, new[] { new PointF(cx - s * 0.42f, cy + s * 0.22f), new PointF(cx - s * 0.42f, cy + s * 0.42f), new PointF(cx + s * 0.42f, cy + s * 0.42f), new PointF(cx + s * 0.42f, cy + s * 0.22f) });
            }
            var textRect = new Rectangle(body.Height, 0, Math.Max(0, Width - body.Height - Dpi.Px(8, dpi)), Height);
            TextRenderer.DrawText(g, Caption(), CaptionFont(dpi), textRect, ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
