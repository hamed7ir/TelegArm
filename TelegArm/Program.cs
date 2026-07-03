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
        /// <summary>Application version (Major.Minor.Build), read from the assembly so AssemblyInfo is the ONE
        /// source of truth. Shown in the login title + About screen, and reported to Telegram as app_version.</summary>
        public static readonly string Version = ReadVersion();

        private static string ReadVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? v.Major + "." + v.Minor + "." + v.Build : "0.9.0";
            }
            catch { return "0.9.0"; }
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

        [STAThread]
        static void Main()
        {
            // NOTE (COMPOSER-IMM-SEVER): the ImmDisableTextFrameService(0xFFFFFFFF) startup call that lived here
            // was REMOVED — it's an obsolete XP-era API that returns FALSE (no-op) on modern Windows incl. RT 8.1.
            // The per-control ImmAssociateContext sever that replaced it was itself proven INERT on RT and also
            // removed — see the dead-ends note above EditInputProbe in MainForm.cs (~L218): the productized input
            // shim (EditInputProbe) is what carries composer input in the CUAS dead mode.

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
                // Exists but we can't open it → another instance owns it.
                ReportAlreadyRunning();
                return;
            }

            using (mutex)
            {
                // A DPI-rescue self-restart (login screen) relaunches with "--restarted"; give the OUTGOING
                // instance a moment to exit and release the mutex instead of the usual zero-wait bounce.
                bool restarted = false;
                foreach (var arg in Environment.GetCommandLineArgs())
                    if (string.Equals(arg, "--restarted", StringComparison.OrdinalIgnoreCase)) { restarted = true; break; }

                bool acquired;
                try { acquired = mutex.WaitOne(restarted ? TimeSpan.FromSeconds(6) : TimeSpan.Zero, false); }
                catch (AbandonedMutexException) { acquired = true; } // prior owner crashed

                if (!acquired)
                {
                    ReportAlreadyRunning();
                    return;
                }

                try { Run(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void ReportAlreadyRunning()
        {
            MessageBox.Show("TelegArm is already running.", "TelegArm",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Run()
        {
            // Crash capture FIRST — always on, independent of the diagnostic-logging toggle. Records to
            // crash.log (Documents-first) from all three unhandled-exception surfaces; zero cost until a crash.
            try { CrashLog.Install(); } catch { /* crash capture must never block startup */ }

            // File logging next — tee every trace to Documents\TelegArm\telegarm.log so the earliest startup
            // traces (native load / VLC extract / fonts) are captured on RT, where DebugView can't run.
            // Default OFF: only the session header is written and no handle is held (Settings→Advanced toggles).
            try { FileLog.Init(AppSettings.Instance.FileLogging); } catch { /* logging must never block startup */ }
            // Re-log the resolved cache root now that the file listener is up (the normalizer's own line fires
            // during AppSettings load, before FileLog registered) — so the RT log shows which root won.
            try { System.Diagnostics.Debug.WriteLine("[CACHE] active cache root = " + AppSettings.Instance.MediaCacheFolder); } catch { }

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

            // Extract the bundled, matching-arch libVLC on first run (one-time) so audio/video work without
            // the user hand-placing a libvlc/ folder. Later launches skip it (version marker). Never throws.
            VlcBootstrap.EnsureReady();

            var service = new TelegramService();

            if (TelegramService.SessionExists)
                Application.Run(new MainForm(service));
            else
                Application.Run(new LoginForm(service));
        }
    }
}
