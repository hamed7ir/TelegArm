using System.Diagnostics;

namespace TelegArm.Core
{
    /// <summary>
    /// The single gate for AUTOMATIC full-media downloads (on receive/render). Thumbnails are always
    /// automatic and never pass through here — only the FULL file of a given type is gated. Each media
    /// type maps to its AppSettings toggle; an enabled type also honors MaxAutoDownloadSizeMB. Defaults are
    /// conservative for RT (everything OFF) — nothing heavy auto-downloads until the user opts in.
    /// Explicit user actions (tap to play / open in viewer) bypass this entirely.
    /// </summary>
    public static class MediaPolicy
    {
        /// <summary>True if the FULL media of <paramref name="type"/> (a MediaClassifier type:
        /// photo/video/gif/voice/audio/document) may auto-download now, given its size.</summary>
        public static bool ShouldAutoDownload(string type, long sizeBytes)
        {
            var s = AppSettings.Instance;
            bool typeOk;
            switch (type)
            {
                case "photo": typeOk = s.AutoDownloadPhotos; break;
                case "video": typeOk = s.AutoDownloadVideos; break;
                case "gif": typeOk = s.AutoDownloadGifs; break;
                case "voice": typeOk = s.AutoDownloadVoice; break;
                case "audio": typeOk = s.AutoDownloadAudio; break;
                case "document": typeOk = s.AutoDownloadDocuments; break;
                default: typeOk = false; break;
            }
            if (!typeOk)
            {
                if (TelegArm.Helpers.Logger.Enabled) Debug.WriteLine("[MEDIA] auto policy: skip " + type + " (toggle off)");
                return false;
            }
            long max = (long)s.MaxAutoDownloadSizeMB * 1024 * 1024;
            bool sizeOk = sizeBytes > 0 && sizeBytes <= max;
            if (!sizeOk && TelegArm.Helpers.Logger.Enabled) Debug.WriteLine("[MEDIA] auto policy: skip " + type + " (size " + sizeBytes + " > max)");
            return sizeOk;
        }
    }
}
