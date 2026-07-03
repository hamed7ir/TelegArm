using System;
using System.IO;

namespace TelegArm.Core
{
    /// <summary>
    /// Detects and one-time-initializes a bundled VLC runtime placed in a "libvlc"
    /// folder next to the executable. No paths are hardcoded; on machines without a
    /// matching-architecture VLC (e.g. an x64/x86 dev box) callers fall back to the
    /// system player. Init is wrapped so a wrong-arch/partial drop can't crash the app.
    /// </summary>
    public static class VlcEnvironment
    {
        private static string _folder;   // set by VlcBootstrap to the extracted location; null → default beside-exe

        /// <summary>The resolved libvlc/ folder. Defaults to beside-exe (a hand-placed folder still works);
        /// <see cref="VlcBootstrap"/> repoints it to the extracted location (which may be under Documents on RT).</summary>
        public static string VlcFolder =>
            _folder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libvlc");

        /// <summary>Points the loader at a specific libvlc/ folder (the bootstrap's extracted/located one).</summary>
        public static void UseFolder(string folder)
        {
            if (!string.IsNullOrEmpty(folder)) _folder = folder;
        }

        /// <summary>True when both core VLC DLLs are present in the libvlc folder.</summary>
        public static bool IsAvailable =>
            File.Exists(Path.Combine(VlcFolder, "libvlc.dll")) &&
            File.Exists(Path.Combine(VlcFolder, "libvlccore.dll"));

        private static bool _initialized;
        private static bool _initFailed;

        /// <summary>
        /// Initializes LibVLC from the bundled folder exactly once. Returns false if
        /// VLC is absent or initialization fails (e.g. architecture mismatch).
        /// </summary>
        public static bool TryInitialize()
        {
            if (_initialized) return true;
            if (_initFailed || !IsAvailable) return false;

            try
            {
                LibVLCSharp.Shared.Core.Initialize(VlcFolder);
                _initialized = true;
                return true;
            }
            catch
            {
                _initFailed = true; // BadImageFormat (wrong arch), missing plugins, etc.
                return false;
            }
        }
    }
}
