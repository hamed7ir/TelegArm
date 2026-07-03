using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Points the OS native-DLL search at the per-architecture folder shipped next to the exe, so
    /// the AnyCPU(+Prefer32Bit) build loads the matching native binary. A native DLL is NOT
    /// portable across architectures — AnyCPU only makes the *managed* assembly neutral. On the
    /// dev PC the process runs as x86; on the Surface RT it runs as ARM32, and each loads its own
    /// <c>rlottie\&lt;arch&gt;\rlottie.dll</c>. If the folder/binary is missing, the dependent
    /// feature (Lottie .tgs playback) simply stays unavailable — no crash.
    /// </summary>
    public static class NativeLibraries
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        public static void Init()
        {
            try
            {
                // Stop the Windows loader from popping a hard-error dialog (e.g. "msvcp140.dll is
                // missing") when an optional native dep can't load — we want a silent, catchable
                // failure that degrades to the static fallback instead.
                SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);

                // A 32-bit process reports "x86" on Intel (incl. WOW64) and "ARM" on ARM32 Windows.
                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
                string sub = arch.StartsWith("ARM", StringComparison.OrdinalIgnoreCase) ? "ARM" : "x86";
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rlottie", sub);
                if (Directory.Exists(dir)) SetDllDirectory(dir);
            }
            catch { /* native features just stay unavailable */ }
        }
    }
}
