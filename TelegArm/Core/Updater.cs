using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TelegArm.Helpers;

namespace TelegArm.Core
{
    /// <summary>
    /// Owner 2026-11-15: the update check, the network half (UpdateCheck is the pure half). Asks GitHub for the newest
    /// TelegArm release; when it is newer, <see cref="Found"/> makes the main window show an Update pill in the title bar.
    /// A click downloads the installer into %TEMP%\TelegArm-update, checks it against the release's SHA256SUMS.txt (a
    /// damaged or swapped file is never run), starts it in update mode ("Setup.exe --update &lt;folder&gt; --wait &lt;pid&gt;": it
    /// waits for TelegArm to close, installs over it and starts it again) and the app exits. A portable copy (not where
    /// Setup's uninstall entry points) gets the release page instead: it was unpacked by hand and is replaced by hand.
    /// Requests carry a generic User-Agent (GitHub's API needs one) and nothing about the user. Never in the --shoot rigs
    /// unless the update rig points it at its own local server (TELEGARM_UPDATE_URL / TELEGARM_UPDATE_PREFIX).
    /// </summary>
    public static class Updater
    {
        private const string UserAgent = "TelegArm-Updater";

        private static string Env(string name)
        {
            try { string v = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? null : v; } catch { return null; }
        }

        private static string LatestUrl { get { return Env("TELEGARM_UPDATE_URL") ?? UpdateCheck.LatestApi; } }
        private static string AllowedPrefix { get { return Env("TELEGARM_UPDATE_PREFIX") ?? UpdateCheck.DownloadPrefix; } }

        /// <summary>This build's version (major.minor.build).</summary>
        public static Version Current { get { return UpdateCheck.ParseVersion(Program.Version) ?? new Version(0, 0, 0); } }

        /// <summary>The newer release found last (null: none yet).</summary>
        public static ReleaseInfo Pending { get; private set; }

        /// <summary>A newer release was found (the background check, or Settings' "Check for updates").</summary>
        public static event Action<ReleaseInfo> Found;

        public static void RaiseFound(ReleaseInfo r)
        {
            if (r == null) return;
            Pending = r;
            var h = Found;
            if (h != null) h(r);
        }

        /// <summary>The newest release when it is newer than this build, else null. Throws on a network error: the caller
        /// decides whether to say so (the background check stays silent).</summary>
        public static async Task<ReleaseInfo> CheckAsync()
        {
            EnableTls12();
            string json = await GetStringAsync(LatestUrl, 15000).ConfigureAwait(false);
            var r = UpdateCheck.Parse(json, AllowedPrefix);
            if (r == null) { Log("the latest release could not be read"); return null; }
            bool newer = UpdateCheck.IsNewer(r.Version, Current);
            Log("latest " + r.Tag + ", this build " + Program.Version + ": " + (newer ? "NEWER" : "up to date")
                + "; installer " + (r.SetupName ?? "none") + ", checksums " + (r.SumsUrl != null ? "yes" : "no"));
            return newer ? r : null;
        }

        /// <summary>Downloads the release's installer and checks it against its SHA256SUMS.txt; returns its path.</summary>
        public static async Task<string> DownloadSetupAsync(ReleaseInfo r, IProgress<int> progress, CancellationToken ct)
        {
            if (r == null || r.SetupUrl == null) throw new InvalidOperationException("This release has no installer.");
            if (r.SumsUrl == null) throw new InvalidDataException("This release has no SHA256SUMS.txt, so the download cannot be checked.");
            EnableTls12();
            string dir = Path.Combine(Path.GetTempPath(), "TelegArm-update");
            Directory.CreateDirectory(dir);
            string sums = await GetStringAsync(r.SumsUrl, 15000).ConfigureAwait(false);
            string want = UpdateCheck.SumFor(sums, r.SetupName);
            if (want == null) throw new InvalidDataException("SHA256SUMS.txt does not list " + r.SetupName + ".");
            string dest = Path.Combine(dir, r.SetupName), part = dest + ".part";
            await DownloadAsync(r.SetupUrl, part, r.SetupSize, progress, ct).ConfigureAwait(false);
            string got = Sha256(part);
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(part); } catch { }
                Log("checksum mismatch for " + r.SetupName + ": got " + got + ", want " + want);
                throw new InvalidDataException("The download is damaged (its checksum does not match). Try again.");
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
            Log("downloaded and checked " + r.SetupName);
            return dest;
        }

