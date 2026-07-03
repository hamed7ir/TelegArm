namespace TelegArm.UI.Controls
{
    /// <summary>
    /// A message row that can briefly pulse an accent tint to draw the eye after a "scroll to message"
    /// jump (pinned message, reply target, Show-in-chat). Implemented by both bubble kinds so the jump
    /// code can flash whichever it lands on, type-agnostically.
    /// </summary>
    public interface IFlashable
    {
        /// <summary>Briefly pulses an accent highlight over the control.</summary>
        void Flash();
    }
}
