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
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): every length is LOGICAL and read through P() at the card's
    /// DPI (the metrics are properties over P()); every font is a shared FontCache font for that DPI — never disposed,
    /// never in a using. At 96 DPI every value is exactly what it was. Like a bubble, the card is measured before it is
    /// added, so the host sets LayoutDpi at creation; OnParentChanged re-measures only if the window's DPI differs.
    /// </summary>
    public sealed class SponsoredCardControl : Control, IDpiAware
    {
        private int _dpi;                                          // the DPI this card is laid out at; 0 = not known yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }
        private int P(int v) { return Dpi.Px(v, CurDpi); }        // a logical length at the card's DPI

        /// <summary>The DPI to lay out (and measure) at before the card is parented; the window's DPI wins once it is.</summary>
        public int LayoutDpi
        {
            get { return CurDpi; }
            set { if (value > 0 && value != _dpi) { _dpi = value; SyncRichDpi(); } }
        }

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

        private int SideGap => P(12);
        private int VMargin => P(6);
        private int Pad => P(12);
        private int LabelH => P(15);
        private int TitleH => P(19);
        private int ButtonH => P(36);
        private int MediaH => P(160);
        private int PhotoD => P(40);
        private const int WM_CONTEXTMENU = 0x007B;

        public SponsoredCardControl(string label, string title, string body, MessageEntity[] entities,
                                    string buttonText, bool hasMedia, Func<long, Image> emojiResolver)
        {
            TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
            _label = label ?? "Sponsored";
            _title = title ?? "";
            _buttonText = string.IsNullOrEmpty(buttonText) ? "Open" : buttonText;
            _hasMedia = hasMedia;
            string text = body ?? "";
            _rtl = FontHelper.IsPersian(text) || FontHelper.IsPersian(_title);
            if (_rtl) RightToLeft = RightToLeft.Yes;
            if (!string.IsNullOrEmpty(text)) _rich = new InlineText(text, entities, emojiResolver) { LayoutDpi = CurDpi };

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

        /// <summary>Step 3.2 — the card is built (and measured) before it is added: adopt the window's DPI once parented,
        /// re-measuring only if it differs from the DPI the card was measured at.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d == _dpi) return;
            int was = CurDpi;   // what the fonts + metrics used so far (the default DPI while _dpi was 0)
            _dpi = d;
            SyncRichDpi();
            if (d != was) { Measure(); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            SyncRichDpi();
            // One card, so it re-measures itself (SET from the logical metrics, never a multiple of the scaled Height):
            // the panel's width handler re-measures only MessageBubbleControls, never this card.
            Measure();
            Invalidate();
        }

        /// <summary>Hands the inline engine the card's DPI and drops its cached layout + bitmap.</summary>
        private void SyncRichDpi()
        {
            if (_rich == null) return;
            int d = CurDpi;
            if (_rich.LayoutDpi != d) { _rich.LayoutDpi = d; _rich.InvalidateLayout(); }
        }

        /// <summary>Shared per-DPI font (step 3.2): never dispose it, never wrap it in a using.</summary>
        private Font BodyFont() => _rtl ? FontHelper.PersianAt(11f, CurDpi) : FontHelper.UiAt(9.75f, CurDpi);

        /// <summary>Recomputes Height for the current Width.</summary>
        public void Measure()
        {
            int innerW = Math.Max(P(40), Width - 2 * SideGap - 2 * Pad);
            int headW = innerW - (_photo != null ? PhotoD + P(10) : 0);   // label/title wrap beside the photo

            int h = Pad;
            _textTop = Pad;   // label starts here (relative to the card top); used by the view trigger
            h += LabelH;
            if (!string.IsNullOrEmpty(_title)) h += TitleH;
            int headBottom = h;

            // body
            _bodyH = 0;
            if (_rich != null)
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))   // pixel-unit fonts measure the same on any DC
                {
                    _rich.Measure(g, innerW, BodyFont());
                    _rich.Position(innerW, _rtl);
                    _bodyH = _rich.Height;
                }
                h += P(4) + _bodyH;
            }
            _textBottom = h;                       // end of the ad TEXT (label+title+body) → view trigger
            // keep the head block at least as tall as the photo
            if (_photo != null) h = Math.Max(h, Pad + PhotoD);

            if (_hasMedia) h += P(8) + MediaH;
            h += P(8) + ButtonH;                   // CTA button
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
            ChatBackdrop.PaintRow(g, this, e.ClipRectangle, IsDark, ThemeHelper.PaletteFor(IsDark).ChatBg, null);   // step 5.4

            _cardRect = new Rectangle(SideGap, VMargin, Width - 2 * SideGap, Height - 2 * VMargin);
            Color cardBg = ThemeHelper.PaletteFor(IsDark).SponsoredCardBg;
            using (var b = new SolidBrush(cardBg))
            using (var p = DrawHelper.RoundedRect(_cardRect, P(12))) g.FillPath(b, p);
            int barW = P(3);
            using (var ab = new SolidBrush(AccentColor))
                g.FillRectangle(ab, _rtl ? _cardRect.Right - barW : _cardRect.X, _cardRect.Y + P(6), barW, _cardRect.Height - P(12));

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
                if (rtl) { headW -= PhotoD + P(10); right -= PhotoD + P(10); }
                else { headLeft += PhotoD + P(10); left += PhotoD + P(10); }
                innerW = headW;
            }

            int y = _cardRect.Y + Pad;
            _textTop = y - _cardRect.Y;

            // LABEL ("Sponsored"/"Recommended")
            _labelRect = new Rectangle(headLeft, y, headW, LabelH);
            TextRenderer.DrawText(g, _label.ToUpperInvariant(), FontHelper.UiAt(7.75f, CurDpi, FontStyle.Bold), _labelRect, ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).SponsoredCardBg), leftFlags);
            y += LabelH;

            // TITLE
            if (!string.IsNullOrEmpty(_title))
            {
                Color tc = ThemeHelper.PaletteFor(IsDark).SponsoredTitle;
                TextRenderer.DrawText(g, _title, FontHelper.ForAt(_title, rtl ? 11f : 10f, CurDpi, FontStyle.Bold),
                    new Rectangle(headLeft, y, headW, TitleH), tc, leftFlags);   // shared font: never dispose
                y += TitleH;
            }

            // restore full width for the body (it wraps under the photo)
            left = _cardRect.X + Pad; right = _cardRect.Right - Pad; innerW = right - left;
            if (_photo != null) y = Math.Max(y, _cardRect.Y + Pad + PhotoD);

            // BODY (inline text)
            if (_rich != null)
            {
                Color body = ThemeHelper.PaletteFor(IsDark).SponsoredBody;
                _rich.Position(innerW, rtl);
                _bodyOrigin = new Point(left, y + P(4));
                _rich.Paint(g, left, y + P(4), body, ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).SponsoredCardBg), BodyFont(), IsDark, AccentColor, false);
                y += P(4) + _bodyH;
            }
            _textBottom = y - _cardRect.Y;

            // MEDIA thumbnail
            if (_hasMedia)
            {
                _mediaRect = new Rectangle(left, y + P(8), innerW, MediaH);
                using (var p = DrawHelper.RoundedRect(_mediaRect, P(8)))
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
                        using (var mb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).SponsoredMediaPlaceholder))
                            g.FillPath(mb, p);
                        TextRenderer.DrawText(g, "Media", FontHelper.UiAt(9f, CurDpi), _mediaRect,
                            ThemeHelper.PaletteFor(IsDark).SponsoredMediaLabel,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                y += P(8) + MediaH;
            }

            // BUTTON
            _buttonRect = new Rectangle(left, y + P(8), innerW, ButtonH);
            using (var bb = new SolidBrush(AccentColor))
            using (var bp = DrawHelper.RoundedRect(_buttonRect, P(8))) g.FillPath(bb, bp);
            TextRenderer.DrawText(g, _buttonText, FontHelper.ForAt(_buttonText, 9.5f, CurDpi, FontStyle.Bold), _buttonRect, ThemeHelper.PaletteFor(IsDark).OnAccent,
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