        /// <summary>The folder Setup installed this copy into, or null for a portable copy (Setup's uninstall entry must
        /// point at the folder this exe runs from).</summary>
        public static string InstallDir()
        {
            if (TestInstallDir != null) return TestInstallDir;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TelegArm"))
                {
                    string loc = k != null ? k.GetValue("InstallLocation") as string : null;
                    if (string.IsNullOrEmpty(loc)) return null;
                    string here = Norm(AppDomain.CurrentDomain.BaseDirectory);
                    return string.Equals(Norm(loc), here, StringComparison.OrdinalIgnoreCase) ? here : null;
                }
            }
            catch { return null; }
        }

        /// <summary>The update rig: pretend this copy is installed in that folder.</summary>
        internal static string TestInstallDir;

        /// <summary>The update rig: called instead of starting the installer (path, arguments).</summary>
        internal static Action<string, string> LaunchOverride;

        /// <summary>Starts the downloaded installer in update mode. It waits for this process to end before it touches a
        /// file, so the caller exits next.</summary>
        public static void LaunchInstaller(string setupPath, string installDir)
        {
            string args = "--update \"" + installDir + "\" --wait " + Process.GetCurrentProcess().Id;
            if (LaunchOverride != null) { LaunchOverride(setupPath, args); return; }
            Process.Start(new ProcessStartInfo(setupPath, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setupPath) });
            Log("installer started: " + args);
        }

        // ── plumbing ──────────────────────────────────────────────────────────────────────────────────────────

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return (p ?? "").TrimEnd('\\', '/'); }
        }

        private static void EnableTls12()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }   // GitHub accepts TLS 1.2 or later only
        }

        private static HttpWebRequest NewRequest(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json, */*";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        /// <summary>GetResponseAsync ignores HttpWebRequest.Timeout: the timeout is enforced here.</summary>
        private static async Task<HttpWebResponse> RespondAsync(HttpWebRequest req, int timeoutMs, CancellationToken ct)
        {
            var t = req.GetResponseAsync();
            var done = await Task.WhenAny(t, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (done != t)
            {
                try { req.Abort(); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("GitHub did not answer in time.");
            }
            return (HttpWebResponse)await t.ConfigureAwait(false);
        }

        private static async Task<string> GetStringAsync(string url, int timeoutMs)
        {
            var req = NewRequest(url, timeoutMs);
            using (var resp = await RespondAsync(req, timeoutMs, CancellationToken.None).ConfigureAwait(false))
            using (var s = resp.GetResponseStream())
            using (var rd = new StreamReader(s, Encoding.UTF8))
                return await rd.ReadToEndAsync().ConfigureAwait(false);
        }

        private static async Task DownloadAsync(string url, string dest, long expected, IProgress<int> progress, CancellationToken ct)
        {
            var req = NewRequest(url, 30000);
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            using (var resp = await RespondAsync(req, 30000, ct).ConfigureAwait(false))
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : expected, got = 0;
                int last = -1, n;
                var buf = new byte[81920];
                while ((n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    got += n;
                    if (total > 0 && progress != null)
                    {
                        int pct = (int)Math.Min(100, got * 100 / total);
                        if (pct != last) { last = pct; progress.Report(pct); }
                    }
                }
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void Log(string m) { Logger.Diag("[UPDATE] " + m); }
    }
}
