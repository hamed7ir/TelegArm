using System;
using System.IO;
using Newtonsoft.Json;

namespace TelegArm.Core
{
    /// <summary>Redesign R1 — where the main window was. Position in physical screen pixels (monitors can differ in DPI,
    /// so a "logical" position means nothing); size in LOGICAL (96-dpi) pixels, so it comes back the same on a monitor
    /// of another scale.</summary>
    public sealed class WindowPlacementSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>Redesign step 6.2 — how a connection reaches Telegram (<see cref="AppSettings.Transport"/>).</summary>
    public enum ProxyKind { Direct, MtProto, Socks5, WebSocket }

    /// <summary>
    /// User-configurable media auto-download settings, persisted to
    /// settings.json next to the executable.
    /// </summary>
    public class AppSettings
    {
        // Auto-download policy (see MediaPolicy). Conservative RT defaults: NOTHING heavy auto-downloads —
        // render shows thumbnails only; full media is fetched on explicit tap/open. The user opts in per type.
        public bool AutoDownloadPhotos { get; set; } = false;
        public bool AutoDownloadVideos { get; set; } = false;
        public bool AutoDownloadDocuments { get; set; } = false;
        public bool AutoDownloadVoice { get; set; } = false;
        public bool AutoDownloadAudio { get; set; } = false;
        public bool AutoDownloadGifs { get; set; } = false;
        public bool AutoPlayGifs { get; set; } = true;
        public int MaxAutoDownloadSizeMB { get; set; } = 5;

        /// <summary>WebM video stickers loop as OPAQUE video (libVLC). The RT safety net: OFF instantly reverts
        /// to the static representative-emoji fallback (no decode/blit) — flip it if ARM32 can't sustain the load.</summary>
        public bool AnimateWebmStickers { get; set; } = true;

        // ── Notifications ─────────────────────────────────────────────────────
        /// <summary>Master switch for desktop notifications — the global "silence everything" (BATCH-TA-32/N4).
        ///
        /// ⚠ IT GATES THE **EMIT PATH ONLY**. Unread badges, the tray icon and tooltip, and the chat-list
        /// preview all keep updating: this means "do not interrupt me", not "hide my messages". Do not widen
        /// it — a user who silences notifications still expects to see what arrived when they look.
        /// ★ IT WINS OVER EVERYTHING, MENTIONS INCLUDED. A mention breaks through a PER-CHAT mute ("not this
        /// conversation"); the master switch says "nothing, from anywhere, right now", and an exception to
        /// that would make it not a master switch. See MainForm.MaybeToast for the full rule.
        /// Both delivery channels obey it — the notification window and the legacy tray balloon — because
        /// it is checked before either is chosen.</summary>
        public bool EnableNotifications { get; set; } = true;

        /// <summary>How the composer sends. "Enter" (default) = Enter sends, Shift+Enter newline.
        /// "CtrlEnter" = Ctrl+Enter sends, Enter newline. "AltEnter" = Alt+Enter sends, Enter newline.
        ///
        /// ⚠ SHIFT+ENTER IS *ALWAYS* A NEWLINE, in every mode — it is the one convention every chat app
        /// shares, and a mode where it did something else would be a trap rather than a preference.
        /// Stored as a string rather than an enum so an unrecognised value from a hand-edited or
        /// newer settings.json degrades to the default instead of throwing on deserialize.</summary>
        public string SendKey { get; set; } = "Enter";

        /// <summary>BATCH-TA-33/L6 — show MESSAGE TEXT in notifications and on the Start tile, or only who
        /// sent it. Off means the notification window, the Action Center entry and the tile all read
        /// "Alice Morgan · 3 new messages" instead of the message body.
        ///
        /// ⚠ ONE SETTING FOR ALL THREE SURFACES, ON PURPOSE. "Who may read my messages over my shoulder"
        /// is a single question; two toggles that answer it differently is not a preference, it is a
        /// privacy bug — the user turns off previews, sees the notification obey, and never learns the
        /// tile is still publishing the text to a pinned Start menu. Any future surface that displays
        /// message content outside the app reads THIS flag; do not add a second one.
        /// Default ON: it is what the app did before this setting existed, and silently making everyone's
        /// notifications less useful is not a safe default to spring on an upgrade.</summary>
        public bool NotificationPreviews { get; set; } = true;

