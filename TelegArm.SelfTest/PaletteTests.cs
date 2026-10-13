using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using TelegArm.Helpers;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 1.2: Palette golden values, ColorMath, the accent override and the trailing debounce.
    /// The golden values below were read off the code by hand (file:line in Palette.cs) — they are the contract
    /// step 1.7 relies on to swap literals for tokens without moving a pixel.</summary>
    internal static class PaletteTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            Add(t, "palette: chat and chat-list tokens equal today's literals (dark and light)", GoldenChat);
            Add(t, "palette: the selection-mode tint reproduces the original truncating Blend", SelectTintGolden);
            Add(t, "palette: chrome tokens carry DroidFinestra's values", ChromeValues);
            Add(t, "colormath: contrast is 21 for black/white, 1 for equal colours, symmetric", ContrastBasics);
            Add(t, "colormath: OnAccent keeps white on blues and magenta, goes dark on yellow/white/light green", OnAccentRule);
            Add(t, "colormath: AccentFg reaches 4.5:1 on the surface for every test accent, both themes", AccentFgContrast);
            Add(t, "theme: ParseAccent / FormatAccent round-trip; invalid input means follow Windows", ParseFormat);
            Add(t, "theme: an accent override wins everywhere and SetAccent raises once, palette already rebuilt", OverridePrecedence);
            Add(t, "theme: accent notifications debounce on the TRAILING edge; an override ignores them", TrailingDebounce);
            Add(t, "theme: PaletteFor(mode) is the cached palette for the current mode and a golden build for the other (step 1.7)", PaletteForModes);
        }

        private static void PaletteForModes()
        {
            ThemeHelper.SetAccent(Color.FromArgb(0x7B, 0x2F, 0xD6));
            try
            {
                var cur = ThemeHelper.Palette;
                Check.True(ReferenceEquals(cur, ThemeHelper.PaletteFor(cur.Dark)), "the current mode returns the cached instance");
                var other = ThemeHelper.PaletteFor(!cur.Dark);
                var want = Palette.Build(!cur.Dark, cur.Accent);
                Check.Equal(!cur.Dark, other.Dark, "the other mode");
                foreach (var pi in typeof(Palette).GetProperties())
                    if (pi.PropertyType == typeof(Color))
                        Check.Equal(((Color)pi.GetValue(want, null)).ToArgb(), ((Color)pi.GetValue(other, null)).ToArgb(), "other-mode token " + pi.Name);
                Check.True(ReferenceEquals(other, ThemeHelper.PaletteFor(!cur.Dark)), "the other mode is built once");
                ThemeHelper.SetAccent(Color.FromArgb(0x40, 0xA7, 0xE3));
                Check.Equal(Color.FromArgb(0x40, 0xA7, 0xE3).ToArgb(), ThemeHelper.PaletteFor(!ThemeHelper.Palette.Dark).Accent.ToArgb(), "an accent change rebuilds the other mode");
            }
            finally { ThemeHelper.SetAccent(null); }
        }

        private static void Add(List<KeyValuePair<string, Action>> t, string name, Action a) { t.Add(new KeyValuePair<string, Action>(name, a)); }

        private static readonly Color Magenta = Color.FromArgb(0xC2, 0x39, 0xB3);   // the dev box's accent

        private static void Same(Color want, Color got, string what)
        {
            if (want.ToArgb() != got.ToArgb()) throw new CheckFailed(what + ": expected " + Show(want) + ", got " + Show(got));
        }

        private static string Show(Color c) { return "ARGB(" + c.A + "," + c.R + "," + c.G + "," + c.B + ")"; }

        private static void GoldenChat()
        {
            foreach (bool dark in new[] { true, false })
            {
                var p = Palette.Build(dark, Magenta);
                string m = dark ? "dark " : "light ";
                Same(dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245), p.ChatBg, m + "ChatBg");
                Same(dark ? Color.FromArgb(60, 60, 63) : Color.FromArgb(226, 226, 226), p.BubbleIn, m + "BubbleIn");
                Same(dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20), p.BubbleInText, m + "BubbleInText");
                Same(dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110), p.BubbleInSub, m + "BubbleInSub");
                Same(dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(120, 120, 120), p.BubbleInTime, m + "BubbleInTime");
                Same(Magenta, p.BubbleOut, m + "BubbleOut");
                Same(Color.White, p.BubbleOutText, m + "BubbleOutText");
                Same(Color.FromArgb(210, 255, 255, 255), p.BubbleOutSub, m + "BubbleOutSub");
                Same(Color.FromArgb(220, 255, 255, 255), p.BubbleOutTime, m + "BubbleOutTime");
                Same(Color.FromArgb(110, Magenta), p.SelectionIn, m + "SelectionIn");
                Same(Color.FromArgb(95, 255, 255, 255), p.SelectionOut, m + "SelectionOut");
                Same(Magenta, p.LinkIn, m + "LinkIn");
                Same(Color.FromArgb(235, 255, 255, 255), p.LinkOut, m + "LinkOut");
                Same(dark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(26, 0, 0, 0), p.ServicePillBg, m + "ServicePillBg");
                Same(dark ? Color.FromArgb(190, 190, 196) : Color.FromArgb(105, 105, 110), p.ServicePillText, m + "ServicePillText");
                Same(dark ? Color.FromArgb(95, 95, 100) : Color.FromArgb(190, 190, 196), p.ScrollThumb, m + "ScrollThumb");
                Same(dark ? Color.FromArgb(234, 97, 219) : Magenta, p.ScrollThumbActive, m + "ScrollThumbActive (+40 in dark)");
                Same(dark ? Color.FromArgb(40, 40, 40) : Color.White, p.ListBg, m + "ListBg");
                Same(dark ? Color.White : Color.FromArgb(30, 30, 30), p.ListName, m + "ListName");
                Same(dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120), p.ListSub, m + "ListSub");
                Same(dark ? Color.FromArgb(55, 55, 58) : Color.FromArgb(225, 235, 245), p.ListSelected, m + "ListSelected");
                Same(dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(235, 235, 235), p.ListDivider, m + "ListDivider");
                Same(dark ? Color.FromArgb(240, 116, 116) : Color.FromArgb(214, 69, 69), p.ListDraft, m + "ListDraft");
                Same(dark ? Color.FromArgb(120, 120, 124) : Color.FromArgb(178, 178, 184), p.ListMuted, m + "ListMuted");
                Same(Color.FromArgb(76, 204, 90), p.OnlineDot, m + "OnlineDot");
            }
            Same(Color.FromArgb(255, 255, 255), Palette.Build(true, Color.FromArgb(250, 230, 240)).ScrollThumbActive, "ScrollThumbActive clamps at 255");
        }

        /// <summary>A verbatim copy of MessageBubbleControl.Blend — the reference the token must match.</summary>
        private static Color OriginalBlend(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));
        }

        private static void SelectTintGolden()
        {
            var accents = new[] { Magenta, Color.FromArgb(64, 167, 227), Color.FromArgb(0, 120, 215), Color.FromArgb(255, 235, 59), Color.Black, Color.White };
            foreach (var a in accents)
                foreach (bool dark in new[] { true, false })
                {
                    var p = Palette.Build(dark, a);
                    Same(OriginalBlend(a, p.ChatBg, 0.18f), p.ChatSelectTint, (dark ? "dark " : "light ") + "tint for " + Show(a));
                }
        }

        private static void ChromeValues()
        {
            var d = Palette.Build(true, Magenta); var l = Palette.Build(false, Magenta);
            Same(Color.FromArgb(32, 32, 36), d.Window, "dark Window"); Same(Color.FromArgb(245, 245, 248), l.Window, "light Window");
            Same(Color.FromArgb(38, 38, 43), d.Footer, "dark Footer"); Same(Color.FromArgb(236, 236, 240), l.Footer, "light Footer");
            Same(Color.FromArgb(44, 44, 50), d.Card, "dark Card"); Same(Color.White, l.Card, "light Card");
            Same(Color.FromArgb(58, 58, 64), d.Divider, "dark Divider"); Same(Color.FromArgb(220, 220, 226), l.Divider, "light Divider");
            Same(Color.FromArgb(236, 236, 240), d.Text, "dark Text"); Same(Color.FromArgb(28, 28, 32), l.Text, "light Text");
            Same(Color.FromArgb(150, 150, 156), d.TextDim, "dark TextDim"); Same(Color.FromArgb(110, 110, 118), l.TextDim, "light TextDim");
            Same(Color.FromArgb(222, 74, 74), d.Danger, "Danger");
        }

        private static void ContrastBasics()
        {
            Check.True(Math.Abs(ColorMath.Contrast(Color.White, Color.Black) - 21.0) < 0.01, "white/black is not 21");
            Check.True(Math.Abs(ColorMath.Contrast(Magenta, Magenta) - 1.0) < 1e-9, "a colour against itself is not 1");
            Check.True(Math.Abs(ColorMath.Contrast(Magenta, Color.White) - ColorMath.Contrast(Color.White, Magenta)) < 1e-12, "not symmetric");
            double tg = ColorMath.Contrast(Color.White, Color.FromArgb(0x40, 0xA7, 0xE3));
            Check.True(tg > 2.5 && tg < 2.8, "white on Telegram blue should be ~2.6, got " + tg.ToString("0.00"));
        }

        private static void OnAccentRule()
        {
            Same(Color.White, ColorMath.OnColor(Color.FromArgb(0x40, 0xA7, 0xE3)), "on Telegram blue #40A7E3");
            Same(Color.White, ColorMath.OnColor(Color.FromArgb(0, 120, 215)), "on Windows blue");
            Same(Color.White, ColorMath.OnColor(Magenta), "on the dev box magenta");
            Same(Color.White, ColorMath.OnColor(Color.FromArgb(0x53, 0x4A, 0xB7)), "on the old TelegArm purple");
            Same(ColorMath.DarkInk, ColorMath.OnColor(Color.FromArgb(0xFF, 0xEB, 0x3B)), "on yellow");
            Same(ColorMath.DarkInk, ColorMath.OnColor(Color.White), "on white");
            Same(ColorMath.DarkInk, ColorMath.OnColor(Color.FromArgb(0x8B, 0xC3, 0x4A)), "on light green");
        }

        private static void AccentFgContrast()
        {
            var accents = new[] { Color.FromArgb(0x40, 0xA7, 0xE3), Color.FromArgb(0xFF, 0xEB, 0x3B), Color.White, Color.Black,
                                  Color.FromArgb(0x8B, 0xC3, 0x4A), Color.FromArgb(0, 120, 215), Magenta, Color.FromArgb(202, 80, 16) };
            foreach (var a in accents)
                foreach (bool dark in new[] { true, false })
                {
                    var p = Palette.Build(dark, a);
                    double c = ColorMath.Contrast(p.AccentFg, p.Surface);
                    Check.True(c >= 4.5, (dark ? "dark " : "light ") + "AccentFg for " + Show(a) + " is only " + c.ToString("0.00") + ":1");
                }
            // NB Windows' own default blue (0,120,215) on white is 4.499:1 — a hair UNDER the rule, so it IS nudged
            // (to 0,114,204). Measured, not assumed: an earlier version of this check expected it untouched.
            Same(Color.FromArgb(0, 90, 158), Palette.Build(false, Color.FromArgb(0, 90, 158)).AccentFg, "an accent that already reads is left alone");
        }

        private static void ParseFormat()
        {
            Same(Color.FromArgb(0x40, 0xA7, 0xE3), ThemeHelper.ParseAccent("#40a7e3").Value, "lower case with #");
            Same(Color.FromArgb(0x40, 0xA7, 0xE3), ThemeHelper.ParseAccent(" 40A7E3 ").Value, "upper case, no #, spaces");
            Check.Equal("#40A7E3", ThemeHelper.FormatAccent(ThemeHelper.ParseAccent("#40a7e3")), "format");
            Check.Equal("", ThemeHelper.FormatAccent(null), "format of null");
            foreach (var bad in new[] { null, "", "#12345", "#1234567", "#GGGGGG", "red" })
                Check.True(!ThemeHelper.ParseAccent(bad).HasValue, "'" + bad + "' parsed as a colour");
        }

        private static void OverridePrecedence()
        {
            var fixedAccent = Color.FromArgb(0x12, 0x34, 0x56);
            int raised = 0; Color seenInHandler = Color.Empty;
            Action h = () => { raised++; seenInHandler = ThemeHelper.Palette.Accent; };
            ThemeHelper.ThemeChanged += h;
            try
            {
                ThemeHelper.InitAccent(null);
                ThemeHelper.SetAccent(fixedAccent);
                Check.Equal(1, raised, "ThemeChanged raised by SetAccent");
                Same(fixedAccent, seenInHandler, "the palette a handler sees (must already be rebuilt)");
                Same(fixedAccent, ThemeHelper.GetWindowsAccentColor(), "GetWindowsAccentColor under an override");
                ThemeHelper.SetAccent(fixedAccent);
                Check.Equal(1, raised, "setting the same accent again raised ThemeChanged");
                ThemeHelper.SetAccent(null);
                Check.Equal(2, raised, "clearing the override");
                Check.True(ThemeHelper.GetWindowsAccentColor().ToArgb() != fixedAccent.ToArgb(), "the override outlived SetAccent(null)");
            }
            finally { ThemeHelper.ThemeChanged -= h; ThemeHelper.InitAccent(null); }
        }

        /// <summary>Waits like a UI thread does — pumping messages. The settled recolor is POSTED to the thread's
        /// synchronization context, and an earlier check that created a window handle installed the WinForms one
        /// on this thread; a plain Sleep would starve it (that is exactly what the app's UI thread never does).</summary>
        private static void Pump(int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount - end < 0) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
        }

        private static void TrailingDebounce()
        {
            int raised = 0; int lastAt = 0; int start = Environment.TickCount;
            Action h = () => { Interlocked.Increment(ref raised); lastAt = Environment.TickCount - start; };
            ThemeHelper.ThemeChanged += h;
            try
            {
                ThemeHelper.InitAccent(Color.FromArgb(1, 2, 3));
                ThemeHelper.NotifyAccentChanged();
                Pump(600);
                Check.Equal(0, raised, "a fixed accent still reacted to a Windows accent change");

                ThemeHelper.InitAccent(null);
                start = Environment.TickCount;
                for (int i = 0; i < 4; i++) { ThemeHelper.NotifyAccentChanged(); Pump(100); }   // a burst over ~400 ms
                Pump(700);
                Check.Equal(1, raised, "notifications in one burst");
                Check.True(lastAt >= 500, "the recolor came " + lastAt + " ms in, before the burst had settled (trailing edge lands ~600, leading ~0)");
            }
            finally { ThemeHelper.ThemeChanged -= h; ThemeHelper.InitAccent(null); }
        }
    }
}
