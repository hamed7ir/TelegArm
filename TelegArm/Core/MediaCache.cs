using System;
using System.Collections.Generic;
using System.IO;

namespace TelegArm.Core
{
    /// <summary>Helpers for the on-disk media cache, split into two namespaces under the resolved root:
    /// <c>thumbs/</c> (always, tiny — previews/posters/covers shown on render) and <c>media/</c> (full files,
    /// downloaded ONLY on explicit tap/open). Render never writes to <c>media/</c>.</summary>
    public static class MediaCache
    {
        /// <summary>The thumbnails sub-folder of the PER-ACCOUNT cache root (render previews). Keyed by the
        /// active account id (Cache/{id}/thumbs) so account-scoped object ids can't collide across accounts.</summary>
        public static string ThumbsFolder => Path.Combine(AccountContext.CacheRootFor(AppSettings.Instance.MediaCacheFolder), "thumbs");
        /// <summary>The full-media sub-folder of the per-account cache root (on-demand downloads).</summary>
        public static string MediaFolder => Path.Combine(AccountContext.CacheRootFor(AppSettings.Instance.MediaCacheFolder), "media");

        /// <summary>Absolute path for a thumbnail file (ensures <c>thumbs/</c> exists).</summary>
        public static string ThumbPath(string fileName) { return Path.Combine(EnsureFolder(ThumbsFolder), fileName); }
        /// <summary>The ONE constructor for a document-thumbnail cache path ("thumb_{id}.png") — shared by the
        /// chat renderer and the emoji/sticker picker so the stems can never drift apart.</summary>
        public static string ThumbCachePath(long id) { return ThumbPath("thumb_" + id + ".png"); }
        /// <summary>Absolute path for a full-media file (ensures <c>media/</c> exists).</summary>
        public static string MediaPath(string fileName) { return Path.Combine(EnsureFolder(MediaFolder), fileName); }

        /// <summary>Total bytes under a folder (recursive); 0 if missing. Never throws.</summary>
        public static long FolderSize(string folder)
        {
            long total = 0;
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                        try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }

        /// <summary>
        /// Consistent cache file name per media type:
        /// photo_{id}.jpg, video_{id}.mp4 (video/gif), voice_{id}.ogg, doc_{id}_{filename}.
        /// </summary>
        public static string CacheFileName(string type, long id, string fileName = null)
        {
            switch (type)
            {
                case "photo": return "photo_" + id + ".jpg";
                case "video":
                case "gif": return "video_" + id + ".mp4";
                case "voice": return "voice_" + id + ".ogg";
                default: return "doc_" + id + "_" + Sanitize(fileName ?? "file");
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static readonly HashSet<string> _loggedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Dirs already created THIS process run → skip the Exists+CreateDirectory syscall pair on repeat calls
        // (ThumbPath/MediaPath run these per render). Keyed by FULL PATH, which naturally partitions per account
        // (CacheRootFor embeds the active id) so an account switch never reuses a stale entry. INVALIDATED by
        // InvalidateEnsured() from every path that removes cache directories (DeleteOlderThan + account delete).
        private static readonly HashSet<string> _ensuredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops the created-this-run set so the next <see cref="EnsureFolder"/> re-creates. MUST be
        /// called after anything deletes cache directories, or a subsequent write targets a nonexistent folder.</summary>
        public static void InvalidateEnsured()
        {
            lock (_ensuredDirs) _ensuredDirs.Clear();
        }

        /// <summary>Creates the folder if it doesn't exist (best-effort); returns the path. The SINGLE mkdir
        /// point for the disk cache (thumbs/, media/, per-account). Skips the syscalls when the dir was already
        /// created this run (<see cref="_ensuredDirs"/>). [CACHE]-logs each unique dir once and logs the
        /// EXCEPTION when the mkdir FAILS — so a swallowed account-subfolder-creation failure is finally visible.</summary>
        public static string EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            lock (_ensuredDirs) { if (_ensuredDirs.Contains(path)) return path; }   // fast path: one syscall pair per dir per run
            try
            {
                bool existed = Directory.Exists(path);
                Directory.CreateDirectory(path);
                lock (_ensuredDirs) _ensuredDirs.Add(path);
                lock (_loggedDirs)
                    if (_loggedDirs.Add(path))
                        System.Diagnostics.Debug.WriteLine("[CACHE] dir " + (existed ? "OK(existing) " : "OK(created) ") + path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CACHE] mkdir FAILED " + path + " : " + ex.GetType().FullName + ": " + ex.Message);
            }
            return path;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  BATCH-TA-3 — THE PRUNE PATH IS A DELETE PATH. Two defects were fixed here, and one trap avoided.
        //
        //  D1  Retention 0 used to mean DELETE EVERYTHING, while the Settings label said "0 = keep forever"
        //      (SettingsForm.cs:478) and the stepper's Minimum=0 made it reachable. Anyone who set 0 was
        //      losing their entire cache once a day, silently. 0 now means RETAIN INDEFINITELY, matching
        //      the label. No migration: users who set 0 wanted exactly what they now get.
        //
        //      ⚠ THE TRAP: Settings "Clear now" called DeleteOlderThan(root, 0) and relied on 0 meaning
        //      "everything" — its own comment said so. Simply redefining 0 would have turned that button
        //      into a silent no-op reporting "Cleared 0 bytes". The sentinel was doing two opposite jobs,
        //      so it is gone: retention lives in DeleteOlderThan, "delete the lot" lives in ClearAll.
        //
        //  D2  MediaCacheFolder is free text and the sweep recurses AllDirectories, so a mis-set folder
        //      could reach account session files. TWO independent layers, both required, because either
        //      alone is one mistake away from data loss:
        //        (a) refuse outright when the target IS, CONTAINS, or SITS INSIDE the accounts root;
        //        (b) delete ONLY files whose names match a stem this cache actually writes. A prune that
        //            removes just what it recognises cannot delete "session" or "meta.json" even if (a)
        //            were somehow bypassed.
        //
        //  D3  Every decision is logged via Logger.Diag (R7: Trace, survives Release). A delete path must
        //      never be silent in the shipped build.
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Filename stems this cache actually writes — the complete set, swept from every call site
        /// of <see cref="ThumbPath"/>/<see cref="MediaPath"/>/<see cref="CacheFileName"/>:
        /// thumb_ photo_ video_ voice_ doc_ (MediaCache) · avatar_ (AvatarStore) · sticker_ gif_ (EmojiPicker)
        /// · customemoji_ tgs_ sticker_ (MainForm) · audio_.
        /// ⚠ ADD A STEM HERE WHENEVER A NEW CACHE WRITER IS ADDED, or its files will never be pruned and the
        /// cache will grow without bound. That is the deliberate trade: an unrecognised file is kept, never
        /// deleted, because the cost of keeping junk is disk and the cost of the opposite is a lost session.</summary>
        private static readonly string[] CacheStems =
        {
            "thumb_", "photo_", "video_", "voice_", "doc_", "avatar_",
            "sticker_", "gif_", "tgs_", "customemoji_", "audio_"
        };

        /// <summary>True when a file name matches something this cache created (D2 layer b).</summary>
        public static bool IsCacheFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            foreach (var stem in CacheStems)
                if (fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormDir(string p)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p)) return null;
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            }
            catch { return null; }
        }

