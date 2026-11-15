using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── app updates (owner 2026-11-15) ─────────────────────────────────────────────────────────────────────
        // A quiet check against GitHub 20 s after the window shows, then every 12 hours (Settings, About: "Check for
        // updates automatically"). A newer release puts an Update pill in the title bar, beside the proxy pill. A click:
        // an installed copy downloads the installer (progress in the pill), checks it, starts it in update mode and exits;
        // a portable copy opens the release page. Tool runs (DemoNoSession) never check on their own.

        private UpdatePill _updatePill;
        private System.Windows.Forms.Timer _updateTimer;
        private bool _updateBusy;
        private const int FirstUpdateCheckMs = 20000, UpdateCheckEveryMs = 12 * 60 * 60 * 1000;

        /// <summary>From the constructor (on Shown): listen for a found update and start the background checks.</summary>
        private void StartUpdateChecks()
        {
            Action<ReleaseInfo> found = r => OnUpdateFound(r);
            Updater.Found += found;
            FormClosed += (s, e) => { Updater.Found -= found; if (_updateTimer != null) { _updateTimer.Stop(); _updateTimer.Dispose(); } };
            if (Updater.Pending != null) OnUpdateFound(Updater.Pending);
            if (DemoNoSession) return;
            _updateTimer = new System.Windows.Forms.Timer { Interval = FirstUpdateCheckMs };
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = UpdateCheckEveryMs;
                await CheckForUpdateAsync(false);
            };
            _updateTimer.Start();
        }

        /// <summary>Asks GitHub. <paramref name="userAsked"/>: say what came out (else silent, also on errors).</summary>
        internal async Task CheckForUpdateAsync(bool userAsked)
        {
            if (_updateBusy || IsDisposed) return;
            if (!userAsked && !AppSettings.Instance.CheckForUpdates) return;
            _updateBusy = true;
            try
            {
                var r = await Updater.CheckAsync();
                if (IsDisposed) return;
                if (r != null) Updater.RaiseFound(r);
                else if (userAsked) ThemedDialog.Show(this, "Updates", "You have the latest version (" + Program.Version + ").", "OK");
            }
            catch (Exception ex)
            {
                Logger.Diag("[UPDATE] check failed: " + ex.GetType().Name + ": " + ex.Message);
                if (userAsked && !IsDisposed) ThemedDialog.Show(this, "Updates", "Couldn't check for updates: " + ex.Message, "OK");
            }
            finally { _updateBusy = false; }
        }

        private void OnUpdateFound(ReleaseInfo r)
        {
            if (IsDisposed || r == null) return;
            if (InvokeRequired) { try { BeginInvoke((Action)(() => OnUpdateFound(r))); } catch { } return; }
            EnsureUpdatePill();
            _updatePill.Version = r.Version.ToString(3);
            if (!_updateBusy) _updatePill.Progress = -1;
            _updatePill.Visible = true;
            _updatePill.Fit();
        }

        private void EnsureUpdatePill()
        {
            if (_updatePill != null && !_updatePill.IsDisposed) return;
            _updatePill = new UpdatePill();
            _updatePill.Click += OnUpdatePillClick;
            if (CustomChrome) { _updatePill.OnAccent = true; Bar.LeadSlot = _updatePill; }
            else if (_chatListHost != null)
            {
                // native chrome: floating at the bottom right of the chat list (the proxy pill takes the bottom left)
                _updatePill.OnAccent = false;
                _chatListHost.Controls.Add(_updatePill);
                _updatePill.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                _updatePill.Fit();
                _updatePill.Location = new System.Drawing.Point(Math.Max(0, _chatListHost.ClientSize.Width - _updatePill.Width - Px(18)),
                                                                Math.Max(0, _chatListHost.ClientSize.Height - _updatePill.Height - Px(12)));
                _updatePill.BringToFront();
            }
        }

        private async void OnUpdatePillClick(object sender, EventArgs e)
        {
            var r = Updater.Pending;
            if (r == null || _updateBusy) return;
            string v = r.Version.ToString(3);
            string dir = Updater.InstallDir();
            if (dir == null || r.SetupUrl == null)
            {
                int c = ThemedDialog.Show(this, "Update TelegArm", "TelegArm " + v + " is out.\n\nThis copy was unpacked by hand (portable), "
                    + "so download the new one from the release page and unpack it over this folder.", "Open the release page", "Later");
                if (c == 0) OpenUrl(r.PageUrl);
                return;
            }
            string size = r.SetupSize > 0 ? " (" + (r.SetupSize / 1048576.0).ToString("0") + " MB)" : "";
            int choice = ThemedDialog.Show(this, "Update TelegArm", "TelegArm " + v + " is ready to download" + size + ".\n\n"
                + "TelegArm closes, installs it and opens again. Your accounts and settings stay as they are.", "Update now", "What's new", "Later");
            if (choice == 1) { OpenUrl(r.PageUrl); return; }
            if (choice != 0) return;
            string err = await RunUpdateAsync(r, dir);
            if (err != null && !IsDisposed) ThemedDialog.Show(this, "Update TelegArm", "The update could not be downloaded: " + err, "OK");
        }

        /// <summary>Downloads, checks and starts the installer, then exits. Returns null on success, else the reason (no
        /// dialog here: the rig calls it directly).</summary>
        internal async Task<string> RunUpdateAsync(ReleaseInfo r, string dir)
        {
            if (_updateBusy) return "an update is already running";
            _updateBusy = true;
            EnsureUpdatePill();
            _updatePill.Progress = 0;
            try
            {
                var progress = new Progress<int>(p => { if (_updatePill != null && !_updatePill.IsDisposed) _updatePill.Progress = p; });
                string setup = await Updater.DownloadSetupAsync(r, progress, CancellationToken.None);
                _updatePill.Progress = 100;
                Updater.LaunchInstaller(setup, dir);
                if (Updater.LaunchOverride == null) ExitApp();   // the installer waits for this process to end
                return null;
            }
            catch (Exception ex)
            {
                Logger.Diag("[UPDATE] download failed: " + ex.GetType().Name + ": " + ex.Message);
                if (_updatePill != null && !_updatePill.IsDisposed) _updatePill.Progress = -1;
                return ex.Message;
            }
            finally { _updateBusy = false; }
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(url); } catch { }
        }
    }
}
