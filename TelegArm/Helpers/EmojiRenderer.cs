using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Loads color emoji images (Google Noto Emoji, 72×72 PNGs) by codepoint, from ONE packed
    /// <c>noto-emoji.zip</c> opened once at startup. Each emoji's PNG is decoded on first use and the
    /// Bitmap is cached for the app's lifetime (the cache owns them — callers must not dispose);
    /// subsequent paints are pure in-memory blits (identical to the old loose-folder behaviour, and
    /// RT-safe — no color-font rendering, no per-paint zip access). If the zip is absent,
    /// <see cref="Available"/> is false and <see cref="Get"/> returns null (callers fall back to
    /// drawing the emoji glyph with a font).
    /// </summary>
    public static class EmojiRenderer
    {
        // Codepoint-sequence key → zip entry, built ONCE from the zip's real entry list. The key is
        // the canonical form (FE0F-stripped, minimal lowercase hex, '-' joined) — the SAME shape the
        // request side produces (KeyOf / KeyFromCps), so Noto's 4-digit padding / FE0F naming choices
        // are normalised away on both sides and lookups are a plain dictionary hit.
        private static readonly Dictionary<string, ZipArchiveEntry> _entries = LoadEntries();
        private static readonly object _zipLock = new object();
        private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>();
        private static readonly Dictionary<string, Image> _scaled = new Dictionary<string, Image>();

        public static bool Available => _entries != null && _entries.Count > 0;

        /// <summary>An emoji rendered to a crisp <paramref name="size"/>px bitmap (cached; null if unavailable)
        /// — for UI glyphs (menu/drawer/reply icons) so they look good + consistent on RT instead of a font glyph.</summary>
        public static Image GetScaled(string emoji, int size)
        {
            if (size <= 0) return null;
            string key = KeyOf(emoji) + "@" + size;
            if (string.IsNullOrEmpty(key)) return null;
            if (_scaled.TryGetValue(key, out var cached)) return cached;
            Image scaled = null;
            try
            {
                var src = Get(emoji);
                if (src != null)
                {
                    var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, size, size));
                    }
                    scaled = bmp;
                }
            }
            catch { scaled = null; }
            _scaled[key] = scaled;   // cache null too
            return scaled;
        }

        private static Dictionary<string, ZipArchiveEntry> LoadEntries()
        {
            string zip = FindZip();
            if (zip == null) return null;
            try
            {
                // Open once and keep open for the app's lifetime (lazy per-emoji reads). Never disposed
                // on purpose — process exit reclaims it; closing would break later emoji loads.
                var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
                var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;   // directory entry
                    if (!e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    string canon = CanonFromEntryName(e.Name);
                    if (canon != null && !map.ContainsKey(canon)) map[canon] = e;   // first wins
                }
                return map.Count > 0 ? map : null;
            }
            catch { return null; }
        }

        private static string FindZip()
        {
            try
            {
                string exe = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(exe, "noto-emoji.zip"),                        // next to the exe (deploy)
                    Path.Combine(exe, "..", "..", "..", "noto-emoji.zip"),      // repo root (dev: bin\Debug\..\..)
                    @"C:\Users\hamed\source\repos\TelegArm\noto-emoji.zip"      // repo root (absolute fallback)
                };
                foreach (var c in candidates)
                    if (File.Exists(c)) return c;
            }
            catch { }
            return null;
        }

        /// <summary>Canonical key from a Noto file name: "emoji_u0031_20e3.png" → "31-20e3".</summary>
        private static string CanonFromEntryName(string name)
        {
            string stem = name.Substring(0, name.Length - 4);   // drop ".png"
            const string prefix = "emoji_u";
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(prefix.Length);
            var sb = new StringBuilder();
            foreach (var part in stem.Split('_'))
            {
                if (part.Length == 0) continue;
                int cp;
                try { cp = Convert.ToInt32(part, 16); } catch { return null; }
                if (cp == 0xFE0F) continue;                 // strip the variation selector (KEEP 200D / 20E3)
                if (sb.Length > 0) sb.Append('-');
                sb.Append(cp.ToString("x"));                // minimal lowercase hex: 0031 → 31
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>Returns the color image for an emoji string (e.g. "👍"), or null if unavailable.</summary>
        public static Image Get(string emoji)
        {
            if (!Available || string.IsNullOrEmpty(emoji)) return null;
            string key = KeyOf(emoji);
            return key.Length == 0 ? null : GetByKey(key);
        }

        private static Image GetByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out var img)) { PerfLog.Inc(PerfLog.P.EmojiHit); return img; }   // hit (incl. cached null misses)
            PerfLog.Inc(PerfLog.P.EmojiMiss);   // miss → a one-time zip read + decode (the expensive path)

            Image loaded = null;
            try
            {
                if (_entries != null && _entries.TryGetValue(key, out var entry))
                {
                    byte[] bytes;
                    lock (_zipLock)   // ZipArchive isn't thread-safe; reads are quick + one-time per emoji
                    {
                        using (var s = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                    }
                    using (var src = new MemoryStream(bytes))
                    using (var t = Image.FromStream(src))
                        loaded = new Bitmap(t);
                }
            }
            catch { loaded = null; }
            _cache[key] = loaded;   // cache misses too (null) so we don't re-read the zip
            return loaded;
        }

        /// <summary>A text run: either plain text or one emoji image.</summary>
        public struct Run { public string Text; public Image Emoji; }

        /// <summary>Quick check (codepoint ranges) whether the text may contain emoji.</summary>
        public static bool ContainsEmoji(string s)
        {
            if (!Available || string.IsNullOrEmpty(s)) return false;
            var pts = new List<int>();
            for (int i = 0; i < s.Length;)
            {
                int cp;
                try { cp = char.ConvertToUtf32(s, i); } catch { break; }
                i += (i + 1 < s.Length && char.IsSurrogatePair(s, i)) ? 2 : 1;
                pts.Add(cp);
            }
            for (int k = 0; k < pts.Count; k++)
                if (CouldStart(pts[k]) || IsKeycap(pts, k)) return true;
            return false;
        }

        /// <summary>Splits text into runs (text / emoji image), greedily matching the longest emoji.</summary>
        public static IEnumerable<Run> Segment(string s)
        {
            var pts = new List<int>();
            for (int i = 0; i < s.Length;)
            {
                int cp;
                try { cp = char.ConvertToUtf32(s, i); } catch { break; }
                pts.Add(cp);
                i += (i + 1 < s.Length && char.IsSurrogatePair(s, i)) ? 2 : 1;
            }
            var sb = new StringBuilder();
            int idx = 0;
            while (idx < pts.Count)
            {
                Image img = null; int len = 0;
                if (CouldStart(pts[idx]) || IsKeycap(pts, idx))
                {
                    int max = Math.Min(8, pts.Count - idx);
                    for (int L = max; L >= 1; L--)
                    {
                        var im = GetByKey(KeyFromCps(pts, idx, L));
                        if (im != null) { img = im; len = L; break; }
                    }
                }
                if (len > 0)
                {
                    if (sb.Length > 0) { yield return new Run { Text = sb.ToString() }; sb.Clear(); }
                    var es = new StringBuilder();   // keep the source codepoints so a selection can copy the emoji
                    for (int i = 0; i < len; i++) es.Append(char.ConvertFromUtf32(pts[idx + i]));
                    yield return new Run { Emoji = img, Text = es.ToString() };
                    idx += len;
                }
                else { sb.Append(char.ConvertFromUtf32(pts[idx])); idx++; }
            }
            if (sb.Length > 0) yield return new Run { Text = sb.ToString() };
        }

        /// <summary>An emoji occurrence located by UTF-16 index — for overpainting a native TextBox's emoji cell.</summary>
        public struct EmojiSpan { public int CharIndex; public int CharLength; public Image Emoji; }

        /// <summary>Locates every emoji in <paramref name="s"/> by its UTF-16 char range (start + length) with
        /// its Noto image. Mirrors <see cref="Segment"/>'s greedy longest-match, but tracks UTF-16 offsets so a
        /// native TextBox's per-emoji slot (GetPositionFromCharIndex) can be overpainted with the color image.</summary>
        public static List<EmojiSpan> EmojiSpans(string s)
        {
            var result = new List<EmojiSpan>();
            if (!Available || string.IsNullOrEmpty(s)) return result;
            var pts = new List<int>();
            var off = new List<int>();   // UTF-16 index where each codepoint starts
            for (int i = 0; i < s.Length;)
            {
                int cp;
                try { cp = char.ConvertToUtf32(s, i); } catch { break; }
                pts.Add(cp); off.Add(i);
                i += (i + 1 < s.Length && char.IsSurrogatePair(s, i)) ? 2 : 1;
            }
            off.Add(s.Length);   // sentinel: end offset for the last codepoint
            int idx = 0;
            while (idx < pts.Count)
            {
                int len = 0; Image img = null;
                if (CouldStart(pts[idx]) || IsKeycap(pts, idx))
                {
                    int max = Math.Min(8, pts.Count - idx);
                    for (int L = max; L >= 1; L--)
                    {
                        var im = GetByKey(KeyFromCps(pts, idx, L));
                        if (im != null) { img = im; len = L; break; }
                    }
                }
                if (len > 0)
                {
                    int cStart = off[idx], cEnd = off[idx + len];
                    result.Add(new EmojiSpan { CharIndex = cStart, CharLength = cEnd - cStart, Emoji = img });
                    idx += len;
                }
                else idx++;
            }
            return result;
        }

        /// <summary>Draws one line of text with inline color emoji, left-aligned, clipped to <paramref name="rect"/>.</summary>
        public static void DrawLine(Graphics g, string text, Font font, Color color, Rectangle rect)
            => DrawLineFrom(g, text, font, color, rect, rect.X);

        /// <summary>Same as <see cref="DrawLine"/> but horizontally CENTERED in the rect (for bot keyboard buttons).</summary>
        public static void DrawLineCentered(Graphics g, string text, Font font, Color color, Rectangle rect)
            => DrawLineFrom(g, text, font, color, rect, rect.X + Math.Max(0, (rect.Width - MeasureLineWidth(g, text, font)) / 2));

        /// <summary>Pixel width of a text+emoji line as <see cref="DrawLine"/> would lay it out.</summary>
        public static int MeasureLineWidth(Graphics g, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int es = Math.Max(12, (int)Math.Round(font.GetHeight(g)));
            float w = 0;
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
                foreach (var run in Segment(text))
                    w += run.Emoji != null ? es + 1 : g.MeasureString(run.Text, font, int.MaxValue, sf).Width;
            return (int)Math.Ceiling(w);
        }

        private static void DrawLineFrom(Graphics g, string text, Font font, Color color, Rectangle rect, float startX)
        {
            if (string.IsNullOrEmpty(text)) return;
            int es = Math.Max(12, (int)Math.Round(font.GetHeight(g)));
            float x = startX, cy = rect.Y + rect.Height / 2f;
            var oldClip = g.Clip;
            g.SetClip(rect);
            using (var brush = new SolidBrush(color))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                foreach (var run in Segment(text))
                {
                    if (x > rect.Right) break;
                    if (run.Emoji != null)
                    {
                        g.DrawImage(run.Emoji, x, cy - es / 2f, es, es);
                        x += es + 1;
                    }
                    else
                    {
                        var sz = g.MeasureString(run.Text, font, int.MaxValue, sf);
                        g.DrawString(run.Text, font, brush, new PointF(x, cy - sz.Height / 2f), sf);
                        x += sz.Width;
                    }
                }
            }
            g.Clip = oldClip;
        }

        private static bool CouldStart(int cp)
        {
            return cp == 0xA9 || cp == 0xAE || cp == 0x203C || cp == 0x2049 ||
                   (cp >= 0x2100 && cp <= 0x32FF) ||
                   (cp >= 0x1F000 && cp <= 0x1FAFF) ||
                   (cp >= 0x1F1E6 && cp <= 0x1F1FF);
        }

        /// <summary>
        /// A keycap sequence (#/* /0-9, optional FE0F, then U+20E3). CouldStart deliberately excludes
        /// ASCII bases so plain digits don't turn into emoji; we only start a match when 20E3 follows.
        /// </summary>
        private static bool IsKeycap(List<int> pts, int i)
        {
            int c = pts[i];
            bool baseOk = c == 0x23 || c == 0x2A || (c >= 0x30 && c <= 0x39);
            if (!baseOk) return false;
            int j = i + 1;
            if (j < pts.Count && pts[j] == 0xFE0F) j++;
            return j < pts.Count && pts[j] == 0x20E3;
        }

        private static string KeyFromCps(List<int> pts, int start, int len)
        {
            var sb = new StringBuilder();
            for (int k = start; k < start + len; k++)
            {
                if (pts[k] == 0xFE0F) continue;
                if (sb.Length > 0) sb.Append('-');
                sb.Append(pts[k].ToString("x"));
            }
            return sb.ToString();
        }

        /// <summary>One entry in the picker catalog: either a category header or an emoji.</summary>
        public sealed class EmojiCell
        {
            public string Header;   // non-null = category header row
            public string Emoji;    // the emoji string to insert (when not a header)
            public bool IsHeader => Header != null;
        }

        private static List<EmojiCell> _catalog;

        /// <summary>All emoji grouped into categories (built once from the zip's entries).
        /// Skin-tone variants are folded into their base to reduce clutter.</summary>
        public static List<EmojiCell> Catalog()
        {
            if (_catalog != null) return _catalog;
            var cells = new List<EmojiCell>();
            if (!Available) { _catalog = cells; return cells; }
            try
            {
                var entries = new List<(int ord, int cp, string emoji)>();
                foreach (var key in _entries.Keys)
                {
                    int[] cps;
                    try { cps = key.Split('-').Select(h => Convert.ToInt32(h, 16)).ToArray(); }
                    catch { continue; }
                    if (cps.Length == 0) continue;
                    if (cps.Any(c => c >= 0x1F3FB && c <= 0x1F3FF)) continue;   // skip skin-tone variants
                    string emoji;
                    try { emoji = string.Concat(cps.Select(char.ConvertFromUtf32)); } catch { continue; }
                    entries.Add((Cat(cps[0]).ord, cps[0], emoji));
                }
                entries.Sort((a, b) => a.ord != b.ord ? a.ord.CompareTo(b.ord) : a.cp.CompareTo(b.cp));
                int last = -1;
                foreach (var e in entries)
                {
                    if (e.ord != last) { cells.Add(new EmojiCell { Header = Cat(e.cp).name }); last = e.ord; }
                    cells.Add(new EmojiCell { Emoji = e.emoji });
                }
            }
            catch { }
            _catalog = cells;
            return cells;
        }

        /// <summary>Coarse category for an emoji's base codepoint (by Unicode block).</summary>
        private static (int ord, string name) Cat(int cp)
        {
            if (cp >= 0x1F600 && cp <= 0x1F64F) return (0, "Smileys & Emotion");
            if ((cp >= 0x1F90C && cp <= 0x1F9FF) || (cp >= 0x1F440 && cp <= 0x1F4AB) ||
                (cp >= 0x270A && cp <= 0x270D) || cp == 0x1F44B) return (1, "People & Body");
            if ((cp >= 0x1F400 && cp <= 0x1F43F) || (cp >= 0x1F980 && cp <= 0x1F9AE) ||
                (cp >= 0x1F300 && cp <= 0x1F344)) return (2, "Animals & Nature");
            if ((cp >= 0x1F345 && cp <= 0x1F37F) || (cp >= 0x1F950 && cp <= 0x1F96F) ||
                (cp >= 0x1F32D && cp <= 0x1F32F)) return (3, "Food & Drink");
            if ((cp >= 0x1F680 && cp <= 0x1F6FF) || (cp >= 0x1F30D && cp <= 0x1F320) ||
                (cp >= 0x1F3D4 && cp <= 0x1F3F0)) return (4, "Travel & Places");
            if ((cp >= 0x1F3A0 && cp <= 0x1F3FA) || (cp >= 0x1F93C && cp <= 0x1F945) ||
                (cp >= 0x26BD && cp <= 0x26BE)) return (5, "Activities");
            if ((cp >= 0x1F4A0 && cp <= 0x1F5FF) || (cp >= 0x1F6E0 && cp <= 0x1F6FC)) return (6, "Objects");
            if (cp >= 0x1F1E6 && cp <= 0x1F1FF) return (9, "Flags");
            return (8, "Symbols");
        }

        /// <summary>Canonical key for an emoji string: minimal lowercase hex codepoints joined by '-',
        /// with VS16 (FE0F) stripped (matches the keys built from the zip entries).</summary>
        private static string KeyOf(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length;)
            {
                int cp;
                try { cp = char.ConvertToUtf32(s, i); }
                catch { break; }
                i += (i + 1 < s.Length && char.IsSurrogatePair(s, i)) ? 2 : 1;
                if (cp == 0xFE0F) continue;
                if (sb.Length > 0) sb.Append('-');
                sb.Append(cp.ToString("x"));
            }
            return sb.ToString();
        }
    }
}
