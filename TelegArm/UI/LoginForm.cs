using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
    ///
    /// Redesign step 2.4: a fixed <see cref="AppForm"/> (was a MaterialForm), 480×600 logical. TelegArm's own title bar
    /// does the drag, the Back glyph (past the first step, or always when adding an account) and ✕ — the hand-drawn
    /// "‹" / "✕" labels and the manual Location drag are gone. Fields are ring fields; the "▾" / "▸" symbol characters
    /// are drawn or plain text (tofu on RT). Colours follow the live palette. The proxy pill and the Settings button stay
    /// (the only proxy/theme access before sign-in). EVERY auth path below — phone, code, 2FA, QR, AddMode, the live
    /// proxy re-apply, the DPI rescue — is unchanged.
    /// </summary>
    public class LoginForm : AppForm
    {
        private enum View { Phone, Code, Password, Qr }

        private readonly TelegramService _service;

        private RingPanel _countryRow, _phoneRow, _codeRow, _pwdRow;
        private ProxyStatusPill _proxyPill;          // BATCH-TA-16/P3 — bottom-left floating proxy pill
        private RoundedButton _settingsBtn;          // bottom-right — Settings is otherwise unreachable pre-login
        private Label _bigTitle, _subtitle, _flagLabel, _countryName, _statusLabel, _qrSteps;
        private Label _qrToggleLink, _phoneToggleLink, _resendLink;
        private PictureBox _flagPic;
        private TextBox _dialBox, _phoneBox, _codeBox, _pwdBox;
        private RoundedButton _submitButton;
        private QrControl _qr;

        private Country _country;
        private View _view = View.Qr;

        /// <summary>When true this is an ADD-ACCOUNT dialog (shown modally over MainForm): on success it sets
        /// DialogResult.OK and closes (the caller relocates the pending session + rebuilds), instead of
        /// creating a new MainForm; the back arrow on the root step cancels (DialogResult.Cancel).</summary>
        public bool AddMode { get; set; }
        private bool _syncing, _formatting;
        private int _loginGen, _pwdAsks;
        private CancellationTokenSource _qrCts;

        // 1.x laid the views out in FORM coordinates under a 48 px header; the same numbers, minus that header, are
        // logical offsets inside Content now (whatever the title bar's height: full 46 or compact 36 on RT).
        private const int HeaderH = 48;

        public LoginForm(TelegramService service) : base(false)
        {
            _service = service;

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
            Text = "TelegArm v" + Program.Version + " — Sign in";   // taskbar / Alt-Tab identity
            Bar.Title = "Sign in to Telegram";                         // what the bar shows (after Text, which also sets it)
            Bar.LeftClick += (s, e) => OnBack();
            Size = new Size(Px(480), Px(600));
            StartPosition = FormStartPosition.CenterScreen;
            int dpi = WindowDpiValue;
            // ✕ (the title bar's): in ADD mode → Close() on a modal dialog = Cancel (a safe, non-destructive abort);
            // first launch → close the app. FormClosed stops the QR poll in both cases.

            // BATCH-TA-16/P3 — the floating proxy pill, bottom-left. This lives on the LOGIN screen
            // specifically: a user whose network blocks Telegram can never reach MainForm's settings, so
            // this is the only place the proxy can be configured when it is actually needed.
            // ⚠ LoginForm ONLY this release (TA-16/Amendment 3). MainForm already shows a "Connecting…"
            //   overlay and a second indicator beside it would read as a bug; the connected case is served
            //   by the Settings row instead.
            _proxyPill = new ProxyStatusPill { Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            _proxyPill.Click += (s, e) => OpenProxySettings();
            Content.Controls.Add(_proxyPill);

            // Settings, bottom-RIGHT, opposite the proxy pill. The login screen is a dead end otherwise:
            // Settings is only reachable from MainForm's drawer, so anyone who cannot get past sign-in
            // could not change the theme, the DPI mode, diagnostic logging — or reach the connection card.
            // Same instance the drawer opens; it takes the service and works before authentication.
            _settingsBtn = new RoundedButton
            {
                Text = "Settings", Width = Px(104), Height = Px(34), Modern = true,
                Kind = RoundedButtonKind.Secondary, Font = TextRoles.Font(TextRole.Field, dpi),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            _settingsBtn.Click += (s, e) => OpenSettings();
            Content.Controls.Add(_settingsBtn);
            PlaceCorners();
            RefreshProxyPill();

            // ── Shared title / subtitle ──
            _bigTitle = new Label { Width = Px(440), Height = Px(36), Font = FontHelper.UiAt(16.5f, dpi, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            _subtitle = new Label { Width = Px(400), Height = Px(44), Font = FontHelper.UiAt(10f, dpi), TextAlign = ContentAlignment.TopCenter };
            Content.Controls.Add(_bigTitle); Content.Controls.Add(_subtitle);

            // ── Phone: country row + matched "+code | number" row (ring fields) ──
            _countryRow = new RingPanel { Width = Px(360), Height = Px(48), Cursor = Cursors.Hand, Chevron = true };
            _flagPic = new PictureBox { Left = Px(12), Top = Px(12), Width = Px(30), Height = Px(24), SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand };
            _flagLabel = new Label { Left = Px(12), Top = Px(12), Width = Px(30), Height = Px(24), Font = FontHelper.UiAt(8f, dpi, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Visible = false, Cursor = Cursors.Hand };
            _countryName = new Label { Left = Px(52), Top = Px(1), Width = Px(274), Height = Px(46), TextAlign = ContentAlignment.MiddleLeft, Font = FontHelper.UiAt(11.5f, dpi), Cursor = Cursors.Hand };
            foreach (Control c in new Control[] { _flagPic, _flagLabel, _countryName }) c.Click += (s, e) => OpenCountryPicker();
            _countryRow.Click += (s, e) => OpenCountryPicker();
            _countryRow.Controls.AddRange(new Control[] { _flagPic, _flagLabel, _countryName });

            _phoneRow = new RingPanel { Width = Px(360), Height = Px(48), SeparatorX = 72 };
            _dialBox = new TextBox { Left = Px(10), Top = Px(13), Width = Px(54), BorderStyle = BorderStyle.None, Font = FontHelper.UiAt(13.5f, dpi), TextAlign = HorizontalAlignment.Center };
            _phoneBox = new TextBox { Left = Px(84), Top = Px(13), Width = Px(266), BorderStyle = BorderStyle.None, Font = FontHelper.UiAt(13.5f, dpi) };
            _dialBox.TextChanged += OnDialChanged;
            _dialBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _phoneBox.Focus(); } };
            _phoneBox.TextChanged += (s, e) => { if (!_formatting) ReformatPhone(); };
            _phoneBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartPhoneLogin(); } };
            _phoneRow.Controls.AddRange(new Control[] { _dialBox, _phoneBox });
            Content.Controls.Add(_countryRow); Content.Controls.Add(_phoneRow);

            // ── Code / password fields (ring fields around a borderless TextBox) ──
            _codeBox = new TextBox { BorderStyle = BorderStyle.None, Font = FontHelper.UiAt(16f, dpi, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };
            _codeBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitCode(); } };
            _codeRow = RingAround(_codeBox, Px(300), Px(46));
            _pwdBox = new TextBox { BorderStyle = BorderStyle.None, UseSystemPasswordChar = true, Font = FontHelper.UiAt(13.5f, dpi) };
            _pwdBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitPassword(); } };
            _pwdRow = RingAround(_pwdBox, Px(300), Px(46));
            Content.Controls.Add(_codeRow); Content.Controls.Add(_pwdRow);

            // ── QR: big code with the app logo in the center + numbered steps ──
            _qr = new QrControl { Width = Px(240), Height = Px(240), Dark = Color.Black, Light = Color.White, CenterLogo = IconBitmap() };
            _qrSteps = new Label
            {
                Width = Px(330), Height = Px(96), Font = FontHelper.UiAt(11f, dpi), TextAlign = ContentAlignment.TopLeft,
                // ">" not "▸": a symbol character comes out as a box on RT 8.1.
                Text = "1.    Open Telegram on your phone\n2.    Go to Settings > Devices > Add Device\n3.    Scan this image to Log In"
            };
            Content.Controls.Add(_qr); Content.Controls.Add(_qrSteps);

            // ── Toggle links (Telegram-Desktop text links) ──
            _qrToggleLink = AccentLink("Log in using QR code", () => ShowView(View.Qr));
            _phoneToggleLink = AccentLink("Log in using phone number", () => { StopQr(); ShowView(View.Phone); });
            _resendLink = AccentLink("Resend code", ResendCode);
            Content.Controls.Add(_qrToggleLink); Content.Controls.Add(_phoneToggleLink); Content.Controls.Add(_resendLink);

            // ── Shared filled-accent action button + status ──
            _submitButton = new RoundedButton { Width = Px(300), Height = Px(48), Kind = RoundedButtonKind.Primary, Modern = true, Font = FontHelper.UiAt(12.5f, dpi, FontStyle.Bold) };   // a11y-ok: its Text is set per step
            _submitButton.Click += OnSubmitClick;
            _statusLabel = new Label { Width = Px(420), Height = Px(40), Font = TextRoles.Font(TextRole.Field, dpi), TextAlign = ContentAlignment.TopCenter };
            Content.Controls.Add(_submitButton); Content.Controls.Add(_statusLabel);

            ApplyColors();

            // Hide everything but the corners until Shown → ShowView(Qr) lays out the active view.
            // ⚠ BATCH-TA-16f/F1 — THE PROXY PILL MUST BE EXEMPT, and this line is why nobody could see it.
            // This blanket hide runs at the END of BuildUi; ShowView then re-shows only the controls belonging to the
            // active view; the pill is in NO view, so nothing would ever turn it back on. It was invisible on every
            // sign-in surface: first run, post-logout, and add-account — the one place a blocked user MUST be able to
            // reach proxy settings. So it is exempt here, and ShowView never touches it.
            foreach (Control c in Content.Controls) if (c != _proxyPill && c != _settingsBtn) c.Visible = false;
        }

        /// <summary>Step 3.2: the corner pill and the Settings button re-measure themselves on a DPI change; put them back.</summary>
        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            PlaceCorners();
        }

        /// <summary>The proxy pill bottom-left and Settings bottom-right, inside Content (their height can change).</summary>
        private void PlaceCorners()
        {
            int m = Px(14), w = Content.ClientSize.Width, h = Content.ClientSize.Height;
            if (_proxyPill != null) { _proxyPill.Left = m; _proxyPill.Top = h - _proxyPill.Height - m; }
            if (_settingsBtn != null) { _settingsBtn.Left = w - _settingsBtn.Width - m; _settingsBtn.Top = h - _settingsBtn.Height - m; }
        }

        protected override void OnThemeChanged() { ApplyColors(); }

        /// <summary>Every colour from the palette — on build and on each theme change (1.x froze them at construction).</summary>
        private void ApplyColors()
        {
            var p = ThemeHelper.Palette;
            foreach (var l in new[] { _bigTitle, _countryName, _qrSteps }) if (l != null) l.ForeColor = p.Text;
            foreach (var l in new[] { _subtitle, _statusLabel, _flagLabel }) if (l != null) l.ForeColor = p.TextDim;
            foreach (var t in new[] { _dialBox, _phoneBox, _codeBox, _pwdBox }) if (t != null) { t.BackColor = p.Card; t.ForeColor = p.Text; }
            if (_flagPic != null) _flagPic.BackColor = p.Card;
            // Inside a ring the labels sit on the Card fill; left alone they would inherit the panel's system grey.
            foreach (var l in new[] { _countryName, _flagLabel }) if (l != null) l.BackColor = p.Card;
            foreach (var r in new[] { _countryRow, _phoneRow, _codeRow, _pwdRow }) if (r != null) r.BackColor = p.Card;
            if (_proxyPill != null) { _proxyPill.IsDark = p.Dark; _proxyPill.AccentColor = p.Accent; _proxyPill.Invalidate(); }
            foreach (Control c in Content.Controls) c.Invalidate(true);
        }

        private Image IconBitmap() { try { return Icon != null ? Icon.ToBitmap() : null; } catch { return null; } }

        /// <summary>An owner-drawn ACCENT text link (Telegram-Desktop colored secondary link), in Palette.AccentFg (readable
        /// on the window in both themes); underlines on hover.</summary>
        private Label AccentLink(string text, Action onClick)
        {
            var link = new Label { Width = Px(380), Height = Px(26), Cursor = Cursors.Hand, AutoSize = false };
            bool hover = false;
            link.MouseEnter += (s, e) => { hover = true; link.Invalidate(); };
            link.MouseLeave += (s, e) => { hover = false; link.Invalidate(); };
            link.Paint += (s, e) =>
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                using (var bb = new SolidBrush(link.Parent != null ? link.Parent.BackColor : p.Window)) g.FillRectangle(bb, link.ClientRectangle);
                var font = FontHelper.UiAt(10.5f, Dpi.Of(link), hover ? (FontStyle.Bold | FontStyle.Underline) : FontStyle.Bold);
                TextRenderer.DrawText(g, text, font, link.ClientRectangle, p.AccentFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            link.Click += (s, e) => onClick();
            return link;
        }

        /// <summary>A ring field around one borderless TextBox, the box centred vertically and inset horizontally.</summary>
        private RingPanel RingAround(TextBox box, int width, int height)
        {
            var ring = new RingPanel { Width = width, Height = height };
            ring.Controls.Add(box);
            ring.Layout += (s, e) =>
            {
                int inset = Px(10);
                box.SetBounds(inset, Math.Max(0, (ring.Height - box.Height) / 2), Math.Max(1, ring.Width - 2 * inset), box.Height);
            };
            ring.Click += (s, e) => box.Focus();
            return ring;
        }

        /// <summary>Centers a control horizontally in Content and sets its Top from 1.x's form-coordinate value.</summary>
        private void Center(Control c, int top1x) { c.Left = (Content.ClientSize.Width - c.Width) / 2; c.Top = Px(top1x - HeaderH); }

        /// <summary>Opens the SAME SettingsForm the drawer opens. It takes the service and does not
        /// require an authorised connection; its Devices page fetches lazily only when selected.</summary>
        private void OpenSettings()
        {
            try { using (var dlg = new SettingsForm(_service)) dlg.ShowDialog(this); }
            catch (Exception ex) { Logger.Diag("[LOGIN] settings form failed: " + ex.Message); }
            // The theme may have changed under us, and the proxy may have been edited from its card.
            RefreshProxyPill();
        }

        /// <summary>BATCH-TA-16/P3 — opens the shared proxy form, then re-reads the pill.
        /// The form persists on close; because LoginForm has not built a client yet, the new setting is
        /// picked up by TelegramService.EnsureClient on the next connect attempt. Applying a change to an
        /// ALREADY-CONNECTED client (and the warm pool) is the separate, gated problem — TA-16/P5.</summary>
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
            try { via = AppSettings.Instance.TransportLabel; } catch { }
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
            PlaceCorners();
        }

        // ── Country ↔ dial-code two-way sync ──
        private void OpenCountryPicker()
        {
            using (var dlg = new CountryPickerForm(ThemeHelper.Palette.Dark, ThemeHelper.Palette.Accent))
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

            foreach (var c in new Control[] { _countryRow, _phoneRow, _codeRow, _pwdRow, _qr, _qrSteps,
                _subtitle, _submitButton, _qrToggleLink, _phoneToggleLink, _resendLink })
                c.Visible = false;
            _bigTitle.Visible = true;
            _statusLabel.Visible = true; _statusLabel.Text = "";
            _submitButton.Enabled = true;

            // QR is the root step (except add-account, where Back = cancel).
            Bar.LeftKind = v != View.Qr || AddMode ? TitleBar.LeftButton.Back : TitleBar.LeftButton.None;

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
                    _codeRow.Visible = true; Center(_codeRow, 170);
                    _submitButton.Visible = true; _submitButton.Text = "Verify"; Center(_submitButton, 238);
                    Center(_statusLabel, 300);
                    _resendLink.Visible = true; Center(_resendLink, 352);
                    _codeBox.Focus();
                    break;

                case View.Password:
                    Center(_bigTitle, 78); _bigTitle.Text = "Two-Step Verification";
                    _subtitle.Visible = true; Center(_subtitle, 116); _subtitle.Text = "Your account is protected with an additional password.";
                    _pwdRow.Visible = true; Center(_pwdRow, 170);
                    _submitButton.Visible = true; _submitButton.Text = "Submit"; Center(_submitButton, 238);
                    Center(_statusLabel, 300);
                    _pwdBox.Focus();
                    break;

                default: // Qr
                    Center(_qr, 64); _qr.Visible = true;
                    Center(_bigTitle, 320); _bigTitle.Text = "Scan From Mobile Telegram";
                    _qrSteps.Visible = true; _qrSteps.Left = (Content.ClientSize.Width - _qrSteps.Width) / 2; _qrSteps.Top = Px(366 - HeaderH);
                    Center(_statusLabel, 472);
                    _phoneToggleLink.Visible = true; Center(_phoneToggleLink, 512);
                    StartQr();
                    break;
            }

            // The pill is view-independent chrome: re-assert it on EVERY view switch so no future view
            // branch can strand it again the way the BuildUi sweep did, and so its Top follows the caption
            // height when ProxyStatus flips it to "Connecting via proxy…" / "Proxy not working".
            if (_proxyPill != null) { _proxyPill.Visible = true; _proxyPill.BringToFront(); }
            if (_settingsBtn != null) { _settingsBtn.Visible = true; _settingsBtn.BringToFront(); }
            PlaceCorners();
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

        /// <summary>A ring field: a rounded Card-filled frame (Divider hairline, accent when a child has focus), optionally a
        /// vertical separator (the "+code | number" row) and a DRAWN chevron (the country row — "▾" is a box on RT).</summary>
        private sealed class RingPanel : Panel
        {
            /// <summary>Logical x of a separator line (0 = none).</summary>
            public int SeparatorX;
            public bool Chevron;

            public RingPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnControlAdded(ControlEventArgs e)
            {
                base.OnControlAdded(e);
                e.Control.GotFocus += (s, a) => Invalidate();
                e.Control.LostFocus += (s, a) => Invalidate();
                if (e.Control is TextBox) e.Control.BackColor = ThemeHelper.Palette.Card;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                g.Clear(Parent != null ? Parent.BackColor : p.Window);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool focused = ContainsFocus;
                using (var path = DrawHelper.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Dpi.Px(8, dpi)))
                {
                    using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);
                    using (var pen = new Pen(focused ? p.AccentGlyph : p.Divider, focused ? Dpi.F(1.5f, dpi) : 1f)) g.DrawPath(pen, path);
                }
                if (SeparatorX > 0)
                    using (var pen = new Pen(p.Divider, Dpi.F(1.6f, dpi)))
                        g.DrawLine(pen, Dpi.Px(SeparatorX, dpi), Dpi.Px(9, dpi), Dpi.Px(SeparatorX, dpi), Height - Dpi.Px(9, dpi));
                if (Chevron)
                {
                    float cx = Width - Dpi.Px(20, dpi), cy = Height / 2f, u = Dpi.F(4f, dpi);
                    using (var pen = new Pen(p.TextDim, Dpi.F(1.6f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, cx - u, cy - u / 2, cx, cy + u / 2);
                        g.DrawLine(pen, cx, cy + u / 2, cx + u, cy - u / 2);
                    }
                }
            }
        }
    }
}
