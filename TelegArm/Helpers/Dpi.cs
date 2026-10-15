using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.1 — THE one place the process declares its DPI awareness, and the arithmetic every
    /// later step uses to size things.
    ///
    /// Modes (<see cref="AppSettings.DpiMode"/>, overridden by the environment variable TELEGARM_DPI for testing):
    ///   ""        build default — PER-MONITOR since redesign step 3.7 (PMv1; was system-aware, 1.x's call)
    ///   "System"  system-aware: one DPI for the whole process, set at logon
    ///   "Auto"    per-monitor (PMv1, the same semantics on RT 8.1 and Windows 11)
    ///   "Unaware" Windows stretches the whole UI (the "proportional scaling" rescue)
    ///   TELEGARM_DPI = unaware | system | pmv1 | pmv2 — pmv1 reproduces RT 8.1's semantics on a Windows 11 box;
    ///                  pmv2 is opt-in only until verified (plan D4, §13 item 2).
    ///
    /// ⚠ NOTHING MAY CREATE A WINDOW OR READ A SCREEN METRIC BEFORE <see cref="Init"/>. Awareness can be set once
    ///   per process; a window created first pins it, and Screen values read first are in the wrong units.
    /// ⚠ Every API is located with GetProcAddress first, so the summary can say "export missing" (RT 8.0 and
    ///   Windows 7 have no shcore SetProcessDpiAwareness; only Windows 10 has the *Context functions) apart from
    ///   "call failed" (E_ACCESSDENIED: something declared awareness before us — a manifest, a compat shim).
    /// ⚠ Never read Control.DeviceDpi: under per-monitor v1 it reports the SYSTEM DPI on every monitor. Use
    ///   <see cref="Of"/>, which asks the window (scripts\check.ps1 enforces this from step 1.1).</summary>
    public static class Dpi
    {
        /// <summary>The DPI every logical length in the code is written at: 100%.</summary>
        public const int Logical = 96;

        /// <summary>The system DPI after the declaration (96 when unaware, since the process is then virtualized).</summary>
        public static int SystemDpi { get; private set; } = Logical;

        /// <summary>The EFFECTIVE awareness read back after declaring: "unaware", "system", "per-monitor",
        /// "per-monitor-v2", or "unknown" when no read-back API exists.</summary>
        public static string Mode { get; private set; } = "not declared";

        /// <summary>One line for logs, Copy diagnostics and Settings → Advanced: what the OS offers, what was asked
        /// for and why, what was called and returned, what is in force, and the monitors.</summary>
        public static string ProbeSummary { get; private set; } = "(not probed)";

        /// <summary>Short form of the request for the Settings row, e.g. "system (build default)".</summary>
        public static string Request { get; private set; } = "";

        private static int _busy;

        /// <summary>True while a rescale runs (<see cref="DpiScaler"/>). Expensive SizeChanged / measure handlers
        /// skip their work while it is set and catch up in the one layout pass at the end (plan §6.1).</summary>
        public static bool Busy { get { return Volatile.Read(ref _busy) > 0; } }
        internal static void EnterBusy() { Interlocked.Increment(ref _busy); }
        internal static void ExitBusy() { Interlocked.Decrement(ref _busy); }

        // ── the interface scale (step 3.8a) ─────────────────────────────────────────────────────────────────────────

        /// <summary>Step 3.8a — the user's interface scale in percent: 50-200 in 25% steps, 100 = off. It multiplies the
        /// monitor's DPI into the EFFECTIVE DPI every window lays out at (<see cref="Effective(int)"/>), so it composes
        /// with per-monitor DPI: a monitor move keeps the user's choice. Resolved once in <see cref="Init"/> from
        /// --scale=N, then TELEGARM_SCALE, then AppSettings.InterfaceScale; step 3.8b changes it live.
        /// ⚠ At 100 every formula is the identity (<see cref="Effective(int,int)"/> returns raw), so the RT's 100%
        ///   stays pixel-identical; SelfTest checks it.</summary>
        public static int UserScalePercent { get; private set; } = 100;

        /// <summary>Where <see cref="UserScalePercent"/> came from: "default", "--scale", "TELEGARM_SCALE" or "setting".</summary>
        public static string ScaleSource { get; private set; } = "default";

        /// <summary>A RAW monitor DPI at <paramref name="pct"/>: round(raw·pct/100), away from zero. EXACTLY raw at 100.
        /// Every pair of a monitor DPI (multiples of 24) and a 25% detent is exact.</summary>
        internal static int Effective(int raw, int pct)
        {
            if (pct == 100 || raw <= 0 || pct <= 0) return raw;
            return (int)Math.Round(raw * (double)pct / 100, MidpointRounding.AwayFromZero);
        }

        /// <summary>The EFFECTIVE DPI for a raw monitor DPI: what windows lay out at (the monitor × the user's scale).
        /// What stays RAW: finger slop and flick speeds, the touch keyboard's DIP rectangle, the tray icon, work-area and
        /// screen maths, and WM_DPICHANGED's suggested rectangle (the scale cancels in its ratio).</summary>
        public static int Effective(int raw) { return Effective(raw, UserScalePercent); }

        /// <summary>The effective DPI of a window that tracks none (unparented controls, plain Forms): the system DPI
        /// at the user's scale.</summary>
        public static int DefaultDpi { get { return Effective(SystemDpi); } }

        /// <summary>50-200 in 25% steps (the slider's detents, plan 3.8b).</summary>
        internal static bool ValidScale(int pct) { return pct >= 50 && pct <= 200 && pct % 25 == 0; }

        /// <summary>The scale to use: "--scale=N" in <paramref name="args"/>, then <paramref name="env"/>
        /// (TELEGARM_SCALE), then <paramref name="setting"/> — the first VALID one wins; none: 100. Pure, for SelfTest.</summary>
        internal static int ResolveScale(string[] args, string env, int setting, out string source)
        {
            int v;
            if (args != null)
                foreach (var a in args)
                    if (a != null && a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(a.Substring(8).TrimEnd('%'), out v) && ValidScale(v)) { source = "--scale"; return v; }
            if (!string.IsNullOrEmpty(env) && int.TryParse(env.Trim().TrimEnd('%'), out v) && ValidScale(v)) { source = "TELEGARM_SCALE"; return v; }
            if (setting != 100 && ValidScale(setting)) { source = "setting"; return setting; }
            source = "default";
            return 100;
        }

        // ── arithmetic ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A logical length at <paramref name="dpi"/>: round(n·dpi/96), away from zero. EXACTLY n at 96 —
        /// the RT runs at 100%, and nothing there may move by a pixel because of this (SelfTest checks it).</summary>
        public static int Px(int n, int dpi)
        {
            if (dpi == Logical || dpi <= 0) return n;
            return (int)Math.Round(n * (double)dpi / Logical, MidpointRounding.AwayFromZero);
        }

        /// <summary>A logical length at the DPI of <paramref name="c"/>'s window.</summary>
        public static int Px(int n, Control c) { return Px(n, Of(c)); }

        /// <summary>A logical float metric (pen widths, radii, font points) at <paramref name="dpi"/>.</summary>
        public static float F(float v, int dpi) { return dpi <= 0 ? v : v * dpi / Logical; }

        public static float F(float v, Control c) { return F(v, Of(c)); }

        /// <summary>The EFFECTIVE DPI of the window <paramref name="c"/> is in: its top-level window's own DPI when that
        /// window tracks one (<see cref="IDpiHost"/>), else <see cref="DefaultDpi"/> (the system DPI at the user's scale).</summary>
        public static int Of(Control c)
        {
            var host = (c == null ? null : (c.TopLevelControl ?? c)) as IDpiHost;
            int d = host != null ? host.Dpi : 0;
            return d > 0 ? d : DefaultDpi;
        }

        /// <summary>Step 3.1 — the RAW DPI of the monitor at a screen point, as this process sees it (the system DPI unless
        /// per-monitor aware). For a popup, BEFORE it has a window: a menu or a toast is sized for where it will open.</summary>
        public static int ForPoint(Point screen)
        {
            try
            {
                if (HasCached(ref _hasMonitorDpi, "shcore.dll", "GetDpiForMonitor"))   // 8.1 / RT 8.1+
                {
                    uint x, y;
                    if (GetDpiForMonitor(MonitorFromPoint(new POINT { X = screen.X, Y = screen.Y }, 2), 0, out x, out y) == 0 && x > 0) return (int)x;
                }
            }
            catch { }
            return SystemDpi;
        }

        /// <summary>The RAW DPI of a screen (its centre's monitor).</summary>
        public static int ForScreen(Screen s)
        {
            if (s == null) return SystemDpi;
            var b = s.Bounds;
            return ForPoint(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
        }

        /// <summary>THE entry point for a popup's DPI: the EFFECTIVE DPI (monitor × user scale) at the point it opens.</summary>
        public static int ForPopup(Point screen) { return Effective(ForPoint(screen)); }

        /// <summary>The DPI of the monitor <paramref name="hwnd"/> is on, AS THIS PROCESS SEES IT: the real per-monitor
        /// value when per-monitor aware, the system DPI otherwise (Windows virtualizes it — measured in step 1.1).
        /// So a system-aware build gets SystemDpi everywhere and every per-window path stays inert.</summary>
        public static int ForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return SystemDpi;
            try
            {
                if (HasCached(ref _hasWindowDpi, "user32.dll", "GetDpiForWindow")) { int d = (int)GetDpiForWindow(hwnd); if (d > 0) return d; }   // 1607+
                if (HasCached(ref _hasMonitorDpi, "shcore.dll", "GetDpiForMonitor"))                                                                 // 8.1 / RT 8.1
                {
                    uint x, y;
                    if (GetDpiForMonitor(MonitorFromWindow(hwnd, 2), 0, out x, out y) == 0 && x > 0) return (int)x;
                }
            }
            catch { }
            return SystemDpi;
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        // ── the declaration ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Declares the process's DPI awareness once, reads back what is really in force, and builds the
        /// probe summary (logged with Logger.Diag, so it reaches a Release log when logging is on).</summary>
        internal static void Init()
        {
            bool spdac = Has("user32.dll", "SetProcessDpiAwarenessContext");   // Windows 10 1703+
            bool spda = Has("shcore.dll", "SetProcessDpiAwareness");           // Windows 8.1 / RT 8.1+
            bool spdaw = Has("user32.dll", "SetProcessDPIAware");               // Vista+
            bool gtdac = Has("user32.dll", "GetThreadDpiAwarenessContext") && Has("user32.dll", "GetAwarenessFromDpiAwarenessContext");
            bool gpda = Has("shcore.dll", "GetProcessDpiAwareness");

            string source;
            string want = Requested(out source);
            Request = want + " (" + source + ")";
            string call;
            try { call = Declare(want, spdac, spda, spdaw); }
            catch (Exception ex) { call = "threw " + ex.GetType().Name; }
            Mode = ReadBack(gtdac, gpda);
            SystemDpi = ReadSystemDpi();
            // Step 3.8a: the interface scale (rig and rescue: --scale=N / TELEGARM_SCALE; else the setting).
            string scaleSource, env = null;
            int setting = 100;
            try { env = Environment.GetEnvironmentVariable("TELEGARM_SCALE"); } catch { }
            try { setting = AppSettings.Instance.InterfaceScale; } catch { }
            string[] args = null;
            try { args = Environment.GetCommandLineArgs(); } catch { }
            UserScalePercent = ResolveScale(args, env, setting, out scaleSource);
            ScaleSource = scaleSource;

            ProbeSummary = "os=" + OsVersion()
                + " exports{SetProcessDpiAwarenessContext=" + YesNo(spdac) + " SetProcessDpiAwareness=" + YesNo(spda)
                + " SetProcessDPIAware=" + YesNo(spdaw) + " readback=" + (gtdac ? "context" : gpda ? "shcore" : "legacy") + "}"
                + " request=" + Request + " call=" + call + " mode=" + Mode + " systemDpi=" + SystemDpi
                + " scale=" + UserScalePercent + "% (" + ScaleSource + ")"
                + " monitors=[" + Monitors(spda) + "]";
            Logger.Diag("[DPI] " + ProbeSummary);
        }

        /// <summary>The build default — "pmv1" since redesign step 3.7 (it was "system"). It changes every user who never
        /// chose, because AppSettings stores "" for them (step 0.4); Settings > Advanced > "Display scaling mode" is the
        /// off-switch (System, or Compatibility = unaware). Where per-monitor is unavailable (RT 8.0, Windows 7), Declare
        /// falls back to system-aware.</summary>
        private const string BuildDefault = "pmv1";

        private static string Requested(out string source)
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable("TELEGARM_DPI"); } catch { }
            if (!string.IsNullOrEmpty(env))
            {
                string e = env.Trim().ToLowerInvariant();
                if (e == "unaware" || e == "system" || e == "pmv1" || e == "pmv2") { source = "TELEGARM_DPI"; return e; }
                Logger.Diag("[DPI] TELEGARM_DPI=" + env + " not understood: using the setting");   // falls through (step 3.7)
            }
            string mode = "";
            try { mode = AppSettings.Instance.DpiMode ?? ""; } catch { }
            switch (mode.Trim().ToLowerInvariant())
            {
                case "unaware": source = "setting"; return "unaware";
                case "system": source = "setting"; return "system";
                case "auto": source = "setting"; return "pmv1";
                case "": source = "build default"; return BuildDefault;
                default: source = "build default; DpiMode=" + mode + " not understood"; return BuildDefault;
            }
        }

        /// <summary>Makes the call for <paramref name="want"/>, falling back down the chain when an export is missing
        /// or a call fails. Returns what was called and with what result.
        /// Unaware and System use shcore's SetProcessDpiAwareness first: that is the exact call 1.x made, so on
        /// Windows 8.1 / RT 8.1 and later the default build declares precisely what it always did.
        /// ⚠ NOT on Windows 7, 8.0 and RT 8.0, which lack that export: 1.x's call threw, was swallowed, and the app
        ///   ran DPI-UNAWARE there by accident. From 2.0 System falls back to SetProcessDPIAware, so those systems
        ///   become system-aware like every other — no change at 100%, the "mixed look" (and the login's
        ///   larger-UI rescue) at 125%+. Deliberate, untested on Windows 7 (review 1.1 asked for this to be said).
        /// The per-monitor modes prefer the Windows 10 context API, the only one that knows v2.</summary>
        private static string Declare(string want, bool spdac, bool spda, bool spdaw)
        {
            var log = new StringBuilder();
            switch (want)
            {
                case "pmv2":
                    if (spdac && Context(-4, log)) return log.ToString();
                    goto case "pmv1";   // v2 unavailable or refused: v1 is the same semantics minus the extras
                case "pmv1":
                    if (spdac && Context(-3, log)) return log.ToString();
                    if (spda && Shcore(2, log)) return log.ToString();
                    log.Append("per-monitor unavailable -> ");
                    goto case "system";   // RT 8.0 / Windows 7: no per-monitor story at all
                case "system":
                    if (spda && Shcore(1, log)) return log.ToString();
                    if (spdac && Context(-2, log)) return log.ToString();
                    if (spdaw) { log.Append("SetProcessDPIAware=" + YesNo(SetProcessDPIAware())); return log.ToString(); }
                    log.Append("nothing to call (unaware)");
                    return log.ToString();
                default:   // unaware
                    if (spda && Shcore(0, log)) return log.ToString();
                    if (spdac && Context(-1, log)) return log.ToString();
                    log.Append("none needed (unaware is the default)");
                    return log.ToString();
            }
        }

        private static bool Shcore(int value, StringBuilder log)
        {
            int hr = SetProcessDpiAwareness(value);
            log.Append("SetProcessDpiAwareness(" + value + ")=0x" + hr.ToString("X8") + (hr == E_ACCESSDENIED ? "(already set)" : "") + " ");
            return hr == 0;
        }

        private static bool Context(int ctx, StringBuilder log)
        {
            bool ok = SetProcessDpiAwarenessContext(new IntPtr(ctx));
            log.Append("SetProcessDpiAwarenessContext(" + ctx + ")=" + YesNo(ok) + (ok ? "" : "(err " + Marshal.GetLastWin32Error() + ")") + " ");
            return ok;
        }

        private static string ReadBack(bool gtdac, bool gpda)
        {
            try
            {
                if (gtdac)
                {
                    IntPtr ctx = GetThreadDpiAwarenessContext();
                    int a = GetAwarenessFromDpiAwarenessContext(ctx);
                    if (a == 2 && Has("user32.dll", "AreDpiAwarenessContextsEqual") && AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4)))
                        return "per-monitor-v2";
                    return Name(a);
                }
                if (gpda)
                {
                    int v;
                    if (GetProcessDpiAwareness(IntPtr.Zero, out v) == 0) return Name(v);
                }
                if (Has("user32.dll", "IsProcessDPIAware")) return IsProcessDPIAware() ? "system" : "unaware";
            }
            catch { }
            return "unknown";
        }

        private static string Name(int awareness)
        {
            return awareness == 0 ? "unaware" : awareness == 1 ? "system" : awareness == 2 ? "per-monitor" : "unknown(" + awareness + ")";
        }

        private static int ReadSystemDpi()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    int dpi = GetDeviceCaps(dc, LOGPIXELSX);
                    ReleaseDC(IntPtr.Zero, dc);
                    if (dpi > 0) return dpi;
                }
            }
            catch { }
            return Logical;
        }

        /// <summary>"1920x1080@144*" per monitor (* = primary), AS THIS PROCESS SEES THEM. ⚠ Only a per-monitor-aware
        /// process sees the truth: measured on the dev box, a 250% secondary monitor reads "1944x1296@144" to a
        /// system-aware process (size and DPI virtualized to the system DPI) and "3240x2160@240" under PMv1.</summary>
        private static string Monitors(bool shcore)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var s in Screen.AllScreens)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(s.Bounds.Width).Append('x').Append(s.Bounds.Height);
                    if (shcore && Has("shcore.dll", "GetDpiForMonitor"))
                    {
                        var pt = new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
                        uint dx, dy;
                        if (GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out dx, out dy) == 0) sb.Append('@').Append(dx);
                    }
                    if (s.Primary) sb.Append('*');
                }
            }
            catch (Exception ex) { sb.Append("unreadable: " + ex.GetType().Name); }
            return sb.ToString();
        }

        private static string OsVersion()
        {
            try
            {
                var v = new OSVERSIONINFOW { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOW)) };
                if (RtlGetVersion(ref v) == 0) return v.dwMajorVersion + "." + v.dwMinorVersion + "." + v.dwBuildNumber;
            }
            catch { }
            return Environment.OSVersion.Version.ToString();
        }

        private static string YesNo(bool b) { return b ? "yes" : "no"; }

        private static int _hasWindowDpi = -1, _hasMonitorDpi = -1;

        /// <summary><see cref="Has"/>, asked once: ForWindow runs on every resize and activation (GetModuleHandle plus
        /// GetProcAddress each time, before step 3.1).</summary>
        private static bool HasCached(ref int flag, string dll, string fn)
        {
            if (flag < 0) flag = Has(dll, fn) ? 1 : 0;
            return flag == 1;
        }

        /// <summary>True when <paramref name="dll"/> exists and exports <paramref name="fn"/>. Lets an absent API be
        /// told apart from a failing one, and keeps every call below from ever throwing EntryPointNotFound.</summary>
        internal static bool Has(string dll, string fn)
        {
            try
            {
                IntPtr m = GetModuleHandle(dll);
                if (m == IntPtr.Zero) m = LoadLibrary(dll);
                return m != IntPtr.Zero && GetProcAddress(m, fn) != IntPtr.Zero;
            }
            catch { return false; }
        }

        // ── interop ──────────────────────────────────────────────────────────────────────────────────────────
        private const int LOGPIXELSX = 88;
        private const int E_ACCESSDENIED = unchecked((int)0x80070005);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int value);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool IsProcessDPIAware();
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        }
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OSVERSIONINFOW v);
    }
}
