using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 5.9 — the window backdrop, "faux Mica" (owner's choice 2026-10-13): TelegArm draws a blurred,
    /// tinted copy of the desktop wallpaper behind its chrome, on every OS including RT 8.1 (real Mica needs Windows 11 22621+
    /// and alpha-0 GDI painting; Windows 8 has no blur).
    /// ★ BAKE ONCE: the wallpaper of every monitor, laid out over the virtual screen at a QUARTER of its resolution and
    ///   blurred (down to a sixteenth and back up), is rebuilt only when the wallpaper or the displays change. The surface's
    ///   own colour is laid over it at paint time (85% in light, 80% in dark), so one bake serves the window, the footer, the
    ///   Settings rail…
    /// ★ PAINT A SCREEN-ALIGNED SLICE: a surface draws the part of the bake under its screen rectangle, so it looks like a
    ///   pane of frosted glass over the desktop. A move, or a scroll inside a surface, repaints the surfaces (Mica re-blits
    ///   on move too): AppForm.OnMove and <see cref="Attach"/>'s scroll hooks do that.
    /// ★ ONLY BACKGROUNDS: windows, gutters, dialog bodies and footers, the Settings rail and pages. Cards, rows, text, the
    ///   EDITs, VLC and the chat list stay opaque. The four "nearest opaque ancestor" lookups (setting rows, setting groups,
    ///   themed labels, scrollbar lanes) go through <see cref="ClearBehind"/>, which paints the slice when that ancestor is a
    ///   registered surface.
    /// ★ SETTING: Appearance → "Window background: Solid / Wallpaper", default Solid (INVARIANT 4). Forced off in High
    ///   Contrast and when Windows' transparency effects are off. TELEGARM_WALLPAPER=&lt;picture&gt; forces it on with that
    ///   picture (the screenshot rig).</summary>
    internal static class WindowBackdrop
    {
        private static readonly ConditionalWeakTable<Control, object> _surfaces = new ConditionalWeakTable<Control, object>();
        private static readonly object Mark = new object();
        private static bool _wanted, _allowed = true, _hooked;
        private static string _forcedPicture;
        private static Bitmap _bake;           // the virtual screen at 1/4, blurred
        private static Rectangle _bakeFor;     // the virtual screen it was baked for
        private const int Scale = 4;

        /// <summary>The backdrop is on: the setting asks for it and Windows allows it (not High Contrast, transparency on).</summary>
        public static bool Enabled { get { return (_wanted || _forcedPicture != null) && _allowed; } }

        /// <summary>Startup: the setting ("wallpaper" = on) and TELEGARM_WALLPAPER. Never throws.</summary>
        public static void Init(string setting)
        {
            try { var f = Environment.GetEnvironmentVariable("TELEGARM_WALLPAPER"); if (!string.IsNullOrEmpty(f) && File.Exists(f)) _forcedPicture = f; } catch { }
            _wanted = setting == "wallpaper";
            ReadAllowed();
            Hook();
        }

        /// <summary>Settings: turn it on or off, live. Every open window repaints.</summary>
        public static void SetWanted(bool on)
        {
            if (_wanted == on) return;
            _wanted = on;
            if (!Enabled) DropBake();
            RepaintAll();
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop || e.Category == Microsoft.Win32.UserPreferenceCategory.General
                        || e.Category == Microsoft.Win32.UserPreferenceCategory.Accessibility)
                    { ReadAllowed(); DropBake(); RepaintAll(); }
                };
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => { DropBake(); RepaintAll(); };
            }
            catch { }
            TelegArm.UI.Controls.TouchScroller.Scrolled += sc => { if (Enabled && sc != null && IsSurface(sc)) sc.Invalidate(true); };
        }

        /// <summary>High Contrast off and Windows' transparency effects on (a missing value — Windows 8.1 has no switch — = on).</summary>
        private static void ReadAllowed()
        {
            bool allowed = !SystemInformation.HighContrast;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k != null ? k.GetValue("EnableTransparency") : null;
                    if (v is int && (int)v == 0) allowed = false;
                }
            }
            catch { }
            _allowed = allowed;
        }

        // ── surfaces ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Makes <paramref name="c"/> a backdrop surface: when the backdrop is on, its background is the slice (painted
        /// in its Paint pass, over the BackColor fill), and a scroll inside it repaints it (the slice must not move with
        /// the content).</summary>
        public static void Attach(Control c)
        {
            if (c == null || IsSurface(c)) return;
            _surfaces.Add(c, Mark);
            c.Paint += (s, e) => { if (Enabled) Paint(e.Graphics, c, e.ClipRectangle, c.BackColor); };
            var sc = c as ScrollableControl;
            if (sc != null)
            {
                sc.Scroll += (s, e) => { if (Enabled) sc.Invalidate(true); };
                sc.MouseWheel += (s, e) => { if (Enabled) try { sc.BeginInvoke((Action)(() => sc.Invalidate(true))); } catch { } };
            }
        }

        /// <summary>Attaches <paramref name="root"/> and every plain container under it (panels, tables, flow panels) — the
        /// surfaces of a whole pane — except <paramref name="skip"/> and what is under it (the chat area paints its own).</summary>
        public static void AttachTree(Control root, Control skip)
        {
            if (root == null || root == skip) return;
            // a Panel only when it CONTAINS controls: a childless panel usually draws its own content in Paint (the header
            // avatar), and the backdrop would be painted over it
            if (root is TableLayoutPanel || root is FlowLayoutPanel || (root is Panel && root.HasChildren)) Attach(root);
            foreach (Control c in root.Controls) AttachTree(c, skip);
        }

        /// <summary>Marks a control that paints its own backdrop (the Settings rail, a form's background) as a surface, so the
        /// controls on it find the slice through <see cref="ClearBehind"/>.</summary>
        public static void Mark_(Control c) { if (c != null && !IsSurface(c)) _surfaces.Add(c, Mark); }

        public static bool IsSurface(Control c) { object o; return c != null && _surfaces.TryGetValue(c, out o); }

        /// <summary>The four "nearest opaque ancestor" lookups: clears <paramref name="clip"/> of <paramref name="paintFor"/> with
        /// what is behind it — the slice, when the nearest opaque ancestor above <paramref name="walkFrom"/> is a surface and
        /// the backdrop is on; else that ancestor's colour (or <paramref name="fallback"/>), exactly as before.</summary>
        public static void ClearBehind(Graphics g, Control paintFor, Control walkFrom, Rectangle clip, Color fallback)
        {
            Control c = walkFrom;
            while (c != null && c.BackColor.A != 255) c = c.Parent;
            Color col = c != null ? c.BackColor : fallback;
            if (Enabled && c != null && IsSurface(c)) Paint(g, paintFor, clip, col);
            else g.Clear(col);
        }

        /// <summary>Repaints every registered surface of <paramref name="f"/> and the form (after a move).</summary>
        public static void RepaintForm(Form f)
        {
            if (!Enabled || f == null || f.IsDisposed) return;
            f.Invalidate(false);
            Walk(f);
        }

        private static void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (IsSurface(c)) c.Invalidate(true);
                else if (c.HasChildren) Walk(c);
            }
        }

        private static void RepaintAll()
        {
            try { foreach (Form f in Application.OpenForms) if (!f.IsDisposed) { f.Invalidate(true); } } catch { }
        }

        // ── painting ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The slice of the backdrop under <paramref name="c"/>'s <paramref name="clip"/>, with <paramref name="tint"/>
        /// laid over it (85% light / 80% dark). Off, or when the bake fails: a plain <paramref name="tint"/> fill. Never throws.</summary>
        public static void Paint(Graphics g, Control c, Rectangle clip, Color tint)
        {
            // ⚠ No anti-aliasing for these fills: a row that turned it on (the chat rows do) filled its edge pixel rows only
            //   half, and every row boundary showed a half-tinted line (owner test 2026-10-15).
            var smooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            try
            {
                if (!Enabled) { using (var b = new SolidBrush(tint)) g.FillRectangle(b, clip); return; }
                var bake = Bake();
                Point screen = c.PointToScreen(Point.Empty);
                if (bake != null)
                {
                    // ⚠ GDI+ mirrors / clamps at the edges of the SOURCE RECTANGLE, not of the bitmap: a slice sampled exactly
                    //   showed a 1-px seam at every row boundary (owner test 2026-10-15). Sample 2 bake px more on every side and
                    //   clip to the real area.
                    const int m = 2;
                    float sx = (screen.X + clip.X - _bakeFor.X) / (float)Scale, sy = (screen.Y + clip.Y - _bakeFor.Y) / (float)Scale;
                    var dst = new Rectangle(clip.X - m * Scale, clip.Y - m * Scale, clip.Width + 2 * m * Scale, clip.Height + 2 * m * Scale);
                    var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode; var oldC = g.CompositingMode;
                    var state = g.Save();
                    g.SetClip(clip, CombineMode.Intersect);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(bake, dst, sx - m, sy - m, clip.Width / (float)Scale + 2 * m, clip.Height / (float)Scale + 2 * m, GraphicsUnit.Pixel, ia);
                    }
                    g.Restore(state);
                    g.InterpolationMode = oldI; g.PixelOffsetMode = oldP; g.CompositingMode = oldC;
                }
                int a = ThemeHelper.Palette.Dark ? 204 : 217;   // 80% / 85% of the surface colour over the wallpaper
                using (var b = new SolidBrush(ColorMath.WithAlpha(tint, bake != null ? a : 255))) g.FillRectangle(b, clip);
            }
            catch { try { using (var b = new SolidBrush(tint)) g.FillRectangle(b, clip); } catch { } }
            finally { g.SmoothingMode = smooth; }
        }

        private static void DropBake() { if (_bake != null) { _bake.Dispose(); _bake = null; } }

        /// <summary>The blurred wallpaper over the virtual screen at 1/4 (null when there is nothing to show).</summary>
        private static Bitmap Bake()
        {
            var vs = SystemInformation.VirtualScreen;
            if (_bake != null && _bakeFor == vs) return _bake;
            DropBake();
            _bakeFor = vs;
            int w = Math.Max(1, vs.Width / Scale), h = Math.Max(1, vs.Height / Scale);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(DesktopColour());
                using (var pic = LoadWallpaper())
                    if (pic != null)
                    {
                        // a sixteenth of each monitor's area, "fill" (crop to cover) per monitor, then the whole thing back up
                        int sw = Math.Max(1, w / 4), sh = Math.Max(1, h / 4);
                        using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
                        {
                            using (var gs = Graphics.FromImage(small))
                            {
                                gs.Clear(DesktopColour());
                                gs.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                foreach (var scr in Screen.AllScreens)
                                {
                                    var b = scr.Bounds;
                                    var dst = new RectangleF((b.X - vs.X) / (float)(Scale * 4), (b.Y - vs.Y) / (float)(Scale * 4), b.Width / (float)(Scale * 4), b.Height / (float)(Scale * 4));
                                    double k = Math.Max(dst.Width / pic.Width, dst.Height / pic.Height);
                                    float pw = (float)(pic.Width * k), ph = (float)(pic.Height * k);
                                    var state = gs.Save();
                                    gs.SetClip(dst);
                                    gs.DrawImage(pic, dst.X + (dst.Width - pw) / 2, dst.Y + (dst.Height - ph) / 2, pw, ph);
                                    gs.Restore(state);
                                }
                            }
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(small, new Rectangle(0, 0, w, h));
                        }
                    }
            }
            _bake = bmp;
            return _bake;
        }

        private static Image LoadWallpaper()
        {
            string path = _forcedPicture;
            if (path == null)
            {
                var sb = new StringBuilder(520);
                try { if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0)) path = sb.ToString(); } catch { }
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    // never keep more than ~2 MP of it: it is about to be blurred to a sixteenth anyway
                    double k = Math.Min(1.0, Math.Sqrt(2e6 / Math.Max(1.0, img.Width * (double)img.Height)));
                    var copy = new Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
                    using (var g = Graphics.FromImage(copy)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(img, 0, 0, copy.Width, copy.Height); }
                    return copy;
                }
            }
            catch (Exception ex) { Logger.Diag("[BACKDROP] wallpaper unreadable: " + ex.Message); return null; }
        }

        private static Color DesktopColour()
        {
            try { int v = GetSysColor(1 /* COLOR_DESKTOP */); return ColorMath.Rgb(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF); }
            catch { return ThemeHelper.Palette.Window; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, StringBuilder pvParam, int fWinIni);

        [DllImport("user32.dll")]
        private static extern int GetSysColor(int nIndex);
    }
}
