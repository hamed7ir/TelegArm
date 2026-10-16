using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>Redesign steps 5.4/5.5 — the chat background: every message row (and the chat panel between rows) paints its
    /// slice of one backdrop instead of clearing to ChatBg.
    /// ★ PLAIN IS THE FAST PATH: the default ("" mode) is today's flat ChatBg; callers check <see cref="IsPlain"/> and keep
    ///   their g.Clear, so nothing changes (or costs) for anyone who never picks a background.
    /// ★ SCROLL-INVARIANT kinds (solid, a left→right gradient, a pattern) are sampled in CONTENT coordinates (the row's
    ///   place in the scrolled list), so the ScrollWindowEx blit of a scroll stays correct and nothing repaints.
    /// ★ VIEWPORT-FIXED kinds (a top→bottom or diagonal gradient, a photo) are sampled in the panel's CLIENT coordinates from
    ///   one cached bitmap the size of the panel (+64 px slack); they cannot be blitted, so the chat repaints its visible rows
    ///   on every scroll step (MainForm invalidates on Scrolled when <see cref="ScrollInvariant"/> is false). Gate G5 measures
    ///   that cost on the Surface.
    /// Selection and flash tints are overlays the rows draw over the slice.</summary>
    internal static class ChatBackdrop
    {
        public enum Kind { Plain, Solid, Gradient, Pattern, Photo }

        /// <summary>A resolved background for one mode.</summary>
        public sealed class Spec
        {
            public Kind Kind;
            public Color C1, C2;
            public int Angle;          // 0 left→right, 90 top→bottom, 45 diagonal
            public string Pattern;     // dots | grid | lines
            public string ImagePath, Fit;
            public int Dim, ImageRev;
            public bool Blur;
            public bool ScrollInvariant { get { return Kind != Kind.Photo && !(Kind == Kind.Gradient && Angle != 0); } }
        }

        /// <summary>Raised after a change (settings, theme, an imported photo): the chat repaints.</summary>
        public static event Action Changed;

        private static int _rev;
        private static Spec _specLight, _specDark;
        private static int _specRev = -1;
        private static Palette _specPalLight, _specPalDark;

        /// <summary>Call after the background settings changed (Settings, import, reset). Drops every cache, raises Changed.</summary>
        public static void NotifyChanged()
        {
            _rev++;
            DropCaches();
            var h = Changed;
            if (h != null) h();
        }

        /// <summary>The background of a mode, resolved from AppSettings.Theme and that mode's palette.</summary>
        public static Spec Resolve(bool dark)
        {
            var pal = ThemeHelper.PaletteFor(dark);
            if (_specRev != _rev) { _specLight = _specDark = null; _specRev = _rev; }
            if (dark ? (_specDark != null && ReferenceEquals(_specPalDark, pal)) : (_specLight != null && ReferenceEquals(_specPalLight, pal)))
                return dark ? _specDark : _specLight;
            var s = Build(AppSettings.Instance.Theme.For(dark), pal, dark);
            if (dark) { _specDark = s; _specPalDark = pal; } else { _specLight = s; _specPalLight = pal; }
            return s;
        }

        public static bool IsPlain(bool dark) { return Resolve(dark).Kind == Kind.Plain; }
        /// <summary>Plain under the window backdrop (Wallpaper) is screen-aligned too: it cannot be blitted by a scroll.</summary>
        public static bool ScrollInvariant(bool dark) { var s = Resolve(dark); return s.Kind == Kind.Plain ? !WindowBackdrop.Enabled : s.ScrollInvariant; }

        private static Spec Build(ChatBackgroundSettings cs, Palette pal, bool dark)
        {
            var s = new Spec { Kind = Kind.Plain, C1 = pal.ChatBg, C2 = pal.ChatBg, Fit = "fill" };
            if (cs == null) return s;
            switch (cs.Mode)
            {
                case "preset":
                    if (cs.Preset < 0 || cs.Preset >= Palette.ChatPresets.Length) return s;
                    var pr = Palette.ChatPresets[cs.Preset];
                    s.C1 = dark ? pr.Dark1 : pr.Light1;
                    s.C2 = dark ? pr.Dark2 : pr.Light2;
                    s.Kind = pr.Kind == "gradient" ? Kind.Gradient : pr.Kind == "pattern" ? Kind.Pattern : Kind.Solid;
                    s.Pattern = pr.Pattern;
                    return s;
                case "solid":
                    s.C1 = ThemeHelper.ParseAccent(cs.Color1) ?? pal.ChatBg; s.Kind = Kind.Solid; return s;
                case "gradient":
                    s.C1 = ThemeHelper.ParseAccent(cs.Color1) ?? pal.ChatBg; s.C2 = ThemeHelper.ParseAccent(cs.Color2) ?? pal.ChatBg;
                    s.Angle = cs.Angle; s.Kind = Kind.Gradient; return s;
                case "pattern":
                    s.C1 = ThemeHelper.ParseAccent(cs.Color1) ?? pal.ChatBg; s.Pattern = string.IsNullOrEmpty(cs.Pattern) ? "dots" : cs.Pattern;
                    s.Kind = Kind.Pattern; return s;
                case "photo":
                    string path = PhotoPath(cs.ImageFile);
                    if (path == null || !File.Exists(path)) return s;   // a missing file falls back to plain, never crashes
                    s.ImagePath = path; s.Fit = cs.Fit; s.Dim = cs.Dim; s.Blur = cs.Blur; s.ImageRev = cs.ImageRev; s.Kind = Kind.Photo;
                    return s;
            }
            return s;
        }

        // ── painting ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Paints <paramref name="row"/>'s slice of the backdrop into <paramref name="clip"/> (row client coordinates).
        /// The row's parent is the chat panel. Never throws (a paint must not: INVARIANTS 9) — it falls back to ChatBg.</summary>
        public static void Paint(Graphics g, Control row, Rectangle clip, bool dark)
        {
            var host = row.Parent;
            Point inHost = host != null ? row.Location : Point.Empty;
            PaintAt(g, host, inHost, clip, dark);
        }

        /// <summary>The rows' one call. Plain: g.Clear(<paramref name="plain"/>) exactly as before (pixel-identical, no cost).
        /// Otherwise the row's slice, then the selection tint (<paramref name="tint"/>) as an overlay.</summary>
        public static void PaintRow(Graphics g, Control row, Rectangle clip, bool dark, Color plain, Color? tint)
        {
            var smooth = g.SmoothingMode;   // the rows turn anti-aliasing on; a fill must cover its edge pixels fully
            g.SmoothingMode = SmoothingMode.None;
            try { PaintRowCore(g, row, clip, dark, plain, tint); }
            finally { g.SmoothingMode = smooth; }
        }

        private static void PaintRowCore(Graphics g, Control row, Rectangle clip, bool dark, Color plain, Color? tint)
        {
            if (IsPlain(dark))
            {
                if (!WindowBackdrop.Enabled) { g.Clear(plain); return; }
                // step 5.9 (owner: the main window too): the plain chat over the window backdrop, tinted with ChatBg
                WindowBackdrop.Paint(g, row, clip, ThemeHelper.PaletteFor(dark).ChatBg);
            }
            else Paint(g, row, clip, dark);
            if (tint.HasValue) using (var b = new SolidBrush(ColorMath.WithAlpha(tint.Value, 46))) g.FillRectangle(b, clip);
        }

        /// <summary>Paints the chat panel's own background (between and below the rows).</summary>
        public static void PaintHost(Graphics g, Control host, Rectangle clip, bool dark)
        {
            if (IsPlain(dark) && WindowBackdrop.Enabled) { WindowBackdrop.Paint(g, host, clip, ThemeHelper.PaletteFor(dark).ChatBg); return; }
            PaintAt(g, host, Point.Empty, clip, dark);
        }

        private static void PaintAt(Graphics g, Control host, Point offset, Rectangle clip, bool dark)
        {
            var s = Resolve(dark);
            try
            {
                switch (s.Kind)
                {
                    case Kind.Plain: Fill(g, ThemeHelper.PaletteFor(dark).ChatBg, clip); return;
                    case Kind.Solid: Fill(g, s.C1, clip); return;
                }
                var scroll = host is ScrollableControl ? ((ScrollableControl)host).AutoScrollPosition : Point.Empty;   // <= 0
                if (s.Kind == Kind.Gradient && s.Angle == 0)
                {
                    int w = Math.Max(1, host != null ? host.ClientSize.Width : clip.Width);
                    using (var lg = new LinearGradientBrush(new Rectangle(-offset.X, 0, w, 1), s.C1, s.C2, LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipX })
                        g.FillRectangle(lg, clip);
                    return;
                }
                if (s.Kind == Kind.Pattern)
                {
                    int dpi = host != null ? Dpi.Of(host) : Dpi.Logical;
                    var tile = Tile(s, dark, dpi);
                    using (var tb = new TextureBrush(tile, WrapMode.Tile))
                    {
                        // content coordinates: the slice stays put while the list scrolls (blit-safe)
                        int cx = offset.X - scroll.X, cy = offset.Y - scroll.Y;
                        tb.TranslateTransform(-(cx % tile.Width), -(cy % tile.Height));
                        g.FillRectangle(tb, clip);
                    }
                    return;
                }
                // viewport-fixed: a vertical / diagonal gradient, a photo
                var view = Viewport(s, host, dark);
                if (view == null) { Fill(g, ThemeHelper.PaletteFor(dark).ChatBg, clip); return; }
                var src = new Rectangle(clip.X + offset.X, clip.Y + offset.Y, clip.Width, clip.Height);
                var oldComp = g.CompositingMode; var oldInterp = g.InterpolationMode; var oldPix = g.PixelOffsetMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(view, clip, src, GraphicsUnit.Pixel);
                g.CompositingMode = oldComp; g.InterpolationMode = oldInterp; g.PixelOffsetMode = oldPix;
            }
            catch { try { Fill(g, ThemeHelper.PaletteFor(dark).ChatBg, clip); } catch { } }
        }

        private static void Fill(Graphics g, Color c, Rectangle clip) { using (var b = new SolidBrush(c)) g.FillRectangle(b, clip); }

        // ── caches ─────────────────────────────────────────────────────────────────────────────────────────────

        private static Bitmap _view; private static Spec _viewSpec; private static Size _viewFor; private static int _viewDpi;
        private static Bitmap _tile; private static Spec _tileSpec; private static int _tileDpi;
        private static Bitmap _photo; private static string _photoKey;

        /// <summary>The panel-sized bake (rounded up to 64 px, so a resize drag rebakes once per step, not per pixel).</summary>
        private static Bitmap Viewport(Spec s, Control host, bool dark)
        {
            if (host == null) return null;
            var cs = host.ClientSize;
            if (cs.Width <= 0 || cs.Height <= 0) return null;
            int dpi = Dpi.Of(host);
            if (_view != null && ReferenceEquals(_viewSpec, s) && _viewDpi == dpi && _viewFor.Width >= cs.Width && _viewFor.Height >= cs.Height
                && _viewFor.Width - cs.Width < 128 && _viewFor.Height - cs.Height < 128)
                return _view;
            var size = new Size((cs.Width + 63) / 64 * 64, (cs.Height + 63) / 64 * 64);
            var bmp = Bake(s, size, dpi, dark);
            if (_view != null) _view.Dispose();
            _view = bmp; _viewSpec = s; _viewFor = size; _viewDpi = dpi;
            return _view;
        }

        /// <summary>Frees the bakes (on minimise, on a change). The photo stays decoded only while it is the background.</summary>
        public static void DropCaches()
        {
            if (_view != null) { _view.Dispose(); _view = null; }
            if (_tile != null) { _tile.Dispose(); _tile = null; }
            _viewSpec = _tileSpec = null;
        }

        /// <summary>Releases the viewport bake while the window is minimised (it is rebuilt on the next paint).</summary>
        public static void Release() { if (_view != null) { _view.Dispose(); _view = null; _viewSpec = null; } }

        // ── baking ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The whole backdrop of <paramref name="dark"/>'s mode at <paramref name="size"/> (the viewport cache, and the
        /// 5.7 preview tile). The caller owns the bitmap.</summary>
        public static Bitmap Bake(bool dark, Size size, int dpi) { return Bake(Resolve(dark), size, dpi, dark); }

        /// <summary>As <see cref="Bake(bool, Size, int)"/> for settings that are not (yet) applied: the preset tiles and the
        /// Settings preview while a slider is dragged. Shares no cache with the live chat.</summary>
        public static Bitmap BakeSettings(ChatBackgroundSettings cs, bool dark, Size size, int dpi)
        {
            var s = Build(cs, ThemeHelper.PaletteFor(dark), dark);
            if (s.Kind == Kind.Pattern) { var t = TileFor(s, dark, dpi); try { return BakeWith(s, size, dpi, dark, t); } finally { t.Dispose(); } }
            return BakeWith(s, size, dpi, dark, null);
        }

        private static Bitmap Bake(Spec s, Size size, int dpi, bool dark) { return BakeWith(s, size, dpi, dark, s.Kind == Kind.Pattern ? Tile(s, dark, dpi) : null); }

        private static Bitmap BakeWith(Spec s, Size size, int dpi, bool dark, Bitmap tile)
        {
            var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var r = new Rectangle(Point.Empty, bmp.Size);
                var bg = ThemeHelper.PaletteFor(dark).ChatBg;
                switch (s.Kind)
                {
                    case Kind.Plain: Fill(g, bg, r); break;
                    case Kind.Solid: Fill(g, s.C1, r); break;
                    case Kind.Gradient:
                        using (var lg = new LinearGradientBrush(r, s.C1, s.C2, s.Angle == 0 ? 0f : s.Angle == 90 ? 90f : 45f)) g.FillRectangle(lg, r);
                        break;
                    case Kind.Pattern:
                        using (var tb = new TextureBrush(tile, WrapMode.Tile)) g.FillRectangle(tb, r);
                        break;
                    case Kind.Photo:
                        Fill(g, bg, r);
                        var img = Photo(s);
                        if (img != null) DrawPhoto(g, img, r, s.Fit);
                        if (s.Dim > 0) using (var b = new SolidBrush(ColorMath.WithAlpha(ThemeHelper.PaletteFor(dark).MediaViewerBg, s.Dim * 255 / 100))) g.FillRectangle(b, r);
                        break;
                }
            }
            return bmp;
        }

        private static void DrawPhoto(Graphics g, Image img, Rectangle r, string fit)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (fit == "center")
            {
                g.DrawImage(img, new Rectangle(r.X + (r.Width - img.Width) / 2, r.Y + (r.Height - img.Height) / 2, img.Width, img.Height));
                return;
            }
            double sx = r.Width / (double)img.Width, sy = r.Height / (double)img.Height;
            double k = fit == "fit" ? Math.Min(sx, sy) : Math.Max(sx, sy);   // fill = cover (crop), fit = contain
            int w = (int)Math.Ceiling(img.Width * k), h = (int)Math.Ceiling(img.Height * k);
            g.DrawImage(img, new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
        }

        /// <summary>The decoded photo (blurred when asked), loaded from bytes so the file is never locked.</summary>
        private static Image Photo(Spec s)
        {
            string key = s.ImagePath + "|" + s.ImageRev + "|" + s.Blur;
            if (_photo != null && _photoKey == key) return _photo;
            if (_photo != null) { _photo.Dispose(); _photo = null; }
            try
            {
                var bytes = File.ReadAllBytes(s.ImagePath);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    Bitmap copy = new Bitmap(img);
                    if (s.Blur)
                    {
                        // soft blur: down to an eighth, back up with bilinear filtering
                        int w = Math.Max(1, copy.Width / 8), h = Math.Max(1, copy.Height / 8);
                        using (var small = new Bitmap(w, h))
                        {
                            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(copy, 0, 0, w, h); }
                            var up = new Bitmap(copy.Width, copy.Height);
                            using (var g = Graphics.FromImage(up)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(small, 0, 0, up.Width, up.Height); }
                            copy.Dispose();
                            copy = up;
                        }
                    }
                    _photo = copy; _photoKey = key;
                }
            }
            catch (Exception ex) { Logger.Diag("[CHATBG] photo load failed: " + ex.Message); _photo = null; _photoKey = null; }
            return _photo;
        }

        /// <summary>A pattern tile (own art): ink a few percent toward the text colour over the pattern's colour.</summary>
        private static Bitmap Tile(Spec s, bool dark, int dpi)
        {
            if (_tile != null && ReferenceEquals(_tileSpec, s) && _tileDpi == dpi) return _tile;
            var bmp = TileFor(s, dark, dpi);
            if (_tile != null) _tile.Dispose();
            _tile = bmp; _tileSpec = s; _tileDpi = dpi;
            return _tile;
        }

        /// <summary>A fresh pattern tile; the caller owns it.</summary>
        private static Bitmap TileFor(Spec s, bool dark, int dpi)
        {
            int n = Dpi.Px(24, dpi);
            var bmp = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var pal = ThemeHelper.PaletteFor(dark);
                g.Clear(s.C1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var ink = ColorMath.Mix(s.C1, pal.Text, dark ? 0.10f : 0.07f);
                using (var pen = new Pen(ink, Math.Max(1f, Dpi.F(1f, dpi))))
                using (var b = new SolidBrush(ink))
                {
                    switch (s.Pattern)
                    {
                        case "grid": g.DrawLine(pen, 0, 0, n, 0); g.DrawLine(pen, 0, 0, 0, n); break;
                        case "lines": g.DrawLine(pen, 0, n, n, 0); g.DrawLine(pen, -n / 2, n / 2, n / 2, -n / 2); g.DrawLine(pen, n / 2, n + n / 2, n + n / 2, n / 2); break;
                        default: float d = Dpi.F(3f, dpi); g.FillEllipse(b, n / 2f - d / 2, n / 2f - d / 2, d, d); break;   // dots
                    }
                }
            }
            return bmp;
        }

        // ── photo import (off the UI thread) ──────────────────────────────────────────────────────────────────

        private static string ThemeDir { get { return Path.Combine(StoragePaths.ResolveDataRoot(), "theme"); } }

        private static string PhotoPath(string file)
        {
            if (string.IsNullOrEmpty(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return Path.Combine(ThemeDir, file);
        }

        /// <summary>Imports a picture as <paramref name="dark"/>'s chat background: reads its size first, refuses one over
        /// 16 MP on ARM32 (a full 32-bit decode of a 25 MB JPEG is 400 MB), downscales to a long edge of 1920 (1280 on ARM32),
        /// saves a JPEG (quality 85) to &lt;DataRoot&gt;\theme\chatbg-light|dark.jpg via temp + replace — not under Cache\, which is
        /// pruned. Returns the file name, or throws with a message for the user. Run it off the UI thread.</summary>
        public static string ImportPhoto(string source, bool dark)
        {
            var info = new FileInfo(source);
            if (!info.Exists) throw new InvalidOperationException("The file is gone.");
            if (info.Length > 60L * 1024 * 1024) throw new InvalidOperationException("That file is too big (over 60 MB).");
            byte[] bytes = File.ReadAllBytes(source);
            int w, h;
            using (var ms = new MemoryStream(bytes))
            using (var probe = Image.FromStream(ms, false, false)) { w = probe.Width; h = probe.Height; }
            double mp = w * (double)h / 1e6;
            if (Platform.IsArm32 && mp > 16) throw new InvalidOperationException("That picture is " + mp.ToString("0") + " megapixels; this device can use up to 16. Pick a smaller one.");
            int longEdge = Platform.IsArm32 ? 1280 : 1920;
            double k = Math.Min(1.0, longEdge / (double)Math.Max(w, h));
            int nw = Math.Max(1, (int)Math.Round(w * k)), nh = Math.Max(1, (int)Math.Round(h * k));
            Directory.CreateDirectory(ThemeDir);
            string name = dark ? "chatbg-dark.jpg" : "chatbg-light.jpg";
            string target = Path.Combine(ThemeDir, name), tmp = target + ".tmp";
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                using (var outBmp = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, nw, nh));
                    }
                    var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                        outBmp.Save(tmp, codec, ep);
                    }
                }
            }
            catch (OutOfMemoryException) { TryDelete(tmp); throw new InvalidOperationException("There was not enough memory to open that picture. Try a smaller one."); }
            if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
            return name;
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // ── pills over a background ───────────────────────────────────────────────────────────────────────────

        /// <summary>True when service lines and the date pill need the scrim (any background other than plain).</summary>
        public static bool PillsNeedScrim(bool dark) { return !IsPlain(dark) || WindowBackdrop.Enabled; }
    }
}
