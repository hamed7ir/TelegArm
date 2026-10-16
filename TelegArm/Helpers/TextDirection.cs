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

        public static bool FirstStrongIsRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c == Rlm || c == Alm) return true;
                if (c == Lrm) return false;
                if (char.IsLetter(c)) return InRtlBlock(c);   // letters only: digits, marks and punctuation in those blocks are weak
            }
            return false;
        }
    }
}
