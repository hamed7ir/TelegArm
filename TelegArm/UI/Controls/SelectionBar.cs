using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted selection toolbar ("N selected" + Forward + Close), drawn as a single
    /// control with GDI+ (no child controls) so it themes cleanly and never shows the black
    /// background that transparent child controls produce. Same pattern as MiniPlayerBar.
    /// </summary>
    public class SelectionBar : Control
    {
        private Rectangle _fwdR, _closeR;

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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BarColor);

            int h = Height, cy = h / 2, d = 30, rightPad = 12;
            _closeR = new Rectangle(Width - rightPad - d, cy - d / 2, d, d);
            _fwdR = new Rectangle(_closeR.X - 8 - d, cy - d / 2, d, d);

            string text = _count + (_count == 1 ? " selected" : " selected");
            using (var f = new Font("Segoe UI", 10f))
            using (var b = new SolidBrush(TextColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(text, f, b, new RectangleF(16, 0, _fwdR.X - 24, h), sf);

            DrawCircle(g, _fwdR); DrawForward(g, _fwdR);
            DrawCircle(g, _closeR); DrawClose(g, _closeR);
        }

        private void DrawCircle(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(AccentColor)) g.FillEllipse(b, r);
        }

        private static void DrawForward(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, cx - 7, cy, cx + 6, cy);
                g.DrawLines(pen, new[] { new PointF(cx + 1, cy - 5), new PointF(cx + 6, cy), new PointF(cx + 1, cy + 5) });
            }
        }

        private static void DrawClose(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
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
    /// </summary>
    public class RecordingBar : Control
    {
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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BgColor);

            int cy = Height / 2;
            using (var dot = new SolidBrush(DotColor))
                g.FillEllipse(dot, 14, cy - 5, 10, 10);

            using (var f = new Font("Segoe UI", 10f))
            using (var b = new SolidBrush(TextColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(_caption, f, b, new RectangleF(32, 0, Width - 40, Height), sf);
        }
    }
}
