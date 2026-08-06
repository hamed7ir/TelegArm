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
        private MaterialButton _sendButton;
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
        // KBD-DISMISS-BLUR 0.2(d): DWM cloak state — the touch-keyboard window can be DWM-CLOAKED (effectively
        // hidden) while IsWindowVisible still reads TRUE and the rect stays on-screen. Nonzero == cloaked ==
        // dismissed. Guarded at the call site: if dwmapi is unavailable on RT we fall back to the rect checks.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        private const int DWMWA_CLOAKED = 14;
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
        // KBD-DISMISS-BLUR: on OSK dismissal, blur the composer so Windows has no focused field to re-summon for.
        private Control _kbFocusSink;         // neutral SELECTABLE non-text sink; BlurForDismiss parks focus here (v3)
        private const int KbArmedTickMs = 120;         // poll cadence while the keyboard is up (armed) — catch the re-summon fast
        private const int KbIdleTickMs = 350;          // poll cadence when idle (the original interval)
        // KBD-DISMISS-v3.1: post-dismiss STATE suppression, gated on input PROVENANCE (RT log proved a real tap is a
        // WM_POINTERDOWN originId=hardware, while every spurious OS/TabTip re-focus is a dev0/org0 injected click).
        // While suppressed, bounce EVERY composer/search focus back to the sink UNTIL a genuine hardware tap on the
        // box — no time bound, so the 3-5s async re-focus can't slip through as the cooldown let it.
        private bool _kbSuppressed;                     // true from a dismiss until a genuine hardware tap re-engages the box
        private int _lastRealEditTouchTick;            // TickCount of the last HARDWARE-origin pointer-down on a composer/search EDIT
        private const int RealTouchWindowMs = 1500;    // a genuine tap's pointer-down lands within this of the focus it triggers
        // KBD-CLOSE-PROBE (instrument only): capture the foreground-✕ moment. _ttLast dedups the per-tick TabTip state
        // dump (log on CHANGE, not 120ms spam); _lastComposerKeyTick separates "actively typing" from "idle keyboard up".
        private string _ttLast = "";
        private int _lastComposerKeyTick;
        private bool _ttCloakedPrev;   // KBD-CLOAK-BLUR: last tick's TabTip cloak state, to detect the False→True dismiss edge
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
            Controls.Add(_split);
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

            _searchBox = new MaterialTextBox2
            {
                Hint = "Search",
                Dock = DockStyle.Fill,
                Margin = new Padding(2, 12, 10, 6)
            };
            _searchBox.TextChanged += (s, e) => OnSearchTextChanged();
            // First MaterialTextBox2 in the process — MaterialSkin resolves its own font stack here, so this
            // rung isolates that from our control-tree construction.
            PerfLog.Boot("    BuildLeftPanel: first MaterialTextBox2 constructed");

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

            // ── BATCH-TA-16d — the floating proxy pill over the chat list ────────────────────────────
            // D1 PARENT: _chatListHost, the Panel that WrapWithScrollbar returns — NOT _chatListPanel.
            // This is the same choice the jump-to-bottom button already makes (":983 _msgHost … a child of
            // the host, not the scrolling panel"): a child of the FlowLayoutPanel would scroll away with the
            // rows and be clipped by its client area. The host also excludes the ThemedScrollBar strip
            // (docked Right), so anchoring Left keeps the pill clear of the bar in both layout modes —
            // sidebar-rail and folder-bar — because both wrap the SAME host.
            // Anchored Bottom|Left so the splitter and form resizes leave it where it belongs.
            if (_chatListHost != null)
            {
                _proxyPill = new ProxyStatusPill { IsDark = _dark, AccentColor = _accent, Visible = false };
                _proxyPill.Click += (s, e) => OpenProxySettings();
                _chatListHost.Controls.Add(_proxyPill);
                _proxyPill.BringToFront();
                _proxyPill.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                // D2: the pill is a SIBLING of the scroll surface, so TouchScroller's parent-walk would find
                // nothing above it and the list would stop scrolling under the chip — for the wheel AND for
                // a touch pan. Declare what it covers so both pass through.
                TouchScroller.MapOverlayTo(_proxyPill, _chatListPanel);
                // VISIBILITY (not just caption) depends on the shared state now that Connecting/Failed
                // show even without a proxy, so MainForm must re-evaluate on every transition — the pill's
                // own subscription only updates what it PAINTS, never whether it is shown.
                ProxyStatus.Changed += OnProxyStatusChangedForVisibility;
                HandleDestroyed += (s, e) => { try { ProxyStatus.Changed -= OnProxyStatusChangedForVisibility; } catch { } };
                _chatListHost.Resize += (s, e) => PositionProxyPill();
                PositionProxyPill();
                RefreshProxyPill();
            }

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
            var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            _chatTitle = new MaterialLabel
            {
                Text = "Select a chat",
                Dock = DockStyle.Fill,
                FontType = MaterialSkinManager.fontType.H6,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Cursor = Cursors.Hand,
                // HEADER-FIT: long/bold channel names must stay on ONE line, ellipsis-truncated inside the title
                // cell (which already stops short of the right-side transfers indicator) — never wrap or clip down
                // into the pinned bar below. AutoSize off + single-line auto-ellipsis; MaterialLabel derives from
                // Label, so the base TextRenderer EndEllipsis|SingleLine painting applies (NoPrefix keeps Persian).
                AutoSize = false,
                AutoEllipsis = true
            };
            _chatStatus = new MaterialLabel
            {
                Text = "",
                Dock = DockStyle.Fill,
                FontType = MaterialSkinManager.fontType.Caption,
                TextAlign = ContentAlignment.MiddleLeft,
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
            // ⚠ ACCENT COMES FROM ThemeHelper VIA _accent. MaterialSkinManager's Accent slot is NEVER
            //   written — it is one app-wide singleton and writing it re-poisons every other form
            //   (§2d / LESSONS_LEARNED.md:163).
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
            //   hidden until a chat is open. MaterialSkinManager's Accent slot is NEVER written (§2d).
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
                // SEND-ENTITIES: desktop formatting shortcuts — wrap the selection in the markdown marker that
                // becomes a MessageEntity on send (Ctrl+B bold, Ctrl+I italic, Ctrl+K link).
                else if (e.Control && e.KeyCode == Keys.B) { e.SuppressKeyPress = true; WrapComposerSelection("**", "**"); }
                else if (e.Control && e.KeyCode == Keys.I) { e.SuppressKeyPress = true; WrapComposerSelection("__", "__"); }
                else if (e.Control && e.KeyCode == Keys.K) { e.SuppressKeyPress = true; InsertComposerLink(); }
            };
            _messageInput.TextChanged += OnComposerTextChanged;   // QUICKWINS-1 PART 1: send our typing (throttled; inert when not typing)
            // COMPOSER-full-revert: the composer is now the PLAIN, known-good MaterialTextBox2 — its inner native
            // TextBox is UNTOUCHED (no reflection font-swap, no EmojiInputPainter overpaint), reverted in case any of
            // that manipulation broke WM_CHAR capture on RT. It shows MaterialSkin's default font (not Vazirmatn) and
            // system/monochrome emoji; the rest of the app keeps Vazirmatn. Only the Enter-to-send KeyDown (above) and
            // the [KBD] diagnostics (below) remain wired to it.
            HookKbdDiag(_messageInput, "composer");   // [KBD] focus/keystroke diagnostics (KEEP)
            HookKbdDiag(_searchBox, "search");

            // SEND-ENTITIES (formatting toolbar): a themed context menu on the composer — select text → tap a format
            // to wrap it in the markdown marker that's parsed to a MessageEntity on send. Setting ContextMenuStrip is a
            // safe property assignment (no inner-TextBox reflection/overpaint, per the COMPOSER-full-revert note above).
            var fmtMenu = new ThemedContextMenuStrip();
            fmtMenu.Items.Add("Bold").Click += (s, e) => WrapComposerSelection("**", "**");
            fmtMenu.Items.Add("Italic").Click += (s, e) => WrapComposerSelection("__", "__");
            fmtMenu.Items.Add("Strikethrough").Click += (s, e) => WrapComposerSelection("~~", "~~");
            fmtMenu.Items.Add("Monospace").Click += (s, e) => WrapComposerSelection("`", "`");
            fmtMenu.Items.Add("Link…").Click += (s, e) => InsertComposerLink();
            fmtMenu.Items.Add(new ToolStripSeparator());
            fmtMenu.Items.Add("Paste").Click += (s, e) => PasteIntoComposer();
            _messageInput.ContextMenuStrip = fmtMenu;

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

            // BATCH-TA-16d/D5 — recolor the proxy pill on the SAME live path. The jump button shipped a bug
            // where it was themed at CONSTRUCTION ONLY, so a theme flip left it stale; that is exactly why
            // this belongs here and not in BuildLeftPanel.
            if (_proxyPill != null)
            {
                _proxyPill.IsDark = _dark;
                _proxyPill.AccentColor = _accent;   // ThemeHelper/ApplyTheme accent — never MaterialSkinManager's Accent slot
                _proxyPill.Invalidate();
            }
            // Chat header uses the theme background (accent reverted); repaint the avatar on theme/accent change.
            if (_headerAvatar != null) _headerAvatar.Invalidate();
            // BUBBLE-DATETIME: the floating date pill is accent-driven — refresh its accent live.
            if (_dateFlyout != null) { _dateFlyout.Accent = _accent; _dateFlyout.IsDark = _dark; _dateFlyout.Invalidate(); }
        }

        /// <summary>ProxyStatus is static and can be raised from a background continuation — marshal.</summary>
        private void OnProxyStatusChangedForVisibility()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)RefreshProxyPill); } catch { /* handle gone */ }
        }

        /// <summary>BATCH-TA-16d/D1 — bottom-LEFT of the chat-list host, clear of the themed scrollbar
        /// (docked Right) and of the paging trigger zone at the very bottom.</summary>
        private void PositionProxyPill()
        {
            if (_proxyPill == null || _chatListHost == null) return;
            _proxyPill.Left = 12;
            _proxyPill.Top = Math.Max(0, _chatListHost.ClientSize.Height - _proxyPill.Height - 12);
        }

        /// <summary>BATCH-TA-16d/D3+D4 — decides whether the pill is on screen at all.
        ///
        /// D4 VISIBILITY: on MainForm it appears ONLY when a proxy is configured. Permanent chrome over the
        /// chat list would be clutter for the majority who never use one. (LoginForm is the opposite: there
        /// it is ALWAYS shown, because someone who cannot connect has no other way to reach the setting.)
        ///
        /// D3 COEXISTENCE — the pill DEFERS to the existing "Connecting…" overlay. MainForm already has one
        /// connection indicator, and two of them disagreeing is worse than either alone: the overlay would
        /// say "Waiting for network" while the pill still said "Proxy connected" from the previous session.
        /// So while the overlay is up it owns connection state entirely and the pill hides; once the overlay
        /// goes away, the pill reports the PROXY dimension only — which the overlay never covered.
        /// The two can therefore never contradict each other, because they are never both on screen.</summary>
        private void RefreshProxyPill()
        {
            if (_proxyPill == null) return;
            bool proxied = false;
            try { proxied = AppSettings.Instance.ActiveProxyUrl != null; } catch { }
            var st = ProxyStatus.State;
            bool overlayUp = _connectingPanel != null && !_connectingPanel.IsDisposed && _connectingPanel.Visible;

            // The pill is a CONNECTION indicator, not merely a proxy badge (revised after field feedback):
            //   · Connecting / Failed → ALWAYS show, proxy or not. A mid-session drop on a DIRECT
            //     connection previously produced no visible signal at all except a window-title change,
            //     so the user just saw a chat list that had quietly stopped updating.
            //   · Connected VIA A PROXY → show (worth stating).
            //   · Connected directly (state Off) → hide. That is the healthy default and permanent chrome
            //     over the chat list would be clutter for the majority who never use a proxy.
            bool show = (st == ProxyConnectState.Connecting || st == ProxyConnectState.Failed || proxied)
                        && !overlayUp;
            if (_proxyPill.Visible != show) _proxyPill.Visible = show;
            if (show) { PositionProxyPill(); _proxyPill.BringToFront(); }
        }

        /// <summary>Opens the shared ProxyForm — the SAME form the login screen's pill opens (TA-15/X7:
        /// one form, two doors). Applying a change to the already-connected client and the warm pool is
        /// deliberately NOT done here; that is TA-16c.</summary>
        private async void OpenProxySettings()
        {
            bool changed = false;
            try
            {
                using (var dlg = new ProxyForm(_service)) { dlg.ShowDialog(this); changed = dlg.ConnectionSettingsChanged; }
            }
            catch (Exception ex) { Logger.Diag("[PROXY] settings form failed: " + ex.Message); }   // never echoes a link

            ProxyStatus.Reset();     // new proxy → fresh grace period for the failure clock
            RefreshProxyPill();
            if (changed) await ApplyProxyChangeAsync();
        }

        /// <summary>BATCH-TA-17 — make a proxy change take effect NOW, on the running app.
        ///
        /// Previously the setting only reached the NEXT client, so switching proxies (or switching to
        /// direct) left the app connected through the old one — a proxy the user had just watched fail
        /// stayed in use, which is the opposite of what the UI implied.
        ///
        /// ORDER MATTERS AND IS DELIBERATE:
        ///   1. WARM POOL FIRST, AWAITED. Warm clients were built with the OLD transport. Leaving them
        ///      would mean a later account switch silently moves the user between proxied and direct —
        ///      the "half-proxied state" this design has warned about since TA-15/X7. The AWAITED
        ///      DisposeWarmServiceAsync is used, never the sync DisposeWarmService: the sync one does not
        ///      wait for the socket abort or the session-handle release, and dropping a warm client whose
        ///      handle is still releasing is the documented account-loss race (ACCOUNT-RECOVERY-SAFETY
        ///      Bug 1). Tear them down BEFORE the active client reconnects so the two can't overlap.
        ///   2. Then the ACTIVE client, via the existing ForceReconnectAsync.
        ///   3. Then re-warm. WarmOthersAsync is idempotent and skips active/already-warm, and the
        ///      rebuilt clients pick up the new transport through ApplyProxyTo in CreateWarmClientAsync.
        ///
        /// ⚠ THIS IS THE DANGER ZONE (HANDOFF §5.10/§5.11). A proxy change is now a deliberate way to
        ///   enter the warm-teardown-then-rebuild window — the same window as Bug 3b. It is INSTRUMENTED,
        ///   not fixed: the [SESSPATH] probes already log every client-open with its path, so a
        ///   same-path double-open with no teardown between is visible in the log. Verify on the device
        ///   that no [RECOVERY] or [ACCT] DELETE line appears and both account dirs survive.</summary>
        private async System.Threading.Tasks.Task ApplyProxyChangeAsync()
        {
            string via = "(direct)";
            try { var u = AppSettings.Instance.ActiveProxyUrl; via = u == null ? "(direct)" : ProxyUrl.SafeForLog(u); } catch { }
            Logger.Diag("[PROXY] APPLY-LIVE start → " + via + "  warm=" + _warm.Count);

            ProxyStatus.NoteAttempt();   // the pill says "Connecting…" for the whole swap
            RefreshProxyPill();

            try
            {
                // 1 — warm pool, awaited, before anything reconnects.
                var warm = _warm.Values.ToList();
                _warm.Clear();
                foreach (var svc in warm)
                {
                    try { await svc.DisposeWarmServiceAsync(); }
                    catch (Exception ex) { Logger.Diag("[PROXY] APPLY-LIVE warm teardown failed: " + ex.Message); }
                }
                Logger.Diag("[PROXY] APPLY-LIVE warm pool torn down (" + warm.Count + ")");

                // 2 — the active client.
                if (_service != null) await _service.ApplyProxyChangeAsync();
                Logger.Diag("[PROXY] APPLY-LIVE active client reconnected → " + via);
            }
            catch (Exception ex)
            {
                Logger.Diag("[PROXY] APPLY-LIVE FAILED: " + ex.Message);   // never echoes a link
            }

            // 3 — re-warm with the new transport (fire-and-forget, as at startup).
            try { var _ = WarmOthersAsync(); } catch { }
            RefreshProxyPill();
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
                EnsureFocusSink();   // KBD-DISMISS-v3 PART 1: the neutral parking control must exist BEFORE any dismiss
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
                if (KbdDiag && (_kbActive || textFocused)) LogKbSignals(showing ? "shown" : "gone");   // 0.2 raw-signal dump while armed
                if (LogOn && (_kbActive || textFocused)) LogTabTipTick();   // KBD-CLOSE-PROBE: TabTip state (change-detected) around ✕

                // KBD-CLOAK-BLUR: the EARLIEST dismiss signal on RT is TabTip becoming DWM-cloaked (the ✕/close), and it
                // fires while the composer STILL holds focus. Blur at THIS edge — ahead of the HIDE/restore path below —
                // so the field is unfocused before the OS can re-summon (what an Explorer-defocus does, but earlier and
                // without leaving the app). The HIDE branch still runs for the resize; its own blur is then a no-op. Keyed
                // on the RAW cloak edge + live focus (not the `showing` composite), so it fires even if `showing` lags.
                bool ttCloaked = _kbActive && IsTabTipCloaked();
                if (ttCloaked && !_ttCloakedPrev
                    && ((_messageInput != null && _messageInput.ContainsFocus) || (_searchBox != null && _searchBox.ContainsFocus)))
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] cloak dismiss → pre-emptive blur (composer still focused)");
                    _kbSuppressed = true; _lastRealEditTouchTick = 0;   // arm suppression exactly as the HIDE dismiss branch does
                    BlurForDismiss();                                    // park on the sink NOW, pre-empting the re-summon
                }
                _ttCloakedPrev = ttCloaked;
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
                    // KBD-DISMISS-v3.1: the keyboard went away while shrunk. Park focus on the neutral non-text sink FIRST
                    // (so the restore can't re-pick the composer as the active control), THEN restore. Enter SUPPRESSION:
                    // until a genuine hardware tap on the box, every composer/search refocus (the OS/TabTip re-summon,
                    // dev0/org0) is bounced back to the sink — state-based, so the unbounded 3-5s async re-focus can't slip.
                    _kbSuppressed = true; _lastRealEditTouchTick = 0;   // stale tap can't authorize a reshow
                    if ((_messageInput != null && _messageInput.ContainsFocus)
                        || (_searchBox != null && _searchBox.ContainsFocus))
                        BlurForDismiss();          // park focus on the sink — the OSK has no text field to re-summon for
                    RestoreFromKeyboard();         // reclaim the space, AFTER the park
                }

                // Adaptive cadence (1.1): poll fast ONLY while the keyboard is up (armed), slow when idle — not a new
                // always-on high-freq timer (freeze-era discipline holds). Set only on change to avoid restart churn.
                int wantInterval = _kbActive ? KbArmedTickMs : KbIdleTickMs;
                if (_kbTimer != null && _kbTimer.Interval != wantInterval) _kbTimer.Interval = wantInterval;
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
                if (IsCloaked(h)) return false;   // 0.2(d): DWM-cloaked == effectively hidden (a dismiss signal on 8.1)
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

        /// <summary>0.2(d): true if the window is DWM-cloaked (hidden by the shell while still "visible"). Guarded —
        /// if dwmapi/the attribute is unavailable (RT), returns false so detection falls back to the rect checks.</summary>
        private static bool IsCloaked(IntPtr hwnd)
        {
            try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0; }
            catch { return false; }
        }

        /// <summary>KBD-CLOAK-BLUR: is the TabTip touch-keyboard window currently DWM-cloaked? The EARLIEST dismiss
        /// signal on RT — the ✕/close cloaks it a beat before it fully hides, while the composer still holds focus.
        /// Cheap (one FindWindow + attribute read); used to blur pre-emptively before the OS can re-summon.</summary>
        private static bool IsTabTipCloaked()
        {
            try { IntPtr h = FindWindow("IPTip_Main_Window", null); return h != IntPtr.Zero && IsCloaked(h); }
            catch { return false; }
        }

        /// <summary>KBD-DISMISS-BLUR instrument (KbdDiag-gated): dumps the raw 0.2 dismiss signals + the composite
        /// each armed poll, so the RT log reveals WHICH signal actually flips when the keyboard closes. Read-only.</summary>
        private void LogKbSignals(string tag)
        {
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);
                bool exists = h != IntPtr.Zero;
                bool vis = exists && IsWindowVisible(h);
                bool cloaked = exists && IsCloaked(h);
                int top = 0, bot = 0, ht = 0; bool onScr = false;
                if (exists && GetWindowRect(h, out NativeRect r))
                {
                    var scr = Screen.FromControl(this).Bounds;
                    top = r.Top; bot = r.Bottom; ht = r.Bottom - r.Top;
                    onScr = ht >= 150 && r.Top < scr.Bottom - 80 && r.Bottom > scr.Bottom - 80;
                }
                double occH = 0;
                try { if (WinRtKeyboard.TryOccludedRect(out double _, out double _, out double _, out double oh)) occH = oh; } catch { }
                bool composite = exists && !cloaked && onScr;   // the TabTip "shown" predicate (occludedH>0 covers the InputPane path)
                System.Diagnostics.Debug.WriteLine("[KBD] sig " + tag + " exists=" + exists + " vis=" + vis + " cloaked=" + cloaked
                    + " rect=(" + top + ".." + bot + " h=" + ht + ") onScreen=" + onScr + " occludedH=" + occH.ToString("0")
                    + " composite=" + composite);
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3 PART 1: create the neutral, non-text focus sink EAGERLY and idempotently. It is the
        /// UNCONDITIONAL parking spot for BlurForDismiss — SELECTABLE (so ActiveControl can rest on it and WinForms
        /// restores IT, not the composer, after RestoreFromKeyboard re-maximizes) but NON-TEXT, so it can never summon
        /// the touch keyboard. Off-screen + inert.</summary>
        private void EnsureFocusSink()
        {
            try
            {
                if (_kbFocusSink != null && !_kbFocusSink.IsDisposed) return;
                // A Button IS selectable (ActiveControl can rest on it); a Panel is not. TabStop keeps it a valid
                // active-control target even if WinForms ever falls back to SelectNextControl during the restore.
                _kbFocusSink = new Button { Size = new Size(1, 1), Left = -100, Top = -100, TabStop = true };
                Controls.Add(_kbFocusSink);
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3 PART 1: on OSK dismissal, park focus UNCONDITIONALLY on the neutral non-text sink —
        /// so Windows has no focused text field to re-summon the keyboard for, AND so when RestoreFromKeyboard
        /// re-maximizes, WinForms restores the SINK (not the composer) as the active control. That severs the reshow
        /// loop at its root: the composer is never re-focused, so TabTip never fires the synthetic caret-click that
        /// made v2's focus-gate signals (real vs synthetic) identical. Logs where focus landed (the RT proof).</summary>
        private void BlurForDismiss()
        {
            try
            {
                if (IsDisposed) return;
                EnsureFocusSink();
                if (_kbFocusSink != null && !_kbFocusSink.IsDisposed) { try { ActiveControl = _kbFocusSink; } catch { } }
                else ActiveControl = null;   // sink unavailable → the old release primitive (best-effort)
                if (LogOn)
                {
                    Control fc = ActiveControl;
                    string who = fc == null ? "null" : (string.IsNullOrEmpty(fc.Name) ? fc.GetType().Name : fc.Name + "(" + fc.GetType().Name + ")");
                    System.Diagnostics.Debug.WriteLine("[KBD] keyboard dismissed → blur (parked=" + ReferenceEquals(fc, _kbFocusSink) + "); focus now = " + who);
                }
            }
            catch { }
        }

        /// <summary>KBD-DISMISS-v3.1: a HARDWARE-origin pointer-down (real finger/mouse, provenance originId=hardware —
        /// NOT the dev0/org0 injected click the OS/TabTip fires) landed on a composer/search EDIT. Records it so a
        /// following focus-gain during post-dismiss suppression is recognised as a genuine re-tap and allowed.</summary>
        private void NoteRealEditTouch() { _lastRealEditTouchTick = Environment.TickCount; }

        /// <summary>KBD-DISMISS-v3.1 (the fix): EditInputProbe calls this on a composer/search WM_SETFOCUS. When NOT
        /// suppressed, inert (normal tap-to-type). While SUPPRESSED (after a dismiss), the focus is allowed ONLY if a
        /// genuine hardware tap on the box landed within RealTouchWindowMs — that ends suppression; otherwise it's the
        /// OS/TabTip re-summon (no hardware provenance) and is bounced back to the sink. State-based, not timed, so the
        /// unbounded 3-5s async re-focus is caught the same as the instant one. Deferred so the pointer-down registers
        /// first; re-checks at run.</summary>
        private void OnTextBoxFocusGain(Control box, string name)
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (IsDisposed || box == null || box.IsDisposed || !box.ContainsFocus) return;
                        if (Helpers.TouchKeyboard.HasHardwareKeyboard()) return;   // hardware kbd: no OSK to fight
                        if (!_kbSuppressed) return;                                 // normal entry — allow the keyboard
                        int since = unchecked(Environment.TickCount - _lastRealEditTouchTick);
                        bool genuineTap = _lastRealEditTouchTick != 0 && since >= 0 && since <= RealTouchWindowMs;
                        if (genuineTap)
                        {
                            _kbSuppressed = false;   // a real finger re-engaged the box → allow the keyboard, end suppression
                            if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] suppression cleared: " + name + " genuine hardware tap " + since + "ms");
                        }
                        else
                        {
                            if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] suppressed refocus bounced: " + name + " (no hardware tap; lastRealTouch=" + (_lastRealEditTouchTick == 0 ? "none" : since + "ms") + ")");
                            BlurForDismiss();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
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
            private const int WM_POINTERDOWN = 0x0246, WM_TOUCH = 0x0240;   // KBD-v3 INSTRUMENT: real-finger arrivals at the EDIT
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
            // KBD-DISMISS-v3 INSTRUMENT: provenance of the last pointer/touch that reached THIS EDIT — read at the
            // next WM_SETFOCUS to label the focus a real finger tap (originId=hardware) vs the OS/TabTip synthetic
            // re-focus (injected/system, or NO pointer at all) that re-summons the keyboard 3-5s after a dismiss.
            private int _lastPtrTick;
            private string _lastPtrLabel = "none";

            public EditInputProbe(Control edit, string name, Form owner) { _edit = edit; _name = name; _owner = owner; }

            // Gated write (belt) — hot call sites ALSO wrap with `if (LogOn)` so the argument string is never
            // even built when logging is off (braces — the true hot-path rule).
            private void Log(string s) { if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] " + _name + " " + s); }

            /// <summary>KBD-DISMISS-v3 INSTRUMENT: decode the CURRENT input message's provenance — device (touch/mouse/
            /// pen) + origin (hardware=real finger; injected/system=OS/TabTip synthetic) + sent/posted. The decisive
            /// read for telling a genuine composer tap from the re-focus neither v2 nor v3 could discriminate.</summary>
            private static string InputSrc(out bool hardware)
            {
                hardware = false;
                try
                {
                    INPUT_MESSAGE_SOURCE s;
                    if (GetCurrentInputMessageSource(out s))
                    {
                        hardware = s.originId == 1;   // IMO_HARDWARE — a real finger/mouse, not an injected/synthetic re-focus
                        string dev = s.deviceType == 4 ? "touch" : s.deviceType == 2 ? "mouse" : s.deviceType == 8 ? "pen"
                                   : s.deviceType == 1 ? "kbd" : s.deviceType == 16 ? "touchpad" : "dev" + s.deviceType;
                        string org = s.originId == 1 ? "hardware" : s.originId == 2 ? "injected" : s.originId == 4 ? "system" : "org" + s.originId;
                        return dev + "/" + org + (InSendMessage() ? "/sent" : "/posted");
                    }
                }
                catch { }
                return "src?";
            }

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

                // KBD-DISMISS-v3.1: record pointer/touch arrivals at this EDIT + provenance. A HARDWARE-origin one is a
                // real tap → NoteRealEditTouch re-allows focus during suppression; the dev0/org0 injected re-focus does
                // not. (PTR line kept for the RT read.)
                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK || m.Msg == WM_POINTERDOWN || m.Msg == WM_TOUCH)
                {
                    string kind = m.Msg == WM_LBUTTONDOWN ? "LBDOWN" : m.Msg == WM_LBUTTONDBLCLK ? "LBDBLCLK"
                                : m.Msg == WM_POINTERDOWN ? "POINTERDOWN" : "TOUCH";
                    bool hardware; _lastPtrTick = Environment.TickCount; _lastPtrLabel = kind + " " + InputSrc(out hardware);
                    if (hardware) ((MainForm)_owner).NoteRealEditTouch();   // genuine finger/mouse → re-allow composer focus
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] PTR " + _name + " " + _lastPtrLabel);
                }
                if (m.Msg == WM_CHAR) ((MainForm)_owner)._lastComposerKeyTick = Environment.TickCount;   // KBD-CLOSE-PROBE: idle-vs-typing

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
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] RAW " + _name + " WM_SETFOCUS otherHwnd=0x" + m.WParam.ToInt64().ToString("X")
                        + " lastPtr=" + (_lastPtrTick == 0 ? "never" : unchecked(Environment.TickCount - _lastPtrTick) + "ms[" + _lastPtrLabel + "]") + " srcNow=" + InputSrc(out _));
                    _needStyleLog = true;
                    base.WndProc(ref m);
                    ((MainForm)_owner).OnTextBoxFocusGain(_edit, _name);   // KBD-DISMISS-v3 PART 3: re-park a post-dismiss cooldown refocus
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

        /// <summary>KBD-CLOSE-PROBE (instrument only): the class of the current foreground window ("0"/"?" on failure).</summary>
        private static string FgClass()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return "0";
                var sb = new System.Text.StringBuilder(96);
                GetClassName(fg, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>KBD-CLOSE-PROBE (instrument only): dump the TabTip window's visual state + who holds focus each armed
        /// tick, but ONLY when it CHANGES (no 120ms spam). Around a foreground ✕ tap this shows whether TabTip
        /// moves/shrinks/cloaks BEFORE our HIDE detection and whether the composer still holds focus at that instant —
        /// i.e. is there an early signal we could hook to blur pre-emptively before the OS re-summons. No behavior change.</summary>
        private void LogTabTipTick()
        {
            if (!Logger.Enabled) return;   // hot-path: zero window-probing when logging is off
            try
            {
                IntPtr tt = FindWindow("IPTip_Main_Window", null);
                bool exists = tt != IntPtr.Zero;
                bool vis = exists && IsWindowVisible(tt);
                bool cloaked = exists && IsCloaked(tt);
                NativeRect r = new NativeRect();
                if (exists) GetWindowRect(tt, out r);
                bool composerFocus = _messageInput != null && _messageInput.ContainsFocus;
                bool searchFocus = _searchBox != null && _searchBox.ContainsFocus;
                bool typing = _lastComposerKeyTick != 0 && unchecked(Environment.TickCount - _lastComposerKeyTick) < 1200;
                string state = "vis=" + vis + " cloaked=" + cloaked
                    + " rect=" + r.Left + "," + r.Top + "," + (r.Right - r.Left) + "x" + (r.Bottom - r.Top)
                    + " fg=" + FgClass() + " composerFocus=" + composerFocus + " searchFocus=" + searchFocus + " typing=" + typing;
                if (state != _ttLast) { _ttLast = state; System.Diagnostics.Debug.WriteLine("[KBD] tabtip-tick " + state); }
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

        // ── BATCH-TA-23/D1 — THE RIGHT-SIDE DOCK (shell only) ────────────────────────────────────
        /// <summary>Builds the dock as a Dock.Right strip INSIDE _split.Panel2.
        ///
        /// ⚠ WHY Panel2 AND NOT A NESTED SplitContainer. A nested splitter cannot open on a narrow
        /// window: the outer mins are already Panel1MinSize 240 + Panel2MinSize 360, and an inner
        /// Panel2MinSize for the dock would push the smallest workable width past this form's
        /// MinimumSize of 720 — so on the RT device the dock could never open at all. A docked strip has
        /// no nested min-size arithmetic.
        /// ⚠ AND WHY IT IS ADDED AFTER THE FILL LAYOUT. Docking resolves LAST-ADDED FIRST (measured in
        /// TA-21: three Dock.Right buttons land left-to-right in ADD order), so this strip claims its
        /// width and the Fill layout takes the remainder. Nothing above it had to move.
        /// ⚠ BOTH LAYOUT MODES ARE UNAFFECTED. sidebar-rail and folder-bar only ever build Panel1; a
        /// Panel2-side dock is therefore ONE code path, not two. That is the whole reason for this shape.
        /// ⚠ SplitterDistance IS NOT TOUCHED — it stays hardcoded at 300 (there is no persistence for it
        /// today, and this batch adds none).</summary>
        private void BuildDock()
        {
            Color bg = _dark ? Color.FromArgb(34, 34, 37) : Color.FromArgb(246, 246, 248);
            Color line = _dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(222, 222, 228);

            _dock = new Panel { Dock = DockStyle.Right, Width = DockWidth, Visible = false, BackColor = bg };
            _dock.Paint += (s, e) =>
            {
                using (var p = new Pen(_dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(222, 222, 228)))
                    e.Graphics.DrawLine(p, 0, 0, 0, _dock.Height);   // 1px seam so it reads as its own column
            };

            // The Info pane is an EMBEDDED ProfileForm and the Emoji pane an EMBEDDED EmojiPicker; both
            // dock Fill into this body, so it owns no surface of its own.
            _dockBody = new Panel { Dock = DockStyle.Fill, BackColor = bg };


            _dockTabs = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = bg };
            _dockTabs.Paint += (s, e) =>
            {
                using (var p = new Pen(line)) e.Graphics.DrawLine(p, 0, _dockTabs.Height - 1, _dockTabs.Width, _dockTabs.Height - 1);
            };
            _dockInfoTab = MakeDockTab("Info", DockPane.Info);
            _dockEmojiTab = MakeDockTab("Emoji", DockPane.Emoji);
            _dockTabs.Controls.Add(_dockInfoTab);
            _dockTabs.Controls.Add(_dockEmojiTab);
            _dockTabs.Resize += (s, e) => LayoutDockTabs();

            _dock.Controls.Add(_dockBody);   // Fill added first → takes the remainder
            _dock.Controls.Add(_dockTabs);   // Top
            _split.Panel2.Controls.Add(_dock);
            LayoutDockTabs();
            SetDockPane(DockPane.Info);
        }

        private Label MakeDockTab(string text, DockPane pane)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Height = 37, Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter, Font = FontHelper.Ui(9.5f),
                BackColor = Color.Transparent
            };
            l.Click += (s, e) => SetDockPane(pane);
            l.Paint += (s, e) =>
            {
                if (_dockPane != pane) return;
                using (var p = new Pen(_accent, 2f))
                    e.Graphics.DrawLine(p, 6, l.Height - 2, l.Width - 6, l.Height - 2);   // accent underline
            };
            return l;
        }

        /// <summary>Lays the visible tabs out across the strip. A HIDDEN tab takes no space — the
        /// remaining one fills — because an unavailable source is OMITTED, not greyed (TA-20/S0).</summary>
        private void LayoutDockTabs()
        {
            if (_dockTabs == null) return;
            var shown = new List<Label>();
            if (_dockInfoTab != null && _dockInfoTab.Visible) shown.Add(_dockInfoTab);
            if (_dockEmojiTab != null && _dockEmojiTab.Visible) shown.Add(_dockEmojiTab);
            if (shown.Count == 0) return;
            int w = _dockTabs.ClientSize.Width / shown.Count;
            for (int i = 0; i < shown.Count; i++)
            {
                shown[i].Left = i * w;
                shown[i].Width = i == shown.Count - 1 ? _dockTabs.ClientSize.Width - i * w : w;
                shown[i].Top = 0;
            }
            _dockTabs.Invalidate();
        }

        /// <summary>BATCH-TA-23/D3 — builds the Emoji pane ON FIRST USE, and it is THE COMPOSER'S PANEL:
        /// the same <see cref="EmojiPicker"/> type, with its Emoji / Stickers / GIFs tabs, its sticker-pack
        /// bar, its lazy loading and its caches. Not a second grid — a second grid is two things to fix
        /// every time either changes.
        /// Built lazily because it costs a catalog layout and (on the sticker/GIF tabs) network work, none
        /// of which should land in BuildUi's cold-start path.</summary>
        private void EnsureDockEmoji()
        {
            if (_dockEmoji != null || _dockBody == null || _service == null) return;
            var p = new EmojiPicker(_service, _dark, _accent, embedded: true)
            {
                TopLevel = false,                      // embed: it becomes an ordinary child control
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill
            };
            p.Picked += InsertEmoji;                   // the SAME handlers the popup uses
            p.DocumentPicked += SendDocument;
            _dockBody.Controls.Add(p);
            p.Show();                                  // a non-top-level form still needs this to lay out
            _dockEmoji = p;
        }

        private void SetDockPane(DockPane pane)
        {
            _dockPane = pane;
            if (pane == DockPane.Emoji) EnsureDockEmoji();
            if (_dockEmoji != null) _dockEmoji.Visible = pane == DockPane.Emoji;
            if (pane == DockPane.Info) EnsureDockProfile();
            if (_dockProfile != null) _dockProfile.Visible = pane == DockPane.Info;
            if (_dockInfoTab != null)
            {
                _dockInfoTab.ForeColor = pane == DockPane.Info ? _accent
                    : (_dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(105, 105, 112));
                _dockInfoTab.Invalidate();
            }
            if (_dockEmojiTab != null)
            {
                _dockEmojiTab.ForeColor = pane == DockPane.Emoji ? _accent
                    : (_dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(105, 105, 112));
                _dockEmojiTab.Invalidate();
            }
        }

        // ── BATCH-TA-24 — THE INFO PANE *IS* ProfileForm ──────────────────────────────────────────
        /// <summary>Hosts the REAL ProfileForm in the dock, for the open chat.
        ///
        /// ⚠ THIS REPLACED A HAND-BUILT "compact info pane" AND THAT WAS THE RIGHT CALL. The compact
        /// version showed an avatar, a name and a column of counts behind an "Open full profile" button —
        /// side by side with the reference it read as a stub, and it had already grown its own row painter,
        /// its own media-count fetch and its own phone/username rows. Every one of those was a second
        /// implementation of something ProfileForm already did properly. Now there is one profile, shown
        /// either as a modal or as a pane, and "Open full profile" is gone because there is nothing more to
        /// open.
        ///
        /// Rebuilt per PEER, not per call: constructing it runs LoadDetails (network), so a re-entry for
        /// the same chat is a no-op. Built only while the Info pane is actually visible.</summary>
        private void EnsureDockProfile()
        {
            if (_dockBody == null || _service == null) return;
            var entry = _selectedChat;
            if (entry == null) { DropDockProfile(); return; }
            if (_dockProfile != null && _dockProfilePeerId == entry.PeerId) return;

            DropDockProfile();
            // ⚠ contentWidth is the dock MINUS its own scrollbar strip: ProfileForm docks a ThemedScrollBar
            //   Right inside itself, and handing it the full width would overflow the flow by that strip and
            //   arm a horizontal scroll.
            var pf = new ProfileForm(_service, entry, GetCachedAvatar(entry.PeerId), DockWidth - 12)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill
            };
            pf.Avatars = _avatars;                          // member rows use the shared store
            pf.ForwardRequested += ForwardFromProfile;
            pf.ShowInChatRequested += ShowInChatFromProfile;
            pf.EmbeddedRoute += OnDockProfileRoute;         // instead of DialogResult + Close
            _dockBody.Controls.Add(pf);
            pf.Show();                                      // a non-top-level form still needs this to lay out
            _dockProfile = pf;
            _dockProfilePeerId = entry.PeerId;
        }

        private void DropDockProfile()
        {
            if (_dockProfile == null) return;
            try { _dockBody.Controls.Remove(_dockProfile); _dockProfile.Dispose(); } catch { }
            _dockProfile = null;
            _dockProfilePeerId = 0;
        }

        /// <summary>The embedded profile tapped something the HOST owns — a link, a mention, a hashtag, its
        /// personal-channel card, or it left the chat. Routed through the SAME RouteProfilePending the modal
        /// uses after ShowDialog, so the two modes cannot diverge. The pane does not close: ProfileForm
        /// clears its Pending* fields after this returns.</summary>
        private void OnDockProfileRoute(ProfileForm pf)
        {
            if (pf == null) return;
            RouteProfilePending(pf);
        }

        private void ToggleDock() { SetDockOpen(_dock == null || !_dock.Visible); }

        private void SetDockOpen(bool open)
        {
            if (_dock == null || _dock.Visible == open) return;
            _dock.Visible = open;
            if (open)
            {
                UpdateDockSources();      // may flip Emoji→Info if this chat can't be posted to
                // ⚠ THEN MATERIALISE THE PANE. This line is the fix for "a fresh run shows an empty Info
                //   pane until you visit Emoji or switch chats".
                //   UpdateDockSources only decides which TABS exist; it never builds pane CONTENT. And the
                //   two places that do build it had both already declined:
                //     · BuildDock ends with SetDockPane(Info), but at BuildUi time _selectedChat is null,
                //       so EnsureDockProfile drops and returns;
                //     · the chat-opened site only builds while the dock is VISIBLE, and on a fresh run it
                //       isn't open yet.
                //   So nothing had ever built the profile by the time the user first opened the dock.
                //   Re-applying the current pane is idempotent — EnsureDockProfile returns early when the
                //   peer is unchanged — and it routes through the one place that knows how to build either
                //   pane, rather than duplicating that decision here.
                SetDockPane(_dockPane);
            }
            if (_dockBtn != null) _dockBtn.Invalidate();   // the glyph carries the open/closed state
            if (Logger.Enabled)
                Logger.Diag("[DOCK] " + (open ? "opened" : "closed") + " w=" + _dock.Width
                            + " panel2=" + _split.Panel2.ClientSize.Width
                            + " chatArea=" + (_split.Panel2.ClientSize.Width - (open ? _dock.Width : 0)));
        }

        /// <summary>BATCH-TA-23/D1d — WHICH PANE SOURCES EXIST RIGHT NOW.
        ///
        /// ⚠ THIS SUBSCRIBES TO THE COMPOSER STATE, IT DOES NOT RECOMPUTE "can I post here".
        /// Core/ComposerState.cs is already the pure resolver for that, with a documented precedence
        /// (Blocked &gt; Join &gt; MuteUnmute &gt; BotStart &gt; Restricted), and <c>_footerKind</c> holds
        /// its answer. Re-deriving the rule here is how two surfaces end up disagreeing — the exact bug
        /// class TA-20/S0 exists to prevent.
        /// ⚠ CALLED FROM BOTH PLACES THAT ASSIGN _footerKind, so it tracks a LIVE change — a mute, an
        /// unblock, a slow-mode expiry — with no chat switch. Sampling it once at open would be wrong.
        /// ⚠ AN UNAVAILABLE SOURCE IS OMITTED, NOT GREYED: the Emoji tab disappears and Info fills the
        /// strip, matching AddMenuItem's convention and the Flip-button precedent in RoundRecorderForm.</summary>
        private void UpdateDockSources()
        {
            if (_dockEmojiTab == null) return;
            bool emojiOk = _selectedChat != null && _footerKind == ComposerKind.Compose;
            if (_dockEmojiTab.Visible != emojiOk) _dockEmojiTab.Visible = emojiOk;
            if (!emojiOk && _dockPane == DockPane.Emoji) SetDockPane(DockPane.Info);
            LayoutDockTabs();
        }

        /// <summary>Pushes the current accent/dark state into the custom-painted controls.</summary>
        private void RefreshThemedControls()
        {
            if (_hamburger != null) { _hamburger.ForeColor = _accent; _hamburger.Invalidate(); }   // repaint the drawn icon with the current accent
            if (_chatSearchBtn != null) _chatSearchBtn.Invalidate();   // INCHAT-SEARCH: repaint the drawn magnifier with the current accent
            if (_chatMenuBtn != null)
            {
                // The hover tint is a one-shot property, so unlike the drawn glyph it does NOT follow a live
                // accent change on its own — re-assert it here, where every other themed control is refreshed.
                _chatMenuBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
                _chatMenuBtn.Invalidate();
            }
            if (_dockBtn != null)
            {
                // TA-23/D1c — same one-shot-property gap as _chatMenuBtn: re-assert the hover tint here,
                // because unlike the drawn glyph it does not follow a live accent change by itself.
                _dockBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, _accent);
                _dockBtn.Invalidate();
            }
            if (_dockEmoji != null)
            {
                // ⚠ The embedded panel bakes _dark/_accent at CONSTRUCTION — the popup never had this
                // problem because it is rebuilt on every open. Drop it so the next selection rebuilds it
                // in the new theme, and rebuild NOW if it is the pane on screen, so a theme switch can
                // never leave an empty dock.
                try { _dockBody.Controls.Remove(_dockEmoji); _dockEmoji.Dispose(); } catch { }
                _dockEmoji = null;
            }
            // Same reason for the profile: ProfileForm takes dark/accent at construction. (It also retint
            // itself via ThemeHelper.ThemeChanged, but its ROWS were built with the old colours.)
            DropDockProfile();
            if (_dockTabs != null) { SetDockPane(_dockPane); _dockTabs.Invalidate(); }   // accent underline + labels
            if (_attachButton != null) _attachButton.Invalidate(); // self-themes from ThemeHelper
            if (_footerBar != null) { _footerBar.AccentColor = _accent; _footerBar.IsDark = _dark; _footerBar.Invalidate(); }
            if (_threadJoinBar != null) { _threadJoinBar.AccentColor = _accent; _threadJoinBar.IsDark = _dark; _threadJoinBar.Invalidate(); }
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
            RebuildTopicBar();        // FORUM-TOPICS: same immutable-chip pattern → rebuild to recolor on theme change (any folder mode)
            RebuildStoryTray();       // STORIES: chips are immutable (color baked at build) → rebuild IS the recolor
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
                else if (c is ServiceLineControl s) { s.IsDark = _dark; s.Invalidate(); }   // centered "X pinned…"/join lines
            }
        }

        // ── Hamburger menu ───────────────────────────────────────────────────

        private DrawerMenu _drawer;
        private DrawerOutsideCloser _drawerCloser;   // HAMBURGER-SCRIM-FIX: closes the card-width drawer on an outside tap

        /// <summary>Opens the Telegram-style left drawer (account header + full menu + Night Mode toggle).</summary>
        private void ShowDrawer()
        {
            if (_drawer != null) { CloseDrawer(); return; }

            // HAMBURGER-FIX: the drawer used to snapshot the WHOLE window (DrawToBitmap) each open for a dimmed
            // backdrop — a synchronous full-UI render that was the DOMINANT open-lag (esp. on RT ARM32). The
            // backdrop is now a solid scrim (DrawerMenu's null-snap path), so the open never blocks on a render.

            var me = _service.Me;
            string name = me != null ? string.Join(" ", new[] { me.first_name, me.last_name }).Trim() : "TelegArm";

            var rows = new List<DrawerMenu.Row>();
            // Account switcher: the ACTIVE account is the header above — list only the OTHERS (tap to switch),
            // so the same account is never shown twice. De-duped by id (defensive; folder names are already ids).
            // HAMBURGER-FIX: DON'T decode avatars on the open path — each was a sync disk-read + JPEG-decode +
            // Bitmap-copy on the UI thread. Rows open with a letter placeholder (DrawerMenu draws it for a null
            // Avatar); the real images decode OFF-thread and swap in (LoadDrawerAvatarsAsync, below).
            var seen = new HashSet<long>();
            var pendingAv = new List<KeyValuePair<DrawerMenu.Row, string>>();
            foreach (var acc in AccountStore.ListAccounts())
            {
                if (acc.Id == AccountContext.ActiveId || !seen.Add(acc.Id)) continue;
                long accId = acc.Id; string accName = acc.Name;
                var accRow = new DrawerMenu.Row
                {
                    IsAccount = true, Label = acc.Name, AvatarKey = acc.Id, Avatar = null,
                    Action = Wrap(() => SwitchAccount(accId, accName))
                };
                rows.Add(accRow);
                if (!string.IsNullOrEmpty(acc.AvatarPath) && File.Exists(acc.AvatarPath))
                    pendingAv.Add(new KeyValuePair<DrawerMenu.Row, string>(accRow, acc.AvatarPath));
            }
            rows.Add(Row("➕", "Add Account", AddAccount));
            rows.Add(Sep());
            rows.Add(Row("👤", "My Profile", ShowProfile));
            rows.Add(Row("👥", "New Group", NewGroup));
            rows.Add(Row("📢", "New Channel", NewChannel));
            rows.Add(Row("📇", "Contacts", OpenContacts));
            // RELEASE-FIXES-V11 (H2): "Calls" hidden for release (parked for a later version — don't ship a "coming soon" dead item).
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

            // HAMBURGER-SCRIM-FIX (Option A): the drawer is now only as WIDE AS THE CARD (not the whole window),
            // so the app content to its right stays FULLY VISIBLE + UNDIMMED — no backdrop, no black screen. (A
            // full-window control would occlude the content; WinForms transparency renders the form bg black here.)
            _drawer = new DrawerMenu(null, _dark, _accent, name, letter, av, me?.id ?? 0, rows, Wrap(ShowProfile))
            {
                Bounds = new Rectangle(0, 0, DrawerMenu.CardW, ClientSize.Height)
            };
            _drawer.CloseRequested += () => BeginInvoke((Action)CloseDrawer);
            Controls.Add(_drawer);
            _drawer.BringToFront();
            _drawer.Focus();

            // The narrow drawer no longer covers the outside, so it can't catch the tap-to-close there. A pre-
            // dispatch message filter closes it on any pointer/mouse down that lands OUTSIDE the card (it sees the
            // tap before the content control does). Removed again in CloseDrawer.
            if (_drawerCloser == null) { _drawerCloser = new DrawerOutsideCloser(this); Application.AddMessageFilter(_drawerCloser); }

            // Decode the other-account avatars OFF the UI thread and swap them into the open drawer (letters →
            // photos). Never blocks the open; guarded so a closed/replaced drawer can't get a stale image.
            LoadDrawerAvatarsAsync(_drawer, pendingAv);
        }

        /// <summary>HAMBURGER-FIX: off-thread account-avatar loader for the drawer. Each avatar is decoded on a
        /// worker thread, then marshaled back and assigned to its row (the drawer repaints). If the drawer was
        /// closed/replaced before a decode finished, the freshly-decoded image is disposed (no leak) instead of
        /// assigned. Row avatars stay owned by DrawerMenu.Dispose (per-open lifetime, unchanged).</summary>
        private void LoadDrawerAvatarsAsync(DrawerMenu drawer, List<KeyValuePair<DrawerMenu.Row, string>> pending)
        {
            if (drawer == null || pending == null || pending.Count == 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var kv in pending)
                {
                    Image img = null;
                    try { using (var fs = File.OpenRead(kv.Value)) using (var t = Image.FromStream(fs)) img = new Bitmap(t); }
                    catch { img = null; }
                    if (img == null) continue;
                    var row = kv.Key; var decoded = img;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (_drawer == drawer && !drawer.IsDisposed) { row.Avatar = decoded; drawer.Invalidate(); }
                            else { try { decoded.Dispose(); } catch { } }   // drawer gone → don't leak the decode
                        }));
                    }
                    catch { try { decoded.Dispose(); } catch { } }   // form closing → BeginInvoke unavailable
                }
            });
        }

        private void CloseDrawer()
        {
            // Drop the outside-tap filter first (always, even if the drawer ref is already gone) so it never lingers.
            if (_drawerCloser != null) { try { Application.RemoveMessageFilter(_drawerCloser); } catch { } _drawerCloser = null; }
            var d = _drawer;
            if (d == null) return;
            _drawer = null;
            try { Controls.Remove(d); d.Dispose(); } catch { }
        }

        /// <summary>HAMBURGER-SCRIM-FIX: pre-dispatch outside-tap detector for the (now card-width) drawer. Sees
        /// pointer/mouse DOWN messages before they reach the content control; if one lands on any window that isn't
        /// the drawer (or the hamburger), it closes the drawer. Never SWALLOWS the message (safe for touch — no
        /// half-consumed WM_TOUCH gesture); the tap also proceeds normally. Esc + a menu tap remain fallbacks.</summary>
        private sealed class DrawerOutsideCloser : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_NCLBUTTONDOWN = 0x00A1,
                              WM_POINTERDOWN = 0x0246, WM_TOUCH = 0x0240;
            private readonly MainForm _f;
            public DrawerOutsideCloser(MainForm f) { _f = f; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN:
                    case WM_RBUTTONDOWN:
                    case WM_NCLBUTTONDOWN:
                    case WM_POINTERDOWN:
                    case WM_TOUCH:
                        var d = _f._drawer;
                        if (d != null && !_f.IsDisposed && m.HWnd != d.Handle
                            && (_f._hamburger == null || m.HWnd != _f._hamburger.Handle))
                        {
                            try { _f.BeginInvoke((Action)_f.CloseDrawer); } catch { }
                        }
                        break;
                }
                return false;   // observe only — do not consume the tap
            }
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

            public const int CardW = 300;              // exposed so ShowDrawer sizes the (narrow) drawer to JUST the card
            private const int HeaderH = 92, RowH = 46, SepH = 11;

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
                // HAMBURGER-SCRIM-FIX: no backdrop. The drawer is card-width now, so there's no outside region to
                // dim — the opaque card fill below IS the whole control. (Old full-window snapshot/scrim removed.)

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
            ApplyComments(mb, m);   // COMMENTS-INDICATOR: discussion-comments footer (called wherever a Message bubble is enriched)
            ApplyChannelMeta(mb, m);   // CHANNEL-META-EXTRAS: view count + post_author + group admin role
        }

        // CHANNEL-META-EXTRAS (3): senderId → role (owner/admin/custom rank) for the OPEN megagroup; filled async on
        // open (LoadGroupAdminRolesAsync), cleared on every chat switch. Bubble-build reads it (NO per-bubble RPC).
        private Dictionary<long, string> _groupAdminRoles;

        /// <summary>CHANNEL-META-EXTRAS: sets the flag-gated view count (channel posts) + post_author (signed channels)
        /// off the Message, and the sender's group admin/owner/custom-rank role from the per-open admin cache (or the
        /// per-message from_rank fallback). All no-ops when the fields/flags are absent.</summary>
        private void ApplyChannelMeta(MessageBubbleControl mb, Message m)
        {
            if ((m.flags & Message.Flags.has_views) != 0) mb.SetViews(m.views);
            if ((m.flags & Message.Flags.has_post_author) != 0) mb.SetPostAuthor(m.post_author);
            string role = null;
            if (_groupAdminRoles != null && m.from_id is PeerUser pu && _groupAdminRoles.TryGetValue(pu.user_id, out var r)) role = r;
            else if ((m.flags2 & Message.Flags2.has_from_rank) != 0 && !string.IsNullOrEmpty(m.from_rank)) role = m.from_rank;
            if (role != null) mb.SetAdminRole(role);
        }

        /// <summary>CHANNEL-META-EXTRAS (3): fetch the open megagroup's admins ONCE and cache senderId→role, then
        /// re-apply to the already-built bubbles (they render before this async fetch returns). No-op for non-megagroups;
        /// guarded against a chat switch mid-fetch. Cache-per-open (admin changes reflect on the next open — acceptable).</summary>
        private async void LoadGroupAdminRolesAsync(ChatEntry entry)
        {
            if (entry == null || !(entry.PeerInfo is Channel ch) || (ch.flags & Channel.Flags.megagroup) == 0) return;
            long peerId = entry.PeerId;
            try
            {
                var res = await _service.GetParticipantsAsync(ch, new ChannelParticipantsAdmins(), 0, 100);
                if (res == null || res.participants == null || _selectedChat == null || _selectedChat.PeerId != peerId) return;
                var map = new Dictionary<long, string>();
                foreach (var p in res.participants)
                {
                    if (p is ChannelParticipantCreator cc) map[cc.user_id] = string.IsNullOrEmpty(cc.rank) ? "owner" : cc.rank;
                    else if (p is ChannelParticipantAdmin ca) map[ca.user_id] = string.IsNullOrEmpty(ca.rank) ? "admin" : ca.rank;
                }
                _groupAdminRoles = map;
                // Re-apply to bubbles already built before the fetch returned (match by message id → sender).
                foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>())
                {
                    var msg = _currentChatMessages.FirstOrDefault(x => x.ID == mb.MessageId);
                    if (msg != null && msg.from_id is PeerUser pu && map.TryGetValue(pu.user_id, out var role))
                    { mb.SetAdminRole(role); mb.Measure(); mb.Invalidate(); }
                }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[ADMIN] roles cached=" + map.Count + " for peer=" + peerId);
            }
            catch (Exception ex) { if (LogOn) System.Diagnostics.Debug.WriteLine("[ADMIN] roles fetch failed: " + ex.Message); }
        }

        /// <summary>CHANNEL-META-EXTRAS (1): live view-count bump (UpdateChannelMessageViews). Updates the cached Message
        /// + the loaded/visible bubble; off-window messages no-op. Runs on the UI thread (dispatched), no RPC.</summary>
        private void HandleChannelViews(long channelId, int msgId, int views)
        {
            var msg = _currentChatMessages.FirstOrDefault(x => x.ID == msgId);
            if (msg != null) { msg.views = views; msg.flags |= Message.Flags.has_views; }
            if (_selectedChat == null || _selectedChat.PeerId != channelId) return;
            foreach (var mb in _messagePanel.Controls.OfType<MessageBubbleControl>())
                if (mb.MessageId == msgId) { mb.SetViews(views); mb.Measure(); mb.Invalidate(); break; }
        }

        /// <summary>COMMENTS-INDICATOR (display + tap only): if this is a broadcast-channel post with a linked
        /// discussion group (Message.replies carrying the comments flag), show the "N comments / Leave a comment"
        /// footer and route a tap to the stub. The comments flag is set ONLY on broadcast posts with a linked group,
        /// so it self-selects — a megagroup's in-reply thread carries comments=false and is skipped.</summary>
        private void ApplyComments(MessageBubbleControl mb, Message m)
        {
            if (!(m.replies is MessageReplies mr) || !mr.flags.HasFlag(MessageReplies.Flags.comments)) return;
            mb.SetComments(mr.replies, mr.channel_id);
            mb.CommentsClicked += (postId, linkedChatId) => OpenComments(postId, linkedChatId);
        }

        /// <summary>COMMENTS-THREAD: open a channel post's discussion thread (READ-ONLY this batch). Resolves the linked
        /// group + thread via GetDiscussionMessage, then pages it through the reused history view via GetReplies. The
        /// header becomes a "‹ back" affordance; the composer is hidden (posting is the next batch).</summary>
        private async void OpenComments(int postMsgId, long linkedChatId)
        {
            if (_thread != null || _selectedChat == null) return;
            var channelEntry = _selectedChat;
            var originalPost = _currentChatMessages.FirstOrDefault(x => x.ID == postMsgId);   // the tapped post (channel view still loaded)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] open post=" + postMsgId + " linked=" + linkedChatId);
            try
            {
                var disc = await _service.GetDiscussionMessageAsync(channelEntry.Peer, postMsgId);
                if (disc == null || disc.messages == null || disc.messages.Length == 0)
                { ThemedDialog.Show(this, "Comments", "This discussion isn't available.", "OK"); return; }

                // Build the linked-group entry (sender/avatar resolution + IsGroup rendering) from the returned dicts.
                ChatEntry groupEntry = null;
                if (disc.chats != null && disc.chats.TryGetValue(linkedChatId, out var gcb)) groupEntry = EntryFromPeerInfo(gcb);
                if (groupEntry == null)
                { ThemedDialog.Show(this, "Comments", "Couldn't resolve the discussion group.", "OK"); return; }

                int anchor = TopVisibleMessageId();   // remember the channel position for back (before we switch views)
                // COMMENTS-THREAD-SCOPE: GetReplies must target the DISCUSSION GROUP + the thread root's id IN THAT GROUP
                // (the auto-forwarded post, disc.messages) — NOT the broadcast channel + channel-post id, which does not
                // scope and pulls the group's whole history. disc.messages is non-empty here (checked above).
                int groupRootId = disc.messages[disc.messages.Length - 1].ID;
                _thread = new ThreadCtx { ChannelPeer = channelEntry.Peer, PostMsgId = postMsgId,
                                          GroupPeer = groupEntry.Peer, GroupRootId = groupRootId,
                                          ReturnTo = channelEntry, ReturnAnchorId = anchor,
                                          GroupEntry = groupEntry };   // COMMENTS-JOIN-FLYOUT: join source (Peer + 'left' flag)
                groupEntry.UnreadCount = 0;           // open the thread at the bottom (latest comments)
                UpdateTrayTooltip();     // TA-6b/B: the tray gap at this site — unread dropped to 0 unannounced
                RefreshFolderBadges();   // TA-6b/G1 (DOWN): opening a comment thread reads the discussion group
                _selectedChat = groupEntry;           // paging / scroll / updates target the thread from here
                await LoadHistoryAsync(groupEntry, 0);   // pages via GetReplies (thread mode, source-swapped)
                if (_thread == null) return;             // back was hit while loading
                if (originalPost != null) PrependThreadRootPost(channelEntry, originalPost);   // COMMENTS-THREAD-v2: post at TOP
                _chatTitle.Text = "‹ Comments";
                _chatStatus.Text = channelEntry.Title;
                // COMMENTS-NAV-FIX Bug 1: ALWAYS show the composer in the thread (member or not) — posting is direct;
                // join is only a fallback if the server rejects it (PostThreadComment). Never force a join to comment.
                ShowComposeFooter();
                _joinBarDismissed = false;   // COMMENTS-JOIN-FLYOUT: fresh thread — allow the join bar again
                UpdateThreadJoinBar();        // shows the bar iff we're NOT a member of the linked group
                try { var _ = _service.ReadDiscussionAsync(channelEntry.Peer, postMsgId, disc.read_inbox_max_id); } catch { }
            }
            catch (Exception ex)
            {
                _thread = null;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] open failed: " + ex.Message);
                ThemedDialog.Show(this, "Comments", "Couldn't open comments:\n" + ex.Message, "OK");
            }
        }

        /// <summary>COMMENTS-THREAD: leave the thread and return to the channel at the prior position (reload focusing
        /// the remembered anchor — the channel view was never mutated in place, so this restores it cleanly).</summary>
        private void CloseThread()
        {
            if (_thread == null) return;
            var ret = _thread.ReturnTo; int anchor = _thread.ReturnAnchorId;
            _thread = null;
            UpdateThreadJoinBar();   // COMMENTS-JOIN-FLYOUT: leaving the thread → hide the join bar
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] back to channel peer=" + ret.PeerId);
            var _ = OpenChat(ret, anchor);
        }

        /// <summary>COMMENTS-NAV-FIX Bug 2: exit thread mode (idempotent). Clearing _thread is sufficient — the three
        /// history loaders fall back to GetHistory, ResolveAndApplyComposer resolves the normal footer, and the
        /// LoadHistoryAsync that every open path runs right after clears the panel + _currentChatMessages (dropping the
        /// root-post-at-top). Called at the top of OpenChat so NO conversation-open path can leave a stale thread
        /// routing the paging/composer/send — that was the wrong-destination send bug.</summary>
        private void ClearThreadMode()
        {
            if (_thread == null) return;
            _thread = null;
            UpdateThreadJoinBar();   // COMMENTS-JOIN-FLYOUT: leaving the thread → hide the join bar
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] thread cleared (switched conversation)");
        }

        /// <summary>COMMENTS-THREAD-v2: render the original channel post as the root at the TOP of the thread (context),
        /// reusing the bubble pipeline. Its OWN comments footer is suppressed (no ApplyComments → no recursion).
        /// ReconcileWindowOrHeal is overlap-only, so this extra top bubble (correctly laid out, no overlap) is tolerated;
        /// it's added to the model at index 0 (oldest) so a heal rebuild keeps it. NOTE: on a heavy thread, paging older
        /// comments (InsertRange at 0) can slot them above the post — cosmetic, not corrupting; common threads (≤1 page)
        /// keep it pinned at top.</summary>
        private void PrependThreadRootPost(ChatEntry channelEntry, Message post)
        {
            try
            {
                if (_currentChatMessages.Any(x => x.ID == post.ID)) return;   // already present (channel/group id collision guard)
                var ctl = MakeMessageBubble(channelEntry, post, null) as MessageBubbleControl;
                if (ctl == null) return;
                ctl.MessageId = post.ID;
                ApplyEntities(ctl, post);       // links / mentions — but NOT ApplyComments (would recurse the footer)
                _messagePanel.Controls.Add(ctl);
                _messagePanel.Controls.SetChildIndex(ctl, 0);
                _currentChatMessages.Insert(0, post);
                _shownMessageIds.Add(post.ID);
            }
            catch { }
        }

        /// <summary>The message id nearest the top of the current message view (for restoring position on thread-back).</summary>
        private int TopVisibleMessageId()
        {
            try
            {
                MessageBubbleControl best = null;
                foreach (var c in _messagePanel.Controls)
                    if (c is MessageBubbleControl b && b.MessageId != 0 && b.Bottom > 0 && (best == null || b.Top < best.Top))
                        best = b;
                return best?.MessageId ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Header tap: in a comment thread it's the "‹ back" affordance; otherwise it opens the profile.</summary>
        private void OnHeaderClick()
        {
            if (_thread != null) CloseThread();
            else OpenSelectedProfile();
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

        /// <summary>QUICKWINS-1 PART 2: others' reaction changes arrive live (we render + send, but used to DROP this
        /// update → stale until reload). If the message is loaded in the OPEN chat, refresh its pills from the echoed
        /// MessageReactions. No RPC, no fetch when off-window (same as the other live handlers). Runs on the UI thread
        /// (the update pump already marshals here). Reconciles our own optimistic send to the server truth — no stacking.</summary>
        private void HandleReactionsUpdate(long peerId, int msgId, MessageReactions reactions)
        {
            if (_selectedChat == null || _selectedChat.PeerId != peerId) return;    // not the open chat → no-op (never fetch)
            var m = _currentChatMessages.FirstOrDefault(x => x.ID == msgId);
            if (m == null) return;                                                  // not in the loaded window → no-op
            m.reactions = reactions;                                                // keep the cache current (re-render consistent)
            var mb = _messagePanel.Controls.OfType<MessageBubbleControl>().FirstOrDefault(b => b.MessageId == msgId);
            if (mb != null)
            {
                mb.SetReactions(ExtractReactions(m));   // null when all cleared → pills disappear
                if (LogOn) System.Diagnostics.Debug.WriteLine("[REACT] live update peer=" + peerId + " msg=" + msgId);
            }
        }

        /// <summary>MENTION-REACTION: light a row's passive heart glyph when a reaction to YOUR message arrives unread
        /// (MessagePeerReaction.unread — the clean "reacted to me, unseen" signal; no group false-positives). The OPEN
        /// chat is skipped (you're seeing it → cleared on open). Count-agnostic: the glyph is on/off (exact count
        /// re-syncs on the next dialog load). NO notification — indicator only.</summary>
        private void HandleReactionIndicator(long peerId, MessageReactions reactions)
        {
            if (peerId == 0 || reactions?.recent_reactions == null) return;
            if (_selectedChat != null && _selectedChat.PeerId == peerId) return;   // open → seen (ReadReactions on open)
            bool unreadToMe = reactions.recent_reactions.Any(r => (r.flags & MessagePeerReaction.Flags.unread) != 0);
            if (!unreadToMe) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry == null || entry.UnreadReactions > 0) return;
            entry.UnreadReactions = 1;                     // on/off glyph — exact count comes from the next dialog load
            FindChatItem(peerId)?.Invalidate();
        }

        /// <summary>MENTION-REACTION: clear a channel/megagroup row's "@" badge when its message contents (incl. the
        /// mention) were read on ANOTHER device (UpdateChannelReadMessagesContents carries channel_id). Best-effort —
        /// the exact count re-syncs on the next dialog load; basic-group/DM reads (no channel_id) refresh on reload.</summary>
        private void HandleMentionsReadElsewhere(long channelId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == channelId);
            if (entry == null || entry.UnreadMentions == 0) return;
            entry.UnreadMentions = 0;
            FindChatItem(channelId)?.Invalidate();
        }

        private void ShowProfile()
        {
            using (var dlg = new ProfileForm(_service))   // editable self-profile
            {
                dlg.ShowDialog(this);
                // BATCH-TA-18 — this was MISSING: only OpenSelectedProfile routed pending links, so a link
                // tapped in your OWN profile (bio, or the shared-links gallery) was silently dropped. It has
                // to be here or the proxy-link interception has a hole exactly where the router doesn't run.
                RouteProfilePending(dlg);
            }
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
            else if (dlg.PendingOpenChannel != null) OpenPersonalChannel(dlg.PendingOpenChannel);   // PROFILE-CHANNEL
        }

        /// <summary>PROFILE-CHANNEL: open a user's attached personal channel (tapped in their profile) via the normal
        /// channel-open path — reuse an existing dialog-list entry if we follow it, else build one from the Channel.</summary>
        private async void OpenPersonalChannel(Channel ch)
        {
            if (ch == null) return;
            try
            {
                var entry = _allChats.FirstOrDefault(c => c.PeerId == ch.id) ?? EntryFromPeerInfo(ch);
                if (entry != null) await OpenChat(entry, 0);
            }
            catch (Exception ex) { if (LogOn) System.Diagnostics.Debug.WriteLine("[PROFILE-CHANNEL] open failed: " + ex.Message); }
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
            _service.AccountId = 0;               // CRITICAL (ACCOUNT-SESSION-PATH-FIX): TelegramService.SessionPath checks
                                                  // this.AccountId FIRST — without this reset the reused _service still holds the
                                                  // PREVIOUS account's id, so the add-login opens accounts/{prevId}/session (that
                                                  // account's REAL file) and Telegram rebinds it → the previous account is LOGGED
                                                  // OUT. Pair with ActiveId=0 so the pending login resolves to _pending.
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
                // Pair AccountId with ActiveId=0 (the pending invariant) so nothing resolves a stale session between here
                // and the switch; SwitchAccountAsync then re-binds _service.AccountId to `restore`.
                if (restore != 0) { AccountContext.ActiveId = 0; _service.AccountId = 0; await SwitchAccountAsync(restore, prevName); }
                else ShowLoginForm();   // genuinely no accounts (e.g. add was the very first) → first-launch login (resets AccountId)
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
                _service.AccountId = 0;               // pair with ActiveId=0 (pending invariant); SwitchAccountAsync re-binds it to newId
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
            PerfLog.Boot("MainForm.OnLoad → ResumeSessionAsync");
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
            Logger.Diag("[CONN] connected — entering app");
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
                { Logger.Diag("[ACCT] connect loop aborted by user"); return false; }

                if (_retryNowCts != null) { try { _retryNowCts.Dispose(); } catch { } }
                _retryNowCts = new System.Threading.CancellationTokenSource();   // "Retry now" cancels this (attempt + backoff)
                var token = _retryNowCts.Token;

                ShowConnecting(attempt == 1 ? firstMsg : "Waiting for network — make sure your VPN is on.");
                Logger.Diag("[CONN] connect attempt " + attempt);
                // TA-16b/B1+B2 — report into the shared proxy state. This loop is UNBOUNDED by design, so
                // ProxyStatus decides "failed" on WALL CLOCK, not on this attempt counter (see its remarks:
                // one visible attempt can hide a minute of invisible WTC retries). Nothing here changes the
                // retry behaviour — FAILED is display only.
                ProxyStatus.NoteAttempt();

                var loginTask = _service.LoginAsync(silentResume: true);   // silent: stored phone, no code/password block; MAY hang
                var waiter = System.Threading.Tasks.Task.Delay(TelegramService.ConnectAttemptTimeoutMs, token);
                System.Threading.Tasks.Task finished;
                try { finished = await System.Threading.Tasks.Task.WhenAny(loginTask, waiter); }
                catch { finished = null; }

                if (finished == loginTask)
                {
                    Exception failure = null;
                    try { await loginTask; } catch (Exception ex) { failure = ex; }   // observe success/exception
                    if (_service.IsAuthorized)
                    {
                        PerfLog.Boot("LoginAsync returned AUTHORIZED (attempt " + attempt + ")");
                        ProxyStatus.NoteAuthorized();   // the ONLY evidence a proxy actually works (TA-15/X4)
                        return true;
                    }   // connected + authorized

                    if (failure != null)
                    {
                        bool corrupt = IsCorruptSessionError(failure);
                        bool needsLogin = IsNeedsLoginError(failure) || _service.NeedsInteractiveLogin || IsAuthError(failure);
                        Logger.Diag("[CONN] attempt " + attempt + " failed: " + failure.Message
                            + (corrupt ? "  [SESSION unreadable → recover]"
                               : needsLogin ? "  [no usable session/phone → stop; caller recovers]"
                               : "  [hard failure " + (hardFailures + 1) + "/" + maxHardFailures + "]"));
                        if (corrupt) { _connectCorrupt = true; return false; }        // caller deletes the dead session + recovers
                        if (needsLogin) return false;                                 // no session/phone → LoginForm / next account (NOT retried)

                        // A transport-level failure. Corrupt-session and needs-login are deliberately NOT
                        // counted above: both mean we got far enough to learn something about the ACCOUNT,
                        // which says nothing about the proxy.
                        ProxyStatus.NoteAttemptFailed();

                        // A non-network failure (e.g. the session file is locked by a lingering handle): the SAME
                        // client keeps failing, so DISCARD it (release the file → fresh client next attempt) and CAP
                        // the retries so a locked/unusable account can never loop forever (the repaint-storm bug).
                        hardFailures++;
                        await _service.DiscardFaultedClientAsync();
                        if (hardFailures >= maxHardFailures)
                        { Logger.Diag("[CONN] gave up after " + hardFailures + " non-network failures → caller recovers"); return false; }
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
                    Logger.Diag("[CONN] attempt " + attempt + (userRetry
                        ? " interrupted by Retry-now → tearing down hung attempt"
                        : " timed out after " + (TelegramService.ConnectAttemptTimeoutMs / 1000) + "s → tearing down hung attempt"));
                    await _service.TeardownHungConnectAsync();
                    SwallowFault(loginTask);   // it will fault once the socket is reset; don't let it surface
                    // A hung attempt is the CLASSIC dead-proxy signature: the TCP connect to the proxy
                    // succeeds (or stalls) and nothing ever comes back, so this branch — not the faulted
                    // one — is what a wrong secret usually looks like. A user-requested retry is not a
                    // failure and must not count toward the threshold.
                    if (!userRetry) ProxyStatus.NoteAttemptFailed();
                    if (userRetry) continue;   // Retry-now → immediate fresh attempt (skip backoff)
                }

                int secs = Math.Max(1, backoffMs / 1000);
                SetConnectingDetail("Waiting for network — make sure your VPN is on.\nRetrying in " + secs + "s… (or tap Retry now)");
                Logger.Diag("[CONN] backoff " + secs + "s before next attempt");
                try { await System.Threading.Tasks.Task.Delay(backoffMs, token); }
                catch (OperationCanceledException) { Logger.Diag("[CONN] Retry-now → retrying immediately"); }
                backoffMs = Math.Min(backoffMs * 2, TelegramService.ConnectMaxBackoffMs);
            }
        }

        /// <summary>Post-connect setup shared by resume + switch: updates, dialogs/chat list, manager seed, watchdog.</summary>
        private async System.Threading.Tasks.Task AfterConnectAsync(bool rebind = false)
        {
            _recoveryTried.Clear();   // a clean connect → reset the corrupt-recovery chain for any future corruption
            _recoveryRetried.Clear(); // …and the clean-retry gate, so a LATER corruption episode gets its own retry (ACCOUNT-RECOVERY-SAFETY)
            _service.AccountId = AccountContext.ActiveId;   // MULTI-ACCOUNT (3b): bind the active service to its id → the
                                                            // UM router forwards its updates to the UI; paths stay identical
                                                            // (AccountDir(ActiveId) == the static active path).
            // INCREMENT 3b: on a REBIND to an already-warm service its UpdateManager is ALREADY attached (routed) and
            // seeded — re-attaching/re-seeding would double-hook. Everything below still runs on the now-live target.
            if (!rebind) SubscribeUpdates();
            await LoadDialogsAsync();
            // WARMUP-FIX EARLY: kick the warm-ups HERE — the moment the active account's dialogs render (UI usable) —
            // instead of at the very END of this method (after seed/backfill/notify). That closes the ~10s "nothing
            // warming yet" window: a switch in the first seconds now finds the target already in _warming → wait-then-
            // rebind, not cold. Fire-and-forget → runs CONCURRENTLY with the active account's remaining startup (seed/
            // backfill below); the 400ms warm stagger keeps the overlapping warm connects from hammering RT. Warmed
            // ONCE (the old end-of-method call is removed). Idempotent + gated by WarmConnections.
            var __ = WarmOthersAsync();
            if (!rebind) { await _service.SeedUpdateManagerAsync(); PerfLog.Boot("SeedUpdateManagerAsync RETURNED (full dialog sweep done)"); }   // seed the manager's baseline — REQUIRED for live updates
            // NOTIFY-FIX: persist the freshly-seeded update state NOW. Otherwise the state file only updates at clean
            // exit/switch, and a crash-restart re-attaches from a stale pts → getDifference replays the whole previous
            // session's tail through the notify gate.
            // SAVESTATE-DEADLOCK: MUST be Task.Run — this continuation is inlined on the UI thread INSIDE WTC's
            // GetDifference/LoadDialogs seed (which holds the update-state semaphore); a direct SaveUpdateState() here
            // calls get_State() → Wait on that same held semaphore → UI-thread self-deadlock (HangWatch 5s). Off the UI
            // thread, the pool thread waits for the lock to free once the seed finishes, then persists. Fire-and-forget.
            var _ = System.Threading.Tasks.Task.Run(() => _service.SaveUpdateState());   // fire-and-forget (discard suppresses CS4014)
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
                AccountStore.StampActive(AccountContext.ActiveId);   // WARMUP-FIX 1.2: mark recency → next startup warms this account first
                // Self-heal: every connected account MUST have its phone persisted (silent resume/switch reads
                // it; a missing phone makes the resume need interactive login → a spurious "logged out").
                if (!File.Exists(AccountContext.PhonePath) && !string.IsNullOrEmpty(_service.Me.phone))
                    _service.SavePhone(_service.Me.phone.StartsWith("+") ? _service.Me.phone : "+" + _service.Me.phone);
            }
            // (WARMUP-FIX EARLY: warm-ups were kicked right after LoadDialogsAsync above — NOT here — so they start ~2s
            // sooner and an early switch can wait-then-rebind. Warmed once, earlier. This is still where the active
            // account's own startup completes; re-warming the just-left account happens via that earlier kick too.)
            LoadStoriesAsync();   // STORIES-BUILD-1: fetch the story tray (fire-and-forget; tray hidden if none). Runs on cold connect AND rebind.
            PerfLog.Boot("AfterConnectAsync RETURNED (rebind=" + rebind + ") — startup complete");
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
        // ── WARM CONNECTIONS (ACCOUNT-SWITCH STEP 1): background clients for non-active accounts ──
        private readonly Dictionary<long, TelegramService> _warm = new Dictionary<long, TelegramService>();
        // WARMUP-FIX: in-flight warm-ups (id → the WarmOneAsync task) so a switch to a MID-warm-up account can WAIT for
        // THIS task then REBIND instead of going cold. An entry is present only while warming; on completion it moves
        // into _warm (success) and is removed here. All access is on the UI thread (no locking needed).
        private readonly Dictionary<long, System.Threading.Tasks.Task<TelegramService>> _warming = new Dictionary<long, System.Threading.Tasks.Task<TelegramService>>();
        private int _switchGen;                        // WARMUP-FIX B: bumped per switch; a warm-up wait bails if a newer switch supersedes it
        private const int WarmStartStaggerMs = 400;    // Fix A: gap between warm-up STARTS (overlap them; don't hammer connect at once)
        // Fix B: max wait for a mid-warm-up target before falling back to cold. Must OUTLAST a real warm-up — RT shows
        // ~7-10s (a WTC-internal first-connect retry, "connect attempt 2", + the UM seed), so 5s undershot and timed out
        // ~2-3s before readiness → cold. 12s covers it with margin (a mid-warm-up switch now almost always rebinds). A
        // FAILING warm-up completes early (its task finishes → WhenAny releases), so a longer cap only extends the wait
        // for accounts genuinely still warming — never hurts. Passive/responsive wait; tune here if RT warm-ups run longer.
        private const int WarmupWaitMs = 12000;

        /// <summary>INCREMENT 3b + WARMUP-FIX A: spins up a full warm background SERVICE for every NON-active account with
        /// a session — connected + a ROUTED UpdateManager (maintains live pts, silent while non-active) — so a later switch
        /// REBINDS to it instantly. Fix A: warms the MOST-RECENTLY-USED accounts FIRST (likeliest switch targets ready
        /// soonest) and OVERLAPS the warm-ups (a small stagger between STARTS, not between completions) → all accounts warm
        /// in ~one warm-up instead of N×. Idempotent (skips active/already-warm/already-warming); gated by WarmConnections.</summary>
        private async System.Threading.Tasks.Task WarmOthersAsync()
        {
            if (!AppSettings.Instance.WarmConnections) return;
            var ids = AccountStore.ListAccounts()
                .Where(a => a.Id != 0 && a.Id != AccountContext.ActiveId && !_warm.ContainsKey(a.Id) && !_warming.ContainsKey(a.Id) && AccountStore.Exists(a.Id))
                .OrderByDescending(a => a.LastActiveTicks)   // Fix A 1.2: warm the last-used account first
                .Select(a => a.Id).ToList();
            foreach (var id in ids)
            {
                if (IsDisposed) return;
                if (id == AccountContext.ActiveId || _warm.ContainsKey(id) || _warming.ContainsKey(id)) continue;   // re-check
                _warming[id] = WarmOneAsync(id);   // START it (do NOT await completion) → the warm-ups overlap
                System.Diagnostics.Debug.WriteLine("[WARMCONN] warming started id=" + id);
                await System.Threading.Tasks.Task.Delay(WarmStartStaggerMs);   // small stagger between STARTS only
            }
        }

        /// <summary>Warms ONE account into a resident service, registering it in _warm on success. Tracked in _warming
        /// while in flight (Fix B: a switch to this mid-warm-up account awaits THIS task, then rebinds). Removes itself
        /// from _warming on completion. Never throws (fire-and-forget).</summary>
        private async System.Threading.Tasks.Task<TelegramService> WarmOneAsync(long id)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var svc = await TelegramService.CreateWarmServiceAsync(id, RouteUpdate);
                if (IsDisposed) { if (svc != null) { try { await svc.DisposeWarmServiceAsync(); } catch { } } return null; }
                if (svc != null && id != AccountContext.ActiveId && !_warm.ContainsKey(id))
                {
                    _warm[id] = svc;
                    Logger.Diag("[WARMCONN] warm id=" + id + " READY in " + sw.ElapsedMilliseconds + "ms");   // tune WarmupWaitMs against this
                    return svc;
                }
                // ACCOUNT-RECOVERY-SAFETY (Bug 1, FOURTH drop site — was still the OLD synchronous path).
                // This branch fires when the warm target BECAME THE ACTIVE ACCOUNT mid-warm-up, i.e. exactly when
                // the active service is opening — or is about to open — the SAME accounts/{id}/session file. The
                // sync DisposeWarmService calls Client.Dispose() with no socket-abort and does NOT await handle
                // release, which is the documented recipe for AUTH_KEY_DUPLICATED / a mid-write corruption. The
                // three switch drop sites were fixed in v1.1; this one was missed.
                // ⚠ SHORTENED, NOT CLOSED: CreateWarmServiceAsync never re-checks ActiveId, so the warm client has
                // already held the session for the whole warm-up by the time we get here. Awaiting the teardown
                // ends the overlap promptly; it does not prevent the overlap. Detector = two [SESSPATH] lines
                // (client-open / warm-open) carrying the SAME session path. Closing it needs an ActiveId re-check
                // or a cancellation token inside CreateWarmServiceAsync — deliberately OUT OF SCOPE here.
                if (svc != null) { try { await svc.DisposeWarmServiceAsync(); } catch { } }   // became active mid-warm, or already warm → drop the extra
                return null;
            }
            catch (Exception ex) { Logger.Diag("[WARMCONN] warm-one id=" + id + " error: " + ex.Message); return null; }
            finally { _warming.Remove(id); }
        }

        /// <summary>Removes + returns the warm service for an id (ownership transfers to the caller), or null.</summary>
        private TelegramService TakeWarm(long id)
        {
            TelegramService svc;
            if (_warm.TryGetValue(id, out svc)) { _warm.Remove(id); return svc; }
            return null;
        }

        /// <summary>Disposes + clears all warm background services (app exit, or around an account-list mutation).</summary>
        private void DisposeAllWarm()
        {
            foreach (var kv in _warm) { try { kv.Value.DisposeWarmService(); } catch { } }
            _warm.Clear();
        }

        /// <summary>INCREMENT 3b: the INSTANT switch — rebind the UI to an already-warm target service (no teardown, no
        /// reconnect, no handshake). The outgoing active service is PARKED into the warm pool (stays connected + live; its
        /// routed UM self-silences the instant ActiveId flips). The target's UM (already attached + seeded) starts firing
        /// the UI immediately. A fast dialog load on the already-live connection refreshes the list (tier-2; a later
        /// refinement renders straight from cached dialogs for a zero-fetch switch).</summary>
        private async System.Threading.Tasks.Task RebindToWarmAsync(TelegramService ws)
        {
            var outgoing = _activeService;
            // Only PARK the outgoing if it's still a live account. LogOut() detaches the active client (Client=null →
            // IsAuthorized false) + deletes its session BEFORE switching to a remaining account; parking that would
            // zombie the pool (a dead, session-deleted service). A normal switch keeps the outgoing authorized → park it.
            bool parkOutgoing = outgoing != null && outgoing.IsAuthorized;
            // Detach the outgoing (going background): stop its downloads + watchdog + reconnect UI hook. Its routed UM keeps
            // running but self-silences (its id != the new ActiveId). Do NOT tear it down — staying warm is the whole point.
            try { outgoing.CancelAllDownloads("account-switch-background"); } catch { }
            try { outgoing.StopConnectionWatchdog(); } catch { }
            outgoing.ReconnectingChanged -= OnReconnectingChanged;

            // Flip the router pointer (ActiveId) FIRST, then rebind `_activeService` — with NO await between, so this whole
            // block is atomic on the UI thread and a background UM thread only ever observes the (ActiveId, _service) pair
            // as fully-outgoing or fully-target: the target's queued updates process against the target; the outgoing's
            // route silent (correct — it's now background). Any update marshalled mid-block runs (BeginInvoke) after this.
            AccountContext.ActiveId = ws.AccountId;    // flips the router: ws's UM → the UI, outgoing's UM → silent
            AccountContext.LegacyMode = false;
            _activeService = ws;                       // REBIND: every `_service` site now reads the target service
            if (parkOutgoing) _warm[outgoing.AccountId] = outgoing;   // park the outgoing as a warm background service (resident)
            AccountStore.WriteActive(ws.AccountId);
            AvatarStore.SetActive(ws.Avatars);         // secondary forms read the target account's avatars

            if (!parkOutgoing && outgoing != null) { try { outgoing.DisposeWarmService(); } catch { } }   // detached/logged-out → dispose, don't zombie
            ClearActiveAccountView();                  // REBIND-FIX: clear ONLY the outgoing's view/model — NOT the full
                                                       // per-account teardown. ResetPerAccountState's _avatars.Reset()/
                                                       // CancelAllDownloads now target the INCOMING (already-rebound)
                                                       // service, wiping the target's fresh store mid-bind → that's what
                                                       // corrupted the first switch (cold) and forced a second (warm).

            // TIER-1 seamless: paint the target's chat list INSTANTLY from its warm snapshot (no network) so there's no
            // blank/reload gap between accounts. Synchronous right after the clear (no await) → the empty frame never
            // paints. AfterConnectAsync(rebind) then refreshes from the live connection (fast; catches anything that
            // arrived while it was warm) + arms paging + backfills avatars + starts the watchdog.
            if (ws.CachedDialogs != null)
            {
                try
                {
                    _allChats.AddRange(BuildDialogEntries(ws.CachedDialogs));
                    _allChats.Sort((a, b) => b.Date.CompareTo(a.Date));
                    RenderChatList(_searchBox != null ? _searchBox.Text : "");
                }
                catch (Exception ex) { Logger.Diag("[WARMCONN] tier-1 pre-render failed: " + ex.Message); }
            }
            await AfterConnectAsync(rebind: true);     // fast dialog refresh + backfill + watchdog; SKIPS UM attach/seed (ws has both)
        }

        private async System.Threading.Tasks.Task SwitchAccountAsync(long targetId, string targetName)
        {
            if (targetId == 0 || targetId == AccountContext.ActiveId) return;
            PersistDraftForCurrentChat();   // DRAFTS: save the open chat's draft to the OUTGOING account before _service swaps
            long prevId = AccountContext.ActiveId;
            string prevName = _service.Me != null ? DisplayName(_service.Me) : null;
            Logger.Diag("[ACCT] switch start → " + targetId);
            // [SESSPATH] 0.3: the switch intent + the target's resolved session file + the CURRENT service/global ids.
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[SESSPATH] switch-start from=" + prevId + " to=" + targetId
                    + " targetSession=\"" + System.IO.Path.Combine(AccountContext.AccountDir(targetId), "session") + "\""
                    + " serviceAcctId=" + _service.AccountId + " globalActiveId=" + AccountContext.ActiveId);
            // BATCH-TA-0 (A5): LogPaths was DEAD CODE — defined, never called. It adds exists= and bytes=, which the
            // switch-start line above does NOT print, and a 0-byte session is exactly what distinguishes a genuinely
            // corrupt account from a merely contended one. Wired here (target) and at recovery entry.
            AccountContext.LogPaths(targetId, "switch-start");

            int myGen = ++_switchGen;   // WARMUP-FIX B: a newer switch supersedes this one's warm-up wait / commit
            _switchInProgress = true; _switchAborted = false;

            // TIER 1 — target already warm-READY → the SEAMLESS rebind: NO overlay, NO connecting card, NO teardown. A
            // fast liveness ping runs against the CURRENT on-screen UI; on success we swap with no transition screen.
            var ws = TakeWarm(targetId);
            if (ws != null && ws.IsAuthorized && await ws.PingAliveAsync())
            {
                if (myGen != _switchGen) { _warm[targetId] = ws; return; }   // superseded during the ping → return it to the pool
                Logger.Diag("[WARMCONN] REBIND (seamless) → " + targetId + " — no overlay/teardown/handshake");
                _switchInProgress = false;
                await RebindToWarmAsync(ws);
                HideSwitchOverlay();   // clean up any overlay a superseded prior switch left showing
                Logger.Diag("[ACCT] switch done (rebind) → " + targetId);
                return;
            }
            // ACCOUNT-RECOVERY-SAFETY (Bug 1): warm but not adoptable → AWAIT its full teardown (socket reset + dispose +
            // handle release) BEFORE the cold path reopens the SAME session file, or two clients race one session →
            // AUTH_KEY_DUPLICATED / corruption (what auto-deleted account A). Sync DisposeWarmService did NOT await release.
            if (ws != null) { try { await ws.DisposeWarmServiceAsync(); } catch { } }

            // TIER 2 — WARMUP-FIX B: target is MID-warm-up → WAIT briefly for THAT warm-up to finish, then REBIND. No cold
            // teardown, no connecting card — just the calm switch overlay while it lands. Only a not-warming target or a
            // timeout falls through to the cold path. Passive wait (await the warm task) — it never triggers a teardown.
            System.Threading.Tasks.Task<TelegramService> warming;
            if (_warming.TryGetValue(targetId, out warming) && warming != null)
            {
                Logger.Diag("[WARMCONN] target " + targetId + " mid-warm-up → wait ≤" + WarmupWaitMs + "ms, then rebind");
                ShowSwitchOverlay(targetId, targetName);
                await System.Threading.Tasks.Task.WhenAny(warming, System.Threading.Tasks.Task.Delay(WarmupWaitMs));
                if (myGen != _switchGen) return;   // user switched again → the newer switch owns the overlay + state
                var ws2 = TakeWarm(targetId);      // the warm task adds to _warm on success; null if it failed or isn't done yet
                if (ws2 != null && ws2.IsAuthorized && await ws2.PingAliveAsync())
                {
                    if (myGen != _switchGen) { _warm[targetId] = ws2; return; }
                    Logger.Diag("[WARMCONN] REBIND (after warm-up wait) → " + targetId);
                    _switchInProgress = false;
                    await RebindToWarmAsync(ws2);
                    HideSwitchOverlay();
                    Logger.Diag("[ACCT] switch done (rebind-after-wait) → " + targetId);
                    return;
                }
                if (ws2 != null) { try { await ws2.DisposeWarmServiceAsync(); } catch { } }   // AWAIT release before cold reopen (Bug 1)
                Logger.Diag("[WARMCONN] warm-up wait didn't land for " + targetId + " → cold");
                // overlay stays up; the cold path below reuses it (idempotent) + adds the connecting card
            }

            // TIER 3 — COLD PATH (target not warm, not warming, or the wait didn't land): NOW show the overlay + connect
            // card to mask the slow teardown → handshake → reload.
            if (_abortConnect != null) { try { _abortConnect.Dispose(); } catch { } }
            _abortConnect = new System.Threading.CancellationTokenSource();
            ShowSwitchOverlay(targetId, targetName);   // calm transition masks teardown→connect→reload
            ShowConnecting("Switching to " + (targetName ?? "account") + "…");

            await _service.TeardownForSwitchAsync();
            ResetPerAccountState();
            AccountContext.ActiveId = targetId;     // repoints session path AND the cache root (Cache/{id})
            _service.AccountId = targetId;          // MULTI-ACCOUNT (3b): the reused service now resolves the TARGET's paths
            // WARMUP-FIX B (race guard): a mid-warm-up target could have finished DURING the teardown above and landed
            // in _warm. Now that we're cold-connecting the ACTIVE service to it, drop that redundant warm service —
            // two clients on one session = AUTH_KEY_DUPLICATED. (After ActiveId=targetId, WarmOneAsync self-drops any
            // later completion via its `id != ActiveId` check, so this closes the only window.)
            var staleWarm = TakeWarm(targetId);
            if (staleWarm != null) { try { await staleWarm.DisposeWarmServiceAsync(); } catch { } }   // AWAIT release before cold reopen (Bug 1)
            AccountContext.LegacyMode = false;
            AccountStore.WriteActive(targetId);

            // ACCOUNT-RECOVERY-SAFETY (Bug 1): final gate — do NOT cold-open the target session until its file is actually
            // UNLOCKED (any just-dropped warm client's handle fully released), so two clients never touch one session file.
            await AccountStore.WaitSessionUnlockedAsync(targetId);
            bool connected = await ConnectResilientlyAsync("Switching to " + (targetName ?? "account") + "…");
            _switchInProgress = false;

            if (!connected && _switchAborted && prevId != 0)
            {
                // Cancel → restore the previously-active account (it's still valid; just reconnect on it).
                Logger.Diag("[ACCT] switch cancelled → restoring active=" + prevId);
                _abortConnect = null;                 // the restore must NOT auto-abort
                AccountContext.ActiveId = prevId;
                _service.AccountId = prevId;           // MULTI-ACCOUNT (3b): restore → resolve the previous account's paths
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
            Logger.Diag("[ACCT] switch done (cold) → " + targetId);
        }

        /// <summary>Clears the ACTIVE account's on-screen VIEW + in-memory render model (open chat, chat list,
        /// per-chat caches) so the next account renders fresh with no cross-account mixing — and stops the previous
        /// account's audio/inline video as part of that teardown. SAFE on a seamless REBIND: it touches ONLY MainForm
        /// UI/model state, NEVER the (now already-incoming) service's client/UM/avatar store.</summary>
        private void ClearActiveAccountView()
        {
            CloseDrawer();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: before AudioPlayer.Stop");
            try { AudioPlayer.Stop(); } catch { }
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after AudioPlayer.Stop; before ClearMessagePanel");
            _selectedChat = null; _selectedItem = null;
            // ACCOUNT-RECOVERY-SAFETY (Bug 3): drop the header-avatar reference NOW. It points at a bitmap OWNED by the
            // outgoing account's avatar store, which ResetPerAccountState is about to DISPOSE — leaving a pending header
            // repaint drawing a disposed Image ("Parameter is not valid", fatal in a Paint handler). Null it → the next
            // paint shows the initials circle; SetHeaderAvatar reloads a fresh one when a chat opens.
            _headerAvatarImg = null; _headerAvatarPeerId = 0; _headerAvatarTitle = null;
            if (_chatSearchBtn != null) _chatSearchBtn.Visible = false;   // INCHAT-SEARCH: no open chat → no header magnifier
            if (_chatMenuBtn != null) _chatMenuBtn.Visible = false;       // TA-21/S1a: …and no header ⋮ (it acts on the open chat)
            if (_dockBtn != null) _dockBtn.Visible = false;               // TA-23/D1c: …nor the dock toggle
            SetDockOpen(false);                                           // an open dock with no chat has nothing to show
            ClearMessagePanel();                         // disposes message bubbles + per-chat photo caches
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after ClearMessagePanel");
            _currentChatMessages.Clear();
            foreach (Control c in _chatListPanel.Controls.OfType<Control>().ToArray()) { _chatListPanel.Controls.Remove(c); c.Dispose(); }
            _allChats.Clear();
            _shownMessageIds.Clear();
            _albumBubbles.Clear();
            _peerNames.Clear();
            foreach (var img in _customEmojiCache.Values) { try { img.Dispose(); } catch { } }
            _customEmojiCache.Clear();
            _photoCachePaths.Clear();
            _pinnedMessages = null; _pinnedChatId = 0;
            if (_pinnedBar != null) _pinnedBar.Visible = false;
            _jumpUnread = 0;
            _inChatSearchEntry = null;   // INCHAT-SEARCH: any scoped search belongs to the outgoing account
            // STORIES: the tray is per-account — drop the old account's cache token + chips (the incoming account
            // refetches on its AfterConnectAsync, so the tray never shows the previous account's stories).
            _storiesState = null;
            _storyPeers = new List<StoryTrayEntry>();
            if (_storyTrayBar != null && !_storyTrayBar.IsDisposed)
            {
                foreach (var c in _storyTrayBar.Controls.Cast<Control>().ToArray()) c.Dispose();
                _storyTrayBar.Controls.Clear();
            }
            ShowStoryTray(false);
        }

        /// <summary>FULL per-account teardown for the COLD / logout / add-account switch (teardown-and-rebuild model):
        /// the view clear PLUS disposing the OUTGOING account's transfers + avatar store. Correct ONLY where
        /// <c>_service</c>/<c>_avatars</c> are still the account being LEFT (after TeardownForSwitchAsync / logout).
        /// A seamless REBIND must NOT call this — REBIND-FIX: there <c>_service</c>/<c>_avatars</c> are already the
        /// INCOMING target, per-instance avatar stores never bleed (increment 1), so <c>_avatars.Reset()</c> would
        /// wipe the target's fresh store mid-bind (it corrupted the first switch → the "needs two switches" bug).</summary>
        private void ResetPerAccountState()
        {
            ClearActiveAccountView();
            // ACCOUNT teardown is the ONE place background downloads must die: a cross-account transfer writing into a
            // switched cache root would violate isolation (DOWNLOAD-UX 2.1 invariant).
            try { _service.CancelAllDownloads("account-switch"); } catch { }
            _avatars.Reset();   // account-scoped ids + disk root → clear the OUTGOING store (disposes cached bitmaps)
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

                    // A MID-SESSION DROP had no visible signal beyond this window title — easy to miss,
                    // and on a direct connection the pill was hidden entirely. Drive the shared state so
                    // the pill appears and says "Connecting…" while the link is being re-established.
                    // (Recovery is reported by NoteActivity when traffic resumes — whoever re-established
                    // it, including WTC's own reactor, which never routes through our connect loop.)
                    if (reconnecting) ProxyStatus.NoteAttempt();
                    RefreshProxyPill();
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

        /// <summary>BATCH-TA-16f/F1 — lays out the connecting overlay's action row. There are THREE possible
        /// buttons and room for two, so this is the one place that decides which pair is showing.
        ///
        /// DURING A SWITCH: Retry + Cancel. Cancel wins the second slot because the user is NOT stranded —
        /// aborting restores the account that was already working, which is the more urgent escape hatch.
        /// OTHERWISE (the ordinary blocked-network resume): Retry + Proxy. THIS is the stranded case F1 is
        /// about — no chat list, no pill (D3 hides it under this overlay), and previously no way to reach
        /// proxy settings at all.
        /// Called from both construction and ShowConnecting, so the two can never disagree.</summary>
        private void LayoutConnectingButtons()
        {
            if (_connectingPanel == null || _retryButton == null) return;
            bool switching = _switchInProgress;
            if (_switchCancelButton != null) _switchCancelButton.Visible = switching;
            if (_proxyOverlayButton != null) _proxyOverlayButton.Visible = !switching;

            _retryButton.Location = new Point(_connectingPanel.Width / 2 - _retryButton.Width - 6, 118);
            var second = switching ? _switchCancelButton : _proxyOverlayButton;
            if (second != null) second.Location = new Point(_connectingPanel.Width / 2 + 6, 118);
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
            // BATCH-TA-16f/F1 — the two buttons share the row, so Retry no longer centres alone.
            _retryButton.Location = new Point(_connectingPanel.Width / 2 - _retryButton.Width - 6, 118);
            _retryButton.Click += (s, e) =>
            {
                var c = _retryNowCts;   // cut the backoff wait → retry immediately
                if (c != null) { try { c.Cancel(); } catch { } }
            };

            // ⚠ BATCH-TA-16f/F1 — THE SECOND STRANDED SURFACE. This overlay is what a user stares at when
            // the network is blocking Telegram ("Waiting for network — make sure your VPN is on"), and it
            // covers the chat list, so the floating proxy pill is deliberately hidden underneath it
            // (TA-16d/D3: two connection indicators disagreeing reads as a bug). The consequence, missed
            // until now, is that this screen offered ONLY "Retry now" and "Cancel" — retrying forever on a
            // network that will never work, with the one setting that could fix it unreachable. A proxy is
            // needed EXACTLY here. Opens the same ProxyForm as the login pill and the Settings row.
            _proxyOverlayButton = new MaterialButton
            {
                Text = "Proxy", Type = MaterialButton.MaterialButtonType.Outlined,
                UseAccentColor = false, AutoSize = false, Size = new Size(120, 40)
            };
            _proxyOverlayButton.Location = new Point(_connectingPanel.Width / 2 + 6, 118);
            _proxyOverlayButton.Click += (s, e) => OpenProxySettings();

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
            _connectingPanel.Controls.Add(_proxyOverlayButton);
            _connectingPanel.Controls.Add(_switchCancelButton);
            Controls.Add(_connectingPanel);
            _connectingPanel.BringToFront();
            LayoutConnectingButtons();   // F1: one owner of the action-row layout

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
            LayoutConnectingButtons();
            if (!_connectingPanel.Visible) _connectingPanel.Visible = true;
            _connectingPanel.BringToFront();
            RefreshProxyPill();   // D3: the overlay owns connection state while it is up → pill hides
            CenterConnectingPanel();
            if (_connectingDots != null && !_connectingDots.Enabled) _connectingDots.Start();
        }

        private void HideConnecting()
        {
            if (_connectingDots != null) _connectingDots.Stop();
            if (_connectingPanel != null) _connectingPanel.Visible = false;
            RefreshProxyPill();   // D3: overlay gone → the pill may report the proxy dimension again
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

        /// <summary>ACCOUNT-RECOVERY-SAFETY: the active account's session came back "unreadable". This NEVER auto-deletes the
        /// account (that turned a transient switch-race corruption into permanent loss). Instead: (1) a CLEAN RETRY — release
        /// every handle on the session file and reconnect ONCE (a warm/cold-race contention usually clears here → the account
        /// is KEPT); (2) only if it's STILL corrupt, MOVE THE SESSION ASIDE (rename to accounts/{id}.corrupt-&lt;ts&gt;, preserved
        /// + recoverable) and fall back to another account / the LoginForm. A non-corrupt (network) retry failure leaves the
        /// account untouched. Bounded (one retry + one move-aside per account) → never loops.</summary>
        private async System.Threading.Tasks.Task RecoverCorruptSessionAsync()
        {
            long badId = AccountContext.ActiveId;
            bool legacy = AccountContext.LegacyMode || badId == 0;
            bool stillCorrupt = true;   // we were invoked because a corrupt signal fired; the retry may clear it
            // BATCH-TA-0 (A5): exists= / bytes= at recovery entry — a 0-byte session is the signature of the
            // truncating two-client race, and nothing else in the [RECOVERY] trace reports the file's size.
            AccountContext.LogPaths(badId, "recover-entry");

            // (1) CLEAN RETRY (once per account, real accounts only). A "corrupt" connect is USUALLY transient contention —
            // a just-dropped warm client's handle still releasing, or a mid-write from the switch race — not permanent rot.
            // Release EVERY handle on the session file, confirm it's unlocked, then reconnect once. If it connects, the
            // account is KEPT and NOTHING is deleted or moved.
            if (!legacy && badId != 0 && _recoveryRetried.Add(badId))
            {
                LogRecovery("session corrupt id=" + badId + " → clean-retry (release all handles + reconnect once, NO delete)");
                await _service.DiscardFaultedClientAsync();                 // dispose the faulted client + wait for its file lock to release
                var warmOnBad = TakeWarm(badId);                            // drop any warm client STILL on this session (the race source)
                if (warmOnBad != null) { try { await warmOnBad.DisposeWarmServiceAsync(); } catch { } }
                await AccountStore.WaitSessionUnlockedAsync(badId);         // confirm the file is truly free before reopening
                _connectCorrupt = false;
                if (await ConnectResilientlyAsync("Reconnecting…"))
                {
                    LogRecovery("clean-retry SUCCEEDED id=" + badId + " → account KEPT (corruption was transient; NOT deleted)");
                    HideConnecting();
                    await AfterConnectAsync();
                    return;
                }
                stillCorrupt = _connectCorrupt;   // true → genuinely unreadable (move aside); false → network/other (leave in place)
                LogRecovery("clean-retry did not connect id=" + badId + " stillCorrupt=" + stillCorrupt);
            }

            // (2) FALL BACK — never delete. Move the session ASIDE only if it's still genuinely corrupt (preserved, recoverable);
            // a non-corrupt (network) failure leaves the account's files untouched so it recovers when the connection returns.
            _recoveryTried.Add(badId);
            HideConnecting();
            await _service.TeardownForSwitchAsync();   // release the file lock BEFORE any rename
            if (stillCorrupt)
            {
                string moved = legacy ? AccountStore.MoveLegacySessionAside()
                                      : (badId != 0 ? AccountStore.MoveAccountDirAside(badId) : null);
                LogRecovery("session still unreadable id=" + badId + " → MOVED ASIDE to \"" + (moved ?? "(left in place)") + "\" (PRESERVED, NOT deleted)");
            }
            else LogRecovery("retry failed but NOT corrupt id=" + badId + " → account left in place (no move-aside), falling back");
            _connectCorrupt = false;
            AccountContext.LegacyMode = false;
            AuthManager.Reset();

            // Pick a VALID candidate NOT already tried in this chain and NOT mid-background-delete — so we can't
            // cycle the same broken accounts forever (the bound), nor race the logout cleanup on a deleting account.
            var others = AccountStore.ListAccounts()
                .FindAll(a => a.Id != badId && !_recoveryTried.Contains(a.Id) && !AccountStore.IsDeleting(a.Id));
            if (others.Count > 0)
            {
                LogRecovery("falling back → switching to " + others[0].Id);
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(others[0].Id);
                await SwitchAccountAsync(others[0].Id, others[0].Name);   // if it's ALSO corrupt → recurses, but each id is tried once → terminates
            }
            else
            {
                LogRecovery("no untried valid account left → LoginForm");
                _recoveryTried.Clear();
                _recoveryRetried.Clear();
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
            {
                // MULTI-ACCOUNT (increment 3b): route through RouteUpdate keyed by THIS service's OWN id (captured in
                // `svc`, not read off the live `_service`) — so when this service later becomes a warm/background one
                // (after a rebind) its updates self-silence instead of firing the UI. Behavior-neutral today: the
                // active service's AccountId == the active id → RouteUpdate forwards straight to OnManagerUpdate.
                var svc = _service;
                svc.StartUpdateManager(u => RouteUpdate(svc, u));
            }
        }

        /// <summary>MULTI-ACCOUNT (increment 3b + NOTIFY-BACKGROUND): an update arrived on <paramref name="svc"/>. If it's
        /// the ACTIVE account → the UI handler; a warm (background) account → raise ITS OWN notification (respecting THAT
        /// account's effective-mute, tagged by account). Keyed by svc.AccountId vs the active id.</summary>
        private System.Threading.Tasks.Task RouteUpdate(TelegramService svc, Update u)
        {
            if (svc != null && svc.AccountId == AccountContext.ActiveId) return OnManagerUpdate(u);
            // NOTIFY-BG-MUTE-FIX: keep the BACKGROUND account's OWN mute state current (its warm CachedDialogs snapshot is
            // frozen at seed) so its mute-gate honors a mute changed while backgrounded — else a muted chat leaks a toast.
            if (svc != null && u is UpdateNotifySettings bns) { try { svc.ApplyNotifyUpdate(bns.peer, bns.notify_settings); } catch { } }
            try { RaiseBackgroundNotify(svc, u); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[NOTIFY-BG] route EX: " + ex.Message); }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>NOTIFY-BACKGROUND: a background (non-active) account received an update. If it's an INCOMING message,
        /// marshal to the UI thread and toast IF that account's own effective-mute allows it (mentions break through),
        /// tagged with the account name. Runs on a UM background thread → BeginInvoke for the toast.</summary>
        private void RaiseBackgroundNotify(TelegramService svc, Update u)
        {
            if (svc == null) return;
            Message m = u is UpdateNewMessage unm ? unm.message as Message
                      : u is UpdateNewChannelMessage ucm ? ucm.message as Message : null;
            if (m == null || (m.flags & Message.Flags.out_) != 0 || m.peer_id == null) return;   // no message / own outgoing
            long acctId = svc.AccountId;
            var peer = m.peer_id;

            // ⚠ BATCH-TA-32/M3 — THE MASTER-MUTE CHECK MOVED DOWN HERE, AND THAT IS THE POINT OF IT.
            //   It used to sit on the first line, BEFORE the message was even extracted and before the
            //   BeginInvoke below — so with the master switch on, BackgroundToast was never invoked at all
            //   and a suppression logged THERE could never fire for a background account. The switch would
            //   have looked logged while being silent for half the accounts in the app, which is exactly
            //   the "notifications stopped working" report this batch exists to make diagnosable.
            //   Extracting the message first costs two type tests and two field reads; it does NOT cost the
            //   marshal, because the return below still happens before BeginInvoke.
            //   Safe off the UI thread: NotifyLog → Logger.Diag → Trace.WriteLine, and Logger.Enabled is
            //   volatile and documented for any thread (Logger.cs:12).
            if (!AppSettings.Instance.EnableNotifications)
            { NotifyLog("suppressed(bg)", peer.ID, m.ID, "master"); return; }

            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => { try { BackgroundToast(svc, acctId, peer, m); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[NOTIFY-BG] toast EX: " + ex.Message); } }));
        }

        /// <summary>NOTIFY-BACKGROUND (UI thread): the tagged toast for a background account, gated by ITS effective-mute
        /// (mentions break through). De-duped per (account, peer, msg) so two accounts in the same group both notify.
        /// Stores (account, peer) so a click switches to that account + opens the chat.</summary>
        private void BackgroundToast(TelegramService svc, long acctId, Peer peer, Message m)
        {
            if (svc == null || peer == null || m == null) return;
            if (!CanDeliverNotification()) return;           // TA-27/W7: the tray guard, split
            if (acctId == AccountContext.ActiveId) return;   // became active mid-flight → its own path handles it (no double)
            // TA-32/M2+M3 — the master switch, on this path too and LOGGED. Normally RaiseBackgroundNotify
            // has already returned before we get here; this is the belt-and-braces copy for the case where
            // the user flips the switch off between that check and this BeginInvoke landing.
            if (!AppSettings.Instance.EnableNotifications)
            { NotifyLog("suppressed(bg)", peer.ID, m.ID, "master"); return; }
            long peerId = peer.ID;

            // ⚠ BATCH-TA-26/B1 — THE SAME BACKLOG GATE AS THE ACTIVE PATH, AND IT MATTERS MORE HERE.
            // Every warm account runs its own UpdateManager off its own persisted state file, so on a cold
            // start EACH of them replays its own offline backlog. Fixing only the active account would have
            // left a two-account user with the identical burst, just attributed to account B.
            // TA-26a/S1 — same as the active path: the sender marked it silent, so no ping. The unread
            // badge for this account is unaffected.
            if ((m.flags & Message.Flags.silent) != 0)
            { NotifyLog("suppressed(bg)", peerId, m.ID, "silent"); return; }

            if (IsBacklog(m)) { NotifyLog("suppressed(bg)", peerId, m.ID, "backlog"); return; }

            var key = (acctId, peerId, m.ID);
            if (_bgToastSeen.Contains(key)) { NotifyLog("suppressed(bg)", peerId, m.ID, "dup"); return; }
            _bgToastSeen.Add(key); _bgToastSeenOrder.Enqueue(key);
            while (_bgToastSeenOrder.Count > ToastSeenCap) _bgToastSeen.Remove(_bgToastSeenOrder.Dequeue());

            // TA-26c — the SAME client-side mention test as the active path. A background account's muted
            // chat must break through on a mention for the same reason the active one does.
            string mentionHow;
            bool mentioned = MentionsMe(m, svc.Me, out mentionHow);
            // Already the correct resolver — TA-26/B2 changed only its FAILURE direction (unknown ⇒ silent).
            if (!mentioned && svc.IsPeerEffectivelyMuted(peer))
            { NotifyLog("suppressed(bg)", peerId, m.ID, "muted" + (m.reply_to != null ? " reply=1" : "")); return; }

            // TA-26b/D4 — same deciding factor as the active path (see MaybeToast's emit line).
            Logger.Diag("[NOTIFY-BG] toast acct=" + acctId + " peer=" + peerId + " msg=" + m.ID
                        + " reason=" + (mentioned ? "mention:" + mentionHow : "not-muted"));
            // TA-27/W6 — the SAME builder and the SAME emitter as the active path. The account name is
            // what distinguishes a background notification, and it is the only difference left.
            EmitNotification(BuildNotification(acctId, peerId, m, BgPeerTitle(svc, peer) ?? "Chat",
                                               svc.Me != null ? DisplayName(svc.Me) : ("Account " + acctId),
                                               mentioned));
        }

        private string BgPeerTitle(TelegramService svc, Peer peer)
        {
            try
            {
                var info = svc.Updates != null ? svc.Updates.UserOrChat(peer) : null;
                if (info is User u) return DisplayName(u);
                if (info is ChatBase cb) return cb.Title;
            }
            catch { }
            return null;
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
                        // TA-6b/F: a throwing handler branch is a PERMANENTLY LOST update — UpdateManager
                        // commits local.pts BEFORE calling us (UpdateManager.cs:182/223/281) and RaiseUpdate
                        // (:519-530) discards whatever we throw, so getDifference will never re-deliver it.
                        // Until now this was recorded with Debug.WriteLine, i.e. invisible in Release (R7) —
                        // the exact build where it matters. Now it names the update type in the Release log.
                        // NOT rethrown here, deliberately: this lambda runs on the UI thread under BeginInvoke,
                        // NOT on WTC's callback stack, so a rethrow would reach Application.ThreadException and
                        // put a crash dialog in front of the user rather than being swallowed by WTC. The
                        // rethrow the batch asked for belongs on the outer catch below, which IS WTC's stack.
                        try { ProcessSingleUpdate(update); }
                        catch (Exception ex)
                        {
                            Logger.Diag("[UM] HANDLER EX type=" + (update != null ? update.GetType().Name : "null")
                                + " — UPDATE PERMANENTLY LOST (pts already committed; getDifference cannot recover it): " + ex);
                            CrashLog.RecordThrottled("ProcessSingleUpdate", ex);
                        }
                    }));
                // TA-6b/D: the guard's ELSE was silent. An update arriving before the handle exists, or
                // after Dispose, is DISCARDED here — and (see the commit message) the UpdateManager has
                // already advanced its pts/seq by the time our callback returns, so getDifference will
                // NOT bring it back. That makes this an unrecoverable hole, not a deferral. Logging it is
                // not the fix; it is how we find out whether it ever actually happens in the field.
                else if (Logger.Enabled)
                    Logger.Diag("[UM] UPDATE DROPPED (window not ready) type="
                        + (update != null ? update.GetType().Name : "null")
                        + " handleCreated=" + IsHandleCreated + " disposed=" + IsDisposed);
            }
            catch (Exception ex)
            {
                // TA-6b/F: THIS one is on WTC's callback stack, so the rethrow is safe — RaiseUpdate
                // (UpdateManager.cs:519-530) catches and logs it internally. Log first: WTC's own
                // Helpers.Log is not wired into our FileLog, so without this line the throw is invisible.
                Logger.Diag("[UM] CALLBACK EX type=" + (update != null ? update.GetType().Name : "null") + ": " + ex);
                throw;
            }
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
                // NOTIFY-BG-MUTE-FIX: also fold the change into THIS (active) service's own map/defaults, so when this
                // account is later switched to the background its mute-gate is already current (no re-warm needed).
                _service.ApplyNotifyUpdate(uns.peer, uns.notify_settings);
                // Per-peer OR category-level (muting "All groups" etc. in the official client) — both live.
                if (uns.peer is NotifyPeer np && np.peer != null)
                    HandleNotifySettings(np.peer.ID, uns.notify_settings);
                else if (uns.peer is NotifyUsers) { _muteDefUsers = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue; ReapplyEffectiveMutes(); }
                else if (uns.peer is NotifyChats) { _muteDefChats = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue; ReapplyEffectiveMutes(); }
                else if (uns.peer is NotifyBroadcasts) { _muteDefBroadcasts = MuteUntilOf(uns.notify_settings) ?? DateTime.MinValue; ReapplyEffectiveMutes(); }
            }
            else if (update is UpdateDraftMessage udraft)   // DRAFTS: cross-device sync — a draft changed here or on another device
                HandleDraftUpdate(udraft.peer, udraft.draft);
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
            else if (update is UpdateMessageReactions umr)   // QUICKWINS-1 PART 2: others' reactions changed live
            {
                HandleReactionsUpdate(umr.peer?.ID ?? 0, umr.msg_id, umr.reactions);      // re-render pills (open chat)
                HandleReactionIndicator(umr.peer?.ID ?? 0, umr.reactions);                // MENTION-REACTION: light the row heart
            }
            else if (update is UpdateChannelReadMessagesContents urcc)   // MENTION-REACTION: mention read on another device
                HandleMentionsReadElsewhere(urcc.channel_id);
            else if (update is UpdateChannelMessageViews ucmv)   // CHANNEL-META-EXTRAS (1): live view count on channel posts
                HandleChannelViews(ucmv.channel_id, ucmv.id, ucmv.views);
            // DIALOG-LIVE-UPDATES: a dialog left/kicked/deleted from ANOTHER device. UpdateChannel carries the new
            // membership on the channel/supergroup entity (left flag, or ChannelForbidden on kick/ban); UpdateChat is
            // the basic-group equivalent. Drop the stale list row live instead of waiting for a manual refresh.
            else if (update is UpdateChannel uchn)
                HandleChannelStateUpdate(uchn.channel_id);
            else if (update is UpdateChat ucht)
                HandleBasicChatStateUpdate(ucht.chat_id);
            // ── TA-6b/G2: THE DEFAULT ARM ────────────────────────────────────────────────────────────
            // This chain had NO else, so every update type we do not handle was dropped in complete
            // silence — and the only per-update trace above uses Debug.WriteLine, which Release strips
            // (rail R7). On the shipped build there was literally no evidence an update had arrived,
            // let alone been ignored. BATCH-TA-7's coverage matrix (rename, avatar, pin/unpin, folder
            // membership — all unhandled) had to be assembled by reading code, because the running app
            // could not report it. This one arm makes that class of gap self-reporting: the next time
            // something "doesn't update live", grep the Release log for [UPDATE] UNHANDLED.
            // Deduped per session so a chatty type (typing, view counts) cannot flood the log; the
            // FIRST occurrence of each distinct type is what tells you the handler is missing.
            // Logging only — no behaviour change, no update is consumed differently.
            else if (Logger.Enabled)
            {
                var __tn = update != null ? update.GetType().Name : "null";
                if (_unhandledUpdateTypes.Add(__tn))
                    Logger.Diag("[UPDATE] UNHANDLED type=" + __tn + " (first this session; no handler in ProcessSingleUpdateCore)");
            }
        }

        /// <summary>TA-6b/G2: distinct update type names already reported as unhandled, so each is logged
        /// once per session rather than once per occurrence. UI-thread only (ProcessSingleUpdateCore is
        /// always reached via BeginInvoke), so a plain HashSet needs no lock.</summary>
        private readonly HashSet<string> _unhandledUpdateTypes = new HashSet<string>();

        /// <summary>MUTE-PREDICATE-FIX: a peer is muted IFF it's set SILENT (has_silent + silent=true — mute_until may be
        /// 0/past for these, the missing "7"), OR mute_until is in the FUTURE (a timed mute OR the 9999 "forever" max).
        /// mute_until is a UTC unix timestamp compared UTC-to-UTC (no local skew). Flag-absent mute_until = MinValue
        /// (past) → not muted; an EXPIRED timed mute (mute_until &lt; now) correctly reads UNMUTED.</summary>
        private static bool IsMuted(PeerNotifySettings ns)
        {
            if (ns == null) return false;
            if ((ns.flags & PeerNotifySettings.Flags.has_silent) != 0 && ns.silent) return true;   // "silent" mute
            return ns.mute_until > DateTime.UtcNow;   // timed/forever mute; flag-absent mute_until = MinValue = false
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
            ReapplyEffectiveMutes();   // MUTE-EFFECTIVE: category-muted chats now paint the bell (the icon was per-peer only)

            // ⚠ BATCH-TA-26/B2 — THE ACTIVE ACCOUNT MUST POPULATE THE **SERVICE'S** STATE TOO.
            // The notify gate now asks svc.IsPeerEffectivelyMuted, which reads the SERVICE's MuteDef* /
            // _liveNotify / CachedDialogs — not the three MainForm fields set just above. Those fields still
            // drive the row bell icon, but they are invisible to the gate. Without this call the service's
            // defaults stay MinValue for the ACTIVE account and every category mute ("mute all groups") would
            // be ignored by the gate — a regression the routing change would otherwise have introduced.
            // Both are idempotent (LoadNotifyDefaultsAsync has a _notifyDefaultsLoaded latch).
            try { await _service.LoadNotifyDefaultsAsync(); } catch { }
            try { await _service.SeedNotifyExceptionsAsync(); } catch { }   // TA-26/B3

            if (LogOn) System.Diagnostics.Debug.WriteLine("[NOTIFY] category defaults: users="
                + _muteDefUsers.ToString("u") + " chats=" + _muteDefChats.ToString("u") + " broadcasts=" + _muteDefBroadcasts.ToString("u")
                + " → effective-muted=" + _allChats.Count(ComputeEffectiveMuted) + "/" + _allChats.Count);
        }

        // ⚠ BATCH-TA-26/B2 — `IsEffectivelyMuted(ChatEntry, out string)` WAS DELETED HERE, DELIBERATELY.
        // It was the notify gate's mute resolution and it is the bug: it opened with
        //     if (entry == null) return false;   // "unknown chat → can't be muted"
        // and its caller looked the entry up in `_allChats`, which holds one page of ~100 dialogs until the
        // list is scrolled. A muted chat further down was never found, so the gate answered "not muted".
        // The gate now asks TelegramService.IsPeerEffectivelyMuted — no UI list, and it fails CLOSED.
        // It is removed rather than left unused because an unreferenced helper that answers this question
        // WRONGLY is an invitation to reuse it. ComputeEffectiveMuted (below) is the surviving `_allChats`
        // reader and is fine: it only decides which BELL ICON a row paints, where being wrong costs a glyph.

        /// <summary>MUTE-EFFECTIVE: the mute state the ROW ICON should show — the peer's EXPLICIT setting when present
        /// (future=muted, past/0=unmuted, overriding the category), else the peer-kind CATEGORY default. This is what
        /// makes a channel/group muted via a muted category (e.g. "mute all channels" on another device) paint the
        /// bell — the old entry.Muted was per-peer-explicit ONLY. Reads MuteUntil (not entry.Muted) → not sticky.</summary>
        private bool ComputeEffectiveMuted(ChatEntry entry)
        {
            if (entry == null) return false;
            if (entry.MuteUntil.HasValue) return entry.MuteUntil.Value > DateTime.UtcNow;   // explicit setting decides
            DateTime cat = entry.PeerInfo is User ? _muteDefUsers
                : entry.PeerInfo is Channel bc && (bc.flags & Channel.Flags.broadcast) != 0 ? _muteDefBroadcasts
                : entry.PeerInfo != null ? _muteDefChats
                : entry.IsGroup ? _muteDefChats : _muteDefUsers;   // no explicit setting → inherit the peer-kind category
            return cat > DateTime.UtcNow;
        }

        /// <summary>MUTE-EFFECTIVE: recompute every chat's icon-mute from explicit + category (call once the category
        /// defaults are known / when they change) and repaint the list. UI thread, no RPC.</summary>
        private void ReapplyEffectiveMutes()
        {
            if (_allChats.Count == 0) return;
            foreach (var e in _allChats) e.Muted = ComputeEffectiveMuted(e);
            if (_chatListPanel != null) RenderChatList(_searchBox != null ? _searchBox.Text : "");
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
            entry.MuteUntil = MuteUntilOf(ns);   // explicit-vs-inherited (NOTIFY-FIX) — set BEFORE the effective compute
            entry.Muted = ComputeEffectiveMuted(entry);   // MUTE-EFFECTIVE: icon = explicit setting OR category default
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
            RefreshFolderBadges();   // TA-6b/G1 (DOWN): read on ANOTHER device — the fix that matters most
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
            HandleForumTopicUnread(m, peerId, outgoing);   // FORUM-TOPICS: bump the live per-topic unread badge (fires regardless of _thread)

            // COMMENTS-THREAD: while a comment thread is open, nothing is the "open chat" for live-append — defer live
            // comments (they load on re-open) and let all incoming messages fall to the chat-list update path.
            bool isOpen = _thread == null && _selectedChat != null && peerId == _selectedChat.PeerId;
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
                    // REPLIES-INBOX (live): decorate the just-appended reply with its source + "View in chat". No history
                    // dict here → resolve the source group from the manager cache (best-effort; refines on reopen).
                    if (IsRepliesInbox)
                    {
                        var rhl = m.reply_to as MessageReplyHeader;
                        if (rhl != null && rhl.reply_to_peer_id != null && !_repliesSourceCache.ContainsKey(rhl.reply_to_peer_id.ID))
                        {
                            var gi = ResolvePeer(rhl.reply_to_peer_id);
                            if (gi != null) _repliesSourceCache[rhl.reply_to_peer_id.ID] = gi;
                        }
                        var lb = _messagePanel.Controls.OfType<MessageBubbleControl>().FirstOrDefault(x => x.MessageId == m.ID);
                        if (lb != null) DecorateRepliesBubble(lb, m);
                    }
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
            // BATCH-TA-6/P3 (BUG-2): a folder's unread badge is a function of UnreadCount, so remember
            // whether this message actually moved it. Only this branch changes an unread count — an own
            // message, or one into the open chat, changes no badge and must not pay for a refresh.
            bool unreadChanged = false;
            if (!outgoing && entry != _selectedChat)
            {
                entry.UnreadCount += 1;
                unreadChanged = true;
                // MENTION-REACTION: a mentioned/replied-to message lights the "@" badge (Message.Flags.mentioned covers
                // both @mentions and replies). The break-through-mute NOTIFICATION is decided separately in MaybeToast.
                if (m is Message mm && (mm.flags & Message.Flags.mentioned) != 0) entry.UnreadMentions += 1;
            }

            if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] chat-list refresh peer=" + peerId
                + " preview='" + (entry.Preview ?? "") + "' unread=" + entry.UnreadCount + " out=" + outgoing);

            var item = FindChatItem(peerId);

            // Archive filter (place #2): an archived chat (or one not matching the current view) must
            // NOT appear here — e.g. a pinned+archived chat shows ONLY in Archive, never in All.
            if (!IsVisibleInCurrentView(entry))
            {
                if (item != null) { _chatListPanel.Controls.Remove(item); item.Dispose(); }
                if (unreadChanged) RefreshFolderBadges();   // P3: the chat is off-view but still counts toward its folders' badges
                UpdateTrayTooltip();
                return;
            }

            if (item == null) { RenderChatList(_searchBox.Text); UpdateTrayTooltip(); return; }   // should show but absent

            // Pinned-in-THIS-view chats keep their position; non-pinned move to the TOP OF THE NON-PINNED
            // SECTION — index just after the last pinned-in-view row, never absolute 0.
            //
            // BATCH-TA-5/C3 — DO NOT REPOSITION WHILE THE PANEL IS FILTERED.
            // PinnedBoundary() (:5659) counts leading ChatListItemControls and `else break`s on the first
            // non-row child, so it returns 0 whenever a Label sits at index 0. That is true in BOTH
            // filtered modes: the "CHATS" section header (AddSectionHeader, added by RenderChatListCore
            // when a filter is active) and the in-chat-search scope chip (AddInChatScopeChip, a Label).
            // Repositioning then moved the row to absolute 0 — ABOVE the header and above pinned rows.
            // In-chat search is worse still: those rows are search RESULTS whose Entry.PeerId is the
            // SENDER's id (RenderInChatResults), so FindChatItem above can match a result row that has
            // nothing to do with this chat, and we would move+repaint someone else's row.
            // Both filtered views are rebuilt wholesale when the filter changes, so skipping the move
            // costs only a momentarily stale ORDER inside a filtered list — the row content is still
            // repainted by the Invalidate() below.
            bool filtered = _inChatSearchEntry != null
                            || (_searchBox != null && !string.IsNullOrWhiteSpace(_searchBox.Text));
            if (!filtered && !IsPinnedInView(entry))
                _chatListPanel.Controls.SetChildIndex(item, PinnedBoundary());
            item.Invalidate();

            // BATCH-TA-6/P3 — closes BUG-2. RefreshFolderBadges() was reachable ONLY from the
            // RenderChatList wrapper, so this incremental path (the steady-state per-message path)
            // never updated per-folder unread badges — they went stale until some unrelated event
            // forced a full rebuild. Measured cost of a refresh with 9 folders / 107 chats: 0.19-0.23 ms,
            // against a 200-315 ms full rebuild, so calling it per message is affordable. Gated on
            // unreadChanged so an own message or one into the open chat costs nothing.
            if (unreadChanged) RefreshFolderBadges();

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
                try   // per-row guard: one throwing dialog must not abort the loop (dropping this + all later rows)
                {

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
                else continue;   // unresolved peer (no User/ChatBase) → skip this dialog

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
                    UnreadMentions = dlg?.unread_mentions_count ?? 0,   // MENTION-REACTION: @ badge source
                    UnreadReactions = dlg?.unread_reactions_count ?? 0, // MENTION-REACTION: heart glyph source
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
                    Archived = isArchived,
                    DraftText = (dlg?.draft as DraftMessage)?.message,             // DRAFTS: seed from the dialog (null / DraftMessageEmpty = none)
                    DraftDate = (dlg?.draft as DraftMessage)?.date ?? DateTime.MinValue
                });
                }
                catch { }   // a per-row build throw is swallowed so it can't drop this + all later dialogs
            }
            // MUTE-SYNC diagnostic (gated): the mute-read path (Muted = IsMuted(notify_settings)) is already wired here,
            // so if server-muted chats still show unmuted the question is whether Messages_GetDialogs even delivers the
            // mute. This one-line summary answers it: has_mute_until-flag count vs IsMuted count vs a few examples.
            if (Logger.Enabled)
            {
                int nsPresent = 0, flagSet = 0, silentCount = 0;
                var suspects = new List<string>();   // MUTE-PREDICATE-FIX: has_mute_until set but mute_until NOT future (the "7") — raw values
                foreach (var d in dialogs.Dialogs)
                {
                    var ns = (d as Dialog)?.notify_settings;
                    if (ns == null) continue;
                    nsPresent++;
                    bool hasMU = (ns.flags & PeerNotifySettings.Flags.has_mute_until) != 0;
                    bool hasSilent = (ns.flags & PeerNotifySettings.Flags.has_silent) != 0;
                    if (hasMU) flagSet++;
                    if (hasSilent && ns.silent) silentCount++;
                    if (hasMU && !(ns.mute_until > DateTime.UtcNow))
                        suspects.Add((d.Peer?.ID ?? 0) + "[mu=" + ns.mute_until.ToString("u")
                            + " silent=" + (hasSilent ? ns.silent.ToString() : "-") + "]");
                }
                int mutedCount = list.Count(e => e.Muted);
                System.Diagnostics.Debug.WriteLine("[MUTE] load: " + list.Count + " chats; notify_settings present=" + nsPresent
                    + " has_mute_until=" + flagSet + " silent=" + silentCount + " IsMuted(true)=" + mutedCount
                    + (suspects.Count > 0 ? " | mute_until-set-but-not-future: " + string.Join(" ", suspects.Take(12)) : ""));
            }
            // MUTE-EFFECTIVE: resolve the category-inherited mute for EVERY built entry — paging (LoadMoreDialogsAsync)
            // and archive reuse this and would otherwise show per-peer-only. At first load the category defaults aren't
            // fetched yet (all MinValue → equals IsMuted); FetchNotifyDefaultsAsync's ReapplyEffectiveMutes upgrades then.
            foreach (var e in list) e.Muted = ComputeEffectiveMuted(e);
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
            if (IsDisposed || _chatListPanel == null) return;
            // INCHAT-SEARCH: the scoped results list pages OLDER matches via its own near-bottom loader.
            if (_inChatSearchEntry != null) { CheckInChatPaging(); return; }
            if (_dlgExhausted || _dlgLoadingMore) return;
            // SEARCH-FIX-2 (BUG 1): in SEARCH MODE the panel holds search RESULTS, not the paged dialog list. Dialog
            // load-more would RenderChatList → Controls.Clear() → WIPE the appended search sections (Chats/Messages/
            // Channels/Groups/Sponsored/Go-to). There's nothing to scroll-load in search (public "Show more" is an
            // explicit button), so the load-more is INERT while the search box has text; it re-enables when cleared.
            if (_searchBox != null && !string.IsNullOrWhiteSpace(_searchBox.Text)) return;
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

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  BATCH-TA-14/T1 — THE ONE MERGE RULE. Every path that receives freshly-built ChatEntries for
        //  peers we may already hold uses THIS: the scroll pager and the targeted folder fetch. Written
        //  once so the two cannot drift into disagreeing about what "fresh" means.
        //
        //  It replaces TA-8's P1 defect. The old page merge was:
        //      foreach (var e in fresh) if (known.Add(e.PeerId)) { _allChats.Add(e); }
        //  i.e. a peer ALREADY present was skipped entirely and its freshly-built entry DISCARDED, along
        //  with everything the server had just told us — title, preview, top message, unread/mention/
        //  reaction counts, read watermarks, mute state, pin flags and draft. The expensive rebuild was
        //  not even rendering the freshest data it held.
        //
        //  ⚠ UPDATE IN PLACE, NEVER REPLACE THE OBJECT. ChatEntry identity is load-bearing:
        //    RenderChatListCore compares `entry == _selectedChat` BY REFERENCE, and every live
        //    ChatListItemControl holds an Entry reference. Swapping the object would silently deselect the
        //    open chat and leave existing rows bound to an orphan.
        //
        //  Three field classes, and the two non-obvious ones are both anti-flicker rules:
        //    · SERVER WINS for descriptive state.
        //    · MONOTONIC MAX for read watermarks — a chat read locally must not be marked unread again
        //      just because a merge landed before the read round-tripped.
        //    · NEWEST WINS for drafts — a draft being typed must never be erased by a merge.
        //
        //  ⚠ AND A FOURTH CLASS THAT MUST NOT BE TOUCHED AT ALL: ParticipantsCount, CountFetchTried,
        //    OnlineCount and FocusMessageId are LOCAL state that the dialogs response does not carry.
        //    A naive field-for-field copy would reset ParticipantsCount to 0 and CountFetchTried to false,
        //    blanking the header subtitle and re-arming the lazy count fetch on every merge.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private static void MergeServerEntry(ChatEntry local, ChatEntry fresh)
        {
            if (local == null || fresh == null) return;

            // ── SERVER WINS ──
            local.Title = fresh.Title;
            local.Preview = fresh.Preview;
            local.Date = fresh.Date;
            local.TopMessageId = fresh.TopMessageId;
            local.IsGroup = fresh.IsGroup;
            if (fresh.PeerInfo != null) local.PeerInfo = fresh.PeerInfo;
            // Not in the specified list, added deliberately: a peer's access_hash can rotate, and a stale
            // InputPeer makes every later call for that chat fail. Guarded so a null never clears a good one.
            if (fresh.Peer != null) local.Peer = fresh.Peer;
            local.OnlineUntil = fresh.OnlineUntil;
            local.Muted = fresh.Muted;
            local.MuteUntil = fresh.MuteUntil;
            local.MainPinOrder = fresh.MainPinOrder;
            local.ArchivePinOrder = fresh.ArchivePinOrder;
            local.Archived = fresh.Archived;
            local.UnreadMentions = fresh.UnreadMentions;
            local.UnreadReactions = fresh.UnreadReactions;

            // ── MONOTONIC MAX: read watermarks, with UnreadCount following whichever side won ──
            // If our local inbox watermark is at least the server's, our read is the newer truth and the
            // server's unread_count is stale — keep ours. Only adopt the server's count when it genuinely
            // knows about reads we don't.
            bool serverInboxNewer = fresh.ReadInboxMaxId > local.ReadInboxMaxId;
            local.ReadInboxMaxId = Math.Max(local.ReadInboxMaxId, fresh.ReadInboxMaxId);
            local.ReadOutboxMaxId = Math.Max(local.ReadOutboxMaxId, fresh.ReadOutboxMaxId);
            if (serverInboxNewer) local.UnreadCount = fresh.UnreadCount;
            else if (local.ReadInboxMaxId >= fresh.ReadInboxMaxId && fresh.UnreadCount < local.UnreadCount)
                local.UnreadCount = fresh.UnreadCount;   // server saw a read we hadn't: only ever LOWERS here

            // ── NEWEST WINS: drafts ──
            if (fresh.DraftDate > local.DraftDate)
            {
                local.DraftText = fresh.DraftText;
                local.DraftDate = fresh.DraftDate;
            }

            // ParticipantsCount / CountFetchTried / OnlineCount / FocusMessageId: deliberately untouched.
        }

        /// <summary>BATCH-TA-14/T1: merges a freshly-built batch into _allChats — updating peers we already
        /// hold (never discarding their fresh state) and adding the ones we don't. Returns how many were ADDED;
        /// <paramref name="updated"/> reports how many existing rows were refreshed.</summary>
        private int MergeFreshEntries(System.Collections.Generic.IEnumerable<ChatEntry> fresh, out int updated)
        {
            updated = 0;
            int added = 0;
            if (fresh == null) return 0;
            var byId = new Dictionary<long, ChatEntry>(_allChats.Count);
            foreach (var c in _allChats) byId[c.PeerId] = c;   // last wins; RenderChatListCore dedups by PeerId anyway
            foreach (var e in fresh)
            {
                if (e == null) continue;
                ChatEntry local;
                if (byId.TryGetValue(e.PeerId, out local)) { MergeServerEntry(local, e); updated++; }
                else { _allChats.Add(e); byId[e.PeerId] = e; added++; }
            }
            return added;
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
                // BATCH-TA-14/T1: was an add-only merge that DISCARDED the fresh entry for any peer already
                // present (TA-8's P1 defect — a shipping staleness bug). Now the one shared merge rule.
                int updated;
                int added = MergeFreshEntries(fresh, out updated);
                // BATCH-TA-6/P1 (R7): Logger.Diag, not Debug — Release strips Debug.WriteLine, and the
                // absence of this line is why the TA-5 gate could not confirm from the log that paging
                // had happened and had to fall back to a human eyeball pass.
                if (LogOn) Logger.Diag("[SCROLL] chat-list page merged +" + added + " updated=" + updated
                    + " (total=" + _allChats.Count + ", exhausted=" + _dlgExhausted + ")");
                if (added > 0 || updated > 0)
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
                PerfLog.Boot("Messages_GetDialogs RESPONSE (first page)");

                _allChats.Clear();
                _allChats.AddRange(BuildDialogEntries(dialogs));
                _dlgLoadingMore = false; _dlgExhausted = false;
                CaptureDialogOffsets(dialogs);   // arm paging from page one

                _allChats.Sort((a, b) => b.Date.CompareTo(a.Date));
                _chatTitle.Text = "Select a chat";
                SetHeaderAvatar(null);
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
            if (pa) return ra.CompareTo(rb);         // both pinned: this view's pin order (drafts never move a pinned chat)
            return b.SortDate.CompareTo(a.SortDate); // both non-pinned: newest first by max(last-msg, draft) → a draft floats up
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
            // INCHAT-SEARCH: the left panel holds the scoped in-chat results — don't let an incoming message / folder
            // refresh / OpenChat wipe them. ExitInChatSearch clears the flag and re-renders the normal list.
            if (_inChatSearchEntry != null) return;
            TouchScroller.StopMomentum();   // 3.4: the chat list is being rebuilt — a coast must not scroll the new content
            long __t = PerfLog.T();
            // BATCH-TA-4 (A1): a dedicated rung so ms/row is readable directly instead of being inferred from the
            // aggregated [PERF] fullRender bucket. Spans exactly the same code as that bucket (keepY + Core +
            // RefreshFolderBadges) so it stays comparable to the recorded device baseline: 2632 ms at 627 rows,
            // 462 ms at 90 rows, ~4 ms/row, usr climbing to 940. Logger.Enabled-gated; a rebuild is a cold-ish
            // site (per incoming message), not a per-frame path.
            long __renderTs = Logger.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            // ⚠ CORRECTED 2026-08-03 (BATCH-TA-5/C1). This comment used to say "rebuilds fire on EVERY
            // incoming message". That WAS true, but has not been for a long time, and the stale wording
            // misled THREE consecutive analyses into targeting the wrong code — leave this note here.
            // What actually happens now: the per-message path is UpdateChatListForMessageCore, and it is
            // INCREMENTAL — it looks the row up (:5309 FindChatItem), then repositions + repaints just
            // that one row (:5324-5326 SetChildIndex + Invalidate). It reaches a full rebuild ONLY via
            // the row-ABSENT fallback at :5320. A steady stream of messages into chats already on screen
            // therefore costs ZERO rebuilds.
            // The real full-rebuild drivers are the BULK sites: the warm-switch pre-render (:4061, which
            // alone renders the whole cached dialog set on every account switch), dialog paging (:5563),
            // the archive load (:5459), first load (:5590), and the pin/archive/folder/search sites.
            // The keepY capture below still matters for exactly those.
            int keepY = -_chatListPanel.AutoScrollPosition.Y;
            RenderChatListCore(filter);
            if (keepY > 0) { try { _chatListPanel.AutoScrollPosition = new Point(0, keepY); } catch { } }
            RefreshFolderBadges();   // FOLDER-SIDEBAR: keep per-folder badges live on whichever navigator is shown
            PerfLog.Rec(PerfLog.P.RenderChatList, __t);
            if (__renderTs != 0)
            {
                double __ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __renderTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int __rows = _chatListPanel.Controls.Count;
                uint __gdi, __usr; PerfLog.GuiHandles(out __gdi, out __usr);
                Logger.Diag("[RENDER] chat-list rebuild rows=" + __rows
                    + " ms=" + __ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " msPerRow=" + (__rows > 0 ? (__ms / __rows).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a")
                    + " gdi=" + __gdi + " usr=" + __usr);
            }
            // BATCH-TA-0: FIRST paint only — this is time-to-first-usable-chat-list, the number the whole
            // load-time effort is judged on. Later rebuilds are counted by the [PERF] fullRender bucket.
            if (!_firstChatListPainted) { _firstChatListPainted = true; PerfLog.Boot("FIRST chat list PAINTED (rows=" + _chatListPanel.Controls.Count + ")"); }
        }

        /// <summary>BATCH-TA-0 cold-start ladder: latches the first chat-list paint so the [BOOT] rung fires once.</summary>
        private bool _firstChatListPainted;

        private void RenderChatListCore(string filter)
        {
            _chatListPanel.SuspendLayout();
            // BATCH-TA-4 (A2): DISPOSE the outgoing rows before clearing. Controls.Clear() only detaches — each
            // ChatListItemControl keeps its HWND plus two per-instance Fonts (ChatListItemControl.cs:22-23,
            // and FontHelper.Make allocates a NEW Font per call — there is no font cache), so a rebuild leaked
            // one window handle + two GDI fonts PER ROW. The device measured usr climbing to 940 at 627 rows.
            // Same shape as the three existing precedents in this file: the folder bar (:6373), the forum-topic
            // bar (:6410) and the story tray (:6476) all Dispose-then-Clear; ClearActiveAccountView (:4203)
            // does the Remove+Dispose variant on THIS very panel.
            // SAFE because the row does NOT own its avatar: ChatListItemControl.Avatar is a plain auto-property
            // (:39) holding a NON-OWNING reference into the LruImageCache, and Dispose (:436-444) frees only
            // _timeFont/_badgeFont. Disposing a row therefore cannot hand a disposed bitmap back to the cache —
            // the trap that the drawer-avatar cache hit. It also RETIRES the ghost-row hazard: a disposed row
            // leaves Controls, so a late avatar arrival can no longer land on an orphan that still reads
            // !IsDisposed.
            foreach (var c in _chatListPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
            _chatListPanel.Controls.Clear();
            _selectedItem = null;

            int w = ContentWidth(_chatListPanel);
            IEnumerable<ChatEntry> q = _allChats.Where(IsVisibleInCurrentView);   // per-view filter (place #1)
            if (!string.IsNullOrWhiteSpace(filter))
                q = q.Where(c => (c.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            // Active-view order: pinned-in-view (by pin rank) above non-pinned (by date desc).
            var ordered = q.ToList();
            ordered.Sort(CompareRowsInView);

            // SEARCH-BUILD-1: in search mode, label the local dialog matches under a "CHATS" section (only when there
            // ARE matches — no empty header). The normal (unfiltered) list has no header.
            if (!string.IsNullOrWhiteSpace(filter) && ordered.Count > 0)
                AddSectionHeader("CHATS");

            // BATCH-TA-14/T4 — be honest about an incomplete folder instead of just looking short. Before this,
            // a folder silently showed only the members that happened to be in the first ~100 dialogs, and the
            // badge undercounted by the same rows, so nothing hinted anything was missing (TA-11/M2).
            if (string.IsNullOrWhiteSpace(filter) && !_showArchive && _activeFolder is TL.DialogFilter)
            {
                if (_folderFetching)
                    AddSectionHeader("LOADING THIS FOLDER'S CHATS…");
                else if (_folderMissingCount > 0)
                    AddSectionHeader(_folderMissingCount + " CHAT" + (_folderMissingCount == 1 ? "" : "S")
                                     + " IN THIS FOLDER COULDN'T BE LOADED");
            }

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
            BuildChatActionMenu(menu, entry, ChatMenuSurface.ChatListRow);
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        // ── BATCH-TA-20/S0 — ONE DEFINITION OF THE CHAT ACTION MENU ──────────────────────────────
        /// <summary>Which surface is asking for the menu. The entry SET is shared; a surface may still
        /// omit what does not belong to it.</summary>
        private enum ChatMenuSurface
        {
            /// <summary>Right-click / long-press on a row in the chat list.</summary>
            ChatListRow,
            /// <summary>The ⋮ button in the open chat's header. No caller yet — the button is TA-19/S1.</summary>
            ChatHeader
        }

        /// <summary>Everything the menu needs to decide what to offer, resolved ONCE and passed explicitly
        /// rather than re-derived per entry. Kept as data so a surface cannot accidentally answer one of
        /// these questions differently from the other.</summary>
        private sealed class ChatMenuContext
        {
            public ChatEntry Entry;
            /// <summary>SAVED MESSAGES — the chat with yourself. Every self-destructive action must be
            /// omitted for it: you cannot leave, block or meaningfully "delete" your own chat.</summary>
            public bool IsSelf;
            public bool IsUser;          // InputPeerUser — includes bots AND self
            public bool IsBot;
            public bool IsBroadcast;     // channel
            public bool IsMegagroup;     // supergroup
            public bool IsBasicGroup;    // legacy InputPeerChat
            /// <summary>TA-10's rule: Pin is offered in All, in Archive and in a real custom folder, but
            /// never in a shared chatlist folder.</summary>
            public bool PinAllowed;
        }

        private ChatMenuContext ChatMenuContextFor(ChatEntry entry)
        {
            var me = _service != null ? _service.Me : null;
            var ch = entry.PeerInfo as Channel;
            return new ChatMenuContext
            {
                Entry = entry,
                IsSelf = me != null && entry.PeerId == me.id,
                IsUser = entry.Peer is InputPeerUser,
                IsBot = (entry.PeerInfo as User)?.IsBot == true,
                IsBroadcast = ch != null && (ch.flags & Channel.Flags.broadcast) != 0,
                IsMegagroup = ch != null && (ch.flags & Channel.Flags.megagroup) != 0,
                IsBasicGroup = entry.Peer is InputPeerChat,
                PinAllowed = _activeFolder == null || _activeFolder is TL.DialogFilter
            };
        }

        /// <summary>THE SINGLE DEFINITION OF THE CHAT ACTION MENU — the chat-list row uses it today, and
        /// the header ⋮ (TA-19/S1) will use the same one.
        ///
        /// ⚠ WHY THIS IS SHARED RATHER THAN COPIED: two builders over the same actions WILL drift, and we
        /// have already paid for that once — TA-9/TA-9b was exactly one surface knowing a rule about
        /// pinning that another did not. A second copy would re-open that class of bug on every entry here.
        ///
        /// ⚠ UNAVAILABLE ENTRIES ARE OMITTED, NEVER GREYED. <see cref="AddMenuItem"/> has no `enabled`
        /// parameter, and that is deliberate — a greyed "Leave" teaches nothing. Same convention as the
        /// TA-9b pin fix.
        ///
        /// <paramref name="surface"/> is DELIBERATELY UNUSED TODAY: both surfaces show the same set, so
        /// nothing branches on it yet. Do not delete it — it is the hook the header ⋮ needs to drop
        /// "Search in chat" (already its own header button) without the row menu losing anything, and
        /// adding it later would mean touching every call site instead of one.</summary>
        private void BuildChatActionMenu(ContextMenuStrip menu, ChatEntry entry, ChatMenuSurface surface)
        {
            if (menu == null || entry == null) return;
            var c = ChatMenuContextFor(entry);

            // BATCH-TA-21/S1b — HEADER ONLY, and this is the first real use of `surface`.
            // The row menu deliberately does NOT gain this entry: clicking a row already opens that chat,
            // and adding it there would change a surface this batch is required to leave byte-identical.
            // On the header it earns its place — the profile IS reachable today by clicking the title or
            // the avatar (:815-819), but nothing advertises that, so it is effectively undiscoverable.
            if (surface == ChatMenuSurface.ChatHeader)
                AddMenuItem(menu, "ℹ   " + InfoLabelFor(c), OpenSelectedProfile);

            // BATCH-TA-10 — the Pin item is offered in All, in Archive, and in a real custom folder.
            // Each of those three routes to a DIFFERENT write, and TogglePin picks by _activeFolder:
            //   All / Archive  → Messages_ToggleDialogPin (no folder_id; the server pins the dialog in
            //                    whatever folder it already lives in, so client and server agree).
            //   custom folder  → Messages_UpdateDialogFilter on that folder's own pinned_peers.
            // DialogFilterChatlist is STILL withheld: those are shared folders owned by their invite
            // link, and writing one back is not the same operation as editing a folder you own. The
            // server's dialogFilterDefault also arrives as a NULL element, but it never becomes
            // _activeFolder (SetActiveFolder(null) means "All"), so null here is the All view.
            if (c.PinAllowed)
                AddMenuItem(menu, IsPinnedInView(entry) ? "📌   Unpin" : "📌   Pin", () => TogglePin(entry));

            AddMenuItem(menu, entry.Muted ? "🔔   Unmute" : "🔕   Mute", () => ToggleChatMute(entry));

            if (entry.UnreadCount > 0)
                AddMenuItem(menu, "✓   Mark as read", () => MarkChatRead(entry));
            else
                AddMenuItem(menu, "●   Mark as unread", () => MarkChatUnread(entry));

            AddMenuItem(menu, entry.Archived ? "📂   Unarchive" : "🗄   Archive", () => ToggleArchive(entry));

            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "🧹   Clear history", () => ClearChatHistory(entry));

            // ⚠ BATCH-TA-20/S0c — SAVED MESSAGES IS NOT A CHAT YOU CAN LEAVE OR DELETE.
            // It is an InputPeerUser of YOURSELF, so without this guard it inherited the private-chat menu
            // and offered "🗑 Delete chat" on your own saved notes. The same flag is what future entries
            // (Block, TA-19/S3) must consult — the check belongs to the context, not to each entry.
            if (!c.IsSelf)
                AddMenuItem(menu, "🗑   " + (c.IsUser ? "Delete chat" : LeaveLabelFor(entry)),
                    () => DeleteOrLeaveChat(entry));
        }

        /// <summary>Names the "view info" entry after what the chat actually is, the way LeaveLabelFor does
        /// for leaving. Bot is checked after the channel cases because a bot is always an InputPeerUser.</summary>
        private static string InfoLabelFor(ChatMenuContext c)
        {
            if (c.IsBroadcast) return "View channel info";
            if (c.IsMegagroup || c.IsBasicGroup) return "View group info";
            if (c.IsBot) return "View bot info";
            return "View profile";
        }

        /// <summary>BATCH-TA-21/S1b — the header ⋮ opens the SAME builder the chat-list row opens, with
        /// surface = ChatHeader. There is no second entry list here by design: one definition is the whole
        /// point of TA-20/S0, and a header that quietly grew its own copy is exactly the drift that fix
        /// exists to prevent.</summary>
        private void ShowChatHeaderMenu()
        {
            var entry = _selectedChat;
            if (entry == null || _chatMenuBtn == null || _chatMenuBtn.IsDisposed) return;
            var menu = new ThemedContextMenuStrip();
            BuildChatActionMenu(menu, entry, ChatMenuSurface.ChatHeader);
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            // Hang it under the button — same gesture as the proxy sheet's ⋮ and every other menu here.
            menu.Show(_chatMenuBtn.PointToScreen(new Point(0, _chatMenuBtn.Height)));
        }

        private async void ToggleChatMute(ChatEntry entry)
        {
            bool target = !entry.Muted;
            bool ok;
            try { ok = await _service.ToggleMuteAsync(entry.Peer, target); }
            catch (Exception ex)
            {
                if (Logger.Enabled) Logger.Diag("[NOTIFY] mute write peer=" + entry.PeerId + " mute=" + target + " FAILED ex=" + ex.Message);   // R7: survives Release (BATCH-TA-6/P1)
                ThemedDialog.Show(this, "Mute", "Couldn't change mute: " + ex.Message, "OK"); return;
            }
            if (Logger.Enabled) Logger.Diag("[NOTIFY] mute write peer=" + entry.PeerId + " mute=" + target + (ok ? " ok" : " FAILED (server returned false)"));   // R7 (BATCH-TA-6/P1)
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
            RefreshFolderBadges();   // TA-6b/G1 (DOWN): context-menu "Mark as read"
        }

        private async void MarkChatUnread(ChatEntry entry)
        {
            try { await _service.MarkDialogUnreadAsync(entry.Peer, true); }
            catch (Exception ex) { ThemedDialog.Show(this, "Mark as unread", "Couldn't mark unread: " + ex.Message, "OK"); return; }
            if (entry.UnreadCount == 0) entry.UnreadCount = 1;   // local cue (we don't track the unread_mark flag separately)
            FindChatItem(entry.PeerId)?.Invalidate();
            UpdateTrayTooltip();
            RefreshFolderBadges();   // TA-6b/G1 (UP): context-menu "Mark as unread" — the one local site that RAISES a count
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
            RefreshFolderBadges();   // TA-6b/G1 (DOWN): clearing a history zeroes its unread
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

            RemoveDialogRow(entry.PeerId);   // shared with the live "left/deleted elsewhere" path (idempotent)
        }

        /// <summary>DIALOG-LIVE-UPDATES: drop a dialog's row from the list + panel. IDEMPOTENT — a no-op if the row
        /// is already gone (so the server echo of a leave WE initiated, or a duplicate update, does nothing). Shared
        /// by the user-initiated leave/delete and the "left/kicked from another device" handlers so the two paths
        /// never diverge. If the removed chat is currently open, returns to the empty state (and exits thread mode if
        /// we were reading its comments). Runs on the UI thread (callers are already marshalled).</summary>
        private void RemoveDialogRow(long peerId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry != null) _allChats.Remove(entry);
            var item = FindChatItem(peerId);
            if (item != null) { _chatListPanel.Controls.Remove(item); item.Dispose(); }   // FlowLayoutPanel reflows
            if (_selectedChat != null && _selectedChat.PeerId == peerId)
            {
                if (_thread != null) ClearThreadMode();   // we were reading this group's comments — leave thread mode
                _selectedChat = null; ClearMessagePanel(); _chatTitle.Text = "Select a chat"; SetHeaderAvatar(null);
                if (_chatSearchBtn != null) _chatSearchBtn.Visible = false;   // INCHAT-SEARCH: chat closed → hide the magnifier
                if (_chatMenuBtn != null) _chatMenuBtn.Visible = false;       // TA-21/S1a: …and the ⋮ with it
                if (_dockBtn != null) _dockBtn.Visible = false;               // TA-23/D1c: …and the dock toggle
                SetDockOpen(false);
            }
            RebuildFolders();
            UpdateTrayTooltip();
        }

        /// <summary>DIALOG-LIVE-UPDATES: a channel/supergroup's state changed elsewhere (UpdateChannel). If it now
        /// reports we're no longer a member — Channel.left (voluntary leave) or the entity became ChannelForbidden
        /// (kick/ban) — drop its row live. Any other reason (title/photo/admin change) keeps the row. The entity is
        /// read from the manager's dict (NO RPC — hot-path safe); a null/still-member entity → no removal, and a
        /// case the manager hasn't reflected yet self-corrects on the next getDifference sync.</summary>
        private void HandleChannelStateUpdate(long channelId)
        {
            if (_allChats.All(c => c.PeerId != channelId)) return;   // not shown (or already removed) → nothing to do
            var info = ResolvePeer(new PeerChannel { channel_id = channelId });
            bool notMember = info is ChannelForbidden
                          || (info is Channel ch && (ch.flags & Channel.Flags.left) != 0);
            if (!notMember) return;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[DIALOG] channel " + channelId + " left/kicked elsewhere → remove row");
            RemoveDialogRow(channelId);
        }

        /// <summary>DIALOG-LIVE-UPDATES: a basic (legacy) group's state changed elsewhere (UpdateChat). Mirrors the
        /// channel path: remove the row when we've left / it deactivated (Chat.left|deactivated) or it became
        /// ChatForbidden. Positive-evidence only — a plain info change keeps the row.</summary>
        private void HandleBasicChatStateUpdate(long chatId)
        {
            if (_allChats.All(e => e.PeerId != chatId)) return;
            var info = ResolvePeer(new PeerChat { chat_id = chatId });
            bool gone = info is ChatForbidden
                     || (info is Chat cht && (cht.flags & (Chat.Flags.left | Chat.Flags.deactivated)) != 0);
            if (!gone) return;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[DIALOG] basic group " + chatId + " left/deactivated elsewhere → remove row");
            RemoveDialogRow(chatId);
        }

        private async void TogglePin(ChatEntry entry)
        {
            // BATCH-TA-10 — a CUSTOM FOLDER pins through the folder's own filter, not through the dialog
            // list. Messages_ToggleDialogPin carries no folder_id and would pin in the chat's HOME list
            // instead, which is a view the user isn't looking at. Archive is NOT this case: SetArchive()
            // nulls _activeFolder, so `_showArchive` never reaches here with a filter active.
            var activeFilter = _activeFolder as TL.DialogFilter;
            if (activeFilter != null) { await TogglePinInFolderAsync(entry, activeFilter); return; }

            bool pinning = !IsPinnedInView(entry);
            try { await _service.ToggleDialogPinAsync(entry.Peer, pinning); }
            catch (Exception ex) { ThemedDialog.Show(this, "Pin", "Couldn't change pin: " + ex.Message, "OK"); return; }

            // Reflect in the ACTIVE view's pin rank; a new pin floats to the top of that view's pinned group.
            //
            // BATCH-TA-9/A1 — SHIFT-UP, replacing a scheme that could never work.
            // The old code was `MinPinRank(...) - 1`, i.e. "one better than the current best". But ranks are
            // seeded from ZERO (BuildDialogEntries :5487-5488, server pin order), and MinPinRank's
            // DefaultIfEmpty(0) also yields 0 when nothing is pinned — so the result was ALWAYS -1, which
            // IsPinnedInView (:5732 `PinRankInView(e) >= 0`) reads as NOT PINNED. Pinning a chat inside
            // TelegArm therefore appeared to do nothing at all. A negative "float above everything" sentinel
            // is structurally incompatible with a `>= 0` pinned test — patching the arithmetic cannot fix it.
            //
            // Chosen scheme: the new pin takes rank 0 and every existing pinned row shifts up by one. That
            // keeps the EXISTING data model intact (int rank, -1 = not pinned, lower floats higher), which is
            // exactly what the server seeds and what CompareRowsInView:5740 `ra.CompareTo(rb)` already means.
            // The alternative — an explicit bool alongside the rank — would have forced changes to
            // PinRankInView, IsPinnedInView, CompareRowsInView, BuildDialogEntries and every consumer, for
            // the same visible result. Shifting is O(pinned), which is a handful of rows.
            // The shift runs BEFORE the assignment and is guarded on `>= 0`, so `entry` (still -1 here,
            // because `pinning` means it was not pinned) is untouched by its own shift.
            // Unpin leaves gaps in the remaining ranks (0,2,3…) — harmless, only relative order is compared.
            if (_showArchive)
            {
                if (pinning)
                {
                    foreach (var c in _allChats) if (c.ArchivePinOrder >= 0) c.ArchivePinOrder++;
                    entry.ArchivePinOrder = 0;
                }
                else entry.ArchivePinOrder = -1;
            }
            else
            {
                if (pinning)
                {
                    foreach (var c in _allChats) if (c.MainPinOrder >= 0) c.MainPinOrder++;
                    entry.MainPinOrder = 0;
                }
                else entry.MainPinOrder = -1;
            }
            RenderChatList(_searchBox.Text);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  BATCH-TA-10 — REAL FOLDER PINNING (messages.updateDialogFilter).
        //
        //  THE VECTORS ARE DISJOINT. Settled from this account's own server state (TA-10/R2a), not from
        //  the docs, which do not say: a chat pinned inside a folder appears in pinned_peers ONLY, and
        //  include_peers is left alone. So:
        //     PIN   → add to pinned_peers only. include_peers stays BYTE-IDENTICAL.
        //     UNPIN → remove from pinned_peers, then ask whether the chat is still in the folder at all.
        //  That second question is the data-loss one. In a flags-based folder ("Channels" = broadcasts)
        //  an unpinned channel still matches the flag and stays. In a pure include-list folder (no type
        //  flags at all — real examples exist in this account) a chat that was ONLY there because it was
        //  pinned has nothing to fall back on, and a naive unpin EJECTS it from the folder. So unpin adds
        //  it to include_peers, but ONLY when it would otherwise vanish — doing it unconditionally would
        //  silently rewrite a flags-based folder into an explicit list the user never asked for.
        //
        //  The "is it still in the folder" test runs the PRODUCTION MatchesFolder against the candidate
        //  filter rather than re-implementing the flag logic, so it cannot drift from real filtering.
        //  Because the candidate's pinned_peers no longer holds the peer, FolderPeerSets' Include∪Pinned
        //  union answers exactly the right question.
        //
        //  ⚠ CONSTRUCT, NEVER MUTATE. The TA-6/P2 folder-match cache is keyed on the filter INSTANCE; a
        //  new object is what makes the stale entry unreachable. Mutating pinned_peers in place would
        //  leave the cache holding pre-edit peer sets → wrong filtering AND wrong badges.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private async System.Threading.Tasks.Task TogglePinInFolderAsync(ChatEntry entry, TL.DialogFilter cached)
        {
            // `pinning` is deliberately derived from the CACHED object — that is the state the user was
            // looking at when they clicked, so it is their intent. It is then applied to the FRESH arrays
            // below, where the add/remove is idempotent, so a pin that already happened elsewhere can't
            // duplicate and an unpin that already happened can't fail.
            bool pinning = !IsPinnedInView(entry);

            // ── BATCH-TA-10/R2b — RE-FETCH BEFORE THE WRITE ──────────────────────────────────────────
            // This is a read-modify-write of a whole filter object, and `_folders` is only ever refreshed
            // by LoadFolders (cold connect / account switch / recovery — TA-7). It can therefore be HOURS
            // stale. Building the payload from it would silently REVERT every folder edit made on another
            // device since this session started: add a chat to the folder on your phone, pin something
            // here, and the phone's edit is gone. We re-read the one filter we are about to overwrite.
            // Only ONE filter is taken from the response — `_folders` as a whole is NOT refreshed here,
            // so the blast radius stays exactly one folder and one write.
            TL.DialogFilterBase[] fresh;
            try { fresh = await _service.GetDialogFiltersFreshAsync(); }
            catch (Exception ex)
            {
                if (Logger.Enabled) Logger.Diag("[FOLDERPIN] folder=" + cached.id + " re-fetch FAILED, no write — " + ex.Message);
                ThemedDialog.Show(this, "Pin", "Couldn't reach Telegram to update the folder: " + ex.Message, "OK");
                return;
            }
            if (IsDisposed) return;

            // Locate BY ID, never by title (this account has two folders both called "Groups"), and skip
            // the null element — the server's dialogFilterDefault deserialises to null in WTC 4.4.6.
            TL.DialogFilter folder = null;
            bool nowChatlist = false;
            foreach (var f in fresh)
            {
                if (f == null || f.ID != cached.id) continue;
                folder = f as TL.DialogFilter;
                nowChatlist = folder == null;
                break;
            }
            if (folder == null)
            {
                string why = nowChatlist ? "is now a shared folder" : "no longer exists";
                if (Logger.Enabled) Logger.Diag("[FOLDERPIN] folder=" + cached.id + " " + why + " on the server — ABORT, nothing written");
                ThemedDialog.Show(this, "Pin", "That folder " + why + ", so nothing was changed.", "OK");
                return;
            }

            // If this ever fires, the difference IS the staleness window — worth seeing once.
            if (Logger.Enabled)
            {
                string cp = PeerIds(cached.pinned_peers),  fp = PeerIds(folder.pinned_peers);
                string ci = PeerIds(cached.include_peers), fi = PeerIds(folder.include_peers);
                string ce = PeerIds(cached.exclude_peers), fe = PeerIds(folder.exclude_peers);
                if (cp != fp || ci != fi || ce != fe)
                    Logger.Diag("[FOLDERPIN] STALE _folders id=" + cached.id
                                + " pinned cached[" + cp + "] fresh[" + fp + "]"
                                + " include cached[" + ci + "] fresh[" + fi + "]"
                                + " exclude cached[" + ce + "] fresh[" + fe + "]"
                                + " → writing from FRESH (the cached copy would have reverted that difference)");
            }

            var pinned  = folder.pinned_peers  ?? new InputPeer[0];
            var include = folder.include_peers ?? new InputPeer[0];

            // Pin → the new pin takes the TOP of this folder's pinned group (index 0), matching the
            // shift-up semantics the main list uses. The Where() also de-dupes a stale entry.
            InputPeer[] newPinned;
            if (pinning)
            {
                var list = new List<InputPeer> { entry.Peer };
                list.AddRange(pinned.Where(p => PeerIdOf(p) != entry.PeerId));
                newPinned = list.ToArray();
            }
            else newPinned = pinned.Where(p => PeerIdOf(p) != entry.PeerId).ToArray();

            var candidate = CloneFilterWith(folder, newPinned, include);
            bool rescuedFromEjection = false;

            if (!pinning && !MatchesFolder(entry, candidate))
            {
                var inc = new List<InputPeer>(include) { entry.Peer };
                candidate = CloneFilterWith(folder, newPinned, inc.ToArray());
                rescuedFromEjection = true;
            }

            if (Logger.Enabled)
                Logger.Diag("[FOLDERPIN] " + (pinning ? "pin" : "unpin") + " peer=" + entry.PeerId
                            + " folder=" + folder.id + " pinned " + pinned.Length + "→" + newPinned.Length
                            + " include " + include.Length + "→" + (candidate.include_peers ?? new InputPeer[0]).Length
                            + (rescuedFromEjection ? " (ADDED to include_peers: unpin would have EJECTED it)" : ""));

            try { await _service.UpdateDialogFilterAsync(folder.id, candidate); }
            catch (Exception ex)
            {
                // Touch NOTHING on failure — _folders and _activeFolder keep pointing at the object the
                // server still has, so local state cannot drift from the server. R7: survives Release.
                if (Logger.Enabled) Logger.Diag("[FOLDERPIN] folder=" + folder.id + " FAILED, no local change — " + ex.Message);
                ThemedDialog.Show(this, "Pin", "Couldn't change pin: " + ex.Message, "OK");
                return;
            }

            // Swap the array element AND repoint _activeFolder TOGETHER. RebuildFolderBar marks the active
            // tab with ReferenceEquals(f, _activeFolder), so doing one without the other loses the active
            // highlight and keeps the view filtering on a stale object.
            // Key on ID: titles are NOT unique (this account has two folders both called "Groups"), and the
            // server's dialogFilterDefault arrives as a NULL element, so never index-shift and always
            // null-check before reading f.ID.
            int idx = -1;
            for (int i = 0; i < _folders.Length; i++)
            {
                var f = _folders[i];
                if (f != null && f.ID == folder.id) { idx = i; break; }
            }
            if (idx >= 0) _folders[idx] = candidate;
            else if (Logger.Enabled) Logger.Diag("[FOLDERPIN] folder=" + folder.id + " not found in _folders; next LoadFolders resyncs");

            // R2b added a second await, so the user may have switched folders while the re-fetch and the
            // write were in flight. Only repoint the ACTIVE view if it is still this folder — otherwise we
            // would yank them back to a folder they navigated away from. The array element is replaced
            // either way, so whenever they return, they see the new state.
            if (_activeFolder != null && _activeFolder.ID == folder.id) _activeFolder = candidate;

            RebuildFolders();                    // re-registers badge sources against the NEW instance
            RenderChatList(_searchBox.Text);
        }

        /// <summary>Peer ids of a filter vector as a stable csv, for the [FOLDERPIN] staleness diff. "-" when
        /// empty, so an empty vector and a missing one read the same in the log.</summary>
        private static string PeerIds(InputPeer[] peers)
        {
            if (peers == null || peers.Length == 0) return "-";
            return string.Join(",", peers.Select(PeerIdOf));
        }

        /// <summary>BATCH-TA-10: a NEW DialogFilter with only pinned_peers/include_peers swapped; every other
        /// field is carried across by reference, verbatim. Reconstructing title/emoticon/flags instead of
        /// copying them is what triggers FILTER_TITLE_EMPTY, and rebuilding exclude_peers would risk
        /// CHATLIST_EXCLUDE_INVALID — so neither is ever rebuilt here.</summary>
        private static TL.DialogFilter CloneFilterWith(TL.DialogFilter src, InputPeer[] pinnedPeers, InputPeer[] includePeers)
        {
            return new TL.DialogFilter
            {
                flags         = src.flags,          // carries has_emoticon / has_color / title_noanimate
                id            = src.id,
                title         = src.title,          // TextWithEntities — reference, never rebuilt
                emoticon      = src.emoticon,
                color         = src.color,
                pinned_peers  = pinnedPeers,
                include_peers = includePeers,
                exclude_peers = src.exclude_peers,  // FENCE: never touched by TA-10
            };
        }

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
                    RefreshStoryAvatar(peerId);   // STORIES: repaint the tray chip when its avatar lands
                    // CHANNEL-PHOTO-REFRESH: if the OPEN chat's avatar just (re)landed — e.g. its photo changed — repaint the header.
                    if (_selectedChat != null && _selectedChat.PeerId == peerId) { _headerAvatarImg = img; _headerAvatar?.Invalidate(); }
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

        // SEARCH-BUILD-1: public-discovery (Contacts_Search) limit. The API has NO offset → "Show more" re-queries
        // with the expanded limit. Reset to the initial limit on every new query (each search starts collapsed).
        private const int PublicSearchLimit = 8, PublicSearchLimitExpanded = 40;
        private int _publicLimit = PublicSearchLimit;

        private void OnSearchTextChanged()
        {
            _searchDebounce.Stop();
            // INCHAT-SEARCH: while scoped to an open chat, typing searches THAT chat (not the global list). The
            // results re-render in DoInChatSearch after the debounce; here we just keep the scope chip shown.
            if (_inChatSearchEntry != null)
            {
                RenderInChatSearchChrome();
                if (!string.IsNullOrWhiteSpace(_searchBox.Text)) _searchDebounce.Start();
                return;
            }
            string q = _searchBox.Text;
            _publicLimit = PublicSearchLimit;   // SEARCH-BUILD-1: a fresh query starts with the collapsed public list
            // Instant local chat matches (folder-aware); message + public results stream in after a debounce.
            RenderChatList(q);
            if (!string.IsNullOrWhiteSpace(q)) _searchDebounce.Start();
        }

        private async void DoMessageSearch()
        {
            _searchDebounce.Stop();
            // INCHAT-SEARCH: scoped-to-open-chat search runs its own path (Messages_Search on the peer).
            if (_inChatSearchEntry != null) { await DoInChatSearch(); return; }
            var query = _searchBox.Text.Trim();
            if (query.Length == 0) return;

            // SEARCH-BUILD-2: a typed @username / t.me link resolves to a public entity — the PRIMARY result. Skip the
            // normal message/public search for it (the resolution IS the answer).
            if (TryResolveSearchLink(query)) return;

            // SEARCH-SPONSORED (PART 6.5): promoted channels/bots for this query — their own "SPONSORED" section (ToS:
            // view reported on display, click on tap). Best-effort; usually empty (no section).
            try
            {
                var sponsored = await _service.GetSponsoredPeersAsync(query);
                if (_searchBox.Text.Trim() != query) return;
                AppendSponsoredResults(sponsored);
            }
            catch { /* sponsored is best-effort */ }

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

            // SEARCH-BUILD-1: public discovery (channels/groups/users you're NOT in) — additive + best-effort; a
            // failure never disturbs the local + message results already shown.
            try
            {
                var found = await _service.SearchContactsAsync(query, _publicLimit);
                if (_searchBox.Text.Trim() != query) return;   // stale (query changed during the await)
                AppendPublicResults(found);
            }
            catch { /* public discovery is best-effort */ }
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
                    FocusMessageId = m.ID,
                    PeerInfo = resolved   // SEARCH-FIX-2 (BUG 2): carry the peer so the avatar can load/fetch
                };
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnSearchResultClick;
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(entry.PeerId);   // SEARCH-FIX-2 (BUG 2): cached avatar shows instantly
                if (item.Avatar == null) LoadAvatar(entry);       // else fetch on-demand (letter circle meanwhile)
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>SEARCH-BUILD-1: appends PUBLIC discovery results (Contacts_Search `results`) below the message
        /// hits — categorized CHANNELS (broadcast) / GROUPS (megagroup) / USERS. Entities you're ALREADY in are
        /// skipped (they show under CHATS). A "Show more" row re-queries with a larger limit when the limit was hit.</summary>
        private void AppendPublicResults(Contacts_Found found)
        {
            if (found == null || found.results == null || found.results.Length == 0) return;
            var known = new HashSet<long>(_allChats.Select(c => c.PeerId));
            var channels = new List<ChatEntry>();
            var groups = new List<ChatEntry>();
            var users = new List<ChatEntry>();

            foreach (var p in found.results)
            {
                if (p == null || known.Contains(p.ID)) continue;   // already in it → shown under CHATS
                var info = found.UserOrChat(p);
                if (info is Channel ch)
                    ((ch.flags & Channel.Flags.broadcast) != 0 ? channels : groups).Add(PublicChannelEntry(ch));
                else if (info is User u)
                    users.Add(PublicUserEntry(u));
                // a basic Chat can't be public (no username) — skip
            }
            if (channels.Count + groups.Count + users.Count == 0) return;

            _chatListPanel.SuspendLayout();
            AppendPublicSection("CHANNELS", channels);
            AppendPublicSection("GROUPS", groups);
            AppendPublicSection("USERS", users);
            // "Show more": the API has NO offset, so re-query with the expanded limit. Offer it only when the query
            // filled the current limit (more likely exist) and we're not already expanded.
            if (found.results.Length >= _publicLimit && _publicLimit < PublicSearchLimitExpanded)
                AddShowMoreRow();
            _chatListPanel.ResumeLayout();
        }

        private void AppendPublicSection(string header, List<ChatEntry> entries)
        {
            if (entries.Count == 0) return;   // no empty sections
            AddSectionHeader(header);
            int w = ContentWidth(_chatListPanel);
            foreach (var entry in entries)
            {
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnSearchResultClick;   // → OnSearchResultClick → OpenChat (public channel = preview + Join)
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(entry.PeerId);
                if (item.Avatar == null) LoadAvatar(entry);
            }
        }

        /// <summary>Builds a public channel/group result row: name + a "N subscribers/members" subtitle (or @username /
        /// "Channel"/"Group" when the count isn't carried). PeerInfo/Peer set so OpenChat can preview it unjoined.</summary>
        private ChatEntry PublicChannelEntry(Channel ch)
        {
            bool broadcast = (ch.flags & Channel.Flags.broadcast) != 0;
            string sub;
            if ((ch.flags & Channel.Flags.has_participants_count) != 0 && ch.participants_count > 0)
                sub = FormatMemberCount(ch.participants_count) + (broadcast ? " subscribers" : " members");
            else if (!string.IsNullOrEmpty(ch.username)) sub = "@" + ch.username;
            else sub = broadcast ? "Channel" : "Group";
            return new ChatEntry
            {
                Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.Title,
                Preview = sub, IsGroup = !broadcast, PeerInfo = ch, Date = default(DateTime)
            };
        }

        private ChatEntry PublicUserEntry(User u)
        {
            string sub = !string.IsNullOrEmpty(u.username) ? "@" + u.username
                       : ((u.flags & User.Flags.bot) != 0 ? "Bot" : "User");
            return new ChatEntry
            {
                Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u),
                Preview = sub, IsGroup = false, PeerInfo = u, Date = default(DateTime)
            };
        }

        private static string FormatMemberCount(int n)
        {
            if (n >= 1000000) return (n / 1000000f).ToString("0.#") + "M";
            if (n >= 1000) return (n / 1000f).ToString("0.#") + "K";
            return n.ToString();
        }

        /// <summary>SEARCH-BUILD-1: a clickable "Show more" row under the public sections — re-queries Contacts_Search
        /// with the EXPANDED limit (no API offset) and re-renders the whole search. One extra message re-fetch is
        /// accepted for simplicity (rare, explicit user action).</summary>
        private void AddShowMoreRow()
        {
            var lbl = new Label
            {
                Text = "Show more",
                AutoSize = false,
                Height = 30,
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0),
                Cursor = Cursors.Hand,
                Font = FontHelper.Ui(9f, FontStyle.Bold),
                ForeColor = _accent
            };
            lbl.Click += (s, e) =>
            {
                _publicLimit = PublicSearchLimitExpanded;
                RenderChatList(_searchBox.Text);   // clears + re-renders CHATS; the deferred fetch re-appends the rest
                DoMessageSearch();
            };
            _chatListPanel.Controls.Add(lbl);
        }

        // ── SEARCH-SPONSORED (PART 6.5): promoted channels/bots in search — reuses the sponsored view/click reporting
        // + the public-row style (NOT SponsoredCardControl, which is the in-channel big card) ──

        /// <summary>Renders promoted peers (Contacts_GetSponsoredPeers) as a labeled "SPONSORED" section of result
        /// rows. ToS: the VIEW is reported once per random_id on display; the CLICK on tap. Reuses the public-row
        /// build + the existing _sponsoredViewed dedup + ViewSponsoredAsync/ClickSponsoredAsync.</summary>
        private void AppendSponsoredResults(Contacts_SponsoredPeers sponsored)
        {
            if (sponsored == null || sponsored.peers == null || sponsored.peers.Length == 0) return;
            _chatListPanel.SuspendLayout();
            AddSectionHeader("SPONSORED");
            int w = ContentWidth(_chatListPanel);
            foreach (var sp in sponsored.peers)
            {
                if (sp == null || sp.peer == null) continue;
                var info = sponsored.UserOrChat(sp.peer);
                var entry = info is Channel ch ? PublicChannelEntry(ch)
                          : info is User u ? PublicUserEntry(u)
                          : (info != null ? EntryFromPeerInfo(info) : null);
                if (entry == null) continue;
                // ToS label: "Sponsored" always visible on the row (in addition to the section header).
                entry.Preview = string.IsNullOrEmpty(entry.Preview) ? "Sponsored" : "Sponsored · " + entry.Preview;
                var rid = sp.random_id;
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += (s, e) => OnSponsoredResultClick(entry, rid);
                _chatListPanel.Controls.Add(item);
                if (entry.PeerId != 0) { item.Avatar = _avatars.GetCached(entry.PeerId); if (item.Avatar == null) LoadAvatar(entry); }
                MaybeReportSponsoredView(rid);
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>ToS: report a sponsored result's VIEW once per random_id (reuses the _sponsoredViewed dedup that
        /// the in-channel sponsored card uses).</summary>
        private void MaybeReportSponsoredView(byte[] randomId)
        {
            if (randomId == null) return;
            if (!_sponsoredViewed.Add(BitConverter.ToString(randomId))) return;
            var _ = SafeSponsored(() => _service.ViewSponsoredAsync(randomId));
        }

        /// <summary>A sponsored result was tapped: report the CLICK (ToS) then open the promoted entity via the
        /// public-open path (a not-joined channel previews with Join).</summary>
        private void OnSponsoredResultClick(ChatEntry entry, byte[] randomId)
        {
            if (randomId != null) { var _ = SafeSponsored(() => _service.ClickSponsoredAsync(randomId, false)); }
            _searchBox.Text = "";
            var target = _allChats.FirstOrDefault(c => c.PeerId == entry.PeerId) ?? entry;
            var __ = OpenChat(target, 0);
        }

        // ── SEARCH-BUILD-2: link / @username resolution in the search box (reuses the in-message router) ──

        /// <summary>Detects whether the query is a resolvable @username or t.me link (reusing the router's STATIC
        /// parser <see cref="ParseTgLink"/>). If so, kicks off the async resolution (a "GO TO" result) and returns
        /// true so the caller skips the normal search. A normal search term parses as External → returns false.</summary>
        private bool TryResolveSearchLink(string query)
        {
            var kind = ParseTgLink(query, out string username, out int _, out long _, out string invite, out string _);
            if (kind == TgKind.User && !string.IsNullOrEmpty(username)) { ResolveSearchUsername(query, username); return true; }
            if (kind == TgKind.Invite && !string.IsNullOrEmpty(invite)) { ResolveSearchInvite(query, invite); return true; }
            return false;
        }

        /// <summary>Resolves an @username / t.me/name (ResolveUsernameAsync) and shows it as a "GO TO" result row
        /// (tap → the router opens it — a public channel previews with Join). Graceful "not found" on failure.</summary>
        private async void ResolveSearchUsername(string query, string username)
        {
            IPeerInfo who = null;
            try { who = await _service.ResolveUsernameAsync(username.TrimStart('@')); } catch { }
            if (IsDisposed || _searchBox.Text.Trim() != query) return;   // torn down / query changed meanwhile

            _chatListPanel.SuspendLayout();
            AddSectionHeader("GO TO");
            if (who == null) AddNotFoundRow("@" + username.TrimStart('@'));
            else
            {
                var entry = who is Channel ch ? PublicChannelEntry(ch)
                          : who is User u ? PublicUserEntry(u)
                          : EntryFromPeerInfo(who);
                if (entry != null) AddResolvedRow(entry, query); else AddNotFoundRow("@" + username.TrimStart('@'));
            }
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Previews an invite link (CheckInviteAsync) as a "GO TO" row: already-a-member → the chat; a preview
        /// → title + member count + "tap to join". Tap → the router (OpenInvite) confirms + joins + opens.</summary>
        private async void ResolveSearchInvite(string query, string hash)
        {
            ChatInviteBase info = null;
            try { info = await _service.CheckInviteAsync(hash); } catch { }
            if (IsDisposed || _searchBox.Text.Trim() != query) return;

            _chatListPanel.SuspendLayout();
            AddSectionHeader("GO TO");
            ChatEntry entry = null;
            if (info is ChatInviteAlready already) entry = EntryFromPeerInfo(already.chat);
            else if (info is ChatInvitePeek peek) entry = EntryFromPeerInfo(peek.chat);
            else if (info is ChatInvite preview) entry = InvitePreviewEntry(preview);
            if (entry == null) AddNotFoundRow("invite link");
            else AddResolvedRow(entry, query);
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Adds a resolved "GO TO" result row (avatar + name + subtitle). Tap → the router opens the target.</summary>
        private void AddResolvedRow(ChatEntry entry, string query)
        {
            var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = ContentWidth(_chatListPanel) };
            item.Click += (s, e) => OpenResolvedTarget(query);
            _chatListPanel.Controls.Add(item);
            if (entry.PeerId != 0) { item.Avatar = _avatars.GetCached(entry.PeerId); if (item.Avatar == null) LoadAvatar(entry); }
        }

        /// <summary>A "GO TO" row was tapped: clear the box (restores the normal list) and hand the query to the
        /// in-message router — it resolves + opens (username/msgId/start-param OR invite preview→join→open).</summary>
        private void OpenResolvedTarget(string query)
        {
            _searchBox.Text = "";
            ResolveLinkAsync(query);
        }

        private ChatEntry InvitePreviewEntry(ChatInvite p)
        {
            bool broadcast = (p.flags & ChatInvite.Flags.broadcast) != 0;
            string sub = p.participants_count > 0
                ? FormatMemberCount(p.participants_count) + (broadcast ? " subscribers" : " members") + " · tap to join"
                : "tap to join";
            return new ChatEntry { PeerId = 0, Title = p.title, Preview = sub, IsGroup = !broadcast, Date = default(DateTime) };
        }

        /// <summary>A subtle themed "no results" row for a link/username that didn't resolve.</summary>
        private void AddNotFoundRow(string what)
        {
            var lbl = new Label
            {
                Text = "No results for " + what,
                AutoSize = false,
                Height = 34,
                Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Margin = new Padding(0),
                Font = FontHelper.Ui(9f),
                ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(120, 120, 125)
            };
            _chatListPanel.Controls.Add(lbl);
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

        // ── FORUM-TOPICS ─────────────────────────────────────────────────────────────────────────────────────
        /// <summary>Rebuild the topic chip bar from the CACHED _forumTopics (no re-fetch). Chips reuse FolderTabItem so
        /// they match the folder tabs (theme/accent/selected-bubble); called on open, on theme change, and on select.</summary>
        private void RebuildTopicBar()
        {
            if (_forumTopicBar == null) return;
            _forumTopicBar.SuspendLayout();
            foreach (var c in _forumTopicBar.Controls.Cast<Control>().ToArray()) c.Dispose();
            _forumTopicBar.Controls.Clear();
            _forumTopicBar.BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White;
            if (_currentForumEntry != null)
            {
                AddTopicChip("All", 0, _selectedTopicId == 0, () => SelectTopic(0));   // shown immediately; topics fill in when the fetch lands
                if (_forumTopics != null)
                    foreach (var t in _forumTopics)
                    {
                        var tt = t;
                        AddTopicChip(string.IsNullOrEmpty(t.title) ? "Topic" : t.title, t.unread_count, _selectedTopicId == t.id, () => SelectTopic(tt.id));
                    }
            }
            _forumTopicBar.ResumeLayout();
        }

        private void AddTopicChip(string text, int unread, bool active, Action onClick)
        {
            var it = new FolderTabItem(text, unread, active, _dark, _accent);
            it.Clicked += onClick;
            _forumTopicBar.Controls.Add(it);
        }

        /// <summary>Show/hide the topic bar row (row 4). Zero height when hidden → takes no space.</summary>
        private void ShowTopicBar(bool show)
        {
            if (_rightLayout != null) _rightLayout.RowStyles[4].Height = show ? 36 : 0;   // FORUM-TOPICS: 46→36 (no scrollbar row now → tighter)
        }

        // ── STORIES-BUILD-1: the story tray ──────────────────────────────────────────────────────────────

        /// <summary>Fetch the story TRAY (GetAllStories) and render it. Fire-and-forget; safe off/on the UI thread.
        /// A null return = NotModified/error → keep the current tray. Empty result → hide the tray row.</summary>
        private async void LoadStoriesAsync()
        {
            try
            {
                var all = await _service.GetAllStoriesAsync(_storiesState);
                if (all == null) return;   // Stories_AllStoriesNotModified or error/no-client → keep the current tray
                _storiesState = all.state;
                var list = new List<StoryTrayEntry>();
                if (all.peer_stories != null)
                    foreach (var ps in all.peer_stories)
                    {
                        if (ps == null || ps.peer == null) continue;
                        long id = ps.peer.ID;
                        IPeerInfo info = null; string name = null; InputPeer input = null;
                        if (ps.peer is PeerUser && all.users != null && all.users.TryGetValue(id, out var u)) { info = u; name = DisplayName(u); input = u.ToInputPeer(); }
                        else if (all.chats != null && all.chats.TryGetValue(id, out var ch)) { info = ch; name = (ch as Channel)?.title ?? (ch as Chat)?.title; input = ch.ToInputPeer(); }
                        bool unseen = ps.stories != null && ps.stories.Any(s => s != null && s.ID > ps.max_read_id);
                        list.Add(new StoryTrayEntry { PeerId = id, Name = string.IsNullOrEmpty(name) ? id.ToString() : name, Unseen = unseen, PeerInfo = info, Input = input });
                    }
                _storyPeers = list;
                if (IsDisposed) return;
                Action apply = () => { RebuildStoryTray(); UpdateStoryTrayVisibility(); };   // gated on at-top (STORY-TRAY-HIDE)
                if (InvokeRequired) { try { BeginInvoke(apply); } catch { } } else apply();
            }
            catch { }
        }

        /// <summary>(Re)builds the tray chips from _storyPeers. Cached avatars paint instantly; misses are requested
        /// (async) and swap in via OnAvatarLoaded → RefreshStoryAvatar (letter fallback until the photo lands).</summary>
        private void RebuildStoryTray()
        {
            if (_storyTrayBar == null || _storyTrayBar.IsDisposed) return;
            _storyTrayBar.SuspendLayout();
            foreach (var c in _storyTrayBar.Controls.Cast<Control>().ToArray()) c.Dispose();
            _storyTrayBar.Controls.Clear();
            _storyTrayBar.BackColor = _dark ? Color.FromArgb(40, 40, 40) : Color.White;
            if (_storyPeers != null)
                foreach (var e in _storyPeers)
                {
                    var entry = e;
                    if (entry.PeerInfo != null && _avatars.GetCached(entry.PeerId) == null) _avatars.Request(entry.PeerId, entry.PeerInfo);
                    var chip = new StoryChip(entry.PeerId, entry.Name, entry.Unseen, _dark, _accent, cid => _avatars.GetCached(cid));
                    chip.Clicked += () => OpenStoryViewer(entry.PeerId);
                    _storyTrayBar.Controls.Add(chip);
                }
            _storyTrayBar.ResumeLayout();
        }

        /// <summary>Shows/hides the tray row (its RowStyle height). Zero space when there are no stories.</summary>
        private void ShowStoryTray(bool show)
        {
            if (_storyTrayLayout != null && _storyTrayLayout.RowStyles.Count > 1)
                _storyTrayLayout.RowStyles[1].Height = show ? StoryTrayHeight : 0;
        }

        private bool IsChatListAtTop() { return _chatListPanel != null && -_chatListPanel.AutoScrollPosition.Y <= 2; }

        /// <summary>STORY-TRAY-HIDE: the tray shows ONLY when there are stories AND the chat list is at its very
        /// top. Purely position-based (no direction): scrolled down at all → hidden; back to the top → shown.
        /// Called on every chat-list scroll event, so stopping mid-list keeps it hidden (no re-check fires there)
        /// — only reaching the top re-shows it. NOTE: collapsing/expanding this row (above a separate scroll
        /// container) shifts the list by the tray height at the top boundary — inherent to a row-based tray.</summary>
        private void UpdateStoryTrayVisibility()
        {
            ShowStoryTray(_storyPeers != null && _storyPeers.Count > 0 && IsChatListAtTop());
        }

        /// <summary>Repaints just the tray chip for <paramref name="peerId"/> when its avatar arrives (from OnAvatarLoaded).</summary>
        private void RefreshStoryAvatar(long peerId)
        {
            if (_storyTrayBar == null || _storyTrayBar.IsDisposed) return;
            foreach (Control c in _storyTrayBar.Controls)
                if (c is StoryChip sc && sc.PeerId == peerId) { sc.Invalidate(); break; }
        }

        /// <summary>STORIES-BUILD-2: opens the full-screen story viewer starting at the tapped peer (it navigates
        /// the whole tray). On close, dims the ring for any peer whose stories were viewed (the viewer marked them
        /// seen server-side via ReadStories).</summary>
        private void OpenStoryViewer(long startPeerId)
        {
            if (_storyPeers == null || _storyPeers.Count == 0) return;
            var refs = _storyPeers.Where(e => e.Input != null)
                                  .Select(e => new StoryPeerRef { PeerId = e.PeerId, Name = e.Name, Input = e.Input })
                                  .ToList();
            if (refs.Count == 0) return;
            int idx = refs.FindIndex(r => r.PeerId == startPeerId); if (idx < 0) idx = 0;
            using (var viewer = new StoryViewerForm(_service, refs, idx, cid => _avatars.GetCached(cid), _accent))
            {
                viewer.ShowDialog(this);
                if (viewer.SeenPeers.Count > 0)
                {
                    foreach (var e in _storyPeers) if (viewer.SeenPeers.Contains(e.PeerId)) e.Unseen = false;
                    RebuildStoryTray();   // rings dim for the seen peers
                }
            }
        }

        /// <summary>One story-tray tile: a circular avatar (or letter) inside an unseen/seen RING, name below.
        /// Owner-drawn; reads the live cached avatar each paint (letter until it lands). Tap → Clicked.</summary>
        private sealed class StoryChip : Control
        {
            private readonly long _peerId; private readonly string _name;
            private bool _unseen; private readonly bool _dark; private readonly Color _accent;
            private readonly Func<long, Image> _avatar;
            public event Action Clicked;
            public long PeerId => _peerId;

            public StoryChip(long peerId, string name, bool unseen, bool dark, Color accent, Func<long, Image> avatarGetter)
            {
                _peerId = peerId; _name = name ?? ""; _unseen = unseen; _dark = dark; _accent = accent; _avatar = avatarGetter;
                Size = new Size(64, 88); Margin = new Padding(4, 2, 4, 2); Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : (_dark ? Color.FromArgb(40, 40, 40) : Color.White));
                const int D = 50; int cx = (Width - D) / 2, cy = 6;
                var avr = new Rectangle(cx, cy, D, D);
                var ring = Rectangle.Inflate(avr, 3, 3);
                using (var rp = new Pen(_unseen ? _accent : (_dark ? Color.FromArgb(95, 95, 100) : Color.FromArgb(200, 200, 205)), _unseen ? 2.5f : 1.5f))
                    g.DrawEllipse(rp, ring);
                var img = _avatar != null ? _avatar(_peerId) : null;
                if (img != null)
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(img, avr); g.ResetClip(); }
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_peerId))) g.FillEllipse(b, avr);
                    string ltr = !string.IsNullOrEmpty(_name) ? _name.Substring(0, 1).ToUpper() : "?";
                    using (var f = FontHelper.Ui(15f, FontStyle.Bold))
                        TextRenderer.DrawText(g, ltr, f, avr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                Color nc = _dark ? Color.FromArgb(210, 210, 215) : Color.FromArgb(50, 50, 55);
                using (var nf = FontHelper.For(_name, 8f))
                    TextRenderer.DrawText(g, _name, nf, new Rectangle(0, cy + D + 3, Width, 15), nc,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>Fetch the open forum's topics (async, non-blocking) then render the bar. Cached in _forumTopics.</summary>
        private async System.Threading.Tasks.Task LoadForumTopicsAsync(ChatEntry forumEntry)
        {
            var topics = await _service.GetForumTopicsAsync(forumEntry.Peer);
            if (IsDisposed || _currentForumEntry != forumEntry) return;   // switched away mid-fetch
            _forumTopics = topics;
            RebuildTopicBar();
            ShowTopicBar(topics != null && topics.Count > 0);
        }

        /// <summary>FORUM-TOPICS: select a topic (0 = All → flat all-topics history; else filter to that topic via the
        /// REUSED _thread/GetReplies machinery — GroupPeer=forumPeer, GroupRootId=topic.id). Posting then routes to the
        /// topic automatically (SendThreadComment gates on _thread != null).</summary>
        private async void SelectTopic(int topicId)
        {
            if (_currentForumEntry == null) return;
            var forum = _currentForumEntry;
            _selectedTopicId = topicId;
            if (topicId != 0 && _forumTopics != null)   // reading a topic clears its unread (badge now + server-side, best-effort)
            {
                var rt = _forumTopics.FirstOrDefault(x => x.id == topicId);
                if (rt != null) { rt.unread_count = 0; var _rd = _service.ReadDiscussionAsync(forum.Peer, topicId, rt.top_message); }
            }
            RebuildTopicBar();   // update the highlight + cleared badge
            try
            {
                if (topicId == 0)
                {
                    if (_thread != null) ClearThreadMode();
                    await LoadHistoryAsync(forum, 0);            // flat all-topics history
                }
                else
                {
                    _thread = new ThreadCtx { GroupPeer = forum.Peer, GroupRootId = topicId, GroupEntry = forum, ReturnTo = forum };
                    _selectedChat = forum;                       // paging/scroll/send target the topic thread
                    await LoadHistoryAsync(forum, 0);            // _thread != null → GetReplies(forumPeer, topicId)
                }
            }
            catch { }
        }

        /// <summary>FORUM-TOPICS: a live incoming forum message bumps ITS topic's unread badge — unless that topic is the
        /// one currently being viewed (it's being read). Topic = reply_to_top_id, else General (1). From HandleIncomingMessage.</summary>
        private void HandleForumTopicUnread(Message m, long peerId, bool outgoing)
        {
            if (_currentForumEntry == null || _forumTopics == null || m == null) return;
            if (outgoing || peerId != _currentForumEntry.PeerId) return;
            int topicId = ForumTopicIdOf(m);
            if (topicId == _selectedTopicId) return;   // that topic is open → being read, no badge bump
            int idx = _forumTopics.FindIndex(x => x.id == topicId);
            if (idx < 0) return;
            _forumTopics[idx].unread_count++;
            // update JUST that chip's badge (chips render as [All, topic0, topic1, …] → chip = idx+1) — no full rebuild, no flicker
            if (_forumTopicBar != null && idx + 1 < _forumTopicBar.Controls.Count && _forumTopicBar.Controls[idx + 1] is FolderTabItem chip)
                chip.Unread = _forumTopics[idx].unread_count;
        }

        private static int ForumTopicIdOf(Message m)
        {
            var rh = m.reply_to as MessageReplyHeader;
            return rh != null && rh.reply_to_top_id != 0 ? rh.reply_to_top_id : 1;   // else General (topic 1)
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
            // BATCH-TA-6/P3 measurement rung. This must be CHEAP before BUG-2 can be fixed by calling it
            // from the per-message incremental path: each source is a full LINQ sweep of _allChats, and a
            // custom folder's sweep runs MatchesFolder per entry. P2 removed the per-chat HashSet churn;
            // this line is how we find out whether what remains is affordable per message.
            long __ts = Logger.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            for (int i = 0; i < _folderBadgeSources.Count; i++)
                _folderBadgeSources[i].Key.Unread = _folderBadgeSources[i].Value();
            if (__ts != 0)
            {
                double __ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Logger.Diag("[BADGES] refresh sources=" + _folderBadgeSources.Count + " folders=" + _folders.Length
                    + " chats=" + _allChats.Count
                    + " ms=" + __ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
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
            // BATCH-TA-14/T4: clear the previous folder's counters BEFORE the first paint, or switching from
            // an incomplete folder to a complete one flashes the old "couldn't be loaded" line.
            _folderMissingCount = 0;
            _folderFetching = false;
            RebuildFolders();
            RenderChatList(_searchBox.Text);
            var _ = EnsureFolderMembersLoadedAsync(folder as TL.DialogFilter);   // BATCH-TA-14/T2
        }

        // BATCH-TA-14/T4 — how many enumerable members of the active folder we could not show, and whether a
        // fetch is in flight. Read by RenderChatListCore to say so instead of silently looking half-empty.
        private int _folderMissingCount;
        private bool _folderFetching;

        /// <summary>TA-14a: distinct InputPeer type names already reported as un-enumerable, so each is logged
        /// once per session rather than once per folder open. Same shape as TA-6b/G2's unhandled-update set.
        /// UI-thread only (SetActiveFolder → EnsureFolderMembersLoadedAsync), so a plain HashSet needs no lock.</summary>
        private readonly HashSet<string> _unenumerablePeerTypes = new HashSet<string>();

        /// <summary>TA-14a: PeerIdOf returned 0 for this peer, so it cannot be matched against _allChats and is
        /// silently excluded from the targeted fetch. PeerIdOf handles InputPeerUser / InputPeerChannel /
        /// InputPeerChat and returns 0 for everything else — InputPeerSelf, InputPeerUserFromMessage,
        /// InputPeerChannelFromMessage, InputPeerEmpty. Naming the concrete type is what turns "4 peers went
        /// missing" into a fixable finding.</summary>
        private void NoteUnenumerablePeer(InputPeer p)
        {
            if (!Logger.Enabled) return;
            string tn = p != null ? p.GetType().Name : "null";
            if (_unenumerablePeerTypes.Add(tn))
                Logger.Diag("[FOLDERFETCH] SKIPPED peer type=" + tn
                            + " (first this session; PeerIdOf returned 0, so it cannot be matched or fetched)");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  BATCH-TA-14/T2 — TARGETED FOLDER FETCH.
        //
        //  A folder view renders _allChats and nothing else (TA-11/M2): no placeholder, no lookup. But
        //  _allChats holds ONE page of ~100 dialogs and only grows when the MAIN list is scrolled to its
        //  bottom — and CheckChatListPaging only fires when the visible content overflows the viewport.
        //  A sparse folder does not overflow, so the model never grows: the folder is incomplete because
        //  the model is incomplete, and the model will not grow because the incomplete folder has nothing
        //  to scroll. That self-lock is what this closes, for the ENUMERABLE case.
        //
        //  Enumerable means a real DialogFilter's pinned_peers ∪ include_peers. Flags-only membership
        //  ("all groups") has no member list and is NOT handled here — that is v1.3 via
        //  Messages_GetAllDialogs, deliberately deferred because that helper has no cancellation and no
        //  progress and sleeps internally on FLOOD_WAIT.
        //
        //  Cheap because we already hold real InputPeers with access_hash, straight off the filter.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private async System.Threading.Tasks.Task EnsureFolderMembersLoadedAsync(TL.DialogFilter folder)
        {
            _folderMissingCount = 0;
            if (folder == null || _service == null) return;

            // BATCH-TA-14a — RAW counts beside the enumerated one. A folder reported enumerable=11 against a
            // dump that had shown include_peers(15), and the log could not distinguish "the user edited the
            // folder" from "PeerIdOf silently dropped 4 peers". These three numbers settle it on sight:
            // includeRaw==enumerable means the folder genuinely changed; includeRaw>enumerable is a PeerIdOf gap.
            int pinnedRaw = folder.pinned_peers != null ? folder.pinned_peers.Length : 0;
            int includeRaw = folder.include_peers != null ? folder.include_peers.Length : 0;

            var wanted = new Dictionary<long, InputPeer>();
            foreach (var arr in new[] { folder.pinned_peers, folder.include_peers })
                if (arr != null)
                    foreach (var p in arr)
                    {
                        long id = PeerIdOf(p);
                        if (id == 0) { NoteUnenumerablePeer(p); continue; }   // TA-14a: name the type that fell through
                        if (!wanted.ContainsKey(id)) wanted[id] = p;
                    }

            if (Logger.Enabled)
                Logger.Diag("[FOLDERFETCH] folder=" + folder.id + " pinnedRaw=" + pinnedRaw
                            + " includeRaw=" + includeRaw + " enumerable=" + wanted.Count
                            + (wanted.Count < pinnedRaw + includeRaw
                               ? "  ⚠ " + (pinnedRaw + includeRaw - wanted.Count) + " raw entr(ies) did not enumerate (duplicate id, or a peer type PeerIdOf does not handle — see [FOLDERFETCH] SKIPPED)"
                               : ""));

            if (wanted.Count == 0) return;

            var have = new HashSet<long>(_allChats.Select(c => c.PeerId));
            var missing = wanted.Where(kv => !have.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (missing.Count == 0) return;

            // T3: remember WHAT we are fetching for, so a switch mid-flight can be detected.
            long forAccount = _service.AccountId;
            int forFolderId = folder.id;
            _folderMissingCount = missing.Count;
            _folderFetching = true;
            RenderChatList(_searchBox.Text);   // paint the "still loading" line immediately (T4)

            if (Logger.Enabled)
                Logger.Diag("[FOLDERFETCH] folder=" + forFolderId + " enumerable=" + wanted.Count
                            + " missing=" + missing.Count + " → fetching");


            int addedTotal = 0, updatedTotal = 0, failedBatches = 0;
            try
            {
                for (int i = 0; i < missing.Count; i += 100)   // TA-11/M3: no documented cap, batch at 100 anyway
                {
                    var batch = missing.Skip(i).Take(100).ToArray();
                    TL.Messages_DialogsBase res;
                    try { res = await _service.GetPeerDialogsAsync(batch); }
                    catch (Exception ex)
                    {
                        // One bad peer (left channel → 406 CHANNEL_PRIVATE) fails the whole REQUEST, not just
                        // that peer. Degrade to "the folder shows what it has" — today's behaviour — never an
                        // error dialog on a folder click.
                        failedBatches++;
                        if (Logger.Enabled) Logger.Diag("[FOLDERFETCH] folder=" + forFolderId + " batch@" + i + " FAILED — " + ex.Message);
                        continue;
                    }

                    // T3 — post-await identity check. The fetch is awaited, so the user may have switched
                    // account or folder, or closed the form, while it was in flight.
                    if (IsDisposed) return;
                    if (_service == null || _service.AccountId != forAccount)
                    {
                        if (Logger.Enabled) Logger.Diag("[FOLDERFETCH] folder=" + forFolderId + " ABANDONED — account switched mid-fetch");
                        return;
                    }
                    var stillActive = _activeFolder as TL.DialogFilter;
                    if (stillActive == null || stillActive.id != forFolderId)
                    {
                        if (Logger.Enabled) Logger.Diag("[FOLDERFETCH] folder=" + forFolderId + " ABANDONED — view changed mid-fetch");
                        return;
                    }

                    if (res == null) continue;
                    int upd;
                    addedTotal += MergeFreshEntries(BuildDialogEntries(res), out upd);
                    updatedTotal += upd;
                }
            }
            finally { _folderFetching = false; }

            if (IsDisposed) return;
            var have2 = new HashSet<long>(_allChats.Select(c => c.PeerId));
            _folderMissingCount = wanted.Keys.Count(id => !have2.Contains(id));

            if (Logger.Enabled)
                Logger.Diag("[FOLDERFETCH] folder=" + forFolderId + " added=" + addedTotal + " updated=" + updatedTotal
                            + " stillMissing=" + _folderMissingCount + (failedBatches > 0 ? " failedBatches=" + failedBatches : ""));

            RebuildFolders();                    // badges were undercounting by the same missing rows
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
            // BATCH-TA-6/P2: the peer sets are now built ONCE PER FOLDER and cached, not rebuilt per
            // chat. This method runs per entry in two sweeps of ~645 chats (IsVisibleInCurrentView when
            // a custom folder is active, and FolderUnread for every folder badge), and it used to call
            // PeerIdSet up to THREE times per call — ~9,700 short-lived HashSets per badge refresh at
            // 645 chats / 5 folders, on a Tegra 3.
            var sets = FolderPeerSets(folder);
            if (df != null && sets.Exclude.Contains(e.PeerId)) return false;
            if (sets.Include.Contains(e.PeerId)) return true;

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

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  BATCH-TA-6/P2 — per-folder peer sets, built once and memoised.
        //
        //  INVALIDATION IS BY CONSTRUCTION, NOT BY HAND — this is the important part.
        //  The cache is keyed on the DialogFilterBase OBJECT ITSELF via a ConditionalWeakTable, so an
        //  entry can only ever be found again by the very object whose contents it summarises. Two facts
        //  make that airtight, both verified before this was written:
        //    (1) TelegramService.GetDialogFiltersAsync (:1930-1934) issues a FRESH Messages_GetDialogFilters
        //        every call and returns the newly-deserialized array — it caches nothing and reuses no
        //        instance. `_folders` is assigned wholesale in LoadFolders.
        //    (2) NOTHING mutates a folder's peer arrays in place — repo-wide there is no assignment to
        //        IncludePeers / PinnedPeers / exclude_peers on an existing filter.
        //  So every event that could invalidate a set REPLACES the key object and the stale entry simply
        //  becomes unreachable:
        //    · folder list refreshed        → LoadFolders → new objects
        //    · account switched             → LoadDialogsAsync → LoadFolders → new objects
        //    · folder edited on another device → only ever surfaces via LoadFolders → new objects
        //    · WE pin/unpin inside a folder → TogglePinInFolderAsync → CloneFilterWith → new object
        //
        //  ⚠ UPDATED BY BATCH-TA-10 — that last bullet is new, and it is the SECOND writer of `_folders`.
        //  Fact (1) used to say "assigned in exactly ONE place"; TogglePinInFolderAsync now also replaces
        //  a single ELEMENT of the array after a successful Messages_UpdateDialogFilter. Fact (2) is what
        //  keeps this cache correct, and it still holds ONLY because that path CONSTRUCTS a brand-new
        //  DialogFilter (CloneFilterWith) instead of writing into the existing one. If anyone ever
        //  "optimises" that into an in-place `folder.pinned_peers = …`, this cache silently keeps the
        //  pre-edit peer sets and the result is WRONG FILTERING AND WRONG BADGES, not a stale perf number.
        //  Any future folder writer must replace the object too.
        //  A ConditionalWeakTable also means entries die with their folder, so nothing accumulates
        //  across account switches. There is deliberately NO manual Invalidate() to forget to call —
        //  the trap with a hand-invalidated cache is that a missed hook turns a perf win into WRONG
        //  filtering and WRONG badges, which is far worse than the allocation it saves.
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        private sealed class FolderSets
        {
            public HashSet<long> Include;   // IncludePeers ∪ PinnedPeers
            public HashSet<long> Exclude;   // exclude_peers (empty for a non-DialogFilter chatlist)
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TL.DialogFilterBase, FolderSets> _folderSets
            = new System.Runtime.CompilerServices.ConditionalWeakTable<TL.DialogFilterBase, FolderSets>();

        /// <summary>The memoised peer sets for one folder. Built on first use per folder INSTANCE; see the
        /// block above for why that is self-invalidating.</summary>
        private static FolderSets FolderPeerSets(TL.DialogFilterBase folder)
        {
            FolderSets s;
            if (_folderSets.TryGetValue(folder, out s)) return s;
            s = new FolderSets
            {
                Include = PeerIdSet(folder.IncludePeers),
                Exclude = PeerIdSet((folder as TL.DialogFilter)?.exclude_peers)
            };
            s.Include.UnionWith(PeerIdSet(folder.PinnedPeers));
            // Add is a no-op-safe race loser: if another thread beat us, keep whichever landed.
            try { _folderSets.Add(folder, s); } catch { }
            return s;
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

        // ── INCHAT-SEARCH: search within the currently-open chat (the LEFT panel becomes scoped results) ──────
        private ChatEntry _inChatSearchEntry;   // non-null ⇔ IN-CHAT search mode (scoped to this open chat)
        private string _inChatQuery;
        private int _inChatOffsetId;             // oldest result id shown → offset for the next (older) page
        private int _inChatTotal;                // total match count ("N messages found")
        private bool _inChatPaging;

        /// <summary>Enters in-chat search for the open chat: the left panel becomes the scoped results view.</summary>
        private void EnterInChatSearch()
        {
            if (_selectedChat == null) return;
            _inChatSearchEntry = _selectedChat;
            _inChatQuery = null; _inChatOffsetId = 0; _inChatTotal = 0;
            _searchBox.Text = "";                 // TextChanged (in-chat mode) shows the chip; explicit call covers no-change
            RenderInChatSearchChrome();
            try { _searchBox.Focus(); } catch { }
        }

        /// <summary>Leaves in-chat search: restores the normal chat list (the chat stays open on the right).</summary>
        private void ExitInChatSearch()
        {
            if (_inChatSearchEntry == null) return;
            _inChatSearchEntry = null; _inChatQuery = null; _inChatOffsetId = 0; _inChatTotal = 0;
            _searchBox.Text = "";
            RenderChatList("");                   // guard is off now → the normal list rebuilds
        }

        /// <summary>Renders the scope chip only (rows come from DoInChatSearch). Used on enter + while typing.</summary>
        private void RenderInChatSearchChrome()
        {
            if (_inChatSearchEntry == null) return;
            _chatListPanel.SuspendLayout();
            // BATCH-TA-5/C2: dispose before clearing — same shape as RenderChatListCore (:5725) and the
            // folder bar (:6373). This path runs on EVERY keystroke while in-chat search is open, so the
            // undisposed rows it left behind leaked one HWND + two GDI fonts each, per keystroke.
            // _selectedItem MUST be nulled: it may point at a row we are about to dispose, and
            // OnChatItemClick later does `_selectedItem.Selected = false`, whose setter calls
            // Invalidate() → ObjectDisposedException on a disposed control. Harmless before this change
            // (Clear left a live orphan), a real fault after it.
            foreach (var c in _chatListPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
            _chatListPanel.Controls.Clear();
            _selectedItem = null;
            AddInChatScopeChip();
            _chatListPanel.ResumeLayout();
        }

        /// <summary>The scope chip: "×  Searching in: NAME" — tap anywhere on it to exit in-chat search.</summary>
        private void AddInChatScopeChip()
        {
            string name = _inChatSearchEntry != null ? (_inChatSearchEntry.Title ?? "this chat") : "this chat";
            var chip = new Label
            {
                Text = "×   Searching in: " + name,
                AutoSize = false, Height = 40, Width = ContentWidth(_chatListPanel),
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Margin = new Padding(0),
                Font = FontHelper.Ui(9.5f, FontStyle.Bold), Cursor = Cursors.Hand, ForeColor = _accent,
                BackColor = _dark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(235, 238, 242)
            };
            chip.Click += (s, e) => ExitInChatSearch();
            _chatListPanel.Controls.Add(chip);
        }

        /// <summary>Runs the scoped search (Messages_Search on the open peer) → renders results, newest-first.</summary>
        private async System.Threading.Tasks.Task DoInChatSearch()
        {
            if (_inChatSearchEntry == null) return;
            var q = _searchBox.Text.Trim();
            if (q.Length == 0) { _inChatQuery = null; RenderInChatSearchChrome(); return; }
            _inChatQuery = q; _inChatOffsetId = 0;
            try
            {
                var res = await _service.SearchInChatAsync(_inChatSearchEntry.Peer, q, 0, 40);
                if (_inChatSearchEntry == null || _searchBox.Text.Trim() != q) return;   // exited / query changed
                RenderInChatResults(res, replace: true);
            }
            catch { /* best-effort; leave the chip up */ }
        }

        /// <summary>Renders (replace) or appends (paging) scoped result rows: sender avatar + name + snippet + date;
        /// tap → jump to that message in the open chat. Next-page offset_id = the oldest id shown.</summary>
        private void RenderInChatResults(Messages_MessagesBase res, bool replace)
        {
            if (_inChatSearchEntry == null || res == null) return;
            var msgs = res.Messages != null ? res.Messages.OfType<Message>().ToList() : new List<Message>();
            _chatListPanel.SuspendLayout();
            if (replace)
            {
                // BATCH-TA-5/C2: dispose before clearing (same shape as :5725 / :6373). This is the
                // heavier of the two in-chat sites — it builds up to 40 real ChatListItemControl HWNDs
                // per query (SearchInChatAsync requests 40) and replace:true fires on every new query.
                // Only the replace branch clears; the append/paging path below must NOT dispose.
                // _selectedItem nulled for the same disposed-control reason as RenderInChatSearchChrome.
                foreach (var c in _chatListPanel.Controls.Cast<Control>().ToArray()) c.Dispose();
                _chatListPanel.Controls.Clear();
                _selectedItem = null;
                AddInChatScopeChip();
                _inChatTotal = (res as Messages_MessagesSlice)?.count ?? (res as Messages_ChannelMessages)?.count ?? msgs.Count;
                AddSectionHeader(_inChatTotal + (_inChatTotal == 1 ? " MESSAGE FOUND" : " MESSAGES FOUND"));
            }
            int w = ContentWidth(_chatListPanel);
            foreach (var m in msgs)
            {
                var sender = m.from_id != null ? res.UserOrChat(m.from_id) : (m.peer_id != null ? res.UserOrChat(m.peer_id) : null);
                long sid = m.from_id?.ID ?? (m.peer_id?.ID ?? 0);
                string name = sender is User su ? DisplayName(su) : ((sender as ChatBase)?.Title ?? _inChatSearchEntry.Title);
                var entry = new ChatEntry
                {
                    Peer = _inChatSearchEntry.Peer, PeerId = sid, Title = name,
                    Preview = GetDisplayText(m), Date = m.date, FocusMessageId = m.ID, PeerInfo = sender
                };
                var item = new ChatListItemControl(entry) { AccentColor = _accent, IsDark = _dark, Width = w };
                item.Click += OnInChatResultClick;
                _chatListPanel.Controls.Add(item);
                item.Avatar = _avatars.GetCached(sid);
                if (item.Avatar == null && sender != null) LoadAvatar(entry);
            }
            if (msgs.Count > 0) _inChatOffsetId = msgs.Min(m => m.ID);   // oldest shown → next-page offset
            _chatListPanel.ResumeLayout();
        }

        /// <summary>Tap a scoped result → jump to that message in the OPEN chat (load a window around it if it isn't in
        /// the current view), scroll + flash. The in-chat search stays up so you can step through matches.</summary>
        private async void OnInChatResultClick(object sender, EventArgs e)
        {
            var result = ((ChatListItemControl)sender).Entry;
            int id = result.FocusMessageId;
            if (_selectedChat == null || id <= 0) return;
            if (!ScrollToAndFlash(id))            // not in the loaded window → reload around it, then flash
            {
                await LoadHistoryAsync(_selectedChat, id);
                ScrollToAndFlash(id);
            }
        }

        /// <summary>Pages OLDER scoped matches when the results list nears the bottom (Messages_Search offset_id).</summary>
        private async void CheckInChatPaging()
        {
            if (_inChatPaging || _inChatSearchEntry == null || string.IsNullOrEmpty(_inChatQuery) || _inChatOffsetId == 0) return;
            int pos = -_chatListPanel.AutoScrollPosition.Y, viewport = _chatListPanel.ClientSize.Height, content = _chatListPanel.DisplayRectangle.Height;
            if (!(content > viewport && pos + viewport >= content - 200)) return;
            _inChatPaging = true;
            try
            {
                var q = _inChatQuery; int off = _inChatOffsetId;
                var res = await _service.SearchInChatAsync(_inChatSearchEntry.Peer, q, off, 40);
                if (_inChatSearchEntry == null || _inChatQuery != q) return;   // exited / query changed mid-fetch
                if (res != null && res.Messages != null && res.Messages.OfType<Message>().Any())
                    RenderInChatResults(res, replace: false);
            }
            catch { }
            finally { _inChatPaging = false; }
        }

        private async void OnChatItemClick(object sender, EventArgs e)
        {
            if (LogOn) Logger.Diag("[OPEN] row click peer=" + ((ChatListItemControl)sender).Entry.PeerId);   // R7 (BATCH-TA-6/P1)
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
                if (LogOn) Logger.Diag("[OPEN] duplicate open suppressed peer=" + entry.PeerId);   // R7 (BATCH-TA-6/P1)
                return;
            }
            _openLatchPeer = entry.PeerId; _openLatchTick = Environment.TickCount;
            if (_inChatSearchEntry != null) ExitInChatSearch();   // INCHAT-SEARCH: opening a (different) chat leaves in-chat search
            if (LogOn) Logger.Diag("[OPEN] OpenChat peer=" + entry.PeerId + " focus=" + focusMessageId);   // R7 (BATCH-TA-6/P1)
            PersistDraftForCurrentChat();   // DRAFTS: save the chat we're LEAVING (its composer text → server draft) before switching
            ClearThreadMode();        // COMMENTS-NAV-FIX Bug 2: leaving ANY comment thread — clear _thread so the loaders,
                                      // composer, and send target the NEW chat (LoadHistoryAsync below then resets the panel).
            _selectedChat = entry;
            if (_chatSearchBtn != null) _chatSearchBtn.Visible = true;   // INCHAT-SEARCH: the header magnifier appears with an open chat
            if (_chatMenuBtn != null) _chatMenuBtn.Visible = true;        // TA-21/S1a: the ⋮ appears with it
            if (_dockBtn != null) _dockBtn.Visible = true;                // TA-23/D1c: …and the dock toggle
            // TA-24: re-target the Info pane at the newly-opened chat, but only if it is actually on screen
            // — building it runs ProfileForm.LoadDetails, i.e. network, and a closed dock must not pay that.
            if (_dock != null && _dock.Visible && _dockPane == DockPane.Info) EnsureDockProfile();
            else DropDockProfile();
            // FORUM-TOPICS: a forum group → show the topic bar + fetch its topics (async); any other chat → hide + clear.
            // Opening still loads the flat all-topics history below (FORUM-GROUPS-FIX preserved) — the bar is additive.
            if (entry.PeerInfo is Channel fch && (fch.flags & Channel.Flags.forum) != 0)
            {
                _currentForumEntry = entry; _selectedTopicId = 0; _forumTopics = null;
                RebuildTopicBar(); ShowTopicBar(true);   // shown now; chips populate when the async fetch lands
                var _ft = LoadForumTopicsAsync(entry);
            }
            else if (_currentForumEntry != null)
            {
                _currentForumEntry = null; _forumTopics = null; _selectedTopicId = 0;
                RebuildTopicBar(); ShowTopicBar(false);
            }
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
                LoadDraftIntoComposer(entry);     // DRAFTS: restore this chat's saved draft (or clear the previous chat's text)
            }
        }

        // ── Composer footer state machine (Batch CF1) ───────────────────────

        /// <summary>Shows the normal "Write a message" composer (the COMPOSE state).</summary>
        private void ShowComposeFooter()
        {
            _footerKind = ComposerKind.Compose;
            UpdateDockSources();   // TA-23/D1d — the dock follows the composer, live (no chat switch needed)
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
            if (st.Kind == ComposerKind.Compose) { ShowComposeFooter(); return; }   // that path updates the dock
            UpdateDockSources();   // TA-23/D1d — every non-compose state hides the Emoji source

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
            // COMMENTS-NAV-FIX Bug 1: in a comment thread ALWAYS show the composer so a NON-member can post directly
            // (Telegram allows commenting without joining). Never gate it behind a "Join" footer — join is only a
            // FALLBACK if the server actually rejects the post (PostThreadComment), never a prerequisite to type.
            if (_thread != null) { ShowComposeFooter(); return; }
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

        // ── COMMENTS-JOIN-FLYOUT: the thread "Join the group" bar (docked above the composer, row 7) ──

        /// <summary>Single source of truth for the join bar's visibility: shows it iff we're in a comment thread,
        /// the linked group's Channel still carries the 'left' flag (not a member), and it wasn't dismissed this
        /// thread. Toggles row 7 to zero height when hidden so it never affects the composer/list layout.</summary>
        private void UpdateThreadJoinBar()
        {
            bool show = _thread != null && !_joinBarDismissed
                        && _thread.GroupEntry != null
                        && _thread.GroupEntry.Peer is InputPeerChannel
                        && _thread.GroupEntry.PeerInfo is Channel ch && (ch.flags & Channel.Flags.left) != 0;
            if (_threadJoinBar != null)
            {
                if (show) { _threadJoinBar.AccentColor = _accent; _threadJoinBar.IsDark = _dark; }
                _threadJoinBar.Visible = show;
                _threadJoinBar.Invalidate();
            }
            if (_rightLayout != null) _rightLayout.RowStyles[8].Height = show ? 48 : 0;   // FORUM-TOPICS: thread join bar row 7→8
        }

        /// <summary>✕ on the join bar — hide it for the rest of this thread session. Posting still works unjoined
        /// (membership is optional); a fresh thread open clears the latch and re-offers the bar.</summary>
        private void DismissThreadJoinBar()
        {
            _joinBarDismissed = true;
            UpdateThreadJoinBar();
        }

        /// <summary>Join the linked discussion group (optional). Reuses <see cref="TelegramService.JoinChannelAsync"/>.
        /// On success the bar hides (membership reflected locally so replies now arrive as normal group messages);
        /// on failure (private / invite-only / FLOOD_WAIT) a themed message shows and the bar stays for retry.</summary>
        private async void DoThreadJoin()
        {
            var t = _thread;
            var ge = t != null ? t.GroupEntry : null;
            if (ge == null || !(ge.Peer is InputPeerChannel ipc)) return;
            try
            {
                await _service.JoinChannelAsync(ipc);
            }
            catch (Exception ex)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + ge.PeerId + " fail: " + ex.Message);
                ThemedDialog.Show(this, "Join", "Couldn't join the group:\n" + ex.Message, "OK");
                return;   // bar stays so the user can retry or dismiss
            }
            if (ge.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;   // reflect membership locally (same as DoJoin)
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + ge.PeerId + " ok");
            if (_thread == t) UpdateThreadJoinBar();   // now a member → the bar hides itself
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
            _rightLayout.RowStyles[7].Height = h;   // FORUM-TOPICS: reply keyboard row 6→7
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

        /// <summary>RELEASE-FIXES-V11 (H1): raised after WE rename a peer (admin channel/group rename in EditChatInfoForm,
        /// or a contact rename in ProfileForm) → MainForm updates the ChatEntry title + repaints the row + header live so
        /// the new name shows WITHOUT an app reload (same instant-refresh spirit as the channel-photo fix).</summary>
        public static event Action<long, string> PeerTitleChanged;

        /// <summary>Raise the rename event from OUTSIDE MainForm (a C# event can only be .Invoke'd inside its
        /// declaring type) — EditChatInfoForm / ProfileForm call this after a rename.</summary>
        public static void RaisePeerTitleChanged(long peerId, string title) { PeerTitleChanged?.Invoke(peerId, title); }

        private void OnPeerTitleChanged(long peerId, string title)
        {
            if (IsDisposed || string.IsNullOrEmpty(title)) return;
            try { if (InvokeRequired) BeginInvoke((Action)(() => ApplyPeerTitle(peerId, title))); else ApplyPeerTitle(peerId, title); }
            catch { }
        }

        private void ApplyPeerTitle(long peerId, string title)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry != null)
            {
                entry.Title = title;
                if (entry.PeerInfo is Channel ch) ch.title = title;          // keep the shared TL entity consistent
                else if (entry.PeerInfo is Chat bc) bc.title = title;
                FindChatItem(peerId)?.Invalidate();                          // repaint the chat-list row
            }
            if (_selectedChat != null && _selectedChat.PeerId == peerId)     // the OPEN chat → header
            {
                if (_selectedChat.PeerInfo is Channel sch) sch.title = title;
                else if (_selectedChat.PeerInfo is Chat sbc) sbc.title = title;
                if (_chatTitle != null) _chatTitle.Text = title;
                _headerAvatarTitle = title;                                  // initials fallback source
                _headerAvatar?.Invalidate();
            }
        }

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

        /// <summary>Owner-draws the header peer avatar (circular) on the accent strip, or a deterministic initials
        /// circle until the image loads — mirrors the chat-row avatar rendering.</summary>
        private void HeaderAvatar_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var p = (Panel)sender;
            g.Clear(p.BackColor);   // blend with the header's theme background
            if (_headerAvatarPeerId == 0) return;   // empty state (no chat) → just the background
            const int d = 40;
            var rect = new Rectangle((p.Width - d) / 2, (p.Height - d) / 2, d, d);
            var img = _headerAvatarImg;   // snapshot: another path can null it between the check and the draw
            if (img != null)
            {
                try
                {
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        clip.AddEllipse(rect);
                        g.SetClip(clip);
                        g.DrawImage(img, rect);
                        g.ResetClip();
                    }
                    return;
                }
                catch
                {
                    // ACCOUNT-RECOVERY-SAFETY (Bug 3): the cached bitmap was disposed out from under us (an account switch
                    // reset the avatar store mid-paint) → DrawImage throws "Parameter is not valid", and in a Paint handler
                    // that CRASHES the app (ThreadException). Drop the dangling reference and fall through to the initials.
                    try { g.ResetClip(); } catch { }
                    _headerAvatarImg = null;
                }
            }
            using (var b = new SolidBrush(DrawHelper.AvatarColor(_headerAvatarPeerId)))
                g.FillEllipse(b, rect);
            string letter = string.IsNullOrEmpty(_headerAvatarTitle) ? "?" : _headerAvatarTitle.Substring(0, 1).ToUpper();
            using (var af = FontHelper.For(_headerAvatarTitle ?? "", 15f, FontStyle.Bold))
                TextRenderer.DrawText(g, letter, af, rect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Points the header avatar at <paramref name="entry"/>'s peer (cache hit paints immediately;
        /// otherwise the bounded loader fills it in, guarded against a stale load for a chat we've since left).
        /// Pass null for the empty "Select a chat" state.</summary>
        private void SetHeaderAvatar(ChatEntry entry)
        {
            if (_headerAvatar == null) return;
            if (entry == null)
            {
                _headerAvatarImg = null; _headerAvatarTitle = null; _headerAvatarPeerId = 0;
                _headerAvatar.Invalidate();
                return;
            }
            _headerAvatarTitle = entry.Title;
            _headerAvatarPeerId = entry.PeerId;
            _headerAvatarImg = GetCachedAvatar(entry.PeerId);
            _headerAvatar.Invalidate();
            if (_headerAvatarImg == null && entry.PeerInfo != null)
            {
                long pid = entry.PeerId;
                GetAvatarBoundedAsync(entry.PeerId, entry.PeerInfo).ContinueWith(t =>
                {
                    if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || t.Result == null) return;
                    try { BeginInvoke((Action)(() => { if (_headerAvatarPeerId == pid) { _headerAvatarImg = t.Result; _headerAvatar.Invalidate(); } })); }
                    catch { }
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
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

        /// <summary>QUICKWINS-1 PART 1: on genuine composer input, tell the open chat's peer we're typing — throttled to
        /// ~once per TypingThrottleMs (the server keeps "typing…" alive ~6s). Empty text (cleared / just sent) cancels.
        /// Inert for a disabled composer (broadcast we can't post to). Fire-and-forget; never blocks or surfaces.</summary>
        private bool _suppressComposerChange;   // DRAFTS: true while a draft is loaded programmatically → don't broadcast "typing"

        private void OnComposerTextChanged(object sender, EventArgs e)
        {
            if (_suppressComposerChange) return;
            try
            {
                if (_selectedChat == null || _messageInput == null || !_messageInput.Enabled) return;
                if (string.IsNullOrEmpty(_messageInput.Text)) { CancelTyping(); return; }   // cleared / sent → stop typing
                int now = Environment.TickCount;
                if (_typingPeer == null || unchecked(now - _lastTypingTick) >= TypingThrottleMs)
                {
                    _lastTypingTick = now;
                    _typingPeer = _selectedChat.Peer;
                    var _ = _service.SendTypingAsync(_typingPeer, true);   // fire-and-forget
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[TYPING] sent typing peer=" + _selectedChat.PeerId);
                }
            }
            catch { /* typing is best-effort */ }
        }

        // ── DRAFTS ──────────────────────────────────────────────────────────

        /// <summary>Saves the CURRENT chat's composer text as its draft (empty text → CLEARS the draft), updates the
        /// local model + the row indicator + float, and pushes it to the server. No-op if unchanged (no SaveDraft spam),
        /// in a comment thread, or while editing. Returns the save Task (or null) so a close can bound-wait it.</summary>
        private void PersistDraftForCurrentChat()
        {
            try
            {
                var chat = _selectedChat;
                if (chat == null || _thread != null || _editTarget != null || _messageInput == null) return;
                string text = (_messageInput.Text ?? "").Trim();
                if (text == (chat.DraftText ?? "")) return;   // unchanged → don't spam SaveDraft
                chat.DraftText = string.IsNullOrEmpty(text) ? null : text;
                chat.DraftDate = string.IsNullOrEmpty(text) ? DateTime.MinValue : DateTime.UtcNow;
                var _ = _service?.SaveDraftAsync(chat.Peer, text);   // fire-and-forget (SaveDraftAsync never throws)
                if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");   // float + "Draft:" indicator, live
                if (LogOn) System.Diagnostics.Debug.WriteLine("[DRAFT] save peer=" + chat.PeerId + " len=" + text.Length);
            }
            catch { }
        }

        /// <summary>Loads a chat's saved draft into the composer on open (or clears the previous chat's text when there is
        /// no draft). Suppresses the "typing" broadcast for the programmatic set. Skipped in a thread / mid-edit.</summary>
        private void LoadDraftIntoComposer(ChatEntry entry)
        {
            if (_messageInput == null || _thread != null || _editTarget != null) return;
            _suppressComposerChange = true;
            try
            {
                _messageInput.Text = entry?.DraftText ?? "";
                try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { }
            }
            finally { _suppressComposerChange = false; }
        }

        /// <summary>SEND emptied the composer → clear this chat's draft too (local + server) so no stale "Draft:" lingers.
        /// Only fires the clear RPC + re-render when the chat actually HAD a draft.</summary>
        private void ClearDraftAfterSend(ChatEntry chat)
        {
            if (chat == null || !chat.HasDraft) { if (chat != null) { chat.DraftText = null; chat.DraftDate = DateTime.MinValue; } return; }
            chat.DraftText = null; chat.DraftDate = DateTime.MinValue;
            var _ = _service?.SaveDraftAsync(chat.Peer, "");   // idempotent if the server already cleared the draft on send
            if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");
        }

        /// <summary>DRAFTS cross-device sync: an UpdateDraftMessage (a draft changed here or on another device). Update the
        /// row's draft + float + preview; if that chat is OPEN and the user hasn't diverged from the last-known draft,
        /// mirror the remote text into the composer too (never clobbering active composing).</summary>
        private void HandleDraftUpdate(Peer peer, DraftMessageBase draft)
        {
            if (peer == null) return;
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peer.ID);
            if (entry == null) return;
            string oldText = entry.DraftText;
            var dm = draft as DraftMessage;
            string text = (dm != null && !string.IsNullOrEmpty(dm.message)) ? dm.message : null;
            entry.DraftText = text;
            entry.DraftDate = text != null ? dm.date : DateTime.MinValue;

            if (_selectedChat == entry && _thread == null && _editTarget == null && _messageInput != null
                && (_messageInput.Text ?? "").Trim() == (oldText ?? "").Trim())   // user hasn't typed past the known draft
            {
                _suppressComposerChange = true;
                try { _messageInput.Text = text ?? ""; try { _messageInput.SelectionStart = _messageInput.Text.Length; } catch { } }
                finally { _suppressComposerChange = false; }
            }
            if (!IsDisposed) RenderChatList(_searchBox != null ? _searchBox.Text : "");
        }

        /// <summary>QUICKWINS-1 PART 1: stop the "typing…" we last showed (message sent, composer cleared, or chat
        /// switched). Cancels for the peer we actually told — so a chat switch stops typing in the chat we LEFT.</summary>
        private void CancelTyping()
        {
            if (_typingPeer == null) return;
            var peer = _typingPeer;
            _typingPeer = null; _lastTypingTick = 0;
            try { var _ = _service.SendTypingAsync(peer, false); } catch { }
            if (LogOn) System.Diagnostics.Debug.WriteLine("[TYPING] cancel");
        }

        // ── Message view ─────────────────────────────────────────────────────

        /// <summary>COMMENTS-THREAD source-swap: in thread mode page via GetReplies(GroupPeer, GroupRootId) — the linked
        /// discussion group + the thread root's GROUP-side id, which scopes to the ONE post's comments; else the normal
        /// GetHistory. Identical offset-id semantics, so the island pager (initial / older / newer) is reused unchanged.</summary>
        private System.Threading.Tasks.Task<Messages_MessagesBase> LoadWindowAsync(ChatEntry entry, int limit, int offsetId, int addOffset)
            => _thread != null
                ? _service.GetRepliesAsync(_thread.GroupPeer, _thread.GroupRootId, limit, offsetId, addOffset)
                : _service.GetHistoryAsync(entry.Peer, limit, offsetId, addOffset);

        private async System.Threading.Tasks.Task LoadHistoryAsync(ChatEntry entry, int focusMessageId = 0)
        {
            _chatTitle.Text = entry.Title;
            SetHeaderAvatar(entry);   // BORDERLESS-CAPTION: peer photo in the accent header
            MarkMentionsReactionsRead(entry);   // MENTION-REACTION: opening = seen → clear @ badge + heart glyph
            CancelReply();                 // a pending reply belongs to the chat we're leaving
            CancelTyping();                // stop any "typing…" we were showing in the chat we're leaving (QUICKWINS-1)
            if (_selectionMode) ExitSelectionMode();   // selection belongs to the old chat too
            if (_voiceState != VoiceState.None) AbortVoice();
            _readOutboxMaxId = entry.ReadOutboxMaxId;   // so outgoing bubbles render ✓✓ correctly
            _shownMessageIds.Clear();
            _albumBubbles.Clear();   // album grouping is per-open-chat
            _currentChatMessages.Clear();
            _repliesSourceCache.Clear();   // REPLIES-INBOX: source groups are per-open (rebuilt from the history dict)
            _groupAdminRoles = null;   // CHANNEL-META-EXTRAS: admin-role cache is per-open (refilled below for megagroups)
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
                    ? await LoadWindowAsync(entry, 50, focusMessageId, -25)
                    : await LoadWindowAsync(entry, 50, 0, 0);
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

                // REPLIES-INBOX: decorate each reply entry with its SOURCE discussion + a "View in chat" affordance
                // (repurposes the reply-quote band). Uses the history dict (reliable) to resolve the source group.
                if (entry.PeerId == RepliesPeerId)
                    foreach (var b in _messagePanel.Controls.OfType<MessageBubbleControl>())
                    {
                        var msg = _currentChatMessages.FirstOrDefault(x => x.ID == b.MessageId);
                        if (msg != null) { IngestRepliesSource(msg, history); DecorateRepliesBubble(b, msg); }
                    }

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
                LoadGroupAdminRolesAsync(entry);   // CHANNEL-META-EXTRAS (3): megagroup admin roles (async; re-applies)
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
            // BATCH-TA-18 — a proxy link is OURS to handle; it must never reach a browser. This is the one
            // funnel every link seam ends at (message text, link-preview card, inline KeyboardButtonUrl,
            // UrlAuth, callback answers, sponsored ads, profile bio, the search box), which is why the
            // interception sits HERE rather than at each caller.
            // ⚠ Note what it fixes: t.me/proxy matches IsTelegramDomain below, so before this the link
            //   skipped even the confirm dialog and went straight to Process.Start.
            if (TryHandleProxyLink(url)) return;
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

        /// <summary>BATCH-TA-18 — is this an MTProxy link, and if so, deal with it instead of shelling out?
        /// Returns TRUE for "handled, do not open a browser" — INCLUDING the malformed case, because a link
        /// that is unmistakably a proxy link but broken should say why, not silently become a web page.
        ///
        /// ⚠ ONE PARSER. The shape test is <see cref="ProxyUrl.IsProxyLink"/>; the actual parse is
        /// <see cref="ProxyUrl.TryNormalize"/> — the identical call the paste box in ProxyForm makes, so the
        /// 17-case harness covers this path too and a link cannot be accepted by one and rejected by the
        /// other. Writing a second parser here is exactly the divergence that harness exists to prevent.</summary>
        private bool TryHandleProxyLink(string url)
        {
            if (!ProxyUrl.IsProxyLink(url)) return false;

            string norm, err;
            if (!ProxyUrl.TryNormalize(url, out norm, out err))
            {
                Logger.Diag("[PROXYLINK] rejected " + ProxyUrl.SafeForLog(url));   // host:port only, never the secret
                ThemedDialog.Show(this, "Can't use that proxy link", err, "OK");   // err never quotes the secret
                try { ActiveControl = null; } catch { }
                return true;
            }

            ShowProxyLinkSheet(norm);
            return true;
        }

        /// <summary>BATCH-TA-18 — the confirmation sheet, then whatever the user chose.
        /// NEVER auto-connects: the link came from a channel, and silently moving every byte this app sends
        /// onto a stranger's server because a finger landed on a button is not acceptable (TA-18/L3).
        /// Dedupe is by exact normalised link: an already-saved proxy is SELECTED, never duplicated.</summary>
        private async void ShowProxyLinkSheet(string norm)
        {
            try
            {
                var s = AppSettings.Instance;
                if (s.ProxyList == null) s.ProxyList = new List<string>();
                int existing = s.ProxyList.FindIndex(u => string.Equals(u, norm, StringComparison.OrdinalIgnoreCase));
                Logger.Diag("[PROXYLINK] tapped " + ProxyUrl.SafeForLog(norm) + (existing >= 0 ? " (already saved)" : " (new)"));

                ProxyLinkAction action;
                using (var dlg = new ProxyLinkForm(norm, existing >= 0))
                {
                    dlg.ShowDialog(this);
                    action = dlg.Action;
                }
                // Same reason as OpenExternalUrl's confirm: don't let focus fall through to the search box.
                try { ActiveControl = null; } catch { }

                if (action == ProxyLinkAction.Cancel) { Logger.Diag("[PROXYLINK] dismissed"); return; }

                int idx = existing;
                if (idx < 0) { s.ProxyList.Add(norm); idx = s.ProxyList.Count - 1; }

                if (action == ProxyLinkAction.AddOnly)
                {
                    // Deliberately does NOT touch ProxyEnabled/ProxyActive: "collect now, test later" must
                    // not move the user off a connection that is currently working.
                    try { s.Save(); } catch { }
                    Logger.Diag("[PROXYLINK] added " + ProxyUrl.SafeForLog(norm) + " without switching ("
                                + s.ProxyList.Count + " total)");
                    ShowToast("Proxy added to your list");
                    return;
                }

                s.ProxyActive = idx;
                s.ProxyEnabled = true;
                try { s.Save(); } catch { }
                Logger.Diag("[PROXYLINK] connect → " + ProxyUrl.SafeForLog(norm) + " (index " + idx + ")");

                // EVERY DOOR MUST APPLY WHAT IT PERSISTED (§2d — the settings-door bug the device log caught).
                // This is a fourth door into the proxy setting, so it applies live like the other three:
                // warm pool down and awaited, active client reconnected, pool re-warmed. No restart.
                RefreshProxyPill();
                await ApplyProxyChangeAsync();
            }
            catch (Exception ex)
            {
                Logger.Diag("[PROXYLINK] failed: " + ex.Message);   // never echoes a link
            }
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
                var history = await LoadWindowAsync(entry, 50, _oldestMessageId, 0);
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
                            if (entry.PeerId == RepliesPeerId) { IngestRepliesSource(m, history); DecorateRepliesBubble(mbo, m); }
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
                var history = await LoadWindowAsync(entry, 50, _newestMessageId, -50);
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

        private MessageBubbleControl CreateBubble(string text, string sender, bool outgoing, DateTime date, int messageId = 0, TL.MessageEntity[] entities = null)
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
            bubble.ViewInChatClicked += OpenRepliesEntryThread;   // REPLIES-INBOX: "View in chat" row → open the source thread
            // ⚠ CALLED UNCONDITIONALLY — the guard that used to be here was
            //       if (entities != null && entities.Length > 0) bubble.SetEntities(entities);
            //   and it RE-DECIDED something SetEntities already owns, differently. SetEntities turns the
            //   inline engine on when the text has entities OR merely CONTAINS EMOJI
            //   (MessageBubbleControl.cs:741). An emoji-only message — "😂😂" — carries NO entities, so the
            //   guard skipped the call, _useRich stayed false, and the optimistic bubble drew the text with
            //   a plain font: tofu boxes. It only fixed itself when the server echo arrived and ran
            //   ApplyEntities unconditionally, which is why it looked like "emoji need a refresh".
            //   Passing null/empty is harmless: SetEntities resolves need = false and leaves the bubble in
            //   exactly the state the old path left it in.
            bubble.SetEntities(entities);   // SEND-ENTITIES: render outgoing formatting on the optimistic echo
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
            // COMMENTS Option C (in-thread reply quote): a TOP-LEVEL comment "replies to" the thread ROOT
            // (the auto-forwarded post) — that is NOT a reply to another comment, so it shows no quote
            // (matches official Telegram). Only a reply whose target is a DIFFERENT comment keeps its quote.
            // Thread-mode only (_thread != null) — normal chat reply quotes are untouched.
            if (hasReply && _thread != null &&
                (rh.reply_to_msg_id == _thread.GroupRootId || rh.reply_to_msg_id == rh.reply_to_top_id))
                hasReply = false;
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
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[KBD] WM_ACTIVATEAPP active=False → IGNORED (keyboard overlay, still foreground) fg=" + FgClass() + " composerFocus=" + (_messageInput != null && _messageInput.ContainsFocus));
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
            else if (m.Msg == TelegArm.Program.WM_ShowExisting && TelegArm.Program.WM_ShowExisting != 0)
            {
                // SINGLE-INSTANCE: a second launch broadcast this (HWND_BROADCAST) asking us to surface. Reuse the
                // tray-restore path (Show + un-minimize + Activate + BringToFront) — flicker-safe, and the second
                // instance already called AllowSetForegroundWindow(ASFW_ANY) so we're permitted to steal foreground.
                try
                {
                    if (!Visible || WindowState == FormWindowState.Minimized) RestoreFromTray();
                    else { Activate(); BringToFront(); }
                }
                catch { }
            }
            else if (m.Msg == 0x001A)   // WM_SETTINGCHANGE — KBD-CLOSE-PROBE: TabTip/tablet-mode broadcasts land here
            {
                if (LogOn)
                {
                    string area = "";
                    try { if (m.LParam != IntPtr.Zero) area = System.Runtime.InteropServices.Marshal.PtrToStringAuto(m.LParam) ?? ""; } catch { }
                    System.Diagnostics.Debug.WriteLine("[KBD] WM_SETTINGCHANGE wParam=0x" + m.WParam.ToInt64().ToString("X") + " area=" + area + " fg=" + FgClass());
                }
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

        /// <summary>BUBBLE-DATETIME (C): the topmost visible message bubble, or null. Its display Top is the scroll
        /// signal (changes with touch/wheel/scrollbar alike) that distinguishes a genuine scroll from the idle watchdog.</summary>
        private MessageBubbleControl TopVisibleBubble()
        {
            try
            {
                MessageBubbleControl best = null;
                foreach (var c in _messagePanel.Controls)
                    if (c is MessageBubbleControl b && b.MessageId != 0 && b.Bottom > 0 && (best == null || b.Top < best.Top))
                        best = b;
                return best;
            }
            catch { return null; }
        }

        /// <summary>BUBBLE-DATETIME (C): the local DateTime of the topmost visible bubble (default if none).</summary>
        private DateTime TopVisibleDate()
        {
            var b = TopVisibleBubble();
            return b != null && b.Date != default(DateTime) ? b.Date.ToLocalTime() : default(DateTime);
        }

        /// <summary>BUBBLE-DATETIME (C): Telegram-style day label — Today / Yesterday / "MMMM d" (+ year when older).</summary>
        private static string FormatDayLabel(DateTime local)
        {
            var today = DateTime.Now.Date;
            if (local.Date == today) return "Today";
            if (local.Date == today.AddDays(-1)) return "Yesterday";
            return local.Year == today.Year ? local.ToString("MMMM d") : local.ToString("MMMM d, yyyy");
        }

        /// <summary>BUBBLE-DATETIME (C): show/refresh the floating day pill for the topmost visible message and keep
        /// the fade timer armed. The topmost-scan is throttled (~80ms) so per-pan-tick calls (~100Hz on touch) stay
        /// cheap — freeze-era discipline; only the fade tick is written every call. Overlay only; never touches paging.</summary>
        private void UpdateDateFlyout()
        {
            if (_dateFlyout == null || _msgHost == null || _selectedChat == null) return;
            int now = Environment.TickCount;
            // DATE-FLYOUT-TUNE (auto-hide fix): the 200ms _scrollWatch watchdog ALSO calls here on every tick — without
            // this gate it reset _dateFlyoutScrollTick forever, so the idle fade never fired ("always shown"). Count it as
            // scroll activity ONLY when the top-visible bubble ACTUALLY moved (reflects touch/wheel/scrollbar equally; it
            // reads the RESULT of scrolling, touching NO scroll code). Idle ticks now fall through → the ~5s fade runs.
            var topBubble = TopVisibleBubble();
            int sig = topBubble != null ? topBubble.Top : int.MinValue;
            if (sig == _dateFlyoutTopSig) return;                          // no real scroll (idle watchdog tick) → leave the pill + fade clock alone
            _dateFlyoutTopSig = sig;
            _dateFlyoutScrollTick = now;                                    // genuine scroll → (re)start the ~5s idle clock (DateFlyoutTick reads this)
            if (_dateFlyoutTimer != null && !_dateFlyoutTimer.Enabled) _dateFlyoutTimer.Start();
            if (_dateFlyout.Visible && unchecked(now - _dateFlyoutCalcTick) < 80) return;   // throttle the control scan
            _dateFlyoutCalcTick = now;
            var d = TopVisibleDate();
            if (d == default(DateTime)) return;
            string label = FormatDayLabel(d);
            if (_dateFlyout.Text != label)
            {
                _dateFlyout.Text = label;
                var sz = TextRenderer.MeasureText(label, _dateFlyout.Font);
                _dateFlyout.Size = new Size(sz.Width + 24, 24);
            }
            _dateFlyout.Left = Math.Max(0, (_msgHost.ClientSize.Width - _dateFlyout.Width) / 2);
            _dateFlyout.Top = DateFlyoutTopOffset + 8;   // DATE-FLYOUT-TUNE: offset-able so a future top TOPIC bar can push it below itself
            _dateFlyout.Alpha = 255;
            if (!_dateFlyout.Visible) { _dateFlyout.Visible = true; _dateFlyout.BringToFront(); }
            else _dateFlyout.Invalidate();
        }

        // Idle → fade the day pill out (single timer; runs only while shown/fading, then stops).
        private void DateFlyoutTick(object sender, EventArgs e)
        {
            if (_dateFlyout == null || !_dateFlyout.Visible) { _dateFlyoutTimer?.Stop(); return; }
            if (unchecked(Environment.TickCount - _dateFlyoutScrollTick) < DateFlyoutHoldMs) return;   // DATE-FLYOUT-TUNE: hold ~5s after the last scroll, then fade (a scroll resets _dateFlyoutScrollTick)
            _dateFlyout.Alpha -= 45;
            if (_dateFlyout.Alpha <= 0) { _dateFlyout.Visible = false; _dateFlyoutTimer.Stop(); }
            else _dateFlyout.Invalidate();
        }

        /// <summary>BUBBLE-DATETIME (C): the floating day pill — a rounded translucent chip with centered text and a
        /// settable Alpha for the fade. Owner-drawn; a host child, never a scroll child.</summary>
        private sealed class DateFlyoutPill : Control
        {
            public bool IsDark { get; set; }
            public Color Accent { get; set; } = Color.FromArgb(0x53, 0x4A, 0xB7);   // accent-driven fill (set live)
            public int Alpha = 255;
            public DateFlyoutPill()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int a = Math.Max(0, Math.Min(255, Alpha));
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Color fill = Color.FromArgb(a, Accent.R, Accent.G, Accent.B);   // accent-driven pill (Alpha = fade)
                using (var b = new SolidBrush(fill))
                using (var p = DrawHelper.RoundedRect(r, Height / 2))
                    g.FillPath(b, p);
                TextRenderer.DrawText(g, Text, Font, r, Color.FromArgb(a, 255, 255, 255),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                // Clip the control to the pill shape so the corners are truly ROUNDED — the transparent square
                // corners were showing the background through. Region matches the FillPath capsule (radius Height/2).
                if (Width > 0 && Height > 0)
                    using (var p = DrawHelper.RoundedRect(new Rectangle(0, 0, Width, Height), Height / 2))
                        Region = new Region(p);
            }
        }

        /// <summary>Updates the floating button's visibility/count and triggers the read when at the bottom.</summary>
        private void OnScrollPositionChanged()
        {
            if (_jumpBtn == null) return;
            bool hasMsgs = _selectedChat != null && _messagePanel.Controls.Count > 0;
            if (!hasMsgs) { if (_jumpBtn.Visible) _jumpBtn.Visible = false; return; }

            UpdateDateFlyout();   // BUBBLE-DATETIME (C): floating day pill (throttled scan; overlay only)
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
            RefreshFolderBadges();   // TA-6b/G1 (DOWN): THE TA-6 GATE FAILURE — reading a chat in TelegArm
        }

        private async System.Threading.Tasks.Task SafeReadHistory(InputPeer peer, int maxId)
        {
            try { await _service.ReadHistoryAsync(peer, maxId); } catch { }
        }

        /// <summary>MENTION-REACTION: on opening a chat, clear its unread @mentions + reactions-to-you (server +
        /// local + row), so the "@" badge and heart glyph disappear once seen. Best-effort RPCs (fire-and-forget).</summary>
        private void MarkMentionsReactionsRead(ChatEntry entry)
        {
            if (entry == null) return;
            if (entry.UnreadMentions > 0)
            {
                entry.UnreadMentions = 0;
                var _ = SafeRead(() => _service.ReadMentionsAsync(entry.Peer));
            }
            if (entry.UnreadReactions > 0)
            {
                entry.UnreadReactions = 0;
                var _ = SafeRead(() => _service.ReadReactionsAsync(entry.Peer));
            }
            FindChatItem(entry.PeerId)?.Invalidate();
        }

        private static async System.Threading.Tasks.Task SafeRead(Func<System.Threading.Tasks.Task> op)
        {
            try { await op(); } catch { }
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
            // REPLIES-INBOX: the reply-quote band is repurposed as a "View in chat" affordance — replyToMsgId is the
            // ENTRY's OWN id. Open the source discussion thread instead of an in-chat scroll.
            if (IsRepliesInbox) { OpenRepliesEntryThread(replyToMsgId); return; }
            if (ScrollToAndFlash(replyToMsgId)) return;     // already in the loaded window (thread OR normal)
            var chat = _selectedChat;
            if (chat == null) return;
            if (_thread != null)
            {
                // COMMENTS Option C: the target comment is older than the loaded thread window. STAY IN THE
                // THREAD — reload the thread island CENTERED on it (LoadHistoryAsync in thread mode swaps to
                // GetReplies via LoadWindowAsync, so this is scoped to the thread, not the whole group), then
                // flash. NEVER OpenChat here — that would exit the thread and open the full discussion group.
                BeginInvoke((Action)(async () =>
                {
                    await LoadHistoryAsync(chat, replyToMsgId);   // _thread!=null → GetReplies focused island
                    if (_thread != null) ScrollToAndFlash(replyToMsgId);   // add the highlight (best-effort if deleted)
                }));
                return;
            }
            BeginInvoke((Action)(async () =>
            {
                await OpenChat(chat, replyToMsgId);          // focused load centered on the target (same chat)
                ScrollToAndFlash(replyToMsgId);
            }));
        }

        // ── REPLIES-INBOX: entry source decoration + "View in chat" jump ─────────────────────────────

        /// <summary>Caches the SOURCE discussion group of a reply entry (from the history dict, keyed by group id) so
        /// "View in chat" can build an InputPeerChannel later without depending on the manager cache. No-op unless the
        /// message is a comment-reply (carries reply_to_peer_id).</summary>
        private void IngestRepliesSource(Message m, Messages_MessagesBase history)
        {
            var rh = m?.reply_to as MessageReplyHeader;
            if (rh == null || rh.reply_to_peer_id == null || history == null) return;
            var g = history.UserOrChat(rh.reply_to_peer_id);
            if (g == null) return;
            _repliesSourceCache[rh.reply_to_peer_id.ID] = g;
            if (g is ChatBase gc) _peerNames[gc.ID] = gc.Title;   // name cache for the header + future resolves
        }

        /// <summary>Restructures a Replies-inbox entry into Telegram's card layout: a QUOTED SOURCE block (reuses the
        /// reply-quote band — the source group name + who replied) at the top, the reply text/media as the body, then a
        /// distinct bottom "View in chat ›" ROW (a real hit region → <see cref="OpenRepliesEntryThread"/>). The stray
        /// "Forwarded from…" header is suppressed (the quoted block replaces it). Non-reply entries stay plain.</summary>
        private void DecorateRepliesBubble(MessageBubbleControl b, Message m)
        {
            var rh = m?.reply_to as MessageReplyHeader;
            if (b == null || rh == null || rh.reply_to_peer_id == null) return;

            // Source discussion group — the quoted block's bold header.
            string src = null;
            IPeerInfo g;
            if (_repliesSourceCache.TryGetValue(rh.reply_to_peer_id.ID, out g) && g != null)
                src = (g as ChatBase)?.Title ?? ((g as User) != null ? DisplayName((User)g) : null);
            if (string.IsNullOrEmpty(src) && _peerNames.TryGetValue(rh.reply_to_peer_id.ID, out var nm)) src = nm;

            // Who replied — the quoted block's second line (resolved from the entry's sender).
            string replier = null;
            if (m.from_id != null)
            {
                if (_peerNames.TryGetValue(m.from_id.ID, out var rn)) replier = rn;
                else { var ri = ResolvePeer(m.from_id); replier = ri is User ru ? DisplayName(ru) : (ri as ChatBase)?.Title; }
            }

            b.ForwardedFrom = null;                 // suppress the stray "Forwarded from…" — the quoted block replaces it
            b.ReplyToMsgId = m.ID;                  // own id → the quoted block is ALSO tappable (the jump lookup key)
            b.ReplySender = src ?? "Discussion";    // the SOURCE discussion group (bold, top of the quote)
            b.ReplyPreview = !string.IsNullOrEmpty(replier) ? replier : "replied to your comment";   // 2nd quote line
            b.ShowViewInChat = true;                // the distinct bottom "View in chat ›" row (the primary affordance)
            b.Measure();
        }

        /// <summary>Tap of a Replies entry's "View in chat" band: resolve the source group + thread root off the
        /// message and open the discussion thread (reuses the comment thread view, entered from the group side).</summary>
        private void OpenRepliesEntryThread(int entryMsgId)
        {
            var m = _currentChatMessages.FirstOrDefault(x => x.ID == entryMsgId);
            var rh = m?.reply_to as MessageReplyHeader;
            if (rh == null || rh.reply_to_peer_id == null)
            { ThemedDialog.Show(this, "Replies", "This entry has no source thread to open.", "OK"); return; }
            if (LogOn) System.Diagnostics.Debug.WriteLine("[REPLIES] viewinchat peer=" + rh.reply_to_peer_id.ID
                + " top=" + rh.reply_to_top_id + " msg=" + rh.reply_to_msg_id);

            IPeerInfo groupInfo;
            if (!_repliesSourceCache.TryGetValue(rh.reply_to_peer_id.ID, out groupInfo) || groupInfo == null)
                groupInfo = ResolvePeer(rh.reply_to_peer_id);
            if (groupInfo == null)
            { ThemedDialog.Show(this, "Replies", "Couldn't resolve the source group.", "OK"); return; }

            var groupEntry = EntryFromPeerInfo(groupInfo);
            if (groupEntry == null || !(groupEntry.Peer is InputPeerChannel))
            { ThemedDialog.Show(this, "Replies", "The source thread isn't available.", "OK"); return; }

            int rootId = rh.reply_to_top_id != 0 ? rh.reply_to_top_id : rh.reply_to_msg_id;
            var returnTo = _selectedChat;   // the Replies inbox — "‹ back" returns here
            var _ = EnterThreadFromGroup(groupEntry, rootId, rh.reply_to_msg_id, returnTo);
        }

        /// <summary>Opens a comment thread DIRECTLY from the discussion group + thread root (no channel post needed —
        /// the Replies entry already carries them). Mirrors <see cref="OpenComments"/> minus GetDiscussionMessage;
        /// ThreadCtx.ChannelPeer is left null (verified never read) and ReadDiscussion is skipped. Reuses the whole
        /// thread view (scoped GetReplies reads, posting via GroupPeer+GroupRootId, the join flyout, nav teardown).</summary>
        private async System.Threading.Tasks.Task EnterThreadFromGroup(ChatEntry groupEntry, int groupRootId, int focusMsgId, ChatEntry returnTo)
        {
            if (_thread != null || groupEntry == null) return;
            if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] view-in-chat group=" + groupEntry.PeerId + " root=" + groupRootId + " focus=" + focusMsgId);
            try
            {
                _thread = new ThreadCtx
                {
                    ChannelPeer = null,      // unknown from the Replies inbox — the field is never read (verified)
                    PostMsgId = 0,
                    GroupPeer = groupEntry.Peer,
                    GroupRootId = groupRootId,
                    GroupEntry = groupEntry,
                    ReturnTo = returnTo,
                    ReturnAnchorId = 0
                };
                groupEntry.UnreadCount = 0;
                // TA-6b/A+B: this site did NOT publish the change at all — no row repaint, no tray, no
                // badges. Same shape as the other six read sites now.
                FindChatItem(groupEntry.PeerId)?.Invalidate();
                UpdateTrayTooltip();
                RefreshFolderBadges();   // (DOWN): entering a thread from the group reads the group
                _selectedChat = groupEntry;                 // paging / scroll / updates target the thread from here
                await LoadHistoryAsync(groupEntry, focusMsgId);   // thread mode → GetReplies focused island
                if (_thread == null) return;                // back hit while loading
                _chatTitle.Text = "‹ Comments";
                _chatStatus.Text = groupEntry.Title;
                ShowComposeFooter();
                _joinBarDismissed = false;
                UpdateThreadJoinBar();                      // offer Join if not a member of the source group
                if (focusMsgId > 0) ScrollToAndFlash(focusMsgId);   // land on the replied-to comment + flash
            }
            catch (Exception ex)
            {
                _thread = null;
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] view-in-chat failed: " + ex.Message);
                ThemedDialog.Show(this, "Replies", "Couldn't open the thread:\n" + ex.Message, "OK");
            }
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
            if (_thread != null) { SendThreadComment(); return; }   // COMMENTS-POST: route to the discussion-thread send
            if (_footerKind != ComposerKind.Compose) return;   // gated: composer isn't shown in non-compose states
            var raw = _messageInput.Text.Trim();
            if (raw.Length == 0 || _selectedChat == null) return;

            var chat = _selectedChat;

            // Editing an existing message takes precedence over sending a new one.
            if (_editTarget != null)
            {
                var target = _editTarget;
                _messageInput.Text = "";
                CancelReply();          // also clears _editTarget + hides the strip
                await ApplyEdit(chat, target, raw);   // ApplyEdit parses the markdown itself
                return;
            }

            // SEND-ENTITIES: markdown → plain text + entities (no markers → entities null → plain send, unchanged).
            TL.MessageEntity[] entities;
            string text = MarkdownEntities.Parse(raw, out entities);
            if (text.Length == 0) return;   // the message was only markers

            int replyId = _replyTarget?.ID ?? 0;       // capture before clearing the composer
            string replyPreview = null;
            if (_replyTarget != null)
            {
                replyPreview = GetDisplayText(_replyTarget);
                if (replyPreview.Length > 60) replyPreview = replyPreview.Substring(0, 60) + "…";
            }

            _messageInput.Text = "";
            CancelReply();
            ClearDraftAfterSend(chat);   // DRAFTS: the composer just emptied on send → clear any draft for this chat

            // Optimistic bubble — clock while sending, then ✓ on confirm. Carry the reply quote.
            var bubble = CreateBubble(text, null, true, DateTime.UtcNow, 0, entities);
            bubble.Pending = true;
            if (replyPreview != null) { bubble.ReplyPreview = replyPreview; bubble.Measure(); }
            _messagePanel.Controls.Add(bubble);
            ScrollMessagesToBottom();

            try
            {
                var sent = await _service.SendTextAsync(chat.Peer, text, replyId, entities);
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

        // ── SEND-ENTITIES: composer formatting helpers — wrap the selection in markdown; MarkdownEntities.Parse
        //    turns it into MessageEntity[] on send. Uses only public SelectionStart/SelectionLength/Text (no internals).
        private void WrapComposerSelection(string open, string close)
        {
            var tb = _messageInput; if (tb == null) return;
            try
            {
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                string sel = t.Substring(start, len);
                tb.Text = t.Substring(0, start) + open + sel + close + t.Substring(start + len);
                tb.SelectionStart = len > 0 ? start + open.Length + len + close.Length : start + open.Length;
                tb.Focus();
            }
            catch { }
        }

        private void InsertComposerLink()
        {
            var tb = _messageInput; if (tb == null) return;
            try
            {
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                string label = len > 0 ? t.Substring(start, len) : "text";
                tb.Text = t.Substring(0, start) + "[" + label + "](url)" + t.Substring(start + len);
                tb.SelectionStart = start + 1 + label.Length + 2;   // caret at the "url" placeholder
                tb.SelectionLength = 3;                              // select "url" so the user overtypes it
                tb.Focus();
            }
            catch { }
        }

        private void PasteIntoComposer()
        {
            var tb = _messageInput; if (tb == null) return;
            try
            {
                if (!Clipboard.ContainsText()) return;
                string clip = Clipboard.GetText();
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                tb.Text = t.Substring(0, start) + clip + t.Substring(start + len);
                tb.SelectionStart = start + clip.Length;
                tb.Focus();
            }
            catch { }
        }

        /// <summary>COMMENTS-POST: post the composer text as a comment in the open discussion thread. Direct post is
        /// the PRIMARY path (Telegram allows commenting without the group appearing in your chat list); join is only a
        /// FALLBACK if the server rejects for membership. Optimistic bubble → raw thread-send → live-append on success.</summary>
        private async void SendThreadComment()
        {
            var thread = _thread;
            if (thread == null || _messageInput == null) return;
            var raw = _messageInput.Text.Trim();
            if (raw.Length == 0) return;

            _messageInput.Text = "";
            CancelReply();

            // SEND-ENTITIES: markdown → plain text + entities for the comment too.
            TL.MessageEntity[] entities;
            string text = MarkdownEntities.Parse(raw, out entities);
            if (text.Length == 0) return;

            var bubble = CreateBubble(text, null, true, DateTime.UtcNow, 0, entities);   // outgoing, optimistic
            bubble.Pending = true;
            _messagePanel.Controls.Add(bubble);
            ScrollMessagesToBottom();

            await PostThreadComment(thread, text, entities, bubble, allowJoinRetry: true);
        }

        /// <summary>The send + membership-fallback core. Posts via the GROUP-scoped thread (GroupPeer + GroupRootId) —
        /// the same thread reads show, and the form a non-admin can post through (channel-peer sends are admin-only).
        /// On success: confirm the optimistic bubble + cache the (group-side) message. On a membership rejection
        /// (CHAT_WRITE_FORBIDDEN): join the group, then retry ONCE. Any other error (or a failed join): drop the
        /// optimistic bubble and restore the text so it isn't lost.</summary>
        private async System.Threading.Tasks.Task PostThreadComment(ThreadCtx thread, string text, TL.MessageEntity[] entities, MessageBubbleControl bubble, bool allowJoinRetry)
        {
            try
            {
                var sent = await _service.SendThreadCommentAsync(thread.GroupPeer, thread.GroupRootId, text, entities);
                if (sent != null)
                {
                    _shownMessageIds.Add(sent.ID);                       // dedupe the echo update
                    if (bubble != null) { bubble.MessageId = sent.ID; bubble.Pending = false; bubble.Invalidate(); }
                    if (!_currentChatMessages.Any(x => x.ID == sent.ID)) _currentChatMessages.Add(sent);
                }
                else if (bubble != null) { bubble.Pending = false; bubble.Invalidate(); }   // sent, but no full message echoed
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post ok root=" + thread.GroupRootId + " post=" + thread.PostMsgId);
            }
            catch (Exception ex)
            {
                if (allowJoinRetry && IsMembershipError(ex) && _selectedChat != null && _selectedChat.Peer is InputPeerChannel ipc)
                {
                    if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post rejected (membership) → join + retry");
                    try
                    {
                        await _service.JoinChannelAsync(ipc);
                        if (_selectedChat.PeerInfo is Channel ch) ch.flags &= ~Channel.Flags.left;
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join linked=" + _selectedChat.PeerId + " ok");
                        await PostThreadComment(thread, text, entities, bubble, allowJoinRetry: false);   // retry ONCE, no further join
                        return;
                    }
                    catch (Exception jex)
                    {
                        FailThreadPost(bubble, text);
                        if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] join fail: " + jex.Message);
                        ThemedDialog.Show(this, "Comments", "Couldn't join to comment:\n" + jex.Message, "OK");
                        return;
                    }
                }
                FailThreadPost(bubble, text);
                if (LogOn) System.Diagnostics.Debug.WriteLine("[COMMENTS] post fail: " + ex.Message);
                ThemedDialog.Show(this, "Comments", FriendlyPostError(ex), "OK");
            }
        }

        /// <summary>COMMENTS-NAV-FIX 2.1: a human message for a failed comment post. CHAT_ADMIN_REQUIRED = the channel
        /// restricts commenting (join can't fix it — never a join prompt); the rest map common send errors.</summary>
        private static string FriendlyPostError(Exception ex)
        {
            string m = ex?.Message ?? "";
            if (m.IndexOf("CHAT_ADMIN_REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Commenting is restricted on this channel — you can't comment here.";
            if (m.IndexOf("MSG_TOO_LONG", StringComparison.OrdinalIgnoreCase) >= 0) return "That comment is too long.";
            if (m.IndexOf("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) >= 0)
                return "You're commenting too fast — wait a moment and try again.";
            return "Couldn't post the comment:\n" + m;
        }

        /// <summary>COMMENTS-POST 2.2: a comment send failed — drop the optimistic bubble and put the text back in the
        /// composer so the user doesn't lose it.</summary>
        private void FailThreadPost(MessageBubbleControl bubble, string text)
        {
            // Unconditional remove+dispose (Controls.Remove is a safe no-op if it isn't a child) so a rejected post
            // NEVER leaves a phantom optimistic bubble, even if the panel was reconciled during the await.
            try { if (bubble != null) { _messagePanel.Controls.Remove(bubble); bubble.Dispose(); } } catch { }
            if (string.IsNullOrEmpty(_messageInput.Text)) _messageInput.Text = text;
        }

        /// <summary>True when a send was rejected because the discussion group requires membership (the ONLY case that
        /// triggers the join fallback — never on FLOOD_WAIT / too-long / network errors).</summary>
        private static bool IsMembershipError(Exception ex)
        {
            string m = ex?.Message ?? "";
            return m.IndexOf("CHAT_WRITE_FORBIDDEN", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("CHANNEL_PRIVATE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Edits a message server-side, then rebuilds its bubble in place with the new text.</summary>
        private async System.Threading.Tasks.Task ApplyEdit(ChatEntry chat, Message target, string newText)
        {
            try
            {
                // SEND-ENTITIES: the edited text is markdown too → plain text + entities.
                TL.MessageEntity[] entities;
                string plain = MarkdownEntities.Parse(newText, out entities);
                await _service.EditMessageAsync(chat.Peer, target.ID, plain, entities);
                if (_selectedChat != chat) return;     // user switched chats meanwhile
                var msg = _currentChatMessages.FirstOrDefault(x => x.ID == target.ID) ?? target;
                msg.message = plain;                    // TL Message.message is settable
                msg.entities = entities;                // so the rebuilt bubble shows the new formatting
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
            _rightLayout.RowStyles[6].Height = active ? 40 : 0;   // FORUM-TOPICS: reply strip row 5→6
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
            bool proxyChanged = false;
            using (var dlg = new SettingsForm(_service))
            {
                dlg.ProxyChangeApplied += () => proxyChanged = true;
                dlg.ShowDialog(this);
            }
            // A proxy switched from Settings must take effect like one switched from the pill — the
            // device log caught this door persisting a change with no APPLY-LIVE behind it. Deferred
            // until the dialog is CLOSED so the teardown/reconnect can't race a modal still on screen.
            if (proxyChanged) { RefreshProxyPill(); var _ = ApplyProxyChangeAsync(); }
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
            ShowInTaskbar = true;   // STARTUP-SETTING: a --startup launch began off-taskbar; ensure the taskbar button returns
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
            // DRAFTS: best-effort save of the open chat's draft on a REAL close (bounded like presence; never blocks
            // shutdown). The primary save is on chat/account switch — this covers "typed then closed without switching".
            try
            {
                var dchat = _selectedChat;
                if (dchat != null && _thread == null && _editTarget == null && _messageInput != null && !TelegramService.TearingDown)
                {
                    string dtext = (_messageInput.Text ?? "").Trim();
                    if (dtext != (dchat.DraftText ?? "")) _service?.SaveDraftAsync(dchat.Peer, dtext)?.Wait(600);
                }
            }
            catch { }
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
        // NOTIFY-BACKGROUND: account-scoped dedup ((account, peer, msg)) so two accounts in the SAME group both notify.
        private readonly HashSet<(long, long, int)> _bgToastSeen = new HashSet<(long, long, int)>();
        private readonly Queue<(long, long, int)> _bgToastSeenOrder = new Queue<(long, long, int)>();

        // ── BATCH-TA-26/B1 — THE BACKLOG GATE ────────────────────────────────────────────────────
        /// <summary>How far BEFORE this process started a message may be dated and still notify.
        /// Covers the ordinary race where a message is sent while we are connecting: the update can be
        /// delivered a few seconds after start-up while its server timestamp predates it. 60 s is generous
        /// for that and still far below any real "I was offline" gap.</summary>
        private const int NotifyBacklogGraceSeconds = 60;
        private static readonly DateTime NotifyProcessStartUtc = DateTime.UtcNow;

        /// <summary>TRUE for a message that predates this run — i.e. one replayed by getDifference rather
        /// than newly received.
        ///
        /// ⚠ WHY A DATE AND NOT A "was this live?" FLAG: there isn't one. WTC's UpdateManager SYNTHESISES
        /// UpdateNewMessage from the getDifference result and raises it through the SAME callback as a live
        /// update (HandleDifference), so nothing on the update distinguishes them. The message's own
        /// server-set timestamp is the only honest signal we have.
        /// ⚠ AND WHY NO PERSISTED WATERMARK, which is the obvious "smarter" version: it re-creates the very
        /// bug this fixes. A watermark says "notify everything since I last looked", so the first launch
        /// after a few hours offline would fire a burst of toasts — exactly the behaviour being removed.
        /// What the user missed is already carried, better, by the unread badges and the chat list; a
        /// notification is an INTERRUPTION, and interrupting about something that happened hours ago is
        /// noise. Deliberate: launching does not re-announce the backlog.
        /// The message pump only starts after start-up, so `_toastSeen` cannot help here — on a cold start
        /// it is empty, which is precisely why every backlogged message used to get through.</summary>
        private static bool IsBacklog(Message m)
        {
            return m != null && m.date < NotifyProcessStartUtc.AddSeconds(-NotifyBacklogGraceSeconds);
        }

        // ── BATCH-TA-26c — DOES THIS MESSAGE MENTION *ME*? ───────────────────────────────────────
        /// <summary>The mute break-through test. Answers <c>how</c> with the rule that fired, so the log
        /// says WHY a message got through instead of leaving it to be re-derived later.
        ///
        /// ⚠ WHY THIS IS NOT JUST `Message.Flags.mentioned`, WHICH IS WHAT WE USED TO DO.
        /// MEASURED (log telegarm_20260806_125552.log, peer 1824808427 — a channel muted only by the
        /// account-level broadcasts default):
        ///     id=90 preview='@xhamedz'  -> [NOTIFY] suppressed … reason=muted
        ///     id=92 preview='@xhamedz'  -> [NOTIFY] suppressed … reason=muted
        /// `@xhamedz` IS the receiving account's username, and the server did NOT set the flag — the gate
        /// only reaches the mute check when the flag is false. In the SAME chat in an earlier run, replies
        /// DID carry it (msgs 86/88 emitted), so the server sets it for replies-to-me and not for this.
        /// Official clients show these notifications because they do not trust one bit: they decide
        /// mention-ness themselves from the entities and the user's own identity. So do we now.
        ///
        /// FOUR RULES, cheapest first, each recorded distinctly so the next log is self-explaining:
        ///   flag        — the server said so (still authoritative; covers replies-to-me)
        ///   entity-id   — MessageEntityMentionName addressed at my user id (a text-mention of me by name)
        ///   entity-text — MessageEntityMention whose text is my @username
        ///   text        — my @username appears in the body with NO entity at all. This is the one that
        ///                 catches the case above, and it is deliberate: a message that writes your handle
        ///                 is addressing you whether or not the server chose to encode an entity.
        /// ⚠ Rule 4 is token-exact: "@hamed" must not match "@hamedz". Without that guard every user whose
        ///   username is a prefix of someone else's would break through on the wrong messages.</summary>
        private static bool MentionsMe(Message m, User me, out string how)
        {
            how = null;
            if (m == null) return false;
            if ((m.flags & Message.Flags.mentioned) != 0) { how = "flag"; return true; }
            if (me == null) return false;

            string text = m.message ?? "";
            if (m.entities != null)
                foreach (var e in m.entities)
                {
                    if (e is MessageEntityMentionName mn && mn.user_id == me.id) { how = "entity-id"; return true; }
                    if (e is InputMessageEntityMentionName imn && imn.user_id is InputUser iu && iu.user_id == me.id)
                    { how = "entity-id"; return true; }
                }

            string un = me.MainUsername;
            if (string.IsNullOrEmpty(un)) return false;

            if (m.entities != null)
                foreach (var e in m.entities)
                    if (e is MessageEntityMention && e.offset >= 0 && e.length > 0
                        && e.offset + e.length <= text.Length
                        && string.Equals(text.Substring(e.offset, e.length).TrimStart('@'), un,
                                         StringComparison.OrdinalIgnoreCase))
                    { how = "entity-text"; return true; }

            if (ContainsUsernameToken(text, un)) { how = "text"; return true; }
            return false;
        }

        /// <summary>"@name" present as a WHOLE token — the next character must not continue a username
        /// (Telegram usernames are letters, digits and underscore), so "@hamed" does not match "@hamedz".</summary>
        private static bool ContainsUsernameToken(string text, string username)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(username)) return false;
            string needle = "@" + username;
            int i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = i + needle.Length;
                if (end >= text.Length) return true;
                char c = text[end];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return true;
                i = end;
            }
            return false;
        }

        /// <summary>One [NOTIFY] line per gate decision (Logger.Enabled-gated; nothing per tick).</summary>
        private static void NotifyLog(string what, long peerId, int msgId, string reason)
        {
            if (!Logger.Enabled) return;   // pre-gate: skip the string building entirely when logging is off
            Logger.Diag("[NOTIFY] " + what + " peer=" + peerId + " msg=" + msgId
                + (reason != null ? " reason=" + reason : ""));
        }

        /// <summary>THE notify gate: own → dup → focused → mute (explicit, then category), then emit.
        /// Every decision is logged; the (peer,msg) key is recorded on FIRST sight so a replayed message
        /// can never notify twice in a process lifetime (NOTIFY-FIX).</summary>
        private void MaybeToast(long peerId, Message m, bool outgoing)
        {
            if (m == null) return;
            if (!CanDeliverNotification()) return;                                 // TA-27/W7: the tray guard, split

            // ── BATCH-TA-32/N4 — THE MASTER MUTE ─────────────────────────────────────────────────
            // ⚠ M1 — IT GATES THE **EMIT PATH ONLY**. The unread badge, the tray icon and tooltip, and
            //   the chat-list preview are all updated by UpdateChatListForMessage / UpdateTrayTooltip
            //   BEFORE this method is ever called, on a path that does not run through here at all. Master
            //   mute means "do not INTERRUPT me", not "hide my messages" — the same distinction TA-26a
            //   drew for Message.Flags.silent. DO NOT widen this to skip the badge, the tray or the list;
            //   a user who silences notifications still expects to see what arrived when they look.
            //
            // ★ M4 — THE RULE, DECIDED: **MASTER MUTE WINS OVER EVERYTHING, MENTIONS INCLUDED.**
            //   A mention breaks through a PER-CHAT mute because that mute says "not this conversation",
            //   and being addressed by name is the documented exception to it (TA-26c). The master switch
            //   is a different statement — "nothing, from anywhere, right now" — and an exception to it
            //   would make it not a master switch. If the user wants mentions only, that is a quiet-hours
            //   or per-category feature, not a hole in the global off.
            //   This is also the EXISTING behaviour, preserved deliberately: the check sits ABOVE the
            //   MentionsMe call below, so a mention has never broken through it. TA-32 documents and logs
            //   that ordering rather than changing it.
            //
            // ⚠ M3 — AND IT IS LOGGED. It used to say "(user choice, not logged)" and return silently,
            //   which is precisely how "notifications stopped working" becomes an unanswerable bug report:
            //   every OTHER gate in this method records a reason, so a log with no [NOTIFY] line at all
            //   looked like the update never arrived rather than like a switch being off.
            //   Note it fires per invocation, and MaybeToast is invoked up to 7x per message (§2f), so a
            //   muted run is chatty — acceptable, because it is Logger.Enabled-gated and off by default.
            if (!AppSettings.Instance.EnableNotifications)
            { NotifyLog("suppressed", peerId, m.ID, "master"); return; }

            if (outgoing) { NotifyLog("suppressed", peerId, m.ID, "own"); return; }

            // ⚠ BATCH-TA-26a/S1 — THE SENDER ASKED FOR NO PING. `silent` is Telegram's "send without
            //   sound/notification" (WTC: "whether this is a silent message (no notification triggered)"),
            //   set by the sender or by a channel posting silently. We never checked it, so we pinged for
            //   messages explicitly marked not to.
            //   ⚠ THIS SUPPRESSES THE **NOTIFICATION ONLY**. The message is still delivered, still lands in
            //     the chat, and STILL COUNTS TOWARD THE UNREAD BADGE — silent means "don't interrupt me",
            //     not "hide it". Do not "fix" this into skipping the badge or the chat-list preview.
            if ((m.flags & Message.Flags.silent) != 0)
            { NotifyLog("suppressed", peerId, m.ID, "silent"); return; }

            if (IsBacklog(m)) { NotifyLog("suppressed", peerId, m.ID, "backlog"); return; }

            var key = (peerId, m.ID);
            if (_toastSeen.Contains(key)) { NotifyLog("suppressed", peerId, m.ID, "dup"); return; }
            _toastSeen.Add(key);
            _toastSeenOrder.Enqueue(key);
            while (_toastSeenOrder.Count > ToastSeenCap) _toastSeen.Remove(_toastSeenOrder.Dequeue());

            if (_isForeground && _selectedChat != null && _selectedChat.PeerId == peerId)
            { NotifyLog("suppressed", peerId, m.ID, "focused"); return; }

            // MENTION-REACTION: break-through-mute. A message flagged `mentioned` NOTIFIES EVEN in a muted
            // chat (client-side rule; Telegram has no server mention-mute). Unchanged by TA-26.
            // ⚠ WHAT `mentioned` ACTUALLY COVERS — **OBSERVED**, not read off a doc (TA-26b/D3).
            //   *** A REPLY TO YOUR OWN MESSAGE SETS THIS FLAG. *** Measured on a real account, log
            //   telegarm_20260806_124156.log, peer 1824808427 — a chat whose OTHER messages the resolver
            //   reported muted in the same run:
            //       12:43:52.121  [NOTIFY] emit      peer=1824808427 msg=86
            //       12:44:00.479  [NOTIFY] suppressed peer=1824808427 msg=87 reason=muted
            //       12:44:23.202  [NOTIFY] emit      peer=1824808427 msg=88
            //       12:44:27.886  [NOTIFY] suppressed peer=1824808427 msg=89 reason=muted
            //   86 and 88 were REPLIES and they got through; the only route past the mute gate is this
            //   flag, so the server had set it on both.
            //   ⚠ I PREVIOUSLY NARROWED THIS TO "@mentions and text-mentions, replies not documented",
            //     reasoning from WTC's summary and Telegram's mentions page. THAT WAS WRONG, and it is a
            //     standing reminder that the docs describe the entity case without excluding the reply
            //     case — absence of a statement is not a statement of absence. The log is the authority.
            //   ⚠ SEPARATE, STILL UNEXPLAINED: in that same run a message the tester INTENDED as an
            //     @mention did NOT carry the flag (it is one of the two suppressed as `muted`). The gate
            //     behaved correctly — it can only read the flag the server set — so this is a question
            //     about how the mention was composed (does the recipient account actually have that
            //     username?), NOT about this code. Do not "fix" the gate for it.
            string mentionHow;
            bool mentioned = MentionsMe(m, _service != null ? _service.Me : null, out mentionHow);

            // ⚠ BATCH-TA-26/B2 — THE MUTE ANSWER COMES FROM THE SERVICE, NOT FROM `_allChats`.
            // This used to be `_allChats.FirstOrDefault(...)` fed into IsEffectivelyMuted, whose first line
            // was `if (entry == null) return false;  // unknown chat → can't be muted`. `_allChats` holds ONE
            // page of ~100 dialogs until the list is scrolled (TA-11/TA-14), so a muted chat further down was
            // simply NOT FOUND, the gate answered "not muted", and it notified. That is the reported bug.
            // The BACKGROUND path never had it: it already asks svc.IsPeerEffectivelyMuted, which resolves
            // live per-peer override → dialog snapshot → category default and touches no UI list at all.
            // Both paths now share that one resolver, and it FAILS CLOSED (unknown ⇒ silent).
            if (!mentioned)
            {
                var svc = _service;
                if (svc == null) { NotifyLog("suppressed", peerId, m.ID, "no-service"); return; }   // fail closed
                if (svc.IsPeerEffectivelyMuted(m.peer_id))
                {
                    // `reply=1` is diagnostic only (TA-26c): if a REPLY to us is ever suppressed here, the
                    // server did not set the flag for it either and the break-through needs a reply rule
                    // as well as the mention rules. Nothing branches on it.
                    NotifyLog("suppressed", peerId, m.ID, "muted" + (m.reply_to != null ? " reply=1" : ""));
                    return;
                }
            }

            // `_allChats` is still consulted for the TITLE — that is presentation, and being wrong there
            // costs a generic caption, not a wrongly-delivered notification.
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);

            // ⚠ BATCH-TA-26b/D4 — RECORD *WHY* IT NOTIFIED, not just that it did.
            //   The gate logged every SUPPRESSION with a reason but emitted anonymously, so "why did this
            //   notify?" was unanswerable from a log — the exact mirror of the bug that started this whole
            //   thread, where a silent drop had no reason either. `mention` means it broke THROUGH a mute;
            //   `not-muted` means the mute gate simply said no. Those two are indistinguishable in the
            //   output otherwise, and telling them apart is what settles whether `mentioned` is set for
            //   replies (TA-26b/D3 could not answer that from the previous logs).
            NotifyLog("emit", peerId, m.ID, mentioned ? "mention:" + mentionHow : "not-muted");
            EmitNotification(BuildNotification(AccountContext.ActiveId, peerId, m,
                                               entry?.Title ?? "TelegArm", null, mentioned));
        }

        // ── BATCH-TA-27 — ONE CONSTRUCTION, ONE EMITTER, TWO CHANNELS ────────────────────────────
        /// <summary>W6 — composes the notification BOTH channels show. Previously the active and
        /// background paths each built their own caption and each carried their own copy of the
        /// "empty ⇒ 'New message', over 160 ⇒ truncate" rules; the window would have made that three.
        /// The ONLY difference between the two callers is <paramref name="accountName"/>.</summary>
        private NotifyInfo BuildNotification(long acctId, long peerId, Message m,
                                             string chatTitle, string accountName, bool mentioned)
        {
            if (string.IsNullOrEmpty(chatTitle)) chatTitle = "TelegArm";
            if (mentioned) chatTitle = "@ " + chatTitle;          // distinguish a mention
            string text = GetDisplayText(m);
            if (string.IsNullOrEmpty(text)) text = "New message";
            if (text.Length > 160) text = text.Substring(0, 160) + "…";
            return new NotifyInfo
            {
                AccountId = acctId,
                PeerId = peerId,
                MessageId = m.ID,
                Title = accountName != null ? accountName + " · " + chatTitle : chatTitle,
                Text = text,
                // ⚠ CACHE ONLY — GetCachedAvatar is `_avatars.GetCached`, a pure lookup. The notification
                //   path must never fetch: it would make an already-late notification later, and fire
                //   network work from a path that can run while the app is otherwise idle. A miss just
                //   draws the initials circle, which is what the chat list does too.
                Avatar = GetCachedAvatar(peerId),
                AvatarPeerId = peerId
            };
        }

        /// <summary>W7 — deliver it. The window is the channel; the tray balloon is an EXCLUSIVE fallback
        /// behind <see cref="AppSettings.LegacyTrayBalloon"/> (default off) so a window that misbehaves on
        /// the RT device cannot leave notifications dead. Remove the balloon branch once the window is
        /// device-proven.</summary>
        private void EmitNotification(NotifyInfo info)
        {
            if (info == null) return;
            if (AppSettings.Instance.LegacyTrayBalloon)
            {
                if (_notifyIcon == null) return;                  // see CanDeliverNotification
                // ⚠ `_lastNotified*` IS THE BALLOON'S LIMITATION, NOT A SHARED MECHANISM. One tray icon can
                //   show one balloon, so one slot is all it can address. The window path deliberately does
                //   NOT touch these fields — each window carries its own (account, peer, message).
                _lastNotifiedPeerId = info.PeerId; _lastNotifiedAccountId = info.AccountId;
                try { _notifyIcon.ShowBalloonTip(4000, info.Title, info.Text, ToolTipIcon.None); }
                catch { /* tray gone */ }
                return;
            }
            NotificationStack.Show(this, info, _dark, _accent);
        }

        /// <summary>⚠ THE TRAY GUARD **SPLITS**, IT DOES NOT MOVE. Both notify paths used to open with
        /// `if (_notifyIcon == null) return;`, which is right for a balloon and wrong for a window — the
        /// window needs no tray at all, and leaving the guard in place would have made a missing tray
        /// silently disable the new channel. But simply LIFTING it is also wrong: the legacy balloon path
        /// would then dereference a null NotifyIcon. So the requirement becomes "is the channel we are
        /// actually going to use available?", which is this.</summary>
        private bool CanDeliverNotification()
        {
            return !AppSettings.Instance.LegacyTrayBalloon || _notifyIcon != null;
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            // The balloon can only ever be about the most recent notification — hence the shared slot.
            OpenNotifiedChat(_lastNotifiedAccountId, _lastNotifiedPeerId);
        }

        /// <summary>TA-27/W5 — a notification WINDOW was clicked. Same destination as a balloon click, but
        /// the identity comes from the window that was actually clicked rather than from a shared slot, so
        /// clicking the second of three notifications opens the second chat.</summary>
        private void OnNotificationActivated(long acctId, long peerId, int msgId)
        {
            OpenNotifiedChat(acctId, peerId);
        }

        /// <summary>The one implementation behind both click paths: restore the window, switch accounts if
        /// the notification belonged to a BACKGROUND account, then open the chat.</summary>
        private async void OpenNotifiedChat(long acctId, long peerId)
        {
            RestoreFromTray();          // the INTENTIONAL activation — see NotificationWindow's W1 block
            if (peerId == 0) return;
            // NOTIFY-BACKGROUND: a BACKGROUND account's notification → SWITCH to that account first (fast warm
            // rebind), THEN open the chat in the now-active account. An active-account one (acctId == active,
            // or 0) just opens the chat.
            if (acctId != 0 && acctId != AccountContext.ActiveId)
            {
                var acc = AccountStore.ListAccounts().FirstOrDefault(a => a.Id == acctId);
                await SwitchAccountAsync(acctId, acc != null ? acc.Name : null);
            }
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
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
            // TA-27: and no notification windows either — clicking one after a logout would try to open a
            // chat in an account that is gone. ⚠ Deliberately NOT done in SwitchAccountAsync: a window for
            // a still-logged-in account stays VALID across a switch, and OpenNotifiedChat already switches
            // back for it. Touching the switch path for a cosmetic tidy-up is not worth entering that code
            // (CLAUDE.md danger zone / HANDOFF §5.10-5.11).
            try { NotificationStack.CloseAll(); } catch { }

            // ACCOUNT-SESSION-PATH-FIX: a fresh login must NOT inherit a prior account's id, or Config("session_pathname")
            // resolves to accounts/{staleId}/session instead of _pending (the same collision as the add-account bug). Every
            // caller means "no active account → log in fresh" (logout-last, all-corrupt recovery, add-cancel-first,
            // connect-failure fallback), so 0 — the first-launch state — is always correct here.
            _service.AccountId = 0;

            var login = new LoginForm(_service);
            login.FormClosed += (s, e) => { _reallyClosing = true; Close(); };
            Hide();
            login.Show();
        }
    }
}
