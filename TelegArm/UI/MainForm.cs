using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    /// <summary>
    /// Telegram-style two-pane shell: a chat list on the left and the selected
    /// conversation on the right.
    /// </summary>
    public class MainForm : MaterialForm
    {
        private readonly TelegramService _service;
        private readonly MaterialSkinManager _skin;
        // AVATAR-PIPELINE: ONE download-once store behind every avatar surface (memory LRU → photo_id-keyed
        // disk files → single-flight bounded downloads with a visible-first backfill queue). Replaces the old
        // _avatarCache/_noAvatar pair — transient failures are retryable now, never marked "no avatar".
        private readonly AvatarStore _avatars;
        private readonly Dictionary<long, string> _peerNames = new Dictionary<long, string>();      // id→display name (for group typing/sender)
        private readonly Dictionary<long, Image> _photoCache = new Dictionary<long, Image>();      // full photos (by photo id)
        private readonly Dictionary<long, Image> _photoThumbCache = new Dictionary<long, Image>(); // small previews (photo id / video doc id)

        // Inline custom-emoji images, fetched in debounced batches by document id.
        private readonly Dictionary<long, Image> _customEmojiCache = new Dictionary<long, Image>();
        private readonly HashSet<long> _customEmojiPending = new HashSet<long>();
        private readonly List<long> _customEmojiQueue = new List<long>();
        private System.Windows.Forms.Timer _customEmojiTimer;
        private readonly Dictionary<long, string> _photoCachePaths = new Dictionary<long, string>();
        private readonly List<Message> _currentChatMessages = new List<Message>();
        private bool _fellBack;

        private Color _accent = Color.DodgerBlue;
        private bool _dark;

        // The official TelegArm channel — the drawer's "TelegArm Channel" row opens it in the chat view.
        private const string CHANNEL_USERNAME = "TelegArm_official";   // <-- set the real channel handle here (without @)

        private SplitContainer _split;
        private Button _hamburger;
        private MaterialTextBox2 _searchBox;
        private FlowLayoutPanel _chatListPanel;
        private MaterialLabel _chatTitle;
        private MaterialLabel _chatStatus;            // online / last seen / typing… subtitle
        private System.Windows.Forms.Timer _typingTimer;
        private bool _typing;
        private FlowLayoutPanel _messagePanel;
        private MaterialTextBox2 _messageInput;
        // COMPOSER-full-revert: the composer's inner native TextBox is left UNTOUCHED — no font-swap fields, no
        // reflection font manipulation (that work was reverted in case it broke WM_CHAR capture on RT). _baseTextBoxField
        // stays: it's used ONLY by the [KBD] diagnostics (HookKbdDiag), never to change the composer's font.
        private static readonly System.Reflection.FieldInfo _baseTextBoxField =
            typeof(MaterialTextBox2).GetField("baseTextBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private MaterialButton _sendButton;
        private TableLayoutPanel _rightLayout;
        private TableLayoutPanel _composerBar;          // the normal "Write a message" row (input + attach + send)
        private ComposerFooterBar _footerBar;           // swapped-in footer for non-compose states
        private ComposerKind _footerKind = ComposerKind.Compose;
        private Panel _msgHost;                     // WrapWithScrollbar host (float parent for the jump button)
        private JumpToBottomButton _jumpBtn;        // floating "scroll to bottom" + unread badge
        private int _jumpUnread;                    // new messages that arrived while scrolled up
        private System.Windows.Forms.Timer _scrollWatch;
        private System.Windows.Forms.Timer _perfTimer;   // [PERF] 1.5s flush of the diagnosis counters
        private const int AtBottomThreshold = 150;  // within this many px of the bottom = "at bottom"
        private PinnedBar _pinnedBar;
        private List<Message> _pinnedMessages;     // full ordered pinned set (newest→oldest), content + ids
        private int _pinnedIndex;
        private long _pinnedChatId;                 // peer id _pinnedMessages belong to (preserve cycle on same-chat reload)
        private PinnedListForm _pinnedListForm;     // open "show all pinned" popup (null when closed)
        private MiniPlayerBar _miniBar;
        private AttachButton _attachButton;

        // Bot interactions (BOT-2): the composer Menu button + the reply-keyboard host row.
        private Button _botMenuButton;                  // composer-left "≡" Menu (bot chats only)
        private bool _botMenuIcon = true;               // true → draw the "≡" hamburger icon; false → show "Menu" text (web-app)
        private TableLayoutPanel _composerColumns;      // the 6-col bottom bar (col 0 = bot Menu, toggled)
        private Panel _replyKbHost;                     // host for the reply keyboard (row 6, toggled)
        private ReplyKeyboardControl _replyKb;
        private TL.BotInfo _currentBotInfo;             // the open bot's BotInfo (commands + menu_button), or null
        private bool _replyKbSingleUse;                 // hide the reply keyboard after one button use

        private EmojiGlyphButton _emojiButton;

        // Voice recording: mic button + an inline recording strip over the input.
        private MicButton _micButton;
        private RecordingBar _recordingBar;
        private VoiceRecorder _recorder;
        private System.Windows.Forms.Timer _recordTimer;
        private enum VoiceState { None, Recording, Ready }
        private VoiceState _voiceState = VoiceState.None;
        private string _pendingVoicePath;
        private int _pendingVoiceDur;
        private byte[] _pendingVoiceWave;

        // Reply composer: the message being replied to (null = not replying) + its strip.
        private Panel _replyStrip;
        private bool _replyEditing;                     // reply strip is owner-drawn (see DrawReplyStrip): editing vs replying
        private string _replyPreview;                   // the preview/label text drawn in the reply strip
        private Button _replyCancelBtn;
        private Message _replyTarget;
        private Message _editTarget;   // message being edited (mutually exclusive with reply)

        // Multi-select: state + the top selection toolbar (owner-painted).
        private bool _selectionMode;
        private readonly HashSet<int> _selectedMessageIds = new HashSet<int>();
        private SelectionBar _selectionBar;

        // Optimistic/pending outgoing attachments (Phase 4 Part 1: UI only — no upload).
        // Temp ids are negative; they go into _shownMessageIds so dedupe treats pending
        // bubbles uniformly, and _pendingBubbles gives Part 2 O(1) access to swap them.
        private int _nextTempMessageId = -1;
        private readonly Dictionary<int, MessageBubbleControl> _pendingBubbles = new Dictionary<int, MessageBubbleControl>();
        private readonly List<Image> _attachmentThumbs = new List<Image>(); // downscaled thumbs we own/dispose
        // Failed media sends, so a "Retry" can re-upload the same file (path/mode/caption).
        private readonly Dictionary<MessageBubbleControl, (string path, SendMode mode, string caption)> _failedSends
            = new Dictionary<MessageBubbleControl, (string, SendMode, string)>();

        private Timer _searchDebounce;

        // Chat folders (dialog filters). _activeFolder == null means "All chats".
        // FOLDER-SIDEBAR: exactly ONE of _folderBar (tabbed, default) / _folderRail (side panel) is non-null,
        // decided once at BuildLeftPanel from AppSettings.FolderSidebar — the other path never runs (2.4).
        private FlowLayoutPanel _folderBar;
        private NoNativeScrollFlowPanel _folderRail;
        // Live per-folder unread badges — shared by BOTH navigators (tab bar + rail); only one is ever
        // populated at a time. RefreshFolderBadges() pushes fresh counts in place (no rebuild/churn).
        private interface IFolderBadge { int Unread { set; } }
        private readonly List<KeyValuePair<IFolderBadge, Func<int>>> _folderBadgeSources
            = new List<KeyValuePair<IFolderBadge, Func<int>>>();
        private TL.DialogFilterBase[] _folders = new TL.DialogFilterBase[0];
        private TL.DialogFilterBase _activeFolder;
        private bool _showArchive;

        private readonly List<ChatEntry> _allChats = new List<ChatEntry>();
        private ChatListItemControl _selectedItem;
        private ChatEntry _selectedChat;

        // Per-open-conversation paging/dedup state (reset on chat switch).
        private readonly HashSet<int> _shownMessageIds = new HashSet<int>();
        private readonly Dictionary<long, MessageBubbleControl> _albumBubbles = new Dictionary<long, MessageBubbleControl>();   // grouped_id → album bubble
        private int _oldestMessageId;
        private bool _hasMoreHistory = true;
        private bool _loadingOlder;
        // Window edges for bidirectional paging. After a focused jump the loaded window is an ISLAND, not
        // the live tail: track its newest id and whether we've reached the true latest message, so scrolling
        // DOWN pages in newer messages instead of dead-ending at the island edge.
        private int _newestMessageId;
        private bool _atLiveTail = true;
        private bool _loadingNewer;
        private int _readOutboxMaxId;   // peer has read my messages up to this id (read ✓✓)

        // Connection watchdog UI hint (see TelegramService.ReconnectingChanged).
        private const string ReconnectingTitle = "TelegArm — Reconnecting…";
        private string _titleBeforeReconnect;

        // Startup "Connecting… / Waiting for network" overlay (never exit on a missing VPN).
        private Panel _connectingPanel;
        private Panel _switchOverlay;          // calm full-area transition during a switch (avatar + "Switching to X")
        private System.Windows.Forms.Timer _switchDots;
        private Image _switchAvatar;
        private string _switchOverlayName;
        private int _switchDotCount;
        private Label _connectingTitle;
        private Label _connectingDetail;
        private MaterialButton _retryButton;
        private MaterialButton _switchCancelButton;            // abort an in-flight account switch (restore the active account)
        private System.Threading.CancellationTokenSource _abortConnect;
        private bool _switchInProgress, _switchAborted;
        private bool _connectCorrupt;   // last connect failed because the session file is unreadable (permanent, not network)
        private readonly HashSet<long> _recoveryTried = new HashSet<long>();   // accounts already tried in the current corrupt-recovery chain (bounds it)
        private System.Windows.Forms.Timer _connectingDots;
        private int _dotPhase;
        private System.Threading.CancellationTokenSource _retryNowCts;

        // Tray icon + notifications. The tray is created only once authorized; until then
        // (and when _reallyClosing is set by Exit/logout) the window closes normally.
        private NotifyIcon _notifyIcon;
        private ThemedContextMenuStrip _trayMenu;         // shown manually on right-click (see SetupTray) — foreground-safe in every window state
        private Icon _trayIconNormal, _trayIconUnread;   // unread-aware tray icons (loaded from beside the exe; null if missing)

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ── On-screen keyboard (TabTip) detection → shrink the window above it (Part B) ──
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect r);
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        // COMPOSER-input-diagnose: the thread's real Win32 keyboard focus — reveals whether WM_CHAR is being
        // delivered to the inner EDIT (baseTextBox → inserts) or the outer MaterialTextBox2 container (no EDIT
        // → char dropped even though the outer's KeyPress still fires).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
        // COMPOSER-native-input-diagnose v2.1 — message-level facts (mods/sent) + the H1 normalized-resend test,
        // + runtime-state probes (window style / IME context) to close H3'.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InSendMessage();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);   // 32-bit process (Prefer32Bit) → GetWindowLong is correct
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        // NOTE (dead ends, tested on RT — do not re-add): the IMM sever (ImmAssociateContext NULL) was INERT
        // (CUAS attaches per-THREAD, not per-window-IMC); SetInputScope(IS_PASSWORD) did NOT fix the dead mode
        // and made the CUAS grab DETERMINISTIC (13+ consecutive all-dead connections vs. dead↔native flipping
        // before it) — both removed. The productized shim (EditInputProbe) is the mechanism that carries input.
        private const int GWL_STYLE = -16, ES_READONLY = 0x0800;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        // v3 — the activation hypothesis + input-source self-labelling.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct INPUT_MESSAGE_SOURCE { public int deviceType; public int originId; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);

        private Timer _kbTimer;
        private bool _kbActive;
        private bool _kbShrinkActive;     // KBD-RESTORE: spans shrink → restore-complete; capture forbidden while set
        private bool _kbLastShowing;      // for logging the show/hide transition
        private bool _kbHwSuppressed;     // one-shot log guard: OSK detected but a hardware keyboard is attached
        private string _lastOcc = "";     // last raw InputPane OccludedRect (for the [KBD] log)
        private Rectangle _savedBounds;
        private FormWindowState _savedState;
        private int _savedMinH;           // MinimumSize.Height saved while shrunk above the keyboard
        private bool _reallyClosing;
        private bool _isForeground = true;
        private long _lastNotifiedPeerId;

        public MainForm(TelegramService service)
        {
            _service = service;
            _avatars = new AvatarStore(p => _service.DownloadAvatarAsync(p));   // THE avatar pipeline (AVATAR-PIPELINE)
            _avatars.AvatarLoaded += OnAvatarLoaded;

            _skin = MaterialSkinManager.Instance;
            _skin.AddFormToManage(this);
            ApplyTheme();

            // Font scaling only; never Dpi/None. MaterialSkin.2 + system-DPI awareness.
            AutoScaleMode = AutoScaleMode.Font;

            Text = "TelegArm";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { }
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            ApplyPanelColors();
            RestorePhotoCacheIndex();
            // TOUCH-FREEZE: UI-hang confessor — a frozen pump writes its own stack to crash.log.
            try { TelegArm.Helpers.HangWatch.Start(this); } catch { /* diagnostics must never block startup */ }
            StartPresenceEngine();   // PRESENCE: our own online/offline status + dot sweep + group refresh
            StartKeyboardWatch();   // Part B: shrink above the on-screen keyboard (guarded; no-op if unavailable)

            // Drag-and-drop files onto the conversation to send them (Phase 4 Part 2).
            AllowDrop = true;
            DragEnter += MainForm_DragEnter;
            DragDrop += MainForm_DragDrop;
            if (_messagePanel != null)
            {
                _messagePanel.AllowDrop = true;
                _messagePanel.DragEnter += MainForm_DragEnter;
                _messagePanel.DragDrop += MainForm_DragDrop;
            }

            // Tray: a real user close (X) hides to tray instead, once the tray exists.
            FormClosing += MainForm_FormClosing;
            Activated += (s, e) => _isForeground = true;
            Deactivate += (s, e) => _isForeground = false;

            ThemeHelper.StartListening();
            ThemeHelper.ThemeChanged += OnSystemThemeChanged;
            FormClosed += (s, e) =>
            {
                ThemeHelper.ThemeChanged -= OnSystemThemeChanged;
                ThemeHelper.StopListening();
                try { _kbTimer?.Stop(); _kbTimer?.Dispose(); } catch { }
                try { _perfTimer?.Stop(); _perfTimer?.Dispose(); } catch { }
                try { _scrollWatch?.Stop(); _scrollWatch?.Dispose(); } catch { }
                _service.StopConnectionWatchdog();   // stop the liveness timer before teardown
                _service.SaveUpdateState();   // persist pts/qts/seq/date so a restart resumes (gap recovery)
                if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); _notifyIcon = null; }
                if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; }
                AudioPlayer.Shutdown();
                try { _recorder?.Dispose(); } catch { }
                _avatars.AvatarLoaded -= OnAvatarLoaded;
                _avatars.Dispose();   // stops the backfill workers + disposes the cached bitmaps
                foreach (var img in _photoCache.Values) img.Dispose();
                foreach (var img in _photoThumbCache.Values) img.Dispose();
                foreach (var img in _attachmentThumbs) img.Dispose();
                _photoCache.Clear();
                _photoThumbCache.Clear();
                _attachmentThumbs.Clear();
                _pendingBubbles.Clear();
                _photoCachePaths.Clear();
                _currentChatMessages.Clear();
            };
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.F:           // focus search
                    _searchBox.Focus();
                    _searchBox.SelectAll();
                    return true;
                case Keys.Control | Keys.L:           // logout (with confirmation)
                    LogOut();
                    return true;
                case Keys.Control | Keys.W:           // hide to tray (or minimize if no tray yet)
                    HideToTray();
                    return true;
                case Keys.Escape:                     // clear an active search
                    if (!string.IsNullOrEmpty(_searchBox.Text)) { _searchBox.Text = ""; return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ApplyTheme()
        {
            _dark = ThemeHelper.IsDark;   // resolved (System → OS, else the override)
            _accent = ThemeHelper.GetWindowsAccentColor();
            ApplyThemeColors();
        }

        private void ApplyThemeColors()
        {
            _skin.Theme = _dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
            var primary = (Primary)(uint)_accent.ToArgb();
            // Accent = the SAME Windows accent as Primary (cast an arbitrary ARGB into the uint-backed Accent
            // enum, exactly as Primary does) so MaterialSkin's accent surfaces — the text-box focus underline
            // and the floating "Write a message…" / "Search" hint — match the app's purple instead of the
            // default light blue.
            var accent = (Accent)(uint)_accent.ToArgb();
            _skin.ColorScheme = new ColorScheme(
                primary, primary, primary, accent, TextShade.WHITE);
        }

        private void BuildUi()
        {
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 1
            };
            // Add first so docking gives it the form's width, THEN set min sizes and
            // splitter distance — setting SplitterDistance while the control is still
            // at its default 150px width throws (distance outside the valid range).
            Controls.Add(_split);
            _split.Panel1MinSize = 240;
            _split.Panel2MinSize = 360;
            try { _split.SplitterDistance = 300; } catch { /* width too small yet */ }

            BuildLeftPanel();
            BuildRightPanel();
        }

        private void BuildLeftPanel()
        {
            // FOLDER-SIDEBAR: layout style is decided ONCE here (restart-apply). In BOTH modes the chat-list
            // host is created identically and placed in its FINAL parent before its handle exists — no live
            // reparent, so TouchScroller registration, the freeze guards, host buffering, and the
            // CompositedScroll CreateParams all apply exactly as today (Part 0.2 invariants preserved).
            bool sidebar = AppSettings.Instance.FolderSidebar;

            _searchBox = new MaterialTextBox2
            {
                Hint = "Search",
                Dock = DockStyle.Fill,
                Margin = new Padding(2, 12, 10, 6)
            };
            _searchBox.TextChanged += (s, e) => OnSearchTextChanged();

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
            searchRow.Controls.Add(_searchBox, 1, 0);

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

                var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                right.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                right.Controls.Add(searchRow, 0, 0);
                right.Controls.Add(WrapWithScrollbar(_chatListPanel), 0, 1);

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
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                _folderBar = new NoNativeScrollFlowPanel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(8, 1, 8, 1),
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White
                };
                RebuildFolderBar();   // starts with just "All" until folders load

                layout.Controls.Add(searchRow, 0, 0);
                layout.Controls.Add(WrapWithHScrollbar(_folderBar), 0, 1);
                layout.Controls.Add(WrapWithScrollbar(_chatListPanel), 0, 2);
                _split.Panel1.Controls.Add(layout);

                TouchScroller.Enable(_folderBar, horizontal: true);
            }

            TouchScroller.Enable(_chatListPanel, horizontal: false);

            // Chat-list paging (DPI-REVERT addendum): the initial fetch is ONE server page (limit 0 →
            // server default ~100 dialogs) — chats beyond it exist but never render. Page them in near
            // the bottom, from the same three trigger paths the message panel uses (Scroll event,
            // wheel-then-check, TouchScroller.Scrolled — wired in BuildRightPanel's touch handler).
            _chatListPanel.Scroll += (s, e) =>
            {
                if (e.ScrollOrientation == ScrollOrientation.VerticalScroll) CheckChatListPaging();
            };
            _chatListPanel.MouseWheel += (s, e) =>
            {
                // Wheel doesn't reliably raise Scroll — check right after the wheel is applied.
                try { BeginInvoke((Action)CheckChatListPaging); } catch { }
            };
        }

        private void BuildRightPanel()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8
            };
            _rightLayout = layout;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));  // 0 header
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 1 mini player (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 2 pinned bar (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 3 selection bar (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 4 messages
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 5 reply strip (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 6 reply keyboard (toggled)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));  // 7 input

            var topBar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Cursor = Cursors.Hand };
            var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            _chatTitle = new MaterialLabel
            {
                Text = "Select a chat",
                Dock = DockStyle.Fill,
                FontType = MaterialSkinManager.fontType.H6,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            _chatStatus = new MaterialLabel
            {
                Text = "",
                Dock = DockStyle.Fill,
                FontType = MaterialSkinManager.fontType.Caption,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
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
            topBar.Click += (s, e) => OpenSelectedProfile();      // header → profile
            _chatTitle.Click += (s, e) => OpenSelectedProfile();
            _chatStatus.Click += (s, e) => OpenSelectedProfile();

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
                foreach (Control c in _messagePanel.Controls)
                {
                    c.Width = w;
                    (c as MessageBubbleControl)?.Measure();
                }
            };
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
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));   // 5 send

            _botMenuButton = new Button
            {
                Text = "", Anchor = AnchorStyles.None, Width = 44, Height = 38, Visible = false,
                FlatStyle = FlatStyle.Flat, Font = FontHelper.Ui(13f), Cursor = Cursors.Hand,
                BackColor = _dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(225, 225, 228),
                ForeColor = _dark ? Color.FromArgb(225, 225, 228) : Color.FromArgb(40, 40, 44)
            };
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

            _messageInput = new MaterialTextBox2
            {
                Hint = "Write a message…",
                Dock = DockStyle.Fill,
                Margin = new Padding(12, 10, 6, 10),
                Enabled = false
            };
            _messageInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SendPathLog("enter");
                    e.SuppressKeyPress = true;
                    SendCurrentMessage();
                }
            };
            // COMPOSER-full-revert: the composer is now the PLAIN, known-good MaterialTextBox2 — its inner native
            // TextBox is UNTOUCHED (no reflection font-swap, no EmojiInputPainter overpaint), reverted in case any of
            // that manipulation broke WM_CHAR capture on RT. It shows MaterialSkin's default font (not Vazirmatn) and
            // system/monochrome emoji; the rest of the app keeps Vazirmatn. Only the Enter-to-send KeyDown (above) and
            // the [KBD] diagnostics (below) remain wired to it.
            HookKbdDiag(_messageInput, "composer");   // [KBD] focus/keystroke diagnostics (KEEP)
            HookKbdDiag(_searchBox, "search");

            _sendButton = new MaterialButton
            {
                Text = "Send",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 14, 12, 14),
                Type = MaterialButton.MaterialButtonType.Contained,
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
            bottomBar.Controls.Add(_messageInput, 2, 0);
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
            // OWNER-DRAW the strip (MaterialLabel owner-paints its own text — it ignores .Image and forces the
            // MaterialSkin font, so the ↩/✎ glyph vanished + Persian fell back). We draw the Noto glyph + the
            // preview via EmojiRenderer.DrawLine (Vazirmatn Persian + inline Noto emoji) ourselves.
            _replyStrip.Paint += (s, e) => DrawReplyStrip(e.Graphics);
            _replyStrip.Controls.Add(_replyCancelBtn);

            _pinnedBar = new PinnedBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _pinnedBar.BarClicked += (s, e) => OnPinnedBarClicked();
            _pinnedBar.ShowAllClicked += (s, e) => ShowPinnedList();

            layout.Controls.Add(topBar, 0, 0);
            layout.Controls.Add(_miniBar, 0, 1);
            layout.Controls.Add(_pinnedBar, 0, 2);
            layout.Controls.Add(_selectionBar, 0, 3);
            _msgHost = WrapWithScrollbar(_messagePanel);
            layout.Controls.Add(_msgHost, 0, 4);

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

            layout.Controls.Add(_replyStrip, 0, 5);

            // Reply keyboard (bot ReplyKeyboardMarkup) — its own toggled row just above the input.
            _replyKbHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
            _replyKb = new ReplyKeyboardControl { Dock = DockStyle.Fill, IsDark = _dark, AccentColor = _accent };
            _replyKb.ButtonActivated += OnReplyKeyboardButton;
            _replyKb.ToggleChanged += (s, e) => SyncReplyKeyboardHeight();
            _replyKbHost.Controls.Add(_replyKb);
            layout.Controls.Add(_replyKbHost, 0, 6);

            layout.Controls.Add(bottomBar, 0, 7);
            _composerBar = bottomBar;
            // The state-machine footer shares the input row (toggled with the composer, like input↔recording).
            _footerBar = new ComposerFooterBar { Dock = DockStyle.Fill, Visible = false, AccentColor = _accent, IsDark = _dark };
            _footerBar.ActionClicked += (s, e) => OnFooterAction();
            layout.Controls.Add(_footerBar, 0, 7);
            _split.Panel2.Controls.Add(layout);

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

        private void ApplyPanelColors()
        {
            if (_chatListPanel != null)
                _chatListPanel.BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White;
            if (_messagePanel != null)
                _messagePanel.BackColor = _dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            foreach (var sb in _scrollBars)
            {
                sb.IsDark = _dark;
                sb.AccentColor = _accent;
                if (sb.Parent != null) sb.Parent.BackColor = _dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
                sb.Invalidate();
            }
            // SCROLLBTN-REGION: the jump button was themed only at construction — recolor it on live
            // theme flips like every other themed surface (paint reads IsDark/AccentColor per frame).
            if (_jumpBtn != null)
            {
                _jumpBtn.IsDark = _dark;
                _jumpBtn.AccentColor = _accent;
                _jumpBtn.Invalidate();
            }
        }

        private void OnSystemThemeChanged()
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                ApplyTheme();      // re-read system dark/accent
                ApplyPanelColors();
                RefreshThemedControls();
                Invalidate(true);
            }));
        }

        // ── Part B: shrink the window above the on-screen keyboard (TabTip), restore on hide ──

        /// <summary>Starts the guarded poller that shrinks the window above the touch keyboard. Best-effort:
        /// any failure leaves the app full-size (this enhancement must NEVER break the app).</summary>
        private void StartKeyboardWatch()
        {
            try
            {
                Helpers.TouchKeyboard.HasHardwareKeyboard();   // log the initial [KBD] hardware keyboard= verdict at startup
                _kbTimer = new Timer { Interval = 350 };
                _kbTimer.Tick += OnKbTick;
                _kbTimer.Start();
            }
            catch { }
        }

        private void OnKbTick(object sender, EventArgs e)
        {
            try
            {
                if (IsDisposed || WindowState == FormWindowState.Minimized) return;

                // The keyboard matters only when a text field is focused (the reliable "keyboard is up" signal —
                // and it means focus-out is a reliable RESTORE trigger, unlike the flaky keyboard-hide detection).
                bool textFocused = (_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus);
                int kbTop = 0; string via = "none";
                bool showing = textFocused && DetectKeyboard(out kbTop, out via) && kbTop > 0;
                // A physical keyboard (Type Cover / USB) is attached → the user isn't using the on-screen
                // keyboard. Windows still auto-pops TabTip on focus on the original Surface RT (it reports slate
                // mode permanently), but we must NOT shrink the window for it. Suppress (and let the restore
                // branch below un-shrink if we were already shrunk).
                if (showing && Helpers.TouchKeyboard.HasHardwareKeyboard())
                {
                    if (!_kbHwSuppressed)
                    {
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] OSK detected but a HARDWARE keyboard is attached → NOT shrinking (via=" + via + " kbTop=" + kbTop + ")");
                        _kbHwSuppressed = true;
                    }
                    showing = false;
                }
                else _kbHwSuppressed = false;
                if (showing != _kbLastShowing)
                {
                    _kbLastShowing = showing;
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] keyboard " + (showing ? "SHOW" : "HIDE")
                        + (showing ? " via=" + via + " kbTop=" + kbTop + " winTop=" + Top + " winH=" + Height + (_lastOcc.Length > 0 ? " occluded=[" + _lastOcc + "]" : "") : ""));
                    if (showing) LogShowCheck();   // Part 1.5: false-SHOW / flicker verification
                }

                if (!AppSettings.Instance.ResizeForKeyboard)
                {
                    if (_kbActive) RestoreFromKeyboard();   // toggle turned off while shrunk → restore
                    return;
                }

                if (showing && !_kbActive)
                {
                    _kbActive = true;
                    // KBD-RESTORE 1.1 CAPTURE GUARD: _savedBounds may be written ONLY when no shrink/restore is
                    // in flight — a SHOW re-fire during the settle window used to capture the shrunk rect (the
                    // RT "1366x384 state=Maximized") or the OS-re-asserted one ("1366x728" → the 600→728 drift).
                    if (!_kbShrinkActive)
                    {
                        bool wasMax = WindowState == FormWindowState.Maximized;
                        _savedState = WindowState;
                        // Maximized: Windows owns that geometry — save the TRUE normal-state rect
                        // (RestoreBounds), never the current Bounds.
                        _savedBounds = wasMax ? RestoreBounds : Bounds;
                        _savedMinH = MinimumSize.Height;
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] saved bounds=" + _savedBounds.X + "," + _savedBounds.Y
                            + "," + _savedBounds.Width + "x" + _savedBounds.Height + " wasMax=" + wasMax);
                    }
                    else if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] show while shrink active → keeping saved bounds");
                    _kbShrinkActive = true;
                    var wa = Screen.FromControl(this).WorkingArea;
                    int left = Left, width = Width;
                    // 1.2: never leave a Maximized-but-shrunk chimera for the OS to correct — Normal FIRST.
                    if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; left = wa.Left; width = wa.Width; }
                    // FIX 2: the keyboard covers below kbTop; fill from the screen top to just above it. The ctor's
                    // MinimumSize.Height (480) would clamp the shrink (kbTop can leave <480 above), so drop it.
                    int h = Math.Max(160, kbTop - wa.Top - 4);
                    MinimumSize = new Size(MinimumSize.Width, 120);
                    SetBounds(left, wa.Top, width, h);
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] resize→ top=" + wa.Top + " h=" + h + " bottom=" + (wa.Top + h) + " (above kbTop=" + kbTop + ")");
                }
                else if (!showing && _kbActive)
                {
                    RestoreFromKeyboard();
                }
            }
            catch { /* detection/resize is best-effort — never break the app over the keyboard */ }
        }

        private void RestoreFromKeyboard()
        {
            _kbActive = false;
            try
            {
                if (_savedMinH > 0) MinimumSize = new Size(MinimumSize.Width, _savedMinH);   // restore the clamp
                bool wasMax = _savedState == FormWindowState.Maximized;
                // 1.2: wasMax → hand the geometry back to Windows (NO SetBounds — the OS owns maximized).
                if (wasMax) WindowState = FormWindowState.Maximized;
                else { WindowState = FormWindowState.Normal; Bounds = _savedBounds; }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] restore → " + _savedBounds.Width + "x" + _savedBounds.Height + " wasMax=" + wasMax);
            }
            catch { }
            finally { _kbShrinkActive = false; }   // cleared LAST — closes the capture guard window (1.1)
        }

        /// <summary>True when the docked TabTip keyboard occupies the bottom of the screen; outputs its top edge
        /// (the line the app's bottom should sit just above). Conservative — unknown/undocked states → false.</summary>
        private bool KeyboardShowing(out int kbTop)
        {
            kbTop = 0;
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);   // the Win8.1/RT touch-keyboard window class
                if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
                if (!GetWindowRect(h, out NativeRect r)) return false;
                var screen = Screen.FromControl(this).Bounds;
                int height = r.Bottom - r.Top;
                if (height < 150) return false;                    // too small to be the OSK
                if (r.Top >= screen.Bottom - 80) return false;     // dismissed / moved off the bottom
                if (r.Bottom < screen.Bottom - 80) return false;   // not bottom-docked → not the case we handle
                kbTop = r.Top;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Detects the touch keyboard and its top edge (screen Y). Prefers the WinRT InputPane's
        /// OccludedRect (the ACTUAL covered area — correct for the immersive keyboard on RT); falls back to the
        /// legacy TabTip window rect if InputPane is unavailable. Sets <see cref="_lastOcc"/> for the log.</summary>
        private bool DetectKeyboard(out int kbTop, out string via)
        {
            kbTop = 0; via = "none";
            try
            {
                if (WinRtKeyboard.TryInit(Handle) && WinRtKeyboard.TryOccludedRect(out double ox, out double oy, out double ow, out double oh))
                {
                    via = "InputPane";
                    _lastOcc = ox.ToString("0") + "," + oy.ToString("0") + "," + ow.ToString("0") + "," + oh.ToString("0");
                    // OccludedRect is window-relative (borderless MaterialForm → client ≈ window): its top (oy)
                    // is where the keyboard starts within the window → screen kbTop = winTop + oy. RAW logged so
                    // the coordinate space can be confirmed from the RT run.
                    kbTop = Top + (int)Math.Round(oy);
                    return true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[KBD] InputPane read failed: " + ex.GetType().Name + ": " + ex.Message); }

            _lastOcc = "";
            if (KeyboardShowing(out kbTop)) { via = "TabTip"; return true; }
            return false;
        }

        /// <summary>[KBD] flicker diagnostics: log focus GOT/LOST (outer + inner native EDIT) and keystroke
        /// arrival on a composer/search box — so the RT log shows if focus toggles in a loop and whether
        /// keystrokes reach the box with the WinRT keyboard (vs the legacy keyboard that types fine).</summary>
        private void HookKbdDiag(MaterialTextBox2 box, string name)
        {
            if (box == null) return;
            try
            {
                var inner = _baseTextBoxField != null ? _baseTextBoxField.GetValue(box) as Control : null;

                // Per-keystroke / per-focus ECHO tracing — gated behind AppSettings.KbdDiag (checked at event time).
                box.GotFocus += (s, e) => { if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] focus " + name + " GOT"); };
                box.LostFocus += (s, e) => { if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] focus " + name + " LOST"); };
                box.KeyDown += (s, e) => { if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] " + name + " keydown " + e.KeyCode); };
                box.KeyPress += (s, e) =>
                {
                    if (KbdDiag) System.Diagnostics.Debug.WriteLine(
                        "[KBD] " + name + " keypress " + KeyDesc(e.KeyChar)
                        + " Handled=" + e.Handled
                        + " focus=" + FocusWhere(box, inner)
                        + " outer.Text=" + Quote(box.Text)
                        + " inner.Text=" + Quote(inner != null ? inner.Text : "<null>"));
                };
                box.TextChanged += (s, e) =>
                {
                    if (KbdDiag) System.Diagnostics.Debug.WriteLine(
                        "[KBD] " + name + " TextChanged outer.Text=" + Quote(box.Text)
                        + " inner.Text=" + Quote(inner != null ? inner.Text : "<null>"));
                };

                if (inner != null)
                {
                    // Runtime-state snapshot at hook time — one-shot launch breadcrumb (unconditional).
                    var tbState = inner as System.Windows.Forms.TextBoxBase;
                    System.Diagnostics.Debug.WriteLine("[KBD] STATE " + name + "(inner) ReadOnly="
                        + (tbState != null ? tbState.ReadOnly.ToString() : "?") + " Enabled=" + inner.Enabled);
                    Control innerRef = inner;   // capture for the focus closures
                    inner.GotFocus += (s, e) =>
                    {
                        if (KbdDiag)
                        {
                            System.Diagnostics.Debug.WriteLine("[KBD] focus " + name + "(inner) GOT");
                            LogEditStyle(innerRef, name + "(inner)", "focus");
                        }
                        LogImeContext(innerRef, name + "(inner)");   // UNCONDITIONAL — the IMM-context heartbeat at focus
                    };
                    inner.LostFocus += (s, e) => { if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] focus " + name + "(inner) LOST"); };
                    inner.KeyDown += (s, e) => { if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] " + name + "(inner) keydown " + e.KeyCode); };
                    inner.KeyPress += (s, e) =>
                    {
                        if (KbdDiag) System.Diagnostics.Debug.WriteLine(
                            "[KBD] " + name + "(inner) keypress " + KeyDesc(((KeyPressEventArgs)e).KeyChar)
                            + " Handled=" + ((KeyPressEventArgs)e).Handled
                            + " focus=" + FocusWhere(box, inner)
                            + " inner.Text=" + Quote(inner.Text));
                    };
                    inner.TextChanged += (s, e) =>
                    {
                        if (KbdDiag) System.Diagnostics.Debug.WriteLine("[KBD] " + name + "(inner) TextChanged inner.Text=" + Quote(inner.Text));
                    };
                    // The productized input shim (EditInputProbe): tier0-first, synchronous force on dead input,
                    // nav-key force, caret-yank guard. Also applies the IS_PASSWORD input scope per handle.
                    AttachInputProbe(inner, name + "(inner)");
                }
            }
            catch { }
        }

        private readonly System.Collections.Generic.List<NativeWindow> _inputProbes = new System.Collections.Generic.List<NativeWindow>();

        /// <summary>Verbose per-keystroke [KBD] tracing gate: requires diagnostic logging ON (Logger.Enabled)
        /// AND the KbdDiag verbosity tier. "Anomaly" lines (forced/corrected) are gated by Logger.Enabled only.</summary>
        private static bool KbdDiag => Logger.Enabled && AppSettings.Instance.KbdDiag;

        /// <summary>Short alias for the app-wide hot-path logging gate (see Helpers.Logger).</summary>
        private static bool LogOn => Logger.Enabled;

        /// <summary>Subclasses a composer/search INNER EDIT's handle so <see cref="EditInputProbe"/> can shim dead
        /// input (see the class doc). Handle-create aware (re-attaches if the EDIT handle is recreated) and released
        /// on destroy; kept referenced so it isn't GC'd. (The IS_PASSWORD InputScope that was applied here was
        /// removed — see the note at the P/Invoke block.)</summary>
        private void AttachInputProbe(Control inner, string name)
        {
            if (inner == null) return;
            EditInputProbe current = null;
            void Attach()
            {
                if (!inner.IsHandleCreated) return;
                foreach (var p in _inputProbes) if (p.Handle == inner.Handle) return;   // already hooked this handle
                var pr = new EditInputProbe(inner, name, this);
                try { pr.AssignHandle(inner.Handle); _inputProbes.Add(pr); current = pr; }
                catch { }
            }
            if (inner.IsHandleCreated) Attach();
            inner.HandleCreated += (s, e) => Attach();
            inner.HandleDestroyed += (s, e) =>
            {
                if (current != null) { try { current.ReleaseHandle(); } catch { } _inputProbes.Remove(current); current = null; }
            };
        }

        /// <summary>Productized input shim (COMPOSER-SHIM-PRODUCT) for the RT dead-input + caret-yank CUAS bugs.
        /// TIER0-FIRST everywhere: every keystroke goes to base.WndProc; a healthy native edit is SILENT and cheap.
        /// If the EDIT declined (dead mode — text/caret unchanged), the shim forces the edit SYNCHRONOUSLY in place:
        /// printable chars via surrogate-aware SelectedText insert, backspace via a faithful selection-delete,
        /// LEFT/RIGHT/HOME/END/DELETE via direct SelectionStart/Length math (Shift+nav is observed, not forced —
        /// long-press/mouse selection is the supported path). One KeyPress per char, strict FIFO by construction.
        /// CARET-YANK GUARD: _expectedCaret tracks where the caret must be after our last edit/nav; every legit
        /// mover (mouse buttons, EM_SETSEL/EM_REPLACESEL/WM_SETTEXT, focus) traverses this subclass and INVALIDATES
        /// the expectation — so a mismatch at the next keystroke can only be CUAS's message-invisible yank, and is
        /// corrected before the edit. Unconditional log lines mark ONLY anomalies (forced …, caret-yank corrected);
        /// the per-keystroke RAW/echo/EMSG tracing is gated behind AppSettings.KbdDiag.</summary>
        private sealed class EditInputProbe : NativeWindow
        {
            private const int WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
            private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103,
                              WM_UNICHAR = 0x0109, WM_IME_CHAR = 0x0286,
                              WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F;
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204;
            private const int VKL = 0x25, VKR = 0x27, VKHOME = 0x24, VKEND = 0x23, VKDEL = 0x2E;
            private const int EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2, WM_SETTEXT = 0x000C;

            private readonly Control _edit;
            private readonly string _name;
            private readonly Form _owner;         // for the fg=self/other field of the (gated) RAW lines
            private bool _needStyleLog;           // (KbdDiag) log window style at the first tracked message after focus
            private char _pendingHigh;            // buffered high surrogate for force-insert (never insert a lone half)
            private int _expectedCaret = -1;      // caret-yank guard: expected caret after our last edit/nav; -1 = none
            private bool _inSelfEdit;             // reentrancy flag: our OWN programmatic Selection*/SelectedText ops
                                                  // send EM messages through this subclass — they must NOT invalidate
                                                  // the expectation (only genuinely external movers may).

            public EditInputProbe(Control edit, string name, Form owner) { _edit = edit; _name = name; _owner = owner; }

            // Gated write (belt) — hot call sites ALSO wrap with `if (LogOn)` so the argument string is never
            // even built when logging is off (braces — the true hot-path rule).
            private void Log(string s) { if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] " + _name + " " + s); }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                // Legit caret movers we can't predict → drop the yank-guard expectation (trust the actual state).
                // All of them traverse this subclass, which is what makes the yank-mismatch signature sound.
                // _inSelfEdit exempts our own force/correction messages — they end by setting the expectation.
                if (!_inSelfEdit)
                {
                    switch (m.Msg)
                    {
                        case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: case WM_RBUTTONDOWN:
                        case EM_SETSEL: case EM_REPLACESEL: case WM_SETTEXT: case WM_SETFOCUS:
                            _expectedCaret = -1;
                            break;
                    }
                }

                // (KbdDiag) programmatic caret/text manipulation traces — these only ever come from code.
                if (KbdDiag)
                {
                    switch (m.Msg)
                    {
                        case EM_SETSEL: System.Diagnostics.Debug.WriteLine("[KBD] EMSG " + _name + " EM_SETSEL start=" + m.WParam.ToInt64() + " end=" + m.LParam.ToInt64()); break;
                        case EM_REPLACESEL: System.Diagnostics.Debug.WriteLine("[KBD] EMSG " + _name + " EM_REPLACESEL canUndo=" + (m.WParam != IntPtr.Zero) + " text=" + PtrText(m.LParam)); break;
                        case WM_SETTEXT: System.Diagnostics.Debug.WriteLine("[KBD] EMSG " + _name + " WM_SETTEXT text=" + PtrText(m.LParam)); break;
                    }
                }

                // Raw OS-level focus flights: wParam = the OTHER window. UNCONDITIONAL — these mark the invisible
                // keyboard reconnections (tap-away+retap) that decide dead-vs-native, so they anchor every RT read.
                if (m.Msg == WM_SETFOCUS)
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] RAW " + _name + " WM_SETFOCUS otherHwnd=0x" + m.WParam.ToInt64().ToString("X"));
                    _needStyleLog = true;
                    base.WndProc(ref m);
                    return;
                }
                if (m.Msg == WM_KILLFOCUS)
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] RAW " + _name + " WM_KILLFOCUS otherHwnd=0x" + m.WParam.ToInt64().ToString("X"));
                    base.WndProc(ref m);
                    return;
                }

                if (KbdDiag && IsTracked(m.Msg))
                {
                    if (_needStyleLog) { _needStyleLog = false; LogEditStyle(_edit, _name, "firstchar"); }
                    LogRaw(ref m);
                }

                if (m.Msg == WM_KEYDOWN)
                {
                    int vk = (int)(m.WParam.ToInt64() & 0xFFFF);
                    if (vk == VKL || vk == VKR || vk == VKHOME || vk == VKEND || vk == VKDEL) { HandleNavKey(ref m, vk); return; }
                }

                if (IsEditingChar(m, out char ch, out bool isBackspace))
                {
                    HandleEditingChar(ref m, ch, isBackspace);
                    return;
                }

                base.WndProc(ref m);
            }

            private static bool IsTracked(int msg)
            {
                switch (msg)
                {
                    case WM_KEYDOWN: case WM_KEYUP: case WM_CHAR: case WM_DEADCHAR: case WM_UNICHAR:
                    case WM_IME_CHAR: case WM_IME_STARTCOMPOSITION: case WM_IME_ENDCOMPOSITION: case WM_IME_COMPOSITION:
                        return true;
                    default: return false;
                }
            }

            private static bool IsEditingChar(System.Windows.Forms.Message m, out char ch, out bool isBackspace)
            {
                ch = '\0'; isBackspace = false;
                if (m.Msg != WM_CHAR && m.Msg != WM_UNICHAR && m.Msg != WM_IME_CHAR) return false;
                ch = (char)(m.WParam.ToInt64() & 0xFFFF);
                if (m.Msg == WM_CHAR && ch == (char)0x08) { isBackspace = true; return true; }   // backspace
                return ch >= ' ' && ch != (char)0x7F;                                             // printable
            }

            private void LogRaw(ref System.Windows.Forms.Message m)
            {
                uint lp = (uint)(m.LParam.ToInt64() & 0xFFFFFFFF);
                int rep = (int)(lp & 0xFFFF), scan = (int)((lp >> 16) & 0xFF), ext = (int)((lp >> 24) & 1);
                int ctx = (int)((lp >> 29) & 1), prev = (int)((lp >> 30) & 1), up = (int)((lp >> 31) & 1);
                bool self = _owner != null && _owner.IsHandleCreated && GetForegroundWindow() == _owner.Handle;
                System.Diagnostics.Debug.WriteLine("[KBD] RAW " + _name + " " + MsgName(m.Msg) + " " + WParamDesc(m.WParam)
                    + " lParam=0x" + lp.ToString("X8") + " rep=" + rep + " scan=0x" + scan.ToString("X2")
                    + " ext=" + ext + " ctx=" + ctx + " prev=" + prev + " up=" + up
                    + " mods=" + ModSnapshot()
                    + " act=" + (GetActiveWindow() != IntPtr.Zero) + " fg=" + (self ? "self" : "other")
                    + " origin=" + InputOrigin() + " sent=" + InSendMessage());
            }

            /// <summary>The caret-yank guard: if the caret differs from where our last edit/nav left it and NO legit
            /// mover was observed since (they all invalidate the expectation), it can only be CUAS's message-invisible
            /// yank — restore it. A live selection disables the guard (selections are legit state). Catches
            /// yank-to-anywhere (both the →0 and →end variants were observed on RT).</summary>
            private void CorrectYank(System.Windows.Forms.TextBoxBase tb)
            {
                try
                {
                    int exp = _expectedCaret;
                    if (exp >= 0 && tb.SelectionLength == 0 && tb.SelectionStart != exp && exp <= (tb.Text ?? "").Length)
                    {
                        int actual = tb.SelectionStart;
                        _inSelfEdit = true;
                        try { tb.SelectionStart = exp; }
                        finally { _inSelfEdit = false; }
                        if (LogOn) Log("caret-yank corrected " + actual + "->" + exp);   // anomaly line
                    }
                }
                catch { }
            }

            private void HandleEditingChar(ref System.Windows.Forms.Message m, char ch, bool isBackspace)
            {
                var tb = _edit as System.Windows.Forms.TextBoxBase;
                if (tb == null) { base.WndProc(ref m); return; }
                CorrectYank(tb);
                string before = _edit.Text ?? "";
                int caretBefore = tb.SelectionStart;

                base.WndProc(ref m);   // tier 0 — native
                if (!string.Equals(before, _edit.Text ?? "", StringComparison.Ordinal))
                {
                    _expectedCaret = tb.SelectionStart;   // healthy native edit — SILENT
                    if (KbdDiag) Log(EditDesc(ch, isBackspace) + " (tier0 native) caret " + caretBefore + "->" + tb.SelectionStart);
                    return;
                }
                if (isBackspace && caretBefore == 0 && tb.SelectionLength == 0) { _expectedCaret = 0; return; }   // legit no-op — silent

                // Dead mode → RE-CHECK the caret before forcing: the RT fingerprint ("forced Left 3->2" then
                // "forced backspace 2->2" = delete at a yanked position) proved CUAS yanks the caret DURING the
                // dead keystroke's own base.WndProc — i.e., between the entry check above and this point. The
                // pre-force correction makes the forced edit land where the user's caret really was.
                CorrectYank(tb);

                // Force SYNCHRONOUSLY in place (strict FIFO, zero added latency).
                if (isBackspace) ForceBackspace(tb, caretBefore);
                else ForceInsert(tb, ch, caretBefore);
            }

            private void ForceInsert(System.Windows.Forms.TextBoxBase tb, char ch, int caretBefore)
            {
                try
                {
                    if (char.IsHighSurrogate(ch)) { _pendingHigh = ch; return; }   // wait for the low half — never insert a lone surrogate
                    string s;
                    if (char.IsLowSurrogate(ch) && _pendingHigh != '\0') { s = new string(new[] { _pendingHigh, ch }); _pendingHigh = '\0'; }
                    else if (char.IsLowSurrogate(ch)) return;                       // lone low half → drop
                    else s = ch.ToString();
                    _inSelfEdit = true;
                    try { tb.SelectedText = s; }               // replaces a live selection too (standard EDIT behavior)
                    finally { _inSelfEdit = false; }
                    _expectedCaret = tb.SelectionStart;
                    if (LogOn) Log("forced " + KeyDesc(ch) + " caret " + caretBefore + "->" + tb.SelectionStart);   // anomaly line
                }
                catch { }
            }

            private void ForceBackspace(System.Windows.Forms.TextBoxBase tb, int caretBefore)
            {
                try
                {
                    _inSelfEdit = true;
                    try
                    {
                        if (tb.SelectionLength > 0) tb.SelectedText = "";   // backspace with a selection deletes it
                        else
                        {
                            int caret = tb.SelectionStart;
                            if (caret <= 0) { _expectedCaret = 0; return; }
                            string t = tb.Text ?? "";
                            int rmStart = caret - 1, rmLen = 1;   // extend leftward over a surrogate pair — never split one
                            if (rmStart - 1 >= 0 && rmStart < t.Length && char.IsLowSurrogate(t[rmStart]) && char.IsHighSurrogate(t[rmStart - 1])) { rmStart -= 1; rmLen = 2; }
                            tb.SelectionStart = rmStart; tb.SelectionLength = rmLen; tb.SelectedText = "";
                        }
                    }
                    finally { _inSelfEdit = false; }
                    _expectedCaret = tb.SelectionStart;
                    if (LogOn) Log("forced backspace caret " + caretBefore + "->" + tb.SelectionStart);   // anomaly line
                }
                catch { }
            }

            private void HandleNavKey(ref System.Windows.Forms.Message m, int vk)
            {
                var tb = _edit as System.Windows.Forms.TextBoxBase;
                if (tb == null) { base.WndProc(ref m); return; }

                // Shift+nav = selection intent — do NOT force selection math; observe and drop the expectation
                // (a selection may now exist). Long-press/mouse selection remains the supported path.
                if ((GetKeyState(VK_SHIFT) & 0x8000) != 0)
                {
                    base.WndProc(ref m);
                    _expectedCaret = -1;
                    Log("shift-nav observed (not forced)");   // unconditional
                    return;
                }

                CorrectYank(tb);
                string before = tb.Text ?? "";
                int s0 = tb.SelectionStart, l0 = tb.SelectionLength;

                base.WndProc(ref m);   // tier 0 — native
                int s1 = tb.SelectionStart, l1 = tb.SelectionLength;
                if (s0 != s1 || l0 != l1 || !string.Equals(before, tb.Text ?? "", StringComparison.Ordinal))
                {
                    _expectedCaret = tb.SelectionStart;   // healthy native nav/edit — SILENT
                    if (KbdDiag) Log("nav " + NavName(vk) + " (tier0 native) caret " + s0 + "->" + s1);
                    return;
                }

                int len = (tb.Text ?? "").Length;
                bool noop =                                     // boundary no-ops — silent, nothing to force
                    (vk == VKL && s0 == 0 && l0 == 0) ||
                    (vk == VKR && s0 == len && l0 == 0) ||
                    (vk == VKHOME && s0 == 0 && l0 == 0) ||
                    (vk == VKEND && s0 == len && l0 == 0) ||
                    (vk == VKDEL && ((s0 == len && l0 == 0) || len == 0));
                if (noop) { _expectedCaret = s0; return; }

                // Dead mode → force the nav/edit directly (under the self-edit flag so our own EM messages
                // don't invalidate the expectation; it's set to the final position right after).
                try
                {
                    _inSelfEdit = true;
                    try
                    {
                        switch (vk)
                        {
                            case VKL:
                                if (l0 > 0) { tb.SelectionStart = s0; tb.SelectionLength = 0; }          // collapse left
                                else tb.SelectionStart = s0 - 1;
                                break;
                            case VKR:
                                if (l0 > 0) { tb.SelectionStart = s0 + l0; tb.SelectionLength = 0; }     // collapse right
                                else tb.SelectionStart = s0 + 1;
                                break;
                            case VKHOME: tb.SelectionStart = 0; tb.SelectionLength = 0; break;
                            case VKEND: tb.SelectionStart = len; tb.SelectionLength = 0; break;
                            case VKDEL:
                                if (l0 > 0) tb.SelectedText = "";                                        // delete the selection
                                else
                                {
                                    string t = tb.Text ?? "";
                                    int rmLen = 1;   // forward delete — never split a surrogate pair
                                    if (s0 + 1 < t.Length && char.IsHighSurrogate(t[s0]) && char.IsLowSurrogate(t[s0 + 1])) rmLen = 2;
                                    tb.SelectionStart = s0; tb.SelectionLength = rmLen; tb.SelectedText = "";
                                }
                                break;
                        }
                    }
                    finally { _inSelfEdit = false; }
                    _expectedCaret = tb.SelectionStart;
                    if (LogOn) Log("forced " + NavName(vk) + " caret " + s0 + "->" + tb.SelectionStart);   // anomaly line
                }
                catch { }
            }

            private static string NavName(int vk)
            {
                switch (vk)
                {
                    case VKL: return "Left";
                    case VKR: return "Right";
                    case VKHOME: return "Home";
                    case VKEND: return "End";
                    default: return "Delete";
                }
            }

            private static string EditDesc(char ch, bool isBackspace) => isBackspace ? "backspace" : KeyDesc(ch);

            /// <summary>First 16 chars of a Unicode message-payload pointer (EM_REPLACESEL/WM_SETTEXT), guarded.</summary>
            private static string PtrText(IntPtr p)
            {
                try
                {
                    if (p == IntPtr.Zero) return "''";
                    string s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? "";
                    if (s.Length > 16) s = s.Substring(0, 16) + "…";
                    return "'" + s + "'";
                }
                catch { return "'<?>'"; }
            }
        }

        /// <summary>Message-name label for the [KBD] RAW line.</summary>
        private static string MsgName(int msg)
        {
            switch (msg)
            {
                case 0x0100: return "WM_KEYDOWN";
                case 0x0101: return "WM_KEYUP";
                case 0x0102: return "WM_CHAR";
                case 0x0103: return "WM_DEADCHAR";
                case 0x0109: return "WM_UNICHAR";
                case 0x0286: return "WM_IME_CHAR";
                case 0x010D: return "WM_IME_STARTCOMPOSITION";
                case 0x010E: return "WM_IME_ENDCOMPOSITION";
                case 0x010F: return "WM_IME_COMPOSITION";
                default: return "0x" + msg.ToString("X");
            }
        }

        /// <summary>wParam as hex, with the printable char if its low word is one — "'h'(0x68)" or "(0x08)".</summary>
        private static string WParamDesc(IntPtr wParam)
        {
            int w = (int)(wParam.ToInt64() & 0xFFFF);
            char c = (char)w;
            return (c >= ' ' && c < (char)0x7F) ? "'" + c + "'(0x" + w.ToString("X2") + ")" : "(0x" + w.ToString("X2") + ")";
        }

        /// <summary>Modifier snapshot from GetKeyState at message time: S/C/A/W, uppercase = down (e.g. "-C--").</summary>
        private static string ModSnapshot()
        {
            bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;
            return (Down(VK_SHIFT) ? "S" : "-") + (Down(VK_CONTROL) ? "C" : "-")
                 + (Down(VK_MENU) ? "A" : "-") + ((Down(VK_LWIN) || Down(VK_RWIN)) ? "W" : "-");
        }

        /// <summary>Logs the EDIT's window style + ES_READONLY flag (H3'/persistent-state check).</summary>
        private static void LogEditStyle(Control edit, string name, string when)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting when logging is off
                if (edit == null || !edit.IsHandleCreated) return;
                int style = GetWindowLong(edit.Handle, GWL_STYLE);
                System.Diagnostics.Debug.WriteLine("[KBD] STYLE " + name + " [" + when + "] style=0x" + style.ToString("X8")
                    + " ES_READONLY=" + (((style & ES_READONLY) != 0) ? "yes" : "no") + " Enabled=" + edit.Enabled);
            }
            catch { }
        }

        /// <summary>Input-source label from GetCurrentInputMessageSource (Win8+): which keyboard produced the
        /// current message — HARDWARE / INJECTED / SYSTEM (IMO_* originId), or n/a if unavailable.</summary>
        private static string InputOrigin()
        {
            try
            {
                if (GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE src))
                {
                    switch (src.originId)   // IMO_HARDWARE=1, IMO_INJECTED=2, IMO_SYSTEM=4
                    {
                        case 1: return "HARDWARE";
                        case 2: return "INJECTED";
                        case 4: return "SYSTEM";
                    }
                }
            }
            catch { }
            return "n/a";
        }

        /// <summary>Part 1.5: at the moment the TabTip poll flips to SHOW, snapshot the TabTip window's visibility
        /// + rect and the current foreground window class — to catch a false-SHOW / flicker (observation only).</summary>
        private void LogShowCheck()
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting (or window probing) when off
                IntPtr tt = FindWindow("IPTip_Main_Window", null);
                bool vis = tt != IntPtr.Zero && IsWindowVisible(tt);
                NativeRect r = new NativeRect();
                if (tt != IntPtr.Zero) GetWindowRect(tt, out r);
                IntPtr fg = GetForegroundWindow();
                var sb = new System.Text.StringBuilder(96);
                if (fg != IntPtr.Zero) GetClassName(fg, sb, sb.Capacity);
                System.Diagnostics.Debug.WriteLine("[KBD] SHOW-check tabtipVis=" + vis
                    + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " fg=" + sb.ToString());
            }
            catch { }
        }

        /// <summary>Logs whether an IME context is attached to the EDIT (immersive keyboard on-show diversion — H3').</summary>
        private static void LogImeContext(Control edit, string name)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting (or IMM calls) when logging is off
                if (edit == null || !edit.IsHandleCreated) return;
                IntPtr himc = ImmGetContext(edit.Handle);
                System.Diagnostics.Debug.WriteLine("[KBD] IME " + name + " imeCtx=" + (himc == IntPtr.Zero ? "null" : "0x" + himc.ToInt64().ToString("X")));
                if (himc != IntPtr.Zero) ImmReleaseContext(edit.Handle, himc);
            }
            catch { }
        }

        /// <summary>[KBD] SEND truth: logs the composer's outer/inner text length + preview from a send entry point,
        /// so an empty send (text-capture bug) is distinguishable and a missing "button" line pinpoints a hit-test bug.</summary>
        private void SendPathLog(string via)
        {
            try
            {
                if (!Logger.Enabled) return;   // hot-path rule: no log formatting when logging is off
                string outer = _messageInput != null ? (_messageInput.Text ?? "") : "";
                var inner = (_messageInput != null && _baseTextBoxField != null) ? _baseTextBoxField.GetValue(_messageInput) as Control : null;
                string innerT = inner != null ? (inner.Text ?? "") : "";
                System.Diagnostics.Debug.WriteLine("[KBD] SEND via=" + via + " outer.len=" + outer.Length
                    + " inner.len=" + innerT.Length + " text=" + Quote(outer));
            }
            catch { }
        }

        /// <summary>'h' for a printable char, "#13" for a control char — compact keypress label for the [KBD] log.</summary>
        private static string KeyDesc(char c) => char.IsControl(c) ? "#" + (int)c : "'" + c + "'";

        /// <summary>Short single-quoted preview of a text buffer (truncated) for the [KBD] log — so an empty box
        /// reads as '' and a filled one shows its content.</summary>
        private static string Quote(string t)
        {
            if (t == null) return "<null>";
            if (t.Length > 24) t = t.Substring(0, 24) + "…";
            return "'" + t + "'";
        }

        /// <summary>Names which HWND currently holds the thread's Win32 keyboard focus, relative to a composer:
        /// "inner" (the real EDIT — WM_CHAR inserts), "outer" (the MaterialTextBox2 container — WM_CHAR is dropped),
        /// or "other/0x…" (something else). This is the crux of the char-not-captured diagnosis.</summary>
        private static string FocusWhere(Control outer, Control inner)
        {
            try
            {
                IntPtr f = GetFocus();
                if (inner != null && inner.IsHandleCreated && f == inner.Handle) return "inner";
                if (outer != null && outer.IsHandleCreated && f == outer.Handle) return "outer";
                return "other(0x" + f.ToInt64().ToString("X") + ")";
            }
            catch { return "?"; }
        }

        /// <summary>After a NON-click reactivation (WA_ACTIVE) WinForms auto-restores focus to the last text
        /// field (composer/search); Windows then auto-reshows the touch keyboard for it → the close→reshow loop
        /// (and the keyboard popping up at startup). Clearing that auto-focus keeps the keyboard closed unless
        /// the USER taps a text field. Runs deferred (BeginInvoke) so it's after WinForms' focus restoration.</summary>
        private void ClearAutoRefocusedTextField()
        {
            try
            {
                if (IsDisposed) return;
                if ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus))
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] cleared auto-refocused text field after WA_ACTIVE (no keyboard auto-reshow)");
                    ActiveControl = null;   // established pattern in this form; removes focus → the OSK won't auto-reshow
                }
            }
            catch { }
        }

        /// <summary>True when a WM_ACTIVATEAPP=False is the touch keyboard STEALING activation (we're still the
        /// real foreground app) rather than the app going to background — so the WebM pause/flicker churn is
        /// skipped. Signals: a composer/search text field still has focus (the steal fires before focus is
        /// lost), OR the new foreground is ours / the keyboard / an immersive input surface. A genuine app
        /// switch (another process, a normal window) → false (real deactivation, pause as before).</summary>
        private bool IsSpuriousDeactivation()
        {
            try
            {
                if ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus)) return true;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return true;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == GetCurrentProcessId()) return true;   // a window of our OWN process (menu/child/popup)
                var sb = new System.Text.StringBuilder(96);
                GetClassName(fg, sb, sb.Capacity);
                string cls = sb.ToString();
                if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] deactivation fg-class=" + cls);
                if (cls.IndexOf("IPTip", StringComparison.OrdinalIgnoreCase) >= 0                    // legacy TabTip
                    || cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)  // immersive input surface
                    || cls.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
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

        /// <summary>Pushes the current accent/dark state into the custom-painted controls.</summary>
        private void RefreshThemedControls()
        {
            if (_hamburger != null) { _hamburger.ForeColor = _accent; _hamburger.Invalidate(); }   // repaint the drawn icon with the current accent
            if (_attachButton != null) _attachButton.Invalidate(); // self-themes from ThemeHelper
            if (_footerBar != null) { _footerBar.AccentColor = _accent; _footerBar.IsDark = _dark; _footerBar.Invalidate(); }
            if (_miniBar != null) { _miniBar.AccentColor = _accent; _miniBar.IsDark = _dark; _miniBar.Invalidate(); }
            if (_pinnedBar != null) { _pinnedBar.AccentColor = _accent; _pinnedBar.IsDark = _dark; _pinnedBar.Invalidate(); }
            if (_replyTarget != null) UpdateReplyStrip(); // re-color the visible reply strip
            ThemeSelectionBar();

            // UI-FIX-T1: the ctor-only color sites (UI-AUDIT A.3) — re-push/re-derive on a live theme switch.
            if (_folderBar != null)
            {
                _folderBar.BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White;
                RebuildFolderBar();   // tab colors derive from _dark/_accent at build → rebuilding IS the recolor
            }
            if (_folderRail != null) RebuildFolderRail();   // FOLDER-SIDEBAR: rebuild IS the recolor (BackColor + item colors)
            if (_botMenuButton != null)
            {
                _botMenuButton.BackColor = _dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(225, 225, 228);
                _botMenuButton.ForeColor = _dark ? Color.FromArgb(225, 225, 228) : Color.FromArgb(40, 40, 44);
                _botMenuButton.Invalidate();
            }
            RecolorConnectingUi();
            if (_switchOverlay != null)
            {
                _switchOverlay.BackColor = _dark ? Color.FromArgb(26, 26, 29) : Color.FromArgb(244, 244, 247);
                if (_switchOverlay.Visible) _switchOverlay.Invalidate();   // title text derives at paint time
            }
            UpdateHeaderStatus();   // status colors derive at invocation — recolors NOW, not on the next presence update

            foreach (Control c in _chatListPanel.Controls)
            {
                if (c is ChatListItemControl item) { item.AccentColor = _accent; item.IsDark = _dark; item.Invalidate(); }
                else if (c is Label lbl)   // search section headers (AddSectionHeader) — ctor-only ForeColor
                    lbl.ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(120, 120, 125);
            }
            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.AccentColor = _accent; b.IsDark = _dark; b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.AccentColor = _accent; v.IsDark = _dark; v.Invalidate(); }
            }
        }

        // ── Hamburger menu ───────────────────────────────────────────────────

        private DrawerMenu _drawer;

        /// <summary>Opens the Telegram-style left drawer (account header + full menu + Night Mode toggle).</summary>
        private void ShowDrawer()
        {
            if (_drawer != null) { CloseDrawer(); return; }

            // Snapshot the current window for a dimmed backdrop (best-effort).
            Bitmap snap = null;
            try { snap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)); DrawToBitmap(snap, new Rectangle(Point.Empty, ClientSize)); }
            catch { snap = null; }

            var me = _service.Me;
            string name = me != null ? string.Join(" ", new[] { me.first_name, me.last_name }).Trim() : "TelegArm";

            var rows = new List<DrawerMenu.Row>();
            // Account switcher: the ACTIVE account is the header above — list only the OTHERS (tap to switch),
            // so the same account is never shown twice. De-duped by id (defensive; folder names are already ids).
            var seen = new HashSet<long>();
            foreach (var acc in AccountStore.ListAccounts())
            {
                if (acc.Id == AccountContext.ActiveId || !seen.Add(acc.Id)) continue;
                long accId = acc.Id; string accName = acc.Name;
                rows.Add(new DrawerMenu.Row
                {
                    IsAccount = true, Label = acc.Name, AvatarKey = acc.Id, Avatar = LoadAccountAvatar(acc),
                    Action = Wrap(() => SwitchAccount(accId, accName))
                });
            }
            rows.Add(Row("➕", "Add Account", AddAccount));
            rows.Add(Sep());
            rows.Add(Row("👤", "My Profile", ShowProfile));
            rows.Add(Row("👥", "New Group", NewGroup));
            rows.Add(Row("📢", "New Channel", NewChannel));
            rows.Add(Row("📇", "Contacts", OpenContacts));
            rows.Add(Row("📞", "Calls", () => ComingSoon("Calls")));
            rows.Add(Row("🔖", "Saved Messages", () => OpenSavedMessages()));
            rows.Add(Sep());
            rows.Add(Row("⚙", "Settings", OpenSettings));
            rows.Add(Toggle("🌙", "Night Mode", () => ThemeHelper.IsDark, ToggleNightMode));
            rows.Add(Sep());
            rows.Add(Row("ℹ", "About TelegArm", ShowAbout));   // "Sticker engine…" moved into Settings → General
            rows.Add(Row("📣", "TelegArm Channel", OpenTelegArmChannel));   // opens the official channel in the chat view
            rows.Add(Sep());
            rows.Add(Danger("🚪", "Log out" + (me != null ? " (" + name + ")" : ""), LogOut));   // removes the ACTIVE account
            string letter = !string.IsNullOrEmpty(name) ? name.Substring(0, 1).ToUpper() : "?";
            var av = _avatars.GetCached(me?.id ?? 0);

            _drawer = new DrawerMenu(snap, _dark, _accent, name, letter, av, me?.id ?? 0, rows, Wrap(ShowProfile))
            {
                Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)
            };
            _drawer.CloseRequested += () => BeginInvoke((Action)CloseDrawer);
            Controls.Add(_drawer);
            _drawer.BringToFront();
            _drawer.Focus();
        }

        private void CloseDrawer()
        {
            var d = _drawer;
            if (d == null) return;
            _drawer = null;
            try { Controls.Remove(d); d.Dispose(); } catch { }
        }

        /// <summary>Owner-draws the reply/edit strip: the Noto ↩/✏️ glyph + the preview (Vazirmatn for Persian,
        /// inline Noto emoji). MaterialLabel couldn't — it ignores .Image and forces the MaterialSkin font.</summary>
        private void DrawReplyStrip(Graphics g)
        {
            if (_replyStrip == null) return;
            int h = _replyStrip.Height;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            int textX = 14;
            var glyph = EmojiRenderer.GetScaled(_replyEditing ? "✏️" : "↩️", 22);
            if (glyph != null) { g.DrawImage(glyph, 12, (h - 22) / 2, 22, 22); textX = 42; }
            int rightPad = (_replyCancelBtn != null ? _replyCancelBtn.Width : 44) + 8;
            var rect = new Rectangle(textX, 0, Math.Max(0, _replyStrip.Width - textX - rightPad), h);
            Color fg = _dark ? Color.FromArgb(220, 220, 220) : Color.FromArgb(40, 40, 40);
            string text = _replyPreview ?? "";
            using (var f = FontHelper.For(text, 9.5f))
                EmojiRenderer.DrawLine(g, text, f, fg, rect);
        }

        // Drawer row builders: each action closes the drawer first, then runs deferred.
        private DrawerMenu.Row Row(string glyph, string label, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, Action = Wrap(action) };
        private DrawerMenu.Row Danger(string glyph, string label, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, IsDanger = true, Action = Wrap(action) };
        private DrawerMenu.Row Toggle(string glyph, string label, Func<bool> isOn, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, IsToggle = true, IsOn = isOn, Action = Wrap(action) };
        private static DrawerMenu.Row Sep() => new DrawerMenu.Row { Separator = true };

        // Defer to the next message-loop tick so the drawer isn't removed/disposed mid mouse-event.
        private Action Wrap(Action action) => () => BeginInvoke((Action)(() => { CloseDrawer(); action?.Invoke(); }));

        private void ComingSoon(string what)
            => ThemedDialog.Show(this, what, what + " isn't implemented yet — coming soon.", "OK");

        // ── Contacts / New Group / New Channel (shared people picker; all network ops bounded in the service) ──

        private async void OpenContacts()
        {
            using (var f = new PeoplePickerForm(() => _service.GetContactsAsync(), false, _dark, _accent, "Contacts", GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.SelectedUser == null) return;
                var u = f.SelectedUser;
                System.Diagnostics.Debug.WriteLine("[PEOPLE] contacts → open chat with " + u.id);
                await OpenChat(new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u), IsGroup = false }, 0);
            }
        }

        private async void NewGroup()
        {
            using (var f = new CreateChatForm(CreateChatForm.Kind.Group, () => _service.GetContactsAsync(), _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Channel ch;
                try { ch = await _service.CreateSupergroupAsync(f.ChatTitle, ""); }
                catch (Exception ex) { ThemedDialog.Show(this, "New Group", "Couldn't create the group:\n" + ex.Message, "OK"); return; }
                if (ch == null) { ThemedDialog.Show(this, "New Group", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
                if (f.Members.Count > 0) await _service.InviteToChannelAsync(ch, f.Members);
                System.Diagnostics.Debug.WriteLine("[PEOPLE] open new supergroup id=" + ch.id);
                await OpenChat(new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.title, IsGroup = true }, 0);
            }
        }

        private async void NewChannel()
        {
            using (var f = new CreateChatForm(CreateChatForm.Kind.Channel, () => _service.GetContactsAsync(), _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Channel ch;
                try { ch = await _service.CreateBroadcastAsync(f.ChatTitle, f.ChatAbout); }
                catch (Exception ex) { ThemedDialog.Show(this, "New Channel", "Couldn't create the channel:\n" + ex.Message, "OK"); return; }
                if (ch == null) { ThemedDialog.Show(this, "New Channel", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
                if (f.Members.Count > 0) await _service.InviteToChannelAsync(ch, f.Members);
                System.Diagnostics.Debug.WriteLine("[PEOPLE] open new channel id=" + ch.id);
                await OpenChat(new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.title, IsGroup = false }, 0);
            }
        }

        private async void OpenSavedMessages()
        {
            var me = _service.Me;
            if (me == null) return;
            var entry = new ChatEntry
            {
                Peer = new InputPeerUser(me.id, me.access_hash),
                PeerId = me.id,
                Title = "Saved Messages",
                PeerInfo = me
            };
            await OpenChat(entry, 0);
        }

        // Night Mode pill: explicit Light/Dark (moves off System). Persists via SetThemeMode
        // (ThemeMode + Save) so it can't desync from the Settings → Appearance picker across restart.
        private void ToggleNightMode() => SetThemeMode(ThemeHelper.IsDark ? ThemeMode.Light : ThemeMode.Dark);

        private void AddMenuItem(ContextMenuStrip menu, string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => BeginInvoke(action);
            menu.Items.Add(item);
        }

        // ── Owner-painted left drawer (account header + menu + Night Mode toggle) ──
        private sealed class DrawerMenu : Control
        {
            public sealed class Row
            {
                public string Glyph, Label;
                public Action Action;
                public bool IsDanger, IsToggle, Separator;
                public Func<bool> IsOn;
                // Account-switcher rows (the OTHER accounts; tap to switch): an avatar or a letter from AvatarKey.
                public bool IsAccount;
                public Image Avatar; public long AvatarKey;
            }

            private const int CardW = 300, HeaderH = 92, RowH = 46, SepH = 11;

            private readonly Bitmap _snap;
            private readonly bool _dark;
            private readonly Color _accent;
            private readonly string _name, _letter;
            private readonly Image _avatar;
            private readonly long _avatarKey;
            private readonly List<Row> _rows;
            private readonly Action _headerAction;
            private readonly Rectangle[] _rects;
            private int _hover = -2;           // -2 none, -1 header, >=0 row index
            private Rectangle _headerRect;
            private int _scrollY, _contentH;   // rows scroll under the fixed header (many accounts overflow the screen)
            private bool _pressed, _moved; private int _pressY, _pressScroll;

            public event Action CloseRequested;

            public DrawerMenu(Bitmap snap, bool dark, Color accent, string name, string letter,
                              Image avatar, long avatarKey, List<Row> rows, Action headerAction)
            {
                _snap = snap; _dark = dark; _accent = accent; _name = name; _letter = letter;
                _avatar = avatar; _avatarKey = avatarKey; _rows = rows; _headerAction = headerAction;
                _rects = new Rectangle[rows.Count];
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                TabStop = false;
                Layout2();
            }

            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Layout2(); Invalidate(); }

            private void Layout2()
            {
                _headerRect = new Rectangle(0, 0, CardW, HeaderH);
                int y = HeaderH + 6;
                for (int i = 0; i < _rows.Count; i++)
                {
                    int h = _rows[i].Separator ? SepH : RowH;
                    _rects[i] = new Rectangle(0, y, CardW, h);
                    y += h;
                }
                _contentH = y + 6;
                _scrollY = Math.Min(_scrollY, MaxScroll());
            }

            private int MaxScroll() { return Math.Max(0, _contentH - Height); }
            private int ClampScroll(int v) { return Math.Max(0, Math.Min(v, MaxScroll())); }

            protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { CloseRequested?.Invoke(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_pressed)   // touch/mouse drag-scroll over the rows
                {
                    if (Math.Abs(e.Y - _pressY) > 6) _moved = true;
                    if (_moved) { _scrollY = ClampScroll(_pressScroll - (e.Y - _pressY)); Invalidate(); }
                    return;
                }
                int h = HitTest(e.Location);
                if (h != _hover) { _hover = h; Cursor = h != -2 ? Cursors.Hand : Cursors.Default; Invalidate(new Rectangle(0, 0, CardW, Height)); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X > CardW) { CloseRequested?.Invoke(); return; }
                _pressed = true; _moved = false; _pressY = e.Y; _pressScroll = _scrollY;   // action fires on MouseUp if not dragged
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool wasPressed = _pressed, moved = _moved;
                _pressed = false; _moved = false;
                if (!wasPressed || moved || e.X > CardW) return;   // a drag-scroll, not a tap
                int h = HitTest(e.Location);
                if (h == -1) { _headerAction?.Invoke(); return; }
                if (h >= 0 && !_rows[h].Separator) _rows[h].Action?.Invoke();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                int ns = ClampScroll(_scrollY - Math.Sign(e.Delta) * RowH);
                if (ns != _scrollY) { _scrollY = ns; _hover = -2; Invalidate(); }
            }

            private int HitTest(Point p)
            {
                if (p.X > CardW) return -2;
                if (p.Y < HeaderH) return _headerRect.Contains(p) ? -1 : -2;   // header is fixed (not scrolled)
                int py = p.Y + _scrollY;   // rows scroll
                for (int i = 0; i < _rows.Count; i++)
                    if (!_rows[i].Separator && _rects[i].Contains(new Point(p.X, py))) return i;
                return -2;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                if (_snap != null) g.DrawImage(_snap, 0, 0);
                else using (var b = new SolidBrush(_dark ? Color.FromArgb(20, 20, 22) : Color.FromArgb(60, 60, 64))) g.FillRectangle(b, ClientRectangle);

                using (var dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                    g.FillRectangle(dim, ClientRectangle);

                // Card.
                using (var cb = new SolidBrush(_dark ? Color.FromArgb(36, 36, 39) : Color.FromArgb(250, 250, 252)))
                    g.FillRectangle(cb, 0, 0, CardW, Height);

                // Header: avatar + name + subtitle.
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var avr = new Rectangle(20, 22, 52, 52);
                if (_avatar != null)
                {
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(_avatar, avr); g.ResetClip(); }
                }
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_avatarKey))) g.FillEllipse(b, avr);
                    using (var f = new Font("Segoe UI", 18f, FontStyle.Bold))
                        TextRenderer.DrawText(g, _letter, f, avr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (_hover == -1)
                    using (var hb = new SolidBrush(Color.FromArgb(_dark ? 22 : 18, _accent))) g.FillRectangle(hb, _headerRect);
                using (var nf = FontHelper.Ui(11.5f))
                    TextRenderer.DrawText(g, _name, nf, new Rectangle(86, 30, CardW - 96, 24),
                        _dark ? Color.White : Color.FromArgb(25, 25, 28), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                using (var sf = FontHelper.Ui(8.5f))
                    TextRenderer.DrawText(g, "View profile", sf, new Rectangle(86, 54, CardW - 96, 18),
                        _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(135, 135, 140), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                // Rows (scroll under the fixed header).
                var rowsClip = g.Clip;
                g.SetClip(new Rectangle(0, HeaderH, CardW, Math.Max(0, Height - HeaderH)));
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    var r = new Rectangle(_rects[i].X, _rects[i].Y - _scrollY, _rects[i].Width, _rects[i].Height);
                    if (r.Bottom < HeaderH || r.Y > Height) continue;   // scrolled offscreen
                    if (row.Separator)
                    {
                        using (var p = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(232, 232, 236)))
                            g.DrawLine(p, 16, r.Y + r.Height / 2, CardW - 16, r.Y + r.Height / 2);
                        continue;
                    }
                    if (_hover == i)
                        using (var hb = new SolidBrush(_dark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(237, 240, 244)))
                            g.FillRectangle(hb, r);

                    if (row.IsAccount)
                    {
                        var accAvr = new Rectangle(16, r.Y + (r.Height - 30) / 2, 30, 30);
                        if (row.Avatar != null)
                        {
                            using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(accAvr); var pc = g.Clip; g.SetClip(clip); g.DrawImage(row.Avatar, accAvr); g.Clip = pc; pc.Dispose(); }
                        }
                        else
                        {
                            using (var b = new SolidBrush(DrawHelper.AvatarColor(row.AvatarKey))) g.FillEllipse(b, accAvr);
                            string ltr = !string.IsNullOrEmpty(row.Label) ? row.Label.Substring(0, 1).ToUpper() : "?";
                            using (var f = FontHelper.Ui(11f, FontStyle.Bold))
                                TextRenderer.DrawText(g, ltr, f, accAvr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        }
                        using (var lf = FontHelper.Ui(10.5f))
                            TextRenderer.DrawText(g, row.Label, lf, new Rectangle(56, r.Y, CardW - 60, r.Height),
                                _dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(35, 35, 38),
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        continue;
                    }

                    Color danger = Color.FromArgb(222, 74, 74);
                    Color glyphC = row.IsDanger ? danger : _accent;
                    var gr = new Rectangle(16, r.Y, 30, r.Height);
                    // Render the glyph via the Noto emoji pack (consistent + crisp on RT). The old font-char
                    // path substituted an UGLY system emoji glyph on RT (like the hamburger did). Fall back to
                    // the font glyph only if Noto lacks this emoji.
                    var glyphImg = EmojiRenderer.Get(row.Glyph);
                    if (glyphImg != null)
                    {
                        const int gsz = 22;
                        var oldIM = g.InterpolationMode;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(glyphImg, new Rectangle(gr.X + (gr.Width - gsz) / 2, gr.Y + (gr.Height - gsz) / 2, gsz, gsz));
                        g.InterpolationMode = oldIM;
                    }
                    else using (var gf = FontHelper.Ui(12f))
                        TextRenderer.DrawText(g, row.Glyph, gf, gr, glyphC, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    Color lc = row.IsDanger ? danger : (_dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(35, 35, 38));
                    using (var lf = FontHelper.Ui(10.5f))
                        TextRenderer.DrawText(g, row.Label, lf, new Rectangle(56, r.Y, CardW - 110, r.Height), lc,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                    if (row.IsToggle)
                        DrawToggle(g, new Rectangle(CardW - 64, r.Y + (r.Height - 20) / 2, 40, 20), row.IsOn != null && row.IsOn());
                }
                g.Clip = rowsClip; rowsClip.Dispose();

                int maxS = MaxScroll();
                if (maxS > 0 && _contentH > 0)   // thin scroll indicator
                {
                    int track = Math.Max(1, Height - HeaderH);
                    int thumbH = Math.Max(30, track * track / _contentH);
                    int thumbY = HeaderH + (int)((track - thumbH) * (_scrollY / (float)maxS));
                    using (var tb = new SolidBrush(Color.FromArgb(110, _dark ? Color.White : Color.Black)))
                        g.FillRectangle(tb, CardW - 5, thumbY, 3, thumbH);
                }
            }

            private void DrawToggle(Graphics g, Rectangle r, bool on)
            {
                using (var track = new SolidBrush(on ? _accent : (_dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(200, 200, 206))))
                using (var p = DrawHelper.RoundedRect(r, r.Height / 2))
                    g.FillPath(track, p);
                int d = r.Height - 6;
                int kx = on ? r.Right - d - 3 : r.X + 3;
                using (var kb = new SolidBrush(Color.White))
                    g.FillEllipse(kb, kx, r.Y + 3, d, d);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_snap != null) { try { _snap.Dispose(); } catch { } }
                    foreach (var row in _rows) if (row.Avatar != null) { try { row.Avatar.Dispose(); } catch { } }
                }
                base.Dispose(disposing);
            }
        }

        // Quick reactions offered in the per-message context menu.
        private static readonly string[] QuickReactions = { "👍", "❤", "🔥", "🎉", "😁", "😢", "👎" };

        /// <summary>Adds a "React ▸" submenu of quick reactions to the message context menu.</summary>
        private void AddReactMenu(ContextMenuStrip menu, Message msg)
        {
            var react = new ToolStripMenuItem("☺   React");
            foreach (var em in QuickReactions)
            {
                var e2 = em;
                var it = new ToolStripMenuItem(em) { Font = FontHelper.Ui(11f) };
                it.Click += (s, e) => BeginInvoke((Action)(() => ToggleReaction(msg, e2)));
                react.DropDownItems.Add(it);
            }
            menu.Items.Add(react);
        }

        /// <summary>Reads a message's reactions into bubble chips (skips custom-emoji reactions).</summary>
        private static List<MessageBubbleControl.ReactionChip> ExtractReactions(Message m)
        {
            var results = m?.reactions?.results;
            if (results == null || results.Length == 0) return null;
            var list = new List<MessageBubbleControl.ReactionChip>();
            foreach (var rc in results)
            {
                string emo = (rc.reaction as ReactionEmoji)?.emoticon;
                if (string.IsNullOrEmpty(emo)) continue;   // ReactionCustomEmoji has no static glyph here
                list.Add(new MessageBubbleControl.ReactionChip
                {
                    Emoji = emo,
                    Count = rc.count,
                    Chosen = (rc.flags & ReactionCount.Flags.has_chosen_order) != 0
                });
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>Wires a bubble's reactions: shows existing chips and routes taps to toggle.</summary>
        /// <summary>Wires inline entities (links/mentions/emoji): colors+hit-tests them and routes taps.</summary>
        private void ApplyEntities(MessageBubbleControl mb, Message m)
        {
            mb.CustomEmojiResolver = ResolveCustomEmoji;   // before SetEntities so the engine gets it
            mb.SetEntities(m.entities);
            // Defer out of the bubble's mouse-click handler so the (modal) confirm can't tangle with the
            // originating click. t.me/tg:// → in-app (no confirm); external → browser (with confirm).
            mb.LinkClicked += url => BeginInvoke((Action)(() => ResolveLinkAsync(url)));
            mb.MentionClicked += (username, userId) => BeginInvoke((Action)(() =>
            {
                if (!string.IsNullOrEmpty(username)) ResolveLinkAsync("@" + username.TrimStart('@'));  // open the chat in-app
                else OpenMention(null, userId);   // mention-by-id (no username) → existing best-effort path
            }));
            mb.HashtagClicked += tag => { if (!string.IsNullOrEmpty(tag)) _searchBox.Text = tag; };   // → combined search
            mb.BotCommandClicked += cmd =>
            {
                if (!string.IsNullOrEmpty(cmd) && _messageInput.Enabled) { _messageInput.Text = cmd; _messageInput.Focus(); }
            };
            ApplyInlineKeyboard(mb, m);   // bot inline-keyboard grid under the message (any bubble type)
        }

        // ── Inline custom emoji (static first frame, batched + cached) ────────
        private Image ResolveCustomEmoji(long id)
        {
            if (_customEmojiCache.TryGetValue(id, out var img)) return img;
            QueueCustomEmoji(id);
            return null;   // fallback char shown until it loads
        }

        private void QueueCustomEmoji(long id)
        {
            if (id == 0 || _customEmojiPending.Contains(id) || _customEmojiCache.ContainsKey(id)) return;
            if (!_customEmojiQueue.Contains(id)) _customEmojiQueue.Add(id);
            if (_customEmojiTimer == null)
            {
                _customEmojiTimer = new System.Windows.Forms.Timer { Interval = 150 };
                _customEmojiTimer.Tick += (s, e) => FlushCustomEmoji();
            }
            _customEmojiTimer.Stop(); _customEmojiTimer.Start();   // debounce so layout passes batch together
        }

        private async void FlushCustomEmoji()
        {
            _customEmojiTimer.Stop();
            var ids = _customEmojiQueue
                .Where(i => !_customEmojiPending.Contains(i) && !_customEmojiCache.ContainsKey(i))
                .Distinct().Take(100).ToArray();
            _customEmojiQueue.Clear();
            if (ids.Length == 0) return;
            foreach (var i in ids) _customEmojiPending.Add(i);
            try
            {
                var docs = await _service.GetCustomEmojiDocsAsync(ids);
                foreach (var doc in docs)
                {
                    var img = await DecodeCustomEmojiAsync(doc);
                    if (img != null) _customEmojiCache[doc.id] = img;
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:FlushCustomEmoji", ex); }
            finally { foreach (var i in ids) _customEmojiPending.Remove(i); }
            if (!IsDisposed) RefreshVisibleRich();
        }

        private async System.Threading.Tasks.Task<Image> DecodeCustomEmojiAsync(Document doc)
        {
            string path = MediaCache.ThumbPath("customemoji_" + doc.id + ".png");
            try { if (File.Exists(path)) using (var fs = File.OpenRead(path)) using (var t = Image.FromStream(fs)) return new Bitmap(t); } catch { }
            try
            {
                Image im = null;
                if (doc.mime_type == "image/webp")
                {
                    var b = await _service.DownloadDocBytesAsync(doc);
                    if (b != null) im = ImageDecoder.DecodeAny(b);
                }
                else if (doc.mime_type == "application/x-tgsticker" && RLottie.Available)
                {
                    var b = await _service.DownloadDocBytesAsync(doc);
                    if (b != null) using (var clip = RLottie.OpenTgs(b)) if (clip != null) im = clip.RenderFrame(0, 64);
                }
                // webm / unknown → null → fallback char stays. (Animated inline deferred.)
                if (im != null)
                    try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); im.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                return im;
            }
            catch { return null; }
        }

        private void RefreshVisibleRich()
        {
            foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>()) mb.RefreshRich();
        }

        /// <summary>Opens a tapped @mention: resolves the username → profile (user) or chat (channel/group).</summary>
        private async void OpenMention(string username, long userId)
        {
            try
            {
                if (string.IsNullOrEmpty(username)) return;   // mention-by-id only → best-effort skip
                var who = await _service.ResolveUsernameAsync(username.TrimStart('@'));
                if (IsDisposed || who == null) return;
                if (who is User u) { OpenUserProfile(u); return; }
                if (who is ChatBase ch)
                {
                    var entry = new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.ID, Title = ch.Title, IsGroup = true, PeerInfo = ch };
                    await OpenChat(entry, 0);
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:OpenMention", ex); }
        }

        // ── Polls ───────────────────────────────────────────────────────────

        /// <summary>Builds the poll view-model from MessageMediaPoll and wires vote / retract / voter-list.</summary>
        private void ApplyPoll(MessageBubbleControl mb, MessageMediaPoll mmp)
        {
            var poll = mmp.poll;
            var results = mmp.results;
            if (poll == null) return;
            bool quiz = (poll.flags & Poll.Flags.quiz) != 0;
            bool multiple = (poll.flags & Poll.Flags.multiple_choice) != 0;
            bool publicV = (poll.flags & Poll.Flags.public_voters) != 0;
            bool closed = (poll.flags & Poll.Flags.closed) != 0;

            var voters = results != null ? results.results : null;   // PollAnswerVoters[]
            int total = results != null ? results.total_voters : 0;
            bool anyChosen = false;
            var opts = new List<MessageBubbleControl.PollOptionVM>();
            foreach (var ans in poll.answers.OfType<PollAnswer>())
            {
                var vm = new MessageBubbleControl.PollOptionVM { Option = ans.option, Text = ans.text != null ? ans.text.text : "" };
                if (voters != null)
                {
                    var av = voters.FirstOrDefault(v => v.option == ans.option);
                    if (av != null)
                    {
                        vm.Voters = av.voters;
                        vm.Chosen = (av.flags & PollAnswerVoters.Flags.chosen) != 0;
                        vm.Correct = (av.flags & PollAnswerVoters.Flags.correct) != 0;
                        if (vm.Chosen) anyChosen = true;
                    }
                }
                opts.Add(vm);
            }
            bool resultsVisible = closed || anyChosen;   // Telegram: bars appear once you've voted, or when closed
            string solution = results != null ? results.solution : null;
            mb.SetPoll(poll.question != null ? poll.question.text : "", opts, total, closed, publicV, multiple, quiz, solution, resultsVisible);

            int msgId = mb.MessageId;
            mb.PollOptionTapped += option => SubmitVote(msgId, new[] { option });
            mb.PollVoteSubmit += options => SubmitVote(msgId, options);
            mb.PollRetract += () => SubmitVote(msgId, new string[0]);
            mb.PollVotersRequested += option => ShowPollVoters(msgId, option, FindOptionText(poll, option));
            System.Diagnostics.Debug.WriteLine("[POLL] rendered msg=" + msgId + " opts=" + opts.Count + " total=" + total + " quiz=" + quiz + " results=" + resultsVisible);
        }

        private static string FindOptionText(Poll poll, string option)
        {
            foreach (var a in poll.answers.OfType<PollAnswer>())
                if (a.option == option) return a.text != null ? a.text.text : option;
            return option;
        }

        /// <summary>Casts our vote (or retracts with an empty array) and refreshes from the returned updates.</summary>
        private async void SubmitVote(int msgId, string[] options)
        {
            if (_selectedChat == null) return;
            try
            {
                System.Diagnostics.Debug.WriteLine("[POLL] SendVote msg=" + msgId + " options=" + options.Length);
                var updates = await _service.SendVoteAsync(_selectedChat.Peer, msgId, options);
                if (updates != null)
                    foreach (var u in updates.UpdateList)
                        if (u is UpdateMessagePoll p) HandlePollUpdate(p);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[POLL] vote err: " + ex.Message); }
        }

        /// <summary>Live poll refresh (UpdateMessagePoll): splice new results into the cached message + rebuild in place.</summary>
        private void HandlePollUpdate(UpdateMessagePoll p)
        {
            if (_selectedChat == null || p == null) return;
            foreach (var m in _currentChatMessages)
            {
                if (m.media is MessageMediaPoll mp && mp.poll != null && mp.poll.id == p.poll_id)
                {
                    if (p.poll != null) mp.poll = p.poll;
                    if (p.results != null) mp.results = MergePollResults(mp.results, p.results);
                    if (_messagePanel.Controls.OfType<MessageBubbleControl>().Any(b => b.MessageId == m.ID))
                        RebuildBubble(m.ID, m);   // re-fold height + re-render (bars/counts/total)
                    System.Diagnostics.Debug.WriteLine("[POLL] live refresh poll_id=" + p.poll_id);
                    return;
                }
            }
        }

        /// <summary>A "min" results update omits our personal chosen/correct flags → carry them over from the old results.</summary>
        private static PollResults MergePollResults(PollResults old, PollResults fresh)
        {
            if (fresh == null) return old;
            bool freshMin = (fresh.flags & PollResults.Flags.min) != 0;
            if (freshMin && old != null && old.results != null && fresh.results != null)
                foreach (var fr in fresh.results)
                {
                    var o = old.results.FirstOrDefault(x => x.option == fr.option);
                    if (o == null) continue;
                    if ((o.flags & PollAnswerVoters.Flags.chosen) != 0) fr.flags |= PollAnswerVoters.Flags.chosen;
                    if ((o.flags & PollAnswerVoters.Flags.correct) != 0) fr.flags |= PollAnswerVoters.Flags.correct;
                }
            return fresh;
        }

        /// <summary>Public poll: shows who voted for one option (Messages_GetPollVotes, paged).</summary>
        private void ShowPollVoters(int msgId, string option, string optionText)
        {
            if (_selectedChat == null) return;
            using (var dlg = new PollVotersForm(_service, _selectedChat.Peer, msgId, option, optionText, _dark, _accent))
                dlg.ShowDialog(this);
        }

        // ── Inline keyboards (bot buttons) ──────────────────────────────────

        /// <summary>Renders message.reply_markup (ReplyInlineMarkup) as a button grid under the bubble.</summary>
        private void ApplyInlineKeyboard(MessageBubbleControl mb, Message m)
        {
            var markup = m.reply_markup as ReplyInlineMarkup;
            if (markup == null || markup.rows == null || markup.rows.Length == 0) return;
            var rows = new List<List<MessageBubbleControl.KbButtonVM>>();
            foreach (var row in markup.rows)
            {
                if (row == null || row.buttons == null) continue;
                var cells = new List<MessageBubbleControl.KbButtonVM>();
                foreach (var btn in row.buttons)
                {
                    var fld = btn.GetType().GetField("text");
                    var vm = new MessageBubbleControl.KbButtonVM { Label = fld != null ? fld.GetValue(btn) as string : "" };
                    if (btn is KeyboardButtonCallback cb) { vm.Kind = MessageBubbleControl.KbKind.Callback; vm.Data = cb.data; }
                    else if (btn is KeyboardButtonUrl ub) { vm.Kind = MessageBubbleControl.KbKind.Url; vm.Url = ub.url; }
                    else if (btn is KeyboardButtonSwitchInline si) { vm.Kind = MessageBubbleControl.KbKind.SwitchInline; vm.Query = si.query; vm.SamePeer = (si.flags & KeyboardButtonSwitchInline.Flags.same_peer) != 0; }
                    else if (btn is KeyboardButtonUrlAuth ua) { vm.Kind = MessageBubbleControl.KbKind.UrlAuth; vm.Url = ua.url; }
                    else vm.Kind = MessageBubbleControl.KbKind.Unsupported;
                    cells.Add(vm);
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count == 0) return;
            mb.KbButtonTapped += vm => OnKbButtonTapped(m, vm);
            mb.SetInlineKeyboard(rows);
            System.Diagnostics.Debug.WriteLine("[BTN] inline keyboard rows=" + rows.Count + " msg=" + m.ID);
        }

        private async void OnKbButtonTapped(Message m, MessageBubbleControl.KbButtonVM vm)
        {
            if (vm == null || _selectedChat == null) return;
            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>().FirstOrDefault(b => b.MessageId == m.ID);
            if (vm.Kind == MessageBubbleControl.KbKind.Url || vm.Kind == MessageBubbleControl.KbKind.UrlAuth)
            {
                System.Diagnostics.Debug.WriteLine("[BTN] url → router: " + vm.Url);
                ResolveLinkAsync(vm.Url);   // UrlAuth: confirm+open via the router (no full SRP auth handshake yet)
                return;
            }
            if (vm.Kind == MessageBubbleControl.KbKind.SwitchInline)
            {
                if (vm.SamePeer && _messageInput.Enabled)
                {
                    string bot = (_selectedChat.PeerInfo as User)?.username;
                    _messageInput.Text = (string.IsNullOrEmpty(bot) ? "" : "@" + bot + " ") + (vm.Query ?? "");
                    _messageInput.Focus();
                    try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
                    System.Diagnostics.Debug.WriteLine("[BTN] switch-inline (same peer) prefill");
                }
                else ThemedDialog.Show(this, "Switch to inline", "Choosing another chat for an inline query isn't supported yet.", "OK");
                return;
            }
            if (vm.Kind == MessageBubbleControl.KbKind.Callback)
            {
                if (mb != null) mb.SetKbLoading(vm);
                try
                {
                    var ans = await _service.GetCallbackAnswerAsync(_selectedChat.Peer, m.ID, vm.Data);
                    if (ans != null)
                    {
                        if (!string.IsNullOrEmpty(ans.url)) { ResolveLinkAsync(ans.url); }
                        else if (!string.IsNullOrEmpty(ans.message))
                        {
                            if ((ans.flags & Messages_BotCallbackAnswer.Flags.alert) != 0) ThemedDialog.Show(this, "", ans.message, "OK");
                            else ShowToast(ans.message);
                        }
                        System.Diagnostics.Debug.WriteLine("[BTN] callback answer alert=" + ((ans.flags & Messages_BotCallbackAnswer.Flags.alert) != 0) + " url=" + (ans.url != null));
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BTN] callback err: " + ex.Message); }
                finally { if (mb != null) mb.SetKbLoading(null); }
                return;
            }
            ThemedDialog.Show(this, "Not supported", "This button type isn't supported yet.", "OK");   // Buy / Game / WebView
        }

        /// <summary>Brief auto-dismissing themed toast near the bottom of the window (non-alert callback answers).</summary>
        private void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false, BackColor = _dark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(55, 55, 58)
                };
                var lbl = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Text = text, Font = FontHelper.Ui(9f) };
                f.Controls.Add(lbl);
                var sz = TextRenderer.MeasureText(text, lbl.Font, new Size(360, 0), TextFormatFlags.WordBreak);
                f.Size = new Size(Math.Min(400, Math.Max(120, sz.Width + 40)), Math.Max(40, sz.Height + 24));
                f.Location = PointToScreen(new Point((ClientSize.Width - f.Width) / 2, ClientSize.Height - f.Height - 90));
                var t = new System.Windows.Forms.Timer { Interval = 2600 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!f.IsDisposed) f.Close(); };
                f.Shown += (s, e) => t.Start();
                f.Show(this);
            }
            catch { }
        }

        /// <summary>Attach (+) menu: send media, or create a poll.</summary>
        private void ShowAttachMenu()
        {
            if (_selectedChat == null) { OpenAttachmentDialog(); return; }
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, "🖼   Photo or File", () => OpenAttachmentDialog());
            AddMenuItem(menu, "⏺   Round Video", () => OpenRoundRecorder());
            AddMenuItem(menu, "📊   Create Poll", () => OpenCreatePoll());
            menu.Show(_attachButton, new Point(0, -menu.PreferredSize.Height));
        }

        private void OpenRoundRecorder()
        {
            if (_selectedChat == null) return;
            using (var f = new RoundRecorderForm(_service, _selectedChat.Peer))
            {
                f.ShowDialog(this);
                // Own-sent round video appears LIVE (the recorder sends directly with no optimistic bubble).
                // AddMessageBubble registers the id in _shownMessageIds, so the later UpdateManager echo of the
                // same message is de-duped (shown once). The received-round-video path is untouched.
                if (f.SentMessage != null)
                {
                    System.Diagnostics.Debug.WriteLine("[ROUND] append own-sent round video live id=" + f.SentMessage.ID);
                    HandleIncomingMessage(f.SentMessage);
                }
            }
        }

        private async void OpenCreatePoll()
        {
            if (_selectedChat == null) return;
            using (var dlg = new CreatePollForm(_dark, _accent))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    await _service.SendPollAsync(_selectedChat.Peer, dlg.Question, dlg.Options, dlg.Anonymous, dlg.Multiple);
                    System.Diagnostics.Debug.WriteLine("[POLL] created q='" + dlg.Question + "' opts=" + dlg.Options.Length + " anon=" + dlg.Anonymous + " multi=" + dlg.Multiple);
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Poll", "Couldn't create the poll:\n" + ex.Message, "OK"); }
            }
        }

        private void ApplyReactions(MessageBubbleControl mb, Message m)
        {
            var chips = ExtractReactions(m);
            if (chips != null) mb.SetReactions(chips);
            mb.ReactionToggled += (s, emoji) => ToggleReaction(m, emoji);
        }

        /// <summary>
        /// Toggles our reaction on a message: tapping the chosen one removes it, tapping another
        /// switches to it. Updates the bubble optimistically, then sends to the server.
        /// </summary>
        private async void ToggleReaction(Message m, string emoji)
        {
            if (_selectedChat == null || m == null || string.IsNullOrEmpty(emoji)) return;

            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>()
                                  .FirstOrDefault(b => b.MessageId == m.ID);

            // Work off the bubble's current chips so repeated taps stay consistent.
            var chips = (mb?.Reactions ?? (IReadOnlyList<MessageBubbleControl.ReactionChip>)new List<MessageBubbleControl.ReactionChip>())
                .Select(c => new MessageBubbleControl.ReactionChip { Emoji = c.Emoji, Count = c.Count, Chosen = c.Chosen })
                .ToList();

            var tapped = chips.FirstOrDefault(c => c.Emoji == emoji);
            bool wasChosen = tapped != null && tapped.Chosen;

            // Telegram allows one reaction here: clear whatever was chosen first.
            foreach (var c in chips.ToList())
                if (c.Chosen)
                {
                    c.Chosen = false;
                    c.Count = Math.Max(0, c.Count - 1);
                    if (c.Count == 0) chips.Remove(c);
                }

            if (!wasChosen)   // add the tapped reaction
            {
                var t = chips.FirstOrDefault(c => c.Emoji == emoji);
                if (t == null) { t = new MessageBubbleControl.ReactionChip { Emoji = emoji, Count = 0 }; chips.Add(t); }
                t.Chosen = true;
                t.Count += 1;
            }

            mb?.SetReactions(chips.Count > 0 ? chips : null);

            await _service.SendReactionAsync(_selectedChat.Peer, m.ID, wasChosen ? "" : emoji);
        }

        private void ShowProfile()
        {
            using (var dlg = new ProfileForm(_service))   // editable self-profile
                dlg.ShowDialog(this);
        }

        /// <summary>Opens the read-only profile of the currently selected chat (header click).</summary>
        private void OpenSelectedProfile()
        {
            if (_selectedChat == null) return;
            var av = _avatars.GetCached(_selectedChat.PeerId);
            using (var dlg = new ProfileForm(_service, _selectedChat, av))
            {
                dlg.Avatars = _avatars;   // PROFILE-MEMBERS: member rows use the shared store
                dlg.ForwardRequested += ForwardFromProfile;
                dlg.ShowInChatRequested += ShowInChatFromProfile;
                dlg.ShowDialog(this);
                RouteProfilePending(dlg);
            }
        }

        /// <summary>Routes a rich-info link/@mention/#hashtag tapped in a (now-closed) profile to the existing
        /// in-app resolvers — same handlers chat bubbles use.</summary>
        private void RouteProfilePending(ProfileForm dlg)
        {
            if (dlg.PendingLink != null) ResolveLinkAsync(dlg.PendingLink);
            else if (!string.IsNullOrEmpty(dlg.PendingMentionUser) || dlg.PendingMentionId != 0) OpenMention(dlg.PendingMentionUser, dlg.PendingMentionId);
            else if (dlg.PendingHashtag != null && _searchBox != null) _searchBox.Text = dlg.PendingHashtag;
        }

        /// <summary>Handles a profile-gallery Forward request via the existing picker → forward flow.</summary>
        private async void ForwardFromProfile(InputPeer src, int msgId)
        {
            var owner = (IWin32Window)Form.ActiveForm ?? this;   // the topmost modal (gallery) owns the picker
            List<ChatEntry> targets;
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(owner) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }
            int ok = await ForwardToTargets(src, new[] { msgId }, targets);
            ThemedDialog.Show(owner, ok > 0 ? "Forwarded" : "Forward failed", ForwardResultText(ok, targets.Count), "OK");
        }

        /// <summary>Forwards ids to each selected target (bounded per target), routing EACH target's result
        /// through <see cref="ApplySendResult"/> — the single chat-list refresh — so forwarded chats update
        /// (preview + re-order) live, exactly like a received/sent message. Returns how many succeeded.</summary>
        private async System.Threading.Tasks.Task<int> ForwardToTargets(InputPeer src, int[] ids, List<ChatEntry> targets)
        {
            int ok = 0;
            foreach (var t in targets)
            {
                if (t == null || t.Peer == null) continue;
                var result = await _service.ForwardToAsync(src, ids, t.Peer);
                if (result == null) continue;
                ApplySendResult(result);   // → ProcessSingleUpdate → HandleIncomingMessage → UpdateChatListForMessage
                System.Diagnostics.Debug.WriteLine("[UPDATE] forward applied → peer=" + t.PeerId);
                ok++;
            }
            return ok;
        }

        private static string ForwardResultText(int ok, int total)
        {
            if (ok == 0) return "Couldn't forward — make sure your VPN is on.";
            if (ok == total) return "Forwarded to " + ok + (ok == 1 ? " chat." : " chats.");
            return "Forwarded to " + ok + " of " + total + " chats (the rest couldn't be reached).";
        }

        /// <summary>Jumps the conversation to a profile-gallery item's message (scroll-into-view + accent flash).</summary>
        private void ShowInChatFromProfile(InputPeer peer, int messageId)
        {
            long id = PeerIdOf(peer);
            // Defer so this runs AFTER the gallery + profile have closed and the message panel is the active view.
            BeginInvoke((Action)(async () =>
            {
                // Prefer the real dialog-list entry; otherwise build a minimal one from the InputPeer so the
                // jump still lands (e.g. a group member's media when you've never opened a 1:1 with them).
                var entry = _allChats.FirstOrDefault(c => c.PeerId == id) ?? BuildMinimalChatEntry(peer, id);
                if (entry == null) return;   // unresolvable peer → graceful no-op

                bool sameChat = _selectedChat != null && _selectedChat.PeerId == entry.PeerId;
                if (sameChat && ScrollToAndFlash(messageId))   // already open + loaded → no reload
                    return;

                await OpenChat(entry, messageId);              // different chat or far-back → focused reload
                ScrollToAndFlash(messageId);                   // flashes whichever row kind it lands on
            }));
        }

        /// <summary>Builds a throwaway ChatEntry from an InputPeer for chats not in the dialog list, so a
        /// Show-in-chat jump can still open them focused on the message. Name resolves from the peer cache.</summary>
        private ChatEntry BuildMinimalChatEntry(InputPeer peer, long id)
        {
            if (peer == null) return null;
            bool isGroup = peer is InputPeerChat || peer is InputPeerChannel;
            string title = _peerNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)
                ? n : (isGroup ? "Group" : "Chat");
            return new ChatEntry { Peer = peer, PeerId = id, Title = title, IsGroup = isGroup };
        }

        private void ShowAbout()
        {
            using (var f = new AboutForm(_dark, _accent))
            {
                f.ShowDialog(this);
                // A Telegram chat/group tapped in About (e.g. @WTelegramClient) → open it IN-APP after the
                // dialog closes, via the same router the drawer channel row uses (off-thread, graceful).
                if (!string.IsNullOrEmpty(f.PendingInAppLink)) ResolveLinkAsync(f.PendingInAppLink);
            }
        }

        /// <summary>Opens the official TelegArm channel in the chat view so the user can browse + join it.
        /// Reuses the in-app link router: resolves the handle OFF the UI thread and, if it can't (offline /
        /// VPN down / bad handle), quietly falls back to the browser — never hangs, never crashes.</summary>
        private void OpenTelegArmChannel()
        {
            string handle = (CHANNEL_USERNAME ?? "").TrimStart('@');
            if (string.IsNullOrEmpty(handle)) return;
            ResolveLinkAsync("https://t.me/" + handle);
        }

        private void SetThemeMode(ThemeMode mode)
        {
            // SetMode raises ThemeChanged → OnSystemThemeChanged re-applies the whole UI
            // (and any open MediaViewerForm, which also subscribes).
            ThemeHelper.SetMode(mode);
            AppSettings.Instance.ThemeMode = mode.ToString();
            AppSettings.Instance.Save();
        }

        /// <summary>Removes the ACTIVE account (distinct from switching, which keeps it): auth.logOut on the
        /// server → delete accounts/{id}/ + Cache/{id}/ → switch to another account, or the LoginForm if none.</summary>
        private void LogOut()
        {
            long id = AccountContext.ActiveId;
            string name = _service.Me != null ? DisplayName(_service.Me) : "this account";
            int c = ThemedDialog.Show(this, "Log Out",
                "Log out of " + name + "?\n\nThis removes the account and deletes its session and cache on this device. (Switching accounts keeps it.)",
                "Log Out", "Cancel");
            try { ActiveControl = null; } catch { }
            if (c != 0) return;

            // INSTANT: detach the client + run ALL the slow work (server logout, dispose, delete folders) in the
            // BACKGROUND, then transition the UI right away — never wait on the network (the freeze fix).
            // [LOGOUT-TRACE] on every UI-thread step → the LAST line printed before a freeze IS the blocker.
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] A confirmed, id=" + id);
            var others = AccountStore.ListAccounts().FindAll(a => a.Id != id);
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] B listed accounts, remaining=" + others.Count);
            _service.BeginLogoutCleanup(id);   // detach + bg: best-effort Auth_LogOut + dispose + delete accounts/{id} & Cache/{id}
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] C after BeginLogoutCleanup (client detached, cleanup running in bg)");
            ResetPerAccountState();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] D after ResetPerAccountState");
            AuthManager.Reset();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] E after AuthManager.Reset");

            if (others.Count > 0)
            {
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(others[0].Id);
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] F before SwitchAccountAsync → " + others[0].Id);
                var ignore = SwitchAccountAsync(others[0].Id, others[0].Name);   // switch to a remaining account (resilient connect)
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] G SwitchAccountAsync returned (running async)");
            }
            else
            {
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(0);
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] F before ShowLoginForm (no remaining accounts)");
                ShowLoginForm();   // no accounts left → first-launch login (instant)
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] G after ShowLoginForm");
            }
        }

        private Image LoadAccountAvatar(AccountInfo acc)
        {
            try
            {
                if (acc != null && !string.IsNullOrEmpty(acc.AvatarPath) && File.Exists(acc.AvatarPath))
                    using (var fs = File.OpenRead(acc.AvatarPath))
                    using (var t = Image.FromStream(fs))
                        return new Bitmap(t);
            }
            catch { }
            return null;
        }

        private async void SwitchAccount(long id, string name)
        {
            try { await SwitchAccountAsync(id, name); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ACCT] switch err: " + ex.Message); }
        }

        /// <summary>Adds another account: teardown the active client → login on accounts/_pending/ → on success
        /// relocate to accounts/{newId}/ and rebuild; an already-added id just switches; cancel restores the
        /// previous account.</summary>
        private async void AddAccount()
        {
            CloseDrawer();
            long prev = AccountContext.ActiveId;
            string prevName = _service.Me != null ? DisplayName(_service.Me) : null;

            // FULL isolation from the previous account so the add login is a GENUINE fresh login (not a resume
            // of a stale identity): drop the client + Me, clear AuthManager (phone/code/password), point the
            // session at an EMPTY accounts/_pending/, and robustly remove any leftover _pending session.
            await _service.TeardownForSwitchAsync();         // disposes the active client; nulls Me + _silentResume
            ResetPerAccountState();
            AuthManager.Reset();                  // no previous phone/identity leaks into the add login
            AccountContext.ActiveId = 0;          // → Config("session_pathname") = accounts/_pending/session
            AccountContext.LegacyMode = false;
            AccountStore.ClearPending();          // a leftover _pending session would be RESUMED → wrong identity
            if (AccountStore.PendingSessionExists()) System.Diagnostics.Debug.WriteLine("[ACCT] WARNING: stale _pending session still present before add");
            System.Diagnostics.Debug.WriteLine("[ACCT] add-account: Me nulled + AuthManager reset + session=" + AccountContext.SessionPath);

            bool added = false; long newId = 0; string newName = null;
            using (var login = new LoginForm(_service) { AddMode = true })
            {
                var result = login.ShowDialog(this);
                if (result == DialogResult.OK && _service.Me != null)
                {
                    added = true; newId = _service.Me.id; newName = DisplayName(_service.Me);
                    System.Diagnostics.Debug.WriteLine("[ACCT] add login completed, new Me.id=" + newId + " (" + newName + "); previous active=" + prev);
                }
            }

            if (!added)
            {
                // CANCEL / BACK-OUT / FAILURE → fully non-destructive: discard ONLY accounts/_pending/ (never a
                // real account or Cache), clear the stale add-attempt phone so the restore silently resumes,
                // and return to the previously-active account (or another existing one — never empty).
                AccountStore.ClearPending();
                AuthManager.Reset();
                long restore = prev;
                if (restore == 0) { var accs = AccountStore.ListAccounts(); if (accs.Count > 0) restore = accs[0].Id; }
                System.Diagnostics.Debug.WriteLine("[ACCT] add cancelled → deleted _pending only, restoring active=" + restore);
                if (restore != 0) { AccountContext.ActiveId = 0; await SwitchAccountAsync(restore, prevName); }
                else ShowLoginForm();   // genuinely no accounts (e.g. add was the very first) → first-launch login
                return;
            }

            if (AccountStore.Exists(newId))
            {
                // Already added → discard the new (duplicate) session, switch to the existing one.
                System.Diagnostics.Debug.WriteLine("[ACCT] add-account: " + newId + " already present → switching");
                await _service.LogOutServerAsync();   // revoke the duplicate pending session
                await _service.TeardownForSwitchAsync();
                AccountStore.ClearPending();
                AccountContext.ActiveId = 0;          // ≠ newId → switch guard passes (even if re-adding the active account)
                await SwitchAccountAsync(newId, newName);
                return;
            }

            // New account → relocate pending → accounts/{newId}/ and reconnect there.
            System.Diagnostics.Debug.WriteLine("[ACCT] add-account new session id=" + newId);
            await _service.TeardownForSwitchAsync();
            AccountStore.RelocatePendingToAccount(newId, newName);
            AccountContext.ActiveId = newId;
            AccountStore.WriteActive(newId);
            ShowConnecting("Loading " + newName + "…");
            bool connected = await ConnectResilientlyAsync("Loading " + newName + "…");
            if (!connected) { await OnConnectFailedAsync(); return; }
            HideConnecting();
            await AfterConnectAsync();
        }

        // ── Login + initial load ─────────────────────────────────────────────

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await ResumeSessionAsync();
        }

        /// <summary>
        /// Resumes the saved session. Stays on MainForm whenever the client ends up
        /// authorized; only drops to LoginForm for genuine auth failures.
        /// </summary>
        private async System.Threading.Tasks.Task ResumeSessionAsync()
        {
            // Connection at launch is reached ONLY via the VPN, which may not be up yet — so a failure is a
            // NORMAL, recoverable state. NEVER exit/give up: retry forever with a capped backoff, showing a
            // "Connecting… / waiting for network" overlay (+ a manual Retry), until the session resumes.
            // CRITICAL: WTelegram's connect BLOCKS (doesn't throw) when there's no network, so every attempt
            // is bounded by a timeout — a hung connect is torn down and treated as a failed attempt.
            bool connected = await ConnectResilientlyAsync("Connecting to Telegram…");
            if (!connected) { await OnConnectFailedAsync(); return; }

            // The session may be at a TEMP location: the legacy flat file (migration) or accounts/_pending/
            // (a first login that landed on a new MainForm). Now that Me.id is known, relocate it (+ cache)
            // under accounts/{id}/ and re-resume there. One-time per account.
            if (_service.Me != null && (AccountContext.LegacyMode || !AccountContext.HasActive))
                if (!await FinalizeNewAccountAsync(AccountContext.LegacyMode)) { await OnConnectFailedAsync(); return; }

            HideConnecting();
            System.Diagnostics.Debug.WriteLine("[CONN] connected — entering app");
            SetupTray();              // only once we're authorized and staying on MainForm
            await AfterConnectAsync();
        }

        /// <summary>The resilient connect loop (CONN-fix): retries forever on a missing/black-holed VPN with a
        /// "Connecting…/waiting" overlay + bounded attempts. Returns true when authorized; false when an
        /// interactive login is required (caller drops to the LoginForm). Shared by resume AND account switch.</summary>
        private async System.Threading.Tasks.Task<bool> ConnectResilientlyAsync(string firstMsg)
        {
            _connectCorrupt = false;   // fresh per connect; set true only on an unreadable-session error
            int backoffMs = TelegramService.ConnectInitialBackoffMs;
            int hardFailures = 0;          // consecutive NON-network failures (locked/unusable session) → capped, never infinite
            const int maxHardFailures = 5;
            for (int attempt = 1; ; attempt++)
            {
                if (_abortConnect != null && _abortConnect.IsCancellationRequested)   // user tapped Cancel on a switch
                { System.Diagnostics.Debug.WriteLine("[ACCT] connect loop aborted by user"); return false; }

                if (_retryNowCts != null) { try { _retryNowCts.Dispose(); } catch { } }
                _retryNowCts = new System.Threading.CancellationTokenSource();   // "Retry now" cancels this (attempt + backoff)
                var token = _retryNowCts.Token;

                ShowConnecting(attempt == 1 ? firstMsg : "Waiting for network — make sure your VPN is on.");
                System.Diagnostics.Debug.WriteLine("[CONN] connect attempt " + attempt);

                var loginTask = _service.LoginAsync(silentResume: true);   // silent: stored phone, no code/password block; MAY hang
                var waiter = System.Threading.Tasks.Task.Delay(TelegramService.ConnectAttemptTimeoutMs, token);
                System.Threading.Tasks.Task finished;
                try { finished = await System.Threading.Tasks.Task.WhenAny(loginTask, waiter); }
                catch { finished = null; }

                if (finished == loginTask)
                {
                    Exception failure = null;
                    try { await loginTask; } catch (Exception ex) { failure = ex; }   // observe success/exception
                    if (_service.IsAuthorized) return true;                            // connected + authorized

                    if (failure != null)
                    {
                        bool corrupt = IsCorruptSessionError(failure);
                        bool needsLogin = IsNeedsLoginError(failure) || _service.NeedsInteractiveLogin || IsAuthError(failure);
                        System.Diagnostics.Debug.WriteLine("[CONN] attempt " + attempt + " failed: " + failure.Message
                            + (corrupt ? "  [SESSION unreadable → recover]"
                               : needsLogin ? "  [no usable session/phone → stop; caller recovers]"
                               : "  [hard failure " + (hardFailures + 1) + "/" + maxHardFailures + "]"));
                        if (corrupt) { _connectCorrupt = true; return false; }        // caller deletes the dead session + recovers
                        if (needsLogin) return false;                                 // no session/phone → LoginForm / next account (NOT retried)

                        // A non-network failure (e.g. the session file is locked by a lingering handle): the SAME
                        // client keeps failing, so DISCARD it (release the file → fresh client next attempt) and CAP
                        // the retries so a locked/unusable account can never loop forever (the repaint-storm bug).
                        hardFailures++;
                        await _service.DiscardFaultedClientAsync();
                        if (hardFailures >= maxHardFailures)
                        { System.Diagnostics.Debug.WriteLine("[CONN] gave up after " + hardFailures + " non-network failures → caller recovers"); return false; }
                    }
                    else
                    {
                        if (_service.NeedsInteractiveLogin) return false;             // returned w/o auth, needs interactive login
                        hardFailures++;                                              // returned w/o auth AND w/o throwing (rare) → cap it
                        if (hardFailures >= maxHardFailures) return false;
                    }
                    // fall through to the backoff (a brief transient — e.g. a lock that may clear next attempt)
                }
                else
                {
                    bool userRetry = token.IsCancellationRequested;
                    System.Diagnostics.Debug.WriteLine("[CONN] attempt " + attempt + (userRetry
                        ? " interrupted by Retry-now → tearing down hung attempt"
                        : " timed out after " + (TelegramService.ConnectAttemptTimeoutMs / 1000) + "s → tearing down hung attempt"));
                    await _service.TeardownHungConnectAsync();
                    SwallowFault(loginTask);   // it will fault once the socket is reset; don't let it surface
                    if (userRetry) continue;   // Retry-now → immediate fresh attempt (skip backoff)
                }

                int secs = Math.Max(1, backoffMs / 1000);
                SetConnectingDetail("Waiting for network — make sure your VPN is on.\nRetrying in " + secs + "s… (or tap Retry now)");
                System.Diagnostics.Debug.WriteLine("[CONN] backoff " + secs + "s before next attempt");
                try { await System.Threading.Tasks.Task.Delay(backoffMs, token); }
                catch (OperationCanceledException) { System.Diagnostics.Debug.WriteLine("[CONN] Retry-now → retrying immediately"); }
                backoffMs = Math.Min(backoffMs * 2, TelegramService.ConnectMaxBackoffMs);
            }
        }

        /// <summary>Post-connect setup shared by resume + switch: updates, dialogs/chat list, manager seed, watchdog.</summary>
        private async System.Threading.Tasks.Task AfterConnectAsync()
        {
            _recoveryTried.Clear();   // a clean connect → reset the corrupt-recovery chain for any future corruption
            SubscribeUpdates();
            await LoadDialogsAsync();
            await _service.SeedUpdateManagerAsync();   // seed the manager's baseline — REQUIRED for live updates
            // NOTIFY-FIX: persist the freshly-seeded update state NOW (the connection is known-good here, so
            // the SaveState-can-hang-on-dead-VPN caveat doesn't apply). Otherwise the state file only updates
            // at clean exit/switch, and a crash-restart re-attaches from a stale pts → getDifference replays
            // the whole previous session's tail through the notify gate.
            _service.SaveUpdateState();
            await FetchNotifyDefaultsAsync();          // category mute defaults (users/chats/broadcasts), per connect
            // AVATAR-PIPELINE 2.1: backfill every dialog peer whose (peer, photo_id) isn't on disk — in list
            // order (visible rows first, then top-to-bottom); on-demand requests always jump ahead of this.
            _avatars.EnqueueBackfill(_allChats.OrderByDescending(c => c.Date)
                .Select(c => new KeyValuePair<long, IPeerInfo>(c.PeerId, c.PeerInfo)));
            _service.ReconnectingChanged -= OnReconnectingChanged;
            _service.ReconnectingChanged += OnReconnectingChanged;
            _service.StartConnectionWatchdog();        // detect black-holed VPN drops + force reconnect
            if (_service.Me != null)
            {
                AccountStore.WriteMeta(AccountContext.ActiveId, DisplayName(_service.Me));   // keep the switcher name fresh
                // Self-heal: every connected account MUST have its phone persisted (silent resume/switch reads
                // it; a missing phone makes the resume need interactive login → a spurious "logged out").
                if (!File.Exists(AccountContext.PhonePath) && !string.IsNullOrEmpty(_service.Me.phone))
                    _service.SavePhone(_service.Me.phone.StartsWith("+") ? _service.Me.phone : "+" + _service.Me.phone);
            }
        }

        /// <summary>Keys a just-resumed temp session to accounts/{Me.id}/: dispose (unlock the temp session),
        /// move session(+cache for legacy) into the per-account layout, then re-resume there. fromLegacy moves
        /// the flat legacy files + Cache/; otherwise the accounts/_pending/ files. Returns false if the
        /// re-resume needs an interactive login.</summary>
        private async System.Threading.Tasks.Task<bool> FinalizeNewAccountAsync(bool fromLegacy)
        {
            long id = _service.Me.id;
            string name = DisplayName(_service.Me);
            System.Diagnostics.Debug.WriteLine("[ACCT] finalize new account → accounts/" + id + " (legacy=" + fromLegacy + ")");
            await _service.TeardownForSwitchAsync();                       // dispose client FIRST → release the temp session lock
            if (fromLegacy) AccountStore.RelocateLegacyToAccount(id, name);   // + moves Cache/ → Cache/{id}
            else AccountStore.RelocatePendingToAccount(id, name);
            AccountContext.ActiveId = id;
            AccountContext.LegacyMode = false;
            // Guard against a bad relocate (a 0-byte/missing session) BEFORE connecting — otherwise a new client
            // would create a fresh empty session at that path and we'd loop on "buffer is null". Route to recovery.
            if (!AccountStore.SessionLooksValid(id))
            {
                System.Diagnostics.Debug.WriteLine("[SESSION] relocated session for " + id + " is missing/0-byte → recovering instead of connecting");
                _connectCorrupt = true;
                return false;
            }
            return await ConnectResilientlyAsync("Finishing account setup…");   // re-resume on accounts/{id}/session
        }

        /// <summary>Switches the active account: teardown (keep session) → clear all per-account state →
        /// repoint the cache root → resilient reconnect on the target → rebuild the UI.</summary>
        private async System.Threading.Tasks.Task SwitchAccountAsync(long targetId, string targetName)
        {
            if (targetId == 0 || targetId == AccountContext.ActiveId) return;
            long prevId = AccountContext.ActiveId;
            string prevName = _service.Me != null ? DisplayName(_service.Me) : null;
            System.Diagnostics.Debug.WriteLine("[ACCT] switch start → " + targetId);

            _switchInProgress = true; _switchAborted = false;
            if (_abortConnect != null) { try { _abortConnect.Dispose(); } catch { } }
            _abortConnect = new System.Threading.CancellationTokenSource();
            ShowSwitchOverlay(targetId, targetName);   // calm transition masks teardown→connect→reload
            ShowConnecting("Switching to " + (targetName ?? "account") + "…");

            await _service.TeardownForSwitchAsync();
            ResetPerAccountState();
            AccountContext.ActiveId = targetId;     // repoints session path AND the cache root (Cache/{id})
            AccountContext.LegacyMode = false;
            AccountStore.WriteActive(targetId);
            bool connected = await ConnectResilientlyAsync("Switching to " + (targetName ?? "account") + "…");
            _switchInProgress = false;

            if (!connected && _switchAborted && prevId != 0)
            {
                // Cancel → restore the previously-active account (it's still valid; just reconnect on it).
                System.Diagnostics.Debug.WriteLine("[ACCT] switch cancelled → restoring active=" + prevId);
                _abortConnect = null;                 // the restore must NOT auto-abort
                AccountContext.ActiveId = prevId;
                AccountStore.WriteActive(prevId);
                ShowSwitchOverlay(prevId, prevName);
                ShowConnecting("Returning to " + (prevName ?? "your account") + "…");
                bool back = await ConnectResilientlyAsync("Returning to " + (prevName ?? "your account") + "…");
                if (!back) { HideSwitchOverlay(); await OnConnectFailedAsync(); return; }
                HideConnecting();
                await AfterConnectAsync();
                HideSwitchOverlay();
                return;
            }
            _abortConnect = null;
            if (!connected) { HideSwitchOverlay(); await OnConnectFailedAsync(); return; }
            HideConnecting();
            await AfterConnectAsync();
            HideSwitchOverlay();
            System.Diagnostics.Debug.WriteLine("[ACCT] switch done → " + targetId);
        }

        /// <summary>Clears ALL account-scoped in-memory state so account B never shows account A's data
        /// (the avatar/photo caches are the correctness hazard — Telegram object ids are account-scoped).</summary>
        private void ResetPerAccountState()
        {
            CloseDrawer();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: before AudioPlayer.Stop");
            try { AudioPlayer.Stop(); } catch { }
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after AudioPlayer.Stop; before ClearMessagePanel");
            _selectedChat = null; _selectedItem = null;
            // ACCOUNT teardown is the ONE place background downloads must die: a cross-account transfer
            // writing into a switched cache root would violate isolation (DOWNLOAD-UX 2.1 invariant).
            try { _service.CancelAllDownloads("account-switch"); } catch { }
            ClearMessagePanel();                         // disposes message bubbles + per-chat photo caches
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after ClearMessagePanel");
            _currentChatMessages.Clear();
            foreach (Control c in _chatListPanel.Controls.OfType<Control>().ToArray()) { _chatListPanel.Controls.Remove(c); c.Dispose(); }
            _allChats.Clear();
            _shownMessageIds.Clear();
            _albumBubbles.Clear();
            _peerNames.Clear();
            _avatars.Reset();   // account-scoped ids + disk root → MUST clear (disposes cached bitmaps)
            foreach (var img in _customEmojiCache.Values) { try { img.Dispose(); } catch { } }
            _customEmojiCache.Clear();
            _photoCachePaths.Clear();
            _pinnedMessages = null; _pinnedChatId = 0;
            if (_pinnedBar != null) _pinnedBar.Visible = false;
            _jumpUnread = 0;
            System.Diagnostics.Debug.WriteLine("[ACCT] per-account in-memory state reset");
        }

        /// <summary>Shows/clears a "Reconnecting…" hint in the window title while the watchdog rebuilds the link.</summary>
        private void OnReconnectingChanged(bool reconnecting)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (reconnecting) { if (Text != ReconnectingTitle) { _titleBeforeReconnect = Text; Text = ReconnectingTitle; } }
                    else if (_titleBeforeReconnect != null) { Text = _titleBeforeReconnect; _titleBeforeReconnect = null; }
                }));
            }
            catch { /* form went away */ }
        }

        // ── "Connecting… / Waiting for network" overlay (startup never exits on a missing VPN) ──

        // ── Account-switch transition: one calm overlay (avatar + "Switching to [Name]") covering the whole
        //    switch (teardown → connect → reload), so the user sees a steady transition instead of the UI
        //    blanking + a connect popup + a load flash. The Retry/Cancel connect card layers over it if a
        //    (still-cold) connect is slow — graceful. (Warm-core swap = a separate staged batch.)
        private void EnsureSwitchOverlay()
        {
            if (_switchOverlay != null) return;
            _switchOverlay = new Panel
            {
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = _dark ? Color.FromArgb(26, 26, 29) : Color.FromArgb(244, 244, 247)
            };
            _switchOverlay.Paint += PaintSwitchOverlay;
            Controls.Add(_switchOverlay);
            _switchDots = new System.Windows.Forms.Timer { Interval = 350 };
            _switchDots.Tick += (s, e) => { _switchDotCount = (_switchDotCount + 1) % 4; if (_switchOverlay.Visible) _switchOverlay.Invalidate(); };
        }

        private void PaintSwitchOverlay(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = _switchOverlay.Width / 2, cy = _switchOverlay.Height / 2;
            int d = 100; var rect = new Rectangle(cx - d / 2, cy - d / 2 - 44, d, d);
            if (_switchAvatar != null) DrawHelper.DrawCircularImage(g, rect, _switchAvatar);
            else
            {
                using (var b = new SolidBrush(_accent)) g.FillEllipse(b, rect);
                string ini = !string.IsNullOrEmpty(_switchOverlayName) ? _switchOverlayName.Substring(0, 1).ToUpperInvariant() : "?";
                using (var f = new Font("Segoe UI", 36f, FontStyle.Bold))
                    TextRenderer.DrawText(g, ini, f, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            string title = "Switching to " + (_switchOverlayName ?? "account") + new string('.', _switchDotCount);
            using (var f = new Font("Segoe UI", 13.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, f, new Rectangle(0, cy + 30, _switchOverlay.Width, 34),
                    _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void ShowSwitchOverlay(long targetId, string name)
        {
            EnsureSwitchOverlay();
            _switchOverlayName = name;
            if (_switchAvatar != null) { try { _switchAvatar.Dispose(); } catch { } _switchAvatar = null; }
            try
            {
                var info = AccountStore.ListAccounts().FirstOrDefault(a => a.Id == targetId);
                if (info != null && !string.IsNullOrEmpty(info.AvatarPath) && File.Exists(info.AvatarPath))
                    using (var fs = File.OpenRead(info.AvatarPath)) using (var t = Image.FromStream(fs)) _switchAvatar = new Bitmap(t);
            }
            catch { _switchAvatar = null; }
            _switchDotCount = 0;
            _switchOverlay.Bounds = ClientRectangle;   // cover the full client area (not docked — avoids layout fights)
            _switchOverlay.Visible = true;
            _switchOverlay.BringToFront();
            if (_switchDots != null && !_switchDots.Enabled) _switchDots.Start();
            System.Diagnostics.Debug.WriteLine("[ACCT] switch overlay shown → " + name);
        }

        private void HideSwitchOverlay()
        {
            if (_switchOverlay == null) return;
            if (_switchDots != null) _switchDots.Stop();
            _switchOverlay.Visible = false;
            if (_switchAvatar != null) { try { _switchAvatar.Dispose(); } catch { } _switchAvatar = null; }
        }

        private void EnsureConnectingUi()
        {
            if (_connectingPanel != null) return;
            // UI-FIX-T1: no local `dark` snapshot — the Paint lambda reads _dark LIVE (captured colors were the
            // stale-after-theme-switch bug, UI-AUDIT A.3); RecolorConnectingUi re-pushes the property colors.
            _connectingPanel = new Panel
            {
                Size = new Size(460, 172),
                BackColor = _dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(250, 250, 252),
                Visible = false
            };
            _connectingPanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(_dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(214, 214, 220)))
                    e.Graphics.DrawRectangle(pen, 0, 0, _connectingPanel.Width - 1, _connectingPanel.Height - 1);
            };

            _connectingTitle = new Label
            {
                Text = "Connecting…",
                AutoSize = false, Size = new Size(428, 34), Location = new Point(16, 20),
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38)
            };
            _connectingDetail = new Label
            {
                Text = "Waiting for network — make sure your VPN is on.",
                AutoSize = false, Size = new Size(428, 46), Location = new Point(16, 58),
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.75f),
                ForeColor = _dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(108, 108, 114)
            };
            _retryButton = new MaterialButton
            {
                Text = "Retry now",
                Type = MaterialButton.MaterialButtonType.Contained,
                UseAccentColor = false,
                AutoSize = false, Size = new Size(170, 40)
            };
            _retryButton.Location = new Point((_connectingPanel.Width - _retryButton.Width) / 2, 118);
            _retryButton.Click += (s, e) =>
            {
                var c = _retryNowCts;   // cut the backoff wait → retry immediately
                if (c != null) { try { c.Cancel(); } catch { } }
            };

            _switchCancelButton = new MaterialButton
            {
                Text = "Cancel", Type = MaterialButton.MaterialButtonType.Outlined,
                AutoSize = false, Size = new Size(150, 40), Visible = false
            };
            _switchCancelButton.Click += (s, e) =>
            {
                if (!_switchInProgress) return;
                _switchAborted = true;
                try { if (_abortConnect != null) _abortConnect.Cancel(); } catch { }
                var c = _retryNowCts; if (c != null) { try { c.Cancel(); } catch { } }   // wake the current wait
            };

            _connectingPanel.Controls.Add(_connectingTitle);
            _connectingPanel.Controls.Add(_connectingDetail);
            _connectingPanel.Controls.Add(_retryButton);
            _connectingPanel.Controls.Add(_switchCancelButton);
            Controls.Add(_connectingPanel);
            _connectingPanel.BringToFront();

            _connectingDots = new System.Windows.Forms.Timer { Interval = 450 };
            _connectingDots.Tick += (s, e) =>
            {
                _dotPhase = (_dotPhase + 1) % 4;
                if (_connectingTitle != null) _connectingTitle.Text = "Connecting" + new string('.', _dotPhase);
            };

            Resize += (s, e) => CenterConnectingPanel();
            CenterConnectingPanel();
        }

        /// <summary>UI-FIX-T1: re-pushes the connect card's construction-time colors on a live theme switch
        /// (the border pen already derives from _dark at paint time). No-op until the card exists.</summary>
        private void RecolorConnectingUi()
        {
            if (_connectingPanel == null) return;
            _connectingPanel.BackColor = _dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(250, 250, 252);
            if (_connectingTitle != null) _connectingTitle.ForeColor = _dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            if (_connectingDetail != null) _connectingDetail.ForeColor = _dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(108, 108, 114);
            _connectingPanel.Invalidate(true);
        }

        private void CenterConnectingPanel()
        {
            if (_connectingPanel == null) return;
            _connectingPanel.Location = new Point(
                Math.Max(0, (ClientSize.Width - _connectingPanel.Width) / 2),
                Math.Max(0, (ClientSize.Height - _connectingPanel.Height) / 2));
        }

        private void ShowConnecting(string detail)
        {
            EnsureConnectingUi();
            _chatTitle.Text = "Connecting…";
            if (_connectingDetail != null) _connectingDetail.Text = detail;
            // During a switch, offer Cancel (abort → restore the active account) beside Retry now.
            if (_switchCancelButton != null)
            {
                _switchCancelButton.Visible = _switchInProgress;
                if (_switchInProgress)
                {
                    _retryButton.Location = new Point(_connectingPanel.Width / 2 - _retryButton.Width - 6, 118);
                    _switchCancelButton.Location = new Point(_connectingPanel.Width / 2 + 6, 118);
                }
                else _retryButton.Location = new Point((_connectingPanel.Width - _retryButton.Width) / 2, 118);
            }
            if (!_connectingPanel.Visible) _connectingPanel.Visible = true;
            _connectingPanel.BringToFront();
            CenterConnectingPanel();
            if (_connectingDots != null && !_connectingDots.Enabled) _connectingDots.Start();
        }

        private void HideConnecting()
        {
            if (_connectingDots != null) _connectingDots.Stop();
            if (_connectingPanel != null) _connectingPanel.Visible = false;
            if (_retryNowCts != null) { try { _retryNowCts.Cancel(); } catch { } }
        }

        private void SetConnectingDetail(string detail)
        {
            if (_connectingDetail != null) _connectingDetail.Text = detail;
        }

        /// <summary>Observes a faulted/abandoned Task so its exception can't surface as Unobserved (a hung
        /// connect we tore down will fault once the socket is reset).</summary>
        private static void SwallowFault(System.Threading.Tasks.Task t)
        {
            if (t == null) return;
            t.ContinueWith(x => { var ignore = x.Exception; },
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>True for RPC errors that mean the session is no longer valid.</summary>
        private static bool IsAuthError(Exception ex)
        {
            var rpc = (ex as RpcException) ?? (ex.InnerException as RpcException);
            if (rpc == null) return false;
            var msg = (rpc.Message ?? "").ToUpperInvariant();
            return msg.Contains("AUTH") || msg.Contains("SESSION") || msg.Contains("USER_DEACTIVATED");
        }

        /// <summary>A PERMANENT, unrecoverable session-file error (corrupt/empty/truncated): WTelegram throws
        /// "Exception while reading session file: Value cannot be null. Parameter name: buffer / Use the correct
        /// api_hash/id/key, or delete the file to start a new session". Retrying NEVER fixes it — the file must
        /// be deleted. Distinct from a network drop (transient) or an RPC auth error (re-login).</summary>
        private static bool IsCorruptSessionError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                if (m.IndexOf("session file", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("delete the file to start a new session", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("correct api_hash", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>A silent resume that can't proceed without interactive login: WTelegram throws "You must
        /// provide a config value for phone_number" (no stored phone / unreadable session). PERMANENT for a
        /// silent resume — retrying just re-throws it (the infinite "config value for phone_number" loop). The
        /// account is unusable as-is → caller drops to LoginForm / the next valid account.</summary>
        private static bool IsNeedsLoginError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                if (m.IndexOf("provide a config value", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Shared handler for a failed connect: a corrupt session is deleted + recovered (never a forever
        /// loop); a genuine auth failure drops to the LoginForm.</summary>
        private async System.Threading.Tasks.Task OnConnectFailedAsync()
        {
            HideConnecting();
            if (_connectCorrupt)
            {
                _connectCorrupt = false;
                await RecoverCorruptSessionAsync();
            }
            else FallBackToLogin();
        }

        /// <summary>The active account's session is unreadable: dispose the dead client (release its handle),
        /// DELETE that session (accounts/{id}/ — keep its cache for a same-account re-login), then move on —
        /// switch to another account if one exists, else show the LoginForm. NEVER loops on the bad file.</summary>
        private async System.Threading.Tasks.Task RecoverCorruptSessionAsync()
        {
            long badId = AccountContext.ActiveId;
            bool legacy = AccountContext.LegacyMode || badId == 0;
            _recoveryTried.Add(badId);
            System.Diagnostics.Debug.WriteLine("[SESSION] corrupt session → recovering. activeId=" + badId + " legacy=" + legacy + " (tried=" + _recoveryTried.Count + ")");

            await _service.TeardownForSwitchAsync();   // dispose the broken client → release the corrupt session-file lock

            if (legacy) AccountStore.DeleteLegacySession();
            else await AccountStore.DeleteAccountDirAsync(badId);
            AccountContext.LegacyMode = false;
            AuthManager.Reset();

            // Pick a VALID candidate NOT already tried in this chain and NOT mid-background-delete — so we can't
            // cycle the same broken accounts forever (the bound), nor race the logout cleanup on a deleting account.
            var others = AccountStore.ListAccounts()
                .FindAll(a => a.Id != badId && !_recoveryTried.Contains(a.Id) && !AccountStore.IsDeleting(a.Id));
            if (others.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine("[SESSION] deleted corrupt session → switching to " + others[0].Id);
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(others[0].Id);
                await SwitchAccountAsync(others[0].Id, others[0].Name);   // if it's ALSO corrupt → recurses, but each id is tried once → terminates
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[SESSION] no untried valid account left → LoginForm");
                _recoveryTried.Clear();
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(0);
                ShowLoginForm();
            }
        }

        private void SubscribeUpdates()
        {
            // UpdateManager: ordered per-update delivery + automatic getDifference gap-recovery on reconnect,
            // plus its Users/Chats entity dictionaries. Replaces the raw client.OnUpdates subscription.
            if (_service.Client != null)
                _service.StartUpdateManager(OnManagerUpdate);
        }

        // ── Live updates ─────────────────────────────────────────────────────

        /// <summary>UpdateManager callback (background thread). Marshals each update to the UI thread, in
        /// order (BeginInvoke is FIFO), then returns immediately so the manager proceeds to the next.</summary>
        private System.Threading.Tasks.Task OnManagerUpdate(Update update)
        {
            try
            {
                _service.NoteActivity();   // any update = the server link is alive → reset the watchdog clock
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UM] MGR UPDATE: " + (update != null ? update.GetType().Name : "null"));
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() =>
                    {
                        // Guard the UI-thread dispatch so a handler exception can't crash the app or stall delivery.
                        try { ProcessSingleUpdate(update); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UM] ProcessSingleUpdate EX: " + ex); }
                    }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UM] OnManagerUpdate EX: " + ex); }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>Resolves a peer to its User/ChatBase via the manager's entity dictionary (the source the
        /// per-batch UpdatesBase used to provide). Null when the manager isn't started yet. HARDENED: never
        /// throws — a resolution failure must NOT abort live bubble creation (group sender resolution runs
        /// only for groups, so a throw here would silently drop group messages while channels render fine).</summary>
        private IPeerInfo ResolvePeer(Peer p)
        {
            if (_service.Updates == null || p == null) return null;
            try { return _service.Updates.UserOrChat(p); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UM] ResolvePeer EX: " + ex.Message); return null; }
        }

        /// <summary>
        /// THE single funnel for an RPC send/forward RESULT (UpdatesBase). RPC-returned updates are NOT pushed
        /// through the live UpdateManager, so without this a send-path silently skips the chat-list refresh —
        /// the recurring "forwarded/own-sent message doesn't update the left panel" bug. Routing the result's
        /// updates through the SAME <see cref="ProcessSingleUpdate"/> the manager uses means the chat-list
        /// refresh (<see cref="UpdateChatListForMessage"/>: preview + re-order + unread) happens via ONE path.
        /// Any new send-path that returns UpdatesBase should call this and is then refreshed for free. De-dupe
        /// (_shownMessageIds / TrySwapPendingBubble) makes a later server echo of the same message a no-op.
        /// </summary>
        private void ApplySendResult(UpdatesBase result)
        {
            if (result == null) return;
            var ups = result.UpdateList;
            if (ups == null) return;
            foreach (var u in ups)
            {
                try { ProcessSingleUpdate(u); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UPDATE] ApplySendResult EX: " + ex.Message); }
            }
        }

        /// <summary>Dispatches ONE update (the manager delivers updates individually, not in UpdatesBase
        /// batches). Body is identical to the former ProcessUpdates loop — every handler is preserved.</summary>
        private void ProcessSingleUpdate(Update update)
        {
            long __t = PerfLog.T();
            ProcessSingleUpdateCore(update);
            PerfLog.Rec(PerfLog.P.Update, __t);
        }

        private void ProcessSingleUpdateCore(Update update)
        {
            if (LogOn) System.Diagnostics.Debug.WriteLine("[UM] ProcessSingleUpdate: " + (update != null ? update.GetType().Name : "null"));
            if (update is UpdateNewMessage unm && unm.message is MessageBase mb)
                HandleIncomingMessage(mb);
            else if (update is UpdateReadHistoryOutbox ro)
                HandleReadOutbox(ro.peer?.ID ?? 0, ro.max_id);
            else if (update is UpdateReadChannelOutbox rco)
                HandleReadOutbox(rco.channel_id, rco.max_id);
            else if (update is UpdateReadHistoryInbox ri)        // read on another device (users/groups)
                HandleReadInbox(ri.peer?.ID ?? 0, ri.max_id, ri.still_unread_count);
            else if (update is UpdateReadChannelInbox rci)       // read on another device (channels)
                HandleReadInbox(rci.channel_id, rci.max_id, rci.still_unread_count);
            // Remote deletion (sender/admin/another device). NOTE: UpdateDeleteChannelMessages : UpdateDeleteMessages,
            // so the channel variant (peer-scoped via channel_id) MUST be matched first.
            else if (update is UpdateDeleteChannelMessages udcm)
                HandleDeletedMessages(udcm.messages, udcm.channel_id);
            else if (update is UpdateDeleteMessages udm)
                HandleDeletedMessages(udm.messages, 0);          // non-channel: ids aren't peer-scoped → match the open chat
            else if (update is UpdateWebPage uw && uw.webpage is WebPage wpg)   // delayed link preview arrived
                HandleWebPageUpdate(wpg);
            else if (update is UpdateEditMessage uem && uem.message is Message em)        // edit / preview populated
                HandleEditMessage(em);
            else if (update is UpdateEditChannelMessage uecm && uecm.message is Message ecm)
                HandleEditMessage(ecm);
            else if (update is UpdateMessagePoll ump)        // live poll results (others voting)
                HandlePollUpdate(ump);
            else if (update is UpdatePinnedMessages upm)
            { if (_selectedChat != null && (upm.peer?.ID ?? 0) == _selectedChat.PeerId) LoadPinnedAsync(_selectedChat); }
            else if (update is UpdatePinnedChannelMessages upcm)
            { if (_selectedChat != null && upcm.channel_id == _selectedChat.PeerId) LoadPinnedAsync(_selectedChat); }
            else if (update is UpdateNotifySettings uns)
            {
                // Per-peer OR category-level (muting "All groups" etc. in the official client) — both live.
                if (uns.peer is NotifyPeer np && np.peer != null)
                    HandleNotifySettings(np.peer.ID, uns.notify_settings);
                else if (uns.peer is NotifyUsers) _muteDefUsers = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue;
                else if (uns.peer is NotifyChats) _muteDefChats = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue;
                else if (uns.peer is NotifyBroadcasts) _muteDefBroadcasts = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue;
            }
            else if (update is UpdateUserStatus ust)
            {
                // PRESENCE 2.2: cache on the entry (the row painter reads ONLY OnlineUntil — hot-path law),
                // keep the PeerInfo User fresh (header/member formatters), invalidate the visible row.
                var pent = _allChats.FirstOrDefault(c => c.PeerId == ust.user_id);
                if (pent != null)
                {
                    if (pent.PeerInfo is User pu) pu.status = ust.status;
                    pent.OnlineUntil = ust.status is UserStatusOnline uon && uon.expires > DateTime.UtcNow
                        ? uon.expires : default(DateTime);
                    FindChatItem(ust.user_id)?.Invalidate();   // dot appears/drops live
                }
                if (_selectedChat != null && _selectedChat.PeerId == ust.user_id && _selectedChat.PeerInfo is User su)
                { su.status = ust.status; UpdateHeaderStatus(); }
                try { UserStatusChanged?.Invoke(ust.user_id, ust.status); } catch { /* subscriber fault must not kill the update pump */ }
            }
            else if (update is UpdateUserTyping utp && _selectedChat?.PeerId == utp.user_id)
                ShowTypingFor(utp.action);
            else if (update is UpdateChatUserTyping cutp && _selectedChat?.PeerId == cutp.chat_id)
                ShowTypingFor(cutp.action, NameOf(cutp.from_id));
            else if (update is UpdateChannelUserTyping chutp && _selectedChat?.PeerId == chutp.channel_id)
                ShowTypingFor(chutp.action, NameOf(chutp.from_id));
        }

        /// <summary>True when a peer's notify settings mute it (mute_until in the future).</summary>
        private static bool IsMuted(PeerNotifySettings ns)
        {
            return ns != null && ns.mute_until > DateTime.UtcNow;
        }

        // ── NOTIFY-FIX: category notify defaults + effective-mute resolution ──
        // A peer with NO explicit notify setting inherits its category default (users / group chats /
        // broadcasts). Fetched per connect (AfterConnectAsync), refreshed live by category-level
        // UpdateNotifySettings. MinValue = not muted (also the safe value when the fetch fails).
        private DateTime _muteDefUsers = DateTime.MinValue, _muteDefChats = DateTime.MinValue, _muteDefBroadcasts = DateTime.MinValue;

        /// <summary>The EXPLICIT mute_until of a settings object, or null when the peer has no explicit
        /// setting (flag absent) and inherits its category. Distinct from IsMuted: a PAST value here means
        /// "explicitly unmuted", which overrides a muted category.</summary>
        private static DateTime? MuteUntilOf(PeerNotifySettings ns)
        {
            return ns != null && (ns.flags & PeerNotifySettings.Flags.has_mute_until) != 0
                ? (DateTime?)ns.mute_until : null;
        }

        /// <summary>Fetches the three category notify defaults post-connect (best-effort: a failure leaves
        /// MinValue = not muted, i.e. the pre-fix behavior).</summary>
        private async System.Threading.Tasks.Task FetchNotifyDefaultsAsync()
        {
            var users = await _service.GetNotifyDefaultsAsync(new InputNotifyUsers());
            var chats = await _service.GetNotifyDefaultsAsync(new InputNotifyChats());
            var bcast = await _service.GetNotifyDefaultsAsync(new InputNotifyBroadcasts());
            _muteDefUsers = MuteUntilOf(users) ?? DateTime.MinValue;
            _muteDefChats = MuteUntilOf(chats) ?? DateTime.MinValue;
            _muteDefBroadcasts = MuteUntilOf(bcast) ?? DateTime.MinValue;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[NOTIFY] category defaults: users="
                + _muteDefUsers.ToString("u") + " chats=" + _muteDefChats.ToString("u") + " broadcasts=" + _muteDefBroadcasts.ToString("u"));
        }

        /// <summary>The notify gate's mute resolution: the peer's explicit setting when present (a past
        /// explicit value = unmuted, overriding the category), else the category default.</summary>
        private bool IsEffectivelyMuted(ChatEntry entry, out string reason)
        {
            reason = null;
            if (entry == null) return false;                          // unknown chat → can't be muted
            if (entry.Muted) { reason = "muted"; return true; }       // explicit mute (incl. optimistic toggle)
            if (entry.MuteUntil.HasValue) return false;               // explicit setting, not muted → overrides category
            DateTime cat = entry.PeerInfo is User ? _muteDefUsers
                : entry.PeerInfo is Channel bc && (bc.flags & Channel.Flags.broadcast) != 0 ? _muteDefBroadcasts
                : entry.PeerInfo != null ? _muteDefChats
                : entry.IsGroup ? _muteDefChats : _muteDefUsers;      // unresolved peer → best guess by kind
            if (cat > DateTime.UtcNow) { reason = "category-muted"; return true; }
            return false;
        }

        private string NameOf(Peer p)
        {
            if (p == null) return "Someone";
            if (_peerNames.TryGetValue(p.ID, out var n) && !string.IsNullOrEmpty(n)) return n;
            var info = ResolvePeer(p);   // fall back to the manager's entity dictionary
            if (info is User u) return DisplayName(u);
            if (info is ChatBase cb) return cb.Title;
            return "Someone";
        }

        /// <summary>Applies a live mute/unmute change to the chat list.</summary>
        private void HandleNotifySettings(long peerId, PeerNotifySettings ns)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry == null) return;
            entry.Muted = IsMuted(ns);
            entry.MuteUntil = MuteUntilOf(ns);   // explicit-vs-inherited for the notify gate (NOTIFY-FIX)
            // MUTE-PERSIST: the self-echo of our own mute write lands here — its presence in the log is the
            // proof the server ACCEPTED the write (its absence after a mute tap = the write was a no-op).
            if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[NOTIFY] settings update peer=" + peerId
                + " muted=" + entry.Muted + " muteUntil=" + (entry.MuteUntil.HasValue ? entry.MuteUntil.Value.ToString("u") : "inherit"));
            FindChatItem(peerId)?.Invalidate();
            if (_selectedChat != null && _selectedChat.PeerId == peerId)
                ResolveAndApplyComposer(_selectedChat);   // live mute/unmute updates the footer label
        }

        /// <summary>The peer read my messages up to <paramref name="maxId"/> → flip those bubbles to ✓✓.</summary>
        private void HandleReadOutbox(long peerId, int maxId)
        {
            if (peerId == 0) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry != null && maxId > entry.ReadOutboxMaxId) entry.ReadOutboxMaxId = maxId;

            if (_selectedChat == null || _selectedChat.PeerId != peerId) return;
            if (maxId > _readOutboxMaxId) _readOutboxMaxId = maxId;
            foreach (Control c in _messagePanel.Controls)
                if (c is MessageBubbleControl b && b.Outgoing && !b.Read && b.MessageId > 0 && b.MessageId <= maxId)
                {
                    b.Read = true;
                    b.Invalidate();
                }
        }

        /// <summary>
        /// Inbox read on ANOTHER device → sync the unread badge here in place. The server gives the
        /// authoritative remaining count (still_unread_count), so set it directly and repaint the row
        /// (badge clears at 0; its color still follows the muted rule). No full reload.
        /// </summary>
        private void HandleReadInbox(long peerId, int maxId, int stillUnread)
        {
            if (peerId == 0) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry == null) return;
            if (maxId > entry.ReadInboxMaxId) entry.ReadInboxMaxId = maxId;
            entry.UnreadCount = Math.Max(0, stillUnread);
            FindChatItem(peerId)?.Invalidate();
            UpdateTrayTooltip();
        }

        /// <summary>Remote deletion → remove the deleted messages' bubbles in the OPEN chat (the SAME dispose
        /// path user-delete uses: Controls.Remove + Dispose → InlineText/text-bitmap freed; VoiceBubble cancels
        /// its download) and, if the chat's LATEST was deleted, refresh its chat-list preview via the single
        /// <see cref="UpdateChatListForMessage"/> path. Peer-correct: a channel delete (channel_id) applies only
        /// to that open channel; a non-channel delete (ids NOT peer-scoped) only when a non-channel chat is open.</summary>
        private void HandleDeletedMessages(int[] ids, long channelId)
        {
            if (ids == null || ids.Length == 0) return;
            var set = new HashSet<int>(ids);

            // ── OPEN chat: strip the deleted bubbles from the live view (the row itself is re-synced below,
            //    together with every other affected chat, so preview/badge/order stay authoritative). ──
            if (_selectedChat != null)
            {
                bool openIsChannel = _selectedChat.Peer is InputPeerChannel;
                // channel deletion → the open chat must BE that channel; non-channel ids → the open non-channel chat.
                bool targetsOpen = channelId != 0 ? (openIsChannel && _selectedChat.PeerId == channelId) : !openIsChannel;
                if (targetsOpen)
                {
                    int removed = 0;
                    foreach (var c in _messagePanel.Controls.Cast<Control>().ToArray())
                    {
                        int id = BubbleMsgId(c);
                        if (id != 0 && set.Contains(id))
                        {
                            _messagePanel.Controls.Remove(c);
                            c.Dispose();   // MessageBubbleControl.Dispose → _rich.Dispose → text bitmap freed (same as user-delete)
                            removed++;
                        }
                    }
                    _currentChatMessages.RemoveAll(x => set.Contains(x.ID));
                    foreach (var id in ids) _shownMessageIds.Remove(id);
                    if (_replyTarget != null && set.Contains(_replyTarget.ID)) CancelReply();
                    System.Diagnostics.Debug.WriteLine("[UPDATE] delete(open) ids=" + ids.Length + " removed=" + removed + " peer=" + _selectedChat.PeerId);
                }
            }

            // ── Chat-list rows: any chat (open or not) whose TOP message was deleted needs its row re-synced —
            //    a lingering deleted preview is a PRIVACY leak, a phantom unread badge is wrong, and the chat
            //    must fall back to its NEW top message's date/position (not stay pinned at the top). Channel
            //    deletion carries channel_id; non-channel message ids are ACCOUNT-GLOBAL so a TopMessageId
            //    match uniquely identifies the affected dialog. ──
            List<ChatEntry> stale = null;
            foreach (var entry in _allChats)
            {
                bool entryIsChannel = entry.Peer is InputPeerChannel;
                if (channelId != 0) { if (!(entryIsChannel && entry.PeerId == channelId)) continue; }
                else if (entryIsChannel) continue;   // a non-channel deletion can't touch a channel dialog
                if (entry.TopMessageId != 0 && set.Contains(entry.TopMessageId))
                    (stale ?? (stale = new List<ChatEntry>())).Add(entry);
            }
            if (stale != null)
                foreach (var e in stale)
                {
                    System.Diagnostics.Debug.WriteLine("[UPDATE] delete(top) peer=" + e.PeerId + " top=" + e.TopMessageId + " → resync dialog");
                    ResyncDialogAfterDeleteAsync(e.PeerId, e.Peer);
                }
        }

        /// <summary>Re-syncs a chat-list row from the SERVER dialog after its top message was deleted elsewhere:
        /// authoritative preview + unread badge + new top's date, then re-sorts the list so the chat drops to
        /// its correct chronological position (the new top's date), not staying pinned at the top. Bounded,
        /// best-effort; blanks + clears the badge when the chat is now empty OR the fetch fails (a stale deleted
        /// preview is a privacy leak, so removal wins — the next full dialog refresh restores it). Discards if
        /// the account/chat changed under it.</summary>
        private async void ResyncDialogAfterDeleteAsync(long peerId, InputPeer peer)
        {
            Dialog dlg = null;
            MessageBase top = null;
            bool fetchOk = false;
            try
            {
                var pd = await _service.GetPeerDialogAsync(peer);
                fetchOk = pd != null;
                dlg = pd?.dialogs?.OfType<Dialog>().FirstOrDefault(d => (d.peer?.ID ?? 0) == peerId)
                      ?? pd?.dialogs?.OfType<Dialog>().FirstOrDefault();
                if (dlg != null && pd.messages != null)
                    top = pd.messages.FirstOrDefault(mb => mb.ID == dlg.top_message);
            }
            catch (Exception ex) { if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] resync failed peer=" + peerId + ": " + ex.Message); }

            if (IsDisposed) return;
            var entry = _allChats.FirstOrDefault(e => e.PeerId == peerId);
            if (entry == null) return;

            if (dlg != null)
            {
                entry.UnreadCount = dlg.unread_count;                 // authoritative → clears a phantom badge
                entry.TopMessageId = dlg.top_message;
                entry.ReadInboxMaxId = dlg.read_inbox_max_id;
                entry.ReadOutboxMaxId = dlg.read_outbox_max_id;
                if (top != null) { entry.Preview = GetDisplayText(top); entry.Date = MsgDate(top); }   // new top's date → re-sorts down
                else { entry.Preview = ""; if (dlg.top_message == 0) entry.Date = default(DateTime); }  // no text available → blank (privacy)
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] resync peer=" + peerId + " top=" + dlg.top_message
                    + " unread=" + dlg.unread_count + " preview='" + (entry.Preview ?? "") + "'");
            }
            else
            {
                // No dialog (fetch failed / chat gone) → blank the leaked text + clear the badge for privacy.
                entry.Preview = ""; entry.TopMessageId = 0; entry.UnreadCount = 0;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] resync peer=" + peerId + " → " + (fetchOk ? "no dialog" : "fetch failed") + ", blanked");
            }

            RenderChatList(_searchBox.Text);   // re-sort: the chat moves to its new top's date position
            UpdateTrayTooltip();               // unread total changed
        }

        /// <summary>A populated WebPage arrived (updateWebPage) → fill in the link-preview card in place.</summary>
        private void HandleWebPageUpdate(WebPage wp)
        {
            if (_selectedChat == null) return;
            foreach (var m in _currentChatMessages)
            {
                // Match the open-chat message whose pending preview carries this web-page id.
                if (m.media is MessageMediaWebPage mw && mw.webpage != null && mw.webpage.ID == wp.ID)
                {
                    mw.webpage = wp;            // populate the cached message so a later scroll/redraw keeps it
                    RebuildBubble(m.ID, m);     // re-fold the card into the bubble in place (DRY via MakeMessageBubble)
                    return;
                }
            }
        }

        /// <summary>A message was edited / re-delivered (UpdateEditMessage) → refresh its bubble in place
        /// (covers a preview becoming populated, an added/removed link card, or a text edit).</summary>
        private void HandleEditMessage(Message em)
        {
            long peerId = em.peer_id?.ID ?? 0;
            if (_selectedChat == null || peerId != _selectedChat.PeerId) return;
            int idx = _currentChatMessages.FindIndex(x => x.ID == em.ID);
            if (idx >= 0) _currentChatMessages[idx] = em;   // keep the cache current
            if (_messagePanel.Controls.OfType<MessageBubbleControl>().Any(b => b.MessageId == em.ID))
                RebuildBubble(em.ID, em);
        }

        private void HandleIncomingMessage(MessageBase mbase)
        {
            if (mbase is MessageService svc) { HandleServiceMessage(svc); return; }
            var m = mbase as Message;
            if (m == null) return;

            long peerId = m.peer_id?.ID ?? 0;
            if (peerId == 0) return;
            bool outgoing = IsOut(m);

            bool isOpen = _selectedChat != null && peerId == _selectedChat.PeerId;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[UM] incoming id=" + m.ID + " peer=" + peerId
                + " openPeer=" + (_selectedChat != null ? _selectedChat.PeerId.ToString() : "none")
                + " match=" + isOpen + " out=" + outgoing + " atTail=" + _atLiveTail
                + " isGroup=" + (_selectedChat != null && _selectedChat.IsGroup));

            // A bot message may (re)set the chat-level reply keyboard (distinct from inline buttons).
            if (isOpen && !outgoing && m.reply_markup != null) ApplyChatReplyMarkup(m.reply_markup);

            // If this is the open conversation, append the bubble live (deduped).
            if (isOpen)
            {
                // Telegram rule: only auto-scroll if the view is already at/near the bottom. If the user
                // is reading up, keep the exact offset and bump the floating button's unread count.
                bool wasAtBottom = AtBottom(AtBottomThreshold);
                int keepY = -_messagePanel.AutoScrollPosition.Y;

                string sender = null;
                IPeerInfo from = null;
                if (!outgoing && _selectedChat.IsGroup && m.from_id != null)
                {
                    from = ResolvePeer(m.from_id);
                    sender = from is User fu ? DisplayName(fu) : (from as ChatBase)?.Title;
                    if (from is User cu) _peerNames[cu.id] = DisplayName(cu);
                }
                if (m.fwd_from != null) CacheForwardName(ResolvePeer, m.fwd_from);   // resolve "Forwarded from X"
                // An outgoing media echo may belong to an optimistic "pending" bubble we
                // already placed (paperclip / drag-drop). Swap it in place instead of
                // adding a duplicate — the echo can beat SendMediaDialog's FileSucceeded
                // because the dialog is modal and its message loop pumps updates while
                // the upload awaits. Whichever of the two fires first does the swap.
                if (!_atLiveTail && !outgoing)
                {
                    // Focused island, not the live tail → don't graft a newer message onto the island's
                    // bottom; just count it. It loads when the user pages down or jumps to the bottom.
                    System.Diagnostics.Debug.WriteLine("[UM] → ISLAND DEFER (bump only) id=" + m.ID);
                    _jumpUnread++;
                    OnScrollPositionChanged();
                }
                else if (outgoing && HasMedia(m) && TrySwapPendingBubble(m))
                {
                    System.Diagnostics.Debug.WriteLine("[UM] → swapped pending bubble id=" + m.ID);
                    _currentChatMessages.Add(m);
                }
                else if (AddMessageBubble(_selectedChat, m, sender, from))
                {
                    System.Diagnostics.Debug.WriteLine("[UM] → APPENDED bubble id=" + m.ID + " wasAtBottom=" + wasAtBottom + " out=" + outgoing);
                    _currentChatMessages.Add(m);
                    if (m.ID > _oldestMessageId && _oldestMessageId == 0) _oldestMessageId = m.ID;
                    // Reveal the new message when we're following the live tail: either the view is already at
                    // the bottom (any sender), OR it's our OWN message echoed from another device — the user
                    // just sent it, so show it instead of leaving it below the fold with no scroll/indicator.
                    if (wasAtBottom || (outgoing && _atLiveTail))
                    {
                        ScrollMessagesToBottom();
                        MarkCaughtUp();                 // we're following along → read it
                    }
                    else
                    {
                        _messagePanel.AutoScrollPosition = new Point(0, keepY);   // keep position (don't yank)
                        if (!outgoing) _jumpUnread++;
                        OnScrollPositionChanged();      // show/refresh the floating button + count
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[UM] → AddMessageBubble returned FALSE (dedup/null) id=" + m.ID);
                }
                KeepSponsoredLast();   // a new live post must appear ABOVE the sponsored card
            }

            UpdateChatListForMessage(peerId, m, outgoing);
            MaybeToast(peerId, m, outgoing);
        }

        /// <summary>A live service event (X joined / pinned / …): append the centered line if the chat is
        /// open (respecting the at-bottom rule) and refresh its chat-list preview (no unread bump).</summary>
        private void HandleServiceMessage(MessageService svc)
        {
            long peerId = svc.peer_id?.ID ?? 0;
            if (peerId == 0) return;
            CacheServiceNames(ResolvePeer, svc);

            if (_selectedChat != null && peerId == _selectedChat.PeerId)
            {
                bool wasAtBottom = AtBottom(AtBottomThreshold);
                if (AddMessageBubble(_selectedChat, svc, null, null) && wasAtBottom)
                    ScrollMessagesToBottom();
            }

            UpdateChatListForMessage(peerId, svc, outgoing: true);   // preview/date/reorder, no unread bump
        }

        private static bool HasMedia(Message m)
        {
            return m.media is MessageMediaPhoto || m.media is MessageMediaDocument;
        }

        /// <summary>
        /// Reconciles an outgoing server echo with the in-flight optimistic media bubble:
        /// repoints it to the real message id and clears its "pending" state, in place.
        /// Sends are sequential (one pending bubble at a time), so the match is the oldest
        /// still-pending bubble (highest temp id — ids are allocated -1, -2, -3…), which
        /// also matches FIFO echo order. Returns false when there's nothing to swap, so
        /// the caller falls through to the normal add/dedupe path.
        /// </summary>
        private bool TrySwapPendingBubble(Message m)
        {
            if (_pendingBubbles.Count == 0) return false;

            int tempId = 0;
            MessageBubbleControl bubble = null;
            foreach (var kv in _pendingBubbles)
                if (bubble == null || kv.Key > tempId) { tempId = kv.Key; bubble = kv.Value; }
            if (bubble == null) return false;

            _pendingBubbles.Remove(tempId);
            _shownMessageIds.Remove(tempId);
            _shownMessageIds.Add(m.ID);     // dedupe any further echo of the same message
            bubble.MessageId = m.ID;
            bubble.Pending = false;
            bubble.Failed = false;
            _messagePanel.PerformLayout();
            bubble.Invalidate();
            return true;
        }

        /// <summary>
        /// THE single chat-list (left panel) refresh for one message — preview + timestamp + re-order + unread.
        /// EVERY send/receive must reach here: received via <see cref="HandleIncomingMessage"/>; own text/media
        /// directly with the sent Message; round-video via HandleIncomingMessage; forwards via
        /// <see cref="ApplySendResult"/> → ProcessSingleUpdate → HandleIncomingMessage. A new send-path that
        /// doesn't end up here will silently fail to refresh the list — route it through ApplySendResult (if it
        /// has an UpdatesBase) or HandleIncomingMessage (if it has a Message), never re-implement the refresh.
        /// </summary>
        private void UpdateChatListForMessage(long peerId, MessageBase m, bool outgoing)
        {
            long __t = PerfLog.T();
            UpdateChatListForMessageCore(peerId, m, outgoing);
            PerfLog.Rec(PerfLog.P.ChatRefresh, __t);
        }

        /// <summary>The peer_id of a live message (Message or MessageService); null for neither.</summary>
        private static Peer PeerOf(MessageBase mb)
            => mb is Message mm ? mm.peer_id : mb is MessageService ms ? ms.peer_id : null;

        private void UpdateChatListForMessageCore(long peerId, MessageBase m, bool outgoing)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry == null)
            {
                // LIST-UPDATE-SYNC: the chat isn't in _allChats yet — it sits below the initial dialog page
                // (paged in on scroll since DPI-REVERT) or was opened via search/mention as a throwaway entry.
                // The notify path fires for it regardless (MaybeToast tolerates a null entry), so the list MUST
                // surface it too, or the two diverge. Resolve the peer from the update's own entities and INSERT
                // a fresh row (the official clients float any messaged dialog to the top). A null resolve (rare —
                // entity not carried) falls back to the old give-up: the toast still fired, and the next full
                // dialog refresh will include it. No duplicate risk: RenderChatListCore + paging both dedup by PeerId.
                var info = ResolvePeer(PeerOf(m));
                entry = info != null ? EntryFromPeerInfo(info) : null;
                if (entry == null) return;
                _allChats.Add(entry);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] chat-list INSERT new peer=" + peerId + " title='" + (entry.Title ?? "") + "'");
            }

            entry.Preview = GetDisplayText(m);
            entry.Date = MsgDate(m);
            entry.TopMessageId = m.ID;
            if (!outgoing && entry != _selectedChat)
                entry.UnreadCount += 1;

            if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] chat-list refresh peer=" + peerId
                + " preview='" + (entry.Preview ?? "") + "' unread=" + entry.UnreadCount + " out=" + outgoing);

            var item = FindChatItem(peerId);

            // Archive filter (place #2): an archived chat (or one not matching the current view) must
            // NOT appear here — e.g. a pinned+archived chat shows ONLY in Archive, never in All.
            if (!IsVisibleInCurrentView(entry))
            {
                if (item != null) { _chatListPanel.Controls.Remove(item); item.Dispose(); }
                UpdateTrayTooltip();
                return;
            }

            if (item == null) { RenderChatList(_searchBox.Text); UpdateTrayTooltip(); return; }   // should show but absent

            // Pinned-in-THIS-view chats keep their position; non-pinned move to the TOP OF THE NON-PINNED
            // SECTION — index just after the last pinned-in-view row, never absolute 0.
            if (!IsPinnedInView(entry))
                _chatListPanel.Controls.SetChildIndex(item, PinnedBoundary());
            item.Invalidate();

            UpdateTrayTooltip();
        }

        private ChatListItemControl FindChatItem(long peerId)
        {
            return _chatListPanel.Controls.OfType<ChatListItemControl>()
                .FirstOrDefault(c => c.Entry.PeerId == peerId);
        }

        /// <summary>Builds ChatEntry rows from a dialogs response. Archived state + pin order are read from
        /// each dialog's OWN folder_id (data-driven), NOT from which load returned it.</summary>
        private List<ChatEntry> BuildDialogEntries(Messages_DialogsBase dialogs)
        {
            var list = new List<ChatEntry>();
            if (dialogs == null) return list;

            var topMessages = new Dictionary<(long, int), MessageBase>();
            foreach (var mb in dialogs.Messages)
            {
                if (mb is Message m) topMessages[(m.peer_id?.ID ?? 0, m.ID)] = m;
                else if (mb is MessageService s) topMessages[(s.peer_id?.ID ?? 0, s.ID)] = s;
            }

            int mainPinSeq = 0, archivePinSeq = 0;   // the API returns pinned dialogs first, in pin order (per folder)
            foreach (var d in dialogs.Dialogs)
            {
                var peer = d.Peer;
                if (peer == null) continue;

                string title;
                InputPeer inputPeer;
                bool isGroup = false;

                var resolved = dialogs.UserOrChat(peer);
                if (resolved is User user) { title = DisplayName(user); inputPeer = user.ToInputPeer(); }
                else if (resolved is ChatBase chat)
                {
                    if (!chat.IsActive) continue;
                    title = chat.Title; inputPeer = chat.ToInputPeer(); isGroup = true;
                }
                else continue;

                string preview = "";
                DateTime date = default;
                if (topMessages.TryGetValue((peer.ID, d.TopMessage), out var top))
                {
                    if (top is MessageService tsvc) CacheServiceNames(dialogs.UserOrChat, tsvc);
                    preview = GetDisplayText(top);
                    date = MsgDate(top);
                }

                // Bucketing is DATA-DRIVEN off the dialog's OWN folder_id (folder 1 = Archive, else main),
                // so a folder-0 pinned dialog returned in any response is correctly non-archived.
                var dlg = d as Dialog;
                bool isArchived = dlg != null && dlg.folder_id == 1;
                bool pinned = dlg != null && (dlg.flags & Dialog.Flags.pinned) != 0;
                list.Add(new ChatEntry
                {
                    Peer = inputPeer,
                    PeerId = peer.ID,
                    Title = title,
                    Preview = preview,
                    Date = date,
                    UnreadCount = dlg?.unread_count ?? 0,
                    ReadOutboxMaxId = dlg?.read_outbox_max_id ?? 0,
                    ReadInboxMaxId = dlg?.read_inbox_max_id ?? 0,
                    TopMessageId = d.TopMessage,
                    IsGroup = isGroup,
                    PeerInfo = resolved,
                    OnlineUntil = resolved is User ru && ru.status is UserStatusOnline ron ? ron.expires : default(DateTime),
                    Muted = IsMuted(dlg?.notify_settings),
                    MuteUntil = MuteUntilOf(dlg?.notify_settings),
                    MainPinOrder = (!isArchived && pinned) ? mainPinSeq++ : -1,
                    ArchivePinOrder = (isArchived && pinned) ? archivePinSeq++ : -1,
                    Archived = isArchived
                });
            }
            return list;
        }

        /// <summary>Loads archived dialogs (folder_id = 1) into the model and refreshes the folder bar.</summary>
        private async void LoadArchivedAsync()
        {
            try
            {
                var arch = await _service.GetArchivedDialogsAsync();
                if (IsDisposed || arch == null) return;
                // The archive load OWNS only genuinely-archived dialogs (folder_id == 1). Any folder-0 chat
                // that leaks into this response is dropped here so it can NEVER flip a main chat to archived.
                var archEntries = BuildDialogEntries(arch).Where(e => e.Archived).ToList();
                var ids = new HashSet<long>(archEntries.Select(e => e.PeerId));
                _allChats.RemoveAll(c => c.Archived || ids.Contains(c.PeerId));   // dedup by PeerId; archived wins for these
                _allChats.AddRange(archEntries);
                RebuildFolders();
                RenderChatList(_searchBox.Text);
                DumpPinnedEntries();   // diagnostic: confirm bucketing on-device
            }
            catch { /* archive is best-effort */ }
        }

        /// <summary>One-time debug dump of every pinned entry, to confirm pin/archive bucketing on-device.</summary>
        private void DumpPinnedEntries()
        {
            try
            {
                foreach (var e in _allChats)
                    if (e.MainPinOrder >= 0 || e.ArchivePinOrder >= 0)
                        System.Diagnostics.Debug.WriteLine(
                            $"[PIN] id={e.PeerId} \"{e.Title}\" Archived={e.Archived} Main={e.MainPinOrder} Arch={e.ArchivePinOrder}");
            }
            catch { /* archive is best-effort */ }
        }

        // ── TOUCH-FREEZE rate limiters: triggers that ride pan ticks (~100Hz) must space their work.
        // TickCount subtraction is wrap-safe (int arithmetic). ──
        private int _lastLoadOlderDoneTick;      // spaces load-older COMPLETIONS (head check + finally stamp)
        private int _lastNearTopAttemptTick;     // de-storms the touch near-top trigger
        private int _lastHealTick;               // at most one reconcile heal per 3s
        private int _lastListPageAttemptTick;    // de-storms the chat-list near-bottom evaluation

        // ── Chat-list paging state (DPI-REVERT addendum). Offsets follow TL getDialogs pagination:
        // date/id/peer of the LAST dialog's top message in the previous page, in RESPONSE order. ──
        private bool _dlgLoadingMore, _dlgExhausted;
        private DateTime _dlgOffsetDate;
        private int _dlgOffsetId;
        private InputPeer _dlgOffsetPeer;

        /// <summary>Advances the pagination cursor past this page and detects exhaustion — a full
        /// Messages_Dialogs (non-slice) response means the server delivered EVERYTHING that's left.</summary>
        private void CaptureDialogOffsets(Messages_DialogsBase page)
        {
            try
            {
                if (page == null || page.Dialogs == null || page.Dialogs.Length == 0) { _dlgExhausted = true; return; }
                _dlgExhausted = !(page is Messages_DialogsSlice);
                Dialog last = null;
                foreach (var d in page.Dialogs) if (d is Dialog dd) last = dd;   // last in response order
                if (last == null) { _dlgExhausted = true; return; }
                var resolved = page.UserOrChat(last.Peer);
                var user = resolved as User; var chat = resolved as ChatBase;
                _dlgOffsetPeer = user != null ? user.ToInputPeer() : chat != null ? chat.ToInputPeer() : null;
                _dlgOffsetId = last.TopMessage;
                _dlgOffsetDate = default(DateTime);
                foreach (var mb in page.Messages)
                {
                    long pid = mb is Message mm ? (mm.peer_id?.ID ?? 0) : mb is MessageService ms ? (ms.peer_id?.ID ?? 0) : 0;
                    if (mb.ID == last.TopMessage && pid == last.Peer.ID) { _dlgOffsetDate = MsgDate(mb); break; }
                }
                if (_dlgOffsetPeer == null) _dlgExhausted = true;   // can't build a valid cursor → stop cleanly
            }
            catch { _dlgExhausted = true; }
        }

        /// <summary>Near-bottom evaluation for the chat list — fed by Scroll, wheel-then-check, and
        /// TouchScroller.Scrolled, mirroring the message panel's three trigger paths.</summary>
        private void CheckChatListPaging()
        {
            if (IsDisposed || _dlgExhausted || _dlgLoadingMore || _chatListPanel == null) return;
            if (Environment.TickCount - _lastListPageAttemptTick < 150) return;   // pan ticks arrive at ~100Hz
            _lastListPageAttemptTick = Environment.TickCount;
            int pos = -_chatListPanel.AutoScrollPosition.Y;
            int viewport = _chatListPanel.ClientSize.Height;
            int content = _chatListPanel.DisplayRectangle.Height;
            bool fire = content > viewport && pos + viewport >= content - 200;
            if (LogOn && AppSettings.Instance.KbdDiag)
                System.Diagnostics.Debug.WriteLine("[SCROLL] list near-bottom check pos=" + pos
                    + " viewport=" + viewport + " content=" + content + " → " + (fire ? "fire" : "no"));
            if (fire) { var _ = LoadMoreDialogsAsync(); }
        }

        private async System.Threading.Tasks.Task LoadMoreDialogsAsync()
        {
            if (_dlgLoadingMore || _dlgExhausted || _service == null) return;
            _dlgLoadingMore = true;
            try
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] chat-list load-more requested (offset id=" + _dlgOffsetId + ")");
                var page = await _service.GetDialogsPageAsync(_dlgOffsetDate, _dlgOffsetId, _dlgOffsetPeer);
                if (IsDisposed || page == null) return;
                CaptureDialogOffsets(page);   // advance the cursor FIRST — even an all-known page moves forward
                var fresh = BuildDialogEntries(page);
                var known = new HashSet<long>(_allChats.Select(c => c.PeerId));
                int added = 0;
                foreach (var e in fresh) if (known.Add(e.PeerId)) { _allChats.Add(e); added++; }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] chat-list page merged +" + added
                    + " (total=" + _allChats.Count + ", exhausted=" + _dlgExhausted + ")");
                if (added > 0)
                {
                    _allChats.Sort((a, b) => b.Date.CompareTo(a.Date));
                    int y = -_chatListPanel.AutoScrollPosition.Y;      // keep the user's place through the rebuild
                    RenderChatList(_searchBox.Text);
                    _chatListPanel.AutoScrollPosition = new Point(0, y);
                }
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] chat-list load-more FAILED: " + ex.Message);
            }
            finally { _dlgLoadingMore = false; }
        }

        private async System.Threading.Tasks.Task LoadDialogsAsync()
        {
            try
            {
                _chatTitle.Text = "Loading chats…";
                var dialogs = await _service.GetDialogsAsync();

                _allChats.Clear();
                _allChats.AddRange(BuildDialogEntries(dialogs));
                _dlgLoadingMore = false; _dlgExhausted = false;
                CaptureDialogOffsets(dialogs);   // arm paging from page one

                _allChats.Sort((a, b) => b.Date.CompareTo(a.Date));
                _chatTitle.Text = "Select a chat";
                RenderChatList(_searchBox.Text);
                UpdateTrayTooltip();
                LoadFolders();     // fetch chat folders and build their tabs (non-blocking)
                LoadArchivedAsync(); // fetch archived dialogs into the model (non-blocking)
                WarmUpVlc();       // build VLC's plugin cache in the background so first playback is instant
            }
            catch (Exception ex)
            {
                _chatTitle.Text = "Failed to load chats: " + ex.Message;
            }
        }

        /// <summary>
        /// Pre-warms LibVLC off the UI thread: the first <c>new LibVLC()</c> scans the plugin folder
        /// and builds the on-disk plugin cache, which is slow on RT. Doing it during idle startup
        /// (not on the first tap-to-play) makes playback feel instant. Best-effort and guarded.
        /// </summary>
        private void WarmUpVlc()
        {
            if (!VlcEnvironment.IsAvailable) { System.Diagnostics.Debug.WriteLine("[VLC] warm-up skipped (libVLC not available — extract failed or absent)"); return; }
            System.Diagnostics.Debug.WriteLine("[VLC] init started (background) — first new LibVLC() scans plugins off the UI thread");
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                try { ok = AudioPlayer.TryEnsure(); } catch { }
                System.Diagnostics.Debug.WriteLine("[VLC] init " + (ok ? "ready" : "FAILED") + " in " + sw.ElapsedMilliseconds + "ms (background; UI was never blocked)");
            });
        }

        // ── Chat list ────────────────────────────────────────────────────────

        /// <summary>Whether a chat belongs in the currently-shown view (Archive / All / a custom folder).</summary>
        private bool IsVisibleInCurrentView(ChatEntry c)
        {
            if (_showArchive) return c.Archived;            // Archive tab: archived only
            if (_activeFolder == null) return !c.Archived;  // All: exclude archived
            return MatchesFolder(c, _activeFolder);         // custom folder: its own rules (archived allowed)
        }

        /// <summary>
        /// Pin rank of a chat in the ACTIVE view (never a global flag): All → MainPinOrder,
        /// Archive → ArchivePinOrder, custom folder → index of the peer in the folder's pinned_peers.
        /// &gt;=0 = pinned (lower floats higher), -1 = not pinned in this view.
        /// </summary>
        private int PinRankInView(ChatEntry e)
        {
            if (_showArchive) return e.ArchivePinOrder;
            if (_activeFolder == null) return e.MainPinOrder;
            var pinned = _activeFolder.PinnedPeers;   // the folder's own pinned_peers (peer → index)
            if (pinned != null)
                for (int i = 0; i < pinned.Length; i++)
                    if (PeerIdOf(pinned[i]) == e.PeerId) return i;
            return -1;
        }

        private bool IsPinnedInView(ChatEntry e) => PinRankInView(e) >= 0;

        /// <summary>Telegram order in the active view: pinned (by pin rank asc) above non-pinned (date desc).</summary>
        private int CompareRowsInView(ChatEntry a, ChatEntry b)
        {
            int ra = PinRankInView(a), rb = PinRankInView(b);
            bool pa = ra >= 0, pb = rb >= 0;
            if (pa != pb) return pa ? -1 : 1;        // pinned first
            if (pa) return ra.CompareTo(rb);         // both pinned: this view's pin order
            return b.Date.CompareTo(a.Date);         // both non-pinned: newest first
        }

        /// <summary>Count of rows at the top that are pinned-in-THIS-view = the non-pinned section boundary.</summary>
        private int PinnedBoundary()
        {
            int n = 0;
            foreach (Control c in _chatListPanel.Controls)
            {
                if (c is ChatListItemControl ci && IsPinnedInView(ci.Entry)) n++;
                else break;   // pinned rows are contiguous at the top
            }
            return n;
        }

        private void RenderChatList(string filter)
        {
            TouchScroller.StopMomentum();   // 3.4: the chat list is being rebuilt — a coast must not scroll the new content
            long __t = PerfLog.T();
            // DPI-REVERT addendum: rebuilds fire on EVERY incoming message (busy channels = every few
            // seconds) and used to reset the scroll to the top — the bottom of a long list was practically
            // unreachable, which alone reads as "older chats never load". Keep the user's place.
            int keepY = -_chatListPanel.AutoScrollPosition.Y;
            RenderChatListCore(filter);
            if (keepY > 0) { try { _chatListPanel.AutoScrollPosition = new Point(0, keepY); } catch { } }
            RefreshFolderBadges();   // FOLDER-SIDEBAR: keep per-folder badges live on whichever navigator is shown
            PerfLog.Rec(PerfLog.P.RenderChatList, __t);
        }

        private void RenderChatListCore(string filter)
        {
            _chatListPanel.SuspendLayout();
            _chatListPanel.Controls.Clear();
            _selectedItem = null;

            int w = ContentWidth(_chatListPanel);
            IEnumerable<ChatEntry> q = _allChats.Where(IsVisibleInCurrentView);   // per-view filter (place #1)
            if (!string.IsNullOrWhiteSpace(filter))
                q = q.Where(c => (c.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            // Active-view order: pinned-in-view (by pin rank) above non-pinned (by date desc).
            var ordered = q.ToList();
            ordered.Sort(CompareRowsInView);

            var seen = new HashSet<long>();   // dedup by PeerId → exactly one row per peer
            foreach (var entry in ordered)
            {
                if (!seen.Add(entry.PeerId)) continue;
                var item = new ChatListItemControl(entry)
                {
                    AccentColor = _accent,
                    IsDark = _dark,
                    Width = w,
                    Selected = entry == _selectedChat,
                    PinnedInView = IsPinnedInView(entry)   // pin glyph reflects THIS view, not a global flag
                };
                if (entry == _selectedChat) _selectedItem = item;
                item.Click += OnChatItemClick;
                item.ContextMenuRequested += OnChatContextMenu;
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(entry.PeerId);   // memory hit paints with the row
                if (item.Avatar == null) LoadAvatar(entry);       // else queued ahead of backfill → OnAvatarLoaded repaints
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Telegram-style chat-row menu (pin / mute / read / archive / clear / delete-or-leave).</summary>
        private void OnChatContextMenu(object sender, Point screenPt)
        {
            var entry = (sender as ChatListItemControl)?.Entry;
            if (entry == null) return;
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, IsPinnedInView(entry) ? "📌   Unpin" : "📌   Pin", () => TogglePin(entry));
            AddMenuItem(menu, entry.Muted ? "🔔   Unmute" : "🔕   Mute", () => ToggleChatMute(entry));
            if (entry.UnreadCount > 0)
                AddMenuItem(menu, "✓   Mark as read", () => MarkChatRead(entry));
            else
                AddMenuItem(menu, "●   Mark as unread", () => MarkChatUnread(entry));
            AddMenuItem(menu, entry.Archived ? "📂   Unarchive" : "🗄   Archive", () => ToggleArchive(entry));
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "🧹   Clear history", () => ClearChatHistory(entry));
            AddMenuItem(menu, "🗑   " + (entry.Peer is InputPeerUser ? "Delete chat" : LeaveLabelFor(entry)),
                () => DeleteOrLeaveChat(entry));
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        private async void ToggleChatMute(ChatEntry entry)
        {
            bool target = !entry.Muted;
            bool ok;
            try { ok = await _service.ToggleMuteAsync(entry.Peer, target); }
            catch (Exception ex)
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[NOTIFY] mute write peer=" + entry.PeerId + " mute=" + target + " FAILED ex=" + ex.Message);
                ThemedDialog.Show(this, "Mute", "Couldn't change mute: " + ex.Message, "OK"); return;
            }
            if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[NOTIFY] mute write peer=" + entry.PeerId + " mute=" + target + (ok ? " ok" : " FAILED (server returned false)"));
            if (!ok) { ThemedDialog.Show(this, "Mute", "Telegram didn't accept the change — try again.", "OK"); return; }
            entry.Muted = target;
            // Keep the explicit-setting mirror consistent for the notify gate until the server echo lands
            // (mirrors ToggleMuteAsync's own values: 10 years forward / epoch).
            entry.MuteUntil = target ? DateTime.UtcNow.AddYears(10) : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            FindChatItem(entry.PeerId)?.Invalidate();   // repaint badge color + mute glyph
            if (entry == _selectedChat) ResolveAndApplyComposer(entry);   // refresh the footer Mute/Unmute label
        }

        private async void MarkChatRead(ChatEntry entry)
        {
            try { await _service.ReadHistoryAsync(entry.Peer, entry.TopMessageId); }
            catch (Exception ex) { ThemedDialog.Show(this, "Mark as read", "Couldn't mark read: " + ex.Message, "OK"); return; }
            entry.UnreadCount = 0;
            FindChatItem(entry.PeerId)?.Invalidate();
            UpdateTrayTooltip();
        }

        private async void MarkChatUnread(ChatEntry entry)
        {
            try { await _service.MarkDialogUnreadAsync(entry.Peer, true); }
            catch (Exception ex) { ThemedDialog.Show(this, "Mark as unread", "Couldn't mark unread: " + ex.Message, "OK"); return; }
            if (entry.UnreadCount == 0) entry.UnreadCount = 1;   // local cue (we don't track the unread_mark flag separately)
            FindChatItem(entry.PeerId)?.Invalidate();
            UpdateTrayTooltip();
        }

        private async void ClearChatHistory(ChatEntry entry)
        {
            if (ThemedDialog.Show(this, "Clear history", "Clear all messages in this chat?", "Clear", "Cancel") != 0) return;
            try { await _service.ClearHistoryAsync(entry.Peer); }
            catch (Exception ex) { ThemedDialog.Show(this, "Clear history", "Couldn't clear: " + ex.Message, "OK"); return; }
            entry.Preview = ""; entry.UnreadCount = 0;
            FindChatItem(entry.PeerId)?.Invalidate();
            if (_selectedChat == entry) await LoadHistoryAsync(entry, 0);   // refresh the now-empty open chat
            UpdateTrayTooltip();
        }

        /// <summary>"Delete chat" for a user; "Leave channel"/"Leave group" for a channel/group.</summary>
        private string LeaveLabelFor(ChatEntry entry)
        {
            if (entry.PeerInfo is Channel c)
                return (c.flags & Channel.Flags.broadcast) != 0 ? "Leave channel"
                     : (c.flags & Channel.Flags.megagroup) != 0 ? "Leave group" : "Leave";
            return "Leave group";   // basic (legacy) group
        }

        private async void DeleteOrLeaveChat(ChatEntry entry)
        {
            bool isUser = entry.Peer is InputPeerUser;
            string label = isUser ? "Delete chat" : LeaveLabelFor(entry);
            string body = isUser ? "Delete this chat and its history (your side)?"
                                 : "You'll stop receiving messages from this chat.";
            if (ThemedDialog.Show(this, label, body, label, "Cancel") != 0) return;
            try
            {
                if (isUser) await _service.DeleteChatAsync(entry.Peer, revoke: false);   // delete on my side
                else await _service.LeaveChatAsync(entry.Peer);
            }
            catch (Exception ex) { ThemedDialog.Show(this, label, "Couldn't complete: " + ex.Message, "OK"); return; }

            _allChats.Remove(entry);
            var item = FindChatItem(entry.PeerId);
            if (item != null) { _chatListPanel.Controls.Remove(item); item.Dispose(); }
            if (_selectedChat == entry) { _selectedChat = null; ClearMessagePanel(); _chatTitle.Text = "Select a chat"; }
            RebuildFolders();
            UpdateTrayTooltip();
        }

        private async void TogglePin(ChatEntry entry)
        {
            bool pinning = !IsPinnedInView(entry);
            try { await _service.ToggleDialogPinAsync(entry.Peer, pinning); }
            catch (Exception ex) { ThemedDialog.Show(this, "Pin", "Couldn't change pin: " + ex.Message, "OK"); return; }

            // Reflect in the ACTIVE view's pin rank; a new pin floats to the top of that view's pinned group.
            // (Custom-folder pinning needs updateDialogFilter — not wired here; ToggleDialogPinAsync pins in
            //  the chat's home folder, so the change shows in All/Archive.)
            if (_showArchive)
                entry.ArchivePinOrder = pinning ? MinPinRank(c => c.ArchivePinOrder) - 1 : -1;
            else
                entry.MainPinOrder = pinning ? MinPinRank(c => c.MainPinOrder) - 1 : -1;
            RenderChatList(_searchBox.Text);
        }

        private int MinPinRank(Func<ChatEntry, int> sel)
            => _allChats.Where(c => sel(c) >= 0).Select(sel).DefaultIfEmpty(0).Min();

        private async void ToggleArchive(ChatEntry entry)
        {
            bool archive = !entry.Archived;
            try { await _service.SetChatFolderAsync(entry.Peer, archive ? 1 : 0); }
            catch (Exception ex) { ThemedDialog.Show(this, "Archive", "Couldn't change archive: " + ex.Message, "OK"); return; }
            entry.Archived = archive;       // move in/out of Archive in place
            RebuildFolders();
            RenderChatList(_searchBox.Text);
        }

        // ── Avatars (profile photos) — all surfaces route through AvatarStore (AVATAR-PIPELINE) ──

        /// <summary>Cached avatar for a peer id (sync), or null if not loaded yet — for picker rows to draw
        /// immediately and fall back to the initials-circle until <see cref="GetAvatarBoundedAsync"/> fills in.</summary>
        public Image GetCachedAvatar(long id)
        {
            return _avatars.GetCached(id);
        }

        /// <summary>Awaitable avatar for the pickers (memory → disk → single-flight bounded download via the
        /// store's queue). Null = no photo / failed — callers keep the letter circle. Same delegate signature
        /// the picker dialogs always took, so they are unchanged.</summary>
        public System.Threading.Tasks.Task<Image> GetAvatarBoundedAsync(long id, IPeerInfo peer)
        {
            return _avatars.GetAsync(id, peer);
        }

        /// <summary>Chat-list row hook: memory hit paints immediately; otherwise a store request is queued
        /// AHEAD of the backfill and the row repaints via <see cref="OnAvatarLoaded"/> when it lands.</summary>
        private void LoadAvatar(ChatEntry entry)
        {
            if (entry == null) return;
            _avatars.Request(entry.PeerId, entry.PeerInfo);
        }

        /// <summary>AvatarLoaded (worker thread): repaint every live surface showing this peer — the chat-list
        /// row and any open group bubbles awaiting this sender. Rows/bubbles are looked up LIVE (both get
        /// recreated constantly), never through captured references.</summary>
        private void OnAvatarLoaded(long peerId)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    var img = _avatars.GetCached(peerId);
                    if (img == null) return;
                    ApplyAvatarToRow(peerId, img);
                    if (_messagePanel != null && !_messagePanel.IsDisposed)
                        foreach (Control c in _messagePanel.Controls)
                            if (c is MessageBubbleControl b && !b.IsDisposed && b.SenderPeerId == peerId && b.SenderAvatar == null)
                            { b.SenderAvatar = img; b.Invalidate(); }
                }));
            }
            catch { /* form tearing down */ }
        }

        /// <summary>Sets a freshly-arrived avatar on the current chat-list row for <paramref name="peerId"/> and
        /// repaints just that row. Looks the row up live (rows are recreated on every RenderChatList), marshals
        /// to the UI thread if the avatar arrived off-thread, and is safe against a disposed list/row.</summary>
        private void ApplyAvatarToRow(long peerId, Image img)
        {
            if (img == null) return;
            var panel = _chatListPanel;
            if (panel == null || panel.IsDisposed) return;
            if (panel.InvokeRequired) { try { panel.BeginInvoke((Action)(() => ApplyAvatarToRow(peerId, img))); } catch { } return; }
            foreach (Control c in panel.Controls)
                if (c is ChatListItemControl row && !row.IsDisposed && row.Entry != null && row.Entry.PeerId == peerId)
                { row.Avatar = img; row.Invalidate(); break; }
        }

        // ── Combined search (chats + global messages in one list) ────────────

        private void OnSearchTextChanged()
        {
            _searchDebounce.Stop();
            string q = _searchBox.Text;
            // Instant local chat matches (folder-aware); message results stream in after a debounce.
            RenderChatList(q);
            if (!string.IsNullOrWhiteSpace(q)) _searchDebounce.Start();
        }

        private async void DoMessageSearch()
        {
            _searchDebounce.Stop();
            var query = _searchBox.Text.Trim();
            if (query.Length == 0) return;

            try
            {
                var results = await _service.SearchMessagesAsync(query, 50);
                // Drop stale responses (query changed meanwhile).
                if (_searchBox.Text.Trim() != query) return;
                AppendMessageResults(results);
            }
            catch (Exception ex)
            {
                _chatTitle.Text = "Search failed: " + ex.Message;
            }
        }

        /// <summary>Appends global message hits below the already-shown chat matches (combined search).</summary>
        private void AppendMessageResults(Messages_MessagesBase results)
        {
            var msgs = results.Messages.OfType<Message>().Where(m => m.peer_id != null).ToList();
            if (msgs.Count == 0) return;

            _chatListPanel.SuspendLayout();
            int w = ContentWidth(_chatListPanel);
            AddSectionHeader("MESSAGES");
            foreach (var m in msgs)
            {
                string title;
                InputPeer peer;
                bool isGroup = false;
                var resolved = results.UserOrChat(m.peer_id);
                if (resolved is User u) { title = DisplayName(u); peer = u.ToInputPeer(); }
                else if (resolved is ChatBase ch) { title = ch.Title; peer = ch.ToInputPeer(); isGroup = true; }
                else continue;

                var entry = new ChatEntry
                {
                    Peer = peer,
                    PeerId = m.peer_id.ID,
                    Title = title,
                    Preview = GetDisplayText(m),
                    Date = m.date,
                    IsGroup = isGroup,
                    FocusMessageId = m.ID
                };
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnSearchResultClick;
                _chatListPanel.Controls.Add(item);
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Adds a small section divider label into the chat list (search groupings).</summary>
        private void AddSectionHeader(string text)
        {
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Height = 22,
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Margin = new Padding(0),
                Font = FontHelper.Ui(8f, FontStyle.Bold),
                ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(120, 120, 125)
            };
            _chatListPanel.Controls.Add(lbl);
        }

        // ── Chat folders (dialog filters) ─────────────────────────────────────

        private async void LoadFolders()
        {
            try { _folders = await _service.GetDialogFiltersAsync(); }
            catch { _folders = new TL.DialogFilterBase[0]; }
            if (IsDisposed) return;
            RebuildFolders();
        }

        private void RebuildFolderBar()
        {
            if (_folderBar == null) return;
            _folderBar.SuspendLayout();
            foreach (var c in _folderBar.Controls.Cast<Control>().ToArray()) c.Dispose();
            _folderBar.Controls.Clear();
            _folderBadgeSources.Clear();

            AddFolderTab("All", AllUnread, !_showArchive && _activeFolder == null, () => SetActiveFolder(null));
            foreach (var f in _folders)
            {
                if (f == null) continue;
                string title;
                try { title = (f.Title?.text ?? "").Trim(); } catch { title = ""; }
                var ff = f;
                AddFolderTab(string.IsNullOrEmpty(title) ? "Folder" : title, () => FolderUnread(ff),
                    !_showArchive && ReferenceEquals(f, _activeFolder), () => SetActiveFolder(ff));
            }

            // Archive tab (only when there are archived chats); its unread rides the shared badge now.
            if (_allChats.Any(c => c.Archived))
                AddFolderTab("Archive", ArchiveUnread, _showArchive, SetArchive);

            _folderBar.ResumeLayout();
        }

        private void AddFolderTab(string text, Func<int> unread, bool active, Action onClick)
        {
            var it = new FolderTabItem(text, unread(), active, _dark, _accent);
            it.Clicked += onClick;
            _folderBar.Controls.Add(it);
            _folderBadgeSources.Add(new KeyValuePair<IFolderBadge, Func<int>>(it, unread));
        }

        /// <summary>FOLDER-SIDEBAR: rebuild whichever folder navigator is active — exactly one of the tab bar
        /// (default) or the side rail is non-null. Replaces every RebuildFolderBar() call site so both layouts
        /// stay current without the caller knowing which is live.</summary>
        private void RebuildFolders()
        {
            if (_folderBar != null) RebuildFolderBar();
            if (_folderRail != null) RebuildFolderRail();
        }

        // Per-folder unread, derived from the SAME model + filter the list uses (no new fetch). "All" excludes
        // archived (matching IsVisibleInCurrentView(null)); a folder uses its own MatchesFolder rules.
        private int AllUnread() => _allChats.Where(c => !c.Archived).Sum(c => Math.Max(0, c.UnreadCount));
        private int ArchiveUnread() => _allChats.Where(c => c.Archived).Sum(c => Math.Max(0, c.UnreadCount));
        private int FolderUnread(TL.DialogFilterBase f) => _allChats.Where(c => MatchesFolder(c, f)).Sum(c => Math.Max(0, c.UnreadCount));

        /// <summary>Builds the vertical folder rail (side-panel mode): All + user folders + Archive, each an
        /// icon/label/unread-badge tile that filters the list through the SAME SetActiveFolder/SetArchive path
        /// the tabs use (2.1 — reuse, not reinvent). Icons from DialogFilter.emoticon; badges from the model.</summary>
        private void RebuildFolderRail()
        {
            if (_folderRail == null) return;
            _folderRail.SuspendLayout();
            foreach (var c in _folderRail.Controls.Cast<Control>().ToArray()) c.Dispose();
            _folderRail.Controls.Clear();
            _folderBadgeSources.Clear();
            _folderRail.BackColor = _dark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(244, 244, 246);

            AddRailItem("\U0001F4AC", "All", AllUnread, !_showArchive && _activeFolder == null, () => SetActiveFolder(null));
            foreach (var f in _folders)
            {
                if (f == null) continue;
                var df = f as TL.DialogFilter;
                string title; try { title = (f.Title?.text ?? "").Trim(); } catch { title = ""; }
                if (string.IsNullOrEmpty(title)) title = "Folder";
                string icon = !string.IsNullOrEmpty(df?.emoticon) ? df.emoticon : "\U0001F4C1";   // 📁 default
                var ff = f;
                AddRailItem(icon, title, () => FolderUnread(ff), !_showArchive && ReferenceEquals(f, _activeFolder), () => SetActiveFolder(ff));
            }
            if (_allChats.Any(c => c.Archived))
                AddRailItem("\U0001F5C4", "Archive", ArchiveUnread, _showArchive, SetArchive);   // 🗄

            _folderRail.ResumeLayout();
        }

        private void AddRailItem(string icon, string label, Func<int> unread, bool active, Action onClick)
        {
            var it = new FolderRailItem(icon, label, unread(), active, _dark, _accent) { Width = ContentWidth(_folderRail) };
            it.Clicked += onClick;
            _folderRail.Controls.Add(it);
            _folderBadgeSources.Add(new KeyValuePair<IFolderBadge, Func<int>>(it, unread));
        }

        /// <summary>Cheap in-place badge refresh for whichever navigator is live (tab bar OR rail) — the
        /// per-folder counts track the model as messages arrive/read, no rebuild/churn. No-op if neither
        /// has registered sources.</summary>
        private void RefreshFolderBadges()
        {
            for (int i = 0; i < _folderBadgeSources.Count; i++)
                _folderBadgeSources[i].Key.Unread = _folderBadgeSources[i].Value();
        }

        /// <summary>One folder TAB (tabbed mode): label + unread badge pill, accent when active. Owner-drawn
        /// so it can carry a live badge like the rail (parity). Auto-sizes to text + badge.</summary>
        private sealed class FolderTabItem : Control, IFolderBadge
        {
            private readonly string _label;
            private int _unread;
            private readonly bool _active, _dark;
            private readonly Color _accent;
            public event Action Clicked;

            public int Unread { set { if (_unread != value) { _unread = value; RecalcWidth(); Invalidate(); } } }

            public FolderTabItem(string label, int unread, bool active, bool dark, Color accent)
            {
                _label = label; _unread = unread; _active = active; _dark = dark; _accent = accent;
                Height = 28; Margin = new Padding(2, 3, 2, 3);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
                RecalcWidth();
            }

            private static string BadgeText(int n) => n > 99 ? "99+" : n.ToString();

            private void RecalcWidth()
            {
                using (var lf = FontHelper.Ui(9.5f, _active ? FontStyle.Bold : FontStyle.Regular))
                {
                    int tw = TextRenderer.MeasureText(_label, lf).Width;
                    int extra = 0;
                    if (_unread > 0)
                        using (var bf = FontHelper.Ui(7f, FontStyle.Bold))
                            extra = 5 + Math.Max(15, TextRenderer.MeasureText(BadgeText(_unread), bf).Width + 7);
                    Width = Math.Max(46, 12 + tw + extra);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Parent != null ? Parent.BackColor : (_dark ? Color.FromArgb(40, 40, 40) : Color.White));

                Color fg = _active ? _accent : (_dark ? Color.FromArgb(170, 170, 175) : Color.FromArgb(110, 110, 115));
                using (var lf = FontHelper.Ui(9.5f, _active ? FontStyle.Bold : FontStyle.Regular))
                {
                    int tw = TextRenderer.MeasureText(_label, lf).Width;
                    TextRenderer.DrawText(g, _label, lf, new Rectangle(6, 0, tw + 4, Height), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    if (_unread > 0)
                    {
                        string t = BadgeText(_unread);
                        using (var bf = FontHelper.Ui(7f, FontStyle.Bold))
                        {
                            int bw = Math.Max(15, TextRenderer.MeasureText(t, bf).Width + 7);
                            var badge = new Rectangle(6 + tw + 5, (Height - 15) / 2, bw, 15);
                            using (var bb = new SolidBrush(_accent))
                            using (var pth = DrawHelper.RoundedRect(badge, 7)) g.FillPath(bb, pth);
                            TextRenderer.DrawText(g, t, bf, badge, Color.White,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        }
                    }
                }
            }
        }

        /// <summary>One folder tile in the side rail: centered icon, label under it, unread badge on the icon
        /// corner, active = accent tint + left bar. Owner-drawn, touch-sized, no per-tile RPC.</summary>
        private sealed class FolderRailItem : Control, IFolderBadge
        {
            private readonly string _icon, _label;
            private int _unread;
            private bool _active;
            private readonly bool _dark;
            private readonly Color _accent;
            public event Action Clicked;

            public int Unread { get { return _unread; } set { if (_unread != value) { _unread = value; Invalidate(); } } }
            public bool Active { get { return _active; } set { if (_active != value) { _active = value; Invalidate(); } } }

            public FolderRailItem(string icon, string label, int unread, bool active, bool dark, Color accent)
            {
                _icon = icon; _label = label; _unread = unread; _active = active; _dark = dark; _accent = accent;
                Height = 60; Margin = new Padding(0);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color bg = _dark ? Color.FromArgb(34, 34, 36) : Color.FromArgb(244, 244, 246);
                g.Clear(bg);
                if (_active)
                {
                    using (var tint = new SolidBrush(Color.FromArgb(30, _accent))) g.FillRectangle(tint, 0, 0, Width, Height);
                    using (var bar = new SolidBrush(_accent)) g.FillRectangle(bar, 0, 8, 3, Height - 16);
                }
                Color fg = _active ? _accent : (_dark ? Color.FromArgb(205, 205, 210) : Color.FromArgb(90, 90, 95));
                using (var iconFont = new Font("Segoe UI Emoji", 16f))
                    TextRenderer.DrawText(g, _icon, iconFont, new Rectangle(0, 5, Width, 26), fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                using (var lf = FontHelper.Ui(7.25f, _active ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, _label, lf, new Rectangle(1, 37, Width - 2, 16), fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (_unread > 0)
                {
                    string t = _unread > 99 ? "99+" : _unread.ToString();
                    using (var bf = FontHelper.Ui(6.75f, FontStyle.Bold))
                    {
                        int bw = Math.Max(15, TextRenderer.MeasureText(t, bf).Width + 7);
                        var badge = new Rectangle(Width / 2 + 5, 3, bw, 15);
                        using (var bb = new SolidBrush(_accent))
                        using (var pth = DrawHelper.RoundedRect(badge, 7)) g.FillPath(bb, pth);
                        TextRenderer.DrawText(g, t, bf, badge, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        private void SetActiveFolder(TL.DialogFilterBase folder)
        {
            _showArchive = false;
            _activeFolder = folder;
            RebuildFolders();
            RenderChatList(_searchBox.Text);
        }

        private void SetArchive()
        {
            _showArchive = true;
            _activeFolder = null;
            RebuildFolders();
            RenderChatList(_searchBox.Text);
        }

        /// <summary>True if a chat belongs to the given folder (null folder = "All").</summary>
        private bool MatchesFolder(ChatEntry e, TL.DialogFilterBase folder)
        {
            if (folder == null) return true;
          try
          {
            var df = folder as TL.DialogFilter;
            if (df != null && PeerIdSet(df.exclude_peers).Contains(e.PeerId)) return false;

            var incl = PeerIdSet(folder.IncludePeers);
            incl.UnionWith(PeerIdSet(folder.PinnedPeers));
            if (incl.Contains(e.PeerId)) return true;

            if (df == null) return false;   // chatlist with no explicit match
            var flags = df.flags;
            if ((flags & TL.DialogFilter.Flags.exclude_muted) != 0 && e.Muted) return false;
            if ((flags & TL.DialogFilter.Flags.exclude_read) != 0 && e.UnreadCount <= 0) return false;

            // Broad type flags (best-effort from the resolved peer).
            bool anyType = (flags & (TL.DialogFilter.Flags.contacts | TL.DialogFilter.Flags.non_contacts
                                     | TL.DialogFilter.Flags.groups | TL.DialogFilter.Flags.broadcasts
                                     | TL.DialogFilter.Flags.bots)) != 0;
            if (!anyType) return false;

            var pi = e.PeerInfo;
            if (pi is User u)
            {
                bool bot = (u.flags & User.Flags.bot) != 0;
                bool contact = (u.flags & User.Flags.contact) != 0;
                if (bot) return (flags & TL.DialogFilter.Flags.bots) != 0;
                return (flags & (contact ? TL.DialogFilter.Flags.contacts : TL.DialogFilter.Flags.non_contacts)) != 0;
            }
            if (pi is Channel c)
            {
                bool broadcast = (c.flags & Channel.Flags.broadcast) != 0;
                return (flags & (broadcast ? TL.DialogFilter.Flags.broadcasts : TL.DialogFilter.Flags.groups)) != 0;
            }
            if (pi is ChatBase) return (flags & TL.DialogFilter.Flags.groups) != 0;   // basic group
            return false;
          }
          catch { return true; }   // never let a malformed folder break the chat list
        }

        private static HashSet<long> PeerIdSet(InputPeer[] peers)
        {
            var set = new HashSet<long>();
            if (peers != null)
                foreach (var p in peers) { long id = PeerIdOf(p); if (id != 0) set.Add(id); }
            return set;
        }

        private static long PeerIdOf(InputPeer p)
        {
            switch (p)
            {
                case InputPeerUser u: return u.user_id;
                case InputPeerChannel c: return c.channel_id;
                case InputPeerChat ch: return ch.chat_id;
                default: return 0;
            }
        }

        private async void OnSearchResultClick(object sender, EventArgs e)
        {
            var result = ((ChatListItemControl)sender).Entry;
            int focusId = result.FocusMessageId;

            // Clearing the box re-renders the normal chat list (via TextChanged), then open the chat.
            _searchBox.Text = "";

            var entry = _allChats.FirstOrDefault(c => c.PeerId == result.PeerId) ?? result;
            await OpenChat(entry, focusId);
        }

        private async void OnChatItemClick(object sender, EventArgs e)
        {
            if (LogOn) System.Diagnostics.Debug.WriteLine("[OPEN] row click peer=" + ((ChatListItemControl)sender).Entry.PeerId);
            await OpenChat(((ChatListItemControl)sender).Entry, 0);
        }

        // OPENCHAT-ONCE: a touch-synthesized tap (posted DOWN+UP pair) raises the row's Click TWICE
        // ~44ms apart (mouse clicks raise once — log-convicted); no duplicate wiring exists to delete,
        // so a per-peer latch absorbs the echo. Focused opens (pin jumps / search) are exempt —
        // rapid same-chat re-entry with a focus id is legitimate.
        private long _openLatchPeer;
        private int _openLatchTick;

        /// <summary>Selects a chat (highlighting its row if present) and loads its history.</summary>
        private async System.Threading.Tasks.Task OpenChat(ChatEntry entry, int focusMessageId)
        {
            if (focusMessageId == 0 && entry.PeerId == _openLatchPeer && Environment.TickCount - _openLatchTick < 500)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[OPEN] duplicate open suppressed peer=" + entry.PeerId);
                return;
            }
            _openLatchPeer = entry.PeerId; _openLatchTick = Environment.TickCount;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[OPEN] OpenChat peer=" + entry.PeerId + " focus=" + focusMessageId);
            _selectedChat = entry;
            ResetBotUi();             // clear any previous bot Menu button / reply keyboard
            LoadPinnedAsync(entry);   // refresh the pinned-messages bar for this chat

            var row = FindChatItem(entry.PeerId);
            if (_selectedItem != null && _selectedItem != row) _selectedItem.Selected = false;
            _selectedItem = row;
            if (row != null)
            {
                row.Selected = true;
                // NB: do NOT clear the unread badge on open — it clears only when the user reaches the
                // bottom (MarkCaughtUp), so opening at the first-unread message keeps the badge meaningful.
                row.Invalidate();
                UpdateTrayTooltip();
            }

            ShowComposeFooter();   // default to the normal composer; the state machine refines it below
            _typing = false; _typingTimer?.Stop();
            UpdateHeaderStatus();
            // PEER-PRESENTATION + PRESENCE: groups take the presence refresh (ONE fetch = members+online,
            // 3.3's per-open refresh); users/broadcasts keep the count ladder (dialog-cached → ≤1 lazy fetch).
            if (entry.PeerInfo is Chat || (entry.PeerInfo is Channel mgc && (mgc.flags & Channel.Flags.broadcast) == 0))
                RefreshOpenGroupPresence();
            else
                MaybeFetchChatCount(entry);
            await LoadHistoryAsync(entry, focusMessageId);
            if (_selectedChat == entry)
            {
                ResolveAndApplyComposer(entry);   // history loaded → resolve footer state
                LoadBotMenuAsync(entry);          // bot? → fetch commands + reveal the Menu button
                ApplyInitialReplyKeyboard();      // restore the latest reply-keyboard markup in view
            }
        }

        // ── Composer footer state machine (Batch CF1) ───────────────────────

        /// <summary>Shows the normal "Write a message" composer (the COMPOSE state).</summary>
        private void ShowComposeFooter()
        {
            _footerKind = ComposerKind.Compose;
            if (_footerBar != null) _footerBar.Visible = false;
            if (_composerBar != null) { _composerBar.Visible = true; _composerBar.BringToFront(); }
            _messageInput.Enabled = true;
            _sendButton.Enabled = true;
            _attachButton.Enabled = true; _attachButton.Invalidate();
            _micButton.Enabled = true; _micButton.Invalidate();
            _emojiButton.Enabled = true; _emojiButton.Invalidate();
        }

        /// <summary>Renders the footer matching a resolved state — composer, a centered button, or a label.</summary>
        private void ApplyComposerState(ComposerState st)
        {
            _footerKind = st.Kind;
            if (st.Kind == ComposerKind.Compose) { ShowComposeFooter(); return; }

            // Non-compose: hide + gate the composer, show the footer bar.
            if (_composerBar != null) _composerBar.Visible = false;
            _messageInput.Enabled = false;
            _sendButton.Enabled = false;

            string label;
            bool isButton;
            switch (st.Kind)
            {
                case ComposerKind.Join: label = "Join"; isButton = true; break;
                case ComposerKind.MuteUnmute: label = st.Muted ? "Unmute" : "Mute"; isButton = true; break;
                case ComposerKind.BotStart: label = "Start"; isButton = true; break;
                case ComposerKind.Blocked: label = "Unblock"; isButton = true; break;
                case ComposerKind.Restricted:
                    label = st.HasCountdown ? "You can send again in " + FormatComposerUntil(st.Until)
                                            : "You can't send messages here";
                    isButton = false; break;
                case ComposerKind.SlowmodeWait:
                    label = "Slow mode — wait " + FormatComposerUntil(st.Until);
                    isButton = false; break;
                default: ShowComposeFooter(); return;
            }
            if (_footerBar == null) return;
            if (isButton) _footerBar.SetButton(label); else _footerBar.SetLabel(label);
            _footerBar.Visible = true;
            _footerBar.BringToFront();
        }

        /// <summary>Gathers the inputs (fetching UserFull for the blocked flag) and applies the footer state.</summary>
        private async void ResolveAndApplyComposer(ChatEntry entry)
        {
            if (entry == null || entry != _selectedChat) return;
            UserFull uf = null;
            if (entry.PeerInfo is User u && entry.Peer is InputPeerUser)
            {
                try { uf = await _service.GetUserFullAsync(u); } catch { }
                if (entry != _selectedChat) return;   // chat switched while fetching
            }
            // channelFull is null in v1 (base Channel carries left/broadcast/admin_rights/banned_rights;
            // slow-mode timing — the only full-only field — is deferred → resolves as COMPOSE).
            var st = ComposerResolver.Resolve(entry.PeerInfo, null, uf, entry.Muted, _currentChatMessages.Count == 0);
            ApplyComposerState(st);
        }

        private static string FormatComposerUntil(DateTime until)
        {
            var ts = until - DateTime.UtcNow;
            if (ts.TotalHours >= 1) return (int)ts.TotalHours + "h " + ts.Minutes + "m";
            if (ts.TotalMinutes >= 1) return (int)ts.TotalMinutes + "m";
            return Math.Max(1, (int)ts.TotalSeconds) + "s";
        }

        /// <summary>The footer button was tapped — dispatch by the current state.</summary>
        private void OnFooterAction()
        {
            var entry = _selectedChat;
            if (entry == null) return;
            switch (_footerKind)
            {
                case ComposerKind.Join: DoJoin(entry); break;
                case ComposerKind.MuteUnmute: ToggleChatMute(entry); break;   // re-resolves the label at its end
                case ComposerKind.BotStart: DoBotStart(entry); break;
                case ComposerKind.Blocked: DoUnblock(entry); break;
            }
        }

        private async void DoJoin(ChatEntry entry)
        {
            if (!(entry.Peer is InputPeerChannel ipc)) return;
            try { await _service.JoinChannelAsync(ipc); }
            catch (Exception ex) { ThemedDialog.Show(this, "Join", "Couldn't join: " + ex.Message, "OK"); return; }
            if (entry.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;   // reflect membership locally
            if (entry == _selectedChat) ResolveAndApplyComposer(entry);          // → COMPOSE, or Mute for a broadcast
        }

        private async void DoBotStart(ChatEntry entry)
        {
            try { await _service.SendTextAsync(entry.Peer, "/start"); }
            catch (Exception ex) { ThemedDialog.Show(this, "Start", "Couldn't start the bot: " + ex.Message, "OK"); return; }
            if (entry == _selectedChat) ApplyComposerState(new ComposerState { Kind = ComposerKind.Compose });
        }

        // ── Bot menu + commands + reply keyboard (Batch BOT-2) ──────────────

        /// <summary>Resets all bot UI on chat switch (hide the Menu button + clear any reply keyboard).</summary>
        private void ResetBotUi()
        {
            _currentBotInfo = null;
            _replyKbSingleUse = false;
            if (_botMenuButton != null) _botMenuButton.Visible = false;
            if (_composerColumns != null) _composerColumns.ColumnStyles[0].Width = 0;
            if (_replyKb != null) _replyKb.Clear();
            if (_messageInput != null) _messageInput.Hint = "Write a message…";
            SyncReplyKeyboardHeight();
        }

        /// <summary>For a bot chat, fetch BotInfo (commands + menu_button) and reveal the composer Menu button.</summary>
        private async void LoadBotMenuAsync(ChatEntry entry)
        {
            if (entry == null || !(entry.PeerInfo is User u) || (u.flags & User.Flags.bot) == 0) return;
            try
            {
                var full = await _service.GetUserFullAsync(u);
                if (entry != _selectedChat) return;
                _currentBotInfo = full != null ? full.bot_info : null;
                bool hasCommands = _currentBotInfo != null && _currentBotInfo.commands != null && _currentBotInfo.commands.Length > 0;
                bool webAppMenu = _currentBotInfo != null && _currentBotInfo.menu_button is BotMenuButton;
                bool show = hasCommands || webAppMenu;
                if (_botMenuButton != null)
                {
                    bool webMenu = webAppMenu && !hasCommands;   // web-app → "Menu" text; else the drawn "≡" icon
                    _botMenuIcon = !webMenu;
                    _botMenuButton.Text = webMenu ? "Menu" : "";
                    _botMenuButton.Visible = show;
                    _botMenuButton.Invalidate();
                }
                if (_composerColumns != null) _composerColumns.ColumnStyles[0].Width = show ? 48 : 0;
                System.Diagnostics.Debug.WriteLine("[BOT] menu loaded cmds=" + (hasCommands ? _currentBotInfo.commands.Length : 0) + " webapp=" + webAppMenu);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] menu load err: " + ex.Message); }
        }

        /// <summary>The composer Menu button: a WebApp menu → "not supported" notice; otherwise the command list.</summary>
        private void ShowBotMenu()
        {
            if (_currentBotInfo == null) return;
            bool hasCommands = _currentBotInfo.commands != null && _currentBotInfo.commands.Length > 0;
            if (_currentBotInfo.menu_button is BotMenuButton webapp && !hasCommands)
            {
                ThemedDialog.Show(this, string.IsNullOrEmpty(webapp.text) ? "Menu" : webapp.text,
                    "This bot's menu opens a web app, which isn't supported yet.", "OK");
                return;
            }
            if (!hasCommands) { ThemedDialog.Show(this, "Bot", "This bot has no command list.", "OK"); return; }
            var menu = new ThemedContextMenuStrip();
            foreach (var c in _currentBotInfo.commands)
            {
                string cmd = c.command;
                string label = "/" + cmd + (string.IsNullOrEmpty(c.description) ? "" : "   —   " + c.description);
                AddMenuItem(menu, label, () => SendBotCommand(cmd));
            }
            if (_botMenuButton != null && _botMenuButton.Visible) menu.Show(_botMenuButton, new Point(0, -menu.PreferredSize.Height));
            else menu.Show(Cursor.Position);
        }

        private async void SendBotCommand(string command)
        {
            if (_selectedChat == null || string.IsNullOrEmpty(command)) return;
            try { await _service.SendTextAsync(_selectedChat.Peer, "/" + command.TrimStart('/')); System.Diagnostics.Debug.WriteLine("[BOT] command sent /" + command); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] command err: " + ex.Message); }
        }

        /// <summary>Sets the reply-keyboard row height from the control's desired height (and its host visibility).</summary>
        private void SyncReplyKeyboardHeight()
        {
            if (_replyKb == null || _rightLayout == null) return;
            int h = _replyKb.HasButtons ? _replyKb.DesiredHeight : 0;
            _rightLayout.RowStyles[6].Height = h;
            if (_replyKbHost != null) _replyKbHost.Visible = h > 0;
        }

        /// <summary>Applies a message's reply markup to the chat: show/replace the reply keyboard, hide it, or force a reply.</summary>
        private void ApplyChatReplyMarkup(ReplyMarkup markup)
        {
            if (markup is ReplyKeyboardMarkup rk && rk.rows != null)
            {
                var rows = new List<List<ReplyKeyboardControl.RKButton>>();
                foreach (var row in rk.rows)
                {
                    if (row == null || row.buttons == null) continue;
                    var cells = new List<ReplyKeyboardControl.RKButton>();
                    foreach (var btn in row.buttons)
                    {
                        var fld = btn.GetType().GetField("text");
                        var b = new ReplyKeyboardControl.RKButton { Label = (fld != null ? fld.GetValue(btn) as string : null) ?? "" };
                        if (btn is KeyboardButtonRequestPhone) b.Kind = ReplyKeyboardControl.RKKind.RequestPhone;
                        else if (btn is KeyboardButtonRequestGeoLocation) b.Kind = ReplyKeyboardControl.RKKind.RequestGeo;
                        else if (btn is KeyboardButtonRequestPeer) b.Kind = ReplyKeyboardControl.RKKind.RequestPeer;
                        else if (btn is KeyboardButtonRequestPoll) b.Kind = ReplyKeyboardControl.RKKind.RequestPoll;
                        else if (btn is KeyboardButtonSimpleWebView || btn is KeyboardButtonWebView) b.Kind = ReplyKeyboardControl.RKKind.WebView;
                        else b.Kind = ReplyKeyboardControl.RKKind.Text;
                        cells.Add(b);
                    }
                    if (cells.Count > 0) rows.Add(cells);
                }
                _replyKb.SetButtons(rows);
                _replyKbSingleUse = (rk.flags & ReplyKeyboardMarkup.Flags.single_use) != 0;
                if ((rk.flags & ReplyKeyboardMarkup.Flags.has_placeholder) != 0 && !string.IsNullOrEmpty(rk.placeholder))
                    _messageInput.Hint = rk.placeholder;
                SyncReplyKeyboardHeight();
                System.Diagnostics.Debug.WriteLine("[BOT] reply keyboard shown rows=" + rows.Count + " single_use=" + _replyKbSingleUse);
            }
            else if (markup is ReplyKeyboardHide)
            {
                _replyKb.Clear(); SyncReplyKeyboardHeight();
                _messageInput.Hint = "Write a message…";
                System.Diagnostics.Debug.WriteLine("[BOT] reply keyboard hidden");
            }
            else if (markup is ReplyKeyboardForceReply fr)
            {
                _replyKb.Clear(); SyncReplyKeyboardHeight();
                if ((fr.flags & ReplyKeyboardForceReply.Flags.has_placeholder) != 0 && !string.IsNullOrEmpty(fr.placeholder))
                    _messageInput.Hint = fr.placeholder;
                if (_messageInput.Enabled) { try { _messageInput.Focus(); } catch { } }
                System.Diagnostics.Debug.WriteLine("[BOT] force reply");
            }
        }

        /// <summary>On chat open: apply the latest reply-keyboard markup found in the loaded history (newest wins).</summary>
        private void ApplyInitialReplyKeyboard()
        {
            for (int i = _currentChatMessages.Count - 1; i >= 0; i--)
            {
                var mk = _currentChatMessages[i].reply_markup;
                if (mk is ReplyKeyboardMarkup || mk is ReplyKeyboardHide || mk is ReplyKeyboardForceReply)
                { ApplyChatReplyMarkup(mk); return; }
            }
        }

        private async void OnReplyKeyboardButton(ReplyKeyboardControl.RKButton b)
        {
            if (b == null || _selectedChat == null) return;
            if (b.Kind == ReplyKeyboardControl.RKKind.Text)
            {
                try { await _service.SendTextAsync(_selectedChat.Peer, b.Label ?? ""); System.Diagnostics.Debug.WriteLine("[BOT] reply-kbd text sent: " + b.Label); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] reply-kbd send err: " + ex.Message); }
                if (_replyKbSingleUse) { _replyKb.Clear(); SyncReplyKeyboardHeight(); }
                return;
            }
            if (b.Kind == ReplyKeyboardControl.RKKind.RequestPhone)
            {
                int c = ThemedDialog.Show(this, "Share phone number", "Send your phone number to this bot?", "Share", "Cancel");
                try { ActiveControl = null; } catch { }
                if (c != 0) return;
                var me = _service.Me;
                if (me != null) { try { await _service.SendContactAsync(_selectedChat.Peer, me.phone, me.first_name, me.last_name); } catch { } }
                if (_replyKbSingleUse) { _replyKb.Clear(); SyncReplyKeyboardHeight(); }
                return;
            }
            ThemedDialog.Show(this, "Not supported", "This button type (location / peer / poll request) isn't supported yet.", "OK");
        }

        private async System.Threading.Tasks.Task StartBotIfPossible(string startParam)
        {
            var u = _selectedChat != null ? _selectedChat.PeerInfo as User : null;
            if (u == null || (u.flags & User.Flags.bot) == 0) return;
            try { await _service.StartBotAsync(u, startParam); System.Diagnostics.Debug.WriteLine("[BOT] start deep-link param=" + startParam); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BOT] start err: " + ex.Message); }
        }

        private async void DoUnblock(ChatEntry entry)
        {
            try { await _service.SetBlockedAsync(entry.Peer, false); }
            catch (Exception ex) { ThemedDialog.Show(this, "Unblock", "Couldn't unblock: " + ex.Message, "OK"); return; }
            if (entry == _selectedChat) ApplyComposerState(new ComposerState { Kind = ComposerKind.Compose });
        }

        // ── PRESENCE (v0.9.0): our own online status + peer presence surfaces ─

        /// <summary>Member lists subscribe for live row updates (unsubscribe on dispose — E-3).
        /// Raised on the UI thread from the UpdateUserStatus handler.</summary>
        public static event Action<long, UserStatus> UserStatusChanged;

        private Timer _presenceTimer;             // ONE 15s host: idle/offline transitions + dot-expiry sweep + 60s group refresh
        private int _lastActivityTick, _lastPresenceSentTick, _bgSinceTick, _presenceTicks;
        private int _lastGroupRefreshTick;        // dedupes the double-running open path (one RPC per open)
        private long _lastGroupRefreshPeer;
        private bool _presenceOnline;             // the state we last TOLD the server

        private void StartPresenceEngine()
        {
            _lastActivityTick = Environment.TickCount;
            Application.AddMessageFilter(new ActivityFilter(this));   // app-lifetime, like the wheel filter
            // PRESENCE-TUNE A.3: 5s cadence — the tick only reads cached fields (sweep work only on an
            // actual expiry), so the faster interval is near-zero cost; offline now lands ≤10s.
            _presenceTimer = new Timer { Interval = 5000 };
            _presenceTimer.Tick += (s, e) => PresenceTick();
            _presenceTimer.Start();
        }

        /// <summary>PRESENCE 1.1: activity = REAL input only (key/click/wheel; touch taps arrive as the
        /// synthesized posted click, touch pans via TouchScroller.Scrolled) — never our own timers.</summary>
        private sealed class ActivityFilter : IMessageFilter
        {
            private readonly MainForm _f;
            public ActivityFilter(MainForm f) { _f = f; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                // WM_KEYDOWN / WM_LBUTTONDOWN / WM_RBUTTONDOWN / WM_MOUSEWHEEL
                if (m.Msg == 0x0100 || m.Msg == 0x0201 || m.Msg == 0x0204 || m.Msg == 0x020A) _f.NoteActivity();
                return false;
            }
        }

        internal void NoteActivity()
        {
            _lastActivityTick = Environment.TickCount;
            if (!_appActive || IsDisposed) return;
            if (!_presenceOnline || Environment.TickCount - _lastPresenceSentTick >= 25000)
                SendPresence(false);
        }

        /// <summary>Single presence sender: state + throttle stamps set SYNCHRONOUSLY before the await,
        /// so a burst of activity can't double-send. ≥25s between online refreshes (1.1).</summary>
        private async void SendPresence(bool offline)
        {
            if (_service?.Client == null || TelegramService.TearingDown) return;
            _lastPresenceSentTick = Environment.TickCount;
            _presenceOnline = !offline;
            try
            {
                bool ok = await _service.UpdateStatusAsync(offline);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] sent " + (offline ? "offline" : "online") + " ok=" + ok);
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] send failed: " + ex.Message);
            }
        }

        private void PresenceTick()
        {
            if (IsDisposed) return;
            int now = Environment.TickCount;
            // PRESENCE-TUNE A.2 (amended): offline after the 5s deactivation grace (tray-hide rides it),
            // or after 5 MINUTES of true input idle while foreground — passive READING has no input, and
            // the old 60s bar flipped the user offline mid-read. Return does NOT send here —
            // activation/next input does (A.1); reconnects never auto-send.
            if (_presenceOnline)
            {
                bool bgLong = !_appActive && _bgSinceTick != 0 && now - _bgSinceTick > 5000;
                bool idleLong = _appActive && now - _lastActivityTick > 300000;
                if (bgLong || idleLong) SendPresence(true);
            }
            // 2.3: dot-expiry sweep over VISIBLE rows — dots die even without a server update.
            // (No suitable existing 30s-class timer existed; this ONE timer hosts all presence cadence.)
            try
            {
                var utc = DateTime.UtcNow;
                foreach (Control c in _chatListPanel.Controls)
                    if (c is ChatListItemControl ci && ci.Entry.OnlineUntil != default(DateTime) && ci.Entry.OnlineUntil <= utc)
                    {
                        ci.Entry.OnlineUntil = default(DateTime);
                        ci.Invalidate();
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] dot expired peer=" + ci.Entry.PeerId + " (sweep)");
                    }
            }
            catch { /* rebuild race — next tick */ }
            // Every 12th tick (60s at the 5s cadence), foreground only:
            _presenceTicks++;
            if (_presenceTicks % 12 == 0 && _appActive)
            {
                // PRESENCE keep-alive (official-client cadence): the server's online window is shorter
                // than a reading session, and reading produces ZERO input — refresh online every ~60s
                // while foreground and not truly idle, so the session stays visibly online on other
                // devices (Settings→Devices) without any input.
                if (_presenceOnline && now - _lastActivityTick < 300000) SendPresence(false);
                RefreshOpenGroupPresence();   // 3.3: open-group member/online counts
            }
        }

        /// <summary>PRESENCE 3.3: ONE bounded full-chat RPC for the OPEN group's member/online counts —
        /// at open and every 60s while it stays open and foreground. A switch mid-flight discards the
        /// result (entry comparison — E-3). Broadcasts never come here (subscribers only, no online).</summary>
        private async void RefreshOpenGroupPresence()
        {
            var entry = _selectedChat;
            if (entry?.PeerInfo == null || entry.PeerInfo is User) return;
            if (entry.PeerInfo is Channel bch && (bch.flags & Channel.Flags.broadcast) != 0) return;
            // The open path can run twice per row click (same double the [PIN] fetch shows) — a short
            // window guard keeps it to ONE RPC per open without touching the 60s cadence.
            if (entry.PeerId == _lastGroupRefreshPeer && Environment.TickCount - _lastGroupRefreshTick < 5000) return;
            _lastGroupRefreshPeer = entry.PeerId;
            _lastGroupRefreshTick = Environment.TickCount;
            try
            {
                var d = await _service.GetPeerDetailsAsync(entry.Peer, entry.PeerInfo);
                if (_selectedChat != entry || IsDisposed) return;   // switched away → discard
                if (d.members > 0) entry.ParticipantsCount = d.members;
                entry.OnlineCount = d.online;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] group refresh peer=" + entry.PeerId
                    + " members=" + d.members + " online=" + d.online);
                UpdateHeaderStatus();
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] group refresh failed: " + ex.Message);
            }
        }

        // ── Online / typing status ───────────────────────────────────────────

        internal static string StatusText(User u)
        {
            if (u == null) return "";
            switch (u.status)
            {
                case UserStatusOnline _: return "online";
                case UserStatusOffline off: return "last seen " + DrawHelper.ShortStamp(off.was_online);
                case UserStatusRecently _: return "last seen recently";
                case UserStatusLastWeek _: return "last seen within a week";
                case UserStatusLastMonth _: return "last seen within a month";
                default: return "";
            }
        }

        /// <summary>Refreshes the header subtitle (unless typing): users → presence/last-seen;
        /// channels/groups → subscriber/member count (PEER-PRESENTATION; empty until known).</summary>
        private void UpdateHeaderStatus()
        {
            if (_chatStatus == null || _typing) return;
            var entry = _selectedChat;
            var u = entry?.PeerInfo as User;
            if (u == null && entry?.PeerInfo != null)
            {
                int n = entry.ParticipantsCount;
                bool bcast = entry.PeerInfo is Channel bch && (bch.flags & Channel.Flags.broadcast) != 0;
                string txt = n > 0
                    ? n.ToString("N0") + (bcast ? (n == 1 ? " subscriber" : " subscribers")
                                                : (n == 1 ? " member" : " members"))
                    : "";   // count unknown → EMPTY, never "0 subscribers"
                // PRESENCE 3.1: groups append ", M online" when known; broadcasts never show online.
                if (!bcast && txt.Length > 0 && entry.OnlineCount >= 1)
                    txt += ", " + entry.OnlineCount.ToString("N0") + " online";
                _chatStatus.Text = txt;
                _chatStatus.ForeColor = _dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
                return;
            }
            _chatStatus.Text = u != null ? StatusText(u) : "";
            bool online = u?.status is UserStatusOnline;
            _chatStatus.ForeColor = online ? _accent : (_dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120));
        }

        /// <summary>ONE lazy count resolution per chat open (PEER-PRESENTATION 2.1): dialog-cached
        /// counts first (basic Chat always carries one; Channel sometimes does); else a single
        /// full-chat fetch per entry EVER (CountFetchTried latches even on a missing result). The
        /// header updates via BeginInvoke when the fetch lands — zero per-paint/per-update RPCs.</summary>
        private async void MaybeFetchChatCount(ChatEntry entry)
        {
            if (entry == null || entry.PeerInfo == null || entry.PeerInfo is User) return;
            if (entry.ParticipantsCount > 0) { UpdateHeaderStatus(); return; }   // cached — zero RPCs
            if (entry.PeerInfo is Chat c && c.participants_count > 0) entry.ParticipantsCount = c.participants_count;
            else if (entry.PeerInfo is Channel ch && ch.participants_count > 0) entry.ParticipantsCount = ch.participants_count;
            if (entry.ParticipantsCount > 0)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] subtitle count peer=" + entry.PeerId + " n=" + entry.ParticipantsCount + " src=dialog");
                UpdateHeaderStatus();
                return;
            }
            if (entry.CountFetchTried) return;   // the one lazy fetch already ran — missing stays missing quietly
            entry.CountFetchTried = true;
            try
            {
                var details = await _service.GetPeerDetailsAsync(entry.Peer, entry.PeerInfo);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] subtitle count peer=" + entry.PeerId + " n=" + details.members + " src=full-chat fetch");
                if (details.members > 0)
                {
                    entry.ParticipantsCount = details.members;
                    if (_selectedChat == entry && !IsDisposed)
                        try { BeginInvoke((Action)UpdateHeaderStatus); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[HDR] count fetch failed: " + ex.Message);
            }
        }

        private void ShowTypingFor(SendMessageAction action, string name = null)
        {
            if (action is SendMessageCancelAction)
            {
                _typing = false; _typingTimer.Stop(); UpdateHeaderStatus();
                return;
            }
            _typing = true;
            _chatStatus.Text = string.IsNullOrEmpty(name) ? "typing…" : name + " is typing…";
            _chatStatus.ForeColor = _accent;
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        // ── Message view ─────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task LoadHistoryAsync(ChatEntry entry, int focusMessageId = 0)
        {
            _chatTitle.Text = entry.Title;
            CancelReply();                 // a pending reply belongs to the chat we're leaving
            if (_selectionMode) ExitSelectionMode();   // selection belongs to the old chat too
            if (_voiceState != VoiceState.None) AbortVoice();
            _readOutboxMaxId = entry.ReadOutboxMaxId;   // so outgoing bubbles render ✓✓ correctly
            _shownMessageIds.Clear();
            _albumBubbles.Clear();   // album grouping is per-open-chat
            _currentChatMessages.Clear();
            _oldestMessageId = 0;
            _newestMessageId = 0;
            _hasMoreHistory = true;
            _loadingOlder = false;
            _loadingNewer = false;
            _atLiveTail = true;
            _sponsoredCard = null;   // card belongs to the chat we're leaving (panel is cleared below)

            _messagePanel.SuspendLayout();
            ClearMessagePanel();
            _messagePanel.ResumeLayout();

            try
            {
                // When focusing a specific message, load a window centred on it.
                var history = focusMessageId > 0
                    ? await _service.GetHistoryAsync(entry.Peer, 50, focusMessageId, addOffset: -25)
                    : await _service.GetHistoryAsync(entry.Peer, 50);
                if (_selectedChat != entry) return; // selection changed while loading

                var ordered = history.Messages.OrderBy(MsgDate).ToList();   // include service events, chronological

                _messagePanel.SuspendLayout();
                foreach (var mb in ordered)
                {
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fu) _peerNames[fu.id] = DisplayName(fu);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        AddMessageBubble(entry, m, ResolveSender(entry, m, from), from);
                        _currentChatMessages.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        CacheServiceNames(history.UserOrChat, svc);
                        AddMessageBubble(entry, svc, null, null);
                    }
                }
                _messagePanel.ResumeLayout();
                UpdateAudioPlaylist();

                if (ordered.Count > 0)
                {
                    _oldestMessageId = ordered[0].ID;
                    _newestMessageId = ordered.Max(mb => mb.ID);
                    // A focused jump loads an island; we're only at the live tail if it reached the chat's
                    // latest message. A plain bottom-load (focusMessageId==0) is always the tail. When the
                    // chat's latest id is unknown (TopMessageId==0), treat a focused load as an island so
                    // downward paging runs (it stops itself on a short page).
                    _atLiveTail = focusMessageId == 0
                                  || (entry.TopMessageId > 0 && _newestMessageId >= entry.TopMessageId);
                    // Open at the FIRST UNREAD message (not the bottom) and DON'T blanket-read on open:
                    // reading happens progressively when the user actually reaches the bottom (MarkCaughtUp).
                    bool openUnread = focusMessageId == 0 && entry.UnreadCount > 0 && entry.ReadInboxMaxId > 0;
                    if (focusMessageId > 0) ScrollToMessage(focusMessageId);
                    else if (openUnread && ScrollToFirstUnread(entry)) _jumpUnread = entry.UnreadCount;
                    else ScrollMessagesToBottom();
                    OnScrollPositionChanged();   // if we ended at the bottom (nothing unread) → read; else show the button
                }
                else
                {
                    _hasMoreHistory = false;
                }

                LoadSponsoredAsync(entry);   // Telegram ads (channels) — required for API ToS compliance
            }
            catch (Exception ex)
            {
                AddBubble("Failed to load history: " + ex.Message, null, false, DateTime.UtcNow);
            }
        }

        // ── Sponsored messages (Telegram ads — required for API ToS 3.3 compliance) ──
        // Channel sponsored messages: fetched per open channel (5-min cache), rendered as ONE distinct
        // card BELOW the last post (NOT history — excluded from paging/read-state/jump/reply), with the
        // mandatory view-on-display + click-on-interaction telemetry and the report flow.

        private readonly Dictionary<long, (DateTime at, Messages_SponsoredMessages res)> _sponsoredCache
            = new Dictionary<long, (DateTime, Messages_SponsoredMessages)>();
        private SponsoredCardControl _sponsoredCard;        // the appended card (null = none)
        private SponsoredMessage _sponsoredMsg;             // the current sponsored message
        private Messages_SponsoredMessages _sponsoredRes;   // holds chats/users for sponsor name/photo
        private readonly HashSet<string> _sponsoredViewed = new HashSet<string>();   // random_id hex → view sent

        /// <summary>Fetches a channel's sponsored message (5-min cache) and renders the card below the
        /// last post. Channel-only; empty results (region/Premium/account-dependent) show nothing.</summary>
        private async void LoadSponsoredAsync(ChatEntry entry)
        {
            _sponsoredCard = null;
            _sponsoredMsg = null;
            _sponsoredRes = null;
            if (entry?.Peer == null || !(entry.Peer is InputPeerChannel)) return;   // ads only in channels

            long key = entry.PeerId;
            Messages_SponsoredMessages res = null;
            if (_sponsoredCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.at) < TimeSpan.FromMinutes(5))
                res = cached.res;                                  // reuse within 5 minutes (spec-mandated cache)
            else
            {
                try { res = await _service.GetSponsoredMessagesAsync(entry.Peer); }
                catch { return; }                                  // no ad / not allowed → silent
                if (res != null) _sponsoredCache[key] = (DateTime.UtcNow, res);
            }
            if (_selectedChat != entry) return;                    // chat changed while awaiting
            if (res?.messages == null || res.messages.Length == 0) return;   // empty = no card, no error

            _sponsoredRes = res;
            _sponsoredMsg = res.messages[0];   // one card below the last post (posts_between insertion is out of scope)
            BuildSponsoredCard(entry, res, _sponsoredMsg);
        }

        /// <summary>Builds the sponsored card, appends it as the LAST panel item, and lazily loads its art.</summary>
        private void BuildSponsoredCard(ChatEntry entry, Messages_SponsoredMessages res, SponsoredMessage sm)
        {
            bool recommended = (sm.flags & SponsoredMessage.Flags.recommended) != 0;
            bool hasMedia = (sm.flags & SponsoredMessage.Flags.has_media) != 0 && sm.media != null;

            var card = new SponsoredCardControl(recommended ? "Recommended" : "Sponsored", sm.title, sm.message,
                sm.entities, sm.button_text, hasMedia, ResolveCustomEmoji)
            {
                AccentColor = SponsoredAccent(sm),
                IsDark = _dark,
                Width = ContentWidth(_messagePanel)
            };
            card.LinkClicked += url => OnSponsoredLink(sm, url);
            card.ButtonClicked += () => OnSponsoredOpen(sm, false);
            card.MediaClicked += () => OnSponsoredOpen(sm, true);
            card.SponsorClicked += () => OnSponsoredOpen(sm, false);
            card.MenuRequested += pt => ShowSponsoredMenu(sm, pt);
            card.Measure();

            _sponsoredCard = card;
            _messagePanel.Controls.Add(card);
            KeepSponsoredLast();

            if ((sm.flags & SponsoredMessage.Flags.has_photo) != 0 && sm.photo is Photo ph)
                LoadSponsoredImage(card, ph, false);
            if (hasMedia && sm.media is MessageMediaPhoto mmp && mmp.photo is Photo mph)
                LoadSponsoredImage(card, mph, true);   // photo media → thumbnail (doc/video media shows a placeholder)

            OnScrollPositionChanged();   // re-check the view trigger now the card is present
        }

        /// <summary>Maps a sponsored PeerColor base index to an accent; falls back to the theme accent.</summary>
        private Color SponsoredAccent(SponsoredMessage sm)
        {
            if ((sm.flags & SponsoredMessage.Flags.has_color) != 0 && sm.color is PeerColor pc)
                switch (pc.color)   // base palette 0..6
                {
                    case 0: return Color.FromArgb(229, 57, 53);
                    case 1: return Color.FromArgb(245, 124, 0);
                    case 2: return Color.FromArgb(142, 36, 170);
                    case 3: return Color.FromArgb(67, 160, 71);
                    case 4: return Color.FromArgb(0, 172, 193);
                    case 5: return Color.FromArgb(30, 136, 229);
                    case 6: return Color.FromArgb(216, 27, 96);
                }
            return _accent;
        }

        private async void LoadSponsoredImage(SponsoredCardControl card, Photo photo, bool asMedia)
        {
            try
            {
                var bytes = await _service.DownloadPhotoThumbAsync(photo);
                if (bytes == null || bytes.Length == 0 || card.IsDisposed) return;
                var img = await ToBitmapAsync(bytes);
                if (asMedia) card.SetMediaThumb(img); else card.SetPhoto(img);
            }
            catch { /* art is best-effort */ }
        }

        /// <summary>Keeps the sponsored card pinned to the bottom (below any newly appended real posts).</summary>
        private void KeepSponsoredLast()
        {
            if (_sponsoredCard != null && !_sponsoredCard.IsDisposed && _messagePanel.Controls.Contains(_sponsoredCard))
                _messagePanel.Controls.SetChildIndex(_sponsoredCard, _messagePanel.Controls.Count - 1);
        }

        private void RemoveSponsoredCard()
        {
            if (_sponsoredCard != null && !_sponsoredCard.IsDisposed)
            {
                _messagePanel.Controls.Remove(_sponsoredCard);
                _sponsoredCard.Dispose();
            }
            _sponsoredCard = null;
        }

        // ── Mandatory telemetry: view on display, click on interaction ──────

        /// <summary>Fires viewSponsoredMessage ONCE, when the ad's full text band is within the viewport.</summary>
        private void MaybeViewSponsored()
        {
            var card = _sponsoredCard;
            if (card == null || card.IsDisposed || _sponsoredMsg == null) return;
            string key = BitConverter.ToString(_sponsoredMsg.random_id);
            if (_sponsoredViewed.Contains(key)) return;

            int vTop = -_messagePanel.AutoScrollPosition.Y;
            int vBottom = vTop + _messagePanel.ClientSize.Height;
            int tTop = card.Top + card.ViewTextTop, tBottom = card.Top + card.ViewTextBottom;
            // Full text shown: its bottom is visible AND (its top is visible OR it's taller than the viewport).
            bool full = tBottom <= vBottom && (tTop >= vTop || (tBottom - tTop) >= (vBottom - vTop));
            if (!full) return;

            _sponsoredViewed.Add(key);
            var rid = _sponsoredMsg.random_id;
            var _ = SafeSponsored(() => _service.ViewSponsoredAsync(rid));
        }

        private async System.Threading.Tasks.Task SafeSponsored(Func<System.Threading.Tasks.Task> call)
        { try { await call(); } catch { /* telemetry is best-effort once attempted */ } }

        private void OnSponsoredLink(SponsoredMessage sm, string url)
        {
            var _ = SafeSponsored(() => _service.ClickSponsoredAsync(sm.random_id, false));
            ResolveLinkAsync(url);                     // link in the ad text → in-app if t.me, else browser
        }

        /// <summary>Button / sponsor name / photo / media click → ClickSponsored(+media) then route the url
        /// through the in-app resolver (so a t.me "VIEW CHANNEL" opens in-app; external urls go to browser).</summary>
        private void OnSponsoredOpen(SponsoredMessage sm, bool media)
        {
            var _ = SafeSponsored(() => _service.ClickSponsoredAsync(sm.random_id, media));
            ResolveLinkAsync(sm.url);
        }

        private static bool IsTelegramDomain(string url)
        {
            try
            {
                var host = new Uri(NormalizeUrl(url), UriKind.Absolute).Host;
                return System.Text.RegularExpressions.Regex.IsMatch(host,
                    @"(^|\.)(telegram\.(org|me|dog)|t\.me|te\.?legra\.ph|graph\.org|fragment\.com|telesco\.pe)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        // ── In-app link / mention router (Part A) ────────────────────────────
        private enum TgKind { External, User, PrivateChannel, Invite }

        /// <summary>Single router every link/mention/sponsored-button click goes through: opens t.me/tg://
        /// targets IN-APP, sends everything else to the browser (with the Telegram-domain confirm gate).</summary>
        private async void ResolveLinkAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var kind = ParseTgLink(url, out string username, out int msgId, out long channelId, out string invite, out string startParam);
            bool startGroup = url.IndexOf("startgroup", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                switch (kind)
                {
                    case TgKind.User:
                        await OpenByUsername(username, msgId);
                        if (startGroup) ThemedDialog.Show(this, "Add to group", "Adding a bot to a group isn't supported yet.", "OK");
                        else if (!string.IsNullOrEmpty(startParam)) await StartBotIfPossible(startParam);   // /start <param> handshake
                        break;
                    case TgKind.PrivateChannel: await OpenByChannelId(channelId, msgId); break;
                    case TgKind.Invite: await OpenInvite(invite); break;
                    default: OpenExternalUrl(url); break;
                }
            }
            catch { OpenExternalUrl(url); }   // any resolution failure → graceful browser fallback
        }

        /// <summary>Opens an external url; confirms first UNLESS the host is one of Telegram's own domains.</summary>
        private void OpenExternalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!IsTelegramDomain(url))
            {
                int c = ThemedDialog.Show(this, "Open link", "Open this link?\n\n" + url, "Open", "Cancel");
                // After the modal closes, don't let focus fall through to the search box (MaterialSkin would
                // otherwise re-focus the first text box) — clear the active control.
                try { ActiveControl = null; } catch { }
                if (c != 0) return;
            }
            try { System.Diagnostics.Process.Start(NormalizeUrl(url)); } catch { }   // scheme-less ("site.com") needs https://
        }

        /// <summary>Adds an https:// scheme to a scheme-less link so Uri parsing + Process.Start work.</summary>
        private static string NormalizeUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return u;
            u = u.Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(u, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:") ? u : "https://" + u;
        }

        /// <summary>Classifies a url/@username/tg:// link; out-params carry the resolved target.</summary>
        private static TgKind ParseTgLink(string url, out string username, out int msgId, out long channelId, out string invite, out string startParam)
        {
            username = null; msgId = 0; channelId = 0; invite = null; startParam = null;
            string u = (url ?? "").Trim();
            if (u.Length == 0) return TgKind.External;

            if (u[0] == '@')   // bare @username
            {
                username = u.Substring(1).Split('?', '/')[0];
                return string.IsNullOrEmpty(username) ? TgKind.External : TgKind.User;
            }

            if (u.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            {
                var q = ParseQuery(u);
                if (u.StartsWith("tg://resolve", StringComparison.OrdinalIgnoreCase) && q.TryGetValue("domain", out var dom) && !string.IsNullOrEmpty(dom))
                {
                    username = dom;
                    if (q.TryGetValue("post", out var post)) int.TryParse(post, out msgId);
                    if (q.TryGetValue("start", out var sp) && !string.IsNullOrEmpty(sp)) startParam = sp;   // /start deep-link
                    return TgKind.User;
                }
                if (u.StartsWith("tg://join", StringComparison.OrdinalIgnoreCase) && q.TryGetValue("invite", out var inv) && !string.IsNullOrEmpty(inv))
                { invite = inv; return TgKind.Invite; }
                return TgKind.External;
            }

            Uri uri;
            // Message links are often scheme-less ("t.me/Channel") — absolute-Uri parsing needs a scheme.
            if (!Uri.TryCreate(NormalizeUrl(u), UriKind.Absolute, out uri)) return TgKind.External;
            string host = uri.Host.ToLowerInvariant();
            if (host != "t.me" && host != "telegram.me" && host != "telegram.dog") return TgKind.External;

            var segs = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return TgKind.External;
            string first = segs[0];

            if (first.StartsWith("+")) { invite = first.Substring(1); return string.IsNullOrEmpty(invite) ? TgKind.External : TgKind.Invite; }
            if (first.Equals("joinchat", StringComparison.OrdinalIgnoreCase))
            { invite = segs.Length > 1 ? segs[1] : null; return string.IsNullOrEmpty(invite) ? TgKind.External : TgKind.Invite; }
            if (first.Equals("c", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length >= 2 && long.TryParse(segs[1], out channelId))
                {
                    if (segs.Length >= 3) int.TryParse(segs[2], out msgId);
                    return TgKind.PrivateChannel;
                }
                return TgKind.External;
            }
            // non-username reserved first segments → browser
            if (first.Equals("share", StringComparison.OrdinalIgnoreCase) || first.Equals("addstickers", StringComparison.OrdinalIgnoreCase)
                || first.Equals("addemoji", StringComparison.OrdinalIgnoreCase) || first.Equals("setlanguage", StringComparison.OrdinalIgnoreCase)
                || first.Equals("proxy", StringComparison.OrdinalIgnoreCase) || first.Equals("socks", StringComparison.OrdinalIgnoreCase)
                || first.Equals("login", StringComparison.OrdinalIgnoreCase) || first.Equals("bg", StringComparison.OrdinalIgnoreCase))
                return TgKind.External;

            int postIdx = 1;
            if (first.Equals("s", StringComparison.OrdinalIgnoreCase) && segs.Length >= 2) { first = segs[1]; postIdx = 2; }   // t.me/s/<channel>

            username = first;
            if (segs.Length > postIdx) int.TryParse(segs[postIdx], out msgId);
            var query = ParseQuery(url);
            if (query.TryGetValue("start", out var qsp) && !string.IsNullOrEmpty(qsp)) startParam = qsp;   // t.me/<bot>?start=<param>
            return TgKind.User;
        }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int qi = url.IndexOf('?');
            if (qi < 0) return d;
            foreach (var pair in url.Substring(qi + 1).Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) try { d[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]); } catch { }
            }
            return d;
        }

        /// <summary>Resolves a @username/domain → opens that chat in-app (focused on msgId when given).</summary>
        private async System.Threading.Tasks.Task OpenByUsername(string username, int msgId)
        {
            if (string.IsNullOrEmpty(username)) return;
            var who = await _service.ResolveUsernameAsync(username.TrimStart('@'));
            if (IsDisposed) return;
            var entry = EntryFromPeerInfo(who);
            if (entry == null) { OpenExternalUrl("https://t.me/" + username); return; }   // unresolved → browser
            await OpenChat(entry, msgId);
            if (msgId > 0) ScrollToAndFlash(msgId);
        }

        /// <summary>t.me/c/&lt;id&gt;/&lt;msg&gt;: opens the private channel IF we're a member; else browser.</summary>
        private async System.Threading.Tasks.Task OpenByChannelId(long channelId, int msgId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == channelId);
            if (entry == null) { OpenExternalUrl("https://t.me/c/" + channelId + (msgId > 0 ? "/" + msgId : "")); return; }
            await OpenChat(entry, msgId);
            if (msgId > 0) ScrollToAndFlash(msgId);
        }

        /// <summary>Invite link: open if already a member/peekable; otherwise confirm + join, then open.</summary>
        private async System.Threading.Tasks.Task OpenInvite(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            ChatInviteBase info;
            try { info = await _service.CheckInviteAsync(hash); }
            catch { OpenExternalUrl("https://t.me/+" + hash); return; }

            if (info is ChatInviteAlready already) { await OpenInvitedChat(already.chat); return; }
            if (info is ChatInvitePeek peek) { await OpenInvitedChat(peek.chat); return; }
            if (info is ChatInvite preview)
            {
                int c = ThemedDialog.Show(this, "Join",
                    "Join \"" + preview.title + "\"?", "Join", "Cancel");
                if (c != 0) return;
                try
                {
                    await _service.JoinInviteAsync(hash);
                    var after = await _service.CheckInviteAsync(hash);   // now resolves to the joined chat
                    if (after is ChatInviteAlready joined) await OpenInvitedChat(joined.chat);
                    else if (after is ChatInvitePeek peeked) await OpenInvitedChat(peeked.chat);
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Join failed", ex.Message, "OK"); }
                return;
            }
            OpenExternalUrl("https://t.me/+" + hash);
        }

        private async System.Threading.Tasks.Task OpenInvitedChat(ChatBase chat)
        {
            var entry = EntryFromPeerInfo(chat);
            if (entry != null) await OpenChat(entry, 0);
        }

        /// <summary>A ChatEntry for a resolved peer — the existing dialog-list row if present, else a fresh one.</summary>
        private ChatEntry EntryFromPeerInfo(IPeerInfo info)
        {
            if (info is User u)
                return _allChats.FirstOrDefault(c => c.PeerId == u.id)
                    ?? new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u), IsGroup = false, PeerInfo = u };
            if (info is ChatBase cb)
                return _allChats.FirstOrDefault(c => c.PeerId == cb.ID)
                    ?? new ChatEntry { Peer = cb.ToInputPeer(), PeerId = cb.ID, Title = cb.Title, IsGroup = true, PeerInfo = cb };
            return null;
        }

        // ── Sponsor info + report flow (card menu) ──────────────────────────

        private void ShowSponsoredMenu(SponsoredMessage sm, Point screenPt)
        {
            var menu = new ThemedContextMenuStrip();
            bool hasInfo = (sm.flags & SponsoredMessage.Flags.has_sponsor_info) != 0
                        || (sm.flags & SponsoredMessage.Flags.has_additional_info) != 0;
            if (hasInfo) AddMenuItem(menu, "ⓘ   Sponsor info", () => ShowSponsorInfo(sm));
            if ((sm.flags & SponsoredMessage.Flags.can_report) != 0)
                AddMenuItem(menu, "⚑   About this ad", () => ReportSponsored(sm, ""));
            if (menu.Items.Count == 0) { menu.Dispose(); return; }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        private void ShowSponsorInfo(SponsoredMessage sm)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(sm.sponsor_info)) sb.AppendLine(sm.sponsor_info);
            if (!string.IsNullOrEmpty(sm.additional_info)) { if (sb.Length > 0) sb.AppendLine(); sb.Append(sm.additional_info); }
            ThemedDialog.Show(this, "Sponsor info", sb.ToString().Trim(), "OK");
        }

        /// <summary>The ad report flow: empty option → ChooseOption picker → re-report; handles AdsHidden /
        /// Reported / AD_EXPIRED / PREMIUM_ACCOUNT_REQUIRED.</summary>
        private async void ReportSponsored(SponsoredMessage sm, string option)
        {
            try
            {
                var result = await _service.ReportSponsoredAsync(sm.random_id, option);
                if (result is Channels_SponsoredMessageReportResultChooseOption choose)
                {
                    string picked = PickReportOption(choose);
                    if (picked != null) ReportSponsored(sm, picked);     // re-call with the chosen option
                }
                else if (result is Channels_SponsoredMessageReportResultAdsHidden)
                {
                    ThemedDialog.Show(this, "Ads hidden", "Sponsored messages are now hidden.", "OK");
                    RemoveSponsoredCard();
                }
                else // Channels_SponsoredMessageReportResultReported
                {
                    ThemedDialog.Show(this, "Reported", "Thank you — this ad has been reported.", "OK");
                    RemoveSponsoredCard();
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message != null && ex.Message.Contains("AD_EXPIRED") ? "This ad has expired."
                    : ex.Message != null && ex.Message.Contains("PREMIUM_ACCOUNT_REQUIRED") ? "Reporting ads requires Telegram Premium."
                    : ex.Message;
                ThemedDialog.Show(this, "Report", msg, "OK");
            }
        }

        private string PickReportOption(Channels_SponsoredMessageReportResultChooseOption choose)
        {
            if (choose.options == null || choose.options.Length == 0) return null;
            var labels = new List<string>();
            foreach (var o in choose.options) labels.Add(o.text);
            labels.Add("Cancel");
            int idx = ThemedDialog.Show(this, string.IsNullOrEmpty(choose.title) ? "Report ad" : choose.title, "", labels.ToArray());
            return (idx >= 0 && idx < choose.options.Length) ? choose.options[idx].option : null;
        }

        private void ScrollToMessage(int messageId)
        {
            _messagePanel.PerformLayout();
            var c = FindMessageControl(messageId);
            if (c != null) { _messagePanel.ScrollControlIntoView(c); return; }
            ScrollMessagesToBottom(); // target not in the loaded window
        }

        private async System.Threading.Tasks.Task LoadOlderMessages()
        {
            if (_loadingOlder || !_hasMoreHistory || _selectedChat == null || _oldestMessageId == 0)
                return;
            // TOUCH-FREEZE: a pan parked near the top re-fires this at pan-tick rate (85/s captured live).
            // The in-flight guard only spaces FETCHES — completions must be spaced too, or back-to-back
            // fetch+merge+reflow cycles saturate the UI thread on slow hardware (the RT/SB2 hard freeze).
            if (Environment.TickCount - _lastLoadOlderDoneTick < 300) return;

            _loadingOlder = true;                                                  // in-flight guard (reset in finally)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] in-flight guard set");
            try
            {
                var entry = _selectedChat;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] load-older requested (offset id=" + _oldestMessageId + ")");
                var history = await _service.GetHistoryAsync(entry.Peer, 50, _oldestMessageId);
                if (_selectedChat != entry) return;

                var raw = history.Messages ?? new MessageBase[0];
                var older = raw
                    .Where(mb => !_shownMessageIds.Contains(mb.ID))
                    .OrderBy(MsgDate)
                    .ToList();   // MessageBase (messages + service events)

                if (older.Count == 0)
                {
                    // Latch "end of chat" ONLY when the SERVER genuinely returned no older messages. A
                    // transient/aborted fetch throws (caught below, retryable) — and a page that deduped to
                    // zero (overlap/race) is NOT the top: advance past it and keep paging open.
                    if (raw.Length == 0)
                    {
                        _hasMoreHistory = false;
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] reached real top (server: no more)");
                    }
                    else
                    {
                        int minRaw = raw.Min(mb => mb.ID);
                        if (minRaw < _oldestMessageId) _oldestMessageId = minRaw;   // step past the overlap
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] older page all-deduped; advanced offset, NOT latching");
                    }
                    return;
                }

                // Anchor on the current top message so the view doesn't jump.
                var anchor = _messagePanel.Controls.Count > 0 ? _messagePanel.Controls[0] : null;

                _messagePanel.SuspendLayout();
                int idx = 0;
                var addedMsgs = new List<Message>();
                foreach (var mb in older)
                {
                    Control ctl;
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fuo) _peerNames[fuo.id] = DisplayName(fuo);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        // CHAT-RENDER-INTEGRITY: album items GROUP here too. The old direct-MakeMessageBubble
                        // bypass gave every grouped item its OWN full bubble and re-measured any existing album
                        // it should have merged into — the pin-window corruption trigger. ONE bubble per album,
                        // dedup via HandleAlbumItem's own shownIds bookkeeping (albums counted consistently).
                        if (m.grouped_id != 0)
                        {
                            MessageBubbleControl created;
                            int handled = HandleAlbumItem(entry, m, ResolveSender(entry, m, from), from, out created);
                            if (handled == 1)
                            {
                                addedMsgs.Add(m);
                                if (created != null)
                                {
                                    _messagePanel.Controls.Add(created);
                                    _messagePanel.Controls.SetChildIndex(created, idx++);
                                }
                            }
                            continue;   // merged into an existing bubble → no new control to position
                        }
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        ctl = MakeMessageBubble(entry, m, ResolveSender(entry, m, from));
                        if (ctl is MessageBubbleControl mbo)
                        {
                            if (entry.IsGroup && !IsOut(m) && from is User fuo2) WireSenderAvatar(mbo, fuo2);
                            ApplyReactions(mbo, m);
                            ApplyEntities(mbo, m);
                        }
                        addedMsgs.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        CacheServiceNames(history.UserOrChat, svc); ctl = MakeServiceLine(svc);
                    }
                    else continue;
                    if (ctl == null) continue;
                    _messagePanel.Controls.Add(ctl);
                    _messagePanel.Controls.SetChildIndex(ctl, idx++);
                }
                _oldestMessageId = older[0].ID;
                _currentChatMessages.InsertRange(0, addedMsgs);
                _messagePanel.ResumeLayout();

                if (ReconcileWindowOrHeal(entry)) return;   // 1.4: overlap detected → self-healing reload

                if (anchor != null) _messagePanel.ScrollControlIntoView(anchor);
            }
            catch (Exception ex)
            {
                // Transient (reconnect / aborted): do NOT latch _hasMoreHistory — stays retryable next scroll.
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] load-older failed (retryable): " + ex.Message);
            }
            finally
            {
                _loadingOlder = false;                                             // reset so the next scroll can retry
                _lastLoadOlderDoneTick = Environment.TickCount;                    // TOUCH-FREEZE: space completions
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] in-flight guard reset");
            }
        }

        /// <summary>
        /// DOWNWARD paging (Part 3): after a focused jump the loaded window is an island; when the user
        /// scrolls near its bottom and the newest loaded id isn't the chat's latest, fetch the page of
        /// messages NEWER than the newest loaded id and append them — repeating until the live tail is
        /// reached, at which point normal new-message appending resumes.
        /// </summary>
        private async System.Threading.Tasks.Task LoadNewerMessages()
        {
            if (_loadingNewer || _atLiveTail || _selectedChat == null || _newestMessageId == 0)
                return;

            _loadingNewer = true;
            try
            {
                var entry = _selectedChat;
                // Messages newer than _newestMessageId: anchor at it, move back (toward newer) by one page.
                var history = await _service.GetHistoryAsync(entry.Peer, 50, _newestMessageId, addOffset: -50);
                if (_selectedChat != entry) return;

                var newer = history.Messages
                    .Where(mb => mb.ID > _newestMessageId && !_shownMessageIds.Contains(mb.ID))
                    .OrderBy(MsgDate)
                    .ToList();   // strictly newer than what we hold, chronological

                if (newer.Count == 0) { _atLiveTail = true; OnScrollPositionChanged(); return; }

                int keepY = -_messagePanel.AutoScrollPosition.Y;
                bool wasAtBottom = AtBottom(AtBottomThreshold);

                _messagePanel.SuspendLayout();
                var addedMsgs = new List<Message>();
                foreach (var mb in newer)
                {
                    Control ctl;
                    if (mb is Message m)
                    {
                        var from = SenderInfo(history, m);
                        if (from is User fu) _peerNames[fu.id] = DisplayName(fu);
                        if (m.fwd_from != null) CacheForwardName(history.UserOrChat, m.fwd_from);
                        // CHAT-RENDER-INTEGRITY: same album-grouping fix as the older-page merge.
                        if (m.grouped_id != 0)
                        {
                            MessageBubbleControl created;
                            int handled = HandleAlbumItem(entry, m, ResolveSender(entry, m, from), from, out created);
                            if (handled == 1)
                            {
                                addedMsgs.Add(m);
                                if (created != null) _messagePanel.Controls.Add(created);
                            }
                            continue;
                        }
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        ctl = MakeMessageBubble(entry, m, ResolveSender(entry, m, from));
                        if (ctl is MessageBubbleControl mb2)
                        {
                            if (entry.IsGroup && !IsOut(m) && from is User fu2) WireSenderAvatar(mb2, fu2);
                            ApplyReactions(mb2, m);
                            ApplyEntities(mb2, m);
                        }
                        addedMsgs.Add(m);
                    }
                    else if (mb is MessageService svc)
                    {
                        if (!_shownMessageIds.Add(mb.ID)) continue;
                        CacheServiceNames(history.UserOrChat, svc); ctl = MakeServiceLine(svc);
                    }
                    else continue;
                    if (ctl == null) continue;
                    _messagePanel.Controls.Add(ctl);   // append at the bottom (this is the newer page)
                }
                _currentChatMessages.AddRange(addedMsgs);   // newer than all loaded → already in order
                _newestMessageId = Math.Max(_newestMessageId, newer.Max(mb => mb.ID));
                KeepSponsoredLast();                        // the ad card stays below the newly appended posts
                _messagePanel.ResumeLayout();
                if (ReconcileWindowOrHeal(entry)) return;   // 1.4: overlap detected → self-healing reload
                UpdateAudioPlaylist();

                // A short page (or reaching the chat's latest) means the island just met the live tail.
                if (newer.Count < 50 || _newestMessageId >= entry.TopMessageId) _atLiveTail = true;

                if (!wasAtBottom) _messagePanel.AutoScrollPosition = new Point(0, keepY);   // don't yank a mid-island reader
                OnScrollPositionChanged();
            }
            catch { /* transient — retried on next scroll */ }
            finally { _loadingNewer = false; }
        }

        /// <summary>CHAT-RENDER-INTEGRITY 1.4: cheap post-mutation insurance — after a window merge the panel
        /// must lay out as one strictly descending column (each child at/below the previous one's bottom).
        /// Any overlap (this bug class's symptom) logs the state triplet and SELF-HEALS by reloading the
        /// window through the proven full-load path, anchored at the current top message. Returns true when
        /// healing was triggered — callers must stop touching the panel.</summary>
        private bool ReconcileWindowOrHeal(ChatEntry entry)
        {
            bool overlap = false;
            int prevBottom = int.MinValue;
            Control prev = null, bad = null;
            foreach (Control c in _messagePanel.Controls)
            {
                if (!c.Visible) continue;
                // prev==null guard: prevBottom-2 would UNDERFLOW int.MinValue and flag the first control
                if (prev != null && c.Top < prevBottom - 2) { overlap = true; bad = c; break; }   // 2px slack for margins/rounding
                if (c.Bottom > prevBottom) prevBottom = c.Bottom;
                prev = c;
            }
            if (!overlap) return false;
            // TOUCH-FREEZE conviction (131 heals in one session log): healing DURING a live gesture
            // disposes the control under the finger — the WM_TOUCH stream dies mid-gesture (the stuck-
            // _active incident) and the reloaded window re-arms the near-top trigger → an unbounded
            // fetch-merge-heal livelock that reads as a hard freeze on slow hardware. Defer while a
            // finger/coast drives the panel, and never heal more than once per 3s — every subsequent
            // pager merge re-checks anyway, so a real persistent overlap still gets healed.
            if (TouchScroller.GestureOrCoastActive)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch — heal DEFERRED (gesture active)");
                return false;
            }
            if (Environment.TickCount - _lastHealTick < 3000)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch — heal SUPPRESSED (rate limit)");
                return false;
            }
            _lastHealTick = Environment.TickCount;
            if (LogOn)
            {
                System.Diagnostics.Debug.WriteLine("[RENDER] reconcile mismatch model=" + _currentChatMessages.Count
                    + " panel=" + _messagePanel.Controls.Count + " shown=" + _shownMessageIds.Count + " → full re-render");
                if (bad != null) System.Diagnostics.Debug.WriteLine("[RENDER]   pair: prev=" + (prev == null ? "?" : prev.GetType().Name)
                    + " id=" + (prev == null ? 0 : BubbleMsgId(prev)) + " bounds=" + (prev == null ? Rectangle.Empty : prev.Bounds)
                    + " | bad=" + bad.GetType().Name + " id=" + BubbleMsgId(bad) + " bounds=" + bad.Bounds
                    + " idx=" + _messagePanel.Controls.IndexOf(bad));
            }
            int anchorId = 0;
            foreach (Control c in _messagePanel.Controls) { anchorId = BubbleMsgId(c); if (anchorId > 0) break; }
            var _ = LoadHistoryAsync(entry, anchorId);   // async self-heal through the healthy path (resets the triplet)
            return true;
        }

        private async void MessagePanel_Scroll(object sender, ScrollEventArgs e)
        {
            if (e.ScrollOrientation != ScrollOrientation.VerticalScroll) return;
            OnScrollPositionChanged();
            if (e.NewValue <= 0)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[SCROLL] scroll-event near-top → load older");
                await LoadOlderMessages();
            }
        }

        private static string ResolveSender(ChatEntry entry, Message m, IPeerInfo from)
        {
            if (IsOut(m) || !entry.IsGroup || m.from_id == null) return null;
            return from is User fu ? DisplayName(fu) : (from as ChatBase)?.Title;
        }

        private static bool IsOut(Message m)
        {
            return (m.flags & Message.Flags.out_) != 0;
        }

        private MessageBubbleControl CreateBubble(string text, string sender, bool outgoing, DateTime date, int messageId = 0)
        {
            var bubble = new MessageBubbleControl(text, sender, outgoing, date)
            {
                AccentColor = _accent,
                IsDark = _dark,
                Width = ContentWidth(_messagePanel),
                MessageId = messageId
            };
            bubble.Read = outgoing && messageId > 0 && messageId <= _readOutboxMaxId;
            bubble.ContextMenuRequested += OnBubbleContextMenu;
            bubble.SelectionMode = _selectionMode;
            bubble.SelectionToggled += OnBubbleSelectionToggled;
            bubble.ReplyQuoteClicked += JumpToReply;       // tap the reply quote → scroll to + flash the original
            bubble.Measure();
            return bubble;
        }

        /// <summary>Adds a bubble, skipping duplicates by message id. Returns false if skipped.</summary>
        private bool AddBubble(string text, string sender, bool outgoing, DateTime date, int messageId = 0)
        {
            if (messageId != 0 && !_shownMessageIds.Add(messageId))
                return false; // already shown
            _messagePanel.Controls.Add(CreateBubble(text, sender, outgoing, date, messageId));
            return true;
        }

        /// <summary>
        /// Sets the "replying to…" quote on a bubble when the message is a reply, looking up
        /// the target's text in the already-loaded history (history loads oldest→newest, so the
        /// older target is present). Re-measures so the quote's height is included.
        /// </summary>
        private void SetReply(MessageBubbleControl bubble, Message m)
        {
            bubble.Edited = (m.flags & Message.Flags.has_edit_date) != 0;   // "edited" before the timestamp
            // Part 2 — forward attribution (independent of any reply).
            bubble.ForwardedFrom = ResolveForwardName(m.fwd_from);

            var rh = m.reply_to as MessageReplyHeader;
            bool hasReply = rh != null && rh.reply_to_msg_id != 0;
            if (hasReply)
            {
                bubble.ReplyToMsgId = rh.reply_to_msg_id;   // Part B — tap the quote to jump to it
                var target = _currentChatMessages.FirstOrDefault(x => x.ID == rh.reply_to_msg_id);
                string preview = target != null ? GetDisplayText(target) : "Message";
                if (preview.Length > 60) preview = preview.Substring(0, 60) + "…";
                bubble.ReplyPreview = preview;
                bubble.ReplySender = target != null ? ResolveReplySenderName(target) : null;   // Part 3
            }

            if (hasReply || bubble.ForwardedFrom != null) bubble.Measure();
        }

        /// <summary>Resolves a forward source name. Order: from_id (peer cache) → from_name → post_author.</summary>
        private string ResolveForwardName(MessageFwdHeader fwd)
        {
            if (fwd == null) return null;
            if (fwd.from_id != null && _peerNames.TryGetValue(fwd.from_id.ID, out var cached)) return cached;
            if (!string.IsNullOrEmpty(fwd.from_name)) return fwd.from_name;        // user who hid their account
            if (!string.IsNullOrEmpty(fwd.post_author)) return fwd.post_author;    // channel post byline
            return fwd.from_id != null ? NameOf(fwd.from_id) : "Unknown";
        }

        /// <summary>Display name of a quoted message's sender ("You" / group member / chat peer).</summary>
        private string ResolveReplySenderName(Message target)
        {
            if (IsOut(target)) return "You";
            if (target.from_id != null) return NameOf(target.from_id);
            return _selectedChat?.Title;   // 1:1 incoming carries no from_id → the chat peer
        }

        /// <summary>Caches a forward source's display name so the header resolves it instead of "Someone".</summary>
        private void CacheForwardName(Func<Peer, IPeerInfo> resolve, MessageFwdHeader fwd)
        {
            if (fwd?.from_id == null) return;
            var info = resolve(fwd.from_id);
            if (info is User u) _peerNames[u.id] = DisplayName(u);
            else if (info is ChatBase cb) _peerNames[cb.ID] = cb.Title;
        }

        private static string StickerCachePath(long id)
        {
            return MediaCache.ThumbPath("sticker_" + id + ".png");
        }

        /// <summary>Disk path for a cached doc thumbnail (video/GIF poster), reused across sessions.</summary>
        /// <summary>Disk path for a cached raw .tgs (animated sticker) payload, to skip re-download.</summary>
        private static string TgsCachePath(long id)
        {
            return MediaCache.ThumbPath("tgs_" + id + ".tgs");
        }

        /// <summary>Sticker pixel dims from its video / image-size attribute (default 512²) — for the WebM render.</summary>
        private static void StickerDims(Document doc, out int w, out int h)
        {
            w = 512; h = 512;
            var v = doc.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
            if (v != null && v.w > 0) { w = v.w; h = v.h; return; }
            var s = doc.attributes?.OfType<DocumentAttributeImageSize>().FirstOrDefault();
            if (s != null && s.w > 0) { w = s.w; h = s.h; }
        }

        private async void StartStickerLoad(MessageBubbleControl bubble, Document doc)
        {
            try
            {
                // WebM video sticker: static thumbnail by default, play the looping video on CLICK (handles its
                // own thumb caching + click registration, so it runs BEFORE the generic thumb-cache shortcut).
                if (doc.mime_type == "video/webm") { await SetupWebmSticker(bubble, doc); return; }

                if (_photoThumbCache.TryGetValue(doc.id, out var cached))
                { if (!bubble.IsDisposed) bubble.SetImage(cached); return; }

                // Animated Lottie (.tgs): loop it via rlottie (if the native lib is present).
                if (doc.mime_type == "application/x-tgsticker" && RLottie.Available)
                {
                    string tgsPath = TgsCachePath(doc.id);
                    byte[] tgs = null;
                    if (File.Exists(tgsPath) && new FileInfo(tgsPath).Length > 0)
                        try { tgs = File.ReadAllBytes(tgsPath); } catch { }
                    if (tgs == null)
                    {
                        tgs = await _service.DownloadDocBytesAsync(doc);
                        if (tgs != null)
                            try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); File.WriteAllBytes(tgsPath, tgs); } catch { }
                    }
                    var clip = await System.Threading.Tasks.Task.Run(() => RLottie.OpenTgs(tgs));   // gunzip + Lottie parse OFF the UI thread
                    if (clip != null)
                    {
                        if (bubble.IsDisposed) { clip.Dispose(); return; }
                        bubble.AnimationOwner = new LottieAnimator(clip, 160,
                            img => { if (!bubble.IsDisposed) bubble.SetFrame(img); });
                        return;
                    }
                }

                Image bmp = null;
                string path = StickerCachePath(doc.id);
                if (System.IO.File.Exists(path))
                    try { using (var fs = System.IO.File.OpenRead(path)) using (var t = Image.FromStream(fs)) bmp = new Bitmap(t); } catch { }

                if (bmp == null && doc.mime_type == "image/webp")
                {
                    var full = await _service.DownloadDocBytesAsync(doc);
                    if (full != null) bmp = await System.Threading.Tasks.Task.Run(() => (Image)ImageDecoder.DecodeAny(full));   // webp decode OFF the UI thread
                    if (bmp != null)
                        try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
                }

                if (bmp == null)   // animated → representative emoji (cache owns it; don't store in _photoThumbCache)
                {
                    string alt = doc.attributes?.OfType<DocumentAttributeSticker>().FirstOrDefault()?.alt;
                    var em = !string.IsNullOrEmpty(alt) ? EmojiRenderer.Get(alt) : null;
                    if (em != null) { if (!bubble.IsDisposed) bubble.SetImage(em); return; }
                }

                if (bmp != null) { _photoThumbCache[doc.id] = bmp; if (!bubble.IsDisposed) bubble.SetImage(bmp); }
                else if (!bubble.IsDisposed) bubble.SetPlaceholder();
            }
            catch { if (!bubble.IsDisposed) bubble.SetPlaceholder(); }
        }

        // ── Inline video: static thumbnail, play looping video on CLICK (WebM stickers + GIFs) ──
        // Shared model for BOTH WebM video stickers AND GIFs (Telegram GIFs are muted MP4/H.264). A bubble
        // shows a STATIC THUMBNAIL by default and plays the looping video INLINE only when CLICKED — at most
        // ONE plays at a time (_playingInline), so the chat is never a wall of decoders. The playing one stops
        // on a second click, scroll-off, chat switch, background, or toggle-OFF; the WebmAnimator's dispose
        // fence makes every stop crash-safe. All the WebM lessons are inherited unchanged here, so GIFs don't
        // re-walk them: the shared app-lifetime LibVLC, the EndReached-restart loop (NOT :input-repeat), the
        // in-flight fence, the RectangleToScreen + 2s-grace visibility check, and the atomic temp→rename cache.
        private sealed class InlineVid
        {
            public Document Doc;          // to lazily download the full clip on first play
            public int W, H;
            public string CacheFile;      // per-document, account-scoped cache name ("webm_{id}.webm" / "gif_{id}.mp4")
            public string Path;           // resolved cached file path (set on first play)
            public Image Static;          // fallback when not playing if there's no cached thumb (e.g. a sticker's emoji)
            public Image Frame;           // current delivered video frame (owned by us — disposed on swap/stop)
            public UI.Controls.WebmAnimator Animator;
            public int PlayStartTick;     // Environment.TickCount when play started (grace window + double-tap guard)
        }

        private const int InlineVideoFps = 15;   // frame cap (~half of 30fps — smooth enough, gentler on ARM32)
        private readonly Dictionary<MessageBubbleControl, InlineVid> _inlineVid = new Dictionary<MessageBubbleControl, InlineVid>();
        private readonly List<MessageBubbleControl> _inlineBubbles = new List<MessageBubbleControl>();
        private MessageBubbleControl _playingInline;   // the single bubble currently playing (null = none)

        /// <summary>Registers a bubble for inline play-on-click (shared by WebM stickers + GIFs) and wires the tap.</summary>
        private void RegisterInline(MessageBubbleControl bubble, Document doc, int w, int h, string cacheFile, Image staticFallback)
        {
            _inlineVid[bubble] = new InlineVid { Doc = doc, W = w, H = h, CacheFile = cacheFile, Static = staticFallback };
            if (!_inlineBubbles.Contains(bubble)) _inlineBubbles.Add(bubble);
            bubble.IsInlineVideo = true;   // draws the "tap to play" hint on static WebM stickers (GIFs already have the video overlay)
            bubble.ImageClicked += (s, e) => PlayInline(bubble);   // tap the thumbnail → play / stop the video
        }

        /// <summary>WebM sticker: static thumbnail (Telegram thumb, bitmap-cached — no video decode) or the
        /// representative emoji; the looping video plays on click. Never auto-plays.</summary>
        private async System.Threading.Tasks.Task SetupWebmSticker(MessageBubbleControl bubble, Document doc)
        {
            Image thumb = null;
            if (_photoThumbCache.TryGetValue(doc.id, out var cachedThumb)) thumb = cachedThumb;
            else
            {
                var tb = await _service.DownloadThumbAsync(doc);   // small static preview — NOT the video
                if (tb != null)
                {
                    try { thumb = await System.Threading.Tasks.Task.Run(() => (Image)ImageDecoder.DecodeAny(tb)); } catch { }
                    if (thumb != null) _photoThumbCache[doc.id] = thumb;   // chat-scoped bitmap cache (freed on switch)
                }
            }
            if (bubble.IsDisposed) return;

            Image fallback = thumb;
            if (fallback == null)   // no thumb → representative emoji
            {
                string alt = doc.attributes?.OfType<DocumentAttributeSticker>().FirstOrDefault()?.alt;
                fallback = !string.IsNullOrEmpty(alt) ? EmojiRenderer.Get(alt) : null;
            }
            if (fallback != null) bubble.SetImage(fallback); else bubble.SetPlaceholder();

            int pw, ph; StickerDims(doc, out pw, out ph);
            RegisterInline(bubble, doc, pw, ph, "webm_" + doc.id + ".webm", fallback);
        }

        /// <summary>GIF (muted MP4): the bubble already shows its static thumbnail (ConfigureVideoThumb) — just
        /// register it to play the looping video inline on click (replaces the old open-in-MediaViewer).</summary>
        private void SetupGifInline(MessageBubbleControl bubble, Document doc, MediaItem mi)
        {
            int w = mi.Width > 0 ? mi.Width : 320;
            int h = mi.Height > 0 ? mi.Height : 200;
            const int cap = 512;   // decode/blit at ≤512 (aspect-preserved) — VLC scales to it; gentler on ARM32
            if (w > cap || h > cap)
            {
                double s = Math.Min((double)cap / w, (double)cap / h);
                w = Math.Max(1, (int)(w * s)); h = Math.Max(1, (int)(h * s));
            }
            RegisterInline(bubble, doc, w, h, "gif_" + doc.id + ".mp4", null);   // revert target = the cached thumb (looked up on stop)
            // A GIF with NO document thumbnail is shown as a Placeholder — whose click fires DownloadRequested,
            // NOT ImageClicked. Wire it to play too, so a thumbless GIF is playable in EVERY state (not a dead tile).
            bubble.DownloadRequested += (s, e) => PlayInline(bubble);
            System.Diagnostics.Debug.WriteLine("[GIF] setup id=" + doc.id + " mime=" + doc.mime_type + " thumbs=" + (doc.thumbs != null ? doc.thumbs.Length : 0) + " " + w + "x" + h);
        }

        /// <summary>Tap an inline-video bubble (WebM sticker or GIF): play its looping video (downloading the
        /// clip on first play, atomically). A second tap stops it; starting one stops any other (at most one
        /// decoder live). No-op when the toggle is OFF.</summary>
        private async void PlayInline(MessageBubbleControl bubble)
        {
            try
            {
                if (!AppSettings.Instance.AnimateWebmStickers) return;   // the "Animate WebM stickers" toggle gates inline video
                InlineVid st;
                if (bubble.IsDisposed || !_inlineVid.TryGetValue(bubble, out st)) return;
                if (st.Animator != null)
                {
                    // Already playing. A tap a moment after start is almost certainly an accidental double-fire
                    // (touch raising both a touch- and a mouse-click) — ignore it; a deliberate later tap stops.
                    if (unchecked(Environment.TickCount - st.PlayStartTick) < 600)
                    { System.Diagnostics.Debug.WriteLine("[WEBM] PlayInline: ignore double-tap (just started)"); return; }
                    System.Diagnostics.Debug.WriteLine("[WEBM] PlayInline: tap-to-stop");
                    StopInline(bubble, "tap-stop"); return;
                }
                if (_playingInline != null && _playingInline != bubble) StopInline(_playingInline, "switch");   // only one at a time

                if (st.Path == null)   // resolve the cached clip the first time it's played (per-document, account-scoped → hits across chats)
                {
                    string wp = MediaCache.ThumbPath(st.CacheFile);
                    if (TelegramService.IsFileComplete(wp, st.Doc != null ? st.Doc.size : 0))
                    {
                        System.Diagnostics.Debug.WriteLine("[WEBM] cache HIT -> play existing (no download) " + st.CacheFile);
                    }
                    else if (st.Doc != null && st.Doc.size >= 1024 * 1024)
                    {
                        // DOWNLOAD-UX v3 1.1: GIFs ≥1MB are MANAGED — pausable, panel row, survive chat
                        // switches, sidecar resume. The handle's own .part→rename IS the atomic contract
                        // the .tmp dance provided. Ring shows on the bubble (tap = pause / resume).
                        MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                        var gh = StartManagedDownload(st.Doc, "gif", wp, true, _selectedChat?.Peer, 0, st.CacheFile);
                        if (gh == null) return;
                        bubble.SetDownloadHandle(gh);
                        await AwaitHandle(gh);
                        if (gh.State != DownloadHandle.DState.Done) return;   // paused/cancelled/failed → the ring/glyph says so
                        if (bubble.IsDisposed || !_inlineVid.ContainsKey(bubble))
                        { System.Diagnostics.Debug.WriteLine("[GIF] done id=" + st.Doc.id + " (chat switched — no auto-start)"); return; }
                    }
                    else   // small GIF / webm sticker → lightweight TEMP+rename path (unchanged: no panel row)
                    {
                        string tmp = wp + ".tmp";
                        try
                        {
                            MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                            await _service.DownloadDocumentToFileAsync(st.Doc, tmp);   // streams (no whole-file buffering)
                            if (bubble.IsDisposed || !_inlineVid.ContainsKey(bubble)) { try { File.Delete(tmp); } catch { } return; }
                            if (!(File.Exists(tmp) && new FileInfo(tmp).Length > 0)) return;
                            if (File.Exists(wp)) File.Delete(wp);
                            File.Move(tmp, wp);   // atomic rename → wp only ever holds a COMPLETE file
                        }
                        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } return; }
                        System.Diagnostics.Debug.WriteLine("[WEBM] cache MISS -> downloaded " + DrawHelper.FormatSize(new FileInfo(wp).Length) + " " + st.CacheFile);
                    }
                    st.Path = wp;
                }
                if (bubble.IsDisposed || !_inlineVid.ContainsKey(bubble)) return;
                if (st.Animator != null) return;   // a second fire during the await already started it
                if (!VlcEnvironment.TryInitialize()) return;   // libVLC not ready → stay static
                st.PlayStartTick = Environment.TickCount;
                st.Animator = new UI.Controls.WebmAnimator(st.Path, st.W, st.H, InlineVideoFps, bubble, img => OnInlineFrame(bubble, img));
                _playingInline = bubble;
                bubble.Animating = true;                       // hide the play overlay / GIF badge while playing
                if (!bubble.IsDisposed) bubble.Invalidate();
                System.Diagnostics.Debug.WriteLine("[WEBM] PlayInline: started " + st.CacheFile);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[WEBM] play FAILED: " + ex.Message); }
        }

        /// <summary>Stops a playing inline video: dispose the animator (crash-safe fence), free the last frame,
        /// revert to the static thumbnail (the cached thumb, or the sticker emoji). Keeps the registration.</summary>
        private void StopInline(MessageBubbleControl b, string reason)
        {
            if (!_inlineVid.TryGetValue(b, out var st)) return;
            if (st.Animator != null)
            {
                System.Diagnostics.Debug.WriteLine("[WEBM] StopInline reason=" + reason);
                try { st.Animator.Dispose(); } catch { } st.Animator = null;   // fence: stop → drain → free
            }
            // Replace the bubble's image (which currently references the last video frame) with the static
            // thumbnail FIRST, THEN dispose the frame — so no queued paint ever draws a disposed bitmap.
            if (!b.IsDisposed)
            {
                b.Animating = false;   // restore the play overlay / GIF badge
                Image revert = (st.Doc != null && _photoThumbCache.TryGetValue(st.Doc.id, out var t)) ? t : st.Static;
                if (revert != null) b.SetImage(revert); else b.SetPlaceholder();   // never leave _image on the about-to-be-disposed frame
            }
            if (st.Frame != null) { var f = st.Frame; st.Frame = null; try { f.Dispose(); } catch { } }
            if (_playingInline == b) _playingInline = null;
        }

        /// <summary>UI-thread frame delivery from a WebmAnimator: swap in the fresh opaque frame, dispose the
        /// previous one (we own them — the bubble doesn't dispose _image). Orphan frames (stopped/disposed) freed.</summary>
        private void OnInlineFrame(MessageBubbleControl b, Image img)
        {
            InlineVid st;
            if (b.IsDisposed || !_inlineVid.TryGetValue(b, out st) || st.Animator == null) { try { img.Dispose(); } catch { } return; }
            var old = st.Frame; st.Frame = img; b.SetFrame(img); if (old != null) try { old.Dispose(); } catch { }
        }

        /// <summary>Stop the playing inline video if it scrolled out of view (saves the decoder when you scroll
        /// away). Called from the 200ms scroll-watch; only ever STOPS — playing is click-only, so no scroll
        /// churn. The 2s grace + RectangleToScreen check stop a just-clicked item being disposed ~1 frame in.</summary>
        private void CheckInlineVisibility()
        {
            var b = _playingInline;
            if (b == null) return;
            if (b.IsDisposed) { StopInline(b, "bubble-disposed"); return; }
            if (!_inlineVid.TryGetValue(b, out var st)) return;
            if (unchecked(Environment.TickCount - st.PlayStartTick) < 2000) return;   // grace: don't kill a just-clicked item
            // Coordinate-space-AGNOSTIC visibility: compare absolute screen rects (RectangleToScreen walks the
            // full parent chain + scroll), so it doesn't depend on the panel's AutoScroll/content-coord quirks.
            try
            {
                Rectangle view = _messagePanel.RectangleToScreen(_messagePanel.ClientRectangle);
                Rectangle bub = b.RectangleToScreen(b.ClientRectangle);
                view.Inflate(48, 48);   // small margin — keep an item just off the edge playing
                if (!view.IntersectsWith(bub))
                {
                    System.Diagnostics.Debug.WriteLine("[WEBM] off-screen → stop (view=" + view + " bubble=" + bub + ")");
                    StopInline(b, "scrolled-off");
                }
            }
            catch { }
        }

        /// <summary>Full teardown for a chat switch: dispose the playing animator (fence) + frame, forget the
        /// bubbles. Must run BEFORE the bubbles are disposed so no late frame touches them.</summary>
        private void StopAllInline()
        {
            foreach (var b in _inlineBubbles.ToArray()) StopInline(b, "chat-clear");
            _inlineVid.Clear();
            _inlineBubbles.Clear();
            _playingInline = null;
        }

        /// <summary>Pause-on-background / resume-on-foreground (Telegram-like): pauses ALL chat + panel animation
        /// (rlottie's shared ticker + the playing inline video — a WebM sticker OR a GIF) when the app loses
        /// foreground, resumes on regain.</summary>
        private void SetAnimationPaused(bool paused)
        {
            try { TelegArm.Helpers.LottieAnimator.SetPaused(paused); } catch { }
            if (_playingInline != null && _inlineVid.TryGetValue(_playingInline, out var st) && st.Animator != null)
                st.Animator.SetPaused(paused);
            System.Diagnostics.Debug.WriteLine("[WEBM] app " + (paused ? "BACKGROUND -> pause animation" : "FOREGROUND -> resume animation"));
        }

        private const int WM_ACTIVATEAPP = 0x001C;   // sent when the APPLICATION (not just a form) gains/loses foreground
        private bool _appActive = true;
        private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;   // OS accent/colorization changed (fires on 8.1 AND 10/11)

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_ACTIVATEAPP)
            {
                bool active = m.WParam != IntPtr.Zero;   // wParam != 0 → app activated; 0 → app backgrounded
                if (!active && IsSpuriousDeactivation())
                {
                    // FIX 1: the WinRT/touch keyboard is a SEPARATE surface that steals process activation while
                    // we're still the real foreground app + a text field is focused → this is NOT a background.
                    // Do NOT pause the WebM animation / churn — that pause+resume on every steal was the flicker.
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] WM_ACTIVATEAPP active=False → IGNORED (keyboard overlay, still foreground)");
                }
                else
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] WM_ACTIVATEAPP active=" + active);
                    if (active != _appActive)
                    {
                        _appActive = active;
                        SetAnimationPaused(!active);   // real fg/bg change → pause/resume as before
                        // PRESENCE-TUNE A.1/A.2: focus-aware — activation sends online NOW (≤25s throttle);
                        // deactivation arms the 5s offline grace (alt-tab flap protection).
                        if (active) NoteActivity();
                        else _bgSinceTick = Environment.TickCount;
                    }
                }
            }
            else if (m.Msg == 0x0006)   // WM_ACTIVATE — 0=inactive 1=active(non-click) 2=clickactive
            {
                long wa = m.WParam.ToInt64() & 0xFFFF;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] WM_ACTIVATE " + wa);
                // WA_ACTIVE (1, NOT a mouse click): reactivated by the keyboard-close returning activation / alt-tab /
                // startup → WinForms auto-restores focus to the last text field, and Windows then auto-RESHOWS the
                // touch keyboard for it (the close→reshow loop). Clear that auto-focus so ONLY a user TAP (which
                // comes through WA_CLICKACTIVE / a direct hit, never WA_ACTIVE) opens the keyboard.
                if (wa == 1) try { BeginInvoke((Action)ClearAutoRefocusedTextField); } catch { }
            }
            else if (m.Msg == 0x0086) { if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] WM_NCACTIVATE active=" + (m.WParam != IntPtr.Zero)); }
            else if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED)
            {
                // Windows accent / colorization changed (fires on 8.1 AND 10/11). Re-read the CHOSEN accent from
                // the registry via ThemeHelper (NOT m.WParam — that carries the composed colorization color, not the
                // picked swatch) and recolor every themed surface live, debounced. No restart.
                ThemeHelper.NotifyAccentChanged();
            }
            base.WndProc(ref m);
        }

        /// <summary>Builds a bubble for a message, or a centered system line for a MessageService event.</summary>
        private Control MakeMessageBubble(ChatEntry entry, MessageBase mb, string sender)
        {
            if (mb is MessageService svc) return MakeServiceLine(svc);   // centered system line (no bubble)
            var m = mb as Message;
            if (m == null) return null;

            var photo = (m.media as MessageMediaPhoto)?.photo as Photo;
            var doc = (m.media as MessageMediaDocument)?.document as Document;

            if (m.media is MessageMediaPoll mmp)
            {
                var pollBubble = CreateBubble("", sender, IsOut(m), m.date, m.ID);
                ApplyPoll(pollBubble, mmp);
                SetReply(pollBubble, m);
                return pollBubble;
            }

            if (photo != null)
            {
                var bubble = CreateBubble(m.message ?? "", sender, IsOut(m), m.date, m.ID);
                ConfigurePhoto(bubble, photo);
                bubble.ImageClicked += (s, e) => OpenMediaViewer(bubble.MessageId);
                // "tap to download" → fetch the full photo inline (and show it), not open an empty viewer.
                bubble.DownloadRequested += (s, e) => { bubble.SetLoading(); StartPhotoLoad(bubble, photo, true); };
                SetReply(bubble, m);
                return bubble;
            }

            if (doc != null)
            {
                // Stickers → render the image itself (no bubble), like Telegram.
                if (doc.attributes != null && doc.attributes.Any(a => a is DocumentAttributeSticker))
                {
                    var imgAttr = doc.attributes.OfType<DocumentAttributeImageSize>().FirstOrDefault();
                    int sw = imgAttr?.w ?? 160, sh = imgAttr?.h ?? 160;
                    var sb = CreateBubble("", sender, IsOut(m), m.date, m.ID);
                    sb.IsSticker = true;
                    sb.ConfigurePhoto(sw, sh, MessageBubbleControl.PhotoState.Loading);
                    StartStickerLoad(sb, doc);
                    SetReply(sb, m);
                    return sb;
                }

                var mi = MediaClassifier.FromMessage(m);
                MaybeAutoFetchDocument(doc, mi);   // policy-gated background full download (default OFF → no-op; render stays thumb-only)
                bool isVideoOrGif = mi != null && (mi.Type == "video" || mi.Type == "gif");
                if (isVideoOrGif)
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[GIF] media id=" + doc.id + " type=" + mi.Type + " mime=" + doc.mime_type
                        + " anim=" + (doc.attributes != null && doc.attributes.OfType<DocumentAttributeAnimated>().Any())
                        + " thumbs=" + (doc.thumbs != null ? doc.thumbs.Length : 0));
                    var bubble = CreateBubble(m.message ?? "", sender, IsOut(m), m.date, m.ID);
                    if (mi.IsRound) ConfigureRoundVideo(bubble, doc, mi);   // round "video note" → circle
                    else ConfigureVideoThumb(bubble, doc, mi);
                    if (mi.Type == "gif" && !mi.IsRound)
                        SetupGifInline(bubble, doc, mi);   // GIF → play the looping video INLINE on click (no MediaViewer)
                    else
                        bubble.ImageClicked += (s, e) => OpenMediaViewer(bubble.MessageId);   // video / round note → full-screen viewer
                    SetReply(bubble, m);
                    return bubble;
                }
                if (mi != null && mi.Type == "document")
                {
                    var fb = CreateBubble("", sender, IsOut(m), m.date, m.ID);
                    fb.IsFile = true;
                    fb.FileName = mi.FileName;
                    fb.FileSizeText = DrawHelper.FormatSize(mi.FileSize);
                    fb.Measure();
                    fb.ClickableMedia = true;
                    // In-bubble pausable download (ring + MB/MB) to media/; tap opens once cached.
                    string docPath = MediaCache.MediaPath(MediaCache.CacheFileName("document", doc.id, mi.FileName));
                    var refPeer = _selectedChat?.Peer; int refMsgId = m.ID;   // FILE_REFERENCE refresh context
                    fb.ConfigureFileDownload(
                        () => StartManagedDownload(doc, "doc", docPath, true, refPeer, refMsgId, mi.FileName),
                        TelegramService.IsFileComplete(docPath, doc.size));
                    // DOWNLOAD-UX 2.2: a transfer started before a chat switch may still be running — REBIND
                    // the ring to the live handle instead of showing idle.
                    var liveDoc = _service.GetDownload(doc.id);
                    if (liveDoc != null)
                    {
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] rebind id=" + doc.id + " state=inflight");
                        fb.SetDownloadHandle(liveDoc);
                    }
                    fb.ImageClicked += (s, e) => OpenMediaViewer(fb.MessageId);
                    SetReply(fb, m);
                    return fb;
                }

                if (mi != null && (mi.Type == "voice" || mi.Type == "audio"))
                    return MakeVoiceBubble(m, doc, mi);

                // any other document → text bubble, clickable to open.
                var b = CreateBubble(GetDisplayText(m), sender, IsOut(m), m.date, m.ID);
                b.ClickableMedia = true;
                b.ImageClicked += (s, e) => OpenMediaViewer(b.MessageId);
                SetReply(b, m);
                return b;
            }

            // Text message carrying a web-page link preview → text bubble + a preview card below it.
            if (m.media is MessageMediaWebPage mw && mw.webpage is WebPage wp)
            {
                var lb = CreateBubble(m.message ?? "", sender, IsOut(m), m.date, m.ID);
                ConfigureLinkPreview(lb, wp);
                SetReply(lb, m);
                return lb;
            }

            var tb = CreateBubble(GetDisplayText(m), sender, IsOut(m), m.date, m.ID);
            SetReply(tb, m);
            return tb;
        }

        /// <summary>Centered system line for a service event, or null when the action is skipped.</summary>
        private Control MakeServiceLine(MessageService svc)
        {
            string text = ServiceText(svc);
            if (string.IsNullOrEmpty(text)) return null;
            return new ServiceLineControl(text) { IsDark = _dark, Width = ContentWidth(_messagePanel) };
        }

        /// <summary>True when the peer is a BROADCAST channel (Channel.broadcast); megagroups are Channel
        /// too but carry GROUP semantics — presentation branches on this, never on Channel-vs-Chat alone.
        /// The dialog-fed ChatEntry.PeerInfo is checked FIRST: it's authoritative and always full, while
        /// the update-manager dict can hold a min entity (or none) for small channels and miss the flag.</summary>
        private bool IsBroadcastPeer(Peer p)
        {
            if (!(p is PeerChannel)) return false;
            var e = _selectedChat != null && _selectedChat.PeerId == p.ID
                ? _selectedChat
                : _allChats.FirstOrDefault(c => c.PeerId == p.ID);
            if (e?.PeerInfo is Channel ec) return (ec.flags & Channel.Flags.broadcast) != 0;
            return ResolvePeer(p) is Channel ch && (ch.flags & Channel.Flags.broadcast) != 0;
        }

        /// <summary>Actor display name, or NULL when unresolvable — service lines word themselves
        /// actorless instead of inventing "Someone" (NameOf keeps that fallback for other callers).</summary>
        private string ActorOf(Peer p)
        {
            if (p == null) return null;
            if (_peerNames.TryGetValue(p.ID, out var n) && !string.IsNullOrEmpty(n)) return n;
            var info = ResolvePeer(p);
            if (info is User u) return DisplayName(u);
            if (info is ChatBase cb) return cb.Title;
            return null;
        }

        /// <summary>Maps a MessageService action to human-readable text (null = skip). PEER-PRESENTATION:
        /// wording follows the PEER TYPE — broadcast channels get ACTORLESS channel strings (a broadcast
        /// has no visible actor; the old group wording produced "Someone changed the group name" inside
        /// channels); megagroups + basic groups resolve the real actor and render actorless when the
        /// actor can't be resolved — an invented "Someone" never appears anywhere anymore.</summary>
        private string ServiceText(MessageService svc)
        {
            if (svc?.action == null) return null;
            bool bc = IsBroadcastPeer(svc.peer_id);
            string who = bc ? null : ActorOf(svc.from_id);
            long actor = (svc.from_id as PeerUser)?.user_id ?? 0;
            switch (svc.action)
            {
                case MessageActionChatAddUser au:
                    if (bc) return "Channel updated";
                    string added = au.users != null && au.users.Length > 0 && !au.users.Contains(actor)
                        ? string.Join(", ", au.users.Select(NameOfUserId)) : null;
                    if (added != null) return who != null ? who + " added " + added : added + " joined the group";
                    return who != null ? who + " joined the group" : "New members joined the group";
                case MessageActionChatJoinedByLink _:
                    return bc ? "Channel updated"
                        : who != null ? who + " joined via invite link" : "A member joined via invite link";
                case MessageActionChatDeleteUser du:
                    if (bc) return "Channel updated";
                    if (du.user_id == actor) return who != null ? who + " left the group" : "A member left the group";
                    return who != null ? who + " removed " + NameOfUserId(du.user_id) : NameOfUserId(du.user_id) + " was removed";
                case MessageActionChatCreate _:
                    return bc ? "Channel created" : who != null ? who + " created the group" : "Group created";
                case MessageActionChannelCreate _:
                    return bc ? "Channel created" : "Group created";   // megagroups emit ChannelCreate too
                case MessageActionChatEditTitle et:
                    string t = string.IsNullOrEmpty(et.title) ? null : "«" + et.title + "»";
                    if (bc) return t != null ? "Channel name changed to " + t : "Channel name changed";
                    if (who != null) return t != null ? who + " changed the group name to " + t : who + " changed the group name";
                    return t != null ? "Group name changed to " + t : "Group name changed";
                case MessageActionChatEditPhoto _:
                    return bc ? "Channel photo updated"
                        : who != null ? who + " changed the group photo" : "Group photo updated";
                case MessageActionChatDeletePhoto _:
                    return bc ? "Channel photo removed"
                        : who != null ? who + " removed the group photo" : "Group photo removed";
                case MessageActionPinMessage _:
                    return bc ? "A message was pinned"
                        : who != null ? who + " pinned a message" : "A message was pinned";
                case MessageActionContactSignUp _:
                    return who != null ? who + " joined Telegram" : "Joined Telegram";
                case MessageActionSetMessagesTTL ttl:
                    return ttl.period > 0 ? "Auto-delete timer set to " + FormatTtl(ttl.period) : "Auto-delete disabled";
                case MessageActionCustomAction ca: return string.IsNullOrEmpty(ca.message) ? null : ca.message;
                case MessageActionHistoryClear _: return null;   // deliberately hidden
                default:
                    // 1.3: unhandled action types render a peer-correct generic instead of vanishing.
                    return bc ? "Channel updated" : svc.peer_id is PeerUser ? null : "Group updated";
            }
        }

        private string NameOfUserId(long id)
        {
            if (_peerNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)) return n;
            return ResolvePeer(new PeerUser { user_id = id }) is User u ? DisplayName(u) : "a user";   // manager fallback
        }

        private static string FormatTtl(int seconds)
        {
            if (seconds >= 86400) { int d = seconds / 86400; return d == 7 ? "1 week" : d + (d == 1 ? " day" : " days"); }
            if (seconds >= 3600) { int h = seconds / 3600; return h + (h == 1 ? " hour" : " hours"); }
            int mi = Math.Max(1, seconds / 60); return mi + (mi == 1 ? " minute" : " minutes");
        }

        /// <summary>Caches the names referenced by a service event so the line resolves them (not "Someone").</summary>
        private void CacheServiceNames(Func<Peer, IPeerInfo> resolve, MessageService svc)
        {
            void Cache(long id) { if (id != 0 && resolve(new PeerUser { user_id = id }) is User u) _peerNames[u.id] = DisplayName(u); }
            if (svc.from_id is PeerUser pu) Cache(pu.user_id);
            if (svc.action is MessageActionChatAddUser au && au.users != null) foreach (var id in au.users) Cache(id);
            if (svc.action is MessageActionChatDeleteUser du) Cache(du.user_id);
        }

        /// <summary>Attaches a web-page preview card to a text bubble and lazily loads its thumbnail.</summary>
        private void ConfigureLinkPreview(MessageBubbleControl bubble, WebPage wp)
        {
            var photo = wp.photo as Photo;
            var doc = wp.document as Document;
            string url = !string.IsNullOrEmpty(wp.url) ? wp.url : wp.display_url;
            bubble.SetLinkPreview(wp.site_name, wp.title, wp.description, url, photo != null || doc != null);
            if (photo != null) StartCardThumbLoad(bubble, photo);
            else if (doc != null) StartCardDocThumbLoad(bubble, doc);
        }

        private async void StartCardThumbLoad(MessageBubbleControl bubble, Photo photo)
        {
            try
            {
                if (_photoCache.TryGetValue(photo.id, out var full)) { bubble.SetCardThumb(full); return; }
                if (_photoThumbCache.TryGetValue(photo.id, out var cached)) { bubble.SetCardThumb(cached); return; }
                var tb = await _service.DownloadPhotoThumbAsync(photo);
                if (tb != null && tb.Length > 0)
                {
                    var img = await ToBitmapAsync(tb);
                    _photoThumbCache[photo.id] = img;
                    if (!bubble.IsDisposed) bubble.SetCardThumb(img);
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:StartCardThumbLoad", ex); }
        }

        private async void StartCardDocThumbLoad(MessageBubbleControl bubble, Document doc)
        {
            try
            {
                if (_photoThumbCache.TryGetValue(doc.id, out var cached)) { bubble.SetCardThumb(cached); return; }
                var tb = await _service.DownloadThumbAsync(doc);
                if (tb != null && tb.Length > 0)
                {
                    var img = await ToBitmapAsync(tb);
                    _photoThumbCache[doc.id] = img;
                    if (!bubble.IsDisposed) bubble.SetCardThumb(img);
                }
            }
            catch (Exception ex) { CrashLog.RecordThrottled("async-void:StartCardDocThumbLoad", ex); }
        }

        /// <summary>Builds an inline voice/audio player bubble (downloads to cache on play).</summary>
        private Control MakeVoiceBubble(Message m, Document doc, MediaItem mi)
        {
            bool isAudio = mi.Type == "audio";
            long id = doc.id;
            string path = AudioCachePath(doc, isAudio, mi.FileName);
            string cached = TelegramService.IsFileComplete(path, doc.size) ? path : null;

            var vb = new VoiceBubbleControl(id, mi.Duration, isAudio, isAudio ? mi.FileName : null,
                isAudio ? DrawHelper.FormatSize(mi.FileSize) : null, IsOut(m), cached, () => StartAudioDownload(id))
            {
                AccentColor = _accent,
                IsDark = _dark,
                Width = ContentWidth(_messagePanel),
                MessageId = m.ID
            };
            vb.ContextMenuRequested += OnVoiceContextMenu;
            vb.SelectionMode = _selectionMode;
            vb.SelectionToggled += OnVoiceSelectionToggled;
            StartAudioCoverLoad(vb, doc);   // show embedded cover art in the circle (if any); else plain circle
            return vb;
        }

        /// <summary>Loads a standalone audio track's embedded cover art into the bubble's circular button — the
        /// SAME thumbs/ path + cache the album rows use. No thumb (voice / art-less) → plain circle fallback.</summary>
        private async void StartAudioCoverLoad(VoiceBubbleControl vb, Document doc)
        {
            try
            {
                if (doc.thumbs == null || doc.thumbs.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[AUDIO] cover: no thumb id=" + doc.id + " → plain circle");
                    return;   // voice message / music without embedded art
                }
                Image cachedImg;
                if (_photoThumbCache.TryGetValue(doc.id, out cachedImg)) { if (!vb.IsDisposed) vb.SetCover(cachedImg); return; }
                var tb = await _service.DownloadThumbAsync(doc);   // → thumbs/ on disk (cached), decoded off-thread
                if (tb != null && tb.Length > 0)
                {
                    var img = await ToBitmapAsync(tb);
                    _photoThumbCache[doc.id] = img;   // chat-scoped; freed by ClearMessagePanel (no per-bubble dispose)
                    System.Diagnostics.Debug.WriteLine("[AUDIO] cover: loaded id=" + doc.id);
                    if (!vb.IsDisposed) vb.SetCover(img);
                }
            }
            catch { /* cover is best-effort → plain circle */ }
        }

        /// <summary>Starts (or returns the in-flight) cancellable download of a voice/audio doc → its media/ path.</summary>
        private DownloadHandle StartAudioDownload(long docId)
        {
            foreach (var msg in _currentChatMessages)
            {
                var d = (msg.media as MessageMediaDocument)?.document as Document;
                if (d == null || d.id != docId) continue;
                var cmi = MediaClassifier.FromMessage(msg);
                bool isAudio = cmi != null && cmi.Type == "audio";
                if (isAudio)
                    return StartManagedDownload(d, "audio", AudioCachePath(d, true, cmi?.FileName), true,
                                                _selectedChat?.Peer, msg.ID, AudioRowTitle(d, cmi, true));
                var refPeer = _selectedChat?.Peer; int refMsg = msg.ID;   // FILE_REFERENCE coverage (DOWNLOAD-UX)
                return _service.StartDocumentDownload(d, AudioCachePath(d, false, cmi?.FileName),
                    () => _service.RefetchDocumentAsync(refPeer, refMsg),
                    null, _selectedChat?.Title,
                    track: false, type: "voice");   // voice notes are small/instant → not manager-tracked, not pausable
            }
            return null;
        }

        /// <summary>Audio cache extension from the document's MIME type (deterministic — the same doc always
        /// resolves to ONE path regardless of call site); falls back to the filename ext, then .bin.</summary>
        private static string AudioExtFromMime(string mime, string fileName)
        {
            switch ((mime ?? "").ToLowerInvariant())
            {
                case "audio/mpeg": case "audio/mp3": return ".mp3";
                case "audio/ogg": case "application/ogg": case "audio/opus": return ".ogg";
                case "audio/mp4": case "audio/m4a": case "audio/x-m4a": case "audio/aac": return ".m4a";
                case "audio/flac": case "audio/x-flac": return ".flac";
                case "audio/wav": case "audio/x-wav": return ".wav";
            }
            string ext = Path.GetExtension(fileName ?? "");
            return string.IsNullOrEmpty(ext) ? ".bin" : ext;
        }

        private static string AudioCachePath(Document doc, bool isAudio, string fileName)
        {
            long docId = doc != null ? doc.id : 0;
            string ext = isAudio ? AudioExtFromMime(doc?.mime_type, fileName) : ".ogg";   // voice notes stay opus-in-ogg
            string newPath = MediaCache.MediaPath((isAudio ? "audio_" : "voice_") + docId + ext);   // full media → media/
            // ONE-TIME MIGRATION: the previous scheme derived the ext from the FILENAME (default .mp3), so a doc
            // whose filename ext differs from its mime ext may sit cached under the legacy name. If a complete
            // legacy file exists and the new path doesn't, move it — no re-download.
            if (isAudio && doc != null)
            {
                string legacyExt = Path.GetExtension(fileName ?? "");
                if (string.IsNullOrEmpty(legacyExt)) legacyExt = ".mp3";
                if (!string.Equals(legacyExt, ext, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string legacy = MediaCache.MediaPath("audio_" + docId + legacyExt);
                        if (!TelegramService.IsFileComplete(newPath, doc.size) && TelegramService.IsFileComplete(legacy, doc.size))
                        {
                            if (File.Exists(newPath)) File.Delete(newPath);   // stale partial at the new name
                            File.Move(legacy, newPath);
                            System.Diagnostics.Debug.WriteLine("[DOWNLOAD] migrated legacy audio id=" + docId + " " + legacyExt + "→" + ext);
                        }
                    }
                    catch { /* best-effort; a failed move just re-downloads */ }
                }
            }
            return newPath;
        }

        // ── Universal Save + Reveal in folder (every media type) ─────────────
        private readonly Dictionary<long, string> _lastSavedPaths = new Dictionary<long, string>();   // media id → saved file (for Reveal)

        /// <summary>The volatile media/ cache path for a message's full media (NO download). Null if not media.</summary>
        private string MediaFullPath(Message msg, MediaItem mi)
        {
            if (mi == null) return null;
            if (mi.Type == "photo")
            {
                var photo = (msg.media as MessageMediaPhoto)?.photo as Photo;
                if (photo == null) return null;
                if (_photoCachePaths.TryGetValue(photo.id, out var p)) return p;
                return MediaCache.MediaPath("photo_" + photo.id + ".jpg");
            }
            var doc = (msg.media as MessageMediaDocument)?.document as Document;
            if (doc == null) return null;
            if (mi.Type == "voice" || mi.Type == "audio") return AudioCachePath(doc, mi.Type == "audio", mi.FileName);
            return MediaCache.MediaPath(MediaCache.CacheFileName(mi.Type, doc.id, mi.FileName));
        }

        /// <summary>Ensures the FULL media is in media/ (downloading via the shared handle if needed); returns its path.</summary>
        private async System.Threading.Tasks.Task<string> EnsureFullMediaAsync(Message msg, MediaItem mi)
        {
            if (mi.Type == "photo")
            {
                var photo = (msg.media as MessageMediaPhoto)?.photo as Photo;
                if (photo == null) return null;
                string pp = MediaFullPath(msg, mi);
                if (!string.IsNullOrEmpty(pp) && File.Exists(pp)) return pp;
                var result = await _service.DownloadPhotoAsync(photo);
                if (!string.IsNullOrEmpty(result.cachePath)) _photoCachePaths[photo.id] = result.cachePath;
                return result.cachePath;
            }
            var doc = (msg.media as MessageMediaDocument)?.document as Document;
            if (doc == null) return null;
            string path = MediaFullPath(msg, mi);
            if (TelegramService.IsFileComplete(path, doc.size)) { System.Diagnostics.Debug.WriteLine("[SAVE] copied-from-cache id=" + doc.id); return path; }
            var h = StartManagedDownload(doc, mi.Type, path, true, _selectedChat?.Peer, msg.ID, mi.FileName);   // dedups with an in-flight bubble download (shows its ring)
            if (h == null) return null;
            System.Diagnostics.Debug.WriteLine("[SAVE] downloaded-then-saved id=" + doc.id);
            await AwaitHandle(h);
            return TelegramService.IsFileComplete(path, doc.size) ? path : null;
        }

        private DownloadIndicator _dlIndicator;

        /// <summary>DOWNLOAD-UX v3 1.3: THE routing choke point for managed-class media (video / gif / audio /
        /// document). Bubble code AND MediaPolicy auto-downloads both call this — no scattered per-callsite
        /// policy. User-initiated → always panel-visible; policy-driven → visible only ≥5MB (tracked either
        /// way, so they survive switches and dedup). Photos are NOT routed here: they ride a byte[] pipeline
        /// with no Document/file-location, so the handle machinery doesn't apply (deviation, reported).</summary>
        private DownloadHandle StartManagedDownload(Document doc, string type, string path, bool userInitiated,
                                                    InputPeer refPeer, int refMsgId, string title)
        {
            const long PanelVisibleAutoMin = 5L * 1024 * 1024;
            bool visible = userInitiated || (doc != null && doc.size >= PanelVisibleAutoMin);
            Func<System.Threading.Tasks.Task<Document>> refresh = refPeer != null && refMsgId > 0
                ? () => _service.RefetchDocumentAsync(refPeer, refMsgId)
                : (Func<System.Threading.Tasks.Task<Document>>)null;
            return _service.StartDocumentDownload(doc, path, refresh, title, _selectedChat?.Title,
                                                  track: true, panelVisible: visible, type: type);
        }

        /// <summary>Opens the downloads-manager popup under the header indicator (DOWNLOAD-UX Part 4).</summary>
        private void OpenDownloadsPanel()
        {
            var p = new DownloadsPanel(_service, _dark, _accent);
            var anchor = _dlIndicator != null
                ? _dlIndicator.PointToScreen(new Point(_dlIndicator.Width - p.Width, _dlIndicator.Height + 2))
                : PointToScreen(new Point(ClientSize.Width - p.Width - 8, 64));
            var wa = Screen.FromControl(this).WorkingArea;
            p.Location = new Point(Math.Max(wa.Left, Math.Min(anchor.X, wa.Right - p.Width)),
                                   Math.Max(wa.Top, Math.Min(anchor.Y, wa.Bottom - p.Height)));
            p.Show(this);   // borderless popup; closes on Deactivate/Esc (rows unsubscribe on close)
        }

        /// <summary>Bridges a DownloadHandle's completion to an awaitable (resumes on the UI thread).</summary>
        private static System.Threading.Tasks.Task AwaitHandle(DownloadHandle h)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            Action<DownloadHandle> handler = null;
            handler = hh => { if (hh.State != DownloadHandle.DState.Downloading) { hh.Changed -= handler; tcs.TrySetResult(true); } };
            h.Changed += handler;
            if (h.State != DownloadHandle.DState.Downloading) { h.Changed -= handler; tcs.TrySetResult(true); }   // already finished
            return tcs.Task;
        }

        /// <summary>Copies the full media to the PERSISTENT save folder (cache is volatile) with a de-duped name.</summary>
        private async System.Threading.Tasks.Task SaveMediaAsync(Message msg)
        {
            var mi = MediaClassifier.FromMessage(msg);
            if (mi == null) return;
            System.Diagnostics.Debug.WriteLine("[SAVE] save requested type=" + mi.Type + " id=" + mi.Id);
            string full = await EnsureFullMediaAsync(msg, mi);
            if (string.IsNullOrEmpty(full) || !File.Exists(full)) { ThemedDialog.Show(this, "Save", "Couldn't download this item — try again.", "OK"); return; }
            try
            {
                string folder = MediaCache.EnsureFolder(AppSettings.Instance.DefaultSaveFolder);
                string target = DedupPath(folder, SaveFileName(mi, full));
                File.Copy(full, target, false);   // COPY (the cache keeps its copy); never overwrite (de-duped)
                _lastSavedPaths[mi.Id] = target;
                System.Diagnostics.Debug.WriteLine("[SAVE] saved to " + target);
                ThemedDialog.Show(this, "Saved", "Saved to:\n" + target, "OK");
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Save", "Save failed: " + ex.Message, "OK"); }
        }

        private static string SaveFileName(MediaItem mi, string fullPath)
        {
            string name = string.IsNullOrEmpty(mi.FileName) ? (mi.Type + "_" + mi.Id) : mi.FileName;
            if (string.IsNullOrEmpty(Path.GetExtension(name)))
            {
                string ext = Path.GetExtension(fullPath);
                if (string.IsNullOrEmpty(ext)) ext = ExtForType(mi.Type);
                name += ext;
            }
            return MediaSaver.SafeName(name);
        }

        private static string ExtForType(string type)
        {
            if (type == "photo") return ".jpg";
            if (type == "video" || type == "gif") return ".mp4";
            if (type == "voice") return ".ogg";
            if (type == "audio") return ".mp3";
            return "";
        }

        private static string DedupPath(string folder, string name)
        {
            string target = Path.Combine(folder, name);
            if (!File.Exists(target)) return target;
            string baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 1; i < 1000; i++)
            {
                string t = Path.Combine(folder, baseName + " (" + i + ")" + ext);
                if (!File.Exists(t)) return t;
            }
            return target;
        }

        /// <summary>Opens the containing folder and SELECTS the real file (saved copy preferred, else media/).
        /// Reveal acts only on a file that actually exists — not a thumbnail; Save is the action for downloading.</summary>
        private void RevealMedia(Message msg)
        {
            var mi = MediaClassifier.FromMessage(msg);
            if (mi == null) return;
            string path = null;
            if (_lastSavedPaths.TryGetValue(mi.Id, out var saved) && File.Exists(saved)) path = saved;
            if (path == null)
            {
                string cached = MediaFullPath(msg, mi);
                if (!string.IsNullOrEmpty(cached) && File.Exists(cached)) path = cached;
            }
            if (path == null) { ThemedDialog.Show(this, "Reveal in folder", "Save or download this item first.", "OK"); return; }
            Reveal(path);
        }

        private static void Reveal(string path)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SAVE] reveal " + path);
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");   // open folder + select file
            }
            catch
            {
                try   // RT/Explorer fallback: just open the containing folder
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
                    System.Diagnostics.Debug.WriteLine("[SAVE] reveal fallback open-folder");
                }
                catch { }
            }
        }

        /// <summary>Auto-download a document's FULL media to media/ ONLY if the policy opts that type in
        /// (default OFF → no-op, so render stays thumbnail-only). The in-bubble progress ring is MEDIA-2;
        /// here the toggle simply makes the file get cached in the background. Manual tap always works.</summary>
        private void MaybeAutoFetchDocument(Document doc, MediaItem mi)
        {
            if (doc == null || mi == null) return;
            if (mi.Type != "video" && mi.Type != "gif" && mi.Type != "voice" && mi.Type != "audio" && mi.Type != "document") return;
            if (!MediaPolicy.ShouldAutoDownload(mi.Type, mi.FileSize)) return;
            var _ = AutoFetchDocumentAsync(doc, mi);
        }

        private async System.Threading.Tasks.Task AutoFetchDocumentAsync(Document doc, MediaItem mi)
        {
            try
            {
                string path = (mi.Type == "voice" || mi.Type == "audio")
                    ? AudioCachePath(doc, mi.Type == "audio", mi.FileName)
                    : MediaCache.MediaPath(MediaCache.CacheFileName(mi.Type, doc.id, mi.FileName));
                if (TelegramService.IsFileComplete(path, doc.size)) return;   // already cached (size-verified)
                System.Diagnostics.Debug.WriteLine("[MEDIA] auto-download full " + mi.Type + " id=" + doc.id);
                if (mi.Type == "voice")
                {
                    await _service.DownloadDocumentToFileAsync(doc, path);   // voice stays lightweight (not managed)
                    return;
                }
                // DOWNLOAD-UX v3 1.2: managed-class auto-downloads run through handles (survive switches,
                // dedup with user taps) but stay OFF the panel unless ≥5MB (the choke point decides).
                var h = StartManagedDownload(doc, mi.Type, path, userInitiated: false, _selectedChat?.Peer, 0, mi.FileName);
                if (h != null) await AwaitHandle(h);
            }
            catch { /* best-effort; manual tap still downloads */ }
        }

        /// <summary>Downloads (if needed) the voice/audio for a document id and returns its cache path.</summary>
        private async System.Threading.Tasks.Task<string> ResolveAudioPath(long docId)
        {
            foreach (var msg in _currentChatMessages)
            {
                var d = (msg.media as MessageMediaDocument)?.document as Document;
                if (d == null || d.id != docId) continue;
                var cmi = MediaClassifier.FromMessage(msg);
                bool isAudio = cmi != null && cmi.Type == "audio";
                MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                string path = AudioCachePath(d, isAudio, cmi?.FileName);
                if (!TelegramService.IsFileComplete(path, d.size))
                    await _service.DownloadDocumentToFileAsync(d, path);
                return path;
            }
            return null;
        }

        /// <summary>Builds the per-chat audio playlist (voice + audio, in order) for the mini player.</summary>
        private void UpdateAudioPlaylist()
        {
            var list = new System.Collections.Generic.List<(long, string)>();
            foreach (var msg in _currentChatMessages)
            {
                var d = (msg.media as MessageMediaDocument)?.document as Document;
                if (d == null) continue;
                var cmi = MediaClassifier.FromMessage(msg);
                if (cmi == null || (cmi.Type != "voice" && cmi.Type != "audio")) continue;
                string title = cmi.Type == "audio" ? (cmi.FileName ?? "Audio") : "Voice message";
                list.Add((d.id, title));
            }
            AudioPlayer.SetPlaylist(list, ResolveAudioPath);
        }

        /// <summary>Configures a bubble to show a video/gif thumbnail with play/duration/GIF overlays.</summary>
        /// <summary>Round video note → a fixed-size CIRCLE (square thumbnail masked by the bubble); tap opens the
        /// viewer which clips playback to a circle too. Reuses the thumbnail-download + play-overlay plumbing.</summary>
        private void ConfigureRoundVideo(MessageBubbleControl bubble, Document doc, MediaItem mi)
        {
            const int side = 200;
            bubble.IsRoundVideo = true;
            bubble.IsVideoThumb = true;     // reuse play-overlay + tap-to-open
            bubble.DurationText = mi.Duration > 0 ? FormatDuration(mi.Duration) : null;
            System.Diagnostics.Debug.WriteLine("[ROUND] round video note id=" + doc.id + " → circle");
            if (_photoThumbCache.TryGetValue(doc.id, out var cached))
            {
                bubble.ConfigurePhoto(side, side, MessageBubbleControl.PhotoState.Loaded);
                bubble.SetImage(cached);
                return;
            }
            bubble.ConfigurePhoto(side, side, MessageBubbleControl.PhotoState.Loading);
            StartThumbDownload(bubble, doc);
        }

        private void ConfigureVideoThumb(MessageBubbleControl bubble, Document doc, MediaItem mi)
        {
            var thumb = doc.thumbs?.OfType<PhotoSize>().OrderBy(s => s.size).LastOrDefault();
            int w = thumb?.Width ?? (mi.Width > 0 ? mi.Width : 320);
            int h = thumb?.Height ?? (mi.Height > 0 ? mi.Height : 200);

            bubble.IsVideoThumb = true;
            bubble.IsGif = mi.Type == "gif";
            bubble.DurationText = mi.Duration > 0 ? FormatDuration(mi.Duration) : null;
            WireVideoTransfer(bubble, doc, mi);   // DOWNLOAD-UX v3 2.2: rebind + paused-tap-resumes

            if (_photoThumbCache.TryGetValue(doc.id, out var cached))
            {
                bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loaded);
                bubble.SetImage(cached);
                return;
            }
            bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loading);
            StartThumbDownload(bubble, doc);
        }

        /// <summary>DOWNLOAD-UX v3 2.2: a video/GIF bubble reflects its managed transfer — REBIND at build
        /// (in-flight/paused ring survives chat switches) and wire the paused-tap → same-handle resume.
        /// Completion has NO side-effect here by construction: nothing plays, no animator, no LibVLC — the
        /// bubble just repaints its state (the viewer is the one sanctioned auto-play, because the user is
        /// watching it there).</summary>
        private void WireVideoTransfer(MessageBubbleControl bubble, Document doc, MediaItem mi)
        {
            if (mi.Type != "gif")   // GIF resume already converges through PlayInline (DownloadRequested → PlayInline)
            {
                string path = MediaCache.MediaPath(MediaCache.CacheFileName(mi.Type, doc.id, mi.FileName));
                bubble.DownloadRequested += (s, e) =>
                {
                    var rh = StartManagedDownload(doc, mi.Type, path, true, _selectedChat?.Peer, 0, mi.FileName);
                    if (rh != null) bubble.SetDownloadHandle(rh);
                };
            }
            var ti = _service.GetTransfer(doc.id);
            if (ti != null && ti.Handle != null &&
                (ti.Handle.State == DownloadHandle.DState.Downloading || ti.Handle.State == DownloadHandle.DState.Paused))
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] rebind id=" + doc.id + " type=" + mi.Type
                    + " state=" + (ti.Handle.State == DownloadHandle.DState.Downloading ? "inflight" : "paused"));
                bubble.SetDownloadHandle(ti.Handle);
            }
        }

        private async void StartThumbDownload(MessageBubbleControl bubble, Document doc)
        {
            try
            {
                if (!_photoThumbCache.TryGetValue(doc.id, out var img))
                {
                    string path = MediaCache.ThumbCachePath(doc.id);
                    // Disk cache first (instant reuse across restarts).
                    if (File.Exists(path) && new FileInfo(path).Length > 0)
                        try { using (var fs = File.OpenRead(path)) using (var t = Image.FromStream(fs)) img = new Bitmap(t); } catch { }

                    if (img == null)
                    {
                        var bytes = await _service.DownloadThumbAsync(doc);
                        if (bytes != null && bytes.Length > 0)
                        {
                            img = await ToBitmapAsync(bytes);   // decode off the UI thread
                            try { img.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }   // path is under thumbs/ (ensured)
                        }
                    }
                    // No document thumbnail for a GIF → decode its FIRST FRAME as the still (what official
                    // Telegram does), so it's not a blank/dark dead tile. (GIFs only — videos stay thumb-only.)
                    if (img == null && IsGifDoc(doc))
                    {
                        System.Diagnostics.Debug.WriteLine("[GIF] no doc thumb id=" + doc.id + " -> first-frame fallback");
                        img = await GifFirstFrameAsync(doc);
                    }
                    if (img != null) _photoThumbCache[doc.id] = img;
                }
                if (bubble.IsDisposed) return;
                if (img != null) bubble.SetImage(img);
                else bubble.SetPlaceholder(); // dark area + play overlay (still clickable-to-play for GIFs via DownloadRequested)
            }
            catch
            {
                if (!bubble.IsDisposed) bubble.SetPlaceholder();
            }
        }

        private static bool IsGifDoc(Document doc)
            => doc != null && ((doc.attributes != null && doc.attributes.OfType<DocumentAttributeAnimated>().Any()) || doc.mime_type == "image/gif");

        /// <summary>Decodes a thumbless GIF's first frame as its static still: download the clip (atomically, to
        /// the SAME gif_{id}.mp4 the inline player reuses) then grab frame 0 via libVLC off the UI thread. Cached
        /// to thumb_{id}.png so later opens skip it. Null on failure.</summary>
        private async System.Threading.Tasks.Task<Image> GifFirstFrameAsync(Document doc)
        {
            try
            {
                string clip = MediaCache.ThumbPath("gif_" + doc.id + ".mp4");
                if (!(File.Exists(clip) && new FileInfo(clip).Length > 0))
                {
                    string tmp = clip + ".tmp";
                    try
                    {
                        MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        await _service.DownloadDocumentToFileAsync(doc, tmp);
                        if (!(File.Exists(tmp) && new FileInfo(tmp).Length > 0)) return null;
                        if (File.Exists(clip)) File.Delete(clip);
                        File.Move(tmp, clip);   // atomic → the play path holds a complete file
                    }
                    catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } return null; }
                }
                var v = doc.attributes?.OfType<DocumentAttributeVideo>().FirstOrDefault();
                int fw = v != null && v.w > 0 ? v.w : 320, fh = v != null && v.h > 0 ? v.h : 240;
                var bmp = await System.Threading.Tasks.Task.Run(() => (Image)UI.Controls.WebmAnimator.GrabFirstFrame(clip, fw, fh));
                if (bmp != null)
                    try { MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder); bmp.Save(MediaCache.ThumbCachePath(doc.id), System.Drawing.Imaging.ImageFormat.Png); } catch { }
                System.Diagnostics.Debug.WriteLine("[GIF] first-frame fallback " + (bmp != null ? "OK" : "FAILED") + " id=" + doc.id);
                return bmp;
            }
            catch { return null; }
        }

        private static string FormatDuration(int seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        /// <summary>Adds a message bubble, skipping duplicates by id. Returns false if skipped.</summary>
        private bool AddMessageBubble(ChatEntry entry, MessageBase mb, string sender, IPeerInfo from = null)
        {
            // Album items (grouped_id) merge into one album bubble instead of separate bubbles.
            if (mb is Message gm && gm.grouped_id != 0)
            {
                MessageBubbleControl created;
                int handled = HandleAlbumItem(entry, gm, sender, from, out created);
                if (created != null) _messagePanel.Controls.Add(created);
                return handled == 1;   // newly handled (new bubble or freshly-merged item); false = duplicate
            }

            if (!_shownMessageIds.Add(mb.ID)) return false;
            var ctl = MakeMessageBubble(entry, mb, sender);
            if (ctl == null) return false;   // service action skipped (e.g. HistoryClear) — nothing to show
            if (ctl is MessageBubbleControl bubble && mb is Message m)
            {
                if (entry.IsGroup && !IsOut(m) && from is User fu) WireSenderAvatar(bubble, fu);
                ApplyReactions(bubble, m);
                ApplyEntities(bubble, m);
            }
            _messagePanel.Controls.Add(ctl);
            return true;
        }

        /// <summary>Merges an album (grouped_id) message into one album bubble. Returns 0=not an album item,
        /// 1=newly handled (created out = a new album bubble to add, or null when merged into an existing one),
        /// 2=already shown (duplicate).</summary>
        private int HandleAlbumItem(ChatEntry entry, Message m, string sender, IPeerInfo from, out MessageBubbleControl created)
        {
            created = null;
            if (m.grouped_id == 0) return 0;
            bool fresh = _shownMessageIds.Add(m.ID);

            MessageBubbleControl album;
            if (_albumBubbles.TryGetValue(m.grouped_id, out album) && album != null && !album.IsDisposed)
            {
                if (fresh)
                {
                    album.AddAlbumItem(m.ID, IsVideoMessage(m));
                    StartAlbumTileLoad(album, m);
                    ConfigureAlbumAudio(album, m);   // → renders this item as an audio ROW (if it's audio)
                    // The caption lives on exactly ONE grouped item — may be a LATER one. First non-empty wins.
                    if (!album.AlbumHasCaption && !string.IsNullOrEmpty(m.message)) album.SetCaption(m.message, m.entities);
                    return 1;
                }
                return 2;
            }
            if (!fresh) return 2;

            // Create the album bubble with NO caption; resolve the caption from whichever item carries it.
            var bubble = CreateBubble("", sender, IsOut(m), m.date, m.ID);
            if (entry != null && entry.IsGroup && !IsOut(m) && from is User fu) WireSenderAvatar(bubble, fu);
            bubble.AlbumTileClicked += id => OpenMediaViewer(id);                              // photo/video tile → viewer
            bubble.AudioRowActivated += docId => OnAlbumAudioActivated(bubble, docId);         // audio row → play/download
            bubble.BeginAlbum(IsVideoMessage(m));
            ApplyReactions(bubble, m);
            ApplyEntities(bubble, m);   // wires the caption's link/mention router + custom-emoji resolver
            SetReply(bubble, m);        // edited / forward / reply on the album
            if (!string.IsNullOrEmpty(m.message)) bubble.SetCaption(m.message, m.entities);   // caption on the first item
            StartAlbumTileLoad(bubble, m);
            ConfigureAlbumAudio(bubble, m);   // → renders the first item as an audio ROW (if it's audio)
            _albumBubbles[m.grouped_id] = bubble;
            created = bubble;
            return 1;
        }

        private static bool IsVideoMessage(Message m)
        {
            var mi = MediaClassifier.FromMessage(m);
            return mi != null && (mi.Type == "video" || mi.Type == "gif");
        }

        /// <summary>Downloads (or reuses cached) the thumbnail for one album tile and sets it on the bubble.</summary>
        private async void StartAlbumTileLoad(MessageBubbleControl album, Message m)
        {
            try
            {
                var photo = (m.media as MessageMediaPhoto)?.photo as Photo;
                var doc = (m.media as MessageMediaDocument)?.document as Document;
                if (photo != null)
                {
                    Image cached;
                    if (_photoThumbCache.TryGetValue(photo.id, out cached)) { album.SetAlbumTileImage(m.ID, cached); return; }
                    var bytes = await _service.DownloadPhotoThumbAsync(photo);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var img = await ToBitmapAsync(bytes); _photoThumbCache[photo.id] = img;
                        if (!album.IsDisposed) album.SetAlbumTileImage(m.ID, img);
                    }
                }
                else if (doc != null)
                {
                    Image cached;
                    if (_photoThumbCache.TryGetValue(doc.id, out cached)) { album.SetAlbumTileImage(m.ID, cached); return; }
                    var bytes = await _service.DownloadThumbAsync(doc);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var img = await ToBitmapAsync(bytes); _photoThumbCache[doc.id] = img;
                        if (!album.IsDisposed) album.SetAlbumTileImage(m.ID, img);
                    }
                }
            }
            catch { /* album tile thumbnail is best-effort */ }
        }

        /// <summary>If this album item is audio/voice, render it as a vertical audio ROW (title + "M:SS • size").</summary>
        private void ConfigureAlbumAudio(MessageBubbleControl album, Message m)
        {
            var mi = MediaClassifier.FromMessage(m);
            if (mi == null || (mi.Type != "audio" && mi.Type != "voice")) return;
            var doc = (m.media as MessageMediaDocument)?.document as Document;
            if (doc == null) return;
            bool isAudio = mi.Type == "audio";
            string dur = mi.Duration > 0 ? FormatDuration(mi.Duration) : null;
            string size = mi.FileSize > 0 ? DrawHelper.FormatSize(mi.FileSize) : null;
            string sub = dur != null && size != null ? dur + "  •  " + size : (dur ?? size ?? "");
            string title = AudioRowTitle(doc, mi, isAudio);
            bool cached = TelegramService.IsFileComplete(AudioCachePath(doc, isAudio, mi.FileName), doc.size);
            album.SetAlbumAudio(m.ID, doc.id, title, sub, cached);
            System.Diagnostics.Debug.WriteLine("[ALBUM] audio row id=" + doc.id + " title='" + title + "' cached=" + cached);
            // DOWNLOAD-UX 2.2: re-entering the chat while this row's transfer kept running (or sits paused)
            // → REBIND the ring to the live/paused handle instead of showing an idle arrow.
            var ti = _service.GetTransfer(doc.id);
            if (ti != null && ti.Handle != null &&
                (ti.Handle.State == DownloadHandle.DState.Downloading || ti.Handle.State == DownloadHandle.DState.Paused))
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] rebind id=" + doc.id + " state="
                    + (ti.Handle.State == DownloadHandle.DState.Downloading ? "inflight" : "paused"));
                album.SetAudioRowHandle(doc.id, ti.Handle);
            }
        }

        /// <summary>An audio-album row was tapped: play if cached, else download (ring on the row) then play.</summary>
        private async void OnAlbumAudioActivated(MessageBubbleControl album, long docId)
        {
            Document doc = null; MediaItem mi = null; string title = null; int srcMsgId = 0;
            foreach (var msg in _currentChatMessages)
            {
                var d = (msg.media as MessageMediaDocument)?.document as Document;
                if (d == null || d.id != docId) continue;
                var cmi = MediaClassifier.FromMessage(msg);
                if (cmi == null || (cmi.Type != "audio" && cmi.Type != "voice")) continue;
                doc = d; mi = cmi; title = AudioRowTitle(d, cmi, cmi.Type == "audio"); srcMsgId = msg.ID; break;
            }
            if (doc == null) return;
            bool isAudio = mi.Type == "audio";
            MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
            string path = AudioCachePath(doc, isAudio, mi.FileName);
            if (TelegramService.IsFileComplete(path, doc.size))
            {
                System.Diagnostics.Debug.WriteLine("[AUDIO] album row play/pause (cached) id=" + docId);
                AudioPlayer.Toggle(docId, path, title);
                return;
            }
            // FILE_REFERENCE refresh-retry covers this route (via the choke point — DOWNLOAD-UX v3 1.3).
            var h = StartManagedDownload(doc, "audio", path, true, _selectedChat?.Peer, srcMsgId, title);
            if (h == null) return;
            System.Diagnostics.Debug.WriteLine("[AUDIO] album row download start id=" + docId);
            album.SetAudioRowHandle(docId, h);   // album owns the ring + pause tap
            var chatAtStart = _selectedChat;     // DOWNLOAD-UX 2.3: transfers outlive chats now
            await AwaitHandle(h);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                // Auto-play ONLY if the user is still in this chat — a background completion from a
                // switched-away chat must not hijack the speakers.
                if (_selectedChat != null && ReferenceEquals(_selectedChat, chatAtStart))
                {
                    System.Diagnostics.Debug.WriteLine("[AUDIO] album row download complete → play id=" + docId);
                    AudioPlayer.Toggle(docId, path, title);
                }
                else System.Diagnostics.Debug.WriteLine("[AUDIO] album row download complete (chat switched — no auto-play) id=" + docId);
            }
        }

        /// <summary>Display title for an audio row: "Performer – Title", else Title, else file name, else "Audio".</summary>
        private static string AudioRowTitle(Document doc, MediaItem mi, bool isAudio)
        {
            var a = doc.attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
            string title = a != null ? a.title : null, performer = a != null ? a.performer : null;
            if (!string.IsNullOrEmpty(performer) && !string.IsNullOrEmpty(title)) return performer + " – " + title;
            if (!string.IsNullOrEmpty(title)) return title;
            if (!isAudio) return "Voice message";
            if (!string.IsNullOrEmpty(mi.FileName) && !mi.FileName.StartsWith("file_")) return System.IO.Path.GetFileNameWithoutExtension(mi.FileName);
            return "Audio";
        }

        /// <summary>Group incoming: show the clickable sender avatar (→ member profile).</summary>
        private void WireSenderAvatar(MessageBubbleControl bubble, User u)
        {
            bubble.ShowAvatar = true;
            bubble.AvatarPeerId = u.id;
            bubble.AvatarClicked += (s, e) => OpenUserProfile(u);
            SetSenderAvatar(bubble, u);
        }

        /// <summary>Opens a group member's profile; "Send Message" there opens a 1:1 chat.</summary>
        private async void OpenUserProfile(User u)
        {
            if (u == null) return;
            var entry = new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u), IsGroup = false, PeerInfo = u };
            var av = _avatars.GetCached(u.id);
            using (var dlg = new ProfileForm(_service, entry, av))
            {
                dlg.Avatars = _avatars;   // PROFILE-MEMBERS: member rows use the shared store
                dlg.ForwardRequested += ForwardFromProfile;
                dlg.ShowInChatRequested += ShowInChatFromProfile;
                dlg.ShowDialog(this);
                if (dlg.SendMessageRequested) await OpenChat(entry, 0);
                else RouteProfilePending(dlg);
            }
        }

        /// <summary>Group sender avatar via the shared store: memory hit paints now; otherwise the bubble is
        /// tagged with the sender's id and repainted by <see cref="OnAvatarLoaded"/> when the fetch lands
        /// (the bubble is found LIVE by id — no captured reference; the old private fetch path is gone).</summary>
        private void SetSenderAvatar(MessageBubbleControl bubble, User u)
        {
            bubble.SenderPeerId = u.id;
            var img = _avatars.GetCached(u.id);
            if (img != null) { bubble.SenderAvatar = img; return; }
            _avatars.Request(u.id, u);
        }

        private void ClearMessagePanel()
        {
            TouchScroller.StopMomentum();   // 3.4: a coast must not scroll the NEXT chat's content

            StopAllInline();   // stop the inline video player (WebM/GIF) + free buffers/frames BEFORE the bubbles are disposed

            // DOWNLOAD-UX POLICY: chat switches NO LONGER cancel downloads — transfers run in the background
            // and returning bubbles REBIND to them. (The old "reason=chat-switch CANCELLED" log lines are gone
            // by design.) Account teardown still cancels via ResetPerAccountState (cache isolation).

            var old = _messagePanel.Controls.Cast<Control>().ToArray();
            _messagePanel.Controls.Clear();
            foreach (var c in old) c.Dispose(); // stops timers / frees fonts; VoiceBubble.Dispose cancels its download

            // Free the per-chat media bitmaps instead of holding them for the whole session (bounded memory
            // on ARM32). No visible control references them after the panel is cleared. The disk-path index
            // (_photoCachePaths) is kept (tiny strings) so a re-open reloads full photos from media/ on disk.
            foreach (var img in _photoThumbCache.Values) img.Dispose();
            _photoThumbCache.Clear();
            foreach (var img in _photoCache.Values) img.Dispose();
            _photoCache.Clear();
            System.Diagnostics.Debug.WriteLine("[MEDIA] chat-switch: per-chat bitmap caches disposed");

            // Pending attachment bubbles are local-only; drop them and free the thumbnails we own.
            _pendingBubbles.Clear();
            foreach (var img in _attachmentThumbs) img.Dispose();
            _attachmentThumbs.Clear();
        }

        // ── Inline photo download ────────────────────────────────────────────

        /// <summary>Rebuilds the photo→file index from the cache folder so restarts reuse downloads.</summary>
        private void RestorePhotoCacheIndex()
        {
            try
            {
                var folder = MediaCache.MediaFolder;   // full photos live under media/
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.GetFiles(folder, "photo_*.jpg"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (long.TryParse(name.Replace("photo_", ""), out long id))
                        _photoCachePaths[id] = file;
                }
            }
            catch { /* cache index is best-effort */ }
        }

        private void ConfigurePhoto(MessageBubbleControl bubble, Photo photo)
        {
            var size = photo.LargestPhotoSize;
            int w = size?.Width ?? 0;
            int h = size?.Height ?? 0;
            long bytes = size?.FileSize ?? 0;

            if (_photoCache.TryGetValue(photo.id, out var cached))
            {
                bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loaded);
                bubble.SetImage(cached);
                return;
            }

            // Disk cache: load instantly without re-downloading.
            if (_photoCachePaths.TryGetValue(photo.id, out var cachedPath) && File.Exists(cachedPath))
            {
                try
                {
                    var diskBytes = File.ReadAllBytes(cachedPath);
                    using (var src = new MemoryStream(diskBytes))
                    using (var tmp = Image.FromStream(src))
                        cached = new Bitmap(tmp);
                    _photoCache[photo.id] = cached;   // already-cached full (from a prior open) → show it; do NOT re-cache bytes
                    bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loaded);
                    bubble.SetImage(cached);
                    return;
                }
                catch
                {
                    _photoCachePaths.Remove(photo.id); // corrupt/locked — fall through to re-download
                }
            }

            // Render = THUMBNAIL ONLY by default; the full image is fetched on open-in-viewer. The full
            // image loads on render only if the auto-download policy opts photos in (conservative default OFF).
            bool autoOk = MediaPolicy.ShouldAutoDownload("photo", bytes);

            if (_photoThumbCache.TryGetValue(photo.id, out var thumb))
            {
                bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loaded);
                bubble.SetImage(thumb);
            }
            else
            {
                bubble.ConfigurePhoto(w, h, MessageBubbleControl.PhotoState.Loading);
            }
            StartPhotoLoad(bubble, photo, autoOk);
        }

        private static Image ToBitmap(byte[] bytes)
        {
            using (var src = new MemoryStream(bytes))
            using (var tmp = Image.FromStream(src))
                return new Bitmap(tmp); // self-contained copy (detach from stream)
        }

        /// <summary>Decodes bytes into a Bitmap OFF the UI thread (RT is single-UI-thread + slow on ARM32).
        /// The finished Bitmap is handed back to the caller's UI-thread continuation; the worker never touches a control.</summary>
        private static System.Threading.Tasks.Task<Image> ToBitmapAsync(byte[] bytes)
            => System.Threading.Tasks.Task.Run(() => ToBitmap(bytes));

        private async void StartPhotoLoad(MessageBubbleControl bubble, Photo photo, bool loadFull)
        {
            try
            {
                // 1) quick small-thumbnail preview (replaces the old "tap to download" text)
                if (!_photoCache.ContainsKey(photo.id) && !_photoThumbCache.ContainsKey(photo.id))
                {
                    var tb = await _service.DownloadPhotoThumbAsync(photo);
                    if (tb != null && tb.Length > 0)
                    {
                        var timg = await ToBitmapAsync(tb);   // decode off the UI thread
                        _photoThumbCache[photo.id] = timg;
                        System.Diagnostics.Debug.WriteLine("[MEDIA] render: thumb only (photo " + photo.id + ")");
                        if (!bubble.IsDisposed && !_photoCache.ContainsKey(photo.id)) bubble.SetImage(timg);
                    }
                }

                // 2) full image — ONLY when the auto-download policy opted photos in (never by default)
                if (loadFull && !_photoCache.ContainsKey(photo.id))
                {
                    System.Diagnostics.Debug.WriteLine("[MEDIA] auto-download full photo " + photo.id);
                    var result = await _service.DownloadPhotoAsync(photo);
                    if (result.bytes != null && result.bytes.Length > 0)
                    {
                        var img = await ToBitmapAsync(result.bytes);   // decode off the UI thread
                        _photoCache[photo.id] = img;
                        if (!string.IsNullOrEmpty(result.cachePath)) _photoCachePaths[photo.id] = result.cachePath;  // bytes are a viewer concern; not cached in RAM
                        if (!bubble.IsDisposed) bubble.SetImage(img);
                    }
                }

                // nothing to show at all → placeholder text
                if (!bubble.IsDisposed && !_photoCache.ContainsKey(photo.id) && !_photoThumbCache.ContainsKey(photo.id))
                    bubble.SetPlaceholder();
            }
            catch
            {
                if (!bubble.IsDisposed && !_photoThumbCache.ContainsKey(photo.id)) bubble.SetPlaceholder();
            }
        }

        /// <summary>Opens the media viewer for the clicked message, with all chat media for navigation.</summary>
        private void OpenMediaViewer(int messageId)
        {
            var media = new List<MediaItem>();
            foreach (var m in _currentChatMessages)
            {
                var mi = MediaClassifier.FromMessage(m);
                if (mi == null) continue;
                if (mi.Type == "photo")
                {
                    // Full bytes are no longer cached in RAM; the viewer loads from media/ disk via LocalPath.
                    if (_photoCachePaths.TryGetValue(mi.Id, out var path)) mi.LocalPath = path;
                }
                media.Add(mi);
            }
            if (media.Count == 0) return;

            int idx = media.FindIndex(mi => mi.MessageId == messageId);
            if (idx < 0) idx = 0;

            using (var viewer = new MediaViewerForm(media, idx, _service))
                viewer.ShowDialog(this);
        }

        private int DistanceFromBottom()
        {
            int max = Math.Max(0, _messagePanel.DisplayRectangle.Height - _messagePanel.ClientSize.Height);
            return Math.Max(0, max - (-_messagePanel.AutoScrollPosition.Y));
        }
        private bool AtBottom(int threshold) => DistanceFromBottom() <= threshold;

        private void PositionJumpButton()
        {
            if (_jumpBtn == null || _msgHost == null) return;
            int x = _msgHost.ClientSize.Width - _jumpBtn.Width - 14 - 6;   // clear of the themed scrollbar
            int y = _msgHost.ClientSize.Height - _jumpBtn.Height - 10;
            _jumpBtn.Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        /// <summary>Updates the floating button's visibility/count and triggers the read when at the bottom.</summary>
        private void OnScrollPositionChanged()
        {
            if (_jumpBtn == null) return;
            bool hasMsgs = _selectedChat != null && _messagePanel.Controls.Count > 0;
            if (!hasMsgs) { if (_jumpBtn.Visible) _jumpBtn.Visible = false; return; }

            CheckInlineVisibility();   // stop the playing inline video (WebM/GIF) if it scrolled out of view
            MaybeViewSponsored();   // fire viewSponsoredMessage once when the ad's full text scrolls into view

            // From a focused island, page newer toward the live tail before the user hits the bottom edge.
            if (!_atLiveTail && !_loadingNewer && DistanceFromBottom() <= 300)
                _ = LoadNewerMessages();

            if (_atLiveTail && AtBottom(40))   // at the REAL bottom → caught up
            {
                _jumpUnread = 0;
                if (_jumpBtn.Visible) _jumpBtn.Visible = false;
                MarkCaughtUp();
            }
            else                // scrolled up, OR sitting at an island edge that isn't the true tail → show button
            {
                _jumpBtn.UnreadCount = _jumpUnread;
                if (!_jumpBtn.Visible) { PositionJumpButton(); _jumpBtn.Visible = true; _jumpBtn.BringToFront(); }
            }
        }

        private void JumpToBottomClicked()
        {
            _jumpUnread = 0;
            // From an island, a plain scroll only reaches the island edge — reload the bottom window so the
            // jump lands on the real latest message (simpler + guaranteed to reach the tail).
            if (!_atLiveTail && _selectedChat != null) { var _ = LoadHistoryAsync(_selectedChat); return; }
            ScrollMessagesToBottom();
            if (_jumpBtn != null) _jumpBtn.Visible = false;
            MarkCaughtUp();
        }

        /// <summary>Reads the open chat up to the latest loaded message — only when there's something newer
        /// (so it can't spam ReadHistory). This is the "caught up" trigger (reaching the bottom).</summary>
        private void MarkCaughtUp()
        {
            if (_selectedChat == null) return;
            int latest = _currentChatMessages.Count > 0
                ? _currentChatMessages[_currentChatMessages.Count - 1].ID
                : _selectedChat.TopMessageId;
            if (latest <= 0 || latest <= _selectedChat.ReadInboxMaxId) return;
            _selectedChat.ReadInboxMaxId = latest;
            _selectedChat.UnreadCount = 0;
            var _ = SafeReadHistory(_selectedChat.Peer, latest);
            FindChatItem(_selectedChat.PeerId)?.Invalidate();
            UpdateTrayTooltip();
        }

        private async System.Threading.Tasks.Task SafeReadHistory(InputPeer peer, int maxId)
        {
            try { await _service.ReadHistoryAsync(peer, maxId); } catch { }
        }

        private static int BubbleMsgId(Control c)
        {
            if (c is MessageBubbleControl b) return b.MessageId;
            if (c is VoiceBubbleControl v) return v.MessageId;
            return 0;
        }

        /// <summary>Finds the rendered row for a message id — any kind (text/photo/file/voice/audio).</summary>
        private Control FindMessageControl(int messageId)
        {
            foreach (Control c in _messagePanel.Controls)
            {
                if (BubbleMsgId(c) == messageId) return c;
                var mb = c as MessageBubbleControl;
                if (mb != null && mb.IsAlbum && mb.ContainsMessageId(messageId)) return mb;   // any album item → the album
            }
            return null;
        }

        /// <summary>Scrolls a message into view and flashes it via IFlashable (works for voice/audio too).</summary>
        private bool ScrollToAndFlash(int messageId)
        {
            TouchScroller.StopMomentum();   // 3.5: jump-to-message wins over a coast
            _messagePanel.PerformLayout();
            var c = FindMessageControl(messageId);
            if (c == null) return false;
            _messagePanel.ScrollControlIntoView(c);
            (c as IFlashable)?.Flash();
            return true;
        }

        /// <summary>Reply-quote tap (Part B): jump to the replied-to message in the OPEN chat and flash it.
        /// If it's loaded, scroll+flash immediately; if it's older than the loaded window, do a focused
        /// history load centered on it (same path as Show-in-chat / search / pinned-jump), then flash.</summary>
        private void JumpToReply(int replyToMsgId)
        {
            if (replyToMsgId <= 0) return;
            if (ScrollToAndFlash(replyToMsgId)) return;     // already in the loaded window
            var chat = _selectedChat;
            if (chat == null) return;
            BeginInvoke((Action)(async () =>
            {
                await OpenChat(chat, replyToMsgId);          // focused load centered on the target (same chat)
                ScrollToAndFlash(replyToMsgId);
            }));
        }

        /// <summary>Scrolls the first message newer than read_inbox_max_id to the TOP of the viewport.
        /// Returns false if no such message is in the loaded window (caller falls back to the bottom).</summary>
        private bool ScrollToFirstUnread(ChatEntry entry)
        {
            TouchScroller.StopMomentum();   // 3.5: open-at-unread positioning wins over a coast
            _messagePanel.PerformLayout();
            foreach (Control c in _messagePanel.Controls)
            {
                int id = BubbleMsgId(c);
                if (id > 0 && id > entry.ReadInboxMaxId)
                {
                    _messagePanel.AutoScrollPosition = new Point(0, Math.Max(0, c.Top));
                    return true;
                }
            }
            return false;
        }

        private void ScrollMessagesToBottom()
        {
            TouchScroller.StopMomentum();   // 3.5: programmatic scroll (send / open-at-bottom) wins over a coast
            if (_messagePanel.Controls.Count == 0) return;
            // Make sure the scroll range reflects the just-added bubble…
            _messagePanel.PerformLayout();
            // …then scroll fully to the bottom so the panel's bottom padding is
            // revealed and the last bubble clears the input bar. (ScrollControlIntoView
            // would stop flush against the bar, leaving the bubble looking hidden.)
            _messagePanel.AutoScrollPosition = new Point(0, _messagePanel.VerticalScroll.Maximum);
        }

        private async void SendCurrentMessage()
        {
            if (_footerKind != ComposerKind.Compose) return;   // gated: composer isn't shown in non-compose states
            var text = _messageInput.Text.Trim();
            if (text.Length == 0 || _selectedChat == null) return;

            var chat = _selectedChat;

            // Editing an existing message takes precedence over sending a new one.
            if (_editTarget != null)
            {
                var target = _editTarget;
                _messageInput.Text = "";
                CancelReply();          // also clears _editTarget + hides the strip
                await ApplyEdit(chat, target, text);
                return;
            }

            int replyId = _replyTarget?.ID ?? 0;       // capture before clearing the composer
            string replyPreview = null;
            if (_replyTarget != null)
            {
                replyPreview = GetDisplayText(_replyTarget);
                if (replyPreview.Length > 60) replyPreview = replyPreview.Substring(0, 60) + "…";
            }

            _messageInput.Text = "";
            CancelReply();

            // Optimistic bubble — clock while sending, then ✓ on confirm. Carry the reply quote.
            var bubble = CreateBubble(text, null, true, DateTime.UtcNow);
            bubble.Pending = true;
            if (replyPreview != null) { bubble.ReplyPreview = replyPreview; bubble.Measure(); }
            _messagePanel.Controls.Add(bubble);
            ScrollMessagesToBottom();

            try
            {
                var sent = await _service.SendTextAsync(chat.Peer, text, replyId);
                if (sent != null)
                {
                    _shownMessageIds.Add(sent.ID);     // dedupe a possible echo update
                    bubble.MessageId = sent.ID;        // make the bubble actionable (reply/forward/delete)
                    bubble.Pending = false;            // → single check (✓)
                    bubble.Invalidate();
                    if (!_currentChatMessages.Any(x => x.ID == sent.ID)) _currentChatMessages.Add(sent);
                    UpdateChatListForMessage(chat.PeerId, sent, true);   // update the chat-list row now
                }
            }
            catch
            {
                bubble.Pending = false;
                bubble.Failed = true;                  // → red failed mark on the bubble
                bubble.Invalidate();
            }
        }

        /// <summary>Edits a message server-side, then rebuilds its bubble in place with the new text.</summary>
        private async System.Threading.Tasks.Task ApplyEdit(ChatEntry chat, Message target, string newText)
        {
            try
            {
                await _service.EditMessageAsync(chat.Peer, target.ID, newText);
                if (_selectedChat != chat) return;     // user switched chats meanwhile
                var msg = _currentChatMessages.FirstOrDefault(x => x.ID == target.ID) ?? target;
                msg.message = newText;                  // TL Message.message is settable
                msg.edit_date = DateTime.UtcNow;        // so the rebuilt bubble shows "edited"
                msg.flags |= Message.Flags.has_edit_date;
                RebuildBubble(target.ID, msg);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Edit", "Couldn't edit: " + ex.Message, "OK");
            }
        }

        /// <summary>Replaces the bubble for <paramref name="messageId"/> with a fresh one (same position),
        /// re-wiring entities/reactions and re-folding any media/card. Preserves scroll (or stays pinned to
        /// the bottom if the view was at the bottom, so a card popping in on the newest message stays visible).</summary>
        private void RebuildBubble(int messageId, Message msg)
        {
            for (int i = 0; i < _messagePanel.Controls.Count; i++)
            {
                if (_messagePanel.Controls[i] is MessageBubbleControl old && old.MessageId == messageId)
                {
                    // Preserve the group sender name (MakeMessageBubble needs it; the cache has it).
                    string sender = null;
                    if (_selectedChat != null && _selectedChat.IsGroup && !IsOut(msg) && msg.from_id is PeerUser pu)
                        _peerNames.TryGetValue(pu.user_id, out sender);

                    bool atBottom = -_messagePanel.AutoScrollPosition.Y + _messagePanel.ClientSize.Height
                                    >= _messagePanel.DisplayRectangle.Height - 8;
                    int scrollY = -_messagePanel.AutoScrollPosition.Y;

                    _messagePanel.SuspendLayout();
                    var fresh = MakeMessageBubble(_selectedChat, msg, sender) as MessageBubbleControl;
                    if (fresh == null) { _messagePanel.ResumeLayout(); return; }
                    ApplyReactions(fresh, msg);
                    ApplyEntities(fresh, msg);   // re-wires inline links + the card-tap (LinkClicked) path
                    _messagePanel.Controls.Add(fresh);
                    _messagePanel.Controls.SetChildIndex(fresh, i);
                    _messagePanel.Controls.Remove(old);
                    old.Dispose();
                    _messagePanel.ResumeLayout(true);

                    if (atBottom) ScrollMessagesToBottom();
                    else _messagePanel.AutoScrollPosition = new Point(0, scrollY);
                    return;
                }
            }
        }

        // ── Message actions (context menu: copy / reply / delete) ────────────────

        /// <summary>Builds + shows the per-message context menu (right-click / touch-hold).</summary>
        private void OnBubbleContextMenu(object sender, Point screenPt)
        {
            var bubble = sender as MessageBubbleControl;
            if (bubble == null) return;
            Message msg = bubble.MessageId > 0
                ? _currentChatMessages.FirstOrDefault(x => x.ID == bubble.MessageId)
                : null;
            // Retry only applies to a failed optimistic media send (we kept its source).
            var failed = bubble.Failed && _failedSends.ContainsKey(bubble) ? bubble : null;
            ShowMessageMenu(bubble, msg, screenPt, failed);
        }

        private void OnVoiceContextMenu(object sender, Point screenPt)
        {
            var vb = sender as VoiceBubbleControl;
            if (vb == null) return;
            Message msg = vb.MessageId > 0
                ? _currentChatMessages.FirstOrDefault(x => x.ID == vb.MessageId)
                : null;
            ShowMessageMenu(vb, msg, screenPt, null);
        }

        /// <summary>Builds + shows the themed per-message menu shared by message and voice bubbles.</summary>
        private void ShowMessageMenu(Control ctl, Message msg, Point screenPt, MessageBubbleControl failedBubble)
        {
            if (_selectedChat == null) return;

            var menu = new ThemedContextMenuStrip();
            if (failedBubble != null)
                AddMenuItem(menu, "⟳   Retry", () => RetrySend(failedBubble));

            // DOWNLOAD-UX 3.3: ring-tap now PAUSES; explicit cancel lives here (and in the manager panel).
            {
                var mdoc = (msg?.media as MessageMediaDocument)?.document as Document;
                if (mdoc != null)
                {
                    var live = _service.GetDownload(mdoc.id);
                    var ti = live == null ? _service.GetTransfer(mdoc.id) : null;
                    bool pausedRow = ti != null && ti.Handle != null && ti.Handle.State == DownloadHandle.DState.Paused;
                    if (live != null || pausedRow)
                        AddMenuItem(menu, "✕   Cancel download", () => _service.CancelTransfer(mdoc.id));
                }
            }

            var mb = ctl as MessageBubbleControl;
            if (mb != null && mb.HasTextSelection)
                AddMenuItem(menu, "⧉   Copy Selected Text", () =>
                {
                    try { Clipboard.SetText(mb.GetSelectedText() ?? ""); } catch { }
                    mb.ClearTextSelection();
                });
            else
            {
                string copyText = msg?.message ?? "";
                if (!string.IsNullOrEmpty(copyText))
                    AddMenuItem(menu, "⧉   Copy text", () => { try { Clipboard.SetText(copyText); } catch { } });
            }
            if (mb != null && mb.HasSelectableText && !mb.HasTextSelection)
                AddMenuItem(menu, "✍   Select Text", () => mb.SelectAllText());
            if (msg != null)
            {
                AddMenuItem(menu, "↩   Reply", () => StartReply(msg));
                AddReactMenu(menu, msg);
                if ((msg.flags & Message.Flags.pinned) != 0)
                    AddMenuItem(menu, "📌   Unpin", () => UnpinMessage(msg));
                else
                    AddMenuItem(menu, "📌   Pin", () => PinMessage(msg));
                if (IsOut(msg) && !string.IsNullOrEmpty(msg.message))
                    AddMenuItem(menu, "✎   Edit", () => StartEdit(msg));
                // In selection mode, Forward acts on the whole selection (not just this bubble).
                if (_selectionMode && _selectedMessageIds.Count > 0)
                    AddMenuItem(menu, "⮕   Forward (" + _selectedMessageIds.Count + ")", () => ForwardSelected());
                else
                    AddMenuItem(menu, "⮕   Forward", () => ForwardMessage(msg));
                AddMenuItem(menu, "☑   Select", () => EnterSelectionMode(msg.ID));
                if (MediaClassifier.FromMessage(msg) != null)   // any media (photo/video/audio/voice/file/gif/sticker)
                {
                    AddMenuItem(menu, "💾   Save", () => { var _ = SaveMediaAsync(msg); });
                    AddMenuItem(menu, "📁   Reveal in folder", () => RevealMedia(msg));
                }
                AddMenuItem(menu, "🗑   Delete", () => DeleteMessage(ctl, msg));
            }

            if (menu.Items.Count == 0) { menu.Dispose(); return; }

            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        /// <summary>Pins a message after asking who should see it (everyone vs. just me).</summary>
        private async void PinMessage(Message msg)
        {
            if (_selectedChat == null) return;
            // 0 = everyone, 1 = just me (pm_oneside), 2 = cancel.
            int choice = ThemedDialog.Show(this, "Pin message",
                "Who should see this message pinned?", "Everyone", "Just me", "Cancel");
            if (choice != 0 && choice != 1) return;
            try { await _service.PinMessageAsync(_selectedChat.Peer, msg.ID, choice == 1); LoadPinnedAsync(_selectedChat); }
            catch (Exception ex) { ThemedDialog.Show(this, "Pin failed", ex.Message, "OK"); }
        }

        private async void UnpinMessage(Message msg)
        {
            if (_selectedChat == null) return;
            try { await _service.UnpinMessageAsync(_selectedChat.Peer, msg.ID); LoadPinnedAsync(_selectedChat); }
            catch (Exception ex) { ThemedDialog.Show(this, "Unpin failed", ex.Message, "OK"); }
        }

        // ── Pinned-messages bar ──────────────────────────────────────────────

        /// <summary>Fetches the chat's pinned messages and refreshes the bar (best-effort, non-blocking).</summary>
        private async void LoadPinnedAsync(ChatEntry entry)
        {
            // ROOT CAUSE of the loop-at-3rd-pin: this runs again whenever a pinned jump triggers a focused
            // OpenChat (for an off-window pin) — and it used to reset _pinnedIndex=0, clobbering the cycle.
            // Only reset when switching to a DIFFERENT chat; on a same-chat reload, keep the cycle position.
            bool sameChat = entry != null && _pinnedChatId == entry.PeerId && _pinnedMessages != null;
            if (!sameChat)
            {
                _pinnedMessages = null;
                _pinnedIndex = 0;
                _pinnedChatId = entry?.PeerId ?? 0;
                UpdatePinnedBar();
            }
            if (entry == null) return;
            try
            {
                var pins = await _service.GetPinnedMessagesAsync(entry.Peer);
                if (IsDisposed || _selectedChat != entry) return;
                _pinnedMessages = new List<Message>(pins);
                _pinnedChatId = entry.PeerId;
                if (_pinnedIndex >= _pinnedMessages.Count) _pinnedIndex = 0;   // clamp; do NOT reset on same-chat reload
                System.Diagnostics.Debug.WriteLine("[PIN] open '" + entry.Title + "' count=" + _pinnedMessages.Count
                    + " ids=[" + string.Join(",", _pinnedMessages.ConvertAll(p => p.ID)) + "] index=" + _pinnedIndex);
                UpdatePinnedBar();
            }
            catch { /* pinned bar is best-effort */ }
        }

        private void UpdatePinnedBar()
        {
            bool has = _pinnedMessages != null && _pinnedMessages.Count > 0;
            if (_rightLayout != null) _rightLayout.RowStyles[2].Height = has ? 46 : 0;
            if (_pinnedListForm != null && !_pinnedListForm.IsDisposed)
            {
                if (!has) _pinnedListForm.Close();                       // no pins left → close the popup
                else _pinnedListForm.SetPins(BuildPinnedRows());         // keep it in sync after (un)pins
            }
            if (_pinnedBar == null) return;
            _pinnedBar.Visible = has;
            if (!has) return;
            if (_pinnedIndex >= _pinnedMessages.Count) _pinnedIndex = 0;
            var m = _pinnedMessages[_pinnedIndex];
            string title = _pinnedMessages.Count > 1
                ? "Pinned · " + (_pinnedIndex + 1) + "/" + _pinnedMessages.Count
                : "Pinned message";
            _pinnedBar.SetContent(title, GetDisplayText(m));
            _pinnedBar.Invalidate();
        }

        /// <summary>Tap the bar: jump to the current pinned message (highlight it), then advance to the next.</summary>
        private void OnPinnedBarClicked()
        {
            if (_pinnedMessages == null || _pinnedMessages.Count == 0) return;
            var m = _pinnedMessages[_pinnedIndex];
            System.Diagnostics.Debug.WriteLine("[PIN] click index=" + _pinnedIndex + "/" + _pinnedMessages.Count + " jump id=" + m.ID);
            _pinnedIndex = (_pinnedIndex + 1) % _pinnedMessages.Count;   // advance over the FULL stored list; wrap only after the last
            UpdatePinnedBar();
            ScrollToAndHighlight(m.ID);
        }

        private void ScrollToAndHighlight(int msgId)
        {
            if (ScrollToAndFlash(msgId)) return;                                            // in the loaded window
            if (_selectedChat != null) { var _ = OpenChat(_selectedChat, msgId); }          // else load around it
        }

        // ── Pinned-messages list (Part 2) ────────────────────────────────────

        /// <summary>Builds the ordered (newest→oldest) rows for the pinned-list popup.</summary>
        private List<(int id, string label, string preview)> BuildPinnedRows()
        {
            var rows = new List<(int, string, string)>();
            if (_pinnedMessages == null) return rows;
            int n = _pinnedMessages.Count;
            for (int i = 0; i < n; i++)
            {
                var m = _pinnedMessages[i];
                rows.Add((m.ID, "Pinned #" + (i + 1) + " of " + n, GetDisplayText(m)));
            }
            return rows;
        }

        /// <summary>Opens the "show all pinned" popup, positioned under the pinned bar.</summary>
        private void ShowPinnedList()
        {
            if (_pinnedMessages == null || _pinnedMessages.Count == 0) return;
            if (_pinnedListForm != null && !_pinnedListForm.IsDisposed) { _pinnedListForm.Activate(); return; }

            var form = new PinnedListForm(_dark, _accent);
            _pinnedListForm = form;
            form.JumpRequested += id => { ScrollToAndHighlight(id); if (!form.IsDisposed) form.Close(); };
            form.ContextRequested += OnPinnedRowContext;
            form.FormClosed += (s, e) => { if (_pinnedListForm == form) _pinnedListForm = null; };
            form.SetPins(BuildPinnedRows());

            // Anchor the popup under the pinned bar's right edge (where the list icon is).
            var anchor = _pinnedBar != null && _pinnedBar.Visible
                ? _pinnedBar.PointToScreen(new Point(_pinnedBar.Width - form.Width, _pinnedBar.Height))
                : PointToScreen(new Point((Width - form.Width) / 2, 80));
            var scr = Screen.FromControl(this).WorkingArea;
            int fx = Math.Max(scr.Left, Math.Min(anchor.X, scr.Right - form.Width));
            int fy = Math.Max(scr.Top, Math.Min(anchor.Y, scr.Bottom - form.Height));
            form.Location = new Point(fx, fy);
            form.Show(this);
        }

        /// <summary>Right-click / long-press on a pinned row → themed Jump / Unpin menu.</summary>
        private void OnPinnedRowContext(int msgId, Point screenPt)
        {
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, "↧   Jump to message", () => { ScrollToAndHighlight(msgId); _pinnedListForm?.Close(); });
            AddMenuItem(menu, "📌   Unpin", () => UnpinFromList(msgId));
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        /// <summary>Unpin from the list: for-everyone where the peer allows it, else just on our side.</summary>
        private async void UnpinFromList(int msgId)
        {
            var chat = _selectedChat;
            if (chat == null) return;

            // Private chats let you choose; in groups/channels unpin needs pin rights (the RPC enforces it).
            bool isPrivate = chat.Peer is InputPeerUser;
            bool forEveryone;
            if (isPrivate)
            {
                int choice = ThemedDialog.Show(this, "Unpin message",
                    "Unpin this message?", "Unpin for everyone", "Unpin for me", "Cancel");
                if (choice != 0 && choice != 1) return;
                forEveryone = choice == 0;
            }
            else
            {
                int choice = ThemedDialog.Show(this, "Unpin message",
                    "Unpin this message for everyone?", "Unpin", "Cancel");
                if (choice != 0) return;
                forEveryone = true;
            }

            try
            {
                await _service.UnpinMessageAsync(chat.Peer, msgId, forEveryone);
                if (_selectedChat != chat) return;
                RemovePinLocally(msgId);
                if (_pinnedListForm != null && !_pinnedListForm.IsDisposed) _pinnedListForm.SetPins(BuildPinnedRows());
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Unpin failed", ex.Message, "OK"); }
        }

        /// <summary>Removes a pin from the local set and keeps the bar/index in sync after an unpin.</summary>
        private void RemovePinLocally(int msgId)
        {
            if (_pinnedMessages == null) return;
            int idx = _pinnedMessages.FindIndex(m => m.ID == msgId);
            if (idx < 0) return;
            _pinnedMessages.RemoveAt(idx);
            if (_pinnedMessages.Count == 0) _pinnedIndex = 0;
            else
            {
                if (_pinnedIndex > idx) _pinnedIndex--;          // keep pointing at the same logical pin
                _pinnedIndex %= _pinnedMessages.Count;           // and if it was the last/current, wrap to a valid one
            }
            UpdatePinnedBar();
        }

        private async void DeleteMessage(Control bubble, Message msg)
        {
            // 0 = for everyone (revoke), 1 = for me only, 2 = cancel.
            int choice = ThemedDialog.Show(this, "Delete message",
                "Delete this message?", "For everyone", "Just for me", "Cancel");
            if (choice != 0 && choice != 1) return;
            bool revoke = choice == 0;

            try
            {
                await _service.DeleteMessagesAsync(_selectedChat.Peer, new[] { msg.ID }, revoke);
                _messagePanel.Controls.Remove(bubble);
                bubble.Dispose();
                _currentChatMessages.RemoveAll(x => x.ID == msg.ID);
                _shownMessageIds.Remove(msg.ID);
                if (_replyTarget != null && _replyTarget.ID == msg.ID) CancelReply();
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Delete failed", "Couldn't delete: " + ex.Message, "OK");
            }
        }

        /// <summary>Re-uploads a previously failed media send, reusing the same (failed) bubble.</summary>
        private async void RetrySend(MessageBubbleControl bubble)
        {
            if (_selectedChat == null || _service.Client == null) return;
            if (!_failedSends.TryGetValue(bubble, out var info)) return;

            _failedSends.Remove(bubble);
            int tempId = bubble.MessageId;                      // still the negative placeholder id
            if (tempId < 0) _pendingBubbles[tempId] = bubble;   // re-track for echo reconciliation
            bubble.Failed = false;
            bubble.Pending = true;
            bubble.Invalidate();

            try
            {
                var msg = await MediaSender.SendAsync(_service.Client, _selectedChat.Peer,
                    info.path, info.mode, info.caption, null, System.Threading.CancellationToken.None);

                // Swap in place unless the live echo already reconciled it (TrySwapPendingBubble).
                if (bubble.MessageId < 0)
                {
                    _pendingBubbles.Remove(tempId);
                    _shownMessageIds.Remove(tempId);
                    if (msg != null)
                    {
                        _shownMessageIds.Add(msg.ID);
                        bubble.MessageId = msg.ID;
                        if (!_currentChatMessages.Any(x => x.ID == msg.ID)) _currentChatMessages.Add(msg);
                    }
                    bubble.Pending = false;
                    bubble.Invalidate();
                }
            }
            catch (Exception)
            {
                if (bubble.MessageId < 0) _pendingBubbles.Remove(bubble.MessageId);
                bubble.Pending = false;
                bubble.Failed = true;
                _failedSends[bubble] = info;
                bubble.Invalidate();
            }
        }

        /// <summary>Forwards a message to a chat picked from a themed dialog.</summary>
        private async void ForwardMessage(Message msg)
        {
            if (_selectedChat == null) return;

            List<ChatEntry> targets;
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }
            int ok = await ForwardToTargets(_selectedChat.Peer, new[] { msg.ID }, targets);
            ThemedDialog.Show(this, ok > 0 ? "Forwarded" : "Forward failed", ForwardResultText(ok, targets.Count), "OK");
        }

        // ── Multi-select ─────────────────────────────────────────────────────────

        private void OnBubbleSelectionToggled(object sender, EventArgs e)
        {
            var b = (MessageBubbleControl)sender;
            ToggleSelection(b.MessageId, b);
        }

        private void OnVoiceSelectionToggled(object sender, EventArgs e)
        {
            var v = (VoiceBubbleControl)sender;
            ToggleSelection(v.MessageId, v);
        }

        /// <summary>Enters selection mode and selects the message the menu was opened on.</summary>
        private void EnterSelectionMode(int firstId)
        {
            _selectionMode = true;
            _selectedMessageIds.Clear();
            if (firstId > 0) _selectedMessageIds.Add(firstId);

            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.SelectionMode = true; b.Selected = _selectedMessageIds.Contains(b.MessageId); b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.SelectionMode = true; v.Selected = _selectedMessageIds.Contains(v.MessageId); v.Invalidate(); }
            }

            ShowSelectionBar(true);
            UpdateSelectionCount();
        }

        private void ExitSelectionMode()
        {
            _selectionMode = false;
            _selectedMessageIds.Clear();
            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.SelectionMode = false; b.Selected = false; b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.SelectionMode = false; v.Selected = false; v.Invalidate(); }
            }
            ShowSelectionBar(false);
        }

        private void ToggleSelection(int messageId, Control ctl)
        {
            if (messageId <= 0) return;
            bool nowSelected;
            if (_selectedMessageIds.Contains(messageId)) { _selectedMessageIds.Remove(messageId); nowSelected = false; }
            else { _selectedMessageIds.Add(messageId); nowSelected = true; }

            if (ctl is MessageBubbleControl b) b.Selected = nowSelected;
            else if (ctl is VoiceBubbleControl v) v.Selected = nowSelected;
            ctl.Invalidate();

            if (_selectedMessageIds.Count == 0) { ExitSelectionMode(); return; }
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            if (_selectionBar != null) _selectionBar.Count = _selectedMessageIds.Count;
        }

        private void ShowSelectionBar(bool show)
        {
            if (_selectionBar == null) return;
            int scrollY = _messagePanel != null ? -_messagePanel.AutoScrollPosition.Y : 0;
            _selectionBar.Visible = show;
            _rightLayout.RowStyles[3].Height = show ? 48 : 0;
            if (_messagePanel != null) _messagePanel.AutoScrollPosition = new Point(0, scrollY);
        }

        private void ThemeSelectionBar()
        {
            if (_selectionBar != null) { _selectionBar.AccentColor = _accent; _selectionBar.IsDark = _dark; }
            if (_recordingBar != null) _recordingBar.IsDark = _dark;
        }

        /// <summary>Forwards all selected messages to a chat picked from the themed picker.</summary>
        private async void ForwardSelected()
        {
            if (_selectedChat == null || _selectedMessageIds.Count == 0) return;

            List<ChatEntry> targets;
            using (var picker = new ForwardPickerDialog(_allChats, _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChats.Count == 0) return;
                targets = picker.SelectedChats;
            }

            var ids = _selectedMessageIds.OrderBy(x => x).ToArray();   // chronological order
            int ok = await ForwardToTargets(_selectedChat.Peer, ids, targets);
            if (ok > 0) ExitSelectionMode();
            ThemedDialog.Show(this, ok > 0 ? "Forwarded" : "Forward failed",
                ok == 0 ? "Couldn't forward — make sure your VPN is on."
                        : (ids.Length + (ids.Length == 1 ? " message" : " messages") + " forwarded to " + ok + (ok == 1 ? " chat." : " chats.")), "OK");
        }

        // ── Emoji picker ─────────────────────────────────────────────────────────

        private void OpenEmojiPicker()
        {
            if (_selectedChat == null) return;
            var picker = new EmojiPicker(_service, _dark, _accent);
            picker.Picked += InsertEmoji;
            picker.DocumentPicked += SendDocument;
            var p = _emojiButton.PointToScreen(new Point(0, 0));
            picker.Location = new Point(p.X - picker.Width + _emojiButton.Width, p.Y - picker.Height - 4); // above the button
            picker.Show(this);
            picker.Activate();
        }

        /// <summary>Sends a picked sticker/GIF document to the open chat (optimistic bubble + chat list).</summary>
        private async void SendDocument(Document doc)
        {
            if (_selectedChat == null || _service.Client == null || doc == null) return;
            var chat = _selectedChat;
            try
            {
                var msg = await _service.SendDocumentAsync(chat.Peer, doc);
                if (msg != null)
                {
                    if (_selectedChat == chat && _shownMessageIds.Add(msg.ID))
                    {
                        _currentChatMessages.Add(msg);
                        _messagePanel.Controls.Add(MakeMessageBubble(chat, msg, null));
                        ScrollMessagesToBottom();
                    }
                    UpdateChatListForMessage(chat.PeerId, msg, true);
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Send", "Couldn't send: " + ex.Message, "OK");
            }
        }

        private void InsertEmoji(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return;
            int caret = _messageInput.SelectionStart;
            string t = _messageInput.Text ?? "";
            if (caret < 0 || caret > t.Length) caret = t.Length;
            _messageInput.Text = t.Insert(caret, emoji);
            _messageInput.SelectionStart = caret + emoji.Length;
            _messageInput.Focus();
        }

        // ── Voice recording ──────────────────────────────────────────────────────

        /// <summary>Mic-button click: start → (record) → stop-to-ready → (ready) → discard.</summary>
        private void OnMicClick()
        {
            switch (_voiceState)
            {
                case VoiceState.None: StartRecording(); break;
                case VoiceState.Recording: StopToReady(); break;
                case VoiceState.Ready: DiscardVoice(); break;
            }
        }

        private void StartRecording()
        {
            if (_selectedChat == null || _service.Client == null) return;
            _recorder = new VoiceRecorder();
            _recorder.Finished += OnVoiceFinished;
            try
            {
                _recorder.Start();
            }
            catch (Exception ex)
            {
                _recorder = null;
                ThemedDialog.Show(this, "Microphone", "Couldn't start recording:\n" + ex.Message, "OK");
                return;
            }
            _voiceState = VoiceState.Recording;
            _recordingBar.DotColor = Color.FromArgb(229, 57, 53);
            _recordingBar.Caption = "Recording…   0:00";
            ApplyVoiceUi();
            _recordTimer.Start();
        }

        /// <summary>Stops recording and moves to the Ready state — does NOT send (Send button does).</summary>
        private void StopToReady()
        {
            _recordTimer.Stop();
            _recorder?.Stop();   // Finished fires (bg) → OnVoiceFinished sets Ready (or None if too short)
        }

        private void DiscardVoice()
        {
            TryDeleteFile(_pendingVoicePath);
            _pendingVoicePath = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
        }

        /// <summary>Aborts any in-progress recording/ready clip (chat switch, close).</summary>
        private void AbortVoice()
        {
            _recordTimer.Stop();
            if (_recorder != null && _recorder.IsRecording) _recorder.Cancel();
            TryDeleteFile(_pendingVoicePath);
            _pendingVoicePath = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
        }

        private void OnVoiceFinished(string path, int durationSec, byte[] waveform)
        {
            _recorder = null;
            if (IsDisposed) { TryDeleteFile(path); return; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (string.IsNullOrEmpty(path)) { _voiceState = VoiceState.None; ApplyVoiceUi(); return; }
                    _pendingVoicePath = path;
                    _pendingVoiceDur = durationSec;
                    _pendingVoiceWave = waveform;
                    _voiceState = VoiceState.Ready;
                    _recordingBar.DotColor = _accent;
                    _recordingBar.Caption = "Voice  " + FormatDuration(durationSec) + "   — tap Send";
                    ApplyVoiceUi();
                }));
            }
            catch { TryDeleteFile(path); }
        }

        /// <summary>Reflects the current voice state across the input-row controls.</summary>
        private void ApplyVoiceUi()
        {
            bool none = _voiceState == VoiceState.None;
            if (_recordingBar != null) _recordingBar.Visible = !none;
            if (_messageInput != null) _messageInput.Visible = none;
            if (_attachButton != null) { _attachButton.Enabled = none && _selectedChat != null; _attachButton.Invalidate(); }
            if (_micButton != null)
            {
                _micButton.Mode = _voiceState == VoiceState.Recording ? MicMode.Stop
                                : _voiceState == VoiceState.Ready ? MicMode.Discard
                                : MicMode.Mic;
                _micButton.Invalidate();
            }
            // Send is usable except while actively recording (nothing finalized yet).
            if (_sendButton != null) _sendButton.Enabled = _voiceState != VoiceState.Recording && _selectedChat != null;
        }

        /// <summary>Send button while a recorded clip is ready → sends it as a voice note.</summary>
        private void SendPendingVoice()
        {
            if (_voiceState != VoiceState.Ready || string.IsNullOrEmpty(_pendingVoicePath)) return;
            string path = _pendingVoicePath;
            int dur = _pendingVoiceDur;
            byte[] wave = _pendingVoiceWave;
            _pendingVoicePath = null;
            _pendingVoiceWave = null;
            _voiceState = VoiceState.None;
            ApplyVoiceUi();
            _ = SendVoice(path, dur, wave);
        }

        private async System.Threading.Tasks.Task SendVoice(string path, int durationSec, byte[] waveform)
        {
            if (string.IsNullOrEmpty(path)) return;          // cancelled / too short
            if (_selectedChat == null || _service.Client == null) { TryDeleteFile(path); return; }

            var chat = _selectedChat;
            try
            {
                var msg = await MediaSender.SendVoiceAsync(_service.Client, chat.Peer, path, durationSec, waveform,
                    System.Threading.CancellationToken.None);
                if (msg != null)
                {
                    // Pre-seed the audio cache so the bubble can play instantly (no re-download).
                    try
                    {
                        var d = (msg.media as MessageMediaDocument)?.document as Document;
                        if (d != null)
                        {
                            MediaCache.EnsureFolder(AppSettings.Instance.MediaCacheFolder);
                            File.Copy(path, AudioCachePath(d, false, null), true);
                        }
                    }
                    catch { }

                    if (_selectedChat == chat && _shownMessageIds.Add(msg.ID))
                    {
                        _currentChatMessages.Add(msg);
                        _messagePanel.Controls.Add(MakeMessageBubble(chat, msg, null));
                        ScrollMessagesToBottom();
                    }
                    UpdateChatListForMessage(chat.PeerId, msg, true);
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Voice message", "Couldn't send: " + ex.Message, "OK");
            }
            finally { TryDeleteFile(path); }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── Reply composer ───────────────────────────────────────────────────────

        private void StartReply(Message msg)
        {
            _editTarget = null;       // reply and edit are mutually exclusive
            _replyTarget = msg;
            UpdateReplyStrip();
            _messageInput.Focus();
        }

        /// <summary>Begins editing my message: fill the input with its text, show the edit strip.</summary>
        private void StartEdit(Message msg)
        {
            _replyTarget = null;
            _editTarget = msg;
            _messageInput.Text = msg.message ?? "";
            UpdateReplyStrip();
            _messageInput.Focus();
            try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
        }

        private void CancelReply()
        {
            _replyTarget = null;
            _editTarget = null;
            UpdateReplyStrip();
        }

        /// <summary>Shows/hides + (re)themes the reply/edit strip above the input.</summary>
        private void UpdateReplyStrip()
        {
            if (_replyStrip == null) return;
            bool active = _replyTarget != null || _editTarget != null;

            // Toggling the strip's row resizes the message panel, which resets AutoScroll;
            // preserve the offset like UpdateMiniBar does.
            int scrollY = _messagePanel != null ? -_messagePanel.AutoScrollPosition.Y : 0;

            if (active)
            {
                if (_editTarget != null)
                {
                    _replyEditing = true;
                    _replyPreview = "Editing message";
                }
                else
                {
                    string preview = GetDisplayText(_replyTarget);
                    if (preview.Length > 90) preview = preview.Substring(0, 90) + "…";
                    _replyEditing = false;
                    _replyPreview = preview;
                }

                _replyStrip.BackColor = _dark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(232, 232, 235);
                _replyStrip.Invalidate();   // owner-drawn (DrawReplyStrip) — repaint with the new glyph/preview
                _replyCancelBtn.BackColor = _replyStrip.BackColor;
                _replyCancelBtn.ForeColor = _dark ? Color.FromArgb(210, 210, 210) : Color.FromArgb(90, 90, 90);
                _replyCancelBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
            }

            _replyStrip.Visible = active;
            _rightLayout.RowStyles[5].Height = active ? 40 : 0;
            if (_messagePanel != null)
                _messagePanel.AutoScrollPosition = new Point(0, scrollY);
        }

        // ── Attachments ─────────────────────────────────────────────────────────
        // Pending-bubble visual kind (chosen from the SendMediaDialog mode).

        private enum AttachKind { Image, Video, Audio, Document }

        private void OpenAttachmentDialog()
        {
            if (_selectedChat == null) return;
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Attach files", Filter = "All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                // Same real send path as drag-and-drop: SendMediaDialog (compress / file).
                OnFilesDropped(dlg.FileNames);
            }
        }

        /// <summary>
        /// Creates an optimistic, PENDING outgoing bubble for a local file — mirroring the
        /// text-send path (add bubble, mark sending, reserve a temp id in _shownMessageIds).
        /// The drag-drop send path swaps these via _pendingBubbles on the server-confirmed id.
        /// Returns the bubble so callers can track it for the swap.
        /// </summary>
        private MessageBubbleControl AddPendingBubble(string path, AttachKind kind, string caption)
        {
            string name = Path.GetFileName(path);
            long size = SafeFileSize(path);
            // The caption is rendered like a real media caption (under the thumbnail / as
            // bubble text), so the optimistic bubble matches the post-reload appearance.
            // No caption → empty: the thumbnail/tile/card already conveys the media type.
            string cap = caption ?? "";

            int tempId = _nextTempMessageId--;          // -1, -2, -3, …
            _shownMessageIds.Add(tempId);               // dedupe path treats it uniformly

            MessageBubbleControl bubble;
            switch (kind)
            {
                case AttachKind.Image:
                {
                    var thumb = LoadDownscaledThumb(path, 480, 480, out int sw, out int sh);
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    if (thumb != null)
                    {
                        _attachmentThumbs.Add(thumb); // we own these (the photo cache doesn't)
                        bubble.ConfigurePhoto(sw, sh, MessageBubbleControl.PhotoState.Loaded);
                        bubble.SetImage(thumb);
                    }
                    else
                    {
                        // Unreadable image → fall back to a file card.
                        bubble.IsFile = true; bubble.FileName = name;
                        bubble.FileSizeText = DrawHelper.FormatSize(size); bubble.Measure();
                    }
                    break;
                }

                case AttachKind.Video:
                    // Generic video tile (gray area + play overlay). No VLC / frame extraction.
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    bubble.IsVideoThumb = true;
                    bubble.ConfigurePhoto(320, 200, MessageBubbleControl.PhotoState.Placeholder);
                    break;

                case AttachKind.Audio:
                default: // Audio + Document → file card (name + size; ext-colored GDI+ icon).
                    bubble = CreateBubble(cap, null, true, DateTime.UtcNow, tempId);
                    bubble.IsFile = true;
                    bubble.FileName = name;
                    bubble.FileSizeText = DrawHelper.FormatSize(size);
                    bubble.Measure();
                    break;
            }

            bubble.Pending = true;
            _messagePanel.Controls.Add(bubble);
            _pendingBubbles[tempId] = bubble;
            return bubble;
        }

        // ── Drag-and-drop send (Phase 4 Part 2) ─────────────────────────────────

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = (_selectedChat != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            if (_selectedChat == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                OnFilesDropped(paths);
        }

        private void OnFilesDropped(string[] paths)
        {
            if (_selectedChat == null || _service.Client == null || paths == null || paths.Length == 0)
                return;

            var dlg = new SendMediaDialog(_service.Client, _selectedChat.Peer, paths);
            var byToken = new Dictionary<int, MessageBubbleControl>();
            var infoByToken = new Dictionary<int, (string path, SendMode mode, string caption)>();
            var albumPendingByToken = new Dictionary<int, MessageBubbleControl>();

            // Optimistic bubble per file (negative tempId), swapped to the real id on success.
            dlg.FileStarting += (token, path, mode, caption) =>
            {
                var kind = mode == SendMode.Photo ? AttachKind.Image
                         : mode == SendMode.Video ? AttachKind.Video
                         : AttachKind.Document;
                var bubble = AddPendingBubble(path, kind, caption);
                byToken[token] = bubble;
                infoByToken[token] = (path, mode, caption);
                ScrollMessagesToBottom();
            };
            dlg.FileSucceeded += (token, msg) =>
            {
                if (!byToken.TryGetValue(token, out var b)) return;
                // If the live echo already reconciled this bubble (TrySwapPendingBubble),
                // its id is now the real (positive) one — nothing left to do.
                if (b.MessageId >= 0) return;

                int tempId = b.MessageId;                 // negative placeholder id
                _pendingBubbles.Remove(tempId);
                _shownMessageIds.Remove(tempId);
                if (msg != null)
                {
                    _shownMessageIds.Add(msg.ID);         // dedupe the (possibly later) echo
                    b.MessageId = msg.ID;
                    if (!_currentChatMessages.Any(x => x.ID == msg.ID))
                        _currentChatMessages.Add(msg);    // so the viewer/playlist see it pre-reload
                    if (_selectedChat != null)
                        UpdateChatListForMessage(_selectedChat.PeerId, msg, true); // refresh the row now
                }
                else b.MessageId = 0;
                b.Pending = false;
                b.Invalidate();
            };
            dlg.FileFailed += (token, err) =>
            {
                if (!byToken.TryGetValue(token, out var b)) return;
                _pendingBubbles.Remove(b.MessageId);
                b.Pending = false;
                b.Failed = true;
                b.Invalidate();
                if (infoByToken.TryGetValue(token, out var info))
                    _failedSends[b] = info;   // remember the source so Retry can re-upload
            };

            // Album send: one pending album bubble while uploading; resolved to the real album on send.
            dlg.AlbumSending += (token0, apaths, caption) =>
            {
                var bubble = CreateBubble(caption ?? "", null, true, DateTime.UtcNow, 0);
                bubble.Pending = true;
                bubble.BeginAlbum(false);
                for (int i = 1; i < apaths.Count; i++) bubble.AddAlbumItem(-(1000000 + token0 * 100 + i), false);   // gray placeholder tiles
                _messagePanel.Controls.Add(bubble);
                albumPendingByToken[token0] = bubble;
                ScrollMessagesToBottom();
            };
            dlg.AlbumSent += (token0, msgs) =>
            {
                MessageBubbleControl pend;
                if (albumPendingByToken.TryGetValue(token0, out pend) && pend != null && !pend.IsDisposed)
                { _messagePanel.Controls.Remove(pend); pend.Dispose(); }
                albumPendingByToken.Remove(token0);
                if (msgs == null || _selectedChat == null) return;
                foreach (var msg in msgs)
                {
                    MessageBubbleControl created;
                    HandleAlbumItem(_selectedChat, msg, null, null, out created);   // builds/merges the real album bubble
                    if (created != null) _messagePanel.Controls.Add(created);
                    if (!_currentChatMessages.Any(x => x.ID == msg.ID)) _currentChatMessages.Add(msg);
                }
                if (msgs.Length > 0) UpdateChatListForMessage(_selectedChat.PeerId, msgs[msgs.Length - 1], true);
                ScrollMessagesToBottom();
            };
            dlg.AlbumFailed += (token0, err) =>
            {
                MessageBubbleControl pend;
                if (albumPendingByToken.TryGetValue(token0, out pend) && pend != null && !pend.IsDisposed)
                { pend.Pending = false; pend.Failed = true; pend.Invalidate(); }
            };

            using (dlg) dlg.ShowDialog(this);
        }

        private static long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        /// <summary>
        /// Loads a downscaled thumbnail from a local image file. Only the small bitmap is
        /// retained — the full-resolution source is decoded transiently and disposed
        /// immediately (no full-res image kept in memory; ARM32-safe). Returns null on failure.
        /// </summary>
        private static Image LoadDownscaledThumb(string path, int maxW, int maxH, out int srcW, out int srcH)
        {
            srcW = srcH = 0;
            try
            {
                using (var src = Image.FromFile(path))
                {
                    srcW = src.Width; srcH = src.Height;
                    double scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
                    if (scale > 1) scale = 1; // never upscale the source
                    int w = Math.Max(1, (int)(src.Width * scale));
                    int h = Math.Max(1, (int)(src.Height * scale));
                    var thumb = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(thumb))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                    return thumb;
                }
            }
            catch { return null; }
        }

        private static IPeerInfo SenderInfo(Messages_MessagesBase history, Message m)
        {
            return m.from_id != null ? history.UserOrChat(m.from_id) : null;
        }

        // ── Display text / media placeholders ────────────────────────────────

        private static string GetDisplayText(Message m)
        {
            if (!string.IsNullOrEmpty(m.message)) return m.message; // text or media caption
            return MediaPlaceholder(m.media);
        }

        /// <summary>Preview text for any message union — service events render their system text.</summary>
        private string GetDisplayText(MessageBase mb)
        {
            if (mb is MessageService svc) return ServiceText(svc) ?? "";
            return mb is Message m ? GetDisplayText(m) : "";
        }

        private static DateTime MsgDate(MessageBase mb)
            => mb is Message m ? m.date : (mb is MessageService s ? s.date : default(DateTime));

        private static string MediaPlaceholder(MessageMedia media)
        {
            switch (media)
            {
                case null: return "";
                case MessageMediaPhoto _: return "📷 Photo";
                case MessageMediaGeoLive _: return "📍 Live location"; // derives from Geo — keep first
                case MessageMediaVenue _: return "📍 Venue";           // derives from Geo — keep first
                case MessageMediaGeo _: return "📍 Location";
                case MessageMediaContact _: return "👤 Contact";
                case MessageMediaPoll _: return "📊 Poll";
                case MessageMediaDice _: return "🎲 Dice";
                case MessageMediaDocument mdoc: return DocumentPlaceholder(mdoc.document as Document);
                default: return "[media]";
            }
        }

        private static string DocumentPlaceholder(Document doc)
        {
            if (doc == null) return "📎 File";
            var attrs = doc.attributes ?? new DocumentAttribute[0];

            if (attrs.Any(a => a is DocumentAttributeSticker)) return "🎭 Sticker";
            if (attrs.Any(a => a is DocumentAttributeAnimated)) return "🎞 GIF";

            var audio = attrs.OfType<DocumentAttributeAudio>().FirstOrDefault();
            if (audio != null)
                return (audio.flags & DocumentAttributeAudio.Flags.voice) != 0 ? "🎤 Voice message" : "🎵 Audio";

            if (attrs.Any(a => a is DocumentAttributeVideo)) return "🎥 Video";

            var fn = attrs.OfType<DocumentAttributeFilename>().FirstOrDefault();
            return fn != null ? "📎 File: " + fn.file_name : "📎 File";
        }

        private static string DisplayName(User user)
        {
            var name = string.Join(" ", new[] { user.first_name, user.last_name }
                .Where(s => !string.IsNullOrEmpty(s))).Trim();
            if (!string.IsNullOrEmpty(name)) return name;
            if (!string.IsNullOrEmpty(user.MainUsername)) return "@" + user.MainUsername;
            return "User " + user.id;
        }

        private void OpenSettings()
        {
            // SettingsForm edits and persists AppSettings.Instance directly on OK; the service powers Devices.
            using (var dlg = new SettingsForm(_service))
                dlg.ShowDialog(this);
        }

        // ── Tray icon + notifications ────────────────────────────────────────────

        /// <summary>Creates the tray icon (idempotent). Called once the session is authorized.</summary>
        private void SetupTray()
        {
            if (_notifyIcon != null) return;

            _trayIconNormal = TryLoadIcon("trayicon.ico");           // distinct from official Telegram; deployed beside the exe
            _trayIconUnread = TryLoadIcon("trayicon-unread.ico");    // shown while any chat has unread

            _trayMenu = new ThemedContextMenuStrip();
            AddMenuItem(_trayMenu, "Open TelegArm", RestoreFromTray);
            _trayMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(_trayMenu, "Exit", ExitApp);

            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIconNormal ?? Icon ?? SystemIcons.Application,
                Text = "TelegArm",
                Visible = true,
                ContextMenuStrip = _trayMenu   // Windows' NATIVE tray right-click trigger — reliable (the manual MouseUp+Show showed nothing)
            };
            // The only NotifyIcon+ContextMenuStrip quirk is foreground: while the main window is VISIBLE the
            // menu could misbehave (the native path foregrounds the NotifyIcon's invisible helper window, which
            // doesn't hold activation). Correct it WITHOUT losing the native trigger — foreground the app just
            // before the menu opens. No-op when hidden (SFW can't foreground an invisible window), where the
            // native path already worked. Worst case: back to the old behavior (works hidden/minimized), never broken.
            _trayMenu.Opening += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] menu opening");
                try { SetForegroundWindow(Handle); } catch { }
            };
            // Tray diagnostics + touch fixes (COMPOSER-CLOSEOUT): left-tap = one-directional restore (never hides,
            // so an accidental tap can't lose the window); right-click menu foregrounds ITSELF once shown so the
            // first touch tap lands on the menu instead of flying to the desktop (RT log: item taps died
            // reason=AppFocusChange with fg-class=Progman ~1s after open).
            _notifyIcon.MouseUp += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] icon click button=" + e.Button + " windowVisible=" + Visible);
                if (e.Button == MouseButtons.Left)
                {
                    System.Diagnostics.Debug.WriteLine("[TRAY] left-tap restore");
                    try
                    {
                        if (!Visible || WindowState == FormWindowState.Minimized) RestoreFromTray();
                        else { Activate(); BringToFront(); }
                    }
                    catch { }
                }
            };
            _trayMenu.Opened += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] menu opened");
                try { SetForegroundWindow(_trayMenu.Handle); } catch { }   // the MENU's handle — not the form's
            };
            _trayMenu.Closed += (s, e) => System.Diagnostics.Debug.WriteLine("[TRAY] menu closed reason=" + e.CloseReason);
            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
            _notifyIcon.BalloonTipClicked += OnBalloonClicked;
            UpdateTrayTooltip();
        }

        /// <summary>Hides the window to the tray (falls back to minimize if there's no tray yet).</summary>
        private void HideToTray()
        {
            if (_notifyIcon == null) { WindowState = FormWindowState.Minimized; return; }
            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitApp()
        {
            _reallyClosing = true;
            Close();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // A user-initiated close (X / Alt+F4) hides to the tray instead of exiting —
            // but only once the tray exists and we're not genuinely closing (Exit/logout).
            if (!_reallyClosing && _notifyIcon != null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;   // tray-hide = backgrounded; the presence timer sends offline after 30s
            }
            // PRESENCE 1.2: best-effort offline on a REAL close — bounded wait, never blocks
            // shutdown (800ms cap) and never fights teardown (_tearingDown discipline).
            if (_presenceOnline && !TelegramService.TearingDown)
            {
                _presenceOnline = false;
                try { _service?.UpdateStatusAsync(true).Wait(800); } catch { }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] sent offline (app close)");
            }
        }

        /// <summary>Updates the tray tooltip with the total unread count.</summary>
        private void UpdateTrayTooltip()
        {
            if (_notifyIcon == null) return;
            int unread = 0;
            foreach (var c in _allChats) unread += Math.Max(0, c.UnreadCount);
            _notifyIcon.Text = unread > 0 ? "TelegArm — " + unread + " unread" : "TelegArm";
            // Swap the tray icon on the 0↔nonzero boundary (unread → unread icon; none → normal). Only assign
            // when it changes (avoids redundant handle churn). Falls back gracefully if an icon is missing.
            var want = unread > 0 ? (_trayIconUnread ?? _trayIconNormal) : _trayIconNormal;
            if (want != null && !ReferenceEquals(_notifyIcon.Icon, want)) _notifyIcon.Icon = want;
        }

        /// <summary>Loads an .ico from beside the exe (RT-safe: null if missing/unreadable, never throws).</summary>
        private static Icon TryLoadIcon(string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (System.IO.File.Exists(path)) return new Icon(path);
            }
            catch { }
            return null;
        }

        // NOTIFY-FIX 2.2: a message that reached the gate once is DECIDED — re-delivery (getDifference
        // replay on the flappy VPN link, stale-state re-attach) must never re-decide it. FIFO-capped.
        private readonly HashSet<(long, int)> _toastSeen = new HashSet<(long, int)>();
        private readonly Queue<(long, int)> _toastSeenOrder = new Queue<(long, int)>();
        private const int ToastSeenCap = 500;

        /// <summary>One [NOTIFY] line per gate decision (Logger.Enabled-gated; nothing per tick).</summary>
        private static void NotifyLog(string what, long peerId, int msgId, string reason)
        {
            if (!Logger.Enabled) return;
            System.Diagnostics.Debug.WriteLine("[NOTIFY] " + what + " peer=" + peerId + " msg=" + msgId
                + (reason != null ? " reason=" + reason : ""));
        }

        /// <summary>THE notify gate: own → dup → focused → mute (explicit, then category), then emit.
        /// Every decision is logged; the (peer,msg) key is recorded on FIRST sight so a replayed message
        /// can never notify twice in a process lifetime (NOTIFY-FIX).</summary>
        private void MaybeToast(long peerId, Message m, bool outgoing)
        {
            if (m == null || _notifyIcon == null) return;
            if (!AppSettings.Instance.EnableNotifications) return;                 // master switch off (user choice, not logged)
            if (outgoing) { NotifyLog("suppressed", peerId, m.ID, "own"); return; }

            var key = (peerId, m.ID);
            if (_toastSeen.Contains(key)) { NotifyLog("suppressed", peerId, m.ID, "dup"); return; }
            _toastSeen.Add(key);
            _toastSeenOrder.Enqueue(key);
            while (_toastSeenOrder.Count > ToastSeenCap) _toastSeen.Remove(_toastSeenOrder.Dequeue());

            if (_isForeground && _selectedChat != null && _selectedChat.PeerId == peerId)
            { NotifyLog("suppressed", peerId, m.ID, "focused"); return; }

            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            string muteReason;
            if (IsEffectivelyMuted(entry, out muteReason))
            { NotifyLog("suppressed", peerId, m.ID, muteReason); return; }

            string title = entry?.Title ?? "TelegArm";
            string text = GetDisplayText(m);
            if (string.IsNullOrEmpty(text)) text = "New message";
            if (text.Length > 160) text = text.Substring(0, 160) + "…";

            _lastNotifiedPeerId = peerId;
            NotifyLog("emit", peerId, m.ID, null);
            try { _notifyIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.None); } catch { /* tray gone */ }
        }

        private async void OnBalloonClicked(object sender, EventArgs e)
        {
            RestoreFromTray();
            if (_lastNotifiedPeerId == 0) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == _lastNotifiedPeerId);
            if (entry != null && entry != _selectedChat) await OpenChat(entry, 0);
        }

        private void FallBackToLogin()
        {
            if (_fellBack) return;
            _service.Dispose();
            AuthManager.Reset();
            ShowLoginForm();
        }

        /// <summary>Hides MainForm and shows a fresh LoginForm; closing it ends the app.</summary>
        private void ShowLoginForm()
        {
            if (_fellBack) return;
            _fellBack = true;

            if (_notifyIcon != null) _notifyIcon.Visible = false; // no tray while on the login screen

            var login = new LoginForm(_service);
            login.FormClosed += (s, e) => { _reallyClosing = true; Close(); };
            Hide();
            login.Show();
        }
    }
}
