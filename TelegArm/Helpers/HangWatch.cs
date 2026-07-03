using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>
    /// UI-hang confessor (TOUCH-FREEZE Part 0): a background thread heartbeats the UI thread via
    /// BeginInvoke; when the pump goes silent for a full 5s window it records the UI thread's managed
    /// stack plus a CPU spin/wait verdict to crash.log. Hangs are invisible to every exception hook,
    /// and on RT no debugger can attach — this is the only way a freeze leaves evidence. One record
    /// per hang episode; re-arms when the pump recovers. Zero cost while the app is healthy
    /// (one no-op BeginInvoke per 5s).
    /// </summary>
    public static class HangWatch
    {
        private static Thread _uiThread;
        private static Control _anchor;
        private static int _beat;              // bumped by the UI thread; read/compared on the watcher
        private static int _silentRounds;      // consecutive 5s windows with no heartbeat
        private static bool _reported;         // one confession per episode
        private static bool _sessionReported;  // …and at most ONE crash.log record per session — startup
                                               // churn and modal-dialog builds can stall the pump >5s
                                               // legitimately; recurring episodes stay visible via the
                                               // gated [HANG] line without polluting crash.log per launch
        private static bool _started;

        /// <summary>Call ONCE from the UI thread (captures it as the suspect-to-be).</summary>
        public static void Start(Control anchor)
        {
            if (_started || anchor == null) return;
            _started = true;
            _anchor = anchor;
            _uiThread = Thread.CurrentThread;
            var t = new Thread(Loop) { IsBackground = true, Name = "hang-watch" };
            t.Start();
        }

        private static void Loop()
        {
            var proc = Process.GetCurrentProcess();
            TimeSpan lastCpu = TimeSpan.Zero;
            try { lastCpu = proc.TotalProcessorTime; } catch { }
            while (true)
            {
                int before = _beat;
                try
                {
                    if (_anchor.IsHandleCreated && !_anchor.IsDisposed)
                        _anchor.BeginInvoke((Action)(() => Interlocked.Increment(ref _beat)));
                }
                catch { /* handle churn — try again next round */ }
                Thread.Sleep(5000);

                double cpuMs = 0;
                try
                {
                    proc.Refresh();
                    TimeSpan cpu = proc.TotalProcessorTime;
                    cpuMs = (cpu - lastCpu).TotalMilliseconds;
                    lastCpu = cpu;
                }
                catch { }

                if (Volatile.Read(ref _beat) != before) { _silentRounds = 0; _reported = false; continue; }   // pump alive
                if (!_anchor.IsHandleCreated || _anchor.IsDisposed) continue;              // not hung — not pumping yet/anymore
                _silentRounds++;
                if (_silentRounds < 2 || _reported) continue;   // confess only after 10s of CONTINUOUS silence
                _reported = true;

                // Spin vs wait: >50% of one core across the window = a loop; near-zero = a blocking wait.
                // (Process-wide CPU — background decode threads can inflate it; read as a hint, not proof.)
                string verdict = cpuMs > 2500 ? "SPIN" : "WAIT";
                if (Logger.Enabled)
                    Debug.WriteLine("[HANG] UI thread silent >10s — " + verdict + " cpu=" + (int)cpuMs + "ms/5000ms" + (_sessionReported ? "" : " (stack → crash.log)"));
                if (_sessionReported) continue;
                _sessionReported = true;
                CrashLog.Record("UI-HANG (" + verdict + " cpu=" + (int)cpuMs + "ms/5000ms)", CaptureUiStack());
            }
        }

        private static string CaptureUiStack()
        {
            try
            {
                // Thread.Suspend/StackTrace(thread) are obsolete but functional on .NET Framework —
                // and this only ever runs against an ALREADY-hung UI thread, so the usual hazards
                // (suspending a thread that holds runtime locks) can't make things worse.
#pragma warning disable 618
                _uiThread.Suspend();
                try { return new StackTrace(_uiThread, true).ToString(); }
                finally { _uiThread.Resume(); }
#pragma warning restore 618
            }
            catch (Exception ex)
            {
                return "stack capture failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
