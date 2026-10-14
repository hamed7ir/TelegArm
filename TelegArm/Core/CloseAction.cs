namespace TelegArm.Core
{
    /// <summary>Redesign plan D8 — what the window's close button does. The setting itself ("When I close the window",
    /// default MinimizeToTray = today's behaviour) arrives with step 2.7c; step 1.4 only builds the dialog that asks.</summary>
    public enum CloseAction
    {
        Ask,
        MinimizeToTray,
        Exit,
    }

    /// <summary>Step 2.7c: the stored form of the setting (AppSettings.CloseBehavior). "" = MinimizeToTray, the default —
    /// so a later build may re-default it; anything unrecognised also falls back to it (never to Exit).</summary>
    public static class CloseBehaviorSetting
    {
        public static CloseAction Parse(string s)
        {
            if (string.Equals(s, "Ask", System.StringComparison.OrdinalIgnoreCase)) return CloseAction.Ask;
            if (string.Equals(s, "Exit", System.StringComparison.OrdinalIgnoreCase)) return CloseAction.Exit;
            return CloseAction.MinimizeToTray;
        }

        public static string Format(CloseAction a)
        {
            return a == CloseAction.Ask ? "Ask" : a == CloseAction.Exit ? "Exit" : "";
        }
    }
}
