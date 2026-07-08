using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TelegArm.Core
{
    /// <summary>One account as shown in the switcher.</summary>
    public sealed class AccountInfo
    {
        public long Id;
        public string Name;
        public string AvatarPath;   // Cache/{id}/thumbs/avatar_{id}.jpg if it exists, else null
        public long LastActiveTicks; // WARMUP-FIX 1.2: last time this account was ACTIVATED → warm most-recent first
    }

    /// <summary>
    /// On-disk account registry: the accounts/ directory (one subdir per userId holding session/phone/
    /// updates/meta), the active-account pointer, legacy single-account migration, and per-account relocate/
    /// delete (session + cache subtree). Pure storage; no UI, no networking.
    /// </summary>
    public static class AccountStore
    {
        private sealed class Meta { public long Id; public string Name; public long LastActiveTicks; }

        // Accounts whose files a background logout cleanup is mid-deleting. A switch / corrupt-session recovery
        // must NOT pick one of these as a target (its session is being removed → "being used by another process").
        private static readonly HashSet<long> _deleting = new HashSet<long>();
        public static void MarkDeleting(long id) { lock (_deleting) _deleting.Add(id); }
        public static void UnmarkDeleting(long id) { lock (_deleting) _deleting.Remove(id); }
        public static bool IsDeleting(long id) { lock (_deleting) return _deleting.Contains(id); }

        /// <summary>Reads the active pointer / detects a legacy install and primes <see cref="AccountContext"/>.</summary>
        public static void Init()
        {
            try { Directory.CreateDirectory(AccountContext.AccountsRoot); } catch { }
            AccountContext.LogEnvOnce();   // [SESSPATH] 0.1/0.5: base + which-location + a real write-test of accounts/

            var ids = AccountIds();
            if (ids.Count > 0)
            {
                long active = ReadActive();
                if (active == 0 || !ids.Contains(active)) active = ids[0];
                AccountContext.ActiveId = active;
                AccountContext.LegacyMode = false;
                WriteActive(active);
                System.Diagnostics.Debug.WriteLine("[ACCT] active account loaded id=" + active + " (of " + ids.Count + ")");
                // [SESSPATH] 0.4: the accounts present on disk + the chosen active. Two ids here that later collide on
                // ONE session path (see the client-open probe) is the corruption fingerprint.
                if (TelegArm.Helpers.Logger.Enabled)
                    TelegArm.Helpers.Logger.Diag("[SESSPATH] init ids=[" + string.Join(",", ids) + "] active=" + active + " count=" + ids.Count);
            }
            else if (File.Exists(AccountContext.LegacySession))
            {
                AccountContext.ActiveId = 0;
                AccountContext.LegacyMode = true;   // resume the flat session, migrate after the id is known
                System.Diagnostics.Debug.WriteLine("[ACCT] legacy single-account session detected → will migrate");
                if (TelegArm.Helpers.Logger.Enabled)
                    TelegArm.Helpers.Logger.Diag("[SESSPATH] init legacyMode session=\"" + AccountContext.LegacySession + "\"");
            }
            else if (TelegArm.Helpers.Logger.Enabled)
            {
                TelegArm.Helpers.Logger.Diag("[SESSPATH] init NO accounts, NO legacy session");
            }
        }

        /// <summary>True when there's any account to resume (a multi-account dir or a legacy session).</summary>
        public static bool HasAnyAccountOrLegacy()
        {
            return AccountIds().Count > 0 || File.Exists(AccountContext.LegacySession);
        }

        public static bool Exists(long id) { return File.Exists(Path.Combine(AccountContext.AccountDir(id), "session")); }

        /// <summary>All accounts (id + name + avatar-if-cached) for the switcher.</summary>
        public static List<AccountInfo> ListAccounts()
        {
            var list = new List<AccountInfo>();
            foreach (var id in AccountIds())
            {
                var m = ReadMeta(id);
                string av = AvatarPathFor(id);
                list.Add(new AccountInfo { Id = id, Name = (m != null && !string.IsNullOrEmpty(m.Name)) ? m.Name : ("Account " + id), AvatarPath = av, LastActiveTicks = m != null ? m.LastActiveTicks : 0L });
            }
            return list;
        }

        private static List<long> AccountIds()
        {
            var ids = new List<long>();
            try
            {
                if (!Directory.Exists(AccountContext.AccountsRoot)) return ids;
                foreach (var d in Directory.GetDirectories(AccountContext.AccountsRoot))
                {
                    long id;
                    if (long.TryParse(Path.GetFileName(d), out id) && File.Exists(Path.Combine(d, "session")) && !IsDeleting(id))
                        ids.Add(id);
                }
            }
            catch { }
            return ids.OrderBy(x => x).ToList();
        }

        public static long ReadActive()
        {
            long value = 0;
            try { long id; value = (File.Exists(AccountContext.ActivePointerPath) && long.TryParse(File.ReadAllText(AccountContext.ActivePointerPath).Trim(), out id)) ? id : 0; }
            catch { value = 0; }
            if (TelegArm.Helpers.Logger.Enabled)   // [SESSPATH] 0.4: what the active pointer currently reads back as
                TelegArm.Helpers.Logger.Diag("[SESSPATH] readActive value=" + value + " path=\"" + AccountContext.ActivePointerPath + "\"");
            return value;
        }

        public static void WriteActive(long id)
        {
            // [SESSPATH] 0.4 / hypothesis 2C: this write SWALLOWS its exception (catch {}). If it fails silently in
            // the installed build, the active pointer keeps the STALE id — a candidate for "the switch reverts". The
            // ok flag surfaces that in the log without changing the (behavior-neutral) swallow.
            bool ok = false;
            try { Directory.CreateDirectory(AccountContext.AccountsRoot); File.WriteAllText(AccountContext.ActivePointerPath, id.ToString()); ok = true; } catch { }
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[SESSPATH] writeActive id=" + id + " ok=" + ok + " path=\"" + AccountContext.ActivePointerPath + "\"");
        }

        public static void WriteMeta(long id, string name)
        {
            try
            {
                Directory.CreateDirectory(AccountContext.AccountDir(id));
                var prev = ReadMeta(id);   // preserve the last-active stamp across a name refresh
                File.WriteAllText(AccountContext.MetaPath(id), JsonConvert.SerializeObject(
                    new Meta { Id = id, Name = name ?? ("Account " + id), LastActiveTicks = prev != null ? prev.LastActiveTicks : 0L }));
            }
            catch { }
        }

        /// <summary>WARMUP-FIX 1.2: record that this account was just ACTIVATED, so startup warm-up prioritizes the
        /// most-recently-used accounts (the likeliest switch targets). Merges into the meta, preserving the name.</summary>
        public static void StampActive(long id)
        {
            try
            {
                var m = ReadMeta(id) ?? new Meta { Id = id };
                m.LastActiveTicks = DateTime.UtcNow.Ticks;
                Directory.CreateDirectory(AccountContext.AccountDir(id));
                File.WriteAllText(AccountContext.MetaPath(id), JsonConvert.SerializeObject(m));
            }
            catch { }
        }

        private static Meta ReadMeta(long id)
        {
            try { return File.Exists(AccountContext.MetaPath(id)) ? JsonConvert.DeserializeObject<Meta>(File.ReadAllText(AccountContext.MetaPath(id))) : null; }
            catch { return null; }
        }

        /// <summary>The self-avatar cache path for an account, if it's been cached (else null). Probes the
        /// legacy avatar_{id}.jpg AND the photo_id-keyed avatar_{id}_{photoId}.jpg the AvatarStore writes
        /// (the store renames legacy files on confirm, so both names must be honored here).</summary>
        public static string AvatarPathFor(long id)
        {
            try
            {
                string dir = Path.Combine(AppSettings.Instance.MediaCacheFolder, id.ToString(), "thumbs");
                string p = Path.Combine(dir, "avatar_" + id + ".jpg");
                if (File.Exists(p) && new FileInfo(p).Length > 0) return p;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "avatar_" + id + "_*.jpg"))
                        if (new FileInfo(f).Length > 0) return f;   // the store keeps exactly one current version
                return null;
            }
            catch { return null; }
        }

        /// <summary>Migrates the legacy flat install into accounts/{id}/ + moves Cache/ → Cache/{id}/.
        /// Caller must have DISPOSED the client first (the session file is locked while connected).</summary>
        public static void RelocateLegacyToAccount(long id, string name)
        {
            try { Directory.CreateDirectory(AccountContext.AccountDir(id)); } catch { }
            MoveFile(AccountContext.LegacySession, Path.Combine(AccountContext.AccountDir(id), "session"));
            MoveFile(AccountContext.LegacyPhone, Path.Combine(AccountContext.AccountDir(id), "phone"));
            MoveFile(AccountContext.LegacyUpdates, Path.Combine(AccountContext.AccountDir(id), "updates"));
            MoveLegacyCache(id);
            WriteMeta(id, name);
            WriteActive(id);
            System.Diagnostics.Debug.WriteLine("[ACCT] migrated legacy session+cache → accounts/" + id);
        }

        /// <summary>Moves accounts/_pending/* into accounts/{id}/ after an add-account login resolved the id.</summary>
        public static void RelocatePendingToAccount(long id, string name)
        {
            try
            {
                Directory.CreateDirectory(AccountContext.AccountDir(id));
                foreach (var f in new[] { "session", "phone", "updates" })
                    MoveFile(Path.Combine(AccountContext.PendingDir, f), Path.Combine(AccountContext.AccountDir(id), f));
                WriteMeta(id, name);
                WriteActive(id);
                System.Diagnostics.Debug.WriteLine("[ACCT] relocated _pending → accounts/" + id);
            }
            catch { }
        }

        public static void ClearPending()
        {
            // Robust: a just-disposed client can briefly hold the session-file lock, so retry. A leftover
            // _pending session is the IDENTITY BUG — the next add-account login would RESUME it (the old
            // account) instead of doing a fresh login.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(AccountContext.PendingDir)) return;
                    Directory.Delete(AccountContext.PendingDir, true);
                    return;
                }
                catch { System.Threading.Thread.Sleep(50); }
            }
            try { string s = Path.Combine(AccountContext.PendingDir, "session"); if (File.Exists(s)) File.Delete(s); } catch { }
            System.Diagnostics.Debug.WriteLine("[ACCT] WARNING: _pending not fully cleared");
        }

        /// <summary>True if a (stale) session lingers in accounts/_pending/ — it would be wrongly RESUMED by an
        /// add-account login (the identity bug). Used to assert a fresh add login.</summary>
        public static bool PendingSessionExists()
        {
            try { string s = Path.Combine(AccountContext.PendingDir, "session"); return File.Exists(s) && new FileInfo(s).Length > 0; }
            catch { return false; }
        }

        /// <summary>Removes an account entirely: its accounts/{id}/ session dir AND its Cache/{id}/ subtree.
        /// ASYNC + bounded-retry (await Task.Delay, never Thread.Sleep) so a briefly-locked session file (an
        /// abandoned dispose) clears as the lock releases — and never blocks the UI / loops forever.</summary>
        public static async System.Threading.Tasks.Task DeleteAccountAsync(long id)
        {
            await DeleteDirAsync(AccountContext.AccountDir(id), "accounts/" + id);
            await DeleteDirAsync(Path.Combine(AppSettings.Instance.MediaCacheFolder, id.ToString()), "Cache/" + id);
            // This removed Cache/{id}/thumbs|media (real directory deletion) — drop the created-this-run set so a
            // re-login of the same id in this run re-creates the folders instead of writing into deleted paths.
            MediaCache.InvalidateEnsured();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] DeleteAccount: done id=" + id);
        }

        private static async System.Threading.Tasks.Task DeleteDirAsync(string dir, string label)
        {
            if (string.IsNullOrEmpty(dir)) return;
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) { System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] DeleteAccount: " + label + " already gone"); return; }
                    Directory.Delete(dir, true);
                    System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] DeleteAccount: deleted " + label);
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] DeleteAccount: " + label + " delete retry " + attempt + " (locked: " + ex.Message + ")");
                    await System.Threading.Tasks.Task.Delay(150);
                }
            }
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] WARNING: " + label + " could not be deleted (still locked)");
        }

        private static void MoveLegacyCache(long id)
        {
            try
            {
                string baseRoot = AppSettings.Instance.MediaCacheFolder;
                string acctRoot = Path.Combine(baseRoot, id.ToString());
                Directory.CreateDirectory(acctRoot);
                foreach (var sub in new[] { "thumbs", "media" })
                {
                    string from = Path.Combine(baseRoot, sub), to = Path.Combine(acctRoot, sub);
                    if (Directory.Exists(from) && !Directory.Exists(to)) { try { Directory.Move(from, to); } catch { } }
                }
            }
            catch { }
        }

        /// <summary>Move that NEVER destroys the destination on a locked source. We move the source to a temp
        /// beside dest FIRST (throws harmlessly if the source is still locked — dest untouched), then swap it in.
        /// The old code did File.Delete(to) then File.Move — if the move then threw (source locked) it left the
        /// destination GONE, the root of the empty/missing session corruption. Returns true on success.</summary>
        private static bool MoveFile(string from, string to)
        {
            if (!File.Exists(from)) return false;
            string tmp = to + ".incoming";
            for (int attempt = 0; attempt < 8; attempt++)   // a just-disposed client may still hold the lock briefly
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    File.Move(from, tmp);                       // locked source → throws here, dest still intact
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(tmp, to);                         // local rename → atomic, fast
                    return true;
                }
                catch { System.Threading.Thread.Sleep(60); }
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            System.Diagnostics.Debug.WriteLine("[ACCT] WARNING: move failed " + from + " → " + to);
            return false;
        }

        /// <summary>True if an account's session exists and is non-empty (a 0-byte session = corrupt → "buffer is null").</summary>
        public static bool SessionLooksValid(long id)
        {
            try { string s = Path.Combine(AccountContext.AccountDir(id), "session"); return File.Exists(s) && new FileInfo(s).Length > 0; }
            catch { return false; }
        }

        /// <summary>Deletes accounts/{id}/ (the unreadable session + its meta/phone/updates) but KEEPS Cache/{id}/
        /// so a same-account re-login reuses its media. Bounded async retry; never blocks/loops.</summary>
        public static async System.Threading.Tasks.Task DeleteAccountDirAsync(long id)
        {
            await DeleteDirAsync(AccountContext.AccountDir(id), "accounts/" + id);
            System.Diagnostics.Debug.WriteLine("[SESSION] deleted corrupt account dir id=" + id + " (cache kept)");
        }

        /// <summary>Deletes the flat legacy session files (a corrupt pre-multi-account install).</summary>
        public static void DeleteLegacySession()
        {
            foreach (var p in new[] { AccountContext.LegacySession, AccountContext.LegacyPhone, AccountContext.LegacyUpdates })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            System.Diagnostics.Debug.WriteLine("[SESSION] deleted corrupt legacy session");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────
        //  ACCOUNT-RECOVERY-SAFETY — a corrupt session is NEVER auto-deleted. It is MOVED ASIDE (renamed) so the
        //  login stays recoverable; and a clean-retry first waits for the file to actually unlock (the two-clients
        //  race often leaves it briefly contended, not truly corrupt).
        // ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Waits (bounded) until accounts/{id}/session can be opened EXCLUSIVELY — i.e. no other handle holds it —
        /// so a clean-retry / cold reconnect never opens a session a just-disposed warm client is still releasing (the
        /// two-clients race). READ-ONLY probe: it opens+closes the file to test the lock, never writing. Never loops.</summary>
        public static async System.Threading.Tasks.Task WaitSessionUnlockedAsync(long id, int timeoutMs = 4000)
        {
            string s = Path.Combine(AccountContext.AccountDir(id), "session");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (!File.Exists(s)) return;                                                  // nothing to wait on
                    using (new FileStream(s, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // exclusive open OK → unlocked
                    return;
                }
                catch (IOException) { await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false); }   // still locked → wait + retry
                catch { return; }                                                                 // perms/other → don't block recovery
            }
            System.Diagnostics.Debug.WriteLine("[SESSION] WaitSessionUnlocked timed out id=" + id + " (proceeding)");
        }

        /// <summary>ACCOUNT-RECOVERY-SAFETY (Bug 2): a genuinely-unreadable session is MOVED ASIDE, NEVER deleted — renames
        /// accounts/{id}/ → accounts/{id}.corrupt-&lt;timestamp&gt;/ so the login is PRESERVED (recoverable / inspectable) and a
        /// fresh re-login of {id} starts clean (the moved dir no longer parses as an id, so it drops out of the switcher).
        /// Returns the new path; null if the move failed — in which case the dir is LEFT IN PLACE (still never deleted).
        /// The caller MUST have released the session handle first (TeardownForSwitchAsync).</summary>
        public static string MoveAccountDirAside(long id)
        {
            try
            {
                string src = AccountContext.AccountDir(id);
                if (!Directory.Exists(src)) return null;
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dst = src + ".corrupt-" + stamp;
                for (int i = 1; Directory.Exists(dst); i++) dst = src + ".corrupt-" + stamp + "_" + i;   // never overwrite an earlier backup
                for (int attempt = 0; attempt < 8; attempt++)                                            // a just-released handle may linger briefly
                {
                    try { Directory.Move(src, dst); System.Diagnostics.Debug.WriteLine("[SESSION] corrupt account dir MOVED ASIDE (not deleted): " + dst); return dst; }
                    catch { System.Threading.Thread.Sleep(60); }
                }
                System.Diagnostics.Debug.WriteLine("[SESSION] WARNING: could not move corrupt account dir aside id=" + id + " → LEFT IN PLACE (still NOT deleted)");
                return null;
            }
            catch { return null; }
        }

        /// <summary>Moves the flat legacy session files aside (…&lt;name&gt;.corrupt-&lt;ts&gt;) instead of deleting — a corrupt
        /// pre-multi-account install stays recoverable. Returns a moved path (best-effort), or null.</summary>
        public static string MoveLegacySessionAside()
        {
            string stamp = ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string moved = null;
            foreach (var p in new[] { AccountContext.LegacySession, AccountContext.LegacyPhone, AccountContext.LegacyUpdates })
            {
                try { if (File.Exists(p)) { string d = p + stamp; if (!File.Exists(d)) { File.Move(p, d); moved = d; } } } catch { }
            }
            if (moved != null) System.Diagnostics.Debug.WriteLine("[SESSION] legacy session MOVED ASIDE (not deleted): " + moved);
            return moved;
        }
    }
}
