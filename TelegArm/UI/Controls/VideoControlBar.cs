using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Owner-painted video transport bar, styled to match <see cref="MiniPlayerBar"/>
    /// (accent circles for prev / play-pause / next, owner-drawn seek + time, mute).
    /// Lives in a TableLayoutPanel row BELOW the VLC surface. Like MiniPlayerBar it
    /// paints the WHOLE control itself (no child controls) and hit-tests by region —
    /// the one owner-painted style that renders in a toolbar row. A periodic
    /// Invalidate() (driven by the host's timer) keeps it visible over VLC repaints.
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): lengths are LOGICAL (96-DPI) px drawn through Px at the
    /// bar's DPI; the time font is a shared FontCache font (never disposed — this paints every 200 ms); the glyphs
    /// scale from their rectangles. Identity at 96. The height is the host's (MediaViewerForm's Px(BarHeight) row).
    /// </summary>
    public class VideoControlBar : Control, IDpiAware
    {
        private readonly VideoPlayerControl _video;

        private int _dpi;                                          // the DPI this bar is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

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

        private Color BarColor => ThemeHelper.PaletteFor(_isDark).ControlBarBg;
        private Color TextColor => ThemeHelper.PaletteFor(_isDark).ControlBarText;
        private Color SubTextColor => ThemeHelper.PaletteFor(_isDark).ControlBarSubText;
        private Color TrackColor => ThemeHelper.PaletteFor(_isDark).ControlBarTrack;
        private Color DisabledColor => ThemeHelper.PaletteFor(_isDark).ControlBarDisabled;

        public VideoControlBar(VideoPlayerControl video)
        {
            TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
            _video = video;
            Cursor = Cursors.Hand;
            BackColor = BarColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
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

            int h = Height, cd = P(30), cy = h / 2, gap = P(6);
            _prevR = new Rectangle(P(10), cy - cd / 2, cd, cd);
            _playR = new Rectangle(P(10) + cd + gap, cy - cd / 2, cd, cd);
            _nextR = new Rectangle(P(10) + 2 * (cd + gap), cy - cd / 2, cd, cd);

            int rightPad = P(12);
            _muteR = new Rectangle(Width - rightPad - P(28), cy - P(12), P(28), P(24));
            int totW = P(66); var totR = new Rectangle(_muteR.X - P(8) - totW, 0, totW, h);
            int curW = P(66); var curR = new Rectangle(_nextR.Right + P(10), 0, curW, h);
            int seekX = curR.Right + P(6);
            _seekR = new Rectangle(seekX, cy - P(6), Math.Max(P(40), (totR.X - P(6)) - seekX), P(12));

            // transport circles (dimmed when navigation isn't available)
            DrawCircle(g, _prevR, CanPrev); DrawPrev(g, _prevR, CanPrev);
            DrawCircle(g, _playR, true);
            if (_video != null && _video.IsPlaying) DrawPause(g, _playR); else DrawPlay(g, _playR);
            DrawCircle(g, _nextR, CanNext); DrawNext(g, _nextR, CanNext);

            long timeMs = _video != null ? _video.Time : 0;
            long lenMs = _video != null ? _video.Length : 0;

            // times
            var f = FontCache.Get("Segoe UI", 8.5f, FontStyle.Regular, dpi);   // shared: never dispose
            using (var gb = new SolidBrush(SubTextColor))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Fmt(timeMs), f, gb, curR, sf);
                g.DrawString(Fmt(lenMs), f, gb, totR, sf);
            }

            // seek
            int th = P(4), ty = _seekR.Y + (_seekR.Height - th) / 2;
            using (var tb = new SolidBrush(TrackColor))
                g.FillRectangle(tb, _seekR.X, ty, _seekR.Width, th);
            float pos = _video != null ? _video.Position : 0f;
            if (pos < 0) pos = 0; else if (pos > 1) pos = 1;
            int fillW = (int)(pos * _seekR.Width);
            using (var fb = new SolidBrush(AccentColor))
                g.FillRectangle(fb, _seekR.X, ty, fillW, th);
            using (var hb = new SolidBrush(AccentColor))
                g.FillEllipse(hb, _seekR.X + fillW - P(5), _seekR.Y + _seekR.Height / 2 - P(5), P(10), P(10));

            DrawMute(g, _muteR, _muted, TextColor);
        }

        private void DrawCircle(Graphics g, Rectangle r, bool enabled)
        {
            using (var b = new SolidBrush(enabled ? AccentColor : DisabledColor))
                g.FillEllipse(b, r);
        }

        // The transport glyphs are drawn for the 30 px circle; they scale with it (k == 1 at 96).
        private void DrawPlay(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(ColorMath.OnColor(AccentColor)))
                g.FillPolygon(w, new[] { new PointF(cx - 5 * k, cy - 7 * k), new PointF(cx + 7 * k, cy), new PointF(cx - 5 * k, cy + 7 * k) });
        }

        private void DrawPause(Graphics g, Rectangle r)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(ColorMath.OnColor(AccentColor)))
            { g.FillRectangle(w, cx - 5 * k, cy - 7 * k, 3.5f * k, 14 * k); g.FillRectangle(w, cx + 1.5f * k, cy - 7 * k, 3.5f * k, 14 * k); }
        }

        private void DrawPrev(Graphics g, Rectangle r, bool enabled)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(ColorMath.OnColor(enabled ? AccentColor : DisabledColor)))
            {
                g.FillRectangle(w, cx - 8 * k, cy - 6 * k, 2.5f * k, 12 * k);
                g.FillPolygon(w, new[] { new PointF(cx + 6 * k, cy - 6 * k), new PointF(cx - 3 * k, cy), new PointF(cx + 6 * k, cy + 6 * k) });
            }
        }

        private void DrawNext(Graphics g, Rectangle r, bool enabled)
        {
            float k = r.Width / 30f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var w = new SolidBrush(ColorMath.OnColor(enabled ? AccentColor : DisabledColor)))
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
