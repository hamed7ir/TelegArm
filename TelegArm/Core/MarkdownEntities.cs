using System;
using System.Collections.Generic;
using System.Text;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// SEND-ENTITIES: converts the composer's markdown into plain text + a <see cref="MessageEntity"/>[] for sending
    /// (the inverse of the inline RENDER engine). Supported syntax:
    ///   **bold**  __italic__  ~~strike~~  ||spoiler||  `code`  ```pre```  [text](url)
    /// Offsets/lengths are UTF-16 code units — the same as C# string indices AND Telegram's convention — so Persian
    /// and emoji land on the right characters and the sent formatting round-trips with the render side.
    /// A backslash escapes the next marker char (\* → literal *). Unmatched markers are left as literal text — the
    /// parser never throws. Flat (a formatted span's inner markers are NOT re-parsed) — reasonable + crash-free.
    /// </summary>
    public static class MarkdownEntities
    {
        /// <summary>Parses <paramref name="input"/> → plain text (return) + entities (out; null when there are none).</summary>
        public static string Parse(string input, out MessageEntity[] entities)
        {
            if (string.IsNullOrEmpty(input)) { entities = null; return input ?? ""; }

            var ents = new List<MessageEntity>();
            var sb = new StringBuilder(input.Length);
            int i = 0, n = input.Length;

            while (i < n)
            {
                char c = input[i];

                // Escape: "\<marker>" emits the literal marker char.
                if (c == '\\' && i + 1 < n && IsMarkerChar(input[i + 1]))
                {
                    sb.Append(input[i + 1]);
                    i += 2;
                    continue;
                }

                // Link: [text](url)
                if (c == '[')
                {
                    int consumed = TryLink(input, i, sb, ents);
                    if (consumed > 0) { i += consumed; continue; }
                }

                // Paired style markers (longest-first so ``` beats ` and ** isn't split).
                string marker = MatchMarker(input, i);
                if (marker != null)
                {
                    int close = input.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                    if (close > i)
                    {
                        int start = sb.Length;
                        sb.Append(input, i + marker.Length, close - (i + marker.Length));   // content, markers stripped
                        int len = sb.Length - start;
                        if (len > 0) ents.Add(EntityFor(marker, start, len));
                        i = close + marker.Length;
                        continue;
                    }
                    // No matching close → the marker is literal text.
                    sb.Append(marker);
                    i += marker.Length;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            entities = ents.Count > 0 ? ents.ToArray() : null;
            return sb.ToString();
        }

        private static bool IsMarkerChar(char c)
        {
            return c == '*' || c == '_' || c == '~' || c == '|' || c == '`' || c == '\\'
                || c == '[' || c == ']' || c == '(' || c == ')';
        }

        private static string MatchMarker(string s, int i)
        {
            if (Has(s, i, "```")) return "```";
            if (Has(s, i, "**")) return "**";
            if (Has(s, i, "__")) return "__";
            if (Has(s, i, "~~")) return "~~";
            if (Has(s, i, "||")) return "||";
            if (s[i] == '`') return "`";
            return null;
        }

        private static bool Has(string s, int i, string m)
        {
            return i + m.Length <= s.Length && string.CompareOrdinal(s, i, m, 0, m.Length) == 0;
        }

        private static MessageEntity EntityFor(string marker, int offset, int length)
        {
            switch (marker)
            {
                case "**":  return new MessageEntityBold    { offset = offset, length = length };
                case "__":  return new MessageEntityItalic  { offset = offset, length = length };
                case "~~":  return new MessageEntityStrike   { offset = offset, length = length };
                case "||":  return new MessageEntitySpoiler  { offset = offset, length = length };
                case "```": return new MessageEntityPre      { offset = offset, length = length, language = "" };
                default:    return new MessageEntityCode     { offset = offset, length = length };   // "`"
            }
        }

        /// <summary>At input[i]=='[', tries to parse [text](url): appends `text` to the output + a TextUrl entity.
        /// Returns the number of INPUT chars consumed, or 0 if it isn't a well-formed link (then '[' is literal).</summary>
        private static int TryLink(string s, int i, StringBuilder sb, List<MessageEntity> ents)
        {
            int bracket = s.IndexOf(']', i + 1);
            if (bracket < 0 || bracket + 1 >= s.Length || s[bracket + 1] != '(') return 0;
            int paren = s.IndexOf(')', bracket + 2);
            if (paren < 0) return 0;
            string text = s.Substring(i + 1, bracket - (i + 1));
            string url = s.Substring(bracket + 2, paren - (bracket + 2));
            if (text.Length == 0 || url.Length == 0) return 0;
            int start = sb.Length;
            sb.Append(text);
            ents.Add(new MessageEntityTextUrl { offset = start, length = text.Length, url = url });
            return (paren + 1) - i;
        }
    }
}
