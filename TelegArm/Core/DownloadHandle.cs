using System;
using System.IO;
using System.Threading.Tasks;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// One document download for the LIFETIME of (doc, destination) — across pauses (DOWNLOAD-RESUME Part 1):
    /// Pause() and Resume() mutate THIS handle (state flip + internal transfer-task swap), so registry entries,
    /// panel rows and bubble rings bind ONCE; "rebinding on resume" no longer exists.
    ///
    /// FRESH downloads run WTelegram's parallel-part helper (faster). RESUME runs a manual sequential
    /// Upload_GetFile loop from the verified offset (the helper hard-codes offset 0 — IL fact), appending to
    /// the kept .part. CRASH-SAFETY: a .part is resumable ONLY with a matching "&lt;part&gt;.meta" sidecar,
    /// written on PAUSE and on graceful FAIL after the stream is flushed/closed; a hard kill leaves no sidecar
    /// → the part is discarded (guards torn tails). The helper writes up to TWO parts in parallel
    /// (client-wide SemaphoreSlim(2), IL fact), so helper-phase watermarks rewind one full 1MB window; the
    /// raw loop is append-only and trusts its own length (128KB alignment).
    /// FILE_REFERENCE → refreshDoc retry-once; FILE_MIGRATE_X → GetClientForDC (public in 4.4.6);
    /// CDN redirect → full helper refetch (no hand-rolled AES-CTR until frequency data says otherwise).
    /// <see cref="Changed"/> fires on progress/state (download thread — UI subscribers must marshal).
    /// <see cref="Completion"/> completes ONLY at a terminal state (Done/Cancelled/Failed) — pause keeps
    /// joiners waiting, so pause→resume→done still releases them correctly.
    /// </summary>
    public sealed class DownloadHandle
    {
        public enum DState { Downloading, Done, Cancelled, Failed, Paused }

        public long DocId { get; private set; }
        public string Path { get; private set; }

        /// <summary>Media class for log readability (audio|video|gif|photo|doc|voice) — DOWNLOAD-UX v3 3.3.</summary>
        internal string TypeTag = "doc";
        private string T { get { return " type=" + TypeTag; } }
        public long Transmitted { get; private set; }
        public long Total { get; private set; }
        public DState State { get; private set; }

        /// <summary>Raised on each progress tick and on every state change. Fired on the download thread.</summary>
        public event Action<DownloadHandle> Changed;

        /// <summary>Completes when the transfer reaches a TERMINAL state (awaitable join for dedup).</summary>
        public Task Completion => _done.Task;
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>();

        private WTelegram.Client _client;
        private Document _doc;
        private Action<DownloadHandle> _onDone;
        private Func<Task<Document>> _refreshDoc;

        private FileStream _fs;
        private readonly object _gate = new object();
        private bool _cancelRequested, _pauseRequested, _finished, _running, _rawPhase;
        // RT trip 2026-10-16 ("pause can't be resumed, can't stop or cancel"): Pause / Cancel used to wait for the transfer in
        // flight to NOTICE a flag - an RPC that never returns (Wi-Fi asleep) froze the row. Now they take effect at once: each
        // attempt carries the generation it started with, Pause / Cancel / Resume move it on, and a stale attempt that wakes
        // up later changes nothing.
        private int _gen;
        private string _cancelReason = "user";
        private long _lastLogPct = -1;
        private DateTime _lastLogAt;

        private const int RawLimit = 128 * 1024;          // offset%4096==0, limit%4096==0, 1MB%limit==0 → precise not needed
        private const long HelperWindow = 1024 * 1024;    // parallel(2) helper phase: rewind one full window on pause/fail
        private const string SidecarExt = ".meta";

        internal DownloadHandle(long docId, string path)
        {
            DocId = docId; Path = path; State = DState.Downloading;
        }

        /// <summary>0..1 download fraction (0 until the total size is known).</summary>
        public float Fraction { get { long t = Total; return t > 0 ? Math.Min(1f, (float)Transmitted / t) : 0f; } }

        private static bool LogOn => TelegArm.Helpers.Logger.Enabled;

        /// <summary>First start (fire-and-forget); <paramref name="onDone"/> fires once at a TERMINAL state.</summary>
        internal void Run(WTelegram.Client client, Document doc, Action<DownloadHandle> onDone, Func<Task<Document>> refreshDoc = null)
        {
            int gen;
            lock (_gate)
            {
                _client = client; _doc = doc; _onDone = onDone; _refreshDoc = refreshDoc;
                if (_finished) return;
                if (_cancelRequested) { TerminalCancelled(); return; }
                if (State == DState.Paused) return;   // paused before its deferred start: Resume starts it
                _running = true;
                gen = ++_gen;
            }
            Attempt(gen);
        }

        private bool Stale(int gen) { lock (_gate) return gen != _gen; }

        /// <summary>Same-handle resume (DOWNLOAD-RESUME Part 1). Idempotent: no-op unless currently Paused.</summary>
        public void Resume()
        {
            int gen;
            bool start;
            lock (_gate)
            {
                if (_finished || State != DState.Paused) return;   // downloading/terminal → no-op
                _pauseRequested = false;
                State = DState.Downloading;
                start = _client != null;   // a deferred start not run yet: it runs by itself
                _running = start;
                gen = ++_gen;               // an older attempt still awaiting the network is stale now
            }
            Log("resume id=" + DocId + T + " got=" + Transmitted + "/" + Total);
            RaiseChanged();
            if (start) Attempt(gen);
        }

        /// <summary>Aborts the transfer; the .part is kept but the sidecar is DELETED (a cancel is final).</summary>
        public void Cancel(string reason = "user")
        {
            lock (_gate)
            {
                if (_finished) return;
                if (!_cancelRequested) _cancelReason = reason;
                _cancelRequested = true;
                try { _fs?.Dispose(); } catch { }   // aborts an in-flight write
                _fs = null;
                _gen++;                             // whatever is in flight is stale: the cancel is final NOW
                _running = false;
            }
            TerminalCancelled();
        }

        private void TerminalCancelled()
        {
            State = DState.Cancelled;
            TryDelete(Path + ".part" + SidecarExt);
            Log("CANCELLED id=" + DocId + T + " got=" + Transmitted + "/" + Total + " reason=" + _cancelReason);
            TerminalFinish();
        }

        /// <summary>PAUSE: aborts the in-flight transfer, keeps the .part, writes the resume sidecar; the
        /// handle stays alive in the Paused state and Resume() continues from the verified offset.</summary>
        public void Pause()
        {
            lock (_gate)
            {
                if (_finished || _cancelRequested || State != DState.Downloading) return;
                _pauseRequested = true;
                try { _fs?.Dispose(); } catch { }   // the .part is closed: its length is final for the sidecar
                _fs = null;
                _gen++;                             // the attempt in flight no longer decides anything
                _running = false;
                State = DState.Paused;
            }
            WriteSidecar(Path + ".part", _doc != null ? _doc.size : 0);
            Log("paused id=" + DocId + T + " got=" + Transmitted + "/" + Total + " (part kept)");
            RaiseChanged();   // NON-terminal: no onDone, Completion stays pending for resume→done
        }

        private static void Log(string s) { if (LogOn) TelegArm.Helpers.Logger.Diag("[DOWNLOAD] " + s); }   // R7: survives Release

        // ── the transfer attempt (fresh helper OR raw resume loop) ─────────

        private async void Attempt(int gen)
        {
            string part = Path + ".part";
            long expected = _doc != null ? _doc.size : 0;
            try
            {
                if (_cancelRequested || Stale(gen)) throw new OperationCanceledException();
                if (TelegramService.IsFileComplete(Path, expected))
                {
                    Log("already complete (verified) id=" + DocId);
                    lock (_gate) { if (gen != _gen) return; }
                    State = DState.Done;
                    TryDelete(part + SidecarExt);
                    TerminalFinish();
                    return;
                }
                if (File.Exists(Path))   // heal a stranded/mismatched file at the FINAL path
                {
                    long onDisk = new FileInfo(Path).Length;
                    if (expected > 0 && onDisk != expected)
                    {
                        Log("heal id=" + DocId + " onDisk=" + onDisk + " expected=" + expected + " → re-download");
                        File.Delete(Path);
                    }
                }

                long from = SidecarResumePoint(part, expected);
                if (from > 0)
                {
                    Log("resume id=" + DocId + T + " from=" + from + "/" + expected + " via=raw-loop");
                    Transmitted = from; Total = expected;
                    try
                    {
                        await RawResumeTransfer(part, from, gen).ConfigureAwait(false);
                    }
                    catch (CdnFallbackException)
                    {
                        // We don't hand-roll CDN AES-CTR (yet — frequency data first): full helper refetch.
                        Log("cdn → full refetch id=" + DocId);
                        TryDelete(part + SidecarExt); TryDelete(part);
                        await HelperTransferWithRefresh(part, gen).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (File.Exists(part))
                    {
                        Log("part discarded id=" + DocId + " (no resume sidecar)");
                        File.Delete(part);
                    }
                    Log("start id=" + DocId + T + " size=" + expected + " path=" + Path);
                    await HelperTransferWithRefresh(part, gen).ConfigureAwait(false);
                }
                lock (_gate) { if (gen != _gen) return; }   // paused / cancelled / resumed meanwhile: not this attempt's file any more

                // VERIFIED completion only → rename to final.
                long written = File.Exists(part) ? new FileInfo(part).Length : -1;
                bool match = expected <= 0 ? written > 0 : written == expected;
                if (!match) throw new IOException("incomplete transfer: wrote " + written + " of " + expected);
                try { if (File.Exists(Path)) File.Delete(Path); } catch { }
                File.Move(part, Path);
                TryDelete(part + SidecarExt);
                Log("done id=" + DocId + T + " wrote=" + written + " expected=" + expected + " match=Y → renamed");
                State = DState.Done;
                TerminalFinish();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (gen != _gen)   // Pause / Cancel / Resume already decided: this attempt only ends
                    {
                        Log("stale attempt ended id=" + DocId + " (" + ex.GetType().Name + ")");
                        return;
                    }
                    try { _fs?.Dispose(); } catch { }
                    _fs = null;
                }
                State = _cancelRequested ? DState.Cancelled : DState.Failed;
                if (State == DState.Failed) WriteSidecar(part, expected);   // graceful fail → resumable next request
                else TryDelete(part + SidecarExt);
                if (State == DState.Cancelled)
                    Log("CANCELLED id=" + DocId + T + " got=" + Transmitted + "/" + Total + " reason=" + _cancelReason);
                else
                    Log("FAILED id=" + DocId + T + " got=" + Transmitted + "/" + Total + " ex=" + ex.GetType().Name + ":" + ex.Message + " part=kept");
                TerminalFinish();
            }
        }

        private void TerminalFinish()
        {
            lock (_gate) { _finished = true; _running = false; }
            var od = _onDone; if (od != null) { try { od(this); } catch { } }
            RaiseChanged();
            try { _done.TrySetResult(true); } catch { }
        }

        private void RaiseChanged() { var h = Changed; if (h != null) { try { h(this); } catch { } } }

        /// <summary>Fresh download via the parallel-part helper, with the FILE_REFERENCE retry-once ladder.</summary>
        private async Task HelperTransferWithRefresh(string part, int gen)
        {
            var docNow = _doc; bool refreshed = false;
            _rawPhase = false;
            for (; ; )
            {
                try { await HelperTransfer(docNow, part, gen).ConfigureAwait(false); return; }
                catch (RpcException rex) when (!refreshed && _refreshDoc != null
                                               && rex.Message != null && rex.Message.Contains("FILE_REFERENCE"))
                {
                    refreshed = true;
                    Log("FILE_REFERENCE expired id=" + DocId + " → refreshing + retry once");
                    var fresh = await _refreshDoc().ConfigureAwait(false);
                    if (fresh == null) throw;
                    docNow = fresh; _doc = fresh;
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                }
            }
        }

        private async Task HelperTransfer(Document doc, string part, int gen)
        {
            FileStream fs = File.Create(part);
            lock (_gate)
            {
                if (gen != _gen || _cancelRequested || _pauseRequested) { try { fs.Dispose(); } catch { } throw new OperationCanceledException(); }
                _fs = fs;
            }
            await _client.DownloadFileAsync(doc, fs, (PhotoSizeBase)null, (t, tot) =>
            {
                if (Stale(gen) || _cancelRequested || _pauseRequested) throw new OperationCanceledException();   // a stale part never moves the bar
                Transmitted = t; Total = tot;
                ThrottledProgressLog(t, tot);
                RaiseChanged();
            }).ConfigureAwait(false);
            lock (_gate) { if (_fs == fs) _fs = null; }
            fs.Dispose();
            if (Stale(gen) || _cancelRequested || _pauseRequested) throw new OperationCanceledException();
        }

        /// <summary>RESUME path: sequential Upload_GetFile loop appending from <paramref name="from"/> —
        /// slower than the helper's parallel fresh download, but it never repays already-downloaded bytes.</summary>
        private async Task RawResumeTransfer(string part, long from, int gen)
        {
            _rawPhase = true;
            var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None);
            fs.SetLength(from);                      // truncate DOWN to the verified boundary
            fs.Position = from;
            lock (_gate)
            {
                if (gen != _gen || _cancelRequested || _pauseRequested) { try { fs.Dispose(); } catch { } throw new OperationCanceledException(); }
                _fs = fs;
            }
            var client = _client;
            var docNow = _doc; bool refreshed = false;
            long offset = from, expected = _doc.size;
            try
            {
                while (offset < expected)
                {
                    if (Stale(gen) || _cancelRequested || _pauseRequested) throw new OperationCanceledException();
                    Upload_FileBase res;
                    try
                    {
                        res = await client.Upload_GetFile(MakeLocation(docNow), offset, RawLimit).ConfigureAwait(false);
                    }
                    catch (RpcException rex) when (rex.Message != null && rex.Message.Contains("FILE_REFERENCE") && !refreshed && _refreshDoc != null)
                    {
                        refreshed = true;
                        Log("FILE_REFERENCE expired id=" + DocId + " → refreshing + retry once (raw)");
                        var fresh = await _refreshDoc().ConfigureAwait(false);
                        if (fresh == null) throw;
                        docNow = fresh; _doc = fresh;
                        continue;
                    }
                    catch (RpcException rex) when (rex.Message != null && rex.Message.StartsWith("FILE_MIGRATE_"))
                    {
                        // WTC keeps the literal "_X" in Message; the number rides in RpcException.X (IL-proven).
                        int dc = rex.X;
                        if (dc <= 0) throw;
                        Log("FILE_MIGRATE id=" + DocId + " → dc" + dc + " (raw loop continues there)");
                        client = await _client.GetClientForDC(dc, true).ConfigureAwait(false);
                        if (client == null) throw;
                        continue;   // retry the same offset on the media DC
                    }
                    if (res is Upload_FileCdnRedirect) throw new CdnFallbackException();
                    var uf = res as Upload_File;
                    if (uf == null || uf.bytes == null || uf.bytes.Length == 0)
                        throw new IOException("empty chunk at offset " + offset);
                    if (Stale(gen)) throw new OperationCanceledException();   // paused / cancelled while the chunk was in flight
                    fs.Write(uf.bytes, 0, uf.bytes.Length);
                    offset += uf.bytes.Length;
                    Transmitted = offset; Total = expected;
                    ThrottledProgressLog(offset, expected);
                    RaiseChanged();
                }
                fs.Flush();
            }
            finally
            {
                lock (_gate) { if (_fs == fs) _fs = null; }
                try { fs.Dispose(); } catch { }
            }
        }

        private static InputDocumentFileLocation MakeLocation(Document doc)
        {
            return new InputDocumentFileLocation
            {
                id = doc.id,
                access_hash = doc.access_hash,
                file_reference = doc.file_reference,
                thumb_size = ""
            };
        }

        private void ThrottledProgressLog(long t, long tot)
        {
            if (!LogOn || tot <= 0) return;
            long pct = t * 100 / tot;   // HOT: throttle to every ~10% or 2s
            if (pct >= _lastLogPct + 10 || (DateTime.UtcNow - _lastLogAt).TotalSeconds >= 2)
            {
                _lastLogPct = pct; _lastLogAt = DateTime.UtcNow;
                Log("progress id=" + DocId + " got=" + t + "/" + tot);
            }
        }

        private sealed class CdnFallbackException : Exception { }

        // ── crash-safety sidecar (.part.meta): "docId|expectedSize|flushedLen" ──

        private void WriteSidecar(string part, long expected)
        {
            try
            {
                if (!File.Exists(part)) return;
                long partLen = new FileInfo(part).Length;
                long mark;
                if (_rawPhase)
                    mark = AlignDown(partLen, RawLimit);                       // append-only loop → its own length is the truth
                else
                {
                    // Helper phase: up to TWO parts in flight (SemaphoreSlim(2), IL) — the tail may hold an
                    // out-of-order window. Rewind one full window below the smaller of disk/transmitted.
                    long basis = Math.Min(partLen, Transmitted);
                    mark = Math.Max(0, AlignDown(basis, HelperWindow) - HelperWindow);
                }
                if (mark <= 0) { TryDelete(part + SidecarExt); return; }
                File.WriteAllText(part + SidecarExt, DocId + "|" + expected + "|" + mark);
            }
            catch { /* best-effort — no sidecar just means a clean refetch */ }
        }

        /// <summary>Verified resume offset from the sidecar, or 0 (discard-and-refetch).</summary>
        private long SidecarResumePoint(string part, long expected)
        {
            try
            {
                string meta = part + SidecarExt;
                if (!File.Exists(part) || !File.Exists(meta)) return 0;
                var s = File.ReadAllText(meta).Split('|');
                if (s.Length != 3) return 0;
                long docId, exp, mark;
                if (!long.TryParse(s[0], out docId) || !long.TryParse(s[1], out exp) || !long.TryParse(s[2], out mark)) return 0;
                if (docId != DocId || exp != expected) return 0;               // different doc/size → not ours
                long partLen = new FileInfo(part).Length;
                mark = Math.Min(mark, AlignDown(partLen, RawLimit));
                return (mark > 0 && mark < expected) ? mark : 0;
            }
            catch { return 0; }
        }

        private static long AlignDown(long v, long a) { return v - v % a; }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
