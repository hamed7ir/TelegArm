using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.Core
{
    /// <summary>
    /// Bundles libVLC inside the app: the matching-architecture runtime ships as a zip beside the exe and is
    /// EXTRACTED ONCE, on first run, into a real libvlc/ folder — native DLLs and libVLC's plugins/ scan dir
    /// CANNOT load from inside a zip. A version-marker file makes later launches skip extraction entirely
    /// (identical runtime cost to a hand-placed folder). Extraction is temp-then-rename so a failure can't
    /// leave a half-written folder; the target base is chosen with a beside-exe / Documents write-probe
    /// (RT-safe). Never throws — if it can't extract, media just stays unavailable until resolved.
    /// </summary>
    public static class VlcBootstrap
    {
        // Bump when the bundled zips change → forces a one-time re-extract.
        private const string BundleVersion = "1";
        private const string MarkerName = ".telegarm_vlc";

        /// <summary>Ensures a usable libvlc/ folder exists (extracting on first run) and points
        /// <see cref="VlcEnvironment"/> at it. Call once at startup, before any LibVLC init.</summary>
        public static void EnsureReady()
        {
            try
            {
                // BATCH-TA-0.1: bracket the WHOLE bootstrap. It runs synchronously before any window exists,
                // so its cost is dead time on a blank screen — measured at ~19 s for a first-run extract on the
                // x64 dev box, and the Tegra-3 number can only ever be captured on a device's FIRST run.
                var swBoot = System.Diagnostics.Stopwatch.StartNew();
                string arch = ProcessArch();                 // "arm" | "x86"
                string want = arch + "|" + BundleVersion;
                Logger.Diag("[VLC] bootstrap START arch=" + arch + " bundleVersion=" + BundleVersion);

                // 1. A usable folder already present (our CURRENT marker, OR a foreign/manual install) → use it.
                foreach (var baseDir in CandidateBases())
                {
                    string folder = Path.Combine(baseDir, "libvlc");
                    if (!HasCoreDlls(folder)) continue;
                    string marker = ReadMarker(folder);
                    if (marker == null || marker == want)
                    {
                        VlcEnvironment.UseFolder(folder);
                        Logger.Diag("[VLC] libvlc/ current → skip extraction (" + folder + ")");
                        Logger.Diag("[VLC] bootstrap END in " + swBoot.ElapsedMilliseconds + "ms (warm: no extraction)");
                        return;
                    }
                    Logger.Diag("[VLC] libvlc/ STALE at " + folder + " (marker=" + marker + ") → re-extract");
                }

                // 2. None usable → extract the matching-arch zip into the first writable base.
                string zip = Path.Combine(BesideExe, arch == "arm" ? "libvlc-arm32.zip" : "libvlc-x86.zip");
                bool zipExists = File.Exists(zip);
                long zipLen = zipExists ? SafeLen(zip) : 0;
                Logger.Diag("[VLC] zip path=" + zip + " exists=" + zipExists + " size=" + zipLen);
                if (!zipExists) { Logger.Diag("[VLC] EXTRACT ABORT: bundled zip MISSING (copy " + Path.GetFileName(zip) + " beside the exe on RT)"); Logger.Diag("[VLC] bootstrap END in " + swBoot.ElapsedMilliseconds + "ms (aborted: zip missing)"); return; }

                string target = FirstWritableBase();   // logs each candidate base's write-probe result
                if (target == null) { Logger.Diag("[VLC] EXTRACT ABORT: NO writable base (beside-exe/Documents/AppData all failed the write-probe)"); Logger.Diag("[VLC] bootstrap END in " + swBoot.ElapsedMilliseconds + "ms (aborted: no writable base)"); return; }
                Logger.Diag("[VLC] extract target=" + target + " freeDisk=" + FreeDiskMB(target) + "MB → extracting " + Path.GetFileName(zip));

                bool ok = false;
                using (var splash = new PrepForm(() => { ok = Extract(zip, target, want); }))
                    splash.ShowDialog();

                if (ok)
                {
                    string folder = Path.Combine(target, "libvlc");
                    VlcEnvironment.UseFolder(folder);
                    Logger.Diag("[VLC] LibVLC will init from " + folder);
                }
                else Logger.Diag("[VLC] extraction failed → media unavailable until resolved");
                Logger.Diag("[VLC] bootstrap END in " + swBoot.ElapsedMilliseconds + "ms (FIRST-RUN extraction path, ok=" + ok + ")");
            }
            catch (Exception ex) { Logger.Diag("[VLC] bootstrap error: " + ex.Message); }
        }

        // ── arch + candidate folders ──
        private static string ProcessArch()
        {
            // A 32-bit process reports "x86" on Intel (incl. WOW64) and "ARM" on ARM32 Windows (RT 8.1).
            string a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            return a.StartsWith("ARM", StringComparison.OrdinalIgnoreCase) ? "arm" : "x86";
        }

        private static string BesideExe { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>Where a libvlc/ folder may live, in lookup priority: beside the exe (co-located with the
        /// zips), then Documents, then LocalAppData — the RT-safe Documents-first probe order.</summary>
        private static IEnumerable<string> CandidateBases()
        {
            yield return BesideExe;
            string docs = Safe(Environment.SpecialFolder.MyDocuments);
            if (docs != null) yield return Path.Combine(docs, "TelegArm");
            string lad = Safe(Environment.SpecialFolder.LocalApplicationData);
            if (lad != null) yield return Path.Combine(lad, "TelegArm");
        }

        private static string FirstWritableBase()
        {
            string chosen = null;
            foreach (var b in CandidateBases())
            {
                bool w = CanWrite(b);
                Logger.Diag("[VLC] write-probe " + b + " => " + (w ? "WRITABLE" : "no"));
                if (w && chosen == null) chosen = b;   // keep probing the rest for the log, but use the first writable
            }
            return chosen;
        }

        private static long SafeLen(string p) { try { return new FileInfo(p).Length; } catch { return -1; } }

        private static long FreeDiskMB(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace / (1024 * 1024); }
            catch { return -1; }
        }

        private static string Safe(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return null; } }

        private static bool HasCoreDlls(string folder)
        {
            return File.Exists(Path.Combine(folder, "libvlc.dll")) && File.Exists(Path.Combine(folder, "libvlccore.dll"));
        }

        private static string ReadMarker(string folder)
        {
            try { string p = Path.Combine(folder, MarkerName); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
            catch { return null; }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x"); File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // ── extraction (temp-then-rename; never leaves a half-written libvlc/) ──
        private static bool Extract(string zipPath, string baseDir, string marker)
        {
            string finalFolder = Path.Combine(baseDir, "libvlc");
            string tmp = Path.Combine(baseDir, ".vlc_extract_" + Guid.NewGuid().ToString("N").Substring(0, 8));   // short tmp name → extra MAX_PATH headroom
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // NO timeout — extraction is a one-time first-launch cost; a slow RT eMMC must be allowed to FINISH.
            Logger.Diag("[VLC] extract START (no timeout) → " + tmp);
            try
            {
                // The zip root already CONTAINS a "libvlc" folder → extracting into tmp yields tmp/libvlc/.
                // Manual entry-by-entry via ZipArchive (System.IO.Compression) — the SAME API the emoji engine
                // uses fine on RT — instead of ZipFile.ExtractToDirectory (System.IO.Compression.FileSystem,
                // untested on RT). It logs WHICH entry/path fails and skips a bad entry instead of a silent
                // whole-extract failure.
                ExtractZipManual(zipPath, tmp);
                string staged = Path.Combine(tmp, "libvlc");
                if (!HasCoreDlls(staged)) { Logger.Diag("[VLC] EXTRACT FAILED after " + sw.ElapsedMilliseconds + "ms: staged folder missing core DLLs (" + staged + ")"); SafeDeleteDir(tmp); return false; }

                File.WriteAllText(Path.Combine(staged, MarkerName), marker);

                if (Directory.Exists(finalFolder)) SafeDeleteDir(finalFolder);   // drop a stale/partial folder first
                Directory.Move(staged, finalFolder);                            // atomic swap-in (no doubled libvlc/)
                SafeDeleteDir(tmp);

                // BATCH-TA-0.1: same enumeration as before, now also summed for bytes — the device needs
                // ms AND size to make the first-run extraction cost interpretable (a slow eMMC vs a big payload).
                var extracted = Directory.GetFiles(finalFolder, "*", SearchOption.AllDirectories);
                int n = extracted.Length;
                long bytes = 0;
                foreach (var f in extracted) { try { bytes += new FileInfo(f).Length; } catch { } }
                long ms = sw.ElapsedMilliseconds;
                Logger.Diag("[VLC] extract DONE in " + ms + "ms (" + n + " files, " + bytes + " bytes = "
                    + (bytes / (1024 * 1024)) + " MB, " + (ms > 0 ? (bytes / 1024 / Math.Max(1, ms)).ToString() : "n/a")
                    + " KB/ms) → " + finalFolder);
                return HasCoreDlls(finalFolder);
            }
            catch (Exception ex)
            {
                Logger.Diag("[VLC] EXTRACT FAILED after " + sw.ElapsedMilliseconds + "ms: " + ex);   // full exception (incl. inner/stack)
                SafeDeleteDir(tmp);
                return false;
            }
        }

        /// <summary>Entry-by-entry extract via <see cref="ZipArchive"/> (System.IO.Compression — proven on RT
        /// by the emoji engine), NOT ZipFile.ExtractToDirectory. Logs each failed entry (name + full target
        /// path + its length + exception type/message; the first also gets a stack) so the real cause is
        /// visible in the RT [VLC] log, and skips a bad entry rather than failing the whole extract —
        /// <see cref="HasCoreDlls"/> then decides overall success.</summary>
        private static void ExtractZipManual(string zipPath, string destRoot)
        {
            Directory.CreateDirectory(destRoot);
            int ok = 0, failed = 0, total = 0, processed = 0, maxPath = 0;
            long bytes = 0;   // BATCH-TA-0.1: uncompressed bytes written — also reported when the extract FAILS
                              // and Extract's DONE line therefore never prints.
            var swEntries = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                total = archive.Entries.Count;
                foreach (var entry in archive.Entries)
                {
                    processed++;
                    string dest = Path.Combine(destRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }   // directory entry (trailing slash)
                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (dest.Length > maxPath) maxPath = dest.Length;
                        using (var es = entry.Open())
                        using (var os = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                            es.CopyTo(os);
                        ok++;
                        bytes += entry.Length;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (failed <= 3)   // first few in detail; avoids 390 noisy lines if the failure is systematic
                            Logger.Diag("[VLC] ENTRY FAILED (" + dest.Length + " chars) " + entry.FullName
                                + " → " + dest + " : " + ex.GetType().FullName + ": " + ex.Message
                                + (failed == 1 ? " | " + ex.StackTrace : ""));
                    }
                    if (processed % 130 == 0) Logger.Diag("[VLC] extracting… " + processed + "/" + total);
                }
            }
            Logger.Diag("[VLC] extract entries: " + ok + " ok, " + failed + " failed of " + total
                + " in " + swEntries.ElapsedMilliseconds + "ms, " + bytes + " bytes (longest dest=" + maxPath + " chars)");
        }

        private static void SafeDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ── one-time "Preparing…" splash (only shown while actually extracting) ──
        private sealed class PrepForm : Form
        {
            private readonly Action _work;

            public PrepForm(Action work)
            {
                _work = work;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(380, 132);
                TopMost = true; ShowInTaskbar = false; ControlBox = false;

                bool dark = ThemeHelper.IsDark;
                Color accent = ThemeHelper.GetWindowsAccentColor();
                BackColor = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);

                var bar = new Panel { Left = 0, Top = 0, Width = 380, Height = 6, BackColor = accent };
                var title = new Label { Left = 20, Top = 28, Width = 340, Height = 32, Text = "Preparing media components…", TextAlign = ContentAlignment.MiddleCenter, ForeColor = dark ? Color.White : Color.FromArgb(20, 20, 20), Font = new Font("Segoe UI", 11.5f, FontStyle.Bold) };
                var sub = new Label { Left = 20, Top = 62, Width = 340, Height = 24, Text = "Setting up audio and video. This happens only once.", TextAlign = ContentAlignment.MiddleCenter, ForeColor = dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110), Font = new Font("Segoe UI", 9f) };
                var pb = new ProgressBar { Left = 30, Top = 98, Width = 320, Height = 16, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
                Controls.AddRange(new Control[] { bar, title, sub, pb });
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { _work(); } catch { }
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Close); } catch { }
                });
            }
        }
    }
}
