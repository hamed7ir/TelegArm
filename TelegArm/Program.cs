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
        /// <summary>Minimum WTelegramClient log severity teed into our log. WTC uses 1..5.
        ///
        /// BATCH-TA-13/W1 set this to 3. BATCH-TA-16b/F2 LOWERED IT TO 2, and the reason is written down
        /// here because that comment demanded one. Measured on this box, all three points:
        ///   · floor 1 — 131 library lines on a QUIET 40 s run, taking the session log 137 → 213 lines with
        ///     no chat activity at all. Level 1 is per-packet ("Sending/Receiving …"). Far too loud; it
        ///     distorts the [BOOT] and [PERF] numbers read from this same file.
        ///   · floor 2 — 6 lines in 167 on the same quiet run. Cheap, and it is the level that carries the
        ///     ONE line that matters most for diagnosing a proxy on the device:
        ///         "Connecting to DC {n} via MTProxy {server}:{port}..."   (src/Client.cs:896, level 2)
        ///     plus "Connecting to {endpoint}..." / "Connected to DC {n}..." for the direct path. At floor 3
        ///     all of those are invisible, which is precisely the black box W1 existed to open.
        ///   · floor 3 — 0 lines on a clean connect; only faults (4/5) ever appear.
        ///
        /// ⚠ CONFIRM ON THE DEVICE TRIP THAT THIS STAYS QUIET OVER A FLAKY LINK. The 6-line figure comes
        /// from a HEALTHY connection. A real proxy run on 2026-08-04 showed the reactor dropping and
        /// reconnecting roughly every 5-10 s ("[WTC:5] An exception occured in the reactor: Could not read
        /// payload length : Connection shut down"), and at floor 2 EACH of those reconnects also emits its
        /// own level-2 "Connecting to …" line. On a bad link the volume therefore scales with reconnect
        /// churn, not with time. If the device log turns into a reconnect transcript, raise this back to 3.</summary>
        private const int WtcLogSeverityFloor = 2;

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

        /// <summary>The EFFECTIVE awareness read back after the declaration — a wrapper over <see cref="Dpi.Mode"/>.</summary>
        internal static string DpiAwareness { get { return Dpi.Mode; } }

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

        /// <summary>BATCH-TA-37/A4 — true when COM launched us to serve a toast activation (-Embedding).
        /// ⚠ THE ONLY THING THIS CHANGES is what happens when the single-instance mutex is NOT acquired.
        /// Every path with -Embedding ABSENT behaves exactly as before; that is deliberate, because this
        /// file's job is preventing two live clients on one session (AUTH_KEY_DUPLICATED), and the diff
        /// has to make that provable by reading it.</summary>
        internal static bool ComEmbedding;

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
                // BATCH-TA-37/A4a — COM passes -Embedding (or /Embedding) when it launches a LocalServer32
                // for an activation. It means "you were started to serve a click", not "the user ran you".
                else if (string.Equals(arg, "-Embedding", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(arg, "/Embedding", StringComparison.OrdinalIgnoreCase)) ComEmbedding = true;
            }

            // STEP 0.1 — the hidden "--shoot <dir>" screenshot rig. Taken BEFORE the single-instance mutex on
            // purpose: it never touches a real account (RunShoot isolates every writable root first) and never
            // shows a window, so it may run beside a live TelegArm without either one noticing the other.
            string shootDir = ShotRig.ParseDir(Environment.GetCommandLineArgs());
            if (shootDir != null) { RunShoot(shootDir); return; }

            // STEP 1.3 — the design gallery (--gallery) and its real-window chrome probe (--gallery-probe <file>).
            // Same rules as --shoot: before the mutex, storage isolated, never a Telegram client.
            string probe = null; bool gallery = false, inputLab = false;
            var cmd = Environment.GetCommandLineArgs();
            for (int i = 1; i < cmd.Length; i++)
            {
                if (string.Equals(cmd[i], "--gallery", StringComparison.OrdinalIgnoreCase)) gallery = true;
                // Device trip 2026-10: the typing A/B lab — same isolation as the gallery (UI/InputLabForm.cs).
                else if (string.Equals(cmd[i], "--input-lab", StringComparison.OrdinalIgnoreCase)) { gallery = true; inputLab = true; }
                else if (string.Equals(cmd[i], "--gallery-probe", StringComparison.OrdinalIgnoreCase))
                { gallery = true; probe = i + 1 < cmd.Length ? System.IO.Path.GetFullPath(cmd[i + 1]) : System.IO.Path.GetFullPath("gallery-probe.txt"); }
            }
            if (gallery) { RunGallery(probe, inputLab); return; }

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
                    // ⚠ BATCH-TA-37/A4c — A COM ACTIVATION LAUNCH MUST NOT SURFACE THE WINDOW.
                    //   COM launched us because a toast was clicked, but an instance is already running (or
                    //   starting) and IT owns the registered class object, so IT will serve the activation.
                    //   Broadcasting WM_ShowExisting here would raise that instance's window with NO chat
                    //   opened — the user clicks a notification and gets the wrong thing. Exit quietly and
                    //   let the owner deliver.
                    //   ⚠ BUT NEVER SILENTLY: this is the exact race that makes a click do nothing, so it
                    //     leaves a line behind. A silent return here is unanswerable from a log.
                    if (ComEmbedding)
                    {
                        Helpers.Logger.Diag("[SHELL] activation dropped, instance starting — "
                                            + "another process owns the class object; not surfacing a window");
                        return;
                    }

                    // Another instance holds the session — don't start a second Client (AUTH_KEY_DUPLICATED).
                    // Bring the existing window to the front instead of the old popup, then exit silently.
                    if (!restarted) ActivateExistingInstance();
                    return;
                }

                // ⚠ BATCH-TA-37/A4b — REGISTER THE CLASS OBJECT **HERE**, the moment the mutex is ours and
                //   before Run() does anything heavy. When the app was launched BY a click, COM is already
                //   blocked waiting for this registration; TelegArm's cold start does session and network
                //   work, and registering after that races CoCreateInstance's timeout — the click would
                //   silently do nothing.
                //   ⚠ A7 — ShellNotify.Init() decides Available, which requires a Start-Menu shortcut
                //     carrying our AUMID. A portable copy therefore never registers, and its existing
                //     "[SHELL] Action Center OFF" line already explains the absence.
                Helpers.ShellNotify.Init();
                if (Helpers.ShellNotify.Available) Helpers.ToastActivator.Register();
                else if (ComEmbedding)
                    Helpers.Logger.Diag("[SHELL] launched for activation but Action Center is unavailable — "
                                        + Helpers.ShellNotify.Reason);

                try { Run(); }
                finally
                {
                    Helpers.ToastActivator.Revoke();   // A4e — never leave a class object for a dead process
                    mutex.ReleaseMutex();
                }
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

            // ⚠ TA-37 FIX — REPLAY THE [SHELL] LINES Init() RAISED BEFORE THIS POINT.
            //   ShellNotify.Init() runs in Main(), ahead of Run(), so COM's activation timeout is met —
            //   but that is BEFORE the line above registers the trace tee, and Logger.Diag drops anything
            //   raised earlier. The device showed the result: Action Center working and NOT ONE [SHELL]
            //   line in the log, which made the live-tile question undiagnosable, because "no log line"
            //   and "the code never ran" are indistinguishable.
            try { Helpers.ShellNotify.ReplayLog(); } catch { }

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

            // DPI: declared HERE at runtime (no manifest) by Dpi.Init — see Helpers/Dpi.cs for the modes, the probe
            // chain and why. The build default is still SYSTEM-aware (1.x's exact call on 8.1+; see Dpi.Declare for Win7/8.0) until redesign
            // step 3.7: per-monitor is gated on the AppForm/WindowDpi work of steps 1.3–3.x being finished.
            // Settings → Advanced "Proportional scaling" = DpiMode "Unaware".
            // INVARIANT: nothing before this call may create UI or read Screen metrics. The settings read
            // below is file-only on every path (AppSettings.Load + StoragePaths probe files, never UI/Screen
            // — verified DPI-MODE-TOGGLE 0.1; AppSettings.Instance already resolves above for FileLog.Init).
            // Must run before any window is created.
            DeclareDpiAwareness();

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

            // BATCH-TA-33 — set the AUMID and probe Action Center / tile support ONCE, before any window.
            // It must run early: the AUMID also decides taskbar grouping, and Windows reads it when the
            // first window is created. Logs exactly one line saying which branch was taken, because on RT
            // 8.1 and Windows 7 this whole feature correctly does nothing and "nothing" needs a reason.
            TelegArm.Helpers.ShellNotify.Init();

            // ⚠ NO TabTip launcher for text boxes (device result 2026-10-14, alpha.2 Input Lab on the Surface RT): TelegArm
            //   starting TabTip.exe itself on a touch tap (the old TouchKeyboard.Filter) KILLED typing in every native EDIT —
            //   MaterialSkin's and a plain TextBox alike — the "dead input" of 1.x. Windows opens the keyboard for an EDIT on
            //   its own (Finestra relies on that and types fine). Only a control that takes WM_CHAR itself (the new composer)
            //   may call TouchKeyboard.Show(); it is immune.

            // Prune old cached media in the background (at most once a day); never blocks the UI.
            var settings = AppSettings.Instance; // force init on the UI thread first

            // Apply the persisted theme mode before any window reads the theme.
            ThemeMode mode;
            if (!Enum.TryParse(settings.ThemeMode, true, out mode)) mode = ThemeMode.System;
            ThemeHelper.InitMode(mode);
            // Step 1.2 — the user's accent (or TELEGARM_ACCENT) BEFORE anything reads it: VlcBootstrap below does.
            InitAccent(settings);

            // Step 1.2 — the theme listener is process-wide now (it was MainForm's, so the login screen never
            // followed a live Windows change, and a logout-to-login stopped it). Installed on THIS thread's WinForms
            // context first: SystemEvents raises on the context it captures, and before any Control exists there
            // is none — ThemeChanged would arrive on a background thread.
            if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ThemeHelper.StartListening();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (DateTime.Now - settings.LastCacheCleanup > TimeSpan.FromDays(1))
                    {
                        // BATCH-TA-3/D1 — SECOND GUARD, deliberately duplicated. DeleteOlderThan already
                        // refuses days <= 0, but this call site is the one that runs UNATTENDED once a day
                        // against the whole cache root, so the intent is stated where the damage would have
                        // happened rather than only inside the callee. Retention 0 = keep forever.
                        int retain = settings.MediaCacheRetentionDays;
                        if (retain <= 0)
                        {
                            Logger.Diag("[CACHE-PRUNE] daily job SKIPPED — retention=" + retain + " means keep forever");
                        }
                        else
                        {
                            string refusal = MediaCache.PruneRefusalReason(settings.MediaCacheFolder);
                            if (refusal != null)
                                Logger.Diag("[CACHE-PRUNE] daily job REFUSED folder=\"" + (settings.MediaCacheFolder ?? "") + "\" reason=" + refusal);
                            else
                                MediaCache.DeleteOlderThan(settings.MediaCacheFolder, retain);
                        }
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
            ThemeHelper.StopListening();
        }

        /// <summary>Step 1.2 — the accent override: TELEGARM_ACCENT=#RRGGBB (testing) wins over the AccentColor
        /// setting; neither set (or invalid) = follow the Windows accent, exactly as 1.x did.</summary>
        private static void InitAccent(AppSettings settings)
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable("TELEGARM_ACCENT"); } catch { }
            var accent = ThemeHelper.ParseAccent(env) ?? ThemeHelper.ParseAccent(settings.AccentColor);
            ThemeHelper.InitAccent(accent);
            Logger.Diag("[ACCENT] " + (accent.HasValue
                ? "fixed " + ThemeHelper.FormatAccent(accent) + (ThemeHelper.ParseAccent(env).HasValue ? " (TELEGARM_ACCENT)" : " (setting)")
                : "follows Windows"));

            // Step 5.2: the base colours, the same way (TELEGARM_LIGHTBASE / TELEGARM_DARKBASE = #RRGGBB for the rig).
            string envLight = null, envDark = null;
            try { envLight = Environment.GetEnvironmentVariable("TELEGARM_LIGHTBASE"); envDark = Environment.GetEnvironmentVariable("TELEGARM_DARKBASE"); } catch { }
            var light = ThemeHelper.ParseAccent(envLight) ?? ThemeHelper.ParseAccent(settings.LightBase);
            var dark = ThemeHelper.ParseAccent(envDark) ?? ThemeHelper.ParseAccent(settings.DarkBase);
            ThemeHelper.InitBases(light, dark);
            ThemeHelper.InitOutBubble(ThemeHelper.ParseAccent(settings.Theme != null ? settings.Theme.OutBubble : null));   // step 5.7
            WindowBackdrop.Init(settings.WindowBackground);   // step 5.9 (TELEGARM_WALLPAPER forces it on for the rig)

            // Steps 5.4/5.5, for the rig: TELEGARM_CHATBG = preset:N | solid:#RRGGBB | gradient:#A,#B,angle | pattern:dots|grid|lines
            // | photo:<path> sets BOTH modes' chat background in memory (photo runs the real import). Not saved by itself.
            string bg = null;
            try { bg = Environment.GetEnvironmentVariable("TELEGARM_CHATBG"); } catch { }
            if (!string.IsNullOrEmpty(bg) && settings.Theme != null)
            {
                try
                {
                    int colon = bg.IndexOf(':');
                    string kind = colon > 0 ? bg.Substring(0, colon).Trim().ToLowerInvariant() : bg.Trim().ToLowerInvariant(), arg = colon > 0 ? bg.Substring(colon + 1).Trim() : "";
                    foreach (bool d in new[] { false, true })
                    {
                        var cs = settings.Theme.For(d);
                        var parts = arg.Split(',');
                        if (kind == "preset") { cs.Mode = "preset"; cs.Preset = int.Parse(arg); }
                        else if (kind == "solid") { cs.Mode = "solid"; cs.Color1 = arg; }
                        else if (kind == "gradient") { cs.Mode = "gradient"; cs.Color1 = parts[0]; cs.Color2 = parts.Length > 1 ? parts[1] : parts[0]; cs.Angle = parts.Length > 2 ? int.Parse(parts[2]) : 0; }
                        else if (kind == "pattern") { cs.Mode = "pattern"; cs.Pattern = arg; }
                        else if (kind == "photo") { cs.Mode = "photo"; cs.ImageFile = ChatBackdrop.ImportPhoto(arg, d); cs.ImageRev++; }
                        cs.Repair();
                    }
                    Logger.Diag("[CHATBG] TELEGARM_CHATBG=" + bg);
                }
                catch (Exception ex) { Logger.Diag("[CHATBG] TELEGARM_CHATBG not understood: " + ex.Message); }
            }
            if (light.HasValue || dark.HasValue)
                Logger.Diag("[ACCENT] base light=" + (light.HasValue ? ThemeHelper.FormatAccent(light) : "default")
                            + " dark=" + (dark.HasValue ? ThemeHelper.FormatAccent(dark) : "default"));
        }

        /// <summary>The one DPI declaration, shared by Run() and the --shoot rig so both declare exactly the same thing
        /// before any form exists. Dpi.Init logs the [DPI] probe summary; the touch threshold derives from the DPI
        /// just declared, so it is logged here, after it (DPI truth line: a manifest, a compat shim or a library can
        /// silently override the declaration, and that must be diagnosable from the log alone).</summary>
        private static void DeclareDpiAwareness()
        {
            Dpi.Init();
            Logger.Diag("[DPI] touch moveThreshold=" + TelegArm.UI.Controls.TouchScroller.MoveThresholdPx + "px");
        }

        private static void ProbeCrashed(string file, string where, Exception ex)
        {
            try { System.IO.File.AppendAllText(file, "FAIL  probe crashed (" + where + "): " + ex + Environment.NewLine, new System.Text.UTF8Encoding(true)); } catch { }
            Logger.Diag("[PROBE] crashed (" + where + "): " + ex);
            Environment.Exit(98);
        }

        /// <summary>STEP 1.3 — the design gallery, with the same isolation and startup as the screenshot rig. With a
        /// probe file it runs the real-window chrome checks and exits with the number of failures.</summary>
        private static void RunGallery(string probeFile, bool inputLab = false)
        {
            string iso = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                "TelegArm-gallery-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            StoragePaths.UseIsolatedRoot(iso);
            DeclareDpiAwareness();
            InitAccent(AppSettings.Instance);
            NativeLibraries.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (probeFile != null)
            {
                // A probe that dies must say where and why — RunGallery returns before Run() installs the app's handlers,
                // and an intermittent no-report exit was seen. The report is written as it goes (RunProbe); this appends
                // the exception and exits 98.
                // ⚠ BEFORE the synchronization context below: it creates a hidden marshaling control, and
                //   SetUnhandledExceptionMode throws once any control exists on the thread (measured: every run crashed).
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ProbeCrashed(probeFile, "UI thread", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ProbeCrashed(probeFile, "AppDomain", e.ExceptionObject as Exception);
            }
            if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ThemeHelper.StartListening();
            int code = 0;
            if (probeFile != null) code = DesignGalleryForm.RunProbe(probeFile);
            else Application.Run(inputLab ? (Form)new InputLabForm() : new DesignGalleryForm());
            ThemeHelper.StopListening();
            try { System.IO.Directory.Delete(iso, true); } catch { }
            Environment.Exit(code);
        }

        /// <summary>STEP 0.1 — runs the screenshot rig, then exits the process.
        /// ⚠ ORDER IS THE SAFETY: UseIsolatedRoot runs before ANYTHING resolves a path (AppSettings, AccountStore,
        ///   logs, crash.log), and throws if something already did. Everything after it — settings, accounts, the
        ///   forms — sees an empty throwaway folder, never the user's profile.
        /// Deliberately skipped versus Run(): the mutex, ShellNotify/ToastActivator (registry + COM), file logging,
        /// the daily cache prune and VlcBootstrap (writes outside the isolated root). None of them draws anything.</summary>
        private static void RunShoot(string dir)
        {
            string iso = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                "TelegArm-shoot-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            StoragePaths.UseIsolatedRoot(iso);
            DeclareDpiAwareness();
            InitAccent(AppSettings.Instance);   // the isolated settings have none: TELEGARM_ACCENT shoots an accent set
            NativeLibraries.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int code = ShotRig.Run(dir, iso);
            try { System.IO.Directory.Delete(iso, true); } catch { }
            Environment.Exit(code);
        }
    }
}
