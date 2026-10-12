using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>STEP 0.1 — the text behind Settings → Advanced → "Copy diagnostics", and the header of the
    /// --shoot rig's shots.txt. One place, so a bug report and a screenshot baseline describe the machine the
    /// same way.
    ///
    /// ⚠ NOTHING PRIVATE GOES IN HERE. No phone numbers, account ids, chat names or paths under the user's
    ///   profile; the proxy is reduced to ProxyUrl.SafeForLog (host:port, never the secret). The user pastes
    ///   this into a public issue.
    /// ⚠ Every probe is individually guarded: this runs on RT 8.1 and Windows 7, where several of these
    ///   exports do not exist, and a diagnostics button that throws is worse than none.</summary>
    internal static class DiagReport
    {
        internal static string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TelegArm " + Program.Version + Informational(typeof(Program).Assembly));
            sb.AppendLine("Windows: " + Safe(OsLine));
            sb.AppendLine("Arch: " + Safe(ArchLine));
            sb.AppendLine(".NET: " + Safe(NetLine));
            sb.AppendLine("DPI: awareness=" + Program.DpiAwareness + " systemDpi=" + Program.SystemDpi
                          + " proportionalSetting=" + Safe(() => AppSettings.Instance.DpiUnaware ? "on" : "off"));
            sb.Append(Safe(MonitorLines));
            sb.AppendLine("Theme: " + Safe(() => ThemeHelper.Mode + (ThemeHelper.IsDark ? " (dark)" : " (light)")
                          + " accent=#" + (ThemeHelper.GetWindowsAccentColor().ToArgb() & 0xFFFFFF).ToString("X6")));
            sb.AppendLine("Proxy: " + Safe(ProxyLine));
            sb.AppendLine("WTelegramClient: " + Safe(() => typeof(WTelegram.Client).Assembly.GetName().Version
                          + Informational(typeof(WTelegram.Client).Assembly)));
            sb.AppendLine("Logging: " + Safe(() => AppSettings.Instance.FileLogging ? "on" : "off")
                          + "  data root via " + (StoragePaths.CacheVia ?? "(unresolved)"));
            return sb.ToString();
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "(none)"; }
            catch (Exception ex) { return "(unavailable: " + ex.GetType().Name + ")"; }
        }

        private static string Informational(Assembly a)
        {
            var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
            return attr != null && !string.IsNullOrEmpty(attr.InformationalVersion) ? " (" + attr.InformationalVersion + ")" : "";
        }

        // ── Windows: RtlGetVersion, because GetVersionEx lies to an unmanifested exe (it says 6.2 on 8.1+) ──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        }
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OSVERSIONINFOW v);

        private static string OsLine()
        {
            var v = new OSVERSIONINFOW { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOW)) };
            string ver = RtlGetVersion(ref v) == 0
                ? v.dwMajorVersion + "." + v.dwMinorVersion + "." + v.dwBuildNumber
                : Environment.OSVersion.Version.ToString();
            string product = null, display = null, ubr = null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    product = k.GetValue("ProductName") as string;
                    display = (k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId")) as string;
                    object u = k.GetValue("UBR");
                    if (u != null) ubr = u.ToString();
                }
            }
            // Windows 11 still reports "Windows 10" as ProductName; the build number (22000+) is the truth.
            return ver + (ubr != null ? "." + ubr : "") + "  " + (product ?? "?") + (display != null ? " " + display : "");
        }

        private static string ArchLine()
        {
            string os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                        ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
            return "machine=" + os + " process=" + (IntPtr.Size == 4 ? "32-bit" : "64-bit")
                   + " (" + (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?") + ")";
        }

        private static string NetLine()
        {
            object rel = null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (k != null) rel = k.GetValue("Release");
            return "CLR " + Environment.Version + (rel != null ? "  release " + rel : "  (no v4 Full release key)");
        }

        // ── Monitors: GetDpiForMonitor is 8.1+ (RT 8.1 has it); its absence is reported, not thrown ──
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

        private static string MonitorLines()
        {
            var sb = new StringBuilder();
            foreach (var s in Screen.AllScreens)
            {
                string dpi;
                try
                {
                    var c = new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
                    uint dx, dy;
                    dpi = GetDpiForMonitor(MonitorFromPoint(c, 2 /*NEAREST*/), 0 /*EFFECTIVE*/, out dx, out dy) == 0
                        ? dx.ToString() : "?";
                }
                catch (DllNotFoundException) { dpi = "n/a (no shcore)"; }
                catch (EntryPointNotFoundException) { dpi = "n/a (pre-8.1)"; }
                sb.AppendLine("Monitor: " + s.Bounds.Width + "x" + s.Bounds.Height + " at " + s.Bounds.X + "," + s.Bounds.Y
                              + " work " + s.WorkingArea.Width + "x" + s.WorkingArea.Height
                              + " dpi=" + dpi + (s.Primary ? " primary" : ""));
            }
            return sb.ToString();
        }

        private static string ProxyLine()
        {
            var s = AppSettings.Instance;
            string active = s.ActiveProxyUrl;
            int n = s.ProxyList != null ? s.ProxyList.Count : 0;
            return active != null ? "MTProxy " + ProxyUrl.SafeForLog(active) + " (" + n + " saved)"
                                  : "direct (" + n + " saved)";
        }
    }
}
