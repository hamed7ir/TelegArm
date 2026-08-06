using System;
using System.IO;
using Newtonsoft.Json;

namespace TelegArm.Core
{
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
        /// <summary>Master switch for desktop notifications (muted chats are always skipped).</summary>
        public bool EnableNotifications { get; set; } = true;

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

        /// <summary>FOLDER-SIDEBAR: folder navigation style. false = horizontal TAB bar above the chat list
        /// (default, today's exact layout); true = a vertical icon RAIL left of the chat list (Telegram-Desktop
        /// style). Read ONCE at construction (BuildLeftPanel) — restart to apply, like CompositedScroll, so the
        /// chat-list host's handle is created with its final parent and the touch/scroll invariants are never
        /// disturbed by a live reparent.</summary>
        public bool FolderSidebar { get; set; } = false;

        /// <summary>DPI-MODE-TOGGLE: opt-in DPI-UNAWARE declaration ("proportional scaling") — Windows
        /// stretches the whole UI uniformly on scaled displays (slightly blurry) instead of the system-aware
        /// mixed look (2× fonts over 1× layout). No effect at 100% scale (RT identical either way). Read at
        /// the very top of startup, BEFORE any UI — restart to apply. Default FALSE = today's system-aware.</summary>
        public bool DpiUnaware { get; set; } = false;

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
                if (!ProxyEnabled) return null;
                var list = ProxyList;
                if (list == null || ProxyActive < 0 || ProxyActive >= list.Count) return null;
                string u = list[ProxyActive];
                return string.IsNullOrWhiteSpace(u) ? null : u;
            }
        }

        // ── Appearance ───────────────────────────────────────────────────────
        /// <summary>Theme mode: "System" (default), "Light", or "Dark". Stored as text
        /// so the file stays human-readable; parsed into ThemeHelper.ThemeMode at startup.</summary>
        public string ThemeMode { get; set; } = "System";

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
            try
            {
                if (File.Exists(SettingsPath))
                    settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath));
            }
            catch
            {
                // Corrupt/unreadable file → fall back to defaults.
            }
            if (settings == null) settings = new AppSettings();
            settings.NormalizeCachePath();
            return settings;
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

        /// <summary>Writes the given settings to disk (best-effort).</summary>
        public static void Save(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Non-fatal — settings just won't persist this time.
            }
        }

        /// <summary>Persists this instance to disk.</summary>
        public void Save() => Save(this);
    }
}
