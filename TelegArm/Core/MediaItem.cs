using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// A viewable media attachment. Photos carry their data in <see cref="Bytes"/>;
    /// large media (video/documents) are streamed to <see cref="LocalPath"/> on disk.
    /// </summary>
    public class MediaItem
    {
        public long Id { get; set; }              // media id (photo/document)
        public int MessageId { get; set; }        // owning Telegram message id
        public byte[] Bytes { get; set; }         // in-memory data (photos)
        public string LocalPath { get; set; }     // on-disk path (video/documents)
        public string Type { get; set; }          // "photo","video","gif","document","voice"
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Duration { get; set; }         // seconds
        public bool IsRound { get; set; }          // round video message (video note) — render/play clipped to a circle
        public string MimeType { get; set; }
        public MessageMedia RawMedia { get; set; } // for re-download in later phases
    }
}
