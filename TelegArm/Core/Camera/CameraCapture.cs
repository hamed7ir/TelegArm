using System;
using System.Threading.Tasks;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// Chooses the camera backend: WinRT MediaCapture FIRST (the default everywhere it works — RT 8.1 +
    /// Win8/8.1/10/11 + the dev box, so dev-box testing exercises the EXACT code RT runs), libVLC DirectShow
    /// as the automatic fallback (Windows 7 / broken WinRT — the capability probe handles it, NOT an OS check).
    /// The chosen backend is CACHED for the session (the choice, not the camera — each open gets a fresh,
    /// ready recorder). <see cref="Forced"/> overrides it for testing the fallback on demand.
    /// </summary>
    public static class CameraCapture
    {
        private static string _cachedBackend;   // "winrt" | "vlc" | "none" | null(=unprobed)

        /// <summary>Debug override (session-only, set from Settings): "winrt" / "vlc" forces a backend so the
        /// rarely-run VLC/Win7 path can be exercised on the dev box.</summary>
        public static string Forced;

        /// <summary>Creates a ready recorder using the selected/cached backend, or null if none works.</summary>
        public static async Task<ICameraRecorder> CreateAsync()
        {
            if (Forced == "vlc") { System.Diagnostics.Debug.WriteLine("[ROUND-REC] backend FORCED=vlc"); return await MakeVlcAsync().ConfigureAwait(false); }
            if (Forced == "winrt") { System.Diagnostics.Debug.WriteLine("[ROUND-REC] backend FORCED=winrt"); return await MakeWinRtAsync().ConfigureAwait(false); }

            if (_cachedBackend == "winrt") { var r = await MakeWinRtAsync().ConfigureAwait(false); if (r != null) return r; }
            else if (_cachedBackend == "vlc") { var r = await MakeVlcAsync().ConfigureAwait(false); if (r != null) return r; }
            else if (_cachedBackend == "none") return null;

            // First open (or the cached backend failed): probe WinRT-first, then VLC; cache the winner.
            var w = await MakeWinRtAsync().ConfigureAwait(false);
            if (w != null) { _cachedBackend = "winrt"; System.Diagnostics.Debug.WriteLine("[ROUND-REC] backend selected=winrt"); return w; }
            var v = await MakeVlcAsync().ConfigureAwait(false);
            if (v != null) { _cachedBackend = "vlc"; System.Diagnostics.Debug.WriteLine("[ROUND-REC] backend selected=vlc"); return v; }
            _cachedBackend = "none";
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] no camera backend available → recording unavailable");
            return null;
        }

        private static async Task<ICameraRecorder> MakeWinRtAsync()
        {
            WinRtCameraRecorder r = null;
            try { r = new WinRtCameraRecorder(); if (await r.ProbeAsync().ConfigureAwait(false)) return r; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT make failed: " + ex.Message); }
            if (r != null) try { r.Dispose(); } catch { }
            return null;
        }

        private static async Task<ICameraRecorder> MakeVlcAsync()
        {
            VlcCameraRecorder r = null;
            try { r = new VlcCameraRecorder(); if (await r.ProbeAsync().ConfigureAwait(false)) return r; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC make failed: " + ex.Message); }
            if (r != null) try { r.Dispose(); } catch { }
            return null;
        }
    }
}
