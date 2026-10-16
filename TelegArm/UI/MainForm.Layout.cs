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
            // Step 3.4: logical px at the window's DPI (the splitter stays a 1 px hairline).
            _split.Panel1MinSize = Px(240);
            _split.Panel2MinSize = Px(360);
            try { _split.SplitterDistance = Px(300); } catch { /* width too small yet */ }
            HookColumnSplitter();   // owner 2026-10-15: the list width survives a trip through one column
            PerfLog.Boot("  BuildUi: SplitContainer ready");

            BuildLeftPanel();
            PerfLog.Boot("  BuildUi: BuildLeftPanel done");
            BuildRightPanel();
            PerfLog.Boot("  BuildUi EXIT (BuildRightPanel done)");
        }

        /// <summary>The horizontal folder strip (tab mode; side-panel mode in one column).</summary>
        private NoNativeScrollFlowPanel NewFolderBar()
        {
            return new NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, Px(1), Px(8), Px(1)),   // flush left: "All" now aligns with the chat-row avatars below
                WrapContents = false,
                AutoScroll = true,
                BackColor = ThemeHelper.PaletteFor(_dark).ListBg
            };
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
                Margin = new Padding(Px(2), Px(12), Px(10), Px(6)),
                FontPoints = 10.5f
            });
            _searchBox.TextChanged += (s, e) => OnSearchTextChanged();
            PerfLog.Boot("    BuildLeftPanel: search box constructed");

            _searchDebounce = new Timer { Interval = 500 };
            _searchDebounce.Tick += (s, e) => DoMessageSearch();

            _hamburger = new Button
            {
                AccessibleName = "Menu",   // R6: a drawn glyph has no text to read
                Size = new Size(Px(38), Px(38)),
                Anchor = AnchorStyles.None,            // centered in its cell
                FlatStyle = FlatStyle.Flat,
                ForeColor = ThemeHelper.PaletteFor(_dark).AccentGlyph,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            _hamburger.FlatAppearance.BorderSize = 0;
            _hamburger.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _hamburger.FlatAppearance.MouseDownBackColor = ColorMath.WithAlpha(_accent, 70);
            _hamburger.Click += (s, e) => ShowDrawer();
            // Draw the hamburger as a FIXED GDI+ shape (3 rounded accent lines), NOT a font glyph: the glyph
            // (☰ U+2630 / Segoe UI Symbol) rendered inconsistently across fonts/DPI/RT — 2-vs-3 lines and a
            // small font-fallback with no accent. A drawn shape is deterministic: always 3 lines, always accent.
            _hamburger.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _hamburger);   // step 5.9: before the glyph
            _hamburger.Paint += (s, e) => DrawHamburger(e.Graphics, _hamburger.ClientRectangle, ThemeHelper.PaletteFor(_dark).AccentGlyph);

            var searchRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ColumnCount = 2,
                RowCount = 1
            };
            searchRow.ColumnStyles.Add(ColPx(52));
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
                if (Dpi.Busy || _dpiRelayoutPending) return;   // step 3.4: the deferred DPI relayout does it once, at the final width
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
                BackColor = ThemeHelper.PaletteFor(_dark).ListBg
            };

            if (sidebar)
            {
                // SIDE PANEL: a vertical folder rail on the far left (full height), the search row + chat-list
                // host stacked to its right. Owner 2026-10-16: the rail is OUTSIDE the split (a Dock.Left strip of
                // Content), so it stays beside the open chat in one column, as in Telegram Desktop; Panel1 holds the
                // list only. Near the minimum width the tab strip (row 2 of `right`) stands in for it.
                _folderRail = new NoNativeScrollFlowPanel
                {
                    Dock = DockStyle.Left,
                    Width = Px(72),
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    BackColor = ThemeHelper.PaletteFor(_dark).FolderRailBg
                };
                RebuildFolderRail();

                var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
                right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // owner bug 2026-10-15: an explicit column, or the AUTO-SIZED default column takes a DPI-scaled child's width and never shrinks back
                right.RowStyles.Add(RowPx(60));
                right.RowStyles.Add(RowPx(0));      // STORIES: tray row 1 (hidden until stories load)
                right.RowStyles.Add(RowPx(0));      // owner 2026-10-15/16: the folder TAB strip, open only near the minimum width
                right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                right.Controls.Add(searchRow, 0, 0);
                right.Controls.Add(_storyTrayBar, 0, 1);
                _folderBar = NewFolderBar();        // built when shown (SwapFolderNavigator → RebuildFolders)
                _folderBarHost = WrapWithHScrollbar(_folderBar);
                _folderBarHost.Visible = false;
                right.Controls.Add(_folderBarHost, 0, 2);
                _chatListHost = WrapWithScrollbar(_chatListPanel);
                right.Controls.Add(_chatListHost, 0, 3);
                _storyTrayLayout = right;
                TouchScroller.Enable(_folderBar, horizontal: true);

                _split.Panel1.Controls.Add(right);
                Content.Controls.Add(_folderRail);   // added after _split, so it docks first: outermost left, full height
                _sidebarMode = true;

                TouchScroller.Enable(_folderRail, horizontal: false);   // rail is touch-pannable if it overflows
            }
            else
            {
                // TABBED (default): today's exact 3-row layout — search / horizontal folder bar / chat list.
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // owner bug 2026-10-15: an explicit column, or the AUTO-SIZED default column takes a DPI-scaled child's width and never shrinks back
                layout.RowStyles.Add(RowPx(60));
                layout.RowStyles.Add(RowPx(0));     // STORIES: tray row 1 (hidden until stories load)
                layout.RowStyles.Add(RowPx(46));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                _folderBar = NewFolderBar();
                RebuildFolderBar();   // starts with just "All" until folders load

                layout.Controls.Add(searchRow, 0, 0);
                layout.Controls.Add(_storyTrayBar, 0, 1);
                _folderBarHost = WrapWithHScrollbar(_folderBar);
                layout.Controls.Add(_folderBarHost, 0, 2);
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

        // ── Step 5.4/5.5: the chat background ──────────────────────────────────────────────────────────────────────

        /// <summary>The chat panel's own background (between and below the rows); plain = the panel's BackColor, as before.</summary>
        private void PaintChatBackdrop(object sender, PaintEventArgs e)
        {
            if (!ChatBackdrop.IsPlain(_dark) || WindowBackdrop.Enabled) ChatBackdrop.PaintHost(e.Graphics, _messagePanel, e.ClipRectangle, _dark);
        }

        /// <summary>A viewport-fixed background (vertical / diagonal gradient, photo) cannot be blitted by a scroll: repaint the
        /// visible rows. Scroll-invariant ones (and plain) need nothing.</summary>
        private void InvalidateFixedBackdrop()
        {
            if (_messagePanel == null || IsDisposed || ChatBackdrop.ScrollInvariant(_dark)) return;
            _messagePanel.Invalidate(true);
        }

        /// <summary>Step 5.9: a flat header / search-row button over the window backdrop. WinForms fills a "transparent" flat
        /// button with its parent's solid colour; with the backdrop on, paint the slice (and the hover tint) before the glyph.</summary>
        private void PaintButtonBackdrop(Graphics g, Button b)
        {
            if (!WindowBackdrop.Enabled || b == null || b.IsDisposed) return;
            WindowBackdrop.ClearBehind(g, b, b.Parent, b.ClientRectangle, ThemeHelper.PaletteFor(_dark).Header);
            if (b.ClientRectangle.Contains(b.PointToClient(Cursor.Position)))
                using (var hb = new SolidBrush(ColorMath.WithAlpha(_accent, 40))) g.FillRectangle(hb, b.ClientRectangle);
        }

        private void OnChatBackdropChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (_messagePanel != null) _messagePanel.Invalidate(true); })); } catch { }
        }

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
        private const int ComposerRowH = 64;   // logical: the input row's built height (plan E3 grows it from there)

        // ── Step 3.4: after a DPI change (plan §6.1) ─────────────────────────────────────────────────────────────────────
        // DpiScaler has ratio-scaled every bound, padding, explicit font and Absolute table style, and the IDpiAware controls
        // re-set what they own. Left for MainForm: what it derives from state (rows toggled open are re-set from their
        // LOGICAL size, so one opened before the change and one opened after match to the pixel), hairlines, the keyboard
        // shrink, things built at a DPI (drawer, dock profile), and the bubbles' measured heights. Those are DEFERRED: the
        // panel's final width exists only after the scaler's last layout. Tegra 3 (§6.1 cost control): the bubbles within
        // a page of the viewport are measured at once, the rest in batches of 12 on a 15 ms timer, nearest first, keeping the
        // reader's place (the first visible bubble's offset, or the bottom).
        private int _dpiRelayoutGen;
        private bool _dpiRelayoutPending;
        private Timer _remeasureTimer;
        private readonly List<Control> _remeasureQueue = new List<Control>();
        private MessageBubbleControl _remeasureAnchor;
        private int _remeasureAnchorTop;
        private bool _remeasureAtBottom;

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_split != null) _split.SplitterWidth = 1;                  // hairlines (the scaler rounds 1 up to 2)
            if (_headerDivider != null) _headerDivider.Height = 1;
            if (_rightLayout != null)
            {
                ResetOpenRow(_rightLayout, 1, 48);   // mini player
                ResetOpenRow(_rightLayout, 2, 46);   // pinned bar
                ResetOpenRow(_rightLayout, 3, 48);   // selection bar
                ResetOpenRow(_rightLayout, 4, 36);   // forum topics
                ResetOpenRow(_rightLayout, 6, 40);   // reply strip
                ResetOpenRow(_rightLayout, 8, 48);   // thread join bar
                SetRowPx(_rightLayout, 0, 56);       // header
                SetRowPx(_rightLayout, 9, ComposerRowH);   // the composer grows from here in the deferred pass
            }
            if (_storyTrayLayout != null)
            {
                SetRowPx(_storyTrayLayout, 0, 60);
                ResetOpenRow(_storyTrayLayout, 1, StoryTrayHeight);
                SetRowPx(_storyTrayLayout, 2, FolderBarShown ? 46 : 0);   // the folder strip (side-panel mode: one column only)
            }
            if (_sidebarMode && _folderRail != null) _folderRail.Width = Px(72);   // the rail, outside the split (owner 2026-10-16)
            if (_composerColumns != null && _composerColumns.ColumnStyles.Count > 0 && _composerColumns.ColumnStyles[0].Width > 0)
                SetColPx(_composerColumns, 0, 48);   // bot menu
            try { SyncReplyKeyboardHeight(); } catch { }
            if (_kbActive) MinimumSize = new Size(MinimumSize.Width, Px(120));   // AppForm just reset it to the full minimum
            if (_drawer != null) CloseDrawer();   // built at the old DPI; the next open builds it at the new one
            if (_dock != null) _dock.Width = Px(DockWidth);   // EnforceDockWidth runs in RelayoutAfterDpi (it skips while Dpi.Busy)
            _dpiRelayoutPending = true;
            int gen = ++_dpiRelayoutGen;
            try { BeginInvoke((Action)(() => RelayoutAfterDpi(gen))); } catch { _dpiRelayoutPending = false; }
        }

        private void ResetOpenRow(TableLayoutPanel t, int index, int logical)
        {
            if (t != null && index >= 0 && index < t.RowStyles.Count && t.RowStyles[index].Height > 0) SetRowPx(t, index, logical);
        }

        private void RelayoutAfterDpi(int gen)
        {
            if (gen != _dpiRelayoutGen) return;
            _dpiRelayoutPending = false;
            if (IsDisposed) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rows = 0, sync = 0;
            if (_dockProfile != null)
            {
                // ProfileForm's content width is fixed at construction: rebuild it at the new one (here, not inside the
                // scaler's pass: it is a whole form and starts a details load).
                DropDockProfile();
                if (_dock != null && _dock.Visible && _dockPane == DockPane.Info) EnsureDockProfile();
            }
            if (_chatListPanel != null)
            {
                int w = ContentWidth(_chatListPanel);
                foreach (Control c in _chatListPanel.Controls) { c.Width = w; rows++; }
            }
            StopRemeasure();
            if (_messagePanel != null && _messagePanel.Controls.Count > 0)
            {
                _remeasureAtBottom = AtBottom(AtBottomThreshold);
                _remeasureAnchor = TopVisibleBubble();
                _remeasureAnchorTop = _remeasureAnchor != null ? _remeasureAnchor.Top : 0;
                int w = ContentWidth(_messagePanel), page = Math.Max(1, _messagePanel.ClientSize.Height);
                _bubbleLayoutWidth = w;
                var later = new List<KeyValuePair<int, Control>>();
                _messagePanel.SuspendLayout();
                try
                {
                    foreach (Control c in _messagePanel.Controls)
                    {
                        c.Width = w;
                        (c as SponsoredCardControl)?.Measure();
                        var bubble = c as MessageBubbleControl;
                        if (bubble == null) continue;
                        int dist = c.Bottom < 0 ? -c.Bottom : c.Top > page ? c.Top - page : 0;   // from the viewport
                        if (dist <= page) { bubble.Measure(); sync++; }
                        else later.Add(new KeyValuePair<int, Control>(dist, c));
                    }
                }
                finally { _messagePanel.ResumeLayout(true); }
                RestoreReadingPlace();
                later.Sort((a, c) => a.Key.CompareTo(c.Key));   // nearest first
                foreach (var kv in later) _remeasureQueue.Add(kv.Value);
                if (_remeasureQueue.Count > 0)
                {
                    if (_remeasureTimer == null)
                    {
                        _remeasureTimer = new Timer { Interval = 15 };
                        _remeasureTimer.Tick += (s, e) => RemeasureBatch();
                    }
                    _remeasureTimer.Start();
                }
            }
            GrowComposerRow();
            CenterConnectingPanel();
            LayoutConnectingButtons();
            PositionJumpButton();
            PositionProxyPill();
            EnforceDockWidth();                                   // after the rescale: a clamped window may be too narrow for the dock
            UpdateColumnLayout();                                 // owner 2026-10-15: one column or two, at the final width
            if (_dateFlyout != null) _dateFlyout.Text = "";       // re-measured at the new DPI on its next update
            Logger.Diag("[DPI] relayout dpi=" + WindowDpiValue + " chatRows=" + rows + " bubbles now=" + sync
                        + " queued=" + _remeasureQueue.Count + " " + sw.ElapsedMilliseconds + "ms");
        }

        /// <summary>The deferred DPI re-measure: up to 12 bubbles per tick, nearest the viewport first. Re-baselined PER BATCH
        /// (review): it compensates only this batch's own layout shift, so a scroll the reader makes between ticks is kept,
        /// and it keeps the view pinned to the bottom only if the reader is there now.</summary>
        private void RemeasureBatch()
        {
            if (IsDisposed || _messagePanel == null || _remeasureQueue.Count == 0) { StopRemeasure(); return; }
            bool atBottom = DistanceFromBottom() <= Px(2);
            var anchor = atBottom ? null : TopVisibleBubble();
            int before = anchor != null ? anchor.Top : 0;
            int n = 0;
            _messagePanel.SuspendLayout();
            try
            {
                while (_remeasureQueue.Count > 0 && n < 12)
                {
                    var c = _remeasureQueue[0];
                    _remeasureQueue.RemoveAt(0);
                    // A chat switch cleared the panel (or the row was removed): skip what is gone.
                    if (c.IsDisposed || !ReferenceEquals(c.Parent, _messagePanel)) continue;
                    (c as MessageBubbleControl)?.Measure();
                    n++;
                }
            }
            finally { _messagePanel.ResumeLayout(true); }
            if (atBottom) ScrollMessagesToBottom();
            else if (anchor != null && !anchor.IsDisposed && ReferenceEquals(anchor.Parent, _messagePanel))
            {
                int delta = anchor.Top - before;
                if (delta != 0) _messagePanel.AutoScrollPosition = new Point(0, Math.Max(0, -_messagePanel.AutoScrollPosition.Y + delta));
            }
            if (_remeasureQueue.Count == 0) StopRemeasure();
        }

        private void StopRemeasure()
        {
            if (_remeasureTimer != null) _remeasureTimer.Stop();
            _remeasureQueue.Clear();
        }

        /// <summary>Back to the bottom, or the anchor bubble back at its offset in the viewport.</summary>
        private void RestoreReadingPlace()
        {
            if (_messagePanel == null) return;
            if (_remeasureAtBottom) { ScrollMessagesToBottom(); return; }
            var a = _remeasureAnchor;
            if (a == null || a.IsDisposed || !ReferenceEquals(a.Parent, _messagePanel)) return;
            int delta = a.Top - _remeasureAnchorTop;
            if (delta == 0) return;
            int y = Math.Max(0, -_messagePanel.AutoScrollPosition.Y + delta);
            _messagePanel.AutoScrollPosition = new Point(0, y);
        }

        // ── Step 3.4: the shell in LOGICAL px. Rows and columns are created at the window's DPI (identity at 96) and every
        //    run-time write goes through SetRowPx / SetColPx: DpiScaler rescales what exists during a DPI change, but a row
        //    shown AFTER it (mini player, pinned bar, reply strip...) used to come back at its 96-DPI size.
        private RowStyle RowPx(int logical) { return new RowStyle(SizeType.Absolute, logical <= 0 ? 0 : Px(logical)); }
        private ColumnStyle ColPx(int logical) { return new ColumnStyle(SizeType.Absolute, logical <= 0 ? 0 : Px(logical)); }

        private void SetRowPx(TableLayoutPanel t, int index, int logical)
        {
            if (t != null && index >= 0 && index < t.RowStyles.Count) t.RowStyles[index].Height = logical <= 0 ? 0 : Px(logical);
        }

        private void SetColPx(TableLayoutPanel t, int index, int logical)
        {
            if (t != null && index >= 0 && index < t.ColumnStyles.Count) t.ColumnStyles[index].Width = logical <= 0 ? 0 : Px(logical);
        }

        /// <summary>Plan E3: the input row (row 9 of the right panel) follows the colour-emoji composer's content height, from
        /// its built height up to 13 lines and never more than 45% of the pane. A reader at the bottom of the chat stays there.</summary>
        private void GrowComposerRow()
        {
            try
            {
                var rich = _messageInput != null ? _messageInput.Rich : null;
                if (rich == null || _rightLayout == null || _rightLayout.RowStyles.Count < 10) return;
                var row = _rightLayout.RowStyles[9];
                // Step 3.4: from the logical minimum and the composer's own margins, every time — a cached pixel value went
                // stale on a DPI change.
                int rowMin = Px(ComposerRowH), margins = rich.Margin.Vertical;
                int cap = Math.Min(rich.HeightForLines(13) + margins, (int)(_rightLayout.Height * 0.45));
                int want = Math.Max(rowMin, Math.Min(Math.Max(rowMin, cap), rich.ContentHeight + margins));
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
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // owner bug 2026-10-15: an explicit column, or the AUTO-SIZED default column takes a DPI-scaled child's width and never shrinks back
            layout.RowStyles.Add(RowPx(56));  // 0 header
            layout.RowStyles.Add(RowPx(0));   // 1 mini player (toggled)
            layout.RowStyles.Add(RowPx(0));   // 2 pinned bar (toggled)
            layout.RowStyles.Add(RowPx(0));   // 3 selection bar (toggled)
            layout.RowStyles.Add(RowPx(0));   // 4 FORUM topic bar (toggled) — FORUM-TOPICS
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 5 messages
            layout.RowStyles.Add(RowPx(0));   // 6 reply strip (toggled)
            layout.RowStyles.Add(RowPx(0));   // 7 reply keyboard (toggled)
            layout.RowStyles.Add(RowPx(0));   // 8 thread join bar (toggled) — COMMENTS-JOIN-FLYOUT
            layout.RowStyles.Add(RowPx(ComposerRowH));  // 9 input

            var topBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Cursor = Cursors.Hand };
            _headerBar = topBar;   // 2.7b: its surface comes from the Header token (ApplyPanelColors)
            var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            titleStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // owner bug 2026-10-15: an explicit column, or the AUTO-SIZED default column takes a DPI-scaled child's width and never shrinks back
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
                Padding = new Padding(Px(10), 0, 0, 0),
                Cursor = Cursors.Hand
            };
            _chatStatus = new ThemedLabel
            {
                Text = "",
                Dock = DockStyle.Fill,
                PointSize = 9f,
                Role = LabelRole.Dim,
                Ellipsis = true,
                Padding = new Padding(Px(10), 0, 0, 0),
                Cursor = Cursors.Hand
            };
            titleStack.Controls.Add(_chatTitle, 0, 0);
            titleStack.Controls.Add(_chatStatus, 0, 1);
            topBar.Controls.Add(titleStack);
            // DOWNLOAD-UX Part 4: transfers indicator (aggregate ring + badge; hidden when the roster is
            // empty). Fill sibling added first so the Right dock takes the edge (codebase dock convention).
            _dlIndicator = new DownloadIndicator(_service) { AccessibleName = "Downloads", Dock = DockStyle.Right };
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
            // 2026-10-16: the phone (a call), leftmost of the header's buttons; shown for a private chat with a person (MainForm.Calls CanCall).
            // Metrics as _chatMenuBtn below.
            _callBtn = new Button
            {
                Dock = DockStyle.Right, Width = Px(44), FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false, Visible = false, AccessibleName = "Call"
            };
            _callBtn.FlatAppearance.BorderSize = 0;
            _callBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _callBtn.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _callBtn);
            _callBtn.Paint += (s, e) =>
            {
                var glyph = ThemeHelper.PaletteFor(_dark).AccentGlyph;
                // by the chat's KIND (owner 2026-10-16): the voice chat only in groups and channels, the phone everywhere else
                if (IsGroupOrChannel(_selectedChat)) DrawVoiceChatGlyph(e.Graphics, _callBtn.ClientRectangle, glyph);
                else DrawPhoneGlyph(e.Graphics, _callBtn.ClientRectangle, glyph);
            };
            _callBtn.Click += (s, e) => OnCallButton();
            _callBtn.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowCallMenu(); };   // Call / Video call; Join voice chat
            _chatMenuBtn = new Button
            {
                AccessibleName = "Chat menu",   // R6: a drawn glyph has no text to read
                Dock = DockStyle.Right, Width = Px(44), FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false   // shown only while a chat is open — toggled beside _chatSearchBtn
            };
            _chatMenuBtn.FlatAppearance.BorderSize = 0;
            _chatMenuBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _chatMenuBtn.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _chatMenuBtn);
            _chatMenuBtn.Paint += (s, e) => DrawKebab(e.Graphics, _chatMenuBtn.ClientRectangle, ThemeHelper.PaletteFor(_dark).AccentGlyph);
            _chatMenuBtn.Click += (s, e) => ShowChatHeaderMenu();
            topBar.Controls.Add(_chatMenuBtn);
            // INCHAT-SEARCH: a magnifier in the chat header → enter in-chat search (the left panel becomes scoped
            // results for the open chat). Drawn (GDI), not a font glyph, for crisp/consistent rendering on RT.
            _chatSearchBtn = new Button
            {
                AccessibleName = "Search in chat",   // R6: a drawn glyph has no text to read
                Dock = DockStyle.Right, Width = Px(44), FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false   // shown only while a chat is open (toggled in OpenChat / on leaving a chat)
            };
            _chatSearchBtn.FlatAppearance.BorderSize = 0;
            _chatSearchBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _chatSearchBtn.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _chatSearchBtn);
            _chatSearchBtn.Paint += (s, e) => DrawMagnifier(e.Graphics, _chatSearchBtn.ClientRectangle, ThemeHelper.PaletteFor(_dark).AccentGlyph);
            _chatSearchBtn.Click += (s, e) => EnterInChatSearch();
            topBar.Controls.Add(_chatSearchBtn);
            topBar.Controls.Add(_callBtn);
            // BATCH-TA-23/D1c — the dock toggle. Added LAST of the right-docked group, so per the measured
            // rule (first-added = leftmost) it lands OUTERMOST RIGHT — at the very edge the dock itself
            // slides in from, which is the only placement that reads as "open the thing next to me".
            // No existing Controls.Add moves.
            // ⚠ Metrics copied from _chatMenuBtn exactly as that copied _chatSearchBtn: 44 px, Flat,
            //   transparent, borderless, 40-alpha accent hover, drawn glyph, accent from ThemeHelper,
            //   hidden until a chat is open.
            _dockBtn = new Button
            {
                AccessibleName = "Chat info panel",   // R6: a drawn glyph has no text to read
                Dock = DockStyle.Right, Width = Px(44), FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false,
                Visible = false
            };
            _dockBtn.FlatAppearance.BorderSize = 0;
            _dockBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _dockBtn.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _dockBtn);
            _dockBtn.Paint += (s, e) => DrawDockGlyph(e.Graphics, _dockBtn.ClientRectangle, ThemeHelper.PaletteFor(_dark).AccentGlyph,
                                                      _dock != null && _dock.Visible);
            _dockBtn.Click += (s, e) => ToggleDock();
            topBar.Controls.Add(_dockBtn);
            // Peer avatar before the name. Added LAST so its Dock.Left resolves outermost-left; titleStack (Fill,
            // added first) then occupies the space between the avatar and the right-docked transfers indicator.
            _headerAvatar = new Panel { AccessibleName = "Chat info", Dock = DockStyle.Left, Width = Px(54), Margin = new Padding(0), Cursor = Cursors.Hand };
            _headerAvatar.Paint += (s2, e2) =>   // step 5.9: behind the round avatar, the backdrop (before the avatar is drawn)
            {
                if (WindowBackdrop.Enabled)
                    WindowBackdrop.ClearBehind(e2.Graphics, _headerAvatar, _headerAvatar.Parent, _headerAvatar.ClientRectangle, ThemeHelper.PaletteFor(_dark).Header);
            };
            _headerAvatar.Paint += HeaderAvatar_Paint;
            _headerAvatar.Click += (s, e) => OnHeaderClick();
            topBar.Controls.Add(_headerAvatar);
            // Owner 2026-10-15: one column's back arrow (+ the other chats' unread). Added after the avatar, so it docks
            // outermost left. Metrics as _dockBtn; hidden in two columns (UpdateBackButton).
            _backBtn = new Button
            {
                Dock = DockStyle.Left, Width = Px(44), FlatStyle = FlatStyle.Flat, Text = "",
                BackColor = Color.Transparent, Cursor = Cursors.Hand, TabStop = false, Visible = false,
                AccessibleName = "Back"
            };
            _backBtn.FlatAppearance.BorderSize = 0;
            _backBtn.FlatAppearance.MouseOverBackColor = ColorMath.WithAlpha(_accent, 40);
            _backBtn.Paint += (s, e) => PaintButtonBackdrop(e.Graphics, _backBtn);
            _backBtn.Paint += (s, e) => PaintBackButton(e.Graphics);
            _backBtn.Click += (s, e) => OnBackClick();
            topBar.Controls.Add(_backBtn);
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
                Padding = new Padding(0, 0, 0, Px(8))
            };
            ScrollbarTheme.Apply(_messagePanel, _dark);
            _messagePanel.SizeChanged += (s, e) =>
            {
                if (Dpi.Busy || _dpiRelayoutPending) return;   // step 3.4: mid-rescale widths are interim; RelayoutAfterDpi measures
                int w = ContentWidth(_messagePanel);
                // Plan E3: bubbles depend on the WIDTH only. The growing composer changes the panel's HEIGHT on every
                // wrapped line; re-measuring every bubble for that was the cost the plan flagged (Tegra 3).
                if (w == _bubbleLayoutWidth) return;
                _bubbleLayoutWidth = w;
                foreach (Control c in _messagePanel.Controls)
                {
                    c.Width = w;
                    (c as MessageBubbleControl)?.Measure();
                    (c as SponsoredCardControl)?.Measure();   // step 3.2: the card measures from its width too
                }
            };
            // Redesign 2.7b: the empty state. With no chat open, a centred service pill says what to do, as in Telegram.
            _messagePanel.Paint += PaintChatBackdrop;   // step 5.4: before the empty state, which draws over it
            _messagePanel.Paint += PaintEmptyState;
            _messagePanel.Resize += (s, e) => { if (_selectedChat == null) _messagePanel.Invalidate(); };
            _messagePanel.Scroll += MessagePanel_Scroll;
            _messagePanel.Scroll += (s, e) => InvalidateFixedBackdrop();   // step 5.5
            _messagePanel.Resize += (s, e) => InvalidateFixedBackdrop();
            ChatBackdrop.Changed += OnChatBackdropChanged;
            FormClosed += (s, e) => ChatBackdrop.Changed -= OnChatBackdropChanged;
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) ChatBackdrop.Release(); };
            // Mouse-wheel scrolling doesn't reliably raise Scroll with NewValue==0, so
            // check the position right after the wheel is applied (hence BeginInvoke)
            // and page in older messages when we land near the top.
            _messagePanel.MouseWheel += (s, e) =>
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] wheel-handled (delta=" + e.Delta + ")");
                BeginInvoke((Action)InvalidateFixedBackdrop);   // step 5.5: after the wheel has scrolled
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
            bottomBar.ColumnStyles.Add(ColPx(0));    // 0 bot Menu (toggled: bot chats only)
            bottomBar.ColumnStyles.Add(ColPx(48));   // 1 attach
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 2 input / recording
            bottomBar.ColumnStyles.Add(ColPx(40));   // 3 emoji
            bottomBar.ColumnStyles.Add(ColPx(48));   // 4 mic
            // ⚠ 96 -> 40, MATCHING THE EMOJI COLUMN. 96 was historical — sized for the old text button that
            //   had the word "Send" in it. The round send disc replaced that button but the COLUMN kept its width,
            //   so a 40px control sat in a 96px cell with ~28px of dead space either side — which is why
            //   the button read as oversized and unbalanced even after the disc itself was shrunk. The
            //   control was never the problem; its cell was.
            bottomBar.ColumnStyles.Add(ColPx(40));   // 5 send (== emoji)

            // FIRST FontHelper call in BuildUi. FontHelper's static ctor is LAZY and registers five embedded
            // TTFs with BOTH GDI+ (PrivateFontCollection) and gdi32 (AddFontMemResourceEx) — if that cost is
            // in the ~1.9 s, it lands between these two rungs.
            PerfLog.Boot("    BuildRightPanel: before first FontHelper.Ui call");
            _botMenuButton = new Button
            {
                AccessibleName = "Bot menu",   // R6: a drawn glyph has no text to read
                Text = "", Anchor = AnchorStyles.None, Width = Px(44), Height = Px(38), Visible = false,
                FlatStyle = FlatStyle.Flat, Font = FontHelper.UiAt(13f, WindowDpiValue), Cursor = Cursors.Hand,   // shared: never dispose
                BackColor = ThemeHelper.PaletteFor(_dark).BotMenuBg,
                ForeColor = ThemeHelper.PaletteFor(_dark).BotMenuFg
            };
            PerfLog.Boot("    BuildRightPanel: after first FontHelper.Ui call (fonts registered)");
            _botMenuButton.FlatAppearance.BorderSize = 0;
            _botMenuButton.Click += (s, e) => ShowBotMenu();
            // The "≡" menu icon is DRAWN (3 accent lines, like the hamburger) — a font "≡" substitutes an ugly
            // glyph on RT. In web-app mode the button shows the "Menu" text instead (_botMenuIcon = false).
            _botMenuButton.Paint += (s, e) => { if (_botMenuIcon) DrawHamburger(e.Graphics, _botMenuButton.ClientRectangle, ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).BotMenuBg, 3.0)); };

            _attachButton = new AttachButton
            {
                AccessibleName = "Attach",   // R6: a drawn glyph has no text to read
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
                    Margin = new Padding(Px(12), Px(10), Px(6), Px(10)),
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
                    Margin = new Padding(Px(12), Px(10), Px(6), Px(10)),
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
                else if (e.Control && e.KeyCode == Keys.U) { e.SuppressKeyPress = true; WrapComposerSelection("<u>", "</u>"); }   // T8
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
            fmtMenu.Items.Add("Underline").Click += (s, e) => WrapComposerSelection("<u>", "</u>");   // T8
            fmtMenu.Items.Add("Spoiler").Click += (s, e) => WrapComposerSelection("||", "||");
            fmtMenu.Items.Add("Quote").Click += (s, e) => ComposerFormat.Quote(_messageInput);
            fmtMenu.Items.Add("Link…").Click += (s, e) => InsertComposerLink();
            // T10: the server makes a preview of the first link unless told not to — for the next message only
            var previewItem = new ToolStripMenuItem("Link preview") { CheckOnClick = true, Checked = true };
            previewItem.CheckedChanged += (s, e) => _sendNoLinkPreview = !previewItem.Checked;
            fmtMenu.Opening += (s, e) => previewItem.Checked = !_sendNoLinkPreview;
            fmtMenu.Items.Add(previewItem);
            fmtMenu.Items.Add(new ToolStripSeparator());
            fmtMenu.Items.Add("Paste").Click += (s, e) => PasteIntoComposer();
            fmtMenu.Items.Add(new ToolStripSeparator());
            fmtMenu.Items.Add("Edit in a window…").Click += (s, e) => OpenComposerEditor();   // step 2.7d (both engines)
            _messageInput.ContextMenuStrip = fmtMenu;

            // TA-34/A — a round accent disc with a drawn paper-plane, sized and centred exactly like the
            // mic/emoji/attach buttons beside it (Anchor.None, 40x40 from the control itself).
            _sendButton = new UI.Controls.SendButton
            {
                AccessibleName = "Send",   // R6: a drawn glyph has no text to read
                Anchor = AnchorStyles.None,
                Enabled = false
            };
            _sendButton.Click += (s, e) => { SendPathLog("button"); if (_voiceState == VoiceState.Ready) SendPendingVoice(); else SendCurrentMessage(); };
            // T11: right-click (or press-and-hold, which Windows turns into one) → schedule the message
            _sendButton.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowSendMenu(); };

            _emojiButton = new EmojiGlyphButton { AccessibleName = "Emoji, stickers and GIFs", Anchor = AnchorStyles.None, Enabled = false };
            _emojiButton.Click += (s, e) => OpenEmojiPicker();

            _micButton = new MicButton { AccessibleName = "Record a voice message", Anchor = AnchorStyles.None, Enabled = false };
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
                AccessibleName = "Cancel reply",   // R6: a drawn glyph has no text to read
                Text = "✕",
                Dock = DockStyle.Right,
                Width = Px(44),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                Font = FontCache.Get("Segoe UI", 11f, FontStyle.Regular, WindowDpiValue)   // shared: never dispose
            };
            _replyCancelBtn.FlatAppearance.BorderSize = 0;
            _replyCancelBtn.Click += (s, e) => CancelReplyByUser();
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
            _forumTopicBar = new NoNativeScrollFlowPanel { Dock = DockStyle.Fill, Margin = new Padding(Px(2), Px(1), Px(2), Px(1)), WrapContents = false, AutoScroll = true, BackColor = ThemeHelper.PaletteFor(_dark).ListBg };   // FORUM-TOPICS: symmetric L/R (was 0/8); "All" lands ~10px, aligning with the mini/pinned bars
            layout.Controls.Add(_forumTopicBar, 0, 4);   // FORUM-TOPICS: added DIRECTLY (no WrapWithHScrollbar) → NO visible scrollbar; NoNativeScroll suppresses the native bar + TouchScroller gives drag-scroll
            TouchScroller.Enable(_forumTopicBar, horizontal: true);
            _msgHost = WrapWithScrollbar(_messagePanel);
            layout.Controls.Add(_msgHost, 0, 5);

            // Floating "scroll to bottom" button over the message panel (jitter-free: a child of the host,
            // not the scrolling panel). Visible only when scrolled up; badge shows messages missed meanwhile.
            _jumpBtn = new JumpToBottomButton
            {
                AccessibleName = "Go to the latest messages",   // R6: a drawn glyph has no text to read
                IsDark = _dark, AccentColor = _accent, Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _jumpBtn.Click += (s, e) => JumpToBottomClicked();
            _msgHost.Controls.Add(_jumpBtn);
            _jumpBtn.BringToFront();
            // T2: jump to the unread @mentions / reactions of the open chat (MainForm.Scroll: CollectThenRead)
            _mentionBtn = new JumpToBottomButton { AccessibleName = "Go to the next mention", IsDark = _dark, AccentColor = _accent, Visible = false, Glyph = "@", Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _mentionBtn.Click += (s, e) => JumpToNextUnread(true);
            _msgHost.Controls.Add(_mentionBtn);
            _reactionBtn = new JumpToBottomButton { AccessibleName = "Go to the next reaction", IsDark = _dark, AccentColor = _accent, Visible = false, Glyph = "\u2665", Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _reactionBtn.Click += (s, e) => JumpToNextUnread(false);
            _msgHost.Controls.Add(_reactionBtn);
            _msgHost.Resize += (s, e) => PositionJumpButton();

            // BUBBLE-DATETIME (C): floating date pill — same float-parent trick as the jump button (a child of the
            // host, NOT the scrolling panel → never participates in paging/reconcile). Shown while scrolling; fades
            // ~1s after scrolling stops. The single fade timer runs only while it's shown/fading, then stops.
            _dateFlyout = new DateFlyoutPill { IsDark = _dark, Accent = _accent, Visible = false, Font = FontHelper.UiAt(8.5f, WindowDpiValue, FontStyle.Bold) };   // shared: never dispose
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
            // Step 5.9 (owner: the main window too): with "Wallpaper", these show the window backdrop; cards, rows' contents,
            // bubbles and the text boxes stay solid. Solid (the default) changes nothing.
            WindowBackdrop.Attach(_split.Panel1);
            WindowBackdrop.Attach(_split.Panel2);
            WindowBackdrop.Attach(_headerBar);
            WindowBackdrop.Attach(bottomBar);
            WindowBackdrop.Attach(_chatListPanel);
            if (_folderBar != null) WindowBackdrop.Attach(_folderBar);
            if (_folderRail != null) WindowBackdrop.Attach(_folderRail);
            WindowBackdrop.AttachTree(_split, _messagePanel);   // the panes' inner containers (header title stack, search row…)
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
                InvalidateFixedBackdrop();   // step 5.5
                // TOUCH-FREEZE: this runs per coalesced pan tick (~100Hz) — a pan parked near the top
                // fired the line + call 85×/s. Attempts are tick-limited; the head limiter in
                // LoadOlderMessages spaces the actual work for every other trigger path too.
                if (!_loadingOlder && _hasMoreHistory && -_messagePanel.AutoScrollPosition.Y <= Px(40)
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
            SetRowPx(_rightLayout, 1, active ? 48 : 0);
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
            // Drawn for a 38 px button (the hamburger; the bot-menu button is 44 x 38): scales with the button (step 3.4).
            float k = Math.Min(area.Width, area.Height) / 38f;
            int lineW = (int)Math.Round(20 * k), lineH = Math.Max(1, (int)Math.Round(3 * k)), gap = (int)Math.Round(4 * k);   // 3 lines, evenly spaced
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
