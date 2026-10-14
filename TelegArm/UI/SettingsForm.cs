using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Settings. Edits the shared <see cref="AppSettings.Instance"/>.
    ///
    /// Redesign step 2.6: a <see cref="DialogForm"/> with a LEFT RAIL of pages (plan D3) built from SectionHeader +
    /// SettingGroup rows (was a ThemedChrome form with a top tab strip, hand-laid cards and MaterialSkin controls).
    /// Pages: General, Appearance (theme + accent swatches — new), Notifications, Chats &amp; Composer, Data &amp; Storage,
    /// Connection, Privacy, Security, Devices, Advanced (+ window frame and "Open data folder" — new), About.
    ///
    /// ⚠ THE TWO APPLY MODELS OF 1.x ARE KEPT, ROW FOR ROW:
    ///   LIVE (persisted on change): theme, accent, send key, folder layout, startup (registry), diagnostic logging,
    ///     composited scrolling, plain text boxes, proportional scaling, window frame, camera backend, privacy rules,
    ///     security actions, session terminations, the proxy (raises <see cref="ProxyChangeApplied"/>), clear cache,
    ///     crash-report clear.
    ///   ON OK (Cancel / Esc / ✕ discard them): the 7 auto-download switches, max download size, animate WebM stickers,
    ///     notifications, the media-cache and save folders, cache retention — and the cache folder is VALIDATED BEFORE
    ///     ANYTHING IS MUTATED (BATCH-TA-34/B1, see OnOk).
    /// Every page scrolls with TelegArm's stack (NoNativeScrollPanel + ThemedScrollBar + TouchScroller momentum).
    /// </summary>
    public class SettingsForm : DialogForm
    {
        private const int PageGeneral = 0, PageAppearance = 1, PageNotifications = 2, PageChats = 3, PageData = 4,
                          PageConnection = 5, PrivacyPage = 6, SecurityPage = 7, DevicesPage = 8, PageAdvanced = 9, PageAbout = 10;
        private static readonly string[] PageNames =
            { "General", "Appearance", "Notifications", "Chats & Composer", "Data & Storage", "Connection",
              "Privacy", "Security", "Devices", "Advanced", "About" };

        private readonly TelegramService _service;
        private Panel[] _pages;                    // each page's scroll surface (the --shoot rig reads it by name)
        private PageStack[] _stacks;
        private SettingsRail _rail;

        // ON-OK rows (read in OnOk).
        private ToggleRow _photos, _videos, _documents, _voice, _audio, _gifs, _autoPlayGifs, _notifications, _animateWebm;
        private StepperRow _maxSize, _retention;
        private TextRow _cacheBox, _saveBox;
        private ActionRow _clearCacheRow, _proxyRow;

        // Security (two-step verification) page.
        private SettingGroup _securityGroup;
        private bool _securityLoaded;
        private Account_Password _passwordState;

        // Privacy (account privacy rules) page.
        private readonly List<Tuple<InputPrivacyKey, SegmentedRow>> _privacyRows = new List<Tuple<InputPrivacyKey, SegmentedRow>>();
        private bool _privacyLoaded;

        private static readonly Tuple<string, InputPrivacyKey>[] PrivacyKeys = new[]
        {
            Tuple.Create("Last Seen & Online", InputPrivacyKey.StatusTimestamp),
            Tuple.Create("Profile Photo", InputPrivacyKey.ProfilePhoto),
            Tuple.Create("Phone Number", InputPrivacyKey.PhoneNumber),
            Tuple.Create("Calls", InputPrivacyKey.PhoneCall),
            Tuple.Create("Forwarded Messages", InputPrivacyKey.Forwards),
            Tuple.Create("Groups & Channels", InputPrivacyKey.ChatInvite),
            Tuple.Create("Bio", InputPrivacyKey.About),
        };

        // Devices (active sessions) page.
        private SettingGroup _devCurrent, _devOthers;
        private ActionRow _termAllRow;
        private bool _devicesLoaded;

        public SettingsForm(TelegramService service = null) : base("Settings", 780, 600)
        {
            _service = service;
            BuildUi(AppSettings.Instance);
        }

        /// <summary>Raised when the user changed the proxy from the Settings connection row. MainForm
        /// subscribes and runs the live-apply (warm-pool teardown → reconnect → re-warm); SettingsForm
        /// deliberately does NOT do that itself, because the warm pool is MainForm's to own.</summary>
        public event Action ProxyChangeApplied;

        // ── frame: rail + pages + OK / Cancel ────────────────────────────────────────────────────────────────

        private void BuildUi(AppSettings s)
        {
            var body = new Panel();
            var host = new Panel { Dock = DockStyle.Fill };
            _rail = new SettingsRail(PageNames) { Dock = DockStyle.Left, Width = Px(196) };
            _rail.PageSelected += ShowPage;
            body.Controls.Add(host);    // Fill first: docking takes the last-added (the rail) first
            body.Controls.Add(_rail);
            UseOwnBody(body);

            _pages = new Panel[PageNames.Length];
            _stacks = new PageStack[PageNames.Length];
            for (int i = 0; i < PageNames.Length; i++)
            {
                var pageHost = new Panel { Dock = DockStyle.Fill, Visible = i == 0 };
                var scroll = ScrollHost.Wrap(pageHost, ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent);   // + TouchScroller momentum
                host.Controls.Add(pageHost);
                _pages[i] = scroll;
                _stacks[i] = new PageStack(scroll, this);
            }

            BuildGeneralPage(_stacks[PageGeneral]);
            BuildAppearancePage(_stacks[PageAppearance], s);
            BuildNotificationsPage(_stacks[PageNotifications], s);
            BuildChatsPage(_stacks[PageChats], s);
            BuildDataStoragePage(_stacks[PageData], s);
            BuildConnectionPage(_stacks[PageConnection]);
            BuildPrivacyPage(_stacks[PrivacyPage]);
            BuildSecurityPage(_stacks[SecurityPage]);
            BuildDevicesPage(_stacks[DevicesPage]);
            BuildAdvancedPage(_stacks[PageAdvanced], s);
            BuildAboutPage(_stacks[PageAbout]);

            // Footer: the first added is the rightmost — OK at the right, Cancel to its left.
            var ok = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.None, 104);
            ok.Click += OnOk;
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel, 104);
            ApplyPageColors();
            ShowPage(0);
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            ApplyPageColors();
        }

        private void ApplyPageColors()
        {
            if (_pages == null) return;
            var p = ThemeHelper.Palette;
            foreach (var pg in _pages)
            {
                if (pg == null) continue;
                pg.BackColor = p.Window;
                if (pg.Parent == null) continue;
                pg.Parent.BackColor = p.Window;
                foreach (Control c in pg.Parent.Controls)
                {
                    var bar = c as ThemedScrollBar;
                    if (bar != null) { bar.IsDark = p.Dark; bar.AccentColor = p.Accent; bar.Invalidate(); }
                }
            }
            if (_rail != null) _rail.Invalidate();
        }

        private void ShowPage(int idx)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                var hostPanel = _pages[i].Parent;
                hostPanel.Visible = i == idx;
                if (i == idx) hostPanel.BringToFront();
            }
            if (_rail != null) _rail.Selected = idx;
            _stacks[idx].Restack();
            if (idx == DevicesPage && !_devicesLoaded) { _devicesLoaded = true; LoadDevicesAsync(); }    // lazy fetch
            if (idx == PrivacyPage && !_privacyLoaded) { _privacyLoaded = true; LoadPrivacyAsync(); }     // lazy fetch
            if (idx == SecurityPage && !_securityLoaded) { _securityLoaded = true; LoadSecurityAsync(); } // lazy fetch
        }

        // ── General ───────────────────────────────────────────────────────────────────────────────────────────

        private void BuildGeneralPage(PageStack page)
        {
            // STARTUP-SETTING: launch TelegArm at Windows login (HKCU Run key) — starts silently in the tray. The
            // toggle state is read from the REGISTRY (the true source), so it stays accurate even if changed elsewhere.
            var start = new ToggleRow("Start when Windows starts", StartupIsEnabled()) { Subtitle = "Launch TelegArm at login, minimized to the tray" };
            bool reverting = false;
            start.Changed += (snd, ev) =>
            {
                if (reverting) return;
                if (!StartupSetEnabled(start.On))
                {
                    ThemedDialog.Show(this, "Startup", "Couldn't update the Windows startup setting — your account may not allow it.", "OK");
                    reverting = true;
                    try { start.On = StartupIsEnabled(); }   // back to the TRUE state, without re-running this handler
                    finally { reverting = false; }
                }
            };
            page.Section("Startup", start);
        }

        // ── STARTUP-SETTING: HKCU Run-key launch-at-login (pure managed, HKCU = no admin). The REGISTRY is the
        // source of truth for the toggle (not AppSettings) so it reflects reality even if changed externally. ──
        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "TelegArm";
        private static bool StartupIsEnabled()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRunKey)) return k != null && k.GetValue(StartupValueName) != null; }
            catch { return false; }
        }
        private static bool StartupSetEnabled(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRunKey, true) ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupRunKey))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\" --startup");   // overwrite = self-heal a stale path
                    else if (k.GetValue(StartupValueName) != null) k.DeleteValue(StartupValueName, false);      // idempotent
                }
                System.Diagnostics.Debug.WriteLine("[STARTUP] set " + (on ? "on" : "off") + " ok");
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[STARTUP] set " + (on ? "on" : "off") + " FAIL: " + ex.Message); return false; }
        }

        // ── Appearance ────────────────────────────────────────────────────────────────────────────────────────

        private void BuildAppearancePage(PageStack page, AppSettings s)
        {
            var cur = ThemeHelper.Mode;
            var theme = new SegmentedRow("Theme", new[] { "System", "Light", "Dark" },
                                         cur == ThemeMode.System ? 0 : cur == ThemeMode.Light ? 1 : 2) { Subtitle = "Light, dark, or follow Windows" };
            theme.SelectedIndexChanged += (snd, ev) =>
            {
                int idx = theme.SelectedIndex;
                ApplyThemeMode(idx == 0 ? ThemeMode.System : idx == 1 ? ThemeMode.Light : ThemeMode.Dark);
            };

            // Step 2.6 (plan 5): the accent — Follow Windows, or a fixed colour (the custom picker is step 5.2). Live, like
            // the theme; persisted as AppSettings.AccentColor ("" = follow Windows). TELEGARM_ACCENT still wins at startup.
            var accent = new SwatchRow("Accent colour", ThemeHelper.AccentOverride);
            accent.Changed += (snd, ev) =>
            {
                var c = accent.SelectedAccent;
                ThemeHelper.SetAccent(c);
                AppSettings.Instance.AccentColor = ThemeHelper.FormatAccent(c);
                AppSettings.Instance.Save();
            };
            page.Section("Look", theme, accent);
        }

        /// <summary>Applies a theme live (raises ThemeChanged so every form recolors) and persists it.</summary>
        private void ApplyThemeMode(ThemeMode mode)
        {
            ThemeHelper.SetMode(mode);
            AppSettings.Instance.ThemeMode = mode.ToString();
            AppSettings.Instance.Save();
        }

        // ── Notifications ─────────────────────────────────────────────────────────────────────────────────────

        private void BuildNotificationsPage(PageStack page, AppSettings s)
        {
            // TA-32/N4: the subtitle states the master-mute rule, because the toggle's behaviour is not guessable from its
            // label — off silences EVERYTHING including mentions, while unread badges keep counting. Applied on OK.
            _notifications = new ToggleRow("Enable notifications", s.EnableNotifications) { Subtitle = "Off silences everything, mentions included" };
            page.Section("Notifications", _notifications);
            page.Note("Unread badges keep counting while notifications are off.");
        }

        // ── Chats & Composer ──────────────────────────────────────────────────────────────────────────────────

        private void BuildChatsPage(PageStack page, AppSettings s)
        {
            // COMPOSER (TA-36) — which key sends. Applied LIVE (the composer reads AppSettings on each keypress).
            int sendIdx = s.SendKey == "CtrlEnter" ? 1 : s.SendKey == "AltEnter" ? 2 : 0;
            var send = new SegmentedRow("Send message with", new[] { "Enter", "Ctrl+Enter", "Alt+Enter" }, sendIdx) { Subtitle = "Shift+Enter is always a new line" };
            send.SelectedIndexChanged += (snd, ev) =>
            {
                int idx = send.SelectedIndex;
                s.SendKey = idx == 1 ? "CtrlEnter" : idx == 2 ? "AltEnter" : "Enter";
                s.Save();
            };
            page.Section("Composer", send);

            // FOLDER-SIDEBAR: folder navigation style — restart-apply (the layout is decided once at BuildLeftPanel).
            var folders = new SegmentedRow("Folder layout", new[] { "Tabs", "Side panel" }, s.FolderSidebar ? 1 : 0) { Subtitle = "Takes effect after restart" };
            folders.SelectedIndexChanged += (snd, ev) => { s.FolderSidebar = folders.SelectedIndex == 1; s.Save(); };
            page.Section("Layout", folders);

            _animateWebm = new ToggleRow("Animate WebM stickers", s.AnimateWebmStickers) { Subtitle = "Off shows the static sticker emoji" };
            bool rl = RLottie.Available;
            var engine = new ActionRow("Sticker engine") { Subtitle = rl ? "Animated (.tgs) stickers available" : "Animated stickers unavailable — tap for details" };
            engine.Click += (snd, ev) => ThemedDialog.Show(this, "Animated sticker engine (rlottie)", RLottie.Diagnose(), "OK");
            page.Section("Stickers", _animateWebm, engine);
        }

        // ── Data & Storage ────────────────────────────────────────────────────────────────────────────────────

        private void BuildDataStoragePage(PageStack page, AppSettings s)
        {
            _photos = new ToggleRow("Photos", s.AutoDownloadPhotos) { Subtitle = "Download photos automatically" };
            _videos = new ToggleRow("Videos", s.AutoDownloadVideos);
            _documents = new ToggleRow("Documents & files", s.AutoDownloadDocuments);
            _voice = new ToggleRow("Voice messages", s.AutoDownloadVoice);
            _audio = new ToggleRow("Music & audio", s.AutoDownloadAudio);
            _gifs = new ToggleRow("GIFs & animations", s.AutoDownloadGifs);
            _autoPlayGifs = new ToggleRow("Auto-play GIFs", s.AutoPlayGifs) { Subtitle = "Play GIFs without tapping" };
            _maxSize = new StepperRow("Max download size", 1, 4096, Clamp(s.MaxAutoDownloadSizeMB, 1, 4096), 1, " MB") { Subtitle = "Skip auto-download above this" };
            page.Section("Auto-download", _photos, _videos, _documents, _voice, _audio, _gifs, _autoPlayGifs, _maxSize);

            _cacheBox = new TextRow("Media cache folder", s.MediaCacheFolder);
            var cacheBrowse = new ActionRow("Choose the media cache folder", "Browse…");
            cacheBrowse.Click += (snd, ev) => Browse(_cacheBox);
            _saveBox = new TextRow("Default save folder", s.DefaultSaveFolder);
            var saveBrowse = new ActionRow("Choose the save folder", "Browse…");
            saveBrowse.Click += (snd, ev) => Browse(_saveBox);
            _retention = new StepperRow("Keep cached media", 0, 3650, Clamp(s.MediaCacheRetentionDays, 0, 3650), 1, " d") { Subtitle = "Auto-delete older files (0 = keep forever)" };
            _clearCacheRow = new ActionRow("Clear cache", "Clear now");
            _clearCacheRow.Click += OnClearCache;
            page.Section("Storage", _cacheBox, cacheBrowse, _saveBox, saveBrowse, _retention, _clearCacheRow);
            RefreshCacheSizes();
        }

        /// <summary>Shows the resolved cache root + separate thumbnail / downloaded-media sizes.</summary>
        private void RefreshCacheSizes()
        {
            if (_clearCacheRow == null) return;
            long thumbs = MediaCache.FolderSize(MediaCache.ThumbsFolder);
            long media = MediaCache.FolderSize(MediaCache.MediaFolder);
            _clearCacheRow.Subtitle = "Thumbnails " + SizeText(thumbs) + "  •  Downloaded media " + SizeText(media);
        }

        /// <summary>FormatSize, but an empty folder reads "0 KB" (FormatSize returns "" for 0, which left "Thumbnails •").</summary>
        private static string SizeText(long bytes) { return bytes > 0 ? DrawHelper.FormatSize(bytes) : "0 KB"; }

        private void Browse(TextRow box)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (Directory.Exists(box.Value)) dlg.SelectedPath = box.Value;
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Value = dlg.SelectedPath;
            }
        }

        private void OnClearCache(object sender, EventArgs e)
        {
            // Scope to the ACTIVE ACCOUNT only (audit G-5): clear Cache/{activeId}/ — the same folders the size
            // label measures (thumbs/ + media/) — NOT the global root (which would wipe OTHER accounts too). The
            // daily retention job stays global by design. DeleteOlderThan skips locked/in-use files (can't crash)
            // and invalidates the ensured-dir set so the next download recreates the folders.
            // BATCH-TA-3/D1: was DeleteOlderThan(root, 0), relying on "0 days = everything". Retention 0 now
            // means KEEP FOREVER (matching the label two rows up), so that call would have made this button a
            // silent no-op. ClearAll is the explicit "delete the lot" path — same D2 safety layers, no age filter.
            long freed = MediaCache.ClearAll(AccountContext.CacheRootFor(_cacheBox.Value.Trim()));
            System.Diagnostics.Debug.WriteLine("[MEDIA] active-account cache cleared, freed " + freed + " bytes");
            RefreshCacheSizes();
            ThemedDialog.Show(this, "Clear cache", "Cleared " + SizeText(freed), "OK");
        }

        // ── Connection ────────────────────────────────────────────────────────────────────────────────────────

        private void BuildConnectionPage(PageStack page)
        {
            // BATCH-TA-16b/B3 — the SECOND door to the same ProxyForm. The first is the pill on the login screen, which is
            // the one that matters when the network is blocked — but once connected, Settings is where a user looks.
            _proxyRow = new ActionRow("Proxy") { Subtitle = ProxySubtitle() };
            _proxyRow.Click += (snd, ev) =>
            {
                bool changed = false;
                using (var dlg = new ProxyForm(_service)) { dlg.ShowDialog(this); changed = dlg.ConnectionSettingsChanged; }
                TelegArm.Core.ProxyStatus.Reset();
                _proxyRow.Subtitle = ProxySubtitle();
                // ⚠ THIS DOOR MUST APPLY THE CHANGE TOO. Caught from a device log: a proxy switched from HERE wrote
                // "[PROXY] saved … active=…" with NO "[PROXY] APPLY-LIVE" after it, so the app kept running on the
                // previous transport while the UI said otherwise. ProxyForm is opened from three places (login pill,
                // connecting-overlay button, this row); every one of them owns applying what it just persisted.
                if (changed) ProxyChangeApplied?.Invoke();
            };
            page.Section("Connection", _proxyRow);
        }

        /// <summary>Secret-free one-line summary of the proxy setting for the Settings row.</summary>
        private static string ProxySubtitle()
        {
            var s = AppSettings.Instance;
            string active = null;
            try { active = s.ActiveProxyUrl; } catch { }
            int n = s.ProxyList != null ? s.ProxyList.Count : 0;
            if (active != null) return "Using " + TelegArm.Core.ProxyUrl.SafeForLog(active);
            if (n > 0) return "Off · " + n + " saved";
            return "Off — set one up if Telegram is blocked on your network";
        }

        // ── Privacy ───────────────────────────────────────────────────────────────────────────────────────────

        private void BuildPrivacyPage(PageStack page)
        {
            var rows = new List<Control>();
            foreach (var pk in PrivacyKeys)
            {
                var key = pk.Item2;
                var seg = new SegmentedRow(pk.Item1, new[] { "Everybody", "My Contacts", "Nobody" }) { Enabled = false };   // until fetched
                seg.SelectedIndexChanged += (snd, ev) => OnPrivacyChanged(key, seg, seg.SelectedIndex);
                _privacyRows.Add(Tuple.Create(key, seg));
                rows.Add(seg);
            }
            page.Section("Who can see / contact me", rows.ToArray());
        }

        private async void LoadPrivacyAsync()
        {
            if (_service == null) return;
            foreach (var pair in _privacyRows)
            {
                var key = pair.Item1;
                var seg = pair.Item2;
                try
                {
                    var res = await _service.GetPrivacyAsync(key);
                    if (IsDisposed) return;
                    int v = (int)TelegramService.ReducePrivacy(res?.rules);
                    seg.SetSelected(v, false);
                    seg.Applied = v;
                    seg.Enabled = true;
                    seg.Invalidate();
                }
                catch { /* leave this row disabled on failure */ }
            }
        }

        private async void OnPrivacyChanged(InputPrivacyKey key, SegmentedRow seg, int newIdx)
        {
            if (_service == null || newIdx == seg.Applied) return;
            int prev = seg.Applied;
            seg.Enabled = false;
            try
            {
                var res = await _service.SetPrivacyAsync(key, (TelegramService.PrivacyPrimary)newIdx);
                int confirmed = (int)TelegramService.ReducePrivacy(res?.rules);   // reflect what the server actually set
                seg.SetSelected(confirmed, false);
                seg.Applied = confirmed;
            }
            catch (Exception ex)
            {
                seg.SetSelected(prev, false);   // revert
                ThemedDialog.Show(this, "Privacy", "Couldn't change this setting: " + ex.Message, "OK");
            }
            finally { if (!seg.IsDisposed) { seg.Enabled = true; seg.Invalidate(); } }
        }

        // ── Security / Two-step verification ─────────────────────────────────────────────────────────────────

        private void BuildSecurityPage(PageStack page)
        {
            _securityGroup = page.Section("Two-Step Verification", new NoteRow("Loading…"));
        }

        private void SetSecurityRows(params Control[] rows)
        {
            if (_securityGroup == null) return;
            _securityGroup.SuspendLayout();
            try
            {
                while (_securityGroup.Controls.Count > 0) { var c = _securityGroup.Controls[0]; _securityGroup.Controls.Remove(c); c.Dispose(); }
                _securityGroup.AddRange(rows);
            }
            finally { _securityGroup.ResumeLayout(true); }
            _stacks[SecurityPage].Restack();
        }

        private static ActionRow SecAction(string text, Action onClick)
        {
            var r = new ActionRow(text);
            r.Click += (s, e) => onClick();
            return r;
        }

        private async void LoadSecurityAsync()
        {
            if (_service == null) { SetSecurityRows(new NoteRow("Sign in to manage two-step verification.")); return; }
            try
            {
                _passwordState = await _service.GetPasswordAsync();
                if (IsDisposed) return;
                RenderSecurity();
            }
            catch (Exception ex) { if (!IsDisposed) RenderSecurityError("Couldn't load: " + ex.Message); }
        }

        private async Task ReloadSecurityAsync()
        {
            try { _passwordState = await _service.GetPasswordAsync(); if (!IsDisposed) RenderSecurity(); }
            catch { /* keep the current view */ }
        }

        private void RenderSecurity()
        {
            bool on = _passwordState != null && _passwordState.flags.HasFlag(Account_Password.Flags.has_password);
            var rows = new List<Control>
            {
                new NoteRow(on ? "Status: ON — a cloud password is set." : "Status: OFF") { Role = LabelRole.Body },
                new NoteRow(on ? "This password is required (with the SMS code) to log in on a new device."
                               : "Add an extra layer of security: a password asked when logging in on a new device."),
            };
            if (!on) rows.Add(SecAction("Set Password", OnSetPassword));
            else
            {
                if (!string.IsNullOrEmpty(_passwordState.hint)) rows.Add(new NoteRow("Hint: " + _passwordState.hint));
                if (!string.IsNullOrEmpty(_passwordState.email_unconfirmed_pattern))
                {
                    rows.Add(new NoteRow("Recovery email pending confirmation: " + _passwordState.email_unconfirmed_pattern));
                    rows.Add(SecAction("Confirm Email", OnConfirmEmail));
                }
                rows.Add(SecAction("Change Password", OnChangePassword));
                rows.Add(SecAction("Disable Password", OnDisablePassword));
            }
            SetSecurityRows(rows.ToArray());
        }

        private void RenderSecurityError(string msg)
        {
            SetSecurityRows(new NoteRow(msg), SecAction("Retry", LoadSecurityAsync));
        }

        private async void OnSetPassword()
        {
            using (var d = new TfaDialog("Set Password", "Create a cloud password (Two-Step Verification).",
                new[] { "New password", "Confirm password", "Hint (optional)", "Recovery email (optional)" },
                new[] { true, true, false, false }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string np = d.Value(0), cf = d.Value(1), hint = d.Value(2), email = d.Value(3).Trim();
                if (string.IsNullOrEmpty(np)) { ThemedDialog.Show(this, "Set Password", "Password can't be empty.", "OK"); return; }
                if (np != cf) { ThemedDialog.Show(this, "Set Password", "The passwords don't match.", "OK"); return; }
                try
                {
                    await _service.SetPasswordAsync(np, hint, email);
                    await ReloadSecurityAsync();
                    ThemedDialog.Show(this, "Set Password", string.IsNullOrEmpty(email)
                        ? "Two-Step Verification is now ON."
                        : "Password set. Check your email and confirm the recovery address.", "OK");
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Set Password", FriendlyPwdError(ex), "OK"); }
            }
        }

        private async void OnChangePassword()
        {
            using (var d = new TfaDialog("Change Password", "Enter your current password and a new one.",
                new[] { "Current password", "New password", "Confirm new password", "Hint (optional)" },
                new[] { true, true, true, false }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string cur = d.Value(0), np = d.Value(1), cf = d.Value(2), hint = d.Value(3);
                if (string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(np)) { ThemedDialog.Show(this, "Change Password", "Fill in both passwords.", "OK"); return; }
                if (np != cf) { ThemedDialog.Show(this, "Change Password", "The new passwords don't match.", "OK"); return; }
                try
                {
                    await _service.ChangePasswordAsync(cur, np, hint);
                    await ReloadSecurityAsync();
                    ThemedDialog.Show(this, "Change Password", "Password changed.", "OK");
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Change Password", FriendlyPwdError(ex), "OK"); }
            }
        }

        private async void OnDisablePassword()
        {
            using (var d = new TfaDialog("Disable Password", "Enter your current password to turn off Two-Step Verification.",
                new[] { "Current password" }, new[] { true }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string cur = d.Value(0);
                if (string.IsNullOrEmpty(cur)) return;
                try
                {
                    await _service.DisablePasswordAsync(cur);
                    await ReloadSecurityAsync();
                    ThemedDialog.Show(this, "Disable Password", "Two-Step Verification is now OFF.", "OK");
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Disable Password", FriendlyPwdError(ex), "OK"); }
            }
        }

        private async void OnConfirmEmail()
        {
            using (var d = new TfaDialog("Confirm Email", "Enter the confirmation code sent to your recovery email.",
                new[] { "Code" }, new[] { false }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string code = d.Value(0).Trim();
                if (string.IsNullOrEmpty(code)) return;
                try
                {
                    await _service.ConfirmPasswordEmailAsync(code);
                    await ReloadSecurityAsync();
                    ThemedDialog.Show(this, "Confirm Email", "Recovery email confirmed.", "OK");
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Confirm Email", FriendlyPwdError(ex), "OK"); }
            }
        }

        private static string FriendlyPwdError(Exception ex)
        {
            var msg = (ex.Message ?? "").ToUpperInvariant();
            if (msg.Contains("PASSWORD_HASH_INVALID")) return "Incorrect current password. Please try again.";
            if (msg.Contains("SRP_ID_INVALID") || msg.Contains("SRP_PASSWORD_CHANGED")) return "Session expired — please try again.";
            if (msg.Contains("EMAIL_INVALID")) return "That email address looks invalid.";
            if (msg.Contains("CODE_INVALID") || msg.Contains("EMAIL_HASH")) return "Incorrect code. Please try again.";
            if (msg.Contains("TOO_FRESH") || msg.Contains("FLOOD_WAIT")) return "Telegram asks you to wait a while before doing this again.";
            return "Couldn't complete: " + ex.Message;
        }

        /// <summary>A small themed modal that collects one or more (optionally masked) text fields — a DialogForm (step 2.6).</summary>
        private sealed class TfaDialog : DialogForm
        {
            private readonly TextRow[] _boxes;

            public TfaDialog(string title, string intro, string[] labels, bool[] secret) : base(title, 400, 300)
            {
                var rows = new List<Control> { new NoteRow(intro) };
                _boxes = new TextRow[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    bool masked = secret != null && i < secret.Length && secret[i];
                    _boxes[i] = new TextRow(labels[i], "", false, masked);
                    rows.Add(_boxes[i]);
                }
                PopulateBody(rows.ToArray());
                AcceptButton = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.OK);
                CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
                if (_boxes.Length > 0) FocusOnShow(_boxes[0].Field.Inner);
                FitToContent();
            }

            public string Value(int i) { return (i >= 0 && i < _boxes.Length) ? _boxes[i].Value : ""; }
        }

        // ── Devices (active sessions) ────────────────────────────────────────────────────────────────────────

        private void BuildDevicesPage(PageStack page)
        {
            _devCurrent = page.Section("This device", new NoteRow(_service == null ? "Sign in to manage active sessions." : "Loading…"));
            _termAllRow = new ActionRow("Terminate all other sessions") { Subtitle = "Every other device will be logged out", Enabled = false };
            _termAllRow.Click += OnTerminateAll;
            page.Group(_termAllRow);
            _devOthers = page.Section("Other sessions", new NoteRow(_service == null ? "Unavailable while signed out." : "Loading…"));
        }

        private async void LoadDevicesAsync()
        {
            if (_service == null) return;   // the page already says "sign in"
            Account_Authorizations res;
            try { res = await _service.GetAuthorizationsAsync(); }
            catch (Exception ex) { if (!IsDisposed) ShowDevicesMessage("Couldn't load sessions: " + ex.Message); return; }
            if (IsDisposed || res == null || res.authorizations == null) return;

            var current = new List<Control>();
            var others = new List<Control>();
            foreach (var a in res.authorizations)
            {
                bool isCurrent = (a.flags & Authorization.Flags.current) != 0;
                (isCurrent ? current : others).Add(MakeSessionRow(a, isCurrent));
            }
            Replace(_devCurrent, current.Count > 0 ? current.ToArray() : new Control[] { new NoteRow("This device is not listed.") });
            Replace(_devOthers, others.Count > 0 ? others.ToArray() : new Control[] { new NoteRow("No other sessions.") });
            _termAllRow.Enabled = others.Count > 0;
            _stacks[DevicesPage].Restack();
        }

        private static void Replace(SettingGroup g, Control[] rows)
        {
            g.SuspendLayout();
            try
            {
                while (g.Controls.Count > 0) { var c = g.Controls[0]; g.Controls.Remove(c); c.Dispose(); }
                g.AddRange(rows);
            }
            finally { g.ResumeLayout(true); }
        }

        private SessionRowControl MakeSessionRow(Authorization a, bool isCurrent)
        {
            string title = !string.IsNullOrEmpty(a.app_name)
                ? (a.app_name + (string.IsNullOrEmpty(a.app_version) ? "" : " " + a.app_version)).Trim()
                : (!string.IsNullOrEmpty(a.device_model) ? a.device_model : "Unknown app");

            string line2 = Join(" · ", a.device_model, (a.platform + " " + a.system_version).Trim());

            string loc = Join(", ", a.country, a.region);
            if (!string.IsNullOrEmpty(a.ip)) loc = Join(" · ", loc, a.ip);
            string line3 = Join(" · ", loc, isCurrent ? "online · this device" : ActiveText(a.date_active));

            var p = ThemeHelper.Palette;
            var row = new SessionRowControl(a.hash, title, line2, line3)
            {
                IsCurrent = isCurrent,
                IsDark = p.Dark,
                AccentColor = p.Accent
            };
            if (!isCurrent) row.TerminateClicked += hash => OnTerminateOne(row, hash);
            return row;
        }

        private void ShowDevicesMessage(string text)
        {
            if (_devOthers == null) return;
            var retry = new ActionRow("Retry");
            retry.Click += (snd, ev) => { Replace(_devCurrent, new Control[] { new NoteRow("Loading…") }); _stacks[DevicesPage].Restack(); LoadDevicesAsync(); };
            Replace(_devCurrent, new Control[] { new NoteRow(text), retry });
            Replace(_devOthers, new Control[] { new NoteRow("Unavailable.") });
            _stacks[DevicesPage].Restack();
        }

        private async void OnTerminateOne(SessionRowControl row, long hash)
        {
            int c = ThemedDialog.Show(this, "Terminate session",
                "Terminate this session? The device will be logged out.", "Terminate", "Cancel");
            if (c != 0) return;
            try { await _service.ResetAuthorizationAsync(hash); }
            catch (Exception ex)
            {
                string msg = (ex.Message != null && ex.Message.IndexOf("FRESH_RESET_AUTHORISATION_FORBIDDEN", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "This session is too new to terminate — sessions can't be reset within 24 hours of signing in."
                    : ex.Message;
                ThemedDialog.Show(this, "Terminate session", msg, "OK");
                return;
            }
            if (row != null && !row.IsDisposed) { _devOthers.Controls.Remove(row); row.Dispose(); }
            int left = 0;
            foreach (Control x in _devOthers.Controls) if (x is SessionRowControl) left++;
            if (left == 0) Replace(_devOthers, new Control[] { new NoteRow("No other sessions.") });
            _termAllRow.Enabled = left > 0;
            _stacks[DevicesPage].Restack();
        }

        private async void OnTerminateAll(object sender, EventArgs e)
        {
            int c = ThemedDialog.Show(this, "Terminate sessions",
                "Terminate ALL other sessions? Every other device will be logged out.", "Terminate all", "Cancel");
            if (c != 0) return;
            try { await _service.TerminateOtherSessionsAsync(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Terminate sessions", ex.Message, "OK"); return; }
            Replace(_devOthers, new Control[] { new NoteRow("No other sessions.") });
            _termAllRow.Enabled = false;
            _stacks[DevicesPage].Restack();
        }

        private static string Join(string sep, string a, string b)
        {
            bool ea = string.IsNullOrEmpty(a), eb = string.IsNullOrEmpty(b);
            if (ea && eb) return "";
            if (ea) return b;
            if (eb) return a;
            return a + sep + b;
        }

        /// <summary>Humanizes a session's last-active time ("online" / "today HH:mm" / "yesterday" / date).</summary>
        private static string ActiveText(DateTime utc)
        {
            if (utc == default(DateTime)) return "";
            var diff = DateTime.UtcNow - utc;
            if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
            if (diff.TotalMinutes < 3) return "online";
            var local = utc.ToLocalTime();
            if (local.Date == DateTime.Now.Date) return "today " + local.ToString("HH:mm");
            if (local.Date == DateTime.Now.Date.AddDays(-1)) return "yesterday";
            if (diff.TotalDays < 7) return (int)diff.TotalDays + " days ago";
            return local.ToString("yyyy-MM-dd");
        }

        // ── Advanced (camera · diagnostics · display) ─────────────────────────────────────────────────────────

        private void BuildAdvancedPage(PageStack page, AppSettings s)
        {
            string curB = TelegArm.Core.Camera.CameraCapture.Forced;
            var backend = new SegmentedRow("Camera backend", new[] { "Auto", "WinRT", "VLC" }, curB == "winrt" ? 1 : curB == "vlc" ? 2 : 0) { Subtitle = "Round-video capture path" };
            backend.SelectedIndexChanged += (snd, ev) => { int idx = backend.SelectedIndex; TelegArm.Core.Camera.CameraCapture.Forced = idx == 1 ? "winrt" : idx == 2 ? "vlc" : null; };
            var probe = new ActionRow("Camera capture probe", "Run") { Subtitle = "Test whether libVLC can record" };
            probe.Click += (snd, ev) => { using (var f = new RoundProbeForm()) f.ShowDialog(this); };
            page.Section("Camera (round video)", backend, probe);

            // ── DIAGNOSTICS (LOG-TOGGLE+CRASH batch, amendment 2: per-session files) ──
            var rows = new List<Control>();
            var logSw = new ToggleRow("Enable diagnostic logging", s.FileLogging) { Subtitle = "Session logs for troubleshooting; off is fastest" };
            logSw.Changed += (snd, ev) =>
            {
                // Persists on change and takes effect immediately (independent of the OK button; no restart).
                s.FileLogging = logSw.On;
                s.Save();
                FileLog.SetEnabled(logSw.On);
                // Step 1.1: the [DPI] summary was logged at startup only if logging was on THEN — replay it into
                // a log started now, so a log turned on mid-session still says how the process is scaled.
                if (logSw.On) Logger.Diag("[DPI] " + Dpi.ProbeSummary);
            };
            rows.Add(logSw);
            bool logsExist = false;
            try { logsExist = FileLog.LogsDirectory != null && Directory.Exists(FileLog.LogsDirectory); } catch { }
            if (logsExist)
            {
                // Session files exist → offer the folder directly (users shouldn't have to hunt the path).
                var openLogs = new ActionRow("Session logs", "Open folder") { Subtitle = "One file per session, auto-cleaned after 2 days" };
                openLogs.Click += (snd, ev) => { try { System.Diagnostics.Process.Start(FileLog.LogsDirectory); } catch { } };
                rows.Add(openLogs);
            }
            // Crash-report rows. Capture is ALWAYS active (no switch here by design); the count is read once, when this page
            // is built — no cost anywhere else.
            var crashView = new ActionRow("Crash reports", "View") { Subtitle = CrashStatusText(CrashLog.Count()) };
            crashView.Click += (snd, ev) =>
            {
                try
                {
                    string cp = CrashLog.FilePath;
                    if (cp != null && File.Exists(cp)) System.Diagnostics.Process.Start(cp);
                    else ThemedDialog.Show(this, "Crash reports", "No crashes recorded.", "OK");
                }
                catch { }
            };
            var crashClear = new ActionRow("Clear crash reports", "Clear");
            crashClear.Click += (snd, ev) =>
            {
                if (CrashLog.Count() == 0) { crashView.Subtitle = CrashStatusText(0); return; }
                if (ThemedDialog.Show(this, "Crash reports", "Delete all recorded crash reports?", "Delete", "Cancel") != 0) return;
                CrashLog.Clear();
                crashView.Subtitle = CrashStatusText(CrashLog.Count());
            };
            rows.Add(crashView); rows.Add(crashClear);
            page.Section("Diagnostics", rows.ToArray());

            // ── DISPLAY & WINDOW (restart-applied: read once at startup) ──
            // SCROLL-SMOOTH-T1 A/B: composited scrolling (the style participates in handle creation).
            var compSw = new ToggleRow("Smoother scrolling (composited)", s.CompositedScroll) { Subtitle = "Less row overlap while scrolling · after restart" };
            compSw.Changed += (snd, ev) => { s.CompositedScroll = compSw.On; s.Save(); };
            // DPI-MODE-TOGGLE: opt-in DPI-unaware declaration (STEP 0.4: DpiMode is the source of truth; Save mirrors it).
            var dpiSw = new ToggleRow("Proportional scaling on high-DPI displays", s.DpiUnaware) { Subtitle = "Fixes mismatched sizes; blurry · after restart" };
            dpiSw.Changed += (snd, ev) => { s.DpiMode = dpiSw.On ? "Unaware" : ""; s.Save(); };
            // Step 2.6 (plan 2.6 "Advanced: ChromeMode"): TelegArm's own title bar, or the ordinary Windows frame — the
            // escape hatch of step 1.3, reachable without editing settings.json.
            bool native = string.Equals(s.ChromeMode, "Native", StringComparison.OrdinalIgnoreCase);
            var frame = new SegmentedRow("Window frame", new[] { "TelegArm", "Windows" }, native ? 1 : 0) { Subtitle = "Takes effect after restart" };
            frame.SelectedIndexChanged += (snd, ev) => { s.ChromeMode = frame.SelectedIndex == 1 ? "Native" : ""; s.Save(); };
            // STEP 1.1 — how the process is actually scaled. Tap for the full probe summary.
            var scale = new ActionRow("Display scaling") { Subtitle = Dpi.Mode + " · " + Dpi.SystemDpi + " dpi — tap for details" };
            scale.Click += (snd, ev) => ThemedDialog.Show(this, "Display scaling", Dpi.ProbeSummary.Replace(" request=", "\nrequest=")
                .Replace(" call=", "\ncall=").Replace(" mode=", "\nmode=").Replace(" monitors=", "\nmonitors="), "OK");
            page.Section("Display & window", compSw, dpiSw, frame, scale);

            BuildInputTestSection(page, s);

            // STEP 0.1 — one tap collects what a bug report needs and nothing private (DiagReport says what and why).
            var copy = new ActionRow("Copy diagnostics", "Copy") { Subtitle = "Version, Windows build, DPI and connection mode" };
            copy.Click += (snd, ev) =>
            {
                string report = DiagReport.Build();
                bool copied = false;
                try { Clipboard.SetText(report); copied = true; } catch { /* clipboard held by another app */ }
                ThemedDialog.Show(this, "Copy diagnostics",
                    (copied ? "Copied to the clipboard:" : "The clipboard is busy. Select the text below and press Ctrl+C:")
                    + "\n\n" + report, "OK");
            };
            // Step 2.6 (plan R7): the data folder — useful on RT, where Documents is hidden behind the Metro shell.
            var data = new ActionRow("Open data folder", "Open") { Subtitle = "Settings, accounts and caches live here" };
            data.Click += (snd, ev) => { try { System.Diagnostics.Process.Start(StoragePaths.ResolveDataRoot()); } catch (Exception ex) { ThemedDialog.Show(this, "Data folder", ex.Message, "OK"); } };
            page.Section("Support", copy, data);
        }

        /// <summary>Device trip 2026-10 (alpha.2): every switch the RT typing A/B needs, on the device itself (env vars are
        /// impractical there). All live except the composer engine and shim Off (restart). See Helpers.InputPolicy.</summary>
        private void BuildInputTestSection(PageStack page, AppSettings s)
        {
            // REDESIGN 1.5 — the composer / search engine for device gate G1 (was "Plain text boxes (test)").
            bool plain = string.Equals(s.ComposerEngine, "Plain", StringComparison.OrdinalIgnoreCase);
            var engine = new SegmentedRow("Composer", new[] { "Colour emoji", "Plain" }, plain ? 1 : 0) { Subtitle = "Plain is a standard Windows text box · after restart" };
            engine.SelectedIndexChanged += (snd, ev) => { s.ComposerEngine = engine.SelectedIndex == 1 ? "Plain" : ""; s.Save(); };

            var resize = new ToggleRow("Move the window above the keyboard", s.ResizeForKeyboard) { Subtitle = "The main window shrinks; dialogs move up" };
            resize.Changed += (snd, ev) => { s.ResizeForKeyboard = resize.On; s.Save(); Logger.Diag("[INPUT] " + InputPolicy.Summary); };
            var dismiss = KeyboardSwitch(InputPolicy.Dismiss, "Keep it closed after closing it", "Stops Windows bringing the keyboard back");
            var refocus = KeyboardSwitch(InputPolicy.Refocus, "No text focus on reactivation", "Switching back to TelegArm opens no keyboard");
            var actGuard = KeyboardSwitch(InputPolicy.ActGuard, "Ignore the keyboard's focus steal", "Animations keep playing while it is open");

            var trace = new ToggleRow("Per-keystroke input tracing", s.KbdDiag) { Subtitle = "Very detailed; needs diagnostic logging" };
            trace.Changed += (snd, ev) => { s.KbdDiag = trace.On; s.Save(); };
            page.Section("Typing and touch keyboard", engine, resize, dismiss, refocus, actGuard, trace);
        }

        private static ToggleRow KeyboardSwitch(string token, string caption, string subtitle)
        {
            var row = new ToggleRow(caption, InputPolicy.IsOn(token)) { Subtitle = subtitle };
            row.Changed += (snd, ev) => InputPolicy.SetOn(token, row.On);
            return row;
        }

        private static string CrashStatusText(int n) =>
            n == 0 ? "None recorded (capture is always active)"
                   : n + (n == 1 ? " crash" : " crashes") + " recorded (capture is always active)";

        // ── About ─────────────────────────────────────────────────────────────────────────────────────────────

        private void BuildAboutPage(PageStack page)
        {
            var version = new NoteRow("TelegArm " + Program.Version) { Role = LabelRole.Body };
            var about = new ActionRow("About TelegArm") { Subtitle = "Credits, links and the story" };
            about.Click += (snd, ev) =>
            {
                using (var f = new AboutForm(ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent)) f.ShowDialog(this);
            };
            page.Section("About", version, about);
        }

        // ── OK ────────────────────────────────────────────────────────────────────────────────────────────────

        private void OnOk(object sender, EventArgs e)
        {
            // ══ BATCH-TA-34/B1 — VALIDATE **BEFORE** MUTATING ANYTHING ═══════════════════════════
            // ⚠ THIS BLOCK USED TO LIVE ~10 LINES LOWER, AND THAT WAS A REAL BUG. Ten settings were
            //   assigned into AppSettings.Instance — the LIVE singleton every reader consults — and only
            //   THEN was the cache folder validated. On the refusal path the method returned before
            //   s.Save(), so every one of those ten changes took effect immediately and then SILENTLY
            //   REVERTED on the next launch. The user changed a setting, watched it work, and lost it.
            // ⚠ REORDERING IS THE FIX, NOT A SNAPSHOT-AND-ROLLBACK. This validation reads only the cache
            //   folder text, so it has no dependency on the assignments below — moving it up is sufficient.
            //   ⚠ KEEP IT THAT WAY: any future validation belongs ABOVE the assignments too.
            // (Original rationale, BATCH-TA-3/D2 — VALIDATE ON SET, not only when the daily job runs.
            //  MediaCacheFolder is free text and the prune recurses AllDirectories, so a folder that is /
            //  contains / sits inside the accounts root would put a delete sweep next to session files.)
            string wantCache = _cacheBox.Value.Trim();
            string cacheRefusal = MediaCache.PruneRefusalReason(wantCache);
            // "does not exist" is not a reason to reject — EnsureFolder below creates it. Only overlap is fatal.
            if (cacheRefusal != null && cacheRefusal.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) < 0)
            {
                TelegArm.Helpers.Logger.Diag("[CACHE-PRUNE] settings REJECTED cache folder=\"" + wantCache + "\" reason=" + cacheRefusal);
                ShowPage(PageData);
                ThemedDialog.Show(this, "Cache folder",
                    "That media-cache folder can't be used, because " + cacheRefusal + ".\n\n" +
                    "TelegArm deletes old files from this folder, so it must not overlap where your account " +
                    "sessions are stored. Pick a dedicated folder (the default is a \"Cache\" folder).", "OK");
                return;   // NOTHING has been touched yet — in memory or on disk. The dialog stays open.
            }

            var s = AppSettings.Instance;
            s.AutoDownloadPhotos = _photos.On;
            s.AutoDownloadVideos = _videos.On;
            s.AutoDownloadDocuments = _documents.On;
            s.AutoDownloadVoice = _voice.On;
            s.AutoDownloadAudio = _audio.On;
            s.AutoDownloadGifs = _gifs.On;
            s.AutoPlayGifs = _autoPlayGifs.On;
            s.AnimateWebmStickers = _animateWebm.On;
            s.EnableNotifications = _notifications.On;
            s.MaxAutoDownloadSizeMB = _maxSize.Value;
            // B2 — the cache folder was validated and accepted ABOVE, before any of these assignments ran,
            // so by the time we reach here every path leads to s.Save().
            s.MediaCacheFolder = wantCache;
            s.DefaultSaveFolder = _saveBox.Value.Trim();
            s.MediaCacheRetentionDays = _retention.Value;

            MediaCache.EnsureFolder(s.MediaCacheFolder);
            MediaCache.EnsureFolder(s.DefaultSaveFolder);
            s.Save();

            DialogResult = DialogResult.OK;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ── layout helpers ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One page: section headers and setting groups stacked top-down at the page's width (inset), re-stacked when
        /// the page or any item changes size (a group grows as its rows are replaced; a note wraps).</summary>
        private sealed class PageStack
        {
            private readonly Panel _page;
            private readonly SettingsForm _form;
            private readonly List<Control> _items = new List<Control>();
            private bool _busy, _again;
            private const int Margin = 16, SectionGap = 8, Top = 8, Bottom = 24;

            public PageStack(Panel page, SettingsForm form)
            {
                _page = page; _form = form;
                _page.ClientSizeChanged += (s, e) => Restack();
            }

            private void Add(Control c)
            {
                _page.Controls.Add(c);
                _items.Add(c);
                c.SizeChanged += (s, e) => Restack();
            }

            /// <summary>A header and a card of rows; returns the card.</summary>
            public SettingGroup Section(string title, params Control[] rows)
            {
                Add(new SectionHeader(title));
                return Group(rows);
            }

            public SettingGroup Group(params Control[] rows)
            {
                var g = new SettingGroup();
                g.AddRange(rows);
                Add(g);
                Restack();
                return g;
            }

            public void Note(string text) { Add(new NoteRow(text)); Restack(); }

            public void Restack()
            {
                if (_busy) { _again = true; return; }
                _busy = true;
                try
                {
                    for (int pass = 0; pass < 3; pass++)
                    {
                        _again = false;
                        int m = _form.Px(Margin), w = Math.Max(_form.Px(120), _page.ClientSize.Width - 2 * m);
                        int y = _form.Px(Top) + _page.AutoScrollPosition.Y;
                        foreach (var c in _items)
                        {
                            if (c.Width != w) c.Width = w;
                            if (c.Left != m) c.Left = m;
                            if (c.Top != y) c.Top = y;
                            y += c.Height + (c is SectionHeader ? 0 : _form.Px(SectionGap));
                        }
                        // the scroll extent: a spacer at the end, so the last card clears the bottom edge
                        _page.AutoScrollMinSize = new Size(0, y - _page.AutoScrollPosition.Y + _form.Px(Bottom));
                        if (!_again) break;
                    }
                }
                finally { _busy = false; }
            }
        }

        /// <summary>The left rail (plan D3): one row per page, the selected one in AccentSoft with an accent bar, Up/Down
        /// and Enter from the keyboard, touch-tappable rows (40 logical px).</summary>
        private sealed class SettingsRail : Control
        {
            private readonly string[] _names;
            private int _selected;
            private int _hover = -1;
            public event Action<int> PageSelected;
            private const int RowH = 40, PadX = 16, TopPad = 8;

            public SettingsRail(string[] names)
            {
                _names = names;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
                AccessibleRole = AccessibleRole.PageTabList;
                AccessibleName = "Settings pages";
            }

            public int Selected
            {
                get { return _selected; }
                set { _selected = value; Invalidate(); }
            }

            private int RowAt(int y)
            {
                int dpi = Dpi.Of(this);
                int i = (y - Dpi.Px(TopPad, dpi)) / Dpi.Px(RowH, dpi);
                return y < Dpi.Px(TopPad, dpi) || i < 0 || i >= _names.Length ? -1 : i;
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int r = RowAt(e.Y); if (r != _hover) { _hover = r; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                int r = RowAt(e.Y);
                if (r >= 0) Pick(r);
            }

            private void Pick(int i)
            {
                if (i < 0 || i >= _names.Length) return;
                _selected = i;
                Invalidate();
                var h = PageSelected;
                if (h != null) h(i);
            }

            protected override bool IsInputKey(Keys keyData)
            {
                var k = keyData & Keys.KeyCode;
                return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Up) { Pick(Math.Max(0, _selected - 1)); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Pick(Math.Min(_names.Length - 1, _selected + 1)); e.Handled = true; }
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), rowH = Dpi.Px(RowH, dpi), padX = Dpi.Px(PadX, dpi), top = Dpi.Px(TopPad, dpi);
                g.Clear(p.Footer);
                using (var pen = new Pen(p.Divider)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                for (int i = 0; i < _names.Length; i++)
                {
                    var r = new Rectangle(0, top + i * rowH, Width - 1, rowH);
                    bool sel = i == _selected;
                    if (sel || i == _hover)
                    {
                        var inner = Rectangle.Inflate(r, -Dpi.Px(6, dpi), -Dpi.Px(2, dpi));
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var path = DrawHelper.RoundedRect(inner, Dpi.Px(6, dpi)))
                        using (var b = new SolidBrush(sel ? p.AccentSoft : p.Hover)) g.FillPath(b, path);
                        if (sel) using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, inner.X, inner.Y + Dpi.Px(8, dpi), Dpi.Px(3, dpi), inner.Height - Dpi.Px(16, dpi));
                    }
                    var font = FontHelper.UiAt(10.5f, dpi, sel ? FontStyle.Bold : FontStyle.Regular);
                    TextRenderer.DrawText(g, _names[i], font, new Rectangle(padX, r.Y, Width - padX - Dpi.Px(8, dpi), r.Height),
                        sel ? p.AccentFg : p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    if (sel && Focused && ShowFocusCues)
                        using (var pen = new Pen(p.Accent, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                            g.DrawRectangle(pen, Rectangle.Inflate(r, -Dpi.Px(4, dpi), -Dpi.Px(1, dpi)));
                }
            }
        }
    }
}