        /// <summary>BATCH-TA-27/W7 — ESCAPE HATCH: deliver notifications as the OLD tray balloon
        /// (<c>NotifyIcon.ShowBalloonTip</c>) instead of TelegArm's own notification window.
        ///
        /// ⚠ WHY THIS EXISTS AT ALL, GIVEN THE WINDOW IS THE POINT. The window is written and measured on
        /// an x64 Windows 11 dev box, and the device it has to work on is a Windows RT 8.1 ARM32 Surface
        /// that is not iterated on per batch. A window that misbehaves there — mispainted, mispositioned,
        /// or worst of all stealing focus — must not leave the user with NO notifications at all. Flipping
        /// this to true restores the previous behaviour exactly, with no rebuild.
        /// ⚠ It is EXCLUSIVE, not additive: true ⇒ balloon only, false ⇒ window only. Both at once would
        /// double every notification.
        /// ⚠ REMOVE THIS ONCE THE WINDOW IS DEVICE-PROVEN. It is a temporary safety net, not a feature —
        /// leaving it forever means maintaining two delivery channels for one notification.</summary>
        public bool LegacyTrayBalloon { get; set; } = false;

        // ── Diagnostics ──────────────────────────────────────────────────────
        /// <summary>Diagnostic logging to Documents\TelegArm\telegarm.log (the Settings→Advanced toggle).
        /// Default OFF — with it off no log-purpose string formatting executes on hot paths (Logger.Enabled
        /// gates them) and no file handle is held. Crash capture (crash.log) is ALWAYS on, independent of this.</summary>
        public bool FileLogging { get; set; } = false;

        /// <summary>Shrink the main window above the on-screen keyboard (TabTip) when it appears, so the composer
        /// isn't hidden behind it; restore on hide. Default ON; set false if it misbehaves on a device.</summary>
        public bool ResizeForKeyboard { get; set; } = true;

        /// <summary>Verbose keyboard-input diagnostics: the per-keystroke [KBD] RAW / echo / EMSG / style tracing.
        /// Default OFF — a healthy session logs a near-silent [KBD] stream; ANOMALY lines (forced edits/nav,
        /// caret-yank corrections) and keyboard lifecycle events are ALWAYS logged regardless of this flag.
        /// Flip to true when diagnosing RT input issues.</summary>
        public bool KbdDiag { get; set; } = false;

        /// <summary>WS_EX_COMPOSITED on the chat-list/message hosts (SCROLL-SMOOTH-T1: coherent frames
        /// during scrolls at extra paint cost). Restart to apply. Default TRUE on this build for the A/B
        /// test — the RELEASE default gets decided by the RT A/B numbers.</summary>
        public bool CompositedScroll { get; set; } = true;

        /// <summary>Owner 2026-11-13 ("cant we add animations to make it feel smooth?"): the master switch of the small
        /// animations (Helpers.Anim). ON by default: the owner asked for them. Applied at once (every animation asks when it
        /// starts). Windows' own "Show animations" turns them off too. Off = every state change is instant, as before.</summary>
        public bool Animations { get; set; } = true;

        /// <summary>Animate even when Windows' own "Animation effects" setting is off. OFF by default: that setting is how a
        /// person tells every app they do not want motion. (The dev box has it off, found 2026-11-13: nothing moved there
        /// until this was added.)</summary>
        public bool AnimateDespiteSystem { get; set; } = false;

        /// <summary>On/off switches slide (needs <see cref="Animations"/>). Each kind has its own switch so that one which
        /// misbehaves on a device (the Surface RT has not seen them yet) can be turned off alone.</summary>
        public bool AnimateToggles { get; set; } = true;

        /// <summary>Hover and press colours fade on buttons, rows and menus (needs <see cref="Animations"/>).</summary>
        public bool AnimateHover { get; set; } = true;

