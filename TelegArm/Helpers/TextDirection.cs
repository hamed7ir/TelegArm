namespace TelegArm.Helpers
{
    /// <summary>
    /// A paragraph's direction from its first STRONG character (Unicode's bidi rule P2, simplified): a letter of a
    /// right-to-left script (Hebrew, Arabic, Persian, Syriac, Thaana, N'Ko...) or the RLM / ALM marks make it RTL; any other
    /// letter or the LRM mark makes it LTR. Digits (the Arabic-Indic ones too), punctuation, combining marks, spaces and
    /// emoji are weak or neutral and skipped, so "۱۲۳ hello" is LTR and "123 سلام" or "😀 سلام" is RTL. No letter = LTR.
    /// Pure (no UI): the composer's paragraph direction; linked into SelfTest.
    /// </summary>
    public static class TextDirection
    {
        private const char Lrm = '‎', Rlm = '‏', Alm = '؜';

        /// <summary>The blocks of the right-to-left scripts in the BMP (Hebrew .. Arabic Extended-A, and the Hebrew / Arabic
        /// presentation forms).</summary>
        public static bool InRtlBlock(char c)
        {
            return (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);
        }

        /// <summary>1 = a right-to-left letter (or RLM / ALM), -1 = any other letter (or LRM), 0 = weak or neutral.</summary>
        public static int StrongOf(char c)
        {
            if (c == Rlm || c == Alm) return 1;
            if (c == Lrm) return -1;
            if (char.IsLetter(c)) return InRtlBlock(c) ? 1 : -1;   // letters only: digits, marks and punctuation in those blocks are weak
            return 0;
        }

        /// <summary>The direction of the first strong character: 1 right to left, -1 left to right, 0 none (no letter).</summary>
        public static int FirstStrong(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            foreach (char c in s)
            {
                int d = StrongOf(c);
                if (d != 0) return d;
            }
            return 0;
        }

        public static bool FirstStrongIsRtl(string s) { return FirstStrong(s) > 0; }

        /// <summary>True when two lines (paragraphs) of the text start in different directions: a Persian line and an
        /// English one in the same message. A line with no letter (digits, emoji) counts for neither.</summary>
        public static bool MixedParagraphs(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\n') < 0) return false;
            int first = 0, cur = 0;
            foreach (char c in s)
            {
                if (c == '\n') { cur = 0; continue; }
                if (cur != 0) continue;   // this line's direction is known: skip to its end
                cur = StrongOf(c);
                if (cur == 0) continue;
                if (first == 0) first = cur;
                else if (cur != first) return true;
            }
            return false;
        }
    }
}
