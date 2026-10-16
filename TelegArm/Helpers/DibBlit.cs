using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Paints a 32 bpp BGRA buffer (top-down, stride = width * 4) into a rectangle with GDI StretchDIBits: no GDI+ bitmap
    /// per frame, the cheap path on a Tegra. Used by the video player and the camera preview (2026-10-16).
    /// </summary>
    internal static class DibBlit
    {
        public static void Draw(Graphics g, byte[] px, int w, int h, Rectangle dest) { Draw(g, px, w, h, dest, Rectangle.Empty); }

        /// <summary>As <see cref="Draw(Graphics, byte[], int, int, Rectangle)"/>, cut to <paramref name="clip"/> (a picture
        /// scaled to cover an area larger than the area it may paint; GDI clips, GDI+'s clip does not reach the HDC).</summary>
        public static void Draw(Graphics g, byte[] px, int w, int h, Rectangle dest, Rectangle clip)
        {
            if (px == null || w <= 0 || h <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h,   // negative: top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            IntPtr hdc = g.GetHdc();
            int saved = 0;
            try
            {
                if (!clip.IsEmpty) { saved = SaveDC(hdc); IntersectClipRect(hdc, clip.Left, clip.Top, clip.Right, clip.Bottom); }
                SetStretchBltMode(hdc, COLORONCOLOR);
                StretchDIBits(hdc, dest.X, dest.Y, dest.Width, dest.Height, 0, 0, w, h, px, ref bmi, 0, SRCCOPY);
            }
            finally
            {
                if (saved != 0) RestoreDC(hdc, saved);
                g.ReleaseHdc(hdc);
            }
        }

        /// <summary>Telegram Desktop's rule for call video (DecideFrameResize, 1:2): fill the box when the picture is at
        /// least as tall as the box (cutting at most half its height off, top and bottom), else fit it whole. Centred.</summary>
        public static Rectangle CoverOrFit(int w, int h, Rectangle box)
        {
            if (w <= 0 || h <= 0 || box.Width <= 0 || box.Height <= 0) return Rectangle.Empty;
            double s = Math.Max((double)box.Width / w, (double)box.Height / h);
            int bw = (int)Math.Round(w * s), bh = (int)Math.Round(h * s);
            if (bw > box.Width || bh > box.Height * 2)
            {
                var f = Fit(w, h, box.Size);
                return new Rectangle(box.X + f.X, box.Y + f.Y, f.Width, f.Height);
            }
            return new Rectangle(box.X + (box.Width - bw) / 2, box.Y + (box.Height - bh) / 2, bw, bh);
        }

        /// <summary>The largest rectangle of the w:h shape that fits in <paramref name="box"/>, centred.</summary>
        public static Rectangle Fit(int w, int h, Size box)
        {
            if (w <= 0 || h <= 0 || box.Width <= 0 || box.Height <= 0) return Rectangle.Empty;
            double s = Math.Min((double)box.Width / w, (double)box.Height / h);
            int fw = Math.Max(2, (int)(w * s)), fh = Math.Max(2, (int)(h * s));
            return new Rectangle((box.Width - fw) / 2, (box.Height - fh) / 2, fw, fh);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        private const int SRCCOPY = 0x00CC0020, COLORONCOLOR = 3;
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
        [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc,
                                                byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, int rop);
    }
}
