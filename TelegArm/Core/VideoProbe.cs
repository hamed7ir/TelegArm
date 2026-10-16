using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TelegArm.Core.Media;

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
    /// Probes a local video for its dimensions and duration and grabs a poster frame (2026-10-16: FFmpeg through the tgav
    /// shim; was libVLC's scene filter writing PNGs to a temp folder). Best-effort and fully guarded: any failure returns
    /// an empty <see cref="VideoInfo"/> and the caller sends the video as before. One frame is decoded, at thumbnail size.
    /// </summary>
    public static class VideoProbe
    {
        private const int ThumbMaxWidth = 320;

        public static Task<VideoInfo> ProbeAsync(string path, CancellationToken ct)
        {
            return Task.Run(() => Probe(path, ct));
        }

        private static VideoInfo Probe(string path, CancellationToken ct)
        {
            var info = new VideoInfo();
            try
            {
                string err;
                using (var f = MediaFile.Open(path, Tgav.NoAudio, out err))
                {
                    if (f == null) return info;
                    info.DurationSec = f.Info.DurationMs / 1000;
                    if (!f.HasVideo) return info;
                    info.Width = f.Info.Width;
                    info.Height = f.Info.Height;
                    if (ct.IsCancellationRequested || info.Width <= 0 || info.Height <= 0) return info;
                    // a frame a little past the start (less likely to be black), as before
                    if (f.Info.DurationMs > 2000) f.Seek(1000);
                    double s = Math.Min(1.0, (double)ThumbMaxWidth / info.Width);
                    int w = Math.Max(2, (int)(info.Width * s)) & ~1, h = Math.Max(2, (int)(info.Height * s)) & ~1;
                    using (var bmp = UI.Controls.WebmAnimator.ReadBitmap(f, w, h))
                    {
                        if (bmp == null) return info;
                        string outp = Path.Combine(Path.GetTempPath(), "tgvthumb_" + Guid.NewGuid().ToString("N") + ".jpg");
                        bmp.Save(outp, ImageFormat.Jpeg);
                        info.ThumbPath = outp;
                    }
                }
            }
            catch { /* best-effort: empty info → the caller sends the video unchanged */ }
            return info;
        }
    }
}
