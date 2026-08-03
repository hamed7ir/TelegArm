// =============================================================================
//  TelegArm AnyCPU installer  (Setup.cs)
//
//  A .NET Framework 4.7 AnyCPU/MSIL installer, so it runs on Windows RT 8.1 (ARM32)
//  AND x86/x64 — unlike an Inno Setup installer, which is native x86 and cannot run
//  on RT. Ships next to "payload.zip" (the app in its dll\ deploy layout).
//
//  Modes:
//    (no args)              -> install wizard UI
//    --silent [dir]         -> install to dir (default per-user) with no UI (scriptable)
//    --uninstall <dir>      -> remove install dir + shortcuts + registry + user data
//    (run as uninstall.exe) -> confirm, then uninstall its own folder
//
//  Installs per-user to %LocalAppData%\Programs\TelegArm by default (no elevation).
//  The app itself writes session/cache/settings under the user profile (resolved
//  LocalAppData-first), so no admin is needed and RT works without the jailbreak
//  admin dance.
// =============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TelegArmSetup
{
    internal static class Program
    {
        internal const string AppName = "TelegArm";
        internal const string AppVersion = "1.1.0";
        internal const string Publisher = "Hamed (hamed7ir)";
        internal const string PayloadName = "payload.zip";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TelegArm";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 1 && Eq(args[0], "--uninstall"))
                {
                    string dir = args.Length >= 2 ? args[1] : Path.GetDirectoryName(Application.ExecutablePath);
                    return Uninstaller.Run(dir) ? 0 : 1;
                }
                if (args.Length >= 1 && Eq(args[0], "--silent"))
                {
                    string dir = args.Length >= 2 ? args[1] : DefaultDir();
                    Installer.Install(dir, true, null);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Double-clicking the dropped uninstall.exe (no args) -> confirm + uninstall its own folder.
                if (Eq(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "uninstall"))
                {
                    if (MessageBox.Show("Remove " + AppName + " and its data from this computer?",
                        "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Uninstaller.Run(Path.GetDirectoryName(Application.ExecutablePath));
                    return 0;
                }

                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show(ex.ToString(), AppName + " Setup - error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        internal static string DefaultDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(p, "Programs", AppName);
        }
    }

    internal static class Installer
    {
        internal static void Install(string targetDir, bool desktopShortcut, Action<string> progress)
        {
            Report(progress, "Preparing...");
            string payload = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), Program.PayloadName);
            if (!File.Exists(payload))
                throw new FileNotFoundException("Installer payload not found next to Setup.exe:\n" + payload
                    + "\n\nKeep Setup.exe and payload.zip together.");

            // Fresh install of the BINARIES only. User data lives under the profile (LocalAppData/Documents)
            // and is never here, so wiping the target dir is safe.
            if (Directory.Exists(targetDir)) { Report(progress, "Removing previous version..."); TryDeleteDir(targetDir); }
            Directory.CreateDirectory(targetDir);

            Report(progress, "Extracting files...");
            using (var fs = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    string dest = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }  // dir entry
                    string d = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    using (var es = entry.Open())
                    using (var os = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        es.CopyTo(os);
                }
            }

            string exe = Path.Combine(targetDir, "TelegArm.exe");
            string uninst = Path.Combine(targetDir, "uninstall.exe");

            Report(progress, "Creating shortcuts...");
            File.Copy(Application.ExecutablePath, uninst, true);   // uninstall.exe = this Setup (uninstall mode only)
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            Shortcut.Create(Path.Combine(programs, Program.AppName + ".lnk"), exe, null, targetDir, exe, "TelegArm - Telegram client");
            if (desktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Shortcut.Create(Path.Combine(desktop, Program.AppName + ".lnk"), exe, null, targetDir, exe, "TelegArm - Telegram client");
            }

            Report(progress, "Registering...");
            RegisterUninstall(targetDir, exe, uninst);
            Report(progress, "Done.");
        }

        private static void RegisterUninstall(string dir, string exe, string uninst)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Program.AppName);
                    k.SetValue("DisplayVersion", Program.AppVersion);
                    k.SetValue("Publisher", Program.Publisher);
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\"");
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

        internal static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        private static void Report(Action<string> p, string s) { if (p != null) p(s); }
    }

    internal static class Uninstaller
    {
        internal static bool Run(string dir)
        {
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"));
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"));
            try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }
            RemoveUserData();
            SelfDeleteDir(dir);   // remove the install dir last (uninstall.exe runs from inside it)
            return true;
        }

        // Same policy as the Inno [UninstallDelete]: remove ALL app-generated data across the profile, but
        // PRESERVE user-saved media (Documents\TelegArm is dropped only if it ends up empty).
        private static void RemoveUserData()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            DelDir(Path.Combine(local, "TelegArm"));   // cache + data root (accounts/settings) on desktop
            DelDir(Path.Combine(roam, "TelegArm"));    // Roaming: Countries cache

            string dt = Path.Combine(docs, "TelegArm");
            DelDir(Path.Combine(dt, "Cache"));
            DelDir(Path.Combine(dt, "accounts"));
            DelDir(Path.Combine(dt, "logs"));
            DelDir(Path.Combine(dt, "libvlc"));
            DelFile(Path.Combine(dt, "settings.json"));
            DelFile(Path.Combine(dt, "crash.log"));
            DelFile(Path.Combine(dt, "TelegArm.session"));
            DelFile(Path.Combine(dt, "TelegArm.phone"));
            DelFile(Path.Combine(dt, "TelegArm.updates"));
            try { if (Directory.Exists(dt) && Directory.GetFileSystemEntries(dt).Length == 0) Directory.Delete(dt); } catch { }
        }

        private static void RemoveShortcut(string lnk) { try { if (File.Exists(lnk)) File.Delete(lnk); } catch { } }
        private static void DelDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
        private static void DelFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        private static void SelfDeleteDir(string dir)
        {
            try
            {
                // uninstall.exe lives inside `dir` and is running -> it can't delete itself. Launch a detached
                // cmd that waits ~2s for this process to exit, then removes the whole dir (incl. uninstall.exe).
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
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
                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch { /* shortcut is best-effort */ }
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

    internal sealed class InstallForm : Form
    {
        private readonly TextBox _path;
        private readonly CheckBox _desktop;
        private readonly Button _install, _cancel, _browse;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private static readonly Color Accent = Color.FromArgb(198, 24, 124);

        public InstallForm()
        {
            Text = Program.AppName + " " + Program.AppVersion + " Setup";
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TelegArmSetup.icon.ico")) if (s != null) Icon = new Icon(s); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 300);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
            var title = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), Text = "Install " + Program.AppName + " " + Program.AppVersion };
            header.Controls.Add(title);

            var lbl = new Label { Left = 18, Top = 88, Width = 444, Height = 18, Text = "Install location (per-user, no admin needed):" };
            _path = new TextBox { Left = 18, Top = 110, Width = 350, Text = Program.DefaultDir() };
            _browse = new Button { Left = 378, Top = 108, Width = 84, Height = 26, Text = "Browse..." };
            _browse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { try { d.SelectedPath = _path.Text; } catch { } if (d.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(d.SelectedPath, Program.AppName); } };
            _desktop = new CheckBox { Left = 18, Top = 148, Width = 300, Text = "Create a desktop shortcut", Checked = true };
            _status = new Label { Left = 18, Top = 194, Width = 444, Height = 18, ForeColor = Color.Gray, Text = "" };
            _bar = new ProgressBar { Left = 18, Top = 216, Width = 444, Height = 14, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
            _install = new Button { Left = 300, Top = 256, Width = 84, Height = 28, Text = "Install" };
            _cancel = new Button { Left = 388, Top = 256, Width = 74, Height = 28, Text = "Cancel" };
            _install.Click += OnInstall;
            _cancel.Click += (s, e) => Close();

            Controls.Add(header);
            Controls.Add(lbl); Controls.Add(_path); Controls.Add(_browse); Controls.Add(_desktop);
            Controls.Add(_status); Controls.Add(_bar); Controls.Add(_install); Controls.Add(_cancel);
            AcceptButton = _install; CancelButton = _cancel;
        }

        private async void OnInstall(object sender, EventArgs e)
        {
            string dir = (_path.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "Choose an install location."); return; }
            bool desktop = _desktop.Checked;
            SetBusy(true);
            try
            {
                await Task.Run(() => Installer.Install(dir, desktop, msg => { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }));
                _bar.Visible = false;
                if (MessageBox.Show(this, "TelegArm was installed.\n\nLaunch it now?", "Setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start(Path.Combine(dir, "TelegArm.exe")); } catch { }
                Close();
            }
            catch (Exception ex)
            {
                _bar.Visible = false;
                MessageBox.Show(this, "Install failed:\n\n" + ex.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _install.Enabled = _cancel.Enabled = _browse.Enabled = _path.Enabled = _desktop.Enabled = !busy;
            _bar.Visible = busy;
        }
    }
}