        /// <summary>The hamburger menu slides in and out (needs <see cref="Animations"/>).</summary>
        public bool AnimateDrawer { get; set; } = true;

        /// <summary>Popups and menus fade in: the emoji picker, the downloads list, the reaction grid, context menus (needs
        /// <see cref="Animations"/>). The only animation that changes a window style (a layered window while it fades).</summary>
        public bool AnimatePopups { get; set; } = true;

        /// <summary>The message list glides to the bottom, to a replied message, a search result or a mention, and
        /// follows a new message (needs <see cref="Animations"/>). Off = those jumps are instant, as before.</summary>
        public bool AnimateScroll { get; set; } = true;

        /// <summary>In a narrow window (one column), going from the list to a chat and back slides (needs
        /// <see cref="Animations"/>).</summary>
        public bool AnimateColumns { get; set; } = true;

        /// <summary>2026-11-15: look for a new TelegArm release on GitHub in the background (20 s after start, then every 12
        /// hours); a newer one shows an Update pill in the title bar. Settings, About.</summary>
        public bool CheckForUpdates { get; set; } = true;

        /// <summary>FOLDER-SIDEBAR: folder navigation style. false = horizontal TAB bar above the chat list
        /// (default, today's exact layout); true = a vertical icon RAIL left of the chat list (Telegram-Desktop
        /// style). Read ONCE at construction (BuildLeftPanel) — restart to apply, like CompositedScroll, so the
        /// chat-list host's handle is created with its final parent and the touch/scroll invariants are never
        /// disturbed by a live reparent.</summary>
        public bool FolderSidebar { get; set; } = false;

        /// <summary>DPI-MODE-TOGGLE: opt-in DPI-UNAWARE declaration ("proportional scaling") — Windows
        /// stretches the whole UI uniformly on scaled displays (slightly blurry) instead of the system-aware
        /// mixed look (2× fonts over 1× layout). No effect at 100% scale (RT identical either way). Read at
        /// the very top of startup, BEFORE any UI — restart to apply. Default FALSE = today's system-aware.
        /// ⚠ STEP 0.4: A MIRROR NOW — DO NOT WRITE IT. <see cref="DpiMode"/> is the source of truth; Load and
        ///   Save set this to (DpiMode == "Unaware"). It stays in the file only so a 1.9.x build that the user
        ///   rolls back to still sees their choice. A write here is overwritten on the next Save.</summary>
        public bool DpiUnaware { get; set; } = false;

        /// <summary>STEP 0.4 — DPI awareness mode: "" = the BUILD DEFAULT, resolved in code (system-aware until
        /// redesign step 3.7 flips it), or "Auto" / "System" / "Unaware". Stored as "" rather than the default's
        /// name because Save writes every property: an explicit value could never be re-defaulted by a later
        /// build, so "" is how "the user never chose" survives on disk. Read at startup (Program), restart to apply.</summary>
        public string DpiMode { get; set; } = "";

        /// <summary>Redesign step 3.8a — the interface scale, percent: 50-200 in 25% steps, 100 = off. Multiplies the
        /// per-monitor DPI (Dpi.Effective). The slider arrives with 3.8b; until then only the rig and --scale use it.</summary>
        public int InterfaceScale { get; set; } = 100;

        /// <summary>STEP 0.4 — schema version of this file. ⚠ NO INITIALIZER, ON PURPOSE: a 1.x file has no such
        /// field, and Json.NET leaves an absent property at its initializer — "= 2" would make every old file look
        /// already migrated. Absent ⇒ 0 ⇒ <see cref="Migrate"/> runs once.</summary>
        public int SettingsVersion { get; set; }

        /// <summary>The schema this build writes. 2 = the 2.x line (adds DpiMode).</summary>
        internal const int CurrentSettingsVersion = 2;

        /// <summary>ACCOUNT-SWITCH STEP 1: keep every NON-active account connected in the background (a headless warm
        /// WTC client per account, ~1 MB each) so switching REUSES the live connection (skips the MTProto handshake).
        /// Warm clients are silent (no notifications yet — STEP 2). Adoption falls back to a normal connect on any
        /// failure. Default TRUE; set this flag FALSE (persisted setting) to disable if it ever misbehaves on RT.</summary>
        public bool WarmConnections { get; set; } = true;

