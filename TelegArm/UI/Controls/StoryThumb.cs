using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI.Controls
{
    /// <summary>PROFILE-STORIES: one "posted story" tile — a rounded, cover-cropped thumbnail (placeholder until it
    /// lands); video tiles get a play triangle + duration over a bottom scrim. SELF-loads its thumbnail off the UI
    /// thread (photo → smallest PhotoSize; video → the document thumb). Tap → Clicked. Reused by the profile's
    /// preview strip AND the full <see cref="StoriesGridForm"/>.</summary>
    internal sealed class StoryThumb : Control
    {
        public const int W = 86, H = 112;

        private readonly bool _isVideo;
        private readonly int _durSec;
        private readonly bool _dark;
        private Image _thumb;
        public event Action Clicked;

        public StoryThumb(TelegramService service, StoryItem story, bool dark)
        {
            _dark = dark;
            var vdoc = (story.media as MessageMediaDocument)?.document as Document;
            if (vdoc != null)
            {
                var v = vdoc.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
                if (v != null) { _isVideo = true; _durSec = (int)v.duration; }
            }
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(W, H);
            Cursor = Cursors.Hand;
            var _ = LoadAsync(service, story);
        }

        private async System.Threading.Tasks.Task LoadAsync(TelegramService service, StoryItem story)
        {
            try
            {
                byte[] bytes = null;
                var photo = (story.media as MessageMediaPhoto)?.photo as Photo;
                if (photo != null) bytes = await service.DownloadPhotoThumbAsync(photo);
                else
                {
                    var doc = (story.media as MessageMediaDocument)?.document as Document;
                    if (doc != null) bytes = await service.DownloadThumbAsync(doc);
                }
                if (bytes == null || bytes.Length == 0 || IsDisposed) return;
                var img = await System.Threading.Tasks.Task.Run(() => Decode(bytes));
                if (img == null) return;
                if (IsDisposed) { img.Dispose(); return; }
                _thumb = img;
                Invalidate();
            }
            catch { }
        }

        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Clicked?.Invoke(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = DrawHelper.RoundedRect(outer, 10))
            {
                var save = g.Save();
                g.SetClip(path);
                if (_thumb != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_thumb, CoverRect(_thumb.Size, new Rectangle(0, 0, Width, Height)));
                }
                else
                {
                    using (var b = new SolidBrush(_dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(220, 220, 224)))
                        g.FillRectangle(b, 0, 0, Width, Height);
                }
                if (_isVideo)
                {
                    using (var scrim = new LinearGradientBrush(new Rectangle(0, Height - 30, Width, 30),
                               Color.FromArgb(0, 0, 0, 0), Color.FromArgb(160, 0, 0, 0), LinearGradientMode.Vertical))
                        g.FillRectangle(scrim, 0, Height - 30, Width, 30);
                    using (var tb = new SolidBrush(Color.White))
                        g.FillPolygon(tb, new[] { new PointF(9, Height - 19), new PointF(9, Height - 7), new PointF(19, Height - 13) });
                    using (var f = FontHelper.Ui(8f, FontStyle.Bold))
                        TextRenderer.DrawText(g, FormatDur(_durSec), f, new Rectangle(23, Height - 22, Width - 25, 16), Color.White,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                g.Restore(save);
            }
            using (var pen = new Pen(_dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(206, 206, 210), 1f))
            using (var path2 = DrawHelper.RoundedRect(outer, 10))
                g.DrawPath(pen, path2);
        }

        private static Image Decode(byte[] b)
        {
            try { using (var ms = new MemoryStream(b)) using (var t = Image.FromStream(ms)) return new Bitmap(t); }
            catch { return null; }
        }

        private static Rectangle CoverRect(Size img, Rectangle bounds)
        {
            if (img.Width <= 0 || img.Height <= 0) return bounds;
            float scale = Math.Max(bounds.Width / (float)img.Width, bounds.Height / (float)img.Height);
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            return new Rectangle(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
        }

        private static string FormatDur(int sec)
        {
            if (sec < 0) sec = 0;
            return (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _thumb != null) { try { _thumb.Dispose(); } catch { } _thumb = null; }
            base.Dispose(disposing);
        }
    }
}
