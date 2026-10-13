using System;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.2 — every colour the UI uses, as named tokens, for one (dark, accent) pair. Immutable;
    /// <see cref="ThemeHelper.Palette"/> holds the current one and rebuilds it BEFORE ThemeChanged fires, so a
    /// paint handler reads a field instead of the registry (plan §2.6: today a bubble's paint reads the accent
    /// and the dark flag from the registry on every paint, for hundreds of bubbles on a Tegra 3).
    ///
    /// TWO GROUPS, ON PURPOSE:
    ///   · CHAT and CHAT LIST tokens are GOLDEN — exactly today's literals and arithmetic (file:line cited), so
    ///     step 1.7 can swap the literals for tokens with a ZERO-pixel diff against the baseline. Do not "tidy"
    ///     them; change them in a later, visible step.
    ///   · CHROME tokens are the new design (DroidFinestra's values), used by the new controls from step 1.3.
    ///     Surface tokens without a DF value start at the nearest current literal and are tuned in step 5.1.
    /// The accent-derived inks (OnAccent, AccentFg) are what makes a user-picked light accent readable (5.3).</summary>
    public sealed class Palette
    {
        public bool Dark { get; private set; }

        // ── brand ────────────────────────────────────────────────────────────────────────────────────────────
        public Color Accent { get; private set; }
        public Color AccentHover { get; private set; }
        public Color AccentPressed { get; private set; }
        /// <summary>Text/glyphs ON an accent fill: white, or near-black on a light accent.</summary>
        public Color OnAccent { get; private set; }
        /// <summary>The accent as ink on <see cref="Surface"/> (links, names): contrast ≥ 4.5.</summary>
        public Color AccentFg { get; private set; }
        /// <summary>An opaque accent wash on <see cref="Surface"/> (selected rows, chips).</summary>
        public Color AccentSoft { get; private set; }
        public Color TitleBar { get; private set; }

        // ── surfaces (chrome) ────────────────────────────────────────────────────────────────────────────────
        public Color Window { get; private set; }
        public Color Footer { get; private set; }
        public Color Card { get; private set; }
        public Color Surface { get; private set; }
        public Color SurfaceAlt { get; private set; }
        public Color Sidebar { get; private set; }
        public Color Header { get; private set; }
        public Color Composer { get; private set; }
        public Color Divider { get; private set; }
        public Color Hover { get; private set; }
        public Color Selected { get; private set; }

        // ── text (chrome) ────────────────────────────────────────────────────────────────────────────────────
        public Color Text { get; private set; }
        public Color TextDim { get; private set; }
        public Color TextFaint { get; private set; }
        public Color TextDisabled { get; private set; }

        // ── chat (GOLDEN) ────────────────────────────────────────────────────────────────────────────────────
        /// <summary>MessageBubbleControl:1645, VoiceBubbleControl:236, ServiceLineControl:34, SponsoredCardControl:144,
        /// ThemedScrollBar:188, MainForm.Theme:44,49 — the seven sites that must agree.</summary>
        public Color ChatBg { get; private set; }
        /// <summary>A row's background in selection mode: Blend(accent, ChatBg, 0.18), TRUNCATED like the original
        /// (MessageBubbleControl:1646/1774, VoiceBubbleControl:237/311).</summary>
        public Color ChatSelectTint { get; private set; }
        public Color BubbleIn { get; private set; }        // MessageBubbleControl:435
        public Color BubbleInText { get; private set; }    // :436
        public Color BubbleInSub { get; private set; }     // :452
        public Color BubbleInTime { get; private set; }    // :496
        public Color BubbleOut { get; private set; }       // :435 (the accent)
        public Color BubbleOutText { get; private set; }   // :436 (white — NOT OnAccent yet; that is step 5.3)
        public Color BubbleOutSub { get; private set; }    // :451
        public Color BubbleOutTime { get; private set; }   // :495
        public Color SelectionIn { get; private set; }     // :697
        public Color SelectionOut { get; private set; }    // :697
        public Color LinkIn { get; private set; }          // :706
        public Color LinkOut { get; private set; }         // :706
        public Color ServicePillBg { get; private set; }   // ServiceLineControl:46
        public Color ServicePillText { get; private set; } // ServiceLineControl:50
        public Color ScrollThumb { get; private set; }     // ThemedScrollBar:196
        public Color ScrollThumbActive { get; private set; } // ThemedScrollBar:195 (+40 per channel in dark, clamped)

        // ── chat list (GOLDEN, ChatListItemControl) ─────────────────────────────────────────────────────────
        public Color ListBg { get; private set; }          // :85, MainForm.Theme:42,137
        public Color ListName { get; private set; }        // :86
        public Color ListSub { get; private set; }         // :87
        public Color ListSelected { get; private set; }    // :89
        public Color ListDivider { get; private set; }     // :226
        public Color ListDraft { get; private set; }       // :170
        public Color ListMuted { get; private set; }       // :192
        public Color OnlineDot { get; private set; }       // :29

        // ── status ───────────────────────────────────────────────────────────────────────────────────────────
        public Color Danger { get; private set; }
        public Color Success { get; private set; }
        public Color Warning { get; private set; }

        private Palette() { }

        private static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        private static Color Pick(bool dark, Color d, Color l) { return dark ? d : l; }

        public static Palette Build(bool dark, Color accent)
        {
            accent = Color.FromArgb(accent.R, accent.G, accent.B);   // opaque, whatever arrived
            var p = new Palette { Dark = dark, Accent = accent };

            // chat + chat list: golden
            p.ChatBg = Pick(dark, Rgb(30, 30, 30), Rgb(245, 245, 245));
            p.ChatSelectTint = Color.FromArgb(
                (int)(accent.R * 0.18f + p.ChatBg.R * (1 - 0.18f)),
                (int)(accent.G * 0.18f + p.ChatBg.G * (1 - 0.18f)),
                (int)(accent.B * 0.18f + p.ChatBg.B * (1 - 0.18f)));
            p.BubbleIn = Pick(dark, Rgb(60, 60, 63), Rgb(226, 226, 226));
            p.BubbleInText = Pick(dark, Rgb(235, 235, 235), Rgb(20, 20, 20));
            p.BubbleInSub = Pick(dark, Rgb(160, 160, 160), Rgb(110, 110, 110));
            p.BubbleInTime = Pick(dark, Rgb(160, 160, 160), Rgb(120, 120, 120));
            p.BubbleOut = accent;
            p.BubbleOutText = Color.White;
            p.BubbleOutSub = Color.FromArgb(210, 255, 255, 255);
            p.BubbleOutTime = Color.FromArgb(220, 255, 255, 255);
            p.SelectionIn = Color.FromArgb(110, accent);
            p.SelectionOut = Color.FromArgb(95, 255, 255, 255);
            p.LinkIn = accent;
            p.LinkOut = Color.FromArgb(235, 255, 255, 255);
            p.ServicePillBg = Pick(dark, Color.FromArgb(70, 0, 0, 0), Color.FromArgb(26, 0, 0, 0));
            p.ServicePillText = Pick(dark, Rgb(190, 190, 196), Rgb(105, 105, 110));
            p.ScrollThumb = Pick(dark, Rgb(95, 95, 100), Rgb(190, 190, 196));
            p.ScrollThumbActive = dark ? Rgb(Math.Min(255, accent.R + 40), Math.Min(255, accent.G + 40), Math.Min(255, accent.B + 40)) : accent;
            p.ListBg = Pick(dark, Rgb(40, 40, 40), Color.White);
            p.ListName = Pick(dark, Color.White, Rgb(30, 30, 30));
            p.ListSub = Pick(dark, Rgb(150, 150, 150), Rgb(120, 120, 120));
            p.ListSelected = Pick(dark, Rgb(55, 55, 58), Rgb(225, 235, 245));
            p.ListDivider = Pick(dark, Rgb(55, 55, 55), Rgb(235, 235, 235));
            p.ListDraft = Pick(dark, Rgb(240, 116, 116), Rgb(214, 69, 69));
            p.ListMuted = Pick(dark, Rgb(120, 120, 124), Rgb(178, 178, 184));
            p.OnlineDot = Rgb(76, 204, 90);

            // chrome: DroidFinestra's values where it has one; else the nearest current literal (tuned in 5.1)
            p.Window = Pick(dark, Rgb(32, 32, 36), Rgb(245, 245, 248));
            p.Footer = Pick(dark, Rgb(38, 38, 43), Rgb(236, 236, 240));
            p.Card = Pick(dark, Rgb(44, 44, 50), Color.White);
            p.Divider = Pick(dark, Rgb(58, 58, 64), Rgb(220, 220, 226));
            p.Text = Pick(dark, Rgb(236, 236, 240), Rgb(28, 28, 32));
            p.TextDim = Pick(dark, Rgb(150, 150, 156), Rgb(110, 110, 118));
            p.TextFaint = p.ListMuted;
            p.TextDisabled = Pick(dark, Rgb(100, 100, 106), Rgb(176, 176, 182));
            p.Surface = p.ListBg;          // the chat list: the largest chrome surface today
            p.SurfaceAlt = p.ChatBg;
            p.Sidebar = p.ListBg;
            p.Header = p.ListBg;
            p.Composer = p.ListBg;
            p.Selected = p.ListSelected;
            p.Hover = Color.FromArgb(40, accent);   // MainForm.Theme:97 (header buttons' hover)
            p.Danger = Rgb(222, 74, 74);
            p.Success = Rgb(76, 175, 80);           // the quiz "correct" green, MessageBubbleControl:1351
            p.Warning = Rgb(232, 163, 23);

            // brand
            p.TitleBar = accent;
            p.AccentHover = ColorMath.Mix(accent, dark ? Color.White : Color.Black, 0.10f);
            p.AccentPressed = ColorMath.Mix(accent, dark ? Color.White : Color.Black, 0.20f);
            p.OnAccent = ColorMath.OnColor(accent);
            p.AccentFg = ColorMath.Ink(accent, p.Surface);
            p.AccentSoft = ColorMath.Mix(p.Surface, accent, 0.18f);
            return p;
        }
    }
}
