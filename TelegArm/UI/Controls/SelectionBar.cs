using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted selection toolbar ("N selected" + Forward + Close), drawn as a single
    /// control with GDI+ (no child controls) so it themes cleanly and never shows the black
    /// background that transparent child controls produce. Same pattern as MiniPlayerBar.
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI, the font is a shared FontCache font (never disposed), the glyphs scale from their circles. Identity
    /// at 96. The height is the host's (MainForm's selection-bar row).
    /// </summary>
    public class SelectionBar : Control, IDpiAware
    {
        private Rectangle _fwdR, _closeR;

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public Color AccentColor { get; set; } = Color.DodgerBlue;

        private bool _isDark;
        public bool IsDark
        {
            get { return _isDark; }
            set { _isDark = value; BackColor = BarColor; Invalidate(); }
        }

        private int _count;
        public int Count
        {
            get { return _count; }
            set { _count = value; Invalidate(); }
        }

        public event Action ForwardRequested;
        public event Action CloseRequested;

        private Color BarColor => _isDark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(236, 236, 238);
        private Color TextColor => _isDark ? Color.White : Color.FromArgb(30, 30, 30);

        public SelectionBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = BarColor;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BarColor);

            int h = Height, cy = h / 2, d = P(30), rightPad = P(12);
            _closeR = new Rectangle(Width - rightPad - d, cy - d / 2, d, d);
            _fwdR = new Rectangle(_closeR.X - P(8) - d, cy - d / 2, d, d);

            string text = _count + (_count == 1 ? " selected" : " selected");
            var f = FontCache.Get("Segoe UI", 10f, FontStyle.Regular, dpi);   // shared: never dispose
            using (var b = new SolidBrush(TextColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(text, f, b, new RectangleF(P(16), 0, _fwdR.X - P(24), h), sf);

            DrawCircle(g, _fwdR); DrawForward(g, _fwdR);
            DrawCircle(g, _closeR); DrawClose(g, _closeR);
        }

        private void DrawCircle(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(AccentColor)) g.FillEllipse(b, r);
        }

        // The glyphs are drawn for the 30 px circle; they scale with it (k == 1 at 96).
        private static void DrawForward(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(Color.White, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, cx - 7 * k, cy, cx + 6 * k, cy);
                g.DrawLines(pen, new[] { new PointF(cx + 1 * k, cy - 5 * k), new PointF(cx + 6 * k, cy), new PointF(cx + 1 * k, cy + 5 * k) });
            }
        }

        private static void DrawClose(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(Color.White, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx - 5 * k, cy - 5 * k, cx + 5 * k, cy + 5 * k);
                g.DrawLine(pen, cx + 5 * k, cy - 5 * k, cx - 5 * k, cy + 5 * k);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_fwdR.Contains(e.Location)) ForwardRequested?.Invoke();
            else if (_closeR.Contains(e.Location)) CloseRequested?.Invoke();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = (_fwdR.Contains(e.Location) || _closeR.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }
    }

    /// <summary>
    /// Owner-painted recording/ready strip (red dot + caption), drawn as one control so it
    /// themes cleanly without the transparent-background black band.
    ///
    /// Redesign step 3.2: as SelectionBar above (LOGICAL px through Px, a shared FontCache font). Identity at 96.
    /// The size is the host's (the composer row's input cell).
    /// </summary>
    public class RecordingBar : Control, IDpiAware
    {
        private int _dpi;                                          // the DPI this strip is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        private bool _isDark;
        public bool IsDark
        {
            get { return _isDark; }
            set { _isDark = value; BackColor = BgColor; Invalidate(); }
        }

        public Color DotColor { get; set; } = Color.FromArgb(229, 57, 53);

        private string _caption = "";
        public string Caption
        {
            get { return _caption; }
            set { _caption = value ?? ""; Invalidate(); }
        }

        private Color BgColor => _isDark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(250, 250, 250);
        private Color TextColor => _isDark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(40, 40, 40);

        public RecordingBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = BgColor;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BgColor);

            int cy = Height / 2;
            using (var dot = new SolidBrush(DotColor))
                g.FillEllipse(dot, P(14), cy - P(5), P(10), P(10));

            var f = FontCache.Get("Segoe UI", 10f, FontStyle.Regular, dpi);   // shared: never dispose
            using (var b = new SolidBrush(TextColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(_caption, f, b, new RectangleF(P(32), 0, Width - P(40), Height), sf);
        }
    }
}
