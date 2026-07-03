using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace TelegArm.Core
{
    /// <summary>
    /// PROBE-ONLY (WEBM-probe batch): is rendering Telegram WebM video stickers (VP9 + alpha) feasible on this
    /// libVLC/arch? Decodes ONE frame of a real .webm sticker through libVLC's raw video-frame callbacks
    /// (SetVideoFormat + SetVideoCallbacks) requesting a 32-bit format, then inspects the ALPHA channel: real
    /// per-pixel transparency (alpha varies — transparent corners ~0, opaque shape ~255) vs DROPPED (constant
    /// = VLC treats the video as opaque). Two passes: RGBA (alpha test) then, if RGBA yields nothing, RV32
    /// (does VP9 decode AT ALL?). Logs the verdict under [WEBM]. Does NOT render/loop/cache — feasibility only.
    /// Runs once per session on a background thread (never touches the UI). The full render is a separate batch
    /// gated on this answer.
    /// </summary>
    public static class WebmAlphaProbe
    {
        private static int _ran;   // run-once latch (Interlocked)

        public static void ProbeOnce(string webmPath, int w, int h)
        {
            if (Interlocked.Exchange(ref _ran, 1) != 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Probe(webmPath, w, h); }
                catch (Exception ex) { Log("probe EXCEPTION: " + ex); }
            });
        }

        private static void Probe(string path, int w, int h)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log("no webm file: " + path); return; }
            if (!VlcEnvironment.IsAvailable || !VlcEnvironment.TryInitialize())
            { Log("libVLC NOT available — can't probe (extraction/init failed). VERDICT: unknown (no libVLC)"); return; }
            if (w <= 0 || w > 2048) w = 512;
            if (h <= 0 || h > 2048) h = 512;
            Log("probe START file=" + Path.GetFileName(path) + " dims=" + w + "x" + h
                + " arch=" + (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?"));

            byte[] rgba = DecodeOneFrame(path, "RGBA", w, h);
            if (rgba != null) { AnalyzeAlpha(rgba, w, h); return; }

            Log("RGBA yielded no frame → retry RV32 to confirm VP9 decodes at all");
            byte[] rv32 = DecodeOneFrame(path, "RV32", w, h);
            if (rv32 != null)
                Log("VERDICT: VP9 DECODES (RV32, opaque) but RGBA/alpha is NOT exposed on this arch → "
                    + "fallback = render OPAQUE (no transparency) or a static first-frame");
            else
                Log("VERDICT: VP9 does NOT decode on this libVLC/arch → WebM stickers BLOCKED here "
                    + "(fallback = the representative emoji, as today)");
        }

        /// <summary>Decodes a single frame into a pinned buffer of the requested chroma; null if no frame in 6s.</summary>
        private static byte[] DecodeOneFrame(string path, string chroma, int w, int h)
        {
            int stride = w * 4;
            byte[] buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            IntPtr bufPtr = handle.AddrOfPinnedObject();
            bool frameReady = false;

            // Keep the delegates rooted for the whole play (native holds the function pointers).
            MediaPlayer.LibVLCVideoLockCb lockCb = (opaque, planes) => { Marshal.WriteIntPtr(planes, bufPtr); return bufPtr; };
            MediaPlayer.LibVLCVideoUnlockCb unlockCb = (opaque, picture, planes) => { };
            MediaPlayer.LibVLCVideoDisplayCb displayCb = (opaque, picture) => { frameReady = true; };

            LibVLC libvlc = null; MediaPlayer mp = null; Media media = null;
            try
            {
                libvlc = new LibVLC("--no-audio", "--quiet", "--no-stats", "--no-osd");
                mp = new MediaPlayer(libvlc);
                mp.SetVideoFormat(chroma, (uint)w, (uint)h, (uint)stride);
                mp.SetVideoCallbacks(lockCb, unlockCb, displayCb);
                media = new Media(libvlc, path, FromType.FromPath);
                mp.Media = media;
                mp.Play();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!frameReady && sw.ElapsedMilliseconds < 6000) Thread.Sleep(25);

                Log(chroma + ": " + (frameReady ? "FRAME in " + sw.ElapsedMilliseconds + "ms state=" + mp.State
                                                 : "NO frame (6s timeout) state=" + mp.State));
                if (!frameReady) return null;
                byte[] frame = new byte[buf.Length];
                Buffer.BlockCopy(buf, 0, frame, 0, buf.Length);   // copy before another frame overwrites it
                return frame;
            }
            finally
            {
                try { if (mp != null) mp.Stop(); } catch { }
                try { if (media != null) media.Dispose(); } catch { }
                try { if (mp != null) mp.Dispose(); } catch { }   // fully stops callbacks before we free the buffer
                try { if (libvlc != null) libvlc.Dispose(); } catch { }
                if (handle.IsAllocated) handle.Free();
                GC.KeepAlive(lockCb); GC.KeepAlive(unlockCb); GC.KeepAlive(displayCb);
            }
        }

        /// <summary>Inspects the alpha byte (offset 3 of each pixel) — REAL (varies) vs DROPPED (constant).</summary>
        private static void AnalyzeAlpha(byte[] f, int w, int h)
        {
            int stride = w * 4;
            int nonZeroRgb = 0; int aMin = 255, aMax = 0; int samples = 0;
            for (int y = 0; y < h; y += 4)
                for (int x = 0; x < w; x += 4)
                {
                    int i = y * stride + x * 4;
                    if (i + 3 >= f.Length) continue;
                    if (f[i] != 0 || f[i + 1] != 0 || f[i + 2] != 0) nonZeroRgb++;
                    int a = f[i + 3];
                    if (a < aMin) aMin = a;
                    if (a > aMax) aMax = a;
                    samples++;
                }
            int cornerA = f.Length > 3 ? f[3] : -1;                                   // (0,0) — usually transparent
            int ci = (h / 2) * stride + (w / 2) * 4 + 3;
            int centerA = ci < f.Length ? f[ci] : -1;                                 // center — usually opaque shape

            bool decoded = nonZeroRgb > 0;
            bool alphaReal = aMin != aMax;
            Log("VP9 decode: " + (decoded ? "OK (" + nonZeroRgb + "/" + samples + " sampled px have colour)" : "frame all-zero")
                + " | alpha: " + (alphaReal ? "REAL (varies)" : "DROPPED (constant)")
                + " aMin=" + aMin + " aMax=" + aMax
                + " | corner(0,0).A=" + cornerA + " center(" + (w / 2) + "," + (h / 2) + ").A=" + centerA);
            Log(alphaReal
                ? "VERDICT: alpha is USABLE → WebM stickers FEASIBLE on this arch (build the full render next)"
                : "VERDICT: alpha DROPPED (opaque) → WebM transparency NOT exposed by this libVLC on this arch");
        }

        private static void Log(string s) { System.Diagnostics.Debug.WriteLine("[WEBM] " + s); }
    }
}
