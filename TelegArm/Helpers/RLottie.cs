using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Managed wrapper over Samsung's rlottie C API for rendering Telegram <c>.tgs</c> (gzipped
    /// Lottie) animated stickers. The native <c>rlottie.dll</c> is architecture-specific (x86 /
    /// ARM32) and is located by <see cref="NativeLibraries"/>'s startup SetDllDirectory call.
    /// If the native library is absent, every entry point degrades to null/false so callers fall
    /// back to a static representation (e.g. the sticker's representative emoji).
    /// </summary>
    public static class RLottie
    {
        private const string Dll = "rlottie";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr lottie_animation_from_data(string data, string key, string resourcePath);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lottie_animation_destroy(IntPtr animation);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr lottie_animation_get_totalframe(IntPtr animation);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern double lottie_animation_get_framerate(IntPtr animation);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lottie_animation_render(
            IntPtr animation, UIntPtr frameNo, IntPtr buffer, UIntPtr width, UIntPtr height, UIntPtr bytesPerLine);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private static bool? _available;
        private static string _lastError;

        /// <summary>True if the native rlottie library loaded successfully (probed once).</summary>
        public static bool Available
        {
            get
            {
                if (_available == null)
                {
                    try
                    {
                        // Resolves the DLL without rendering anything; "{}" parses but yields no frames.
                        IntPtr h = lottie_animation_from_data("{}", "probe", "");
                        if (h != IntPtr.Zero) lottie_animation_destroy(h);
                        _available = true;
                    }
                    catch (Exception ex)
                    {
                        _available = false;
                        _lastError = ex.GetType().Name + ": " + ex.Message;
                    }
                }
                return _available.Value;
            }
        }

        /// <summary>
        /// Human-readable report of whether the per-arch rlottie.dll can be located and loaded,
        /// with the exact Win32 error when it can't — for diagnosing device-specific failures
        /// (e.g. a missing CRT dependency on Windows RT 8.1). Safe to call from the UI.
        /// </summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
            string sub = arch.StartsWith("ARM", StringComparison.OrdinalIgnoreCase) ? "ARM" : "x86";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rlottie", sub, "rlottie.dll");

            sb.AppendLine("Process arch: " + arch + "  (IntPtr.Size=" + IntPtr.Size + ")");
            sb.AppendLine("Expected dll: " + path);
            bool exists = File.Exists(path);
            sb.AppendLine("File exists: " + exists);

            if (exists)
            {
                try { sb.AppendLine("PE machine: 0x" + ReadPeMachine(path).ToString("X4") + MachineHint(ReadPeMachine(path))); }
                catch { }

                IntPtr h = LoadLibraryEx(path, IntPtr.Zero, 0);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    sb.AppendLine("LoadLibrary FAILED: Win32 " + err + " — " + new System.ComponentModel.Win32Exception(err).Message);
                    sb.AppendLine(ErrorHint(err));
                }
                else { sb.AppendLine("LoadLibrary: OK"); FreeLibrary(h); }
            }

            sb.AppendLine("Managed probe (Available): " + Available);
            if (!string.IsNullOrEmpty(_lastError)) sb.AppendLine("Probe error: " + _lastError);
            return sb.ToString();
        }

        private static ushort ReadPeMachine(string file)
        {
            var b = File.ReadAllBytes(file);
            int e = BitConverter.ToInt32(b, 0x3C);            // e_lfanew → PE header offset
            return BitConverter.ToUInt16(b, e + 4);          // IMAGE_FILE_HEADER.Machine
        }

        private static string MachineHint(ushort m)
        {
            switch (m)
            {
                case 0x014C: return " (x86)";
                case 0x01C4: return " (ARM/Thumb-2, 32-bit)";
                case 0x8664: return " (x64)";
                case 0xAA64: return " (ARM64)";
                default: return "";
            }
        }

        private static string ErrorHint(int err)
        {
            switch (err)
            {
                case 126:  // ERROR_MOD_NOT_FOUND
                    return "→ The dll OR one of its dependencies is missing. On Windows RT 8.1 this is " +
                           "almost always the VC++/Universal CRT (vcruntime140/msvcp140/api-ms-win-crt-*). " +
                           "Rebuild rlottie with the STATIC CRT (/MT) and target the Windows 8.1 SDK.";
                case 193:  // ERROR_BAD_EXE_FORMAT
                    return "→ Architecture mismatch: this dll's bitness doesn't match the process.";
                case 127:  // ERROR_PROC_NOT_FOUND
                    return "→ A required export is missing in a dependency (built against a newer API set than RT 8.1 has).";
                case 1114: // ERROR_DLL_INIT_FAILED
                    return "→ The dll's startup (DllMain) failed.";
                default:
                    return "";
            }
        }

        /// <summary>Gunzips a .tgs payload to its raw Lottie JSON (null if not gzip / undecodable).</summary>
        public static string DecompressTgs(byte[] tgs)
        {
            if (tgs == null || tgs.Length < 2) return null;
            try
            {
                using (var ms = new MemoryStream(tgs))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>Opens a Lottie animation from JSON; null on failure or when the native lib is absent.</summary>
        public static LottieClip Open(string json)
        {
            if (!Available || string.IsNullOrEmpty(json)) return null;
            try
            {
                IntPtr h = lottie_animation_from_data(json, Guid.NewGuid().ToString("N"), "");
                if (h == IntPtr.Zero) return null;
                int total = (int)lottie_animation_get_totalframe(h);
                double fps = lottie_animation_get_framerate(h);
                if (total <= 0) { lottie_animation_destroy(h); return null; }
                return new LottieClip(h, total, fps);
            }
            catch { return null; }
        }

        /// <summary>Convenience: gunzip + open a .tgs in one call.</summary>
        public static LottieClip OpenTgs(byte[] tgs) => Open(DecompressTgs(tgs));

        // Internal native shims used by LottieClip (keeps all P/Invoke in one place).
        internal static void Render(IntPtr h, int frame, IntPtr buffer, int size, int stride)
            => lottie_animation_render(h, (UIntPtr)(uint)frame, buffer,
                                       (UIntPtr)(uint)size, (UIntPtr)(uint)size, (UIntPtr)(uint)stride);

        internal static void Destroy(IntPtr h)
        {
            try { lottie_animation_destroy(h); } catch { }
        }
    }

    /// <summary>A live rlottie animation. Render frames into a 32bpp premultiplied bitmap; Dispose frees it.</summary>
    public sealed class LottieClip : IDisposable
    {
        private IntPtr _h;
        public int TotalFrames { get; }
        public double Fps { get; }

        internal LottieClip(IntPtr h, int total, double fps)
        {
            _h = h; TotalFrames = Math.Max(1, total); Fps = fps > 1 ? fps : 30;
        }

        /// <summary>Renders frame N into an existing square 32bpp-PArgb bitmap (reused across frames).</summary>
        public void RenderInto(Bitmap bmp, int frame)
        {
            if (_h == IntPtr.Zero || bmp == null) return;
            frame = ((frame % TotalFrames) + TotalFrames) % TotalFrames;
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { RLottie.Render(_h, frame, data.Scan0, bmp.Width, data.Stride); }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>Renders a single frame into a fresh size×size bitmap (for static stills / caching).</summary>
        public Bitmap RenderFrame(int frame, int size)
        {
            if (_h == IntPtr.Zero || size <= 0) return null;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            RenderInto(bmp, frame);
            return bmp;
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { RLottie.Destroy(_h); _h = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Loops a <see cref="LottieClip"/> onto a single reused bitmap, pushing each frame to a callback.
    /// All animators share one WinForms timer (~25 Hz) ticking on the UI thread, so rendering and
    /// painting never overlap and frames need no per-tick allocation. Dispose to stop + free.
    /// </summary>
    public sealed class LottieAnimator : IDisposable
    {
        private static readonly System.Windows.Forms.Timer _ticker;
        private static readonly List<LottieAnimator> _active = new List<LottieAnimator>();
        private static bool _globallyPaused;   // pause-on-background: keep the ticker stopped even as new animators register

        static LottieAnimator()
        {
            _ticker = new System.Windows.Forms.Timer { Interval = 40 };   // ~25 fps, gentle on ARM32
            _ticker.Tick += (s, e) =>
            {
                LottieAnimator[] snap;
                lock (_active) snap = _active.ToArray();
                foreach (var a in snap) a.Step();
            };
        }

        private readonly LottieClip _clip;
        private readonly Bitmap _buffer;
        private readonly Action<Image> _onFrame;
        private readonly int _startTick;
        private int _lastFrame = -1;
        private bool _disposed;

        public LottieAnimator(LottieClip clip, int size, Action<Image> onFrame)
        {
            _clip = clip;
            _buffer = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            _onFrame = onFrame;
            _startTick = Environment.TickCount;
            Render(0);                          // show first frame immediately
            lock (_active) _active.Add(this);
            if (!_globallyPaused && !_ticker.Enabled) _ticker.Start();   // don't un-pause a backgrounded app
        }

        /// <summary>Pause/resume ALL rlottie animation (pause-on-background). Stops the shared ticker so no
        /// frame renders while the app is hidden; resumes it if anything is still active.</summary>
        public static void SetPaused(bool paused)
        {
            _globallyPaused = paused;
            if (paused) _ticker.Stop();
            else lock (_active) { if (_active.Count > 0) _ticker.Start(); }
        }

        private void Step()
        {
            if (_disposed) return;
            double elapsed = unchecked(Environment.TickCount - _startTick) / 1000.0;
            int frame = (int)(elapsed * _clip.Fps) % _clip.TotalFrames;
            if (frame != _lastFrame) Render(frame);
        }

        private void Render(int frame)
        {
            _lastFrame = frame;
            try { _clip.RenderInto(_buffer, frame); } catch { return; }
            _onFrame?.Invoke(_buffer);          // same object each tick → consumer just repaints
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_active) { _active.Remove(this); if (_active.Count == 0) _ticker.Stop(); }
            _clip.Dispose();
            try { _buffer.Dispose(); } catch { }
        }
    }
}
