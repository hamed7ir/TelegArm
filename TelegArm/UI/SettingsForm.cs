using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// MaterialSkin dialog for editing media auto-download and storage settings.
    /// Edits the shared <see cref="AppSettings.Instance"/>; persists on OK.
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly MaterialSkinManager _skin;

        private Toggle _photos, _videos, _documents, _voice, _audio, _gifs, _autoPlayGifs, _notifications;
        private Toggle _animateWebm;
        private Stepper _maxSize;
        private MaterialTextBox2 _cacheBox, _saveBox;
        private Stepper _retention;
        private Label _cacheSizeLabel;
        private Segmented3 _themeSeg;

        // Card-layout theming (grouped rounded cards; recomputed on theme change so the pages recolor live).
        private const int CardX = 16, CardW = 560, RowH = 54, CardPad = 4, SecGap = 16, SecLabelH = 22;
        private const int RowRightZone = 238;   // reserved width on the right for the row control (segmented/stepper/toggle)
        private Color _cardBg, _cardBorder, _titleColor, _subColor;

        private SettingsChrome _chrome;
        private Panel _content;   // ThemedChrome's content panel (below the accent title bar)
        private Panel[] _pages;
        private static readonly string[] CategoryNames = { "General", "Data & Storage", "Advanced", "Privacy", "Security", "Devices" };

        // Security (two-step verification) page.
        private const int SecurityPage = 4;
        private NoNativeScrollFlowPanel _securityBody;
        private bool _securityLoaded;
        private Account_Password _passwordState;

        // Privacy (account privacy rules) page.
        private const int PrivacyPage = 3;
        private NoNativeScrollFlowPanel _privacyList;
        private readonly System.Collections.Generic.List<Tuple<InputPrivacyKey, Segmented3>> _privacyRows
            = new System.Collections.Generic.List<Tuple<InputPrivacyKey, Segmented3>>();
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
        private const int DevicesPage = 5;
        private readonly TelegramService _service;
        private readonly Color _accent = ThemeHelper.GetWindowsAccentColor();
        private NoNativeScrollFlowPanel _devList;
        private Panel _devCurrentHost;
        private MaterialButton _termAllBtn;
        private bool _devicesLoaded;

        public SettingsForm(TelegramService service = null)
        {
            _service = service;
            _skin = MaterialSkinManager.Instance;   // configures the ColorScheme the remaining MaterialSkin controls read (no managed MaterialForm now)
            ApplyTheme();

            BuildUi(AppSettings.Instance);
        }

        private void ApplyTheme()
        {
            _skin.Theme = ThemeHelper.IsDark
                ? MaterialSkinManager.Themes.DARK
                : MaterialSkinManager.Themes.LIGHT;
            var win = ThemeHelper.GetWindowsAccentColor();
            var accent = (Primary)(uint)win.ToArgb();
            var msAccent = (Accent)(uint)win.ToArgb();   // accent slot = Windows accent (shared singleton — no blue re-poison)
            _skin.ColorScheme = new ColorScheme(accent, accent, accent, msAccent, TextShade.WHITE);
            if (_chrome != null) { _chrome.IsDark = ThemeHelper.IsDark; _chrome.Invalidate(); }   // re-theme the chrome live
            ComputeThemeColors();
            RecolorCardPages();   // recolor the grouped cards + rows live when the theme is switched in-place
            if (_content != null) { _content.BackColor = _skin.BackgroundColor; _content.Invalidate(true); }   // repaint MaterialSkin controls with the new scheme
        }

        /// <summary>Card/row palette for the grouped-card pages (General / Data & Storage). Recomputed on theme change.</summary>
        private void ComputeThemeColors()
        {
            bool d = ThemeHelper.IsDark;
            _cardBg = d ? Color.FromArgb(45, 45, 50) : Color.FromArgb(250, 250, 252);
            _cardBorder = d ? Color.FromArgb(58, 58, 64) : Color.FromArgb(226, 226, 230);
            _titleColor = d ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            _subColor = d ? Color.FromArgb(152, 152, 160) : Color.FromArgb(120, 120, 128);
        }

        private void RecolorCardPages()
        {
            if (_pages == null) return;
            // UI-FIX-T1: ALL pages, not just the card pages 0-2 — Privacy (3) hosts Segmented3 rows and the
            // flow pages (3-5) hold plain host panels whose BackColor was captured at build time.
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] == null) continue;
                _pages[i].BackColor = _skin.BackgroundColor;
                RecolorTree(_pages[i]);
                _pages[i].Invalidate(true);
            }
            // The page host + OK/Cancel footer are _content children outside the pages walk — without this the
            // footer keeps the old background after an in-place switch.
            if (_content != null)
                foreach (Control c in _content.Controls)
                    if (c.GetType() == typeof(Panel)) c.BackColor = _skin.BackgroundColor;
        }

        private void RecolorTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                string tag = c.Tag as string;
                if (tag == "card") { c.BackColor = _cardBg; c.Invalidate(); }
                else if (tag == "title") { c.ForeColor = _titleColor; c.BackColor = _cardBg; }
                else if (tag == "sub") { c.ForeColor = _subColor; c.BackColor = _cardBg; }
                else if (tag == "div") { c.BackColor = _cardBorder; }
                else if (tag == "sec") { c.BackColor = _skin.BackgroundColor; }   // section labels (accent ForeColor is theme-invariant)
                else if (tag == null && (c is FlowLayoutPanel || c.GetType() == typeof(Panel)))
                    c.BackColor = _skin.BackgroundColor;   // untagged hosts (flow pages, privacy/session rows, spacers) follow the page bg
                if (c is Segmented3 sg) { sg.IsDark = ThemeHelper.IsDark; sg.Invalidate(); }
                else if (c is Stepper st) { st.IsDark = ThemeHelper.IsDark; st.Invalidate(); }
                else if (c is Toggle tg) { tg.IsDark = ThemeHelper.IsDark; tg.Invalidate(); }
                else if (c is ThemedScrollBar sb) { sb.IsDark = ThemeHelper.IsDark; sb.Invalidate(); }
                if (c.Controls.Count > 0) RecolorTree(c);
            }
        }

        private void BuildUi(AppSettings s)
        {
            AutoScaleMode = AutoScaleMode.Font;
            Text = "Settings";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            KeyPreview = true;
            KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            // Bump ClientSize by BarH so the content area (below ThemedChrome's title bar) stays the intended size.
            ClientSize = new Size(600, 470 + TelegArm.Helpers.ThemedChrome.BarH);

            Color bg = _skin.BackgroundColor;
            ComputeThemeColors();

            // The app's STANDARD title bar (accent bar + one ✕ + drag + app icon), like the admin dialogs /
            // MainForm — no more MaterialForm caption. Everything below lives in the returned content panel.
            _content = TelegArm.Helpers.ThemedChrome.Apply(this, "Settings", _accent, ThemeHelper.IsDark);
            _content.BackColor = bg;

            // Content host fills the area between the tab strip and the footer.
            // (Add the Fill control FIRST so it docks last and takes the leftover space.)
            var host = new Panel { Dock = DockStyle.Fill, BackColor = bg };
            _content.Controls.Add(host);

            // Tab strip (below the title bar): a flat underline-style strip with the overflow affordance.
            _chrome = new SettingsChrome
            {
                Dock = DockStyle.Top,
                Tabs = CategoryNames,
                Accent = _accent,
                IsDark = ThemeHelper.IsDark
            };
            _chrome.TabSelected += ShowPage;
            _content.Controls.Add(_chrome);

            // Footer with OK / Cancel (bottom, always visible).
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = bg };
            var ok = new MaterialButton { Text = "OK", Width = 90, Type = MaterialButton.MaterialButtonType.Contained };
            ok.Click += OnOk;
            var cancel = new MaterialButton { Text = "Cancel", Width = 90, Type = MaterialButton.MaterialButtonType.Outlined };
            cancel.Click += (snd, e) => { DialogResult = DialogResult.Cancel; Close(); };
            footer.Resize += (s2, e2) =>
            {
                cancel.Location = new Point(footer.ClientSize.Width - 100, 10);
                ok.Location = new Point(footer.ClientSize.Width - 198, 10);
            };
            footer.Controls.Add(ok);
            footer.Controls.Add(cancel);
            _content.Controls.Add(footer);

            // Category pages (one visible at a time). The two card pages (General / Data & Storage) are wrapped
            // in a vertical AutoScroll so rows below the fold are reachable; the flow pages (Privacy/Security/
            // Devices) host their own scrollable Dock.Fill panel. Themed bar + finger-drag (NoNativeScroll).
            _pages = new Panel[6];
            for (int i = 0; i < _pages.Length; i++)
            {
                if (i <= 2)
                {
                    var sp = new NoNativeScrollPanel
                    {
                        Dock = DockStyle.Fill, BackColor = bg, Visible = i == 0,
                        AutoScroll = true, Padding = new Padding(0, 0, 0, 16)
                    };
                    host.Controls.Add(sp);
                    sp.Controls.Add(new ThemedScrollBar(sp, ThemeHelper.IsDark, _accent) { Dock = DockStyle.Right });
                    TouchScroller.Enable(sp, horizontal: false);
                    _pages[i] = sp;
                }
                else
                {
                    _pages[i] = new Panel { Dock = DockStyle.Fill, BackColor = bg, Visible = i == 0 };
                    host.Controls.Add(_pages[i]);
                }
            }
            BuildGeneralPage(_pages[0], s);
            BuildDataStoragePage(_pages[1], s);
            BuildAdvancedPage(_pages[2], s);
            BuildPrivacyPage(_pages[3]);
            BuildSecurityPage(_pages[4]);
            BuildDevicesPage(_pages[5]);
            ShowPage(0);
        }

        private void ShowPage(int idx)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Visible = i == idx;
                if (i == idx) _pages[i].BringToFront();
            }
            if (_chrome != null) { _chrome.Selected = idx; _chrome.Invalidate(); }   // move the active-tab underline
            if (idx == DevicesPage && !_devicesLoaded) { _devicesLoaded = true; LoadDevicesAsync(); }    // lazy fetch
            if (idx == PrivacyPage && !_privacyLoaded) { _privacyLoaded = true; LoadPrivacyAsync(); }     // lazy fetch
            if (idx == SecurityPage && !_securityLoaded) { _securityLoaded = true; LoadSecurityAsync(); } // lazy fetch
        }

        // ── General (appearance · notifications) ─────────────────────────────
        private void BuildGeneralPage(Panel p, AppSettings s)
        {
            int y = 12;

            y = SectionLabel(p, "APPEARANCE", y);
            var appCard = Card(p, y, 1); y += appCard.Height + SecGap;
            RowTitle(appCard, 0, "Theme", "Choose light, dark, or follow the system");
            var cur = ThemeHelper.Mode;
            _themeSeg = new Segmented3
            {
                Options = new[] { "System", "Light", "Dark" },
                Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(210, 30)
            };
            _themeSeg.SetSelected(cur == ThemeMode.System ? 0 : cur == ThemeMode.Light ? 1 : 2, false);
            _themeSeg.Applied = _themeSeg.Selected;
            _themeSeg.SelectionChanged += idx => ApplyThemeMode(idx == 0 ? ThemeMode.System : idx == 1 ? ThemeMode.Light : ThemeMode.Dark);
            PlaceRight(appCard, 0, _themeSeg);

            y = SectionLabel(p, "NOTIFICATIONS", y);
            var notif = Card(p, y, 1); y += notif.Height + SecGap;
            RowTitle(notif, 0, "Enable notifications", "Muted chats are always skipped");
            _notifications = RowSwitch(notif, 0, s.EnableNotifications);
            RowClickable(notif, 0, () => _notifications.Flip());   // UI-FIX-T1: whole row flips the switch (touch)

            // FOLDER-SIDEBAR: folder navigation style — tabs (default) or a side rail. Restart-apply (the
            // layout is decided once at BuildLeftPanel), same pattern as composited scroll / proportional scaling.
            y = SectionLabel(p, "LAYOUT", y);
            var layoutCard = Card(p, y, 1); y += layoutCard.Height + SecGap;
            RowTitle(layoutCard, 0, "Folder layout", "Tabs above the list, or a side panel. Takes effect after restart.");
            var folderSeg = new Segmented3
            {
                Options = new[] { "Tabs", "Side panel" },
                Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(190, 30)
            };
            folderSeg.SetSelected(s.FolderSidebar ? 1 : 0, false);
            folderSeg.Applied = folderSeg.Selected;
            folderSeg.SelectionChanged += idx => { s.FolderSidebar = idx == 1; s.Save(); };
            PlaceRight(layoutCard, 0, folderSeg);

            // STARTUP-SETTING: launch TelegArm at Windows login (HKCU Run key) — starts silently in the tray. The
            // toggle state is read from the REGISTRY (the true source), so it stays accurate even if changed elsewhere.
            y = SectionLabel(p, "STARTUP", y);
            var startCard = Card(p, y, 1); y += startCard.Height + SecGap;
            RowTitle(startCard, 0, "Start when Windows starts", "Launch TelegArm at login, minimized to the tray");
            var startSw = RowSwitch(startCard, 0, StartupIsEnabled());
            startSw.CheckedChanged += (snd, ev) =>
            {
                if (!StartupSetEnabled(startSw.Checked))
                {
                    ThemedDialog.Show(this, "Startup", "Couldn't update the Windows startup setting — your account may not allow it.", "OK");
                    startSw.Checked = StartupIsEnabled();   // revert to the TRUE state (the setter doesn't re-raise CheckedChanged)
                }
            };
            RowClickable(startCard, 0, () => startSw.Flip());

            Spacer(p, y);
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

        // ── Advanced (stickers · camera / round video) ───────────────────────
        private void BuildAdvancedPage(Panel p, AppSettings s)
        {
            int y = 12;

            y = SectionLabel(p, "STICKERS", y);
            var stick = Card(p, y, 2); y += stick.Height + SecGap;
            RowTitle(stick, 0, "Animate WebM stickers", "Off shows the static sticker emoji");
            _animateWebm = RowSwitch(stick, 0, s.AnimateWebmStickers);
            RowClickable(stick, 0, () => _animateWebm.Flip());   // UI-FIX-T1: whole row flips the switch (touch)
            Divider(stick, 0);
            bool rl = RLottie.Available;
            RowTitle(stick, 1, "Sticker engine", rl ? "Animated (.tgs) stickers available" : "Animated stickers unavailable — tap for details");
            Chevron(stick, 1);
            RowClickable(stick, 1, () => ThemedDialog.Show(this, "Animated sticker engine (rlottie)", RLottie.Diagnose(), "OK"));

            y = SectionLabel(p, "CAMERA (ROUND VIDEO)", y);
            var cam = Card(p, y, 2); y += cam.Height + SecGap;
            RowTitle(cam, 0, "Camera backend", "Round-video capture path");
            var backend = new Segmented3 { Options = new[] { "Auto", "WinRT", "VLC" }, Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(180, 30) };
            string curB = TelegArm.Core.Camera.CameraCapture.Forced;
            backend.SetSelected(curB == "winrt" ? 1 : curB == "vlc" ? 2 : 0, false);
            backend.Applied = backend.Selected;
            backend.SelectionChanged += idx => TelegArm.Core.Camera.CameraCapture.Forced = idx == 1 ? "winrt" : idx == 2 ? "vlc" : null;
            PlaceRight(cam, 0, backend);
            Divider(cam, 0);
            RowTitle(cam, 1, "Camera capture probe", "Test whether libVLC can record");
            var probe = new MaterialButton { Text = "Run", Width = 96, Type = MaterialButton.MaterialButtonType.Text };
            probe.Click += (snd, ev) => { using (var f = new RoundProbeForm()) f.ShowDialog(this); };
            PlaceRight(cam, 1, probe);

            // ── DIAGNOSTICS (LOG-TOGGLE+CRASH batch, amendment 2: per-session files) ─────────────
            y = SectionLabel(p, "DIAGNOSTICS", y);
            bool logsExist = false;
            try { logsExist = FileLog.LogsDirectory != null && Directory.Exists(FileLog.LogsDirectory); } catch { }
            var diag = Card(p, y, logsExist ? 5 : 4); y += diag.Height + SecGap;   // +composited-scroll A/B +proportional-scaling rows
            RowTitle(diag, 0, "Enable diagnostic logging", "Writes session logs for troubleshooting. Leave off for best performance.");
            var logSw = RowSwitch(diag, 0, s.FileLogging);
            logSw.CheckedChanged += (snd, ev) =>
            {
                // Persists on change and takes effect immediately (independent of the OK button; no restart).
                s.FileLogging = logSw.Checked;
                s.Save();
                FileLog.SetEnabled(logSw.Checked);
            };
            RowClickable(diag, 0, () => logSw.Flip());   // UI-FIX-T1: whole row flips the switch (Flip raises CheckedChanged → live toggle)
            Divider(diag, 0);
            int nextRow = 1;
            if (logsExist)
            {
                // Session files exist → offer the folder directly (users shouldn't have to hunt the path).
                RowTitle(diag, 1, "Session logs", "One file per session, auto-cleaned after 2 days");
                var openBtn = new MaterialButton { Text = "Open logs folder", Width = 170, Type = MaterialButton.MaterialButtonType.Text };
                openBtn.Click += (snd, ev) => { try { System.Diagnostics.Process.Start(FileLog.LogsDirectory); } catch { } };
                PlaceRight(diag, 1, openBtn);
                Divider(diag, 1);
                nextRow = 2;
            }
            // Crash-report status row. Capture is ALWAYS active (no switch here by design); the count is read
            // once, when this page is built — no cost anywhere else.
            int crashRowY = CardPad + nextRow * RowH;
            int crashes = CrashLog.Count();
            AddLabel(diag, "Crash reports", "title", 16, crashRowY + 8, 200, FontHelper.Ui(10.5f));
            var crashLbl = AddLabel(diag, CrashStatusText(crashes), "sub", 16, crashRowY + 31, 280, FontHelper.Ui(8.25f));
            var viewBtn = new MaterialButton { Text = "View", Width = 92, Type = MaterialButton.MaterialButtonType.Text, Location = new Point(CardW - 232, crashRowY + (RowH - 36) / 2) };
            viewBtn.Click += (snd, ev) =>
            {
                try
                {
                    string cp = CrashLog.FilePath;
                    if (cp != null && File.Exists(cp)) System.Diagnostics.Process.Start(cp);
                    else ThemedDialog.Show(this, "Crash reports", "No crashes recorded.", "OK");
                }
                catch { }
            };
            var clearBtn = new MaterialButton { Text = "Clear", Width = 92, Type = MaterialButton.MaterialButtonType.Outlined, Location = new Point(CardW - 128, crashRowY + (RowH - 36) / 2) };
            clearBtn.Click += (snd, ev) =>
            {
                if (CrashLog.Count() == 0) { crashLbl.Text = CrashStatusText(0); return; }
                if (MessageBox.Show(this, "Delete all recorded crash reports?", "TelegArm",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                CrashLog.Clear();
                crashLbl.Text = CrashStatusText(CrashLog.Count());
            };

            // SCROLL-SMOOTH-T1 A/B: composited scrolling (restart-applied — the style participates in
            // handle creation, so it can't flip live like the logging toggle above).
            int compRow = nextRow + 1;
            Divider(diag, nextRow);
            RowTitle(diag, compRow, "Smoother scrolling (composited)", "Reduces row overlap while scrolling. Takes effect after restart.");
            var compSw = RowSwitch(diag, compRow, s.CompositedScroll);
            compSw.CheckedChanged += (snd, ev) => { s.CompositedScroll = compSw.Checked; s.Save(); };
            RowClickable(diag, compRow, () => compSw.Flip());

            // DPI-MODE-TOGGLE: opt-in DPI-unaware declaration — read at the very top of startup, so it
            // can only ever be restart-applied (same pattern as composited scroll above).
            int dpiRow = compRow + 1;
            Divider(diag, compRow);
            RowTitle(diag, dpiRow, "Proportional scaling on high-DPI displays", "Fixes mismatched sizes on scaled screens; blurry. Restart to apply.");
            var dpiSw = RowSwitch(diag, dpiRow, s.DpiUnaware);
            dpiSw.CheckedChanged += (snd, ev) => { s.DpiUnaware = dpiSw.Checked; s.Save(); };
            RowClickable(diag, dpiRow, () => dpiSw.Flip());

            diag.Controls.Add(viewBtn);
            diag.Controls.Add(clearBtn);

            Spacer(p, y);
        }

        private static string CrashStatusText(int n) =>
            n == 0 ? "None recorded (capture is always active)"
                   : n + (n == 1 ? " crash" : " crashes") + " recorded (capture is always active)";

        // ── Data & Storage (auto-download · storage · advanced) ──────────────
        private void BuildDataStoragePage(Panel p, AppSettings s)
        {
            int y = 12;

            y = SectionLabel(p, "AUTO-DOWNLOAD", y);
            var dl = Card(p, y, 8); y += dl.Height + SecGap;
            RowTitle(dl, 0, "Photos", "Download photos automatically"); _photos = RowSwitch(dl, 0, s.AutoDownloadPhotos); Divider(dl, 0);
            RowTitle(dl, 1, "Videos", null); _videos = RowSwitch(dl, 1, s.AutoDownloadVideos); Divider(dl, 1);
            RowTitle(dl, 2, "Documents & files", null); _documents = RowSwitch(dl, 2, s.AutoDownloadDocuments); Divider(dl, 2);
            RowTitle(dl, 3, "Voice messages", null); _voice = RowSwitch(dl, 3, s.AutoDownloadVoice); Divider(dl, 3);
            RowTitle(dl, 4, "Music & audio", null); _audio = RowSwitch(dl, 4, s.AutoDownloadAudio); Divider(dl, 4);
            RowTitle(dl, 5, "GIFs & animations", null); _gifs = RowSwitch(dl, 5, s.AutoDownloadGifs); Divider(dl, 5);
            RowTitle(dl, 6, "Auto-play GIFs", "Play GIFs without tapping"); _autoPlayGifs = RowSwitch(dl, 6, s.AutoPlayGifs); Divider(dl, 6);
            // UI-FIX-T1: whole-row tap flips each auto-download switch (46x26 was the only <40px touch target).
            RowClickable(dl, 0, () => _photos.Flip()); RowClickable(dl, 1, () => _videos.Flip());
            RowClickable(dl, 2, () => _documents.Flip()); RowClickable(dl, 3, () => _voice.Flip());
            RowClickable(dl, 4, () => _audio.Flip()); RowClickable(dl, 5, () => _gifs.Flip());
            RowClickable(dl, 6, () => _autoPlayGifs.Flip());
            RowTitle(dl, 7, "Max download size", "Skip auto-download above this");
            _maxSize = new Stepper { Minimum = 1, Maximum = 4096, Value = (int)Clamp(s.MaxAutoDownloadSizeMB, 1, 4096), Step = 1, Suffix = " MB", Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(122, 32) };
            PlaceRight(dl, 7, _maxSize);

            y = SectionLabel(p, "STORAGE", y);
            int storeH = 84 + 84 + RowH + 70;
            var st = CardCustom(p, y, storeH); y += storeH + SecGap;
            int iy = CardPad;
            AddLabel(st, "Media cache folder", "title", 16, iy + 8, CardW - 130, FontHelper.Ui(10.5f));
            _cacheBox = new MaterialTextBox2 { Text = s.MediaCacheFolder, Location = new Point(16, iy + 32), Width = CardW - 148 };
            st.Controls.Add(_cacheBox);
            st.Controls.Add(BrowseButton(CardW - 118, iy + 36, _cacheBox));
            iy += 84; Divider(st, iy);
            AddLabel(st, "Default save folder", "title", 16, iy + 8, CardW - 130, FontHelper.Ui(10.5f));
            _saveBox = new MaterialTextBox2 { Text = s.DefaultSaveFolder, Location = new Point(16, iy + 32), Width = CardW - 148 };
            st.Controls.Add(_saveBox);
            st.Controls.Add(BrowseButton(CardW - 118, iy + 36, _saveBox));
            iy += 84; Divider(st, iy);
            AddLabel(st, "Keep cached media", "title", 16, iy + 9, 300, FontHelper.Ui(10.5f));
            AddLabel(st, "Auto-delete older files (0 = keep forever)", "sub", 16, iy + 31, 320, FontHelper.Ui(8.25f));
            _retention = new Stepper { Minimum = 0, Maximum = 3650, Value = (int)Clamp(s.MediaCacheRetentionDays, 0, 3650), Step = 1, Suffix = " d", Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(122, 32) };
            _retention.Location = new Point(CardW - _retention.Width - 16, iy + (RowH - 32) / 2);
            st.Controls.Add(_retention);
            iy += RowH; Divider(st, iy);
            var clear = new MaterialButton { Text = "Clear now", Location = new Point(CardW - 132, iy + 15), Width = 116, Type = MaterialButton.MaterialButtonType.Outlined };
            clear.Click += OnClearCache;
            st.Controls.Add(clear);
            AddLabel(st, "Clear cache", "title", 16, iy + 8, 200, FontHelper.Ui(10.5f));
            _cacheSizeLabel = new Label { Text = "", Location = new Point(16, iy + 31), AutoSize = false, Size = new Size(CardW - 168, 34), BackColor = _cardBg, ForeColor = _subColor, Font = FontHelper.Ui(8.25f), TextAlign = ContentAlignment.TopLeft, Tag = "sub" };
            st.Controls.Add(_cacheSizeLabel);
            RefreshCacheSizes();

            Spacer(p, y);
        }

        // ── Card-layout helpers (grouped rounded cards + touch rows) ─────────

        /// <summary>An accent uppercase section header above a card; returns the Y just below it.</summary>
        private int SectionLabel(Control parent, string text, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text, Location = new Point(CardX + 2, y), AutoSize = false, Size = new Size(CardW, SecLabelH),
                ForeColor = _accent, BackColor = _skin.BackgroundColor, Tag = "sec",
                Font = FontHelper.Ui(8.25f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft
            });
            return y + SecLabelH;
        }

        private Panel Card(Control parent, int y, int rows) => CardCustom(parent, y, rows * RowH + 2 * CardPad);

        private Panel CardCustom(Control parent, int y, int height)
        {
            var card = new Panel { Location = new Point(CardX, y), Size = new Size(CardW, height), BackColor = _cardBg, Tag = "card" };
            using (var pth = DrawHelper.RoundedRect(new Rectangle(0, 0, CardW, height), 12))
                card.Region = new Region(pth);
            card.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(_cardBorder))
                using (var pth = DrawHelper.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 12))
                    g.DrawPath(pen, pth);
            };
            parent.Controls.Add(card);
            return card;
        }

        private void RowTitle(Panel card, int row, string title, string subtitle)
        {
            int iy = CardPad + row * RowH;
            // Labels must stop BEFORE the right-aligned control zone (widest control ≈ the 210px theme segmented
            // + its 16px margin) — otherwise their opaque background clips the control's left edge ("...m | Light").
            int lw = CardW - 16 - RowRightZone;
            AddLabel(card, title, "title", 16, subtitle != null ? iy + 8 : iy + (RowH - 22) / 2, lw, FontHelper.Ui(10.5f));
            if (subtitle != null)
                AddLabel(card, subtitle, "sub", 16, iy + 30, lw, FontHelper.Ui(8.25f));
        }

        private Label AddLabel(Panel card, string text, string tag, int x, int y, int w, Font font)
        {
            var lbl = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, tag == "sub" ? 18 : 22),
                ForeColor = tag == "sub" ? _subColor : _titleColor, BackColor = _cardBg, Tag = tag,
                Font = font, TextAlign = ContentAlignment.MiddleLeft
            };
            card.Controls.Add(lbl);
            return lbl;
        }

        private void PlaceRight(Panel card, int row, Control ctrl, int margin = 16)
        {
            int iy = CardPad + row * RowH;
            ctrl.Location = new Point(CardW - ctrl.Width - margin, iy + (RowH - ctrl.Height) / 2);
            card.Controls.Add(ctrl);
            ctrl.BringToFront();   // draw above the row labels (belt-and-suspenders against any overlap)
        }

        private Toggle RowSwitch(Panel card, int row, bool check)
        {
            var t = new Toggle { Checked = check, Accent = _accent, IsDark = ThemeHelper.IsDark, Size = new Size(46, 26) };
            PlaceRight(card, row, t, 12);
            return t;
        }

        private void Divider(Panel card, int atY)
        {
            int dy = atY < RowH ? CardPad + (atY + 1) * RowH : atY;   // row index (small) OR an absolute Y
            card.Controls.Add(new Panel { Location = new Point(14, dy), Size = new Size(CardW - 28, 1), BackColor = _cardBorder, Tag = "div" });
        }

        private void Chevron(Panel card, int row)
        {
            int iy = CardPad + row * RowH;
            card.Controls.Add(new Label
            {
                Text = "›", AutoSize = false, Size = new Size(20, 22), Location = new Point(CardW - 30, iy + (RowH - 22) / 2),
                ForeColor = _subColor, BackColor = _cardBg, Font = FontHelper.Ui(13f), TextAlign = ContentAlignment.MiddleCenter,
                Tag = "sub"   // UI-FIX-T1: same palette pair as sub-labels → RecolorTree re-themes it live
            });
        }

        /// <summary>Makes a whole card row tap to an action (labels + chevron + the row gap).</summary>
        private void RowClickable(Panel card, int row, Action action)
        {
            int iy = CardPad + row * RowH;
            card.Cursor = Cursors.Hand;
            card.Click += (s, e) => { var mp = card.PointToClient(Cursor.Position); if (mp.Y >= iy && mp.Y < iy + RowH) action(); };
            // Interactive controls are excluded: they handle their own click (a Toggle tapped directly must not
            // ALSO fire a row action, or a toggle-row tap would flip twice — UI-FIX-T1).
            foreach (Control c in card.Controls)
                if (c.Top >= iy && c.Top < iy + RowH && !(c is MaterialSwitch) && !(c is Segmented3) && !(c is Stepper) && !(c is Toggle))
                { c.Cursor = Cursors.Hand; c.Click += (s, e) => action(); }
        }

        /// <summary>Extends the scroll extent so the last card is fully reachable.</summary>
        private void Spacer(Control parent, int y)
        {
            parent.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(4, SecGap), BackColor = _skin.BackgroundColor });
        }

        /// <summary>Shows the resolved cache root + separate thumbnail / downloaded-media sizes.</summary>
        private void RefreshCacheSizes()
        {
            if (_cacheSizeLabel == null) return;
            long thumbs = MediaCache.FolderSize(MediaCache.ThumbsFolder);
            long media = MediaCache.FolderSize(MediaCache.MediaFolder);
            _cacheSizeLabel.Text = "Thumbnails " + DrawHelper.FormatSize(thumbs) + "   •   Downloaded media " + DrawHelper.FormatSize(media);
        }

        /// <summary>Applies a theme live (raises ThemeChanged so every form recolors) and persists it.</summary>
        private void ApplyThemeMode(ThemeMode mode)
        {
            ThemeHelper.SetMode(mode);
            AppSettings.Instance.ThemeMode = mode.ToString();
            AppSettings.Instance.Save();
            ApplyTheme();   // re-skin this dialog immediately to match
        }

        // ── Devices (active sessions) ────────────────────────────────────────

        private void BuildDevicesPage(Panel p)
        {
            // Scrollable "other sessions" list — add the Fill FIRST so it docks last (takes leftover space).
            var listOuter = new Panel { Dock = DockStyle.Fill, BackColor = p.BackColor, Padding = new Padding(20, 2, 8, 10) };
            _devList = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = p.BackColor
            };
            _devList.Resize += (s, e) => SizeDeviceRows();
            listOuter.Controls.Add(_devList);
            listOuter.Controls.Add(new ThemedScrollBar(_devList, ThemeHelper.IsDark, _accent) { Dock = DockStyle.Right });
            TouchScroller.Enable(_devList, horizontal: false);   // finger-pan the sessions list (RT touch)
            p.Controls.Add(listOuter);

            // Top: header + current session + "terminate all others" + the OTHER-SESSIONS label.
            var top = new Panel { Dock = DockStyle.Top, Height = 200, BackColor = p.BackColor };
            top.Controls.Add(Header("Active sessions", 8));
            top.Controls.Add(Label("THIS DEVICE", 24, 42, 300));
            _devCurrentHost = new Panel { Location = new Point(20, 62), Size = new Size(556, 74), BackColor = p.BackColor };
            top.Controls.Add(_devCurrentHost);
            _termAllBtn = new MaterialButton
            {
                Text = "Terminate all other sessions",
                Location = new Point(22, 144),
                Width = 280,
                Type = MaterialButton.MaterialButtonType.Outlined,
                Enabled = false
            };
            _termAllBtn.Click += OnTerminateAll;
            top.Controls.Add(_termAllBtn);
            top.Controls.Add(Label("OTHER SESSIONS", 24, 180, 300));
            p.Controls.Add(top);
        }

        private async void LoadDevicesAsync()
        {
            if (_service == null) { ShowDevicesMessage("Sign in to manage active sessions."); return; }
            Account_Authorizations res;
            try { res = await _service.GetAuthorizationsAsync(); }
            catch (Exception ex) { ShowDevicesMessage("Couldn't load sessions: " + ex.Message); return; }
            if (IsDisposed || res == null || res.authorizations == null) return;

            _devCurrentHost.Controls.Clear();
            ClearRows();
            foreach (var a in res.authorizations)
            {
                bool isCurrent = (a.flags & Authorization.Flags.current) != 0;
                var row = MakeSessionRow(a, isCurrent);
                if (isCurrent) { row.Dock = DockStyle.Fill; _devCurrentHost.Controls.Add(row); }
                else _devList.Controls.Add(row);
            }
            _termAllBtn.Enabled = _devList.Controls.Count > 0;
            SizeDeviceRows();
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

            var row = new SessionRowControl(a.hash, title, line2, line3)
            {
                IsCurrent = isCurrent,
                IsDark = ThemeHelper.IsDark,
                AccentColor = _accent
            };
            if (!isCurrent) row.TerminateClicked += hash => OnTerminateOne(row, hash);
            return row;
        }

        private void SizeDeviceRows()
        {
            if (_devList == null) return;
            int w = _devList.ClientSize.Width
                  - (_devList.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            foreach (Control c in _devList.Controls) c.Width = Math.Max(80, w);
        }

        private void ClearRows()
        {
            while (_devList.Controls.Count > 0)
            {
                var c = _devList.Controls[0];
                _devList.Controls.Remove(c);
                c.Dispose();
            }
        }

        private void ShowDevicesMessage(string text)
        {
            if (_devList == null) return;
            ClearRows();
            _devList.Controls.Add(new MaterialLabel { Text = text, AutoSize = false, Size = new Size(520, 44), Margin = new Padding(4, 10, 4, 4) });
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
            if (row != null && !row.IsDisposed) { _devList.Controls.Remove(row); row.Dispose(); }
            _termAllBtn.Enabled = _devList.Controls.Count > 0;
            SizeDeviceRows();
        }

        private async void OnTerminateAll(object sender, EventArgs e)
        {
            int c = ThemedDialog.Show(this, "Terminate sessions",
                "Terminate ALL other sessions? Every other device will be logged out.", "Terminate all", "Cancel");
            if (c != 0) return;
            try { await _service.TerminateOtherSessionsAsync(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Terminate sessions", ex.Message, "OK"); return; }
            ClearRows();
            _termAllBtn.Enabled = false;
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

        // ── Privacy & Security ───────────────────────────────────────────────

        private void BuildPrivacyPage(Panel p)
        {
            // Scrollable rule list (add the Fill FIRST so it docks last and fills below the header).
            var listOuter = new Panel { Dock = DockStyle.Fill, BackColor = p.BackColor, Padding = new Padding(18, 2, 8, 10) };
            _privacyList = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = p.BackColor
            };
            _privacyList.Resize += (s, e) => SizePrivacyRows();
            listOuter.Controls.Add(_privacyList);
            listOuter.Controls.Add(new ThemedScrollBar(_privacyList, ThemeHelper.IsDark, _accent) { Dock = DockStyle.Right });
            TouchScroller.Enable(_privacyList, horizontal: false);   // finger-pan the privacy list (RT touch)
            p.Controls.Add(listOuter);

            var top = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = p.BackColor };
            top.Controls.Add(Header("Who can see / contact me", 8));
            p.Controls.Add(top);

            foreach (var pk in PrivacyKeys)
                _privacyList.Controls.Add(MakePrivacyRow(pk.Item1, pk.Item2));
            SizePrivacyRows();
        }

        private Panel MakePrivacyRow(string name, InputPrivacyKey key)
        {
            var row = new Panel { Height = 46, Margin = new Padding(0, 1, 0, 1), BackColor = _skin.BackgroundColor };
            row.Controls.Add(new MaterialLabel
            {
                Text = name,
                Location = new Point(4, 0),
                AutoSize = false,
                Size = new Size(190, 46),
                TextAlign = ContentAlignment.MiddleLeft
            });
            var seg = new Segmented3
            {
                Options = new[] { "Everybody", "My Contacts", "Nobody" },
                Accent = _accent,
                IsDark = ThemeHelper.IsDark,
                Size = new Size(282, 30),
                Location = new Point(200, 8),
                Enabled = false   // until the current value is fetched
            };
            seg.SelectionChanged += idx => OnPrivacyChanged(key, seg, idx);
            row.Controls.Add(seg);
            _privacyRows.Add(Tuple.Create(key, seg));
            return row;
        }

        private void SizePrivacyRows()
        {
            if (_privacyList == null) return;
            int w = _privacyList.ClientSize.Width
                  - (_privacyList.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            foreach (var pair in _privacyRows)
            {
                var seg = pair.Item2;
                var row = seg.Parent;
                if (row == null) continue;
                row.Width = Math.Max(120, w);
                seg.Left = Math.Max(200, row.ClientSize.Width - seg.Width - 12);   // right-align the selector
            }
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

        private async void OnPrivacyChanged(InputPrivacyKey key, Segmented3 seg, int newIdx)
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

        private static MaterialLabel Header(string text, int y)
        {
            return new MaterialLabel
            {
                Text = text,
                Location = new Point(24, y),
                AutoSize = false,
                Size = new Size(472, 30),
                FontType = MaterialSkinManager.fontType.H6
            };
        }

        // ── Security / Two-step verification ─────────────────────────────────

        private void BuildSecurityPage(Panel p)
        {
            var listOuter = new Panel { Dock = DockStyle.Fill, BackColor = p.BackColor, Padding = new Padding(22, 14, 10, 12) };
            _securityBody = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = p.BackColor
            };
            listOuter.Controls.Add(_securityBody);
            listOuter.Controls.Add(new ThemedScrollBar(_securityBody, ThemeHelper.IsDark, _accent) { Dock = DockStyle.Right });
            TouchScroller.Enable(_securityBody, horizontal: false);   // finger-pan the security page (RT touch)
            p.Controls.Add(listOuter);

            _securityBody.Controls.Add(SecLabel("Two-Step Verification", true));
            _securityBody.Controls.Add(SecLabel("Loading…", false));
        }

        private MaterialLabel SecLabel(string text, bool header)
        {
            return new MaterialLabel
            {
                Text = text,
                AutoSize = false,
                Size = new Size(440, header ? 34 : 24),
                Margin = new Padding(0, 0, 0, header ? 6 : 3),
                FontType = header ? MaterialSkinManager.fontType.H6 : MaterialSkinManager.fontType.Body1
            };
        }

        private MaterialButton SecButton(string text, bool contained, EventHandler onClick)
        {
            var b = new MaterialButton
            {
                Text = text,
                AutoSize = false,
                Size = new Size(230, 42),
                Margin = new Padding(0, 12, 0, 0),
                Type = contained ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Outlined,
                UseAccentColor = false
            };
            b.Click += onClick;
            return b;
        }

        private async void LoadSecurityAsync()
        {
            if (_service == null) { RenderSecurityError("Not connected."); return; }
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
            if (_securityBody == null) return;
            _securityBody.SuspendLayout();
            _securityBody.Controls.Clear();

            bool on = _passwordState != null && _passwordState.flags.HasFlag(Account_Password.Flags.has_password);
            _securityBody.Controls.Add(SecLabel("Two-Step Verification", true));
            _securityBody.Controls.Add(SecLabel(on ? "Status: ON — a cloud password is set." : "Status: OFF", false));
            _securityBody.Controls.Add(SecLabel(on
                ? "This password is required (with the SMS code) to log in on a new device."
                : "Add an extra layer of security: a password asked when logging in on a new device.", false));

            if (!on)
            {
                _securityBody.Controls.Add(SecButton("Set Password", true, (s, e) => OnSetPassword()));
            }
            else
            {
                if (!string.IsNullOrEmpty(_passwordState.hint))
                    _securityBody.Controls.Add(SecLabel("Hint: " + _passwordState.hint, false));
                if (!string.IsNullOrEmpty(_passwordState.email_unconfirmed_pattern))
                {
                    _securityBody.Controls.Add(SecLabel("Recovery email pending confirmation: " + _passwordState.email_unconfirmed_pattern, false));
                    _securityBody.Controls.Add(SecButton("Confirm Email", true, (s, e) => OnConfirmEmail()));
                }
                _securityBody.Controls.Add(SecButton("Change Password", true, (s, e) => OnChangePassword()));
                _securityBody.Controls.Add(SecButton("Disable Password", false, (s, e) => OnDisablePassword()));
            }
            _securityBody.ResumeLayout();
        }

        private void RenderSecurityError(string msg)
        {
            if (_securityBody == null) return;
            _securityBody.Controls.Clear();
            _securityBody.Controls.Add(SecLabel("Two-Step Verification", true));
            _securityBody.Controls.Add(SecLabel(msg, false));
            _securityBody.Controls.Add(SecButton("Retry", true, (s, e) => LoadSecurityAsync()));
        }

        private async void OnSetPassword()
        {
            using (var d = new TfaDialog(ThemeHelper.IsDark, _accent, "Set Password",
                "Create a cloud password (Two-Step Verification).",
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
            using (var d = new TfaDialog(ThemeHelper.IsDark, _accent, "Change Password",
                "Enter your current password and a new one.",
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
            using (var d = new TfaDialog(ThemeHelper.IsDark, _accent, "Disable Password",
                "Enter your current password to turn off Two-Step Verification.",
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
            using (var d = new TfaDialog(ThemeHelper.IsDark, _accent, "Confirm Email",
                "Enter the confirmation code sent to your recovery email.",
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

        /// <summary>A small themed modal that collects one or more (optionally masked) text fields.</summary>
        private sealed class TfaDialog : Form
        {
            private readonly TextBox[] _boxes;

            public TfaDialog(bool dark, Color accent, string title, string intro, string[] labels, bool[] secret)
            {
                Text = title;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
                TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in Alt-Tab / title bar (dialog is off-taskbar)
                StartPosition = FormStartPosition.CenterParent;
                Font = new Font("Segoe UI", 9.5f);

                Color bg = dark ? Color.FromArgb(42, 42, 46) : Color.FromArgb(250, 250, 252);
                Color fg = dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
                Color sub = dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(108, 108, 114);
                Color boxBg = dark ? Color.FromArgb(56, 56, 60) : Color.White;
                BackColor = bg; ForeColor = fg;

                const int W = 372, x = 18;
                int y = 16;
                Controls.Add(new Label { Text = intro, AutoSize = false, Location = new Point(x, y), Size = new Size(W - 36, 36), ForeColor = sub });
                y += 44;

                _boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    Controls.Add(new Label { Text = labels[i], AutoSize = false, Location = new Point(x, y), Size = new Size(W - 36, 18), ForeColor = sub });
                    y += 20;
                    var tb = new TextBox { Location = new Point(x, y), Width = W - 36, BackColor = boxBg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };
                    if (secret != null && i < secret.Length && secret[i]) tb.UseSystemPasswordChar = true;
                    Controls.Add(tb); _boxes[i] = tb;
                    y += 34;
                }
                y += 8;

                var cancel = new TelegArm.UI.Controls.RoundedButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 92, Height = 32, Kind = TelegArm.UI.Controls.RoundedButtonKind.Secondary, Location = new Point(W - 18 - 92, y) };
                var ok = new TelegArm.UI.Controls.RoundedButton { Text = "OK", DialogResult = DialogResult.OK, Width = 92, Height = 32, Kind = TelegArm.UI.Controls.RoundedButtonKind.Primary, Location = new Point(W - 18 - 92 - 8 - 92, y) };
                Controls.Add(ok); Controls.Add(cancel);
                AcceptButton = ok; CancelButton = cancel;

                ClientSize = new Size(W, y + 32 + 16);
                if (_boxes.Length > 0) ActiveControl = _boxes[0];
            }

            public string Value(int i) { return (i >= 0 && i < _boxes.Length) ? _boxes[i].Text : ""; }
        }

        private static MaterialLabel Label(string text, int x, int y, int width)
        {
            return new MaterialLabel
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = false,
                Size = new Size(width, 26),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private MaterialSwitch Switch(Control parent, string text, int y, bool check)
        {
            var sw = new MaterialSwitch { Text = text, Location = new Point(20, y), Width = 470, Checked = check };
            parent.Controls.Add(sw);
            return sw;
        }

        private MaterialButton BrowseButton(int x, int y, MaterialTextBox2 target)
        {
            var b = new MaterialButton
            {
                Text = "Browse…",
                Location = new Point(x, y),
                Width = 110,
                Type = MaterialButton.MaterialButtonType.Text
            };
            b.Click += (s, e) => Browse(target);
            return b;
        }

        private void Browse(MaterialTextBox2 box)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
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
            long freed = MediaCache.ClearAll(AccountContext.CacheRootFor(_cacheBox.Text.Trim()));
            System.Diagnostics.Debug.WriteLine("[MEDIA] active-account cache cleared, freed " + freed + " bytes");
            RefreshCacheSizes();
            MessageBox.Show(this, "Cleared " + DrawHelper.FormatSize(freed),
                "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnOk(object sender, EventArgs e)
        {
            var s = AppSettings.Instance;
            s.AutoDownloadPhotos = _photos.Checked;
            s.AutoDownloadVideos = _videos.Checked;
            s.AutoDownloadDocuments = _documents.Checked;
            s.AutoDownloadVoice = _voice.Checked;
            s.AutoDownloadAudio = _audio.Checked;
            s.AutoDownloadGifs = _gifs.Checked;
            s.AutoPlayGifs = _autoPlayGifs.Checked;
            s.AnimateWebmStickers = _animateWebm.Checked;
            s.EnableNotifications = _notifications.Checked;
            s.MaxAutoDownloadSizeMB = (int)_maxSize.Value;
            // BATCH-TA-3/D2 — VALIDATE ON SET, not only when the daily job runs. MediaCacheFolder is free text
            // and the prune recurses AllDirectories, so a folder that is / contains / sits inside the accounts
            // root would put a delete sweep next to session files. The prune refuses such a target at run time
            // too, but refusing it HERE means the bad value never reaches settings.json in the first place, and
            // the user finds out while they are looking at the field rather than silently a day later.
            string wantCache = _cacheBox.Text.Trim();
            string cacheRefusal = MediaCache.PruneRefusalReason(wantCache);
            // "does not exist" is not a reason to reject — EnsureFolder below creates it. Only overlap is fatal.
            if (cacheRefusal != null && cacheRefusal.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) < 0)
            {
                TelegArm.Helpers.Logger.Diag("[CACHE-PRUNE] settings REJECTED cache folder=\"" + wantCache + "\" reason=" + cacheRefusal);
                MessageBox.Show(this,
                    "That media-cache folder can't be used, because " + cacheRefusal + ".\n\n" +
                    "TelegArm deletes old files from this folder, so it must not overlap where your account " +
                    "sessions are stored. Pick a dedicated folder (the default is a \"Cache\" folder).",
                    "TelegArm — cache folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;   // nothing saved; the dialog stays open on the offending value
            }

            s.MediaCacheFolder = wantCache;
            s.DefaultSaveFolder = _saveBox.Text.Trim();
            s.MediaCacheRetentionDays = (int)_retention.Value;

            MediaCache.EnsureFolder(s.MediaCacheFolder);
            MediaCache.EnsureFolder(s.DefaultSaveFolder);
            s.Save();

            DialogResult = DialogResult.OK;
            Close();
        }

        private static decimal Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// The Settings TAB STRIP (the window title bar is now ThemedChrome's accent bar above it). A flat
        /// underline-style strip: owner-painted, double-buffered. Tabs are laid out at their NATURAL (measured)
        /// widths and the strip SCROLLS HORIZONTALLY (drag / mouse-wheel) when they don't all fit — no
        /// truncation, with an edge fade + chevron affordance. The active tab is accent-colored with a thin
        /// accent underline, auto-scrolled into view on selection; clicking a tab raises <see cref="TabSelected"/>.
        /// Touch = single-finger drag (WinForms synthesizes mouse events from touch on RT).
        /// </summary>
        private sealed class SettingsChrome : Control
        {
            public string[] Tabs;
            public Color Accent = Color.MediumPurple;
            public bool IsDark;
            public event Action<int> TabSelected;

            private int _selected;
            public int Selected
            {
                get { return _selected; }
                set { _selected = value; _pendingScrollInto = value; Invalidate(); }
            }

            private const int TabH = 42, TabPadX = 18, MinTabW = 60;

            private int[] _tabX, _tabW;
            private int _totalW;
            private bool _layoutDone;
            private int _scrollX;
            private int _pendingScrollInto = -1;
            private bool _mouseDown, _dragging;
            private int _dragStartX, _dragStartScroll;

            public SettingsChrome()
            {
                Height = TabH;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            private static Font TabFont() { return new Font("Segoe UI", 9.5f, FontStyle.Bold); }

            private void EnsureLayout(Graphics g)
            {
                if (_layoutDone || Tabs == null || Tabs.Length == 0) return;
                using (var f = TabFont())
                {
                    _tabX = new int[Tabs.Length];
                    _tabW = new int[Tabs.Length];
                    int x = 0;
                    for (int i = 0; i < Tabs.Length; i++)
                    {
                        int w = TextRenderer.MeasureText(g, Tabs[i], f).Width + 2 * TabPadX;
                        if (w < MinTabW) w = MinTabW;
                        _tabX[i] = x; _tabW[i] = w; x += w;
                    }
                    _totalW = x;
                }
                _layoutDone = true;
            }

            private void ClampScroll()
            {
                int max = Math.Max(0, _totalW - Width);
                if (_scrollX > max) _scrollX = max;
                if (_scrollX < 0) _scrollX = 0;
            }

            private void ScrollIntoView(int idx)
            {
                if (!_layoutDone || _tabX == null || idx < 0 || idx >= _tabX.Length) return;
                int left = _tabX[idx], right = left + _tabW[idx];
                if (left - _scrollX < 0) _scrollX = left;
                else if (right - _scrollX > Width) _scrollX = right - Width;
                ClampScroll();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                _mouseDown = true; _dragging = false;
                _dragStartX = e.X; _dragStartScroll = _scrollX;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_mouseDown) return;
                int dx = e.X - _dragStartX;
                if (!_dragging && Math.Abs(dx) > 5) _dragging = true;   // distinguish drag from tap
                if (_dragging) { _scrollX = _dragStartScroll - dx; ClampScroll(); Invalidate(); }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool wasDrag = _dragging;
                _mouseDown = false; _dragging = false;
                if (wasDrag || Tabs == null || _tabX == null) return;
                // Tap the overflow edge (the fade/chevron) → page the strip instead of picking a tab.
                int maxScroll = Math.Max(0, _totalW - Width);
                if (_scrollX < maxScroll && e.X >= Width - 24) { _scrollX = Math.Min(maxScroll, _scrollX + Width - 44); Invalidate(); return; }
                if (_scrollX > 0 && e.X <= 24) { _scrollX = Math.Max(0, _scrollX - (Width - 44)); Invalidate(); return; }
                int localX = e.X + _scrollX;   // a tap → select the tab under the cursor
                for (int i = 0; i < Tabs.Length; i++)
                    if (localX >= _tabX[i] && localX < _tabX[i] + _tabW[i]) { TabSelected?.Invoke(i); break; }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (_totalW <= Width) return;
                _scrollX -= Math.Sign(e.Delta) * 48;
                ClampScroll();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Tab strip background + bottom separator (the window title bar is ThemedChrome's, above this).
                Color barBg = IsDark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(246, 246, 248);
                using (var bb = new SolidBrush(barBg))
                    g.FillRectangle(bb, 0, 0, Width, TabH);
                using (var sep = new Pen(IsDark ? Color.FromArgb(60, 60, 64) : Color.FromArgb(224, 224, 228)))
                    g.DrawLine(sep, 0, TabH - 1, Width, TabH - 1);

                if (Tabs == null || Tabs.Length == 0) return;

                EnsureLayout(g);
                if (_pendingScrollInto >= 0) { ScrollIntoView(_pendingScrollInto); _pendingScrollInto = -1; }
                ClampScroll();

                Color inactive = IsDark ? Color.FromArgb(168, 168, 174) : Color.FromArgb(110, 110, 116);
                // ONE scrolled space for everything: subtract _scrollX from each tab's logical X by hand.
                // (We must NOT use TranslateTransform here — TextRenderer.DrawText is GDI and ignores the
                //  GDI+ world transform, so the labels would stay put while the GDI+ underline scrolled.)
                g.SetClip(new Rectangle(0, 0, Width, TabH));   // clips the GDI+ underline to the strip
                using (var f = TabFont())
                {
                    for (int i = 0; i < Tabs.Length; i++)
                    {
                        var r = new Rectangle(_tabX[i] - _scrollX, 0, _tabW[i], TabH);   // logical → screen
                        if (r.Right <= 0 || r.Left >= Width) continue;                    // skip off-screen tabs
                        bool sel = i == _selected;
                        TextRenderer.DrawText(g, Tabs[i], f, r, sel ? Accent : inactive,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        if (sel)
                        {
                            int tw = TextRenderer.MeasureText(g, Tabs[i], f).Width + 12;
                            using (var ub = new SolidBrush(Accent))
                                g.FillRectangle(ub, r.X + (r.Width - tw) / 2, TabH - 3, tw, 3);
                        }
                    }
                }
                g.ResetClip();

                // "More tabs" affordance: a fade + chevron on whichever edge still hides tabs (they wheel/drag-
                // scroll, but nothing else signals it). Tapping the outer edge pages the strip (see OnMouseUp).
                int max = Math.Max(0, _totalW - Width);
                Color chevC = IsDark ? Color.FromArgb(158, 158, 166) : Color.FromArgb(120, 120, 128);
                const int fadeW = 30;
                using (var cf = new Font("Segoe UI", 12f, FontStyle.Bold))
                {
                    if (_scrollX > 0)
                    {
                        var rc = new Rectangle(0, 0, fadeW, TabH);
                        using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(rc, barBg, Color.FromArgb(0, barBg), System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                            g.FillRectangle(lb, rc);
                        TextRenderer.DrawText(g, "‹", cf, new Rectangle(0, 0, 22, TabH), chevC,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                    if (_scrollX < max)
                    {
                        var rc = new Rectangle(Width - fadeW, 0, fadeW, TabH);
                        using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(rc, Color.FromArgb(0, barBg), barBg, System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                            g.FillRectangle(lb, rc);
                        TextRenderer.DrawText(g, "›", cf, new Rectangle(Width - 22, 0, 22, TabH), chevC,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        /// <summary>A compact accent-colored toggle switch (replaces MaterialSwitch, whose ON color is locked to
        /// the MaterialSkin ColorScheme accent enum — a fixed blue). Uses the app accent; themed dark/light.</summary>
        private sealed class Toggle : Control
        {
            private bool _checked;
            public bool Checked { get { return _checked; } set { if (_checked != value) { _checked = value; Invalidate(); } } }
            public Color Accent = Color.MediumPurple;
            public bool IsDark;
            public event EventHandler CheckedChanged;

            public Toggle()
            {
                Size = new Size(46, 26);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            /// <summary>Flips the switch AND raises CheckedChanged — the user-gesture path (a bare Checked set
            /// deliberately does not raise, so programmatic init can't fire handlers). Used by the row-tap
            /// wiring (UI-FIX-T1) and OnClick alike.</summary>
            public void Flip()
            {
                Checked = !Checked;
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                Flip();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : (IsDark ? Color.FromArgb(45, 45, 50) : Color.White));
                var track = new Rectangle(0, 0, Width - 1, Height - 1);
                Color off = IsDark ? Color.FromArgb(78, 78, 84) : Color.FromArgb(200, 200, 206);
                using (var b = new SolidBrush(_checked ? Accent : off))
                using (var path = DrawHelper.RoundedRect(track, Height / 2))
                    g.FillPath(b, path);
                int knob = Height - 8;
                int kx = _checked ? Width - knob - 4 : 4;
                using (var b = new SolidBrush(Color.White))
                    g.FillEllipse(b, kx, 4, knob, knob);
            }
        }

        /// <summary>A compact themed −/＋ stepper (replaces the bare native NumericUpDown). Integer Value in
        /// [Minimum, Maximum]; the ends dim when at the limit; the value shows an optional Suffix (e.g. " MB").</summary>
        private sealed class Stepper : Control
        {
            public int Minimum, Maximum = 100, Value, Step = 1;
            public string Suffix = "";
            public Color Accent = Color.MediumPurple;
            public bool IsDark;
            private Rectangle _minus, _plus;

            public Stepper()
            {
                Size = new Size(122, 32);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (_minus.Contains(e.Location)) Set(Value - Step);
                else if (_plus.Contains(e.Location)) Set(Value + Step);
            }

            private void Set(int v)
            {
                if (v < Minimum) v = Minimum; else if (v > Maximum) v = Maximum;
                if (v != Value) { Value = v; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Parent != null ? Parent.BackColor : (IsDark ? Color.FromArgb(45, 45, 50) : Color.White));

                int bw = 34;
                _minus = new Rectangle(0, 0, bw, Height);
                _plus = new Rectangle(Width - bw, 0, bw, Height);
                var full = new Rectangle(0, 0, Width - 1, Height - 1);

                Color border = IsDark ? Color.FromArgb(82, 82, 88) : Color.FromArgb(198, 198, 204);
                Color baseBg = IsDark ? Color.FromArgb(52, 52, 58) : Color.White;
                Color txt = IsDark ? Color.FromArgb(224, 224, 230) : Color.FromArgb(45, 45, 50);

                using (var b = new SolidBrush(baseBg))
                using (var path = DrawHelper.RoundedRect(full, 6))
                    g.FillPath(b, path);

                using (var f = FontHelper.Ui(13f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, "−", f, _minus, Value > Minimum ? Accent : border,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, "+", f, _plus, Value < Maximum ? Accent : border,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                using (var f = FontHelper.Ui(9.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Value + Suffix, f, new Rectangle(bw, 0, Width - 2 * bw, Height), txt,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                using (var p = new Pen(border))
                {
                    using (var path = DrawHelper.RoundedRect(full, 6)) g.DrawPath(p, path);
                    g.DrawLine(p, bw, 5, bw, Height - 5);
                    g.DrawLine(p, Width - bw, 5, Width - bw, Height - 5);
                }
            }
        }

        /// <summary>
        /// A touch-friendly 3-option segmented selector (Everybody / My Contacts / Nobody). Owner-painted,
        /// double-buffered; the selected segment is an accent block with white text. Clicking a segment
        /// raises <see cref="SelectionChanged"/>. Themed dark/light; disabled (dimmed) until its value loads.
        /// </summary>
        private sealed class Segmented3 : Control
        {
            public string[] Options = { "A", "B", "C" };
            public int Selected = -1;
            public int Applied = -1;   // the last server-confirmed value (for revert on failure)
            public Color Accent = Color.MediumPurple;
            public bool IsDark;
            public event Action<int> SelectionChanged;

            public Segmented3()
            {
                Height = 30;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            /// <summary>Sets the selected segment; raise=false applies it programmatically (no event).</summary>
            public void SetSelected(int idx, bool raise)
            {
                Selected = idx;
                if (!IsDisposed) Invalidate();
                if (raise) SelectionChanged?.Invoke(idx);
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (!Enabled || Options == null || Options.Length == 0) return;
                int segW = Math.Max(1, Width / Options.Length);
                int idx = e.X / segW;
                if (idx < 0) idx = 0; else if (idx >= Options.Length) idx = Options.Length - 1;
                if (idx != Selected) SetSelected(idx, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Parent != null ? Parent.BackColor : (IsDark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(246, 246, 248)));

                if (Options == null || Options.Length == 0) return;
                int n = Options.Length;
                int segW = Math.Max(1, Width / n);
                var full = new Rectangle(0, 0, segW * n - 1, Height - 1);

                Color border = IsDark ? Color.FromArgb(82, 82, 88) : Color.FromArgb(198, 198, 204);
                Color baseBg = IsDark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(255, 255, 255);
                Color txt = IsDark ? Color.FromArgb(208, 208, 214) : Color.FromArgb(60, 60, 66);
                Color accent = Enabled ? Accent : Blend(Accent, baseBg, 0.5f);

                using (var bg = new SolidBrush(baseBg))
                using (var path = DrawHelper.RoundedRect(full, 6))
                    g.FillPath(bg, path);

                using (var f = FontHelper.Ui(8.25f, FontStyle.Bold))
                {
                    for (int i = 0; i < n; i++)
                    {
                        var cell = new Rectangle(i * segW, 0, segW, Height);
                        bool sel = i == Selected;
                        if (sel)
                            using (var ab = new SolidBrush(accent))
                                g.FillRectangle(ab, new Rectangle(cell.X + 2, 2, segW - 4, Height - 4));
                        else if (i > 0)
                            using (var p = new Pen(border))
                                g.DrawLine(p, cell.X, 5, cell.X, Height - 5);

                        Color c = sel ? Color.White : (Enabled ? txt : Blend(txt, baseBg, 0.45f));
                        TextRenderer.DrawText(g, Options[i], f, cell, c,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }

                using (var p = new Pen(border))
                using (var path = DrawHelper.RoundedRect(full, 6))
                    g.DrawPath(p, path);
            }

            private static Color Blend(Color a, Color b, float t)
            {
                return Color.FromArgb(
                    (int)(a.R * t + b.R * (1 - t)),
                    (int)(a.G * t + b.G * (1 - t)),
                    (int)(a.B * t + b.B * (1 - t)));
            }
        }
    }
}
