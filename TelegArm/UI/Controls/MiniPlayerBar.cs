using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Persistent mini audio player bar, fully owner-painted (GDI+) like the message
    /// bubbles — the only style that renders reliably in this app. Prev / play-pause /
    /// next as accent circles, title, draggable seek, elapsed/total, speed, mute, close.
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI; fonts are shared FontCache fonts (never disposed — this paints every 200 ms); the glyphs scale from
    /// their rectangles. Identity at 96, except the right-edge reserve: it is now the themed message-list scrollbar's
    /// lane (ThemedScrollBar.LaneWidth, 12 px at 96) instead of the native bar's width (17) it never sat beside.
    /// </summary>
    public class MiniPlayerBar : UserControl, IDpiAware
    {
        private const int BarH = 48;   // logical px
        private static readonly float[] Speeds = { 1.0f, 1.5f, 2.0f };

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

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

        private Color BarColor => ThemeHelper.PaletteFor(_isDark).PlayerBarBg;
        private Color TextColor => ThemeHelper.PaletteFor(_isDark).PlayerBarText;
        private Color SubTextColor => ThemeHelper.PaletteFor(_isDark).PlayerBarSub;
        private Color TrackColor => ThemeHelper.PaletteFor(_isDark).PlayerBarTrack;

        public MiniPlayerBar()
        {
            Dock = DockStyle.Top;
            Height = Dpi.Px(BarH, CurDpi);
            BackColor = BarColor;
            Visible = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => OnTick();
            AudioPlayer.StateChanged += OnAudioState;
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Height = Dpi.Px(BarH, d); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Height = Dpi.Px(BarH, newDpi);   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BarColor);

            int h = Height, cd = P(30), cy = h / 2, gap = P(6);
            _prevR = new Rectangle(P(10), cy - cd / 2, cd, cd);
            _playR = new Rectangle(P(10) + cd + gap, cy - cd / 2, cd, cd);
            _nextR = new Rectangle(P(10) + 2 * (cd + gap), cy - cd / 2, cd, cd);

            // reserve the message-list scrollbar column so controls don't sit over it — step 3.2: that bar is the themed
            // one (its lane at this DPI), not the native bar SystemInformation measures (17 px at 96; the lane is 12)
            int rightPad = ThemedScrollBar.LaneWidth(dpi) + P(10);
            _closeR = new Rectangle(Width - rightPad - P(24), cy - P(12), P(24), P(24));
            _muteR = new Rectangle(_closeR.X - P(34), cy - P(12), P(28), P(24));
            _speedR = new Rectangle(_muteR.X - P(46), cy - P(12), P(42), P(24));
            int timeRight = _speedR.X - P(8), timeW = P(92);
            var timeR = new Rectangle(timeRight - timeW, 0, timeW, h);

            int cx = _nextR.Right + P(14);
            int cw = Math.Max(P(40), (timeR.X - P(8)) - cx);
            var titleR = new Rectangle(cx, P(5), cw, P(16));
            _seekR = new Rectangle(cx, P(27), cw, P(12));

            // transport circles
            DrawCircle(g, _prevR); DrawPrev(g, _prevR);
            DrawCircle(g, _playR);
            if (AudioPlayer.IsPlaying) DrawPause(g, _playR); else DrawPlay(g, _playR);
            DrawCircle(g, _nextR); DrawNext(g, _nextR);

            // title (shared font: never dispose)
            using (var wb = new SolidBrush(TextColor))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(AudioPlayer.Title ?? "", FontCache.Get("Segoe UI", 8.5f, FontStyle.Bold, dpi), wb, titleR, sf);

            // seek
            int th = P(4), ty = _seekR.Y + (_seekR.Height - th) / 2;
            using (var tb = new SolidBrush(TrackColor))
                g.FillRectangle(tb, _seekR.X, ty, _seekR.Width, th);
            float pos = AudioPlayer.Position; if (pos < 0) pos = 0; else if (pos > 1) pos = 1;
            int fillW = (int)(pos * _seekR.Width);
            using (var fb = new SolidBrush(AccentColor))
                g.FillRectangle(fb, _seekR.X, ty, fillW, th);
            using (var hb = new SolidBrush(AccentColor))
                g.FillEllipse(hb, _seekR.X + fillW - P(5), _seekR.Y + _seekR.Height / 2 - P(5), P(10), P(10));

            // time, speed, mute, close
            var f = FontCache.Get("Segoe UI", 8f, FontStyle.Regular, dpi);   // shared: never dispose
            using (var gb = new SolidBrush(SubTextColor))
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

        // The transport glyphs are drawn for the 30 px circle; they scale with it (k == 1 at 96).
        private static void DrawPlay(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
                g.FillPolygon(w, new[] { new PointF(cx - 5 * k, cy - 7 * k), new PointF(cx + 7 * k, cy), new PointF(cx - 5 * k, cy + 7 * k) });
        }

        private static void DrawPause(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            { g.FillRectangle(w, cx - 5 * k, cy - 7 * k, 3.5f * k, 14 * k); g.FillRectangle(w, cx + 1.5f * k, cy - 7 * k, 3.5f * k, 14 * k); }
        }

        private static void DrawPrev(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, cx - 8 * k, cy - 6 * k, 2.5f * k, 12 * k);
                g.FillPolygon(w, new[] { new PointF(cx + 6 * k, cy - 6 * k), new PointF(cx - 3 * k, cy), new PointF(cx + 6 * k, cy + 6 * k) });
            }
        }

        private static void DrawNext(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                g.FillPolygon(w, new[] { new PointF(cx - 6 * k, cy - 6 * k), new PointF(cx + 3 * k, cy), new PointF(cx - 6 * k, cy + 6 * k) });
                g.FillRectangle(w, cx + 5.5f * k, cy - 6 * k, 2.5f * k, 12 * k);
            }
        }

        /// <summary>Drawn for the 28 px-wide mute box; scales with it.</summary>
        private static void DrawMute(Graphics g, Rectangle r, bool muted, Color color)
        {
            float k = r.Width / 28f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(color))
            {
                g.FillRectangle(w, cx - 8 * k, cy - 3 * k, 4 * k, 6 * k);
                g.FillPolygon(w, new[] { new PointF(cx - 4 * k, cy - 5 * k), new PointF(cx + 1 * k, cy - 8 * k), new PointF(cx + 1 * k, cy + 8 * k), new PointF(cx - 4 * k, cy + 5 * k) });
                if (muted)
                    using (var rp = new Pen(Color.Red, 2f * k))
                    { g.DrawLine(rp, cx + 4 * k, cy - 4 * k, cx + 9 * k, cy + 4 * k); g.DrawLine(rp, cx + 9 * k, cy - 4 * k, cx + 4 * k, cy + 4 * k); }
                else
                    using (var p = new Pen(color, 1.6f * k))
                        g.DrawArc(p, cx + 2 * k, cy - 6 * k, 8 * k, 12 * k, -55, 110);
            }
        }

        /// <summary>Drawn for the 24 px close box; scales with it.</summary>
        private static void DrawClose(Graphics g, Rectangle r, Color color)
        {
            float k = r.Width / 24f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var p = new Pen(color, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            { g.DrawLine(p, cx - 6 * k, cy - 6 * k, cx + 6 * k, cy + 6 * k); g.DrawLine(p, cx + 6 * k, cy - 6 * k, cx - 6 * k, cy + 6 * k); }
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
