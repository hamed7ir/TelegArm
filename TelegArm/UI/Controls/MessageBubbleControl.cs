using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// A single chat bubble. Outgoing messages are right-aligned in the accent
    /// color; incoming are left-aligned in grey, with an optional sender name.
    /// Width/height are computed from the wrapped text; Persian/Arabic text is
    /// detected and rendered right-to-left so it stays inside the bubble.
    ///
    /// Redesign step 3.2 (the ChatListItemControl pattern): every length is LOGICAL (96-DPI) and drawn through P() at
    /// the bubble's DPI; the metrics that used to be consts are now properties over P(), so each use scales with no
    /// other edit. Every font is a shared FontCache font for that DPI, fetched where it is used — NEVER disposed,
    /// never in a using. Media natural sizes and the 160 px sticker box are logical too (same physical size as at
    /// 96). At 96 DPI every value is exactly what it was. The bubble is MEASURED BEFORE it is added (CreateBubble), so
    /// the host hands it its DPI (LayoutDpi); OnParentChanged re-measures only if the window's DPI differs. On a DPI
    /// change it refreshes fonts and drops the InlineText layout, but does NOT measure: the host's relayout does.
    /// </summary>
    public class MessageBubbleControl : Control, IFlashable, IDpiAware
    {
        private int _dpi;                                          // the DPI this bubble is laid out at; 0 = not known yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }
        private int P(int v) { return Dpi.Px(v, CurDpi); }        // a logical length at the bubble's DPI

        /// <summary>The DPI to lay out (and measure) at. Bubbles are measured BEFORE they are parented, so the host sets
        /// this at creation to its window's DPI; once parented the window's DPI wins (OnParentChanged / OnDpiChanged).</summary>
        public int LayoutDpi
        {
            get { return CurDpi; }
            set { if (value > 0 && value != _dpi) { _dpi = value; SyncRichDpi(); } }
        }

        // Fonts — never Control.Font (ambient, not ours; MaterialSkin used to override it). Persian/Arabic text uses
        // Vazirmatn (nicer + larger); Latin uses Roboto, chosen from the message's script. Shared FontCache fonts at the
        // bubble's DPI (step 3.2): never dispose them. Only TextFont follows an album caption's re-derived _rtl; the
        // others keep the script the bubble was BUILT with (_builtRtl) — exactly what the old ctor-made fields did.
        private readonly bool _builtRtl;
        private Font TextFont => _rtl ? FontHelper.PersianAt(12f, CurDpi) : FontHelper.UiAt(9.75f, CurDpi);
        private Font SenderFont => _builtRtl ? FontHelper.PersianAt(10.5f, CurDpi, FontStyle.Bold) : FontHelper.UiAt(8.5f, CurDpi, FontStyle.Bold);
        private Font TimeFont => FontHelper.UiAt(7.5f, CurDpi);
        private Font ReplyFont => _builtRtl ? FontHelper.PersianAt(10f, CurDpi) : FontHelper.UiAt(8.25f, CurDpi);

        private int Pad => P(10);             // inner bubble padding
        private int VMargin => P(4);          // vertical gap between bubbles
        private int SideGap => P(12);         // gap from panel edge
        private int TimeH => P(14);           // timestamp strip height
        private int SenderH => P(16);         // sender-name strip height
        private int ReplyH => P(26);          // "replying to…" quote strip height (text only)
        private int ReplyNameH => P(15);      // extra strip for the quoted sender's name line
        private int FwdH => P(17);            // "Forwarded from …" header strip height
        private int MinBubbleWidth => P(80);  // floor so the timestamp always fits
        private int MinBubbleHeight => P(48); // floor for very short messages

        // Content-width policy (Telegram-style): media, captions and text all live within
        // min(absolute cap, a fraction of the chat pane). Media SCALES TO FIT this — the bubble
        // never shrinks to hug the media (that caused the narrow-caption ribbon).
        private int AbsContentCap => P(480);            // max content width (logical px)
        private const double ContentFraction = 0.72;    // of the chat-pane width

        private string _text;       // not readonly: an album caption (which may live on a non-first item) re-sets it
        private readonly string _sender;
        private readonly bool _outgoing;
        private readonly DateTime _date;
        private bool _rtl;          // re-derived when an album caption is set (its script may differ)

        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        /// <summary>Telegram message id this bubble renders (0 for optimistic/local bubbles).</summary>
        public int MessageId { get; set; }

        /// <summary>When true, draws a "sending" clock glyph (optimistic/pending outgoing bubble).</summary>
        public bool Pending { get; set; }

        /// <summary>When true, draws a red "failed" mark instead of the clock (send error).</summary>
        public bool Failed { get; set; }

        /// <summary>For outgoing messages: true once the peer has read it (✓✓ instead of ✓).</summary>
        public bool Read { get; set; }

        /// <summary>When true, the timestamp is prefixed with "edited" (Message.edit_date set).</summary>
        public bool Edited { get; set; }
        /// <summary>The message's UTC timestamp (for the floating date flyout / day grouping).</summary>
        public DateTime Date => _date;
        /// <summary>BUBBLE-DATETIME (A): the bubble meta shows TIME ONLY (never a date) — the day lives in the day
        /// separators + the floating flyout. Empty for an unset date.</summary>
        private string TimeStamp() => _date == default(DateTime) ? "" : _date.ToLocalTime().ToString("HH:mm");
        /// <summary>The timestamp text, prefixed with "edited" when the message was edited.</summary>
        private string StampText() { return (Edited ? "edited " : "") + TimeStamp(); }
        private int StatusGlyphW => P(14);   // ticks footprint reserved for the outgoing meta strip
        private int MetaGap => P(6);         // gap reserved for the outgoing meta strip
        /// <summary>BUBBLE-DATETIME (B): the full width the meta strip needs = stamp text + (outgoing: ticks + gap).
        /// Folded into the bubble content width so "edited HH:MM" + ticks never left-clips; measured with the paint font.</summary>
        // CHANNEL-META-EXTRAS: channel-post view count (eye + compact number) sits LEFT of the stamp on the meta row;
        // the "sign messages" post_author sits far-LEFT. StampText stays time-only; these reserve their own width so
        // nothing clips (the FIT/DATETIME discipline). Admin role (groups) is a separate label by the sender name.
        private int EyeW => P(13);
        private int EyeGap => P(3);
        private int _views = -1;           // >= 0 → render the channel-post view count
        private string _postAuthor;        // channel byline when the channel signs posts (Message.post_author)
        private string _adminRole;         // group owner/admin/custom-rank label (top row, by the sender name)
        public void SetViews(int v) { _views = v; }
        public void SetPostAuthor(string a) { _postAuthor = string.IsNullOrEmpty(a) ? null : a; }
        public void SetAdminRole(string r) { _adminRole = string.IsNullOrEmpty(r) ? null : r; }
        /// <summary>Compact count formatting matching Telegram: 1234→"1.2K", 1200000→"1.2M".</summary>
        internal static string FormatViews(int n)
        {
            if (n < 0) return "";
            if (n < 1000) return n.ToString();
            if (n < 1000000) { double k = n / 1000.0; return (k < 10 ? k.ToString("0.0") : k.ToString("0")) + "K"; }
            double m = n / 1000000.0; return (m < 10 ? m.ToString("0.0") : m.ToString("0")) + "M";
        }
        private float ViewsBlockW(Graphics g) => _views < 0 ? 0f : EyeW + EyeGap + g.MeasureString(FormatViews(_views), TimeFont).Width + MetaGap;
        private float AuthorBlockW(Graphics g) => string.IsNullOrEmpty(_postAuthor) ? 0f : g.MeasureString(_postAuthor, TimeFont).Width + MetaGap;
        private int MetaWidth(Graphics g) => (int)Math.Ceiling(
            g.MeasureString(StampText(), TimeFont).Width + ViewsBlockW(g) + AuthorBlockW(g))
            + (_outgoing ? StatusGlyphW + MetaGap : 0);

        /// <summary>Group sender's avatar photo (incoming only; referenced — the cache owns it).</summary>
        public Image SenderAvatar { get; set; }

        /// <summary>Sender's peer id — lets a late-arriving avatar find this bubble live (AVATAR-PIPELINE).</summary>
        public long SenderPeerId { get; set; }
        /// <summary>Reserve+draw the sender avatar lane (group incoming), even without a photo.</summary>
        public bool ShowAvatar { get; set; }
        /// <summary>Sender id, for the letter-avatar color when there's no photo.</summary>
        public long AvatarPeerId { get; set; }
        /// <summary>Raised when the sender avatar is clicked (open the member's profile).</summary>
        public event EventHandler AvatarClicked;
        private int AvatarLane => P(40);   // 32px avatar + 8px gap
        private int LeftInset => (!_outgoing && ShowAvatar) ? AvatarLane : 0;
        private Rectangle AvatarRect => new Rectangle(SideGap, Height - VMargin - P(34), P(32), P(32));

        private Rectangle _senderRect;   // painted sender-name area (clickable → profile)

        /// <summary>Per-sender color for the group name label (stable hash), else the accent.</summary>
        private Color SenderNameColor =>
            (!_outgoing && AvatarPeerId != 0) ? DrawHelper.AvatarColor(AvatarPeerId) : ThemeHelper.PaletteFor(IsDark).LinkIn;

        /// <summary>True if this is an outgoing (sent-by-me) bubble.</summary>
        public bool Outgoing => _outgoing;

        /// <summary>One-line preview of the message this one replies to (null/empty = no reply quote).</summary>
        public string ReplyPreview { get; set; }
        private bool HasReply => !string.IsNullOrEmpty(ReplyPreview);

        /// <summary>Id of the replied-to message (0 = none). Tapping the quote jumps to it.</summary>
        public int ReplyToMsgId { get; set; }
        /// <summary>Painted reply-quote band, in control coords (hit-test → ReplyQuoteClicked).</summary>
        private Rectangle _replyRect;
        /// <summary>Raised when the reply quote is tapped, carrying the replied-to message id.</summary>
        public event Action<int> ReplyQuoteClicked;

        /// <summary>Display name of the quoted message's sender (null = unknown → text-only quote).</summary>
        public string ReplySender { get; set; }
        private bool HasReplySender => HasReply && !string.IsNullOrEmpty(ReplySender);
        /// <summary>Reply-quote block height — grows by a name line when the quoted sender is known.</summary>
        private int ReplyBlockH => HasReply ? (HasReplySender ? ReplyH + ReplyNameH : ReplyH) : 0;

        /// <summary>"Forwarded from X" source name (null/empty = not a forward).</summary>
        public string ForwardedFrom { get; set; }
        private bool HasForward => !string.IsNullOrEmpty(ForwardedFrom);
        private int FwdBlockH => HasForward ? FwdH : 0;

        /// <summary>When true, a left-click toggles selection instead of opening media.</summary>
        public bool SelectionMode { get; set; }
        /// <summary>Whether this bubble is currently selected (selection-mode highlight).</summary>
        public bool Selected { get; set; }
        /// <summary>Raised when the bubble is clicked while in selection mode.</summary>
        public event EventHandler SelectionToggled;

        // Sticker mode: render the image with no bubble background, capped small.
        public bool IsSticker { get; set; }

        // Video/gif thumbnail overlays (drawn on top of the photo-style thumbnail).
        public bool IsVideoThumb { get; set; }
        public string DurationText { get; set; }
        public bool IsGif { get; set; }
        /// <summary>The inline video (WebM sticker / GIF) is currently playing in this bubble — suppress the
        /// play overlay + GIF badge while frames render; they return when playback stops.</summary>
        public bool Animating { get; set; }
        /// <summary>This bubble is a tap-to-play inline video (WebM sticker / GIF) — draws a "tap to play"
        /// affordance over the static thumbnail. (WebM stickers only; GIFs already show the video overlay.)</summary>
        public bool IsInlineVideo { get; set; }
        /// <summary>Round "video note" — the square thumbnail is masked to a CIRCLE and the bubble has no
        /// background (like a sticker). Tap still opens the viewer, which clips playback to a circle too.</summary>
        public bool IsRoundVideo { get; set; }

        // ── Reactions ────────────────────────────────────────────────────────
        /// <summary>One reaction chip: an emoji, its count, and whether we picked it.</summary>
        public sealed class ReactionChip
        {
            public string Emoji;
            public int Count;
            public bool Chosen;
        }

        private List<ReactionChip> _reactions;
        private readonly List<KeyValuePair<string, Rectangle>> _reactionRects = new List<KeyValuePair<string, Rectangle>>();
        private int ReactionH => P(26);   // reaction strip height (chips are ReactionH-6 tall)

        private bool HasReactions => _reactions != null && _reactions.Count > 0;

        /// <summary>Current reaction chips (read-only; null when none).</summary>
        public IReadOnlyList<ReactionChip> Reactions => _reactions;

        /// <summary>Raised when a reaction chip is clicked, with the emoji to toggle.</summary>
        public event EventHandler<string> ReactionToggled;

        /// <summary>Sets the reactions shown beneath this bubble and re-measures.</summary>
        public void SetReactions(List<ReactionChip> reactions)
        {
            _reactions = reactions;
            Measure();
            Invalidate();
        }

        // ── Channel-post comments footer (COMMENTS-INDICATOR: display + tap-target only) ──────────────────
        private bool _hasComments;
        private int _commentCount;
        private long _linkedChatId;              // linked discussion supergroup (stored for the later thread-open)
        private Rectangle _commentsRect;         // painted footer band → hit-test → CommentsClicked
        private int CommentH => P(30);           // comments footer strip height (separator + one line)
        private bool HasComments => _hasComments;

        /// <summary>Raised when the comments footer is tapped: (post msg_id, linked discussion chat_id).</summary>
        public event Action<int, long> CommentsClicked;

        /// <summary>COMMENTS-INDICATOR: show the discussion-comments footer under a broadcast post. count 0 →
        /// "Leave a comment"; >0 → "N comments". linkedChatId = linked discussion group (for the later thread-open).</summary>
        public void SetComments(int count, long linkedChatId)
        {
            _hasComments = true;
            _commentCount = count;
            _linkedChatId = linkedChatId;
            Measure();
            Invalidate();
        }

        // ── REPLIES-INBOX "View in chat" footer (display + tap-target only) ────────────────────────────────
        private Rectangle _viewInChatRect;       // painted bottom row → hit-test → ViewInChatClicked
        private int ViewInChatH => P(30);        // "View in chat" row height (separator + one line + chevron)
        /// <summary>REPLIES-INBOX: show a bottom "View in chat ›" row on a Replies-inbox entry (tap → open the source thread).</summary>
        public bool ShowViewInChat { get; set; }
        private bool HasViewInChat => ShowViewInChat;
        /// <summary>Raised when the "View in chat" row is tapped: carries the entry's own message id.</summary>
        public event Action<int> ViewInChatClicked;

        // Document file-card mode (PDF/DOC/ZIP/etc.).
        public bool IsFile { get; set; }
        public string FileName { get; set; }
        public string FileSizeText { get; set; }
        private int FileCardW => P(260);
        private int FileCardH => P(72);

        // ── Inline photo support ─────────────────────────────────────────────
        public enum PhotoState { None, Placeholder, Loading, Loaded }

        private int MaxPhotoHeight => P(360);   // max displayed photo height (taller portrait is cover-cropped)
        private int MaxThumbHeight => P(280);   // max displayed video-thumbnail height

        private PhotoState _photoState = PhotoState.None;
        private int _photoW = 1, _photoH = 1;       // intrinsic photo dimensions
        private Image _image;                        // referenced only — the cache owns/disposes it
        private Rectangle _imageRect;                // last painted image area (for hit-testing)
        private Rectangle _fileRect;                 // last painted file-card area (for hit-testing)
        private Rectangle _bodyRect;                 // last painted text-bubble area (for hit-testing)
        private System.Windows.Forms.Timer _spinnerTimer;
        private float _spinnerAngle;

        /// <summary>Raised when the user clicks a "tap to download" photo placeholder.</summary>
        public event EventHandler DownloadRequested;

        /// <summary>Raised when the user clicks an already-loaded photo (to open the viewer).</summary>
        public event EventHandler ImageClicked;

        // ── Album mode (grouped media grid) ──────────────────────────────────
        private sealed class AlbumTile
        {
            public int MessageId; public Image Image; public bool IsVideo;
            // Audio-album extras (a grouped-audio album renders as a vertical row list, not a grid):
            public bool IsAudio; public long DocId; public string Title; public string Sub; public bool Cached;
            public DownloadHandle Handle;   // in-flight download for this row (ring/cancel); null otherwise
        }
        private List<AlbumTile> _album;   // non-null → render a grid (photo/video) OR a row list (audio)
        private bool _albumAudio;         // true → render as a vertical audio row list
        private readonly List<KeyValuePair<int, Rectangle>> _albumTileRects = new List<KeyValuePair<int, Rectangle>>();
        private int AlbumGap => P(3);
        private int AudioRowH => P(56);      // one audio row
        private int AudioCircle => P(44);    // its circular cover button
        private int _albumAudioTick;      // progress-repaint throttle
        private bool _audioStateHooked;

        /// <summary>Raised when an audio-album ROW is tapped to play/download, with that row's document id.</summary>
        public event Action<long> AudioRowActivated;

        /// <summary>True when this bubble renders a grouped-media album grid.</summary>
        public bool IsAlbum { get { return _album != null; } }

        /// <summary>Raised when an album tile is tapped, with that item's message id (→ open the viewer).</summary>
        public event Action<int> AlbumTileClicked;

        /// <summary>Turns this bubble into an album whose first tile is the bubble's own MessageId.</summary>
        public void BeginAlbum(bool firstIsVideo)
        {
            _album = new List<AlbumTile> { new AlbumTile { MessageId = MessageId, IsVideo = firstIsVideo } };
            Measure();
        }

        /// <summary>Appends another media item to the album (live-merge of a same-grouped_id message).</summary>
        public void AddAlbumItem(int messageId, bool isVideo)
        {
            if (_album == null) return;
            foreach (var t in _album) if (t.MessageId == messageId) return;   // dedupe
            _album.Add(new AlbumTile { MessageId = messageId, IsVideo = isVideo });
            Measure();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>Sets a tile's thumbnail once downloaded.</summary>
        public void SetAlbumTileImage(int messageId, Image img)
        {
            if (_album == null) return;
            foreach (var t in _album) if (t.MessageId == messageId) { t.Image = img; if (!IsDisposed) Invalidate(); return; }
        }

        /// <summary>Marks an album item as AUDIO (→ vertical row list) and sets its row metadata.</summary>
        public void SetAlbumAudio(int messageId, long docId, string title, string sub, bool cached)
        {
            if (_album == null) return;
            foreach (var t in _album)
                if (t.MessageId == messageId)
                {
                    t.IsAudio = true; t.DocId = docId; t.Title = title; t.Sub = sub; t.Cached = cached;
                    _albumAudio = true;
                    if (!_audioStateHooked) { AudioPlayer.StateChanged += OnAlbumAudioStateChanged; _audioStateHooked = true; }
                    Measure();
                    if (!IsDisposed) Invalidate();
                    return;
                }
        }

        /// <summary>Hands the album the in-flight download for a row so it can draw the ring + handle pause.
        /// Same-handle re-attach is a no-op (DOWNLOAD-RESUME: pause/resume keep ONE handle — a resubscribe
        /// here would double every Changed event).</summary>
        public void SetAudioRowHandle(long docId, DownloadHandle handle)
        {
            if (_album == null || handle == null) return;
            foreach (var t in _album)
                if (t.DocId == docId)
                {
                    if (ReferenceEquals(t.Handle, handle)) { if (!IsDisposed) Invalidate(); return; }
                    if (t.Handle != null) t.Handle.Changed -= OnAlbumAudioProgress;
                    t.Handle = handle; handle.Changed += OnAlbumAudioProgress;
                    if (!IsDisposed) Invalidate();
                    return;
                }
        }

        private void OnAlbumAudioStateChanged()   // AudioPlayer play/pause → refresh row glyphs
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) Invalidate(); })); } catch { }
        }

        private void OnAlbumAudioProgress(DownloadHandle h)   // download thread → throttle + marshal
        {
            if (IsDisposed) return;
            if (h.State == DownloadHandle.DState.Downloading)
            {
                int now = Environment.TickCount;
                if (now - _albumAudioTick < 150) return;
                _albumAudioTick = now;
            }
            try { BeginInvoke((Action)(() => OnAlbumAudioProgressUi(h))); } catch { }
        }

        private void OnAlbumAudioProgressUi(DownloadHandle h)
        {
            if (IsDisposed) return;
            if (h.State != DownloadHandle.DState.Downloading)   // finished
            {
                h.Changed -= OnAlbumAudioProgress;
                if (_album != null)
                    foreach (var t in _album)
                        if (t.Handle == h) { t.Handle = null; if (h.State == DownloadHandle.DState.Done) t.Cached = true; }
            }
            Invalidate();
        }

        private Rectangle AudioRowCircle(Rectangle rowRect)   // instance since step 3.2: AudioCircle is per-DPI
        {
            return new Rectangle(rowRect.X, rowRect.Y + (rowRect.Height - AudioCircle) / 2, AudioCircle, AudioCircle);
        }

        private AlbumTile FindAlbumTile(int messageId)
        {
            if (_album != null) foreach (var t in _album) if (t.MessageId == messageId) return t;
            return null;
        }

        /// <summary>All message ids in this album (so a jump to any item lands here).</summary>
        public bool ContainsMessageId(int id)
        {
            if (_album == null) return false;
            foreach (var t in _album) if (t.MessageId == id) return true;
            return false;
        }

        private int AlbumCols { get { int n = _album.Count; return n <= 4 ? 2 : 3; } }

        private int AlbumGridHeight(int contentW)
        {
            int n = _album.Count, cols = AlbumCols;
            int rows = (n + cols - 1) / cols;
            int tile = Math.Max(1, (contentW - (cols - 1) * AlbumGap) / cols);
            return rows * tile + (rows - 1) * AlbumGap;
        }

        /// <summary>The album's content height — the SINGLE value used by Measure AND PaintAlbum so they
        /// agree. Audio albums = N rows × rowHeight; photo/video albums = the cover grid.</summary>
        private int AlbumContentHeight(int contentW)
        {
            return _albumAudio ? _album.Count * AudioRowH : AlbumGridHeight(contentW);
        }

        private int AlbumCaptionHeight(int contentW)
        {
            if (_useRich)
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                { _rich.Measure(g, contentW, TextFont); _rich.Position(contentW, _rtl); return _rich.Height + P(6); }
            }
            if (string.IsNullOrEmpty(_text)) return 0;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))   // pixel-unit fonts measure the same on any DC (step 3.2)
            using (var sf = MakeFormat())
                return (int)Math.Ceiling(g.MeasureString(_text, TextFont, new SizeF(contentW, float.MaxValue), sf).Height) + P(6);
        }

        private void PaintAlbum(Graphics g)
        {
            int contentW = MaxInnerWidth;
            int senderH = _sender != null ? SenderH : 0;
            int gridH = AlbumContentHeight(contentW);   // grid (photo/video) OR N audio rows — single source
            int captionH = AlbumCaptionHeight(contentW);
            int bubbleW = contentW + 2 * Pad;
            int bubbleH = Math.Max(MinBubbleHeight, 2 * Pad + senderH + FwdBlockH + ReplyBlockH + gridH + captionH + TimeH);

            int bx = _outgoing ? Width - bubbleW - SideGap : SideGap + LeftInset;
            int by = VMargin;
            var rect = new Rectangle(bx, by, bubbleW, bubbleH);
            _bodyRect = rect;

            Color bubbleColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOut : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
            Color textColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : (ThemeHelper.PaletteFor(IsDark).BubbleInText);
            using (var b = new SolidBrush(bubbleColor))
            using (var path = DrawHelper.RoundedRect(rect, P(14)))
                g.FillPath(b, path);

            int innerX = bx + Pad;
            int ty = by + Pad;
            if (_sender != null) { DrawSenderName(g, innerX, ty, contentW); ty += SenderH; }
            if (HasForward) { DrawForwardHeader(g, innerX, ty, contentW); ty += FwdH; }
            if (HasReply) { DrawReplyHeader(g, innerX, ty, contentW); ty += ReplyBlockH; }

            int n = _album.Count;
            _albumTileRects.Clear();
            if (_albumAudio)   // vertical audio row list (Telegram-Desktop style)
            {
                Color subColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSub
                                           : (ThemeHelper.PaletteFor(IsDark).BubbleInSub);
                for (int i = 0; i < n; i++)
                {
                    var rowRect = new Rectangle(innerX, ty + i * AudioRowH, contentW, AudioRowH - P(4));
                    _albumTileRects.Add(new KeyValuePair<int, Rectangle>(_album[i].MessageId, rowRect));
                    DrawAudioRow(g, _album[i], rowRect, textColor, subColor);
                }
            }
            else   // photo/video cover grid (unchanged)
            {
                int cols = AlbumCols;
                int tile = Math.Max(1, (contentW - (cols - 1) * AlbumGap) / cols);
                for (int i = 0; i < n; i++)
                {
                    int r = i / cols, c = i % cols;
                    var tr = new Rectangle(innerX + c * (tile + AlbumGap), ty + r * (tile + AlbumGap), tile, tile);
                    _albumTileRects.Add(new KeyValuePair<int, Rectangle>(_album[i].MessageId, tr));
                    using (var pth = DrawHelper.RoundedRect(tr, P(6)))
                    {
                        var prev = g.Clip;
                        g.SetClip(pth);
                        if (_album[i].Image != null) DrawFittedImage(g, _album[i].Image, tr, P(8), cover: true);
                        else using (var fb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).BubbleAlbumCoverPlaceholder)) g.FillPath(fb, pth);
                        g.Clip = prev; prev.Dispose();
                    }
                    if (_album[i].IsVideo && _album[i].Image != null) DrawVideoOverlay(g, tr);
                }
            }
            ty += gridH;

            if (captionH > 0)
            {
                if (_useRich)
                {
                    _richOrigin = new Point(innerX, ty);
                    _rich.PaintCached(g, innerX, ty, textColor, LinkColor, TextFont, IsDark, AccentColor, _outgoing, bubbleColor);
                    PaintSelection(g, innerX, ty);   // translucent highlight OVER the opaque text bitmap
                }
                else using (var sf = MakeFormat())
                using (var tb = new SolidBrush(textColor))
                    g.DrawString(_text, TextFont, tb, new RectangleF(innerX, ty, contentW, captionH - P(6)), sf);
            }

            Color timeColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutTime
                                        : (ThemeHelper.PaletteFor(IsDark).BubbleInTime);
            using (var timeSf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far })
            using (var tcb = new SolidBrush(timeColor))
                g.DrawString(StampText(), TimeFont, tcb, new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeSf);
            DrawStatusGlyph(g, bx + Pad + P(6), by + bubbleH - TimeH / 2f, timeColor);
            DrawMetaExtras(g, new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeColor);

            DrawFooter(g, bx, by + bubbleH + P(2), bubbleW);
        }

        /// <summary>One audio-album row: circular cover/play-download button + title + "M:SS • size".</summary>
        private void DrawAudioRow(Graphics g, AlbumTile t, Rectangle rowRect, Color fg, Color sub)
        {
            var circle = AudioRowCircle(rowRect);
            if (t.Image != null)
                DrawHelper.DrawAudioCover(g, circle, t.Image);   // shared cover-in-circle (clip + dim overlay)
            else
                using (var fb = new SolidBrush(_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutIconDisc : AccentColor)) g.FillEllipse(fb, circle);

            bool downloading = t.Handle != null && t.Handle.State == DownloadHandle.DState.Downloading;
            bool paused = t.Handle != null && t.Handle.State == DownloadHandle.DState.Paused;
            if (downloading)
                DrawHelper.DrawProgressRing(g, Rectangle.Inflate(circle, -P(3), -P(3)), t.Handle.Fraction, (_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent), ThemeHelper.PaletteFor(IsDark).MediaRingTrack, cancel: true);
            else if (paused)
            {
                // Frozen ring + a play triangle = "tap to resume" (DOWNLOAD-UX 3.3; resume = fresh transfer).
                DrawHelper.DrawProgressRing(g, Rectangle.Inflate(circle, -P(3), -P(3)), t.Handle.Fraction, (_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent), ThemeHelper.PaletteFor(IsDark).MediaRingTrack, cancel: false);
                float k = circle.Width / 44f;   // drawn for the 44 px circle; scales with it
                float cx = circle.X + circle.Width / 2f, cy = circle.Y + circle.Height / 2f;
                using (var wb = new SolidBrush((_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent)))
                    g.FillPolygon(wb, new[] { new PointF(cx - 4 * k, cy - 6 * k), new PointF(cx + 6 * k, cy), new PointF(cx - 4 * k, cy + 6 * k) });
            }
            else
                DrawAudioRowGlyph(g, circle, t);

            int tx = circle.Right + P(10);
            int tw = rowRect.Right - tx;
            var tf = (_rtl ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, t.Title ?? "Audio", SenderFont, new Rectangle(tx, rowRect.Y + P(7), tw, P(20)), fg, tf | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, t.Sub ?? "", TimeFont, new Rectangle(tx, rowRect.Y + P(29), tw, P(16)), sub, tf);
        }

        /// <summary>Drawn for the 44 px circle; scales with it.</summary>
        private void DrawAudioRowGlyph(Graphics g, Rectangle circle, AlbumTile t)
        {
            float k = circle.Width / 44f;
            float cx = circle.X + circle.Width / 2f, cy = circle.Y + circle.Height / 2f;
            using (var wb = new SolidBrush((_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent)))
            using (var pen = new Pen((_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent), 2.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (!t.Cached)   // download arrow
                {
                    g.DrawLine(pen, cx, cy - 7 * k, cx, cy + 4 * k);
                    g.DrawLine(pen, cx - 5 * k, cy - 1 * k, cx, cy + 4 * k);
                    g.DrawLine(pen, cx + 5 * k, cy - 1 * k, cx, cy + 4 * k);
                    g.DrawLine(pen, cx - 6 * k, cy + 8 * k, cx + 6 * k, cy + 8 * k);
                }
                else if (AudioPlayer.IsPlayingId(t.DocId))   // pause
                {
                    g.FillRectangle(wb, cx - 5 * k, cy - 7 * k, 3.6f * k, 14 * k);
                    g.FillRectangle(wb, cx + 1.4f * k, cy - 7 * k, 3.6f * k, 14 * k);
                }
                else   // play
                {
                    g.FillPolygon(wb, new[] { new PointF(cx - 5 * k, cy - 8 * k), new PointF(cx + 8 * k, cy), new PointF(cx - 5 * k, cy + 8 * k) });
                }
            }
        }

        /// <summary>
        /// Raised on right-click, touch-and-hold, or the keyboard menu key, with the
        /// screen-space anchor point for the context menu. Windows turns touch-and-hold
        /// into WM_CONTEXTMENU, so this covers touch on the tablet for free.
        /// </summary>
        public event EventHandler<Point> ContextMenuRequested;

        private const int WM_CONTEXTMENU = 0x007B;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CONTEXTMENU)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1
                    ? PointToScreen(new Point(Width / 2, Height / 2))        // keyboard menu key
                    : new Point(unchecked((short)(lp & 0xFFFF)),             // mouse / touch (signed)
                                unchecked((short)((lp >> 16) & 0xFFFF)));
                ContextMenuRequested?.Invoke(this, pt);
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>When true, the whole (non-photo) media bubble is clickable to open the viewer.</summary>
        public bool ClickableMedia
        {
            get { return _clickableMedia; }
            set { _clickableMedia = value; Cursor = value ? Cursors.Hand : Cursors.Default; }
        }
        private bool _clickableMedia;

        // ── File/document in-bubble download (ring + MB/MB + ✕), same model as VoiceBubbleControl ──
        private Func<DownloadHandle> _fileStarter;   // starts (or returns the in-flight) download for this file
        private DownloadHandle _fileHandle;          // in-flight; null when idle/done
        private bool _fileCached;                    // true once the full file is on disk → tap opens
        private int _fileTick;                       // progress repaint throttle

        /// <summary>Wires the file's pausable download. <paramref name="cached"/> = already on disk (tap opens).</summary>
        public void ConfigureFileDownload(Func<DownloadHandle> starter, bool cached)
        {
            _fileStarter = starter; _fileCached = cached;
        }

        /// <summary>DOWNLOAD-UX 2.2: attaches an ALREADY-RUNNING transfer (rebind when the chat is reopened
        /// while its download kept going in the background). Same-handle re-attach is a no-op.</summary>
        public void SetDownloadHandle(DownloadHandle h)
        {
            if (h == null || IsDisposed || ReferenceEquals(_fileHandle, h)) return;
            if (_fileHandle != null) _fileHandle.Changed -= OnFileHandleChanged;
            _fileHandle = h;
            h.Changed += OnFileHandleChanged;
            Invalidate();
        }

        private Rectangle FileIconRect()   // matches PaintFile's icon rect (the ring + ✕ live here)
        {
            int cardW = Math.Min(FileCardW, MaxInnerWidth + 2 * Pad);
            int bx = _outgoing ? Width - cardW - SideGap : SideGap + LeftInset;
            int top = VMargin + FwdBlockH + ReplyBlockH;
            return new Rectangle(bx + P(12), top + P(12), P(48), P(48));
        }

        private void StartFileDownload()
        {
            if (_fileStarter == null) return;
            var h = _fileStarter();
            if (h == null) return;
            if (h.State == DownloadHandle.DState.Done) { _fileCached = true; Invalidate(); return; }
            _fileHandle = h;
            h.Changed += OnFileHandleChanged;
            Invalidate();
        }

        private void OnFileHandleChanged(DownloadHandle h)
        {
            if (IsDisposed) return;
            if (h.State == DownloadHandle.DState.Downloading)
            {
                int now = Environment.TickCount;
                if (now - _fileTick < 150) return;   // throttle
                _fileTick = now;
            }
            try { BeginInvoke((Action)(() => OnFileHandleChangedUi(h))); } catch { }
        }

        private void OnFileHandleChangedUi(DownloadHandle h)
        {
            if (IsDisposed) return;
            if (h.State != DownloadHandle.DState.Downloading)
            {
                h.Changed -= OnFileHandleChanged;
                if (_fileHandle == h) _fileHandle = null;
                if (h.State == DownloadHandle.DState.Done) _fileCached = true;   // tap now opens
            }
            Invalidate();
        }

        private bool HasPhoto => _photoState != PhotoState.None;

        /// <summary>Sets the intrinsic photo size and initial state, then re-measures.</summary>
        public void ConfigurePhoto(int intrinsicW, int intrinsicH, PhotoState state)
        {
            _photoW = intrinsicW > 0 ? intrinsicW : 1;
            _photoH = intrinsicH > 0 ? intrinsicH : 1;
            SetPhotoState(state);
            Measure();
        }

        public void SetImage(Image img) { _image = img; SetPhotoState(PhotoState.Loaded); }
        public void SetLoading() { SetPhotoState(PhotoState.Loading); }
        public void SetPlaceholder() { SetPhotoState(PhotoState.Placeholder); }

        /// <summary>Lightweight per-frame image swap for animated (.tgs) stickers — avoids the
        /// state-machine churn of SetImage when called ~25×/sec. Same bitmap object each tick.</summary>
        public void SetFrame(Image img)
        {
            _image = img;
            _photoState = PhotoState.Loaded;
            StopSpinner();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>An owner (e.g. a LottieAnimator) disposed with this bubble to stop animation.</summary>
        public IDisposable AnimationOwner { get; set; }

        // Inline rich text (links / @mentions / inline emoji). Built only for messages with
        // entities or emoji — plain text (incl. plain Persian) stays on the untouched DrawString path.
        private InlineText _rich;
        private bool _useRich;
        private Point _richOrigin;
        private InlineTextSelection _sel;   // shared text-selection engine over _rich (within-bubble)
        private bool _suppressClick;        // a drag-select just finished → swallow the click (don't open a link)
        // Drawn OVER the (opaque) text bitmap now, so it must contrast with the bubble: a translucent white on
        // the accent (outgoing) bubble, the translucent accent on the gray (incoming) bubble.
        private Color SelectionColor => _outgoing ? ThemeHelper.PaletteFor(IsDark).SelectionOut : ColorMath.WithAlpha(AccentColor, 110);
        public event Action<string> LinkClicked;
        public event Action<string, long> MentionClicked;
        public event Action<string> HashtagClicked;
        public event Action<string> BotCommandClicked;

        /// <summary>Resolves a custom-emoji document id to its cached image (null until loaded).</summary>
        public Func<long, Image> CustomEmojiResolver;

        private Color LinkColor => _outgoing ? ThemeHelper.PaletteFor(IsDark).LinkOut : ThemeHelper.PaletteFor(IsDark).LinkIn;

        // ── Link-preview card (MessageMediaWebPage) ──────────────────────────
        // A web-page preview stacked BELOW the message text (like a quote block): accent bar +
        // site/title/description + a small thumbnail. Tapping the card opens the URL.
        private int CardBar => P(3);
        private int CardPad => P(8);
        private int CardGap => P(6);
        private int CardThumb => P(56);
        private int CardTopGap => P(4);
        private bool _hasCard, _cardHasThumb;
        private string _cardSite, _cardTitle, _cardDesc, _cardUrl;
        private Image _cardThumb;                 // referenced — MainForm's thumb cache owns it
        private Rectangle _cardRect;              // painted card area (hit-test → open url)
        private int _cardHeight, _cardTextColW, _cardSiteH, _cardTitleH, _cardDescH;
        // Shared per-DPI card fonts (step 3.2: never disposed). They used to be made once in SetLinkPreview from _rtl;
        // only an album caption (SetCaption) ever changes _rtl after the ctor, and albums carry no card, so the
        // built script is the same value.
        private Font CardTitleFont => _builtRtl ? FontHelper.PersianAt(11f, CurDpi, FontStyle.Bold) : FontHelper.UiAt(9f, CurDpi, FontStyle.Bold);
        private Font CardDescFont => _builtRtl ? FontHelper.PersianAt(10.5f, CurDpi) : FontHelper.UiAt(8.5f, CurDpi);
        private Font CardSiteFont => _builtRtl ? FontHelper.PersianAt(9.5f, CurDpi, FontStyle.Bold) : FontHelper.UiAt(8f, CurDpi, FontStyle.Bold);

        /// <summary>Attaches a web-page link preview (rendered as a card under the text). url empty = no card.</summary>
        public void SetLinkPreview(string site, string title, string desc, string url, bool hasThumb)
        {
            _cardSite = site; _cardTitle = title; _cardDesc = desc; _cardUrl = url; _cardHasThumb = hasThumb;
            _hasCard = !string.IsNullOrEmpty(url) &&
                       (hasThumb || !string.IsNullOrEmpty(site) || !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(desc));
            Measure();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>Sets the card thumbnail (from the shared photo-thumb cache); repaints in place.</summary>
        public void SetCardThumb(Image img) { _cardThumb = img; if (!IsDisposed) Invalidate(); }

        /// <summary>Supplies the message's TL entities; switches the body to the inline engine if needed.</summary>
        public void SetEntities(TL.MessageEntity[] entities)
        {
            bool need = _text.Length > 0 &&
                        ((entities != null && entities.Length > 0) || EmojiRenderer.ContainsEmoji(_text));
            _rich?.Dispose();
            _useRich = need;
            _rich = need ? new InlineText(_text, entities, CustomEmojiResolver) { LayoutDpi = CurDpi } : null;   // step 3.2: its pixel font's DPI
            _sel?.Attach(_rich);   // rebind the selection engine to the new InlineText (clears any selection)
            Measure();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>True when this album bubble already carries a caption.</summary>
        public bool AlbumHasCaption { get { return _album != null && !string.IsNullOrEmpty(_text); } }

        /// <summary>
        /// Sets/replaces the album caption. The caption can live on ANY one grouped item (not always the
        /// first), so it's resolved by the caller and set here. Re-derives the script font + RTL from the
        /// caption text (a Persian caption needs Vazirmatn / right-alignment) and rebuilds the inline engine
        /// (entities, links, emoji) — reusing the normal caption machinery.
        /// </summary>
        public void SetCaption(string text, TL.MessageEntity[] entities)
        {
            _text = text ?? "";
            bool newRtl = IsRtl(_text);
            if (newRtl != _rtl)
            {
                _rtl = newRtl;   // TextFont follows _rtl (shared fonts — nothing to swap or dispose, step 3.2)
                RightToLeft = _rtl ? RightToLeft.Yes : RightToLeft.No;
            }
            SetEntities(entities);   // rebuilds _useRich/_rich from _text + entities → Measure + Invalidate
        }

        /// <summary>Re-lays out the inline text (e.g. after custom-emoji images finish loading).</summary>
        public void RefreshRich()
        {
            if (!_useRich) return;
            _rich?.InvalidateLayout();   // a custom emoji just loaded → defeat the measure/bitmap caches so it re-resolves + re-renders
            Measure();
            if (!IsDisposed) Invalidate();
        }

        // Brief accent flash (used when jumping to a pinned message).
        private System.Windows.Forms.Timer _flashTimer;
        private int _flashTicks;

        /// <summary>Briefly pulses an accent tint over the bubble (e.g. on "scroll to pinned message").</summary>
        public void Flash()
        {
            _flashTicks = 6;
            if (_flashTimer == null)
            {
                _flashTimer = new System.Windows.Forms.Timer { Interval = 120 };
                _flashTimer.Tick += (s, e) => { if (--_flashTicks <= 0) _flashTimer.Stop(); if (!IsDisposed) Invalidate(); };
            }
            _flashTimer.Start();
            if (!IsDisposed) Invalidate();
        }

        private void SetPhotoState(PhotoState state)
        {
            _photoState = state;
            Cursor = state == PhotoState.Placeholder ? Cursors.Hand : Cursors.Default;
            if (state == PhotoState.Loading) StartSpinner(); else StopSpinner();
            Invalidate();
        }

        private void StartSpinner()
        {
            if (_spinnerTimer == null)
            {
                _spinnerTimer = new System.Windows.Forms.Timer { Interval = 80 };
                _spinnerTimer.Tick += (s, e) => { _spinnerAngle = (_spinnerAngle + 30f) % 360f; Invalidate(); };
            }
            _spinnerTimer.Start();
        }

        private void StopSpinner() { _spinnerTimer?.Stop(); }

        /// <summary>
        /// Bubble width policy + media fit. <paramref name="contentW"/> is the bubble's inner width
        /// (caption wrap width) — the policy width when there's a caption, else it hugs the media.
        /// <paramref name="dispW"/>/<paramref name="dispH"/> are the media's displayed size (scaled to
        /// fit the policy width, never upscaled, tall/portrait cover-cropped at the height cap).
        /// </summary>
        private void GetMediaLayout(out int contentW, out int dispW, out int dispH)
        {
            // Step 3.2: the natural size is LOGICAL (a photo keeps its physical size at any DPI), as is the sticker box.
            int natW = Math.Max(1, P(_photoW)), natH = Math.Max(1, P(_photoH));

            if (IsSticker)
            {
                double box = P(160);
                double s = Math.Min(box / natW, box / natH);
                if (s > 1) s = 1;
                dispW = Math.Max(1, (int)(natW * s));
                dispH = Math.Max(1, (int)(natH * s));
                contentW = dispW;
                return;
            }

            int policy = MaxInnerWidth;
            int fillW = Math.Min(policy, natW);                  // media fills the policy width, never upscaled
            contentW = _text.Length > 0 ? policy : fillW;        // caption → policy width; no caption → hug media
            dispW = fillW;
            int maxH = IsVideoThumb ? MaxThumbHeight : MaxPhotoHeight;
            dispH = (int)((long)dispW * natH / natW);
            if (dispH > maxH) dispH = maxH;                      // tall/portrait → cover-crop on draw
            dispH = Math.Max(1, dispH);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_suppressClick) { _suppressClick = false; return; }   // a drag-select just ended → keep it; not a click
            // (a plain LEFT click already cleared the selection in InlineTextSelection.MouseUp — don't clear here,
            //  so a right-click to open "Copy Selected Text" doesn't wipe the selection before the menu reads it)
            if (SelectionMode)
            {
                SelectionToggled?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!_outgoing && AvatarPeerId != 0 &&
                ((ShowAvatar && AvatarRect.Contains(e.Location)) || _senderRect.Contains(e.Location)))
            {
                AvatarClicked?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (HasReactions)
            {
                foreach (var kvp in _reactionRects)
                    if (kvp.Value.Contains(e.Location)) { ReactionToggled?.Invoke(this, kvp.Key); return; }
            }
            if (!_commentsRect.IsEmpty && _commentsRect.Contains(e.Location))   // COMMENTS-INDICATOR: footer tap (distinct bottom rect)
            {
                CommentsClicked?.Invoke(MessageId, _linkedChatId);
                return;
            }
            if (!_viewInChatRect.IsEmpty && _viewInChatRect.Contains(e.Location))   // REPLIES-INBOX: "View in chat" row tap
            {
                ViewInChatClicked?.Invoke(MessageId);
                return;
            }
            // Inline keyboard (any bubble) + poll options/vote/retract — bounded sub-rects, tap-vs-scroll via OnMouseClick.
            if (HandleInteractiveClick(e.Location)) return;
            if (IsAlbum)
            {
                foreach (var kvp in _albumTileRects)
                    if (kvp.Value.Contains(e.Location))
                    {
                        if (_albumAudio)
                        {
                            var t = FindAlbumTile(kvp.Key);
                            if (t == null) return;
                            if (t.Handle != null && t.Handle.State == DownloadHandle.DState.Downloading)
                            {
                                // DOWNLOAD-UX 3.3: tap-while-downloading = PAUSE (cancel lives in the manager
                                // panel / context menu). Tap-while-paused falls through → activate = resume.
                                if (AudioRowCircle(kvp.Value).Contains(e.Location)) t.Handle.Pause();
                                return;
                            }
                            AudioRowActivated?.Invoke(t.DocId);   // play / download(-resume)-then-play
                            return;
                        }
                        AlbumTileClicked?.Invoke(kvp.Key); return;   // photo/video → viewer
                    }
                if (_useRich && _rich != null)   // a link / @mention in the album caption
                {
                    var local = new Point(e.X - _richOrigin.X, e.Y - _richOrigin.Y);
                    if (_rich.HasHiddenSpoilerAt(local)) { _rich.RevealSpoilers(); if (!IsDisposed) Invalidate(); return; }
                    var hit = _rich.HitTest(local);
                    if (hit != null)
                    {
                        if (hit.Kind == InlineKind.Url && !string.IsNullOrEmpty(hit.Url)) LinkClicked?.Invoke(hit.Url);
                        else if (hit.Kind == InlineKind.Mention) MentionClicked?.Invoke(hit.Username, hit.UserId);
                        else if (hit.Kind == InlineKind.Hashtag) HashtagClicked?.Invoke(hit.Data);
                        else if (hit.Kind == InlineKind.BotCommand) BotCommandClicked?.Invoke(hit.Data);
                        return;
                    }
                }
                return;   // taps elsewhere on the album do nothing
            }
            if (ReplyToMsgId != 0 && !_replyRect.IsEmpty && _replyRect.Contains(e.Location))
            {
                ReplyQuoteClicked?.Invoke(ReplyToMsgId);   // jump to the replied-to message (distinct from body taps)
                return;
            }
            if (_useRich && _rich != null)
            {
                var local = new Point(e.X - _richOrigin.X, e.Y - _richOrigin.Y);
                if (_rich.HasHiddenSpoilerAt(local))   // first tap on a spoiler reveals it (link opens on the next tap)
                {
                    _rich.RevealSpoilers();
                    if (!IsDisposed) Invalidate();
                    return;
                }
                var hit = _rich.HitTest(local);
                if (hit != null)
                {
                    if (hit.Kind == InlineKind.Url && !string.IsNullOrEmpty(hit.Url)) LinkClicked?.Invoke(hit.Url);
                    else if (hit.Kind == InlineKind.Mention) MentionClicked?.Invoke(hit.Username, hit.UserId);
                    else if (hit.Kind == InlineKind.Hashtag) HashtagClicked?.Invoke(hit.Data);
                    else if (hit.Kind == InlineKind.BotCommand) BotCommandClicked?.Invoke(hit.Data);
                    return;
                }
            }
            if (_hasCard && _cardRect.Contains(e.Location) && !string.IsNullOrEmpty(_cardUrl))
            {
                LinkClicked?.Invoke(_cardUrl);   // tap the link-preview card → open its URL
                return;
            }
            if (HasPhoto)
            {
                if (!_imageRect.Contains(e.Location)) return;
                // DOWNLOAD-UX v3 2.3: while a managed transfer owns this video/GIF thumb, the tap controls
                // the TRANSFER (downloading→pause, paused→resume); idle/complete taps keep their meaning.
                if (IsVideoThumb && _fileHandle != null)
                {
                    if (_fileHandle.State == DownloadHandle.DState.Downloading)
                    { System.Diagnostics.Debug.WriteLine("[DL] video/gif pause tap"); _fileHandle.Pause(); return; }
                    if (_fileHandle.State == DownloadHandle.DState.Paused)
                    { System.Diagnostics.Debug.WriteLine("[DL] video/gif resume tap"); DownloadRequested?.Invoke(this, EventArgs.Empty); return; }
                }
                if (_photoState == PhotoState.Placeholder)
                    DownloadRequested?.Invoke(this, EventArgs.Empty);
                else if (_photoState == PhotoState.Loaded)
                    ImageClicked?.Invoke(this, EventArgs.Empty);
            }
            else if (_clickableMedia)
            {
                // Only the visible bubble/card is clickable — not the empty track beside it (the control
                // spans the full panel width for alignment). An unset (unpainted) rect is inert, NOT any-click.
                Rectangle hit = IsFile ? _fileRect : _bodyRect;
                if (hit == Rectangle.Empty || !hit.Contains(e.Location))
                {
                    if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] " + (IsFile ? "file" : "media") + ": tap outside bubble → ignored");
                    return;
                }
                if (IsFile && _fileStarter != null)
                {
                    // idle → start download; downloading → ✕ (icon) cancels; done → open.
                    if (_fileHandle != null && _fileHandle.State == DownloadHandle.DState.Downloading)
                    {
                        if (FileIconRect().Contains(e.Location)) { System.Diagnostics.Debug.WriteLine("[DL] file pause tap"); _fileHandle.Pause(); }   // DOWNLOAD-UX 3.3
                        return;
                    }
                    if (_fileCached) { if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] file: open"); ImageClicked?.Invoke(this, EventArgs.Empty); return; }
                    if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] file: start download");
                    StartFileDownload();
                    return;
                }
                if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] media: tap on bubble → action");
                ImageClicked?.Invoke(this, EventArgs.Empty);
            }
        }

        public MessageBubbleControl(string text, string sender, bool outgoing, DateTime date)
        {
            _text = string.IsNullOrEmpty(text) ? "" : text;
            _sender = sender;
            _outgoing = outgoing;
            _date = date;
            _rtl = IsRtl(_text);
            _builtRtl = _rtl;
            if (_rtl) RightToLeft = RightToLeft.Yes;
            // (Fonts are the TextFont/SenderFont/TimeFont/ReplyFont properties: shared, per-DPI — step 3.2.)

            Margin = new Padding(0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _sel = new InlineTextSelection(this);
        }

        /// <summary>Step 3.2 — bubbles are built (and measured) before they are added: adopt the window's DPI once
        /// parented, re-measuring only if it differs from the DPI the bubble was measured at (no extra measure per
        /// bubble in the normal case, where CreateBubble already set LayoutDpi).</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d == _dpi) return;
            int was = CurDpi;   // what fonts + metrics used so far (the default DPI while _dpi was 0)
            _dpi = d;
            SyncRichDpi();
            if (d != was) { Measure(); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;     // the fonts and every metric follow CurDpi from here on
            SyncRichDpi();     // the InlineText layout was measured at the old DPI: drop it
            // NOT Measure(): DpiScaler has ratio-scaled Height already, and the host's deferred relayout pass measures
            // every bubble once (step 3.4) — measuring here too would double the Tegra 3 cost of a DPI change.
            Invalidate();
        }

        /// <summary>Hands the inline engine the bubble's DPI (its pixel-unit fonts' points and its own lengths) and
        /// drops its cached layout + bitmap, so the next measure/paint lays out afresh at that DPI.</summary>
        private void SyncRichDpi()
        {
            if (_rich == null) return;
            int d = CurDpi;
            if (_rich.LayoutDpi != d) { _rich.LayoutDpi = d; _rich.InvalidateLayout(); }
        }

        // ── Within-bubble text selection (shared engine over _rich) ──────────────────────────────
        private bool InRich(Point p)
        {
            return _useRich && _rich != null
                && p.X >= _richOrigin.X && p.X <= _richOrigin.X + _rich.Width
                && p.Y >= _richOrigin.Y && p.Y <= _richOrigin.Y + _rich.Height;
        }
        private Point ToRichLocal(Point p) { return new Point(p.X - _richOrigin.X, p.Y - _richOrigin.Y); }
        private void PaintSelection(Graphics g, int innerX, int ty)
        {
            if (_sel != null && _sel.HasSelection) _sel.Paint(g, innerX, ty, SelectionColor);
        }

        public bool HasTextSelection => _sel != null && _sel.HasSelection;
        public bool HasSelectableText => _sel != null && _sel.HasText;
        public string GetSelectedText() { return _sel != null ? _sel.SelectedText : null; }
        public void SelectAllText() { if (_sel != null) _sel.SelectAll(); }
        public void ClearTextSelection() { if (_sel != null) _sel.Clear(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !SelectionMode && InRich(e.Location))
                _sel.MouseDown(ToRichLocal(e.Location));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_sel != null && _sel.MouseMove(ToRichLocal(e.Location))) Cursor = Cursors.IBeam;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_sel != null && e.Button == MouseButtons.Left) _suppressClick = _sel.MouseUp();
        }

        /// <summary>The content-width policy (inner, excludes padding): min(cap, fraction·pane), never wider than the pane.</summary>
        private int MaxInnerWidth
        {
            get
            {
                int avail = Width - 2 * SideGap - LeftInset - 2 * Pad;                 // never exceed the pane
                int policy = Math.Min(AbsContentCap, (int)(Width * ContentFraction)) - 2 * Pad;
                return Math.Max(MinBubbleWidth - 2 * Pad, Math.Min(policy, avail));
            }
        }

        /// <summary>Builds a wrapping StringFormat with the correct text direction.</summary>
        private StringFormat MakeFormat()
        {
            var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Trimming = StringTrimming.None,
                LineAlignment = StringAlignment.Near
            };
            sf.FormatFlags &= ~StringFormatFlags.NoWrap; // ensure word wrap is on
            if (_rtl)
            {
                sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                sf.Alignment = StringAlignment.Far;
            }
            else
            {
                sf.Alignment = StringAlignment.Near;
            }
            return sf;
        }

        /// <summary>
        /// Computes the bubble geometry for the current width. Measures with the
        /// SAME StringFormat and width that OnPaint draws with, so wrapped text
        /// can never paint outside the bubble.
        /// </summary>
        private void ComputeLayout(Graphics g, StringFormat sf,
            out int contentW, out int textH, out int bubbleW, out int bubbleH)
        {
            int maxInner = MaxInnerWidth;

            int longest = _useRich
                ? _rich.Measure(g, maxInner, TextFont)
                : (int)Math.Ceiling(g.MeasureString(_text, TextFont, new SizeF(maxInner, float.MaxValue), sf).Width);

            int senderW = 0;
            if (_sender != null)
            {
                var sm = g.MeasureString(_sender, SenderFont, new SizeF(maxInner, float.MaxValue), sf);
                senderW = (int)Math.Ceiling(sm.Width);
                if (!string.IsNullOrEmpty(_adminRole))   // CHANNEL-META-EXTRAS (3): name + role share the top row
                    senderW += (int)Math.Ceiling(g.MeasureString(_adminRole, TimeFont).Width) + P(10);
            }

            // The timestamp is painted inside the bubble, so the bubble must be wide
            // enough to hold it — otherwise short messages clip it.
            int timeW = MetaWidth(g);   // BUBBLE-DATETIME (B): stamp + ticks (outgoing) reserved so the meta never clips

            int replyW = 0;
            if (HasReply)
            {
                replyW = (int)Math.Ceiling(
                    g.MeasureString(ReplyPreview, ReplyFont, new SizeF(maxInner, ReplyH), sf).Width) + P(12);
                if (HasReplySender)
                    replyW = Math.Max(replyW, (int)Math.Ceiling(
                        g.MeasureString(ReplySender, ReplyFont, new SizeF(maxInner, ReplyNameH), sf).Width) + P(12));
            }

            int fwdW = HasForward
                ? (int)Math.Ceiling(g.MeasureString("Forwarded from " + ForwardedFrom, ReplyFont,
                    new SizeF(maxInner, FwdH), sf).Width) + P(4)
                : 0;

            int minInner = MinBubbleWidth - 2 * Pad;       // floor from min bubble width
            // COMMENTS-FIT-v2: reserve width for the comments footer. It paints at bubbleW (= contentW + 2*Pad), so
            // contentW must be >= CommentsFooterWidth - 2*Pad → fold it into the content-width max() (the failing case:
            // a short "Hi" post whose body/time width is far under "Leave a comment").
            int commentsW = HasComments ? Math.Max(0, CommentsFooterWidth(g) - 2 * Pad) : 0;
            int vicW = HasViewInChat ? Math.Max(0, ViewInChatWidth(g) - 2 * Pad) : 0;   // REPLIES-INBOX: "View in chat" row width
            int needed = Math.Max(Math.Max(Math.Max(Math.Max(Math.Max(Math.Max(longest, senderW), timeW), replyW), fwdW), commentsW), vicW);
            if (_hasCard) needed = Math.Max(needed, maxInner);   // link cards take the full content width
            contentW = Math.Min(maxInner, Math.Max(needed, minInner));

            if (_useRich)
            {
                _rich.Position(contentW, _rtl);
                textH = _rich.Height;
            }
            else
            {
                // Re-measure height at the final content width (+1px slack to avoid rewrap; a rounding guard, not a
                // length — it stays 1 at every DPI, step 3.2).
                var fit = g.MeasureString(_text, TextFont, new SizeF(contentW + 1, float.MaxValue), sf);
                textH = (int)Math.Ceiling(fit.Height);
            }

            _cardHeight = _hasCard ? MeasureCard(g, contentW) : 0;   // link-preview block under the text

            bubbleW = contentW + 2 * Pad;
            bubbleH = textH + (_cardHeight > 0 ? _cardHeight + CardTopGap : 0)
                    + (_sender != null ? SenderH : 0) + FwdBlockH + ReplyBlockH + 2 * Pad + TimeH;
            bubbleH = Math.Max(MinBubbleHeight, bubbleH);
        }

        // ── Interactive layer: polls + inline keyboards (shared row/cell render + hit-test) ──────────
        private int PollLabelH => P(16);
        private int PollOptRowH => P(44);
        private int PollVoteRowH => P(38);
        private int PollFooterH => P(20);
        private int PollQGap => P(6);
        private int PollOptGap => P(6);
        private int KbRowH => P(38);
        private int KbGap => P(5);
        private int KbTopGap => P(4);

        public enum KbKind { Callback, Url, SwitchInline, UrlAuth, Unsupported }
        public sealed class KbButtonVM { public string Label; public KbKind Kind; public byte[] Data; public string Url; public string Query; public bool SamePeer; }
        public sealed class PollOptionVM { public string Option; public string Text; public int Voters; public bool Chosen; public bool Correct; }

        private List<PollOptionVM> _pollOptions;
        private string _pollQuestion, _pollSolution;
        private int _pollTotal;
        private bool _pollClosed, _pollPublic, _pollMultiple, _pollQuiz, _pollResults;
        private readonly HashSet<string> _pollSelected = new HashSet<string>();          // multiple-choice pre-vote selection
        private readonly List<KeyValuePair<string, Rectangle>> _pollRects = new List<KeyValuePair<string, Rectangle>>();
        private Rectangle _pollVoteRect, _pollRetractRect;
        private bool IsPoll { get { return _pollOptions != null; } }

        private List<List<KbButtonVM>> _kbRows;
        private readonly List<KeyValuePair<KbButtonVM, Rectangle>> _kbRects = new List<KeyValuePair<KbButtonVM, Rectangle>>();
        private KbButtonVM _kbLoading;   // button awaiting a callback answer (spinner overlay)

        /// <summary>Single-choice vote (the option id), OR — on a voted public poll — a request to see that option's voters.</summary>
        public event Action<string> PollOptionTapped;
        public event Action<string[]> PollVoteSubmit;     // multiple-choice "Vote"
        public event Action PollRetract;
        public event Action<string> PollVotersRequested;  // public poll: "who voted for X"
        public event Action<KbButtonVM> KbButtonTapped;

        /// <summary>Configures this bubble as a poll (regular/anonymous/multiple/quiz). resultsVisible = bars shown.</summary>
        public void SetPoll(string question, List<PollOptionVM> options, int totalVoters,
                            bool closed, bool publicVoters, bool multiple, bool quiz, string solution, bool resultsVisible)
        {
            _pollQuestion = question; _pollOptions = options; _pollTotal = totalVoters;
            _pollClosed = closed; _pollPublic = publicVoters; _pollMultiple = multiple; _pollQuiz = quiz;
            _pollSolution = solution; _pollResults = resultsVisible;
            _pollSelected.Clear();
            Measure();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>Attaches an inline-keyboard grid (rows of buttons) under the message content.</summary>
        public void SetInlineKeyboard(List<List<KbButtonVM>> rows) { _kbRows = rows; Measure(); if (!IsDisposed) Invalidate(); }

        /// <summary>Shows/clears a spinner on a callback button while its answer is in flight (null = clear).</summary>
        public void SetKbLoading(KbButtonVM b) { _kbLoading = b; if (!IsDisposed) Invalidate(); }

        private bool AnyChosen() { if (_pollOptions == null) return false; foreach (var o in _pollOptions) if (o.Chosen) return true; return false; }
        private bool PollRetractable() { return _pollResults && !_pollClosed && !_pollQuiz && AnyChosen(); }

        private static int MeasureWrapped(string text, Font font, int w)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, w), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        private int KeyboardHeight()
        {
            return (_kbRows == null || _kbRows.Count == 0) ? 0 : KbTopGap + _kbRows.Count * (KbRowH + KbGap);
        }

        /// <summary>The SINGLE poll-content height (label + question + options + vote/retract + solution + footer)
        /// used by BOTH Measure and PaintPoll so painted and measured heights always agree.</summary>
        private int PollContentHeight(int contentW)
        {
            int h = PollLabelH + MeasureWrapped(_pollQuestion, SenderFont, contentW) + PollQGap;
            h += _pollOptions.Count * (PollOptRowH + PollOptGap);
            if (_pollMultiple && !_pollResults) h += PollVoteRowH + PollOptGap;
            if (PollRetractable()) h += PollVoteRowH;
            if (_pollQuiz && _pollResults && !string.IsNullOrEmpty(_pollSolution))
                h += MeasureWrapped(_pollSolution, TimeFont, contentW) + P(4);
            h += PollFooterH;
            return h;
        }

        private void PaintPoll(Graphics g)
        {
            int contentW = MaxInnerWidth;
            int senderH = _sender != null ? SenderH : 0;
            int bodyH = Math.Max(MinBubbleHeight, 2 * Pad + senderH + FwdBlockH + ReplyBlockH + PollContentHeight(contentW) + TimeH);
            int bubbleW = contentW + 2 * Pad;
            int bx = _outgoing ? Width - bubbleW - SideGap : SideGap + LeftInset;
            int by = VMargin;
            var rect = new Rectangle(bx, by, bubbleW, bodyH);
            _bodyRect = rect;

            Color bubbleColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOut : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
            Color fg = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : (ThemeHelper.PaletteFor(IsDark).BubbleInText);
            Color sub = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSub : (ThemeHelper.PaletteFor(IsDark).BubbleInSub);
            using (var b = new SolidBrush(bubbleColor))
            using (var path = DrawHelper.RoundedRect(rect, P(14)))
                g.FillPath(b, path);

            int innerX = bx + Pad, ty = by + Pad;
            if (_sender != null) { DrawSenderName(g, innerX, ty, contentW); ty += SenderH; }
            if (HasForward) { DrawForwardHeader(g, innerX, ty, contentW); ty += FwdH; }
            if (HasReply) { DrawReplyHeader(g, innerX, ty, contentW); ty += ReplyBlockH; }

            var tf = (_rtl ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.NoPrefix;

            string typeLabel = _pollClosed ? "Final results" : (_pollQuiz ? "Quiz" : (_pollPublic ? "Poll" : "Anonymous Poll"));
            TextRenderer.DrawText(g, typeLabel, TimeFont, new Rectangle(innerX, ty, contentW, PollLabelH), sub, tf);
            ty += PollLabelH;

            int qh = MeasureWrapped(_pollQuestion, SenderFont, contentW);
            TextRenderer.DrawText(g, _pollQuestion ?? "", SenderFont, new Rectangle(innerX, ty, contentW, qh), fg, tf | TextFormatFlags.WordBreak);
            ty += qh + PollQGap;

            _pollRects.Clear();
            foreach (var opt in _pollOptions)
            {
                var orect = new Rectangle(innerX, ty, contentW, PollOptRowH);
                _pollRects.Add(new KeyValuePair<string, Rectangle>(opt.Option, orect));
                DrawPollOption(g, orect, opt, fg, sub);
                ty += PollOptRowH + PollOptGap;
            }

            _pollVoteRect = Rectangle.Empty; _pollRetractRect = Rectangle.Empty;
            if (_pollMultiple && !_pollResults)
            {
                _pollVoteRect = new Rectangle(innerX, ty, contentW, PollVoteRowH);
                bool can = _pollSelected.Count > 0;
                DrawPillButton(g, _pollVoteRect, "Vote", can ? AccentColor : sub, ThemeHelper.PaletteFor(IsDark).OnAccent, !can);
                ty += PollVoteRowH + PollOptGap;
            }
            else if (PollRetractable())
            {
                _pollRetractRect = new Rectangle(innerX, ty, contentW, PollVoteRowH);
                TextRenderer.DrawText(g, "Retract vote", TimeFont, _pollRetractRect, _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ThemeHelper.PaletteFor(IsDark).LinkIn,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                ty += PollVoteRowH;
            }

            if (_pollQuiz && _pollResults && !string.IsNullOrEmpty(_pollSolution))
            {
                int sh = MeasureWrapped(_pollSolution, TimeFont, contentW);
                TextRenderer.DrawText(g, _pollSolution, TimeFont, new Rectangle(innerX, ty, contentW, sh), sub, tf | TextFormatFlags.WordBreak);
                ty += sh + P(4);
            }

            string footer = _pollTotal <= 0 ? "No votes yet"
                : (_pollTotal + (_pollMultiple ? (_pollTotal == 1 ? " answer" : " answers") : (_pollTotal == 1 ? " voter" : " voters")));
            TextRenderer.DrawText(g, footer, TimeFont, new Rectangle(innerX, ty, contentW, PollFooterH), sub, tf);

            Color timeColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutTime : (ThemeHelper.PaletteFor(IsDark).BubbleInTime);
            using (var timeSf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far })
            using (var tcb = new SolidBrush(timeColor))
                g.DrawString(StampText(), TimeFont, tcb, new RectangleF(bx + Pad, by + bodyH - TimeH, bubbleW - 2 * Pad, TimeH), timeSf);
            DrawStatusGlyph(g, bx + Pad + P(6), by + bodyH - TimeH / 2f, timeColor);
            DrawMetaExtras(g, new RectangleF(bx + Pad, by + bodyH - TimeH, bubbleW - 2 * Pad, TimeH), timeColor);

            DrawFooter(g, bx, by + bodyH + P(2), bubbleW);
        }

        private void DrawPollOption(Graphics g, Rectangle rect, PollOptionVM opt, Color fg, Color sub)
        {
            int pct = (_pollResults && _pollTotal > 0) ? (int)Math.Round(opt.Voters * 100.0 / _pollTotal) : 0;
            using (var bg = new SolidBrush(_outgoing ? ThemeHelper.PaletteFor(IsDark).PollOptionBgOut : ThemeHelper.PaletteFor(IsDark).PollOptionBgIn))
            using (var p = DrawHelper.RoundedRect(rect, P(9)))
                g.FillPath(bg, p);

            if (_pollResults)   // proportional result bar
            {
                int barW = Math.Max(0, (int)(rect.Width * Math.Min(1f, pct / 100f)));
                if (barW > P(4))
                {
                    Color barCol = opt.Correct ? ColorMath.WithAlpha(ThemeHelper.PaletteFor(IsDark).Success, 120) : ColorMath.WithAlpha(AccentColor, 110);
                    var br = new Rectangle(rect.X, rect.Y, barW, rect.Height);
                    using (var bb = new SolidBrush(barCol))
                    using (var bp = DrawHelper.RoundedRect(br, P(9)))
                        g.FillPath(bb, bp);
                }
            }

            var tf = (_rtl ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int lx = rect.X + P(12), lw = rect.Width - P(24);
            if (_pollResults) { lw -= P(44); }                               // room for the % on the right
            else if (_pollMultiple) { lx += P(26); lw -= P(26); }            // room for the checkbox on the left

            if (!_pollResults && _pollMultiple)   // pre-vote checkbox
            {
                int cbD = P(18);
                var cb = new Rectangle(rect.X + P(12), rect.Y + (rect.Height - cbD) / 2, cbD, cbD);
                bool sel = _pollSelected.Contains(opt.Option);
                using (var pen = new Pen(sel ? (_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0)) : sub, Dpi.F(1.8f, CurDpi))) g.DrawEllipse(pen, cb);
                if (sel) using (var fb = new SolidBrush((_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0)))) g.FillEllipse(fb, Rectangle.Inflate(cb, -P(4), -P(4)));
            }

            TextRenderer.DrawText(g, opt.Text ?? "", TextFont, new Rectangle(lx, rect.Y, lw, rect.Height), fg, tf);

            if (_pollResults)
            {
                float k = Dpi.F(1f, CurDpi);
                TextRenderer.DrawText(g, pct + "%", TimeFont, new Rectangle(rect.Right - P(44), rect.Y, P(38), rect.Height), fg,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (_pollQuiz && opt.Correct) DrawTick(g, rect.Right - P(60), rect.Y + rect.Height / 2, ThemeHelper.PaletteFor(IsDark).Success, true, k);
                else if (_pollQuiz && opt.Chosen && !opt.Correct) DrawTick(g, rect.Right - P(60), rect.Y + rect.Height / 2, ThemeHelper.PaletteFor(IsDark).AlertRed, false, k);
                else if (opt.Chosen) DrawTick(g, rect.Right - P(60), rect.Y + rect.Height / 2, _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0), true, k);
            }
        }

        /// <summary>Drawn at 96-DPI sizes; <paramref name="k"/> = dpi / 96 scales it.</summary>
        private static void DrawTick(Graphics g, int cx, int cy, Color c, bool check, float k)
        {
            using (var pen = new Pen(c, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (check) { g.DrawLine(pen, cx - 5 * k, cy, cx - 1 * k, cy + 4 * k); g.DrawLine(pen, cx - 1 * k, cy + 4 * k, cx + 6 * k, cy - 4 * k); }
                else { g.DrawLine(pen, cx - 5 * k, cy - 5 * k, cx + 5 * k, cy + 5 * k); g.DrawLine(pen, cx + 5 * k, cy - 5 * k, cx - 5 * k, cy + 5 * k); }
            }
        }

        private void DrawPillButton(Graphics g, Rectangle rect, string label, Color fill, Color text, bool dim)
        {
            using (var b = new SolidBrush(dim ? ColorMath.WithAlpha(fill, 90) : fill))
            using (var p = DrawHelper.RoundedRect(rect, P(9)))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, label, SenderFont, rect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Reactions strip + inline keyboard grid below the bubble body (single footer for every paint path).
        private void DrawFooter(Graphics g, int bx, int yBelowBubble, int bubbleW)
        {
            DrawReactions(g, bx, yBelowBubble, Width - SideGap - bx);
            int ky = yBelowBubble + (HasReactions ? ReactionH : 0) + KbTopGap;
            DrawInlineKeyboard(g, bx, ky, bubbleW);
            int cy = yBelowBubble + (HasReactions ? ReactionH : 0) + KeyboardHeight();   // bottom-most strip
            DrawCommentsFooter(g, bx, cy, bubbleW);
            DrawViewInChatRow(g, bx, cy + (HasComments ? CommentH : 0), bubbleW);   // REPLIES-INBOX: below any comments strip
        }

        /// <summary>REPLIES-INBOX: the bubble width the "View in chat" row needs so its label + chevron don't clip
        /// (same font + paddings the row paints with). Guarantee: bubbleW >= this.</summary>
        private int ViewInChatWidth(Graphics g)
        {
            if (!HasViewInChat) return 0;
            int textW = TextRenderer.MeasureText(g, "View in chat", SenderFont).Width;
            return CmtPadL + textW + CmtGap + P(12) + CmtPadR + P(2);   // pad + text + gap + chevron + pad + slack
        }

        /// <summary>REPLIES-INBOX: a full-width bottom row "View in chat ›" (accent, subtle top separator, chevron on
        /// the trailing side). RTL-aware (chevron on the left, text right-aligned). Records _viewInChatRect for the tap.</summary>
        private void DrawViewInChatRow(Graphics g, int bx, int y, int bubbleW)
        {
            _viewInChatRect = Rectangle.Empty;
            if (!HasViewInChat) return;
            int bandW = Math.Max(bubbleW, Math.Min(ViewInChatWidth(g), Width - SideGap - bx));
            var band = new Rectangle(bx, y, bandW, ViewInChatH);
            _viewInChatRect = band;
            Color sep = ThemeHelper.PaletteFor(IsDark).BubbleBandSeparator;
            using (var sp = new Pen(sep)) g.DrawLine(sp, band.Left + P(2), band.Top, band.Right - P(2), band.Top);   // hairline: stays 1 px
            const string label = "View in chat";
            int chevW = P(12);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            if (_rtl)
            {
                var chevRect = new Rectangle(band.Left + CmtPadL, band.Top, chevW, ViewInChatH);
                DrawChevron(g, chevRect, ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0), true);
                var textRect = new Rectangle(chevRect.Right + CmtGap, band.Top,
                    band.Right - CmtPadR - (chevRect.Right + CmtGap), ViewInChatH);
                TextRenderer.DrawText(g, label, SenderFont, textRect, ThemeHelper.PaletteFor(IsDark).LinkIn, flags | TextFormatFlags.Right);
            }
            else
            {
                var chevRect = new Rectangle(band.Right - CmtPadR - chevW, band.Top, chevW, ViewInChatH);
                DrawChevron(g, chevRect, ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0), false);
                var textRect = new Rectangle(band.Left + CmtPadL, band.Top,
                    chevRect.Left - CmtGap - (band.Left + CmtPadL), ViewInChatH);
                TextRenderer.DrawText(g, label, SenderFont, textRect, ThemeHelper.PaletteFor(IsDark).LinkIn, flags | TextFormatFlags.Left);
            }
        }

        /// <summary>A small chevron (› LTR / ‹ RTL) drawn as two strokes — no font glyph (RT-safe). Drawn for a 12 px
        /// wide box; scales with it (integer points, as before, so 96 DPI is unchanged).</summary>
        private static void DrawChevron(Graphics g, Rectangle r, Color c, bool pointLeft)
        {
            float k = r.Width / 12f;
            int S(int v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);
            int i3 = S(3), i5 = S(5), i6 = S(6);
            int cy = r.Y + r.Height / 2;
            int cx = pointLeft ? r.Right - i3 : r.Left + i3;
            int dx = pointLeft ? -i6 : i6;
            using (var pen = new Pen(c, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new Point(cx, cy - i5), new Point(cx + dx, cy), new Point(cx, cy + i5) });
        }

        // COMMENTS-INDICATOR: "N comments / Leave a comment" strip under a broadcast post — GDI+ speech-bubble glyph
        // (never an emoji-font codepoint — the cross-OS rule), accent-colored, subtle top separator, RTL-aware.
        // Records _commentsRect for the tap target; resets it to empty when the footer is absent.
        // COMMENTS-FIT-v2: ONE shared source for the footer's paint metrics + label, so the width RESERVED in
        // ComputeLayout and the width PAINTED here can never diverge (the #1 cause of "reserved but still clips").
        private int CmtGlyphW => P(16);
        private int CmtPadL => P(6);
        private int CmtGap => P(6);
        private int CmtPadR => P(6);
        private string CommentLabel() => _commentCount > 0 ? (_commentCount == 1 ? "1 comment" : _commentCount + " comments") : "Leave a comment";

        /// <summary>The bubble width the comments footer needs to paint its label without clipping — measured with the
        /// SAME font (SenderFont), glyph width and paddings DrawCommentsFooter paints with. Guarantee: bubbleW >= this.</summary>
        private int CommentsFooterWidth(Graphics g)
        {
            if (!HasComments) return 0;
            int textW = TextRenderer.MeasureText(g, CommentLabel(), SenderFont).Width;   // default padding ≈ DrawText's
            return CmtPadL + CmtGlyphW + CmtGap + textW + CmtPadR + P(2);   // +2 slack vs measure/paint padding
        }

        private void DrawCommentsFooter(Graphics g, int bx, int y, int bubbleW)
        {
            _commentsRect = Rectangle.Empty;
            if (!HasComments) return;
            // The bubble was widened to fit the footer in ComputeLayout, so for text bubbles bandW == bubbleW; for
            // media bubbles (fixed media width) this grows the band to fit the label — clip safety-net, panel-clamped.
            int bandW = Math.Max(bubbleW, Math.Min(CommentsFooterWidth(g), Width - SideGap - bx));
            var band = new Rectangle(bx, y, bandW, CommentH);
            _commentsRect = band;
            Color sep = ThemeHelper.PaletteFor(IsDark).BubbleBandSeparator;
            using (var sp = new Pen(sep)) g.DrawLine(sp, band.Left + P(2), band.Top, band.Right - P(2), band.Top);   // hairline: stays 1 px
            string label = CommentLabel();
            int glyphH = P(13);
            var gRect = new Rectangle(band.Left, band.Top + (CommentH - glyphH) / 2, CmtGlyphW, glyphH);   // X is set below
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            Rectangle textRect;
            if (_rtl)
            {
                gRect.X = band.Right - CmtPadR - gRect.Width;
                textRect = new Rectangle(band.Left + CmtPadL, band.Top, gRect.Left - CmtGap - (band.Left + CmtPadL), CommentH);
                flags |= TextFormatFlags.Right;
            }
            else
            {
                gRect.X = band.Left + CmtPadL;
                textRect = new Rectangle(gRect.Right + CmtGap, band.Top, band.Right - CmtPadR - (gRect.Right + CmtGap), CommentH);
                flags |= TextFormatFlags.Left;
            }
            DrawCommentGlyph(g, gRect, ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0));
            TextRenderer.DrawText(g, label, SenderFont, textRect, ThemeHelper.PaletteFor(IsDark).LinkIn, flags);
        }

        // A small speech-bubble glyph, GDI+-drawn (cross-OS: never an emoji-font codepoint). Drawn for a 16 x 13 box;
        // scales with it (integer offsets, as before, so 96 DPI is unchanged).
        private void DrawCommentGlyph(Graphics g, Rectangle r, Color c)
        {
            float k = r.Width / 16f;
            int S(int v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);
            int i3 = S(3), i4 = S(4), i8 = S(8);
            var body = new Rectangle(r.X, r.Y, r.Width, r.Height - i3);
            using (var p = new Pen(c, 1.4f * k))
            using (var path = DrawHelper.RoundedRect(body, i3))
            {
                g.DrawPath(p, path);
                g.DrawLine(p, r.X + i4, body.Bottom - 1, r.X + i4, r.Bottom);   // little tail
                g.DrawLine(p, r.X + i4, r.Bottom, r.X + i8, body.Bottom - 1);
            }
        }

        private void DrawInlineKeyboard(Graphics g, int bx, int yTop, int bubbleW)
        {
            _kbRects.Clear();
            if (_kbRows == null || _kbRows.Count == 0) return;
            Color cell = ThemeHelper.PaletteFor(IsDark).BubbleKbCell;
            Color text = ThemeHelper.PaletteFor(IsDark).BubbleKbText;
            int y = yTop;
            foreach (var row in _kbRows)
            {
                int n = Math.Max(1, row.Count);
                int cw = (bubbleW - (n - 1) * KbGap) / n;
                for (int i = 0; i < row.Count; i++)
                {
                    int cx = bx + i * (cw + KbGap);
                    var r = new Rectangle(cx, y, cw, KbRowH);
                    _kbRects.Add(new KeyValuePair<KbButtonVM, Rectangle>(row[i], r));
                    using (var b = new SolidBrush(cell))
                    using (var p = DrawHelper.RoundedRect(r, P(8)))
                        g.FillPath(b, p);
                    if (_kbLoading == row[i])
                        DrawHelper.DrawProgressRing(g, new Rectangle(r.X + r.Width / 2 - P(9), r.Y + (KbRowH - P(18)) / 2, P(18), P(18)), 0f, text, ColorMath.WithAlpha(text, 90), false);
                    else
                    {
                        // Per-button script-aware font (Vazirmatn for Persian) + inline Noto emoji, centered —
                        // button labels have their own script + often carry emoji. Shared font: never dispose.
                        string label = row[i].Label ?? "";
                        EmojiRenderer.DrawLineCentered(g, label, FontHelper.ForAt(label, 9.5f, CurDpi), text, r);
                    }
                }
                y += KbRowH + KbGap;
            }
        }

        // Poll/keyboard hit-test (called from OnMouseClick). Returns true when the tap was consumed.
        private bool HandleInteractiveClick(Point loc)
        {
            foreach (var kv in _kbRects)
                if (kv.Value.Contains(loc)) { KbButtonTapped?.Invoke(kv.Key); return true; }
            if (!IsPoll) return false;

            if (!_pollVoteRect.IsEmpty && _pollVoteRect.Contains(loc))
            {
                if (_pollSelected.Count > 0) PollVoteSubmit?.Invoke(new List<string>(_pollSelected).ToArray());
                return true;
            }
            if (!_pollRetractRect.IsEmpty && _pollRetractRect.Contains(loc)) { PollRetract?.Invoke(); return true; }

            foreach (var kv in _pollRects)
                if (kv.Value.Contains(loc))
                {
                    if (_pollResults)
                    {
                        if (_pollPublic) PollVotersRequested?.Invoke(kv.Key);   // who voted for X
                    }
                    else if (_pollMultiple)
                    {
                        if (!_pollSelected.Remove(kv.Key)) _pollSelected.Add(kv.Key);   // toggle
                        Invalidate();
                    }
                    else PollOptionTapped?.Invoke(kv.Key);   // single-choice vote
                    return true;
                }
            return IsPoll;   // a poll bubble swallows stray taps inside it
        }

        /// <summary>Recomputes Height for the current Width. Call after Width changes.</summary>
        public void Measure()
        {
            int reactH = HasReactions ? ReactionH : 0;
            int kbH = KeyboardHeight();
            int commentsH = HasComments ? CommentH : 0;   // COMMENTS-INDICATOR: bottom-most footer strip
            int viewInChatH = HasViewInChat ? ViewInChatH : 0;   // REPLIES-INBOX: "View in chat" row (below comments)

            if (IsPoll)
            {
                int contentW = MaxInnerWidth;
                int senderH = _sender != null ? SenderH : 0;
                int h = 2 * Pad + senderH + FwdBlockH + ReplyBlockH + PollContentHeight(contentW) + TimeH;
                Height = Math.Max(MinBubbleHeight, h) + reactH + kbH + commentsH + viewInChatH + 2 * VMargin;
                return;
            }

            if (IsAlbum)
            {
                int contentW = MaxInnerWidth;
                int senderH = _sender != null ? SenderH : 0;
                int h = 2 * Pad + senderH + FwdBlockH + ReplyBlockH + AlbumContentHeight(contentW)
                        + AlbumCaptionHeight(contentW) + TimeH;
                Height = Math.Max(MinBubbleHeight, h) + reactH + kbH + commentsH + viewInChatH + 2 * VMargin;
                return;
            }
            if (IsFile)
            {
                Height = FileCardH + FwdBlockH + ReplyBlockH + reactH + kbH + commentsH + viewInChatH + 2 * VMargin;
                return;
            }
            if (HasPhoto)
            {
                GetMediaLayout(out _, out _, out int dispH);
                int captionH = MeasureCaption();
                int senderH = _sender != null ? SenderH : 0;
                int h = 2 * Pad + senderH + FwdBlockH + ReplyBlockH + dispH + captionH + TimeH;
                Height = Math.Max(MinBubbleHeight, h) + reactH + kbH + commentsH + viewInChatH + 2 * VMargin;
                return;
            }

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            using (var sf = MakeFormat())
            {
                ComputeLayout(g, sf, out _, out _, out _, out int bubbleH);
                Height = bubbleH + reactH + kbH + commentsH + viewInChatH + 2 * VMargin;
            }
        }

        /// <summary>Caption height (text shown under a photo); 0 when there is no caption.</summary>
        private int MeasureCaption()
        {
            if (_text.Length == 0) return 0;
            GetMediaLayout(out int contentW, out _, out _);   // caption wraps at the bubble width, NOT the media width
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            using (var sf = MakeFormat())
            {
                if (_useRich)
                {
                    _rich.Measure(g, contentW, TextFont);
                    _rich.Position(contentW, _rtl);
                    return _rich.Height + P(4);
                }
                var sz = g.MeasureString(_text, TextFont, new SizeF(contentW, float.MaxValue), sf);
                return (int)Math.Ceiling(sz.Height) + P(4);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            long __t = PerfLog.T();
            OnPaintCore(e);
            PerfLog.Rec(PerfLog.P.BubblePaint, __t);
        }

        private void OnPaintCore(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Color basePanelBg = ThemeHelper.PaletteFor(IsDark).ChatBg;
            Color panelBg = (SelectionMode && Selected) ? Blend(AccentColor, basePanelBg, 0.18f) : basePanelBg;
            ChatBackdrop.PaintRow(g, this, e.ClipRectangle, IsDark, panelBg, (SelectionMode && Selected) ? AccentColor : (Color?)null);   // step 5.4
            DrawSenderAvatar(g);

            if (IsPoll) { PaintPoll(g); DrawSelectionCheck(g); return; }
            if (IsAlbum) { PaintAlbum(g); DrawSelectionCheck(g); return; }
            if (IsFile) { PaintFile(g); DrawSelectionCheck(g); return; }
            if (HasPhoto) { PaintPhoto(g, panelBg); DrawSelectionCheck(g); return; }

            using (var sf = MakeFormat())
            {
                ComputeLayout(g, sf, out int contentW, out int textH, out int bubbleW, out int bubbleH);

                int bx = _outgoing ? Width - bubbleW - SideGap : SideGap + LeftInset;
                int by = VMargin;
                var rect = new Rectangle(bx, by, bubbleW, bubbleH);
                _bodyRect = rect;

                Color bubbleColor = _outgoing
                    ? ThemeHelper.PaletteFor(IsDark).BubbleOut
                    : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
                Color textColor = _outgoing
                    ? ThemeHelper.PaletteFor(IsDark).BubbleOutText
                    : (ThemeHelper.PaletteFor(IsDark).BubbleInText);

                using (var b = new SolidBrush(bubbleColor))
                using (var path = DrawHelper.RoundedRect(rect, P(14)))
                    g.FillPath(b, path);

                int innerX = bx + Pad;
                int ty = by + Pad;

                if (_sender != null)
                {
                    DrawSenderName(g, innerX, ty, contentW);
                    ty += SenderH;
                }

                if (HasForward) { DrawForwardHeader(g, innerX, ty, contentW); ty += FwdH; }

                if (HasReply) { DrawReplyHeader(g, innerX, ty, contentW); ty += ReplyBlockH; }

                if (_useRich)
                {
                    _richOrigin = new Point(innerX, ty);
                    _rich.PaintCached(g, innerX, ty, textColor, LinkColor, TextFont, IsDark, AccentColor, _outgoing, bubbleColor);
                    PaintSelection(g, innerX, ty);   // translucent highlight OVER the opaque text bitmap
                }
                else
                {
                    using (var tb = new SolidBrush(textColor))
                        g.DrawString(_text, TextFont, tb,
                            new RectangleF(innerX, ty, contentW, textH), sf);
                }

                if (_hasCard) PaintCard(g, innerX, ty + textH + CardTopGap, contentW);

                Color timeColor = _outgoing
                    ? ThemeHelper.PaletteFor(IsDark).BubbleOutTime
                    : (ThemeHelper.PaletteFor(IsDark).BubbleInTime);
                using (var timeSf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far })
                using (var tcb = new SolidBrush(timeColor))
                    g.DrawString(StampText(), TimeFont, tcb,
                        new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeSf);
                DrawStatusGlyph(g, bx + Pad + P(6), by + bubbleH - TimeH / 2f, timeColor);
                DrawMetaExtras(g, new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeColor);

                DrawFooter(g, bx, by + bubbleH + P(2), bubbleW);
            }

            DrawSelectionCheck(g);
        }

        /// <summary>In selection mode, draws a check circle (filled accent when selected) in the margin.</summary>
        /// <summary>Draws the group sender's avatar (photo, or a colored letter) bottom-left.</summary>
        private void DrawSenderAvatar(Graphics g)
        {
            if (_outgoing || !ShowAvatar) return;
            var ar = AvatarRect;
            if (SenderAvatar != null)
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(ar);
                    var old = g.Clip;
                    g.SetClip(clip);
                    g.DrawImage(SenderAvatar, ar);
                    g.Clip = old;
                }
            }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(AvatarPeerId)))
                    g.FillEllipse(b, ar);
                string letter = string.IsNullOrEmpty(_sender) ? "?" : DrawHelper.Initial(_sender);
                DrawHelper.DrawInitial(g, letter, FontHelper.ForAt(_sender ?? "", 13f, CurDpi, FontStyle.Bold), ar, Color.White);   // ink-centred (owner report 2026-10-14)   // shared font: never dispose
            }
        }

        private void DrawSelectionCheck(Graphics g)
        {
            if (_flashTicks > 0)
                using (var fb = new SolidBrush(ColorMath.WithAlpha(AccentColor, 70)))
                    g.FillRectangle(fb, ClientRectangle);   // brief "jump to pinned" pulse (all paint paths)
            if (!SelectionMode) return;
            int d = P(18);
            float k = d / 18f;   // the check is drawn for the 18 px circle; scales with it
            int x = _outgoing ? SideGap + P(2) : Width - SideGap - d - P(2);
            int y = (Height - d) / 2;
            var circle = new Rectangle(x, y, d, d);
            if (Selected)
            {
                using (var b = new SolidBrush(AccentColor))
                    g.FillEllipse(b, circle);
                using (var pen = new Pen(ThemeHelper.PaletteFor(IsDark).OnAccent, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x + 4.5f * k, y + 9f * k), new PointF(x + 7.5f * k, y + 12f * k), new PointF(x + 13.5f * k, y + 6f * k)
                    });
            }
            else
            {
                using (var pen = new Pen(ThemeHelper.PaletteFor(IsDark).BubbleSelectRing, 1.6f * k))
                    g.DrawEllipse(pen, circle);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return ColorMath.Rgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }

        private void PaintFile(Graphics g)
        {
            int cardW = Math.Min(FileCardW, MaxInnerWidth + 2 * Pad);   // policy-capped so it fits a narrow pane
            int bx = _outgoing ? Width - cardW - SideGap : SideGap + LeftInset;
            int by = VMargin;
            int fwdOff = FwdBlockH;
            int replyOff = ReplyBlockH;
            var rect = new Rectangle(bx, by + fwdOff + replyOff, cardW, FileCardH);
            _fileRect = rect;

            Color bubbleColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOut : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
            Color textColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : (ThemeHelper.PaletteFor(IsDark).BubbleInText);
            Color subColor = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSubStrong : (ThemeHelper.PaletteFor(IsDark).BubbleInSub);

            if (HasForward) DrawForwardHeader(g, bx, by, cardW);
            if (HasReply) DrawReplyHeader(g, bx, by + fwdOff, cardW);

            using (var b = new SolidBrush(bubbleColor))
            using (var path = DrawHelper.RoundedRect(rect, P(12)))
                g.FillPath(b, path);

            bool dlActive = _fileHandle != null && _fileHandle.State == DownloadHandle.DState.Downloading;
            var iconRect = new Rectangle(bx + P(12), rect.Y + P(12), P(48), P(48));
            if (dlActive)   // progress ring + ✕ replaces the file icon while downloading
                DrawHelper.DrawProgressRing(g, iconRect, _fileHandle.Fraction, textColor, ColorMath.WithAlpha(subColor, 90), cancel: true);
            else
                DrawHelper.DrawFileIcon(g, iconRect, FileName);

            int tx = iconRect.Right + P(12);
            int tw = bx + cardW - P(12) - tx;
            TextRenderer.DrawText(g, FileName ?? "File", SenderFont, new Rectangle(tx, rect.Y + P(14), tw, P(22)), textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string sizeLine = dlActive
                ? DrawHelper.FormatSize(_fileHandle.Transmitted) + " / " + DrawHelper.FormatSize(_fileHandle.Total)
                : (FileSizeText ?? "");
            TextRenderer.DrawText(g, sizeLine, TimeFont, new Rectangle(tx, rect.Y + P(40), tw, P(18)), subColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            DrawStatusGlyph(g, bx + cardW - P(14), rect.Bottom - P(12), subColor);

            DrawFooter(g, bx, rect.Bottom + P(2), rect.Width);
        }

        private void PaintPhoto(Graphics g, Color panelBg)
        {
            GetMediaLayout(out int contentW, out int dispW, out int dispH);
            int senderH = _sender != null ? SenderH : 0;
            int fwdHh = FwdBlockH;
            int replyHh = ReplyBlockH;
            int captionH = MeasureCaption();
            int bubbleW = contentW + 2 * Pad;   // bubble = policy width; media is centered within it
            int bubbleH = Math.Max(MinBubbleHeight, 2 * Pad + senderH + fwdHh + replyHh + dispH + captionH + TimeH);

            int bx = _outgoing ? Width - bubbleW - SideGap : SideGap + LeftInset;
            int by = VMargin;
            var rect = new Rectangle(bx, by, bubbleW, bubbleH);

            Color bubbleColor = _outgoing
                ? ThemeHelper.PaletteFor(IsDark).BubbleOut
                : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
            Color textColor = _outgoing
                ? ThemeHelper.PaletteFor(IsDark).BubbleOutText
                : (ThemeHelper.PaletteFor(IsDark).BubbleInText);

            if (!IsSticker && !IsRoundVideo)   // stickers + round video notes have no bubble background
                using (var b = new SolidBrush(bubbleColor))
                using (var path = DrawHelper.RoundedRect(rect, P(14)))
                    g.FillPath(b, path);

            int innerX = bx + Pad;
            int ty = by + Pad;

            using (var sf = MakeFormat())
            {
                if (_sender != null)
                {
                    DrawSenderName(g, innerX, ty, contentW);
                    ty += SenderH;
                }

                if (HasForward) { DrawForwardHeader(g, innerX, ty, contentW); ty += FwdH; }

                if (HasReply) { DrawReplyHeader(g, innerX, ty, contentW); ty += ReplyBlockH; }

                int imgX = innerX + (contentW - dispW) / 2;   // center media within the policy-width bubble
                _imageRect = new Rectangle(imgX, ty, dispW, dispH);
                // Round video notes: mask the (square) thumbnail to a circle while painting the image.
                System.Drawing.Drawing2D.GraphicsPath roundClip = IsRoundVideo ? CirclePath(_imageRect) : null;
                System.Drawing.Region savedClip = null;
                if (roundClip != null) { savedClip = g.Clip; g.SetClip(roundClip); }
                switch (_photoState)
                {
                    case PhotoState.Loaded when _image != null:
                        DrawFittedImage(g, _image, _imageRect, P(8), cover: !IsSticker);   // photos/videos fill+crop; stickers fit
                        break;
                    case PhotoState.Loading:
                        FillImageArea(g, _imageRect);
                        if (!IsVideoThumb) DrawSpinner(g, _imageRect);
                        break;
                    default: // Placeholder
                        FillImageArea(g, _imageRect);
                        if (!IsVideoThumb)
                            TextRenderer.DrawText(g, "📷  Photo (tap to download)", TextFont, _imageRect,
                                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.WordBreak);
                        break;
                }

                if (roundClip != null) { g.Clip = savedClip; savedClip.Dispose(); roundClip.Dispose(); }

                // Video/gif overlays (play button, duration, GIF badge) on top of the thumbnail — hidden while
                // an inline GIF is actually playing. While a MANAGED transfer owns the thumb, the overlay is
                // the transfer ring instead (downloading = live ring; paused = frozen ring + play glyph).
                if (IsVideoThumb && !Animating)
                {
                    bool vDl = _fileHandle != null && _fileHandle.State == DownloadHandle.DState.Downloading;
                    bool vPz = _fileHandle != null && _fileHandle.State == DownloadHandle.DState.Paused;
                    if (vDl || vPz)
                    {
                        int rd = P(46);
                        var ring = new Rectangle(_imageRect.X + (_imageRect.Width - rd) / 2,
                                                 _imageRect.Y + (_imageRect.Height - rd) / 2, rd, rd);
                        using (var dim = new SolidBrush(ThemeHelper.PaletteFor(IsDark).MediaDim)) g.FillEllipse(dim, ring);
                        DrawHelper.DrawProgressRing(g, ring, _fileHandle.Fraction, Color.White, ThemeHelper.PaletteFor(IsDark).MediaRingTrack, cancel: vDl);
                        if (vPz)
                        {
                            float k = rd / 46f;   // drawn for the 46 px ring; scales with it
                            float pcx = ring.X + ring.Width / 2f, pcy = ring.Y + ring.Height / 2f;
                            using (var wb = new SolidBrush(Color.White))
                                g.FillPolygon(wb, new[] { new PointF(pcx - 5 * k, pcy - 7 * k), new PointF(pcx + 8 * k, pcy), new PointF(pcx - 5 * k, pcy + 7 * k) });
                        }
                    }
                    else DrawVideoOverlay(g, _imageRect);
                }
                if (IsGif && !Animating) DrawGifBadge(g, _imageRect);
                // WebM stickers (IsSticker, no video overlay) get a SUBTLE centered "tap to play" triangle when
                // static — so the user knows the thumbnail is playable. Hidden while playing; GIFs already have
                // the video overlay above, so this is webm-sticker-only.
                if (IsInlineVideo && IsSticker && !Animating && _photoState == PhotoState.Loaded)
                    DrawStickerPlayHint(g, _imageRect);

                ty += dispH;

                if (captionH > 0)
                {
                    if (_useRich)
                    {
                        _richOrigin = new Point(innerX, ty);
                        _rich.PaintCached(g, innerX, ty, textColor, LinkColor, TextFont, IsDark, AccentColor, _outgoing, bubbleColor);
                        PaintSelection(g, innerX, ty);   // translucent highlight OVER the opaque text bitmap
                    }
                    else
                    {
                        using (var tb = new SolidBrush(textColor))
                            g.DrawString(_text, TextFont, tb, new RectangleF(innerX, ty, contentW, captionH - P(4)), sf);
                    }
                }
            }

            Color timeColor = _outgoing
                ? ThemeHelper.PaletteFor(IsDark).BubbleOutTime
                : (ThemeHelper.PaletteFor(IsDark).BubbleInTime);
            using (var timeSf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far })
            using (var tcb = new SolidBrush(timeColor))
                g.DrawString(StampText(), TimeFont, tcb,
                    new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeSf);
            DrawStatusGlyph(g, bx + Pad + P(6), by + bubbleH - TimeH / 2f, timeColor);
            DrawMetaExtras(g, new RectangleF(bx + Pad, by + bubbleH - TimeH, bubbleW - 2 * Pad, TimeH), timeColor);

            DrawFooter(g, bx, by + bubbleH + P(2), bubbleW);
        }

        /// <summary>
        /// Draws the group sender's name (incoming only). Uses the NAME's own script font via
        /// FontHelper.For + TextRenderer (which does font fallback) — GDI+ DrawString with the
        /// message-body font would render a Persian name as blank when the body is Latin/media.
        /// </summary>
        private void DrawSenderName(Graphics g, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(_sender)) return;
            Color c = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : SenderNameColor;
            bool persianName = FontHelper.IsPersian(_sender);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                        | (persianName ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(g, _sender, FontHelper.ForAt(_sender, persianName ? 10.5f : 9f, CurDpi, FontStyle.Bold),
                new Rectangle(x, y, w, SenderH), c, flags);   // shared font: never dispose
            _senderRect = new Rectangle(x, y, w, SenderH);
            // CHANNEL-META-EXTRAS (3): group admin/owner/custom-rank label, right-aligned on the sender row.
            if (!string.IsNullOrEmpty(_adminRole))
            {
                Color rc = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutRole
                                     : ThemeHelper.PaletteFor(IsDark).BubbleInRole;
                bool prole = FontHelper.IsPersian(_adminRole);
                TextRenderer.DrawText(g, _adminRole, FontHelper.ForAt(_adminRole, prole ? 9f : 8f, CurDpi, FontStyle.Regular),
                    new Rectangle(x, y, w, SenderH), rc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>CHANNEL-META-EXTRAS (1+2): view count (eye + compact number) just LEFT of the right-aligned stamp,
        /// and the post_author byline far-left, on the same meta row. Widths are reserved by MetaWidth so nothing clips.</summary>
        private void DrawMetaExtras(Graphics g, RectangleF metaRect, Color color)
        {
            if (_views < 0 && string.IsNullOrEmpty(_postAuthor)) return;
            float stampW = g.MeasureString(StampText(), TimeFont).Width;
            float x = metaRect.Right - stampW;   // left edge of the right-aligned stamp
            using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Far })
            using (var b = new SolidBrush(color))
            {
                if (_views >= 0)
                {
                    string vc = FormatViews(_views);
                    float vcW = g.MeasureString(vc, TimeFont).Width;
                    x -= MetaGap + vcW;
                    g.DrawString(vc, TimeFont, b, new RectangleF(x, metaRect.Y, vcW + P(4), metaRect.Height), sf);
                    DrawEye(g, x - EyeGap - EyeW, metaRect.Bottom - TimeH / 2f, color, EyeW);
                    x -= EyeGap + EyeW;
                }
                if (!string.IsNullOrEmpty(_postAuthor))
                {
                    float availW = Math.Max(0f, x - metaRect.Left - MetaGap);
                    if (availW > Dpi.F(8f, CurDpi))
                        g.DrawString(_postAuthor, TimeFont, b,
                            new RectangleF(metaRect.Left, metaRect.Y, availW, metaRect.Height), sf);
                }
            }
        }

        /// <summary>A small owner-drawn eye (outline almond + pupil) — deterministic, no emoji-font dependency. Drawn
        /// for the 13 px wide eye (<paramref name="w"/> = EyeW at the bubble's DPI); scales with it.</summary>
        private static void DrawEye(Graphics g, float x, float cy, Color color, float w)
        {
            float k = w / 13f;
            var sm = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 1.1f * k))
                g.DrawEllipse(pen, x, cy - 3.5f * k, w, 7f * k);
            using (var pb = new SolidBrush(color))
                g.FillEllipse(pb, x + w / 2f - 1.5f * k, cy - 1.5f * k, 3f * k, 3f * k);
            g.SmoothingMode = sm;
        }

        /// <summary>Draws the reaction chips beneath the bubble (single row, clipped to availW).</summary>
        private void DrawReactions(Graphics g, int x, int y, int availW)
        {
            _reactionRects.Clear();
            if (!HasReactions) return;

            int cx = x;
            int chipH = ReactionH - P(6), emojiSz = P(16);
            var f = FontHelper.UiAt(8.25f, CurDpi, FontStyle.Bold);   // shared: never dispose
            foreach (var r in _reactions)
            {
                string cnt = r.Count > 1 ? r.Count.ToString() : "";
                int textW = cnt.Length == 0 ? 0 : TextRenderer.MeasureText(cnt, f).Width + P(3);
                int chipW = P(7) + emojiSz + textW + P(8);
                if (cx > x && cx + chipW > x + availW) break;   // single row: stop on overflow

                var chip = new Rectangle(cx, y, chipW, chipH);
                Color bg = r.Chosen ? AccentColor : (ThemeHelper.PaletteFor(IsDark).BubbleChipBg);
                Color fg = r.Chosen ? ThemeHelper.PaletteFor(IsDark).OnAccent : (ThemeHelper.PaletteFor(IsDark).BubbleChipFg);
                using (var b = new SolidBrush(bg))
                using (var path = DrawHelper.RoundedRect(chip, chipH / 2))
                    g.FillPath(b, path);

                var emojiRect = new Rectangle(cx + P(7), y + (chipH - emojiSz) / 2, emojiSz, emojiSz);
                var img = EmojiRenderer.Get(r.Emoji);
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, emojiRect);
                }
                else
                {
                    TextRenderer.DrawText(g, r.Emoji, f, emojiRect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (cnt.Length > 0)
                    TextRenderer.DrawText(g, cnt, f, new Rectangle(emojiRect.Right + P(2), y, textW + P(2), chipH), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                _reactionRects.Add(new KeyValuePair<string, Rectangle>(r.Emoji, chip));
                cx += chipW + P(4);
            }
        }

        /// <summary>
        /// Draws the "replying to…" quote as up to TWO stacked sub-strips inside the ReplyBlockH band:
        /// the quoted-sender NAME (top, ReplyNameH tall) and the quoted TEXT (below, ReplyH tall), each
        /// vertically centered in — and clipped to — its own sub-strip via TextRenderer, so neither
        /// bleeds into the other or into the message body that begins at y + ReplyBlockH. RTL (Persian):
        /// accent bar on the RIGHT, both lines right-aligned. Also records the tap rect for Part B.
        /// </summary>
        private void DrawReplyHeader(Graphics g, int x, int y, int w)
        {
            int blockH = ReplyBlockH;
            bool rtl = _rtl;
            _replyRect = new Rectangle(x, y, w, blockH);   // tap target → ReplyQuoteClicked

            // Accent bar spans the WHOLE block, on the leading edge (right for RTL, left for LTR).
            int barW = P(3);
            int barX = rtl ? x + w - barW : x;
            using (var ab = new SolidBrush(_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0)))
                g.FillRectangle(ab, barX, y + P(2), barW, blockH - P(4));

            int textX = rtl ? x : x + P(9);  // text inset past the bar on the bar side
            int textW = w - P(9);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.VerticalCenter
                        | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);

            // TEXT sub-strip defaults to the whole block; when a sender is shown it drops below the name strip.
            int textStripY = y;
            int textStripH = blockH;
            if (HasReplySender)
            {
                Color nameC = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ThemeHelper.PaletteFor(IsDark).LinkIn;
                TextRenderer.DrawText(g, ReplySender, FontHelper.ForAt(ReplySender, FontHelper.IsPersian(ReplySender) ? 9f : 8f, CurDpi, FontStyle.Bold),
                    new Rectangle(textX, y, textW, ReplyNameH), nameC, flags);   // shared font: never dispose
                textStripY = y + ReplyNameH;
                textStripH = blockH - ReplyNameH;
            }

            Color rc = _outgoing
                ? ThemeHelper.PaletteFor(IsDark).BubbleOutSoftText
                : ThemeHelper.PaletteFor(IsDark).BubbleInReplyText;
            // TextRenderer clips to bounds + does script fallback (Persian/mixed) — no GDI+ bleed.
            TextRenderer.DrawText(g, ReplyPreview, ReplyFont, new Rectangle(textX, textStripY, textW, textStripH), rc, flags);
        }

        /// <summary>Draws the "Forwarded from X" header above the content. RTL: right-aligned (Persian).</summary>
        private void DrawForwardHeader(Graphics g, int x, int y, int w)
        {
            string label = "Forwarded from " + ForwardedFrom;
            Color c = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSoftText : SenderNameColor;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                        | (_rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(g, label, FontHelper.ForAt(label, _rtl ? 9.5f : 8f, CurDpi, FontStyle.Bold),
                new Rectangle(x, y, w, FwdH), c, flags);   // shared font: never dispose
        }

        /// <summary>Status glyph: Failed → red mark; Pending → clock; else (outgoing) ✓ / ✓✓ (read). The glyphs are
        /// drawn at 96-DPI sizes around (cx, cy); k = dpi / 96 scales them (step 3.2).</summary>
        private void DrawStatusGlyph(Graphics g, float cx, float cy, Color color)
        {
            float k = Dpi.F(1f, CurDpi);
            if (Failed) { DrawFailedMark(g, cx, cy, k); return; }
            if (Pending) { DrawPendingClock(g, cx, cy, color, k); return; }
            if (_outgoing) { if (Read) DrawDoubleCheck(g, cx, cy, color, k); else DrawSingleCheck(g, cx, cy, color, k); }
        }

        private static void DrawSingleCheck(Graphics g, float cx, float cy, Color color, float k)
        {
            using (var pen = new Pen(color, 1.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - 4 * k, cy + 1 * k), new PointF(cx - 1 * k, cy + 4 * k), new PointF(cx + 5 * k, cy - 4 * k) });
        }

        private static void DrawDoubleCheck(Graphics g, float cx, float cy, Color color, float k)
        {
            using (var pen = new Pen(color, 1.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(cx - 6 * k, cy + 1 * k), new PointF(cx - 3 * k, cy + 4 * k), new PointF(cx + 2 * k, cy - 4 * k) });
                g.DrawLines(pen, new[] { new PointF(cx - 1 * k, cy + 4 * k), new PointF(cx, cy + 4 * k), new PointF(cx + 6 * k, cy - 4 * k) });
            }
        }

        /// <summary>Small "sending" clock glyph for optimistic/pending bubbles.</summary>
        private static void DrawPendingClock(Graphics g, float cx, float cy, Color color, float k)
        {
            float r = 5f * k;
            using (var pen = new Pen(color, 1.3f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
                g.DrawLine(pen, cx, cy, cx, cy - r * 0.6f);              // minute hand (up)
                g.DrawLine(pen, cx, cy, cx + r * 0.5f, cy + r * 0.15f);  // hour hand (right)
            }
        }

        /// <summary>Small red "!" disc indicating a failed send.</summary>
        private static void DrawFailedMark(Graphics g, float cx, float cy, float k)
        {
            float r = 6f * k;
            var red = ThemeHelper.Palette.AlertRed;   // the same in both modes
            using (var b = new SolidBrush(red))
                g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            using (var pen = new Pen(Color.White, 1.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx, cy - 3f * k, cx, cy + 1f * k); // stem
                g.DrawLine(pen, cx, cy + 3f * k, cx, cy + 3.2f * k); // dot
            }
        }

        private void FillImageArea(Graphics g, Rectangle area)
        {
            using (var b = new SolidBrush(ThemeHelper.PaletteFor(IsDark).BubbleImagePlaceholder))
            using (var path = DrawHelper.RoundedRect(area, P(8)))
                g.FillPath(b, path);
        }

        /// <summary>The largest centered circle inscribed in <paramref name="r"/> (round-video mask).</summary>
        private static System.Drawing.Drawing2D.GraphicsPath CirclePath(Rectangle r)
        {
            int d = Math.Min(r.Width, r.Height);
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddEllipse(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            return p;
        }

        private void DrawVideoOverlay(Graphics g, Rectangle area)
        {
            int d = P(48);       // play-button circle, radius 24
            float k = d / 48f;   // the triangle is drawn for the 48 px circle; scales with it
            int cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            using (var cb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).MediaPlayDisc))
                g.FillEllipse(cb, cx - d / 2, cy - d / 2, d, d);
            using (var wb = new SolidBrush(Color.White))
                g.FillPolygon(wb, new[]
                {
                    new PointF(cx - 7 * k, cy - 11 * k), new PointF(cx + 12 * k, cy), new PointF(cx - 7 * k, cy + 11 * k)
                });

            if (!string.IsNullOrEmpty(DurationText))
            {
                var sz = TextRenderer.MeasureText(DurationText, TimeFont);
                int bw = sz.Width + P(12), bh = sz.Height + P(6);
                var rect = new Rectangle(area.Right - bw - P(6), area.Bottom - bh - P(6), bw, bh);
                using (var bb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).MediaBadgeBg))
                using (var path = DrawHelper.RoundedRect(rect, P(4)))
                    g.FillPath(bb, path);
                TextRenderer.DrawText(g, DurationText, TimeFont, rect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>A SUBTLE centered "tap to play" affordance for WebM stickers (smaller + softer than the
        /// video play button). Drawn LIVE over the cached thumbnail, so it shows only in the static state.</summary>
        private void DrawStickerPlayHint(Graphics g, Rectangle area)
        {
            int d = Math.Max(P(28), Math.Min(P(40), Math.Min(area.Width, area.Height) / 4));
            int cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var cb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).MediaPlayDiscSoft))   // soft translucent disc (subtle, theme-agnostic)
                g.FillEllipse(cb, cx - d / 2, cy - d / 2, d, d);
            using (var wb = new SolidBrush(ThemeHelper.PaletteFor(IsDark).MediaPlayGlyph))
                g.FillPolygon(wb, new[]   // right-pointing triangle, proportioned like the video play glyph
                {
                    new PointF(cx - 0.146f * d, cy - 0.229f * d),
                    new PointF(cx + 0.25f * d, cy),
                    new PointF(cx - 0.146f * d, cy + 0.229f * d)
                });
            g.SmoothingMode = oldMode;
        }

        private void DrawGifBadge(Graphics g, Rectangle area)
        {
            var sz = TextRenderer.MeasureText("GIF", TimeFont);
            int bw = sz.Width + P(12), bh = sz.Height + P(6);
            var rect = new Rectangle(area.X + P(6), area.Y + P(6), bw, bh);
            using (var bb = new SolidBrush(AccentColor))
            using (var path = DrawHelper.RoundedRect(rect, P(4)))
                g.FillPath(bb, path);
            TextRenderer.DrawText(g, "GIF", TimeFont, rect, ThemeHelper.PaletteFor(IsDark).OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawSpinner(Graphics g, Rectangle area)
        {
            int d = P(30);
            var arc = new Rectangle(area.X + (area.Width - d) / 2, area.Y + (area.Height - d) / 2, d, d);
            using (var pen = new Pen(Color.White, Dpi.F(3f, CurDpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, _spinnerAngle, 270);
        }

        /// <summary><paramref name="radius"/>: the corner radius in pixels at the caller's DPI (8 logical).</summary>
        private static void DrawFittedImage(Graphics g, Image img, Rectangle area, int radius, bool cover = false)
        {
            // cover = fill the area and crop the overflow (center-crop); else fit/letterbox.
            double scale = cover
                ? Math.Max((double)area.Width / img.Width, (double)area.Height / img.Height)
                : Math.Min((double)area.Width / img.Width, (double)area.Height / img.Height);
            int w = Math.Max(1, (int)(img.Width * scale));
            int h = Math.Max(1, (int)(img.Height * scale));
            var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);

            var oldMode = g.InterpolationMode;
            using (var oldClip = g.Clip)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var path = DrawHelper.RoundedRect(area, radius))
                    g.SetClip(path);
                g.DrawImage(img, dest);
                g.Clip = oldClip;
            }
            g.InterpolationMode = oldMode;
        }

        /// <summary>True if the text contains Hebrew/Arabic/Persian characters.</summary>
        private static bool IsRtl(string s)
        {
            foreach (char c in s)
            {
                if ((c >= 0x0590 && c <= 0x05FF) || // Hebrew
                    (c >= 0x0600 && c <= 0x06FF) || // Arabic
                    (c >= 0x0750 && c <= 0x077F) || // Arabic Supplement
                    (c >= 0x08A0 && c <= 0x08FF) || // Arabic Extended-A
                    (c >= 0xFB50 && c <= 0xFDFF) || // Arabic Presentation Forms-A
                    (c >= 0xFE70 && c <= 0xFEFF))   // Arabic Presentation Forms-B
                    return true;
            }
            return false;
        }

        // ── Link-preview card measure/draw ──────────────────────────────────
        /// <summary>Computes the card height; stashes the text-column width + per-line heights for paint.</summary>
        private int MeasureCard(Graphics g, int cardW)
        {
            int thumb = _cardHasThumb ? CardThumb : 0;
            _cardTextColW = Math.Max(P(20), cardW - (CardBar + CardGap) - CardPad - (thumb > 0 ? thumb + CardGap : 0));
            _cardSiteH = string.IsNullOrEmpty(_cardSite) ? 0 : LineHeight(g, CardSiteFont);
            _cardTitleH = ClampWrapped(g, _cardTitle, CardTitleFont, _cardTextColW, 2);
            _cardDescH = ClampWrapped(g, _cardDesc, CardDescFont, _cardTextColW, 3);
            int textH = _cardSiteH + _cardTitleH + _cardDescH
                      + (_cardSiteH > 0 && (_cardTitleH > 0 || _cardDescH > 0) ? P(2) : 0)
                      + (_cardTitleH > 0 && _cardDescH > 0 ? P(2) : 0);
            return Math.Max(textH, thumb) + 2 * CardPad;
        }

        private void PaintCard(Graphics g, int x, int y, int cardW)
        {
            _cardRect = new Rectangle(x, y, cardW, _cardHeight);

            Color bg = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleCardBgOut
                                 : ThemeHelper.PaletteFor(IsDark).BubbleCardBgIn;
            using (var b = new SolidBrush(bg))
            using (var p = DrawHelper.RoundedRect(_cardRect, P(8)))
                g.FillPath(b, p);

            Color barC = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ColorMath.Ink(AccentColor, ThemeHelper.PaletteFor(IsDark).BubbleIn, 3.0);
            int barX = _rtl ? x + cardW - CardBar : x;
            using (var bb = new SolidBrush(barC))
                g.FillRectangle(bb, barX, y + P(4), CardBar, _cardHeight - P(8));

            int thumb = _cardHasThumb ? CardThumb : 0;
            int textLeft, thumbX;
            if (!_rtl) { textLeft = x + CardBar + CardGap; thumbX = x + cardW - CardPad - thumb; }
            else { thumbX = x + CardPad; textLeft = thumb > 0 ? thumbX + thumb + CardGap : x + CardPad; }

            if (thumb > 0)
            {
                var tr = new Rectangle(thumbX, y + (_cardHeight - thumb) / 2, thumb, thumb);
                if (_cardThumb != null)
                {
                    var old = g.Clip;
                    using (var clip = DrawHelper.RoundedRect(tr, P(6))) g.SetClip(clip);
                    double scale = Math.Max((double)tr.Width / _cardThumb.Width, (double)tr.Height / _cardThumb.Height);
                    int w = (int)(_cardThumb.Width * scale), h = (int)(_cardThumb.Height * scale);
                    g.DrawImage(_cardThumb, tr.X + (tr.Width - w) / 2, tr.Y + (tr.Height - h) / 2, w, h);  // cover-crop
                    g.Clip = old;
                }
                else
                {
                    using (var ph = new SolidBrush(ThemeHelper.PaletteFor(IsDark).BubbleCardThumbPlaceholder))
                    using (var p = DrawHelper.RoundedRect(tr, P(6))) g.FillPath(ph, p);
                }
            }

            Color titleC = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : (ThemeHelper.PaletteFor(IsDark).BubbleInText);
            Color descC = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSubStrong : ThemeHelper.PaletteFor(IsDark).BubbleInCardDesc;
            int ty = y + CardPad;
            if (_cardSiteH > 0) { DrawCardLine(g, _cardSite, CardSiteFont, textLeft, ty, _cardTextColW, _cardSiteH, (_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : ThemeHelper.PaletteFor(IsDark).LinkIn), false); ty += _cardSiteH + P(2); }
            if (_cardTitleH > 0) { DrawCardLine(g, _cardTitle, CardTitleFont, textLeft, ty, _cardTextColW, _cardTitleH, titleC, true); ty += _cardTitleH + P(2); }
            if (_cardDescH > 0) DrawCardLine(g, _cardDesc, CardDescFont, textLeft, ty, _cardTextColW, _cardDescH, descC, true);
        }

        private void DrawCardLine(Graphics g, string text, Font font, int x, int y, int w, int h, Color color, bool multiline)
        {
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                      | (multiline ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine)
                      | (_rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, w, h), color, flags);
        }

        private static int LineHeight(Graphics g, Font f)
            => TextRenderer.MeasureText(g, "Ag", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;

        /// <summary>Wrapped text height at <paramref name="width"/>, clamped to <paramref name="maxLines"/>.</summary>
        private static int ClampWrapped(Graphics g, string text, Font font, int width, int maxLines)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int h = TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), flags).Height;
            return Math.Min(h, LineHeight(g, font) * maxLines);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel any in-flight file download so it can't outlive the bubble (zombie transfer).
                // DOWNLOAD-UX: bubbles no longer CANCEL their transfers on dispose — downloads run in the
                // background across chat switches; a returning bubble REBINDS to the live handle. Unsubscribe only.
                if (_fileHandle != null) { _fileHandle.Changed -= OnFileHandleChanged; _fileHandle = null; }
                // Same for audio-album row downloads + unhook the player state listener.
                if (_album != null)
                    foreach (var t in _album)
                        if (t.Handle != null) { t.Handle.Changed -= OnAlbumAudioProgress; t.Handle = null; }   // DOWNLOAD-UX: unsubscribe only, never cancel
                if (_audioStateHooked) { AudioPlayer.StateChanged -= OnAlbumAudioStateChanged; _audioStateHooked = false; }
                if (_rich != null) { _rich.Dispose(); _rich = null; }   // release the cached text bitmap + fonts
                // (No font disposal: step 3.2 — every font here is a shared FontCache font, never disposed.)
                _spinnerTimer?.Dispose();
                _flashTimer?.Dispose();
                _rich?.Dispose();            // frees the inline engine's style fonts
                AnimationOwner?.Dispose();   // stops the Lottie animator + frees its frame buffer
                // NOTE: _image is owned by MainForm's photo cache (or the animator) — not disposed here.
            }
            base.Dispose(disposing);
        }
    }
}
