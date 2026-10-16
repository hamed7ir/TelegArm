using System;
using System.Threading.Tasks;
using TelegArm.Helpers;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// Picks the round-video camera backend (2026-10-16):
    ///  1. Windows 10 1607+: <see cref="FrameReaderCameraRecorder"/>, a live ~30 fps preview and a square crop
    ///     (MediaFrameReader, the Venetium camera handoff);
    ///  2. Windows 8.1: <see cref="WinRtCameraRecorder"/>, the photo-poll preview (MediaFrameReader does not exist there).
    /// The libVLC DirectShow fallback (Windows 7) went with libVLC. A failed probe is NOT remembered for the session (the
    /// handoff: the camera may be busy, or privacy may be switched on later); the backend that worked is.
    /// </summary>
    public static class CameraCapture
    {
        private static string _cachedBackend;   // "framereader" | "winrt" | null (= probe again)

        /// <summary>Debug override (session-only): "winrt" forces the 8.1 photo backend, "framereader" the Windows 10 one.</summary>
        public static string Forced;

        /// <summary>Does this Windows have MediaFrameReader (Windows 10 1607+), and the FFmpeg shim the preview converts with?
        /// Asked by NAME, so on 8.1 no Windows 10 type is resolved; and it lives here, in a class that names no
        /// TelegArm.Camera10 type, so answering it never loads anything that does.</summary>
        public static bool FrameReaderSupported
        {
            get
            {
                try
                {
                    return Type.GetType("Windows.Media.Capture.Frames.MediaFrameReader, Windows, ContentType=WindowsRuntime") != null
                           && Media.Tgav.Available && Media.Tgav.HasScaler;
                }
                catch { return false; }
            }
        }

        /// <summary>Does this Windows have MediaFrameReader (Windows 10 1607+)? The call camera's test (it needs no FFmpeg):
        /// by name, as above.</summary>
        public static bool FrameReaderPresent
        {
            get
            {
                try { return Type.GetType("Windows.Media.Capture.Frames.MediaFrameReader, Windows, ContentType=WindowsRuntime") != null; }
                catch { return false; }
            }
        }

        /// <summary>Creates a ready recorder using the selected/cached backend, or null if none works.</summary>
        public static async Task<ICameraRecorder> CreateAsync()
        {
            bool frames = FrameReaderSupported;
            Logger.Diag("[ROUND-REC] frame reader supported=" + frames + " forced=" + (Forced ?? "-") + " cached=" + (_cachedBackend ?? "-"));
            if (Forced == "winrt") return await MakeWinRtAsync().ConfigureAwait(false);
            if (Forced == "framereader") return frames ? await MakeFrameReaderAsync().ConfigureAwait(false) : null;

            if (_cachedBackend == "winrt") { var r = await MakeWinRtAsync().ConfigureAwait(false); if (r != null) return r; }
            if (frames && _cachedBackend != "winrt")
            {
                var f = await MakeFrameReaderAsync().ConfigureAwait(false);
                if (f != null) { _cachedBackend = "framereader"; Logger.Diag("[ROUND-REC] backend selected=framereader"); return f; }
            }
            var w = await MakeWinRtAsync().ConfigureAwait(false);
            if (w != null) { _cachedBackend = "winrt"; Logger.Diag("[ROUND-REC] backend selected=winrt"); return w; }
            _cachedBackend = null;
            Logger.Diag("[ROUND-REC] no camera backend available → recording unavailable (probed again next time)");
            return null;
        }

        /// <summary>Only reached when <see cref="FrameReaderSupported"/>: the one place that names the Windows 10 backend.</summary>
        private static async Task<ICameraRecorder> MakeFrameReaderAsync()
        {
            FrameReaderCameraRecorder r = null;
            try { r = new FrameReaderCameraRecorder(); if (await r.ProbeAsync().ConfigureAwait(false)) return r; }
            catch (Exception ex) { Logger.Diag("[ROUND-REC] frame-reader make failed: " + ex.GetType().Name + " " + ex.Message); }
            if (r != null) try { r.Dispose(); } catch { }
            return null;
        }

        private static async Task<ICameraRecorder> MakeWinRtAsync()
        {
            WinRtCameraRecorder r = null;
            try { r = new WinRtCameraRecorder(); if (await r.ProbeAsync().ConfigureAwait(false)) return r; }
            catch (Exception ex) { Logger.Diag("[ROUND-REC] WinRT make failed: " + ex.Message); }
            if (r != null) try { r.Dispose(); } catch { }
            return null;
        }
    }
}
