using System;
using System.IO;
using System.Runtime.InteropServices;
using TelegArm.Helpers;

namespace TelegArm.Core.Media
{
    /// <summary>
    /// The FFmpeg shim (native\tgav: tgav.h is the contract). 2026-10-16: replaces libVLC for every playback path. The
    /// DLLs live in ffmpeg\&lt;ARM|x86&gt;\ beside the exe: tgav.dll and the ffmpeg-rt libraries it imports. tgav.dll is loaded
    /// by FULL PATH with LOAD_WITH_ALTERED_SEARCH_PATH, so the loader finds avcodec-62.dll and the rest in tgav's own folder
    /// (no SetDllDirectory: NativeLibraries already points that at rlottie's folder); the [DllImport]s below then bind to
    /// the module already loaded under the name "tgav".
    /// </summary>
    internal static class Tgav
    {
        private const string Dll = "tgav";

        public const int NoAudio = 1, NoVideo = 2, SingleThread = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct Info
        {
            public int HasVideo, Width, Height, Rotation, FpsNum, FpsDen;
            public int HasAudio, SampleRate, Channels, DurationMs, VideoCodec, AudioCodec;
        }

        private static readonly object _gate = new object();
        private static int _state;   // 0 not tried, 1 loaded, -1 unavailable
        private static string _why;

        /// <summary>True once tgav.dll (and through it FFmpeg) has loaded. Tried once per process.</summary>
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

        private static int _version;

        /// <summary>tgav 1.1+: the camera-frame scaler (tgav_scale).</summary>
        public static bool HasScaler { get { return Available && _version >= 0x10001; } }

        /// <summary>For the diagnostics report: where it loaded from, or why not.</summary>
        public static string Status { get { bool ok = Available; return _why ?? (ok ? "loaded" : "not loaded"); } }

        public static string Folder
        {
            get
            {
                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg",
                                    arch.StartsWith("ARM", StringComparison.OrdinalIgnoreCase) ? "ARM" : "x86");
            }
        }

        private static bool Load()
        {
            string path = Path.Combine(Folder, "tgav.dll");
            try
            {
                if (!File.Exists(path)) { _why = "missing: " + path; Logger.Diag("[MEDIA] " + _why); return false; }
                IntPtr h = LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (h == IntPtr.Zero)
                {
                    _why = "LoadLibraryEx failed (Win32 " + Marshal.GetLastWin32Error() + "): " + path;
                    Logger.Diag("[MEDIA] " + _why);
                    return false;
                }
                int v = tgav_version(), ff = tgav_ffmpeg_version();
                _version = v;
                _why = "tgav " + (v >> 16) + "." + (v & 0xFFFF) + ", avcodec " + (ff >> 16) + "." + ((ff >> 8) & 0xFF) + "." + (ff & 0xFF)
                     + " (" + Path.GetFileName(Folder) + ")";
                Logger.Diag("[MEDIA] " + _why);
                return true;
            }
            catch (Exception ex) { _why = ex.GetType().Name + ": " + ex.Message; Logger.Diag("[MEDIA] load failed: " + _why); return false; }
        }

        public static string ErrorText(int err)
        {
            try
            {
                var b = new byte[256];
                tgav_strerror(err, b, b.Length);
                int n = Array.IndexOf(b, (byte)0);
                return System.Text.Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n) + " (" + err + ")";
            }
            catch { return "error " + err; }
        }

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_ffmpeg_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgav_strerror(int err, byte[] buf, int len);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern IntPtr tgav_open(string path, int flags, out int err);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_get_info(IntPtr t, out Info info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tgav_read_video(IntPtr t, IntPtr dst, int stride, int w, int h, out int ptsMs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_set_audio_format(IntPtr t, int rate, int channels);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_set_tempo(IntPtr t, int tempoPercent);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tgav_read_audio(IntPtr t, short[] dst, int maxSamples, out int ptsMs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int tgav_seek(IntPtr t, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgav_close(IntPtr t);

        // tgav 1.1: camera frames (NV12 / YUY2 / BGRA) → BGRA, cropped, scaled, optionally mirrored
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr tgav_scaler_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tgav_scale(IntPtr s, int fmt, IntPtr plane0, int stride0, IntPtr plane1, int stride1,
                                            int srcW, int srcH, int cropX, int cropY, int cropW, int cropH,
                                            IntPtr dst, int dstStride, int dstW, int dstH, int mirror);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void tgav_scaler_free(IntPtr s);
    }

    /// <summary>One open media file (a tgav handle). Not thread-safe by itself: MediaPlayback serializes its calls.</summary>
    internal sealed class MediaFile : IDisposable
    {
        private IntPtr _h;
        public Tgav.Info Info;
        public string Path { get; private set; }

        private MediaFile(IntPtr h, string path) { _h = h; Path = path; Tgav.tgav_get_info(h, out Info); }

        /// <summary>Null (and the reason in <paramref name="error"/>) when the file can't be opened or FFmpeg isn't here.</summary>
        public static MediaFile Open(string path, int flags, out string error)
        {
            error = null;
            if (!Tgav.Available) { error = "media engine unavailable: " + Tgav.Status; return null; }
            int err;
            IntPtr h = Tgav.tgav_open(path, flags, out err);
            if (h == IntPtr.Zero) { error = Tgav.ErrorText(err); return null; }
            return new MediaFile(h, path);
        }

        public bool HasVideo { get { return Info.HasVideo != 0; } }
        public bool HasAudio { get { return Info.HasAudio != 0; } }

        /// <summary>1 = a frame, 0 = end, &lt; 0 = error. dst = IntPtr.Zero decodes without converting (a dropped frame).</summary>
        public int ReadVideo(IntPtr dst, int stride, int w, int h, out int ptsMs) { return Tgav.tgav_read_video(_h, dst, stride, w, h, out ptsMs); }
        public int ReadAudio(short[] dst, int maxSamples, out int ptsMs) { return Tgav.tgav_read_audio(_h, dst, maxSamples, out ptsMs); }
        public int SetAudioFormat(int rate, int channels) { return Tgav.tgav_set_audio_format(_h, rate, channels); }
        public int SetTempo(int percent) { return Tgav.tgav_set_tempo(_h, percent); }
        public int Seek(int ms) { return Tgav.tgav_seek(_h, ms); }

        public void Dispose()
        {
            var h = _h;
            _h = IntPtr.Zero;
            if (h != IntPtr.Zero) Tgav.tgav_close(h);
        }
    }
}
