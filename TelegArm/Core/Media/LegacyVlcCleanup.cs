using System;
using System.IO;
using System.Threading.Tasks;
using TelegArm.Helpers;

namespace TelegArm.Core.Media
{
    /// <summary>
    /// 2026-10-16: libVLC is gone (FFmpeg plays everything). Up to 2.0.0-alpha the app extracted a ~100 MB libVLC into a
    /// libvlc\ folder beside the exe, in Documents\TelegArm or in LocalAppData\TelegArm (VlcBootstrap). The installer
    /// already removes the one beside the exe; this removes the other two once, in the background.
    /// ONLY OUR OWN EXTRACTION IS TOUCHED: a libvlc\ folder is deleted only when it carries VlcBootstrap's marker file
    /// (.telegarm_vlc), so a VLC the user placed there by hand stays. Its interrupted-extraction temp folders
    /// (.vlc_extract_*) go too. Nothing else in those folders is read or written.
    /// </summary>
    internal static class LegacyVlcCleanup
    {
        private const string MarkerName = ".telegarm_vlc";

        public static void RunInBackground()
        {
            Task.Run(() =>
            {
                foreach (var b in Bases())
                {
                    try { Clean(b); }
                    catch (Exception ex) { Logger.Diag("[MEDIA] libvlc cleanup in " + b + ": " + ex.Message); }
                }
            });
        }

        private static string[] Bases()
        {
            string docs = Safe(Environment.SpecialFolder.MyDocuments), lad = Safe(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                docs != null ? Path.Combine(docs, "TelegArm") : null,
                lad != null ? Path.Combine(lad, "TelegArm") : null,
            };
        }

        private static void Clean(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;
            string folder = Path.Combine(baseDir, "libvlc");
            if (File.Exists(Path.Combine(folder, MarkerName)))
            {
                Directory.Delete(folder, true);
                Logger.Diag("[MEDIA] removed the old extracted libVLC: " + folder);
            }
            foreach (var d in Directory.GetDirectories(baseDir, ".vlc_extract_*"))
            {
                Directory.Delete(d, true);
                Logger.Diag("[MEDIA] removed an interrupted libVLC extraction: " + d);
            }
        }

        private static string Safe(Environment.SpecialFolder f)
        {
            try { string p = Environment.GetFolderPath(f); return string.IsNullOrEmpty(p) ? null : p; } catch { return null; }
        }
    }
}
