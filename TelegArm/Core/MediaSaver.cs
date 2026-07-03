using System;
using System.IO;
using System.Threading.Tasks;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// Shared save/download helpers for a <see cref="MediaItem"/>, used by both the media viewer and
    /// the profile gallery so the two paths stay identical (download-to-cache → copy / Save As filter).
    /// </summary>
    public static class MediaSaver
    {
        /// <summary>Ensures the item's bytes/file are present locally, downloading to cache if needed.</summary>
        public static async Task<bool> EnsureLocalAsync(MediaItem item, TelegramService service)
        {
            if (item == null) return false;
            if (!string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath)) return true;
            if (item.Bytes != null && item.Bytes.Length > 0) return true;
            try
            {
                if (item.RawMedia is MessageMediaPhoto mp && mp.photo is Photo ph)
                {
                    var (bytes, path) = await service.DownloadPhotoAsync(ph);
                    item.Bytes = bytes; item.LocalPath = path;
                    return (bytes != null && bytes.Length > 0) || (!string.IsNullOrEmpty(path) && File.Exists(path));
                }
                if (item.RawMedia is MessageMediaDocument md && md.document is Document doc)
                {
                    string path = MediaCache.MediaPath(MediaCache.CacheFileName(item.Type, item.Id, item.FileName));
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        await service.DownloadDocumentToFileAsync(doc, path);
                    item.LocalPath = path;
                    return File.Exists(path);
                }
            }
            catch { }
            return false;
        }

        /// <summary>Copies the already-local item bytes/file to <paramref name="target"/>.</summary>
        public static bool Write(MediaItem item, string target)
        {
            if (!string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath))
            {
                File.Copy(item.LocalPath, target, true);
                return true;
            }
            if (item.Bytes != null) { File.WriteAllBytes(target, item.Bytes); return true; }
            return false;
        }

        public static string FilterFor(string type)
        {
            switch (type)
            {
                case "photo": return "Image Files|*.jpg;*.png|All Files|*.*";
                case "video":
                case "gif": return "Video Files|*.mp4;*.mkv|All Files|*.*";
                default: return "All Files|*.*";
            }
        }

        public static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
