using System;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.2 — colour arithmetic for the Palette: WCAG contrast, mixing, and the two accent
    /// derivations a user-picked accent needs before it can ship (plan §2.6: white-on-accent is hard-coded
    /// everywhere today, so a light accent would make bubbles, buttons and the title bar unreadable).</summary>
    public static class ColorMath
    {
        /// <summary>WCAG 2 relative luminance (0 = black, 1 = white) of an opaque colour.</summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        private static double Lin(int channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG contrast ratio, 1 (none) to 21 (black on white). Order-independent.</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary><paramref name="a"/> moved toward <paramref name="b"/> by <paramref name="t"/> (0 = a, 1 = b), opaque.</summary>
        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return Color.FromArgb(a.R, a.G, a.B);
            if (t >= 1f) return Color.FromArgb(b.R, b.G, b.B);
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>The contrast a fill must reach with white for white text to be used on it. Tuned so Telegram's
        /// blue #40A7E3 (contrast 2.6) keeps white text, as Telegram does; yellow, light green and white get dark.</summary>
        public const double OnAccentThreshold = 2.5;

        /// <summary>Near-black used for text on a light accent (not pure black: it reads softer on colour).</summary>
        public static readonly Color DarkInk = Color.FromArgb(20, 20, 20);

        /// <summary>Text colour for anything filled with <paramref name="fill"/>: white when white reads well enough
        /// on it (see <see cref="OnAccentThreshold"/>), else near-black.</summary>
        public static Color OnColor(Color fill)
        {
            return Contrast(Color.White, fill) >= OnAccentThreshold ? Color.White : DarkInk;
        }

        /// <summary>The accent as INK — links, names, quote bars drawn on <paramref name="surface"/>: the accent itself
        /// when it already reaches <paramref name="minContrast"/> against the surface, else nudged toward black (light
        /// surface) or white (dark surface) in 5% steps until it does. Telegram Desktop splits fill #40a7e3 and
        /// ink #168acd the same way.</summary>
        public static Color Ink(Color accent, Color surface, double minContrast = 4.5)
        {
            Color c = Color.FromArgb(accent.R, accent.G, accent.B);
            Color toward = Luminance(surface) > 0.5 ? Color.Black : Color.White;
            for (int i = 1; i <= 20 && Contrast(c, surface) < minContrast; i++)
                c = Mix(accent, toward, i * 0.05f);
            return c;
        }

        /// <summary>A Color as "#RRGGBB" (alpha dropped) — for logs and the palette map.</summary>
        public static string Hex(Color c) { return "#" + (c.ToArgb() & 0xFFFFFF).ToString("X6"); }
    }
}