        // ── Proxy (BATCH-TA-16) ──────────────────────────────────────────────
        // Additive: absent in an existing settings.json = these defaults, so no migration.
        /// <summary>MTProxy on/off. FALSE (default) = connect directly, exactly as before this existed.</summary>
        public bool ProxyEnabled { get; set; } = false;

        /// <summary>Saved MTProxy links, normalised to tg://proxy?server=..&amp;port=..&amp;secret=.. by
        /// <see cref="ProxyUrl"/>. Order is the display order.
        /// ⚠ THE SECRET IS STORED IN PLAIN TEXT HERE. That is a deliberate, stated decision (TA-15/X7):
        /// it is what the official desktop clients do, and the session file sitting beside settings.json is
        /// far more sensitive. It is ALSO why the secret must never reach a log — see <see cref="ProxyUrl"/>.</summary>
        public System.Collections.Generic.List<string> ProxyList { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Index into <see cref="ProxyList"/> of the proxy in use; -1 = none selected.
        /// Always bounds-checked on read — a hand-edited settings.json can point anywhere.</summary>
        public int ProxyActive { get; set; } = -1;

        /// <summary>The active proxy link, or null when proxying is off / nothing valid is selected.
        /// This is the ONE place that decides whether we are proxied, so the bounds check lives here
        /// rather than being repeated at every call site.</summary>
        [JsonIgnore]
        public string ActiveProxyUrl
        {
            get
            {
                // step 6.2/6.6: the tunnel, then a SOCKS5 proxy, win over a hand-edited file that also enables this
                if (!ProxyEnabled || WsTunnelEnabled || ActiveSocksUrl != null) return null;
                var list = ProxyList;
                if (list == null || ProxyActive < 0 || ProxyActive >= list.Count) return null;
                string u = list[ProxyActive];
                return string.IsNullOrWhiteSpace(u) ? null : u;
            }
        }

        // ── Window (redesign R1) ─────────────────────────────────────────────
        /// <summary>The main window's normal bounds and maximised flag when it last closed or went to the tray; null =
        /// never saved (first run: centred at the default size). Additive: absent = null.</summary>
        public WindowPlacementSettings MainWindow { get; set; }

        // ── WebSocket tunnel (redesign step 6.2) ─────────────────────────────
        // ⚠ ITS OWN FIELDS, NEVER AN ENTRY IN ProxyList: a 1.9.x build hands every ProxyList entry to WTC's MTProxyUrl,
        //   and a pseudo-link there would fail every connect. Turning the tunnel on also turns ProxyEnabled off, so an
        //   older build reading this file simply connects directly. Additive: absent = these defaults.
        /// <summary>The built-in WebSocket tunnel (Core/WsTunnel). FALSE (default) = not used (decision D5).</summary>
        public bool WsTunnelEnabled { get; set; } = false;

        /// <summary>Never connect directly while the tunnel is on: a DC without a gateway address, or a gateway that
        /// fails, fails the connection (WTC retries) instead of falling back to direct TCP.</summary>
        public bool WsTunnelStrict { get; set; } = false;

        /// <summary>Gateway addresses per DC, "2=149.154.167.220;4=149.154.167.220". Empty = the defaults
        /// (<see cref="WsRoutes.DefaultPins"/>). IPv4 only.</summary>
        public string WsTunnelDcIps { get; set; } = "";

        /// <summary>Decision D6: accept a certificate CHAIN error (an old device's stale root store) for
        /// *.web.telegram.org only — never a name mismatch — and log each time. FALSE (default) = strict.</summary>
        public bool WsTunnelAcceptChainErrors { get; set; } = false;

        // ── SOCKS5 (redesign step 6.6) ───────────────────────────────────────
        // ⚠ Same rule as the tunnel: SOCKS5 links live in THEIR OWN list, never in ProxyList (1.9.x would hand them to
        //   MTProxyUrl and fail every connect), and while one is in use ProxyEnabled is false.
        /// <summary>Saved SOCKS5 links, normalised to tg://socks?server=..&amp;port=..[&amp;user=..&amp;pass=..] by
        /// <see cref="ProxyUrl"/>. Credentials are stored in plain text, as MTProxy secrets are (TA-15/X7).</summary>
        public System.Collections.Generic.List<string> Socks5List { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Index into <see cref="Socks5List"/> of the selected SOCKS5 proxy; -1 = none.</summary>
        public int Socks5Active { get; set; } = -1;

        /// <summary>A SOCKS5 proxy is in use.</summary>
        public bool Socks5Enabled { get; set; } = false;

        /// <summary>The SOCKS5 link in use, or null (off, the tunnel on, or nothing valid selected).</summary>
        [JsonIgnore]
        public string ActiveSocksUrl
        {
            get
            {
                if (!Socks5Enabled || WsTunnelEnabled) return null;
                var list = Socks5List;
                if (list == null || Socks5Active < 0 || Socks5Active >= list.Count) return null;
                string u = list[Socks5Active];
                return string.IsNullOrWhiteSpace(u) ? null : u;
            }
        }

        /// <summary>Every saved proxy link, MTProto ones first, then SOCKS5 — as the Proxy dialog lists them.</summary>
        [JsonIgnore]
        public System.Collections.Generic.List<string> SavedProxies
        {
            get
            {
                var all = new System.Collections.Generic.List<string>();
                if (ProxyList != null) all.AddRange(ProxyList);
                if (Socks5List != null) all.AddRange(Socks5List);
                return all;
            }
        }

        /// <summary>The remembered proxy (in use or not), whichever kind; null when none is selected.</summary>
        [JsonIgnore]
        public string SelectedProxy
        {
            get
            {
                bool socksOk = Socks5List != null && Socks5Active >= 0 && Socks5Active < Socks5List.Count;
                bool mtOk = ProxyList != null && ProxyActive >= 0 && ProxyActive < ProxyList.Count;
                if (socksOk && (Socks5Enabled || !mtOk)) return Socks5List[Socks5Active];
                return mtOk ? ProxyList[ProxyActive] : null;
            }
        }

        /// <summary>Stores a combined list of links, each in its kind's own field, and the selection: <paramref name="use"/>
        /// = connect through <paramref name="selected"/> (or "proxy on, none selected" when it is null).</summary>
        public void SetSavedProxies(System.Collections.Generic.IEnumerable<string> all, string selected, bool use)
        {
            var mt = new System.Collections.Generic.List<string>();
            var sx = new System.Collections.Generic.List<string>();
            foreach (var u in all) { if (string.IsNullOrWhiteSpace(u)) continue; if (ProxyUrl.IsSocks(u)) sx.Add(u); else mt.Add(u); }
            ProxyList = mt;
            Socks5List = sx;
            bool socks = selected != null && ProxyUrl.IsSocks(selected);
            ProxyActive = selected != null && !socks ? mt.FindIndex(u => string.Equals(u, selected, StringComparison.OrdinalIgnoreCase)) : -1;
            Socks5Active = socks ? sx.FindIndex(u => string.Equals(u, selected, StringComparison.OrdinalIgnoreCase)) : -1;
            ProxyEnabled = use && !socks;
            Socks5Enabled = use && socks;
        }

        /// <summary>The transport in one secret-free phrase, for logs and the diagnostics report: "WebSocket tunnel",
        /// "SOCKS5 host:port", "MTProxy host:port" or "direct".</summary>
        [JsonIgnore]
        public string TransportLabel
        {
            get
            {
                switch (Transport)
                {
                    case ProxyKind.WebSocket: return "WebSocket tunnel";
                    case ProxyKind.Socks5: return ProxyUrl.DisplayLabel(ActiveSocksUrl);
                    case ProxyKind.MtProto: return "MTProxy " + ProxyUrl.SafeForLog(ActiveProxyUrl);
                    default: return "direct";
                }
            }
        }

        /// <summary>How the next connection reaches Telegram. The ONE place that ranks the transports.</summary>
        [JsonIgnore]
        public ProxyKind Transport
        {
            get
            {
                if (WsTunnelEnabled) return ProxyKind.WebSocket;
                if (ActiveSocksUrl != null) return ProxyKind.Socks5;
                return ActiveProxyUrl != null ? ProxyKind.MtProto : ProxyKind.Direct;
            }
        }

        // ── Appearance ───────────────────────────────────────────────────────
        /// <summary>Theme mode: "System" (default), "Light", or "Dark". Stored as text
        /// so the file stays human-readable; parsed into ThemeHelper.ThemeMode at startup.</summary>
        public string ThemeMode { get; set; } = "System";

        /// <summary>Redesign step 1.2 — the user's accent as "#RRGGBB", or "" to follow the Windows accent (the
        /// default, and 1.x's only behaviour). GLOBAL, not per account: the login screen exists before any account.
        /// The environment variable TELEGARM_ACCENT=#RRGGBB overrides it, so accents can be tested before the
        /// Appearance page exists (step 5.2).</summary>
        public string AccentColor { get; set; } = "";

        /// <summary>Step 5.2 — the base colour of the light and of the dark theme: "" = the default (white / graphite), or
        /// "#RRGGBB". Global, like AccentColor.</summary>
        public string LightBase { get; set; } = "";
        public string DarkBase { get; set; } = "";

        /// <summary>Steps 5.4-5.7 — the chat background of each mode and the outgoing-bubble colour (see ThemeSettings).</summary>
        public ThemeSettings Theme { get; set; } = new ThemeSettings();

        /// <summary>Step 5.9 — the window backdrop: "" = Solid (the default), "wallpaper" = the blurred desktop picture.</summary>
        public string WindowBackground { get; set; } = "";

        /// <summary>Redesign step 1.3 — window chrome: "" = build default (TelegArm's own title bar), "Custom", or
        /// "Native" = the ordinary Windows frame (the escape hatch if the custom chrome misbehaves on a device).
        /// --native-caption and TELEGARM_CHROME=native|custom override it. Restart to apply.</summary>
        public string ChromeMode { get; set; } = "";

        /// <summary>The composer: "" = the colour-emoji composer (EmojiComposer, the default since plan step E5), "Plain" = a
        /// standard Windows text box (ThemedTextBox). The search box is always plain. TELEGARM_COMPOSER=emoji|plain overrides
        /// it. Restart to apply. ("Legacy", MaterialSkin's box, is gone and now means the default.)</summary>
        public string ComposerEngine { get; set; } = "";

        /// <summary>Step 2.7c (plan D8) — the window's close button: "" = minimise to the tray (the default, 1.x behaviour),
        /// "Ask" = the CloseChoiceDialog each time, "Exit" = quit. Only once the tray exists; before sign-in X always quits.</summary>
        public string CloseBehavior { get; set; } = "";

        /// <summary>Step 2.7d — the composer editor window's last normal size, in logical px (0 = the default 640 x 480).</summary>
        public int ComposerEditorWidth { get; set; }
        public int ComposerEditorHeight { get; set; }


        /// <summary>Device trip 2026-10 — the touch-keyboard workarounds switched OFF, comma-separated: launch, dismiss,
        /// refocus, actguard. "" = all on (1.x). TELEGARM_KBOFF overrides. The window shrink is ResizeForKeyboard.</summary>
        public string KeyboardOff { get; set; } = "";

        // ── Storage ──────────────────────────────────────────────────────────
        // Cache root is resolved at load time (Documents-first write-test probe — see StoragePaths) rather
        // than hard-wired to %APPDATA%, which is unreliable on RT 8.1. Left null here so a fresh instance
        // (or a legacy AppData value) is normalized by NormalizeCachePath() during Load().
        public string MediaCacheFolder { get; set; }
        public string DefaultSaveFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TelegArm");
        public int MediaCacheRetentionDays { get; set; } = 7;
        public DateTime LastCacheCleanup { get; set; }

        /// <summary>Absolute path to settings.json, under the user-writable data root (NOT the install dir —
        /// Program Files is read-only to a normal user, so a beside-exe settings.json couldn't be saved).</summary>
        [JsonIgnore]
        public static string SettingsPath =>
            Path.Combine(StoragePaths.ResolveDataRoot(), "settings.json");

        private static AppSettings _instance;

        /// <summary>Process-wide settings singleton (lazy-loaded from disk).</summary>
        [JsonIgnore]
        public static AppSettings Instance => _instance ?? (_instance = Load());

        /// <summary>Loads settings from disk, falling back to defaults on any problem.</summary>
        public static AppSettings Load()
        {
            AppSettings settings = null;
            string path = SettingsPath;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    try { settings = JsonConvert.DeserializeObject<AppSettings>(json); }
                    catch (Exception ex) { settings = null; Helpers.Logger.Diag("[SETTINGS] unreadable: " + ex.Message); }
                    // ⚠ STEP 0.4 — a file that exists but does not parse is MOVED ASIDE, never overwritten. Until
                    //   now the defaults were simply saved over it (the cache-path self-heal below saves on every
                    //   first run), silently losing the user's proxies and choices to one bad byte.
                    if (settings == null && !string.IsNullOrWhiteSpace(json)) MoveAside(path);
                }
            }
            catch (Exception ex) { Helpers.Logger.Diag("[SETTINGS] load failed: " + ex.Message); }
            if (settings == null) settings = new AppSettings();
            settings.RestoreNullDefaults();
            settings.Migrate(path);      // ⚠ BEFORE NormalizeCachePath: that may Save, and the 1.x backup must come first
            settings.NormalizeCachePath();
            return settings;
        }

