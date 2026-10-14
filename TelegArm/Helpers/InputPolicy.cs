using System;
using System.Collections.Generic;
using System.Text;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>The touch-keyboard workarounds, each with an off switch (Settings > Advanced, or TELEGARM_KBOFF for a scripted
    /// run): keep the keyboard closed after it is dismissed, clear the auto-refocus after a reactivation, ignore its
    /// activation steal, and move the window above it. Defaults are all on. (The device-trip input shim and its counters were
    /// removed in plan step E5; the TabTip launcher in d9123b5.)</summary>
    internal static class InputPolicy
    {
        private static readonly string EnvKbOff = Env("TELEGARM_KBOFF");

        private static string Env(string name)
        {
            try { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? null : v; }
            catch { return null; }
        }

        /// <summary>The touch-keyboard workaround tokens that can be switched off.</summary>
        public const string Dismiss = "dismiss", Refocus = "refocus", ActGuard = "actguard", Resize = "resize";

        /// <summary>Is this touch-keyboard workaround ON? (TELEGARM_KBOFF / AppSettings.KeyboardOff list the OFF ones.)</summary>
        public static bool IsOn(string token)
        {
            string list = EnvKbOff;
            if (list == null) { try { list = AppSettings.Instance.KeyboardOff; } catch { list = null; } }
            return !Contains(list, token);
        }

        /// <summary>After the keyboard is closed: park focus on the sink and bounce the OS re-summon (the "it never
        /// goes down" fix).</summary>
        public static bool DismissGuard => IsOn(Dismiss);
        /// <summary>Clear WinForms' auto-refocus of a text field after a non-click reactivation (WA_ACTIVE).</summary>
        public static bool RefocusClear => IsOn(Refocus);
        /// <summary>Ignore WM_ACTIVATEAPP=false while the keyboard steals activation (shipped 2026-07-01 with the shrink).</summary>
        public static bool ActivationGuard => IsOn(ActGuard);

        /// <summary>Shrink the main window above the keyboard / shift dialogs: the setting (Settings row) AND not listed off
        /// (so TELEGARM_KBOFF=resize can switch it off for a scripted run too).</summary>
        public static bool ResizeForKeyboard
        {
            get
            {
                bool setting = true;
                try { setting = AppSettings.Instance.ResizeForKeyboard; } catch { }
                return setting && IsOn(Resize);
            }
        }

        /// <summary>Adds or removes <paramref name="token"/> from the setting's OFF list (Settings rows).</summary>
        public static void SetOn(string token, bool on)
        {
            var s = AppSettings.Instance;
            var set = new List<string>();
            foreach (var t in (s.KeyboardOff ?? "").Split(',')) { var x = t.Trim().ToLowerInvariant(); if (x.Length > 0 && !set.Contains(x)) set.Add(x); }
            set.Remove(token);
            if (!on) set.Add(token);
            s.KeyboardOff = string.Join(",", set);
            s.Save();
            Logger.Diag("[INPUT] " + Summary);
        }

        private static bool Contains(string list, string token)
        {
            if (string.IsNullOrEmpty(list)) return false;
            foreach (var t in list.Split(',')) if (string.Equals(t.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>One line for the log and DiagReport: every switch's effective value and where it came from.</summary>
        public static string Summary
        {
            get
            {
                bool resize = ResizeForKeyboard; string engine = "";
                try { engine = AppSettings.Instance.ComposerEngine; } catch { }
                return "dismiss=" + Onf(DismissGuard) + " refocus=" + Onf(RefocusClear)
                    + " actguard=" + Onf(ActivationGuard) + (EnvKbOff != null ? "(env)" : "")
                    + " resize=" + Onf(resize) + " engineSetting=" + (string.IsNullOrEmpty(engine) ? "default(Emoji)" : engine)
#if DEBUG
                    + " build=Debug";
#else
                    + " build=Release";
#endif
            }
        }

        private static string Onf(bool b) => b ? "on" : "off";

    }
}
