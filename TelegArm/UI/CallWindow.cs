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
    /// </summary>
    public sealed class CallWindow : DialogForm
    {
        private readonly CallManager _calls;
        private CallSession _s;
        private readonly Func<long, Image> _avatar;
        private readonly Func<CallSession, string> _name;
        private readonly CallStage _stage;
        private readonly RoundedButton _accept, _decline, _mute, _hangup, _close;
        private readonly Timer _tick;
        private readonly Ringer _ringer = new Ringer();
        private int _endedTick;

        public CallWindow(CallManager calls, CallSession s, Func<long, Image> avatar, Func<CallSession, string> name)
            : base("Call", 340, 400)
        {
            _calls = calls; _s = s; _avatar = avatar; _name = name;
            _stage = new CallStage(this) { Dock = DockStyle.Fill };
            UseOwnBody(_stage);
            // first added = rightmost
            _accept = AddFooterButton("Accept", RoundedButtonKind.Primary, DialogResult.None, 100);
            _hangup = AddFooterButton("Hang up", RoundedButtonKind.Danger, DialogResult.None, 100);
            _decline = AddFooterButton("Decline", RoundedButtonKind.Danger, DialogResult.None, 100);
            _mute = AddFooterButton("Mute", RoundedButtonKind.Secondary, DialogResult.None, 100);
            _close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.None, 100);
            _accept.Click += async (o, e) => { _ringer.Stop(); await _calls.AcceptAsync(); };
            _decline.Click += async (o, e) => { _ringer.Stop(); await _calls.HangUpAsync(); };
            _hangup.Click += async (o, e) => await _calls.HangUpAsync();
            _mute.Click += (o, e) => { if (_s != null) _calls.SetMuted(!_s.Muted); };
            _close.Click += (o, e) => Close();
            _calls.Changed += OnChanged;
            _tick = new Timer { Interval = 500 };
            _tick.Tick += (o, e) => Tick();
            _tick.Start();
            TopMost = true;
            Apply();
        }

        /// <summary>Points the window at another call (a new one while this window is still showing the last).</summary>
        public void Bind(CallSession s) { _s = s; _endedTick = 0; Apply(); }

        private void OnChanged(CallSession s) { if (ReferenceEquals(s, _s) && !IsDisposed) Apply(); }

        private void Apply()
        {
            var s = _s;
            if (s == null) return;
            Text = s.Video ? "Video call" : "Call";
            bool incoming = s.Phase == CallPhase.Incoming, ended = s.Ended;
            _accept.Visible = _decline.Visible = incoming;
            _close.Visible = ended;
            _mute.Visible = _hangup.Visible = !incoming && !ended;
            _mute.Text = s.Muted ? "Unmute" : "Mute";
            if (incoming) _ringer.Ring(Ringer.Pattern.Incoming);
            else if (s.Outgoing && (s.Phase == CallPhase.Waiting || s.Phase == CallPhase.Ringing)) _ringer.Ring(Ringer.Pattern.Ringback);
            else _ringer.Stop();
            if (ended && _endedTick == 0) _endedTick = Environment.TickCount;
            _stage.Invalidate();
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
                case CallPhase.Requesting: return "Requesting…";
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
                _tick.Dispose();
                _ringer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Avatar, name, status, signal bars.</summary>
        private sealed class CallStage : Control
        {
            private readonly CallWindow _w;
            public CallStage(CallWindow w)
            {
                _w = w;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = ThemeHelper.Palette;
                int dpi = _w.WindowDpiValue;
                Func<int, int> Px = v => Dpi.Px(v, dpi);   // the window's DPI (the check script reads Px( as scaled)
                g.Clear(p.Window);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var s = _w._s;
                if (s == null) return;
                string name = _w._name(s);
                int d = Px(112);
                var ar = new Rectangle((Width - d) / 2, Px(28), d, d);
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
