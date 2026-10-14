using System;
using System.Collections.Generic;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.1 — PIXEL-unit fonts for a given DPI, created once and shared.
    ///
    /// ⚠ SHARED AND NEVER DISPOSED. Never wrap one in <c>using</c>, never Dispose it, never hand it to code that
    ///   does: every other user of the same (family, size, style, dpi) holds the same instance, and a disposed
    ///   Font throws on its next use — inside a paint handler, that is a crash (INVARIANTS #9).
    ///   The legacy FontHelper.Ui / For / Persian keep returning fresh, disposable fonts; nothing changes for them.
    ///
    /// WHY PIXEL UNITS: a Point-unit font is converted with the DPI GDI+ believes in, which under per-monitor v1 is
    /// the SYSTEM DPI on every monitor — so it comes out the wrong size on a second monitor. A pixel-unit font
    /// created for a known DPI is the same size wherever it is drawn. Size = pt × dpi / 72, unrounded.
    ///
    /// FAMILY IDENTITY: keyed by NAME. A name TelegArm bundles (Roboto, Vazirmatn) always resolves to the bundled
    /// family (FontHelper.Family): the dev box has Roboto installed too, and "Roboto" here always means ours.</summary>
    public static class FontCache
    {
        private struct Key : IEquatable<Key>
        {
            public string Family; public float Pt; public FontStyle Style; public int Dpi;
            public bool Equals(Key o) { return Pt == o.Pt && Style == o.Style && Dpi == o.Dpi && string.Equals(Family, o.Family, StringComparison.OrdinalIgnoreCase); }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode()
            {
                unchecked { return (StringComparer.OrdinalIgnoreCase.GetHashCode(Family ?? "") * 397) ^ Pt.GetHashCode() ^ ((int)Style << 20) ^ (Dpi << 8); }
            }
        }

        private static readonly Dictionary<Key, Font> _fonts = new Dictionary<Key, Font>();
        private static readonly object _lock = new object();

        /// <summary>The shared pixel-unit font for <paramref name="family"/> at <paramref name="pt"/> points on a
        /// <paramref name="dpi"/> display. Falls back to the regular face, then to Segoe UI, never throws.</summary>
        public static Font Get(string family, float pt, FontStyle style, int dpi)
        {
            if (dpi <= 0) dpi = Dpi.Logical;
            // Normalised once, here, so every road to the same size meets on one key (review: PersianAt passes
            // 12 x 0.85 = 10.200001 while Rescale arrives at 10.2, which made two fonts for one size).
            pt = (float)Math.Round(pt, 3);   // 3, not 2: 8.925 / 9.775 / 10.625 are the legacy Persian sizes exactly
            var key = new Key { Family = family ?? "Segoe UI", Pt = pt, Style = style, Dpi = dpi };
            lock (_lock)
            {
                Font f;
                if (!_fonts.TryGetValue(key, out f))
                {
                    f = Make(key.Family, pt * dpi / 72f, style);
                    _fonts[key] = f;
                }
                return f;
            }
        }

        /// <summary>The shared equivalent of <paramref name="font"/> after a move from <paramref name="oldDpi"/> to
        /// <paramref name="newDpi"/>: same family and style, same POINT size, pixel units at the new DPI.
        /// A pixel-unit font is read as pixels at the old DPI. Any other unit is read through SizeInPoints.</summary>
        public static Font Rescale(Font font, int oldDpi, int newDpi)
        {
            if (font == null) return null;
            float pt = font.Unit == GraphicsUnit.Pixel ? font.Size * 72f / (oldDpi > 0 ? oldDpi : Dpi.Logical) : font.SizeInPoints;
            return Get(font.FontFamily.Name, (float)Math.Round(pt, 3), font.Style, newDpi);
        }

        /// <summary>How many distinct fonts are cached (for diagnostics and the cache-growth check of step 3.5).</summary>
        public static int Count { get { lock (_lock) return _fonts.Count; } }

        private static Font Make(string family, float px, FontStyle style)
        {
            FontFamily fam = FontHelper.Family(family);
            if (fam != null)
            {
                try { return new Font(fam, px, style, GraphicsUnit.Pixel); } catch { }
                try { return new Font(fam, px, FontStyle.Regular, GraphicsUnit.Pixel); } catch { }
            }
            return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
        }
    }
}
