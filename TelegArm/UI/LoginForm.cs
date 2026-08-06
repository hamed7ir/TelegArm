using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Login redesigned to match Telegram Desktop: a spacious centered window with an accent (purple) title
    /// bar, QR-code as the DEFAULT view (Telegram logo in the QR center + numbered steps), filled accent
    /// action buttons, and a Phone↔QR toggle. Cosmetic only — the auth backbone (LoginUserIfNeeded /
    /// LoginWithQRCode + the AuthManager/Config bridge) is unchanged.
    /// </summary>
    public class LoginForm : MaterialForm
    {
        private enum View { Phone, Code, Password, Qr }

        private readonly TelegramService _service;
        private readonly MaterialSkinManager _skin;
        private readonly bool _dark;
        private readonly Color _accent, _fg, _sub, _field, _bg, _link;

        private Panel _header, _countryRow, _phoneRow;
        private ProxyStatusPill _proxyPill;          // BATCH-TA-16/P3 — bottom-left floating proxy pill
        private RoundedButton _settingsBtn;          // bottom-right — Settings is otherwise unreachable pre-login
        private Label _backBtn, _closeBtn, _bigTitle, _subtitle, _flagLabel, _countryName, _statusLabel, _qrSteps;
        private Label _qrToggleLink, _phoneToggleLink, _resendLink, _dpiRescueLink;
        private PictureBox _flagPic;
        private TextBox _dialBox, _phoneBox, _codeBox, _pwdBox;
        private AccentButton _submitButton;
        private QrControl _qr;

        private Country _country;
        private View _view = View.Qr;

        /// <summary>When true this is an ADD-ACCOUNT dialog (shown modally over MainForm): on success it sets
        /// DialogResult.OK and closes (the caller relocates the pending session + rebuilds), instead of
        /// creating a new MainForm; the back arrow on the root step cancels (DialogResult.Cancel).</summary>
        public bool AddMode { get; set; }
        private bool _syncing, _formatting;
        private bool _dragging; private Point _dragStart;
        private int _loginGen, _pwdAsks;
        private CancellationTokenSource _qrCts;

        private const int HeaderH = 48;

        public LoginForm(TelegramService service)
        {
            _service = service;
            _dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();
            _bg = _dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = _dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = _dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            _field = _dark ? Color.FromArgb(54, 54, 58) : Color.White;
            // Accent for TEXT links — brightened on dark (toward white) so the purple reads clearly like
            // Telegram Desktop's colored links; slightly deepened on light for contrast.
            _link = _dark ? Blend(_accent, Color.White, 0.40f) : Blend(_accent, Color.Black, 0.12f);

            _skin = MaterialSkinManager.Instance;
            _skin.AddFormToManage(this);
            _skin.Theme = _dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
            var a = (Primary)(uint)_accent.ToArgb();
            var msA = (Accent)(uint)_accent.ToArgb();   // accent slot = Windows accent (shared singleton — no blue re-poison)
            _skin.ColorScheme = new ColorScheme(a, a, a, msA, TextShade.WHITE);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            AuthManager.Reset();
            BuildUi();
            ApplyCountry(Countries.MatchDial("1") ?? FirstOr(Countries.All), true);

            AuthManager.CodeRequested += OnCodeRequested;
            AuthManager.PasswordRequested += OnPasswordRequested;
            FormClosed += (s, e) =>
            {
                AuthManager.CodeRequested -= OnCodeRequested;
                AuthManager.PasswordRequested -= OnPasswordRequested;
                StopQr();
            };
            // Start on the QR view AFTER the handle exists (StartQr marshals via the sync context).
            Shown += (s, e) => { ShowView(View.Qr); var ignore = RefreshCountriesAsync(); };
        }

        private static Country FirstOr(System.Collections.Generic.List<Country> list)
        { return list != null && list.Count > 0 ? list[0] : null; }

        private async Task RefreshCountriesAsync()
        { try { await Countries.RefreshLiveAsync(_service); } catch { } }

        private void BuildUi()
        {
            AutoScaleMode = AutoScaleMode.Font;
            Text = "TelegArm v" + Program.Version + " — Sign in";
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            FormStyle = FormStyles.ActionBar_None;
            ClientSize = new Size(480, 600);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; Sizable = false;
            int W = ClientSize.Width;

            // ── Accent title bar (back arrow + title + ✕) ──
            _header = new Panel { Left = 0, Top = 0, Width = W, Height = HeaderH, BackColor = _accent };
            _header.Paint += (s, e) =>
            {
                using (var b = new SolidBrush(_accent)) e.Graphics.FillRectangle(b, _header.ClientRectangle);
                int tx = (_backBtn != null && _backBtn.Visible) ? 50 : 20;
                TextRenderer.DrawText(e.Graphics, "Sign in to Telegram", FontHelper.Ui(12.5f, FontStyle.Bold),
                    new Rectangle(tx, 0, _header.Width - tx - 52, HeaderH), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            _header.MouseDown += (s, e) => { _dragging = true; _dragStart = e.Location; };
            _header.MouseMove += (s, e) => { if (_dragging) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); };
            _header.MouseUp += (s, e) => _dragging = false;
            _backBtn = new Label { Left = 8, Top = 0, Width = 40, Height = HeaderH, Cursor = Cursors.Hand, Visible = false };
            StyleBarButton(_backBtn, "‹", 21f);
            _backBtn.Click += (s, e) => OnBack();
            _header.Controls.Add(_backBtn);
            // ✕ close: in ADD mode → safely abort the add (non-destructive cancel); first-launch → close the app.
            _closeBtn = new Label { Left = W - 48, Top = 0, Width = 44, Height = HeaderH, Cursor = Cursors.Hand };
            StyleBarButton(_closeBtn, "✕", 13f);
            _closeBtn.Click += (s, e) => { StopQr(); if (AddMode) DialogResult = DialogResult.Cancel; Close(); };
            _header.Controls.Add(_closeBtn);
            Controls.Add(_header);

            // BATCH-TA-16/P3 — the floating proxy pill, bottom-left. This lives on the LOGIN screen
            // specifically: a user whose network blocks Telegram can never reach MainForm's settings, so
            // this is the only place the proxy can be configured when it is actually needed.
            // ⚠ LoginForm ONLY this release (TA-16/Amendment 3). MainForm already shows a "Connecting…"
            //   overlay and a second indicator beside it would read as a bug; the connected case is served
            //   by the Settings row instead.
            _proxyPill = new ProxyStatusPill { IsDark = _dark, AccentColor = _accent };
            _proxyPill.Left = 14;
            _proxyPill.Top = ClientSize.Height - _proxyPill.Height - 14;
            _proxyPill.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _proxyPill.Click += (s, e) => OpenProxySettings();
            Controls.Add(_proxyPill);
            _proxyPill.BringToFront();

            // Settings, bottom-RIGHT, opposite the proxy pill. The login screen is a dead end otherwise:
            // Settings is only reachable from MainForm's drawer, so anyone who cannot get past sign-in
            // could not change the theme, the DPI mode, diagnostic logging — or reach the connection card.
            // Same instance the drawer opens; it takes the service and works before authentication.
            _settingsBtn = new RoundedButton
            {
                Text = "Settings", Width = 104, Height = 34,
                Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f)
            };
            _settingsBtn.Left = ClientSize.Width - _settingsBtn.Width - 14;
            _settingsBtn.Top = ClientSize.Height - _settingsBtn.Height - 14;
            _settingsBtn.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _settingsBtn.Click += (s, e) => OpenSettings();
            Controls.Add(_settingsBtn);
            _settingsBtn.BringToFront();
            RefreshProxyPill();

            // ── Shared title / subtitle ──
            _bigTitle = new Label { Width = 440, Height = 36, ForeColor = _fg, Font = FontHelper.Ui(16.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            _subtitle = new Label { Width = 400, Height = 44, ForeColor = _sub, Font = FontHelper.Ui(10f), TextAlign = ContentAlignment.TopCenter };
            Controls.Add(_bigTitle); Controls.Add(_subtitle);

            // ── Phone: country row + matched "+code | number" row ──
            _countryRow = new Panel { Width = 360, Height = 48, BackColor = _field, Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle };
            _flagPic = new PictureBox { Left = 12, Top = 12, Width = 30, Height = 24, SizeMode = PictureBoxSizeMode.Zoom, BackColor = _field, Cursor = Cursors.Hand };
            _flagLabel = new Label { Left = 12, Top = 12, Width = 30, Height = 24, ForeColor = _sub, Font = FontHelper.Ui(8f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Visible = false, Cursor = Cursors.Hand };
            _countryName = new Label { Left = 52, Top = 0, Width = 274, Height = 46, ForeColor = _fg, TextAlign = ContentAlignment.MiddleLeft, Font = FontHelper.Ui(11.5f), Cursor = Cursors.Hand };
            var chevron = new Label { Text = "▾", Left = 328, Top = 0, Width = 26, Height = 46, ForeColor = _sub, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            foreach (Control c in new Control[] { _flagPic, _flagLabel, _countryName, chevron }) c.Click += (s, e) => OpenCountryPicker();
            _countryRow.Click += (s, e) => OpenCountryPicker();
            _countryRow.Controls.AddRange(new Control[] { _flagPic, _flagLabel, _countryName, chevron });

            _phoneRow = new Panel { Width = 360, Height = 48, BackColor = _field, BorderStyle = BorderStyle.FixedSingle };
            _dialBox = new TextBox { Left = 10, Top = 13, Width = 54, BorderStyle = BorderStyle.None, BackColor = _field, ForeColor = _fg, Font = FontHelper.Ui(13.5f), TextAlign = HorizontalAlignment.Center };
            _phoneBox = new TextBox { Left = 84, Top = 13, Width = 266, BorderStyle = BorderStyle.None, BackColor = _field, ForeColor = _fg, Font = FontHelper.Ui(13.5f) };
            _phoneRow.Paint += (s, e) =>
            {
                using (var p = new Pen(_dark ? Color.FromArgb(110, 110, 116) : Color.FromArgb(188, 188, 194), 1.6f))
                    e.Graphics.DrawLine(p, 72, 9, 72, _phoneRow.Height - 9);
            };
            _dialBox.TextChanged += OnDialChanged;
            _dialBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _phoneBox.Focus(); } };
            _phoneBox.TextChanged += (s, e) => { if (!_formatting) ReformatPhone(); };
            _phoneBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartPhoneLogin(); } };
            _phoneRow.Controls.AddRange(new Control[] { _dialBox, _phoneBox });
            Controls.Add(_countryRow); Controls.Add(_phoneRow);

            // ── Code / password fields ──
            _codeBox = new TextBox { Width = 300, Height = 46, BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(16f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };
            _codeBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitCode(); } };
            _pwdBox = new TextBox { Width = 300, Height = 46, BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true, Font = FontHelper.Ui(13.5f) };
            _pwdBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitPassword(); } };
            Controls.Add(_codeBox); Controls.Add(_pwdBox);

            // ── QR: big code with the app logo in the center + numbered steps ──
            _qr = new QrControl { Width = 240, Height = 240, Dark = Color.Black, Light = Color.White, CenterLogo = IconBitmap() };
            _qrSteps = new Label
            {
                Width = 330, Height = 96, ForeColor = _fg, Font = FontHelper.Ui(11f), TextAlign = ContentAlignment.TopLeft,
                Text = "1.    Open Telegram on your phone\n2.    Go to Settings ▸ Devices ▸ Add Device\n3.    Scan this image to Log In"
            };
            Controls.Add(_qr); Controls.Add(_qrSteps);

            // ── Toggle links (Telegram-Desktop text links) ──
            _qrToggleLink = AccentLink("Log in using QR code", () => ShowView(View.Qr));
            _phoneToggleLink = AccentLink("Log in using phone number", () => { StopQr(); ShowView(View.Phone); });
            _resendLink = AccentLink("Resend code", ResendCode);
            Controls.Add(_qrToggleLink); Controls.Add(_phoneToggleLink); Controls.Add(_resendLink);

            // DPI-rescue: on a high-DPI display the system-aware default renders this window small, and the
            // toggle that fixes it is buried in Settings (unreachable through the tiny UI). Surface it here as a
            // one-tap switch to proportional (DPI-unaware) scaling + self-restart. Shown only when warranted and
            // only on the safe root views (Qr/Phone) — see ShowView / DpiRescueApplicable.
            _dpiRescueLink = AccentLink("Display looks small?  Switch to larger UI", ApplyProportionalAndRestart);
            Controls.Add(_dpiRescueLink);

            // ── Shared filled-accent action button + status ──
            _submitButton = new AccentButton(_accent, _bg) { Width = 300, Height = 48 };
            _submitButton.Click += OnSubmitClick;
            _statusLabel = new Label { Width = 420, Height = 40, ForeColor = _sub, Font = FontHelper.Ui(9.5f), TextAlign = ContentAlignment.TopCenter };
            Controls.Add(_submitButton); Controls.Add(_statusLabel);

            // Hide everything but the header until Shown → ShowView(Qr) lays out the active view.
            // ⚠ BATCH-TA-16f/F1 — THE PROXY PILL MUST BE EXEMPT, and this line is why nobody could see it.
            // The pill is added at :143 and BringToFront()'d, but this blanket hide runs at the END of
            // BuildUi and switches off EVERY control except the header — including the pill. ShowView then
            // re-shows only the controls belonging to the active view (:355-357 hides an explicit list and
            // the switch below re-shows per view); the pill is in NO view, so nothing ever turns it back on.
            // It was therefore invisible on every sign-in surface: first run, post-logout, and add-account.
            // That is the one place a blocked user MUST be able to reach proxy settings, so it is exempt
            // here exactly like _header, and ShowView never touches it.
            foreach (Control c in Controls) if (c != _header && c != _proxyPill && c != _settingsBtn) c.Visible = false;
        }

        private Image IconBitmap() { try { return Icon != null ? Icon.ToBitmap() : null; } catch { return null; } }

        /// <summary>An owner-drawn ACCENT text link (Telegram-Desktop colored secondary link). Owner-drawn so the
        /// accent color survives MaterialSkin's child re-coloring; underlines + brightens on hover.</summary>
        private Label AccentLink(string text, Action onClick)
        {
            var link = new Label { Width = 380, Height = 26, Cursor = Cursors.Hand, AutoSize = false, BackColor = _bg };
            bool hover = false;
            link.MouseEnter += (s, e) => { hover = true; link.Invalidate(); };
            link.MouseLeave += (s, e) => { hover = false; link.Invalidate(); };
            link.Paint += (s, e) =>
            {
                var g = e.Graphics;
                Color back = link.Parent != null ? link.Parent.BackColor : _bg;
                using (var bb = new SolidBrush(back)) g.FillRectangle(bb, link.ClientRectangle);
                var font = FontHelper.Ui(10.5f, hover ? (FontStyle.Bold | FontStyle.Underline) : FontStyle.Bold);
                TextRenderer.DrawText(g, text, font, link.ClientRectangle, hover ? Scale(_link, 1.12f) : _link,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            link.Click += (s, e) => onClick();
            return link;
        }

        /// <summary>Owner-draws a title-bar glyph button flush with the accent bar (no contrasting chip) — fills
        /// the accent + white glyph, darken on press / lighten on hover. Owner-drawn so MaterialSkin can't paint
        /// a dark box behind it.</summary>
        private void StyleBarButton(Label lbl, string glyph, float size)
        {
            lbl.Text = "";
            lbl.BackColor = _accent;
            var font = FontHelper.Ui(size, FontStyle.Bold);
            bool hover = false, down = false;
            lbl.MouseEnter += (s, e) => { hover = true; lbl.Invalidate(); };
            lbl.MouseLeave += (s, e) => { hover = false; down = false; lbl.Invalidate(); };
            lbl.MouseDown += (s, e) => { down = true; lbl.Invalidate(); };
            lbl.MouseUp += (s, e) => { down = false; lbl.Invalidate(); };
            lbl.Paint += (s, e) =>
            {
                Color c = down ? Scale(_accent, 0.82f) : hover ? Scale(_accent, 1.16f) : _accent;
                using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, lbl.ClientRectangle);
                TextRenderer.DrawText(e.Graphics, glyph, font, lbl.ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
        }

        // Shared color math (also used by AccentButton).
        private static Color Scale(Color c, float f) { return Color.FromArgb(c.A, Clamp(c.R * f), Clamp(c.G * f), Clamp(c.B * f)); }
        private static Color Blend(Color a, Color b, float t) { return Color.FromArgb(255, (int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t)); }
        private static int Clamp(float v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

        /// <summary>Centers a control horizontally and sets its Top (form coordinates).</summary>
        private void Center(Control c, int top) { c.Left = (ClientSize.Width - c.Width) / 2; c.Top = top; }

        /// <summary>BATCH-TA-16/P3 — opens the shared proxy form, then re-reads the pill.
        /// The form persists on close; because LoginForm has not built a client yet, the new setting is
        /// picked up by TelegramService.EnsureClient on the next connect attempt. Applying a change to an
        /// ALREADY-CONNECTED client (and the warm pool) is the separate, gated problem — TA-16/P5.</summary>
        /// <summary>Opens the SAME SettingsForm the drawer opens. It takes the service and does not
        /// require an authorised connection; its Devices page fetches lazily only when selected.</summary>
        private void OpenSettings()
        {
            try { using (var dlg = new SettingsForm(_service)) dlg.ShowDialog(this); }
            catch (Exception ex) { Logger.Diag("[LOGIN] settings form failed: " + ex.Message); }
            // The theme may have changed under us, and the proxy may have been edited from its card.
            RefreshProxyPill();
        }

        private async void OpenProxySettings()
        {
            bool changed = false;
            try
            {
                using (var dlg = new ProxyForm(_service)) { dlg.ShowDialog(this); changed = dlg.ConnectionSettingsChanged; }
            }
            catch (Exception ex)
            {
                Logger.Diag("[PROXY] settings form failed: " + ex.Message);   // never echoes a link
            }
            RefreshProxyPill();
            if (changed) await ApplyProxyChangeAsync();
        }

        /// <summary>BATCH-TA-17 — apply a proxy change immediately on the LOGIN screen, so adding a proxy
        /// and connecting happen in one step instead of needing a restart.
        ///
        /// Simpler and safer than MainForm's equivalent: there is NO warm pool here and nothing is
        /// authorised yet, so there is no session to protect and no half-proxied account state possible.
        /// The client is DISCARDED rather than reconnected — the next attempt rebuilds it through
        /// EnsureClient, which applies the new transport. Then the in-flight sign-in is restarted, because
        /// a QR poll or a pending code request is bound to the connection we just dropped.</summary>
        private async System.Threading.Tasks.Task ApplyProxyChangeAsync()
        {
            string via = "(direct)";
            try { var u = AppSettings.Instance.ActiveProxyUrl; via = u == null ? "(direct)" : ProxyUrl.SafeForLog(u); } catch { }
            Logger.Diag("[PROXY] APPLY-LIVE (login) → " + via);

            ProxyStatus.NoteAttempt();      // pill says "Connecting…" across the swap
            _loginGen++;                    // invalidate any in-flight login continuation
            StopQr();
            try { AuthManager.SubmitCode(null); } catch { }   // release a waiter blocked on the old connection

            try { if (_service != null) await _service.DiscardFaultedClientAsync(); }
            catch (Exception ex) { Logger.Diag("[PROXY] APPLY-LIVE (login) discard failed: " + ex.Message); }

            if (IsDisposed) return;
            AuthManager.Reset();
            ShowView(View.Qr);              // restarts the QR poll on a FRESH client via EnsureClient
        }

        /// <summary>BATCH-TA-16b/B1 — the pill follows ProxyStatus by itself, so this only has to restart
        /// the wall clock for a possibly-changed proxy and re-anchor the control (its height tracks the
        /// caption). It must NOT set a state directly: the pill may only ever claim what the connect loop
        /// has actually observed.</summary>
        private void RefreshProxyPill()
        {
            if (_proxyPill == null) return;
            ProxyStatus.Reset();                                            // new proxy → fresh grace period
            _proxyPill.Top = ClientSize.Height - _proxyPill.Height - 14;
        }

        // ── Country ↔ dial-code two-way sync ──
        private void OpenCountryPicker()
        {
            using (var dlg = new CountryPickerForm(_dark, _accent))
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedCountry != null)
                    ApplyCountry(dlg.SelectedCountry, true);
        }

        private void OnDialChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            string d = DigitsOf(_dialBox.Text);
            var match = Countries.MatchDial(d);
            if (match != null && (_country == null || match.DialCode != _country.DialCode)) ApplyCountry(match, false);
            else ReformatPhone();   // unknown/partial code → leave as typed, just re-format the national part
        }

        private void ApplyCountry(Country c, bool setDial)
        {
            if (c == null) return;
            _country = c;
            _countryName.Text = c.Name;
            SetFlag(c);
            if (setDial) { _syncing = true; _dialBox.Text = "+" + c.DialCode; _syncing = false; }
            ReformatPhone();
        }

        private void SetFlag(Country c)
        {
            Image flag = EmojiRenderer.Available ? EmojiRenderer.Get(c.FlagEmoji) : null;
            if (flag != null)
            {
                var old = _flagPic.Image;
                _flagPic.Image = new Bitmap(flag);   // PictureBox disposes its Image → clone so the shared Noto bitmap survives
                if (old != null) old.Dispose();
                _flagPic.Visible = true; _flagLabel.Visible = false;
            }
            else { _flagPic.Image = null; _flagPic.Visible = false; _flagLabel.Visible = true; _flagLabel.Text = (c.Iso2 ?? "?").ToUpperInvariant(); }
        }

        private void ReformatPhone()
        {
            if (_country == null) return;
            _formatting = true;
            string digits = DigitsOf(_phoneBox.Text);
            string formatted = Countries.FormatNational(digits, _country.Pattern);
            if (formatted != _phoneBox.Text) { _phoneBox.Text = formatted; _phoneBox.SelectionStart = formatted.Length; }
            _formatting = false;
        }

        private static string DigitsOf(string s) { return string.IsNullOrEmpty(s) ? "" : new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(s, char.IsDigit))); }

        // ── View switching + back nav ──
        private void ShowView(View v)
        {
            _view = v;

            foreach (var c in new Control[] { _countryRow, _phoneRow, _codeBox, _pwdBox, _qr, _qrSteps,
                _subtitle, _submitButton, _qrToggleLink, _phoneToggleLink, _resendLink })
                c.Visible = false;
            _bigTitle.Visible = true;
            _statusLabel.Visible = true; _statusLabel.Text = "";
            _submitButton.Enabled = true;

            _backBtn.Visible = v != View.Qr || AddMode;   // QR is the root step now (except add-account = cancel)
            _header.Invalidate();

            switch (v)
            {
                case View.Phone:
                    Center(_bigTitle, 66); _bigTitle.Text = "Your Phone Number";
                    _subtitle.Visible = true; Center(_subtitle, 104); _subtitle.Text = "Please confirm your country code\nand enter your phone number.";
                    _countryRow.Visible = true; Center(_countryRow, 164);
                    _phoneRow.Visible = true; Center(_phoneRow, 222);
                    _submitButton.Visible = true; _submitButton.Text = "Next"; Center(_submitButton, 296);
                    Center(_statusLabel, 356);
                    _qrToggleLink.Visible = true; Center(_qrToggleLink, 410);
                    _phoneBox.Focus();
                    break;

                case View.Code:
                    Center(_bigTitle, 78); _bigTitle.Text = "Enter the Code";
                    _subtitle.Visible = true; Center(_subtitle, 116); _subtitle.Text = "We sent the code to your other Telegram device.";
                    _codeBox.Visible = true; Center(_codeBox, 170);
                    _submitButton.Visible = true; _submitButton.Text = "Verify"; Center(_submitButton, 238);
                    Center(_statusLabel, 300);
                    _resendLink.Visible = true; Center(_resendLink, 352);
                    _codeBox.Focus();
                    break;

                case View.Password:
                    Center(_bigTitle, 78); _bigTitle.Text = "Two-Step Verification";
                    _subtitle.Visible = true; Center(_subtitle, 116); _subtitle.Text = "Your account is protected with an additional password.";
                    _pwdBox.Visible = true; Center(_pwdBox, 170);
                    _submitButton.Visible = true; _submitButton.Text = "Submit"; Center(_submitButton, 238);
                    Center(_statusLabel, 300);
                    _pwdBox.Focus();
                    break;

                default: // Qr
                    Center(_qr, 64); _qr.Visible = true;
                    Center(_bigTitle, 320); _bigTitle.Text = "Scan From Mobile Telegram";
                    _qrSteps.Visible = true; _qrSteps.Left = (ClientSize.Width - _qrSteps.Width) / 2; _qrSteps.Top = 366;
                    Center(_statusLabel, 472);
                    _phoneToggleLink.Visible = true; Center(_phoneToggleLink, 512);
                    StartQr();
                    break;
            }

            // DPI-rescue affordance: only on the safe root views (Qr/Phone), only when warranted. Pinned near
            // the bottom, clear of the toggle links above.
            bool showRescue = DpiRescueApplicable() && (v == View.Qr || v == View.Phone);
            _dpiRescueLink.Visible = showRescue;
            if (showRescue)
            {
                // BATCH-TA-16f/F1 — this link used to be centred across the full 480px at y=560, which now
                // COLLIDES with the proxy pill: the pill sits at (14, 552) and is ~174x34, so it covers the
                // link's leading ~110px AND, being in front, swallows clicks there. Un-hiding the pill is
                // what exposed this. Inset past the pill instead of centring, so both stay usable.
                // Narrow blast radius: DpiRescueApplicable() is false in AddMode and false below 125% scale,
                // so this only ever runs on a high-DPI first-launch desktop — never on the RT device.
                int x = (_proxyPill != null && _proxyPill.Visible ? _proxyPill.Right : 8) + 10;
                _dpiRescueLink.SetBounds(x, 560, Math.Max(120, ClientSize.Width - x - 12), _dpiRescueLink.Height);
            }

            // The pill is view-independent chrome: re-assert it on EVERY view switch so no future view
            // branch can strand it again the way the BuildUi sweep did, and so its Top follows the caption
            // height when ProxyStatus flips it to "Connecting via proxy…" / "Proxy not working".
            if (_proxyPill != null)
            {
                _proxyPill.Visible = true;
                _proxyPill.Top = ClientSize.Height - _proxyPill.Height - 14;
                _proxyPill.BringToFront();
            }
            if (_settingsBtn != null)
            {
                _settingsBtn.Visible = true;
                _settingsBtn.Left = ClientSize.Width - _settingsBtn.Width - 14;
                _settingsBtn.Top = ClientSize.Height - _settingsBtn.Height - 14;
                _settingsBtn.BringToFront();
            }
        }

        /// <summary>The DPI-rescue affordance applies only on a FIRST-LAUNCH login (not add-account), while the
        /// app is still system-aware (DpiUnaware false) AND the display is scaled to at least 125% (systemDpi
        /// >= 120) — the case where the system-aware window renders too small. On RT/100% or when already
        /// unaware it never shows (avoids confusing noise).</summary>
        private bool DpiRescueApplicable()
        {
            return !AddMode && !AppSettings.Instance.DpiUnaware && Program.SystemDpi >= 120;
        }

        /// <summary>Persists the proportional (DPI-unaware) preference and relaunches so it takes effect pre-UI.
        /// Safe here: pre-login there is no session/QR state to lose (StopQr first). The relaunch passes
        /// "--restarted" so the new instance waits for this one to release the single-instance mutex.</summary>
        private void ApplyProportionalAndRestart()
        {
            try
            {
                StopQr();
                AppSettings.Instance.DpiUnaware = true;
                AppSettings.Instance.Save();   // synchronous flush; settings.json is under the user-writable data root
                System.Diagnostics.Debug.WriteLine("[DPI] rescue -> DpiUnaware=true persisted; relaunching (--restarted)");
                System.Diagnostics.Process.Start(Application.ExecutablePath, "--restarted");
                Application.Exit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DPI] rescue restart FAILED: " + ex.Message);
                try { MessageBox.Show(this, "Couldn't restart automatically - please close and reopen TelegArm to apply the larger UI.",
                    "TelegArm", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
            }
        }

        private void OnBack()
        {
            switch (_view)
            {
                case View.Code: EditNumber(); break;                                  // → Phone (release waiter)
                case View.Password:
                    _loginGen++;
                    AuthManager.SubmitPassword(null);   // release the helper's WaitForPassword (abandon)
                    StopQr();                            // in case this was QR-then-2FA
                    ShowView(View.Phone);
                    break;
                case View.Phone: ShowView(View.Qr); break;                            // Phone → QR (the root)
                default:                                                              // Qr root
                    if (AddMode) { StopQr(); DialogResult = DialogResult.Cancel; Close(); }
                    break;
            }
        }

        private void OnSubmitClick(object sender, EventArgs e)
        {
            if (_view == View.Phone) StartPhoneLogin();
            else if (_view == View.Code) SubmitCode();
            else if (_view == View.Password) SubmitPassword();
        }

        // ── Phone / code / password ──
        private void StartPhoneLogin()
        {
            string dial = DigitsOf(_dialBox.Text), national = DigitsOf(_phoneBox.Text);
            if (dial.Length == 0 || national.Length < 4) { _statusLabel.Text = "Please enter a valid phone number."; return; }
            string full = "+" + dial + national;
            AuthManager.PhoneNumber = full;
            _pwdAsks = 0;
            SetBusy("Connecting to Telegram…");
            System.Diagnostics.Debug.WriteLine("[LOGIN] code requested for " + full);
            int gen = ++_loginGen;
            ProxyStatus.NoteAttempt();   // TA-16b/B1 — starts the wall clock on the first attempt
            var task = _service.LoginAsync();
            task.ContinueWith(t => OnLoginCompleted(t, gen), TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SubmitCode() { AuthManager.SubmitCode((_codeBox.Text ?? "").Trim()); SetBusy("Verifying code…"); System.Diagnostics.Debug.WriteLine("[LOGIN] code submitted"); }
        private void SubmitPassword() { AuthManager.SubmitPassword(_pwdBox.Text ?? ""); SetBusy("Checking password…"); System.Diagnostics.Debug.WriteLine("[LOGIN] 2FA password submitted"); }

        private void EditNumber()
        {
            _loginGen++;
            AuthManager.SubmitCode(null);   // release any code waiter, then back to phone
            ShowView(View.Phone);
            _statusLabel.Text = "";
        }

        private async void ResendCode()
        {
            _statusLabel.Text = "Resending the code…";
            _loginGen++;
            AuthManager.SubmitCode(null);
            await Task.Delay(400);
            System.Diagnostics.Debug.WriteLine("[LOGIN] resend → re-request code");
            StartPhoneLogin();
        }

        private void OnCodeRequested() { BeginInvoke((Action)(() => { ShowView(View.Code); _statusLabel.Text = "We sent you a code. Enter it to continue."; })); }
        private void OnPasswordRequested()
        {
            BeginInvoke((Action)(() =>
            {
                _pwdAsks++;
                ShowView(View.Password);
                _statusLabel.Text = _pwdAsks > 1 ? "Incorrect password. Try again." : "Enter your two-step verification password.";
            }));
        }

        private void OnLoginCompleted(Task<TL.User> task, int gen)
        {
            if (gen != _loginGen) return;
            if (task.IsFaulted)
            {
                var ex = task.Exception != null ? task.Exception.GetBaseException() : null;
                string msg = ex != null ? ex.Message : "unknown error";
                _submitButton.Enabled = true;
                if (msg.IndexOf("PHONE_CODE_INVALID", StringComparison.OrdinalIgnoreCase) >= 0) { ShowView(View.Code); _statusLabel.Text = "That code was incorrect. Try again."; }
                else if (msg.IndexOf("PASSWORD_HASH_INVALID", StringComparison.OrdinalIgnoreCase) >= 0) { ShowView(View.Password); _statusLabel.Text = "Incorrect password. Try again."; }
                else if (msg.IndexOf("PHONE_NUMBER_INVALID", StringComparison.OrdinalIgnoreCase) >= 0) { ShowView(View.Phone); _statusLabel.Text = "That phone number isn't valid."; }
                else if (msg.IndexOf("PHONE_NUMBER_UNOCCUPIED", StringComparison.OrdinalIgnoreCase) >= 0) { ShowView(View.Phone); _statusLabel.Text = "This number isn't registered. Sign-up isn't supported here yet — register on your phone first."; }
                else
                {
                    _statusLabel.Text = "Couldn't reach Telegram — make sure your VPN is on, then tap again.\n(" + msg + ")";
                    // TA-16b/B1 — ONLY this branch is a transport failure. The four cases above (bad code,
                    // bad password, bad/unregistered number) all mean we REACHED Telegram and it answered,
                    // which is positive evidence the proxy works — counting them would eventually accuse a
                    // perfectly good proxy because the user mistyped a code.
                    ProxyStatus.NoteAttemptFailed();
                }
                System.Diagnostics.Debug.WriteLine("[LOGIN] faulted: " + msg);
                return;
            }
            ProxyStatus.NoteAuthorized();
            EnterApp(task.Result);
        }

        // ── QR ──
        private void StartQr()
        {
            StopQr();
            _qr.SetPayload(null);
            _pwdAsks = 0;
            _statusLabel.Text = "Waiting for you to scan the code…";
            _qrCts = new CancellationTokenSource();
            var ct = _qrCts.Token;
            int gen = ++_loginGen;
            System.Diagnostics.Debug.WriteLine("[QR] starting QR login");
            var task = _service.LoginWithQrAsync(url =>
            {
                System.Diagnostics.Debug.WriteLine("[QR] token url (render/refresh)");
                try { BeginInvoke((Action)(() => { if (!IsDisposed && _view == View.Qr) _qr.SetPayload(url); })); } catch { }
            }, ct);
            task.ContinueWith(t => OnQrCompleted(t, gen, ct), TaskScheduler.FromCurrentSynchronizationContext());
            // TEARDOWN-HYGIENE 1.1: OnQrCompleted RETURNS EARLY for a cancelled/superseded generation without
            // touching task.Exception — the faulted poll then surfaced as UnobservedTaskException (the two QR
            // crash records). Observe it unconditionally: teardown races log one [TEARDOWN] line, real faults
            // still reach crash.log.
            TelegramService.Observe(task, "qr-login");
        }

        private void StopQr()
        {
            if (_qrCts != null) { try { _qrCts.Cancel(); } catch { } _qrCts = null; }
        }

        private void OnQrCompleted(Task<TL.User> task, int gen, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || gen != _loginGen) return;
            if (task.IsFaulted)
            {
                var ex = task.Exception != null ? task.Exception.GetBaseException() : null;
                _statusLabel.Text = "QR login failed — make sure your VPN is on. (" + (ex != null ? ex.Message : "error") + ")";
                System.Diagnostics.Debug.WriteLine("[QR] faulted: " + (ex != null ? ex.Message : "?"));
                return;
            }
            System.Diagnostics.Debug.WriteLine("[QR] success");
            EnterApp(task.Result);
        }

        // ── Done ──
        private void EnterApp(TL.User user)
        {
            StopQr();
            string phone = AuthManager.PhoneNumber;
            if (string.IsNullOrEmpty(phone) && user != null && !string.IsNullOrEmpty(user.phone))
                phone = user.phone.StartsWith("+") ? user.phone : "+" + user.phone;
            if (!string.IsNullOrEmpty(phone)) { _service.SavePhone(phone); System.Diagnostics.Debug.WriteLine("[LOGIN] TelegArm.phone written (" + (AuthManager.PhoneNumber != null ? "phone flow" : "QR via user.phone") + ")"); }
            if (AddMode) { DialogResult = DialogResult.OK; Close(); return; }   // add-account: hand back to MainForm.AddAccount
            TelegramService.Observe(Countries.RefreshLiveAsync(_service), "countries-refresh");
            _statusLabel.Text = "Signed in as " + (user != null ? user.first_name : "user");
            var main = new MainForm(_service);
            main.FormClosed += (s, e) => Close();
            Hide();
            main.Show();
        }

        private void SetBusy(string status) { _submitButton.Enabled = false; _statusLabel.Text = status; }

        /// <summary>A filled, rounded ACCENT button (white centered text) with hover/press/disabled states —
        /// owner-drawn so it renders consistently under MaterialSkin / on RT (a plain Button's BackColor was
        /// being flattened to bare text).</summary>
        private sealed class AccentButton : Control
        {
            private readonly Color _accent, _parentBg;
            private bool _hover, _down;

            public AccentButton(Color accent, Color parentBg)
            {
                _accent = accent; _parentBg = parentBg;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
            protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color pbg = Parent != null ? Parent.BackColor : _parentBg;   // match the form so rounded corners blend
                using (var bg = new SolidBrush(pbg)) g.FillRectangle(bg, ClientRectangle);
                Color c = !Enabled ? LoginForm.Blend(_accent, pbg, 0.55f)
                        : _down ? LoginForm.Scale(_accent, 0.86f)
                        : _hover ? LoginForm.Scale(_accent, 1.10f) : _accent;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = DrawHelper.RoundedRect(r, 8)) using (var b = new SolidBrush(c)) g.FillPath(b, path);
                TextRenderer.DrawText(g, Text, FontHelper.Ui(12.5f, FontStyle.Bold), ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
