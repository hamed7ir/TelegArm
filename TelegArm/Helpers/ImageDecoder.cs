using System.Drawing;
using System.IO;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Decodes image bytes to a <see cref="Bitmap"/>. Tries System.Drawing first (JPEG/PNG/GIF/BMP),
    /// then falls back to ImageSharp for formats GDI+ can't read — notably WEBP (sticker images).
    /// Pure-managed (ImageSharp), so it works on ARM32.
    /// </summary>
    public static class ImageDecoder
    {
        public static Bitmap DecodeAny(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            // 1) GDI+ (JPEG/PNG/etc.)
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var tmp = Image.FromStream(ms))
                    return new Bitmap(tmp);
            }
            catch { }

            // 2) ImageSharp → re-encode to PNG → GDI+ (handles WEBP).
            try
            {
                using (var img = SixLabors.ImageSharp.Image.Load(bytes))
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                    ms.Position = 0;
                    using (var tmp = Image.FromStream(ms))
                        return new Bitmap(tmp);
                }
            }
            catch { }

            return null;
        }
    }
}
