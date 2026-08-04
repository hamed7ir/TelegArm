using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Lightweight [PERF] instrumentation (diagnosis batch): a Stopwatch timer + Interlocked counters per hot
    /// path, flushed as ONE summary line per interval (NO per-call log spam) — reads in DebugView (ASCII).
    /// Cheap enough not to distort results (a few ns per record vs ms paints). <see cref="Flush"/> resets the
    /// interval counters. Set <see cref="Enabled"/> = false to mute everything. Read the line to tell the three
    /// failure shapes apart: PER-FRAME (bubblePaint/richPaint/measure dominate, constant), UPDATE-STORM
    /// (update count/sec high AND fullRender &gt; 0 or chatRefresh high per update), GROWTH (g: gauges climb).
    /// </summary>
    public static class PerfLog
    {
        public enum P { BubblePaint, Measure, MeasureRedundant, Position, RichPaint, RichPaintRender, EmojiHit, EmojiMiss, Update, ChatRefresh, RenderChatList, _Count }

        // Driven by the diagnostic-logging toggle (FileLog.Init/SetEnabled); default OFF — when off every
        // T()/Rec()/Inc()/SetGauge()/Flush() is a no-op, so [PERF] costs nothing in normal use.
        public static bool Enabled = false;

        // GDI/USER handle gauge for the RT baseline (Tegra 3 dies slowly on handle leaks). One pair of cheap
        // syscalls per 1.5s tick, only when Enabled — zero cost otherwise.
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

        private static readonly long[] _count = new long[(int)P._Count];
        private static readonly long[] _ticks = new long[(int)P._Count];
        private static readonly Dictionary<string, long> _gauges = new Dictionary<string, long>();
        private static long _intervalStart = Stopwatch.GetTimestamp();
        private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>Start a timing scope (returns a timestamp; 0 when disabled → Rec is a no-op).</summary>
        public static long T() { return Enabled ? Stopwatch.GetTimestamp() : 0L; }

        /// <summary>Record one timed call against a bucket (count++ and add elapsed ticks).</summary>
        public static void Rec(P p, long start)
        {
            if (!Enabled || start == 0L) return;
            Interlocked.Increment(ref _count[(int)p]);
            Interlocked.Add(ref _ticks[(int)p], Stopwatch.GetTimestamp() - start);
        }

        /// <summary>Bump a pure counter (no timing) — for hit/miss / redundant / occurrence counts.</summary>
        public static void Inc(P p, int n = 1)
        {
            if (Enabled) Interlocked.Add(ref _count[(int)p], n);
        }

        /// <summary>Set the latest value of a named gauge (collection size, etc.) — set on the UI thread.</summary>
        public static void SetGauge(string name, long value)
        {
            if (!Enabled) return;
            lock (_gauges) _gauges[name] = value;
        }

        /// <summary>Emit one [PERF] summary line and reset the interval counters.</summary>
        public static void Flush()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            double sec = (now - Interlocked.Exchange(ref _intervalStart, now)) * _msPerTick / 1000.0;
            if (sec <= 0) sec = 0.001;

            var sb = new StringBuilder(256);
            sb.Append("[PERF] ").Append(sec.ToString("0.00")).Append("s |");
            Bucket(sb, "bubblePaint", P.BubblePaint);
            Bucket(sb, "measure", P.Measure);
            sb.Append(" mCacheHit=").Append(Interlocked.Exchange(ref _count[(int)P.MeasureRedundant], 0)).Append(" |");
            sb.Append(" pos=").Append(Interlocked.Exchange(ref _count[(int)P.Position], 0)).Append(" |");
            Bucket(sb, "richPaint", P.RichPaint);
            sb.Append(" richRender=").Append(Interlocked.Exchange(ref _count[(int)P.RichPaintRender], 0)).Append(" |");
            sb.Append(" emoji=").Append(Interlocked.Exchange(ref _count[(int)P.EmojiHit], 0)).Append("h/")
              .Append(Interlocked.Exchange(ref _count[(int)P.EmojiMiss], 0)).Append("m |");
            BucketRate(sb, "update", P.Update, sec);
            Bucket(sb, "chatRefresh", P.ChatRefresh);
            Bucket(sb, "fullRender", P.RenderChatList);

            lock (_gauges)
            {
                sb.Append(" g:");
                foreach (var kv in _gauges) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            }
            try
            {
                IntPtr proc = GetCurrentProcess();
                sb.Append(" | gdi=").Append(GetGuiResources(proc, GR_GDIOBJECTS))
                  .Append(" usr=").Append(GetGuiResources(proc, GR_USEROBJECTS));
            }
            catch { }
            // R7: Logger.Diag (Trace) — Debug.WriteLine is STRIPPED from Release, so the whole [PERF]
            // line was invisible in the installed build, which is where the RT numbers actually matter.
            Logger.Diag(sb.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  COLD-START LADDER (BATCH-TA-0). The app had NO boot timing on any build: PerfLog measures only
        //  steady-state paint/measure/update buckets, and everything emitted via Debug.WriteLine vanished
        //  from Release. Without this ladder every load-time change is unfalsifiable on the RT device.
        //  Emits through Logger.Diag (Trace) so it SURVIVES Release, self-gated by Logger.Enabled.
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>GDI + USER handle counts for this process — the RT leak gauge (Tegra 3 dies slowly on
        /// handle leaks). Already sampled once per [PERF] tick; exposed so a specific rung can sample it at
        /// the exact moment of interest. Never throws; returns 0/0 if the syscall is unavailable.</summary>
        public static void GuiHandles(out uint gdi, out uint usr)
        {
            gdi = 0; usr = 0;
            try
            {
                IntPtr proc = GetCurrentProcess();
                gdi = GetGuiResources(proc, GR_GDIOBJECTS);
                usr = GetGuiResources(proc, GR_USEROBJECTS);
            }
            catch { }
        }

        private static Stopwatch _boot;

        /// <summary>Starts the cold-start clock. Called once at the very top of Program.Run, BEFORE any
        /// logging sink exists — the first <see cref="Boot"/> line lands once FileLog.Init has run.</summary>
        public static void StartBoot()
        {
            if (_boot == null) _boot = Stopwatch.StartNew();
        }

        /// <summary>One rung of the cold-start ladder: "[BOOT] +1234ms &lt;phase&gt;". Cold sites only (a
        /// handful per launch), so no pre-gate is needed — Logger.Diag drops the line when logging is off.</summary>
        public static void Boot(string phase)
        {
            if (_boot == null) return;   // Boot() before StartBoot() → nothing to measure against
            Logger.Diag("[BOOT] +" + _boot.ElapsedMilliseconds + "ms " + phase);
        }

        private static void Bucket(StringBuilder sb, string name, P p)
        {
            long c = Interlocked.Exchange(ref _count[(int)p], 0);
            double ms = Interlocked.Exchange(ref _ticks[(int)p], 0) * _msPerTick;
            sb.Append(' ').Append(name).Append(' ').Append(c).Append("x ").Append(ms.ToString("0.0")).Append("ms");
            if (c > 0) sb.Append('(').Append((ms / c).ToString("0.00")).Append(')');
            sb.Append(" |");
        }

        private static void BucketRate(StringBuilder sb, string name, P p, double sec)
        {
            long c = Interlocked.Exchange(ref _count[(int)p], 0);
            double ms = Interlocked.Exchange(ref _ticks[(int)p], 0) * _msPerTick;
            sb.Append(' ').Append(name).Append(' ').Append(c).Append("x(").Append((c / sec).ToString("0")).Append("/s) ")
              .Append(ms.ToString("0.0")).Append("ms");
            if (c > 0) sb.Append('(').Append((ms / c).ToString("0.00")).Append(')');
            sb.Append(" |");
        }
    }
}
