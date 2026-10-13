using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign plan §5 type roles — the one place the 2.x sizes live, so every converted form agrees.</summary>
    public enum TextRole
    {
        /// <summary>13 pt bold — a window title (the compact bar's 11.5 is TitleBar's own).</summary>
        Title,
        /// <summary>12.5 pt bold — a dialog heading inside its body.</summary>
        DialogTitle,
        /// <summary>11 pt bold, drawn in Palette.AccentFg — a section header.</summary>
        Section,
        /// <summary>10.5 pt — ordinary text.</summary>
        Body,
        /// <summary>9.5 pt — a field caption or hint.</summary>
        Field,
        /// <summary>9.75 pt — message text (unchanged from 1.x).</summary>
        Message,
        /// <summary>10 pt bold — a button label.</summary>
        Button,
    }

    public static class TextRoles
    {
        public static float Points(TextRole role)
        {
            switch (role)
            {
                case TextRole.Title: return 13f;
                case TextRole.DialogTitle: return 12.5f;
                case TextRole.Section: return 11f;
                case TextRole.Field: return 9.5f;
                case TextRole.Message: return 9.75f;
                case TextRole.Button: return 10f;
                default: return 10.5f;
            }
        }

        public static FontStyle Style(TextRole role)
        {
            return role == TextRole.Title || role == TextRole.DialogTitle || role == TextRole.Section || role == TextRole.Button
                ? FontStyle.Bold : FontStyle.Regular;
        }

        /// <summary>The role's font at <paramref name="dpi"/> — the Persian face when <paramref name="text"/> is Persian.
        /// Shared cached pixel-unit font: never dispose it.</summary>
        public static Font Font(TextRole role, int dpi, string text = null)
        {
            return text != null ? FontHelper.ForAt(text, Points(role), dpi, Style(role))
                                : FontHelper.UiAt(Points(role), dpi, Style(role));
        }
    }
}
