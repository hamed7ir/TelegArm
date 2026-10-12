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
    public partial class MainForm : MaterialForm
    {
        // MULTI-ACCOUNT (increment 2): _service is an INDIRECTION to the currently-active service, so a future switch
        // can REBIND the UI to a different (already-warm) service without rebuilding. Behavior-neutral today (one
        // service); all `_service.` sites read through this property.
        private TelegramService _activeService;
        private TelegramService _service => _activeService;
        private readonly MaterialSkinManager _skin;
        // AVATAR-PIPELINE: ONE download-once store behind every avatar surface (memory LRU → photo_id-keyed
        // disk files → single-flight bounded downloads with a visible-first backfill queue). Replaces the old
        // _avatarCache/_noAvatar pair — transient failures are retryable now, never marked "no avatar".
        private AvatarStore _avatars => _service.Avatars;   // MULTI-ACCOUNT: the ACTIVE service's per-instance store
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
        // QUICKWINS-1 PART 1: send our OWN typing (Messages_SetTyping), throttled by timestamp — no new timer (rides
        // composer TextChanged). _typingPeer = the peer we last told we're typing, so the explicit cancel targets it.
        private int _lastTypingTick;
        private InputPeer _typingPeer;
        private const int TypingThrottleMs = 5000;     // re-assert typing at most this often while entering text
        // COMMENTS-THREAD: when non-null, the history view is showing a channel post's comment thread. The three history
        // loaders page via GetReplies(GroupPeer, GroupRootId) — the linked-group peer + the thread root's GROUP-side id
        // (from GetDiscussionMessage), which scopes to THIS post's comments; back re-opens the channel. Live-append is
        // deferred while set (HandleIncomingMessage gates on it). Channel state is never mutated in place → back restores cleanly.
        private sealed class ThreadCtx
        {
            public InputPeer ChannelPeer;   // the BROADCAST channel — GetDiscussionMessage / ReadDiscussion / post routing
            public int PostMsgId;           // the channel post whose comments we're viewing
            public InputPeer GroupPeer;     // COMMENTS-THREAD-SCOPE: the linked DISCUSSION GROUP — the GetReplies target
            public int GroupRootId;         // COMMENTS-THREAD-SCOPE: the thread root's id IN THE GROUP (from disc.messages) — GetReplies msg_id
            public ChatEntry ReturnTo;      // the channel entry to re-open on back
            public int ReturnAnchorId;      // channel message to refocus on back (restores position)
            public ChatEntry GroupEntry;    // COMMENTS-JOIN-FLYOUT: the linked group entry (Peer + Channel w/ 'left' flag) — join source
        }
        private ThreadCtx _thread;

        // REPLIES-INBOX: the special "Replies" pseudo-chat (aggregates replies to your comments when you're NOT a
        // member of the discussion group). It comes down as a normal User dialog with this reserved id.
        private const long RepliesPeerId = 1271266957L;
        // Source discussion group per reply entry, resolved from the history dict at render time (keyed by the group
        // peer id) so "View in chat" can build an InputPeerChannel at tap time without depending on the manager cache.
        private readonly System.Collections.Generic.Dictionary<long, IPeerInfo> _repliesSourceCache
            = new System.Collections.Generic.Dictionary<long, IPeerInfo>();
        /// <summary>True while the Replies inbox itself is the open chat (NOT after navigating into a source thread).</summary>
        private bool IsRepliesInbox => _selectedChat != null && _selectedChat.PeerId == RepliesPeerId && _thread == null;
        private bool _fellBack;

        private Color _accent = Color.DodgerBlue;
        private bool _dark;

        // The official TelegArm channel — the drawer's "TelegArm Channel" row opens it in the chat view.
        private const string CHANNEL_USERNAME = "TelegArm_official";   // <-- set the real channel handle here (without @)

        private SplitContainer _split;
        private Button _hamburger;
        private Button _chatSearchBtn;   // INCHAT-SEARCH: magnifier in the chat header → search within the open chat
        private Button _chatMenuBtn;     // BATCH-TA-21/S1: the header ⋮ → the shared chat action menu

        // ── BATCH-TA-23/D1 — the right-side dock (shell only; panes are placeholders) ──
        /// <summary>Which pane the dock is showing. Emoji is only OFFERED where the composer accepts
        /// input — see <see cref="UpdateDockSources"/>.</summary>
        private enum DockPane { Info, Emoji }
        /// <summary>⚠ WIDENED FROM 280 IN TA-24. The pane now hosts the REAL ProfileForm, whose rows were
        /// designed against a 440-wide dialog; at 280 the content width fell to ~236 and long rows
        /// ("+98 936 590 0925 · Mobile", "125 shared links") crowded. 340 keeps the chat column usable at
        /// every width the app supports — measured: 960 → chat 319, 1366 → chat 725.</summary>
        private const int DockWidth = 340;
        private Panel _dock;                 // the Dock.Right strip inside _split.Panel2
        private Panel _dockBody;             // pane content host (placeholder for now)
        private Panel _dockTabs;
        private Label _dockInfoTab, _dockEmojiTab;
        private Button _dockBtn;             // header toggle
        private DockPane _dockPane = DockPane.Info;
        private EmojiPicker _dockEmoji;      // THE composer's panel, embedded — not a second grid
        private ProfileForm _dockProfile;    // THE profile, embedded — the Info pane has no separate content
        private long _dockProfilePeerId;     // which peer that profile is built for (0 = none)
        private MaterialTextBox2 _searchBox;
        private FlowLayoutPanel _chatListPanel;
        private MaterialLabel _chatTitle;
        private MaterialLabel _chatStatus;            // online / last seen / typing… subtitle
        // Chat header: the peer's circular avatar before the name + the metadata (subscribers/members/last-seen)
        // subtitle. Header uses the THEME background (accent reverted per user).
        private Panel _headerAvatar;                  // owner-drawn circular peer avatar at the header's left
        private Image _headerAvatarImg;               // current peer avatar (cache hit → paints now; null → initials)
        private string _headerAvatarTitle;            // for the initials fallback
        private long _headerAvatarPeerId;             // deterministic fallback color + async-load match guard
        private Font _captionFont;                    // "TelegArm" drawn into the slim status bar (BORDERLESS-CAPTION)
        private System.Windows.Forms.Timer _typingTimer;
        private bool _typing;
        private FlowLayoutPanel _messagePanel;
        private MaterialTextBox2 _messageInput;
        // COMPOSER-full-revert: the composer's inner native TextBox is left UNTOUCHED — no font-swap fields, no
        // reflection font manipulation (that work was reverted in case it broke WM_CHAR capture on RT). _baseTextBoxField
        // stays: it's used ONLY by the [KBD] diagnostics (HookKbdDiag), never to change the composer's font.
        private static readonly System.Reflection.FieldInfo _baseTextBoxField =
            typeof(MaterialTextBox2).GetField("baseTextBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private UI.Controls.SendButton _sendButton;   // TA-34/A: was a MaterialButton labelled "Send"
        private TableLayoutPanel _rightLayout;
        // FORUM-TOPICS: the topic chip bar (chat-panel row 4), shown only for forum groups; chips reuse FolderTabItem so
        // they match the folder tabs (theme/accent/selected-bubble) and recolor live by rebuild on theme change.
        private NoNativeScrollFlowPanel _forumTopicBar;
        private System.Collections.Generic.List<ForumTopic> _forumTopics;   // cached topics for the open forum (rebuild chips w/o re-fetch)
        private ChatEntry _currentForumEntry;   // the open forum group (null = not a forum / no bar)
        private int _selectedTopicId;            // 0 = All (flat all-topics history); else the selected topic id
        private TableLayoutPanel _composerBar;          // the normal "Write a message" row (input + attach + send)
        private UI.Controls.ThreadJoinBar _threadJoinBar;   // COMMENTS-JOIN-FLYOUT: above-composer join bar (thread, non-member)
        private bool _joinBarDismissed;                 // ✕ latch — reset on each thread open
        private ComposerFooterBar _footerBar;           // swapped-in footer for non-compose states
        private ComposerKind _footerKind = ComposerKind.Compose;
        private Panel _msgHost;                     // WrapWithScrollbar host (float parent for the jump button)
        private Panel _chatListHost;                // WrapWithScrollbar host for the chat list (float parent for the proxy pill)
        private ProxyStatusPill _proxyPill;         // BATCH-TA-16d — floating proxy chip over the chat list
        private DateFlyoutPill _dateFlyout;         // BUBBLE-DATETIME (C): floating day pill shown while scrolling
        private System.Windows.Forms.Timer _dateFlyoutTimer;
        private int _dateFlyoutScrollTick, _dateFlyoutCalcTick, _dateFlyoutTopSig = int.MinValue;   // TopSig = topmost-visible-bubble Y; a CHANGE = a genuine scroll (vs the 200ms _scrollWatch idle tick)
        // DATE-FLYOUT-TUNE: stay fully visible this long after the LAST scroll, THEN fade (a scroll resets it). Was ~850ms.
        private const int DateFlyoutHoldMs = 5000;
        // DATE-FLYOUT-TUNE: extra Y pushed down from the top edge so a future TOPIC bar at the top can coexist — the topic
        // bar sets this to its height (the setter is that hook) and the date pill sits below it. Default 0 = today's
        // top-edge position (Top = 8). A property (not a field) so it's a clean, assignable hook with no unused-field warning.
        private int DateFlyoutTopOffset { get; set; }
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
        // STORIES-BUILD-1: the story tray (avatars-with-rings) — a horizontal bar below the search row in BOTH
        // layout modes. Hidden (row height 0) until GetAllStoriesAsync finds peers with active stories.
        private NoNativeScrollFlowPanel _storyTrayBar;
        private TableLayoutPanel _storyTrayLayout;   // the layout that owns the tray row (tabbed=layout, sidebar=right); tray = row index 1 in both
        private const int StoryTrayHeight = 92;
        private List<StoryTrayEntry> _storyPeers = new List<StoryTrayEntry>();
        private string _storiesState;   // Stories_GetAllStories cache token (re-sent on refresh; may reply NotModified)
        private sealed class StoryTrayEntry { public long PeerId; public string Name; public bool Unseen; public IPeerInfo PeerInfo; public InputPeer Input; }
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
        private MaterialButton _proxyOverlayButton;   // BATCH-TA-16f/F1 — proxy route on the connecting overlay
        private MaterialButton _switchCancelButton;            // abort an in-flight account switch (restore the active account)
        private System.Threading.CancellationTokenSource _abortConnect;
        private bool _switchInProgress, _switchAborted;
        private bool _connectCorrupt;   // last connect failed because the session file is unreadable (permanent, not network)
        private readonly HashSet<long> _recoveryTried = new HashSet<long>();   // accounts already tried in the current corrupt-recovery chain (bounds it)
        private readonly HashSet<long> _recoveryRetried = new HashSet<long>(); // accounts given a CLEAN-RETRY this run (retry once, then move-aside — never delete)

        /// <summary>[RECOVERY] one-line diagnostic that SURVIVES Release (Trace, like [SESSPATH]) so the account-recovery
        /// decisions — clean-retry / kept / moved-aside — are visible in the installed build's log when logging is on.
        /// This path must NEVER auto-delete an account, and the log is how we PROVE that on-device.</summary>
        private static void LogRecovery(string line)
        {
            if (TelegArm.Helpers.Logger.Enabled) TelegArm.Helpers.Logger.Diag("[RECOVERY] " + line);
        }
        private System.Windows.Forms.Timer _connectingDots;
        private int _dotPhase;
        private System.Threading.CancellationTokenSource _retryNowCts;

        // Tray icon + notifications. The tray is created only once authorized; until then
        // (and when _reallyClosing is set by Exit/logout) the window closes normally.
        private NotifyIcon _notifyIcon;
        private ThemedContextMenuStrip _trayMenu;         // shown manually on right-click (see SetupTray) — foreground-safe in every window state
        private Icon _trayIconNormal, _trayIconUnread;   // unread-aware tray icons (loaded from beside the exe; null if missing)

        private Rectangle _savedBounds;
        private FormWindowState _savedState;
        private int _savedMinH;           // MinimumSize.Height saved while shrunk above the keyboard
        private bool _reallyClosing;
        private bool _isForeground = true;
        private long _lastNotifiedPeerId;
        private long _lastNotifiedAccountId;   // NOTIFY-BACKGROUND: which account the last toast was for (active/0 → click opens without switching)

        public MainForm(TelegramService service, bool startMinimized = false)
        {
            PerfLog.Boot("MainForm ctor ENTER");
            _activeService = service;
            AvatarStore.SetActive(_service.Avatars);   // MULTI-ACCOUNT: the active account's store is the ambient .Current
            _avatars.AvatarLoaded += OnAvatarLoaded;    // (_avatars ⇒ _service.Avatars — the active service's store)
            PeerTitleChanged += OnPeerTitleChanged;     // RELEASE-FIXES-V11 (H1): live row/header refresh on a rename
            // TA-27/W5: a clicked notification window routes here. Subscribed in the CTOR, not in the tray
            // setup, because the window channel deliberately does not depend on the tray existing. Static
            // event ⇒ it is unsubscribed in the shutdown path, same discipline as PeerTitleChanged above.
            NotificationStack.Clicked += OnNotificationActivated;
            // TA-37/A2 — an Action Center click lands on the SAME handler as a notification-window click,
            // so both end in OpenNotifiedChat. Not a second open-chat path.
            // ⚠ The COM call arrives on an RPC thread, so marshal; and it can arrive BEFORE this handler
            //   was attached (the app was launched BY the click), which is what DrainPending covers.
            ToastActivator.Activated += OnToastActivated;

            // BATCH-TA-0.1: the sub-stamps proved BuildUi is only ~120 ms — the bulk of the pre-network cold
            // start is HERE, between ctor entry and BuildUi. MaterialSkinManager.Instance is the singleton's
            // first touch (it loads its OWN embedded Roboto set); ApplyTheme + the icon decode follow.
            PerfLog.Boot("  ctor: before MaterialSkinManager.Instance");
            _skin = MaterialSkinManager.Instance;
            PerfLog.Boot("  ctor: MaterialSkinManager.Instance resolved");
            _skin.AddFormToManage(this);
            PerfLog.Boot("  ctor: AddFormToManage done");
            ApplyTheme();
            PerfLog.Boot("  ctor: ApplyTheme done");

            // Font scaling only; never Dpi/None. MaterialSkin.2 + system-DPI awareness.
            AutoScaleMode = AutoScaleMode.Font;

            Text = "TelegArm";   // taskbar / alt-tab identity — the tall title action bar that drew it is removed below
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { }
            PerfLog.Boot("  ctor: window icon decoded (ExtractAssociatedIcon)");
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            // BORDERLESS-CAPTION: drop MaterialForm's tall 40px title ACTION bar, keep the thin 24px STATUS bar that
            // holds min/max/close → a slim caption that just fits the buttons (reclaims ~40px of title height). Drag/
            // resize/snap stay in MaterialForm's WndProc; the title text no longer draws in the caption (still taskbar).
            FormStyle = MaterialForm.FormStyles.ActionBar_None;
            PerfLog.Boot("  ctor: FormStyle set (reflected, non-const)");
            // STARTUP-SETTING: a --startup (auto-launch-at-login) start is SILENT — minimized + off-taskbar, no full
            // window. OnLoad still fires (the form is shown, just minimized) so the session resumes + the tray icon
            // appears (SetupTray, post-auth); a tray click restores via RestoreFromTray (which re-enables the taskbar).
            if (startMinimized) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

            BuildUi();
            ApplyPanelColors();
            // BATCH-TA-0: splits the constructor's cost. BuildUi triggers FontHelper's static ctor (five embedded
            // TTFs registered with GDI+ AND gdi32) plus the whole control tree; RestorePhotoCacheIndex is a
            // SYNCHRONOUS Directory.GetFiles over Cache/{id}/media on the UI thread, unbounded in the cache size.
            // Both run before any window is shown, so the split decides which one is worth moving off-thread.
            PerfLog.Boot("BuildUi + ApplyPanelColors done → RestorePhotoCacheIndex");
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
                DisposeAllWarm();                     // STEP 1: close all background warm connections
                // SAVESTATE-DEADLOCK: persist pts/qts/seq/date for the next-launch resume, but OFF the UI thread and
                // BOUNDED — a reconnect-sync mid-close could hold WTC's state semaphore, and a direct UI-thread
                // SaveState would block/deadlock. Save on the pool, wait ≤1.5s, then close regardless (an unsaved state
                // just means a slightly larger getDifference next launch — never a hang).
                try { System.Threading.Tasks.Task.Run(() => _service.SaveUpdateState()).Wait(1500); } catch { }
                // RELEASE-LICENSE-V1: dispose the ACTIVE service AFTER the state save above — its Dispose fires
                // CancelAllDownloads("app-exit") (aborts in-flight downloads cleanly) + disposes the client. Warm
                // services were already disposed via DisposeAllWarm(); nothing below uses _service.
                try { _service?.Dispose(); } catch { }
                if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); _notifyIcon = null; }
                if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; }
                // TA-27: close any live notification windows and drop the static-event handler. A topmost
                // window outliving the app would be an orphan the user cannot get rid of.
                NotificationStack.Clicked -= OnNotificationActivated;
                ToastActivator.Activated -= OnToastActivated;   // TA-37: static event, same leak discipline
                try { NotificationStack.CloseAll(); } catch { }
                AudioPlayer.Shutdown();
                try { _recorder?.Dispose(); } catch { }
                _avatars.AvatarLoaded -= OnAvatarLoaded;
                PeerTitleChanged -= OnPeerTitleChanged;   // RELEASE-FIXES-V11: event-leak discipline (static event)
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
            PerfLog.Boot("MainForm ctor EXIT (BuildUi + RestorePhotoCacheIndex done)");
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

        /// <summary>BORDERLESS-CAPTION: the title ACTION bar is removed (FormStyle=ActionBar_None) so MaterialForm no
        /// longer draws the app name. Redraw "TelegArm" into the slim STATUS bar (left side, clear of the min/max/close
        /// on the right) after the base chrome paints. Padding.Top == the status-bar height (DPI-scaled).</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);   // MaterialForm draws the status bar + window buttons
            int h = Padding.Top;   // ACTION bar removed → Padding.Top is exactly the status-bar height
            if (h <= 1) return;
            if (_captionFont == null) _captionFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            var rect = new Rectangle(12, 0, Math.Max(0, Width - 96), h);   // left; the buttons sit on the right
            TextRenderer.DrawText(e.Graphics, "TelegArm", _captionFont, rect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

    }
}
