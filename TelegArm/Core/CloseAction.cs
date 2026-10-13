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
}
