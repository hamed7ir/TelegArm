using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NAudio.Wave;
using TelegArm.Core.Calls;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// The call window (2026-10-16, Telegram calls via tgcalls): who, what is happening (requesting, ringing, connecting,
    /// the running time, reconnecting, how it ended) and the signal, with Accept / Decline while a call rings here, Mute /
    /// Hang up during it, Close at the end (it also closes by itself). Not modal and on top, like Telegram's; it follows
    /// CallManager.Changed. A ring for an incoming call and a ringback while an outgoing one rings out.
    /// Video: a Start / Stop video button; the other side's picture fills the window (Telegram Desktop's rule: fill when
    /// the picture is at least as tall as the window, else fit) with ours mirrored in a corner; until theirs arrives ours
    /// fills it; a round button switches cameras. The window grows the first time video is involved, and stays so.
    /// </summary>
    public sealed class CallWindow : DialogForm
    {
        private const int VoiceW = 360, VoiceH = 400, VideoW = 480, VideoH = 620;   // logical

        private readonly CallManager _calls;
        private CallSession _s, _listening;
        private readonly Func<long, Image> _avatar;
        private readonly Func<CallSession, string> _name;
        private readonly CallStage _stage;
        private readonly RoundedButton _accept, _decline, _camera, _mute, _hangup, _close;
        private readonly Timer _tick;
        private readonly Ringer _ringer = new Ringer();
        private int _endedTick, _framePending;
        private bool _videoSize;

        /// <summary>The screenshot rig: draw the switch-camera button without a camera behind it.</summary>
        internal static bool ShowSwitchForRig;

        public CallWindow(CallManager calls, CallSession s, Func<long, Image> avatar, Func<CallSession, string> name)
            : base("Call", VoiceW, VoiceH)
        {
            _calls = calls; _s = s; _avatar = avatar; _name = name;
            _stage = new CallStage(this) { Dock = DockStyle.Fill };
            UseOwnBody(_stage);
            // first added = rightmost
            _accept = AddFooterButton("Accept", RoundedButtonKind.Primary, DialogResult.None, 100);
            _hangup = AddFooterButton("Hang up", RoundedButtonKind.Danger, DialogResult.None, 100);
            _decline = AddFooterButton("Decline", RoundedButtonKind.Danger, DialogResult.None, 100);
            _camera = AddFooterButton("Start video", RoundedButtonKind.Secondary, DialogResult.None, 100);
            _mute = AddFooterButton("Mute", RoundedButtonKind.Secondary, DialogResult.None, 100);
            _close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.None, 100);
            _accept.Click += async (o, e) => { _ringer.Stop(); await _calls.AcceptAsync(); };
            _decline.Click += async (o, e) => { _ringer.Stop(); await _calls.HangUpAsync(); };
            _hangup.Click += async (o, e) => await _calls.HangUpAsync();
            _camera.Click += async (o, e) => { if (_s != null) await _calls.SetCameraAsync(!_s.CameraOn); };
            _mute.Click += (o, e) => { if (_s != null) _calls.SetMuted(!_s.Muted); };
            _close.Click += (o, e) => Close();
            _calls.Changed += OnChanged;
            _tick = new Timer { Interval = 500 };
            _tick.Tick += (o, e) => Tick();
            _tick.Start();
            TopMost = true;
            Listen();
            Apply();
        }

        /// <summary>Points the window at another call (a new one while this window is still showing the last).</summary>
        public void Bind(CallSession s) { _s = s; _endedTick = 0; Listen(); Apply(); }

        private void OnChanged(CallSession s) { if (ReferenceEquals(s, _s) && !IsDisposed) Apply(); }

        /// <summary>Repaints for new pictures: at most one repaint waiting, however fast they come (the Tegra's UI thread).</summary>
        private void Listen()
        {
            if (ReferenceEquals(_listening, _s)) return;
            if (_listening != null) { _listening.LocalVideo.FrameReady -= OnFrame; _listening.RemoteFrames.FrameReady -= OnFrame; }
            _listening = _s;
            if (_s != null) { _s.LocalVideo.FrameReady += OnFrame; _s.RemoteFrames.FrameReady += OnFrame; }
        }

        private void OnFrame()
        {
            if (System.Threading.Interlocked.Exchange(ref _framePending, 1) != 0) return;
            try { BeginInvoke((Action)(() => { _framePending = 0; if (!IsDisposed) _stage.Invalidate(); })); }
            catch { _framePending = 0; }   // not shown yet, or closing
        }

        private void Apply()
        {
            var s = _s;
            if (s == null) return;
            Text = s.Video ? "Video call" : "Call";
            bool incoming = s.Phase == CallPhase.Incoming, ended = s.Ended;
            _accept.Visible = _decline.Visible = incoming;
            _close.Visible = ended;
            _mute.Visible = _hangup.Visible = !incoming && !ended;
            _camera.Visible = !ended;
            _camera.Text = s.CameraBusy ? "Camera…" : s.CameraOn ? "Stop video" : "Start video";
            _camera.Enabled = !s.CameraBusy;
            _mute.Text = s.Muted ? "Unmute" : "Mute";
            if (incoming) _ringer.Ring(Ringer.Pattern.Incoming);
            else if (s.Outgoing && (s.Phase == CallPhase.Waiting || s.Phase == CallPhase.Ringing)) _ringer.Ring(Ringer.Pattern.Ringback);
            else _ringer.Stop();
            if (ended && _endedTick == 0) _endedTick = Environment.TickCount;
            if (!_videoSize && (s.Video || s.CameraOn || s.CameraBusy || s.RemoteVideo == 2)) GrowForVideo();
            _stage.Invalidate();
        }

        /// <summary>The video size, no larger than the screen's work area (the Surface RT's is 728 px tall: at 125-150 % the
        /// full size would put the buttons below it), kept on screen with the right edge where it was.</summary>
        private void GrowForVideo()
        {
            _videoSize = true;
            int right = Right, top = Top;
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            Size = new Size(Math.Min(Px(VideoW), wa.Width - Px(16)), Math.Min(Px(VideoH), wa.Height - Px(16)));
            if (!IsHandleCreated) return;
            Left = Math.Max(wa.Left, Math.Min(right - Width, wa.Right - Width));
            Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - Height));
        }

        private void Tick()
        {
            _stage.Invalidate();   // the running time
            if (_endedTick != 0 && unchecked(Environment.TickCount - _endedTick) > 3000) { _endedTick = 0; Close(); }
        }

        internal string StatusText()
        {
            var s = _s;
            if (s == null) return "";
            switch (s.Phase)
            {
                case CallPhase.Requesting: return s.CameraBusy ? "Starting the camera…" : "Requesting…";
                case CallPhase.Waiting: return "Waiting…";
                case CallPhase.Ringing: return "Ringing…";
                case CallPhase.Incoming: return s.Video ? "Incoming video call" : "Incoming call";
                case CallPhase.Accepting: return "Answering…";
                case CallPhase.Connecting: return "Connecting…";
                case CallPhase.Reconnecting: return "Reconnecting…";
                case CallPhase.Established:
                    int d = s.DurationSeconds;
                    return (d >= 3600 ? (d / 3600) + ":" + (d / 60 % 60).ToString("00") : (d / 60).ToString()) + ":" + (d % 60).ToString("00")
                           + (s.RemoteMuted ? "  ·  their microphone is off" : "");
                default: return s.EndText ?? "Call ended";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the window during a call hangs it up (Telegram Desktop's window does the same)
            if (_s != null && !_s.Ended && e.CloseReason == CloseReason.UserClosing) { var t = _calls.HangUpAsync(); }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _calls.Changed -= OnChanged;
                if (_listening != null) { _listening.LocalVideo.FrameReady -= OnFrame; _listening.RemoteFrames.FrameReady -= OnFrame; _listening = null; }
                _tick.Dispose();
                _ringer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Avatar, name, status, signal bars - or the video with the name and status over it.</summary>
        private sealed class CallStage : Control
        {
            private readonly CallWindow _w;
            private Rectangle _switchRect;

            public CallStage(CallWindow w)
            {
                TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
                _w = w;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Default;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                Cursor = _switchRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            }

            protected override async void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button == MouseButtons.Left && _switchRect.Contains(e.Location)) await _w._calls.SwitchCameraAsync();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = ThemeHelper.Palette;
                int dpi = _w.WindowDpiValue;
                Func<int, int> Px = v => Dpi.Px(v, dpi);   // the window's DPI (the check script reads Px( as scaled)
                _switchRect = Rectangle.Empty;
                var s = _w._s;
                if (s == null) { g.Clear(p.Window); return; }
                var all = ClientRectangle;
                // the big picture: theirs, else ours (until theirs comes)
                bool theirs = s.RemoteVideo == 2 && DrawPicture(g, s.RemoteFrames.Store, all, false);
                bool ours = !theirs && s.CameraOn && DrawPicture(g, s.LocalVideo.Store, all, false);
                if (theirs || ours)
                {
                    DrawOverlay(g, s, Px, dpi);
                    if (theirs && s.CameraOn)   // ours in the bottom-right corner, as Telegram Desktop's bubble
                    {
                        var st = s.LocalVideo.Store;
                        int fw, fh;
                        lock (st.Sync) { fw = st.FrontWidth; fh = st.FrontHeight; }
                        if (fw > 0 && fh > 0)
                        {
                            int bw = fw >= fh ? Px(132) : Px(96), bh = Math.Max(Px(40), bw * fh / fw);
                            var pip = new Rectangle(Width - bw - Px(12), Height - bh - Px(12), bw, bh);
                            DrawPicture(g, st, pip, true);
                            using (var pen = new Pen(ColorMath.WithAlpha(Color.White, 160), Math.Max(1, Px(1)))) g.DrawRectangle(pen, pip);
                        }
                    }
                }
                else DrawCard(g, s, p, Px, dpi);

                if (s.CameraOn && !s.Ended && (s.CanSwitchCamera || ShowSwitchForRig))   // the round "switch camera" button, bottom-left
                {
                    int d = Px(40);
                    _switchRect = new Rectangle(Px(12), Height - d - Px(12), d, d);
                    DrawSwitchButton(g, _switchRect, s.CameraBusy, Px);
                }
                if (!string.IsNullOrEmpty(s.CameraNote) && !s.Ended)
                {
                    var nr = new Rectangle(Px(12), Height - Px(12) - Px(40) - Px(26), Width - Px(24), Px(24));
                    var fg = theirs || ours ? Color.White : p.TextDim;
                    TextRenderer.DrawText(g, s.CameraNote, FontHelper.UiAt(9.5f, dpi, FontStyle.Regular), nr, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }

            /// <summary>Draws the newest picture of <paramref name="st"/> in <paramref name="area"/> (the whole stage: cover or
            /// fit, black around it; a corner: stretched to it). False when there is no picture yet.</summary>
            private static bool DrawPicture(Graphics g, TelegArm.Core.Media.VideoFrameStore st, Rectangle area, bool exact)
            {
                lock (st.Sync)
                {
                    if (st.Front == null || st.FrontWidth <= 0 || st.FrontHeight <= 0) return false;
                    if (exact) { DibBlit.Draw(g, st.Front, st.FrontWidth, st.FrontHeight, area); return true; }
                    var r = DibBlit.CoverOrFit(st.FrontWidth, st.FrontHeight, area);
                    using (var b = new SolidBrush(Color.Black))
                    {   // only around the picture: no black flash under it
                        if (r.Top > area.Top) g.FillRectangle(b, area.Left, area.Top, area.Width, r.Top - area.Top);
                        if (r.Bottom < area.Bottom) g.FillRectangle(b, area.Left, r.Bottom, area.Width, area.Bottom - r.Bottom);
                        if (r.Left > area.Left) g.FillRectangle(b, area.Left, area.Top, r.Left - area.Left, area.Height);
                        if (r.Right < area.Right) g.FillRectangle(b, r.Right, area.Top, area.Right - r.Right, area.Height);
                    }
                    DibBlit.Draw(g, st.Front, st.FrontWidth, st.FrontHeight, r, area);
                    return true;
                }
            }

            /// <summary>Name, status and bars in white over a shade at the top of the picture.</summary>
            private void DrawOverlay(Graphics g, CallSession s, Func<int, int> Px, int dpi)
            {
                var band = new Rectangle(0, 0, Width, Px(96));
                using (var shade = new LinearGradientBrush(band, ColorMath.WithAlpha(Color.Black, 150), ColorMath.WithAlpha(Color.Black, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(shade, band);
                string name = _w._name(s);
                var nameRect = new Rectangle(Px(12), Px(12), Width - Px(24), Px(28));
                TextRenderer.DrawText(g, name, FontHelper.ForAt(name, 14f, dpi, FontStyle.Bold), nameRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                var stRect = new Rectangle(Px(12), nameRect.Bottom, Width - Px(24), Px(22));
                TextRenderer.DrawText(g, _w.StatusText(), FontHelper.UiAt(10f, dpi, FontStyle.Regular), stRect, ColorMath.WithAlpha(Color.White, 225),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (s.Phase == CallPhase.Established || s.Phase == CallPhase.Reconnecting)
                    DrawBars(g, new Rectangle(Width - Px(12) - Px(24), Px(14), Px(24), Px(14)), s.SignalBars, Color.White, ColorMath.WithAlpha(Color.White, 90));
            }

            private void DrawCard(Graphics g, CallSession s, Palette p, Func<int, int> Px, int dpi)
            {
                g.Clear(p.Window);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                string name = _w._name(s);
                int d = Px(112);
                var ar = new Rectangle((Width - d) / 2, Px(28), d, d);
                if (Height > Px(400)) ar.Y = (Height - Px(260)) / 2;   // the tall video window: the card in the middle
                Image av = _w._avatar != null ? _w._avatar(s.PeerId) : null;
                if (av != null) DrawHelper.DrawCircularImage(g, ar, av);
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(s.PeerId))) g.FillEllipse(b, ar);
                    string ini = DrawHelper.Initial(name);
                    DrawHelper.DrawInitial(g, ini, FontHelper.ForAt(ini, 34f, dpi, FontStyle.Bold), ar, Color.White);
                }
                if (s.Phase == CallPhase.Incoming || s.Phase == CallPhase.Ringing)
                    using (var pen = new Pen(ColorMath.WithAlpha(p.Accent, 90), Px(3)))   // a ring around the avatar while it rings
                        g.DrawEllipse(pen, Rectangle.Inflate(ar, Px(6), Px(6)));
                var nameRect = new Rectangle(Px(12), ar.Bottom + Px(18), Width - Px(24), Px(30));
                TextRenderer.DrawText(g, name, FontHelper.ForAt(name, 15f, dpi, FontStyle.Bold), nameRect, p.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                string st = _w.StatusText();
                var stRect = new Rectangle(Px(12), nameRect.Bottom + Px(4), Width - Px(24), Px(24));
                TextRenderer.DrawText(g, st, FontHelper.UiAt(10.5f, dpi, FontStyle.Regular), stRect, p.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (s.Phase == CallPhase.Established || s.Phase == CallPhase.Reconnecting)
                    DrawBars(g, new Rectangle((Width - Px(28)) / 2, stRect.Bottom + Px(10), Px(28), Px(16)), s.SignalBars, p.Accent, p.Divider);
            }

            /// <summary>A dark round button with two curved arrows (front / back camera).</summary>
            private static void DrawSwitchButton(Graphics g, Rectangle r, bool busy, Func<int, int> Px)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(ColorMath.WithAlpha(Color.Black, busy ? 90 : 150))) g.FillEllipse(b, r);
                var c = Rectangle.Inflate(r, -r.Width * 3 / 10, -r.Height * 3 / 10);
                using (var pen = new Pen(Color.White, Math.Max(1.5f, Px(2))))
                {
                    g.DrawArc(pen, c, 200, 130);
                    g.DrawArc(pen, c, 20, 130);
                }
                float rad = c.Width / 2f, cx = c.X + rad, cy = c.Y + rad, a = Math.Max(3f, Px(4));
                foreach (double deg in new[] { 330.0, 150.0 })   // arrowheads at the arcs' ends, pointing along them
                {
                    double t = deg * Math.PI / 180;
                    float x = cx + (float)(rad * Math.Cos(t)), y = cy + (float)(rad * Math.Sin(t));
                    float tx = (float)Math.Cos(t + Math.PI / 2), ty = (float)Math.Sin(t + Math.PI / 2);   // the tangent
                    float nx = (float)Math.Cos(t), ny = (float)Math.Sin(t);
                    using (var b = new SolidBrush(Color.White))
                        g.FillPolygon(b, new[] { new PointF(x + tx * a, y + ty * a), new PointF(x + nx * a * 0.8f, y + ny * a * 0.8f), new PointF(x - nx * a * 0.8f, y - ny * a * 0.8f) });
                }
            }

            private static void DrawBars(Graphics g, Rectangle r, int bars, Color on, Color off)
            {
                int n = 4, gap = Math.Max(1, r.Width / 14), w = (r.Width - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                {
                    int h = r.Height * (i + 1) / n;
                    using (var b = new SolidBrush(i < bars ? on : off))
                        g.FillRectangle(b, r.X + i * (w + gap), r.Bottom - h, w, h);
                }
            }
        }
    }

    /// <summary>Call tones from NAudio (no audio files): a two-tone ring for an incoming call, a ringback while an outgoing
    /// call rings at the other end.</summary>
    internal sealed class Ringer : IDisposable
    {
        public enum Pattern { None, Incoming, Ringback }

        private WaveOutEvent _out;
        private Pattern _now = Pattern.None;

        /// <summary>The screenshot rig: no sound while it draws the call window.</summary>
        internal static bool Silent;

        public void Ring(Pattern p)
        {
            if (Silent) p = Pattern.None;
            if (p == _now) return;
            Stop();
            if (p == Pattern.None) return;
            try
            {
                var tone = p == Pattern.Incoming
                    ? new ToneProvider(new[] { 400, 200, 400, 1800 }, new[] { new[] { 523.0, 659.0 }, null, new[] { 523.0, 659.0 }, null })
                    : new ToneProvider(new[] { 1000, 3000 }, new[] { new[] { 425.0 }, null });
                _out = new WaveOutEvent { DesiredLatency = 150 };
                _out.Init(tone);
                _out.Play();
                _now = p;
            }
            catch (Exception ex) { Logger.Diag("[CALL] ringer: " + ex.Message); Stop(); }
        }

        public void Stop()
        {
            _now = Pattern.None;
            var o = _out;
            _out = null;
            if (o != null) try { o.Stop(); o.Dispose(); } catch { }
        }

        public void Dispose() { Stop(); }

        /// <summary>A repeating on/off cadence of sine mixes (mono, 48 kHz, 16-bit).</summary>
        private sealed class ToneProvider : IWaveProvider
        {
            private readonly int[] _ms;
            private readonly double[][] _hz;
            private long _sample;
            private readonly long _period;
            public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 1);

            public ToneProvider(int[] ms, double[][] hz)
            {
                _ms = ms; _hz = hz;
                foreach (int m in ms) _period += m * 48L;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int n = count / 2;
                for (int i = 0; i < n; i++, _sample++)
                {
                    long t = _sample % _period, acc = 0;
                    int seg = 0;
                    while (seg < _ms.Length && t >= acc + _ms[seg] * 48L) { acc += _ms[seg] * 48L; seg++; }
                    double v = 0;
                    var f = seg < _hz.Length ? _hz[seg] : null;
                    if (f != null)
                    {
                        double env = Math.Min(1.0, Math.Min(t - acc, acc + _ms[seg] * 48L - t) / 240.0);   // 5 ms fades: no clicks
                        foreach (double hz in f) v += Math.Sin(2 * Math.PI * hz * _sample / 48000.0);
                        v = v / f.Length * 0.18 * env;
                    }
                    short sv = (short)(v * short.MaxValue);
                    buffer[offset + 2 * i] = (byte)sv;
                    buffer[offset + 2 * i + 1] = (byte)(sv >> 8);
                }
                return n * 2;
            }
        }
    }
}