        /// <summary>Why <paramref name="folder"/> must NOT be pruned, or null when it is safe (D2 layer a).
        /// Public so Settings can validate the folder the moment it is set, not only when the job runs.
        /// NOTE the comparison is against the ACCOUNTS root, not the whole data root — the default cache
        /// folder legitimately sits beside accounts\ under the same base, and refusing that would disable
        /// pruning for every default install.</summary>
        public static string PruneRefusalReason(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "no cache folder is configured";
            string f = NormDir(folder);
            if (f == null) return "the configured path is not a usable directory path";

            string accounts = NormDir(AccountContext.AccountsRoot);
            if (accounts != null)
            {
                if (string.Equals(f, accounts, StringComparison.OrdinalIgnoreCase))
                    return "it IS the accounts root — session files live there";
                if (accounts.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                    return "it CONTAINS the accounts root (" + AccountContext.AccountsRoot + ") — a recursive sweep would reach session files";
                if (f.StartsWith(accounts, StringComparison.OrdinalIgnoreCase))
                    return "it sits INSIDE the accounts root — session files live there";
            }
            if (!Directory.Exists(folder)) return "the folder does not exist";
            return null;
        }

        /// <summary>Deletes cache files older than <paramref name="days"/> days. Returns bytes freed.
        /// <paramref name="days"/> &lt;= 0 means RETAIN INDEFINITELY and deletes nothing (D1 — this used to
        /// mean "delete everything", which contradicted the Settings label). Never throws.</summary>
        public static long DeleteOlderThan(string folder, int days)
        {
            if (days <= 0)
            {
                Log("[CACHE-PRUNE] SKIPPED retention=" + days + " (0 = keep forever) folder=\"" + (folder ?? "") + "\" — nothing deleted");
                return 0;
            }
            return PruneCore(folder, DateTime.Now.AddDays(-days), "retention=" + days + "d");
        }

        /// <summary>Explicit user action ("Clear now"): deletes ALL recognised cache files regardless of age.
        /// Separate from <see cref="DeleteOlderThan"/> because the two want opposite things from the same
        /// argument — see the D1 note above. Still subject to BOTH D2 safety layers.</summary>
        public static long ClearAll(string folder)
        {
            return PruneCore(folder, DateTime.MaxValue, "clear-all (explicit user action)");
        }

        private static void Log(string line)
        {
            if (TelegArm.Helpers.Logger.Enabled) TelegArm.Helpers.Logger.Diag(line);
        }

        private static long PruneCore(string folder, DateTime cutoff, string what)
        {
            string refusal = PruneRefusalReason(folder);
            if (refusal != null)
            {
                Log("[CACHE-PRUNE] REFUSED " + what + " folder=\"" + (folder ?? "") + "\" reason=" + refusal);
                return 0;
            }

            long freed = 0;
            int seen = 0, recognised = 0, deleted = 0, skippedUnknown = 0, failed = 0;
            try
            {
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    seen++;
                    // D2 layer (b): only ever touch files this cache created. A session file, meta.json, or
                    // anything a user parked in the folder is not recognised and is therefore untouchable.
                    if (!IsCacheFileName(Path.GetFileName(file))) { skippedUnknown++; continue; }
                    recognised++;
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTime < cutoff)
                        {
                            long len = fi.Length;
                            fi.Delete();
                            freed += len;
                            deleted++;
                        }
                    }
                    catch { failed++; /* locked/in-use — skip, never fatal */ }
                }
            }
            catch (Exception ex)
            {
                Log("[CACHE-PRUNE] ERROR " + what + " folder=\"" + folder + "\" — " + ex.Message);
            }

            Log("[CACHE-PRUNE] " + what + " folder=\"" + folder + "\" files=" + seen
                + " recognised=" + recognised + " deleted=" + deleted
                + " keptUnrecognised=" + skippedUnknown + " lockedSkipped=" + failed
                + " freed=" + freed + "B");

            // Both deletion paths funnel through here. Today this only removes FILES (dirs persist), but
            // invalidating is harmless + future-proofs against a dir-removing change — and keeps the
            // guarantee the audit (G-5/C-6) asked for at one choke point.
            InvalidateEnsured();
            return freed;
        }
    }
}
