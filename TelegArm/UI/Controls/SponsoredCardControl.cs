using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// A channel "Sponsored"/"Recommended" ad card (ToS 3.3). Owner-painted single control (RT-safe),
    /// rendered BELOW the last post — NOT a chat bubble and NOT history. Shows a label, optional sponsor
    /// photo, bold title, the ad text (via the inline engine: entities + char-wrap + RTL), optional media
    /// thumbnail, and a call-to-action button. Raises events for the click matrix; the owner sends the
    /// mandatory view/click telemetry.
    /// </summary>
    public sealed class SponsoredCardControl : Control
    {
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        public event Action<string> LinkClicked;     // a link inside the ad text
        public event Action ButtonClicked;           // the CTA button
        public event Action MediaClicked;            // the media thumbnail
        public event Action SponsorClicked;          // the label / sponsor name / photo
        public event Action<Point> MenuRequested;    // right-click / long-press (screen point)

        private readonly string _label;
        private readonly string _title;
        private readonly string _buttonText;
        private readonly bool _rtl;
        private readonly bool _hasMedia;
        private InlineText _rich;
        private Image _photo;
        private Image _mediaThumb;

        private Rectangle _cardRect, _buttonRect, _photoRect, _mediaRect, _labelRect;
        private Point _bodyOrigin;
        private int _bodyH;
        private int _textTop, _textBottom;   // y-range of label+title+body (for the view trigger)

        private const int SideGap = 12, VMargin = 6, Pad = 12, LabelH = 15, TitleH = 19,
                          ButtonH = 36, MediaH = 160, PhotoD = 40, WM_CONTEXTMENU = 0x007B;

        public SponsoredCardControl(string label, string title, string body, MessageEntity[] entities,
                                    string buttonText, bool hasMedia, Func<long, Image> emojiResolver)
        {
            _label = label ?? "Sponsored";
            _title = title ?? "";
            _buttonText = string.IsNullOrEmpty(buttonText) ? "Open" : buttonText;
            _hasMedia = hasMedia;
            string text = body ?? "";
            _rtl = FontHelper.IsPersian(text) || FontHelper.IsPersian(_title);
            if (_rtl) RightToLeft = RightToLeft.Yes;
            if (!string.IsNullOrEmpty(text)) _rich = new InlineText(text, entities, emojiResolver);

            Margin = new Padding(0);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>The random_id-keyed flag owner uses to fire the view once; exposed text band for the check.</summary>
        public int ViewTextTop => _cardRect.Y + _textTop;
        public int ViewTextBottom => _cardRect.Y + _textBottom;

        public void SetPhoto(Image img) { _photo = img; if (!IsDisposed) { Measure(); Invalidate(); } }
        public void SetMediaThumb(Image img) { _mediaThumb = img; if (!IsDisposed) Invalidate(); }

        private Font BodyFont() => _rtl ? FontHelper.Persian(11f) : FontHelper.Ui(9.75f);

        /// <summary>Recomputes Height for the current Width.</summary>
        public void Measure()
        {
            int innerW = Math.Max(40, Width - 2 * SideGap - 2 * Pad);
            int headW = innerW - (_photo != null ? PhotoD + 10 : 0);   // label/title wrap beside the photo

            int h = Pad;
            _textTop = Pad;   // label starts here (relative to the card top); used by the view trigger
            h += LabelH;
            if (!string.IsNullOrEmpty(_title)) h += TitleH;
            int headBottom = h;

            // body
            _bodyH = 0;
            if (_rich != null)
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                using (var f = BodyFont())
                {
                    _rich.Measure(g, innerW, f);
                    _rich.Position(innerW, _rtl);
                    _bodyH = _rich.Height;
                }
                h += 4 + _bodyH;
            }
            _textBottom = h;                       // end of the ad TEXT (label+title+body) → view trigger
            // keep the head block at least as tall as the photo
            if (_photo != null) h = Math.Max(h, Pad + PhotoD);

            if (_hasMedia) h += 8 + MediaH;
            h += 8 + ButtonH;                      // CTA button
            h += Pad;

            Height = h + 2 * VMargin;
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CONTEXTMENU)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1 ? PointToScreen(new Point(Width / 2, Height / 2))
                                    : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                MenuRequested?.Invoke(pt);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_buttonRect.Contains(e.Location)) { ButtonClicked?.Invoke(); return; }
            if (_hasMedia && _mediaRect.Contains(e.Location)) { MediaClicked?.Invoke(); return; }
            if (_photoRect.Contains(e.Location) || _labelRect.Contains(e.Location)) { SponsorClicked?.Invoke(); return; }
            if (_rich != null)
            {
                var local = new Point(e.X - _bodyOrigin.X, e.Y - _bodyOrigin.Y);
                if (_rich.HasHiddenSpoilerAt(local)) { _rich.RevealSpoilers(); Invalidate(); return; }
                var hit = _rich.HitTest(local);
                if (hit != null && hit.Kind == InlineKind.Url && !string.IsNullOrEmpty(hit.Url)) { LinkClicked?.Invoke(hit.Url); return; }
                if (hit != null) { SponsorClicked?.Invoke(); return; }   // @mention etc. → open sponsor chat
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245));

            _cardRect = new Rectangle(SideGap, VMargin, Width - 2 * SideGap, Height - 2 * VMargin);
            Color cardBg = IsDark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(255, 255, 255);
            using (var b = new SolidBrush(cardBg))
            using (var p = DrawHelper.RoundedRect(_cardRect, 12)) g.FillPath(b, p);
            using (var ab = new SolidBrush(AccentColor))
                g.FillRectangle(ab, _rtl ? _cardRect.Right - 3 : _cardRect.X, _cardRect.Y + 6, 3, _cardRect.Height - 12);

            int left = _cardRect.X + Pad, right = _cardRect.Right - Pad;
            int innerW = right - left;
            bool rtl = _rtl;
            var leftFlags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                            | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);

            // optional sponsor photo (top corner)
            int headLeft = left, headW = innerW;
            if (_photo != null)
            {
                int px = rtl ? right - PhotoD : left;
                _photoRect = new Rectangle(px, _cardRect.Y + Pad, PhotoD, PhotoD);
                using (var sq = FitSquare(_photo, PhotoD))
                using (var tp = new TextureBrush(sq))
                {
                    tp.TranslateTransform(_photoRect.X, _photoRect.Y);
                    using (var pe = DrawHelper.RoundedRect(_photoRect, PhotoD / 2)) g.FillPath(tp, pe);
                }
                if (rtl) { headW -= PhotoD + 10; right -= PhotoD + 10; }
                else { headLeft += PhotoD + 10; left += PhotoD + 10; }
                innerW = headW;
            }

            int y = _cardRect.Y + Pad;
            _textTop = y - _cardRect.Y;

            // LABEL ("Sponsored"/"Recommended")
            _labelRect = new Rectangle(headLeft, y, headW, LabelH);
            using (var lf = FontHelper.Ui(7.75f, FontStyle.Bold))
                TextRenderer.DrawText(g, _label.ToUpperInvariant(), lf, _labelRect, AccentColor, leftFlags);
            y += LabelH;

            // TITLE
            if (!string.IsNullOrEmpty(_title))
            {
                Color tc = IsDark ? Color.FromArgb(236, 236, 238) : Color.FromArgb(20, 20, 22);
                using (var tf = FontHelper.For(_title, rtl ? 11f : 10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _title, tf, new Rectangle(headLeft, y, headW, TitleH), tc, leftFlags);
                y += TitleH;
            }

            // restore full width for the body (it wraps under the photo)
            left = _cardRect.X + Pad; right = _cardRect.Right - Pad; innerW = right - left;
            if (_photo != null) y = Math.Max(y, _cardRect.Y + Pad + PhotoD);

            // BODY (inline text)
            if (_rich != null)
            {
                Color body = IsDark ? Color.FromArgb(220, 220, 224) : Color.FromArgb(40, 40, 44);
                _rich.Position(innerW, rtl);
                _bodyOrigin = new Point(left, y + 4);
                using (var f = BodyFont())
                    _rich.Paint(g, left, y + 4, body, AccentColor, f, IsDark, AccentColor, false);
                y += 4 + _bodyH;
            }
            _textBottom = y - _cardRect.Y;

            // MEDIA thumbnail
            if (_hasMedia)
            {
                _mediaRect = new Rectangle(left, y + 8, innerW, MediaH);
                using (var p = DrawHelper.RoundedRect(_mediaRect, 8))
                {
                    if (_mediaThumb != null)
                    {
                        var old = g.Clip;
                        g.SetClip(p);
                        DrawCover(g, _mediaThumb, _mediaRect);
                        g.Clip = old;
                        old.Dispose();
                    }
                    else
                    {
                        using (var mb = new SolidBrush(IsDark ? Color.FromArgb(60, 60, 64) : Color.FromArgb(225, 225, 230)))
                            g.FillPath(mb, p);
                        using (var mf = FontHelper.Ui(9f))
                            TextRenderer.DrawText(g, "Media", mf, _mediaRect,
                                IsDark ? Color.FromArgb(170, 170, 175) : Color.FromArgb(120, 120, 125),
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                y += 8 + MediaH;
            }

            // BUTTON
            _buttonRect = new Rectangle(left, y + 8, innerW, ButtonH);
            using (var bb = new SolidBrush(AccentColor))
            using (var bp = DrawHelper.RoundedRect(_buttonRect, 8)) g.FillPath(bb, bp);
            using (var bf = FontHelper.For(_buttonText, 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, _buttonText, bf, _buttonRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static Bitmap FitSquare(Image src, int d)
        {
            var bmp = new Bitmap(d, d);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float s = Math.Max((float)d / src.Width, (float)d / src.Height);
                float w = src.Width * s, h = src.Height * s;
                g.DrawImage(src, (d - w) / 2, (d - h) / 2, w, h);
            }
            return bmp;
        }

        private static void DrawCover(Graphics g, Image img, Rectangle r)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Max((float)r.Width / img.Width, (float)r.Height / img.Height);
            float w = img.Width * s, h = img.Height * s;
            g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _rich?.Dispose(); _rich = null; }
            base.Dispose(disposing);
        }
    }
}
