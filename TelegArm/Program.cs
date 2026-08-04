using System;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI;

namespace TelegArm
{
    static class Program
    {
        /// <summary>BATCH-TA-13/W1: minimum WTelegramClient log severity teed into our log. WTC uses 1..5.
        /// Levels 1-2 are PER-PACKET ("Sending/Receiving …") — measured on a quiet 40 s run, floor 1 emitted
        /// 131 library lines and took the session log from 137 to 213 lines with no chat activity at all.
        /// Level 3+ is the exceptional traffic we actually want: reactor faults, alt-DC disconnects, duplicate
        /// /old msg_id (dedupe), server-salt changes, RpcErrors. Raising the volume distorts the [BOOT] and
        /// [PERF] numbers read from this same file, so LOWERING THIS NEEDS A WRITTEN REASON.</summary>
        private const int WtcLogSeverityFloor = 3;

        /// <summary>Application version (Major.Minor.Build), read from the assembly so AssemblyInfo is the ONE
        /// source of truth. Shown in the login title + About screen, and reported to Telegram as app_version.</summary>
        public static readonly string Version = ReadVersion();

        private static string ReadVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? v.Major + "." + v.Minor + "." + v.Build : "1.0.0";
            }
            catch { return "1.0.0"; }
        }

        /// <summary>Physical system DPI captured at startup (96 = 100%). The login screen reads this to offer the
        /// "switch to larger UI" (DPI-unaware) rescue on a scaled display. Stays 96 until the [DPI] probe runs
        /// (and reads 96 when already unaware, since the process is DPI-virtualized → the rescue then hides).</summary>
        public static int SystemDpi { get; private set; } = 96;

        // PROCESS_SYSTEM_DPI_AWARE = 1. MaterialSkin.2 does not support
        // per-monitor DPI, so we deliberately use system-DPI awareness.
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int GetProcessDpiAwareness(IntPtr hProcess, out int value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;

        /// <summary>
        /// The main entry point for the application. Checks for an existing
        /// session file and shows MainForm if present, otherwise LoginForm.
        /// </summary>
        // Machine-wide so two instances can't run even across Windows users / RDP
        // sessions — concurrent clients on one session trigger AUTH_KEY_DUPLICATED,
        // which makes Telegram revoke the session.
        private const string MutexName = @"Global\TelegArm_SingleInstance";

        // ── SINGLE-INSTANCE ACTIVATE-EXISTING ─────────────────────────────────────────────────────────────
        // A second launch must NOT start a second Client. Instead it broadcasts a RegisterWindowMessage that the
        // already-running MainForm listens for and surfaces itself, then exits silently (HWND-find-free; both
        // instances register the same system-unique id). See MainForm.WndProc + Program.ActivateExistingInstance.
        internal static readonly int WM_ShowExisting = RegisterWindowMessage("TelegArm_ShowExisting");
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        /// <summary>STARTUP-SETTING: true when launched with "--startup" (the HKCU Run-key auto-start at login) →
        /// MainForm starts minimized to the tray (silent, Telegram-Desktop style) instead of a full window.</summary>
        internal static bool StartupLaunch;
        private const int ASFW_ANY = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [STAThread]
        static void Main()
        {
            // NOTE (COMPOSER-IMM-SEVER): the ImmDisableTextFrameService(0xFFFFFFFF) startup call that lived here
            // was REMOVED — it's an obsolete XP-era API that returns FALSE (no-op) on modern Windows incl. RT 8.1.
            // The per-control ImmAssociateContext sever that replaced it was itself proven INERT on RT and also
            // removed — see the dead-ends note above EditInputProbe in MainForm.cs (~L218): the productized input
            // shim (EditInputProbe) is what carries composer input in the CUAS dead mode.

            // A DPI-rescue self-restart (login screen) relaunches with "--restarted"; that path WAITS for the
            // outgoing instance to release the mutex (below) and, if the old instance somehow survives, exits
            // WITHOUT surfacing it — activate-existing is only for a NORMAL second launch.
            bool restarted = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--restarted", StringComparison.OrdinalIgnoreCase)) restarted = true;
                else if (string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)) StartupLaunch = true;   // STARTUP-SETTING
            }

            Mutex mutex;
            try
            {
                // Permissive ACL so the mutex is openable from any user account;
                // otherwise a second user would get UnauthorizedAccessException.
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl, AccessControlType.Allow));
                mutex = new Mutex(false, MutexName, out _, security);
            }
            catch (UnauthorizedAccessException)
            {
                // Exists but we can't open it → another instance owns it. Surface it (normal launch), then exit.
                if (!restarted) ActivateExistingInstance();
                return;
            }

            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(restarted ? TimeSpan.FromSeconds(6) : TimeSpan.Zero, false); }
                catch (AbandonedMutexException) { acquired = true; } // prior owner crashed

                if (!acquired)
                {
                    // Another instance holds the session — don't start a second Client (AUTH_KEY_DUPLICATED).
                    // Bring the existing window to the front instead of the old popup, then exit silently.
                    if (!restarted) ActivateExistingInstance();
                    return;
                }

                try { Run(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>A second launch found the single-instance mutex held. Ask the running instance to surface its
        /// window (it restores from tray/minimize via its own flicker-safe path), then exit silently — never a
        /// popup, never a second Client. Best-effort: any failure just falls through to a silent exit.</summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);   // let the running instance steal foreground from us
                if (WM_ShowExisting != 0) PostMessage(HWND_BROADCAST, WM_ShowExisting, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* silent — the single-instance guarantee holds regardless */ }
        }

        private static void Run()
        {
            // BATCH-TA-0: start the cold-start clock before anything else, so every [BOOT] rung below is
            // measured from the same origin. Emitting can only begin once FileLog.Init has registered the
            // tee (a few lines down) — Logger.Diag simply drops anything raised before that.
            PerfLog.StartBoot();

            // Crash capture FIRST — always on, independent of the diagnostic-logging toggle. Records to
            // crash.log (Documents-first) from all three unhandled-exception surfaces; zero cost until a crash.
            try { CrashLog.Install(); } catch { /* crash capture must never block startup */ }

            // File logging next — tee every trace to Documents\TelegArm\telegarm.log so the earliest startup
            // traces (native load / VLC extract / fonts) are captured on RT, where DebugView can't run.
            // Default OFF: only the session header is written and no handle is held (Settings→Advanced toggles).
            try { FileLog.Init(AppSettings.Instance.FileLogging); } catch { /* logging must never block startup */ }

            // BATCH-TA-13/W1 — tee WTelegramClient's OWN log into ours. Until now the library was a black box:
            // TA-12 established from its source (D:\repo\WTelegramClient @ telegarm/shipped-4.4.6) that a
            // FLOOD_WAIT of up to FloodRetryThreshold (default 60) is absorbed as a silent Task.Delay inside
            // the awaited call (src/Client.cs:1623-1630), and that reactor reconnects retry on a fixed 5 s
            // timer and only surface every MaxAutoReconnects'th time (src/Client.cs:388-393). Neither produced
            // a single line in our logs, so on the device both look exactly like a hang. This also makes WTC's
            // update dedupe, gap recovery and server-salt renegotiation visible — all of which TA-7 had to
            // infer from the outside.
            //
            // ⚠ WTelegram.Helpers.Log is a STATIC, PROCESS-WIDE hook — set ONCE here, never per-client.
            //   Reassigning it on each account switch would race the warm-connection pool.
            // ⚠ CONSEQUENCE, ACCEPTED NOT FIXED: with warm multi-account clients live at once, lines from
            //   several Client instances INTERLEAVE with no account id. WTC's own text usually carries a DC
            //   hint ("4>Sending …") and that is all the attribution there is. Threading account state through
            //   a static hook is out of scope here.
            // R7: Logger.Diag (Trace) so it survives Release — the only build that ships to the device.
            try
            {
                WTelegram.Helpers.Log = (level, line) =>
                {
                    if (!Logger.Enabled || level < WtcLogSeverityFloor || line == null) return;
                    Logger.Diag("[WTC:" + level + "] " + line);
                };
            }
            catch { /* observability must never block startup */ }
            // Re-log the resolved cache root now that the file listener is up (the normalizer's own line fires
            // during AppSettings load, before FileLog registered) — so the RT log shows which root won.
            try { System.Diagnostics.Debug.WriteLine("[CACHE] active cache root = " + AppSettings.Instance.MediaCacheFolder); } catch { }
            PerfLog.Boot("Program.Run (logging up)");

            // DPI: declared HERE at runtime (no manifest), TOGGLE-CONTROLLED as of v0.9.0 (DPI-MODE-TOGGLE):
            //  • DEFAULT (DpiUnaware=false): SYSTEM-aware — sharp everywhere, but on a scaled display
            //    point-fonts scale while raw-pixel layout doesn't (the mixed look). The historical rationale
            //    stands: MaterialSkin.2 has no per-monitor support (per-monitor + manual math double-scales;
            //    see LESSONS_LEARNED), and RT 8.1 @100% renders pixel-identical in both modes.
            //  • OPT-IN (Settings→Advanced "Proportional scaling"): DPI-UNAWARE — the DWM stretches the whole
            //    UI uniformly on scaled displays (proportional, slightly blurry). The REAL fix is the
            //    INTERFACE-SCALE internal metrics slider (Telegram-Desktop style) — BANKED for v1.x.
            // INVARIANT: nothing before this call may create UI or read Screen metrics. The settings read
            // below is file-only on every path (AppSettings.Load + StoragePaths probe files, never UI/Screen
            // — verified DPI-MODE-TOGGLE 0.1; AppSettings.Instance already resolves above for FileLog.Init).
            // Must run before any window is created.
            try
            {
                if (AppSettings.Instance.DpiUnaware)
                    SetProcessDpiAwareness(0);   // opt-in: proportional (unaware — uniform DWM stretch)
                else
                    SetProcessDpiAwareness(1);   // DEFAULT: system-aware (today's exact call, unchanged)
            }
            catch { /* shcore unavailable */ }
            // DPI truth line (permanent): the EFFECTIVE awareness + system DPI + the touch threshold that
            // derives from it — a manifest, compat-flag layer, or library call can silently override the
            // declaration above, and that class of regression must be diagnosable from the log alone.
            try
            {
                int aw;
                if (GetProcessDpiAwareness(IntPtr.Zero, out aw) == 0)
                {
                    int dpi = 96;
                    IntPtr dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero) { dpi = GetDeviceCaps(dc, LOGPIXELSX); ReleaseDC(IntPtr.Zero, dc); }
                    SystemDpi = dpi;   // exposed to the login DPI-rescue affordance (aware → real DPI; unaware → 96)
                    System.Diagnostics.Debug.WriteLine("[DPI] awareness=" + (aw == 0 ? "unaware" : aw == 1 ? "system" : "per-monitor")
                        + " systemDpi=" + dpi
                        + " moveThreshold=" + TelegArm.UI.Controls.TouchScroller.MoveThresholdPx + "px");
                }
            }
            catch { /* shcore unavailable (RT 8.1 has it; pre-8.1 would not) */ }

            // Point the native-DLL search at the matching per-arch folder (rlottie x86 / ARM32).
            NativeLibraries.Init();

            // Surface otherwise-silent startup/UI-thread crashes to the USER. Application.ThreadException is a
            // SINGLE-SLOT event (adding a handler REPLACES the previous one — it does not multicast), so this
            // one handler must ALSO do the crash.log recording (record first, then the dialog; app continues).
            // AppDomain.UnhandledException IS multicast — CrashLog.Install already records there.
            Application.ThreadException += (s, e) =>
            {
                CrashLog.Record("Application.ThreadException (UI thread)", e.Exception);
                MessageBox.Show(e.Exception.ToString(), "TelegArm — unexpected error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                MessageBox.Show((e.ExceptionObject as Exception)?.ToString() ?? "Unknown error",
                    "TelegArm — fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Pop the on-screen keyboard when a text field is tapped by touch on a keyboard-less tablet.
            Application.AddMessageFilter(new TouchKeyboard.Filter());

            // Prune old cached media in the background (at most once a day); never blocks the UI.
            var settings = AppSettings.Instance; // force init on the UI thread first

            // Apply the persisted theme mode before any window reads the theme.
            ThemeMode mode;
            if (!Enum.TryParse(settings.ThemeMode, true, out mode)) mode = ThemeMode.System;
            ThemeHelper.InitMode(mode);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (DateTime.Now - settings.LastCacheCleanup > TimeSpan.FromDays(1))
                    {
                        MediaCache.DeleteOlderThan(settings.MediaCacheFolder, settings.MediaCacheRetentionDays);
                        settings.LastCacheCleanup = DateTime.Now;
                        settings.Save();
                    }
                }
                catch { /* cleanup is best-effort */ }
            });

            AccountStore.Init();   // read the active-account pointer / detect a legacy session before resuming
            PerfLog.Boot("AccountStore.Init done → VlcBootstrap.EnsureReady");

            // Extract the bundled, matching-arch libVLC on first run (one-time) so audio/video work without
            // the user hand-placing a libvlc/ folder. Later launches skip it (version marker). Never throws.
            VlcBootstrap.EnsureReady();
            // BATCH-TA-0: bracketed deliberately. This call is SYNCHRONOUS and runs BEFORE any window exists, so
            // whatever it costs is dead time on a blank screen. VlcBootstrap's own [VLC] traces are Debug-only
            // (stripped in Release), so without this rung the cost is invisible on the installed build — which is
            // the only build that matters on RT, where the extract is slowest.
            PerfLog.Boot("VlcBootstrap.EnsureReady done");

            var service = new TelegramService();

            PerfLog.Boot("pre-UI done (vlc+accounts+settings) → Application.Run");
            if (TelegramService.SessionExists)
                Application.Run(new MainForm(service, StartupLaunch));   // STARTUP: silent tray start when launched with --startup
            else
                Application.Run(new LoginForm(service));
        }
    }
}
