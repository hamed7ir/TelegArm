using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TL;

namespace TelegArm.Core
{
    /// <summary>How a dropped file is sent.</summary>
    public enum SendMode
    {
        Photo,     // compressed image → Telegram photo
        Video,     // streamable video document (DocumentAttributeVideo)
        Document   // uncompressed, original filename + MIME (send as file)
    }

    /// <summary>
    /// Uploads a local file and sends it to a peer. Mirrors the text-send shape
    /// (TelegramService.SendTextAsync → Client.SendMessageAsync): we upload with
    /// progress, build the InputMedia, then SendMessageAsync(peer, caption, media)
    /// — which wraps Messages_SendMedia + random_id and returns the confirmed Message.
    ///
    /// ARM32-safe: videos/documents stream from the file path via UploadFileAsync
    /// (never read whole into a byte[]); images are compressed to a temp JPEG first.
    /// </summary>
    public static class MediaSender
    {
        private static readonly string[] ImageExt =
            { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff" };
        private static readonly string[] VideoExt =
            { "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "3gp", "flv" };

        public static bool IsImage(string path) => HasExt(path, ImageExt);
        public static bool IsVideo(string path) => HasExt(path, VideoExt);

        private static bool HasExt(string path, string[] set)
        {
            string ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
            return set.Contains(ext);
        }

        /// <summary>
        /// Uploads <paramref name="path"/> and sends it. Returns the confirmed Message.
        /// Progress is reported via <paramref name="progress"/>; cancellation is checked
        /// before/after upload and (best-effort) from inside the progress callback.
        /// </summary>
        public static async Task<Message> SendAsync(
            WTelegram.Client client, InputPeer peer, string path, SendMode mode, string caption,
            WTelegram.Client.ProgressCallback progress, CancellationToken ct)
        {
            var im = await BuildInputMediaAsync(client, path, mode, progress, ct).ConfigureAwait(false);
            return await client.SendMessageAsync(peer, caption ?? "", im).ConfigureAwait(false);
        }

        /// <summary>
        /// Uploads <paramref name="items"/> and sends them as ONE album (grouped_id) via the WTelegram
        /// SendAlbumAsync helper (which does the uploadMedia + sendMultiMedia dance + grouping). The caption
        /// lands on the first item (album-style). Returns the album's Messages (each with the same grouped_id).
        /// Callers chunk to ≤10 items per album per Telegram's limit.
        /// </summary>
        public static async Task<Message[]> SendAlbumAsync(
            WTelegram.Client client, InputPeer peer, IList<AlbumItem> items, string caption,
            Func<int, WTelegram.Client.ProgressCallback> progressFor, CancellationToken ct)
        {
            var medias = new List<InputMedia>();
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var progress = progressFor != null ? progressFor(i) : null;
                medias.Add(await BuildInputMediaAsync(client, items[i].Path, items[i].Mode, progress, ct).ConfigureAwait(false));
            }
            ct.ThrowIfCancellationRequested();
            return await client.SendAlbumAsync(peer, medias, caption ?? "", 0, null, default(DateTime), false).ConfigureAwait(false);
        }

        /// <summary>One album member: a local file + how to send it.</summary>
        public struct AlbumItem
        {
            public string Path;
            public SendMode Mode;
            public AlbumItem(string path, SendMode mode) { Path = path; Mode = mode; }
        }

        /// <summary>Uploads one file (streaming/compress per mode) and returns its InputMedia — WITHOUT sending.</summary>
        private static async Task<InputMedia> BuildInputMediaAsync(
            WTelegram.Client client, string path, SendMode mode, WTelegram.Client.ProgressCallback progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);

            if (mode == SendMode.Photo)
            {
                // Compress off the UI thread; fall back to the original on any failure.
                string temp = await Task.Run(() => CompressImageToTemp(path)).ConfigureAwait(false);
                string upload = temp ?? path;
                try
                {
                    var file = await client.UploadFileAsync(upload, progress).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return new InputMediaUploadedPhoto { file = file };
                }
                finally { if (temp != null) TryDelete(temp); }   // server has the file now; temp no longer needed
            }

            // Video / Document: stream straight from disk.
            var uploaded = await client.UploadFileAsync(path, progress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (mode == SendMode.Video)
            {
                // No re-encoding (no codec deps). We DO probe width/height/duration and grab a
                // poster frame via the bundled LibVLC (best-effort, fully guarded).
                var info = await VideoProbe.ProbeAsync(path, ct).ConfigureAwait(false);

                InputFile thumbFile = null;
                if (info.ThumbPath != null)
                {
                    try { thumbFile = (await client.UploadFileAsync(info.ThumbPath).ConfigureAwait(false)) as InputFile; }
                    catch { thumbFile = null; }
                    finally { TryDelete(info.ThumbPath); }
                }

                var vid = new DocumentAttributeVideo { flags = DocumentAttributeVideo.Flags.supports_streaming };
                if (info.DurationSec > 0) vid.duration = info.DurationSec;
                if (info.Width > 0 && info.Height > 0) { vid.w = info.Width; vid.h = info.Height; }

                var imd = new InputMediaUploadedDocument
                {
                    file = uploaded,
                    mime_type = "video/mp4",
                    attributes = new DocumentAttribute[] { vid, new DocumentAttributeFilename { file_name = name } }
                };
                if (thumbFile != null)
                {
                    imd.thumb = thumbFile;
                    imd.flags |= InputMediaUploadedDocument.Flags.has_thumb;
                }
                return imd;
            }

            // Document — force "send as file" so Telegram doesn't auto-detect it as media.
            return new InputMediaUploadedDocument
            {
                flags = InputMediaUploadedDocument.Flags.force_file,
                file = uploaded,
                mime_type = GetMimeType(path),
                attributes = new DocumentAttribute[] { new DocumentAttributeFilename { file_name = name } }
            };
        }

        /// <summary>Uploads a square MP4 and sends it as a ROUND video message (video note) — single-path,
        /// backend-blind (the recorder produced an identical 384² MP4 via WinRT or VLC). w==h is mandatory for
        /// a round message; a square poster frame is attached as the thumb (best-effort, via the bundled VLC).</summary>
        public static async Task<Message> SendRoundVideoAsync(
            WTelegram.Client client, InputPeer peer, string mp4Path, int durationSec,
            WTelegram.Client.ProgressCallback progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uploaded = await client.UploadFileAsync(mp4Path, progress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var info = await VideoProbe.ProbeAsync(mp4Path, ct).ConfigureAwait(false);
            if (durationSec <= 0 && info.DurationSec > 0) durationSec = info.DurationSec;

            InputFile thumbFile = null;
            if (info.ThumbPath != null)
            {
                try { thumbFile = (await client.UploadFileAsync(info.ThumbPath).ConfigureAwait(false)) as InputFile; }
                catch { thumbFile = null; }
                finally { TryDelete(info.ThumbPath); }
            }

            int side = (info.Width > 0 && info.Width == info.Height) ? info.Width : 384;
            var vid = new DocumentAttributeVideo
            {
                flags = DocumentAttributeVideo.Flags.round_message,
                duration = durationSec > 0 ? durationSec : 1,
                w = side,
                h = side
            };
            var imd = new InputMediaUploadedDocument
            {
                file = uploaded,
                mime_type = "video/mp4",
                attributes = new DocumentAttribute[] { vid }
            };
            if (thumbFile != null) { imd.thumb = thumbFile; imd.flags |= InputMediaUploadedDocument.Flags.has_thumb; }
            return await client.SendMessageAsync(peer, "", imd).ConfigureAwait(false);
        }

        /// <summary>Uploads an OGG/Opus file and sends it as a Telegram voice note (with waveform bars).</summary>
        public static async Task<Message> SendVoiceAsync(
            WTelegram.Client client, InputPeer peer, string oggPath, int durationSec, byte[] waveform, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uploaded = await client.UploadFileAsync(oggPath).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var audio = new DocumentAttributeAudio
            {
                flags = DocumentAttributeAudio.Flags.voice,
                duration = durationSec
            };
            if (waveform != null && waveform.Length > 0)
            {
                audio.waveform = waveform;
                audio.flags |= DocumentAttributeAudio.Flags.has_waveform;
            }

            var im = new InputMediaUploadedDocument
            {
                file = uploaded,
                mime_type = "audio/ogg",
                attributes = new DocumentAttribute[] { audio }
            };
            return await client.SendMessageAsync(peer, "", im).ConfigureAwait(false);
        }

        /// <summary>MIME type by extension (small known set; default octet-stream).</summary>
        public static string GetMimeType(string filePath)
        {
            switch ((Path.GetExtension(filePath) ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "jpg": case "jpeg": return "image/jpeg";
                case "png": return "image/png";
                case "gif": return "image/gif";
                case "mp4": return "video/mp4";
                case "mov": return "video/quicktime";
                case "pdf": return "application/pdf";
                case "doc": case "docx": return "application/msword";
                default: return "application/octet-stream";
            }
        }

        /// <summary>
        /// Resizes (long side ≤ 1280) and re-encodes an image to a temp JPEG for "compress
        /// and send". Returns the temp path, or null to fall back to the original file.
        /// Decodes the source once into a copy, then disposes everything (memory-safe).
        /// </summary>
        private static string CompressImageToTemp(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var src = Image.FromStream(fs))
                {
                    const int maxDim = 1280;
                    double scale = Math.Min(1.0, (double)maxDim / Math.Max(src.Width, src.Height));
                    int w = Math.Max(1, (int)(src.Width * scale));
                    int h = Math.Max(1, (int)(src.Height * scale));

                    using (var bmp = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, w, h));
                        }

                        var encoder = ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        string temp = Path.Combine(Path.GetTempPath(),
                            "telegarm_" + Guid.NewGuid().ToString("N") + ".jpg");

                        if (encoder != null)
                            using (var ep = new EncoderParameters(1))
                            {
                                ep.Param[0] = new EncoderParameter(Encoder.Quality, 82L);
                                bmp.Save(temp, encoder, ep);
                            }
                        else
                            bmp.Save(temp, ImageFormat.Jpeg);

                        return temp;
                    }
                }
            }
            catch { return null; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup is best-effort */ }
        }
    }
}
