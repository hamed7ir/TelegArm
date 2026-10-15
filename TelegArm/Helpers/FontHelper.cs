using System;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Central font provider. Bundles Roboto (UI) and Vazirmatn (Persian/RTL) as embedded
    /// fonts loaded into a process-wide <see cref="PrivateFontCollection"/>, so the app looks
    /// the same on any machine without installing fonts (works on RT — private-loaded, not
    /// system). Every call returns a fresh <see cref="Font"/> (callers may dispose it); the
    /// families stay alive for the app's life. Any load failure falls back to "Segoe UI" so the
    /// UI never breaks. Persian text renders in Vazirmatn (Regular/Bold); Latin stays Roboto.
    /// </summary>
    public static class FontHelper
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        private static readonly PrivateFontCollection _pfc = new PrivateFontCollection();
        private static FontFamily _ui;
        private static FontFamily _persian;

        /// <summary>Vazirmatn renders LARGER per point than Roboto — scale Persian text DOWN so it VISUALLY
        /// matches Latin at the same nominal size. ONE knob for all Persian text (bubbles + list + titles);
        /// tune by eye. (Bold Persian drawn by TextRenderer/GDI uses the real Vazirmatn-Bold face; GDI+ DrawString
        /// synthesizes bold from Regular — see the static constructor for why.)</summary>
        private const float PersianScale = 0.85f;

        static FontHelper()
        {
            try
            {
                // ⚠ STEP 0.1b — THE BOLD FACES GO TO GDI ONLY, NEVER INTO THE GDI+ COLLECTION.
                //   Regular and Bold share one family name ("Roboto", "Vazirmatn"), and GDI+ does not keep them
                //   apart inside one PrivateFontCollection: both land in the same style slot, and which file serves
                //   a REGULAR request is not stable from run to run. Measured with the --shoot rig: the same
                //   Font object (Vazirmatn, Regular, 10.2pt) measured 315.29 px in five runs and 320.51 in one —
                //   the real Bold face (Bold/Regular = 1.015 measured in isolation) — so a Persian bubble came out
                //   bold and re-wrapped in 2 launches of 10. GDI picks by weight correctly in every probe,
                //   so TextRenderer keeps the real Bold faces. GDI+ now holds Regular only and synthesizes bold
                //   from it — exactly what it already did on the launches that came out right.
                AddFont("TelegArm.Fonts.Roboto-Regular.ttf", gdiPlus: true);
                AddFont("TelegArm.Fonts.Roboto-Medium.ttf", gdiPlus: true);   // its own family ("Roboto Medium") — no clash
                AddFont("TelegArm.Fonts.Roboto-Bold.ttf", gdiPlus: false);
                AddFont("TelegArm.Fonts.Vazirmatn-Regular.ttf", gdiPlus: true);
                AddFont("TelegArm.Fonts.Vazirmatn-Bold.ttf", gdiPlus: false);

                _ui = Find("Roboto");
                _persian = Find("Vazirmatn") ?? FirstNonRoboto();   // Vazirmatn for Persian/RTL (falls back to any non-Roboto private family, then Segoe UI)
            }
            catch { /* fall back to Segoe UI below */ }
        }

        /// <summary>A Roboto (UI) font, or Segoe UI if unavailable.</summary>
        public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Make(_ui, size, style);

        // ── Redesign step 1.1: shared, pixel-unit fonts for a known DPI (see FontCache) ─────────────────────────
        // ⚠ These are SHARED and must never be disposed — the opposite of Ui/Persian/For above, which return a
        //   fresh font the caller owns. New and rewritten UI code uses these, with the DPI of its window (Dpi.Of).

        /// <summary>The shared Roboto font at <paramref name="pt"/> points for a <paramref name="dpi"/> display.</summary>
        public static Font UiAt(float pt, int dpi, FontStyle style = FontStyle.Regular)
            => FontCache.Get(_ui != null ? _ui.Name : "Segoe UI", pt, style, dpi);

        /// <summary>The shared Vazirmatn font, scaled by <see cref="PersianScale"/> like <see cref="Persian"/>.</summary>
        public static Font PersianAt(float pt, int dpi, FontStyle style = FontStyle.Regular)
            => FontCache.Get(_persian != null ? _persian.Name : "Segoe UI", pt * PersianScale, style, dpi);

        /// <summary>Script-aware like <see cref="For"/>: Vazirmatn (+2 pt) for Persian/Arabic text, else Roboto.</summary>
        public static Font ForAt(string text, float pt, int dpi, FontStyle style = FontStyle.Regular)
            => IsPersian(text) ? PersianAt(pt + 2f, dpi, style) : UiAt(pt, dpi, style);

        private static string _menuFamily;
        private static float _menuPt;
        private static FontStyle _menuStyle;

        /// <summary>Step 3.1 — the shared menu font at <paramref name="dpi"/>: the SYSTEM menu font's family, point size and
        /// style (SystemFonts.MenuFont — what an unstyled ToolStrip shows; read once), in pixels for that DPI. The same
        /// size at 96 as the default menu font (Segoe UI 9 pt on the dev box). Shared: never dispose.</summary>
        public static Font MenuAt(int dpi)
        {
            if (_menuFamily == null)
            {
                string family = "Segoe UI";
                float pt = 9f;
                FontStyle style = FontStyle.Regular;
                try
                {
                    using (var f = SystemFonts.MenuFont)
                        if (f != null) { family = f.FontFamily.Name; pt = f.SizeInPoints; style = f.Style; }
                }
                catch { }
                _menuPt = pt;
                _menuStyle = style;
                _menuFamily = family;   // last: it marks the read as done
            }
            return FontCache.Get(_menuFamily, _menuPt, _menuStyle, dpi);
        }

        private static readonly System.Collections.Generic.Dictionary<string, FontFamily> _families =
            new System.Collections.Generic.Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The family called <paramref name="name"/>: the BUNDLED one when TelegArm ships a family by that
        /// name, else the installed one, else null. Bundled wins on purpose — this dev box also has Roboto
        /// installed, and "Roboto" in TelegArm always means the copy it carries. Cached; never disposed.</summary>
        internal static FontFamily Family(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_families)
            {
                FontFamily f;
                if (_families.TryGetValue(name, out f)) return f;
                if (_ui != null && string.Equals(_ui.Name, name, StringComparison.OrdinalIgnoreCase)) f = _ui;
                else if (_persian != null && string.Equals(_persian.Name, name, StringComparison.OrdinalIgnoreCase)) f = _persian;
                else
                {
                    foreach (var p in _pfc.Families)
                        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { f = p; break; }
                    if (f == null) { try { f = new FontFamily(name); } catch (ArgumentException) { f = null; } }
                }
                _families[name] = f;
                return f;
            }
        }

        /// <summary>A Vazirmatn (Persian/RTL) font, auto-scaled to match Latin's visual size, or Segoe UI if
        /// unavailable. Bold is the real Vazirmatn-Bold face under TextRenderer, synthesized under GDI+.</summary>
        public static Font Persian(float size, FontStyle style = FontStyle.Regular) => Make(_persian, size * PersianScale, style);

        /// <summary>Script-aware: Vazirmatn (slightly larger) for Persian/Arabic text, else Roboto.</summary>
        public static Font For(string text, float size, FontStyle style = FontStyle.Regular)
            => IsPersian(text) ? Persian(size + 2f, style) : Ui(size, style);

        /// <summary>True if the text contains Arabic/Persian characters.</summary>
        public static bool IsPersian(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) ||
                    (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF))
                    return true;
            return false;
        }

        private static Font Make(FontFamily family, float size, FontStyle style)
        {
            try { if (family != null) return new Font(family, size, style); } catch { }
            try { if (family != null) return new Font(family, size, FontStyle.Regular); } catch { }
            return new Font("Segoe UI", size, style);
        }

        private static void AddFont(string resourceName, bool gdiPlus)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return;
                    var data = new byte[s.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = s.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    // GDI+ does not copy the buffer — it must stay allocated for the app's
                    // lifetime (the static collection), so we intentionally never free it.
                    IntPtr p = Marshal.AllocCoTaskMem(data.Length);
                    Marshal.Copy(data, 0, p, data.Length);
                    if (gdiPlus) _pfc.AddMemoryFont(p, data.Length);   // GDI+ (Graphics.DrawString)
                    // ALSO register with GDI so TextRenderer/GDI (which the app uses for Persian) can see the
                    // font WITHOUT installing it — otherwise `new Font("Vazirmatn",…)` handed to TextRenderer
                    // resolves to a SYSTEM substitute (the "Persian still system font" bug). Shares the same
                    // never-freed buffer. RT-safe (gdi32 present).
                    try { uint _c; AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, out _c); } catch { }
                }
            }
            catch { }
        }

        /// <summary>The family named exactly <paramref name="name"/>, else the first whose name contains it. Exact
        /// first because "Roboto Medium" also contains "Roboto", and the collection does not promise an order.</summary>
        private static FontFamily Find(string name)
        {
            var all = _pfc.Families;
            foreach (var f in all)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            foreach (var f in all)
                if (f.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return f;
            return null;
        }

        private static FontFamily FirstNonRoboto()
        {
            foreach (var f in _pfc.Families)
                if (f.Name.IndexOf("Roboto", StringComparison.OrdinalIgnoreCase) < 0) return f;
            return null;
        }
    }
}
