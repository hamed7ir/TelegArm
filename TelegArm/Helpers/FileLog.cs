using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Tees every <see cref="Debug"/>/<see cref="Trace"/> trace to PER-SESSION log files, so RT ARM32 — where
    /// DebugView has no build — can be diagnosed by reading text files. One <see cref="TraceListener"/> on
    /// <see cref="Trace.Listeners"/> captures all tagged Debug.WriteLine traces with no call-site changes.
    ///
    /// PER-SESSION MODEL (LOG-TOGGLE+CRASH amendment 2):
    ///  • Files live in &lt;Documents&gt;\TelegArm\logs\telegarm_YYYYMMDD_HHMMSS.log — one file per session,
    ///    NEVER truncated or overwritten; a relaunch cannot destroy a previous session's evidence.
    ///  • Logging OFF at launch = ZERO files, zero I/O — the startup header is held in memory and written
    ///    first if logging is enabled later (the session file is then created with the TOGGLE time).
    ///  • OFF mid-session: stamp, flush, CLOSE (handle released). Back ON in the same run: append to the SAME
    ///    session file with a re-enabled stamp.
    ///  • SIZE BUDGET: 10 MB per part (cheap byte counter, no per-write stat) → "→ continued in part N+1",
    ///    then telegarm_&lt;ts&gt;_partN.log with a continuation header; max 3 parts per session (~30 MB). Budget
    ///    exhausted → final stamp, logging hard-off for the rest of the run (the toggle can't reset it).
    ///  • AUTO-CLEANUP (startup, background, never blocks): delete session logs older than 2 days but ALWAYS
    ///    keep the newest 3 sessions (base+parts aged as one unit). crash.log (app dir root) is never touched.
    ///
    /// SAFE — all I/O in try/catch; NON-BLOCKING — emit only enqueues, a background thread writes.
    /// UTF-8 with BOM (RT Notepad reads →/—/Persian cleanly).
    /// </summary>
    public static class FileLog
    {
        private const long MaxBytesPerPart = 10 * 1024 * 1024;   // 10 MB per part (approx — counter, not stat)
        private const int MaxParts = 3;                          // ~30 MB worst case per session
        private const int KeepSessions = 3;                      // newest sessions always kept by cleanup
        private const int KeepDays = 2;                          // older sessions beyond the newest 3 are deleted

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private static readonly object _gate = new object();   // serializes Init/SetEnabled
        private static Thread _worker;
        private static StreamWriter _writer;
        private static string _logsDir;          // <appDir>\logs (not created until first enable)
        private static string _sessionBase;      // "telegarm_YYYYMMDD_HHMMSS" — set ONCE per run, on first enable
        private static int _part = 1;            // current part number (1-based)
        private static long _partBytes;          // approx bytes written to the current part
        private static string _pendingHeader;    // launch header held in memory while logging is off (3.5.3)
        private static volatile bool _sinkOpen;          // accepting lines
        private static volatile bool _closeAfterDrain;   // worker: flush + close after the next drain
        private static volatile bool _budgetExhausted;   // session budget spent — logging off until next launch
        private static volatile bool _stopping;
        private static bool _listenerAdded, _exitHooked;
        private static int _pending;

        /// <summary>The logs folder path (resolved, NOT created). Null if the app dir can't resolve.</summary>
        public static string LogsDirectory
        {
            get
            {
                try { return Path.Combine(StoragePaths.ResolveAppDir(), "logs"); }
                catch { return null; }
            }
        }

        /// <summary>Startup init. Enabled → open this session's file and start flowing; disabled → hold the
        /// header in memory (zero files, zero I/O). Always schedules the background log cleanup.</summary>
        public static void Init(bool enabledAtLaunch)
        {
            lock (_gate)
            {
                try
                {
                    _logsDir = LogsDirectory;
                    _pendingHeader = Header();
                    if (!_exitHooked) { try { AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown(); _exitHooked = true; } catch { } }

                    if (enabledAtLaunch && _logsDir != null)
                    {
                        StartSession(DateTime.Now);
                        Logger.Enabled = true; PerfLog.Enabled = true;
                    }
                    else
                    {
                        Logger.Enabled = false; PerfLog.Enabled = false;
                    }
                }
                catch { Logger.Enabled = false; PerfLog.Enabled = false; }

                // Deferred, guarded — never blocks or breaks launch (locked/missing files are skipped).
                try { System.Threading.Tasks.Task.Run((Action)CleanupOldSessions); } catch { }
            }
        }

        /// <summary>Live toggle from Settings — effective immediately, no restart. Once the session's size
        /// budget is exhausted, flipping the toggle resets nothing until the next launch (by design).</summary>
        public static void SetEnabled(bool on)
        {
            lock (_gate)
            {
                try
                {
                    if (_budgetExhausted) return;   // spent — the toggle may show ON but nothing restarts this run
                    if (on == _sinkOpen && on == Logger.Enabled) return;

                    if (on)
                    {
                        if (_logsDir == null) _logsDir = LogsDirectory;
                        if (_logsDir == null) return;
                        bool firstEnable = _sessionBase == null;
                        if (firstEnable) StartSession(DateTime.Now);   // session file created NOW (toggle time)
                        else { OpenSink(); Enqueue("========== diagnostic logging re-enabled by user =========="); }
                        Logger.Enabled = true; PerfLog.Enabled = true;
                        if (firstEnable) Enqueue("========== diagnostic logging ENABLED by user ==========");
                    }
                    else
                    {
                        Enqueue("========== diagnostic logging DISABLED by user ==========");
                        Logger.Enabled = false; PerfLog.Enabled = false;
                        _sinkOpen = false;                    // stop accepting lines…
                        _closeAfterDrain = true;              // …then the worker flushes + releases the handle
                        try { _signal.Set(); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>Appends a line directly (also used by the trace listener). Non-blocking + safe.</summary>
        public static void Write(string line) => Enqueue(line);

        // ── session lifecycle ────────────────────────────────────────────────

        private static void StartSession(DateTime stamp)
        {
            Directory.CreateDirectory(_logsDir);
            _sessionBase = "telegarm_" + stamp.ToString("yyyyMMdd_HHmmss");
            _part = 1; _partBytes = 0;
            OpenSink();
            if (_pendingHeader != null) { Enqueue(_pendingHeader); _pendingHeader = null; }   // launch header first (3.5.3)
        }

        private static string PartPath(int part) =>
            Path.Combine(_logsDir, _sessionBase + (part == 1 ? "" : "_part" + part) + ".log");

        private static string Header()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
            return "========== TelegArm v" + Program.Version + " log started "
                   + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  ("
                   + (IntPtr.Size == 8 ? "64-bit" : "32-bit") + " / " + arch + ") ==========";
        }

        private static void OpenSink()
        {
            if (_writer == null) { _writer = OpenWriter(PartPath(_part)); if (_writer == null) return; }
            _closeAfterDrain = false;
            _sinkOpen = true;
            if (_worker == null)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FileLog", Priority = ThreadPriority.BelowNormal };
                _worker.Start();
            }
            if (!_listenerAdded)
            {
                Trace.Listeners.Add(new TeeListener());   // THE single tee point
                _listenerAdded = true;
            }
        }

        private static void Enqueue(string line)
        {
            if (!_sinkOpen || line == null) return;
            if (Volatile.Read(ref _pending) > 20000) return;   // runaway guard (bound memory)
            Interlocked.Increment(ref _pending);
            _queue.Enqueue(Logger.Stamp(line));
            try { _signal.Set(); } catch { }
        }

        // ── worker (owns the writer; rotation happens here) ──────────────────

        private static void WorkerLoop()
        {
            while (!_stopping)
            {
                try { _signal.WaitOne(1000); } catch { }
                Drain();
                if (_closeAfterDrain) CloseWriter();
            }
            Drain();
            CloseWriter();
        }

        private static void Drain()
        {
            if (_writer == null)
            {
                string junk; while (_queue.TryDequeue(out junk)) Interlocked.Decrement(ref _pending);
                return;
            }
            try
            {
                bool any = false; string s;
                while (_queue.TryDequeue(out s))
                {
                    Interlocked.Decrement(ref _pending);
                    _writer.WriteLine(s);
                    any = true;
                    // Cheap size budget: character count approximates bytes (ASCII-dominant lines; multibyte
                    // under-counts slightly — the 10 MB cap has ample slack for that).
                    _partBytes += s.Length + 2;
                    if (_partBytes >= MaxBytesPerPart && !RotatePart()) return;   // budget exhausted → writer closed
                }
                if (any && _writer != null) _writer.Flush();
            }
            catch { /* never let logging take the app (or the writer thread) down */ }
        }

        /// <summary>Part cap reached: roll to the next part, or exhaust the session budget after part 3.
        /// Runs on the worker thread (which owns the writer). False = budget spent, writer closed.</summary>
        private static bool RotatePart()
        {
            try
            {
                if (_part >= MaxParts)
                {
                    _writer.WriteLine(Logger.Stamp("log budget exhausted — logging stopped for this session"));
                    _writer.Flush();
                    CloseWriter();
                    _budgetExhausted = true;
                    _sinkOpen = false;
                    Logger.Enabled = false; PerfLog.Enabled = false;
                    return false;
                }
                _writer.WriteLine(Logger.Stamp("→ continued in part " + (_part + 1)));
                _writer.Flush();
                CloseWriter();
                _part++; _partBytes = 0;
                _writer = OpenWriter(PartPath(_part));
                if (_writer == null) { _sinkOpen = false; return false; }
                string cont = Logger.Stamp("========== TelegArm v" + Program.Version
                              + " log part " + _part + " (session " + _sessionBase + ") ==========");
                _writer.WriteLine(cont);
                _partBytes = cont.Length + 2;
                return true;
            }
            catch { return false; }
        }

        private static void CloseWriter()
        {
            _closeAfterDrain = false;
            try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
            _writer = null;   // handle released — session files are deletable while the app runs
        }

        private static StreamWriter OpenWriter(string path)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                // UTF-8 WITH BOM: RT 8.1's built-in Notepad doesn't auto-detect BOM-less UTF-8 and would show
                // →/—/Persian as mojibake; the BOM (written once per fresh file) makes it read cleanly.
                return new StreamWriter(fs, new UTF8Encoding(true)) { AutoFlush = false };
            }
            catch { return null; }
        }

        private static void Shutdown()
        {
            try { _stopping = true; _sinkOpen = false; _signal.Set(); } catch { }
            try { _worker?.Join(300); } catch { }
            try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); _writer = null; } } catch { }
            Logger.Enabled = false;
        }

        // ── auto-cleanup (3.7) ───────────────────────────────────────────────

        /// <summary>Deletes session logs older than <see cref="KeepDays"/> days, ALWAYS keeping the newest
        /// <see cref="KeepSessions"/> sessions (a session = base file + its parts, aged as one unit by its
        /// newest write). crash.log lives in the app dir root — never touched here. Fully guarded; a locked
        /// or missing file is skipped.</summary>
        private static void CleanupOldSessions()
        {
            try
            {
                string dir = _logsDir ?? LogsDirectory;
                if (dir == null || !Directory.Exists(dir)) return;

                // Group base + parts into one session unit keyed by "telegarm_YYYYMMDD_HHMMSS".
                var sessions = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in new DirectoryInfo(dir).GetFiles("telegarm_*.log"))
                {
                    string key = Path.GetFileNameWithoutExtension(f.Name);
                    int p = key.IndexOf("_part", StringComparison.OrdinalIgnoreCase);
                    if (p > 0) key = key.Substring(0, p);
                    List<FileInfo> list;
                    if (!sessions.TryGetValue(key, out list)) sessions[key] = list = new List<FileInfo>();
                    list.Add(f);
                }

                // Recency by session NAME (telegarm_YYYYMMDD_HHMMSS — lexicographic == chronological): fully
                // deterministic, unlike LastWriteTime which can tie. Age (the 2-day test) stays on file times.
                var ordered = sessions.OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
                DateTime cutoff = DateTime.UtcNow.AddDays(-KeepDays);
                for (int i = KeepSessions; i < ordered.Count; i++)                     // newest N always survive
                {
                    if (ordered[i].Value.Max(f => f.LastWriteTimeUtc) >= cutoff) continue;   // recent enough
                    foreach (var f in ordered[i].Value)
                        try { f.Delete(); } catch { /* locked/missing → skip */ }
                }
            }
            catch { /* cleanup must never affect startup */ }
        }

        private sealed class TeeListener : TraceListener
        {
            public override void WriteLine(string message) { FileLog.Write(message); }
            public override void Write(string message) { FileLog.Write(message); }
        }
    }
}
