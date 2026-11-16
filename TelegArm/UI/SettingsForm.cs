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
        private ToggleRow _wsStrict, _wsChain;   // step 6.5: the WebSocket tunnel's options (committed in OnOk)
        private TextRow _wsPins;
        private ActionRow _clearCacheRow, _proxyRow;

        // Security (two-step verification) page.
        private SettingGroup _securityGroup;
        private bool _securityLoaded;
        private Account_Password _passwordState;

        // Privacy (account privacy rules) page.
        private readonly List<Tuple<InputPrivacyKey, SegmentedRow>> _privacyRows = new List<Tuple<InputPrivacyKey, SegmentedRow>>();
        private bool _privacyLoaded;

        // T22: the rules last read per key (their exceptions are re-sent on every change) and the "Exceptions" rows
        private readonly Dictionary<InputPrivacyKey, Account_PrivacyRules> _privacyRules = new Dictionary<InputPrivacyKey, Account_PrivacyRules>();
        private readonly Dictionary<InputPrivacyKey, ActionRow> _privacyExceptionRows = new Dictionary<InputPrivacyKey, ActionRow>();

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
            WindowBackdrop.Mark_(_rail);   // step 5.9: the rail paints the backdrop itself
            _rail.PageSelected += ShowPage;
            body.Controls.Add(host);    // Fill first: docking takes the last-added (the rail) first
            body.Controls.Add(_rail);
            UseOwnBody(body);

            _pages = new Panel[PageNames.Length];
            _stacks = new PageStack[PageNames.Length];
            for (int i = 0; i < PageNames.Length; i++)
            {
                var pageHost = new Panel { Dock = DockStyle.Fill, Visible = i == 0 };
                var scroll = ScrollHost.Wrap(pageHost, ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent, composited: true);   // + TouchScroller momentum; composited frames as the chat list
                WindowBackdrop.Attach(pageHost);   // step 5.9
                WindowBackdrop.Attach(scroll);
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

        /// <summary>Redesign 2.7b: MainForm's drawer opens Settings on the Appearance page.</summary>
        public void ShowAppearancePage() { ShowPage(PageAppearance); }

        /// <summary>Every message box of this form. Settings can be closed (and disposed: both callers wrap it in using)
        /// while a request of one of its async handlers is still running; a dialog owned by a disposed form throws, and from an
        /// async void handler that reaches the crash handler. Closed = nobody to tell: -1, as a dismissed dialog (review 2026-11-13).</summary>
        private int Say(string title, string message, params string[] buttons)
        {
            return IsDisposed ? -1 : ThemedDialog.Show(this, title, message, buttons);
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
                    Say("Startup", "Couldn't update the Windows startup setting — your account may not allow it.", "OK");
                    reverting = true;
                    try { start.On = StartupIsEnabled(); }   // back to the TRUE state, without re-running this handler
                    finally { reverting = false; }
                }
            };
            page.Section("Startup", start);

            // Step 2.7c (plan D8): the window's close button. Live; the "Ask" dialog's "Remember my choice" writes it too.
            var closeNow = CloseBehaviorSetting.Parse(AppSettings.Instance.CloseBehavior);
            var close = new SegmentedRow("When I close the window", new[] { "Minimise to tray", "Ask", "Exit" },
                                         closeNow == CloseAction.Ask ? 1 : closeNow == CloseAction.Exit ? 2 : 0)
            { Subtitle = "The X button and Alt+F4" };
            close.SelectedIndexChanged += (snd, ev) =>
            {
                int i = close.SelectedIndex;
                AppSettings.Instance.CloseBehavior = CloseBehaviorSetting.Format(i == 1 ? CloseAction.Ask : i == 2 ? CloseAction.Exit : CloseAction.MinimizeToTray);
                AppSettings.Instance.Save();
            };
            page.Section("Window", close);
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

            // Step 2.6 (plan 5): the accent — Follow Windows, a fixed colour, or Custom… (step 5.2: the in-app picker). Live,
            // like the theme; persisted as AppSettings.AccentColor ("" = follow Windows). TELEGARM_ACCENT still wins at startup.
            var accent = _accentRow = new SwatchRow("Accent colour", ThemeHelper.AccentOverride);
            accent.Changed += (snd, ev) =>
            {
                var c = accent.SelectedAccent;
                ThemeHelper.SetAccent(c);
                AppSettings.Instance.AccentColor = ThemeHelper.FormatAccent(c);
                AppSettings.Instance.Save();
            };
            accent.CustomRequested += (snd, ev) =>
            {
                var c = ColorPickerDialog.Pick(this, "Accent colour", accent.CustomColor ?? ThemeHelper.Palette.Accent);
                if (c.HasValue) accent.SetCustom(c.Value);
            };
            // Step 5.9: the window backdrop — a blurred, tinted copy of the desktop wallpaper behind the window's chrome.
            var winBg = new SegmentedRow("Window background", new[] { "Solid", "Wallpaper" }, s.WindowBackground == "wallpaper" ? 1 : 0)
            { Subtitle = "Wallpaper shows the desktop picture, blurred, behind the window" };
            winBg.SelectedIndexChanged += (snd, ev) =>
            {
                s.WindowBackground = winBg.SelectedIndex == 1 ? "wallpaper" : "";
                s.Save();
                WindowBackdrop.SetWanted(winBg.SelectedIndex == 1);
            };
            page.Section("Look", theme, accent, winBg);

            // Step 5.2 (owner, 2026-10-15): the base colour of each theme — the light theme's paper (white, warm white,
            // sepia…) and the dark theme's ground (graphite, true black…). Live; "" = the default.
            page.Section("Theme colours", _lightBaseRow = BaseRow("Light theme", false, s), _darkBaseRow = BaseRow("Dark theme", true, s));

            // Steps 5.4/5.5: the chat background of each theme; 5.7: the outgoing bubble.
            BuildChatBackgroundSection(page, s);
            _bubbleRow = SwatchRow.ForBubble("Outgoing bubbles", ThemeHelper.OutBubble);
            _bubbleRow.Changed += (snd, ev) =>
            {
                var c = _bubbleRow.SelectedAccent;
                ThemeHelper.SetOutBubble(c);
                s.Theme.OutBubble = ThemeHelper.FormatAccent(c);
                s.Save();
            };
            _bubbleRow.CustomRequested += (snd, ev) =>
            {
                var c = ColorPickerDialog.Pick(this, "Outgoing bubble colour", _bubbleRow.CustomColor ?? ThemeHelper.Palette.BubbleOut);
                if (c.HasValue) _bubbleRow.SetCustom(c.Value);
            };
            page.Section("Bubbles", _bubbleRow);

            // Step 3.8b: the interface scale — applied LIVE to every window when the thumb is released, then a 10 s
            // "keep this size?" that reverts; capped per screen so the main window always fits (the setting keeps its value).
            // Dragging redraws only the sample bubble under it.
            _scaleRow = new SliderRow("Interface scale", 50, 200, 25, Dpi.UserScalePercent);
            _scalePreview = new ScalePreviewRow();
            _scaleRow.ValueChanged += (snd, ev) => ChangeInterfaceScale();
            _scaleRow.Previewing += (snd, ev) => _scalePreview.PreviewPercent = _scaleRow.ShownValue;
            UpdateScaleLimit();
            page.Section("Size", _scaleRow, _scalePreview);

            // Owner 2026-11-13: the small animations, each with its own switch (one that misbehaves on a device goes off
            // alone). Applied at once: every animation asks Anim.Enabled when it starts.
            var anim = new ToggleRow("Animations", s.Animations) { Subtitle = "Switches, hover, the menu and popups move a little" };
            // Windows' own "Animation effects" setting stops them too, unless this is on (it says which way Windows is now).
            Func<string> winNote = () => Anim.SystemAnimationsOn ? "Windows has them on right now"
                                                                 : "Windows has them off right now: nothing moves until this is on";
            var animAlways = new ToggleRow("Also when Windows has animations off", s.AnimateDespiteSystem) { Subtitle = winNote() };
            var animToggles = new ToggleRow("Switches slide", s.AnimateToggles);
            var animHover = new ToggleRow("Buttons and rows fade on hover", s.AnimateHover);
            var animDrawer = new ToggleRow("The menu slides in", s.AnimateDrawer);
            var animPopups = new ToggleRow("Popups and menus fade in", s.AnimatePopups);
            var animScroll = new ToggleRow("Scrolling glides to the bottom and to messages", s.AnimateScroll);
            var animColumns = new ToggleRow("Pages slide in a narrow window", s.AnimateColumns);
            Action animParts = () => { animAlways.Enabled = animToggles.Enabled = animHover.Enabled = animDrawer.Enabled = animPopups.Enabled
                                       = animScroll.Enabled = animColumns.Enabled = anim.On; };
            animAlways.Changed += (snd, ev) => { s.AnimateDespiteSystem = animAlways.On; s.Save(); animAlways.Subtitle = winNote(); };
            anim.Changed += (snd, ev) => { s.Animations = anim.On; s.Save(); animParts(); };
            animToggles.Changed += (snd, ev) => { s.AnimateToggles = animToggles.On; s.Save(); };
            animHover.Changed += (snd, ev) => { s.AnimateHover = animHover.On; s.Save(); };
            animDrawer.Changed += (snd, ev) => { s.AnimateDrawer = animDrawer.On; s.Save(); };
            animPopups.Changed += (snd, ev) => { s.AnimatePopups = animPopups.On; s.Save(); };
            animScroll.Changed += (snd, ev) => { s.AnimateScroll = animScroll.On; s.Save(); };
            animColumns.Changed += (snd, ev) => { s.AnimateColumns = animColumns.On; s.Save(); };
            animParts();
            page.Section("Animations", anim, animAlways, animToggles, animHover, animDrawer, animPopups, animScroll, animColumns);

            // Step 5.7: start over, or carry a look to another PC.
            var reset = new ActionRow("Reset appearance", "Reset") { Subtitle = "Accent, theme colours, chat backgrounds and bubbles" };
            reset.Click += (snd, ev) => ResetAppearance(s);
            var export = new ActionRow("Export theme…", "Export") { Subtitle = "Saves these colours to a file (photos stay on this PC)" };
            export.Click += (snd, ev) => ExportTheme(s);
            var import = new ActionRow("Import theme…", "Import");
            import.Click += (snd, ev) => ImportTheme(s);
            page.Section("Manage", reset, export, import);
        }

        // ── Steps 5.4/5.5/5.7: the chat background, the bubbles, reset / export / import ─────────────────────────────

        private SwatchRow _accentRow, _lightBaseRow, _darkBaseRow, _bubbleRow;
        private bool _bgDark, _bgSync;
        private SegmentedRow _bgFor, _bgDirection, _bgPattern, _bgFit;
        private ChatPresetRow _bgPresets;
        private ThemePreviewRow _bgPreview;
        private SliderRow _bgDim;
        private ToggleRow _bgBlur;
        private static readonly string[] PatternNames = { "", "dots", "grid", "lines" };

        private ChatBackgroundSettings Bg() { return AppSettings.Instance.Theme.For(_bgDark); }

        private static int PresetIndex(ChatBackgroundSettings cs) { return cs.Mode == "" ? 0 : cs.Mode == "preset" ? cs.Preset + 1 : -1; }

        /// <summary>One section edits ONE theme's background at a time ("For the: Light theme / Dark theme", starting on the
        /// current one). Every change applies live (ChatBackdrop.NotifyChanged repaints the chat) and saves.</summary>
        private void BuildChatBackgroundSection(PageStack page, AppSettings s)
        {
            _bgDark = ThemeHelper.Palette.Dark;
            _bgFor = new SegmentedRow("For the", new[] { "Light theme", "Dark theme" }, _bgDark ? 1 : 0);
            _bgFor.SelectedIndexChanged += (a, b) =>
            {
                if (_bgSync) return;
                _bgDark = _bgFor.SelectedIndex == 1;
                _bgPresets.Dark = _bgDark;
                _bgPreview.Dark = _bgDark;
                SyncBackgroundRows();
            };
            _bgPreview = new ThemePreviewRow(_bgDark);
            _bgPresets = new ChatPresetRow("Background", _bgDark, PresetIndex(Bg()));
            _bgPresets.Changed += (a, b) =>
            {
                if (_bgSync) return;
                var cs = Bg(); int i = _bgPresets.Selected;
                cs.Mode = i <= 0 ? "" : "preset"; cs.Preset = Math.Max(0, i - 1);
                ApplyBackground(s);
            };
            var solid = new ActionRow("Solid colour…");
            solid.Click += (a, b) =>
            {
                var cs = Bg();
                var c = ColorPickerDialog.Pick(this, "Chat background colour", ThemeHelper.ParseAccent(cs.Color1) ?? ThemeHelper.PaletteFor(_bgDark).ChatBg);
                if (!c.HasValue) return;
                cs.Mode = "solid"; cs.Color1 = ThemeHelper.FormatAccent(c);
                ApplyBackground(s);
            };
            var gradient = new ActionRow("Gradient…") { Subtitle = "Two colours" };
            gradient.Click += (a, b) =>
            {
                var cs = Bg(); var pal = ThemeHelper.PaletteFor(_bgDark);
                var c1 = ColorPickerDialog.Pick(this, "Gradient: first colour", ThemeHelper.ParseAccent(cs.Color1) ?? pal.ChatBg);
                if (!c1.HasValue) return;
                var c2 = ColorPickerDialog.Pick(this, "Gradient: second colour", ThemeHelper.ParseAccent(cs.Color2) ?? ColorMath.Mix(c1.Value, pal.Accent, 0.25f));
                if (!c2.HasValue) return;
                cs.Mode = "gradient"; cs.Color1 = ThemeHelper.FormatAccent(c1); cs.Color2 = ThemeHelper.FormatAccent(c2);
                ApplyBackground(s);
            };
            _bgDirection = new SegmentedRow("Direction", new[] { "Across", "Down", "Diagonal" }, 0);
            _bgDirection.SelectedIndexChanged += (a, b) =>
            {
                if (_bgSync) return;
                int i = _bgDirection.SelectedIndex;
                Bg().Angle = i == 1 ? 90 : i == 2 ? 45 : 0;
                ApplyBackground(s);
            };
            _bgPattern = new SegmentedRow("Pattern", new[] { "None", "Dots", "Grid", "Lines" }, 0);
            _bgPattern.SelectedIndexChanged += (a, b) =>
            {
                if (_bgSync) return;
                var cs = Bg(); int i = _bgPattern.SelectedIndex;
                if (i <= 0) { if (cs.Mode == "pattern") cs.Mode = string.IsNullOrEmpty(cs.Color1) ? "" : "solid"; }
                else
                {
                    // the pattern goes over the colour in use: the solid's, a solid preset's, else the theme's chat colour
                    string under = cs.Mode == "solid" || cs.Mode == "pattern" ? cs.Color1
                                 : cs.Mode == "preset" && cs.Preset < Palette.ChatPresets.Length
                                     ? ThemeHelper.FormatAccent(_bgDark ? Palette.ChatPresets[cs.Preset].Dark1 : Palette.ChatPresets[cs.Preset].Light1) : "";
                    cs.Mode = "pattern"; cs.Pattern = PatternNames[i]; cs.Color1 = under;
                }
                ApplyBackground(s);
            };
            var photo = new ActionRow("Photo…") { Subtitle = "A picture from this PC" };
            photo.Click += (a, b) => ImportBackgroundPhoto(s);
            _bgFit = new SegmentedRow("Photo fit", new[] { "Fill", "Fit", "Centre" }, 0);
            _bgFit.SelectedIndexChanged += (a, b) =>
            {
                if (_bgSync) return;
                int i = _bgFit.SelectedIndex;
                Bg().Fit = i == 1 ? "fit" : i == 2 ? "center" : "fill";
                ApplyBackground(s);
            };
            _bgDim = new SliderRow("Dim the photo", 0, 80, 10, 0);
            _bgDim.Previewing += (a, b) =>
            {
                var clone = Bg().Clone(); clone.Dim = _bgDim.ShownValue;
                _bgPreview.Preview = clone;   // dragging re-bakes only the preview tile
            };
            _bgDim.ValueChanged += (a, b) =>
            {
                if (_bgSync) return;
                Bg().Dim = _bgDim.Value;
                ApplyBackground(s);
            };
            _bgBlur = new ToggleRow("Soft blur");
            _bgBlur.Changed += (a, b) =>
            {
                if (_bgSync) return;
                Bg().Blur = _bgBlur.On;
                ApplyBackground(s);
            };
            page.Section("Chat background", _bgFor, _bgPreview, _bgPresets, solid, gradient, _bgDirection, _bgPattern, photo, _bgFit, _bgDim, _bgBlur);
            page.Note("Down and diagonal gradients and photos repaint the chat while it scrolls: smooth on a PC, slower on a Surface.");
            SyncBackgroundRows();
        }

        private void ApplyBackground(AppSettings s)
        {
            s.Save();
            ChatBackdrop.NotifyChanged();
            SyncBackgroundRows();
        }

        /// <summary>Shows the edited theme's background in the rows (no events), and the options its kind has.</summary>
        private void SyncBackgroundRows()
        {
            if (_bgPresets == null) return;
            var cs = Bg();
            _bgSync = true;
            try
            {
                _bgFor.SelectedIndex = _bgDark ? 1 : 0;
                _bgPresets.Selected = PresetIndex(cs);
                _bgDirection.RowVisible = cs.Mode == "gradient";
                _bgDirection.SelectedIndex = cs.Angle == 90 ? 1 : cs.Angle == 45 ? 2 : 0;
                _bgPattern.SelectedIndex = cs.Mode == "pattern" ? Math.Max(1, Array.IndexOf(PatternNames, cs.Pattern)) : 0;
                bool photo = cs.Mode == "photo";
                _bgFit.RowVisible = photo; _bgDim.RowVisible = photo; _bgBlur.RowVisible = photo;
                _bgFit.SelectedIndex = cs.Fit == "fit" ? 1 : cs.Fit == "center" ? 2 : 0;
                _bgDim.SetValue(cs.Dim, false);
                _bgBlur.On = cs.Blur;
                _bgPreview.Preview = null;
                _bgPreview.Refresh2();
            }
            finally { _bgSync = false; }
        }

        private async void ImportBackgroundPhoto(AppSettings s)
        {
            string path;
            using (var dlg = new OpenFileDialog { Title = "Chat background photo", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            bool dark = _bgDark;
            try
            {
                UseWaitCursor = true;
                string name = await Task.Run(() => ChatBackdrop.ImportPhoto(path, dark));   // decode + downscale off the UI thread
                if (IsDisposed) return;
                var cs = AppSettings.Instance.Theme.For(dark);
                cs.Mode = "photo"; cs.ImageFile = name; cs.ImageRev++;
                ApplyBackground(s);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) Say("Chat background", ex is InvalidOperationException ? ex.Message : "That picture could not be opened: " + ex.Message, "OK");
            }
            finally { if (!IsDisposed) UseWaitCursor = false; }
        }

        private void ResetAppearance(AppSettings s)
        {
            if (Say("Reset appearance", "Go back to the default accent, theme colours, chat backgrounds and bubbles? "
                                  + "The theme (light / dark) and the interface scale stay as they are.", "Cancel", "Reset") != 1) return;
            s.AccentColor = ""; s.LightBase = ""; s.DarkBase = ""; s.Theme = new TelegArm.Core.ThemeSettings();
            s.Save();
            ThemeHelper.SetAccent(null); ThemeHelper.SetBase(false, null); ThemeHelper.SetBase(true, null); ThemeHelper.SetOutBubble(null);
            ChatBackdrop.NotifyChanged();
            RefreshAppearanceRows();
        }

        private void RefreshAppearanceRows()
        {
            if (_accentRow != null) _accentRow.Select(ThemeHelper.AccentOverride);
            if (_lightBaseRow != null) _lightBaseRow.Select(ThemeHelper.BaseFor(false));
            if (_darkBaseRow != null) _darkBaseRow.Select(ThemeHelper.BaseFor(true));
            if (_bubbleRow != null) _bubbleRow.Select(ThemeHelper.OutBubble);
            SyncBackgroundRows();
        }

        private static string NormColour(string v) { var c = ThemeHelper.ParseAccent(v); return c.HasValue ? ThemeHelper.FormatAccent(c) : ""; }

        private void ExportTheme(AppSettings s)
        {
            using (var dlg = new SaveFileDialog { Title = "Export theme", Filter = "TelegArm theme|*.json", FileName = "TelegArm theme.json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var theme = Newtonsoft.Json.JsonConvert.DeserializeObject<TelegArm.Core.ThemeSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(s.Theme));   // a copy
                foreach (var cs in new[] { theme.Light, theme.Dark })
                    if (cs.Mode == "photo") { cs.Mode = ""; cs.ImageFile = ""; }   // photos stay on this PC
                var doc = new { TelegArmTheme = 1, AccentColor = s.AccentColor, LightBase = s.LightBase, DarkBase = s.DarkBase, Theme = theme };
                try { File.WriteAllText(dlg.FileName, Newtonsoft.Json.JsonConvert.SerializeObject(doc, Newtonsoft.Json.Formatting.Indented)); }
                catch (Exception ex) { Say("Export theme", "The theme could not be saved: " + ex.Message, "OK"); }
            }
        }

        private void ImportTheme(AppSettings s)
        {
            string json;
            using (var dlg = new OpenFileDialog { Title = "Import theme", Filter = "TelegArm theme|*.json|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { json = File.ReadAllText(dlg.FileName); }
                catch (Exception ex) { Say("Import theme", "The file could not be read: " + ex.Message, "OK"); return; }
            }
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                if ((int?)o["TelegArmTheme"] != 1) throw new InvalidOperationException("This is not a TelegArm theme file.");
                var theme = o["Theme"] != null ? o["Theme"].ToObject<TelegArm.Core.ThemeSettings>() : new TelegArm.Core.ThemeSettings();
                theme.Repair();
                foreach (var cs in new[] { theme.Light, theme.Dark })
                {
                    if (cs.Mode == "photo") { cs.Mode = ""; cs.ImageFile = ""; }
                    cs.Color1 = NormColour(cs.Color1); cs.Color2 = NormColour(cs.Color2);
                }
                theme.OutBubble = NormColour(theme.OutBubble);
                s.AccentColor = NormColour((string)o["AccentColor"]);
                s.LightBase = NormColour((string)o["LightBase"]);
                s.DarkBase = NormColour((string)o["DarkBase"]);
                s.Theme = theme;
                s.Save();
                ThemeHelper.SetAccent(ThemeHelper.ParseAccent(s.AccentColor));
                ThemeHelper.SetBase(false, ThemeHelper.ParseAccent(s.LightBase));
                ThemeHelper.SetBase(true, ThemeHelper.ParseAccent(s.DarkBase));
                ThemeHelper.SetOutBubble(ThemeHelper.ParseAccent(theme.OutBubble));
                ChatBackdrop.NotifyChanged();
                RefreshAppearanceRows();
            }
            catch (Exception ex)
            {
                Say("Import theme", ex is InvalidOperationException ? ex.Message : "That file is not a theme TelegArm can read: " + ex.Message, "OK");
            }
        }

        /// <summary>Step 5.2 — one theme's base: Default, the presets, Custom… (clamped so the theme stays light / dark).</summary>
        private SwatchRow BaseRow(string caption, bool dark, AppSettings s)
        {
            var row = SwatchRow.ForBase(caption, dark, ThemeHelper.BaseFor(dark));
            row.Changed += (snd, ev) =>
            {
                var c = row.SelectedAccent;
                ThemeHelper.SetBase(dark, c);
                if (dark) s.DarkBase = ThemeHelper.FormatAccent(c); else s.LightBase = ThemeHelper.FormatAccent(c);
                s.Save();
            };
            row.CustomRequested += (snd, ev) =>
            {
                var c = ColorPickerDialog.Pick(this, caption + " colour", row.CustomColor ?? ThemeHelper.PaletteFor(dark).Base, x => Palette.ClampBase(x, dark));
                if (c.HasValue) row.SetCustom(Palette.ClampBase(c.Value, dark));
            };
            return row;
        }

        private SliderRow _scaleRow;
        private ScalePreviewRow _scalePreview;
        private bool _scaleBusy;

        /// <summary>Another screen has another fit cap (and so another current effective scale): re-read both, after the
        /// rescale pass (a row cannot refit while Dpi.Busy).</summary>
        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            try { BeginInvoke((Action)UpdateScaleLimit); } catch { }
        }

        private void ChangeInterfaceScale()
        {
            if (_scaleBusy || _scaleRow == null) return;
            int old = Dpi.UserScalePercent, now = _scaleRow.Value;
            if (now == old) return;
            _scaleBusy = true;
            try
            {
                AppForm.ApplyInterfaceScale(now);
                UpdateScaleLimit();   // the preview's "current" is the new scale while the question is up
                if (KeepScaleDialog.Ask(this, old))
                {
                    AppSettings.Instance.InterfaceScale = now;
                    AppSettings.Instance.Save();
                }
                else
                {
                    AppForm.ApplyInterfaceScale(old);
                    _scaleRow.SetValue(old, false);
                }
            }
            finally { _scaleBusy = false; }
            UpdateScaleLimit();
        }

        /// <summary>The slider cannot pick past this screen's fit cap; the subtitle says when the setting is above it.</summary>
        private void UpdateScaleLimit()
        {
            if (_scaleRow == null) return;
            Rectangle wa;
            try { wa = Screen.FromControl(this).WorkingArea; } catch { wa = Screen.PrimaryScreen.WorkingArea; }
            int cap = Dpi.FitCap(wa, MonitorDpiValue);
            _scaleRow.Limit = cap < 200 ? cap : 0;
            if (_scalePreview != null)
            {
                _scalePreview.SetRange(Dpi.ScaleOn(wa, MonitorDpiValue), Math.Min(_scaleRow.Maximum, cap));
                _scalePreview.PreviewPercent = Math.Min(_scaleRow.Value, cap);
            }
            _scaleRow.Subtitle = Dpi.UserScalePercent > cap ? "Limited to " + cap + "% on this screen"
                                                            : "Every window, live · Ctrl+0 resets";
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
            engine.Click += (snd, ev) => Say("Animated sticker engine (rlottie)", RLottie.Diagnose(), "OK");
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
            Say("Clear cache", "Cleared " + SizeText(freed), "OK");
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

            // Step 6.5 — the WebSocket tunnel's options. The tunnel itself is switched on in the Proxy dialog (the login
            // screen reaches that dialog too, which matters when Telegram is blocked); these apply to its connections.
            var s = AppSettings.Instance;
            var note = new NoteRow("The WebSocket tunnel reaches Telegram through Telegram Web's gateway, the way tg-ws-proxy "
                                   + "does. Turn it on in Proxy. These options apply while it is on.", 2);
            _wsStrict = new ToggleRow("Never connect directly", s.WsTunnelStrict)
            { Subtitle = "Fail rather than fall back to a direct connection" };
            _wsPins = new TextRow("Gateway addresses (DC=IPv4; …)", string.IsNullOrWhiteSpace(s.WsTunnelDcIps) ? WsRoutes.DefaultPins : s.WsTunnelDcIps)
            { Hint = WsRoutes.DefaultPins };
            _wsChain = new ToggleRow("Accept certificate chain errors", s.WsTunnelAcceptChainErrors)
            { Subtitle = "For outdated certificate stores; *.web.telegram.org only" };
            var probe = new ActionRow("Probe the gateways", "Probe");
            probe.Click += OnProbeGateways;
            page.Section("WebSocket tunnel", note, _wsStrict, _wsPins, _wsChain, probe);
        }

        /// <summary>Step 6.5 — TLS + upgrade to every pinned gateway (no MTProto, no account), with the addresses as typed.</summary>
        private async void OnProbeGateways(object sender, EventArgs e)
        {
            var row = sender as ActionRow;
            string errors;
            var o = new WsTunnelOptions { AcceptChainErrors = _wsChain.On, Pins = WsRoutes.ParsePins(_wsPins.Value, out errors) };
            if (errors != null) { Say("Gateway addresses", errors + "\n\nWrite one DC=address per entry, e.g. " + WsRoutes.DefaultPins, "OK"); return; }
            if (row != null) { row.Enabled = false; row.ActionText = "Probing…"; }
            List<string> lines;
            try { lines = await System.Threading.Tasks.Task.Run(() => WsTunnel.ProbeAsync(o)).ConfigureAwait(true); }
            catch (Exception ex) { lines = new List<string> { "Probe failed: " + ex.Message }; }
            if (IsDisposed) return;
            if (row != null) { row.Enabled = true; row.ActionText = "Probe"; }
            string routes = WsTunnelStats.Summary();
            Say("WebSocket gateways", string.Join("\n", lines.ToArray())
                              + (routes != null ? "\n\nLast connections: " + routes : ""), "OK");
        }

        /// <summary>Secret-free one-line summary of the proxy setting for the Settings row.</summary>
        private static string ProxySubtitle()
        {
            var s = AppSettings.Instance;
            string active = null;
            try { active = s.ActiveProxyUrl ?? s.ActiveSocksUrl; } catch { }
            int n = s.SavedProxies.Count;
            if (s.WsTunnelEnabled) return "WebSocket tunnel";
            if (active != null) return "Using " + (TelegArm.Core.ProxyUrl.IsSocks(active) ? "SOCKS5 " : "") + TelegArm.Core.ProxyUrl.SafeForLog(active);
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
                // T22: the people this setting makes an exception for
                string title = pk.Item1;
                var ex = new ActionRow("Exceptions", "Edit") { Subtitle = "Loading…", Enabled = false };
                ex.Click += (snd, ev) => EditPrivacyExceptions(key, title, seg);
                _privacyExceptionRows[key] = ex;
                rows.Add(ex);
            }
            page.Section("Who can see / contact me", rows.ToArray());
        }

        /// <summary>Fetches each rule. A rule that could not be loaded says so, and the next visit to the page loads what is
        /// still missing (it used to stay on "Loading…" for as long as Settings was open: review 2026-11-13).</summary>
        private async void LoadPrivacyAsync()
        {
            if (_service == null)
            {
                foreach (var row in _privacyExceptionRows.Values) row.Subtitle = "Sign in to change this";
                return;
            }
            bool failed = false;
            foreach (var pair in _privacyRows)
            {
                var key = pair.Item1;
                var seg = pair.Item2;
                if (seg.Enabled) continue;   // loaded on an earlier visit
                try
                {
                    var res = await _service.GetPrivacyAsync(key);
                    if (IsDisposed) return;
                    _privacyRules[key] = res;
                    ShowPrivacyExceptions(key);
                    int v = (int)TelegramService.ReducePrivacy(res?.rules);
                    seg.SetSelected(v, false);
                    seg.Applied = v;
                    seg.Enabled = true;
                    seg.Invalidate();
                }
                catch
                {
                    if (IsDisposed) return;
                    failed = true;   // the row stays disabled
                    ActionRow exRow;
                    if (_privacyExceptionRows.TryGetValue(key, out exRow)) exRow.Subtitle = "Couldn't load. Open this page again to retry.";
                }
            }
            if (failed && !IsDisposed) _privacyLoaded = false;
        }

        private async void OnPrivacyChanged(InputPrivacyKey key, SegmentedRow seg, int newIdx)
        {
            if (_service == null || newIdx == seg.Applied) return;
            int prev = seg.Applied;
            seg.Enabled = false;
            try
            {
                Account_PrivacyRules cur;
                _privacyRules.TryGetValue(key, out cur);
                var res = await _service.SetPrivacyAsync(key, (TelegramService.PrivacyPrimary)newIdx, cur);   // T22: exceptions kept
                if (IsDisposed) return;
                if (res != null) { _privacyRules[key] = res; ShowPrivacyExceptions(key); }
                int confirmed = (int)TelegramService.ReducePrivacy(res?.rules);   // reflect what the server actually set
                seg.SetSelected(confirmed, false);
                seg.Applied = confirmed;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                seg.SetSelected(prev, false);   // revert
                Say("Privacy", "Couldn't change this setting: " + ex.Message, "OK");
            }
            finally { if (!seg.IsDisposed) { seg.Enabled = true; seg.Invalidate(); } }
        }

        /// <summary>T22: "Always: 2 · Never: 1" on a key's Exceptions row.</summary>
        private void ShowPrivacyExceptions(InputPrivacyKey key)
        {
            ActionRow row;
            if (!_privacyExceptionRows.TryGetValue(key, out row)) return;
            Account_PrivacyRules rules;
            _privacyRules.TryGetValue(key, out rules);
            List<User> always, never;
            PrivacyRules.People(rules, out always, out never);
            row.Subtitle = always.Count == 0 && never.Count == 0 ? "None" : "Always: " + always.Count + " · Never: " + never.Count;
            row.Enabled = rules != null;
        }

        private async void EditPrivacyExceptions(InputPrivacyKey key, string title, SegmentedRow seg)
        {
            Account_PrivacyRules cur;
            if (_service == null || !_privacyRules.TryGetValue(key, out cur) || cur == null) return;
            List<User> always, never;
            PrivacyRules.People(cur, out always, out never);
            List<User> newAlways, newNever;
            using (var f = new PrivacyExceptionsForm(title, always, never, () => _service.GetContactsAsync()))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                newAlways = f.AlwaysAllow; newNever = f.NeverAllow;
            }
            try
            {
                var res = await _service.SetPrivacyAsync(key, TelegramService.ReducePrivacy(cur.rules), cur, newAlways, newNever);
                if (IsDisposed) return;
                if (res != null) { _privacyRules[key] = res; ShowPrivacyExceptions(key); }
            }
            catch (Exception ex) { Say("Privacy", "Couldn't save the exceptions: " + ex.Message, "OK"); }
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
                if (string.IsNullOrEmpty(np)) { Say("Set Password", "Password can't be empty.", "OK"); return; }
                if (np != cf) { Say("Set Password", "The passwords don't match.", "OK"); return; }
                try
                {
                    await _service.SetPasswordAsync(np, hint, email);
                    await ReloadSecurityAsync();
                    Say("Set Password", string.IsNullOrEmpty(email)
                        ? "Two-Step Verification is now ON."
                        : "Password set. Check your email and confirm the recovery address.", "OK");
                }
                catch (Exception ex) { Say("Set Password", FriendlyPwdError(ex), "OK"); }
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
                if (string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(np)) { Say("Change Password", "Fill in both passwords.", "OK"); return; }
                if (np != cf) { Say("Change Password", "The new passwords don't match.", "OK"); return; }
                try
                {
                    await _service.ChangePasswordAsync(cur, np, hint);
                    await ReloadSecurityAsync();
                    Say("Change Password", "Password changed.", "OK");
                }
                catch (Exception ex) { Say("Change Password", FriendlyPwdError(ex), "OK"); }
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
                    Say("Disable Password", "Two-Step Verification is now OFF.", "OK");
                }
                catch (Exception ex) { Say("Disable Password", FriendlyPwdError(ex), "OK"); }
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
                    Say("Confirm Email", "Recovery email confirmed.", "OK");
                }
                catch (Exception ex) { Say("Confirm Email", FriendlyPwdError(ex), "OK"); }
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
            int c = Say("Terminate session",
                "Terminate this session? The device will be logged out.", "Terminate", "Cancel");
            if (c != 0) return;
            try { await _service.ResetAuthorizationAsync(hash); }
            catch (Exception ex)
            {
                string msg = (ex.Message != null && ex.Message.IndexOf("FRESH_RESET_AUTHORISATION_FORBIDDEN", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "This session is too new to terminate — sessions can't be reset within 24 hours of signing in."
                    : ex.Message;
                Say("Terminate session", msg, "OK");
                return;
            }
            if (IsDisposed) return;
            if (row != null && !row.IsDisposed) { _devOthers.Controls.Remove(row); row.Dispose(); }
            int left = 0;
            foreach (Control x in _devOthers.Controls) if (x is SessionRowControl) left++;
            if (left == 0) Replace(_devOthers, new Control[] { new NoteRow("No other sessions.") });
            _termAllRow.Enabled = left > 0;
            _stacks[DevicesPage].Restack();
        }

        private async void OnTerminateAll(object sender, EventArgs e)
        {
            int c = Say("Terminate sessions",
                "Terminate ALL other sessions? Every other device will be logged out.", "Terminate all", "Cancel");
            if (c != 0) return;
            try { await _service.TerminateOtherSessionsAsync(); }
            catch (Exception ex) { Say("Terminate sessions", ex.Message, "OK"); return; }
            if (IsDisposed) return;
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
            // 2026-10-16: the round-video camera backend, session-only (CameraCapture.Forced): Auto = the Windows 10 frame
            // reader when this Windows has it, else the 8.1 photo poll. A way round a camera the live path misbehaves on.
            string curB = TelegArm.Core.Camera.CameraCapture.Forced;
            bool live = TelegArm.Core.Camera.CameraCapture.FrameReaderSupported;
            var backend = new SegmentedRow("Camera backend", new[] { "Auto", "Live", "Photo" }, curB == "framereader" ? 1 : curB == "winrt" ? 2 : 0)
            {
                Subtitle = live ? "Live: 30 fps (Windows 10). Photo: the 8.1 way" : "Photo only on this Windows",
                Enabled = live
            };
            backend.SelectedIndexChanged += (snd, ev) =>
            {
                int idx = backend.SelectedIndex;
                TelegArm.Core.Camera.CameraCapture.Forced = idx == 1 ? "framereader" : idx == 2 ? "winrt" : null;
            };
            page.Section("Camera (round video)", backend);

            // Redesign R5 — what the main window's keys do (the list lives beside the code, MainForm.Shortcuts).
            var keys = new ActionRow("Keyboard shortcuts", "Show") { Subtitle = "Chats, folders, editing and the window" };
            keys.Click += (snd, ev) =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var k in MainForm.Shortcuts) sb.Append(k[0]).Append("  —  ").Append(k[1]).Append(Environment.NewLine);
                Say("Keyboard shortcuts", sb.ToString().TrimEnd(), "OK");
            };
            page.Section("Keyboard", keys);

            // Owner 2026-11-16: when TelegArm was installed with "prepare it for this device", every update prepares it again
            // (a few minutes, about 8 on a Surface RT). On: updates skip that and install fast (Updater: "--fast").
            var fastUpd = new ToggleRow("Install updates fast", s.FastUpdates) { Subtitle = "Skip preparing for this device, which takes a few minutes" };
            fastUpd.Changed += (snd, ev) => { s.FastUpdates = fastUpd.On; s.Save(); };
            page.Section("Updates", fastUpd);

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
                    else Say("Crash reports", "No crashes recorded.", "OK");
                }
                catch { }
            };
            var crashClear = new ActionRow("Clear crash reports", "Clear");
            crashClear.Click += (snd, ev) =>
            {
                if (CrashLog.Count() == 0) { crashView.Subtitle = CrashStatusText(0); return; }
                if (Say("Crash reports", "Delete all recorded crash reports?", "Delete", "Cancel") != 0) return;
                CrashLog.Clear();
                crashView.Subtitle = CrashStatusText(CrashLog.Count());
            };
            rows.Add(crashView); rows.Add(crashClear);
            page.Section("Diagnostics", rows.ToArray());

            // ── DISPLAY & WINDOW (restart-applied: read once at startup) ──
            // SCROLL-SMOOTH-T1 A/B: composited scrolling (the style participates in handle creation).
            var compSw = new ToggleRow("Smoother scrolling (composited)", s.CompositedScroll) { Subtitle = "Less row overlap while scrolling · after restart" };
            compSw.Changed += (snd, ev) => { s.CompositedScroll = compSw.On; s.Save(); };
            // Step 3.7: the scaling MODE (STEP 0.4: DpiMode is the source of truth; Save mirrors DpiUnaware). Automatic =
            // per-monitor, the build default since 3.7 (stored as ""); System = one DPI for the session; Compatibility =
            // DPI-unaware, Windows stretches everything (blurry, but proportional) — the off-switch plan §0.4 asks for.
            // (It was a toggle that only wrote "Unaware" or "", wiping a hand-set "System".)
            string mode = (s.DpiMode ?? "").Trim().ToLowerInvariant();
            int modeIdx = mode == "system" ? 1 : mode == "unaware" ? 2 : 0;
            var dpiSw = new SegmentedRow("Display scaling mode", new[] { "Automatic", "System", "Compatibility" }, modeIdx)
            { Subtitle = "Compatibility is blurry but proportional · after restart" };
            dpiSw.SelectedIndexChanged += (snd, ev) =>
            {
                s.DpiMode = dpiSw.SelectedIndex == 1 ? "System" : dpiSw.SelectedIndex == 2 ? "Unaware" : "";
                s.Save();
            };
            // Step 2.6 (plan 2.6 "Advanced: ChromeMode"): TelegArm's own title bar, or the ordinary Windows frame — the
            // escape hatch of step 1.3, reachable without editing settings.json.
            bool native = string.Equals(s.ChromeMode, "Native", StringComparison.OrdinalIgnoreCase);
            var frame = new SegmentedRow("Window frame", new[] { "TelegArm", "Windows" }, native ? 1 : 0) { Subtitle = "Takes effect after restart" };
            frame.SelectedIndexChanged += (snd, ev) => { s.ChromeMode = frame.SelectedIndex == 1 ? "Native" : ""; s.Save(); };
            // STEP 1.1 — how the process is actually scaled. Tap for the full probe summary.
            var scale = new ActionRow("Display scaling") { Subtitle = Dpi.Mode + " · " + Dpi.SystemDpi + " dpi · " + Dpi.Request + " — tap for details" };
            scale.Click += (snd, ev) => Say("Display scaling", Dpi.ProbeSummary.Replace(" request=", "\nrequest=")
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
                Say("Copy diagnostics",
                    (copied ? "Copied to the clipboard:" : "The clipboard is busy. Select the text below and press Ctrl+C:")
                    + "\n\n" + report, "OK");
            };
            // Step 2.6 (plan R7): the data folder — useful on RT, where Documents is hidden behind the Metro shell.
            var data = new ActionRow("Open data folder", "Open") { Subtitle = "Settings, accounts and caches live here" };
            data.Click += (snd, ev) => { try { System.Diagnostics.Process.Start(StoragePaths.ResolveDataRoot()); } catch (Exception ex) { Say("Data folder", ex.Message, "OK"); } };
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

            // 2026-11-15: updates from GitHub. The found release shows as an Update pill in the main window's title bar.
            var s = AppSettings.Instance;
            var autoUpdate = new ToggleRow("Check for updates automatically", s.CheckForUpdates) { Subtitle = "Looks for a new version twice a day" };
            autoUpdate.Changed += (snd, ev) => { s.CheckForUpdates = autoUpdate.On; s.Save(); };
            var checkNow = new ActionRow("Check for updates", "Check") { Subtitle = "This is TelegArm " + Program.Version };
            checkNow.Click += async (snd, ev) =>
            {
                if (!checkNow.Enabled) return;
                checkNow.Enabled = false;
                checkNow.Subtitle = "Checking…";
                try
                {
                    var r = await Updater.CheckAsync();
                    if (IsDisposed) return;
                    if (r != null)
                    {
                        Updater.RaiseFound(r);
                        checkNow.Subtitle = "TelegArm " + r.Version.ToString(3) + " is out";
                        Say("Updates", "TelegArm " + r.Version.ToString(3) + " is out. Click Update in the title bar of the main window.", "OK");
                    }
                    else
                    {
                        checkNow.Subtitle = "You have the latest version (" + Program.Version + ")";
                        Say("Updates", "You have the latest version (" + Program.Version + ").", "OK");
                    }
                }
                catch (Exception ex)
                {
                    if (IsDisposed) return;
                    checkNow.Subtitle = "This is TelegArm " + Program.Version;
                    Say("Updates", "Couldn't check for updates: " + ex.Message, "OK");
                }
                finally { if (!IsDisposed) checkNow.Enabled = true; }
            };
            page.Section("Updates", autoUpdate, checkNow);
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
                Say("Cache folder",
                    "That media-cache folder can't be used, because " + cacheRefusal + ".\n\n" +
                    "TelegArm deletes old files from this folder, so it must not overlap where your account " +
                    "sessions are stored. Pick a dedicated folder (the default is a \"Cache\" folder).", "OK");
                return;   // NOTHING has been touched yet — in memory or on disk. The dialog stays open.
            }

            // Step 6.5 — the gateway addresses are free text: refused here, BEFORE any assignment (B1 above).
            string pinErrors = null, pins = null;
            if (_wsPins != null)
            {
                var map = WsRoutes.ParsePins(_wsPins.Value, out pinErrors);
                pins = WsRoutes.FormatPins(map);
                if (pinErrors == null && map.Count == 0) pinErrors = "No addresses.";
            }
            if (pinErrors != null)
            {
                ShowPage(PageConnection);
                Say("Gateway addresses", pinErrors + "\n\nWrite one DC=address per entry, e.g. " + WsRoutes.DefaultPins, "OK");
                return;   // nothing touched yet
            }

            var s = AppSettings.Instance;
            if (_wsPins != null)
            {
                if (pins == WsRoutes.DefaultPins) pins = "";   // the default stays implicit, so a future default reaches this user
                bool tunnelChanged = s.WsTunnelStrict != _wsStrict.On || s.WsTunnelAcceptChainErrors != _wsChain.On
                                     || (s.WsTunnelDcIps ?? "") != pins;
                s.WsTunnelStrict = _wsStrict.On;
                s.WsTunnelAcceptChainErrors = _wsChain.On;
                s.WsTunnelDcIps = pins;
                if (tunnelChanged)
                {
                    WsRoutes.Reset();
                    // the live connections were made with the old options: reconnect them, as a proxy change does
                    if (s.WsTunnelEnabled) ProxyChangeApplied?.Invoke();
                }
            }
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
            private readonly FadeSet _hoverT;   // the hover pill fades from page to page (Helpers.Anim)
            public event Action<int> PageSelected;
            private const int RowH = 40, PadX = 16, TopPad = 8;

            public SettingsRail(string[] names)
            {
                _names = names;
                _hoverT = new FadeSet(this, AnimKind.Hover, names.Length, Anim.HoverInMs, Anim.HoverOutMs);
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

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int r = RowAt(e.Y); if (r != _hover) { _hover = r; _hoverT.Active = r; } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _hoverT.Active = -1; }
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
                if (WindowBackdrop.Enabled) WindowBackdrop.Paint(g, this, e.ClipRectangle, p.Footer);   // step 5.9
                else g.Clear(p.Footer);
                using (var pen = new Pen(p.Divider)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                for (int i = 0; i < _names.Length; i++)
                {
                    var r = new Rectangle(0, top + i * rowH, Width - 1, rowH);
                    bool sel = i == _selected;
                    float ht = sel ? 0f : _hoverT[i];
                    if (sel || ht > 0f)
                    {
                        var inner = Rectangle.Inflate(r, -Dpi.Px(6, dpi), -Dpi.Px(2, dpi));
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var path = DrawHelper.RoundedRect(inner, Dpi.Px(6, dpi)))
                        using (var b = new SolidBrush(sel ? p.AccentSoft : ColorMath.WithAlpha(p.Hover, (int)Math.Round(p.Hover.A * ht)))) g.FillPath(b, path);
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
