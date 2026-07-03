using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TelegArm.Helpers
{
    /// <summary>Shared GDI+ drawing utilities for the custom-painted controls.</summary>
    public static class DrawHelper
    {
        /// <summary>Draws a circular download progress ring inside <paramref name="circle"/>: a faint track,
        /// a foreground arc filling clockwise from 12 o'clock to <paramref name="fraction"/> (0..1), and — when
        /// <paramref name="cancel"/> — an ✕ in the center (the cancel hit target). Pure GDI+ (honors transforms).</summary>
        public static void DrawProgressRing(Graphics g, Rectangle circle, float fraction, Color ringColor, Color trackColor, bool cancel)
        {
            if (circle.Width < 6 || circle.Height < 6) return;
            float pw = Math.Max(2.5f, circle.Width / 12f);
            var arc = new RectangleF(circle.X + pw / 2f, circle.Y + pw / 2f, circle.Width - pw, circle.Height - pw);
            using (var tp = new Pen(trackColor, pw)) g.DrawEllipse(tp, arc);
            if (fraction > 0f)
                using (var rp = new Pen(ringColor, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(rp, arc.X, arc.Y, arc.Width, arc.Height, -90f, Math.Max(2f, Math.Min(1f, fraction) * 360f));
            if (cancel)
            {
                float cx = circle.X + circle.Width / 2f, cy = circle.Y + circle.Height / 2f, r = circle.Width / 6f;
                using (var xp = new Pen(ringColor, pw * 0.85f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(xp, cx - r, cy - r, cx + r, cy + r);
                    g.DrawLine(xp, cx + r, cy - r, cx - r, cy + r);
                }
            }
        }

        /// <summary>Draws <paramref name="cover"/> cover-fitted (center-cropped) and clipped into the circular
        /// <paramref name="circle"/>, then a subtle dim overlay so an overlaid play/download/ring glyph stays
        /// legible. The SINGLE cover-in-circle implementation shared by standalone audio bubbles and audio-album
        /// rows. Call only when a cover exists; with no cover the caller draws its own plain circle. Pure GDI+
        /// (honors transforms). The caller draws the glyph on top afterward.</summary>
        public static void DrawAudioCover(Graphics g, Rectangle circle, Image cover)
        {
            if (cover == null || circle.Width < 2 || circle.Height < 2) return;
            using (var ell = new GraphicsPath())
            {
                ell.AddEllipse(circle);
                var prev = g.Clip;
                g.SetClip(ell);
                // cover-fit: scale to fill the circle's bounding box, center-crop the overflow
                float ir = (float)cover.Width / cover.Height, rr = (float)circle.Width / circle.Height;
                Rectangle dest;
                if (ir > rr) { int w = (int)Math.Ceiling(circle.Height * ir); dest = new Rectangle(circle.X - (w - circle.Width) / 2, circle.Y, w, circle.Height); }
                else { int h = (int)Math.Ceiling(circle.Width / ir); dest = new Rectangle(circle.X, circle.Y - (h - circle.Height) / 2, circle.Width, h); }
                var iq = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cover, dest);
                g.InterpolationMode = iq;
                g.Clip = prev; prev.Dispose();
            }
            using (var ov = new SolidBrush(Color.FromArgb(95, 0, 0, 0))) g.FillEllipse(ov, circle);
        }

        /// <summary>Builds a rounded-rectangle path with the given corner radius.</summary>
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                path.CloseFigure();
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static readonly Color[] AvatarPalette =
        {
            Color.FromArgb(229, 115, 115), Color.FromArgb(149, 117, 205),
            Color.FromArgb(100, 181, 246), Color.FromArgb(77, 182, 172),
            Color.FromArgb(129, 199, 132), Color.FromArgb(255, 183, 77),
            Color.FromArgb(240, 98, 146), Color.FromArgb(121, 134, 203)
        };

        /// <summary>Deterministic avatar color derived from a peer id.</summary>
        public static Color AvatarColor(long peerId)
        {
            int idx = (int)(Math.Abs(peerId) % AvatarPalette.Length);
            return AvatarPalette[idx];
        }

        /// <summary>Draws a profile photo clipped to a circle (same look as the chat-list row). Callers draw the
        /// initials-circle fallback themselves when no image is available yet.</summary>
        public static void DrawCircularImage(Graphics g, Rectangle circle, Image img)
        {
            if (img == null || circle.Width < 2 || circle.Height < 2) return;
            using (var clip = new GraphicsPath())
            {
                clip.AddEllipse(circle);
                var prev = g.Clip;
                g.SetClip(clip);
                var iq = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, circle);
                g.InterpolationMode = iq;
                g.Clip = prev; prev.Dispose();
            }
        }

        /// <summary>Draws a colored rounded file-type icon with the extension label (GDI+ only).</summary>
        public static void DrawFileIcon(Graphics g, Rectangle rect, string fileName)
        {
            string ext = (System.IO.Path.GetExtension(fileName ?? "") ?? "").TrimStart('.').ToUpperInvariant();
            Color bg;
            string label;
            switch (ext)
            {
                case "PDF": bg = Color.FromArgb(0xE5, 0x39, 0x35); label = "PDF"; break;
                case "DOC": case "DOCX": bg = Color.FromArgb(0x15, 0x65, 0xC0); label = "DOC"; break;
                case "XLS": case "XLSX": bg = Color.FromArgb(0x2E, 0x7D, 0x32); label = "XLS"; break;
                case "PPT": case "PPTX": bg = Color.FromArgb(0xD8, 0x43, 0x15); label = "PPT"; break;
                case "ZIP": case "RAR": case "7Z": bg = Color.FromArgb(0xE6, 0x51, 0x00); label = "ZIP"; break;
                case "APK": bg = Color.FromArgb(0x38, 0x8E, 0x3C); label = "APK"; break;
                case "MP3": case "OGG": case "AAC": case "M4A": case "WAV": bg = Color.FromArgb(0x7E, 0x57, 0xC2); label = "MP3"; break;
                case "TXT": bg = Color.FromArgb(0x54, 0x6E, 0x7A); label = "TXT"; break;
                default:
                    bg = Color.FromArgb(0x61, 0x61, 0x61);
                    label = string.IsNullOrEmpty(ext) ? "FILE" : (ext.Length > 3 ? ext.Substring(0, 3) : ext);
                    break;
            }

            using (var b = new SolidBrush(bg))
            using (var path = RoundedRect(rect, Math.Max(4, rect.Height / 8)))
                g.FillPath(b, path);
            using (var f = new Font("Segoe UI", Math.Max(7f, rect.Height / 4.5f), FontStyle.Bold))
            using (var wb = new SolidBrush(Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(label, f, wb, rect, sf);
        }

        /// <summary>Human-readable file size.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            return (kb / 1024.0).ToString("0.0") + " MB";
        }

        /// <summary>Compact timestamp: time for today, otherwise short date.</summary>
        public static string ShortStamp(DateTime utc)
        {
            if (utc == default) return string.Empty;
            var local = utc.ToLocalTime();
            return local.Date == DateTime.Now.Date ? local.ToString("HH:mm") : local.ToString("dd/MM/yy");
        }
    }
}
