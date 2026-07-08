namespace TelegArm.Helpers
{
    /// <summary>
    /// The app-wide HOT-PATH gate for diagnostic logging (LOG-TOGGLE batch). Every high-frequency log site
    /// (per-keystroke, per-paint, per-touch, per-update, per-tick) checks <see cref="Enabled"/> BEFORE building
    /// its log string, so with logging off (the default) no log-purpose formatting executes anywhere. Cold sites
    /// (once per launch/connect/user action) may skip the check — the FileLog sink drops their lines when closed.
    /// Flipped live by the Settings toggle via <see cref="FileLog.SetEnabled"/>; never set it directly.
    /// </summary>
    public static class Logger
    {
        /// <summary>True while diagnostic logging is on. Volatile — read from any thread, hot paths included.</summary>
        public static volatile bool Enabled;

        /// <summary>The one time-of-day line prefix used by the file logger ("HH:mm:ss.fff  &lt;line&gt;").
        /// (crash.log deliberately uses a FULL-date stamp instead — crash records need the day.)</summary>
        public static string Stamp(string line) => System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line;

        /// <summary>A diagnostic emit that SURVIVES a Release build. The app's ~35 ordinary log sites use
        /// <see cref="System.Diagnostics.Debug"/>.WriteLine, which the compiler STRIPS from Release (only TRACE is
        /// defined there) — so none of them reach the <see cref="FileLog"/> tee in the SHIPPED installer. This uses
        /// <see cref="System.Diagnostics.Trace"/>.WriteLine, which Release keeps, so a tagged one-off diagnostic
        /// (e.g. "[SESSPATH]") IS captured in the installed build once the user turns logging on. Gated by
        /// <see cref="Enabled"/> like every other site (the caller may also pre-gate to skip string building).</summary>
        public static void Diag(string line)
        {
            if (Enabled && line != null) System.Diagnostics.Trace.WriteLine(line);
        }
    }
}
