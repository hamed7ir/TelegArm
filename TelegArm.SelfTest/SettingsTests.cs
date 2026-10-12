using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.SelfTest
{
    /// <summary>Step 0.4's promises about settings.json, as permanent checks. They replace the one-off harness that
    /// drove the released 1.9.0 exe (mdfiles/probes/settings-*-0.4.ps1): the fixture is a file 1.9.0 itself wrote
    /// (only its machine-specific MediaCacheFolder replaced), and <see cref="V19Settings"/> stands in for 1.9.0
    /// reading a file back.</summary>
    internal static class SettingsTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            Add(t, "settings: a 1.9.0 file loads with every value intact", OldFileLoadsIntact);
            Add(t, "settings: first load migrates (DpiMode, SettingsVersion) and keeps a byte-identical v1.9 backup", MigrationAndBackup);
            Add(t, "settings: 1.9.0 reads what this build writes and loses nothing", OldBuildReadsNewFile);
            Add(t, "settings: after a 1.9.0 re-save this build migrates again; the backup is untouched", RollbackResave);
            Add(t, "settings: explicit nulls fall back to the defaults", NullsRestoreDefaults);
            Add(t, "settings: an unreadable file is preserved, never overwritten", CorruptFilePreserved);
            Add(t, "settings: DpiMode is the source of truth; DpiUnaware only mirrors it", DpiModeIsTheSourceOfTruth);
            Add(t, "settings: 8 threads x 50 saves never fail or leave a temp file", ParallelSaves);
        }

        private static void Add(List<KeyValuePair<string, Action>> t, string name, Action a)
        {
            t.Add(new KeyValuePair<string, Action>(name, a));
        }

        private static string Backup { get { return Path.Combine(Program.DataRoot, "settings.v1.9.json"); } }

        private static string Fixture()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings-1.9.0.json"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        /// <summary>Starts each check from an empty data root, optionally with a given settings.json.</summary>
        private static void Fresh(string settingsJson)
        {
            foreach (var f in Directory.GetFiles(Program.DataRoot, "settings*")) File.Delete(f);
            if (settingsJson != null) File.WriteAllText(AppSettings.SettingsPath, settingsJson);
        }

        private static V19Settings AsV19(string json) { return JsonConvert.DeserializeObject<V19Settings>(json); }

        /// <summary>Every 1.9.0 property, by name. MediaCacheFolder is skipped: Load re-resolves a path that is not
        /// writable for this user, by design (the fixture's is deliberately unwritable).</summary>
        private static void SameAsV19(V19Settings expected, object actual, string context)
        {
            foreach (var p in typeof(V19Settings).GetProperties())
            {
                if (p.Name == "MediaCacheFolder") continue;
                var q = actual.GetType().GetProperty(p.Name);
                Check.True(q != null, context + ": property " + p.Name + " no longer exists");
                Check.Equal(p.GetValue(expected, null), q.GetValue(actual, null), context + ": " + p.Name);
            }
        }

        private static void OldFileLoadsIntact()
        {
            string fx = Fixture();
            Fresh(fx);
            SameAsV19(AsV19(fx), AppSettings.Load(), "after loading the 1.9.0 file");
        }

        private static void MigrationAndBackup()
        {
            string fx = Fixture();
            Fresh(fx);
            var s = AppSettings.Load();
            Check.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion, "SettingsVersion");
            Check.Equal("Unaware", s.DpiMode, "DpiMode migrated from DpiUnaware=true");
            Check.True(File.Exists(Backup), "settings.v1.9.json was not written");
            Check.True(File.ReadAllBytes(Backup).SequenceEqual(Encoding.UTF8.GetBytes(fx)), "settings.v1.9.json is not byte-identical to the 1.9.0 file");
        }

        private static void OldBuildReadsNewFile()
        {
            string fx = Fixture();
            Fresh(fx);
            AppSettings.Load().Save();
            SameAsV19(AsV19(fx), AsV19(File.ReadAllText(AppSettings.SettingsPath)), "1.9.0 reading this build's file");
        }

        private static void RollbackResave()
        {
            string fx = Fixture();
            Fresh(fx);
            AppSettings.Load().Save();
            // What 1.9.0's Save writes back: only the fields it knows (SettingsVersion and DpiMode are dropped).
            var old = AsV19(File.ReadAllText(AppSettings.SettingsPath));
            File.WriteAllText(AppSettings.SettingsPath, JsonConvert.SerializeObject(old, Formatting.Indented));
            var s = AppSettings.Load();
            Check.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion, "SettingsVersion after re-migration");
            Check.Equal("Unaware", s.DpiMode, "DpiMode after re-migration");
            Check.Equal(fx, File.ReadAllText(Backup), "settings.v1.9.json (must still be the original 1.9.0 file)");
        }

        private static void NullsRestoreDefaults()
        {
            Fresh("{ \"SendKey\": null, \"ProxyList\": null, \"ThemeMode\": null, \"DpiMode\": null, \"DefaultSaveFolder\": null, \"SettingsVersion\": 2 }");
            var s = AppSettings.Load();
            Check.Equal("Enter", s.SendKey, "SendKey");
            Check.True(s.ProxyList != null && s.ProxyList.Count == 0, "ProxyList is null or not empty");
            Check.Equal("System", s.ThemeMode, "ThemeMode");
            Check.Equal("", s.DpiMode, "DpiMode");
            Check.True(!string.IsNullOrEmpty(s.DefaultSaveFolder), "DefaultSaveFolder is empty");
        }

        private static void CorruptFilePreserved()
        {
            const string bad = "{ \"SendKey\": \"AltEnter\", \"ProxyList\": [ \"tg://x\" broken";
            Fresh(bad);
            var s = AppSettings.Load();
            var kept = Directory.GetFiles(Program.DataRoot, "settings.corrupt-*.json");
            Check.Equal(1, kept.Length, "preserved copies of the unreadable file");
            Check.Equal(bad, File.ReadAllText(kept[0]), "preserved content");
            Check.True(!File.Exists(Backup), "the unreadable file was taken for a 1.9 file and backed up");
            Check.Equal("Enter", s.SendKey, "SendKey after falling back to defaults");
            Check.True(JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(AppSettings.SettingsPath)) != null, "the new settings.json does not parse");
        }

        private static void DpiModeIsTheSourceOfTruth()
        {
            Fresh(null);
            var s = AppSettings.Load();
            s.DpiMode = "Unaware";
            s.Save();
            Check.True(AsV19(File.ReadAllText(AppSettings.SettingsPath)).DpiUnaware, "DpiUnaware was not mirrored on for a 1.9.0 rollback");
            s.DpiMode = "";
            s.DpiUnaware = true;   // a stray write to the mirror must lose
            s.Save();
            Check.True(!AsV19(File.ReadAllText(AppSettings.SettingsPath)).DpiUnaware, "a write to DpiUnaware overrode DpiMode");
        }

        private static void ParallelSaves()
        {
            Fresh(null);
            var s = AppSettings.Load();
            var log = new Capture();
            bool wasEnabled = Logger.Enabled;
            Trace.Listeners.Add(log);
            Logger.Enabled = true;
            try { Parallel.For(0, 8, i => { for (int k = 0; k < 50; k++) s.Save(); }); }
            finally { Logger.Enabled = wasEnabled; Trace.Listeners.Remove(log); }
            Check.Equal(0, log.Lines.Count(l => l.Contains("save failed")), "logged save failures");
            Check.True(!File.Exists(AppSettings.SettingsPath + ".tmp"), "a .tmp file was left behind");
            Check.True(JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(AppSettings.SettingsPath)) != null, "settings.json does not parse");
        }

        /// <summary>Collects Logger.Diag output (it writes through Trace).</summary>
        private sealed class Capture : TraceListener
        {
            public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
            public override void Write(string message) { Lines.Enqueue(message); }
            public override void WriteLine(string message) { Lines.Enqueue(message); }
        }
    }
}
