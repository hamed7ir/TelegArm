using System;
using System.Drawing;
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
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The behaviour is the
    /// 1.x one, move for move: Server / Port / Secret / Status rows, the Status row's real handshake test, the ⋮
    /// Share / Get QR Code menu (now in the title bar's slot), Connect and Add to list. What the 2.x chrome changes:
    ///  • NO AcceptButton, on purpose, and NO Cancel button (there never was one): Esc and ✕ dismiss, and Enter acts
    ///    only on whatever button the user has focused — it cannot switch transport by itself. The first tab stop is
    ///    the Status row (a test is harmless), never Connect.
    ///  • the details are flat rows on the window colour (with hairlines) instead of a bordered card, every length is
    ///    logical (Px), the secret is wrapped for the width the row really has, and the colours are palette tokens.
    ///  • the monospace face comes from FontCache (shared, never disposed) — the 1.x private Font was disposed here.
    ///  • the ⋮ menu is also reachable by right-click / long-press on any detail row: in the native-chrome escape
    ///    hatch the title bar (and so its ⋮) is hidden, and Share / Get QR Code must not vanish with it.
    /// </summary>
    public sealed class ProxyLinkForm : DialogForm
    {
        private const int SheetW = 430;

        private readonly string _url;              // ALREADY normalised by ProxyUrl.TryNormalize
        private readonly bool _alreadySaved;

        private DetailRow _statusRow;
        private bool _testing;
        private System.Threading.CancellationTokenSource _test;

        private GlyphButton _more;                 // the ⋮ in the title bar's slot
        private FlashLabel _copiedLabel;
        private Timer _copiedTimer;

        /// <summary>What the user picked. <see cref="ProxyLinkAction.Cancel"/> unless a button was pressed —
        /// closing with ✕, Esc or Alt+F4 all leave it at Cancel, which is the safe default.</summary>
        public ProxyLinkAction Action { get; private set; }

        /// <param name="normalizedUrl">A link that has ALREADY passed <see cref="ProxyUrl.TryNormalize"/>.
        /// This form does not validate — validation belongs to the one parser, at the seam.</param>
        /// <param name="alreadySaved">True when this exact link is already in the saved list, so the sheet
        /// says so and offers only Connect (which selects the existing entry — never a duplicate).</param>
        public ProxyLinkForm(string normalizedUrl, bool alreadySaved) : base("Proxy Server", SheetW, 340)
        {
            _url = normalizedUrl;
            _alreadySaved = alreadySaved;
            Action = ProxyLinkAction.Cancel;

            BuildUi();
        }

        private void BuildUi()
        {
            string host = ProxyUrl.HostOf(_url) ?? "";
            string port = ProxyUrl.PortOf(_url) ?? "";
            string secret = ProxyUrl.SecretOf(_url) ?? "";

            // ── the details ──
            var serverRow = new DetailRow("Server", host, DetailRow.ValueStyle.Line, false, DetailRow.RowH);
            var portRow = new DetailRow("Port", port, DetailRow.ValueStyle.Line, false, DetailRow.RowH);
            // step 6.6: a SOCKS5 link has no secret; its third line is the user name (the password is never shown)
            var secretRow = ProxyUrl.IsSocks(_url)
                ? new DetailRow("User", ProxyUrl.UserOf(_url) ?? "None", DetailRow.ValueStyle.Line, false, DetailRow.RowH)
                : new DetailRow("Secret", secret, DetailRow.ValueStyle.Mono, false, DetailRow.RowH);

            // ── Status: test the server BEFORE committing to it ──────────────────────────────────
            // The whole point of this row is that "Add to list" and "Connect" are decisions, and a user
            // should be able to find out whether a link from a channel actually works before making either.
            // It runs the SAME real-handshake test ProxyForm's "Test this proxy" runs — a throwaway client
            // on a temp session (R5-safe) — and deliberately reuses ProxyForm's exact result wording, so the
            // same proxy never reads differently in the two places it can be tested from.
            // Its height is fixed and tall enough for a two-line failure reason, so a result never resizes
            // the sheet while it is on screen (ProxyTester.Friendly() can return ~70 characters).
            _statusRow = new DetailRow("Status", "Check status", DetailRow.ValueStyle.Wrapped, true, DetailRow.StatusRowH);
            _statusRow.Hairline = false;
            _statusRow.Activated += (s, e) => StartTest();

            serverRow.ContextRequested += ShowOverflowMenu;
            portRow.ContextRequested += ShowOverflowMenu;
            secretRow.ContextRequested += ShowOverflowMenu;
            _statusRow.ContextRequested += ShowOverflowMenu;

            if (_alreadySaved)
                PopulateBody(serverRow, portRow, secretRow, _statusRow,
                    new NoteRow("This proxy is already in your list — connecting selects it instead of adding a "
                              + "second copy.", 2));
            else
                PopulateBody(serverRow, portRow, secretRow, _statusRow);

            // ── actions ── (first added = rightmost)
            var connect = AddFooterButton("Connect", RoundedButtonKind.Primary, DialogResult.None, 132);
            connect.Click += (s, e) => { Action = ProxyLinkAction.Connect; DialogResult = DialogResult.OK; };

            if (!_alreadySaved)
            {
                var addOnly = AddFooterButton("Add to list", RoundedButtonKind.Secondary, DialogResult.None, 132);
                addOnly.Click += (s, e) => { Action = ProxyLinkAction.AddOnly; DialogResult = DialogResult.OK; };
            }

            // BATCH-TA-18a — copy confirmation. There is NO shared copy-with-toast helper in this app (all
            // six Clipboard.SetText call sites are bare try/catch), and MainForm.ShowToast is a MainForm
            // method that would render BEHIND this modal anyway. Rather than invent an app-wide toast in a
            // polish batch, the confirmation is local: this label, left of the buttons, shown for two seconds.
            // It is added LAST, so the right-to-left footer puts it leftmost; its width is what the buttons leave.
            int used = Footer.Padding.Horizontal;
            foreach (Control b in Footer.Controls) used += b.Width + b.Margin.Horizontal;
            _copiedLabel = new FlashLabel
            {
                Margin = Padding.Empty,
                Height = Px(ButtonHeight),
                Width = Math.Max(Px(60), Px(SheetW) - used - Px(16)),
            };
            Footer.Controls.Add(_copiedLabel);

            // Esc dismisses without doing anything (DialogForm: Esc with no CancelButton = Cancel, and Action stays
            // Cancel); there is deliberately no AcceptButton, so Enter cannot switch transport by accident on a
            // form the user did not open themselves.

            // ── the ⋮ overflow menu (BATCH-TA-18a): the title bar's slot, just left of ✕ ──
            _more = new GlyphButton(GlyphButton.Glyph.More)
            {
                BarColor = ThemeHelper.Palette.TitleBar,
                Width = Px(TitleBar.ButtonWidth),
                Height = Bar.Height,
            };
            _more.Click += (s, e) =>
            {
                // The button's bottom-left, so the menu hangs below it like every other menu in the app.
                var pt = _more.PointToScreen(Point.Empty);
                pt.Y += _more.Height;
                ShowOverflowMenu(pt);
            };
            Bar.Slot = _more;
            // The bar is 46 px, or 36 compact (decided once the window exists): keep the button as tall as the bar.
            Bar.SizeChanged += (s, e) =>
            {
                if (_more != null && _more.Height != Bar.Height) { _more.Height = Bar.Height; Bar.PerformLayout(); }
            };

            FitToContent();
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();   // runs from DialogForm's constructor too, before _more exists
            if (_more != null) { _more.BarColor = ThemeHelper.Palette.TitleBar; _more.Invalidate(); }
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

        /// <summary>null = in progress / neutral, true = works, false = failed. The same three inks as
        /// ProxyRowControl (ProxyForm.cs:551) so the two surfaces agree visually as well as textually.</summary>
        private void SetStatus(string text, bool? ok)
        {
            if (_statusRow == null || _statusRow.IsDisposed) return;
            _statusRow.Value = text;
            _statusRow.ValueInk = ok == null ? DetailRow.Ink.Dim
                                : ok.Value ? DetailRow.Ink.Success
                                           : DetailRow.Ink.Danger;
            // Only the un-run state is an invitation to tap.
            _statusRow.Cursor = _testing ? Cursors.Default : Cursors.Hand;
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
            menu.ShowAt(screenPt);
        }

        /// <summary>Two-second inline confirmation. One shared one-shot timer, restarted per flash.</summary>
        private void Flash(string text, bool ok)
        {
            if (_copiedLabel == null || _copiedLabel.IsDisposed) return;
            _copiedLabel.SetMessage(text, ok);
            if (_copiedTimer == null)
            {
                _copiedTimer = new Timer { Interval = 2000 };
                _copiedTimer.Tick += (s, e) =>
                {
                    _copiedTimer.Stop();
                    if (_copiedLabel != null && !_copiedLabel.IsDisposed) _copiedLabel.ClearMessage();
                };
            }
            _copiedTimer.Stop();
            _copiedTimer.Start();
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
            }
            base.Dispose(disposing);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  One "caption · value" row of the details. A SettingRow (opaque, owner-drawn from the palette,
        //  logical lengths, DPI/theme handled by the base), so it needs no card and no child labels.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class DetailRow : SettingRow
        {
            /// <summary>Line = one line, ellipsized; Mono = the secret, hard-wrapped in the monospace face; Wrapped =
            /// word-wrapped (the Status text), centred vertically in a row whose height does not change.</summary>
            public enum ValueStyle { Line, Mono, Wrapped }
            /// <summary>Which palette ink the value is drawn in.</summary>
            public enum Ink { Accent, Dim, Success, Danger }

            /// <summary>Logical heights: an ordinary row, and the Status row (two lines of result text).</summary>
            public const int RowH = 44, StatusRowH = 48;

            private const float LabelPt = 9.5f, ValuePt = 10f, MonoPt = 9.5f;
            private const int CaptionW = 80, PadY = 8, LineGap = 2;

            private readonly ValueStyle _layout;
            private readonly bool _interactive;
            private readonly int _logicalHeight;
            private string _value;
            private Ink _ink = Ink.Accent;
            private bool _hairline = true;
            private static string _monoFamily;

            /// <summary>The row was activated: a left click or tap, or Space on the focused row (interactive rows only).</summary>
            public event EventHandler Activated;
            /// <summary>Right-click or long-press: the screen point to drop the ⋮ menu at.</summary>
            public event System.Action<Point> ContextRequested;

            public DetailRow(string caption, string value, ValueStyle layout, bool interactive, int logicalHeight)
                : base(caption, interactive)
            {
                _value = value ?? "";
                _layout = layout;
                _interactive = interactive;
                _logicalHeight = logicalHeight;
                AccessibleRole = interactive ? AccessibleRole.PushButton : AccessibleRole.StaticText;
                Refit();
            }

            public string Value
            {
                get { return _value; }
                set
                {
                    value = value ?? "";
                    if (value == _value) return;
                    _value = value;
                    Refit();
                    Invalidate();
                }
            }

            public Ink ValueInk
            {
                get { return _ink; }
                set { if (_ink == value) return; _ink = value; Invalidate(); }
            }

            /// <summary>A 1 px Divider line under the row (off for the last one).</summary>
            public bool Hairline
            {
                get { return _hairline; }
                set { if (_hairline == value) return; _hairline = value; Invalidate(); }
            }

            // ── geometry ─────────────────────────────────────────────────────────────────────────────

            private static int ValueX(int dpi) { return Dpi.Px(PadX + CaptionW, dpi); }
            private static int ValueW(int width, int dpi) { return Math.Max(1, width - ValueX(dpi) - Dpi.Px(PadX, dpi)); }

            public override int HeightFor(int width, int dpi)
            {
                int min = Dpi.Px(_logicalHeight, dpi);
                if (_layout != ValueStyle.Mono) return min;
                // The secret: as many lines as the width needs.
                return Math.Max(min, 2 * Dpi.Px(PadY, dpi) + Lines(width, dpi).Length * (LineHeight(MonoAt(MonoPt, dpi)) + Dpi.Px(LineGap, dpi)));
            }

            // ── the secret, wrapped ──────────────────────────────────────────────────────────────────

            private string[] Lines(int width, int dpi)
            {
                string wrapped = WrapFixed(_value, CharsThatFit(ValueW(width, dpi), MonoAt(MonoPt, dpi)));
                return wrapped.Length == 0 ? new[] { "" } : wrapped.Split('\n');
            }

            /// <summary>How many monospace characters fit in <paramref name="px"/>. Measured, not guessed —
            /// the mono face differs between the dev box and RT.</summary>
            private static int CharsThatFit(int px, Font mono)
            {
                int w8 = TextRenderer.MeasureText("00000000", mono, Size.Empty, TextFormatFlags.NoPadding).Width;
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

            /// <summary>Consolas when present (it is, on Win7+ and RT), else the generic monospace face. The shared
            /// pixel-unit font of FontCache: never dispose it.</summary>
            private static Font MonoAt(float pt, int dpi)
            {
                if (_monoFamily == null)
                {
                    string name = "Consolas";
                    try { if (FontHelper.Family(name) == null) name = FontFamily.GenericMonospace.Name; }
                    catch { name = "Courier New"; }
                    _monoFamily = name;
                }
                return FontCache.Get(_monoFamily, pt, FontStyle.Regular, dpi);
            }

            // ── input ────────────────────────────────────────────────────────────────────────────────

            protected override string AccessibleValueText { get { return _value; } }
            protected override string AccessibleAction { get { return _interactive ? "Check" : null; } }
            protected override void AccessibleInvoke() { RaiseActivated(); }

            private void RaiseActivated()
            {
                var h = Activated;
                if (h != null) h(this, EventArgs.Empty);
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                // Left only: Control.Click also fires for a right-click, which opens the menu instead.
                if (e.Button != MouseButtons.Left || !_interactive || !Enabled) return;
                Focus();
                RaiseActivated();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Right) return;
                var h = ContextRequested;
                if (h != null) h(PointToScreen(e.Location));
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (!_interactive || !Enabled || e.KeyCode != Keys.Space) return;
                RaiseActivated();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            // ── painting ─────────────────────────────────────────────────────────────────────────────

            private Color InkColor(Palette p)
            {
                switch (_ink)
                {
                    case Ink.Dim: return p.TextDim;
                    case Ink.Success: return p.Success;
                    case Ink.Danger: return p.Danger;
                    default: return p.AccentFg;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
                PaintBack(g, p);

                int vx = ValueX(dpi), vw = ValueW(Width, dpi);
                Color ink = Dimmed ? p.TextDisabled : InkColor(p);

                // The caption: bold, centred on the row — or level with the secret's first line on the tall row.
                int capTop = _layout == ValueStyle.Mono ? padY : 0;
                int capH = _layout == ValueStyle.Mono ? LineHeight(MonoAt(MonoPt, dpi)) : Height;
                DrawTextLine(g, Text, FontHelper.ForAt(Text, LabelPt, dpi, FontStyle.Bold), Dimmed ? p.TextDisabled : p.Text,
                             new Rectangle(padX, capTop, Math.Max(1, vx - padX - Dpi.Px(8, dpi)), capH), false, true);

                switch (_layout)
                {
                    case ValueStyle.Mono:
                        {
                            var mono = MonoAt(MonoPt, dpi);
                            int lh = LineHeight(mono), step = lh + Dpi.Px(LineGap, dpi), y = padY;
                            foreach (string line in Lines(Width, dpi))
                            {
                                TextRenderer.DrawText(g, line, mono, new Rectangle(vx, y, vw, lh), ink,
                                    BaseFlags | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                                y += step;
                            }
                            break;
                        }
                    case ValueStyle.Wrapped:
                        {
                            var font = FontHelper.ForAt(_value, ValuePt, dpi);
                            var flags = BaseFlags | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis;
                            int h = TextRenderer.MeasureText(_value, font, new Size(vw, 0), flags).Height;
                            int y = Math.Max(0, (Height - h) / 2);
                            TextRenderer.DrawText(g, _value, font, new Rectangle(vx, y, vw, Math.Max(1, Math.Min(h, Height - y))), ink, flags);
                            break;
                        }
                    default:
                        DrawTextLine(g, _value, FontHelper.ForAt(_value, ValuePt, dpi), ink, new Rectangle(vx, 0, vw, Height), false, true);
                        break;
                }

                if (_hairline)
                    using (var pen = new Pen(p.Divider, 1f))
                        g.DrawLine(pen, padX, Height - 1, Width - 1 - padX, Height - 1);

                if (_interactive)
                {
                    int inset = Dpi.Px(6, dpi), grow = Dpi.Px(4, dpi);
                    PaintFocusRing(g, p, new Rectangle(vx - grow, inset, vw + 2 * grow, Math.Max(1, Height - 2 * inset)), Dpi.Px(6, dpi), dpi);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  The two-second "Link copied" / "Couldn't copy" message in the footer, left of the buttons.
        //  It never changes Control.Visible (false for everything on a form that is not shown yet): empty text
        //  is simply nothing drawn, in the space the buttons leave.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class FlashLabel : Control
        {
            private const float Pt = 9f;
            private string _text = "";
            private bool _ok = true;

            public FlashLabel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                AccessibleRole = AccessibleRole.StaticText;
            }

            public void SetMessage(string text, bool ok)
            {
                _text = text ?? "";
                _ok = ok;
                AccessibleName = _text;
                Invalidate();
            }

            public void ClearMessage()
            {
                _text = "";
                AccessibleName = "";
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                Color back = p.Footer;
                for (Control c = Parent; c != null; c = c.Parent)
                    if (c.BackColor.A == 255) { back = c.BackColor; break; }
                e.Graphics.Clear(back);
                if (_text.Length == 0) return;
                TextRenderer.DrawText(e.Graphics, _text, FontHelper.ForAt(_text, Pt, Dpi.Of(this)), ClientRectangle,
                    _ok ? p.Success : p.Danger,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
