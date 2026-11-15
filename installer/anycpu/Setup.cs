// =============================================================================
//  TelegArm installer (Setup.cs) — ONE EXE, payload embedded. Redesign step 0.5.
//
//  Ported from DroidFinestra's installer (installer/Setup.cs @ 3708786), which itself grew out of this one.
//  MSIL so it RUNS ON WINDOWS RT 8.1 (ARM32) as well as x86/x64/ARM64 — an Inno Setup loader is native x86
//  and cannot start on RT.
//
//  From DroidFinestra:
//   * .NET Framework 4.0, Client-Profile assemblies only, so it starts on RT 8.1's in-box 4.5 and on a bare
//     4.0 machine, where it can still say what is missing. 4.0 has no ZipArchive and no async, so the app is
//     EMBEDDED in this exe as gzip resources ("payload/<path>.gz") listed with SHA-256 in payload.list, every
//     file is verified as it is written, and work runs on a plain thread. One file to ship.
//   * A requirements report, on demand ("Check requirements", or --check). It informs; it never gates.
//   * Uninstall removes a shortcut / registry entry only when it points INTO the folder being removed.
//  Kept from TelegArm's previous installer:
//   * The Start-menu shortcut carries the AppUserModelID AND the toast-activator CLSID, and the CLSID's
//     LocalServer32 is registered — without them Action Center notifications silently never arrive.
//   * The .NET 4.7 messaging, with the Microsoft link RT 8.1 users actually need.
//  New (redesign plan step 0.5):
//   * A RUNNING TelegArm blocks install and uninstall with "close it, then Retry" (exit 4 when silent). It is
//     never killed. Detected by its single-instance mutex, or by a TelegArm.exe running from the folder.
//   * Uninstall KEEPS accounts and settings unless the user says otherwise (default No; --purge when silent).
//     The old uninstall deleted them with no question asked, even from Settings → Apps.
//   * Uninstall removes the "start with Windows" Run value when it points into the folder.
//   * NEVER A RECURSIVE DELETE OF THE INSTALL FOLDER. uninstall.manifest — written FIRST, before any file —
//     lists every file the install places; upgrade and uninstall delete exactly those (plus the libvlc\ the app
//     extracts beside itself), and the folder goes only if that leaves it empty. It refuses to install into a
//     non-empty folder that is not TelegArm's, or a drive root / well-known folder, and refuses to uninstall a
//     folder that is not TelegArm's. (DroidFinestra's rmdir /s, gated on "uninstall.exe is here", would have let
//     Setup renamed to uninstall.exe empty Downloads.)
//   * uninstall.exe is a payload-free build of this same file (the build embeds it), not a copy of this
//     ~130 MB exe.
//
//  Modes:
//    (no args)                          install wizard, with a "Check requirements" button
//    --check [file]                     requirements report only, to file (or a dialog); installs nothing.
//                                       exit 0 all present, 3 something missing or old, 2 cannot run here
//    --silent [dir] [--no-desktop]      install without UI. exit 0 ok, 4 TelegArm is running, 1 error
//    --update <dir> --wait <pid>        TelegArm's own update (2026-11-15): a small progress window waits for the
//                                       process <pid> (the TelegArm that downloaded this) to end, installs over <dir>
//                                       (the desktop shortcut kept as it was) and starts TelegArm again.
//                                       TELEGARM_SETUP_DRYRUN=1: no registry, no shortcuts, no start (the rig).
//    --uninstall <dir> [--silent] [--purge]
//                                       remove the install. Without --silent it asks, including whether to
//                                       delete the user's data (default No). exit 0 ok/cancelled, 4 running, 1 error
//    (run as uninstall.exe)             the same as --uninstall <its own folder>
//
//  Built ONLY by installer\anycpu\build.ps1, which also generates the Info class (version) — the version is
//  read from the built TelegArm.exe, so there is no second copy to keep in sync.
//  C# 5, .NET 4.0 Client: keep it that way (see build.ps1). Never share code with the app.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TelegArmSetup
{
    internal static class Program
    {
        internal const string AppName = "TelegArm";
        internal const string InstallFolderName = "TelegArm";
        internal const string ExeName = "TelegArm.exe";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TelegArm";
        internal const string ShortcutDesc = "TelegArm - Telegram client";
        internal const string Publisher = "Hamed (hamed7ir)";

        /// <summary>⚠ MUST EQUAL TelegArm's Program.MutexName — it is how a running TelegArm is detected.</summary>
        internal const string MutexName = @"Global\TelegArm_SingleInstance";

        /// <summary>The app's "Start when Windows starts" value (SettingsForm.StartupRunKey / StartupValueName).</summary>
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunValue = "TelegArm";

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = HasFlag(args, "--silent");
            try
            {
                string a0 = args.Length >= 1 ? args[0] : "";
                if (!silent || Eq(a0, "--check"))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                }

                // ⚠ BEFORE the requirements are looked at, deliberately: removing TelegArm must keep working on
                //   a machine whose .NET was uninstalled first.
                if (Eq(a0, "--uninstall"))
                {
                    string dir = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : OwnDir();
                    return UninstallFlow(dir, silent, HasFlag(args, "--purge"));
                }
                if (Eq(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "uninstall"))
                    return UninstallFlow(OwnDir(), silent, HasFlag(args, "--purge"));

                if (!Payload.Present())
                {
                    Say(silent, "This is TelegArm's uninstaller, not its installer. To remove TelegArm use Settings > Apps, "
                                + "or run uninstall.exe in TelegArm's folder.", MessageBoxIcon.Information);
                    return 1;
                }

                if (Eq(a0, "--update"))
                {
                    string dir = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : DefaultDir();
                    int pid = 0;
                    for (int i = 0; i + 1 < args.Length; i++)
                        if (Eq(args[i], "--wait")) int.TryParse(args[i + 1], out pid);
                    Application.Run(new UpdateForm(Clean(dir), pid));
                    return UpdateForm.ExitCode;
                }

                if (Eq(a0, "--check"))
                {
                    var rep = Requirements.Check(null);
                    string text = Requirements.Format(rep);
                    if (args.Length >= 2) File.WriteAllText(args[1], text, Encoding.UTF8);
                    else MessageBox.Show(text, AppName + " " + Info.AppVersion + " - requirements");
                    return Requirements.ExitCode(rep);
                }

                // --silent anywhere on the line means a silent install (review: "Setup.exe D:\x --silent" used to
                // open the wizard with every error message suppressed).
                if (silent)
                {
                    string dir = DefaultDir();
                    bool desktop = true;
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (Eq(args[i], "--no-desktop")) desktop = false;
                        else if (!args[i].StartsWith("--")) dir = args[i];
                    }
                    string busy = InUse.Describe(dir);
                    if (busy != null) { Console.Error.WriteLine(busy); return 4; }
                    string note = Requirements.RuntimeNote();
                    if (note != null) Console.Error.WriteLine(note);   // informs; a scripted install still proceeds
                    Installer.Install(dir, desktop, null);
                    return 0;
                }

                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception ex)
            {
                Say(silent, ex.ToString(), MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>Asks (unless silent), waits for a running TelegArm to be closed, then uninstalls.</summary>
        private static int UninstallFlow(string dir, bool silent, bool purge)
        {
            dir = Clean(dir);
            string why = Uninstaller.NotAnInstall(dir);
            if (why != null)
            {
                Say(silent, "Nothing was removed: " + why, MessageBoxIcon.Warning);
                return 1;
            }
            if (!silent)
            {
                if (MessageBox.Show("Remove " + AppName + " from this computer?", "Uninstall " + AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                // ⚠ DEFAULT NO. Deleting this signs every account out on this PC; it must be a choice.
                purge = MessageBox.Show(
                    "Also remove your TelegArm data from this PC?\n\n"
                    + "This signs out every account on this PC and deletes settings, logs and cached media. "
                    + "Your chats stay on Telegram, and files you saved yourself are kept.\n\n"
                    + "Choose No to keep it (best if you may install TelegArm again).",
                    "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                string busy;
                while ((busy = InUse.Describe(dir)) != null)
                    if (MessageBox.Show(busy + "\n\nClose it, then press Retry.", "Uninstall " + AppName,
                            MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return 4;
            }
            else
            {
                string busy = InUse.Describe(dir);
                if (busy != null) { Console.Error.WriteLine(busy); return 4; }
            }
            if (!Uninstaller.Run(dir, purge))
                Say(silent, AppName + " was unregistered, but its install list was missing, so its files in " + dir
                            + " were left in place. Delete that folder yourself if you no longer need it.", MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>Full path without a trailing backslash: "C:\x\TelegArm\" quoted in the UninstallString would read
        /// as an escaped quote (review), and every Inside() comparison assumes no trailing separator.</summary>
        internal static string Clean(string dir)
        {
            try { return Path.GetFullPath(dir.Trim()).TrimEnd('\\'); } catch { return dir; }
        }

        private static void Say(bool silent, string text, MessageBoxIcon icon)
        {
            if (silent) { try { Console.Error.WriteLine(text); } catch { } return; }
            try { MessageBox.Show(text, AppName + " Setup", MessageBoxButtons.OK, icon); } catch { }
        }

        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        /// <summary>TELEGARM_SETUP_DRYRUN=1 (the update rig): files only; no registry, no shortcuts, TelegArm not started.</summary>
        internal static bool DryRun { get { return Environment.GetEnvironmentVariable("TELEGARM_SETUP_DRYRUN") == "1"; } }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args) if (Eq(a, flag)) return true;
            return false;
        }

        private static string OwnDir() { return Path.GetDirectoryName(Application.ExecutablePath); }

        internal static string DefaultDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(p, "Programs"), InstallFolderName);
        }
    }

    // ── payload: embedded gzip resources + payload.list (sha256 TAB size TAB relative path [TAB N = native PE]) ──
    internal sealed class PayloadFile
    {
        public string Name, Sha256;
        public long Size;
        public bool Native;   // set by build.ps1: a PE image with no CLR header — the only files the check parses
    }

    internal static class Payload
    {
        internal static bool Present()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.list")) return s != null;
        }

        internal static List<PayloadFile> List()
        {
            var list = new List<PayloadFile>();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.list"))
            {
                if (s == null) throw new InvalidDataException("This Setup has no payload.list - it was not built by installer\\anycpu\\build.ps1.");
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var f = line.Split('\t');
                        if (f.Length < 3) continue;
                        list.Add(new PayloadFile { Sha256 = f[0], Size = long.Parse(f[1]), Name = f[2], Native = f.Length > 3 && f[3] == "N" });
                    }
                }
            }
            return list;
        }

        internal static Stream Open(PayloadFile f)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + f.Name.Replace('\\', '/') + ".gz");
            if (s == null) throw new InvalidDataException("Payload resource missing: " + f.Name);
            return new GZipStream(s, CompressionMode.Decompress);
        }

        internal static byte[] ReadAll(PayloadFile f)
        {
            using (var s = Open(f))
            using (var m = new MemoryStream())
            {
                Copy(s, m);
                return m.ToArray();
            }
        }

        internal static void Copy(Stream from, Stream to)
        {
            var buf = new byte[81920];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
        }

        internal static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // ── requirements ─────────────────────────────────────────────────────────────────────────────────────────
    internal enum Level { Ok, Warn, Missing, Blocked }

    internal sealed class Req
    {
        public Level Level;
        public string Text, Fix, Url;
    }

    internal static class Requirements
    {
        internal const ushort X86 = 0x14c, X64 = 0x8664, ArmNt = 0x1c4, Arm = 0x1c0, Arm64 = 0xaa64;

        /// <summary>4.7 RTM's release number on Windows 10 1703 — every later 4.x is higher. Used to INFORM only:
        /// a 4.7-targeted app runs on an older 4.x unless it calls a 4.7-only API (TA-42: a Windows 10 ARM32
        /// device reporting 460788 was refused by an older installer while TelegArm ran on it perfectly).</summary>
        internal const int Net47Release = 460798;

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
            public byte wProductType, wReserved;
        }
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OSVERSIONINFOEX v);

        /// <summary>Real OS version (Environment.OSVersion stops at 6.2 for an unmanifested app on 8.1+).</summary>
        internal static Version OsVersion()
        {
            try
            {
                var v = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX)) };
                if (RtlGetVersion(ref v) == 0) return new Version(v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.Version;
        }

        /// <summary>"AMD64" | "X86" | "ARM" | "ARM64" — the OS, not this process.</summary>
        internal static string OsArch()
        {
            return (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                    ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
        }

        internal static string MachineName(ushort m)
        {
            switch (m)
            {
                case X86: return "x86";
                case X64: return "x64";
                case ArmNt: case Arm: return "ARM32";
                case Arm64: return "ARM64";
                default: return "machine 0x" + m.ToString("X");
            }
        }

        private static string OsArchName(string a)
        {
            switch (a) { case "AMD64": return "x64"; case "X86": return "x86"; case "ARM": return "ARM32"; case "ARM64": return "ARM64"; default: return a; }
        }

        /// <summary>Null when this Windows can run programs of machine type <paramref name="m"/>, else why not.</summary>
        internal static string CannotRun(ushort m, string os, Version ver)
        {
            switch (m)
            {
                case X86: return os == "ARM" ? "ARM32 Windows cannot run x86 code" : null;
                case X64:
                    if (os == "AMD64") return null;
                    if (os == "ARM64") return ver.Build >= 22000 ? null : "Windows 10 on ARM64 cannot run x64 code (Windows 11 can)";
                    return OsArchName(os) + " Windows cannot run x64 code";
                case ArmNt: case Arm:
                    if (os == "ARM") return null;
                    if (os == "ARM64") return ver.Build >= 26100 ? "Windows 11 24H2 and later no longer run 32-bit ARM code" : null;
                    return OsArchName(os) + " Windows cannot run ARM32 code";
                case Arm64: return os == "ARM64" ? null : OsArchName(os) + " Windows cannot run ARM64 code";
                default: return "unknown machine type 0x" + m.ToString("X");
            }
        }

        /// <summary>The folder a binary of machine type <paramref name="m"/> loads system DLLs from on this Windows.</summary>
        internal static string SystemDirFor(ushort m, string os)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // a 32-bit Setup on 64-bit Windows sees SysWOW64 as System32; Sysnative is the real one
            string native = Path.Combine(win, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32");
            switch (m)
            {
                case X86: return os == "X86" ? native : Path.Combine(win, "SysWOW64");
                case ArmNt: case Arm: return os == "ARM" ? native : Path.Combine(win, "SysArm32");
                default: return native;   // x64 on x64 or ARM64 (ARM64X System32), ARM64 on ARM64
            }
        }

        /// <summary>What a DLL name is: the Universal CRT, a Visual C++ runtime, an API-set contract (resolved by
        /// Windows itself), or an ordinary Windows DLL.</summary>
        internal static string Kind(string dll)
        {
            string d = dll.ToLowerInvariant();
            if (d.StartsWith("api-ms-win-crt-") || d == "ucrtbase.dll") return "ucrt";
            if (d.StartsWith("vcruntime") || d.StartsWith("msvcp1") || d.StartsWith("msvcr1") || d.StartsWith("concrt1")
                || d.StartsWith("vcomp1") || d.StartsWith("vccorlib")) return "vc";
            if (d.StartsWith("api-ms-") || d.StartsWith("ext-ms-")) return "apiset";
            return "system";
        }

        /// <summary>Machine type + imported DLL names of a PE image; false if it is not one.</summary>
        internal static bool ParsePe(byte[] b, out ushort machine, out List<string> imports)
        {
            machine = 0; imports = new List<string>();
            try
            {
                if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return false;
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe < 0 || pe + 24 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0) return false;
                machine = BitConverter.ToUInt16(b, pe + 4);
                int nsec = BitConverter.ToUInt16(b, pe + 6);
                int optSize = BitConverter.ToUInt16(b, pe + 20);
                int opt = pe + 24;
                bool plus = BitConverter.ToUInt16(b, opt) == 0x20b;
                int numDirs = BitConverter.ToInt32(b, opt + (plus ? 108 : 92));
                if (numDirs < 2) return true;
                uint impRva = BitConverter.ToUInt32(b, opt + (plus ? 112 : 96) + 8);
                if (impRva == 0) return true;
                int secs = opt + optSize;
                Func<uint, int> off = rva =>
                {
                    for (int i = 0; i < nsec; i++)
                    {
                        int s = secs + 40 * i;
                        uint vs = BitConverter.ToUInt32(b, s + 8), va = BitConverter.ToUInt32(b, s + 12);
                        uint raw = BitConverter.ToUInt32(b, s + 16), ptr = BitConverter.ToUInt32(b, s + 20);
                        uint size = Math.Max(vs, raw);
                        if (rva >= va && rva < va + size) return (int)(rva - va + ptr);
                    }
                    return -1;
                };
                for (int d = off(impRva); d >= 0 && d + 20 <= b.Length && imports.Count < 1024; d += 20)
                {
                    uint nameRva = BitConverter.ToUInt32(b, d + 12), first = BitConverter.ToUInt32(b, d + 16);
                    if (nameRva == 0 && first == 0) break;
                    int n = off(nameRva);
                    if (n < 0) break;
                    int e = n;
                    while (e < b.Length && b[e] != 0) e++;
                    imports.Add(Encoding.ASCII.GetString(b, n, e - n));
                }
                return true;
            }
            catch { return true; }   // a truncated table: keep what was read
        }

        private static string RedistUrl(ushort m)
        {
            switch (m)
            {
                case X86: return "https://aka.ms/vs/17/release/vc_redist.x86.exe";
                case X64: return "https://aka.ms/vs/17/release/vc_redist.x64.exe";
                case Arm64: return "https://aka.ms/vs/17/release/vc_redist.arm64.exe";
                default: return null;   // Microsoft publishes no current redistributable for ARM32
            }
        }

        // ── .NET Framework ───────────────────────────────────────────────────────────────────────────────────
        /// <summary>Reads the .NET 4 Full key. <paramref name="readable"/> false = the registry could not be read,
        /// which must never produce a scary message (fail open).</summary>
        internal static bool NetFull(out int release, out bool readable)
        {
            release = 0; readable = true;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k == null) return false;
                    object inst = k.GetValue("Install"), rel = k.GetValue("Release");
                    if (rel is int) release = (int)rel;
                    return (inst is int && (int)inst == 1) || release > 0;
                }
            }
            catch { readable = false; return true; }
        }

        internal static bool IsArm() { return OsArch().StartsWith("ARM"); }

        /// <summary>⚠ The link must match the architecture: Microsoft's fwlink below is what RT 8.1 users install
        /// (the README carries the same link). Windows 10 ARM32 already has 4.7.</summary>
        internal static string RuntimeUrl()
        {
            return IsArm() ? "https://go.microsoft.com/fwlink/?linkid=2088632" : "https://dotnet.microsoft.com/download/dotnet-framework";
        }

        /// <summary>Null when .NET 4.7+ is present (or unreadable), else a sentence for the install-time prompt.
        /// It informs; the user decides.</summary>
        internal static string RuntimeNote()
        {
            int release; bool readable;
            bool full = NetFull(out release, out readable);
            if (!readable || (full && release >= Net47Release) || (full && release == 0)) return null;
            string link = "\r\n\r\n    " + RuntimeUrl() + (IsArm() ? "\r\n    (Windows RT 8.1 ships .NET 4.5 and needs this; Windows 10 ARM32 already has it.)" : "");
            if (!full)
                return "TelegArm needs the Microsoft .NET Framework 4.7 or later, and this PC does not have .NET Framework 4 "
                     + "installed, so TelegArm will not start until it is. Download it here:" + link;
            return "This PC has .NET Framework " + NetName(release) + " (release " + release + "), which is older than 4.7. "
                 + "TelegArm usually still runs on an older 4.x. If it does not start, install .NET 4.7 or later:" + link;
        }

        /// <summary>Checks this machine against the payload. <paramref name="exists"/> answers "is this file there"
        /// (null = the real file system) — it lets the check be tested against a machine that lacks a runtime.</summary>
        internal static List<Req> Check(Func<string, bool> exists)
        {
            if (exists == null) exists = File.Exists;
            var reqs = new List<Req>();
            string os = OsArch();
            Version ver = OsVersion();
            reqs.Add(new Req { Level = Level.Ok, Text = "Windows " + ver + " on " + OsArchName(os) });

            int release; bool readable;
            bool full = NetFull(out release, out readable);
            if (!readable) reqs.Add(new Req { Level = Level.Warn, Text = "Could not read the .NET Framework version from the registry" });
            else if (!full)
                reqs.Add(new Req { Level = Level.Missing, Text = ".NET Framework 4 (full) is not installed - " + Program.AppName + " will not start",
                                   Fix = "Install .NET Framework 4.7 or later", Url = RuntimeUrl() });
            else if (release > 0 && release < Net47Release)
                reqs.Add(new Req { Level = Level.Warn, Text = ".NET Framework " + NetName(release) + " (release " + release + ") - "
                                   + Program.AppName + " targets 4.7 and usually still runs on an older 4.x",
                                   Fix = "If " + Program.AppName + " does not start, install .NET Framework 4.7 or later", Url = RuntimeUrl() });
            else reqs.Add(new Req { Level = Level.Ok, Text = ".NET Framework " + NetName(release) + " (full)" });

            // ⚠ NATIVE LIBRARIES COME IN ONE BUILD PER CPU (rlottie\x86, rlottie\ARM) and the app loads the one that
            //   matches. So a file NAME is fine when ANY of its builds can run here; only those builds' imports are
            //   checked. Judging every build would report the ARM one "cannot run" on every x64 PC, and vice versa.
            var groups = new SortedDictionary<string, List<PayloadFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Payload.List())
            {
                if (!f.Native) continue;
                List<PayloadFile> g;
                string key = Path.GetFileName(f.Name);
                if (!groups.TryGetValue(key, out g)) groups[key] = g = new List<PayloadFile>();
                g.Add(f);
            }
            var needs = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // "kind|machine|dll" -> users
            foreach (var kv in groups)
            {
                var runnable = new List<string>(); var reasons = new List<string>();
                foreach (var f in kv.Value)
                {
                    ushort m; List<string> imps;
                    if (!ParsePe(Payload.ReadAll(f), out m, out imps)) continue;
                    string why = CannotRun(m, os, ver);
                    if (why != null) { reasons.Add(MachineName(m) + ": " + why); continue; }
                    runnable.Add(MachineName(m));
                    foreach (var dll in imps)
                    {
                        string kind = Kind(dll);
                        if (kind == "apiset") continue;
                        string key = kind + "|" + m + "|" + (kind == "ucrt" ? "ucrtbase.dll" : dll.ToLowerInvariant());
                        List<string> users;
                        if (!needs.TryGetValue(key, out users)) needs[key] = users = new List<string>();
                        if (!users.Contains(kv.Key)) users.Add(kv.Key);
                    }
                }
                if (runnable.Count > 0)
                    reqs.Add(new Req { Level = Level.Ok, Text = kv.Key + ": the " + string.Join(", ", runnable.ToArray()) + " build can run here" });
                else
                    reqs.Add(new Req { Level = Level.Blocked, Text = kv.Key + ": none of the bundled builds can run here (" + string.Join("; ", reasons.ToArray()) + ")",
                                       Fix = Program.AppName + " may still start, but whatever uses this library will not work" });
            }

            int sysChecked = 0, sysMissing = 0;
            foreach (var kv in needs)
            {
                var p = kv.Key.Split('|');
                string kind = p[0]; ushort m = ushort.Parse(p[1]); string dll = p[2];
                bool ok = exists(Path.Combine(SystemDirFor(m, os), dll));
                if (kind == "system") { sysChecked++; if (!ok) sysMissing++; }
                string by = " - needed by " + string.Join(", ", kv.Value.ToArray());
                if (kind == "ucrt")
                    reqs.Add(ok
                        ? new Req { Level = Level.Ok, Text = "Universal C Runtime (" + MachineName(m) + ")" + by }
                        : new Req { Level = Level.Missing, Text = "Universal C Runtime (" + MachineName(m) + ") is missing" + by,
                                    Fix = "Install the Microsoft Visual C++ 2015-2022 Redistributable (" + MachineName(m) + "), which adds it on Windows 7/8.1, or Windows Update KB2999226",
                                    Url = RedistUrl(m) });
                else if (kind == "vc")
                    reqs.Add(ok
                        ? new Req { Level = Level.Ok, Text = "Visual C++ runtime " + dll + " (" + MachineName(m) + ")" + by }
                        : new Req { Level = Level.Missing, Text = "Visual C++ runtime " + dll + " (" + MachineName(m) + ") is missing" + by,
                                    Fix = "Install the Microsoft Visual C++ 2015-2022 Redistributable (" + MachineName(m) + ")", Url = RedistUrl(m) });
                else if (!ok)   // an ordinary Windows DLL: only worth a line when it is absent
                    reqs.Add(new Req { Level = Level.Missing, Text = "Windows component " + dll + " (" + MachineName(m) + ") is missing" + by,
                                       Fix = "This Windows edition lacks it; the library that needs it will not load" });
            }
            if (sysChecked > 0 && sysMissing == 0)
                reqs.Add(new Req { Level = Level.Ok, Text = "Windows components those libraries import: all " + sysChecked + " present" });
            return reqs;
        }

        internal static string NetName(int release)
        {
            if (release >= 533320) return "4.8.1";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release >= 379893) return "4.5.2";
            if (release >= 378675) return "4.5.1";
            if (release >= 378389) return "4.5";
            return "4.0";
        }

        internal static int ExitCode(List<Req> reqs)
        {
            int code = 0;
            foreach (var r in reqs)
            {
                if (r.Level == Level.Blocked) return 2;
                if (r.Level == Level.Missing || r.Level == Level.Warn) code = 3;
            }
            return code;
        }

        internal static string Tag(Level l)
        {
            switch (l) { case Level.Ok: return "[ OK ]"; case Level.Warn: return "[WARN]"; case Level.Missing: return "[MISSING]"; default: return "[CANNOT RUN]"; }
        }

        internal static string Format(List<Req> reqs)
        {
            var sb = new StringBuilder();
            foreach (var r in reqs)
            {
                sb.Append(Tag(r.Level)).Append(' ').Append(r.Text.Replace("\n", "\r\n")).Append("\r\n");
                if (r.Fix != null) sb.Append("      -> ").Append(r.Fix).Append("\r\n");
                if (r.Url != null) sb.Append("      ").Append(r.Url).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    // ── a running TelegArm: its files are locked, and it must never be killed ───────────────────────────────
    internal static class InUse
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>Null when TelegArm is not running, else the sentence to show. Two signals, either is enough:
        /// the app's machine-wide single-instance mutex (any copy, any user — and with --startup in the Run key,
        /// "running in the tray" is the NORMAL state during an upgrade), and a TelegArm.exe whose image lives in
        /// <paramref name="dir"/> (catches a copy started without the mutex, such as a --shoot run).</summary>
        internal static string Describe(string dir)
        {
            if (!MutexHeld() && !RunningFrom(dir, "TelegArm")) return null;
            return Program.AppName + " is running. Close it first: right-click its icon in the notification area "
                   + "(next to the clock) and choose Exit.";
        }

        private static bool MutexHeld()
        {
            try { using (Mutex.OpenExisting(Program.MutexName)) return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }   // it exists; another user owns it
            catch { return false; }
        }

        private static string ImagePath(Process p)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        private static bool RunningFrom(string dir, string name)
        {
            if (!Directory.Exists(dir)) return false;
            bool hit = false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                string img = ImagePath(p);
                if (img != null && Uninstaller.Inside(img, dir)) hit = true;
                p.Dispose();
            }
            return hit;
        }
    }

    internal static class Installer
    {
        internal static void Install(string targetDir, bool desktopShortcut, Action<string> progress)
        {
            Report(progress, "Preparing...");
            targetDir = Program.Clean(targetDir);
            var files = Payload.List();
            var names = new List<string>();
            foreach (var f in files) names.Add(f.Name);
            names.Add("uninstall.exe");

            // ⚠ Upgrading replaces the previous version, so the target must be empty or TelegArm's (its manifest or
            //   TelegArm.exe) — never someone's folder that happens to be typed after --silent.
            string refuse = Uninstaller.NotReplaceable(targetDir);
            if (refuse != null) throw new InvalidOperationException(refuse);
            if (Directory.Exists(targetDir))
            {
                Report(progress, "Removing previous version...");
                // ⚠ NEVER A RECURSIVE WIPE (review): a portable copy unzipped flat into D:\Tools made all of D:\Tools
                //   "hold an earlier TelegArm". With a manifest: exactly the files it lists. Without one (a 1.x install,
                //   a loose portable copy): only names this package installs itself. Anything else stays where it is.
                Uninstaller.RemoveInstalledFiles(targetDir, false, names);
            }
            Directory.CreateDirectory(targetDir);

            // ⚠ THE UNINSTALLER AND THE MANIFEST GO IN FIRST (review). If extraction then fails — a full eMMC, a
            //   scanner holding a file, the power — the folder is still recognisably TelegArm's: Setup can retry into
            //   it and Settings > Apps can still remove it. Written last, a failure locked the user out of both.
            string exe = Path.Combine(targetDir, Program.ExeName);
            string uninst = Path.Combine(targetDir, "uninstall.exe");
            WriteUninstaller(uninst);
            File.WriteAllLines(Path.Combine(targetDir, Uninstaller.ManifestName), names.ToArray(), new UTF8Encoding(false));

            using (var sha = SHA256.Create())
            {
                int i = 0;
                foreach (var f in files)
                {
                    Report(progress, "Extracting " + (++i) + " of " + files.Count + ": " + f.Name);
                    string dest = Path.Combine(targetDir, f.Name);
                    string d = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    using (var src = Payload.Open(f))
                    using (var os = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var cs = new CryptoStream(os, sha, CryptoStreamMode.Write))
                    {
                        Payload.Copy(src, cs);
                        cs.FlushFinalBlock();
                        string got = Payload.Hex(sha.Hash);
                        if (!string.Equals(got, f.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(f.Name + " did not extract intact (SHA-256 " + got + ", expected " + f.Sha256 + ").");
                    }
                    sha.Initialize();
                }
            }

            if (Program.DryRun) { Report(progress, "Done (dry run: no shortcuts, no registry)."); return; }
            Report(progress, "Creating shortcuts...");
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            Shortcut.Create(Path.Combine(programs, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            if (desktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Shortcut.Create(Path.Combine(desktop, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            }

            Report(progress, "Registering...");
            RegisterUninstall(targetDir, exe, uninst);
            // TA-37/A5 — LocalServer32, so COM can LAUNCH TelegArm when an Action Center entry is clicked while
            // the app is closed. Paired with the ToastActivatorCLSID on the Start-menu shortcut above.
            Shortcut.RegisterActivator(exe);
            Report(progress, "Done.");
        }

        /// <summary>uninstall.exe = the payload-free build of this file that build.ps1 embeds, so the install
        /// folder does not carry a second copy of the whole package. Falls back to copying this exe.</summary>
        private static void WriteUninstaller(string path)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("uninstall.exe"))
            {
                if (s == null) { File.Copy(Application.ExecutablePath, path, true); return; }
                using (var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) Payload.Copy(s, o);
            }
        }

        private static void RegisterUninstall(string dir, string exe, string uninst)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Program.AppName);
                    k.SetValue("DisplayVersion", Info.AppVersion);
                    k.SetValue("Publisher", Program.Publisher);
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\"");
                    k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\" --silent");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try { k.SetValue("EstimatedSize", (int)DirSizeKB(dir), RegistryValueKind.DWord); } catch { }
                }
            }
            catch { /* uninstall entry is best-effort */ }
        }

        private static long DirSizeKB(string dir)
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
            return total / 1024;
        }

        private static void Report(Action<string> p, string s) { if (p != null) p(s); }
    }

    internal static class Uninstaller
    {
        /// <summary>False when the files were left in place because the install list was missing (the caller says so).</summary>
        internal static bool Run(string dir, bool purge)
        {
            dir = Program.Clean(dir);
            // OWNERSHIP (DroidFinestra's rule): the shortcut names, the uninstall key, the activator class and the
            // Run value are fixed names, so another copy of TelegArm may own them. Remove only what points INTO
            // the folder being removed. Removing them blind once deleted a real install's entries during a test.
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"), dir);
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"), dir);
            RemoveUninstallKeyIfOurs(dir);
            Shortcut.UnregisterActivatorIfOurs(dir);   // a stale LocalServer32 makes later activations fail with a COM error
            RemoveRunValueIfOurs(dir);                 // else Windows tries to start a deleted exe at every login
            if (purge) RemoveUserData();
            return SelfDeleteDir(dir);   // last: uninstall.exe runs from inside it
        }

        internal const string ManifestName = "uninstall.manifest";


        /// <summary>Null when <paramref name="dir"/> may be uninstalled, else why not. It must exist, not be a drive
        /// root or a well-known folder, and be TelegArm's: it has the install manifest, TelegArm.exe, or is the
        /// folder the uninstall entry names. ⚠ uninstall.exe ALONE IS NOT ENOUGH — Setup renamed to uninstall.exe
        /// and run from Downloads would otherwise have been allowed to empty Downloads.</summary>
        internal static string NotAnInstall(string dir)
        {
            string full;
            try { full = Path.GetFullPath(dir); } catch { return "\"" + dir + "\" is not a valid folder."; }
            if (!Directory.Exists(full)) return full + " does not exist.";
            if (IsProtected(full)) return full + " is a system or user folder, not a TelegArm installation.";
            if (File.Exists(Path.Combine(full, ManifestName)) || File.Exists(Path.Combine(full, Program.ExeName))) return null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
                {
                    string loc = k != null ? k.GetValue("InstallLocation") as string : null;
                    if (Inside(loc, full) && Inside(full, loc)) return null;
                }
            }
            catch { }
            return full + " does not contain TelegArm.";
        }

        /// <summary>Deletes the files TelegArm put in <paramref name="dir"/> — the manifest's list, or when there is no
        /// manifest, <paramref name="fallback"/> (the names this package installs; null = delete nothing and return
        /// false) — skipping uninstall.exe when <paramref name="keepSelf"/> (it is the running process), then the
        /// app's run-time leftovers, then the folders that ends up emptying. Never a recursive delete of anything
        /// it cannot name. Every path is checked to stay inside <paramref name="dir"/>.</summary>
        internal static bool RemoveInstalledFiles(string dir, bool keepSelf, List<string> fallback)
        {
            string man = Path.Combine(dir, ManifestName);
            IEnumerable<string> listed;
            if (File.Exists(man)) listed = File.ReadAllLines(man, Encoding.UTF8);
            else if (fallback != null) listed = fallback;
            else return false;
            var dirs = new List<string>();
            foreach (var raw in listed)
            {
                try   // one malformed line (a hand edit, a stray character) must not abort the rest half-way (review)
                {
                    string rel = raw.Trim();
                    if (rel.Length == 0 || Path.IsPathRooted(rel)) continue;
                    string full = Path.GetFullPath(Path.Combine(dir, rel));
                    if (!Inside(full, dir) || Inside(dir, full)) continue;   // ".." or the folder itself: never
                    if (keepSelf && Program.Eq(rel, "uninstall.exe")) continue;
                    DelFile(full);
                    for (string d = Path.GetDirectoryName(full); d != null && Inside(d, dir) && !Inside(dir, d); d = Path.GetDirectoryName(d))
                        if (!dirs.Contains(d)) dirs.Add(d);
                }
                catch { }
            }
            // ⚠ NOT IN THE MANIFEST BUT OURS: the app extracts libVLC (~100 MB) into libvlc\ BESIDE ITS EXE when that
            //   folder is writable — VlcBootstrap.CandidateBases tries it first, and a per-user install folder is
            //   writable. Seen on the dev box: <install>\libvlc created 8 s after installing. It extracts through a
            //   .vlc_extract_<id> temp folder (left behind if interrupted) and write-probes with .write_test_<id>.
            //   Without this an uninstall leaves them behind (and the folder with them), and an upgrade keeps a
            //   stale extraction.
            DelDir(Path.Combine(dir, "libvlc"));
            try
            {
                foreach (var d in Directory.GetDirectories(dir, ".vlc_extract_*")) DelDir(d);
                foreach (var f in Directory.GetFiles(dir, ".write_test_*")) DelFile(f);
            }
            catch { }
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));   // deepest first
            foreach (var d in dirs)
                try { if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
            if (!keepSelf) DelFile(man);
            return true;
        }

        /// <summary>Null when <paramref name="dir"/> may be installed into: missing, empty, or TelegArm's (its
        /// manifest — present even after a failed install — or TelegArm.exe), and never a drive root or a
        /// well-known folder. Installing deletes only TelegArm's own files (see RemoveInstalledFiles).</summary>
        internal static string NotReplaceable(string dir)
        {
            if (IsProtected(dir)) return dir + " is a system or user folder. Choose a folder of its own, such as " + Program.DefaultDir();
            if (!Directory.Exists(dir) || Directory.GetFileSystemEntries(dir).Length == 0) return null;
            if (File.Exists(Path.Combine(dir, ManifestName)) || File.Exists(Path.Combine(dir, Program.ExeName))) return null;
            return dir + " is not empty and does not hold an earlier TelegArm. Choose an empty folder, or " + Program.DefaultDir();
        }

        private static bool IsProtected(string dir)
        {
            string d;
            try { d = Path.GetFullPath(dir).TrimEnd('\\'); } catch { return true; }
            if (d.Length <= 2 || Path.GetPathRoot(d).TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase)) return true;   // "C:" / root
            var special = new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Programs,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.CommonApplicationData,
            };
            foreach (var f in special)
            {
                string p;
                try { p = Environment.GetFolderPath(f); } catch { continue; }
                if (!string.IsNullOrEmpty(p) && p.TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
            }
            try
            {
                string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                if (programs.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
                string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (!string.IsNullOrEmpty(pf86) && pf86.TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or inside it. Unreadable or empty
        /// counts as NOT ours: leaving a stale entry is recoverable, deleting another install's is not.</summary>
        internal static bool Inside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string d = Path.GetFullPath(dir).TrimEnd('\\');
                string p = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
                return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>The program path of a command line: the quoted first token, or everything up to ".exe".</summary>
        internal static string ExeOf(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string c = command.Trim();
            if (c.StartsWith("\"")) { int q = c.IndexOf('"', 1); return q > 1 ? c.Substring(1, q - 1) : null; }
            int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return e > 0 ? c.Substring(0, e + 4) : c;
        }

        private static void RemoveShortcutIfOurs(string lnk, string dir)
        {
            try { if (File.Exists(lnk) && Inside(Shortcut.TargetOf(lnk), dir)) File.Delete(lnk); } catch { }
        }

        private static void RemoveUninstallKeyIfOurs(string dir)
        {
            try
            {
                string loc;
                using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
                    loc = k != null ? k.GetValue("InstallLocation") as string : null;
                if (Inside(loc, dir) && Inside(dir, loc)) Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey);
            }
            catch { }
        }

        private static void RemoveRunValueIfOurs(string dir)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Program.RunKey, true))
                {
                    if (k == null) return;
                    if (Inside(ExeOf(k.GetValue(Program.RunValue) as string), dir)) k.DeleteValue(Program.RunValue, false);
                }
            }
            catch { }
        }

        // ⚠ ONLY WHEN THE USER ASKED (purge). Everything the app writes under the profile — accounts (session
        //   files), settings and their backups, logs, caches, the extracted libVLC — but PRESERVES what the user
        //   saved: Documents\TelegArm is the default save folder, so it is removed only if it ends up empty.
        private static void RemoveUserData()
        {
            // ⚠ GetFolderPath returns "" for a folder that does not exist, and Path.Combine("", "TelegArm") is the
            //   RELATIVE "TelegArm" — a recursive delete of a folder in the current directory (review). Skip instead.
            string local = Rooted(Environment.SpecialFolder.LocalApplicationData);
            string roam = Rooted(Environment.SpecialFolder.ApplicationData);
            string docs = Rooted(Environment.SpecialFolder.MyDocuments);

            if (local != null) DelDir(Path.Combine(local, "TelegArm"));   // the data root on desktop Windows (accounts, settings, cache)
            if (roam != null) DelDir(Path.Combine(roam, "TelegArm"));     // Roaming: the countries cache
            if (docs == null) return;

            string dt = Path.Combine(docs, "TelegArm");   // the data root on RT, where LocalAppData is not used
            foreach (var sub in new[] { "Cache", "accounts", "logs", "libvlc", "theme" }) DelDir(Path.Combine(dt, sub));
            foreach (var f in new[] { "settings.json", "settings.json.tmp", "settings.v1.9.json", "crash.log",
                                      "TelegArm.session", "TelegArm.phone", "TelegArm.updates" })
                DelFile(Path.Combine(dt, f));
            try { if (Directory.Exists(dt)) foreach (var f in Directory.GetFiles(dt, "settings.corrupt-*.json")) DelFile(f); } catch { }
            try { if (Directory.Exists(dt) && Directory.GetFileSystemEntries(dt).Length == 0) Directory.Delete(dt); } catch { }
        }

        private static string Rooted(Environment.SpecialFolder f)
        {
            try { string p = Environment.GetFolderPath(f); return !string.IsNullOrEmpty(p) && Path.IsPathRooted(p) ? p : null; }
            catch { return null; }
        }

        private static void DelDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
        private static void DelFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        /// <summary>Removes TelegArm's files by the manifest, then hands the last three steps to a detached cmd.
        /// False — nothing deleted — when there is no manifest: without the list there is no way to tell TelegArm's
        /// files from the user's, so they stay, and the caller says so. NO RECURSIVE DELETE ANYWHERE (review).</summary>
        private static bool SelfDeleteDir(string dir)
        {
            if (!RemoveInstalledFiles(dir, true, null)) return false;
            try
            {
                // uninstall.exe lives inside `dir` and is running, so it cannot delete itself: a detached cmd waits
                // ~2 s for this process to exit, then deletes uninstall.exe, the manifest and the folder — with a
                // NON-recursive rmdir, which fails harmlessly if the user kept anything of their own in it.
                // ⚠ The cmd runs from %TEMP%: it would otherwise inherit uninstall.exe's working directory, which is
                //   the install folder when uninstall.exe is double-clicked — and a process's own working directory
                //   cannot be removed (review).
                string tail = "del /f /q \"" + Path.Combine(dir, "uninstall.exe") + "\" & del /f /q \"" + Path.Combine(dir, ManifestName)
                            + "\" & rmdir \"" + dir + "\"";
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & " + tail);
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
            catch { }
            return true;
        }
    }

    /// <summary>Creates a .lnk via the shell's IShellLink COM object (works on RT desktop).</summary>
    internal static class Shortcut
    {
        internal static void Create(string lnkPath, string target, string args, string workDir, string iconPath, string desc)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
                if (!string.IsNullOrEmpty(desc)) link.SetDescription(desc);

                // ══ BATCH-TA-33/A6 + TA-37/A5 — THE AppUserModelID AND THE TOAST ACTIVATOR CLSID ══════════════
                // ⚠ WITHOUT THESE, TELEGARM'S ACTION CENTER ENTRIES SILENTLY NEVER ARRIVE. Windows resolves an
                //   unpackaged app's notification identity by finding a Start-Menu .lnk carrying this exact AUMID,
                //   and on Windows 10/11 the AUMID alone is not enough: ToastNotifier.Setting threw 0x80070490 on a
                //   real installed shortcut until the CLSID was added too. The app's own calls SUCCEED either way.
                // ⚠ SET THEM BEFORE IPersistFile.Save — the property store is written as part of saving the link.
                // ⚠ The portable package creates no shortcut, so a portable copy correctly gets neither.
                try
                {
                    var store = (IPropertyStore)link;

                    var pv = new PROPVARIANT();
                    pv.vt = 31;                                        // VT_LPWSTR
                    pv.pointerValue = Marshal.StringToCoTaskMemUni(Aumid);
                    var key = PKEY_AppUserModel_ID;
                    store.SetValue(ref key, ref pv);
                    Marshal.FreeCoTaskMem(pv.pointerValue);

                    var cpv = new PROPVARIANT();
                    cpv.vt = 72;                                       // VT_CLSID: the shell stores the GUID itself
                    byte[] raw = new Guid(ToastActivatorClsid).ToByteArray();
                    cpv.pointerValue = Marshal.AllocCoTaskMem(raw.Length);
                    Marshal.Copy(raw, 0, cpv.pointerValue, raw.Length);
                    var ckey = PKEY_AppUserModel_ToastActivatorCLSID;
                    store.SetValue(ref ckey, ref cpv);
                    Marshal.FreeCoTaskMem(cpv.pointerValue);

                    store.Commit();
                }
                catch { /* pre-Win7 shell, or no IPropertyStore — the shortcut is still valid without it */ }

                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch { /* shortcut is best-effort */ }
        }

        /// <summary>The target path a .lnk points at, or null when it cannot be read.</summary>
        internal static string TargetOf(string lnkPath)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        /// <summary>⚠ MUST EQUAL TelegArm.Helpers.ShellNotify.Aumid (build.ps1 checks the built app for it).</summary>
        internal const string Aumid = "hamed7ir.TelegArm";

        /// <summary>⚠ MUST EQUAL TelegArm.Helpers.ToastActivator.Clsid (build.ps1 checks the built app for it).</summary>
        internal const string ToastActivatorClsid = "6E7B4A2C-9F31-4E58-B0D2-1C7A5E9D3F84";

        private const string ActivatorKey = @"Software\Classes\CLSID\{" + ToastActivatorClsid + "}";

        /// <summary>HKCU\Software\Classes\CLSID\{guid}\LocalServer32 = the exe: how COM launches TelegArm when an
        /// Action Center entry is clicked while the app is closed. Per-user, so no admin.</summary>
        internal static void RegisterActivator(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ActivatorKey + @"\LocalServer32"))
                    if (k != null) k.SetValue(null, "\"" + exePath + "\"");
            }
            catch { /* best-effort: without it, clicking a toast while closed simply does nothing */ }
        }

        internal static void UnregisterActivatorIfOurs(string dir)
        {
            try
            {
                string cmd;
                using (var k = Registry.CurrentUser.OpenSubKey(ActivatorKey + @"\LocalServer32"))
                    cmd = k != null ? k.GetValue(null) as string : null;
                if (Uninstaller.Inside(Uninstaller.ExeOf(cmd), dir)) Registry.CurrentUser.DeleteSubKeyTree(ActivatorKey, false);
            }
            catch { }
        }

        // System.AppUserModel.ID — {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, PID 5; ToastActivatorCLSID — PID 26.
        private static PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        private static PROPERTYKEY PKEY_AppUserModel_ToastActivatorCLSID = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 26 };

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        // Only the pointer shapes are needed. The union is padded to the full PROPVARIANT size so the shell reads a
        // correctly-sized structure rather than running off the end of ours.
        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr pointerValue;
            public IntPtr padding;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    /// <summary>The install wizard, with an on-demand "Check requirements" report. The report only informs.</summary>
    /// <summary>"--update dir --wait pid": TelegArm's own update. Waits for that TelegArm to end (it exits right after
    /// starting this), installs over its folder and starts it again. Never kills it: still running after two minutes, it
    /// asks to close it (Retry / Cancel).</summary>
    internal sealed class UpdateForm : Form
    {
        private readonly string _dir;
        private readonly int _pid;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        internal static int ExitCode = 1;   // 0 updated, 4 cancelled while TelegArm still ran, 1 failed

        public UpdateForm(string dir, int pid)
        {
            _dir = dir;
            _pid = pid;
            Text = Program.AppName + " update";
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.icon.ico")) if (s != null) Icon = new Icon(s); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 124);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            var title = new Label { Left = 18, Top = 16, Width = 404, Height = 24, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                                    Text = "Updating " + Program.AppName + " to " + Info.AppVersion };
            _status = new Label { Left = 18, Top = 48, Width = 404, Height = 18, ForeColor = Color.Gray, Text = "Waiting for " + Program.AppName + " to close..." };
            _bar = new ProgressBar { Left = 18, Top = 76, Width = 404, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            Controls.Add(title); Controls.Add(_status); Controls.Add(_bar);
            Shown += (s, e) => { var t = new Thread(Work) { IsBackground = true }; t.Start(); };
        }

        private void Say(string msg) { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }

        /// <summary>update.log in %TEMP%/TelegArm-update: what the update did (a failed one can be read afterwards).</summary>
        private static void Log(string msg)
        {
            try
            {
                string d = Path.Combine(Path.GetTempPath(), "TelegArm-update");
                Directory.CreateDirectory(d);
                File.AppendAllText(Path.Combine(d, "update.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        private void Work()
        {
            Exception err = null;
            Log("update to " + Info.AppVersion + " into " + _dir + ", waiting for pid " + _pid + (Program.DryRun ? " (dry run)" : ""));
            try
            {
                WaitForApp();
                bool desktop = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"));
                Installer.Install(_dir, desktop, Say);
                Log("installed");
            }
            catch (Exception ex) { err = ex; Log("failed: " + ex.GetType().Name + ": " + ex.Message); }
            try { BeginInvoke((Action)(() => Done(err))); } catch { }
        }

        /// <summary>The TelegArm that started this, then anything else of TelegArm's in the folder (its mutex, or an exe from
        /// it), must be gone. It is never killed.</summary>
        private void WaitForApp()
        {
            if (_pid > 0)
            {
                var w = Stopwatch.StartNew();
                try
                {
                    using (var p = Process.GetProcessById(_pid))
                    {
                        string name = p.ProcessName;
                        bool ours = string.Equals(name, "TelegArm", StringComparison.OrdinalIgnoreCase);   // (a reused id: not ours)
                        bool ended = !ours || p.WaitForExit(120000);
                        Log("pid " + _pid + " is " + name + (ours ? (ended ? ", it ended after " : ", STILL RUNNING after ") + w.ElapsedMilliseconds + " ms" : ": not TelegArm, not waited for"));
                    }
                }
                catch (ArgumentException) { Log("pid " + _pid + " already gone"); }
                catch (InvalidOperationException ex) { Log("pid " + _pid + ": " + ex.Message); }
                catch (System.ComponentModel.Win32Exception ex) { Log("pid " + _pid + ": " + ex.Message); }
            }
            var sw = Stopwatch.StartNew();
            string busy;
            while ((busy = InUse.Describe(_dir)) != null)
            {
                if (sw.ElapsedMilliseconds > 15000)
                {
                    string why = busy;
                    bool retry = (bool)Invoke((Func<bool>)(() =>
                        MessageBox.Show(this, why + "\n\nClose it, then press Retry.", Program.AppName + " update",
                                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) == DialogResult.Retry));
                    if (!retry) throw new OperationCanceledException("cancelled");
                    sw = Stopwatch.StartNew();
                }
                Thread.Sleep(250);
            }
        }

        private void Done(Exception err)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = err == null ? 100 : 0;
            if (err is OperationCanceledException) { ExitCode = 4; Close(); return; }
            if (err != null)
            {
                ExitCode = 1;
                MessageBox.Show(this, "The update did not finish:\n\n" + err.Message + "\n\nRun this installer again, or download "
                                + Program.AppName + " from its release page.", Program.AppName + " update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            ExitCode = 0;
            if (!Program.DryRun)
            {
                _status.Text = "Starting " + Program.AppName + "...";
                try { Process.Start(new ProcessStartInfo(Path.Combine(_dir, Program.ExeName)) { WorkingDirectory = _dir, UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, Program.AppName + " is updated but did not start: " + ex.Message, Program.AppName + " update",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            Close();
        }
    }

    internal sealed class InstallForm : Form
    {
        private readonly TextBox _path, _reqs;
        private readonly CheckBox _desktop;
        private readonly Button _install, _cancel, _browse, _check;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly FlowLayoutPanel _links;
        private static readonly Color Accent = Color.FromArgb(198, 24, 124);   // TelegArm magenta

        public InstallForm()
        {
            Text = Program.AppName + " " + Info.AppVersion + " Setup";
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.icon.ico")) if (s != null) Icon = new Icon(s); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 470);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
            var title = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), Text = "Install " + Program.AppName + " " + Info.AppVersion };
            header.Controls.Add(title);

            var lbl = new Label { Left = 18, Top = 80, Width = 524, Height = 18, Text = "Install location (per-user, no admin needed):" };
            _path = new TextBox { Left = 18, Top = 100, Width = 430, Text = Program.DefaultDir() };
            _browse = new Button { Left = 458, Top = 98, Width = 84, Height = 26, Text = "Browse..." };
            _browse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { try { d.SelectedPath = _path.Text; } catch { } if (d.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(d.SelectedPath, Program.InstallFolderName); } };
            _desktop = new CheckBox { Left = 18, Top = 132, Width = 300, Text = "Create a desktop shortcut", Checked = true };

            var rl = new Label { Left = 18, Top = 164, Width = 524, Height = 18, Text = "Requirements (optional check - installing does not depend on it):" };
            _reqs = new TextBox { Left = 18, Top = 184, Width = 524, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 8.5f),
                                  Text = "Press \"Check requirements\" to see whether this PC has what " + Program.AppName + " needs:\r\n.NET Framework 4.7, and the runtimes its bundled libraries use." };
            _links = new FlowLayoutPanel { Left = 18, Top = 338, Width = 524, Height = 44, FlowDirection = FlowDirection.TopDown, WrapContents = false };

            _status = new Label { Left = 18, Top = 386, Width = 524, Height = 18, ForeColor = Color.Gray, Text = "" };
            _bar = new ProgressBar { Left = 18, Top = 406, Width = 524, Height = 14, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
            _check = new Button { Left = 18, Top = 430, Width = 150, Height = 28, Text = "Check requirements" };
            _install = new Button { Left = 380, Top = 430, Width = 84, Height = 28, Text = "Install" };
            _cancel = new Button { Left = 468, Top = 430, Width = 74, Height = 28, Text = "Cancel" };
            _check.Click += (s, e) => RunCheck();
            _install.Click += OnInstall;
            _cancel.Click += (s, e) => Close();

            Controls.Add(header);
            Controls.Add(lbl); Controls.Add(_path); Controls.Add(_browse); Controls.Add(_desktop);
            Controls.Add(rl); Controls.Add(_reqs); Controls.Add(_links);
            Controls.Add(_status); Controls.Add(_bar); Controls.Add(_check); Controls.Add(_install); Controls.Add(_cancel);
            AcceptButton = _install; CancelButton = _cancel;
        }

        private void RunCheck()
        {
            _status.ForeColor = Color.Gray;
            _status.Text = "Checking requirements...";
            _check.Enabled = false;
            _bar.Visible = true;
            var t = new Thread(() =>
            {
                List<Req> rep = null; Exception err = null;
                try { rep = Requirements.Check(null); } catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _bar.Visible = false;
                        _check.Enabled = true;
                        if (err != null) { _reqs.Text = "The check failed: " + err.Message; _status.Text = ""; return; }
                        ShowReport(rep);
                    }));
                }
                catch { }
            }) { IsBackground = true };
            t.Start();
        }

        private void ShowReport(List<Req> rep)
        {
            _reqs.Text = Requirements.Format(rep);
            _links.Controls.Clear();
            var seen = new HashSet<string>();
            foreach (var r in rep)
            {
                if (r.Url == null || !seen.Add(r.Url)) continue;
                var url = r.Url;
                var ll = new LinkLabel { AutoSize = true, Text = "Get: " + url, Margin = new Padding(0, 0, 0, 2) };
                ll.LinkClicked += (s, e) => { try { Process.Start(url); } catch { } };
                _links.Controls.Add(ll);
            }
            int code = Requirements.ExitCode(rep);
            _status.ForeColor = code == 0 ? Color.Gray : Color.DarkOrange;
            _status.Text = code == 0 ? "Everything needed is present."
                         : code == 2 ? "Part of the package cannot run on this PC - see above. Installing is still allowed."
                         : "Something is missing or old - see above. Installing is still allowed.";
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string dir = (_path.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "Choose an install location."); return; }
            try { Path.GetFullPath(dir); } catch { MessageBox.Show(this, "That is not a valid folder."); return; }
            dir = Program.Clean(dir);
            string refuse = Uninstaller.NotReplaceable(dir);
            if (refuse != null) { MessageBox.Show(this, refuse, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            string busy;
            while ((busy = InUse.Describe(dir)) != null)
                if (MessageBox.Show(this, busy + "\n\nClose it, then press Retry.", "Setup", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;

            // Informs, never refuses: the user decides (Yes is the default).
            string note = Requirements.RuntimeNote();
            if (note != null && MessageBox.Show(this, note + "\r\n\r\nInstall anyway?", Program.AppName + " - .NET Framework",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;

            bool desktop = _desktop.Checked;
            SetBusy(true);
            var t = new Thread(() =>
            {
                Exception err = null;
                try { Installer.Install(dir, desktop, msg => { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }); }
                catch (Exception ex) { err = ex; }
                try { BeginInvoke((Action)(() => Finished(dir, err))); } catch { }
            }) { IsBackground = true };
            t.Start();
        }

        private void Finished(string dir, Exception err)
        {
            _bar.Visible = false;
            if (err != null)
            {
                MessageBox.Show(this, "Install failed:\n\n" + err.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
                return;
            }
            _busy = false;
            if (MessageBox.Show(this, Program.AppName + " was installed.\n\nLaunch it now?", "Setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(Path.Combine(dir, Program.ExeName)); } catch { }
            Close();
        }

        private bool _busy;

        /// <summary>⚠ The window cannot close mid-install (review): the worker is a background thread, so closing
        /// would kill it between two files and leave a half-written, unregistered install.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _install.Enabled = _cancel.Enabled = _browse.Enabled = _path.Enabled = _desktop.Enabled = _check.Enabled = !busy;
            _bar.Visible = busy;
        }
    }
}
