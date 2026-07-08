using System;
using System.IO;

namespace TelegArm.Core
{
    /// <summary>
    /// Resolves the writable cache root with a REAL write-test probe, Documents-first — the same
    /// approach CS-Ray uses for its profile settings. On Windows RT 8.1 the %APPDATA% (LocalApplicationData)
    /// location is unreliable, so we prefer Documents, fall back to AppData, then beside the executable,
    /// and only as an absolute last resort the temp folder. Each candidate is validated by actually
    /// creating the folder and writing+deleting a probe file — NOT a mere Directory.Exists check.
    /// </summary>
    public static class StoragePaths
    {
        private const string AppFolder = "TelegArm";
        private const string CacheSub = "Cache";

        private static string _cacheRoot;   // resolved once per process
        private static string _appDir;      // resolved once per process (user-readable files: the log)
        private static string _dataRoot;    // resolved once per process (session/account files, settings.json)
        private static string _cacheVia;    // WHICH candidate won: LocalAppData/Documents/beside-exe/temp (for [SESSPATH] diag)

        /// <summary>Which candidate location the resolved cache/data root came from — "LocalAppData", "Documents",
        /// "beside-exe", or "temp". For the [SESSPATH] session-path diagnostic (ACCOUNT-SESSION-PATH-DIAG): if the
        /// INSTALLED build resolves a different base than the dev box, this is where it shows. Null until resolved.</summary>
        public static string CacheVia { get { return _cacheVia; } }

        /// <summary>The resolved, WRITABLE cache root for the CURRENT user. Prefers %LOCALAPPDATA% (the
        /// conventional cache/temp location); RT 8.1's AppData reliability is unknown, so every candidate is
        /// write-TESTED and we fall back to Documents (proven on RT), then beside-exe, then temp. Every path is
        /// the current user's own (Environment.GetFolderPath) — NEVER a hardcoded username. Memoized.</summary>
        public static string ResolveCacheRoot()
        {
            if (_cacheRoot != null) return _cacheRoot;

            string r;
            if ((r = Combine(SafeFolder(Environment.SpecialFolder.LocalApplicationData))) != null && CanWrite(r)) return Chose(r, "LocalAppData");
            if ((r = Combine(SafeFolder(Environment.SpecialFolder.MyDocuments)))          != null && CanWrite(r)) return Chose(r, "Documents");
            if ((r = Combine(AppDomain.CurrentDomain.BaseDirectory))                       != null && CanWrite(r)) return Chose(r, "beside-exe");

            r = Path.Combine(Path.GetTempPath(), AppFolder, CacheSub);   // absolute last resort
            try { Directory.CreateDirectory(r); } catch { }
            return Chose(r, "temp");
        }

        /// <summary>The writable per-user DATA root (…\TelegArm) — the PARENT of the resolved cache dir, so the
        /// session/account files (accounts\) and settings.json live alongside the cache under the SAME
        /// write-probed, LocalAppData-first root. NEVER the install directory: when installed to Program Files
        /// the exe folder is read-only to a normal user, which is why anything writing under BaseDirectory
        /// broke at first run. Reuses ResolveCacheRoot's single probe; memoized.</summary>
        public static string ResolveDataRoot()
        {
            if (_dataRoot != null) return _dataRoot;
            _dataRoot = Path.GetDirectoryName(ResolveCacheRoot());
            System.Diagnostics.Debug.WriteLine("[PATHS] data root (session/settings) = " + _dataRoot);
            return _dataRoot;
        }

        private static string Chose(string root, string via)
        {
            _cacheRoot = root;
            _cacheVia = via;
            System.Diagnostics.Debug.WriteLine("[CACHE] cache root = " + root + " (via " + via + ")");
            return root;
        }

        /// <summary>Real write-test (create + write + delete a probe file): true if the CURRENT user can write
        /// here. Public so the settings normalizer can reject a stale/wrong-user persisted cache path.</summary>
        public static bool IsWritable(string dir) { return CanWrite(dir); }

        /// <summary>The writable app-data dir (…\TelegArm) for user-READABLE files (the log) — Documents-FIRST
        /// so it's easy to find on RT (the user reads telegarm.log there), unlike the cache which prefers the
        /// hidden LocalAppData. Memoized.</summary>
        public static string ResolveAppDir()
        {
            if (_appDir != null) return _appDir;
            string r;
            if ((r = AppSub(SafeFolder(Environment.SpecialFolder.MyDocuments)))          != null && CanWrite(r)) return (_appDir = r);
            if ((r = AppSub(SafeFolder(Environment.SpecialFolder.LocalApplicationData))) != null && CanWrite(r)) return (_appDir = r);
            if ((r = AppSub(AppDomain.CurrentDomain.BaseDirectory))                       != null && CanWrite(r)) return (_appDir = r);
            _appDir = Path.Combine(Path.GetTempPath(), AppFolder);
            try { Directory.CreateDirectory(_appDir); } catch { }
            return _appDir;
        }

        private static string AppSub(string root) { return string.IsNullOrEmpty(root) ? null : Path.Combine(root, AppFolder); }

        /// <summary>True if <paramref name="path"/> is rooted under either AppData location (so the cache
        /// can be migrated off it). Used to relocate a legacy/AppData cache to the probed root.</summary>
        public static bool IsUnderAppData(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return StartsWith(path, SafeFolder(Environment.SpecialFolder.LocalApplicationData))
                || StartsWith(path, SafeFolder(Environment.SpecialFolder.ApplicationData));
        }

        private static string Combine(string root)
        {
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, AppFolder, CacheSub);
        }

        private static string SafeFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return null; }
        }

        private static bool StartsWith(string path, string root)
        {
            return !string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Real write test: create the folder, write a probe file, delete it. Not an existence check.</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
