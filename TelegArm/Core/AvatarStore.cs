using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// THE avatar pipeline (AVATAR-PIPELINE): one download-once store behind every avatar surface.
    /// Memory (bounded LRU) → disk (thumbs/avatar_{peer}_{photoId}.jpg, temp→rename) → single-flight
    /// download through a 2-wide background queue where on-demand requests (visible rows, bubbles,
    /// pickers) always run before the backfill sweep. photo_id-keyed: the same photo never re-downloads,
    /// a changed photo fetches once and supersedes the old file. "No photo" is decided from photo_id==0
    /// at dialog parse (never from a download result), so a transient VPN failure can NEVER be
    /// misclassified as "no avatar" — failures stay retryable behind a session backoff.
    /// NOT static: the disk root and peer ids are account-scoped; MainForm resets it on account switch.
    /// </summary>
    public sealed class AvatarStore : IDisposable
    {
        private const int MemCapacity = 256;         // bounded for ARM32 (same as the old LruImageCache)
        private const int DownloadTimeoutMs = 15000; // per-item bound — a black-holed VPN abandons, then backoff
        private const int RetryBackoffMs = 5 * 60 * 1000;   // transient-failure backoff (session-level)
        private const int SummaryEvery = 25;         // "[AVATAR] backfill …" cadence

        private sealed class Job
        {
            public long PeerId, PhotoId;
            public IPeerInfo Peer;
            public bool Backfill;
        }

        private readonly object _lock = new object();
        private readonly LruImageCache _mem = new LruImageCache(MemCapacity);
        private readonly HashSet<long> _noPhoto = new HashSet<long>();               // photo_id==0 peers (re-evaluated when a photo appears)
        private readonly Dictionary<long, DateTime> _retryAfter = new Dictionary<long, DateTime>();
        private readonly HashSet<long> _queuedOrInFlight = new HashSet<long>();
        private readonly LinkedList<Job> _demand = new LinkedList<Job>();            // FIFO, always before backfill
        private readonly LinkedList<Job> _backfill = new LinkedList<Job>();          // FIFO, list order = visible-first
        private readonly Dictionary<long, List<TaskCompletionSource<Image>>> _waiters
            = new Dictionary<long, List<TaskCompletionSource<Image>>>();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private readonly Func<IPeerInfo, Task<byte[]>> _download;
        private volatile bool _disposed;
        private int _generation;                                                     // bumped on Reset → in-flight results are dropped
        private int _queuedTotal, _doneDisk, _doneNet, _failed;                      // backfill summary counters (disk-warm vs fetched)

        /// <summary>A peer's avatar landed in memory (raised on a WORKER thread — subscribers marshal).</summary>
        public event Action<long> AvatarLoaded;

        /// <summary>The app's ONE store instance (MainForm owns it; account switches Reset() the same
        /// instance) — ambient access for secondary forms (member lists) without per-site plumbing.</summary>
        public static AvatarStore Current { get; private set; }

        /// <summary>MULTI-ACCOUNT: point the ambient .Current at the ACTIVE account's store (called on activate/switch).
        /// NOT auto-set in the ctor any more, so per-account (warm) stores can't clobber which one secondary forms read.</summary>
        public static void SetActive(AvatarStore store) { Current = store; }

        public AvatarStore(Func<IPeerInfo, Task<byte[]>> download)
        {
            _download = download;
            for (int i = 0; i < 2; i++)                                              // bounded concurrency (2)
            {
                var ignore = Task.Run(WorkerLoop);
            }
        }

        /// <summary>The peer's photo_id as known at dialog parse (0 = no photo / unresolved).</summary>
        public static long PhotoIdOf(IPeerInfo peer)
        {
            if (peer is User u) return u.photo != null ? u.photo.photo_id : 0;
            if (peer is Chat c) return c.photo != null ? c.photo.photo_id : 0;
            if (peer is Channel ch) return ch.photo != null ? ch.photo.photo_id : 0;
            return 0;
        }

        /// <summary>Number of avatars currently in memory (the [PERF] gauge).</summary>
        public int MemCount { get { lock (_lock) return _mem.Count; } }

        /// <summary>Memory-only lookup (sync, render-hot). Null = not loaded (yet).</summary>
        public Image GetCached(long peerId)
        {
            lock (_lock)
            {
                Image img;
                if (_mem.TryGetValue(peerId, out img))
                {
                    if (HotLog) System.Diagnostics.Debug.WriteLine("[AVATAR] hit mem peer=" + peerId);
                    return img;
                }
                return null;
            }
        }

        /// <summary>On-demand load: memory fast-path, else queue a disk-probe/download AHEAD of the backfill.
        /// The result arrives via <see cref="AvatarLoaded"/> (rows/bubbles repaint from there).</summary>
        public void Request(long peerId, IPeerInfo peer)
        {
            Enqueue(peerId, peer, backfill: false);
        }

        /// <summary>Awaitable variant for the pickers (same signature the dialogs already take). Completes
        /// with null on no-photo/failure — callers keep the letter circle.</summary>
        public Task<Image> GetAsync(long peerId, IPeerInfo peer)
        {
            var img = GetCached(peerId);
            if (img != null) return Task.FromResult(img);
            var tcs = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                List<TaskCompletionSource<Image>> list;
                if (!_waiters.TryGetValue(peerId, out list)) _waiters[peerId] = list = new List<TaskCompletionSource<Image>>();
                list.Add(tcs);
            }
            if (!Enqueue(peerId, peer, backfill: false))
            {
                // No job started. If one is ALREADY in flight its completion will complete the waiter;
                // otherwise (no photo / backoff / mem landed meanwhile) complete now so the await can't dangle.
                bool inFlight;
                lock (_lock) inFlight = _queuedOrInFlight.Contains(peerId);
                if (!inFlight) CompleteWaiters(peerId, GetCached(peerId));
            }
            return tcs.Task;
        }

        /// <summary>Backfill (the older-chats fix): queue every dialog peer whose (peerId, photoId) isn't on
        /// disk, in the given order (callers pass the chat-list order → visible rows first, then top-to-bottom).
        /// Cheap to re-run — already-covered peers are filtered here, so retry-next-launch is free.</summary>
        public void EnqueueBackfill(IEnumerable<KeyValuePair<long, IPeerInfo>> peersInListOrder)
        {
            int added = 0;
            foreach (var kv in peersInListOrder)
            {
                long photoId = PhotoIdOf(kv.Value);
                if (photoId == 0) { lock (_lock) _noPhoto.Add(kv.Key); continue; }
                if (DiskFileExists(kv.Key, photoId)) continue;
                if (Enqueue(kv.Key, kv.Value, backfill: true)) added++;
            }
            if (added > 0 && Helpers.Logger.Enabled)
                System.Diagnostics.Debug.WriteLine("[AVATAR] backfill queued=" + added);
        }

        /// <summary>Account switch: drop everything (ids and the disk root are account-scoped).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _generation++;
                foreach (var img in _mem.Values) { try { img.Dispose(); } catch { } }
                _mem.Clear();
                _noPhoto.Clear();
                _retryAfter.Clear();
                _queuedOrInFlight.Clear();
                _demand.Clear();
                _backfill.Clear();
                foreach (var kv in _waiters) foreach (var w in kv.Value) w.TrySetResult(null);
                _waiters.Clear();
                _queuedTotal = 0; _doneDisk = 0; _doneNet = 0; _failed = 0;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Reset();
            _wake.Release(2);   // wake both workers so they observe _disposed and exit
        }

        // ── internals ─────────────────────────────────────────────────────

        private static bool HotLog
        {
            get { return Helpers.Logger.Enabled && AppSettings.Instance.KbdDiag; }   // per-render hot tier
        }

        private bool Enqueue(long peerId, IPeerInfo peer, bool backfill)
        {
            long photoId = PhotoIdOf(peer);
            lock (_lock)
            {
                if (_disposed) return false;
                if (photoId != 0) _noPhoto.Remove(peerId);            // a photo appeared → re-evaluate (1.3)
                else { _noPhoto.Add(peerId); return false; }          // no photo → letter circle, no fetch
                Image dummy;
                if (_mem.TryGetValue(peerId, out dummy)) return false;
                if (_queuedOrInFlight.Contains(peerId)) return false; // single-flight per peer
                DateTime until;
                if (_retryAfter.TryGetValue(peerId, out until) && DateTime.UtcNow < until) return false;
                _queuedOrInFlight.Add(peerId);
                var job = new Job { PeerId = peerId, PhotoId = photoId, Peer = peer, Backfill = backfill };
                if (backfill) _backfill.AddLast(job); else _demand.AddLast(job);
                _queuedTotal++;
            }
            _wake.Release();
            return true;
        }

        private async Task WorkerLoop()
        {
            while (true)
            {
                try { await _wake.WaitAsync().ConfigureAwait(false); } catch { return; }
                if (_disposed) return;
                Job job = null;
                lock (_lock)
                {
                    if (_demand.Count > 0) { job = _demand.First.Value; _demand.RemoveFirst(); }
                    else if (_backfill.Count > 0) { job = _backfill.First.Value; _backfill.RemoveFirst(); }
                }
                if (job == null) continue;
                try { await FetchOne(job).ConfigureAwait(false); }
                catch (Exception ex)   // never let a worker die
                {
                    lock (_lock) { _queuedOrInFlight.Remove(job.PeerId); _failed++; }
                    CompleteWaiters(job.PeerId, null);
                    if (Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[AVATAR] fail peer=" + job.PeerId + " ex=" + ex.Message);
                }
            }
        }

        private async Task FetchOne(Job job)
        {
            int gen;
            lock (_lock) gen = _generation;

            // 1) Disk (keyed file, then the legacy peer-only file — honored as current + renamed on confirm).
            byte[] bytes = TryReadDisk(job.PeerId, job.PhotoId);
            bool fromDisk = bytes != null;

            // 2) Download (bounded; failures are RETRYABLE — never a permanent "no avatar").
            if (bytes == null)
            {
                if (Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[AVATAR] fetch peer=" + job.PeerId + " photo=" + job.PhotoId);
                var t = _download(job.Peer);
                var done = await Task.WhenAny(t, Task.Delay(DownloadTimeoutMs)).ConfigureAwait(false);
                if (done != t) { Fail(job, gen, "timeout"); SwallowFault(t); return; }
                bytes = await t.ConfigureAwait(false);
                // The service returns null on ANY exception and empty when nothing was written — with the
                // photo_id!=0 pre-gate both mean TRANSIENT here (the peer does have a photo).
                if (bytes == null || bytes.Length == 0) { Fail(job, gen, bytes == null ? "download-error" : "empty"); return; }
                WriteDiskAtomic(job.PeerId, job.PhotoId, bytes);
            }

            var bmp = DecodeAvatar(bytes);
            if (bmp == null)
            {
                TryDelete(DiskPath(job.PeerId, job.PhotoId));   // corrupt file must not block a refetch
                Fail(job, gen, "decode");
                return;
            }

            lock (_lock)
            {
                if (gen != _generation || _disposed) { try { bmp.Dispose(); } catch { } return; }
                _mem[job.PeerId] = bmp;
                _retryAfter.Remove(job.PeerId);
                _queuedOrInFlight.Remove(job.PeerId);
                // A3 (log truthfulness): disk-warm passes are not "work" — say which is which.
                if (fromDisk) _doneDisk++; else _doneNet++;
                if (Helpers.Logger.Enabled && ((_doneDisk + _doneNet) % SummaryEvery) == 0)
                    System.Diagnostics.Debug.WriteLine("[AVATAR] backfill queued=" + _queuedTotal
                        + " done disk=" + _doneDisk + " net=" + _doneNet + " failed=" + _failed);
            }
            if (HotLog && fromDisk) System.Diagnostics.Debug.WriteLine("[AVATAR] hit disk peer=" + job.PeerId);
            CompleteWaiters(job.PeerId, bmp);
            var h = AvatarLoaded; if (h != null) { try { h(job.PeerId); } catch { } }
        }

        private void Fail(Job job, int gen, string why)
        {
            lock (_lock)
            {
                if (gen == _generation)
                {
                    _retryAfter[job.PeerId] = DateTime.UtcNow.AddMilliseconds(RetryBackoffMs);
                    _failed++;
                }
                _queuedOrInFlight.Remove(job.PeerId);
            }
            if (Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[AVATAR] fail peer=" + job.PeerId + " ex=" + why);
            CompleteWaiters(job.PeerId, null);
        }

        private void CompleteWaiters(long peerId, Image result)
        {
            List<TaskCompletionSource<Image>> list = null;
            lock (_lock)
            {
                if (_waiters.TryGetValue(peerId, out list)) _waiters.Remove(peerId);
            }
            if (list != null) foreach (var w in list) w.TrySetResult(result);
        }

        // ── disk layer ────────────────────────────────────────────────────

        private static string DiskPath(long peerId, long photoId)
        {
            return MediaCache.ThumbPath("avatar_" + peerId + "_" + photoId + ".jpg");
        }

        private static string LegacyPath(long peerId)
        {
            return MediaCache.ThumbPath("avatar_" + peerId + ".jpg");
        }

        private static bool DiskFileExists(long peerId, long photoId)
        {
            try
            {
                var fi = new FileInfo(DiskPath(peerId, photoId));
                if (fi.Exists && fi.Length > 0) return true;
                var legacy = new FileInfo(LegacyPath(peerId));
                return legacy.Exists && legacy.Length > 0;   // honored as current until a differing photo_id confirms otherwise
            }
            catch { return false; }
        }

        /// <summary>Keyed file first; else the LEGACY avatar_{peer}.jpg is honored as current and RENAMED to
        /// the keyed name (rename-on-confirm — one-time, no refetch). Zero-byte files are deleted on sight.</summary>
        private static byte[] TryReadDisk(long peerId, long photoId)
        {
            try
            {
                string keyed = DiskPath(peerId, photoId);
                var fi = new FileInfo(keyed);
                if (fi.Exists)
                {
                    if (fi.Length > 0) return File.ReadAllBytes(keyed);
                    TryDelete(keyed);                                  // zero-byte = invalid → clears the way for a refetch
                }
                string legacy = LegacyPath(peerId);
                var lf = new FileInfo(legacy);
                if (lf.Exists)
                {
                    if (lf.Length == 0) { TryDelete(legacy); return null; }
                    var bytes = File.ReadAllBytes(legacy);
                    try { File.Move(legacy, keyed); } catch { }        // rename-on-confirm (best-effort)
                    return bytes;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Temp-then-rename write; supersedes any other avatar file for this peer (old photo_id
        /// versions + the legacy name).</summary>
        private static void WriteDiskAtomic(long peerId, long photoId, byte[] bytes)
        {
            string path = DiskPath(peerId, photoId);
            try
            {
                string tmp = path + ".part";
                File.WriteAllBytes(tmp, bytes);
                TryDelete(path);
                File.Move(tmp, path);
                // Supersede: this peer has exactly ONE current photo — drop old versions + the legacy file.
                string dir = Path.GetDirectoryName(path);
                foreach (var f in Directory.GetFiles(dir, "avatar_" + peerId + "_*.jpg"))
                    if (!string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
                TryDelete(LegacyPath(peerId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AVATAR] disk write failed peer=" + peerId + " (in memory, still renders): " + ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Decodes avatar bytes into a small (≤96px) self-contained bitmap (ARM32-safe).</summary>
        private static Image DecodeAvatar(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms))
                {
                    int side = Math.Min(96, Math.Min(src.Width, src.Height));
                    if (side <= 0) side = 96;
                    var bmp = new Bitmap(side, side);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        int crop = Math.Min(src.Width, src.Height);
                        int sx = (src.Width - crop) / 2, sy = (src.Height - crop) / 2;
                        g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(sx, sy, crop, crop), GraphicsUnit.Pixel);
                    }
                    return bmp;
                }
            }
            catch { return null; }
        }

        private static void SwallowFault(Task t)
        {
            var ignore = t.ContinueWith(x => { var e = x.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
