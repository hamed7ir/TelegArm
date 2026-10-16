using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using TelegArm.Helpers;

namespace TelegArm.Core.Media
{
    /// <summary>The player states the UI reads (the names libVLC's VLCState had, so the call sites read the same).</summary>
    public enum MediaState { NothingSpecial, Opening, Buffering, Playing, Paused, Stopped, Ended, Error }

    /// <summary>
    /// The newest decoded video frame, shared between the video thread (writes the back buffer, swaps) and the UI (paints
    /// the front one under <see cref="Sync"/>). BGRA, 32 bpp, top-down, stride = width * 4.
    /// </summary>
    public sealed class VideoFrameStore
    {
        public readonly object Sync = new object();
        public byte[] Front;
        public int FrontWidth, FrontHeight;
        public int Serial;                 // bumps with every new frame
        internal byte[] Back;
        internal GCHandle BackPin;
        internal int BackWidth, BackHeight;

        internal void EnsureBack(int w, int h)
        {
            if (Back != null && BackWidth == w && BackHeight == h) return;
            if (BackPin.IsAllocated) BackPin.Free();
            Back = new byte[w * h * 4];
            BackPin = GCHandle.Alloc(Back, GCHandleType.Pinned);
            BackWidth = w; BackHeight = h;
        }

        internal void Swap()
        {
            lock (Sync)
            {
                var b = Front; int bw = FrontWidth, bh = FrontHeight;
                Front = Back; FrontWidth = BackWidth; FrontHeight = BackHeight;
                if (BackPin.IsAllocated) BackPin.Free();
                Back = b; BackWidth = bw; BackHeight = bh;
                if (Back != null) BackPin = GCHandle.Alloc(Back, GCHandleType.Pinned);
                Serial++;
            }
        }

        internal void Free() { if (BackPin.IsAllocated) BackPin.Free(); }
    }

    /// <summary>
    /// Plays one file through the FFmpeg shim (2026-10-16: replaces libVLC's MediaPlayer). Opening runs off the calling
    /// thread. Audio: the shim's s16 48 kHz stereo (resampled, and tempo-stretched by atempo) into an NAudio waveOut, the
    /// same winmm path VoiceRecorder records through. Video: a thread decodes frames at the size the surface asks for and
    /// swaps them in at their presentation time.
    ///
    /// THE CLOCK: with audio it is the samples the device has played (bounded by the samples actually queued, so an
    /// underrun freezes the clock instead of running it ahead of the picture); without audio, or once the audio has
    /// ended, a stopwatch. Every MediaFile call holds <see cref="_io"/>, and a seek bumps <see cref="_seekGen"/>: a frame or
    /// a block of samples read before the seek is never presented after it.
    /// </summary>
    public sealed class MediaPlayback : IDisposable
    {
        private const int Rate = 48000, Channels = 2, BytesPerFrame = 4;

        private readonly string _path;
        private readonly bool _loop, _wantVideo, _wantAudio;
        private readonly object _io = new object();
        private readonly object _stateLock = new object();
        private MediaFile _file;
        private Thread _openThread, _audioThread, _videoThread;
        private volatile bool _stop;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private volatile MediaState _state = MediaState.Opening;
        private volatile bool _playRequested = true;
        private volatile int _seekGen;

        // audio
        private WaveOutEvent _out;
        private BufferedWaveProvider _buf;
        private long _written;          // sample frames queued since the base
        private long _devBase;          // the device position (sample frames) at the base
        private volatile bool _audioEof, _audioClock;
        private long _lastDevPos = -1;  // the stall guard: the device position last seen moving, and when
        private int _lastDevTick;
        // clock base
        private int _baseMs;
        private readonly Stopwatch _sw = new Stopwatch();
        // video
        private volatile int _tw, _th;
        private volatile bool _videoEof, _needFrame = true;
        private int _lastPts, _frameMs = 40;
        private int _lastFinishTick;

        private volatile int _volume = 100;
        private volatile bool _mute;
        private volatile int _tempo = 100;

