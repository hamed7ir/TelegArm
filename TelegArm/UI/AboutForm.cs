using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// The "About TelegArm" screen — app identity + version, a PROMINENTLY FEATURED acknowledgment of
    /// WTelegramClient and its author Wizou (wiz0u) — the pure-C# MTProto library the whole app is built on —
    /// plus the Open-RT thanks, the source/license links and the open-source credits. Links open in the browser
    /// (RT-safe: Process.Start wrapped in try/catch), except the Telegram group, which is handed to the caller
    /// (<see cref="PendingInAppLink"/>) to open IN-APP.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply), laid out the way
    /// DroidFinestra's AboutForm is: cards on the dialog body, every block MEASURED at its width and at the window's
    /// DPI, so the layout flows from the measured heights and no text is ever cut off (the 1.x version placed text at
    /// fixed pixel heights). <paramref name="dark"/> and <paramref name="accent"/> are kept for the caller and no
    /// longer read: the palette follows the theme live.
    ///
    /// HOW IT IS BUILT. Each block of the screen (header, cards, footer) is one owner-drawn row (<see cref="Block"/>, a
    /// SettingRow) that lays out and paints its own text, picture and links from a list of parts — so there is no
    /// child control to recolour on a theme change, no font stored on a control for DpiScaler to scale twice, and the
    /// height of a block is simply HeightFor(width, dpi). DialogForm stacks the blocks and scrolls them.
    /// ⚠ The two link arrows and the sponsor heart are DRAWN, not typed: the bundled Roboto (Regular and Bold) has
    ///   neither U+2192 nor U+2665 (FACT: read from the font files' character maps), so 1.x showed whatever the
    ///   system fell back to — which is how tofu starts on RT 8.1.
    /// </summary>
    public sealed class AboutForm : DialogForm
    {
        private const string RepoUrl = "https://github.com/hamed7ir/TelegArm";
        private const string WtcUrl = "https://github.com/wiz0u/WTelegramClient";
        private const string WtcGroupUrl = "https://t.me/WTelegramClient";   // the WTelegramClient Telegram group
        private const string SponsorUrl = "https://github.com/sponsors/wiz0u";
        private const string LicenseUrl = "https://github.com/hamed7ir/TelegArm/blob/main/LICENSE";

        private string _pendingLink;
        /// <summary>A Telegram chat/group the user tapped in About — the caller opens it IN-APP after this
        /// modal dialog closes (About can't reach the chat view itself). Null if nothing in-app was tapped.</summary>
        public string PendingInAppLink => _pendingLink;

        public AboutForm(bool dark, Color accent) : base("About TelegArm", 480, 640)
        {
            // ── Header: icon + name + version + description (centered) ──
            var header = new Block(BlockStyle.Plain, "TelegArm", 0, 24, 4, 12)
                .Add(Part.Pic(LoadAppImage(200), 100, 0))
                .Add(Part.Line("TelegArm", 20f, FontStyle.Bold, LabelRole.Body, 10, HorizontalAlignment.Center))
                .Add(Part.Line("Version " + Program.Version, 9.5f, FontStyle.Regular, LabelRole.Dim, 2, HorizontalAlignment.Center))
                .Add(Part.Line("A pure-managed Telegram client for legacy ARM32 Windows.", 9f, FontStyle.Regular, LabelRole.Dim, 8, HorizontalAlignment.Center));

            // ── FEATURED — WTelegramClient / Wizou (accent-tinted, emphasized) ──
            var wtc = new Block(BlockStyle.Feature, "Built on WTelegramClient", 16, 14, 10, 14)
                .Add(Part.Line("Built on WTelegramClient", 12f, FontStyle.Bold, LabelRole.Accent, 0))
                .Add(Part.Line("TelegArm is built entirely on WTelegramClient — the pure-C# MTProto library by Wizou (wiz0u), developed and maintained by one person. Please consider supporting his work.",
                               9f, FontStyle.Regular, LabelRole.Body, 6))
                .Add(Part.Link("github.com/wiz0u/WTelegramClient", GlyphKind.Arrow, () => OpenUrl(WtcUrl), 8))
                .Add(Part.Link("@WTelegramClient — Telegram group", GlyphKind.Arrow, () => OpenInApp(WtcGroupUrl), 0))
                .Add(Part.Link("Support Wizou — GitHub Sponsors", GlyphKind.Heart, () => OpenUrl(SponsorUrl), 0));

            // ── Open-RT community tribute (mirrors the WTC card's layout; adds the openrt.png logo) ──
            var openRt = new Block(BlockStyle.Card, "Open-RT Community", 16, 16, 16, 14)
                .WithSideImage(LoadImageFile("openrt.png", 144))   // shipped next to the exe (Content, PreserveNewest)
                .Add(Part.Line("Open-RT Community", 12f, FontStyle.Bold, LabelRole.Accent, 0))
                .Add(Part.Line("Thanks to the Open-RT community for bringing TelegArm to Windows RT:", 9f, FontStyle.Regular, LabelRole.Dim, 4))
                .Add(Part.Line("samyryu · RHLM fan · Artus100 · Keith · jeybee · die Oberfläche knacken", 9.5f, FontStyle.Bold, LabelRole.Body, 6));

            // ── Source + license ──
            var meta = new Block(BlockStyle.Card, "Source code and license", 16, 0, 0, 14)
                .Add(Part.Row("Source code", 9.5f, "github.com/hamed7ir/TelegArm", 9.5f, FontStyle.Bold, LabelRole.Accent, () => OpenUrl(RepoUrl), 0, 46, 0))
                .Add(Part.Rule())
                .Add(Part.Row("License", 9.5f, "GPL-3.0-only", 9.5f, FontStyle.Bold, LabelRole.Accent, () => OpenUrl(LicenseUrl), 0, 46, 0));

            // ── Open-source credits ──
            string[][] credits =
            {
                // BATCH-TA-2 — WTelegramClient was missing entirely: the MTProto library the whole app
                // is built on. Concentus.OggFile is a separate package from Concentus and was also absent.
                new[] { "WTelegramClient", "MIT" },
                // One row used to cover both as "LGPL-2.1". True for the C# bindings and for
                // libvlc/libvlccore, but NOT for what we actually ship: both bundled zips carry plugins
                // built against GPL upstreams, so the native bundle is GPL-2.0-or-later. (Or-later, so it
                // upgrades to GPL-3 and is compatible with ours.) The plugin table lives in
                // THIRD-PARTY-NOTICES.txt component 4 — the footer below already points there, and this
                // screen deliberately does not restate it.
                new[] { "LibVLCSharp", "LGPL-2.1" },
                new[] { "libVLC + plugins", "GPL-2.0-or-later" },
                new[] { "Newtonsoft.Json", "MIT" },
                new[] { "NAudio", "Ms-PL" },
                new[] { "Concentus (Opus)", "BSD-3-Clause" },
                new[] { "Concentus.OggFile", "BSD-3-Clause" },
                new[] { "SixLabors.ImageSharp", "Apache-2.0" },
                new[] { "QRCoder", "MIT" },
                new[] { "rlottie", "MIT" },
                new[] { "Vazirmatn font", "SIL OFL 1.1" },
                // BATCH-TA-2/L2 — these three lines used to declare licences the copyright holders
                // never granted. Verified from each artefact itself: Roboto Regular/Bold are v3.009
                // (2024) under the OFL per their own `name` table, only Medium is the 2013 Apache-2.0
                // build; and the LICENSE we actually redistribute inside noto-emoji.zip is the OFL,
                // not Apache-2.0. THIRD-PARTY-NOTICES.txt carries the full split.
                new[] { "Roboto (Regular/Bold)", "SIL OFL 1.1" },
                new[] { "Roboto (Medium)", "Apache-2.0" },
                new[] { "Noto Emoji", "SIL OFL 1.1" },
            };
            var cr = new Block(BlockStyle.Card, "Open-source credits", 16, 12, 10, 14)
                .Add(Part.Line("OPEN-SOURCE CREDITS", 8.25f, FontStyle.Bold, LabelRole.Accent, 0));
            bool firstCredit = true;
            foreach (var row in credits)
            {
                cr.Add(Part.Row(row[0], 9f, row[1], 8.5f, FontStyle.Regular, LabelRole.Dim, null, 136, 22, firstCredit ? 8 : 0));
                firstCredit = false;
            }

            // ── Footer ──
            // BATCH-TA-2/L1 — was "(in the repo root)", which was false for every installed copy: neither
            // installer shipped the file at all. Both now drop LICENSE.txt + THIRD-PARTY-NOTICES.txt into
            // the install directory, so point at where the user can actually find them.
            var foot = new Block(BlockStyle.Plain, "License notices", 0, 0, 14, 0)
                .Add(Part.Line("Full license texts: LICENSE.txt and THIRD-PARTY-NOTICES.txt, beside the app.", 8.25f, FontStyle.Regular, LabelRole.Dim, 0, HorizontalAlignment.Center))
                .Add(Part.Line("© 2026 TelegArm · Licensed under GPL-3.0-only", 8.25f, FontStyle.Regular, LabelRole.Dim, 2, HorizontalAlignment.Center));

            PopulateBody(header, wtc, openRt, meta, cr, foot);

            // Close is both the Esc and the Enter button. Its result is Cancel: a plain close is what 1.x returned (a tap on
            // the in-app group link is the only OK, set in OpenInApp), and the caller reads PendingInAppLink, not the result.
            var close = AddFooterButton("Close", RoundedButtonKind.Primary, DialogResult.Cancel);
            CancelButton = close;
            AcceptButton = close;
            FitToContent(MaxBodyLogical());
        }

        /// <summary>The tallest body the dialog may have, in logical px: the 640-high About of 1.x less the title bar and the
        /// footer, and never more than 90% of the work area (the content is taller than that and scrolls).</summary>
        private int MaxBodyLogical()
        {
            int body = 540;
            try
            {
                int dpi = Math.Max(1, WindowDpiValue);
                int room = (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.9) - Bar.Height - Px(FooterHeight) - 1;
                body = Math.Min(body, room * Dpi.Logical / dpi);
            }
            catch { /* no screen to ask: keep the default */ }
            return Math.Max(240, body);
        }

        /// <summary>A Telegram chat/group link — closes About and hands the link to the caller to open IN-APP
        /// (the chat view), instead of the browser.</summary>
        private void OpenInApp(string url)
        {
            _pendingLink = url;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(url); } catch { /* RT-safe: no default browser / blocked → ignore */ }
        }

        /// <summary>Loads the transparent app logo (TelegArm.png, shipped next to the exe) scaled to
        /// <paramref name="size"/>px; falls back to the exe's own icon. Null if neither is available.</summary>
        private static Image LoadAppImage(int size)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TelegArm.png");
                if (File.Exists(p))
                {
                    using (var fs = File.OpenRead(p))
                    using (var src = Image.FromStream(fs))
                    {
                        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, new Rectangle(Point.Empty, new Size(size, size)));
                        }
                        return bmp;
                    }
                }
            }
            catch { }
            try { using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) return ic?.ToBitmap(); }
            catch { return null; }
        }

        /// <summary>Loads a PNG shipped next to the exe (Content/PreserveNewest) scaled to <paramref name="size"/>px
        /// (32bpp). Same mechanism as <see cref="LoadAppImage"/> but returns null (not the app icon) when absent —
        /// the caller simply shows no image. RT-safe: any failure → null.</summary>
        private static Image LoadImageFile(string fileName, int size)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (!File.Exists(p)) return null;
                using (var fs = File.OpenRead(p))
                using (var src = Image.FromStream(fs))
                {
                    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(Point.Empty, new Size(size, size)));
                    }
                    return bmp;
                }
            }
            catch { return null; }
        }

        // ── the screen's building blocks ─────────────────────────────────────────────────────────────────────────

        private enum BlockStyle { Plain, Card, Feature }
        private enum PartKind { Text, Link, Row, Divider, Picture }
        private enum GlyphKind { None, Arrow, Heart }

        /// <summary>One thing a <see cref="Block"/> shows. The factories say what is built; <see cref="Block.Place"/> fills in
        /// the rectangles (R1, R2, Hit) for a width and a DPI. Every length here is LOGICAL (96 dpi).</summary>
        private sealed class Part
        {
            public PartKind Kind;
            public string Text, Text2;
            public float Pt = 9f, Pt2 = 9f;
            public FontStyle Style, Style2;
            public LabelRole Ink = LabelRole.Body, Ink2 = LabelRole.Dim;
            public HorizontalAlignment Align;
            public int GapBefore, MinHeight, RightWidth, Size;
            public GlyphKind Glyph;
            public Action Click;
            public Image Img;
            public Rectangle R1, R2, Hit;   // set by Block.Place

            /// <summary>Wrapped text.</summary>
            public static Part Line(string text, float pt, FontStyle style, LabelRole ink, int gapBefore, HorizontalAlignment align = HorizontalAlignment.Left)
            {
                return new Part { Kind = PartKind.Text, Text = text, Pt = pt, Style = style, Ink = ink, GapBefore = gapBefore, Align = align };
            }

            /// <summary>A tappable link line (a glyph, then the text in the accent ink), at least 26 px tall for a finger.</summary>
            public static Part Link(string text, GlyphKind glyph, Action click, int gapBefore)
            {
                return new Part { Kind = PartKind.Link, Text = text, Pt = 9.5f, Style = FontStyle.Bold, Ink = LabelRole.Accent, Glyph = glyph, Click = click, GapBefore = gapBefore, MinHeight = 26 };
            }

            /// <summary>A left text and a right-aligned text on one row; the right one is the link when <paramref name="click"/>
            /// is set. <paramref name="rightWidth"/> fixes the right column (0 = whatever the left text leaves).</summary>
            public static Part Row(string left, float pt1, string right, float pt2, FontStyle style2, LabelRole ink2, Action click, int rightWidth, int minHeight, int gapBefore)
            {
                return new Part { Kind = PartKind.Row, Text = left, Pt = pt1, Text2 = right, Pt2 = pt2, Style2 = style2, Ink2 = ink2, Click = click, RightWidth = rightWidth, MinHeight = minHeight, GapBefore = gapBefore };
            }

            /// <summary>A hairline across the card.</summary>
            public static Part Rule() { return new Part { Kind = PartKind.Divider }; }

            /// <summary>A square picture, centred. The block owns (and disposes) the image.</summary>
            public static Part Pic(Image img, int size, int gapBefore) { return new Part { Kind = PartKind.Picture, Img = img, Size = size, GapBefore = gapBefore }; }
        }

        /// <summary>A card (or plain strip) of the About screen: owner-drawn, and as tall as its parts measure at its width.
        /// It is a SettingRow only for the machinery that comes with one — the palette-backed clear, the refit when the width
        /// or the window's DPI changes, the theme repaint, and the accessible name — it is not a setting.
        ///
        /// ★ MEASURED, NOT FIXED. <see cref="Place"/> measures every text with TextRenderer at the width it will be drawn at and
        ///   stacks the parts from those heights; <see cref="HeightFor"/> is its result, and OnPaint and the hit test use the
        ///   same placement (cached for one width and DPI). A card is 22 logical px in from each edge, 12 px round.
        /// ★ LINKS. A part with a Click is a link: the hand cursor over it and a left click run it. (Not a tab stop — 1.x's links
        ///   were not either.)
        /// ★ INK comes from the palette at paint time; the accent text is nudged to read on the card it sits on
        ///   (ColorMath.Ink against the card fill), which matters on the accent-tinted featured card.</summary>
        private sealed class Block : SettingRow
        {
            private const int MarginX = 22, Radius = 12, GlyphCol = 22, DividerInset = 14, ColGap = 12;
            private const int LinkPadY = 3, RowPadY = 3;
            private const int SideImageSize = 72, SideImageGap = 14, SideImageTop = 20;

            private readonly BlockStyle _style;
            private readonly List<Part> _parts = new List<Part>();
            private readonly int _padX, _topPad, _bottomPad, _gapAfter;
            private Image _sideImage;
            private Rectangle _card, _side;
            private int _placedWidth = -1, _placedDpi = -1, _placedHeight;

            /// <param name="padX">Logical padding between the card edge and its content.</param>
            /// <param name="gapAfter">Logical blank space below the block (the gap to the next card).</param>
            public Block(BlockStyle style, string caption, int padX, int topPad, int bottomPad, int gapAfter) : base(caption, false)
            {
                _style = style;
                _padX = padX;
                _topPad = topPad;
                _bottomPad = bottomPad;
                _gapAfter = gapAfter;
                AccessibleRole = AccessibleRole.StaticText;
                Refit();
            }

            public Block Add(Part part)
            {
                _parts.Add(part);
                Reflow();
                return this;
            }

            /// <summary>A picture in a column at the left of the card's text (the Open-RT logo). The block owns it.</summary>
            public Block WithSideImage(Image img)
            {
                _sideImage = img;
                Reflow();
                return this;
            }

            private void Reflow()
            {
                _placedWidth = -1;
                AccessibleName = SpokenText();
                if (Parent != null) { Refit(); Invalidate(); }
            }

            /// <summary>What a screen reader announces for the block: every text it shows, in order (1.x's labels each exposed
            /// their own text; a block draws its texts itself, so without this only the short caption would be read).</summary>
            private string SpokenText()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in _parts)
                {
                    if (part.Kind == PartKind.Divider || part.Kind == PartKind.Picture) continue;
                    if (sb.Length > 0) sb.Append(". ");
                    sb.Append(part.Text);
                    if (part.Kind == PartKind.Row && !string.IsNullOrEmpty(part.Text2)) sb.Append(' ').Append(part.Text2);
                }
                return sb.Length > 0 ? sb.ToString() : Text;
            }

            // ── placement ────────────────────────────────────────────────────────────────────────────────────

            public override int HeightFor(int width, int dpi) { return Place(width, dpi); }

            private static int TextHeight(string text, float pt, FontStyle style, int width, HorizontalAlignment align, int dpi)
            {
                return TextRenderer.MeasureText(text, FontHelper.UiAt(pt, dpi, style), new Size(Math.Max(1, width), 0), DrawFlags(align)).Height;
            }

            private static TextFormatFlags DrawFlags(HorizontalAlignment align)
            {
                var f = BaseFlags | TextFormatFlags.WordBreak | TextFormatFlags.Top;
                if (align == HorizontalAlignment.Center) return f | TextFormatFlags.HorizontalCenter;
                if (align == HorizontalAlignment.Right) return f | TextFormatFlags.Right;
                return f | TextFormatFlags.Left;
            }

            /// <summary>Measures and places every part for an outer <paramref name="width"/> at <paramref name="dpi"/>; returns the
            /// block's height (the card plus the gap below it). Cached for one width and DPI.</summary>
            private int Place(int width, int dpi)
            {
                if (width == _placedWidth && dpi == _placedDpi) return _placedHeight;

                int mx = Dpi.Px(MarginX, dpi);
                int left = mx, right = Math.Max(mx + 1, width - mx);
                int x0 = left + Dpi.Px(_padX, dpi);
                int tx = x0 + (_sideImage != null ? Dpi.Px(SideImageSize + SideImageGap, dpi) : 0);   // the text column
                int cw = Math.Max(1, right - Dpi.Px(_padX, dpi) - tx);
                int y = Dpi.Px(_topPad, dpi);

                foreach (var part in _parts)
                {
                    y += Dpi.Px(part.GapBefore, dpi);
                    switch (part.Kind)
                    {
                        case PartKind.Text:
                        {
                            int h = TextHeight(part.Text, part.Pt, part.Style, cw, part.Align, dpi);
                            part.R1 = new Rectangle(tx, y, cw, h);
                            y += h;
                            break;
                        }
                        case PartKind.Link:
                        {
                            int gw = part.Glyph == GlyphKind.None ? 0 : Dpi.Px(GlyphCol, dpi);
                            int tw = Math.Max(1, cw - gw);
                            int h = TextHeight(part.Text, part.Pt, part.Style, tw, HorizontalAlignment.Left, dpi);
                            int rowH = Math.Max(Dpi.Px(part.MinHeight, dpi), h + 2 * Dpi.Px(LinkPadY, dpi));
                            part.Hit = new Rectangle(tx, y, cw, rowH);
                            part.R1 = new Rectangle(tx + gw, y + (rowH - h) / 2, tw, h);
                            part.R2 = new Rectangle(tx, y, gw, rowH);   // the glyph's slot
                            y += rowH;
                            break;
                        }
                        case PartKind.Row:
                        {
                            int gap = Dpi.Px(ColGap, dpi);
                            int w1, w2;
                            if (part.RightWidth > 0)
                            {
                                w2 = Math.Min(Dpi.Px(part.RightWidth, dpi), cw * 3 / 4);
                                w1 = Math.Max(1, cw - w2 - gap);
                            }
                            else
                            {
                                // the left text keeps its whole line (up to 40% of the row); the right one gets the rest
                                int need = TextRenderer.MeasureText(part.Text, FontHelper.UiAt(part.Pt, dpi, part.Style), Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width + Dpi.Px(2, dpi);
                                w1 = Math.Min(need, cw * 2 / 5);
                                w2 = Math.Max(1, cw - w1 - gap);
                            }
                            int h1 = TextHeight(part.Text, part.Pt, part.Style, w1, HorizontalAlignment.Left, dpi);
                            int h2 = TextHeight(part.Text2, part.Pt2, part.Style2, w2, HorizontalAlignment.Right, dpi);
                            int rowH = Math.Max(Dpi.Px(part.MinHeight, dpi), Math.Max(h1, h2) + 2 * Dpi.Px(RowPadY, dpi));
                            part.R1 = new Rectangle(tx, y + (rowH - h1) / 2, w1, h1);
                            part.R2 = new Rectangle(tx + cw - w2, y + (rowH - h2) / 2, w2, h2);
                            part.Hit = part.Click != null ? new Rectangle(part.R2.X, y, w2, rowH) : Rectangle.Empty;
                            y += rowH;
                            break;
                        }
                        case PartKind.Divider:
                        {
                            int inset = Dpi.Px(DividerInset, dpi);
                            part.R1 = new Rectangle(left + inset, y, Math.Max(1, right - left - 2 * inset), 1);
                            y += 1;
                            break;
                        }
                        case PartKind.Picture:
                        {
                            int sz = Dpi.Px(part.Size, dpi);
                            part.R1 = new Rectangle(left + (right - left - sz) / 2, y, sz, sz);
                            y += sz;
                            break;
                        }
                    }
                }

                y += Dpi.Px(_bottomPad, dpi);
                if (_sideImage != null)
                {
                    int sz = Dpi.Px(SideImageSize, dpi);
                    _side = new Rectangle(x0, Dpi.Px(SideImageTop, dpi), sz, sz);
                    y = Math.Max(y, _side.Bottom + Dpi.Px(_bottomPad, dpi));
                }
                _card = _style == BlockStyle.Plain ? Rectangle.Empty : new Rectangle(left, 0, right - left, y);
                _placedWidth = width;
                _placedDpi = dpi;
                _placedHeight = y + Dpi.Px(_gapAfter, dpi);
                return _placedHeight;
            }

            // ── links ────────────────────────────────────────────────────────────────────────────────────────

            private Part LinkAt(Point pt)
            {
                foreach (var part in _parts)
                    if (part.Click != null && !part.Hit.IsEmpty && part.Hit.Contains(pt)) return part;
                return null;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                Cursor want = LinkAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
                if (Cursor != want) Cursor = want;
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (Cursor != Cursors.Default) Cursor = Cursors.Default;
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left) return;
                var part = LinkAt(e.Location);
                if (part != null) part.Click();
            }

            // ── painting ─────────────────────────────────────────────────────────────────────────────────────

            private static Color InkOf(LabelRole role, Palette p, Color accent)
            {
                switch (role)
                {
                    case LabelRole.Dim: return p.TextDim;
                    case LabelRole.Faint: return p.TextFaint;
                    case LabelRole.Accent: return accent;
                    case LabelRole.Danger: return p.Danger;
                    default: return p.Text;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                PaintBack(g, p);
                Place(Width, dpi);

                // The card: the palette's card colour, or the accent washed into it (the featured card: DF's 16% fill, 55% edge).
                Color fill = p.Window, edge = p.Divider;
                if (_style == BlockStyle.Feature) { fill = ColorMath.Mix(p.Card, p.Accent, 0.16f); edge = ColorMath.Mix(p.Card, p.Accent, 0.55f); }
                else if (_style == BlockStyle.Card) fill = p.Card;
                if (_style != BlockStyle.Plain && _card.Width > 2 && _card.Height > 2)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = DrawHelper.RoundedRect(new Rectangle(_card.X, _card.Y, _card.Width - 1, _card.Height - 1), Dpi.Px(Radius, dpi)))
                    {
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        using (var pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
                    }
                    g.SmoothingMode = SmoothingMode.None;
                }
                Color accent = ColorMath.Ink(p.Accent, fill);

                if (_sideImage != null) DrawPicture(g, _sideImage, _side);
                foreach (var part in _parts)
                {
                    switch (part.Kind)
                    {
                        case PartKind.Text:
                            TextRenderer.DrawText(g, part.Text, FontHelper.UiAt(part.Pt, dpi, part.Style), part.R1, InkOf(part.Ink, p, accent), DrawFlags(part.Align));
                            break;
                        case PartKind.Link:
                            DrawGlyph(g, part.Glyph, part.R2, accent, dpi);
                            TextRenderer.DrawText(g, part.Text, FontHelper.UiAt(part.Pt, dpi, part.Style), part.R1, InkOf(part.Ink, p, accent), DrawFlags(HorizontalAlignment.Left));
                            break;
                        case PartKind.Row:
                            TextRenderer.DrawText(g, part.Text, FontHelper.UiAt(part.Pt, dpi, part.Style), part.R1, InkOf(part.Ink, p, accent), DrawFlags(HorizontalAlignment.Left));
                            TextRenderer.DrawText(g, part.Text2, FontHelper.UiAt(part.Pt2, dpi, part.Style2), part.R2, InkOf(part.Ink2, p, accent), DrawFlags(HorizontalAlignment.Right));
                            break;
                        case PartKind.Divider:
                            using (var pen = new Pen(p.Divider, 1f)) g.DrawLine(pen, part.R1.Left, part.R1.Top, part.R1.Right - 1, part.R1.Top);
                            break;
                        case PartKind.Picture:
                            DrawPicture(g, part.Img, part.R1);
                            break;
                    }
                }
            }

            private static void DrawPicture(Graphics g, Image img, Rectangle r)
            {
                if (img == null || r.Width < 2 || r.Height < 2) return;
                var interpolation = g.InterpolationMode;
                var offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                try { g.DrawImage(img, r); }
                catch (Exception) { /* a bitmap GDI+ will not draw: leave the space empty rather than fail a paint */ }
                g.InterpolationMode = interpolation;
                g.PixelOffsetMode = offset;
            }

            /// <summary>The arrow or the heart in front of a link, drawn with GDI+ in the slot's left part, centred on the row
            /// (a font glyph would be tofu on RT 8.1 — see the class notes of AboutForm).</summary>
            private static void DrawGlyph(Graphics g, GlyphKind kind, Rectangle slot, Color ink, int dpi)
            {
                if (kind == GlyphKind.None || slot.Width < 1) return;
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float u = Dpi.F(1f, dpi), x = slot.X + u, cy = slot.Y + slot.Height / 2f;
                if (kind == GlyphKind.Arrow)
                {
                    using (var pen = new Pen(ink, Dpi.F(1.7f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        g.DrawLine(pen, x, cy, x + 11 * u, cy);
                        g.DrawLines(pen, new[] { new PointF(x + 7 * u, cy - 4 * u), new PointF(x + 11 * u, cy), new PointF(x + 7 * u, cy + 4 * u) });
                    }
                }
                else
                {
                    // A heart on a 20 x 18.35 unit grid (x 2..22, y 3..21.35), scaled to 12 logical px wide and centred.
                    float k = 12 * u / 20f, ox = x, oy = cy - 9.175f * k;
                    Func<float, float, PointF> h = (px, py) => new PointF(ox + (px - 2f) * k, oy + (py - 3f) * k);
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(h(12f, 21.35f), h(10.55f, 20.03f));
                        path.AddBezier(h(10.55f, 20.03f), h(5.4f, 15.36f), h(2f, 12.28f), h(2f, 8.5f));
                        path.AddBezier(h(2f, 8.5f), h(2f, 5.42f), h(4.42f, 3f), h(7.5f, 3f));
                        path.AddBezier(h(7.5f, 3f), h(9.24f, 3f), h(10.91f, 3.81f), h(12f, 5.09f));
                        path.AddBezier(h(12f, 5.09f), h(13.09f, 3.81f), h(14.76f, 3f), h(16.5f, 3f));
                        path.AddBezier(h(16.5f, 3f), h(19.58f, 3f), h(22f, 5.42f), h(22f, 8.5f));
                        path.AddBezier(h(22f, 8.5f), h(22f, 12.28f), h(18.6f, 15.36f), h(13.45f, 20.04f));
                        path.AddLine(h(13.45f, 20.04f), h(12f, 21.35f));
                        path.CloseFigure();
                        using (var b = new SolidBrush(ink)) g.FillPath(b, path);
                    }
                }
                g.SmoothingMode = smoothing;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_sideImage != null) { _sideImage.Dispose(); _sideImage = null; }
                    foreach (var part in _parts)
                        if (part.Img != null) { part.Img.Dispose(); part.Img = null; }
                }
                base.Dispose(disposing);
            }
        }
    }
}
