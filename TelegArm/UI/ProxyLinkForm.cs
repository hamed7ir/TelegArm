using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>What the user chose in <see cref="ProxyLinkForm"/>.</summary>
    public enum ProxyLinkAction
    {
        /// <summary>Dismissed — nothing was saved and nothing was applied.</summary>
        Cancel,
        /// <summary>Save it (if new), select it, and switch the connection over to it now.</summary>
        Connect,
        /// <summary>Save it into the list WITHOUT switching — collect now, test later.</summary>
        AddOnly
    }

    /// <summary>
    /// BATCH-TA-18 — the confirmation sheet shown when a tg://proxy · t.me/proxy · telegram.me/proxy link
    /// is tapped anywhere in the app (message text, an inline "Connect" button, a link-preview card, the
    /// shared-links gallery). It shows the server, port and secret, and does nothing until the user picks.
    ///
    /// ⚠⚠ IT MUST NEVER AUTO-CONNECT. ⚠⚠
    /// A proxy link arrives from a CHANNEL — untrusted input the user did not author. Switching the app's
    /// entire transport onto a stranger's server because a finger landed on a button is not a thing this
    /// app may do; every proxy in the list is something the user chose, and this sheet is where they choose.
    /// The same reasoning is why "Add to list" exists at all: a user browsing @ProxyMTProto can collect ten
    /// candidates and then run Test all in ProxyForm, without their session hopping servers ten times.
    ///
    /// ⚠ THE SECRET IS SHOWN BUT NEVER LOGGED. It is on screen so it can be compared against the post the
    /// user tapped; the caller logs <see cref="ProxyUrl.SafeForLog"/> (host:port) and nothing else. See the
    /// rule in ProxyUrl's remarks.
    ///
    /// ⚠ DO NOT ADD THE "sponsored channel" SENTENCE the official clients show here. It is FALSE in this
    /// app: we never call help.getPromoData, so an MTProxy's promoted channel can never appear in the chat
    /// list (see NOTES-carried.md §2c). Telling the user otherwise would be inventing a risk we don't have.
    /// </summary>
    public sealed class ProxyLinkForm : Form
    {
        private const int SheetW = 430;
        private const int Pad = 16;
        private const int LabelW = 96;
        private const int RowH = 40;
        /// <summary>Taller than the others so a two-line failure reason fits WITHOUT resizing the form
        /// while it is on screen. ProxyTester.Friendly() can return ~70 characters ("The server closed the
        /// connection — the secret is probably wrong for it."), which is two lines in this column.</summary>
        private const int StatusRowH = 48;

        private readonly string _url;              // ALREADY normalised by ProxyUrl.TryNormalize
        private readonly bool _alreadySaved;

        private Panel _content;
        private Color _bg, _card, _fg, _sub, _border, _accent;
        private bool _dark;

        private Font _mono;
        private int _secretLines = 1;

        private Label _statusValue;
        private bool _testing;
        private System.Threading.CancellationTokenSource _test;

        private Label _copiedLabel;
        private Timer _copiedTimer;

        /// <summary>What the user picked. <see cref="ProxyLinkAction.Cancel"/> unless a button was pressed —
        /// closing with ✕, Esc or Alt+F4 all leave it at Cancel, which is the safe default.</summary>
        public ProxyLinkAction Action { get; private set; }

        /// <param name="normalizedUrl">A link that has ALREADY passed <see cref="ProxyUrl.TryNormalize"/>.
        /// This form does not validate — validation belongs to the one parser, at the seam.</param>
        /// <param name="alreadySaved">True when this exact link is already in the saved list, so the sheet
        /// says so and offers only Connect (which selects the existing entry — never a duplicate).</param>
        public ProxyLinkForm(string normalizedUrl, bool alreadySaved)
        {
            _url = normalizedUrl;
            _alreadySaved = alreadySaved;
            Action = ProxyLinkAction.Cancel;

            ComputeTheme();
            BuildUi();
        }

        private void ComputeTheme()
        {
            _dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();
            _bg = _dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _card = _dark ? Color.FromArgb(50, 50, 55) : Color.White;
            _fg = _dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
            _sub = _dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
            _border = _dark ? Color.FromArgb(70, 70, 76) : Color.FromArgb(220, 220, 226);
        }

        private void BuildUi()
        {
            // Same chrome as ProxyForm/SettingsForm (TA-16e/E4): ThemedChrome owns the caption, so do NOT
            // set FormBorderStyle here and parent EVERYTHING to the returned content panel.
            Text = "Proxy Server";
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = FontHelper.Ui(9.5f);
            _mono = Mono(9.5f);

            string host = ProxyUrl.HostOf(_url) ?? "";
            string port = ProxyUrl.PortOf(_url) ?? "";
            string secret = ProxyUrl.SecretOf(_url) ?? "";

            int valueW = SheetW - Pad * 2 - LabelW - 24;
            string secretWrapped = WrapFixed(secret, CharsThatFit(valueW));
            _secretLines = Math.Max(1, secretWrapped.Split('\n').Length);
            int secretRowH = Math.Max(RowH, 14 + _secretLines * (_mono.Height + 2));

            int cardH = RowH * 2 + secretRowH + StatusRowH;
            int noteH = _alreadySaved ? 40 : 0;
            int clientH = Pad + cardH + (noteH > 0 ? 10 + noteH : 0) + 16 + 40 + Pad;

            // ⚠ + BarH. ThemedChrome.Apply CARVES THE CAPTION OUT of the existing client area
            // (content.Height = h - BarH, ThemedChrome.cs:54) — its own doc says to bump the height by BarH
            // before calling. Passing the CONTENT height here is what pushed the action buttons 44 px below
            // the visible edge in the first cut of this form: the card fitted, the buttons were simply gone.
            ClientSize = new Size(SheetW, clientH + ThemedChrome.BarH);
            _content = ThemedChrome.Apply(this, "Proxy Server", _accent, _dark, ShowOverflowMenu);
            _content.BackColor = _bg;

            // ── the card ──
            var card = new Panel
            {
                Left = Pad, Top = Pad, Width = SheetW - Pad * 2, Height = cardH,
                BackColor = _card
            };
            int rowsSplit = LabelW;
            int r1 = RowH, r2 = RowH * 2, r3 = r2 + secretRowH;
            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                using (var p = new Pen(_border))
                {
                    g.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
                    g.DrawLine(p, 0, r1, card.Width - 1, r1);
                    g.DrawLine(p, 0, r2, card.Width - 1, r2);
                    g.DrawLine(p, 0, r3, card.Width - 1, r3);
                    g.DrawLine(p, rowsSplit, 0, rowsSplit, card.Height - 1);
                }
            };
            _content.Controls.Add(card);

            AddRow(card, "Server", host, 0, RowH, false);
            AddRow(card, "Port", port, r1, RowH, false);
            AddRow(card, "Secret", secretWrapped, r2, secretRowH, true);

            // ── Status: test the server BEFORE committing to it ──────────────────────────────────
            // The whole point of this row is that "Add to list" and "Connect" are decisions, and a user
            // should be able to find out whether a link from a channel actually works before making either.
            // It runs the SAME real-handshake test ProxyForm's "Test this proxy" runs — a throwaway client
            // on a temp session (R5-safe) — and deliberately reuses ProxyForm's exact result wording, so the
            // same proxy never reads differently in the two places it can be tested from.
            _statusValue = AddRow(card, "Status", "Check status", r3, StatusRowH, false);
            _statusValue.Cursor = Cursors.Hand;
            _statusValue.Click += (s, e) => StartTest();

            int y = Pad + cardH;

            if (_alreadySaved)
            {
                y += 10;
                var note = new Label
                {
                    Left = Pad + 2, Top = y, Width = SheetW - Pad * 2 - 4, Height = noteH,
                    ForeColor = _sub, BackColor = _bg, Font = FontHelper.Ui(9f),
                    Text = "This proxy is already in your list — connecting selects it instead of adding a "
                         + "second copy."
                };
                _content.Controls.Add(note);
                y += noteH;
            }

            // ── actions ──
            y += 16;
            var connect = new RoundedButton
            {
                Text = "Connect", Width = 132, Height = 36, Top = y,
                Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(9.5f, FontStyle.Bold)
            };
            connect.Left = SheetW - Pad - connect.Width;
            connect.Click += (s, e) => { Action = ProxyLinkAction.Connect; DialogResult = DialogResult.OK; Close(); };
            _content.Controls.Add(connect);

            int leftMostBtn = connect.Left;
            if (!_alreadySaved)
            {
                var addOnly = new RoundedButton
                {
                    Text = "Add to list", Width = 132, Height = 36, Top = y,
                    Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9.5f)
                };
                addOnly.Left = connect.Left - 10 - addOnly.Width;
                addOnly.Click += (s, e) => { Action = ProxyLinkAction.AddOnly; DialogResult = DialogResult.OK; Close(); };
                _content.Controls.Add(addOnly);
                leftMostBtn = addOnly.Left;
            }

            // BATCH-TA-18a — copy confirmation. There is NO shared copy-with-toast helper in this app (all
            // six Clipboard.SetText call sites are bare try/catch), and MainForm.ShowToast is a MainForm
            // method that would render BEHIND this modal anyway. Rather than invent an app-wide toast in a
            // polish batch, the confirmation is local: this label, shown for two seconds.
            _copiedLabel = new Label
            {
                Left = Pad, Top = y, Width = Math.Max(60, leftMostBtn - Pad - 8), Height = 36,
                ForeColor = Color.FromArgb(74, 168, 92), BackColor = _bg, Font = FontHelper.Ui(9f),
                TextAlign = ContentAlignment.MiddleLeft, Visible = false
            };
            _content.Controls.Add(_copiedLabel);

            // Esc dismisses without doing anything; there is deliberately no AcceptButton, so Enter cannot
            // switch transport by accident on a form the user did not open themselves.
            CancelButton = null;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Action = ProxyLinkAction.Cancel; Close(); } };
        }

        /// <summary>Adds one label/value row and RETURNS the value label, so the Status row can keep a
        /// handle on it and rewrite itself in place.</summary>
        private Label AddRow(Panel card, string label, string value, int top, int height, bool mono)
        {
            var l = new Label
            {
                Left = 14, Top = top + 1, Width = LabelW - 22, Height = height - 2,
                Text = label, ForeColor = _fg, BackColor = _card,
                Font = FontHelper.Ui(9.5f, FontStyle.Bold),
                TextAlign = mono ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft
            };
            if (mono) l.Top = top + 12;
            card.Controls.Add(l);

            var v = new Label
            {
                Left = LabelW + 14, Top = top + (mono ? 11 : 1),
                Width = card.Width - LabelW - 24, Height = height - (mono ? 14 : 2),
                Text = value, ForeColor = _accent, BackColor = _card,
                Font = mono ? _mono : FontHelper.Ui(10f),
                TextAlign = mono ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
                UseMnemonic = false      // a secret can contain '&'; without this it would render as an underline
            };
            card.Controls.Add(v);
            return v;
        }

        // ── Status: the real handshake test ──────────────────────────────────────────────────────
        /// <summary>Runs ONE real MTProto connection through this proxy and reports it in the Status row.
        /// ⚠ R5-safe: <see cref="ProxyTester"/> points the throwaway client at a FRESH temp session, never
        /// at an account's, so this is safe to run while the app is connected. See ProxyTester's remarks.
        /// Re-entrancy is guarded — every test negotiates a new auth key, which is real work for both
        /// Telegram and the proxy, so a user hammering the row must not fan out handshakes.</summary>
        private async void StartTest()
        {
            if (_testing || IsDisposed) return;
            _testing = true;
            SetStatus("Testing…", null);
            try
            {
                _test = new System.Threading.CancellationTokenSource();
                // ConfigureAwait(true): the continuation touches controls, so it must come back to the UI thread.
                var res = await ProxyTester.TestAsync(_url, _test.Token).ConfigureAwait(true);
                if (IsDisposed) return;
                // ProxyForm's exact wording (ProxyForm.cs:386) — the same proxy must never read differently
                // in the two places it can be tested from.
                if (res.Ok) SetStatus("Works · " + res.Ms + " ms", true);
                else SetStatus(res.Error ?? "Failed", false);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetStatus("Couldn't test that proxy.", false);
                Logger.Diag("[PROXYLINK] test failed for " + ProxyUrl.SafeForLog(_url) + ": " + ex.GetType().Name);
            }
            finally
            {
                _testing = false;
                try { if (_test != null) { _test.Dispose(); _test = null; } } catch { }
            }
        }

        /// <summary>null = in progress / neutral, true = works, false = failed. Colours match ProxyRowControl
        /// (ProxyForm.cs:551) so the two surfaces agree visually as well as textually.</summary>
        private void SetStatus(string text, bool? ok)
        {
            if (_statusValue == null || _statusValue.IsDisposed) return;
            _statusValue.Text = text;
            _statusValue.ForeColor = ok == null ? _sub
                                   : ok.Value ? Color.FromArgb(74, 168, 92)
                                              : Color.FromArgb(214, 78, 78);
            // Only the un-run state is an invitation to tap.
            _statusValue.Cursor = _testing ? Cursors.Default : Cursors.Hand;
        }

        // ── ⋮ overflow menu (BATCH-TA-18a) ───────────────────────────────────────────────────────
        /// <summary>Share / Get QR Code.
        /// ⚠ BOTH ACT ON THE PROXY THIS SHEET IS SHOWING (<c>_url</c>) — never on the ACTIVE one. The sheet
        /// exists precisely because this link is not in use yet; sharing the connected proxy instead would
        /// hand out a different credential than the one on screen, which is the kind of mistake nobody
        /// notices until it has already been sent.
        /// NOTE there is no "Send to chat" entry: ForwardPickerDialog carries no payload (it selects chats
        /// and returns them), and this modal holds none of the state it needs — _allChats, the avatar
        /// getters, dark/accent — so wiring it would be MainForm plumbing, not reuse (TA-18a/A1).</summary>
        private void ShowOverflowMenu(Point screenPt)
        {
            var menu = new ThemedContextMenuStrip();
            // ONE implementation, shared with ProxyForm's row menu — see ProxyShare's remarks for why a
            // second copy of this is dangerous rather than merely untidy.
            ProxyShare.AddMenuItems(menu, this, _url, Flash);
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        /// <summary>Two-second inline confirmation. One shared one-shot timer, restarted per flash.</summary>
        private void Flash(string text, bool ok)
        {
            if (_copiedLabel == null || _copiedLabel.IsDisposed) return;
            _copiedLabel.Text = text;
            _copiedLabel.ForeColor = ok ? Color.FromArgb(74, 168, 92) : Color.FromArgb(214, 78, 78);
            _copiedLabel.Visible = true;
            if (_copiedTimer == null)
            {
                _copiedTimer = new Timer { Interval = 2000 };
                _copiedTimer.Tick += (s, e) =>
                {
                    _copiedTimer.Stop();
                    if (_copiedLabel != null && !_copiedLabel.IsDisposed) _copiedLabel.Visible = false;
                };
            }
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }


        /// <summary>How many monospace characters fit in <paramref name="px"/>. Measured, not guessed —
        /// the mono face differs between the dev box and RT.</summary>
        private int CharsThatFit(int px)
        {
            int w8 = TextRenderer.MeasureText("00000000", _mono, Size.Empty, TextFormatFlags.NoPadding).Width;
            int cw = Math.Max(1, w8 / 8);
            return Math.Max(8, px / cw);
        }

        /// <summary>Hard-wraps at a fixed character count. A secret is unbroken hex/base64, so WinForms'
        /// word wrapping does nothing with it — it would render one clipped line.</summary>
        private static string WrapFixed(string s, int chars)
        {
            if (string.IsNullOrEmpty(s) || chars <= 0) return s ?? "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i += chars)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(s.Substring(i, Math.Min(chars, s.Length - i)));
            }
            return sb.ToString();
        }

        /// <summary>Consolas when present (it is, on Win7+ and RT), else the generic monospace face.</summary>
        private static Font Mono(float size)
        {
            try
            {
                var f = new Font("Consolas", size);
                if (string.Equals(f.Name, "Consolas", StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch { }
            return new Font(FontFamily.GenericMonospace, size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // A handshake in flight must not outlive the sheet: ProxyTester tears the throwaway client
                // down and deletes its temp session on cancellation, and an orphaned one would keep a
                // connection (and a temp directory) alive after the user has walked away.
                try { if (_test != null) _test.Cancel(); } catch { }
                if (_copiedTimer != null) { try { _copiedTimer.Stop(); _copiedTimer.Dispose(); } catch { } _copiedTimer = null; }
                if (_mono != null) { try { _mono.Dispose(); } catch { } _mono = null; }
            }
            base.Dispose(disposing);
        }
    }
}
