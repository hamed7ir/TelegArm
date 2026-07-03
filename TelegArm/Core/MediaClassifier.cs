using System.Linq;
using TL;

namespace TelegArm.Core
{
    /// <summary>Maps a Telegram message's media to a UI-friendly <see cref="MediaItem"/>.</summary>
    public static class MediaClassifier
    {
        /// <summary>Returns a MediaItem for the message, or null when it has no displayable media.</summary>
        public static MediaItem FromMessage(Message m)
        {
            var media = m?.media;

            if (media is MessageMediaPhoto mmp && mmp.photo is Photo p)
            {
                var ps = p.LargestPhotoSize;
                return new MediaItem
                {
                    Id = p.id,
                    MessageId = m.ID,
                    Type = "photo",
                    FileName = "photo_" + p.id + ".jpg",
                    FileSize = ps?.FileSize ?? 0,
                    Width = ps?.Width ?? 0,
                    Height = ps?.Height ?? 0,
                    MimeType = "image/jpeg",
                    RawMedia = media
                };
            }

            if (media is MessageMediaDocument mmd && mmd.document is Document d)
                return FromDocument(m.ID, media, d);

            return null;
        }

        private static MediaItem FromDocument(int messageId, MessageMedia media, Document d)
        {
            var attrs = d.attributes ?? new DocumentAttribute[0];

            string type = "document";
            int w = 0, h = 0, duration = 0;
            bool isRound = false;

            // A GIF/animation: the standard marker is DocumentAttributeAnimated; also treat a raw image/gif
            // as a GIF (some sources send that shape, which official Telegram plays as an animation too).
            if (attrs.OfType<DocumentAttributeAnimated>().Any() || d.mime_type == "image/gif")
                type = "gif";

            var audio = attrs.OfType<DocumentAttributeAudio>().FirstOrDefault();
            if (audio != null)
            {
                duration = audio.duration;
                type = (audio.flags & DocumentAttributeAudio.Flags.voice) != 0 ? "voice" : "audio";
            }

            var video = attrs.OfType<DocumentAttributeVideo>().FirstOrDefault();
            if (video != null)
            {
                if (type != "gif") type = "video";
                w = video.w; h = video.h; duration = (int)video.duration;
                isRound = (video.flags & DocumentAttributeVideo.Flags.round_message) != 0;   // round "video note"
            }

            var img = attrs.OfType<DocumentAttributeImageSize>().FirstOrDefault();
            if (img != null && w == 0) { w = img.w; h = img.h; }

            var fn = attrs.OfType<DocumentAttributeFilename>().FirstOrDefault();
            string fileName = !string.IsNullOrEmpty(fn?.file_name) ? fn.file_name : "file_" + d.id;

            return new MediaItem
            {
                Id = d.id,
                MessageId = messageId,
                Type = type,
                FileName = fileName,
                FileSize = d.size,
                Width = w,
                Height = h,
                Duration = duration,
                IsRound = isRound,
                MimeType = d.mime_type,
                RawMedia = media
            };
        }
    }
}
