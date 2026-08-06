using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.Sockets;
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
    /// </summary>
    public sealed class ProxyForm : Form
    {
        private readonly TelegramService _service;      // may be null (login screen)
        private readonly AppSettings _s;

        private readonly List<string> _list;            // working copy; committed on Save
        private bool _enabled;
        private int _active;
        private bool _changed;

        private Panel _content;                 // ThemedChrome content host — EVERYTHING parents here
        private RadioButton _rbOff, _rbCustom;
        private Panel _listHost;
        private NoNativeScrollPanel _listPanel;
        private ThemedScrollBar _listBar;
        private TextBox _pasteBox;
        private Label _hint;

        private Color _bg, _card, _fg, _sub, _border, _accent;
        private bool _dark;

        /// <summary>True when the user changed anything the connection depends on, so the caller knows
        /// whether a reconnect is warranted at all.</summary>
        public bool ConnectionSettingsChanged { get { return _changed; } }

        public ProxyForm(TelegramService service = null)
        {
            _service = service;
            _s = AppSettings.Instance;
            _list = new List<string>(_s.ProxyList ?? new List<string>());
            _enabled = _s.ProxyEnabled;
            _active = _s.ProxyActive;

            ComputeTheme();
            BuildUi();
            RebuildRows();
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
            // BATCH-TA-16e/E4 — the app's STANDARD chrome, the same one SettingsForm and the admin dialogs
            // use (SettingsForm.cs:163 "no more MaterialForm caption"): an accent title bar with the app
            // icon, a drag region and one ✕. Before this, ProxyForm was a bare FixedDialog and looked like a
            // stray WinForms window next to the rest of the UI.
            // ⚠ ThemedChrome sets FormBorderStyle = None and owns the caption, so DO NOT also set
            //   FormBorderStyle / MaximizeBox here — and everything must be parented to the returned CONTENT
            //   panel, not to the form, or it will sit under the title bar.
            // ⚠ MaterialSkinManager's Accent slot is deliberately NOT written (LESSONS_LEARNED.md:163 — it is
            //   one app-wide singleton and writing it re-poisons every other form). The accent comes from
            //   ThemeHelper, exactly as SettingsForm does it.
            // ⚠ MinimumSize is deliberately left UNSET: it silently clamps resize, and this dialog is fixed
            //   size anyway — ClientSize is the single source of its geometry.
            Text = "Proxy";
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 600);
            Font = FontHelper.Ui(9.5f);
            _content = TelegArm.Helpers.ThemedChrome.Apply(this, "Proxy", _accent, _dark);
            _content.BackColor = _bg;

            int y = 14;
            _rbOff = new RadioButton
            {
                Text = "Don't use a proxy", Left = 18, Top = y, Width = 420, Height = 26,
                ForeColor = _fg, BackColor = _bg, Checked = !_enabled, Font = FontHelper.Ui(10f)
            };
            _rbOff.CheckedChanged += (s, e) => { if (_rbOff.Checked) { _enabled = false; _changed = true; RebuildRows(); } };
            _content.Controls.Add(_rbOff); y += 28;

            _rbCustom = new RadioButton
            {
                Text = "Use a proxy", Left = 18, Top = y, Width = 420, Height = 26,
                ForeColor = _fg, BackColor = _bg, Checked = _enabled, Font = FontHelper.Ui(10f)
            };
            _rbCustom.CheckedChanged += (s, e) => { if (_rbCustom.Checked) { _enabled = true; _changed = true; RebuildRows(); } };
            _content.Controls.Add(_rbCustom); y += 34;

            _content.Controls.Add(new Label
            {
                Text = "A proxy can help when Telegram is blocked on your network. Only MTProto proxies are supported.",
                Left = 18, Top = y, Width = 424, Height = 34, ForeColor = _sub, BackColor = _bg,
                Font = FontHelper.Ui(8.25f)
            });
            y += 40;

            // ── the list ────────────────────────────────────────────────────────────────────────────
            // TA-15/X2 contract: add the TARGET FIRST so it docks LAST and fills the leftover space,
            // then the themed bar docked Right. Native bars are suppressed structurally by the host type.
            _listHost = new Panel { Left = 14, Top = y, Width = 432, Height = 300, BackColor = _card, BorderStyle = BorderStyle.None };
            _listPanel = new NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = _card };
            _listHost.Controls.Add(_listPanel);                       // FIRST
            _listBar = new ThemedScrollBar(_listPanel, _dark, _accent) { Dock = DockStyle.Right };
            _listHost.Controls.Add(_listBar);                         // then the bar
            TouchScroller.RegisterControl(_listPanel);                // touch pan + momentum on RT
            _content.Controls.Add(_listHost);
            y += 308;

            _hint = new Label { Left = 18, Top = y, Width = 424, Height = 18, ForeColor = _sub, BackColor = _bg, Font = FontHelper.Ui(8.25f) };
            _content.Controls.Add(_hint); y += 22;

            _pasteBox = new TextBox
            {
                Left = 16, Top = y, Width = 300, Height = 26, BackColor = _card, ForeColor = _fg,
                BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(9f)
            };
            _pasteBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddFromBox(); } };
            _content.Controls.Add(_pasteBox);

            var addBtn = new RoundedButton { Text = "Add", Left = 322, Top = y - 2, Width = 58, Height = 30, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(9f, FontStyle.Bold) };
            addBtn.Click += (s, e) => AddFromBox();
            _content.Controls.Add(addBtn);

            var clipBtn = new RoundedButton { Text = "Paste", Left = 386, Top = y - 2, Width = 60, Height = 30, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f) };
            clipBtn.Click += (s, e) => AddFromClipboard();
            _content.Controls.Add(clipBtn);
            y += 38;

            var delAll = new RoundedButton { Text = "Delete all", Left = 16, Top = y, Width = 92, Height = 30, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f) };
            delAll.Click += (s, e) => DeleteAll();
            _content.Controls.Add(delAll);

            _testAllBtn = new RoundedButton { Text = "Test all", Left = 116, Top = y, Width = 84, Height = 30, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f) };
            _testAllBtn.Click += (s, e) => { if (_testing) CancelTests(); else TestAll(); };
            _content.Controls.Add(_testAllBtn);

            var close = new RoundedButton { Text = "Done", Left = 356, Top = y, Width = 90, Height = 30, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(9f, FontStyle.Bold) };
            close.Click += (s, e) => { Save(); DialogResult = DialogResult.OK; Close(); };
            _content.Controls.Add(close);

            AcceptButton = null;   // Enter belongs to the paste box
            FormClosing += (s, e) => { CancelTests(); Save(); };
        }

        // ── list ─────────────────────────────────────────────────────────────────────────────────────

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
            TouchScroller.StopMomentum();   // a live coast must not scroll the content we are replacing
                                            // (same reason as MainForm.RenderChatList:5987)
            _listPanel.SuspendLayout();
            foreach (Control c in _listPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
            _listPanel.Controls.Clear();

            int originY = _listPanel.DisplayRectangle.Y;   // 0 when home, negative while scrolled
            int top = 0;
            for (int i = 0; i < _list.Count; i++)
            {
                var row = new ProxyRowControl(_list[i], i)
                {
                    Left = 0, Top = originY + top, Width = _listPanel.ClientSize.Width - 2, Height = 68,
                    IsDark = _dark, AccentColor = _accent,
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
            _listPanel.ResumeLayout();
            UpdateHint();
            if (_listBar != null) _listBar.Invalidate();
        }

        private void UpdateHint()
        {
            if (_hint == null) return;
            if (_list.Count == 0) _hint.Text = "No proxies yet — paste a tg:// or t.me/proxy link below.";
            else if (!_enabled) _hint.Text = _list.Count + " saved · proxy is off";
            else if (_active < 0 || _active >= _list.Count) _hint.Text = _list.Count + " saved · none selected — tap one to use it";
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
            _rbCustom.Checked = true;
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
            _rbOff.Checked = true;
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
            if (_active < 0) { _active = _list.Count - 1; _enabled = true; _rbCustom.Checked = true; }
            _changed = true;
            _pasteBox.Text = "";
            if (Logger.Enabled) Logger.Diag("[PROXY] added " + ProxyUrl.SafeForLog(norm) + " (" + _list.Count + " total)");
            RebuildRows();
        }

        private void Save()
        {
            _s.ProxyList = new List<string>(_list);
            _s.ProxyEnabled = _enabled;
            _s.ProxyActive = (_active >= 0 && _active < _list.Count) ? _active : -1;
            try { _s.Save(); } catch { }
            if (Logger.Enabled)
                Logger.Diag("[PROXY] saved enabled=" + _s.ProxyEnabled + " count=" + _s.ProxyList.Count
                            + " active=" + ProxyUrl.SafeForLog(_s.ActiveProxyUrl));
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
                if (_testAllBtn != null && !_testAllBtn.IsDisposed) _testAllBtn.Text = "Test all";
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
            menu.Show(screenPt);
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
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class ProxyRowControl : Control
        {
            public string Url { get; private set; }
            public int Index { get; private set; }
            public bool Selected { get; set; }
            public bool IsDark { get; set; }
            public Color AccentColor { get; set; } = Color.DodgerBlue;

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
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color card = IsDark ? Color.FromArgb(50, 50, 55) : Color.White;
                g.Clear(card);

                Color fg = IsDark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
                Color sub = IsDark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
                if (!Enabled) { fg = sub; }

                // selection dot
                var dot = new Rectangle(14, Height / 2 - 8, 16, 16);
                using (var p = new Pen(Selected ? AccentColor : (IsDark ? Color.FromArgb(96, 96, 102) : Color.FromArgb(190, 190, 196)), 1.6f))
                    g.DrawEllipse(p, dot);
                if (Selected)
                    using (var b = new SolidBrush(AccentColor))
                        g.FillEllipse(b, Rectangle.Inflate(dot, -4, -4));

                _xRect = new Rectangle(Width - 40, Height / 2 - 14, 28, 28);
                using (var xp = new Pen(_hover ? Color.FromArgb(214, 78, 78) : sub, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = _xRect.X + _xRect.Width / 2, cy = _xRect.Y + _xRect.Height / 2;
                    g.DrawLine(xp, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(xp, cx + 5, cy - 5, cx - 5, cy + 5);
                }

                var textRect = new Rectangle(dot.Right + 12, 8, _xRect.Left - dot.Right - 20, 20);
                using (var f = FontHelper.Ui(9.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, ProxyUrl.DisplayLabel(Url), f, textRect, fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                Color st = _statusOk ? Color.FromArgb(74, 168, 92) : sub;
                if (_status == "No response" || _status == "Bad link") st = Color.FromArgb(214, 78, 78);
                var statusRect = new Rectangle(textRect.X, 30, textRect.Width, 18);
                using (var f2 = FontHelper.Ui(8.25f))
                    TextRenderer.DrawText(g, Selected && Enabled ? "In use · " + _status : _status, f2, statusRect, st,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (_test.Length > 0)
                {
                    Color tc = _testOk ? Color.FromArgb(74, 168, 92) : Color.FromArgb(214, 78, 78);
                    if (_test == "Testing…") tc = sub;
                    var testRect = new Rectangle(textRect.X, 46, textRect.Width, 16);
                    using (var f3 = FontHelper.Ui(8.25f, FontStyle.Bold))
                        TextRenderer.DrawText(g, _test, f3, testRect, tc,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }

                using (var p = new Pen(IsDark ? Color.FromArgb(62, 62, 68) : Color.FromArgb(232, 232, 238)))
                    g.DrawLine(p, 12, Height - 1, Width - 12, Height - 1);
            }
        }
    }
}
