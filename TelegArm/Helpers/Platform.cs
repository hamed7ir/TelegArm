using System;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 3.6 — what the process runs on, asked once. A 32-bit process reports "x86" on Intel (incl.
    /// WOW64) and "ARM" on ARM32 Windows (the Surface RT), the same test NativeLibraries uses to pick its rlottie folder.
    /// TELEGARM_ARM32=1 forces it on (or 0 off) so the ARM32 caps (Lottie render size) can be tried on the dev box.</summary>
    public static class Platform
    {
        private static int _arm32 = -1;

        public static bool IsArm32
        {
            get
            {
                if (_arm32 < 0)
                {
                    bool v = false;
                    try
                    {
                        string forced = Environment.GetEnvironmentVariable("TELEGARM_ARM32");
                        if (forced == "1") v = true;
                        else if (forced == "0") v = false;
                        else
                        {
                            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
                            v = IntPtr.Size == 4 && arch.StartsWith("ARM", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { v = false; }
                    _arm32 = v ? 1 : 0;
                }
                return _arm32 == 1;
            }
        }

        private static int _major = -1;

        /// <summary>The real Windows major version (RtlGetVersion: GetVersionEx says 6.2 to an unmanifested exe on 8.1+).
        /// 6 = Windows 7 / 8 / 8.1 (the Surface RT), 10 = Windows 10 and 11.</summary>
        public static int WindowsMajor
        {
            get
            {
                if (_major < 0)
                {
                    try
                    {
                        var v = new OSVERSIONINFOW { dwOSVersionInfoSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(OSVERSIONINFOW)) };
                        _major = RtlGetVersion(ref v) == 0 ? v.dwMajorVersion : Environment.OSVersion.Version.Major;
                    }
                    catch { _major = Environment.OSVersion.Version.Major; }
                }
                return _major;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        }

        [System.Runtime.InteropServices.DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OSVERSIONINFOW v);
    }
}
