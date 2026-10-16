using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// One tgcalls group instance (tgn_group: a voice or video chat). Mirrors <see cref="NativeCall"/>: it owns the handle and
    /// the delegates the native side calls (kept here, so the GC cannot collect them while the instance lives); its events
    /// are raised on WebRTC's threads, so a subscriber copies what it needs and marshals to the UI. Stop() blocks while
    /// tgcalls shuts down: never on the UI thread, never from one of these events.
    /// </summary>
    internal sealed class NativeGroupCall : IDisposable
    {
        private IntPtr _h;
        private readonly TgCalls.GroupNetworkCallback _network;
        private readonly TgCalls.GroupPayloadCallback _payload;
        private readonly TgCalls.GroupLevelsCallback _levels;
        private readonly TgCalls.IntCallback _bars;
        private readonly object _gate = new object();
        private readonly Dictionary<string, CallVideoSink> _outputs = new Dictionary<string, CallVideoSink>();   // their delegates
        private bool _stopped;

        /// <summary>connected, mode (0 none, 1 RTC).</summary>
        public event Action<bool, int> NetworkChanged;
        /// <summary>Our audio ssrc and the join payload's JSON (for phone.joinGroupCall).</summary>
        public event Action<uint, string> JoinPayload;
        /// <summary>Every 100 ms: the sounding ssrcs (0 = us).</summary>
        public event Action<TgCalls.GroupLevel[]> Levels;
        public event Action<int> SignalBarsChanged;

        private NativeGroupCall()
        {
            _network = (u, c, m) => { var h = NetworkChanged; if (h != null) h(c != 0, m); };
            _bars = (u, v) => { var h = SignalBarsChanged; if (h != null) h(v); };
            _payload = (u, ssrc, json, len) =>
            {
                var h = JoinPayload;
                if (h == null || json == IntPtr.Zero || len <= 0) return;
                var b = new byte[len];
                Marshal.Copy(json, b, 0, len);   // the native string dies when we return
                h(ssrc, Encoding.UTF8.GetString(b));
            };
            _levels = (u, items, count) =>
            {
                var h = Levels;
                if (h == null || items == IntPtr.Zero || count <= 0) return;
                int size = Marshal.SizeOf(typeof(TgCalls.GroupLevel));
                var list = new TgCalls.GroupLevel[count];
                for (int i = 0; i < count; i++) list[i] = (TgCalls.GroupLevel)Marshal.PtrToStructure(items + i * size, typeof(TgCalls.GroupLevel));
                h(list);
            };
        }

        /// <summary>Creates the instance (muted, mode none). <paramref name="wire"/> subscribes the events before it exists;
        /// <paramref name="video"/>: video-capable (a camera may be sent later, pictures received). Null when unavailable.</summary>
        public static NativeGroupCall Create(bool video, bool preferVp8, int audioMode = TgCalls.AudioDevices, int toneHz = 0,
                                             IntPtr camera = default(IntPtr), Action<NativeGroupCall> wire = null)
        {
            if (!TgCalls.Available || !TgCalls.GroupAvailable) return null;
            var g = new NativeGroupCall();
            if (wire != null) wire(g);
            var p = new TgCalls.GroupParams
            {
                AudioMode = audioMode, TestToneHz = toneHz, Video = video ? 1 : 0, NoiseSuppression = 1, PreferVp8 = preferVp8 ? 1 : 0, Camera = camera,
            };
            var cb = new TgCalls.GroupCallbacks
            {
                Network = Marshal.GetFunctionPointerForDelegate(g._network),
                JoinPayload = Marshal.GetFunctionPointerForDelegate(g._payload),
                Levels = Marshal.GetFunctionPointerForDelegate(g._levels),
                SignalBars = Marshal.GetFunctionPointerForDelegate(g._bars),
            };
            g._h = TgCalls.tgn_group_create(ref p, ref cb);
            return g._h == IntPtr.Zero ? null : g;
        }

        private bool Live { get { return _h != IntPtr.Zero && !_stopped; } }

        /// <summary>The join payload comes back through <see cref="JoinPayload"/>.</summary>
        public void EmitJoin() { lock (_gate) if (Live) TgCalls.tgn_group_emit_join(_h); }

        /// <summary>The server's updateGroupCallConnection JSON; false when it is not a join response.</summary>
        public bool SetJoinResponse(string json)
        {
            lock (_gate) return Live && json != null && TgCalls.tgn_group_set_join_response(_h, TgCalls.Z8(json)) == 0;
        }

        public void SetMode(int mode) { lock (_gate) if (Live) TgCalls.tgn_group_set_mode(_h, mode); }
        public void SetMute(bool mute) { lock (_gate) if (Live) TgCalls.tgn_group_set_mute(_h, mute ? 1 : 0); }
        public void SetCamera(IntPtr camera) { lock (_gate) if (Live) TgCalls.tgn_group_set_camera(_h, camera); }
        public void SetVolume(uint ssrc, int permille) { lock (_gate) if (Live) TgCalls.tgn_group_set_volume(_h, ssrc, permille); }

        public void SetVideoChannels(IList<TgCalls.GroupVideo> list)
        {
            lock (_gate)
            {
                if (!Live) return;
                var arr = list != null ? new List<TgCalls.GroupVideo>(list).ToArray() : new TgCalls.GroupVideo[0];
                TgCalls.tgn_group_set_video_channels(_h, arr, arr.Length);
            }
        }

        /// <summary>A participant's pictures into <paramref name="sink"/> (null stops them).</summary>
        public void SetVideoOutput(string endpoint, CallVideoSink sink, int maxW, int maxH, int maxFps)
        {
            lock (_gate)
            {
                if (_h == IntPtr.Zero || string.IsNullOrEmpty(endpoint)) return;
                if (sink != null) _outputs[endpoint] = sink;   // rooted until Dispose: the DLL holds its delegate
                TgCalls.tgn_group_set_video_output(_h, TgCalls.Z8(endpoint), sink != null && !_stopped ? sink.Callback : null, IntPtr.Zero, maxW, maxH, maxFps);
            }
        }

        public TgCalls.AudioStats Stats()
        {
            TgCalls.AudioStats s;
            lock (_gate) { if (_h == IntPtr.Zero) return default(TgCalls.AudioStats); TgCalls.tgn_group_audio_stats(_h, out s); }
            return s;
        }

        /// <summary>Blocking (up to the timeout): not on the UI thread.</summary>
        public void Stop(int timeoutMs = 5000)
        {
            IntPtr h;
            lock (_gate) { if (_h == IntPtr.Zero || _stopped) return; _stopped = true; h = _h; }
            TgCalls.tgn_group_stop(h, timeoutMs);
        }

        public void Dispose()
        {
            IntPtr h;
            lock (_gate) { h = _h; _h = IntPtr.Zero; _stopped = true; }
            if (h != IntPtr.Zero) TgCalls.tgn_group_destroy(h);   // stops first if it has not been, then drains tgcalls
            GC.KeepAlive(_network); GC.KeepAlive(_payload); GC.KeepAlive(_levels); GC.KeepAlive(_bars);
            lock (_gate) _outputs.Clear();
        }
    }
}
