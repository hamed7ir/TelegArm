using System;
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
    /// plus the source/license links and the open-source credits. Plain Form + ThemedChrome (accent title bar
    /// + ✕ + drag + app icon), card layout, dark/accent themed — matching the app's other windows. Links open
    /// in the browser (RT-safe: Process.Start wrapped in try/catch).
    /// </summary>
    public sealed class AboutForm : Form
    {
        private const string RepoUrl = "https://github.com/hamed7ir/TelegArm";
        private const string WtcUrl = "https://github.com/wiz0u/WTelegramClient";
        private const string WtcGroupUrl = "https://t.me/WTelegramClient";   // the WTelegramClient Telegram group
        private const string SponsorUrl = "https://github.com/sponsors/wiz0u";
        private const string LicenseUrl = "https://github.com/hamed7ir/TelegArm/blob/main/LICENSE";

        private readonly Color _card, _border, _title, _sub, _accent;

        private string _pendingLink;
        /// <summary>A Telegram chat/group the user tapped in About — the caller opens it IN-APP after this
        /// modal dialog closes (About can't reach the chat view itself). Null if nothing in-app was tapped.</summary>
        public string PendingInAppLink => _pendingLink;

        public AboutForm(bool dark, Color accent)
        {
            _accent = accent;
            Text = "About TelegArm";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            ClientSize = new Size(468, 556 + ThemedChrome.BarH);

            var content = ThemedChrome.Apply(this, "About TelegArm", accent, dark);
            var scroll = ScrollHost.Wrap(content, dark, accent);

            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            scroll.BackColor = bg;
            _card = dark ? Color.FromArgb(50, 50, 55) : Color.White;
            _border = dark ? Color.FromArgb(62, 62, 68) : Color.FromArgb(226, 226, 230);
            _title = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            _sub = dark ? Color.FromArgb(158, 158, 165) : Color.FromArgb(110, 110, 116);
            Color featBg = Blend(accent, _card, 0.16f);
            Color featBrd = Blend(accent, _card, 0.55f);

            const int M = 22, CW = 424;   // left margin, content width
            int y = 24;

            // ── Header: icon + name + version + description (centered) ──
            var pic = new PictureBox
            {
                Size = new Size(100, 100), Location = new Point(M + (CW - 100) / 2, y),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent
            };
            pic.Image = LoadAppImage(200);
            scroll.Controls.Add(pic);
            y += 100 + 10;

            scroll.Controls.Add(Text2("TelegArm", M, y, CW, _title, FontHelper.Ui(20f, FontStyle.Bold), 34, bg, ContentAlignment.MiddleCenter)); y += 34;
            scroll.Controls.Add(Text2("Version " + Program.Version, M, y, CW, _sub, FontHelper.Ui(9.5f), 20, bg, ContentAlignment.MiddleCenter)); y += 22;
            scroll.Controls.Add(Text2("A pure-managed Telegram client for legacy ARM32 Windows.", M, y, CW, _sub, FontHelper.Ui(9f), 34, bg, ContentAlignment.TopCenter)); y += 42;

            // ── FEATURED — WTelegramClient / Wizou (accent-tinted, emphasized) ──
            var feat = Card(M, y, CW, 196, featBg, featBrd);
            scroll.Controls.Add(feat);
            feat.Controls.Add(Text2("Built on WTelegramClient", 16, 14, CW - 32, accent, FontHelper.Ui(12f, FontStyle.Bold), 24, featBg, ContentAlignment.MiddleLeft));
            feat.Controls.Add(Text2("TelegArm is built entirely on WTelegramClient — the pure-C# MTProto library by Wizou (wiz0u), developed and maintained by one person. Please consider supporting his work.",
                16, 42, CW - 32, _title, FontHelper.Ui(9f), 58, featBg, ContentAlignment.TopLeft));
            feat.Controls.Add(Link("→  github.com/wiz0u/WTelegramClient", 16, 108, CW - 32, WtcUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            feat.Controls.Add(InAppLink("→  @WTelegramClient — Telegram group", 16, 134, CW - 32, WtcGroupUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            feat.Controls.Add(Link("♥  Support Wizou — GitHub Sponsors", 16, 160, CW - 32, SponsorUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            y += 196 + 14;

            // ── Open-RT community tribute (mirrors the WTC card's style; adds the openrt.png logo) ──
            const int orH = 160, orTx = 16 + 72 + 14, orTw = CW - (16 + 72 + 14) - 16;   // logo box + text column
            var orCard = Card(M, y, CW, orH, _card, _border);
            scroll.Controls.Add(orCard);
            var orPic = new PictureBox
            {
                Size = new Size(72, 72), Location = new Point(16, 24),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = _card
            };
            orPic.Image = LoadImageFile("openrt.png", 144);   // shipped next to the exe (Content, PreserveNewest)
            orCard.Controls.Add(orPic);
            orCard.Controls.Add(Text2("Open-RT Community", orTx, 18, orTw, accent, FontHelper.Ui(12f, FontStyle.Bold), 24, _card, ContentAlignment.MiddleLeft));
            orCard.Controls.Add(Text2("Thanks to the Open-RT community for bringing TelegArm to Windows RT:", orTx, 46, orTw, _sub, FontHelper.Ui(9f), 34, _card, ContentAlignment.TopLeft));
            orCard.Controls.Add(Text2("samyryu · RHLM fan · Artus100 · Keith · jeybee · die Oberfläche knacken", orTx, 84, orTw, _title, FontHelper.Ui(9.5f, FontStyle.Bold), 64, _card, ContentAlignment.TopLeft));
            y += orH + 14;

            // ── Source + license ──
            var meta = Card(M, y, CW, 92, _card, _border);
            scroll.Controls.Add(meta);
            MetaRow(meta, 0, "Source code", "github.com/hamed7ir/TelegArm", RepoUrl, CW);
            meta.Controls.Add(new Panel { Location = new Point(14, 46), Size = new Size(CW - 28, 1), BackColor = _border });
            MetaRow(meta, 46, "License", "GPL-3.0-only", LicenseUrl, CW);
            y += 92 + 14;

            // ── Open-source credits ──
            string[][] credits =
            {
                new[] { "MaterialSkin.2", "MIT" },
                new[] { "LibVLCSharp · libVLC", "LGPL-2.1" },
                new[] { "Newtonsoft.Json", "MIT" },
                new[] { "NAudio", "Ms-PL" },
                new[] { "Concentus (Opus)", "BSD-3-Clause" },
                new[] { "SixLabors.ImageSharp", "Apache-2.0" },
                new[] { "QRCoder", "MIT" },
                new[] { "rlottie", "MIT" },
                new[] { "Vazirmatn font", "SIL OFL 1.1" },
                new[] { "Roboto font", "Apache-2.0" },
                new[] { "Noto Emoji", "Apache-2.0" },
            };
            int ch = 40 + credits.Length * 24 + 8;
            var cr = Card(M, y, CW, ch, _card, _border);
            scroll.Controls.Add(cr);
            cr.Controls.Add(Text2("OPEN-SOURCE CREDITS", 16, 12, CW - 32, accent, FontHelper.Ui(8.25f, FontStyle.Bold), 18, _card, ContentAlignment.MiddleLeft));
            int ry = 38;
            foreach (var row in credits)
            {
                cr.Controls.Add(Text2(row[0], 16, ry, CW - 170, _title, FontHelper.Ui(9f), 22, _card, ContentAlignment.MiddleLeft));
                cr.Controls.Add(Text2(row[1], CW - 152, ry, 136, _sub, FontHelper.Ui(8.5f), 22, _card, ContentAlignment.MiddleRight));
                ry += 24;
            }
            y += ch + 14;

            // ── Footer ──
            scroll.Controls.Add(Text2("Full license texts: THIRD-PARTY-NOTICES.txt (in the repo root).", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, bg, ContentAlignment.MiddleCenter)); y += 20;
            scroll.Controls.Add(Text2("© 2026 TelegArm · Licensed under GPL-3.0-only", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, bg, ContentAlignment.MiddleCenter)); y += 30;

            scroll.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(4, 8), BackColor = bg });   // extend the scroll extent
        }

        private void MetaRow(Panel card, int cy, string left, string linkText, string url, int cw)
        {
            card.Controls.Add(Text2(left, 16, cy, 150, _title, FontHelper.Ui(9.5f), 46, _card, ContentAlignment.MiddleLeft));
            var link = Link(linkText, 160, cy, cw - 176, url, FontHelper.Ui(9.5f, FontStyle.Bold), _card);
            link.Height = 46; link.TextAlign = ContentAlignment.MiddleRight;
            card.Controls.Add(link);
        }

        private static Label Text2(string text, int x, int y, int w, Color color, Font font, int h, Color parentBg, ContentAlignment align)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, h),
                ForeColor = color, BackColor = parentBg, Font = font, TextAlign = align
            };
        }

        private Label StyledLink(string text, int x, int y, int w, Font font, Color parentBg)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, 22),
                ForeColor = _accent, BackColor = parentBg, Font = font, Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        /// <summary>A web link — opens in the browser (Process.Start).</summary>
        private Label Link(string text, int x, int y, int w, string url, Font font, Color parentBg)
        {
            var l = StyledLink(text, x, y, w, font, parentBg);
            l.Click += (s, e) => OpenUrl(url);
            return l;
        }

        /// <summary>A Telegram chat/group link — closes About and hands the link to the caller to open IN-APP
        /// (the chat view), instead of the browser.</summary>
        private Label InAppLink(string text, int x, int y, int w, string url, Font font, Color parentBg)
        {
            var l = StyledLink(text, x, y, w, font, parentBg);
            l.Click += (s, e) => { _pendingLink = url; DialogResult = DialogResult.OK; Close(); };
            return l;
        }

        private Panel Card(int x, int y, int w, int h, Color fill, Color brd)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = fill };
            using (var path = DrawHelper.RoundedRect(new Rectangle(0, 0, w, h), 12))
                p.Region = new Region(path);
            p.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(brd))
                using (var pa = DrawHelper.RoundedRect(new Rectangle(0, 0, w - 1, h - 1), 12))
                    g.DrawPath(pen, pa);
            };
            return p;
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
                            g.DrawImage(src, new Rectangle(0, 0, size, size));
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
                        g.DrawImage(src, new Rectangle(0, 0, size, size));
                    }
                    return bmp;
                }
            }
            catch { return null; }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }
    }
}
