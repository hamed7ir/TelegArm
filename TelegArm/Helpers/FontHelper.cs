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
        /// tune by eye. (Bold Persian uses the real Vazirmatn-Bold face at this scaled size — not faux-bold.)</summary>
        private const float PersianScale = 0.85f;

        static FontHelper()
        {
            try
            {
                AddFont("TelegArm.Fonts.Roboto-Regular.ttf");
                AddFont("TelegArm.Fonts.Roboto-Medium.ttf");
                AddFont("TelegArm.Fonts.Roboto-Bold.ttf");
                AddFont("TelegArm.Fonts.Vazirmatn-Regular.ttf");
                AddFont("TelegArm.Fonts.Vazirmatn-Bold.ttf");

                _ui = Find("Roboto");
                _persian = Find("Vazirmatn") ?? FirstNonRoboto();   // Vazirmatn for Persian/RTL (falls back to any non-Roboto private family, then Segoe UI)
            }
            catch { /* fall back to Segoe UI below */ }
        }

        /// <summary>A Roboto (UI) font, or Segoe UI if unavailable.</summary>
        public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Make(_ui, size, style);

        /// <summary>A Vazirmatn (Persian/RTL) font, auto-scaled to match Latin's visual size, or Segoe UI if
        /// unavailable. Bold uses the real Vazirmatn-Bold face.</summary>
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

        private static void AddFont(string resourceName)
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
                    _pfc.AddMemoryFont(p, data.Length);   // GDI+ (Graphics.DrawString)
                    // ALSO register with GDI so TextRenderer/GDI (which the app uses for Persian) can see the
                    // font WITHOUT installing it — otherwise `new Font("Vazirmatn",…)` handed to TextRenderer
                    // resolves to a SYSTEM substitute (the "Persian still system font" bug). Shares the same
                    // never-freed buffer. RT-safe (gdi32 present).
                    try { uint _c; AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, out _c); } catch { }
                }
            }
            catch { }
        }

        private static FontFamily Find(string namePart)
        {
            foreach (var f in _pfc.Families)
                if (f.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return f;
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