        /// <summary>STEP 0.4 — Json.NET assigns an explicit <c>null</c> over a property initializer, so a
        /// hand-edited (or older) file can null out anything reference-typed. Re-establish every default.
        /// ⚠ A new reference-typed setting MUST be added here.</summary>
        private void RestoreNullDefaults()
        {
            if (SendKey == null) SendKey = "Enter";
            if (ProxyList == null) ProxyList = new System.Collections.Generic.List<string>();
            if (Socks5List == null) Socks5List = new System.Collections.Generic.List<string>();
            if (WsTunnelDcIps == null) WsTunnelDcIps = "";
            // A hand-edited file with several transports on: keep what 2.x uses (tunnel > SOCKS5 > MTProto) and turn
            // the rest off, so the next save cannot leave a 1.9.x reader on an MTProxy the user is not using.
            if (WsTunnelEnabled) { ProxyEnabled = false; Socks5Enabled = false; }
            else if (Socks5Enabled && ActiveSocksUrl != null) ProxyEnabled = false;
            if (ThemeMode == null) ThemeMode = "System";
            if (DefaultSaveFolder == null)
                DefaultSaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TelegArm");
            if (DpiMode == null) DpiMode = "";
            if (AccentColor == null) AccentColor = "";
            if (LightBase == null) LightBase = "";
            if (DarkBase == null) DarkBase = "";
            if (Theme == null) Theme = new ThemeSettings();
            if (WindowBackground == null) WindowBackground = "";
            Theme.Repair();
            if (ChromeMode == null) ChromeMode = "";
            if (ComposerEngine == null) ComposerEngine = "";
            if (CloseBehavior == null) CloseBehavior = "";
            if (KeyboardOff == null) KeyboardOff = "";
            // MediaCacheFolder: null is meaningful (unresolved) — NormalizeCachePath resolves it.
        }

