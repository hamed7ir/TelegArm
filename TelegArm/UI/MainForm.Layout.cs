using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        private void BuildUi()
        {
            // BATCH-TA-0.1: BuildUi measured ~1.9 s of a ~2.1 s pre-network cold start on the x64 dev box.
            // These stamps attribute that; they add no logic and reorder nothing. BuildUi itself is only a
            // shell (SplitContainer + two builders), so the cost is inside BuildLeftPanel/BuildRightPanel.
            PerfLog.Boot("  BuildUi ENTER");
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 1
            };
            // Add first so docking gives it the form's width, THEN set min sizes and
            // splitter distance — setting SplitterDistance while the control is still
            // at its default 150px width throws (distance outside the valid range).
            Content.Controls.Add(_split);   // 2.7a: under AppForm's title bar
            _split.Panel1MinSize = 240;
            _split.Panel2MinSize = 360;
            try { _split.SplitterDistance = 300; } catch { /* width too small yet */ }
            PerfLog.Boot("  BuildUi: SplitContainer ready");

            BuildLeftPanel();
            PerfLog.Boot("  BuildUi: BuildLeftPanel done");
            BuildRightPanel();
            PerfLog.Boot("  BuildUi EXIT (BuildRightPanel done)");
        }

        private void BuildLeftPanel()
        {
            PerfLog.Boot("    BuildLeftPanel ENTER");
            // FOLDER-SIDEBAR: layout style is decided ONCE here (restart-apply). In BOTH modes the chat-list
            // host is created identically and placed in its FINAL parent before its handle exists — no live
            // reparent, so TouchScroller registration, the freeze guards, host buffering, and the
            // CompositedScroll CreateParams all apply exactly as today (Part 0.2 invariants preserved).
            bool sidebar = AppSettings.Instance.FolderSidebar;

            // The search box is always the plain native EDIT (plan E5; MaterialSkin's MaterialTextBox2 is gone).
            _searchBox = UI.Controls.InputBox.CreatePlain(new UI.Controls.ThemedTextBox(false)
            {
                Hint = "Search",
                Dock = DockStyle.Fill,
                Margin = new Padding(2, 12, 10, 6),
                FontPoints = 10.5f
            });
            _searchBox.TextChanged += (s, e) => OnSearchTextChanged();
            PerfLog.Boot("    BuildLeftPanel: search box constructed");

            _searchDebounce = new Timer { Interval = 500 };
            _searchDebounce.Tick += (s, e) => DoMessageSearch();

            _hamburger = new Button
            {
                Size = new Size(38, 38),
                Anchor = AnchorStyles.None,            // centered in its cell
                FlatStyle = FlatStyle.Flat,
                ForeColor = _accent,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            _hamburger.FlatAppearance.BorderSize = 0;
            _hamburger.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
            _hamburger.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, _accent);
            _hamburger.Click += (s, e) => ShowDrawer();
            // Draw the hamburger as a FIXED GDI+ shape (3 rounded accent lines), NOT a font glyph: the glyph
            // (☰ U+2630 / Segoe UI Symbol) rendered inconsistently across fonts/DPI/RT — 2-vs-3 lines and a
            // small font-fallback with no accent. A drawn shape is deterministic: always 3 lines, always accent.
            _hamburger.Paint += (s, e) => DrawHamburger(e.Graphics, _hamburger.ClientRectangle, _accent);

            var searchRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ColumnCount = 2,
                RowCount = 1
            };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(_hamburger, 0, 0);
            searchRow.Controls.Add(_searchBox.Control, 1, 0);

            _chatListPanel = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Margin = new Padding(0),
                Padding = new Padding(0),
                ForceComposited = true   // SCROLL-SMOOTH-T1 A/B (gated by AppSettings.CompositedScroll)
            };
            ScrollbarTheme.Apply(_chatListPanel, _dark);
            _chatListPanel.SizeChanged += (s, e) =>
            {
                int w = ContentWidth(_chatListPanel);
                foreach (Control c in _chatListPanel.Controls) c.Width = w;
            };

            // STORIES-BUILD-1: create the story-tray control once (mounted into whichever layout mode runs below).
            // Mirrors _folderBar (NoNativeScrollFlowPanel, no visible scrollbar); its row starts at height 0.
            _storyTrayBar = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                WrapContents = false,
                AutoScroll = true,
                BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White
            };

            if (sidebar)
            {
                // SIDE PANEL: a vertical folder rail on the far left (full height), the search row + chat-list
                // host stacked to its right. The horizontal _folderBar is never created (its code stays inert).
                _folderRail = new NoNativeScrollFlowPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    BackColor = _dark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(244, 244, 246)
                };
                RebuildFolderRail();

                var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
                right.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                right.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));      // STORIES: tray row 1 (hidden until stories load)
                right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                right.Controls.Add(searchRow, 0, 0);
                right.Controls.Add(_storyTrayBar, 0, 1);
                _chatListHost = WrapWithScrollbar(_chatListPanel);
                right.Controls.Add(_chatListHost, 0, 2);
                _storyTrayLayout = right;

                var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));   // rail width
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                outer.Controls.Add(_folderRail, 0, 0);
                outer.Controls.Add(right, 1, 0);
                _split.Panel1.Controls.Add(outer);

                TouchScroller.Enable(_folderRail, horizontal: false);   // rail is touch-pannable if it overflows
            }
            else
            {
                // TABBED (default): today's exact 3-row layout — search / horizontal folder bar / chat list.
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));     // STORIES: tray row 1 (hidden until stories load)
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                _folderBar = new NoNativeScrollFlowPanel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 1, 8, 1),   // flush left: "All" now aligns with the chat-row avatars below
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White
                };
                RebuildFolderBar();   // starts with just "All" until folders load

                layout.Controls.Add(searchRow, 0, 0);
                layout.Controls.Add(_storyTrayBar, 0, 1);
                layout.Controls.Add(WrapWithHScrollbar(_folderBar), 0, 2);
                _chatListHost = WrapWithScrollbar(_chatListPanel);
                layout.Controls.Add(_chatListHost, 0, 3);
                _split.Panel1.Controls.Add(layout);
                _storyTrayLayout = layout;

                TouchScroller.Enable(_folderBar, horizontal: true);
            }

            TouchScroller.Enable(_chatListPanel, horizontal: false);
            if (_storyTrayBar != null) TouchScroller.Enable(_storyTrayBar, horizontal: true);   // STORIES: horizontal drag-scroll

            // ── The proxy pill: in the title bar (redesign 2.7b) ─────────────────────────────────────────────────
            // It used to float over the chat list (BATCH-TA-16d D1/D2: parented to _chatListHost, mapped through
            // TouchScroller). Now it is the TitleBar's slot, drawn on the accent. D3 is unchanged: it hides while the
            // Connecting overlay is up (RefreshProxyPill).
            _proxyPill = new ProxyStatusPill { IsDark = _dark, AccentColor = _accent, Visible = false };
            _proxyPill.Click += (s, e) => OpenProxySettings();
            PlaceProxyPill();   // the title bar, or floating over the chat list in native chrome
            if (_chatListHost != null) _chatListHost.Resize += (s, e) => PositionProxyPill();
            // VISIBILITY (not just caption) depends on the shared state now that Connecting/Failed
            // show even without a proxy, so MainForm must re-evaluate on every transition — the pill's
            // own subscription only updates what it PAINTS, never whether it is shown.
            ProxyStatus.Changed += OnProxyStatusChangedForVisibility;
            HandleDestroyed += (s, e) => { try { ProxyStatus.Changed -= OnProxyStatusChangedForVisibility; } catch { } };
            RefreshProxyPill();

            // Chat-list paging (DPI-REVERT addendum): the initial fetch is ONE server page (limit 0 →
            // server default ~100 dialogs) — chats beyond it exist but never render. Page them in near
            // the bottom, from the same three trigger paths the message panel uses (Scroll event,
            // wheel-then-check, TouchScroller.Scrolled — wired in BuildRightPanel's touch handler).
            _chatListPanel.Scroll += (s, e) =>
            {
                if (e.ScrollOrientation == ScrollOrientation.VerticalScroll) { CheckChatListPaging(); UpdateStoryTrayVisibility(); }
            };
            _chatListPanel.MouseWheel += (s, e) =>
            {
                // Wheel doesn't reliably raise Scroll — check right after the wheel is applied.
                try { BeginInvoke((Action)(() => { CheckChatListPaging(); UpdateStoryTrayVisibility(); })); } catch { }
            };
            // STORY-TRAY-HIDE: touch pans set AutoScrollPosition WITHOUT a Scroll event → gate on TouchScroller too.
            TouchScroller.Scrolled += surface => { if (surface == _chatListPanel) UpdateStoryTrayVisibility(); };
            PerfLog.Boot("    BuildLeftPanel EXIT");
        }

        private int _bubbleLayoutWidth = -1;
        private Panel _headerBar, _headerDivider;   // 2.7b: themed from the chrome tokens in ApplyPanelColors

        private const string EmptyStateText = "Select a chat to start messaging";

        /// <summary>Redesign 2.7b: the empty chat pane. A service pill (the same tokens as the date and "pinned" lines),
        /// centred; only while no chat is open and the panel is empty.</summary>
        private void PaintEmptyState(object sender, PaintEventArgs e)
        {
            if (_selectedChat != null || _messagePanel == null || _messagePanel.Controls.Count > 0) return;
            var pal = ThemeHelper.PaletteFor(_dark);
            var font = FontHelper.UiAt(10f, WindowDpiValue);   // shared: never dispose
            var cs = _messagePanel.ClientSize;
            var t = TextRenderer.MeasureText(e.Graphics, EmptyStateText, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int padX = Px(14), padY = Px(6);
            var r = new Rectangle((cs.Width - t.Width) / 2 - padX, (cs.Height - t.Height) / 2 - padY, t.Width + 2 * padX, t.Height + 2 * padY);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = DrawHelper.RoundedRect(r, r.Height / 2))
            using (var br = new SolidBrush(pal.ServicePillBg))
                g.FillPath(br, path);
            TextRenderer.DrawText(g, EmptyStateText, font, r, pal.ServicePillText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        private int _composerRowMin = -1, _composerMargins = -1;

        /// <summary>Plan E3: the input row (row 9 of the right panel) follows the colour-emoji composer's content height, from
        /// its built height up to 13 lines and never more than 45% of the pane. A reader at the bottom of the chat stays there.</summary>
        private void GrowComposerRow()
        {
            try
            {
                var rich = _messageInput != null ? _messageInput.Rich : null;
                if (rich == null || _rightLayout == null || _rightLayout.RowStyles.Count < 10) return;
                var row = _rightLayout.RowStyles[9];
                if (_composerRowMin < 0)
                {
                    _composerRowMin = (int)row.Height;
                    _composerMargins = Math.Max(0, _composerRowMin - rich.Height);
                }
                int cap = Math.Min(rich.HeightForLines(13) + _composerMargins, (int)(_rightLayout.Height * 0.45));
                int want = Math.Max(_composerRowMin, Math.Min(Math.Max(_composerRowMin, cap), rich.ContentHeight + _composerMargins));
                if ((int)row.Height == want) return;
                bool atBottom = _messagePanel != null && AtBottom(AtBottomThreshold);
                row.Height = want;
                if (atBottom) ScrollMessagesToBottom();
            }
            catch { }
        }

        private void BuildRightPanel()
        {
            PerfLog.Boot("    BuildRightPanel ENTER");
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 10
            };
            _rightLayout = layout;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));  // 0 header
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 1 mini player (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 2 pinned bar (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 3 selection bar (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 4 FORUM topic bar (toggled) — FORUM-TOPICS
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 5 messages
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 6 reply strip (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 7 reply keyboard (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 8 thread join bar (toggled) — COMMENTS-JOIN-FLYOUT
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));  // 9 input

            var topBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Cursor = Cursors.Hand };
            _headerBar = topBar;   // 2.7b: its surface comes from the Header token (ApplyPanelColors)
            var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            // Redesign 2.7a: ThemedLabels (were MaterialLabel H6 / Caption). HEADER-FIT: one line, ellipsis — never wrap or
            // clip into the pinned bar below. Colour by Role (the status line flips Dim/Accent for online and typing).
            _chatTitle = new ThemedLabel
            {
                Text = "Select a chat",
                Dock = DockStyle.Fill,
                PointSize = 12.5f,
                Style = FontStyle.Bold,
                Role = LabelRole.Body,
                Ellipsis = true,
                Padding = new Padding(10, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            _chatStatus = new ThemedLabel
            {
                Text = "",
                Dock = DockStyle.Fill,
                PointSize = 9f,
                Role = LabelRole.Dim,
                Ellipsis = true,
                Padding = new Padding(10, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            titleStack.Controls.Add(_chatTitle, 0, 0);
            titleStack.Controls.Add(_chatStatus, 0, 1);
            topBar.Controls.Add(titleStack);
            // DOWNLOAD-UX Part 4: transfers indicator (aggregate ring + badge; hidden when the roster is
            // empty). Fill sibling added first so the Right dock takes the edge (codebase dock convention).
            _dlIndicator = new DownloadIndicator(_service) { Dock = DockStyle.Right };
            _dlIndicator.Click += (s, e) => OpenDownloadsPanel();
            topBar.Controls.Add(_dlIndicator);
            // BATCH-TA-21/S1a — the header ⋮, beside the magnifier. Added BETWEEN the transfers indicator and
            // the magnifier, so no existing line has to move.
            // ⚠ Dock.Right RESOLVES FIRST-ADDED = LEFTMOST (measured: three Dock.Right buttons in a 400 px
            //   panel land at Left 268 / 312 / 356 in ADD order). So the resolved left-to-right order here is
            //   [transfers] [⋮] [🔍] — the magnifier stays outermost right, and the ⋮ sits immediately to its
            //   LEFT. If the ⋮ should ever be outermost right instead, move this Controls.Add to AFTER
            //   _chatSearchBtn's; nothing else changes.
            // ⚠ EVERY METRIC AND STYLE IS COPIED FROM _chatSearchBtn BELOW rather than invented — same 44 px
            //   width, same Flat/transparent/borderless treatment, same 40-alpha accent hover, same
            //   drawn-not-font glyph (":799 — for crisp/consistent rendering on RT"), same "hidden until a
            //   chat is open". Two adjacent header buttons that disagree by two pixels look like a bug.
            // ⚠ ACCENT COMES FROM ThemeHelper VIA _accent (the palette) — never a hard-coded colour.
            _chatMenuBtn = new Button
            {
                Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false   // shown only while a chat is open — toggled beside _chatSearchBtn
            };
            _chatMenuBtn.FlatAppearance.BorderSize = 0;
            _chatMenuBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
            _chatMenuBtn.Paint += (s, e) => DrawKebab(e.Graphics, _chatMenuBtn.ClientRectangle, _accent);
            _chatMenuBtn.Click += (s, e) => ShowChatHeaderMenu();
            topBar.Controls.Add(_chatMenuBtn);
            // INCHAT-SEARCH: a magnifier in the chat header → enter in-chat search (the left panel becomes scoped
            // results for the open chat). Drawn (GDI), not a font glyph, for crisp/consistent rendering on RT.
            _chatSearchBtn = new Button
            {
                Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false   // shown only while a chat is open (toggled in OpenChat / on leaving a chat)
            };
            _chatSearchBtn.FlatAppearance.BorderSize = 0;
            _chatSearchBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
            _chatSearchBtn.Paint += (s, e) => DrawMagnifier(e.Graphics, _chatSearchBtn.ClientRectangle, _accent);
            _chatSearchBtn.Click += (s, e) => EnterInChatSearch();
            topBar.Controls.Add(_chatSearchBtn);
            // BATCH-TA-23/D1c — the dock toggle. Added LAST of the right-docked group, so per the measured
            // rule (first-added = leftmost) it lands OUTERMOST RIGHT — at the very edge the dock itself
            // slides in from, which is the only placement that reads as "open the thing next to me".
            // No existing Controls.Add moves.
            // ⚠ Metrics copied from _chatMenuBtn exactly as that copied _chatSearchBtn: 44 px, Flat,
            //   transparent, borderless, 40-alpha accent hover, drawn glyph, accent from ThemeHelper,
            //   hidden until a chat is open.
            _dockBtn = new Button
            {
                Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false
            };
            _dockBtn.FlatAppearance.BorderSize = 0;
            _dockBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
            _dockBtn.Paint += (s, e) => DrawDockGlyph(e.Graphics, _dockBtn.ClientRectangle, _accent,
                                                      _dock != null && _dock.Visible);
            _dockBtn.Click += (s, e) => ToggleDock();
            topBar.Controls.Add(_dockBtn);
            // Peer avatar before the name. Added LAST so its Dock.Left resolves outermost-left; titleStack (Fill,
            // added first) then occupies the space between the avatar and the right-docked transfers indicator.
            _headerAvatar = new Panel { Dock = DockStyle.Left, Width = 54, Margin = new Padding(0), Cursor = Cursors.Hand };
            _headerAvatar.Paint += HeaderAvatar_Paint;
            _headerAvatar.Click += (s, e) => OnHeaderClick();
            topBar.Controls.Add(_headerAvatar);
            // 2.7b: a 1 px divider under the header. Added last, so it docks first: the full width, at the bottom.
            _headerDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, Margin = new Padding(0) };
            topBar.Controls.Add(_headerDivider);
            topBar.Click += (s, e) => OnHeaderClick();      // header → profile, or ‹ back when in a comment thread
            _chatTitle.Click += (s, e) => OnHeaderClick();
            _chatStatus.Click += (s, e) => OnHeaderClick();

            _typingTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _typingTimer.Tick += (s, e) => { _typingTimer.Stop(); _typing = false; UpdateHeaderStatus(); };

            _messagePanel = new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Margin = new Padding(0),
                ForceComposited = true,   // SCROLL-SMOOTH-T1 A/B (gated by AppSettings.CompositedScroll)
                // Bottom padding so the last bubble clears the input bar when scrolled.
                Padding = new Padding(0, 0, 0, 8)
            };
            ScrollbarTheme.Apply(_messagePanel, _dark);
            _messagePanel.SizeChanged += (s, e) =>
            {
                int w = ContentWidth(_messagePanel);
                // Plan E3: bubbles depend on the WIDTH only. The growing composer changes the panel's HEIGHT on every
                // wrapped line; re-measuring every bubble for that was the cost the plan flagged (Tegra 3).
                if (w == _bubbleLayoutWidth) return;
                _bubbleLayoutWidth = w;
                foreach (Control c in _messagePanel.Controls)
                {
                    c.Width = w;
                    (c as MessageBubbleControl)?.Measure();
                }
            };
            // Redesign 2.7b: the empty state. With no chat open, a centred service pill says what to do, as in Telegram.
            _messagePanel.Paint += PaintEmptyState;
            _messagePanel.Resize += (s, e) => { if (_selectedChat == null) _messagePanel.Invalidate(); };
            _messagePanel.Scroll += MessagePanel_Scroll;
            // Mouse-wheel scrolling doesn't reliably raise Scroll with NewValue==0, so
            // check the position right after the wheel is applied (hence BeginInvoke)
            // and page in older messages when we land near the top.
            _messagePanel.MouseWheel += (s, e) =>
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] wheel-handled (delta=" + e.Delta + ")");
                BeginInvoke((Action)(() =>
                {
                    if (!_loadingOlder && _hasMoreHistory &&
                        _messagePanel.VerticalScroll.Value <= _messagePanel.VerticalScroll.SmallChange * 3)
                        _ = LoadOlderMessages();
                }));
            };

            var bottomBar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                RowCount = 1,
                Margin = new Padding(0)
            };
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));    // 0 bot Menu (toggled: bot chats only)
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));   // 1 attach
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 2 input / recording
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));   // 3 emoji
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));   // 4 mic
            // ⚠ 96 -> 40, MATCHING THE EMOJI COLUMN. 96 was historical — sized for the old text button that
            //   had the word "Send" in it. The round send disc replaced that button but the COLUMN kept its width,
            //   so a 40px control sat in a 96px cell with ~28px of dead space either side — which is why
            //   the button read as oversized and unbalanced even after the disc itself was shrunk. The
            //   control was never the problem; its cell was.
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));   // 5 send (== emoji)

            // FIRST FontHelper call in BuildUi. FontHelper's static ctor is LAZY and registers five embedded
            // TTFs with BOTH GDI+ (PrivateFontCollection) and gdi32 (AddFontMemResourceEx) — if that cost is
            // in the ~1.9 s, it lands between these two rungs.
            PerfLog.Boot("    BuildRightPanel: before first FontHelper.Ui call");
            _botMenuButton = new Button
            {
                Text = "", Anchor = AnchorStyles.None, Width = 44, Height = 38, Visible = false,
                FlatStyle = FlatStyle.Flat, Font = FontHelper.Ui(13f), Cursor = Cursors.Hand,
                BackColor = _dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(225, 225, 228),
                ForeColor = _dark ? Color.FromArgb(225, 225, 228) : Color.FromArgb(40, 40, 44)
            };
            PerfLog.Boot("    BuildRightPanel: after first FontHelper.Ui call (fonts registered)");
            _botMenuButton.FlatAppearance.BorderSize = 0;
            _botMenuButton.Click += (s, e) => ShowBotMenu();
            // The "≡" menu icon is DRAWN (3 accent lines, like the hamburger) — a font "≡" substitutes an ugly
            // glyph on RT. In web-app mode the button shows the "Menu" text instead (_botMenuIcon = false).
            _botMenuButton.Paint += (s, e) => { if (_botMenuIcon) DrawHamburger(e.Graphics, _botMenuButton.ClientRectangle, _accent); };

            _attachButton = new AttachButton
            {
                Anchor = AnchorStyles.None,   // centered in its cell
                Enabled = false
            };
            _attachButton.Click += (s, e) => ShowAttachMenu();

            if (UI.Controls.InputBox.UseEmojiComposer)
            {
                // Plan E2 — the colour-emoji composer: takes WM_CHAR itself (immune to RT's dead EDIT), opens TabTip itself.
                _messageInput = UI.Controls.InputBox.CreateRich(new UI.Controls.EmojiComposer
                {
                    Hint = "Write a message…",
                    Dock = DockStyle.Fill,
                    Margin = new Padding(12, 10, 6, 10),
                    FontPoints = 11f,
                    ShowExpandGlyph = true   // step 2.7d: top-right, opens the editor window
                });
                _messageInput.Enabled = false;
                _messageInput.Rich.ContentHeightChanged += (s, e) => GrowComposerRow();   // plan E3: up to 13 lines
                _messageInput.Rich.ExpandClick += (s, e) => OpenComposerEditor();
            }
            else
            {
                // Redesign 1.5 — the plain composer: multiline from birth (Shift+Enter needs no reflection), fixed 64 px
                // row like Legacy (no auto-grow), "disabled" = read-only, never Enabled=false (ThemedTextBox notes).
                _messageInput = UI.Controls.InputBox.CreatePlain(new UI.Controls.ThemedTextBox(true)
                {
                    Hint = "Write a message…",
                    Dock = DockStyle.Fill,
                    Margin = new Padding(12, 10, 6, 10),
                    FontPoints = 11f
                });
                _messageInput.Enabled = false;
            }
            _messageInput.KeyDown += (s, e) =>
            {
                if (!_messageInput.Enabled) return;   // review 1.5: the Plain "disabled" composer is read-only, not Enabled=false
                // ── COMPOSER SEND KEY (TA-36) ────────────────────────────────────────────────────
                // ⚠ SHIFT+ENTER IS ALWAYS A NEWLINE, in every mode. It is the one convention every chat
                //   client shares; a setting that changed it would be a trap, not a preference. Falling
                //   through without SuppressKeyPress lets the multiline TextBox insert the break itself,
                //   which is what makes the caret and undo behave normally.
                if (e.KeyCode == Keys.Enter && e.Shift) { /* newline — let the control handle it */ }
                else if (e.KeyCode == Keys.Enter && SendKeyPressed(e))
                {
                    SendPathLog("enter");
                    e.SuppressKeyPress = true;   // eat it, or the TextBox also inserts a newline
                    SendCurrentMessage();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    // A non-sending Enter in Ctrl/Alt mode: an ordinary newline.
                }
                // ── PASTE A FILE OR IMAGE INTO THE COMPOSER (TA-36) ──────────────────────────────
                // ⚠ ONLY swallowed when the clipboard actually holds a file or a bitmap. Ctrl+V with TEXT
                //   must keep pasting text — checking the clipboard FIRST and falling through otherwise is
                //   what stops this hijacking ordinary paste.
                else if (e.Control && e.KeyCode == Keys.V && TryPasteAttachment())
                {
                    e.SuppressKeyPress = true;
                }
                // SEND-ENTITIES: desktop formatting shortcuts — wrap the selection in the markdown marker that
                // becomes a MessageEntity on send (Ctrl+B bold, Ctrl+I italic, Ctrl+K link).
                else if (e.Control && e.KeyCode == Keys.B) { e.SuppressKeyPress = true; WrapComposerSelection("**", "**"); }
                else if (e.Control && e.KeyCode == Keys.I) { e.SuppressKeyPress = true; WrapComposerSelection("__", "__"); }
                else if (e.Control && e.KeyCode == Keys.K) { e.SuppressKeyPress = true; InsertComposerLink(); }
            };
            _messageInput.TextChanged += OnComposerTextChanged;   // QUICKWINS-1 PART 1: send our typing (throttled; inert when not typing)
            // The composer is the colour-emoji EmojiComposer (plan E2-E5) or, by setting, the plain ThemedTextBox; the
            // Enter-to-send KeyDown (above) and the [KBD] diagnostics (below) are wired through the InputBox adapter.
            HookKbdDiag(_messageInput, "composer");   // [KBD] focus/keystroke diagnostics (KEEP)
            HookKbdDiag(_searchBox, "search");

            // SEND-ENTITIES (formatting toolbar): a themed context menu on the composer — select text → tap a format
            // to wrap it in the markdown marker that's parsed to a MessageEntity on send. Setting ContextMenuStrip is a
            // safe property assignment (no inner-TextBox reflection or overpaint).
            var fmtMenu = new ThemedContextMenuStrip();
            fmtMenu.Items.Add("Bold").Click += (s, e) => WrapComposerSelection("**", "**");
            fmtMenu.Items.Add("Italic").Click += (s, e) => WrapComposerSelection("__", "__");
            fmtMenu.Items.Add("Strikethrough").Click += (s, e) => WrapComposerSelection("~~", "~~");
            fmtMenu.Items.Add("Monospace").Click += (s, e) => WrapComposerSelection("`", "`");
            fmtMenu.Items.Add("Link…").Click += (s, e) => InsertComposerLink();
            fmtMenu.Items.Add(new ToolStripSeparator());
            fmtMenu.Items.Add("Paste").Click += (s, e) => PasteIntoComposer();
            fmtMenu.Items.Add(new ToolStripSeparator());
            fmtMenu.Items.Add("Edit in a window…").Click += (s, e) => OpenComposerEditor();   // step 2.7d (both engines)
            _messageInput.ContextMenuStrip = fmtMenu;

            // TA-34/A — a round accent disc with a drawn paper-plane, sized and centred exactly like the
            // mic/emoji/attach buttons beside it (Anchor.None, 40x40 from the control itself).
            _sendButton = new UI.Controls.SendButton
            {
                Anchor = AnchorStyles.None,
                Enabled = false
            };
            _sendButton.Click += (s, e) => { SendPathLog("button"); if (_voiceState == VoiceState.Ready) SendPendingVoice(); else SendCurrentMessage(); };

            _emojiButton = new EmojiGlyphButton { Anchor = AnchorStyles.None, Enabled = false };
            _emojiButton.Click += (s, e) => OpenEmojiPicker();

            _micButton = new MicButton { Anchor = AnchorStyles.None, Enabled = false };
            _micButton.Click += (s, e) => OnMicClick();

            // Recording strip: owner-painted, overlays the input cell while recording/ready.
            _recordingBar = new RecordingBar { Dock = DockStyle.Fill, Visible = false, IsDark = _dark };

            _recordTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _recordTimer.Tick += (s, e) =>
            {
                if (_recorder != null && _recorder.IsRecording)
                    _recordingBar.Caption = "Recording…   " + FormatDuration(_recorder.ElapsedSeconds);
            };

            bottomBar.Controls.Add(_botMenuButton, 0, 0);
            bottomBar.Controls.Add(_attachButton, 1, 0);
            bottomBar.Controls.Add(_messageInput.Control, 2, 0);
            bottomBar.Controls.Add(_recordingBar, 2, 0);   // same cell as the input (toggled)
            bottomBar.Controls.Add(_emojiButton, 3, 0);
            bottomBar.Controls.Add(_micButton, 4, 0);
            bottomBar.Controls.Add(_sendButton, 5, 0);
            _composerColumns = bottomBar;

            _miniBar = new MiniPlayerBar { Dock = DockStyle.Fill, AccentColor = _accent, IsDark = _dark };

            // Selection toolbar (below the mini player, above the message list) — owner-painted.
            _selectionBar = new SelectionBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _selectionBar.ForwardRequested += () => ForwardSelected();
            _selectionBar.CloseRequested += () => ExitSelectionMode();

            _replyStrip = new Panel { Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0) };
            _replyCancelBtn = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 44,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                Font = new Font("Segoe UI", 11f)
            };
            _replyCancelBtn.FlatAppearance.BorderSize = 0;
            _replyCancelBtn.Click += (s, e) => CancelReply();
            // OWNER-DRAW the strip: we draw the ↩/✎ Noto glyph + the preview via EmojiRenderer.DrawLine
            // (Vazirmatn Persian + inline Noto emoji) ourselves. (History: it was a MaterialLabel, which
            // ignored .Image and forced its own font — the glyph vanished and Persian fell back.)
            _replyStrip.Paint += (s, e) => DrawReplyStrip(e.Graphics);
            _replyStrip.Controls.Add(_replyCancelBtn);

            _pinnedBar = new PinnedBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _pinnedBar.BarClicked += (s, e) => OnPinnedBarClicked();
            _pinnedBar.ShowAllClicked += (s, e) => ShowPinnedList();

            layout.Controls.Add(topBar, 0, 0);
            layout.Controls.Add(_miniBar, 0, 1);
            layout.Controls.Add(_pinnedBar, 0, 2);
            layout.Controls.Add(_selectionBar, 0, 3);
            // FORUM-TOPICS: topic chip bar at row 4 (above the messages) — horizontal scrollable, mirrors _folderBar.
            // Hidden (row height 0) unless a forum group is open. Because it's a sibling row ABOVE _msgHost, the date pill
            // (a child of _msgHost) sits below it naturally → DateFlyoutTopOffset stays 0.
            _forumTopicBar = new NoNativeScrollFlowPanel { Dock = DockStyle.Fill, Margin = new Padding(2, 1, 2, 1), WrapContents = false, AutoScroll = true, BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White };   // FORUM-TOPICS: symmetric L/R (was 0/8); "All" lands ~10px, aligning with the mini/pinned bars
            layout.Controls.Add(_forumTopicBar, 0, 4);   // FORUM-TOPICS: added DIRECTLY (no WrapWithHScrollbar) → NO visible scrollbar; NoNativeScroll suppresses the native bar + TouchScroller gives drag-scroll
            TouchScroller.Enable(_forumTopicBar, horizontal: true);
            _msgHost = WrapWithScrollbar(_messagePanel);
            layout.Controls.Add(_msgHost, 0, 5);

            // Floating "scroll to bottom" button over the message panel (jitter-free: a child of the host,
            // not the scrolling panel). Visible only when scrolled up; badge shows messages missed meanwhile.
            _jumpBtn = new JumpToBottomButton
            {
                IsDark = _dark, AccentColor = _accent, Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _jumpBtn.Click += (s, e) => JumpToBottomClicked();
            _msgHost.Controls.Add(_jumpBtn);
            _jumpBtn.BringToFront();
            _msgHost.Resize += (s, e) => PositionJumpButton();

            // BUBBLE-DATETIME (C): floating date pill — same float-parent trick as the jump button (a child of the
            // host, NOT the scrolling panel → never participates in paging/reconcile). Shown while scrolling; fades
            // ~1s after scrolling stops. The single fade timer runs only while it's shown/fading, then stops.
            _dateFlyout = new DateFlyoutPill { IsDark = _dark, Accent = _accent, Visible = false, Font = FontHelper.Ui(8.5f, FontStyle.Bold) };
            _msgHost.Controls.Add(_dateFlyout);
            _dateFlyout.BringToFront();
            _dateFlyoutTimer = new System.Windows.Forms.Timer { Interval = 90 };
            _dateFlyoutTimer.Tick += DateFlyoutTick;

            _scrollWatch = new System.Windows.Forms.Timer { Interval = 200 };
            _scrollWatch.Tick += (s, e) => OnScrollPositionChanged();   // robust across wheel/scrollbar/touch
            _scrollWatch.Start();

            // [PERF] diagnosis: gauge the collection sizes (growth shape) + flush the per-interval summary line.
            // PerfLog.Enabled tracks the diagnostic-logging toggle — when off the whole tick body is skipped.
            _perfTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _perfTimer.Tick += (s, e) =>
            {
                if (!PerfLog.Enabled) return;
                PerfLog.SetGauge("allChats", _allChats.Count);
                PerfLog.SetGauge("curMsgs", _currentChatMessages.Count);
                PerfLog.SetGauge("panel", _messagePanel != null ? _messagePanel.Controls.Count : 0);
                PerfLog.SetGauge("chatRows", _chatListPanel != null ? _chatListPanel.Controls.Count : 0);
                PerfLog.SetGauge("shownIds", _shownMessageIds.Count);
                PerfLog.SetGauge("peerNames", _peerNames.Count);
                PerfLog.SetGauge("avatars", _avatars.MemCount);
                PerfLog.SetGauge("pending", _pendingBubbles.Count);
                PerfLog.Flush();
            };
            _perfTimer.Start();

            layout.Controls.Add(_replyStrip, 0, 6);   // FORUM-TOPICS: +1 (topic bar inserted at row 4)

            // Reply keyboard (bot ReplyKeyboardMarkup) — its own toggled row just above the input.
            _replyKbHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
            _replyKb = new ReplyKeyboardControl { Dock = DockStyle.Fill, IsDark = _dark, AccentColor = _accent };
            _replyKb.ButtonActivated += OnReplyKeyboardButton;
            _replyKb.ToggleChanged += (s, e) => SyncReplyKeyboardHeight();
            _replyKbHost.Controls.Add(_replyKb);
            layout.Controls.Add(_replyKbHost, 0, 7);   // FORUM-TOPICS: +1

            // COMMENTS-JOIN-FLYOUT: the "Join the group" bar — docked ABOVE the composer (row 7, toggled to zero
            // height when hidden so it never affects the list/composer layout). Shown only in a comment thread when
            // the user is NOT a member of the linked discussion group; joining is optional (posting works unjoined).
            _threadJoinBar = new UI.Controls.ThreadJoinBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _threadJoinBar.JoinClicked += (s, e) => DoThreadJoin();
            _threadJoinBar.DismissClicked += (s, e) => DismissThreadJoinBar();
            layout.Controls.Add(_threadJoinBar, 0, 8);   // FORUM-TOPICS: +1

            layout.Controls.Add(bottomBar, 0, 9);   // FORUM-TOPICS: +1
            _composerBar = bottomBar;
            // The state-machine footer shares the input row (toggled with the composer, like input↔recording).
            _footerBar = new ComposerFooterBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _footerBar.ActionClicked += (s, e) => OnFooterAction();
            layout.Controls.Add(_footerBar, 0, 9);   // FORUM-TOPICS: +1
            _split.Panel2.Controls.Add(layout);
            BuildDock();   // BATCH-TA-23/D1b — added AFTER the Fill layout; see BuildDock's remarks

            TouchScroller.Enable(_messagePanel, horizontal: false);
            // Touch pans set AutoScrollPosition directly (no Scroll event), so drive the SAME paging triggers
            // as wheel/scrollbar from the touch callback: page older near the top, and run the bottom checks.
            TouchScroller.Scrolled += surface =>
            {
                if (IsDisposed) return;
                NoteActivity();   // PRESENCE 1.1: touch pans are real user input (taps arrive via the posted click)
                if (surface == _chatListPanel) { CheckChatListPaging(); return; }   // list paging on touch pans
                if (surface != _messagePanel) return;
                // TOUCH-FREEZE: this runs per coalesced pan tick (~100Hz) — a pan parked near the top
                // fired the line + call 85×/s. Attempts are tick-limited; the head limiter in
                // LoadOlderMessages spaces the actual work for every other trigger path too.
                if (!_loadingOlder && _hasMoreHistory && -_messagePanel.AutoScrollPosition.Y <= 40
                    && Environment.TickCount - _lastNearTopAttemptTick >= 250)
                {
                    _lastNearTopAttemptTick = Environment.TickCount;
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] touch-handled near-top → load older");
                    _ = LoadOlderMessages();
                }
                OnScrollPositionChanged();   // jump button + near-bottom LoadNewerMessages (island → tail)
            };

            AudioPlayer.StateChanged += OnAudioStateChanged;
            PerfLog.Boot("    BuildRightPanel EXIT");
        }

        private void OnAudioStateChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)UpdateMiniBar); } catch { }
        }

        private void UpdateMiniBar()
        {
            bool active = AudioPlayer.IsActive;
            // Collapsing/expanding the mini-bar row resizes the message panel, which
            // makes its AutoScroll reset to the top. Preserve the scroll offset.
            int scrollY = _messagePanel != null ? -_messagePanel.AutoScrollPosition.Y : 0;
            _miniBar.Visible = active;
            _rightLayout.RowStyles[1].Height = active ? 48 : 0;
            if (_messagePanel != null)
                _messagePanel.AutoScrollPosition = new Point(0, scrollY);
        }

        private static int ContentWidth(FlowLayoutPanel p)
        {
            // The native bar is suppressed and the themed scrollbar lives OUTSIDE the panel (in the
            // wrapping host), so the panel's client width already excludes it — don't reserve a
            // second bar's width (that was the right-edge gap). Just a small breathing margin.
            return Math.Max(60, p.ClientSize.Width - 2);
        }

        // Custom themed scrollbars (consistent on every Windows, incl. RT 8.1 where the OS has none).
        private readonly List<ThemedScrollBar> _scrollBars = new List<ThemedScrollBar>();

        /// <summary>Wraps an AutoScroll panel in a host with a themed scrollbar docked on the right.</summary>
        private Panel WrapWithScrollbar(ScrollableControl panel)
        {
            panel.Dock = DockStyle.Fill;
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = panel.BackColor };
            var bar = new ThemedScrollBar(panel, _dark, _accent) { Dock = DockStyle.Right };
            _scrollBars.Add(bar);
            host.Controls.Add(panel);   // Fill — add first so it docks last and takes the leftover width
            host.Controls.Add(bar);     // Right strip
            return host;
        }

        /// <summary>Wraps a horizontal AutoScroll strip with a themed scrollbar docked along the bottom.</summary>
        private Panel WrapWithHScrollbar(ScrollableControl strip)
        {
            var margin = strip.Margin;
            strip.Dock = DockStyle.Fill;
            strip.Margin = new Padding(0);
            var host = new Panel { Dock = DockStyle.Fill, Margin = margin, Padding = new Padding(0), BackColor = strip.BackColor };
            var bar = new ThemedScrollBar(strip, _dark, _accent, horizontal: true) { Dock = DockStyle.Bottom };
            _scrollBars.Add(bar);
            host.Controls.Add(strip);   // Fill — add first so it docks last
            host.Controls.Add(bar);     // Bottom strip
            return host;
        }

        /// <summary>Draws the hamburger toggle as three evenly-spaced rounded accent lines — deterministic
        /// (no font glyph → no fallback), so it renders IDENTICALLY on every paint/state/device.</summary>
        private static void DrawHamburger(Graphics g, Rectangle area, Color accent)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int lineW = 20, lineH = 3, gap = 4;   // 3 lines, evenly spaced
            int totalH = lineH * 3 + gap * 2;
            int x = area.X + (area.Width - lineW) / 2;
            int y = area.Y + (area.Height - totalH) / 2;
            using (var brush = new SolidBrush(accent))
                for (int i = 0; i < 3; i++)
                {
                    var lr = new Rectangle(x, y + i * (lineH + gap), lineW, lineH);
                    using (var path = DrawHelper.RoundedRect(lr, 1)) g.FillPath(brush, path);
                }
        }

        /// <summary>INCHAT-SEARCH: draws a magnifier (lens circle + diagonal handle) as a FIXED GDI shape — same
        /// rationale as DrawHamburger (a font/emoji glyph renders inconsistently on RT).</summary>
        /// <summary>BATCH-TA-24 — THE HEADER'S SHARED OPTICAL BOX.
        /// The three header glyphs are drawn, not font characters, so nothing enforces a common size — and
        /// nothing did: the magnifier's lens was 0.42·s PLUS a handle projecting a further d/3, giving it a
        /// ~24 px optical extent at a 44 px button while the ⋮ was ~21 px and the panel icon ~18×14. It read
        /// as the odd one out. Every glyph below now sizes itself against THIS box, so changing the header's
        /// icon weight is one number rather than three guesses.</summary>
        private const float HeaderGlyphScale = 0.45f;
        private static int HeaderGlyphBox(Rectangle area)
        {
            return Math.Max(12, (int)(Math.Min(area.Width, area.Height) * HeaderGlyphScale));
        }

        private static void DrawMagnifier(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int s = Math.Min(area.Width, area.Height);
            int box = HeaderGlyphBox(area);
            // The handle projects d/3 past the lens AND the stroke adds its own width on both sides, so the
            // LENS has to be well under the box for the finished glyph to match the others. Measured at a
            // 44 px button: 2/3 lands the ink at 19 px tall, against 19 for the dots and 18 for the panel.
            int d = Math.Max(9, box * 2 / 3);
            int cx = area.X + area.Width / 2 - d / 6, cy = area.Y + area.Height / 2 - d / 6;
            var lens = new Rectangle(cx - d / 2, cy - d / 2, d, d);
            using (var pen = new Pen(color, Math.Max(2f, s * 0.075f)))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawEllipse(pen, lens);
                g.DrawLine(pen, lens.Right - d / 8, lens.Bottom - d / 8, lens.Right + d / 3, lens.Bottom + d / 3);
            }
        }

        /// <summary>BATCH-TA-21/S1a — the ⋮ glyph, DRAWN rather than a font character, for the same reason
        /// the magnifier is (:799): a font glyph renders inconsistently on RT. Three dots on the vertical
        /// centre line, sized off the button so it matches the magnifier's visual weight.</summary>
        private static void DrawKebab(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int box = HeaderGlyphBox(area);
            // Three dots spanning the shared box: 2·gap + d = box.
            int d = Math.Max(3, box / 4);                         // dot diameter
            int gap = Math.Max(d + 2, (box - d) / 2);             // centre-to-centre spacing
            int cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            using (var b = new SolidBrush(color))
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(b, cx - d / 2f, cy + i * gap - d / 2f, d, d);
        }

        /// <summary>BATCH-TA-23/D1c — a panel glyph: an outlined box whose right column FILLS when the dock
        /// is open. Drawn, not a font character, for the same reason the magnifier is. The fill is the
        /// open/closed signal — a glyph that looks identical in both states is not a toggle.</summary>
        private static void DrawDockGlyph(Graphics g, Rectangle area, Color color, bool open)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int s = Math.Min(area.Width, area.Height);
            int side = HeaderGlyphBox(area);
            // A panel reads as a landscape rectangle, so it fills the shared box horizontally and is ~3/4 of
            // it vertically — same optical weight as the lens and the dots.
            int w = side, h = Math.Max(11, side * 4 / 5);
            var box = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            int split = box.Right - Math.Max(5, w / 3);
            if (open)
                using (var b = new SolidBrush(color))
                    g.FillRectangle(b, split, box.Top, box.Right - split, box.Height);
            using (var pen = new Pen(color, Math.Max(1.5f, s * 0.045f)))
            {
                g.DrawRectangle(pen, box);
                g.DrawLine(pen, split, box.Top, split, box.Bottom);
            }
        }

    }
}