        public VideoFrameStore Frames { get; private set; }
        public string Error { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool HasVideo { get; private set; }
        public bool HasAudio { get; private set; }
        public int LengthMs { get; private set; }

        /// <summary>A new frame is in <see cref="Frames"/> (raised on the video thread).</summary>
        public event Action FrameReady;
        /// <summary>Opened, failed, ended (raised on a playback thread).</summary>
        public event Action StateChanged;

        public MediaPlayback(string path, bool loop, bool video, bool audio)
        {
            _path = path; _loop = loop; _wantVideo = video; _wantAudio = audio;
            Frames = new VideoFrameStore();
            _openThread = new Thread(OpenWorker) { IsBackground = true, Name = "media-open" };
            _openThread.Start();
        }

        public MediaState State { get { return _state; } }

        public int TimeMs { get { return Math.Max(0, Math.Min(ClockMs, LengthMs > 0 ? LengthMs : int.MaxValue)); } }

        public float Position
        {
            get { int len = LengthMs; return len > 0 ? Math.Min(1f, (float)TimeMs / len) : 0f; }
            set { int len = LengthMs; if (len > 0) Seek((int)(Math.Max(0f, Math.Min(1f, value)) * len)); }
        }

        public int Volume { get { return _volume; } set { _volume = Math.Max(0, Math.Min(100, value)); } }
        public bool Mute { get { return _mute; } set { _mute = value; } }

        /// <summary>Playback speed in percent (50..400), pitch kept. Re-seeks to "now" so nothing queued at the old speed is lost.</summary>
        public int TempoPercent
        {
            get { return _tempo; }
            set
            {
                int v = Math.Max(50, Math.Min(400, value));
                if (v == _tempo) return;
                lock (_io)
                {
                    int now = ClockMs;
                    _tempo = v;
                    if (_file != null && HasAudio) _file.SetTempo(v);
                    SeekLocked(now);
                }
            }
        }

        /// <summary>The frame size the surface wants (its fitted video rectangle); the next frame comes at that size.</summary>
        public void SetVideoTarget(int w, int h)
        {
            w &= ~1; h &= ~1;
            if (w < 2 || h < 2) return;
            if (w == _tw && h == _th) return;
            _tw = w; _th = h;
            if (_state != MediaState.Playing) _needFrame = true;   // paused: show the frame again at the new size
            _wake.Set();
        }

        // ── open ─────────────────────────────────────────────────────────────────────────────────────────────────────

        private void OpenWorker()
        {
            string err;
            int flags = (_wantVideo ? 0 : Tgav.NoVideo) | (_wantAudio ? 0 : Tgav.NoAudio);
            var f = MediaFile.Open(_path, flags, out err);
            if (f == null) { Fail(err); return; }
            if (_stop) { f.Dispose(); return; }
            lock (_io) { _file = f; }
            HasVideo = f.HasVideo;
            HasAudio = f.HasAudio;
            Width = f.Info.Width; Height = f.Info.Height;
            LengthMs = f.Info.DurationMs;
            if (f.Info.FpsNum > 0 && f.Info.FpsDen > 0) _frameMs = Math.Max(5, (int)(1000L * f.Info.FpsDen / f.Info.FpsNum));
            if (Logger.Enabled)
                Logger.Diag("[MEDIA] open " + System.IO.Path.GetFileName(_path) + " video=" + (HasVideo ? Width + "x" + Height + " rot" + f.Info.Rotation : "-")
                            + " audio=" + (HasAudio ? f.Info.SampleRate + "Hz/" + f.Info.Channels : "-") + " len=" + LengthMs + "ms");
            if (HasAudio)
            {
                try
                {
                    f.SetAudioFormat(Rate, Channels);
                    _buf = new BufferedWaveProvider(new WaveFormat(Rate, 16, Channels))
                    { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true };
                    _out = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
                    _out.Init(_buf);
                    _audioClock = true;
                }
                catch (Exception ex)
                {
                    // No output device (or winmm refused): play the picture on the stopwatch clock, silently.
                    Logger.Diag("[MEDIA] audio output unavailable: " + ex.Message);
                    try { if (_out != null) _out.Dispose(); } catch { }
                    _out = null; _buf = null; _audioClock = false; HasAudio = false;
                }
            }
            if (HasAudio) { _audioThread = new Thread(AudioWorker) { IsBackground = true, Name = "media-audio", Priority = ThreadPriority.AboveNormal }; _audioThread.Start(); }
            if (HasVideo) { _videoThread = new Thread(VideoWorker) { IsBackground = true, Name = "media-video" }; _videoThread.Start(); }
            SetState(_playRequested ? MediaState.Playing : MediaState.Paused);
            if (_playRequested) StartClock();
        }

        private void Fail(string why)
        {
            Error = why;
            Logger.Diag("[MEDIA] can't play " + System.IO.Path.GetFileName(_path) + ": " + why);
            SetState(MediaState.Error);
        }

        private void SetState(MediaState s)
        {
            lock (_stateLock) { if (_state == s) return; _state = s; }
            try { var h = StateChanged; if (h != null) h(); } catch { }
        }

        // ── controls ─────────────────────────────────────────────────────────────────────────────────────────────────

        public void Play()
        {
            _playRequested = true;
            var s = _state;
            if (s == MediaState.Opening || s == MediaState.Error) return;
            if (s == MediaState.Ended) Seek(0);
            StartClock();
            SetState(MediaState.Playing);
            _wake.Set();
        }

        public void Pause()
        {
            _playRequested = false;
            var s = _state;
            if (s != MediaState.Playing) return;
            StopClock();
            SetState(MediaState.Paused);
        }

        public void TogglePause() { if (_state == MediaState.Playing) Pause(); else Play(); }

        private void StartClock()
        {
            _lastDevTick = Environment.TickCount;   // a pause is not a stall
            try { if (_out != null) _out.Play(); } catch (Exception ex) { Logger.Diag("[MEDIA] waveOut play: " + ex.Message); }
            _sw.Start();
        }

        private void StopClock()
        {
            try { if (_out != null) _out.Pause(); } catch { }
            _sw.Stop();
        }

        public void Seek(int ms)
        {
            lock (_io) SeekLocked(ms);
            if (_state == MediaState.Ended) SetState(MediaState.Paused);   // seeking back into a finished clip
        }

        private void SeekLocked(int ms)
        {
            if (_file == null) return;
            if (LengthMs > 0) ms = Math.Min(ms, Math.Max(0, LengthMs - 1));
            ms = Math.Max(0, ms);
            _file.Seek(ms);
            _seekGen++;
            _audioEof = false; _videoEof = false;
            if (_buf != null) _buf.ClearBuffer();
            _written = 0;
            _devBase = DevicePosition();
            _audioClock = _buf != null;
            _baseMs = ms;
            bool running = _sw.IsRunning;
            _sw.Reset();
            if (running) _sw.Start();
            _needFrame = true;
            _wake.Set();
        }

        private long DevicePosition()
        {
            try { return _out != null ? _out.GetPosition() / BytesPerFrame : 0; } catch { return 0; }
        }

        /// <summary>Media time now (ms).</summary>
        public int ClockMs
        {
            get
            {
                if (_audioClock && _buf != null)
                {
                    long dev = DevicePosition() - _devBase;
                    long queued = _written - _buf.BufferedBytes / BytesPerFrame;
                    long played = Math.Max(0, Math.Min(dev, queued));
                    return _baseMs + (int)(played * 1000L * _tempo / 100 / Rate);
                }
                return _baseMs + (int)(_sw.ElapsedMilliseconds * _tempo / 100);
            }
        }

        // ── workers ──────────────────────────────────────────────────────────────────────────────────────────────────

        private void AudioWorker()
        {
            const int Chunk = 2048;
            var pcm = new short[Chunk * Channels];
            var bytes = new byte[Chunk * BytesPerFrame];
            try
            {
                while (!_stop)
                {
                    if (!_audioEof && _buf.BufferedDuration.TotalMilliseconds < 700)
                    {
                        lock (_io)
                        {
                            if (_stop || _file == null) break;
                            int pts, n = _file.ReadAudio(pcm, Chunk, out pts);
                            if (n <= 0)
                            {
                                if (n < 0) Logger.Diag("[MEDIA] audio read: " + Tgav.ErrorText(n));
                                _audioEof = true;
                            }
                            else
                            {
                                int vol = _mute ? 0 : _volume, count = n * Channels;
                                if (vol >= 100) Buffer.BlockCopy(pcm, 0, bytes, 0, count * 2);
                                else
                                    for (int i = 0; i < count; i++)
                                    {
                                        int v = pcm[i] * vol / 100;
                                        bytes[2 * i] = (byte)v; bytes[2 * i + 1] = (byte)(v >> 8);
                                    }
                                _buf.AddSamples(bytes, 0, count * 2);
                                _written += n;
                            }
                        }
                        continue;
                    }
                    if (_audioEof && _audioClock && _buf.BufferedBytes == 0)
                    {
                        // The queue has drained: once the device has played what it still holds, the clock hands over to
                        // the stopwatch (a video may run on past its audio). The wait is outside the lock: a held _io
                        // would stall the video thread's reads.
                        int gen = _seekGen;
                        _wake.WaitOne(Math.Min(250, _out != null ? _out.DesiredLatency : 0));
                        lock (_io)
                        {
                            if (gen == _seekGen && _audioEof && _audioClock)
                            {
                                _baseMs = ClockMs; _audioClock = false;
                                bool running = _state == MediaState.Playing;
                                _sw.Reset(); if (running) _sw.Start();
                            }
                        }
                    }
                    CheckStall();
                    CheckFinished();
                    _wake.WaitOne(25);
                }
            }
            catch (Exception ex) { Logger.Diag("[MEDIA] audio thread: " + ex); }
        }

        private void VideoWorker()
        {
            bool dropping = false;
            try
            {
                while (!_stop)
                {
                    bool playing = _state == MediaState.Playing;
                    int w = _tw, h = _th;
                    if (_videoEof || (!playing && !_needFrame) || w < 2 || h < 2)
                    {
                        if (_videoEof) CheckFinished();
                        _wake.WaitOne(30);
                        continue;
                    }
                    int gen, pts, r;
                    lock (_io)
                    {
                        if (_stop || _file == null) break;
                        gen = _seekGen;
                        Frames.EnsureBack(w, h);
                        IntPtr dst = dropping && !_needFrame ? IntPtr.Zero : Frames.BackPin.AddrOfPinnedObject();
                        r = _file.ReadVideo(dst, w * 4, w, h, out pts);
                        if (r > 0 && dst == IntPtr.Zero)
                        {
                            if (ClockMs - pts < _frameMs) dropping = false;   // caught up: the next frame is converted
                            continue;
                        }
                    }
                    if (r <= 0)
                    {
                        if (r < 0) Logger.Diag("[MEDIA] video read: " + Tgav.ErrorText(r));
                        _videoEof = true;
                        continue;
                    }
                    // wait for its presentation time (a seek, a stop or a size change while paused cuts the wait short)
                    while (!_stop && gen == _seekGen && !_needFrame)
                    {
                        int d = pts - ClockMs;
                        if (d <= 3) break;
                        _wake.WaitOne(Math.Min(d, 20));
                    }
                    if (_stop || gen != _seekGen) continue;   // read before a seek: never shown after it
                    Frames.Swap();
                    _needFrame = false;
                    _lastPts = pts;
                    if (_state == MediaState.Opening) SetState(MediaState.Playing);
                    try { var fr = FrameReady; if (fr != null) fr(); } catch { }
                    if (ClockMs - pts > 4 * _frameMs + 60) dropping = true;   // behind: decode-only until caught up
                }
            }
            catch (Exception ex) { Logger.Diag("[MEDIA] video thread: " + ex); }
        }

        /// <summary>The audio clock follows the device's play position, so a driver whose position never moves would hold
        /// the picture still for ever. Playing, with audio queued, and the position unmoved for 2 s: the clock hands over to
        /// the stopwatch (the sound plays on; the picture keeps time without it).</summary>
        private void CheckStall()
        {
            if (!_audioClock || _state != MediaState.Playing || _written < Rate / 2) return;
            long pos = DevicePosition();
            int now = Environment.TickCount;
            if (pos != _lastDevPos) { _lastDevPos = pos; _lastDevTick = now; return; }
            if (unchecked(now - _lastDevTick) < 2000) return;
            lock (_io)
            {
                if (!_audioClock) return;
                _baseMs = ClockMs; _audioClock = false;
                _sw.Reset(); _sw.Start();
            }
            Logger.Diag("[MEDIA] the audio device's position has not moved for 2 s: the stopwatch keeps time");
        }

        /// <summary>Both streams done (audio drained, the last frame's time passed) → loop or end.</summary>
        private void CheckFinished()
        {
            if (_state != MediaState.Playing) return;
            bool audioDone = !HasAudio || (_audioEof && !_audioClock);
            bool videoDone = !HasVideo || (_videoEof && ClockMs >= _lastPts + _frameMs);
            if (!audioDone || !videoDone) return;
            if (_loop)
            {
                int now = Environment.TickCount;
                if (unchecked(now - _lastFinishTick) < 250) { Thread.Sleep(250); }   // anti-storm: a clip that ends at once
                _lastFinishTick = now;
                Seek(0);
                return;
            }
            StopClock();
            SetState(MediaState.Ended);
        }

        public void Stop()
        {
            _playRequested = false;
            StopClock();
            SetState(MediaState.Stopped);
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            foreach (var t in new[] { _openThread, _audioThread, _videoThread })
                try { if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(1500); } catch { }
            try { if (_out != null) { _out.Stop(); _out.Dispose(); } } catch { }
            _out = null;
            lock (_io) { try { if (_file != null) _file.Dispose(); } catch { } _file = null; }
            Frames.Free();
            FrameReady = null; StateChanged = null;
        }
    }
}
