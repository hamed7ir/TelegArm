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

        /// <summary>
        /// Deletes files in <paramref name="folder"/> last modified more than
        /// <paramref name="days"/> days ago (days = 0 clears everything). Returns bytes freed.
        /// Silent and resilient — never throws.
        /// </summary>
        public static long DeleteOlderThan(string folder, int days)
        {
            long freed = 0;
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
                var cutoff = DateTime.Now.AddDays(-Math.Max(0, days));
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.LastWriteTime < cutoff)
                        {
                            long len = fi.Length;
                            fi.Delete();
                            freed += len;
                        }
                    }
                    catch { /* skip locked/in-use files */ }
                }
            }
            catch { /* non-fatal */ }
            // Both cache-deletion paths (Settings "Clear now" + the daily retention job) funnel through here.
            // Today this only removes FILES (dirs persist), but invalidating is harmless + future-proofs against
            // a dir-removing change — and keeps the guarantee the audit (G-5/C-6) asked for at one choke point.
            InvalidateEnsured();
            return freed;
        }
    }
}
