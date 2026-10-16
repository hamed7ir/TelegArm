using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TelegArm.Helpers;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// tgcalls_native.dll (native\tgcalls_native; tgcalls_native.h is the contract): Telegram's call library behind a C ABI,
    /// in tgcalls\&lt;ARM|x86&gt;\ beside the exe. Loaded by full path (LOAD_WITH_ALTERED_SEARCH_PATH), then the [DllImport]s
    /// bind to it by name. Structs mirror the header field for field (int32 / byte arrays only).
    /// </summary>
    internal static class TgCalls
    {
        private const string Dll = "tgcalls_native";

        public const int AudioDevices = 0, AudioTest = 1;
        public const int StateWaitInit = 0, StateWaitInitAck = 1, StateEstablished = 2, StateFailed = 3, StateReconnecting = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct Endpoint
        {
            public uint IdLo, IdHi;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] Ipv4;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Ipv6;
            public int Port;
            public int Tcp;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] PeerTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RtcServer
        {
            public int Id;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Host;
            public int Port;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Login;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Password;
            public int IsTurn;
            public int IsTcp;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CallParams
        {
            public IntPtr Version;          // UTF-8
            public IntPtr Key;              // 256 bytes
            public int IsOutgoing;
            public IntPtr Endpoints;
            public int EndpointCount;
            public IntPtr Servers;
            public int ServerCount;
            public int EnableP2p;
            public int AllowTcp;
            public int MaxLayer;
            public IntPtr CustomParameters; // UTF-8 or null
            public int AudioMode;
            public int TestToneHz;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void IntCallback(IntPtr user, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FloatCallback(IntPtr user, float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void BytesCallback(IntPtr user, IntPtr data, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void MediaCallback(IntPtr user, int audio, int video);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LogCallback(IntPtr user, IntPtr line);

        [StructLayout(LayoutKind.Sequential)]
        public struct Callbacks
        {
            public IntPtr User;
            public IntPtr State, Signaling, SignalBars, AudioLevel, RemoteMedia, RemoteBatteryLow;   // function pointers
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioStats
        {
            public int FramesRecorded, FramesRendered, FramesAudible, RenderedHz, RenderedRms, CaptureOverruns, PlaybackUnderruns;
        }

        private static readonly object _gate = new object();
        private static int _state;   // 0 untried, 1 loaded, -1 unavailable
        private static string _why;
        private static LogCallback _log;   // rooted for the process

        public static string Folder
        {
            get
            {
                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tgcalls",
                                    arch.StartsWith("ARM", StringComparison.OrdinalIgnoreCase) ? "ARM" : "x86");
            }
        }

        /// <summary>Loaded and initialised (WSAStartup, implementations registered, its log → Logger.Diag)?</summary>
        public static bool Available
        {
            get
            {
                if (_state != 0) return _state > 0;
                lock (_gate)
                {
                    if (_state != 0) return _state > 0;
                    _state = Load() ? 1 : -1;
                    return _state > 0;
                }
            }
        }

        public static string Status { get { bool ok = Available; return _why ?? (ok ? "loaded" : "not loaded"); } }

        private static bool Load()
        {
            string path = Path.Combine(Folder, "tgcalls_native.dll");
            try
            {
                if (!File.Exists(path)) { _why = "missing: " + path; Logger.Diag("[CALL] " + _why); return false; }
                if (LoadLibraryEx(path, IntPtr.Zero, 0x8) == IntPtr.Zero)
                {
                    // e.g. Windows 7: it imports the Windows 8.1 shcore scaling API set (as tg_owt does)
                    _why = "LoadLibraryEx failed (Win32 " + Marshal.GetLastWin32Error() + "): " + path;
                    Logger.Diag("[CALL] " + _why);
                    return false;
                }
                _log = (u, line) => { try { Logger.Diag("[TGCALLS] " + Utf8(line)); } catch { } };
                tgn_init(_log, IntPtr.Zero);
                int v = tgn_version();
                _why = "tgcalls_native " + (v >> 16) + "." + (v & 0xFFFF) + ", layer " + tgn_max_layer() + ", versions " + Versions() + " (" + Path.GetFileName(Folder) + ")";
                Logger.Diag("[CALL] " + _why);
                return true;
            }
            catch (Exception ex) { _why = ex.GetType().Name + ": " + ex.Message; Logger.Diag("[CALL] load failed: " + _why); return false; }
        }

        /// <summary>Newest first (numeric).</summary>
        public static string Versions() { var b = new byte[512]; tgn_versions(b, b.Length); return Z(b); }
        /// <summary>For phoneCallProtocol.library_versions, in tdesktop's order.</summary>
        public static string[] ApiVersions() { var b = new byte[512]; tgn_versions_api(b, b.Length); return Z(b).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries); }
        public static int MaxLayer() { return tgn_max_layer(); }

        public static string Z(byte[] b) { int n = Array.IndexOf(b, (byte)0); return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n); }

        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        /// <summary>A zero-terminated UTF-8 copy in a fixed array (truncated to fit).</summary>
        public static byte[] Fixed(string s, int size)
        {
            var b = new byte[size];
            if (!string.IsNullOrEmpty(s))
            {
                var u = Encoding.UTF8.GetBytes(s);
                Array.Copy(u, b, Math.Min(u.Length, size - 1));
            }
            return b;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int tgn_init(LogCallback log, IntPtr user);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int tgn_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int tgn_max_layer();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int tgn_versions(byte[] buf, int len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int tgn_versions_api(byte[] buf, int len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr tgn_call_create(ref CallParams p, ref Callbacks cb);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgn_call_receive_signaling(IntPtr c, byte[] data, int len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgn_call_set_mute(IntPtr c, int mute);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgn_call_stop(IntPtr c, byte[] debugLog, int len, int timeoutMs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgn_call_destroy(IntPtr c);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgn_call_audio_stats(IntPtr c, out AudioStats s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgn_selftest_loopback(int seconds, byte[] report, int len);
    }
}
