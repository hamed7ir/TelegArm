using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using TelegArm.Core.Media;

namespace TelegArm.SelfTest
{
    /// <summary>
    /// 2026-10-16 — the FFmpeg media engine (ffmpeg-rt + the tgav shim) that replaced libVLC, run for real in this
    /// process's architecture (x86 on the dev box; the ARM32 build is the device's to run). No user media is used: the voice
    /// note is made here with Concentus, the app's own Opus encoder, and the video is ffmpeg-rt's synthetic test clip.
    /// </summary>
    internal static class MediaTests
    {
        /// <summary>ffmpeg-rt's 640x360 H.264 test pattern (900 frames, 30 fps). Override with TELEGARM_TEST_CLIP.</summary>
        private static string ClipPath
        {
            get { return Environment.GetEnvironmentVariable("TELEGARM_TEST_CLIP") ?? @"D:\repo\ffmpeg-rt\dist\device-trip\h264_360p.mp4"; }
        }

        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("media: FFmpeg (tgav) loads for this process's architecture", Loads));
            t.Add(new KeyValuePair<string, Action>("media: an OGG/Opus voice note decodes whole; at 2x it is half as long and keeps its pitch", Opus));
            t.Add(new KeyValuePair<string, Action>("media: an H.264 clip decodes at a requested size, in order; a seek lands on its target", Video));
        }

        private static void Loads()
        {
            Check.True(Tgav.Available, "tgav loaded (" + Tgav.Status + ", " + (Environment.Is64BitProcess ? "64" : "32") + "-bit process)");
            Check.True(Tgav.Status.Contains("avcodec 62."), "FFmpeg 8 (avcodec 62): " + Tgav.Status);
        }

        // ── audio ────────────────────────────────────────────────────────────────────────────────────────────────────

        private static string MakeVoiceNote(double seconds, int hz)
        {
            string path = Path.Combine(Path.GetTempPath(), "tgav_selftest_" + Guid.NewGuid().ToString("N") + ".ogg");
            using (var file = File.Create(path))
            {
                var enc = OpusEncoder.Create(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);   // as VoiceRecorder
                enc.Bitrate = 24000;
                var ogg = new OpusOggWriteStream(enc, file);
                int n = (int)(seconds * 48000);
                var pcm = new short[n];
                for (int i = 0; i < n; i++) pcm[i] = (short)(12000 * Math.Sin(2 * Math.PI * hz * i / 48000.0));
                ogg.WriteSamples(pcm, 0, n);
                ogg.Finish();
            }
            return path;
        }

        /// <summary>All the audio, mono 48 kHz: the sample count and the zero crossings per second (2 x the pitch).</summary>
        private static void ReadAll(MediaFile f, out int samples, out double crossingsPerSec)
        {
            var buf = new short[4096];
            samples = 0;
            int crossings = 0, guard = 0;
            short prev = 0;
            for (;;)
            {
                int pts, n = f.ReadAudio(buf, buf.Length, out pts);
                Check.True(n >= 0, "audio read " + (n < 0 ? Tgav.ErrorText(n) : "ok"));
                if (n == 0) break;
                for (int i = 0; i < n; i++)
                {
                    short s = buf[i];
                    if ((prev < 0 && s >= 0) || (prev >= 0 && s < 0)) crossings++;
                    prev = s;
                }
                samples += n;
                if (++guard > 10000) throw new Exception("audio never ended");
            }
            crossingsPerSec = samples > 0 ? crossings * 48000.0 / samples : 0;
        }

        private static void Opus()
        {
            string path = MakeVoiceNote(3.0, 440);
            try
            {
                string err;
                using (var f = MediaFile.Open(path, Tgav.NoVideo, out err))
                {
                    Check.True(f != null, "opened (" + err + ")");
                    Check.True(f.HasAudio && !f.HasVideo, "one audio stream, no video");
                    Check.Equal(48000, f.Info.SampleRate, "stream rate");
                    Check.True(Math.Abs(f.Info.DurationMs - 3000) <= 100, "duration " + f.Info.DurationMs + " ms (3000 +- 100)");
                    Check.Equal(0, f.SetAudioFormat(48000, 1), "mono 48 kHz out");

                    int n1; double c1;
                    ReadAll(f, out n1, out c1);
                    Check.True(Math.Abs(n1 - 144000) <= 144000 * 2 / 100, "1x: " + n1 + " samples (144000 +- 2%)");
                    Check.True(Math.Abs(c1 - 880) <= 880 * 5 / 100, "1x: " + c1.ToString("0") + " crossings/s (440 Hz = 880 +- 5%)");

                    Check.Equal(0, f.Seek(0), "seek to the start");
                    Check.Equal(0, f.SetTempo(200), "tempo 200%");
                    int n2; double c2;
                    ReadAll(f, out n2, out c2);
                    Check.True(Math.Abs(n2 - 72000) <= 72000 * 4 / 100, "2x: " + n2 + " samples (72000 +- 4%)");
                    // a plain 2x resample would read 1760: atempo keeps the pitch
                    Check.True(Math.Abs(c2 - 880) <= 880 * 6 / 100, "2x: " + c2.ToString("0") + " crossings/s (pitch kept: 880 +- 6%)");
                }
            }
            finally { try { File.Delete(path); } catch { } }
        }

        // ── video ────────────────────────────────────────────────────────────────────────────────────────────────────

        private static void Video()
        {
            string path = ClipPath;
            if (!File.Exists(path)) { Console.WriteLine("        (no test clip at " + path + ": decode not exercised)"); return; }
            string err;
            using (var f = MediaFile.Open(path, Tgav.NoAudio, out err))
            {
                Check.True(f != null, "opened (" + err + ")");
                Check.True(f.HasVideo, "a video stream");
                Check.Equal(640, f.Info.Width, "width");
                Check.Equal(360, f.Info.Height, "height");
                Check.Equal(0, f.Info.Rotation, "rotation");

                const int W = 320, H = 180;
                var px = new byte[W * H * 4];
                var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
                try
                {
                    int last = -1, distinct = 0;
                    for (int i = 0; i < 30; i++)
                    {
                        int pts, r = f.ReadVideo(pin.AddrOfPinnedObject(), W * 4, W, H, out pts);
                        Check.Equal(1, r, "frame " + i);
                        Check.True(pts > last, "frame " + i + " pts " + pts + " after " + last);
                        last = pts;
                    }
                    for (int i = 4; i < px.Length; i += 4 * 97) if (px[i] != px[0] || px[i + 1] != px[1]) distinct++;
                    Check.True(distinct > 10, "the frame has a picture (" + distinct + " sampled pixels differ from the first)");
                    Check.Equal(255, (int)px[3], "BGRA alpha is opaque");

                    Check.Equal(0, f.Seek(5000), "seek 5 s");
                    int sp, sr = f.ReadVideo(pin.AddrOfPinnedObject(), W * 4, W, H, out sp);
                    Check.Equal(1, sr, "frame after the seek");
                    Check.True(sp > 5000 - 40 && sp <= 5000, "seek landed at " + sp + " ms (the frame showing at 5000)");

                    Check.Equal(0, f.Seek(0), "seek back to 0");
                    int count = 0, p;
                    while (f.ReadVideo(IntPtr.Zero, 0, 0, 0, out p) == 1) count++;   // decode-only: the drop path
                    Check.Equal(900, count, "every frame, decode-only");
                }
                finally { pin.Free(); }
            }
        }
    }
}
