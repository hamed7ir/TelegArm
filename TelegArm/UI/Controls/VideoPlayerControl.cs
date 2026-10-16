using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Core.Media;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// The embedded video player (2026-10-16: FFmpeg through the tgav shim; was VlcVideoControl over libVLC, with the same
    /// public API so MediaViewerForm, StoryViewerForm and VideoControlBar are unchanged in shape). The picture is an
    /// owner-painted surface: each frame arrives at the surface's fitted size and is blitted with GDI StretchDIBits straight
    /// from the frame buffer (no GDI+ bitmap per frame, the cheap path on a Tegra). There is no native child window any
    /// more, so the host's control bar and the round-video Region simply work.
    /// </summary>
    public class VideoPlayerControl : UserControl, IDpiAware
    {
        private MediaPlayback _pb;
        private readonly FrameSurface _surface;
        private Label _loadingLabel, _statusLabel, _fallbackLabel;
        private Panel _fallbackPanel;
        private Button _openBtn;
        private int _dpi;
        private string _currentFile;
        private bool _invalidatePending;

        public bool IsReady { get { return Tgav.Available; } }
        public bool IsPlaying { get { return _pb != null && _pb.State == MediaState.Playing; } }
        public long Time { get { return _pb != null ? _pb.TimeMs : 0; } }
        public long Length { get { return _pb != null ? _pb.LengthMs : 0; } }
        public MediaState State { get { return _pb != null ? _pb.State : MediaState.NothingSpecial; } }
        /// <summary>STORIES-BUILD-3: true once playback reached the end (so a host can advance).</summary>
        public bool HasEnded { get { return _pb != null && _pb.State == MediaState.Ended; } }

        public float Position
        {
            get { return _pb != null ? _pb.Position : 0f; }
            set { if (_pb != null) _pb.Position = value; }
        }

        public int Volume { set { if (_pb != null) _pb.Volume = value; } }

        public VideoPlayerControl()
        {
            BackColor = ThemeHelper.Palette.PlayerBg;
            _surface = new FrameSurface(this) { Dock = DockStyle.Fill, Visible = false };
            _surface.Resize += (s, e) => PushTarget();
            BuildUi();
        }

        private void BuildUi()
        {
            _loadingLabel = new Label
            {
                Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter, Text = "Loading…", Visible = false
            };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill, BackColor = ThemeHelper.Palette.PlayerBg, ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter, Visible = false
            };
            _fallbackPanel = new Panel { Dock = DockStyle.Fill, BackColor = ThemeHelper.Palette.PlayerBg, Visible = false };
            _fallbackLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            _openBtn = new Button { Text = "Open with system player", Dock = DockStyle.Bottom, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            _openBtn.FlatAppearance.BorderColor = Color.Gray;
            _openBtn.Click += (s, e) => OpenExternally();
            _fallbackPanel.Controls.Add(_fallbackLabel);
            _fallbackPanel.Controls.Add(_openBtn);

            Controls.Add(_surface);
            Controls.Add(_loadingLabel);
            Controls.Add(_fallbackPanel);
            Controls.Add(_statusLabel);
            ApplyDpi(Dpi.Of(this));
        }

        // ── DPI (step 3.3: SET from logical values, never multiplied) ─────────────────────────────────────────────────
        private void ApplyDpi(int dpi)
        {
            _dpi = dpi;
            Font = FontCache.Rescale(Control.DefaultFont, Dpi.Logical, dpi);   // shared: never dispose
            if (_openBtn != null) _openBtn.Height = Dpi.Px(44, dpi);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null || Disposing || IsDisposed) return;
            int d = Dpi.Of(this);
            if (d != _dpi) ApplyDpi(d);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int d = Dpi.Of(this);
            if (d != _dpi) ApplyDpi(d);
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { if (newDpi > 0) ApplyDpi(newDpi); }

        // ── playback ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Shows a centered status message (downloading / error).</summary>
        public void ShowStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
            _statusLabel.BringToFront();
            _surface.Visible = false;
            _loadingLabel.Visible = false;
            _fallbackPanel.Visible = false;
        }

        /// <summary>Plays a local file. <paramref name="loop"/> repeats it (GIFs). <paramref name="ensureAudio"/> is kept for
        /// the callers: the VLC-era round-video fix (audio left off); FFmpeg always plays the audio stream it finds.</summary>
        public void PlayFile(string path, bool loop = false, bool ensureAudio = false)
        {
            _currentFile = path;
            DisposePlayback();
            if (!IsReady) { ShowFallback("The media engine is not available: " + Tgav.Status); return; }
            _statusLabel.Visible = false;
            _fallbackPanel.Visible = false;
            _surface.Visible = true;
            _surface.BringToFront();
            _loadingLabel.Visible = true;
            _loadingLabel.BringToFront();
            _pb = new MediaPlayback(path, loop, video: true, audio: true);
            _pb.FrameReady += OnFrameReady;
            _pb.StateChanged += OnPlaybackState;
            PushTarget();
        }

        public void TogglePlay() { if (_pb != null) _pb.TogglePause(); }

        /// <summary>Toggles mute and returns the new mute state.</summary>
        public bool ToggleMute()
        {
            if (_pb == null) return false;
            _pb.Mute = !_pb.Mute;
            return _pb.Mute;
        }

        public void StopPlayback()
        {
            DisposePlayback();
            if (_loadingLabel != null) _loadingLabel.Visible = false;
            _surface.Invalidate();
        }

        /// <summary>The Loading overlay follows the player (called by the host's timer).</summary>
        public void RefreshOverlay()
        {
            if (_pb == null) return;
            bool loading = _pb.State == MediaState.Opening || _pb.State == MediaState.Buffering;
            if (_loadingLabel.Visible != loading) { _loadingLabel.Visible = loading; if (loading) _loadingLabel.BringToFront(); }
            if (_pb.State == MediaState.Error && !_fallbackPanel.Visible) ShowFallback("This video can't be played here.\n\n" + _pb.Error);
        }

        private void OnPlaybackState()
        {
            var pb = _pb;
            if (pb == null) return;
            if (pb.State == MediaState.Playing || pb.State == MediaState.Error)
                try { if (IsHandleCreated) BeginInvoke((Action)(() => { if (ReferenceEquals(pb, _pb)) { PushTarget(); RefreshOverlay(); } })); } catch { }
        }

        /// <summary>Video thread → UI: one pending repaint at most (latest frame wins; the ARM32 UI thread never queues 30 a second).</summary>
        private void OnFrameReady()
        {
            if (_invalidatePending) return;
            _invalidatePending = true;
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() =>
                    {
                        _invalidatePending = false;
                        if (_loadingLabel.Visible) _loadingLabel.Visible = false;
                        _surface.Invalidate();
                    }));
                else _invalidatePending = false;
            }
            catch { _invalidatePending = false; }
        }

        /// <summary>Frames come at the surface's fitted video size (aspect kept).</summary>
        private void PushTarget()
        {
            if (_pb == null || _pb.Width <= 0 || _pb.Height <= 0) return;
            var r = Fit(_pb.Width, _pb.Height, _surface.ClientSize);
            _pb.SetVideoTarget(r.Width, r.Height);
        }

        internal static Rectangle Fit(int vw, int vh, Size box)
        {
            if (vw <= 0 || vh <= 0 || box.Width <= 0 || box.Height <= 0) return Rectangle.Empty;
            double s = Math.Min((double)box.Width / vw, (double)box.Height / vh);
            int w = Math.Max(2, (int)(vw * s)), h = Math.Max(2, (int)(vh * s));
            return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
        }

        private void ShowFallback(string why)
        {
            _fallbackLabel.Text = why;
            _fallbackPanel.Visible = true;
            _fallbackPanel.BringToFront();
            _statusLabel.Visible = false;
            _loadingLabel.Visible = false;
            _surface.Visible = false;
        }

        private void OpenExternally()
        {
            try { if (!string.IsNullOrEmpty(_currentFile)) Process.Start(_currentFile); }
            catch { /* no association / blocked */ }
        }

        private void DisposePlayback()
        {
            var pb = _pb;
            _pb = null;
            if (pb == null) return;
            pb.FrameReady -= OnFrameReady;
            pb.StateChanged -= OnPlaybackState;
            pb.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposePlayback();
            base.Dispose(disposing);
        }

        internal MediaPlayback Playback { get { return _pb; } }

        /// <summary>Paints the newest frame, centered, black around it (GDI: the frame buffer goes to the DC as a DIB).</summary>
        private sealed class FrameSurface : Control
        {
            private readonly VideoPlayerControl _owner;

            public FrameSurface(VideoPlayerControl owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
                BackColor = Color.Black;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pb = _owner._pb;
                var store = pb != null ? pb.Frames : null;
                Rectangle dest = Rectangle.Empty;
                if (store != null)
                    lock (store.Sync)
                    {
                        if (store.Front != null && store.FrontWidth > 0)
                        {
                            dest = Fit(pb.Width > 0 ? pb.Width : store.FrontWidth, pb.Height > 0 ? pb.Height : store.FrontHeight, ClientSize);
                            ClearAround(e.Graphics, dest);
                            Blit(e.Graphics, store.Front, store.FrontWidth, store.FrontHeight, dest);
                            return;
                        }
                    }
                e.Graphics.Clear(Color.Black);
            }

            private void ClearAround(Graphics g, Rectangle r)
            {
                using (var b = new SolidBrush(Color.Black))
                {
                    if (r.Top > 0) g.FillRectangle(b, 0, 0, Width, r.Top);
                    if (r.Bottom < Height) g.FillRectangle(b, 0, r.Bottom, Width, Height - r.Bottom);
                    if (r.Left > 0) g.FillRectangle(b, 0, r.Top, r.Left, r.Height);
                    if (r.Right < Width) g.FillRectangle(b, r.Right, r.Top, Width - r.Right, r.Height);
                }
            }

            private static void Blit(Graphics g, byte[] px, int w, int h, Rectangle dest)
            {
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h,   // negative: top-down
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                };
                IntPtr hdc = g.GetHdc();
                try
                {
                    SetStretchBltMode(hdc, COLORONCOLOR);
                    StretchDIBits(hdc, dest.X, dest.Y, dest.Width, dest.Height, 0, 0, w, h, px, ref bmi, 0, SRCCOPY);
                }
                finally { g.ReleaseHdc(hdc); }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public int biSize, biWidth, biHeight;
                public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            }

            private const int SRCCOPY = 0x00CC0020, COLORONCOLOR = 3;
            [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
            [DllImport("gdi32.dll")]
            private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc,
                                                    byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, int rop);
        }
    }
}
