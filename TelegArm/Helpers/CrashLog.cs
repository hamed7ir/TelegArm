using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>
    /// ALWAYS-ON crash capture (LOG-TOGGLE+CRASH batch), fully independent of the diagnostic-logging toggle:
    /// hooks the three unhandled-exception surfaces at startup and APPENDS a full record (timestamp, version,
    /// arch, which handler, the complete exception chain — never truncated) to crash.log in the same
    /// Documents-first app dir as telegarm.log. Zero cost until a crash occurs: nothing is resolved or opened
    /// at install time, and the file is only created by the first record. Every handler body is guarded so
    /// crash capture can never itself throw. There is deliberately NO off switch.
    /// </summary>
    public static class CrashLog
    {
        private const string Marker = "===== CRASH ";   // record delimiter — Count() counts these lines

        /// <summary>The crash.log path (resolved on demand; null if the app dir can't be resolved).</summary>
        public static string FilePath
        {
            get
            {
                try { return Path.Combine(StoragePaths.ResolveAppDir(), "crash.log"); }
                catch { return null; }
            }
        }

        /// <summary>Hooks AppDomain.UnhandledException, Application.ThreadException (+ CatchException mode) and
        /// TaskScheduler.UnobservedTaskException. Call FIRST in startup, before any window exists. Recording
        /// only — user-facing error dialogs stay with the existing handlers registered after this.</summary>
        public static void Install()
        {
            try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
            // NOTE: Application.ThreadException is a SINGLE-SLOT event (its add accessor REPLACES the previous
            // handler, it does not multicast) — so the UI-thread recording lives inside Program.Run's one
            // ThreadException handler (Record first, then the error dialog), NOT here.
            try
            {
                // Anything else: record; the process is going down (IsTerminating) — the record is the evidence.
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Record("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);
            }
            catch { }
            try
            {
                // Faulted tasks nobody awaited: record and observe so they don't escalate on finalization.
                // TEARDOWN-HYGIENE A1: a task that lost the WTelegram client (our teardown OR the library's
                // own secondary-DC/keepalive disposal — WTC-internal tasks we can never observe at launch)
                // is an EXPECTED race: one gated line, NO record. crash.log stays reserved for the unexpected.
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    try
                    {
                        if (e.Exception != null && AllTeardownTransient(e.Exception))
                        {
                            if (Logger.Enabled)
                                System.Diagnostics.Debug.WriteLine("[TEARDOWN] unobserved bg task lost client (transient)");
                            e.SetObserved();
                            return;
                        }
                    }
                    catch { }
                    Record("TaskScheduler.UnobservedTaskException", e.Exception);
                    try { e.SetObserved(); } catch { }
                };
            }
            catch { }
        }

        /// <summary>True when EVERY inner exception is an expected teardown race (disposed WTelegram client /
        /// cancellation) — any other fault in the aggregate keeps the whole record.</summary>
        private static bool AllTeardownTransient(AggregateException agg)
        {
            try
            {
                var inners = agg.Flatten().InnerExceptions;
                if (inners.Count == 0) return false;
                foreach (var ex in inners)
                    if (!TelegramService.IsTeardownTransient(ex)) return false;
                return true;
            }
            catch { return false; }
        }

        // Per-context throttle for RecordThrottled — a repeating async-void handler failure must leave evidence
        // ONCE, not balloon crash.log.
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> _lastByContext
            = new System.Collections.Generic.Dictionary<string, DateTime>();
        private static readonly object _throttleGate = new object();

        /// <summary>Records at most once per minute per <paramref name="context"/> — for the top-level catches of
        /// fire-and-forget async void handlers, which otherwise swallow silently (the exception never reaches the
        /// ThreadException handler because the catch consumed it). Recording is the ONLY change: callers keep
        /// swallowing afterward, so app resilience is unchanged — only its blindness is.</summary>
        public static void RecordThrottled(string context, Exception ex)
        {
            try
            {
                lock (_throttleGate)
                {
                    DateTime last;
                    if (_lastByContext.TryGetValue(context, out last) && (DateTime.UtcNow - last).TotalSeconds < 60) return;
                    _lastByContext[context] = DateTime.UtcNow;
                }
                Record(context, ex);
            }
            catch { }
        }

        /// <summary>Appends one full crash record. Guarded — never throws.</summary>
        public static void Record(string source, Exception ex)
        {
            Record(source, ex != null ? ex.ToString() : "<no exception object>");   // full chain, never truncated
        }

        /// <summary>Records a NON-exception diagnostic in the same record format — used by the UI-hang
        /// confessor (TOUCH-FREEZE): hangs never reach the exception hooks, but they must still leave
        /// evidence in crash.log, especially on RT where no debugger can attach.</summary>
        public static void Record(string source, string detail)
        {
            try
            {
                string path = FilePath;
                if (path == null) return;
                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
                var sb = new StringBuilder(1024);
                sb.Append(Marker).Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append("  TelegArm v").Append(Program.Version)
                  .Append(" (").Append(IntPtr.Size == 8 ? "64-bit" : "32-bit").Append(" / ").Append(arch).Append(')')
                  .Append("  via ").Append(source).AppendLine(" =====");
                sb.AppendLine(detail ?? "<no detail>");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch { /* crash capture must never take the app down (further) */ }
        }

        /// <summary>Number of recorded crashes (counts record markers). Cheap; call only when the settings
        /// screen opens. 0 when the file doesn't exist or can't be read.</summary>
        public static int Count()
        {
            try
            {
                string path = FilePath;
                if (path == null || !File.Exists(path)) return 0;
                int n = 0;
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith(Marker, StringComparison.Ordinal)) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>Deletes crash.log (best-effort). True if nothing remains afterwards.</summary>
        public static bool Clear()
        {
            try
            {
                string path = FilePath;
                if (path != null && File.Exists(path)) File.Delete(path);
                return path == null || !File.Exists(path);
            }
            catch { return false; }
        }
    }
}