        /// <summary>STEP 0.4 — one-time upgrade of an older file. Idempotent: rolling back to 1.9.x and saving
        /// drops the fields 1.9 does not know (SettingsVersion included), so this simply runs again, and every
        /// step below is safe to repeat.</summary>
        private void Migrate(string path)
        {
            if (SettingsVersion < CurrentSettingsVersion)
            {
                // Keep the 1.x file exactly as it was, once, so a rollback can restore it by hand.
                try
                {
                    string backup = Path.Combine(Path.GetDirectoryName(path), "settings.v1.9.json");
                    if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
                }
                catch (Exception ex) { Helpers.Logger.Diag("[SETTINGS] v1.9 backup failed: " + ex.Message); }

                // The old proportional-scaling switch becomes DpiMode — unless a DpiMode is already there.
                if (DpiUnaware && string.IsNullOrEmpty(DpiMode)) DpiMode = "Unaware";

                Helpers.Logger.Diag("[SETTINGS] migrated v" + SettingsVersion + " -> v" + CurrentSettingsVersion);
                SettingsVersion = CurrentSettingsVersion;
                SyncLegacyMirrors();
                Save();
                return;
            }
            SyncLegacyMirrors();
        }

        /// <summary>Fields kept only for an older build to read: derived, never the other way round.</summary>
        private void SyncLegacyMirrors()
        {
            DpiUnaware = string.Equals(DpiMode, "Unaware", StringComparison.OrdinalIgnoreCase);
        }

