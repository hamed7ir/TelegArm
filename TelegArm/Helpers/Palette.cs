using System;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.2 — every colour the UI uses, as named tokens, for one (dark, accent) pair. Immutable;
    /// <see cref="ThemeHelper.Palette"/> holds the current one and rebuilds it BEFORE ThemeChanged fires, so a
    /// paint handler reads a field instead of the registry (plan §2.6: today a bubble's paint reads the accent
    /// and the dark flag from the registry on every paint, for hundreds of bubbles on a Tegra 3).
    ///
    /// TWO GROUPS, ON PURPOSE:
    ///   · CHAT and CHAT LIST tokens are GOLDEN — exactly today's literals and arithmetic (file:line cited), so
    ///     step 1.7 can swap the literals for tokens with a ZERO-pixel diff against the baseline. Do not "tidy"
    ///     them; change them in a later, visible step.
    ///   · CHROME tokens are the new design (DroidFinestra's values), used by the new controls from step 1.3.
    ///     Surface tokens without a DF value start at the nearest current literal and are tuned in step 5.1.
    /// The accent-derived inks (OnAccent, AccentFg) are what makes a user-picked light accent readable (5.3).</summary>
    public sealed class Palette
    {
        public bool Dark { get; private set; }

        // ── brand ────────────────────────────────────────────────────────────────────────────────────────────
        public Color Accent { get; private set; }
        public Color AccentHover { get; private set; }
        public Color AccentPressed { get; private set; }
        /// <summary>Text/glyphs ON an accent fill: white, or near-black on a light accent.</summary>
        public Color OnAccent { get; private set; }
        /// <summary>The accent as ink on <see cref="Surface"/> (links, names): contrast ≥ 4.5.</summary>
        public Color AccentFg { get; private set; }
        /// <summary>An opaque accent wash on <see cref="Surface"/> (selected rows, chips).</summary>
        public Color AccentSoft { get; private set; }
        /// <summary>Step 5.3 — the accent as a GLYPH or outline on the main surface (header and composer icons, focus rings):
        /// nudged to 3:1, the non-text contrast (WCAG 1.4.11). Text uses <see cref="AccentFg"/> (4.5:1).</summary>
        public Color AccentGlyph { get; private set; }
        /// <summary>Step 5.7 — ink drawn ON the outgoing bubble (its own OnColor: the bubble may be a custom colour).</summary>
        public Color OnBubbleOut { get; private set; }
        /// <summary>Step 5.7 — the outgoing-bubble presets after "From accent" (fixed colours, so they live here).</summary>
        public static readonly Color[] OutBubblePresets =
        {
            Color.FromArgb(220, 248, 198),   // mint
            Color.FromArgb(217, 236, 255),   // sky
            Color.FromArgb(233, 223, 255),   // lilac
            Color.FromArgb(255, 228, 209),   // peach
            Color.FromArgb(74, 79, 87),      // graphite
        };
        public static readonly string[] OutBubbleNames = { "Mint", "Sky", "Lilac", "Peach", "Graphite" };
        /// <summary>Step 5.4 — service lines and the date pill over a chat background that is not plain: a ~55% black scrim
        /// with white text (the same in both modes), so they read on any picture or colour.</summary>
        public Color ChatPillScrim { get; private set; }
        public Color ChatPillText { get; private set; }
        public Color TitleBar { get; private set; }

        // ── surfaces (chrome) ────────────────────────────────────────────────────────────────────────────────
        public Color Window { get; private set; }
        public Color Footer { get; private set; }
        public Color Card { get; private set; }
        public Color Surface { get; private set; }
        public Color SurfaceAlt { get; private set; }
        public Color Sidebar { get; private set; }
        public Color Header { get; private set; }
        public Color Composer { get; private set; }
        public Color Divider { get; private set; }
        public Color Hover { get; private set; }
        public Color Selected { get; private set; }

        // ── text (chrome) ────────────────────────────────────────────────────────────────────────────────────
        public Color Text { get; private set; }
        public Color TextDim { get; private set; }
        public Color TextFaint { get; private set; }
        public Color TextDisabled { get; private set; }

        // ── chat (GOLDEN) ────────────────────────────────────────────────────────────────────────────────────
        /// <summary>MessageBubbleControl:1645, VoiceBubbleControl:236, ServiceLineControl:34, SponsoredCardControl:144,
        /// ThemedScrollBar:188, MainForm.Theme:44,49 — the seven sites that must agree.</summary>
        public Color ChatBg { get; private set; }
        /// <summary>A row's background in selection mode: Blend(accent, ChatBg, 0.18), TRUNCATED like the original
        /// (MessageBubbleControl:1646/1774, VoiceBubbleControl:237/311).</summary>
        public Color ChatSelectTint { get; private set; }
        public Color BubbleIn { get; private set; }        // MessageBubbleControl:435
        public Color BubbleInText { get; private set; }    // :436
        public Color BubbleInSub { get; private set; }     // :452
        public Color BubbleInTime { get; private set; }    // :496
        public Color BubbleOut { get; private set; }       // :435 (the accent)
        public Color BubbleOutText { get; private set; }   // :436 (white — NOT OnAccent yet; that is step 5.3)
        public Color BubbleOutSub { get; private set; }    // :451
        public Color BubbleOutTime { get; private set; }   // :495
        public Color SelectionIn { get; private set; }     // :697
        public Color SelectionOut { get; private set; }    // :697
        public Color LinkIn { get; private set; }          // :706
        public Color LinkOut { get; private set; }         // :706
        public Color ServicePillBg { get; private set; }   // ServiceLineControl:46
        public Color ServicePillText { get; private set; } // ServiceLineControl:50
        public Color ScrollThumb { get; private set; }     // ThemedScrollBar:196
        public Color ScrollThumbActive { get; private set; } // ThemedScrollBar:195 (+40 per channel in dark, clamped)

        // ── chat list (GOLDEN, ChatListItemControl) ─────────────────────────────────────────────────────────
        public Color ListBg { get; private set; }          // :85, MainForm.Theme:42,137
        public Color ListName { get; private set; }        // :86
        public Color ListSub { get; private set; }         // :87
        public Color ListSelected { get; private set; }    // :89
        public Color ListDivider { get; private set; }     // :226
        public Color ListDraft { get; private set; }       // :170
        public Color ListMuted { get; private set; }       // :192
        public Color OnlineDot { get; private set; }       // :29

        // ── status ───────────────────────────────────────────────────────────────────────────────────────────
        public Color Danger { get; private set; }
        public Color Success { get; private set; }
        public Color Warning { get; private set; }

        // ── Step 5.1: the colours the literal sweep moved here, valued exactly as the sites used them ──
        public Color AlertRed { get; private set; }   // alert/record red (quiz wrong tick, red dot); differs from Danger (DE4A4A)
        public Color BubbleAlbumCoverPlaceholder { get; private set; }   // album audio row cover placeholder
        public Color BubbleBandSeparator { get; private set; }   // hairline above View-in-chat / Comments bands
        public Color BubbleCardBgIn { get; private set; }   // link preview card bg, incoming
        public Color BubbleCardBgOut { get; private set; }   // link preview card bg, outgoing
        public Color BubbleCardThumbPlaceholder { get; private set; }   // link preview thumb placeholder
        public Color BubbleChipBg { get; private set; }   // reaction chip bg (unchosen)
        public Color BubbleChipFg { get; private set; }   // reaction chip text (unchosen)
        public Color BubbleImagePlaceholder { get; private set; }   // image area placeholder fill
        public Color BubbleInCardDesc { get; private set; }   // link preview description ink, incoming
        public Color BubbleInReplyText { get; private set; }   // reply preview text in incoming bubble
        public Color BubbleInRole { get; private set; }   // admin role label in incoming bubble (same both modes)
        public Color BubbleKbCell { get; private set; }   // inline-keyboard button cell fill
        public Color BubbleKbText { get; private set; }   // inline-keyboard button text
        public Color BubbleOutIconDisc { get; private set; }   // translucent white disc behind audio/voice button in outgoing bubble
        public Color BubbleOutRole { get; private set; }   // admin role label in outgoing bubble
        public Color BubbleOutSoftText { get; private set; }   // reply preview and forward header text in outgoing bubble (same value as LinkOut)
        public Color BubbleOutSubStrong { get; private set; }   // sub text on outgoing file card / link card description (alpha 220; BubbleOutSub is 210)
        public Color BubbleSelectRing { get; private set; }   // unselected selection-mode circle outline
        public Color InlineCodeBgIn { get; private set; }   // inline code chip background in an incoming bubble
        public Color InlineCodeBgOut { get; private set; }   // inline code chip background in an outgoing bubble
        public Color InlinePreBgIn { get; private set; }   // pre block panel in an incoming bubble
        public Color InlinePreBgOut { get; private set; }   // pre block panel in an outgoing bubble
        public Color InlineSpoilerBg { get; private set; }   // spoiler cover fill
        public Color MediaBadgeBg { get; private set; }   // duration badge background over media
        public Color MediaDim { get; private set; }   // dim disc behind video download ring
        public Color MediaPlayDisc { get; private set; }   // video play disc
        public Color MediaPlayDiscSoft { get; private set; }   // soft play disc (link/web preview)
        public Color MediaPlayGlyph { get; private set; }   // play triangle on soft disc
        public Color MediaRingTrack { get; private set; }   // progress ring track on media/outgoing
        public Color MediaScrimClear { get; private set; }   // transparent end of a video scrim gradient
        public Color PollOptionBgIn { get; private set; }   // poll option row bg, incoming
        public Color PollOptionBgOut { get; private set; }   // poll option row bg, outgoing
        public Color SponsoredBody { get; private set; }   // sponsored card body ink
        public Color SponsoredCardBg { get; private set; }   // sponsored card background
        public Color SponsoredMediaLabel { get; private set; }   // sponsored media placeholder label ink
        public Color SponsoredMediaPlaceholder { get; private set; }   // sponsored media placeholder fill
        public Color SponsoredTitle { get; private set; }   // sponsored card title ink
        public Color StoryThumbOutline { get; private set; }   // story thumb 1px outline
        public Color StoryThumbPlaceholder { get; private set; }   // story thumb fill when no image
        public Color StoryThumbScrim { get; private set; }   // opaque end of the story thumb bottom scrim
        public Color DialogPanelBg { get; private set; }   // Dialog/panel backdrop (RoundedButton parentless clear); same value as ThemedListBox, VlcBootstrap, DownloadsPa
        public Color GlyphButtonBg { get; private set; }   // Fallback backdrop of the Attach/Emoji glyph buttons when there is no Parent
        public Color GlyphButtonDisabled { get; private set; }   // Disabled glyph ink of the Attach/Emoji glyph buttons
        public Color InfoLabelText { get; private set; }   // RichInfoLabel foreground text
        public Color JumpButtonBorder { get; private set; }   // Jump-to-bottom button border
        public Color JumpButtonFill { get; private set; }   // Jump-to-bottom button fill
        public Color MediaViewerBg { get; private set; }   // Near-black backdrop behind zoomable images/video (same both modes); same value as VlcVideoControl literals
        public Color PinnedBarBg { get; private set; }   // Pinned-message bar background
        public Color PinnedBarDivider { get; private set; }   // Pinned bar bottom hairline
        public Color PinnedBarIcon { get; private set; }   // Pinned bar icon/indicator pen
        public Color PinnedBarPreview { get; private set; }   // Pinned bar preview/subtitle text
        public Color PlayerBarBg { get; private set; }   // Mini player bar background; identical literal in SelectionBar/VideoControlBar BarColor - reusable
        public Color PlayerBarSub { get; private set; }   // Mini player bar secondary text; identical literal in VideoControlBar
        public Color PlayerBarText { get; private set; }   // Mini player bar primary text; identical literal in SelectionBar/VideoControlBar - reusable
        public Color PlayerBarTrack { get; private set; }   // Mini player bar progress track; identical literal in VideoControlBar
        public Color ProxyDotConnecting { get; private set; }   // Proxy pill status dot, connecting (amber)
        public Color ProxyDotFailed { get; private set; }   // Proxy pill status dot, failed (red)
        public Color ProxyDotIdle { get; private set; }   // Proxy pill status dot, idle/unknown
        public Color ProxyPillBorder { get; private set; }   // Proxy pill border
        public Color ProxyPillFill { get; private set; }   // Proxy pill resting fill
        public Color ProxyPillFillHover { get; private set; }   // Proxy pill hover fill
        public Color ProxyPillFillPressed { get; private set; }   // Proxy pill pressed fill
        public Color ProxyPillText { get; private set; }   // Proxy pill caption text
        public Color RepliesAvatar { get; private set; }   // Avatar disc of the special Replies chat (same both modes)
        public Color ReplyKbBg { get; private set; }   // Bot reply keyboard background
        public Color ReplyKbKey { get; private set; }   // Bot reply keyboard key fill
        public Color ReplyKbKeyText { get; private set; }   // Bot reply keyboard key text
        public Color ReplyKbSub { get; private set; }   // Bot reply keyboard secondary text
        public Color RoundedBtnDisabledFill { get; private set; }   // Disabled RoundedButton fill
        public Color RoundedBtnDisabledText { get; private set; }   // Disabled RoundedButton text
        public Color RoundedBtnNeutralFill { get; private set; }   // Legacy (non-modern) Neutral RoundedButton fill
        public Color RoundedBtnNeutralText { get; private set; }   // Legacy Neutral RoundedButton text
        public Color SendDisabledFill { get; private set; }   // Disabled send button disc
        public Color SendDisabledGlyph { get; private set; }   // Disabled send button paper plane
        public Color AccentSwatchBlue { get; private set; }   // Fixed accent swatch (Telegram blue)
        public Color AccentSwatchGreen { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchMagenta { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchOrange { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchPink { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchRed { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchTeal { get; private set; }   // Fixed accent swatch
        public Color AccentSwatchViolet { get; private set; }   // Fixed accent swatch
        public Color ControlBarBg { get; private set; }   // SelectionBar / VideoControlBar background
        public Color ControlBarDisabled { get; private set; }   // VideoControlBar disabled glyph
        public Color ControlBarSubText { get; private set; }   // VideoControlBar secondary text
        public Color ControlBarText { get; private set; }   // SelectionBar / VideoControlBar text
        public Color ControlBarTrack { get; private set; }   // VideoControlBar seek track
        public Color FooterBarBg { get; private set; }   // ThreadJoinBar / ComposerFooterBar background
        public Color FooterBarGlyph { get; private set; }   // Footer bar dismiss glyph / hint text
        public Color FooterBarLine { get; private set; }   // Footer bar top separator
        public Color FooterBarText { get; private set; }   // ThreadJoinBar message text
        public Color GlyphButtonBar { get; private set; }   // GlyphButton default BarColor
        public Color ListBoxBg { get; private set; }   // ThemedListBox canvas background
        public Color MicButtonBg { get; private set; }   // MicButton fallback background when unparented
        public Color MicGlyphDisabled { get; private set; }   // Disabled mic glyph
        public Color PinnedRowBg { get; private set; }   // Pinned row background
        public Color PinnedRowDivider { get; private set; }   // Pinned row bottom divider
        public Color PinnedRowHover { get; private set; }   // Pinned row hover background
        public Color PinnedRowPreview { get; private set; }   // Pinned row preview text
        public Color PlayerBg { get; private set; }   // Video surface / fallback panel background
        public Color PlayerButtonFace { get; private set; }   // MediaControlButton idle face
        public Color PlayerButtonHover { get; private set; }   // MediaControlButton hover face
        public Color PlayerControlBg { get; private set; }   // MediaControlButton background (matches control bar)
        public Color RecordRed { get; private set; }   // Recording red (mic stop/discard, recording dot)
        public Color RecordingBarBg { get; private set; }   // RecordingBar background
        public Color RecordingBarText { get; private set; }   // RecordingBar caption text
        public Color DialogBg { get; private set; }   // poll dialog background
        public Color DialogField { get; private set; }   // poll dialog input field
        public Color DialogSub { get; private set; }   // poll dialog secondary text
        public Color DialogText { get; private set; }   // poll dialog text
        public Color DownloadBarTrack { get; private set; }   // progress bar track
        public Color DownloadBtnBg { get; private set; }   // downloads header ring button fallback bg
        public Color DownloadBtnGlyph { get; private set; }   // ring button arrow glyph
        public Color DownloadCancelTint { get; private set; }   // destructive glyph tint (replaces static CancelTint)
        public Color DownloadPill { get; private set; }   // neutral pill fill
        public Color DownloadPillHover { get; private set; }   // neutral pill fill, hovered
        public Color DownloadPillText { get; private set; }   // neutral pill text
        public Color DownloadRingTrack { get; private set; }   // ring track
        public Color DownloadRowBg { get; private set; }   // download row bg
        public Color DownloadRowDivider { get; private set; }   // row hairline
        public Color DownloadRowSub { get; private set; }   // download row secondary text
        public Color DownloadRowText { get; private set; }   // download row title
        public Color DownloadsBg { get; private set; }   // downloads popup background
        public Color DownloadsHeaderBg { get; private set; }   // downloads header bg (also pill fallback bg)
        public Color DownloadsHeaderText { get; private set; }   // downloads header text
        public Color EmojiBg { get; private set; }   // emoji picker background
        public Color EmojiDim { get; private set; }   // dim ink (headers, inactive tab glyph)
        public Color EmojiGrid { get; private set; }   // grid bg
        public Color EmojiHover { get; private set; }   // cell hover
        public Color EmojiSearchText { get; private set; }   // search text
        public Color EmojiSectionBar { get; private set; }   // section separator
        public Color EmojiSectionHeader { get; private set; }   // section header text
        public Color EmojiTabBar { get; private set; }   // tab strip bg
        public Color EmojiTabText { get; private set; }   // tab text
        public Color MediaPrepBg { get; private set; }   // VLC setup dialog bg
        public Color MediaPrepSub { get; private set; }   // VLC setup dialog sub text
        public Color MediaPrepTitle { get; private set; }   // VLC setup dialog title
        public Color MenuLegacyBg { get; private set; }   // 1.x (non-restyled) context menu bg
        public Color MenuLegacyDisabled { get; private set; }   // 1.x context menu disabled text
        public Color MenuLegacyText { get; private set; }   // 1.x context menu text
        public Color NotifyBadgeFallback { get; private set; }   // tray badge fill if the Windows accent can't be read
        public Color NotifyBadgeRim { get; private set; }   // tray badge rim
        public Color NotifyBg { get; private set; }   // toast bg
        public Color NotifyBorder { get; private set; }   // toast border
        public Color NotifySub { get; private set; }   // toast secondary text
        public Color NotifyText { get; private set; }   // toast text
        public Color PinnedBg { get; private set; }   // pinned list form bg
        public Color PinnedDivider { get; private set; }   // pinned list divider
        public Color PinnedHeaderBg { get; private set; }   // pinned list header bg
        public Color PinnedSub { get; private set; }   // pinned list header sub text
        public Color PinnedTitle { get; private set; }   // pinned list title
        public Color StoryCaptionLink { get; private set; }   // caption link colour on scrim
        public Color StoryChevronBg { get; private set; }   // nav chevron disc
        public Color StoryLoadingText { get; private set; }   // story 'Loading' text on black
        public Color StoryMutedText { get; private set; }   // story muted text (video fallback, time)
        public Color StoryProgressTrack { get; private set; }   // progress segment track
        public Color StoryScrimBottom { get; private set; }   // caption scrim gradient end
        public Color StoryScrimClear { get; private set; }   // transparent end of scrim gradients
        public Color StoryScrimTop { get; private set; }   // top scrim gradient start
        public Color BotMenuBg { get; private set; }   // bot menu button background
        public Color BotMenuFg { get; private set; }   // bot menu button text
        public Color DockBg { get; private set; }   // dock tab strip background
        public Color DockLine { get; private set; }   // dock separator lines
        public Color DockTabFg { get; private set; }   // inactive dock tab text
        public Color DrawerBg { get; private set; }   // drawer card background
        public Color DrawerFg { get; private set; }   // drawer foreground/ink
        public Color DrawerHairline { get; private set; }   // drawer 1px hairline
        public Color DrawerHeaderFill { get; private set; }   // drawer header-area fill brush at line 449
        public Color DrawerHeaderSub { get; private set; }   // drawer header secondary text
        public Color DrawerHeaderText { get; private set; }   // drawer header name text
        public Color DrawerRowText { get; private set; }   // drawer row label text
        public Color DrawerSwitchOff { get; private set; }   // drawer toggle track when off
        public Color FolderRailBg { get; private set; }   // folder rail / folder button background
        public Color FolderRailFg { get; private set; }   // inactive folder rail item text
        public Color FolderTabFg { get; private set; }   // inactive folder tab text
        public Color HintText { get; private set; }   // hint/empty-state secondary text
        public Color OverlayBg { get; private set; }   // connecting/switch overlay background
        public Color OverlayCardBg { get; private set; }   // connecting panel card background
        public Color OverlayCardBorder { get; private set; }   // connecting panel border
        public Color OverlayDim { get; private set; }   // overlay secondary text
        public Color OverlayText { get; private set; }   // overlay title text
        public Color PanelPlaceholderBg { get; private set; }   // placeholder panel background in chat-open
        public Color PillDefaultAccent { get; private set; }   // DateFlyoutPill.Accent initial default (overwritten live)
        public Color RecordingDot { get; private set; }   // voice recording bar dot (same both modes)
        public Color ReplyCancelFg { get; private set; }   // reply strip cancel button text
        public Color ReplyStripBg { get; private set; }   // composer reply strip background
        public Color SponsorPeerColor0 { get; private set; }   // sponsored PeerColor base 0 (same both modes)
        public Color SponsorPeerColor1 { get; private set; }   // sponsored PeerColor base 1
        public Color SponsorPeerColor2 { get; private set; }   // sponsored PeerColor base 2
        public Color SponsorPeerColor3 { get; private set; }   // sponsored PeerColor base 3
        public Color SponsorPeerColor4 { get; private set; }   // sponsored PeerColor base 4
        public Color SponsorPeerColor5 { get; private set; }   // sponsored PeerColor base 5
        public Color SponsorPeerColor6 { get; private set; }   // sponsored PeerColor base 6
        public Color StoryName { get; private set; }   // story tray name text
        public Color StoryRingIdle { get; private set; }   // story tray ring when no unseen story
        public Color ToastBg { get; private set; }   // transient toast window background

        private Palette() { }

        /// <summary>Windows' own default accent blue: the fallback when the registry has none (ThemeHelper reads it before
        /// any palette exists, so it is a constant, not a token). Step 5.1 moved it here from ThemeHelper.</summary>
        public static readonly Color WindowsDefaultAccent = Color.FromArgb(0, 120, 215);

        private static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        private static Color Argb(int a, int r, int g, int b) { return Color.FromArgb(a, r, g, b); }
        private static Color Pick(bool dark, Color d, Color l) { return dark ? d : l; }

        public static Palette Build(bool dark, Color accent) { return Build(dark, accent, null); }

        /// <summary>Step 5.7 — as <see cref="Build(bool, Color, Color?)"/>, with the user's outgoing-bubble colour (null = the
        /// accent, as before).</summary>
        public static Palette Build(bool dark, Color accent, Color? baseColor, Color? outBubble) { return BuildCore(dark, accent, baseColor, outBubble); }

        private Color? _outBubble;

        /// <summary>Step 5.2 (owner's idea, 2026-10-15) — the BASE colour of a mode: the paper of the light theme (default
        /// white; warm white, sepia, cool grey...) or the ground of the dark theme (default graphite (30,30,30); true black,
        /// blue-grey...). Null or the default = today's palette, pixel for pixel.</summary>
        public static readonly Color DefaultLightBase = Color.FromArgb(255, 255, 255);
        public static readonly Color DefaultDarkBase = Color.FromArgb(30, 30, 30);

        /// <summary>The base presets Settings offers after "Default" (fixed colours, so they live here).</summary>
        public static readonly Color[] LightBasePresets =
        {
            Color.FromArgb(255, 247, 232),   // warm white
            Color.FromArgb(244, 236, 216),   // sepia
            Color.FromArgb(237, 241, 246),   // cool grey
            Color.FromArgb(238, 246, 240),   // mint
        };
        public static readonly string[] LightBaseNames = { "Warm white", "Sepia", "Cool grey", "Mint" };
        public static readonly Color[] DarkBasePresets =
        {
            Color.FromArgb(0, 0, 0),         // true black (OLED)
            Color.FromArgb(22, 27, 34),      // blue-grey
            Color.FromArgb(16, 20, 32),      // midnight
            Color.FromArgb(32, 28, 25),      // warm dark
        };
        public static readonly string[] DarkBaseNames = { "True black", "Blue-grey", "Midnight", "Warm dark" };

        /// <summary>Step 5.4 — a built-in chat background (own art, never Telegram's): a solid, a soft left→right gradient (so it
        /// is scroll-invariant: cheap on the Surface) or a pattern over a colour, with a light and a dark version.</summary>
        public sealed class ChatPreset
        {
            public string Name, Kind, Pattern;   // Kind: "solid" | "gradient" | "pattern"
            public Color Light1, Light2, Dark1, Dark2;
        }

        public static readonly ChatPreset[] ChatPresets =
        {
            new ChatPreset { Name = "Mist", Kind = "solid", Light1 = Color.FromArgb(232, 237, 242), Dark1 = Color.FromArgb(27, 32, 38) },
            new ChatPreset { Name = "Sand", Kind = "solid", Light1 = Color.FromArgb(239, 232, 220), Dark1 = Color.FromArgb(34, 31, 26) },
            new ChatPreset { Name = "Sky", Kind = "gradient", Light1 = Color.FromArgb(220, 235, 250), Light2 = Color.FromArgb(240, 246, 253),
                             Dark1 = Color.FromArgb(20, 32, 46), Dark2 = Color.FromArgb(28, 39, 51) },
            new ChatPreset { Name = "Mint", Kind = "gradient", Light1 = Color.FromArgb(223, 243, 234), Light2 = Color.FromArgb(242, 250, 246),
                             Dark1 = Color.FromArgb(19, 36, 29), Dark2 = Color.FromArgb(26, 43, 36) },
            new ChatPreset { Name = "Peach", Kind = "gradient", Light1 = Color.FromArgb(251, 227, 214), Light2 = Color.FromArgb(253, 243, 236),
                             Dark1 = Color.FromArgb(42, 30, 25), Dark2 = Color.FromArgb(31, 26, 23) },
            new ChatPreset { Name = "Lavender", Kind = "gradient", Light1 = Color.FromArgb(230, 225, 247), Light2 = Color.FromArgb(244, 241, 252),
                             Dark1 = Color.FromArgb(31, 27, 46), Dark2 = Color.FromArgb(38, 34, 54) },
            new ChatPreset { Name = "Dots", Kind = "pattern", Pattern = "dots", Light1 = Color.FromArgb(236, 239, 243), Dark1 = Color.FromArgb(28, 31, 36) },
            new ChatPreset { Name = "Grid", Kind = "pattern", Pattern = "grid", Light1 = Color.FromArgb(238, 240, 234), Dark1 = Color.FromArgb(30, 31, 27) },
        };

        /// <summary>The range a base may take, so a light theme stays light and a dark one dark (text keeps its contrast):
        /// every channel of a light base is at least 200, of a dark base at most 64.</summary>
        public const int LightBaseMin = 200, DarkBaseMax = 64;

        /// <summary>A base clamped into its mode's range (see <see cref="LightBaseMin"/>).</summary>
        public static Color ClampBase(Color c, bool dark)
        {
            return dark ? Color.FromArgb(Math.Min((int)c.R, DarkBaseMax), Math.Min((int)c.G, DarkBaseMax), Math.Min((int)c.B, DarkBaseMax))
                        : Color.FromArgb(Math.Max((int)c.R, LightBaseMin), Math.Max((int)c.G, LightBaseMin), Math.Max((int)c.B, LightBaseMin));
        }

        /// <summary>The palette of a mode for an accent and a base (null = the default base).</summary>
        public static Palette Build(bool dark, Color accent, Color? baseColor) { return BuildCore(dark, accent, baseColor, null); }

        // The one build: the out-bubble colour must be known BEFORE DeriveAccentInk, which re-inks the white out-bubble
        // tokens exactly once (a second pass would find them no longer white).
        private static Palette BuildCore(bool dark, Color accent, Color? baseColor, Color? outBubble)
        {
            accent = Color.FromArgb(accent.R, accent.G, accent.B);   // opaque, whatever arrived
            var p = new Palette { Dark = dark, Accent = accent };

            // chat + chat list: golden
            p.ChatBg = Pick(dark, Rgb(30, 30, 30), Rgb(245, 245, 245));
            p.ChatSelectTint = Color.FromArgb(
                (int)(accent.R * 0.18f + p.ChatBg.R * (1 - 0.18f)),
                (int)(accent.G * 0.18f + p.ChatBg.G * (1 - 0.18f)),
                (int)(accent.B * 0.18f + p.ChatBg.B * (1 - 0.18f)));
            p.BubbleIn = Pick(dark, Rgb(60, 60, 63), Rgb(226, 226, 226));
            p.BubbleInText = Pick(dark, Rgb(235, 235, 235), Rgb(20, 20, 20));
            p.BubbleInSub = Pick(dark, Rgb(160, 160, 160), Rgb(110, 110, 110));
            p.BubbleInTime = Pick(dark, Rgb(160, 160, 160), Rgb(120, 120, 120));
            p.BubbleOut = accent;
            p.BubbleOutText = Color.White;
            p.BubbleOutSub = Color.FromArgb(210, 255, 255, 255);
            p.BubbleOutTime = Color.FromArgb(220, 255, 255, 255);
            p.SelectionIn = Color.FromArgb(110, accent);
            p.SelectionOut = Color.FromArgb(95, 255, 255, 255);
            p.LinkIn = accent;
            p.LinkOut = Color.FromArgb(235, 255, 255, 255);
            p.ServicePillBg = Pick(dark, Color.FromArgb(70, 0, 0, 0), Color.FromArgb(26, 0, 0, 0));
            p.ServicePillText = Pick(dark, Rgb(190, 190, 196), Rgb(105, 105, 110));
            p.ScrollThumb = Pick(dark, Rgb(95, 95, 100), Rgb(190, 190, 196));
            p.ScrollThumbActive = dark ? Rgb(Math.Min(255, accent.R + 40), Math.Min(255, accent.G + 40), Math.Min(255, accent.B + 40)) : accent;
            p.ListBg = Pick(dark, Rgb(40, 40, 40), Color.White);
            p.ListName = Pick(dark, Color.White, Rgb(30, 30, 30));
            p.ListSub = Pick(dark, Rgb(150, 150, 150), Rgb(120, 120, 120));
            p.ListSelected = Pick(dark, Rgb(55, 55, 58), Rgb(225, 235, 245));
            p.ListDivider = Pick(dark, Rgb(55, 55, 55), Rgb(235, 235, 235));
            p.ListDraft = Pick(dark, Rgb(240, 116, 116), Rgb(214, 69, 69));
            p.ListMuted = Pick(dark, Rgb(120, 120, 124), Rgb(178, 178, 184));
            p.OnlineDot = Rgb(76, 204, 90);

            // chrome: DroidFinestra's values where it has one; else the nearest current literal (tuned in 5.1)
            p.Window = Pick(dark, Rgb(32, 32, 36), Rgb(245, 245, 248));
            p.Footer = Pick(dark, Rgb(38, 38, 43), Rgb(236, 236, 240));
            p.Card = Pick(dark, Rgb(44, 44, 50), Color.White);
            p.Divider = Pick(dark, Rgb(58, 58, 64), Rgb(220, 220, 226));
            p.Text = Pick(dark, Rgb(236, 236, 240), Rgb(28, 28, 32));
            p.TextDim = Pick(dark, Rgb(150, 150, 156), Rgb(110, 110, 118));
            p.TextFaint = p.ListMuted;
            p.TextDisabled = Pick(dark, Rgb(100, 100, 106), Rgb(176, 176, 182));
            p.Surface = p.ListBg;          // the chat list: the largest chrome surface today
            p.SurfaceAlt = p.ChatBg;
            p.Sidebar = p.ListBg;
            p.Header = p.ListBg;
            p.Composer = p.ListBg;
            p.Selected = p.ListSelected;
            p.Hover = Color.FromArgb(40, accent);   // MainForm.Theme:97 (header buttons' hover)
            p.Danger = Rgb(222, 74, 74);
            p.Success = Rgb(76, 175, 80);           // the quiz "correct" green, MessageBubbleControl:1351
            p.Warning = Rgb(232, 163, 23);

            // brand
            p.TitleBar = accent;
            p.AccentHover = ColorMath.Mix(accent, dark ? Color.White : Color.Black, 0.10f);
            p.AccentPressed = ColorMath.Mix(accent, dark ? Color.White : Color.Black, 0.20f);
            p.OnAccent = ColorMath.OnColor(accent);
            p.AccentFg = ColorMath.Ink(accent, p.Surface);
            p.AccentSoft = ColorMath.Mix(p.Surface, accent, 0.18f);

            // Step 5.1 (the literal sweep): exactly the values the sites used
            p.AlertRed = Rgb(229, 57, 53);
            p.BubbleAlbumCoverPlaceholder = Pick(dark, Rgb(70, 70, 74), Rgb(208, 208, 212));
            p.BubbleBandSeparator = Pick(dark, Argb(46, 255, 255, 255), Argb(30, 0, 0, 0));
            p.BubbleCardBgIn = Pick(dark, Argb(40, 255, 255, 255), Argb(20, 0, 0, 0));
            p.BubbleCardBgOut = Argb(36, 255, 255, 255);
            p.BubbleCardThumbPlaceholder = Pick(dark, Rgb(64, 64, 68), Rgb(208, 208, 212));
            p.BubbleChipBg = Pick(dark, Rgb(55, 55, 58), Rgb(224, 224, 230));
            p.BubbleChipFg = Pick(dark, Rgb(220, 220, 225), Rgb(55, 55, 60));
            p.BubbleImagePlaceholder = Pick(dark, Rgb(45, 45, 48), Rgb(200, 200, 200));
            p.BubbleInCardDesc = Pick(dark, Rgb(180, 180, 185), Rgb(90, 90, 95));
            p.BubbleInReplyText = Pick(dark, Rgb(195, 195, 195), Rgb(95, 95, 95));
            p.BubbleInRole = Rgb(140, 140, 146);
            p.BubbleKbCell = Pick(dark, Rgb(58, 58, 62), Rgb(210, 224, 240));
            p.BubbleKbText = Pick(dark, Rgb(225, 230, 235), Rgb(20, 40, 70));
            p.BubbleOutIconDisc = Argb(70, 255, 255, 255);
            p.BubbleOutRole = Argb(200, 255, 255, 255);
            p.BubbleOutSoftText = Argb(235, 255, 255, 255);
            p.BubbleOutSubStrong = Argb(220, 255, 255, 255);
            p.BubbleSelectRing = Pick(dark, Rgb(120, 120, 120), Rgb(170, 170, 170));
            p.InlineCodeBgIn = Pick(dark, Argb(60, 255, 255, 255), Argb(26, 0, 0, 0));
            p.InlineCodeBgOut = Argb(46, 255, 255, 255);
            p.InlinePreBgIn = Pick(dark, Argb(90, 0, 0, 0), Argb(22, 0, 0, 0));
            p.InlinePreBgOut = Argb(55, 0, 0, 0);
            p.InlineSpoilerBg = Pick(dark, Rgb(150, 150, 156), Rgb(168, 168, 176));
            p.MediaBadgeBg = Argb(150, 0, 0, 0);
            p.MediaDim = Argb(90, 0, 0, 0);
            p.MediaPlayDisc = Argb(140, 0, 0, 0);
            p.MediaPlayDiscSoft = Argb(115, 0, 0, 0);
            p.MediaPlayGlyph = Argb(235, 255, 255, 255);
            p.MediaRingTrack = Argb(120, 255, 255, 255);
            p.MediaScrimClear = Argb(0, 0, 0, 0);
            p.PollOptionBgIn = Pick(dark, Rgb(74, 74, 78), Rgb(236, 236, 238));
            p.PollOptionBgOut = Argb(40, 255, 255, 255);
            p.SponsoredBody = Pick(dark, Rgb(220, 220, 224), Rgb(40, 40, 44));
            p.SponsoredCardBg = Pick(dark, Rgb(48, 48, 52), Rgb(255, 255, 255));
            p.SponsoredMediaLabel = Pick(dark, Rgb(170, 170, 175), Rgb(120, 120, 125));
            p.SponsoredMediaPlaceholder = Pick(dark, Rgb(60, 60, 64), Rgb(225, 225, 230));
            p.SponsoredTitle = Pick(dark, Rgb(236, 236, 238), Rgb(20, 20, 22));
            p.StoryThumbOutline = Pick(dark, Rgb(70, 70, 74), Rgb(206, 206, 210));
            p.StoryThumbPlaceholder = Pick(dark, Rgb(58, 58, 62), Rgb(220, 220, 224));
            p.StoryThumbScrim = Argb(160, 0, 0, 0);
            p.DialogPanelBg = Pick(dark, Rgb(40, 40, 44), Rgb(248, 248, 250));
            p.GlyphButtonBg = Pick(dark, Rgb(50, 50, 50), Rgb(250, 250, 250));
            p.GlyphButtonDisabled = Pick(dark, Rgb(90, 90, 94), Rgb(190, 190, 196));
            p.InfoLabelText = Pick(dark, Rgb(225, 225, 228), Rgb(30, 30, 32));
            p.JumpButtonBorder = Pick(dark, Rgb(82, 82, 86), Rgb(218, 218, 222));
            p.JumpButtonFill = Pick(dark, Rgb(54, 54, 58), Rgb(255, 255, 255));
            p.MediaViewerBg = Rgb(18, 18, 18);
            p.PinnedBarBg = Pick(dark, Rgb(45, 45, 48), Rgb(248, 248, 250));
            p.PinnedBarDivider = Pick(dark, Rgb(60, 60, 63), Rgb(230, 230, 233));
            p.PinnedBarIcon = Pick(dark, Rgb(170, 170, 175), Rgb(110, 110, 115));
            p.PinnedBarPreview = Pick(dark, Rgb(180, 180, 185), Rgb(90, 90, 95));
            p.PlayerBarBg = Pick(dark, Rgb(34, 34, 36), Rgb(236, 236, 238));
            p.PlayerBarSub = Pick(dark, Rgb(200, 200, 200), Rgb(95, 95, 95));
            p.PlayerBarText = Pick(dark, Rgb(255, 255, 255), Rgb(30, 30, 30));
            p.PlayerBarTrack = Pick(dark, Rgb(70, 70, 74), Rgb(198, 198, 203));
            p.ProxyDotConnecting = Rgb(230, 170, 60);
            p.ProxyDotFailed = Rgb(214, 78, 78);
            p.ProxyDotIdle = Pick(dark, Rgb(130, 130, 136), Rgb(150, 150, 156));
            p.ProxyPillBorder = Pick(dark, Rgb(92, 92, 98), Rgb(186, 186, 194));
            p.ProxyPillFill = Pick(dark, Rgb(58, 58, 63), Rgb(255, 255, 255));
            p.ProxyPillFillHover = Pick(dark, Rgb(66, 66, 71), Rgb(247, 247, 250));
            p.ProxyPillFillPressed = Pick(dark, Rgb(46, 46, 50), Rgb(236, 236, 240));
            p.ProxyPillText = Pick(dark, Rgb(232, 232, 236), Rgb(38, 38, 42));
            p.RepliesAvatar = Rgb(80, 120, 190);
            p.ReplyKbBg = Pick(dark, Rgb(28, 28, 30), Rgb(236, 236, 238));
            p.ReplyKbKey = Pick(dark, Rgb(58, 58, 62), Rgb(255, 255, 255));
            p.ReplyKbKeyText = Pick(dark, Rgb(228, 228, 230), Rgb(25, 25, 28));
            p.ReplyKbSub = Pick(dark, Rgb(160, 160, 160), Rgb(110, 110, 110));
            p.RoundedBtnDisabledFill = Pick(dark, Rgb(55, 55, 58), Rgb(226, 226, 229));
            p.RoundedBtnDisabledText = Pick(dark, Rgb(120, 120, 124), Rgb(150, 150, 154));
            p.RoundedBtnNeutralFill = Pick(dark, Rgb(60, 60, 64), Rgb(234, 234, 238));
            p.RoundedBtnNeutralText = Pick(dark, Rgb(230, 230, 234), Rgb(35, 35, 40));
            p.SendDisabledFill = Pick(dark, Rgb(58, 58, 63), Rgb(214, 214, 220));
            p.SendDisabledGlyph = Pick(dark, Rgb(96, 96, 102), Rgb(168, 168, 176));
            p.AccentSwatchBlue = Rgb(64, 167, 227);
            p.AccentSwatchGreen = Rgb(63, 174, 90);
            p.AccentSwatchMagenta = Rgb(194, 57, 179);
            p.AccentSwatchOrange = Rgb(232, 147, 43);
            p.AccentSwatchPink = Rgb(208, 79, 138);
            p.AccentSwatchRed = Rgb(229, 83, 75);
            p.AccentSwatchTeal = Rgb(24, 163, 156);
            p.AccentSwatchViolet = Rgb(142, 89, 217);
            p.ControlBarBg = Pick(dark, Rgb(34, 34, 36), Rgb(236, 236, 238));
            p.ControlBarDisabled = Pick(dark, Rgb(70, 70, 74), Rgb(200, 200, 205));
            p.ControlBarSubText = Pick(dark, Rgb(200, 200, 200), Rgb(95, 95, 95));
            p.ControlBarText = Pick(dark, Rgb(255, 255, 255), Rgb(30, 30, 30));
            p.ControlBarTrack = Pick(dark, Rgb(70, 70, 74), Rgb(198, 198, 203));
            p.FooterBarBg = Pick(dark, Rgb(38, 38, 41), Rgb(247, 247, 249));
            p.FooterBarGlyph = Pick(dark, Rgb(150, 150, 156), Rgb(120, 120, 126));
            p.FooterBarLine = Pick(dark, Rgb(60, 60, 63), Rgb(228, 228, 232));
            p.FooterBarText = Pick(dark, Rgb(205, 205, 210), Rgb(70, 70, 76));
            p.GlyphButtonBar = Rgb(0, 120, 215);
            p.ListBoxBg = Pick(dark, Rgb(40, 40, 44), Rgb(248, 248, 250));
            p.MicButtonBg = Pick(dark, Rgb(50, 50, 50), Rgb(250, 250, 250));
            p.MicGlyphDisabled = Pick(dark, Rgb(90, 90, 94), Rgb(190, 190, 196));
            p.PinnedRowBg = Pick(dark, Rgb(36, 36, 39), Rgb(255, 255, 255));
            p.PinnedRowDivider = Pick(dark, Rgb(52, 52, 56), Rgb(232, 232, 236));
            p.PinnedRowHover = Pick(dark, Rgb(50, 50, 54), Rgb(238, 240, 244));
            p.PinnedRowPreview = Pick(dark, Rgb(190, 190, 195), Rgb(80, 80, 85));
            p.PlayerBg = Rgb(18, 18, 18);
            p.PlayerButtonFace = Rgb(45, 45, 48);
            p.PlayerButtonHover = Rgb(80, 80, 80);
            p.PlayerControlBg = Rgb(28, 28, 28);
            p.RecordRed = Rgb(229, 57, 53);
            p.RecordingBarBg = Pick(dark, Rgb(48, 48, 48), Rgb(250, 250, 250));
            p.RecordingBarText = Pick(dark, Rgb(225, 225, 225), Rgb(40, 40, 40));
            p.DialogBg = Pick(dark, Rgb(36, 36, 38), Rgb(245, 245, 247));
            p.DialogField = Pick(dark, Rgb(54, 54, 58), Rgb(255, 255, 255));
            p.DialogSub = Pick(dark, Rgb(150, 150, 150), Rgb(120, 120, 120));
            p.DialogText = Pick(dark, Rgb(235, 235, 235), Rgb(20, 20, 20));
            p.DownloadBarTrack = Pick(dark, Rgb(70, 70, 76), Rgb(215, 215, 220));
            p.DownloadBtnBg = Pick(dark, Rgb(38, 38, 42), Rgb(255, 255, 255));
            p.DownloadBtnGlyph = Pick(dark, Rgb(225, 225, 230), Rgb(60, 60, 66));
            p.DownloadCancelTint = Rgb(228, 100, 100);
            p.DownloadPill = Pick(dark, Rgb(64, 64, 70), Rgb(228, 228, 234));
            p.DownloadPillHover = Pick(dark, Rgb(78, 78, 84), Rgb(214, 214, 220));
            p.DownloadPillText = Pick(dark, Rgb(228, 228, 233), Rgb(45, 45, 50));
            p.DownloadRingTrack = Pick(dark, Rgb(80, 80, 86), Rgb(205, 205, 210));
            p.DownloadRowBg = Pick(dark, Rgb(48, 48, 52), Rgb(255, 255, 255));
            p.DownloadRowDivider = Pick(dark, Rgb(60, 60, 66), Rgb(228, 228, 233));
            p.DownloadRowSub = Pick(dark, Rgb(150, 150, 156), Rgb(120, 120, 126));
            p.DownloadRowText = Pick(dark, Rgb(228, 228, 233), Rgb(40, 40, 45));
            p.DownloadsBg = Pick(dark, Rgb(40, 40, 43), Rgb(248, 248, 250));
            p.DownloadsHeaderBg = Pick(dark, Rgb(50, 50, 54), Rgb(236, 236, 240));
            p.DownloadsHeaderText = Pick(dark, Rgb(230, 230, 235), Rgb(40, 40, 45));
            p.EmojiBg = Pick(dark, Rgb(40, 40, 43), Rgb(245, 245, 247));
            p.EmojiDim = Pick(dark, Rgb(170, 170, 175), Rgb(110, 110, 115));
            p.EmojiGrid = Pick(dark, Rgb(30, 30, 33), Rgb(255, 255, 255));
            p.EmojiHover = Pick(dark, Rgb(58, 58, 62), Rgb(228, 228, 233));
            p.EmojiSearchText = Pick(dark, Rgb(210, 210, 215), Rgb(60, 60, 65));
            p.EmojiSectionBar = Pick(dark, Rgb(60, 60, 64), Rgb(225, 225, 230));
            p.EmojiSectionHeader = Pick(dark, Rgb(170, 170, 175), Rgb(120, 120, 125));
            p.EmojiTabBar = Pick(dark, Rgb(34, 34, 37), Rgb(236, 236, 240));
            p.EmojiTabText = Pick(dark, Rgb(200, 200, 205), Rgb(70, 70, 75));
            p.MediaPrepBg = Pick(dark, Rgb(40, 40, 44), Rgb(248, 248, 250));
            p.MediaPrepSub = Pick(dark, Rgb(160, 160, 160), Rgb(110, 110, 110));
            p.MediaPrepTitle = Pick(dark, Rgb(255, 255, 255), Rgb(20, 20, 20));
            p.MenuLegacyBg = Pick(dark, Rgb(43, 43, 46), Rgb(245, 245, 247));
            p.MenuLegacyDisabled = Pick(dark, Rgb(120, 120, 120), Rgb(165, 165, 165));
            p.MenuLegacyText = Pick(dark, Rgb(235, 235, 235), Rgb(25, 25, 25));
            p.NotifyBadgeFallback = Rgb(0, 120, 212);
            p.NotifyBadgeRim = Argb(235, 255, 255, 255);
            p.NotifyBg = Pick(dark, Rgb(34, 34, 37), Rgb(250, 250, 252));
            p.NotifyBorder = Pick(dark, Rgb(64, 64, 70), Rgb(214, 214, 222));
            p.NotifySub = Pick(dark, Rgb(158, 158, 166), Rgb(112, 112, 120));
            p.NotifyText = Pick(dark, Rgb(234, 234, 238), Rgb(28, 28, 32));
            p.PinnedBg = Pick(dark, Rgb(30, 30, 33), Rgb(248, 248, 250));
            p.PinnedDivider = Pick(dark, Rgb(70, 70, 74), Rgb(210, 210, 214));
            p.PinnedHeaderBg = Pick(dark, Rgb(40, 40, 44), Rgb(238, 238, 242));
            p.PinnedSub = Pick(dark, Rgb(210, 210, 215), Rgb(70, 70, 75));
            p.PinnedTitle = Pick(dark, Rgb(225, 225, 230), Rgb(35, 35, 40));
            p.StoryCaptionLink = Rgb(130, 190, 255);
            p.StoryChevronBg = Argb(70, 0, 0, 0);
            p.StoryLoadingText = Rgb(180, 180, 180);
            p.StoryMutedText = Rgb(200, 200, 200);
            p.StoryProgressTrack = Argb(90, 255, 255, 255);
            p.StoryScrimBottom = Argb(180, 0, 0, 0);
            p.StoryScrimClear = Argb(0, 0, 0, 0);
            p.StoryScrimTop = Argb(150, 0, 0, 0);
            p.BotMenuBg = Pick(dark, Rgb(54, 54, 58), Rgb(225, 225, 228));
            p.BotMenuFg = Pick(dark, Rgb(225, 225, 228), Rgb(40, 40, 44));
            p.DockBg = Pick(dark, Rgb(34, 34, 37), Rgb(246, 246, 248));
            p.DockLine = Pick(dark, Rgb(58, 58, 62), Rgb(222, 222, 228));
            p.DockTabFg = Pick(dark, Rgb(170, 170, 176), Rgb(105, 105, 112));
            p.DrawerBg = Pick(dark, Rgb(36, 36, 39), Rgb(250, 250, 252));
            p.DrawerFg = Pick(dark, Rgb(220, 220, 220), Rgb(40, 40, 40));
            p.DrawerHairline = Pick(dark, Rgb(54, 54, 58), Rgb(232, 232, 236));
            p.DrawerHeaderFill = Pick(dark, Rgb(48, 48, 52), Rgb(237, 240, 244));
            p.DrawerHeaderSub = Pick(dark, Rgb(150, 150, 155), Rgb(135, 135, 140));
            p.DrawerHeaderText = Pick(dark, Rgb(255, 255, 255), Rgb(25, 25, 28));
            p.DrawerRowText = Pick(dark, Rgb(228, 228, 232), Rgb(35, 35, 38));
            p.DrawerSwitchOff = Pick(dark, Rgb(80, 80, 86), Rgb(200, 200, 206));
            p.FolderRailBg = Pick(dark, Rgb(34, 34, 36), Rgb(244, 244, 246));
            p.FolderRailFg = Pick(dark, Rgb(205, 205, 210), Rgb(90, 90, 95));
            p.FolderTabFg = Pick(dark, Rgb(170, 170, 175), Rgb(110, 110, 115));
            p.HintText = Pick(dark, Rgb(150, 150, 155), Rgb(120, 120, 125));
            p.OverlayBg = Pick(dark, Rgb(26, 26, 29), Rgb(244, 244, 247));
            p.OverlayCardBg = Pick(dark, Rgb(44, 44, 48), Rgb(250, 250, 252));
            p.OverlayCardBorder = Pick(dark, Rgb(72, 72, 78), Rgb(214, 214, 220));
            p.OverlayDim = Pick(dark, Rgb(170, 170, 176), Rgb(108, 108, 114));
            p.OverlayText = Pick(dark, Rgb(236, 236, 240), Rgb(33, 33, 38));
            p.PanelPlaceholderBg = Pick(dark, Rgb(48, 48, 52), Rgb(235, 238, 242));
            p.PillDefaultAccent = Rgb(83, 74, 183);
            p.RecordingDot = Rgb(229, 57, 53);
            p.ReplyCancelFg = Pick(dark, Rgb(210, 210, 210), Rgb(90, 90, 90));
            p.ReplyStripBg = Pick(dark, Rgb(45, 45, 48), Rgb(232, 232, 235));
            p.SponsorPeerColor0 = Rgb(229, 57, 53);
            p.SponsorPeerColor1 = Rgb(245, 124, 0);
            p.SponsorPeerColor2 = Rgb(142, 36, 170);
            p.SponsorPeerColor3 = Rgb(67, 160, 71);
            p.SponsorPeerColor4 = Rgb(0, 172, 193);
            p.SponsorPeerColor5 = Rgb(30, 136, 229);
            p.SponsorPeerColor6 = Rgb(216, 27, 96);
            p.StoryName = Pick(dark, Rgb(210, 210, 215), Rgb(50, 50, 55));
            p.StoryRingIdle = Pick(dark, Rgb(95, 95, 100), Rgb(200, 200, 205));
            p.ToastBg = Pick(dark, Rgb(52, 52, 56), Rgb(55, 55, 58));
            if (baseColor.HasValue)
            {
                var b = ClampBase(baseColor.Value, dark);
                if (b.ToArgb() != (dark ? DefaultDarkBase : DefaultLightBase).ToArgb()) p.ApplyBase(b, dark);
            }
            p.Base = baseColor.HasValue ? ClampBase(baseColor.Value, dark) : dark ? DefaultDarkBase : DefaultLightBase;
            p._outBubble = outBubble;
            p.ChatPillScrim = Color.FromArgb(140, 0, 0, 0);
            p.ChatPillText = Color.White;
            p.DeriveAccentInk();
            return p;
        }

        // Step 5.3: the out-bubble inks drawn ON the accent. Their colour is white today; with a light accent (yellow, white,
        // light green) OnAccent is near-black, and they follow it with the same alpha.
        private static readonly string[] OnAccentInks =
        {
            "BubbleOutText", "BubbleOutSub", "BubbleOutTime", "SelectionOut", "LinkOut", "BubbleOutSoftText", "BubbleOutRole",
            "BubbleOutSubStrong", "BubbleOutIconDisc", "InlineCodeBgOut", "PollOptionBgOut", "BubbleCardBgOut",
        };

        /// <summary>Step 5.3 — after the base: ink ON the accent follows OnAccent; ink OF the accent reaches its contrast on
        /// the surface it is drawn on (links in incoming bubbles: 4.5:1 on BubbleIn; glyphs: 3:1 on Surface).</summary>
        private void DeriveAccentInk()
        {
            var accent = Accent;
            LinkIn = ColorMath.Ink(accent, BubbleIn);
            AccentGlyph = ColorMath.Ink(accent, Surface, 3.0);
            // Step 5.7: the outgoing bubble — the user's colour (softened 25% toward the chat in dark mode) or the accent; either
            // way kept apart from the chat background (>= 1.2:1, so a white accent's bubbles do not vanish on a light chat).
            var bubble = _outBubble.HasValue ? (Dark ? ColorMath.Mix(_outBubble.Value, ChatBg, 0.25f) : _outBubble.Value) : accent;
            var away = ColorMath.Luminance(ChatBg) > 0.5 ? Color.Black : Color.White;
            for (int i = 1; i <= 10 && ColorMath.Contrast(bubble, ChatBg) < 1.2; i++) bubble = ColorMath.Mix(bubble, away, 0.04f);
            BubbleOut = bubble;
            OnBubbleOut = ColorMath.OnColor(bubble);
            var on = OnBubbleOut;
            if (on.R == 255 && on.G == 255 && on.B == 255) return;   // white ink: the tokens already are
            foreach (var name in OnAccentInks)
            {
                var prop = typeof(Palette).GetProperty(name);
                if (prop == null) continue;
                var c = (Color)prop.GetValue(this, null);
                if (c.R == 255 && c.G == 255 && c.B == 255) prop.SetValue(this, Color.FromArgb(c.A, on.R, on.G, on.B), null);
            }
        }

        /// <summary>The base this palette was built on (the default base when none was given).</summary>
        public Color Base { get; private set; }

        // Tokens that sit ON the accent or on media, or are made from the accent: the base never moves them.
        private static readonly System.Collections.Generic.HashSet<string> NotOnBase = new System.Collections.Generic.HashSet<string>
        {
            "Accent", "AccentHover", "AccentPressed", "OnAccent", "AccentFg", "AccentSoft", "TitleBar", "LinkIn", "SelectionIn",
            "Hover", "ScrollThumbActive", "ChatSelectTint", "Base",
        };

        /// <summary>Re-grounds every NEUTRAL opaque token (a grey or white: channels within 12 of each other) on the base.
        /// Light: a multiply — black stays, white becomes the base, every grey in between follows. Dark: per channel, an
        /// affine map that keeps white and moves the default ground (30) to the base — true black takes the darkest surfaces
        /// to 0 and keeps their steps; text barely moves. Translucent overlays, saturated colours (status, swatches, avatar
        /// hues), anything on the accent (BubbleOut*) or on media (Media*, Story*) are left alone; the accent-made tokens are
        /// re-derived from the new surfaces at the end.</summary>
        private void ApplyBase(Color b, bool dark)
        {
            foreach (var prop in typeof(Palette).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(Color) || NotOnBase.Contains(prop.Name)) continue;
                string n = prop.Name;
                if (n.StartsWith("BubbleOut") || n.StartsWith("Media") || n.StartsWith("Story") || n.StartsWith("AccentSwatch")) continue;
                var c = (Color)prop.GetValue(this, null);
                if (c.A != 255) continue;
                int hi = Math.Max(c.R, Math.Max(c.G, c.B)), lo = Math.Min(c.R, Math.Min(c.G, c.B));
                if (hi - lo > 12) continue;
                prop.SetValue(this, dark ? Color.FromArgb(Ground(c.R, b.R), Ground(c.G, b.G), Ground(c.B, b.B))
                                         : Color.FromArgb(Paper(c.R, b.R), Paper(c.G, b.G), Paper(c.B, b.B)), null);
            }
            var accent = Accent;
            ChatSelectTint = ColorMath.Mix(ChatBg, accent, 0.18f);
            AccentFg = ColorMath.Ink(accent, Surface);
            AccentSoft = ColorMath.Mix(Surface, accent, 0.18f);
        }

        // ── R6 (2026-11-16): Windows' high contrast ────────────────────────────────────────────────────────────

        /// <summary>The few colours Windows gives in high contrast (SystemColors), or a sample scheme for the rig.</summary>
        public sealed class HcScheme
        {
            public Color Window, WindowText, Highlight, HighlightText, GrayText, HotTrack;
            /// <summary>Windows' "High Contrast Black" and "High Contrast White" (TELEGARM_HC=black / white simulates them).</summary>
            public static readonly HcScheme Black = new HcScheme
            {
                Window = Color.FromArgb(0, 0, 0), WindowText = Color.FromArgb(255, 255, 255), Highlight = Color.FromArgb(26, 235, 255),
                HighlightText = Color.FromArgb(0, 0, 0), GrayText = Color.FromArgb(63, 242, 63), HotTrack = Color.FromArgb(255, 255, 0)
            };
            public static readonly HcScheme White = new HcScheme
            {
                Window = Color.FromArgb(255, 255, 255), WindowText = Color.FromArgb(0, 0, 0), Highlight = Color.FromArgb(55, 0, 110),
                HighlightText = Color.FromArgb(255, 255, 255), GrayText = Color.FromArgb(96, 0, 0), HotTrack = Color.FromArgb(0, 0, 159)
            };
        }

        /// <summary>True for a palette made by <see cref="BuildHighContrast"/>: every chrome colour is one of Windows' own.</summary>
        public bool HighContrast { get; private set; }

        /// <summary>R6 — the palette in Windows' high contrast: the normal palette of the mode the scheme's window colour
        /// belongs to, then every token moved onto the scheme (<see cref="ApplyHighContrast"/>).</summary>
        public static Palette BuildHighContrast(HcScheme s)
        {
            bool dark = ColorMath.Luminance(s.Window) < 0.5;
            var p = BuildCore(dark, s.Highlight, null, null);
            p.ApplyHighContrast(s);
            p.HighContrast = true;
            return p;
        }

        // Colours that are content, not chrome: status, avatar hues, the accent swatches, recording red, and what is drawn
        // over pictures (media and story overlays, the chat pill's scrim).
        private static bool KeepInHighContrast(string n)
        {
            return n == "Danger" || n == "Success" || n == "Warning" || n == "AlertRed" || n == "RecordRed" || n == "RecordingDot"
                || n == "OnlineDot" || n == "ListDraft" || n == "RepliesAvatar" || n == "Base" || n == "ChatPillScrim" || n == "ChatPillText"
                || n.StartsWith("AccentSwatch") || n.StartsWith("SponsorPeerColor") || n.StartsWith("ProxyDot") || n.StartsWith("Media")
                || n.StartsWith("Story");
        }

        private static readonly System.Collections.Generic.HashSet<string> HcHighlight = new System.Collections.Generic.HashSet<string>
        {
            "Accent", "AccentHover", "AccentPressed", "TitleBar", "BubbleOut", "ScrollThumbActive", "SelectionIn", "Hover", "AccentGlyph",
            "PillDefaultAccent",
        };
        private static readonly System.Collections.Generic.HashSet<string> HcLink = new System.Collections.Generic.HashSet<string> { "LinkIn", "AccentFg" };
        private static readonly System.Collections.Generic.HashSet<string> HcSoft = new System.Collections.Generic.HashSet<string> { "AccentSoft", "Selected", "ListSelected", "ChatSelectTint" };

        /// <summary>
        /// Moves every token onto the scheme: the inks on the accent take HighlightText, the accent tokens Highlight, links
        /// HotTrack, a selected row a wash of Highlight (its text stays WindowText), lines (dividers, borders, tracks) full
        /// WindowText so they show, disabled tokens GrayText, and every other NEUTRAL token (a grey: channels within 12)
        /// Window or WindowText by which side of the mode's middle it is on. Translucent overlays keep their alpha. Saturated
        /// colours that are content (status, avatars, media) stay as they are.
        /// </summary>
        private void ApplyHighContrast(HcScheme s)
        {
            bool dark = Dark;
            var onAccent = new System.Collections.Generic.HashSet<string>(OnAccentInks) { "OnAccent", "OnBubbleOut" };
            var soft = ColorMath.Mix(s.Window, s.Highlight, 0.35f);
            foreach (var prop in typeof(Palette).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(Color) || !prop.CanWrite) continue;
                string n = prop.Name;
                if (KeepInHighContrast(n)) continue;
                var c = (Color)prop.GetValue(this, null);
                Color to; int a = c.A;
                if (onAccent.Contains(n) || (n.StartsWith("BubbleOut") && n != "BubbleOut")) to = s.HighlightText;
                else if (HcHighlight.Contains(n)) to = s.Highlight;
                else if (HcLink.Contains(n)) to = s.HotTrack;
                else if (HcSoft.Contains(n)) { to = soft; a = 255; }
                else if (n.Contains("Disabled") || n.EndsWith("Off"))
                    to = n.EndsWith("Fill") || n.EndsWith("Bg") ? s.Window : s.GrayText;   // a disabled button: ground, and its label GrayText
                else if (!n.Contains("Placeholder")
                         && (n.Contains("Divider") || n.Contains("Line") || n.Contains("Hairline") || n.Contains("Border") || n.Contains("Separator")
                             || n.Contains("Outline") || n.Contains("Track") || n.Contains("Rim") || n.Contains("Ring") || n.Contains("Thumb")))
                { to = s.WindowText; a = 255; }
                else
                {
                    int hi = Math.Max(c.R, Math.Max(c.G, c.B)), lo = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (hi - lo > 12) continue;   // a saturated colour the lists above do not name: left as it is
                    double l = ColorMath.Luminance(c);
                    to = (dark ? l > 0.18 : l < 0.45) ? s.WindowText : s.Window;   // ink, or ground
                }
                prop.SetValue(this, Color.FromArgb(a, to.R, to.G, to.B), null);
            }
        }

        private static int Paper(int c, int b) { return (int)Math.Round(c * b / 255.0); }

        private static int Ground(int c, int b)
        {
            int d = DefaultDarkBase.R;
            int v = (int)Math.Round(255 - (255 - c) * (255.0 - b) / (255 - d));
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }
    }
}
