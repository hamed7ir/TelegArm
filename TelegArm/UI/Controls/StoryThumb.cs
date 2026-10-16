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
    /// preview strip AND the full <see cref="StoriesGridForm"/>.
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): W/H and every paint length are LOGICAL (96-DPI) px drawn
    /// through Px at the tile's DPI; the duration font is a shared FontHelper.UiAt font (never disposed). Identity at
    /// 96. The DPI is the window's once parented; IDpiAware SETS the size from W/H, never multiplies.</summary>
    internal sealed class StoryThumb : Control, IDpiAware
    {
        public const int W = 86, H = 112;   // logical px

        private int _dpi;                                          // the DPI this tile is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        private readonly bool _isVideo;
        private readonly int _durSec;
        private readonly bool _dark;
        private Image _thumb;
        public event Action Clicked;

        public StoryThumb(TelegramService service, StoryItem story, bool dark)
        {
            TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
            _dark = dark;
            var vdoc = (story.media as MessageMediaDocument)?.document as Document;
            if (vdoc != null)
            {
                var v = vdoc.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
                if (v != null) { _isVideo = true; _durSec = (int)v.duration; }
            }
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Dpi.Px(W, CurDpi), Dpi.Px(H, CurDpi));
            Cursor = Cursors.Hand;
            var _ = LoadAsync(service, story);
        }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Size = new Size(Dpi.Px(W, d), Dpi.Px(H, d)); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Size = new Size(Dpi.Px(W, newDpi), Dpi.Px(H, newDpi));   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = DrawHelper.RoundedRect(outer, P(10)))
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
                    using (var b = new SolidBrush(ThemeHelper.PaletteFor(_dark).StoryThumbPlaceholder))
                        g.FillRectangle(b, 0, 0, Width, Height);
                }
                if (_isVideo)
                {
                    int sh = P(30);
                    using (var scrim = new LinearGradientBrush(new Rectangle(0, Height - sh, Width, sh),
                               ThemeHelper.Palette.MediaScrimClear, ThemeHelper.Palette.StoryThumbScrim, LinearGradientMode.Vertical))
                        g.FillRectangle(scrim, 0, Height - sh, Width, sh);
                    using (var tb = new SolidBrush(Color.White))
                        g.FillPolygon(tb, new[] { new PointF(P(9), Height - P(19)), new PointF(P(9), Height - P(7)), new PointF(P(19), Height - P(13)) });
                    TextRenderer.DrawText(g, FormatDur(_durSec), FontHelper.UiAt(8f, dpi, FontStyle.Bold),   // shared: never dispose
                        new Rectangle(P(23), Height - P(22), Width - P(25), P(16)), Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                g.Restore(save);
            }
            // the outline stays a 1 px hairline at every DPI (like the dividers of the exemplar row)
            using (var pen = new Pen(ThemeHelper.PaletteFor(_dark).StoryThumbOutline, 1f))
            using (var path2 = DrawHelper.RoundedRect(outer, P(10)))
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
