namespace TelegArm.Core
{
    /// <summary>Redesign steps 5.4/5.5/5.7 — the user's theme beyond the accent and base: the chat background of each mode and
    /// the outgoing-bubble colour. Global (like AccentColor: LoginForm exists before any account). Persisted as
    /// AppSettings.Theme; every string colour is "" (default) or "#RRGGBB".</summary>
    public sealed class ThemeSettings
    {
        public ChatBackgroundSettings Light { get; set; } = new ChatBackgroundSettings();
        public ChatBackgroundSettings Dark { get; set; } = new ChatBackgroundSettings();

        /// <summary>Step 5.7 — the outgoing bubble: "" = the accent (today), or "#RRGGBB".</summary>
        public string OutBubble { get; set; } = "";

        public ChatBackgroundSettings For(bool dark) { return dark ? Dark : Light; }

        /// <summary>Null-safe repair after load (an old or hand-edited file).</summary>
        public void Repair()
        {
            if (Light == null) Light = new ChatBackgroundSettings();
            if (Dark == null) Dark = new ChatBackgroundSettings();
            Light.Repair(); Dark.Repair();
            if (OutBubble == null) OutBubble = "";
        }
    }

    /// <summary>One mode's chat background. Mode: "" = the theme's plain ChatBg (today, and the fast path);
    /// "preset" (Preset = index into Palette.ChatPresets); "solid" (Color1); "gradient" (Color1 → Color2 at Angle: 0 =
    /// left→right, 90 = top→bottom, 45 = diagonal); "pattern" (Pattern over Color1, or ChatBg when empty); "photo"
    /// (ImageFile under &lt;DataRoot&gt;\theme, with Fit, Dim and Blur).</summary>
    public sealed class ChatBackgroundSettings
    {
        public string Mode { get; set; } = "";
        public int Preset { get; set; }
        public string Color1 { get; set; } = "";
        public string Color2 { get; set; } = "";
        public int Angle { get; set; }
        public string Pattern { get; set; } = "";
        public string ImageFile { get; set; } = "";
        public int ImageRev { get; set; }
        public string Fit { get; set; } = "fill";   // fill | fit | center
        public int Dim { get; set; }                // 0-80 (%)
        public bool Blur { get; set; }

        public void Repair()
        {
            if (Mode == null) Mode = "";
            if (Color1 == null) Color1 = "";
            if (Color2 == null) Color2 = "";
            if (Pattern == null) Pattern = "";
            if (ImageFile == null) ImageFile = "";
            if (Fit != "fit" && Fit != "center") Fit = "fill";
            if (Dim < 0) Dim = 0; if (Dim > 80) Dim = 80;
            if (Angle != 0 && Angle != 45 && Angle != 90) Angle = 0;
        }

        public ChatBackgroundSettings Clone() { return (ChatBackgroundSettings)MemberwiseClone(); }
    }
}
