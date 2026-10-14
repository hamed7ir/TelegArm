using System;
using System.Collections.Generic;
using System.Text;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>Device trip 2026-10 (alpha.2): the switches that let ONE sitting on the Surface RT tell what kills typing
    /// there, and the counters that record it. Analysis: <c>mdfiles/analysis/2026-10-13-composer-*.md</c>.
    ///
    /// The record (NOTES §10, "Composer analysis results") clears MaterialSkin's TEXT BOX — it has no input code at all —
    /// and points at TelegArm's own touch-keyboard handling and/or MaterialForm hosting: typing worked until the day the
    /// window shrink and the activation guard shipped (2026-07-01), and CS-Ray types fine with the very same text box.
    /// So nothing is deleted before the trip; each suspect gets an off switch instead, all LIVE (read per event):
    ///   • <see cref="Shim"/> — the forced-insert workaround on the composer/search EDIT: On (1.x) / Observe (never force;
    ///     count and log dead keystrokes) / Off (not attached — restart; also drops the dismiss bookkeeping it carries);
    ///   • <see cref="DismissGuard"/>, <see cref="RefocusClear"/>, <see cref="ActivationGuard"/> —
    ///     the touch-keyboard workarounds, one by one, and the window shrink (<see cref="ResizeForKeyboard"/>: its own
    ///     setting, or the "resize" token in TELEGARM_KBOFF for a scripted run).
    /// Defaults are 1.x behaviour exactly ("" everywhere). Env vars override for scripted runs (read once).</summary>
    internal static class InputPolicy
    {
        public enum ShimMode { On, Observe, Off }

        private static readonly string EnvShim = Env("TELEGARM_INPUTSHIM");
        private static readonly string EnvKbOff = Env("TELEGARM_KBOFF");

        private static string Env(string name)
        {
            try { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? null : v; }
            catch { return null; }
        }

        /// <summary>The input shim's mode. TELEGARM_INPUTSHIM=on|observe|off wins over the setting.</summary>
        public static ShimMode Shim
        {
            get
            {
                string v = EnvShim;
                if (v == null) { try { v = AppSettings.Instance.InputShim; } catch { v = null; } }
                if (string.Equals(v, "Observe", StringComparison.OrdinalIgnoreCase)) return ShimMode.Observe;
                if (string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase)) return ShimMode.Off;
                return ShimMode.On;
            }
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
                return "shim=" + Shim + (EnvShim != null ? "(env)" : "")
                    + " dismiss=" + Onf(DismissGuard) + " refocus=" + Onf(RefocusClear)
                    + " actguard=" + Onf(ActivationGuard) + (EnvKbOff != null ? "(env)" : "")
                    + " resize=" + Onf(resize) + " engineSetting=" + (string.IsNullOrEmpty(engine) ? "Legacy" : engine)
#if DEBUG
                    + " build=Debug";
#else
                    + " build=Release";
#endif
            }
        }

        private static string Onf(bool b) => b ? "on" : "off";

        // ── the counters (UI thread only) ───────────────────────────────────────────────────────────────────────

        /// <summary>Per text field (composer / search, with the engine in the name), totals since launch.</summary>
        public sealed class FieldStats
        {
            public int Sessions, SessionsWithDead, Native, Dead, Forced, Yank, NavNative, NavDead;
        }

        private static readonly Dictionary<string, FieldStats> _stats = new Dictionary<string, FieldStats>();

        public static FieldStats For(string field)
        {
            FieldStats f;
            if (!_stats.TryGetValue(field, out f)) { f = new FieldStats(); _stats[field] = f; }
            return f;
        }

        /// <summary>The counters as text (Settings → Advanced → Input counters, DiagReport). No typed text, ever.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Input switches: " + Summary);
            if (_stats.Count == 0) { sb.Append("No typing recorded yet in this run."); return sb.ToString(); }
            foreach (var kv in _stats)
            {
                var f = kv.Value;
                sb.AppendLine(kv.Key + ": focus sessions " + f.Sessions + " (with dead keys " + f.SessionsWithDead + ")"
                    + " · keys native " + f.Native + " dead " + f.Dead + " forced " + f.Forced
                    + " · caret yanks " + f.Yank + " · nav native " + f.NavNative + " dead " + f.NavDead);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
