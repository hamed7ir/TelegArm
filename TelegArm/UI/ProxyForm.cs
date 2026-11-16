using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// BATCH-TA-16/P2+P4 — MTProto proxy settings. Reachable from BOTH the login screen (via
    /// <see cref="ProxyStatusPill"/>) and Settings, because a user who cannot connect cannot reach
    /// MainForm's settings at all — which is exactly when a proxy is needed.
    ///
    /// Takes an OPTIONAL <see cref="TelegramService"/>: it must work standalone during login, before any
    /// client exists. When one IS supplied, the caller applies the change (BATCH-TA-16/P5) — this form
    /// only persists and reports what changed.
    ///
    /// ⚠ SECRETS NEVER REACH A LOG. Every diagnostic here logs <see cref="ProxyUrl.SafeForLog"/>
    /// (host:port). See the rule in ProxyUrl's remarks.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a Form + ThemedChrome.Apply with hand-coloured WinForms
    /// controls; mdfiles/PHASE2-CONVERSION-GUIDE.md). What did not change: every proxy operation, the per-row status and
    /// test-result lines, the list behaviour, the Save-on-any-close rule and what the callers read afterwards
    /// (<see cref="ConnectionSettingsChanged"/>). The page is laid out by hand (<see cref="LayoutPage"/>) inside
    /// <see cref="DialogForm.UseOwnBody"/> rather than as stacked rows, because the proxy list scrolls by itself and a
    /// scrolling list inside a scrolling body would fight over the finger.
    /// </summary>
    public sealed class ProxyForm : DialogForm
    {
        private const int RowH = 68;                // one proxy row, logical px

        private readonly TelegramService _service;      // may be null (login screen)
        private readonly AppSettings _s;

        private readonly List<string> _list;            // working copy; committed on Save
        private bool _enabled;
        private bool _tunnel;                   // step 6.5: the WebSocket tunnel is the mode (AppSettings.WsTunnelEnabled)
        private readonly bool _tunnelAtOpen;
        private int _active;
        private bool _changed;

        private Panel _page;                    // the dialog body: everything below parents here
        private SegmentedRow _mode;             // "Direct" | "Proxy" | "WebSocket tunnel" (step 6.5; was two RadioButtons)
        private NoteRow _note;
        private Panel _listHost;                // a 1 px Divider frame around the list
        private NoNativeScrollPanel _listPanel;
        private ThemedScrollBar _listBar;
        private ThemedTextBox _pasteBox;
        private ThemedLabel _hint;
        private RoundedButton _addBtn, _clipBtn, _doneBtn;
        private bool _built, _rebuilding;       // our own flags: never lay out from Control.Visible
        private int _rowsW = -1, _rowsH = -1;   // the row width / height (px) the rows were last placed at

        /// <summary>True when the user changed anything the connection depends on, so the caller knows
        /// whether a reconnect is warranted at all.</summary>
        public bool ConnectionSettingsChanged { get { return _changed; } }

        public ProxyForm(TelegramService service = null) : base("Proxy", 460, 600)
        {
            _service = service;
            _s = AppSettings.Instance;
            _list = _s.SavedProxies;                        // step 6.6: MTProto and SOCKS5 links, one list on screen
            _enabled = _s.ProxyEnabled || _s.Socks5Enabled;
            _tunnel = _tunnelAtOpen = _s.WsTunnelEnabled;
            if (_tunnel) _enabled = false;   // the tunnel wins over a hand-edited file (AppSettings.ActiveProxyUrl agrees)
            string selected = _s.SelectedProxy;
            _active = selected != null ? _list.FindIndex(u => string.Equals(u, selected, StringComparison.OrdinalIgnoreCase)) : -1;

            BuildUi();
            RebuildRows();
        }

        private void BuildUi()
        {
            // BATCH-TA-16e/E4 introduced the app's standard chrome here; since step 2.6b that chrome is DialogForm's: the
            // accent title bar with ✕, a footer of action buttons, the palette following the theme live. Nothing in this
            // method colours anything itself — every colour is a ThemeHelper.Palette token.
            // ⚠ The page is attached to the dialog BEFORE its children are created, so each of them resolves its DPI
            //   through the window it sits in (a child added to a parentless panel measures at the system DPI).
            var p = ThemeHelper.Palette;

            _page = new Panel { BackColor = p.Window };
            _page.SizeChanged += (s, e) => LayoutPage();
            UseOwnBody(_page);

            _mode = new SegmentedRow("Connection", new[] { "Direct", "Proxy", "WebSocket tunnel" }, ModeIndex());
            _mode.SelectedIndexChanged += OnModeChanged;
            _page.Controls.Add(_mode);

            _note = new NoteRow(ModeNote(), 3);
            _page.Controls.Add(_note);

            // ── the list ────────────────────────────────────────────────────────────────────────────────────
            // TA-15/X2 contract, now ScrollHost's: the target is added FIRST so it docks LAST and fills the leftover
            // space, then the themed bar docked Right; native bars are suppressed structurally by the panel type; touch
            // pan + momentum come from TouchScroller (ScrollHost enables it — before this step only RegisterControl ran
            // on the panel, so a finger could tap a row but not pan the list).
            _listHost = new Panel { BackColor = p.Divider, Padding = new Padding(1) };   // the 1 px frame is the Divider colour showing through
            _page.Controls.Add(_listHost);
            _listPanel = ScrollHost.Wrap(_listHost, p.Dark, p.Accent);
            _listPanel.BackColor = p.Card;
            _listPanel.AccessibleName = "Saved proxies";
            _listBar = _listHost.Controls.OfType<ThemedScrollBar>().FirstOrDefault();
            _listPanel.ClientSizeChanged += (s, e) => FitRows();   // first layout, and the lane changing with the DPI

            _hint = new ThemedLabel { Role = LabelRole.Dim, PointSize = TextRoles.Points(TextRole.Field), Ellipsis = true };
            _page.Controls.Add(_hint);

            _pasteBox = new ThemedTextBox(false) { Hint = "Paste a proxy link (MTProto or SOCKS5)", AccessibleName = "Proxy link" };
            _pasteBox.Inner.AccessibleName = "Proxy link";
            _pasteBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddFromBox(); } };
            _page.Controls.Add(_pasteBox);

            _addBtn = BodyButton("Add", RoundedButtonKind.Primary);
            _addBtn.Click += (s, e) => AddFromBox();
            _page.Controls.Add(_addBtn);

            _clipBtn = BodyButton("Paste", RoundedButtonKind.Secondary);
            _clipBtn.Click += (s, e) => AddFromClipboard();
            _page.Controls.Add(_clipBtn);

            // ── the footer: first added = rightmost, so this reads  Delete all | Test all | Done ──────────────────
            _doneBtn = AddFooterButton("Done", RoundedButtonKind.Primary, DialogResult.None, 96);
            _doneBtn.Click += (s, e) => { Save(); DialogResult = DialogResult.OK; };

            _testAllBtn = AddFooterButton("Test all", RoundedButtonKind.Secondary, DialogResult.None, 104);
            _testAllBtn.Click += (s, e) => { if (_testing) CancelTests(); else if (_tunnel) TestTunnel(); else TestAll(); };
            if (_tunnel) _testAllBtn.Text = "Test tunnel";

            var delAll = AddFooterButton("Delete all", RoundedButtonKind.Secondary, DialogResult.None, 108);
            delAll.Click += (s, e) => DeleteAll();

            // The footer flows right-to-left, so the default Tab order ran Done first; make it follow the screen.
            delAll.TabIndex = 0; _testAllBtn.TabIndex = 1; _doneBtn.TabIndex = 2;

            // AcceptButton stays null: Enter belongs to the paste box. Esc closes like ✕ does (DialogForm's default
            // when there is no CancelButton) — and every close saves, see FormClosing below.
            AcceptButton = null;
            FocusOnShow(_pasteBox.Inner);   // hardware keyboard only; with touch alone nothing pops the keyboard unasked
            FormClosing += (s, e) => { CancelTests(); Save(); };

            _built = true;
            LayoutPage();
        }

        /// <summary>A button that sits in the page (Add / Paste) — the footer's own are made by AddFooterButton.</summary>
        private RoundedButton BodyButton(string text, RoundedButtonKind kind)
        {
            return new RoundedButton
            {
                Text = text,
                Kind = kind,
                Modern = true,
                Font = TextRoles.Font(TextRole.Button, WindowDpiValue),
            };
        }

        /// <summary>Places the page's pieces from its current size: the mode row and the note on top, the paste row and
        /// the hint line at the bottom, and the list taking everything between. Cheap and idempotent — it runs on every
        /// size change, on the DPI change and when the dialog is shown.
        /// Not skipped while a DPI rescale runs (Dpi.Busy): the rescale's final layout happens INSIDE that window, and
        /// this is what puts the page right after it.</summary>
        private void LayoutPage()
        {
            if (!_built || _page == null) return;
            int w = _page.ClientSize.Width, h = _page.ClientSize.Height;
            if (w < Px(50) || h < Px(50)) return;

            int dpi = WindowDpiValue;
            int padX = Px(SettingRow.PadX), gap = Px(8);

            int y = Px(RowGap);
            int mh = _mode.HeightFor(w, dpi);
            _mode.SetBounds(0, y, w, mh); y += mh;
            int nh = _note.HeightFor(w, dpi);
            _note.SetBounds(0, y, w, nh); y += nh;
            int listTop = y + Px(4);

            int rowH = Math.Max(_pasteBox.PreferredFieldHeight, Px(36));
            int pasteTop = h - Px(12) - rowH;
            int hintH = Px(20);
            int hintTop = pasteTop - gap - hintH;
            int listH = Math.Max(0, hintTop - gap - listTop);
            int innerW = Math.Max(1, w - 2 * padX);

            _listHost.SetBounds(padX, listTop, innerW, listH);
            _hint.SetBounds(padX, hintTop, innerW, hintH);

            int addW = Px(66), clipW = Px(76);
            int fieldW = Math.Max(1, innerW - addW - clipW - 2 * gap);
            _pasteBox.SetBounds(padX, pasteTop, fieldW, rowH);
            _addBtn.SetBounds(padX + fieldW + gap, pasteTop, addW, rowH);
            _clipBtn.SetBounds(padX + fieldW + gap + addW + gap, pasteTop, clipW, rowH);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutPage();   // the page had no real size while the controls were being created
            FitRows();
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            LayoutPage();
            FitRows();
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (!_built) return;   // the base constructor calls this before our fields exist
            var p = ThemeHelper.Palette;
            _page.BackColor = p.Window;
            _listHost.BackColor = p.Divider;
            _listPanel.BackColor = p.Card;
            _pasteBox.Recolor();
            _listPanel.Invalidate(true);   // the rows read the palette when they paint
            if (_listBar != null) _listBar.Invalidate();
        }

        // ── mode ─────────────────────────────────────────────────────────────────────────────────────────────

        private int ModeIndex() { return _tunnel ? 2 : _enabled ? 1 : 0; }

        /// <summary>Step 6.1/6.5 — what the chosen mode means, in the note under the strip.</summary>
        private string ModeNote()
        {
            if (_tunnel) return "The WebSocket tunnel reaches Telegram through Telegram Web's gateway, the way tg-ws-proxy does, with nothing to install. Accounts on DC 2 and 4 by default; other DCs connect directly. More in Settings > Connection.";
            if (_enabled) return "A proxy can help when Telegram is blocked on your network. MTProto and SOCKS5 links work, including the one tg-ws-proxy prints when it runs on a PC on your network (start it with --host 0.0.0.0).";
            return "Connecting to Telegram directly. If it's blocked on your network, use a proxy or the WebSocket tunnel.";
        }

        /// <summary>The user picked Direct / Proxy / WebSocket tunnel.</summary>
        private void OnModeChanged(object sender, EventArgs e)
        {
            // An arrow key off the first segment would leave the strip with nothing selected: put the real state back.
            if (_mode.SelectedIndex < 0) { _mode.SetSelected(ModeIndex(), false); return; }
            _tunnel = _mode.SelectedIndex == 2;
            _enabled = _mode.SelectedIndex == 1;
            _changed = true;
            ShowMode();
            RebuildRows();
        }

        /// <summary>Shows the current mode (strip, note, footer button) WITHOUT raising <see cref="OnModeChanged"/> — the
        /// caller has already set _enabled / _tunnel and _changed and calls <see cref="RebuildRows"/> itself.</summary>
        private void ShowMode()
        {
            if (_mode != null) _mode.SetSelected(ModeIndex(), false);
            if (_note != null) { _note.Text = ModeNote(); LayoutPage(); }
            if (_testAllBtn != null && !_testing) _testAllBtn.Text = _tunnel ? "Test tunnel" : "Test all";
        }

        // ── list ─────────────────────────────────────────────────────────────────────────────────────────────

        private int RowWidth() { return Math.Max(1, _listPanel.ClientSize.Width - Px(2)); }

        private void RebuildRows()
        {
            if (_listPanel == null) return;

            // ⚠ BATCH-TA-16f/F2 — ROWS MUST BE PLACED IN DISPLAY SPACE, NOT CLIENT SPACE.
            // THE BUG THIS FIXES: a child's Top is CLIENT space, and WinForms derives the scroll extent as
            // max(child.Bottom) − DisplayRectangle.Y. So rebuilding while the panel is scrolled down by S
            // put every row S px lower in CONTENT space and inflated the content by exactly S — a permanent
            // blank band above row 0, visible the moment the user scrolled back to the top. Measured on the
            // real control stack: content 680 → 1060 at S=380, gap = 380 = the scroll offset (NOT a row
            // height, so it was never a row-layout-size problem). Worse, it COMPOUNDS: rebuild again at the
            // new bottom and the content grows again, unbounded. The shrink path was worse still — deleting
            // 10 rows down to 3 while scrolled left a 380 px band above 204 px of rows.
            //
            // THE SHARED SCROLL STACK IS NOT AT FAULT and must not be "fixed":
            // ThemedScrollBar.ScrollTo/Metrics (the sign convention is right and the clamp reaches exactly
            // 0), TouchScroller.Pan/MomentumTick, and NoNativeScroll's suppression were each measured
            // correct, and a scroll-down/scroll-up round trip with NO rebuild returns to origin exactly.
            // The chat list does not have this bug either: _chatListPanel is a FlowLayoutPanel, whose layout
            // engine re-anchors children from DisplayRectangle.Location, so nothing there places an absolute
            // Top. This panel is a plain Panel, which is precisely why it needs the offset applied by hand.
            //
            // Placing in display space (originY + top) touches the scroll origin ZERO times, so it also
            // avoids the ScrollWindowEx blit that a home-then-restore would cost on the Tegra 3 device.
            // (FitRows, which re-places the rows when the width or the DPI changes, follows the same rule.)
            TouchScroller.StopMomentum();   // a live coast must not scroll the content we are replacing
                                            // (same reason as MainForm.RenderChatList:5987)
            _rebuilding = true;
            _listPanel.SuspendLayout();
            try
            {
                foreach (Control c in _listPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
                _listPanel.Controls.Clear();

                int originY = _listPanel.DisplayRectangle.Y;   // 0 when home, negative while scrolled
                int w = RowWidth(), rh = Px(RowH);
                int top = 0;
                for (int i = 0; i < _list.Count; i++)
                {
                    var row = new ProxyRowControl(_list[i], i)
                    {
                        Left = 0, Top = originY + top, Width = w, Height = rh,
                        Selected = _enabled && i == _active,
                        Enabled = _enabled
                    };
                    row.ChooseClicked += OnChoose;
                    row.DeleteClicked += OnDelete;
                    row.MenuRequested += OnRowMenu;
                    string cached;
                    if (_testResults.TryGetValue(_list[i], out cached))
                        row.SetTestResult(cached, cached.StartsWith("Works", StringComparison.Ordinal));
                    else
                        row.SetStatus("Not tested", false);
                    _listPanel.Controls.Add(row);
                    top += row.Height;
                }
                _rowsW = w; _rowsH = rh;
            }
            finally
            {
                _rebuilding = false;
                _listPanel.ResumeLayout();
            }
            FitRows();   // a no-op unless the list's width moved while the rows were being replaced
            UpdateHint();
            if (_listBar != null) _listBar.Invalidate();
        }

        /// <summary>Re-places the existing rows when the list's width or the row height changed — the first layout
        /// (the rows are built in the constructor, before the list has its real width) and a DPI change. Nothing is
        /// rebuilt, so a "Testing…" line in flight survives. Same display-space rule as <see cref="RebuildRows"/>.</summary>
        private void FitRows()
        {
            if (_listPanel == null || _rebuilding) return;
            int w = RowWidth(), rh = Px(RowH);
            if (w == _rowsW && rh == _rowsH) return;
            _rowsW = w; _rowsH = rh;

            var rows = _listPanel.Controls.OfType<ProxyRowControl>().OrderBy(r => r.Index).ToList();
            if (rows.Count == 0) return;
            _listPanel.SuspendLayout();
            try
            {
                int originY = _listPanel.DisplayRectangle.Y;   // 0 when home, negative while scrolled
                int top = 0;
                foreach (var r in rows) { r.SetBounds(0, originY + top, w, rh); top += rh; }
            }
            finally { _listPanel.ResumeLayout(); }
        }

        private void UpdateHint()
        {
            if (_hint == null) return;
            if (_tunnel) _hint.Text = _tunnelResult ?? ("Using the WebSocket tunnel" + (_list.Count > 0 ? " · " + _list.Count + " proxies saved" : ""));
            else if (_list.Count == 0) _hint.Text = "No proxies yet. Paste a t.me/proxy or t.me/socks link below.";
            else if (!_enabled) _hint.Text = _list.Count + " saved · proxy is off";
            else if (_active < 0 || _active >= _list.Count) _hint.Text = _list.Count + " saved · none selected. Tap one to use it";
            else _hint.Text = _list.Count + " saved · using " + ProxyUrl.SafeForLog(_list[_active]);
        }

        /// <summary>BATCH-TA-18a — briefly show a confirmation on the hint line, then put it back.
        /// Restoration is a plain <see cref="UpdateHint"/> call, which recomputes from state and is
        /// idempotent — so a flash can never leave the line saying something stale.</summary>
        private void FlashHint(string text, bool ok)
        {
            if (_hint == null || _hint.IsDisposed) return;
            _hint.Text = text;
            if (_hintTimer == null)
            {
                // ⚠ System.Windows.Forms.Timer explicitly — this file has `using System.Threading`, so a
                //   bare `Timer` is ambiguous, and the threading one would fire OFF the UI thread.
                _hintTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _hintTimer.Tick += (s, e) => { _hintTimer.Stop(); if (!IsDisposed) UpdateHint(); };
            }
            _hintTimer.Stop();
            _hintTimer.Start();
        }
        private System.Windows.Forms.Timer _hintTimer;

        private void OnChoose(int index)
        {
            if (index < 0 || index >= _list.Count) return;
            _active = index;
            _enabled = true;
            _tunnel = false;   // choosing a proxy is choosing the Proxy mode
            ShowMode();
            _changed = true;
            RebuildRows();
        }

        private void OnDelete(int index)
        {
            if (index < 0 || index >= _list.Count) return;
            string gone = ProxyUrl.SafeForLog(_list[index]);
            _list.RemoveAt(index);
            // Keep _active pointing at the SAME proxy, not the same index.
            if (_active == index) _active = -1;
            else if (_active > index) _active--;
            _changed = true;
            if (Logger.Enabled) Logger.Diag("[PROXY] removed " + gone + " (" + _list.Count + " left)");
            RebuildRows();
        }

        private void DeleteAll()
        {
            if (_list.Count == 0) return;
            // ThemedDialog.Show returns the BUTTON INDEX (0 = first), not a DialogResult — same idiom as
            // MainForm's "Clear history" / delete-or-leave confirmations.
            if (ThemedDialog.Show(this, "Delete all", "Remove all " + _list.Count + " saved proxies?", "Delete", "Cancel") != 0) return;
            _list.Clear();
            _active = -1;
            _enabled = false;
            ShowMode();
            _changed = true;
            if (Logger.Enabled) Logger.Diag("[PROXY] deleted all saved proxies");
            RebuildRows();
        }

        private void AddFromBox() { AddLink(_pasteBox.Text); }

        private void AddFromClipboard()
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (string.IsNullOrWhiteSpace(t)) { ThemedDialog.Show(this, "Paste", "There's no text on the clipboard.", "OK"); return; }
            AddLink(t);
        }

        /// <summary>Validate-then-add. Rejection happens HERE, next to the box the user pasted into —
        /// never inside ConnectAsync minutes later (TA-15/X4).</summary>
        private void AddLink(string raw)
        {
            string norm, err;
            if (!ProxyUrl.TryNormalize(raw, out norm, out err))
            {
                // err never contains the secret — see ProxyUrl.
                ThemedDialog.Show(this, "Can't add that link", err, "OK");
                return;
            }
            if (_list.Any(u => string.Equals(u, norm, StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Show(this, "Already saved", "That proxy is already in your list.", "OK");
                return;
            }
            _list.Add(norm);
            // the first link becomes the active one; it switches to Proxy only from Direct, never out of the tunnel
            if (_active < 0) { _active = _list.Count - 1; if (!_tunnel) _enabled = true; ShowMode(); }
            _changed = true;
            _pasteBox.Text = "";
            if (Logger.Enabled) Logger.Diag("[PROXY] added " + ProxyUrl.SafeForLog(norm) + " (" + _list.Count + " total)");
            RebuildRows();
        }

        private void Save()
        {
            // Step 6.2/6.6: neither the tunnel nor a SOCKS5 link ever lives in ProxyList, and while either is in use
            // ProxyEnabled is false — so a 1.9.x build reading this file connects directly, never through a broken entry.
            _s.WsTunnelEnabled = _tunnel;
            _s.SetSavedProxies(_list, (_active >= 0 && _active < _list.Count) ? _list[_active] : null, _enabled && !_tunnel);
            if (_tunnel != _tunnelAtOpen) { WsRoutes.Reset(); WsTunnelStats.Reset(); }
            try { _s.Save(); } catch { }
            if (Logger.Enabled)
                Logger.Diag("[PROXY] saved enabled=" + _s.ProxyEnabled + " tunnel=" + _s.WsTunnelEnabled + " count=" + _s.ProxyList.Count
                            + " socks=" + _s.Socks5List.Count + " active=" + ProxyUrl.SafeForLog(_s.ActiveProxyUrl ?? _s.ActiveSocksUrl));
        }

        // ── BATCH-TA-17 — THE TCP "REACHABILITY" PROBE IS GONE. It was showing FALSE INFORMATION. ──
        // TA-16e/E1 measured that on this network a TCP connect completes in ~0 ms even for a host that is
        // unambiguously remote (example.com returned 0 ms, same as the proxies) — something in the path
        // answers the local half of the connect regardless of whether the peer is usable. So a dead
        // MTProxy could, and did, render as "Reachable" while the REAL test reported it broken. A green
        // line that says the opposite of the truth is worse than no line.
        // No honest cheap substitute exists either: ICMP ping proves nothing about the port (and MTProxy
        // hosts frequently drop ICMP), and a TCP connect is the thing we just showed is unreliable here.
        // The only signal entitled to speak is ProxyTester — a real MTProto handshake, which distinguishes
        // unreachable / wrong-secret / working. Rows therefore start at "Not tested" and say nothing until
        // the user runs Test (per row) or Test all.

        // ── BATCH-TA-16e/E2 — REAL proxy testing (an actual MTProto handshake) ───────────────────────
        // ⚠ STRICTLY SEQUENTIAL AND CAPPED. Every test negotiates a NEW auth key with the DC, which is
        //   real work for Telegram and for the proxy; firing N handshakes in parallel would be abusive and
        //   would also skew every measurement. ProxyTester.MaxBatch caps a runaway list.
        // Results are cached per URL for the lifetime of the form, so re-opening the menu doesn't
        // re-handshake. Cancellable, off the UI thread, and the form stays usable throughout.

        private RoundedButton _testAllBtn;
        private bool _testing;
        private bool _stopRequested;
        private CancellationTokenSource _tests;
        private readonly Dictionary<string, string> _testResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void CancelTests()
        {
            _stopRequested = true;
            try { if (_tests != null) _tests.Cancel(); } catch { }
        }

        private async void TestOne(int index)
        {
            if (_testing || index < 0 || index >= _list.Count) return;
            await RunTests(new List<string> { _list[index] });
        }

        private string _tunnelResult;   // the last "Test tunnel" outcome, shown on the hint line while in tunnel mode

        /// <summary>Step 6.5 — one strict handshake through the tunnel (ProxyTester.TestTunnelAsync, a throwaway session).</summary>
        private async void TestTunnel()
        {
            if (_testing) return;
            _testing = true;
            _stopRequested = false;
            if (_testAllBtn != null) _testAllBtn.Text = "Stop";
            try { if (_tests != null) _tests.Dispose(); } catch { }
            _tests = new CancellationTokenSource();
            _tunnelResult = "Testing the WebSocket tunnel…";
            UpdateHint();
            try
            {
                var res = await ProxyTester.TestTunnelAsync(null, _tests.Token).ConfigureAwait(true);
                if (IsDisposed) return;
                _tunnelResult = res.Ok ? "Tunnel works · " + res.Ms + " ms · " + (res.Route ?? "") : (res.Error ?? "Failed");
            }
            finally
            {
                _testing = false;
                if (!IsDisposed)
                {
                    if (_testAllBtn != null && !_testAllBtn.IsDisposed) _testAllBtn.Text = _tunnel ? "Test tunnel" : "Test all";
                    UpdateHint();
                }
            }
        }

        private async void TestAll()
        {
            if (_testing || _list.Count == 0) return;
            var batch = _list.Take(ProxyTester.MaxBatch).ToList();
            if (_list.Count > batch.Count && Logger.Enabled)
                Logger.Diag("[PROXYTEST] capped at " + ProxyTester.MaxBatch + " of " + _list.Count + " — the rest were NOT tested");
            await RunTests(batch);
        }

        private async Task RunTests(List<string> urls)
        {
            _testing = true;
            _stopRequested = false;
            if (_testAllBtn != null) _testAllBtn.Text = "Stop";
            try { if (_tests != null) _tests.Dispose(); } catch { }
            _tests = new CancellationTokenSource();
            var ct = _tests.Token;
            int done = 0;
            try
            {
                foreach (var url in urls)                                  // SEQUENTIAL — see the note above
                {
                    if (ct.IsCancellationRequested) break;
                    SetRowTest(url, "Testing…", false);
                    var res = await ProxyTester.TestAsync(url, ct).ConfigureAwait(true);
                    if (IsDisposed) return;
                    string text = res.Ok ? "Works · " + res.Ms + " ms" : (res.Error ?? "Failed");
                    _testResults[url] = text;
                    SetRowTest(url, text, res.Ok);
                    done++;
                }
            }
            finally
            {
                _testing = false;
                if (_testAllBtn != null && !_testAllBtn.IsDisposed) _testAllBtn.Text = _tunnel ? "Test tunnel" : "Test all";
                // BATCH-TA-16f/F4 — a sweep must not end in silence. Logged HERE rather than in
                // CancelTests because only this scope knows how many actually completed, and because
                // pressing Stop does not guarantee the sweep stopped where the user thinks: the
                // IN-FLIGHT test still runs to completion (TestAsync observes the token, but a handshake
                // already under way finishes or times out first). So the authoritative count is the one
                // taken after the loop exits, not at the moment Stop was pressed.
                if (Logger.Enabled)
                    Logger.Diag("[PROXYTEST] sweep " + (_stopRequested ? "STOPPED" : "finished")
                                + " at " + done + " of " + urls.Count);
            }
        }

        private void SetRowTest(string url, string text, bool ok)
        {
            if (_listPanel == null) return;
            foreach (var r in _listPanel.Controls.OfType<ProxyRowControl>())
                if (string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase)) r.SetTestResult(text, ok);
        }

        /// <summary>Row context menu: the real Test lives here, per the batch.</summary>
        private void OnRowMenu(int index, Point screenPt)
        {
            if (index < 0 || index >= _list.Count) return;
            var menu = new ThemedContextMenuStrip();
            var test = new ToolStripMenuItem(_testing ? "Testing…" : "Test this proxy") { Enabled = !_testing };
            test.Click += (s, e) => BeginInvoke((Action)(() => TestOne(index)));
            menu.Items.Add(test);
            var use = new ToolStripMenuItem("Use this proxy");
            use.Click += (s, e) => BeginInvoke((Action)(() => OnChoose(index)));
            menu.Items.Add(use);
            menu.Items.Add(new ToolStripSeparator());
            // BATCH-TA-18a — the SAME Share / Get QR Code the tapped-link sheet offers, on a saved proxy.
            // ⚠ Acts on _list[index] — the row that was right-clicked — NOT on the active proxy. Sharing
            //   the connected one instead would hand out a different credential than the row shows.
            ProxyShare.AddMenuItems(menu, this, _list[index], FlashHint);
            menu.Items.Add(new ToolStripSeparator());
            var del = new ToolStripMenuItem("Delete");
            del.Click += (s, e) => BeginInvoke((Action)(() => OnDelete(index)));
            menu.Items.Add(del);
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.ShowAt(screenPt);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_tests != null) { _tests.Cancel(); _tests.Dispose(); } } catch { }
                try { if (_hintTimer != null) { _hintTimer.Stop(); _hintTimer.Dispose(); _hintTimer = null; } } catch { }
            }
            base.Dispose(disposing);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  One proxy row — a SINGLE owner-painted control, not a panel of child widgets. That is the
        //  standing pattern in this app (CREATIVE_DECISIONS.md:79): child controls with transparent
        //  backgrounds paint black, and owner-painted children don't render inside toolbar rows.
        //  Nested here because nothing else uses it, the same way MainForm nests DrawerMenu.
        //  Step 2.6b: every colour is a ThemeHelper.Palette token read at paint time (the row no longer carries an
        //  IsDark / AccentColor of its own) and every length is logical px at the row's window DPI. The fonts are the
        //  shared cached ones (FontHelper.UiAt) and are never disposed.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class ProxyRowControl : Control
        {
            // One line of text in a rectangle: left, centred vertically, ellipsis, no & mnemonics.
            private const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                               | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            public string Url { get; private set; }
            public int Index { get; private set; }
            public bool Selected { get; set; }

            public event Action<int> ChooseClicked;
            public event Action<int> DeleteClicked;
            public event Action<int, Point> MenuRequested;

            private string _status = "";
            private bool _statusOk;
            // BATCH-TA-16e/E2 — the REAL test result, shown alongside the cheap reachability line because
            // they measure DIFFERENT LAYERS: reachability = "a socket opened"; this = "Telegram was actually
            // reached through it, so the secret is right too". Never collapse them into one label.
            private string _test = "";
            private bool _testOk;
            private Rectangle _xRect;
            private bool _hover;

            public ProxyRowControl(string url, int index)
            {
                Url = url; Index = index;
                Cursor = Cursors.Hand;
                TabStop = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public void SetStatus(string text, bool ok)
            {
                _status = text ?? ""; _statusOk = ok;
                if (!IsDisposed) Invalidate();
            }

            public void SetTestResult(string text, bool ok)
            {
                _test = text ?? ""; _testOk = ok;
                if (!IsDisposed) Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                bool h = _xRect.Contains(e.Location);
                if (h != _hover) { _hover = h; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    var hm = MenuRequested; if (hm != null) hm(Index, PointToScreen(e.Location));
                    base.OnMouseClick(e); return;
                }
                if (_xRect.Contains(e.Location)) { var h = DeleteClicked; if (h != null) h(Index); }
                else if (Enabled) { var h = ChooseClicked; if (h != null) h(Index); }
                base.OnMouseClick(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var pal = ThemeHelper.Palette;   // the cached tokens — never a registry read in a paint
                int dpi = Dpi.Of(this);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(pal.Card);

                Color fg = pal.Text;
                Color sub = pal.TextDim;
                if (!Enabled) { fg = sub; }

                // selection dot
                int dd = Dpi.Px(16, dpi);
                var dot = new Rectangle(Dpi.Px(14, dpi), Height / 2 - dd / 2, dd, dd);
                using (var p = new Pen(Selected ? pal.AccentGlyph : pal.TextFaint, Dpi.F(1.6f, dpi)))
                    g.DrawEllipse(p, dot);
                if (Selected)
                    using (var b = new SolidBrush(pal.AccentGlyph))
                        g.FillEllipse(b, Rectangle.Inflate(dot, -Dpi.Px(4, dpi), -Dpi.Px(4, dpi)));

                // the ✕ is two drawn lines (no font glyph: tofu on RT 8.1)
                int xs = Dpi.Px(28, dpi);
                _xRect = new Rectangle(Width - Dpi.Px(40, dpi), Height / 2 - xs / 2, xs, xs);
                using (var xp = new Pen(_hover ? pal.Danger : sub, Dpi.F(1.8f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = _xRect.X + _xRect.Width / 2, cy = _xRect.Y + _xRect.Height / 2, k = Dpi.Px(5, dpi);
                    g.DrawLine(xp, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(xp, cx + k, cy - k, cx - k, cy + k);
                }

                int tx = dot.Right + Dpi.Px(12, dpi), tw = Math.Max(1, _xRect.Left - dot.Right - Dpi.Px(20, dpi));
                var textRect = new Rectangle(tx, Dpi.Px(8, dpi), tw, Dpi.Px(20, dpi));
                TextRenderer.DrawText(g, ProxyUrl.DisplayLabel(Url), FontHelper.UiAt(9.5f, dpi, FontStyle.Bold), textRect, fg, Line);

                Color st = _statusOk ? pal.Success : sub;
                if (_status == "No response" || _status == "Bad link") st = pal.Danger;
                var statusRect = new Rectangle(tx, Dpi.Px(30, dpi), tw, Dpi.Px(18, dpi));
                TextRenderer.DrawText(g, Selected && Enabled ? "In use · " + _status : _status, FontHelper.UiAt(8.25f, dpi), statusRect, st, Line);

                if (_test.Length > 0)
                {
                    Color tc = _testOk ? pal.Success : pal.Danger;
                    if (_test == "Testing…") tc = sub;
                    var testRect = new Rectangle(tx, Dpi.Px(46, dpi), tw, Dpi.Px(16, dpi));
                    TextRenderer.DrawText(g, _test, FontHelper.UiAt(8.25f, dpi, FontStyle.Bold), testRect, tc, Line);
                }

                using (var p = new Pen(pal.Divider))
                    g.DrawLine(p, Dpi.Px(12, dpi), Height - 1, Width - Dpi.Px(12, dpi), Height - 1);
            }
        }
    }
}
