using System;
using System.Collections.Generic;
using System.IO;
using TelegArm.Core;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 0.6 — runs every registered check and returns 0 when all pass, 1 otherwise.
    ///   TelegArm.SelfTest.exe [name-filter]
    /// Deliberately no test framework: one exe that runs on the dev box and on the device alike, with nothing to
    /// install. Later phases add their checks here (plan §3, step 0.6): Dpi.Px, Palette golden values, ColorMath,
    /// Obfuscated2 / PacketSplitter, WsClient against a local echo server, the WindowPlacement clamp.
    ///
    /// ⚠ Every writable path is isolated first (StoragePaths.UseIsolatedRoot), so no check can touch the real
    ///   profile: settings, accounts and logs all resolve into a throwaway temp folder.</summary>
    internal static class Program
    {
        internal static string DataRoot;

        private static int Main(string[] args)
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "TelegArm-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            StoragePaths.UseIsolatedRoot(DataRoot);

            var tests = new List<KeyValuePair<string, Action>>();
            SettingsTests.Register(tests);
            DpiTests.Register(tests);
            PaletteTests.Register(tests);
            ChromeTests.Register(tests);
            EmojiTests.Register(tests);
            WsTunnelTests.Register(tests);
            ProxyTests.Register(tests);
            ComposerTests.Register(tests);
            MediaTests.Register(tests);

            string filter = args.Length > 0 ? args[0] : null;
            int pass = 0, fail = 0;
            foreach (var t in tests)
            {
                if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    t.Value();
                    Console.WriteLine("  PASS  " + t.Key);
                    pass++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  FAIL  " + t.Key);
                    Console.WriteLine("        " + (ex is CheckFailed ? ex.Message : ex.ToString()).Replace("\n", "\n        "));
                    fail++;
                }
            }
            try { Directory.Delete(DataRoot, true); } catch { }
            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "All " + pass + " checks passed." : fail + " of " + (pass + fail) + " checks FAILED.");
            return fail == 0 ? 0 : 1;
        }
    }
}