        private static void MoveAside(string path)
        {
            string dest = Path.Combine(Path.GetDirectoryName(path),
                                       "settings.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            try { File.Move(path, dest); }
            catch
            {
                // Could not move (locked?) — a copy still preserves it; the later Save then replaces the original.
                try { File.Copy(path, dest, true); } catch (Exception ex) { Helpers.Logger.Diag("[SETTINGS] could not preserve the unreadable file: " + ex.Message); return; }
            }
            Helpers.Logger.Diag("[SETTINGS] unreadable settings.json preserved as " + Path.GetFileName(dest) + "; starting from defaults");
        }

        /// <summary>Moves the cache root off %APPDATA% (RT 8.1 unreliable) to the Documents-first probed root.
        /// Only relocates when unset or still AppData-rooted — a user-chosen non-AppData folder is left alone.</summary>
        private void NormalizeCachePath()
        {
            string before = MediaCacheFolder;
            // Re-resolve when the persisted path is empty OR NOT WRITABLE by the current user — this heals a
            // stale absolute path carried from another machine/user (e.g. a dev-box "C:\Users\hamed\Documents\
            // TelegArm\Cache" deployed to an RT box running as "venezia" → UnauthorizedAccessException → no
            // disk cache). Never trust a saved absolute path blindly; probe it with a real write-test.
            if (string.IsNullOrEmpty(MediaCacheFolder) || !StoragePaths.IsWritable(MediaCacheFolder))
            {
                string resolved = StoragePaths.ResolveCacheRoot();
                if (!string.IsNullOrEmpty(MediaCacheFolder))
                    System.Diagnostics.Debug.WriteLine("[CACHE] persisted cache path not writable by the current user (" + MediaCacheFolder + ") → re-resolved to " + resolved);
                MediaCacheFolder = resolved;
            }
            System.Diagnostics.Debug.WriteLine("[CACHE] cache root = " + MediaCacheFolder);
            if (MediaCacheFolder != before) { try { Save(); } catch { /* best-effort self-heal of settings.json */ } }
        }

        private static readonly object SaveLock = new object();

        /// <summary>Writes the given settings to disk (best-effort).
        /// ⚠ STEP 0.4 — LOCKED AND ATOMIC (temp file, then File.Replace; ported from DroidFinestra). The daily cache
        ///   prune saves from a pool thread while the UI saves too, and a crash or power loss in the middle of
        ///   File.WriteAllText left a truncated settings.json — which the next Load then replaced with defaults.</summary>
        public static void Save(AppSettings settings)
        {
            lock (SaveLock)
            {
                string path = null, tmp = null;
                try
                {
                    settings.SyncLegacyMirrors();
                    var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    path = SettingsPath;
                    tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (!File.Exists(path)) { File.Move(tmp, path); return; }
                    try { File.Replace(tmp, path, null); }
                    catch (IOException)
                    {
                        // ReplaceFile can refuse where a plain overwrite works (some file systems, a scanner
                        // holding the file). Not atomic, but never worse than the old direct write.
                        File.Copy(tmp, path, true);
                        File.Delete(tmp);
                    }
                }
                catch (Exception ex)
                {
                    // Non-fatal — settings just won't persist this time.
                    Helpers.Logger.Diag("[SETTINGS] save failed: " + ex.Message);
                    try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
        }

        /// <summary>Persists this instance to disk.</summary>
        public void Save() => Save(this);
    }
}
