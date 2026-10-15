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
    }
}
