using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using TelegArm.Core;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Persistent mini audio player bar, fully owner-painted (GDI+) like the message
    /// bubbles — the only style that renders reliably in this app. Prev / play-pause /
    /// next as accent circles, title, draggable seek, elapsed/total, speed, mute, close.
    /// </summary>
    public class MiniPlayerBar : UserControl
    {
        private static readonly float[] Speeds = { 1.0f, 1.5f, 2.0f };

        private readonly System.Windows.Forms.Timer _timer;
        private int _speedIdx;
        private bool _dragging;

        private Rectangle _prevR, _playR, _nextR, _seekR, _speedR, _muteR, _closeR;

        public Color AccentColor { get; set; } = Color.DodgerBlue;

        private bool _isDark = true;
        /// <summary>Follows the app theme; drives the bar background/text colors.</summary>
        public bool IsDark
        {
            get { return _isDark; }
            set { _isDark = value; BackColor = BarColor; Invalidate(); }
        }

        private Color BarColor => _isDark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(236, 236, 238);
        private Color TextColor => _isDark ? Color.White : Color.FromArgb(30, 30, 30);
        private Color SubTextColor => _isDark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(95, 95, 95);
        private Color TrackColor => _isDark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(198, 198, 203);

        public MiniPlayerBar()
        {
            Dock = DockStyle.Top;
            Height = 48;
            BackColor = BarColor;
            Visible = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => OnTick();
            AudioPlayer.StateChanged += OnAudioState;
        }

        private void OnAudioState()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)Invalidate); } catch { }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_timer == null) return;
            if (Visible) { _timer.Start(); Invalidate(); } else _timer.Stop();
        }

        private void OnTick()
        {
            if (AudioPlayer.IsActive && AudioPlayer.State == VLCState.Ended)
            {
                if (AudioPlayer.HasNext) AudioPlayer.Next(); else AudioPlayer.Stop();
                return;
            }
            Invalidate();
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

            // reserve the message-list scrollbar column so controls don't sit over it
            int rightPad = SystemInformation.VerticalScrollBarWidth + 10;
            _closeR = new Rectangle(Width - rightPad - 24, cy - 12, 24, 24);
            _muteR = new Rectangle(_closeR.X - 34, cy - 12, 28, 24);
            _speedR = new Rectangle(_muteR.X - 46, cy - 12, 42, 24);
            int timeRight = _speedR.X - 8, timeW = 92;
            var timeR = new Rectangle(timeRight - timeW, 0, timeW, h);

            int cx = _nextR.Right + 14;
            int cw = Math.Max(40, (timeR.X - 8) - cx);
            var titleR = new Rectangle(cx, 5, cw, 16);
            _seekR = new Rectangle(cx, 27, cw, 12);

            // transport circles
            DrawCircle(g, _prevR); DrawPrev(g, _prevR);
            DrawCircle(g, _playR);
            if (AudioPlayer.IsPlaying) DrawPause(g, _playR); else DrawPlay(g, _playR);
            DrawCircle(g, _nextR); DrawNext(g, _nextR);

            // title
            using (var wb = new SolidBrush(TextColor))
            using (var nf = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(AudioPlayer.Title ?? "", nf, wb, titleR, sf);

            // seek
            int ty = _seekR.Y + (_seekR.Height - 4) / 2;
            using (var tb = new SolidBrush(TrackColor))
                g.FillRectangle(tb, _seekR.X, ty, _seekR.Width, 4);
            float pos = AudioPlayer.Position; if (pos < 0) pos = 0; else if (pos > 1) pos = 1;
            int fillW = (int)(pos * _seekR.Width);
            using (var fb = new SolidBrush(AccentColor))
                g.FillRectangle(fb, _seekR.X, ty, fillW, 4);
            using (var hb = new SolidBrush(AccentColor))
                g.FillEllipse(hb, _seekR.X + fillW - 5, _seekR.Y + _seekR.Height / 2 - 5, 10, 10);

            // time, speed, mute, close
            using (var gb = new SolidBrush(SubTextColor))
            using (var f = new Font("Segoe UI", 8f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Fmt(AudioPlayer.TimeMs) + " / " + Fmt(AudioPlayer.DurationMs), f, gb, timeR, sf);
                g.DrawString(AudioPlayer.Rate.ToString("0.#") + "×", f, gb, _speedR, sf);
            }
            DrawMute(g, _muteR, AudioPlayer.Mute, TextColor);
            DrawClose(g, _closeR, SubTextColor);
        }

        private void DrawCircle(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(AccentColor)) g.FillEllipse(b, r);
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

        private static void DrawClose(Graphics g, Rectangle r, Color color)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var p = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            { g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6); g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_prevR.Contains(e.Location)) { AudioPlayer.Previous(); return; }
            if (_playR.Contains(e.Location)) { AudioPlayer.TogglePause(); return; }
            if (_nextR.Contains(e.Location)) { AudioPlayer.Next(); return; }
            if (_speedR.Contains(e.Location)) { _speedIdx = (_speedIdx + 1) % Speeds.Length; AudioPlayer.SetRate(Speeds[_speedIdx]); return; }
            if (_muteR.Contains(e.Location)) { AudioPlayer.Mute = !AudioPlayer.Mute; return; }
            if (_closeR.Contains(e.Location)) { AudioPlayer.Stop(); return; }
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
            if (_seekR.Width <= 0) return;
            double ratio = (x - _seekR.X) / (double)_seekR.Width;
            if (ratio < 0) ratio = 0; else if (ratio > 1) ratio = 1;
            AudioPlayer.Seek(ratio);
            Invalidate();
        }

        private static string Fmt(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                AudioPlayer.StateChanged -= OnAudioState;
                _timer?.Stop();
                _timer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
