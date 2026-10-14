using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TelegArm.Helpers;

namespace TelegArm.SelfTest
{
    /// <summary>Device-trip build (2026-10): the colour-emoji matcher. Every sequence in the bundled Noto catalog must come
    /// out as ONE emoji — the old matcher capped its window at 8 raw code points, so the 95 nine-part sequences (kiss and
    /// couple with two skin tones) never matched and fell apart into pieces in sent messages.</summary>
    internal static class EmojiTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("emoji: every Noto sequence matches as one emoji (FE0F dropped, as the file names are)", EveryKeyMatches));
            t.Add(new KeyValuePair<string, Action>("emoji: fully-qualified long sequences (with FE0F) match as one emoji, inside text too", FullyQualified));
        }

        private static string FromKey(string key)
        {
            var sb = new StringBuilder();
            foreach (var part in key.Split('-')) sb.Append(char.ConvertFromUtf32(Convert.ToInt32(part, 16)));
            return sb.ToString();
        }

        private static void EveryKeyMatches()
        {
            var keys = EmojiRenderer.CatalogKeys;
            Check.True(keys.Count > 3000, "the Noto catalog loaded (" + keys.Count + " keys)");
            int longest = 0;
            var failed = new List<string>();
            foreach (var key in keys)
            {
                longest = Math.Max(longest, key.Split('-').Length);
                // A bare #, * or digit is in the catalog (as the keycap base), but must stay text on its own — a plain "1"
                // never turns into an emoji; only the keycap sequence (base, FE0F, U+20E3) does.
                if (key.Length <= 2 && Convert.ToInt32(key, 16) < 0x80) continue;
                // Nor are the bare keycap mark (U+20E3) and Noto's private-use glyphs (plane 15) emoji anyone types.
                if (key == "20e3" || Convert.ToInt32(key.Split('-')[0], 16) >= 0xF0000) continue;
                string s = FromKey(key);
                var spans = EmojiRenderer.EmojiSpans(s);
                if (spans.Count != 1 || spans[0].CharIndex != 0 || spans[0].CharLength != s.Length) failed.Add(key);
            }
            Check.True(failed.Count == 0, failed.Count + " sequences did not match as one emoji, e.g. " + string.Join(", ", failed.Take(20)));
            Check.True(longest >= 9, "the catalog still has 9-part sequences (longest " + longest + ")");
        }

        private static void FullyQualified()
        {
            // kiss: person, person — light and medium-light skin (RGI, 10 raw code points with the FE0F)
            string kiss = "\U0001F9D1\U0001F3FB\u200D\u2764\uFE0F\u200D\U0001F48B\u200D\U0001F9D1\U0001F3FC";
            // couple with heart: woman, man — light and medium-light skin
            string couple = "\U0001F469\U0001F3FB\u200D\u2764\uFE0F\u200D\U0001F468\U0001F3FC";
            foreach (var e in new[] { kiss, couple })
            {
                var spans = EmojiRenderer.EmojiSpans("hi " + e + " there");
                Check.True(spans.Count == 1 && spans[0].CharIndex == 3 && spans[0].CharLength == e.Length,
                    "one emoji spanning the whole sequence (spans=" + spans.Count
                    + (spans.Count > 0 ? " first@" + spans[0].CharIndex + " len " + spans[0].CharLength + " of " + e.Length : "") + ")");
                var runs = EmojiRenderer.Segment(e).ToList();
                Check.True(runs.Count == 1 && runs[0].Emoji != null, "Segment renders it as one image run (runs=" + runs.Count + ")");
            }
        }
    }
}
