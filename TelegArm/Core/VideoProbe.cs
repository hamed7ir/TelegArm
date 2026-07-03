using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace TelegArm.Core
{
    /// <summary>Width/height/duration + an optional poster thumbnail for a local video.</summary>
    public sealed class VideoInfo
    {
        public int Width;
        public int Height;
        public int DurationSec;
        public string ThumbPath;   // temp JPEG the caller uploads then deletes (null if none)
    }

    /// <summary>
    /// Probes a local video for dimensions/duration and grabs a poster frame, using the
    /// already-bundled LibVLC (no new codecs — playback working proves decode works).
    /// Everything is best-effort and fully guarded: any failure/timeout returns an empty
    /// <see cref="VideoInfo"/> so the caller just sends the video as before. ARM32-safe —
    /// only one frame is decoded, never the whole file into memory.
    /// </summary>
    public static class VideoProbe
    {
        public static async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var info = new VideoInfo();
            if (!VlcEnvironment.IsAvailable || !VlcEnvironment.TryInitialize()) return info;

            LibVLC libvlc = null;
            Media media = null;
            MediaPlayer mp = null;
            string dir = null;
            try
            {
                dir = Path.Combine(Path.GetTempPath(), "tgvthumb_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);

                libvlc = new LibVLC("--no-audio", "--quiet");
                media = new Media(libvlc, path, FromType.FromPath);
                // Headless frame grab: the "scene" video filter writes decoded frames to disk;
                // a dummy vout means no window is needed.
                media.AddOption(":no-audio");
                media.AddOption(":avcodec-hw=none");
                media.AddOption(":video-filter=scene");
                media.AddOption(":scene-format=png");
                media.AddOption(":scene-prefix=f");
                media.AddOption(":scene-ratio=12");
                media.AddOption(":scene-path=" + dir);
                media.AddOption(":vout=dummy");

                mp = new MediaPlayer(media) { Mute = true };
                mp.Play();

                // Wait up to ~5s for a few frames + a known length.
                for (int i = 0; i < 50; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(100).ConfigureAwait(false);
                    if (info.DurationSec == 0 && mp.Length > 0) info.DurationSec = (int)(mp.Length / 1000);
                    int frames = SafeCount(dir);
                    if (frames >= 3 && info.DurationSec > 0) break;
                    if (frames >= 6) break;
                }

                ReadDimensions(media, info);
                if (info.DurationSec == 0 && mp.Length > 0) info.DurationSec = (int)(mp.Length / 1000);

                try { mp.Stop(); } catch { }

                // Pick a frame a little past the start (less likely to be black) and downscale it.
                var pngs = SafeFiles(dir);
                if (pngs.Length > 0)
                    info.ThumbPath = DownscaleToTemp(pngs[Math.Min(pngs.Length - 1, 2)], 320);
            }
            catch { /* best-effort: empty info → caller sends the video unchanged */ }
            finally
            {
                try { mp?.Dispose(); } catch { }
                try { media?.Dispose(); } catch { }
                try { libvlc?.Dispose(); } catch { }
                try { if (dir != null) Directory.Delete(dir, true); } catch { }
            }
            return info;
        }

        private static void ReadDimensions(Media media, VideoInfo info)
        {
            try
            {
                var tracks = media.Tracks;
                if (tracks == null) return;
                foreach (var t in tracks)
                    if (t.TrackType == TrackType.Video)
                    {
                        info.Width = (int)t.Data.Video.Width;
                        info.Height = (int)t.Data.Video.Height;
                        return;
                    }
            }
            catch { }
        }

        private static int SafeCount(string dir)
        {
            try { return Directory.GetFiles(dir, "*.png").Length; } catch { return 0; }
        }

        private static string[] SafeFiles(string dir)
        {
            try { var f = Directory.GetFiles(dir, "*.png"); Array.Sort(f); return f; } catch { return new string[0]; }
        }

        private static string DownscaleToTemp(string srcPng, int maxW)
        {
            try
            {
                using (var fs = File.OpenRead(srcPng))
                using (var src = Image.FromStream(fs))
                {
                    double scale = Math.Min(1.0, (double)maxW / src.Width);
                    int w = Math.Max(1, (int)(src.Width * scale));
                    int h = Math.Max(1, (int)(src.Height * scale));
                    using (var bmp = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, w, h));
                        }
                        string outp = Path.Combine(Path.GetTempPath(), "tgvthumb_" + Guid.NewGuid().ToString("N") + ".jpg");
                        bmp.Save(outp, ImageFormat.Jpeg);
                        return outp;
                    }
                }
            }
            catch { return null; }
        }
    }
}
