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

        private static readonly Color DefaultAccent = Color.FromArgb(0, 120, 215); // Windows default blue

        private static ThemeMode _mode = ThemeMode.System;

        /// <summary>The current theme mode (defaults to <see cref="ThemeMode.System"/>).</summary>
        public static ThemeMode Mode => _mode;

        /// <summary>
        /// Sets the mode WITHOUT notifying listeners. Use once at startup to apply the
        /// persisted choice before any form reads the theme.
        /// </summary>
        public static void InitMode(ThemeMode mode) => _mode = mode;

        /// <summary>Sets the mode and raises <see cref="ThemeChanged"/> if it actually changed.</summary>
        public static void SetMode(ThemeMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
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
        /// </summary>
        public static Color GetWindowsAccentColor()
        {
            if (!IsWindows10OrGreater())
            {
                LogAccentDiagOnce();   // one-shot [ACCENT] truth line so the 8.1 read + decode is verifiable by log
                Color? c81 = ReadAbgrDword(ExplorerAccentKey, "AccentColor");
                if (c81.HasValue) return c81.Value;
            }
            return ReadAbgrDword(DwmKey, "AccentColor") ?? DefaultAccent;
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
                        return Color.FromArgb(r, g, b);
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

        /// <summary>Begins listening for system theme/color changes (idempotent).</summary>
        public static void StartListening()
        {
            if (_listening) return;
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
                ThemeChanged?.Invoke();
        }

        // ── Live accent-change fan-out (Part 2) ──
        // Called from BOTH MainForm's WM_DWMCOLORIZATIONCOLORCHANGED handler (the reliable signal — it fires on
        // 8.1 AND 10/11 when the accent/colorization changes) and the SystemEvents Color category. Fires
        // ThemeChanged so every subscriber re-reads GetWindowsAccentColor() and recolors live — no restart.
        // Debounced: a single user change often emits the notification several times, so repeats within ~300ms
        // collapse to one recolor.
        private static int _lastAccentTick;
        public static void NotifyAccentChanged()
        {
            int now = Environment.TickCount;
            if (_lastAccentTick != 0 && unchecked((uint)(now - _lastAccentTick)) < 300) return;   // coalesce bursts (2.3)
            _lastAccentTick = now;
            try
            {
                Debug.WriteLine("[ACCENT] change os=" + OsLabel()
                    + " → picked 0x" + ((uint)GetWindowsAccentColor().ToArgb()).ToString("X8"));
            }
            catch { }
            ThemeChanged?.Invoke();
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
