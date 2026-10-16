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
        public static void Draw(Graphics g, byte[] px, int w, int h, Rectangle dest)
        {
            if (px == null || w <= 0 || h <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h,   // negative: top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            IntPtr hdc = g.GetHdc();
            try
            {
                SetStretchBltMode(hdc, COLORONCOLOR);
                StretchDIBits(hdc, dest.X, dest.Y, dest.Width, dest.Height, 0, 0, w, h, px, ref bmi, 0, SRCCOPY);
            }
            finally { g.ReleaseHdc(hdc); }
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
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc,
                                                byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, int rop);
    }
}
