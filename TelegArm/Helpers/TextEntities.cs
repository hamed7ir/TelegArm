using System.Collections.Generic;
using System.Text.RegularExpressions;
using TL;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Lightweight client-side entity detection for PLAIN text that carries no server entities — channel/user
    /// "about" (ChannelFull/UserFull.about are plain strings, no entities field). Detects URLs (incl.
    /// scheme-less www. / t.me), @mentions and #hashtags and emits TL <see cref="MessageEntity"/>[] with UTF-16
    /// offsets, so the existing <c>InlineText</c> engine renders them clickable exactly like a chat message.
    /// Mirrors how Telegram Desktop auto-links bios. Standard emoji need no entities (the engine segments them).
    /// </summary>
    public static class TextEntities
    {
        private static readonly Regex UrlRx = new Regex(@"(?:https?://|www\.|t\.me/|telegram\.me/)[^\s]+", RegexOptions.IgnoreCase);
        private static readonly Regex MentionRx = new Regex(@"(?<![A-Za-z0-9_])@[A-Za-z][A-Za-z0-9_]{2,31}");
        private static readonly Regex HashRx = new Regex(@"(?<![A-Za-z0-9_#])#\w{1,64}");

        public static MessageEntity[] Detect(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var list = new List<MessageEntity>();
            var consumed = new bool[text.Length];

            foreach (Match m in UrlRx.Matches(text))
            {
                int len = TrimTrailing(m.Value);
                if (len <= 0) continue;
                Mark(consumed, m.Index, len);
                list.Add(new MessageEntityUrl { offset = m.Index, length = len });
            }
            foreach (Match m in MentionRx.Matches(text))
            {
                if (consumed[m.Index]) continue;
                Mark(consumed, m.Index, m.Length);
                list.Add(new MessageEntityMention { offset = m.Index, length = m.Length });
            }
            foreach (Match m in HashRx.Matches(text))
            {
                if (consumed[m.Index]) continue;
                Mark(consumed, m.Index, m.Length);
                list.Add(new MessageEntityHashtag { offset = m.Index, length = m.Length });
            }

            if (list.Count == 0) return null;
            list.Sort((a, b) => a.offset.CompareTo(b.offset));
            return list.ToArray();
        }

        // URLs often end a sentence — don't swallow trailing punctuation into the link.
        private static int TrimTrailing(string s)
        {
            int len = s.Length;
            while (len > 0 && ".,;:!?)]}\"'»".IndexOf(s[len - 1]) >= 0) len--;
            return len;
        }

        private static void Mark(bool[] map, int off, int len)
        {
            for (int i = off; i < off + len && i < map.Length; i++) map[i] = true;
        }
    }
}
