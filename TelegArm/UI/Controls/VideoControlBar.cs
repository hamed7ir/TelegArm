using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted video transport bar, styled to match <see cref="MiniPlayerBar"/>
    /// (accent circles for prev / play-pause / next, owner-drawn seek + time, mute).
    /// Lives in a TableLayoutPanel row BELOW the VLC surface. Like MiniPlayerBar it
    /// paints the WHOLE control itself (no child controls) and hit-tests by region —
    /// the one owner-painted style that renders in a toolbar row. A periodic
    /// Invalidate() (driven by the host's timer) keeps it visible over VLC repaints.
    /// </summary>
    public class VideoControlBar : Control
    {
        private readonly VlcVideoControl _video;

        private Rectangle _prevR, _playR, _nextR, _seekR, _muteR;
        private bool _dragging;
        private bool _muted;

        public Color AccentColor { get; set; } = Color.DodgerBlue;

        /// <summary>Whether the host has a previous/next media item to navigate to.</summary>
        public bool CanPrev { get; set; }
        public bool CanNext { get; set; }

        /// <summary>Raised when the prev/next circle is clicked (host performs navigation).</summary>
        public event Action PrevRequested;
        public event Action NextRequested;

        private bool _isDark = true;
        public bool IsDark
        {
            get { return _isDark; }
            set { _isDark = value; BackColor = BarColor; Invalidate(); }
        }

        private Color BarColor => _isDark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(236, 236, 238);
        private Color TextColor => _isDark ? Color.White : Color.FromArgb(30, 30, 30);
        private Color SubTextColor => _isDark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(95, 95, 95);
        private Color TrackColor => _isDark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(198, 198, 203);
        private Color DisabledColor => _isDark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(200, 200, 205);

        public VideoControlBar(VlcVideoControl video)
        {
            _video = video;
            Cursor = Cursors.Hand;
            BackColor = BarColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BarColor);

            int h = Height, cd = 30, cy = h / 2;
            _prevR = new Rectangle(10, cy - cd / 2, cd, cd);
            _playR = new Rectangle(10 + cd + 6, cy - cd / 2, cd, cd);
            _nextR = new Rectangle(10 + 2 * (cd + 6), cy - cd / 2, cd, cd);

            const int rightPad = 12;
            _muteR = new Rectangle(Width - rightPad - 28, cy - 12, 28, 24);
            int totW = 66; var totR = new Rectangle(_muteR.X - 8 - totW, 0, totW, h);
            int curW = 66; var curR = new Rectangle(_nextR.Right + 10, 0, curW, h);
            int seekX = curR.Right + 6;
            _seekR = new Rectangle(seekX, cy - 6, Math.Max(40, (totR.X - 6) - seekX), 12);

            // transport circles (dimmed when navigation isn't available)
            DrawCircle(g, _prevR, CanPrev); DrawPrev(g, _prevR);
            DrawCircle(g, _playR, true);
            if (_video != null && _video.IsPlaying) DrawPause(g, _playR); else DrawPlay(g, _playR);
            DrawCircle(g, _nextR, CanNext); DrawNext(g, _nextR);

            long timeMs = _video != null ? _video.Time : 0;
            long lenMs = _video != null ? _video.Length : 0;

            // times
            using (var gb = new SolidBrush(SubTextColor))
            using (var f = new Font("Segoe UI", 8.5f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Fmt(timeMs), f, gb, curR, sf);
                g.DrawString(Fmt(lenMs), f, gb, totR, sf);
            }

            // seek
            int ty = _seekR.Y + (_seekR.Height - 4) / 2;
            using (var tb = new SolidBrush(TrackColor))
                g.FillRectangle(tb, _seekR.X, ty, _seekR.Width, 4);
            float pos = _video != null ? _video.Position : 0f;
            if (pos < 0) pos = 0; else if (pos > 1) pos = 1;
            int fillW = (int)(pos * _seekR.Width);
            using (var fb = new SolidBrush(AccentColor))
                g.FillRectangle(fb, _seekR.X, ty, fillW, 4);
            using (var hb = new SolidBrush(AccentColor))
                g.FillEllipse(hb, _seekR.X + fillW - 5, _seekR.Y + _seekR.Height / 2 - 5, 10, 10);

            DrawMute(g, _muteR, _muted, TextColor);
        }

        private void DrawCircle(Graphics g, Rectangle r, bool enabled)
        {
            using (var b = new SolidBrush(enabled ? AccentColor : DisabledColor))
                g.FillEllipse(b, r);
        }

        private static void DrawPlay(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
                g.FillPolygon(w, new[] { new PointF(cx - 5, cy - 7), new PointF(cx + 7, cy), new PointF(cx - 5, cy + 7) });
        }

        private static void DrawPause(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            { g.FillRectangle(w, cx - 5, cy - 7, 3.5f, 14); g.FillRectangle(w, cx + 1.5f, cy - 7, 3.5f, 14); }
        }

        private static void DrawPrev(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, cx - 8, cy - 6, 2.5f, 12);
                g.FillPolygon(w, new[] { new PointF(cx + 6, cy - 6), new PointF(cx - 3, cy), new PointF(cx + 6, cy + 6) });
            }
        }

        private static void DrawNext(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillPolygon(w, new[] { new PointF(cx - 6, cy - 6), new PointF(cx + 3, cy), new PointF(cx - 6, cy + 6) });
                g.FillRectangle(w, cx + 5.5f, cy - 6, 2.5f, 12);
            }
        }

        private static void DrawMute(Graphics g, Rectangle r, bool muted, Color color)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(color))
            {
                g.FillRectangle(w, cx - 8, cy - 3, 4, 6);
                g.FillPolygon(w, new[] { new PointF(cx - 4, cy - 5), new PointF(cx + 1, cy - 8), new PointF(cx + 1, cy + 8), new PointF(cx - 4, cy + 5) });
                if (muted)
                    using (var rp = new Pen(Color.Red, 2f))
                    { g.DrawLine(rp, cx + 4, cy - 4, cx + 9, cy + 4); g.DrawLine(rp, cx + 9, cy - 4, cx + 4, cy + 4); }
                else
                    using (var p = new Pen(color, 1.6f))
                        g.DrawArc(p, cx + 2, cy - 6, 8, 12, -55, 110);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_prevR.Contains(e.Location)) { if (CanPrev) PrevRequested?.Invoke(); return; }
            if (_playR.Contains(e.Location)) { _video?.TogglePlay(); Invalidate(); return; }
            if (_nextR.Contains(e.Location)) { if (CanNext) NextRequested?.Invoke(); return; }
            if (_muteR.Contains(e.Location))
            {
                if (_video != null) _muted = _video.ToggleMute();
                Invalidate();
                return;
            }
            if (_seekR.Contains(e.Location)) { _dragging = true; SeekToX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SeekToX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }

        private void SeekToX(int x)
        {
            if (_seekR.Width <= 0 || _video == null) return;
            double ratio = (x - _seekR.X) / (double)_seekR.Width;
            if (ratio < 0) ratio = 0; else if (ratio > 1) ratio = 1;
            _video.Position = (float)ratio;
            Invalidate();
        }

        private static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
