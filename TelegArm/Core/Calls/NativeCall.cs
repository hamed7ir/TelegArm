using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// One tgcalls instance (tgn_call). Owns the native handle and the delegates the native side calls back on (kept here,
    /// so the GC cannot collect them while the call lives). Its events are raised on WebRTC's threads: a subscriber copies
    /// what it needs and marshals to the UI. Stop() blocks (up to the timeout) while tgcalls shuts down: never on the UI
    /// thread, never from one of these events.
    /// </summary>
    internal sealed class NativeCall : IDisposable
    {
        private IntPtr _h;
        private readonly TgCalls.IntCallback _state, _bars, _battery;
        private readonly TgCalls.BytesCallback _signal;
        private readonly TgCalls.FloatCallback _level;
        private readonly TgCalls.MediaCallback _media;
        private readonly object _gate = new object();
        private bool _stopped;

        public event Action<int> StateChanged;
        public event Action<byte[]> SignalingEmitted;
        public event Action<int> SignalBarsChanged;
        public event Action<float> AudioLevel;
        public event Action<int, int> RemoteMediaChanged;   // audio (0 muted, 1 active), video (0 inactive, 1 paused, 2 active)

        private NativeCall()
        {
            _state = (u, v) => { var h = StateChanged; if (h != null) h(v); };
            _bars = (u, v) => { var h = SignalBarsChanged; if (h != null) h(v); };
            _battery = (u, v) => { };
            _level = (u, v) => { var h = AudioLevel; if (h != null) h(v); };
            _media = (u, a, v) => { var h = RemoteMediaChanged; if (h != null) h(a, v); };
            _signal = (u, data, len) =>
            {
                var h = SignalingEmitted;
                if (h == null || data == IntPtr.Zero || len <= 0) return;
                var b = new byte[len];
                Marshal.Copy(data, b, 0, len);   // the native buffer dies when we return
                h(b);
            };
        }

        /// <summary>Starts the call (tgcalls connects at once). Null when this version has no implementation.</summary>
        public static NativeCall Create(string version, byte[] key, bool outgoing, IList<TgCalls.Endpoint> endpoints,
                                        IList<TgCalls.RtcServer> servers, bool p2p, int maxLayer, string customParameters,
                                        int audioMode = TgCalls.AudioDevices, int toneHz = 0)
        {
            if (!TgCalls.Available || key == null || key.Length != 256) return null;
            var call = new NativeCall();
            var allocs = new List<IntPtr>();
            Func<byte[], IntPtr> pin = bytes => { var p = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, p, bytes.Length); allocs.Add(p); return p; };
            Func<string, IntPtr> utf8 = s => s == null ? IntPtr.Zero : pin(Encoding.UTF8.GetBytes(s + "\0"));
            try
            {
                var p = new TgCalls.CallParams
                {
                    Version = utf8(version),
                    Key = pin(key),
                    IsOutgoing = outgoing ? 1 : 0,
                    EnableP2p = p2p ? 1 : 0,
                    MaxLayer = maxLayer,
                    CustomParameters = utf8(customParameters),
                    AudioMode = audioMode,
                    TestToneHz = toneHz,
                };
                if (endpoints != null && endpoints.Count > 0)
                {
                    int size = Marshal.SizeOf(typeof(TgCalls.Endpoint));
                    var arr = Marshal.AllocHGlobal(size * endpoints.Count); allocs.Add(arr);
                    for (int i = 0; i < endpoints.Count; i++) Marshal.StructureToPtr(endpoints[i], arr + i * size, false);
                    p.Endpoints = arr; p.EndpointCount = endpoints.Count;
                }
                if (servers != null && servers.Count > 0)
                {
                    int size = Marshal.SizeOf(typeof(TgCalls.RtcServer));
                    var arr = Marshal.AllocHGlobal(size * servers.Count); allocs.Add(arr);
                    for (int i = 0; i < servers.Count; i++) Marshal.StructureToPtr(servers[i], arr + i * size, false);
                    p.Servers = arr; p.ServerCount = servers.Count;
                }
                var cb = new TgCalls.Callbacks
                {
                    State = Marshal.GetFunctionPointerForDelegate(call._state),
                    Signaling = Marshal.GetFunctionPointerForDelegate(call._signal),
                    SignalBars = Marshal.GetFunctionPointerForDelegate(call._bars),
                    AudioLevel = Marshal.GetFunctionPointerForDelegate(call._level),
                    RemoteMedia = Marshal.GetFunctionPointerForDelegate(call._media),
                    RemoteBatteryLow = Marshal.GetFunctionPointerForDelegate(call._battery),
                };
                call._h = TgCalls.tgn_call_create(ref p, ref cb);   // copies everything it keeps
                return call._h == IntPtr.Zero ? null : call;
            }
            finally { foreach (var a in allocs) Marshal.FreeHGlobal(a); }
        }

        public void ReceiveSignaling(byte[] data)
        {
            lock (_gate) if (_h != IntPtr.Zero && !_stopped && data != null && data.Length > 0) TgCalls.tgn_call_receive_signaling(_h, data, data.Length);
        }

        public void SetMute(bool mute)
        {
            lock (_gate) if (_h != IntPtr.Zero && !_stopped) TgCalls.tgn_call_set_mute(_h, mute ? 1 : 0);
        }

        public TgCalls.AudioStats Stats()
        {
            TgCalls.AudioStats s;
            lock (_gate) { if (_h == IntPtr.Zero) return default(TgCalls.AudioStats); TgCalls.tgn_call_audio_stats(_h, out s); }
            return s;
        }

        /// <summary>Stops the call; the debug log for phone.saveCallDebug. Blocking: not on the UI thread.</summary>
        public string Stop(int timeoutMs = 5000)
        {
            IntPtr h;
            lock (_gate) { if (_h == IntPtr.Zero || _stopped) return ""; _stopped = true; h = _h; }
            var log = new byte[64 * 1024];
            TgCalls.tgn_call_stop(h, log, log.Length, timeoutMs);
            return TgCalls.Z(log);
        }

        public void Dispose()
        {
            IntPtr h;
            lock (_gate) { h = _h; _h = IntPtr.Zero; _stopped = true; }
            if (h != IntPtr.Zero) TgCalls.tgn_call_destroy(h);   // stops first if it has not been
            GC.KeepAlive(_state); GC.KeepAlive(_signal); GC.KeepAlive(_bars); GC.KeepAlive(_level); GC.KeepAlive(_media); GC.KeepAlive(_battery);
        }
    }
}
