using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TelegArm.Helpers
{
    /// <summary>How the app decides light vs. dark: follow the OS, or a fixed override.</summary>
    public enum ThemeMode { System, Light, Dark }

    /// <summary>
    /// Reads the user's Windows accent color and light/dark preference, and
    /// raises <see cref="ThemeChanged"/> when either changes at runtime.
    /// Also holds the app-wide <see cref="Mode"/> (System / Light / Dark).
    /// </summary>
    public static class ThemeHelper
    {
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";
        private const string ExplorerAccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";


        private static ThemeMode _mode = ThemeMode.System;

        /// <summary>The current theme mode (defaults to <see cref="ThemeMode.System"/>).</summary>
        public static ThemeMode Mode => _mode;

        /// <summary>
        /// Sets the mode WITHOUT notifying listeners. Use once at startup to apply the
        /// persisted choice before any form reads the theme.
        /// </summary>
        public static void InitMode(ThemeMode mode) { _mode = mode; _palette = null; }

        /// <summary>Sets the mode and raises <see cref="ThemeChanged"/> if it actually changed.</summary>
        public static void SetMode(ThemeMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            Raise();
        }

        // ── Redesign step 1.2: the accent override (ported from DroidFinestra's ThemeHelper) ─────────────────────
        private static Color? _accentOverride;   // null = follow the Windows accent (1.x's only behaviour)

        /// <summary>The user's fixed accent, or null when the app follows the Windows accent.</summary>
        public static Color? AccentOverride => _accentOverride;

        /// <summary>Sets the accent override WITHOUT notifying (startup, before any form reads it).</summary>
        public static void InitAccent(Color? accent) { _accentOverride = accent; _palette = null; }

        /// <summary>Sets (or clears, with null) the accent override and raises <see cref="ThemeChanged"/> — every
        /// themed control recolors live, the same path a Windows accent change takes.</summary>
        public static void SetAccent(Color? accent)
        {
            if (_accentOverride.HasValue == accent.HasValue
                && (!accent.HasValue || _accentOverride.Value.ToArgb() == accent.Value.ToArgb())) return;
            _accentOverride = accent;
            Raise();
        }

        /// <summary>"#RRGGBB" (the "#" optional) → Color, or null for empty/invalid (= follow Windows).</summary>
        public static Color? ParseAccent(string s)
        {
            s = (s ?? "").Trim().TrimStart('#');
            int v;
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v)) return null;
            return ColorMath.Rgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        public static string FormatAccent(Color? c) => c.HasValue ? "#" + (c.Value.ToArgb() & 0xFFFFFF).ToString("X6") : "";

        // ── Step 5.2: the base colour of each mode (owner, 2026-10-15) — null = the default (white / graphite) ──────────
        private static Color? _lightBase, _darkBase;

        // ── Step 5.7: the outgoing-bubble colour — null = the accent ──────────────────────────────────────────────────
        private static Color? _outBubble;
        public static Color? OutBubble => _outBubble;
        public static void InitOutBubble(Color? c) { _outBubble = c; _palette = null; _otherMode = null; }
        public static void SetOutBubble(Color? c)
        {
            if (_outBubble.HasValue == c.HasValue && (!c.HasValue || _outBubble.Value.ToArgb() == c.Value.ToArgb())) return;
            _outBubble = c;
            Raise();
        }

        /// <summary>The user's base for a mode, or null for the default.</summary>
        public static Color? BaseFor(bool dark) => dark ? _darkBase : _lightBase;

        /// <summary>Sets both bases WITHOUT notifying (startup, before any form reads the theme).</summary>
        public static void InitBases(Color? light, Color? dark) { _lightBase = light; _darkBase = dark; _palette = null; _otherMode = null; }

        /// <summary>Sets (or clears, with null) one mode's base and raises <see cref="ThemeChanged"/> when it changed.</summary>
        public static void SetBase(bool dark, Color? c)
        {
            var cur = BaseFor(dark);
            if (cur.HasValue == c.HasValue && (!c.HasValue || cur.Value.ToArgb() == c.Value.ToArgb())) return;
            if (dark) _darkBase = c; else _lightBase = c;
            Raise();
        }

        // ── Redesign step 1.2: the cached palette ─────────────────────────────────────────────────────────────
        private static Palette _palette;

        /// <summary>The current colour tokens (<see cref="Helpers.Palette"/>). Built once and rebuilt BEFORE
        /// <see cref="ThemeChanged"/> fires, so a handler reads the new colours — and a paint handler reads a field
        /// instead of the registry, which <see cref="IsDark"/> and <see cref="GetWindowsAccentColor"/> hit on every
        /// call. New code uses this; the existing call sites move to it in steps 1.7 and 5.1.</summary>
        public static Palette Palette
        {
            get
            {
                var p = _palette;
                if (p == null) { bool d = IsDark; _palette = p = Helpers.Palette.Build(d, GetWindowsAccentColor(), BaseFor(d), _outBubble); }
                return p;
            }
        }

        private static Palette _otherMode;

        /// <summary>Redesign step 1.7 — the palette for a GIVEN mode. The chat surfaces carry their own dark flag (set by
        /// MainForm, which follows ThemeHelper — so in practice it is the cached <see cref="Palette"/>), and `dark ? X : Y`
        /// becomes `PaletteFor(dark).Token` with the SAME meaning even if the two ever disagree for a moment. The other
        /// mode is built once and rebuilt when the accent moves.</summary>
        public static Palette PaletteFor(bool dark)
        {
            var p = Palette;
            if (p.Dark == dark) return p;
            var o = _otherMode;
            if (o == null || o.Dark != dark || o.Accent != p.Accent) _otherMode = o = Helpers.Palette.Build(dark, p.Accent, BaseFor(dark), _outBubble);
            return o;
        }

        /// <summary>The one way ThemeChanged is raised: palette first, then the event.</summary>
        private static void Raise()
        {
            _otherMode = null;   // step 5.2: a base change must rebuild the other mode too
            try { bool d = IsDark; _palette = Helpers.Palette.Build(d, GetWindowsAccentColor(), BaseFor(d), _outBubble); } catch { _palette = null; }
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// The resolved dark/light decision, honoring the user override:
        /// Dark/Light are fixed; System reads the OS preference (<see cref="IsDarkMode"/>).
        /// </summary>
        public static bool IsDark =>
            _mode == ThemeMode.Dark || (_mode == ThemeMode.System && IsDarkMode());

        /// <summary>
        /// The single accent-resolution entry point for the whole app. Platform-branched because the user's
        /// chosen accent lives in different registry keys on different Windows versions:
        ///   • Windows 10/11    → HKCU\...\DWM\AccentColor                (unchanged from before)
        ///   • Windows 8.1 / RT → HKCU\...\Explorer\Accent\AccentColor    (DWM\AccentColor doesn't exist there;
        ///     the DWM key only holds ColorizationColor — the composed frame TINT, not the picked swatch)
        /// Both are DWORDs in AABBGGRR (ABGR) byte order — low byte is red. Alpha is dropped (opaque).
        /// Never throws: any failure falls back to the DWM read, then the Windows default blue.
        /// A user-picked accent (<see cref="SetAccent"/>, or TELEGARM_ACCENT) takes precedence over all of the above.
        /// </summary>
        public static Color GetWindowsAccentColor()
        {
            if (_accentOverride.HasValue) return _accentOverride.Value;   // step 1.2: the user's accent wins
            return ReadWindowsAccent();
        }

        /// <summary>Step 2.6: the accent WINDOWS has, ignoring the user's override — Settings → Appearance draws it as the
        /// "Follow Windows" swatch. A registry read: never call it from a paint method.</summary>
        public static Color ReadWindowsAccent()
        {
            if (!IsWindows10OrGreater())
            {
                LogAccentDiagOnce();   // one-shot [ACCENT] truth line so the 8.1 read + decode is verifiable by log
                Color? c81 = ReadAbgrDword(ExplorerAccentKey, "AccentColor");
                if (c81.HasValue) return c81.Value;
            }
            return ReadAbgrDword(DwmKey, "AccentColor") ?? Helpers.Palette.WindowsDefaultAccent;
        }

        /// <summary>Reads an HKCU DWORD accent value stored as AABBGGRR (ABGR, low byte = red) → opaque Color,
        /// or null if the key/value is missing or unreadable (so callers can fall back).</summary>
        private static Color? ReadAbgrDword(string subKey, string valueName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (key?.GetValue(valueName) is int dword)
                    {
                        int r = dword & 0xFF;
                        int g = (dword >> 8) & 0xFF;
                        int b = (dword >> 16) & 0xFF;
                        return ColorMath.Rgb(r, g, b);
                    }
                }
            }
            catch { /* fall through to null */ }
            return null;
        }

        // One-shot diagnostic for the 8.1 read: dumps the raw Explorer\Accent\AccentColor DWORD + BOTH decodes so
        // the byte order that yields Hamed's GREEN is verifiable from the log (asABGR expected to match the picked
        // swatch; asARGB shown only for contrast), plus whether DWM\AccentColor — the key the pre-fix read used —
        // even exists on the device.
        private static bool _accentDiagLogged;
        private static void LogAccentDiagOnce()
        {
            if (_accentDiagLogged) return;
            _accentDiagLogged = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ExplorerAccentKey))
                {
                    if (key?.GetValue("AccentColor") is int d)
                    {
                        int argbR = (d >> 16) & 0xFF, argbG = (d >> 8) & 0xFF, argbB = d & 0xFF;
                        int abgrR = d & 0xFF, abgrG = (d >> 8) & 0xFF, abgrB = (d >> 16) & 0xFF;
                        Debug.WriteLine("[ACCENT] Explorer\\Accent\\AccentColor=0x" + ((uint)d).ToString("X8")
                            + "  asARGB=" + argbR + "," + argbG + "," + argbB
                            + "  asABGR=" + abgrR + "," + abgrG + "," + abgrB);
                    }
                    else
                    {
                        Debug.WriteLine("[ACCENT] Explorer\\Accent\\AccentColor MISSING");
                    }
                }
                using (var dwm = Registry.CurrentUser.OpenSubKey(DwmKey))
                {
                    object raw = dwm?.GetValue("AccentColor");
                    Debug.WriteLine("[ACCENT] DWM\\AccentColor=" + (raw is int dd ? "0x" + ((uint)dd).ToString("X8") : "MISSING")
                        + "  (source of the pre-fix read)");
                }
            }
            catch (Exception ex) { Debug.WriteLine("[ACCENT] diag failed: " + ex.Message); }
        }

        /// <summary>
        /// True when Windows is using the dark app theme
        /// (HKCU\...\Personalize\AppsUseLightTheme == 0).
        /// </summary>
        public static bool IsDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (key?.GetValue("AppsUseLightTheme") is int v)
                        return v == 0;
                }
            }
            catch
            {
                // Default to light if the value is missing or unreadable.
            }
            return false;
        }

        /// <summary>Raised when the accent color or light/dark preference changes.</summary>
        public static event Action ThemeChanged;

        private static bool _listening;

        /// <summary>Begins listening for system theme/color changes (idempotent). Process-wide since step 1.2 —
        /// Program.Run starts it, so the login screen follows a live change too (MainForm used to own it).
        /// ⚠ CALL IT ON THE UI THREAD WITH A WINFORMS SYNCHRONIZATION CONTEXT INSTALLED. SystemEvents delivers to
        ///   the context captured at subscription; with none (before any Control exists) it would raise ThemeChanged
        ///   on its own thread, and every subscriber touches UI. The captured context also carries the accent
        ///   debounce's timer back to the UI thread.</summary>
        public static void StartListening()
        {
            if (_listening) return;
            _ui = System.Threading.SynchronizationContext.Current;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        /// <summary>Stops listening for system theme/color changes (idempotent).</summary>
        public static void StopListening()
        {
            if (!_listening) return;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Accent color always follows the OS. The OS light/dark switch (General)
            // only affects the app while we're following the system (Mode == System);
            // a manual Light/Dark override is left untouched.
            if (e.Category == UserPreferenceCategory.Color)
                NotifyAccentChanged();   // route through the debounce so it coalesces with the WM_DWMCOLORIZATION path (2.2/2.3)
            else if (e.Category == UserPreferenceCategory.General && _mode == ThemeMode.System)
                Raise();
        }

        // ── Live accent-change fan-out (Part 2) ──
        // Called from BOTH MainForm's WM_DWMCOLORIZATIONCOLORCHANGED handler (the reliable signal — it fires on
        // 8.1 AND 10/11 when the accent/colorization changes) and the SystemEvents Color category. Fires
        // ThemeChanged so every subscriber re-reads the accent and recolors live — no restart.
        // ★ STEP 1.2: TRAILING-EDGE DEBOUNCE. One user change emits the notification several times, and Windows may
        //   write the accent in steps. The old leading-edge debounce fired on the FIRST notification and dropped the
        //   rest for 300 ms, so the app could recolor with an intermediate value and miss the final one. Now every
        //   notification restarts a 300 ms timer and the recolor happens once, after the burst, with the value that
        //   stuck. The timer's callback is posted back to the UI thread (_ui, captured in StartListening).
        // ★ A user-picked accent ignores Windows' accent: nothing to recolor, so nothing is raised.
        private const int AccentSettleMs = 300;
        private static readonly object _accentLock = new object();
        private static System.Threading.Timer _accentTimer;
        private static System.Threading.SynchronizationContext _ui;

        public static void NotifyAccentChanged()
        {
            if (_accentOverride.HasValue) return;
            if (_ui == null) _ui = System.Threading.SynchronizationContext.Current;
            lock (_accentLock)
            {
                if (_accentTimer == null)
                    _accentTimer = new System.Threading.Timer(OnAccentSettled, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _accentTimer.Change(AccentSettleMs, System.Threading.Timeout.Infinite);
            }
        }

        private static void OnAccentSettled(object state)
        {
            var ui = _ui;
            if (ui != null) ui.Post(s => AccentSettled(), null);
            else AccentSettled();
        }

        private static void AccentSettled()
        {
            try
            {
                Logger.Diag("[ACCENT] change os=" + OsLabel()
                    + " → picked 0x" + ((uint)GetWindowsAccentColor().ToArgb()).ToString("X8"));
            }
            catch { }
            Raise();
        }

        // ── OS version via RtlGetVersion (Environment.OSVersion caps at 6.2 on an unmanifested app) ──
        [StructLayout(LayoutKind.Sequential)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW versionInfo);

        private static int _osMajor = -1, _osMinor, _osBuild;
        private static void EnsureOsVersion()
        {
            if (_osMajor >= 0) return;
            try
            {
                var vi = new RTL_OSVERSIONINFOW();
                vi.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                if (RtlGetVersion(ref vi) == 0)   // STATUS_SUCCESS
                {
                    _osMajor = (int)vi.dwMajorVersion; _osMinor = (int)vi.dwMinorVersion; _osBuild = (int)vi.dwBuildNumber;
                    return;
                }
            }
            catch { /* fall through to managed best-effort */ }
            try { var v = Environment.OSVersion.Version; _osMajor = v.Major; _osMinor = v.Minor; _osBuild = v.Build; }
            catch { _osMajor = 6; _osMinor = 3; _osBuild = 0; }   // assume 8.1 on total failure (safest for the read branch)
        }

        /// <summary>True on Windows 10 or newer (major &gt;= 10). Branches both the accent read and the log label.</summary>
        public static bool IsWindows10OrGreater() { EnsureOsVersion(); return _osMajor >= 10; }

        private static string OsLabel()
        {
            EnsureOsVersion();
            if (_osMajor >= 10) return _osBuild >= 22000 ? "11" : "10";
            if (_osMajor == 6 && _osMinor == 3) return "8.1";
            return _osMajor + "." + _osMinor;
        }
    }
}
