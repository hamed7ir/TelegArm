using System;
using System.IO;

namespace TelegArm.Core
{
    /// <summary>
    /// The ACTIVE account + per-account file/cache path resolution. One account is connected at a time
    /// (switch-and-rebuild — concurrent clients are too heavy for RT). Sessions live under
    /// <c>accounts/{userId}/</c> (next to the exe); media caches under <c>&lt;cacheBase&gt;/{userId}/</c>.
    /// During first-run migration of a legacy single-account install, <see cref="LegacyMode"/> points at the
    /// old flat files until the resumed user's id is known and the files are relocated. CRITICAL: keying the
    /// cache by userId prevents the cross-account object-id collision (Telegram ids are account-scoped).
    /// </summary>
    public static class AccountContext
    {
        // User-WRITABLE data root (…\TelegArm), NOT the install directory. When installed to Program Files the
        // exe folder is read-only to a normal user, so sessions/accounts MUST live under the resolved user path
        // (the SAME root the cache uses). Lazy property → the resolver runs on first use, not at type-load.
        private static string Base { get { return StoragePaths.ResolveDataRoot(); } }

        /// <summary>The active user id; 0 = none / pending (add-account before the new id is known).</summary>
        public static long ActiveId { get; set; }

        /// <summary>True while resuming the pre-multi-account flat session (migrated to accounts/{id} after).</summary>
        public static bool LegacyMode { get; set; }

        public static bool HasActive { get { return ActiveId != 0; } }

        public static string AccountsRoot { get { return Path.Combine(Base, "accounts"); } }
        public static string AccountDir(long id) { return Path.Combine(AccountsRoot, id.ToString()); }
        public static string PendingDir { get { return Path.Combine(AccountsRoot, "_pending"); } }
        public static string ActivePointerPath { get { return Path.Combine(AccountsRoot, "active"); } }
        public static string MetaPath(long id) { return Path.Combine(AccountDir(id), "meta.json"); }

        // Legacy (pre-multi-account) flat files, next to the exe.
        public static string LegacySession { get { return Path.Combine(Base, "TelegArm.session"); } }
        public static string LegacyPhone { get { return Path.Combine(Base, "TelegArm.phone"); } }
        public static string LegacyUpdates { get { return Path.Combine(Base, "TelegArm.updates"); } }

        private static string Dir { get { return HasActive ? AccountDir(ActiveId) : PendingDir; } }

        /// <summary>The session file the WTelegram client opens (Config("session_pathname")).</summary>
        public static string SessionPath { get { return LegacyMode ? LegacySession : Path.Combine(Ensure(Dir), "session"); } }
        public static string PhonePath { get { return LegacyMode ? LegacyPhone : Path.Combine(Dir, "phone"); } }
        public static string UpdatePath { get { return LegacyMode ? LegacyUpdates : Path.Combine(Dir, "updates"); } }

        /// <summary>Per-account cache root under the resolved base (<c>&lt;base&gt;/{id}</c>); the base itself
        /// when there's no active id (the pending add-account window, which doesn't cache media).</summary>
        public static string CacheRootFor(string baseRoot)
        {
            return HasActive ? Path.Combine(baseRoot, ActiveId.ToString()) : baseRoot;
        }

        private static string Ensure(string d) { try { Directory.CreateDirectory(d); } catch { } return d; }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────
        //  [SESSPATH] DIAGNOSTIC (ACCOUNT-SESSION-PATH-DIAG) — read-only path logging for the INSTALLED build.
        //  Purpose: prove whether two accounts ever resolve to the SAME session file (the switch-corruption bug
        //  that only repros installed). SAFE: these READ paths + File.Exists only; the ONE write is a throwaway
        //  ".write_test_<guid>" probe (StoragePaths.IsWritable) — NEVER a session file, and it is deleted at once.
        //  Gated by Logger.Enabled (off by default) and emitted via Logger.Diag → Trace, so it survives Release.
        // ─────────────────────────────────────────────────────────────────────────────────────────────────

        private static bool _envLogged;

        /// <summary>One-shot [SESSPATH] environment dump: the resolved data base, WHICH candidate it came from,
        /// the accounts/ root, a real (non-session) write-test of that root, and the active-pointer path. No-op
        /// when logging is off; runs at most once per process.</summary>
        public static void LogEnvOnce()
        {
            if (!TelegArm.Helpers.Logger.Enabled || _envLogged) return;
            _envLogged = true;
            try
            {
                string root = AccountsRoot;
                bool writeTestOk = StoragePaths.IsWritable(root);   // writes+deletes a NON-session probe file only
                TelegArm.Helpers.Logger.Diag("[SESSPATH] ENV base=\"" + Base + "\" via=" + StoragePaths.CacheVia
                    + " accountsRoot=\"" + root + "\" writeTestOk=" + writeTestOk
                    + " activePointer=\"" + ActivePointerPath + "\"");
            }
            catch { }
        }

        /// <summary>[SESSPATH] logs the session file that account <paramref name="id"/> resolves to under the CURRENT
        /// base, whether it already exists, and its size — plus the global ActiveId/LegacyMode context. Feed it the
        /// same id from two different accounts: identical "session=" paths across accounts = the collision proven.
        /// READ-ONLY (path build + File.Exists). No-op when logging is off.</summary>
        public static void LogPaths(long id, string phase)
        {
            if (!TelegArm.Helpers.Logger.Enabled) return;
            try
            {
                string sess = Path.Combine(AccountDir(id), "session");
                bool exists = false; long bytes = -1;
                try { var fi = new FileInfo(sess); if (fi.Exists) { exists = true; bytes = fi.Length; } } catch { }
                TelegArm.Helpers.Logger.Diag("[SESSPATH] " + phase + " acct=" + id
                    + " session=\"" + sess + "\" exists=" + exists + " bytes=" + bytes
                    + " globalActiveId=" + ActiveId + " legacy=" + LegacyMode);
            }
            catch { }
        }
    }
}
