using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>A peer that has stories, in tray order — the viewer navigates this list.</summary>
    internal sealed class StoryPeerRef
    {
        public long PeerId;
        public string Name;
        public InputPeer Input;
    }

    /// <summary>STORIES-BUILD-2/3: the full-screen story VIEWER. Owner-draws the photo (or hosts a VideoPlayerControl
    /// for a video story) + a segmented progress bar + top/bottom overlays; auto-advances (photos ~5s, videos over
    /// their real duration), tap-left/right steps, hold pauses, swipe-down/✕/Esc closes, advances peer-to-peer.
    /// Marks stories seen on view (dims the ring).
    /// Step 3.6: per-window DPI (a plain Form, so its own WindowDpi): full screen on the screen it opens over, lengths
    /// through Px at that DPI, shared pixel fonts; a rescale re-covers the screen and re-measures the caption.</summary>
    internal sealed class StoryViewerForm : Form, IDpiHost, IDpiAware
    {
        private const int StoryMs = 5000;   // photo dwell time
        private const int TickMs = 50;      // ~20fps progress fill
        private const int VideoTopH = 56;   // logical: video is inset below this chrome strip (progress + header live here)
        private const int SideW = 48;       // logical: left/right nav-chevron strip; video is inset by this so the ‹ › show over it

        private readonly WindowDpi _windowDpi;
        private bool _shown;                // OnShown ran: from then on only WindowDpi moves the window
        private int CurDpi { get { return _windowDpi.Dpi; } }
        private int Px(int n) { return Dpi.Px(n, _windowDpi.Dpi); }
        int IDpiHost.Dpi { get { return _windowDpi.Dpi; } }

        private readonly TelegramService _service;
        private readonly List<StoryPeerRef> _peers;
        private readonly Func<long, Image> _avatar;
        private readonly Color _accent;

        private int _peerIdx;
        private List<StoryItem> _stories = new List<StoryItem>();
        private int _storyIdx;
        private int _initialStoryIdx;   // PROFILE-STORIES: deep-link — the FIRST peer entry starts here (default 0)
        private readonly List<StoryItem> _preloaded;   // PROFILE-STORIES: when set, the start peer shows THIS list (posted/pinned) instead of fetching the active ring
        private readonly int _startPeerIdx;

        private readonly Dictionary<int, Image> _photoCache = new Dictionary<int, Image>();
        private Image _curImage;
        private bool _loading, _videoStory;
        private InlineText _caption;
        private int _captionH;

        // video (BUILD-3)
        private VideoPlayerControl _video;
        private DateTime _videoStartedAt;
        private bool _videoPlayStarted, _videoFallback;
        private readonly VideoTapFilter _tapFilter;

        private readonly System.Windows.Forms.Timer _timer;
        private float _progress;            // 0..1 fill of the current segment
        private bool _paused;
        private int _loadGen;               // supersede token for async media loads

        // input state
        private int _downX, _downY; private DateTime _downAt; private bool _down, _swiping;
        private Rectangle _closeRect;

        // mark-seen + view-register
        private int _peerMaxViewed;
        private readonly HashSet<int> _pendingViews = new HashSet<int>();        // ids shown for the current peer, awaiting IncrementStoryViews
        private readonly HashSet<string> _registeredViews = new HashSet<string>();  // session dedup: "peerId:storyId" already incremented
        public HashSet<long> SeenPeers { get; } = new HashSet<long>();

        public StoryViewerForm(TelegramService service, List<StoryPeerRef> peers, int startIdx,
                               Func<long, Image> avatarGetter, Color accent, int startStoryIdx = 0,
                               List<StoryItem> preloaded = null)
        {
            _service = service;
            _peers = peers ?? new List<StoryPeerRef>();
            _avatar = avatarGetter;
            _accent = accent;
            _peerIdx = Math.Max(0, Math.Min(startIdx, _peers.Count - 1));
            _initialStoryIdx = Math.Max(0, startStoryIdx);   // 0 for the tray (unchanged); the profile deep-links to a tapped story
            _preloaded = preloaded;                          // null for the tray → fetch as before; the profile supplies its posted/pinned list
            _startPeerIdx = _peerIdx;

            _windowDpi = new WindowDpi(this);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            PlaceOnScreen();   // step 3.6: was Screen.PrimaryScreen.Bounds; re-placed on the owner's screen in OnHandleCreated
            BackColor = Color.Black;
            KeyPreview = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;

            _timer = new System.Windows.Forms.Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            // The video is a native surface that swallows mouse events — a filter routes taps over it to the zone
            // logic (self-gated to video stories; photos use the normal OnMouse* path).
            _tapFilter = new VideoTapFilter(this);
            Application.AddMessageFilter(_tapFilter);
        }

        protected override void OnShown(EventArgs e)
        {
            _shown = true;
            base.OnShown(e);
            EnterPeer(_peerIdx, false);
        }

        // ── DPI + placement (step 3.6) ───────────────────────────────────────
        /// <summary>The screen the viewer covers: its owner's (ShowDialog sets Owner before the handle exists), else the
        /// active form's, else the cursor's.</summary>
        private Screen PickScreen()
        {
            try
            {
                Form anchor = Owner;
                if (anchor == null || anchor.IsDisposed || !anchor.IsHandleCreated) anchor = Form.ActiveForm;
                if (anchor != null && !ReferenceEquals(anchor, this) && !anchor.IsDisposed && anchor.IsHandleCreated)
                    return Screen.FromHandle(anchor.Handle);
            }
            catch { }
            try { return Screen.FromPoint(Cursor.Position); } catch { }
            return Screen.PrimaryScreen;
        }

        /// <summary>Full screen on <see cref="PickScreen"/>, at that screen's effective DPI (Dpi.ForPopup) BEFORE it is
        /// shown. Only before OnShown: nothing DPI-derived exists yet (caption and video come after), so the DPI is taken
        /// over without a rescale.</summary>
        private void PlaceOnScreen()
        {
            var b = PickScreen().Bounds;
            _windowDpi.Adopt(Dpi.ForPopup(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2)));
            Bounds = b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_shown) PlaceOnScreen();       // the owner is known now
            _windowDpi.Resync("handle");        // the window's real monitor DPI wins (inert while system-aware)
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); _windowDpi.Resync("activated"); }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (_windowDpi != null && _windowDpi.OnMessage(ref m)) return;   // WM_DPICHANGED: ours, never WinForms' (no base call)
            base.WndProc(ref m);
        }

        /// <summary>DpiScaler has moved the window (suggested / scaled-in-place rect) and ratio-scaled the video's bounds:
        /// re-cover the screen it is on now and re-derive the video inset and the caption from logical values.</summary>
        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            try { Bounds = Screen.FromHandle(Handle).Bounds; } catch { }
            if (_caption != null)
            {
                _caption.LayoutDpi = newDpi;
                var st = _storyIdx >= 0 && _storyIdx < _stories.Count ? _stories[_storyIdx] : null;
                MeasureCaption(st != null ? st.caption : null);
            }
            LayoutVideo();
            Invalidate();
        }

        // ── navigation ───────────────────────────────────────────────────────
        private async void EnterPeer(int idx, bool atEnd)
        {
            FlushSeen();                       // persist the OUTGOING peer's seen watermark first
            StopVideo();
            if (idx < 0) idx = 0;              // can't step before the first peer → clamp
            if (idx >= _peers.Count) { Close(); return; }
            _peerIdx = idx; _storyIdx = 0; _peerMaxViewed = 0;
            _stories = new List<StoryItem>(); _curImage = null; _videoStory = false;
            _progress = 0; _timer.Stop(); _loading = true; Invalidate();

            List<StoryItem> items = null;
            if (_preloaded != null && idx == _startPeerIdx)
            {
                items = _preloaded;   // PROFILE-STORIES: show exactly the posted/pinned stories already fetched (no expire filter)
            }
            else
            {
                try
                {
                    var res = await _service.GetPeerStoriesAsync(_peers[idx].Input);
                    var arr = res?.stories?.stories;   // Stories_PeerStories.stories (PeerStories).stories (StoryItemBase[])
                    if (arr != null)
                        items = arr.OfType<StoryItem>()
                                   .Where(s => s.expire_date == default(DateTime) || s.expire_date > DateTime.UtcNow)
                                   .OrderBy(s => s.id).ToList();
                }
                catch { items = null; }
            }

            if (IsDisposed || _peerIdx != idx) return;                 // superseded mid-fetch
            if (items == null || items.Count == 0) { EnterPeer(idx + 1, false); return; }   // nothing active → skip peer
            _stories = items;
            int start = atEnd ? _stories.Count - 1 : Math.Min(_initialStoryIdx, _stories.Count - 1);
            _initialStoryIdx = 0;   // one-shot: only the initial (deep-link) entry honors it; later peers start at 0
            ShowStory(start);
        }

        private void ShowStory(int idx)
        {
            if (idx < 0) { if (_peerIdx > 0) EnterPeer(_peerIdx - 1, true); return; }   // before the first → prev peer's last
            if (idx >= _stories.Count) { EnterPeer(_peerIdx + 1, false); return; }      // past the last → next peer
            StopVideo();
            _storyIdx = idx;
            _progress = 0; _paused = false;
            var st = _stories[idx];
            SeenPeers.Add(_peers[_peerIdx].PeerId);
            if (st.id > _peerMaxViewed) _peerMaxViewed = st.id;
            _pendingViews.Add(st.id);   // VIEW-REGISTER: this story is now viewed → register on FlushSeen

            _caption?.Dispose();
            _caption = string.IsNullOrEmpty(st.caption) ? null : new InlineText(st.caption, st.entities, null) { LayoutDpi = CurDpi };
            MeasureCaption(st.caption);

            LoadMedia(st);
            _timer.Start();
            Invalidate();
        }

        private void NextStory() { ShowStory(_storyIdx + 1); }
        private void PrevStory() { ShowStory(_storyIdx - 1); }

        // ── media ────────────────────────────────────────────────────────────
        private async void LoadMedia(StoryItem st)
        {
            int gen = ++_loadGen;
            _videoStory = false; _videoPlayStarted = false; _videoFallback = false;

            var photo = (st.media as MessageMediaPhoto)?.photo as Photo;
            if (photo != null)
            {
                Image cached;
                if (_photoCache.TryGetValue(st.id, out cached) && cached != null)
                { _curImage = cached; _loading = false; Invalidate(); return; }
                _curImage = null; _loading = true; Invalidate();   // _loading pauses auto-advance until the photo lands
                try
                {
                    var res = await _service.DownloadPhotoAsync(photo);
                    if (IsDisposed || gen != _loadGen) return;     // advanced away → drop
                    if (res.bytes == null || res.bytes.Length == 0) { _loading = false; Invalidate(); return; }
                    var img = await Task.Run(() => DecodeBytes(res.bytes));   // decode off the UI thread
                    if (IsDisposed || gen != _loadGen) { img?.Dispose(); return; }
                    if (img != null) _photoCache[st.id] = img;
                    _curImage = img; _loading = false; Invalidate();
                }
                catch { if (!IsDisposed && gen == _loadGen) { _loading = false; Invalidate(); } }
                return;
            }

            var doc = (st.media as MessageMediaDocument)?.document as Document;
            if (doc != null && IsVideoDoc(doc))
            {
                _videoStory = true; _curImage = null; _loading = true; Invalidate();
                try
                {
                    string path = await EnsureVideoFileAsync(doc);
                    if (IsDisposed || gen != _loadGen) return;
                    if (path == null) { _loading = false; Invalidate(); return; }
                    EnsureVideoControl();
                    _video.Visible = true; LayoutVideo();
                    _video.PlayFile(path, loop: false, ensureAudio: false);
                    _videoFallback = !_video.IsReady;            // VLC unavailable → fall back to the fixed timer
                    _videoStartedAt = DateTime.UtcNow;
                    _loading = false; Invalidate();
                }
                catch { if (!IsDisposed && gen == _loadGen) { _loading = false; Invalidate(); } }
                return;
            }

            // unsupported media → a placeholder that still auto-advances (never hangs)
            _videoStory = true; _videoFallback = true; _curImage = null; _loading = false; Invalidate();
        }

        private static bool IsVideoDoc(Document doc)
        {
            return doc.attributes != null && doc.attributes.OfType<DocumentAttributeVideo>().Any();
        }

        private async Task<string> EnsureVideoFileAsync(Document doc)
        {
            string fileName = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
            string path = MediaCache.MediaPath(MediaCache.CacheFileName("video", doc.id, fileName));
            if (TelegramService.IsFileComplete(path, doc.size)) return path;   // cached from a prior view
            var bytes = await _service.DownloadDocBytesAsync(doc);
            if (bytes == null || bytes.Length == 0) return null;
            return await Task.Run(() =>
            {
                try
                {
                    string tmp = path + ".part";
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return path;
                }
                catch { return (string)null; }
            });
        }

        private void EnsureVideoControl()
        {
            if (_video != null) return;
            _video = new VideoPlayerControl { Visible = false };
            Controls.Add(_video);
        }

        private void LayoutVideo()
        {
            if (_video == null) return;
            int botH = _caption != null ? Math.Min(_captionH + Px(30), ClientSize.Height / 3) : 0;
            // Inset by SideW on both sides so the nav chevrons (drawn in OnPaint) stay visible over the native video.
            int side = Px(SideW), top = Px(VideoTopH);
            _video.Bounds = new Rectangle(side, top, Math.Max(1, ClientSize.Width - 2 * side), Math.Max(1, ClientSize.Height - top - botH));
        }

        private void StopVideo()
        {
            if (_video == null) return;
            try { _video.StopPlayback(); } catch { }
            _video.Visible = false;
            _videoPlayStarted = false;
        }

        private void PauseVideo() { if (_video != null && _video.IsReady && _video.IsPlaying) _video.TogglePlay(); }
        private void ResumeVideo() { if (_video != null && _video.IsReady && !_video.IsPlaying) _video.TogglePlay(); }

        private void OnTick(object sender, EventArgs e)
        {
            if (_paused || _loading) return;   // frozen while held or while the media loads
            if (_videoStory && !_videoFallback && _video != null && _video.Visible && _video.IsReady)
            {
                _video.RefreshOverlay();
                if (_video.IsPlaying && !_videoPlayStarted) { _videoPlayStarted = true; _videoStartedAt = DateTime.UtcNow; }
                if (_video.HasEnded) { NextStory(); return; }
                float pos = _video.Position;
                if (pos > 0f) _progress = Math.Min(1f, pos);          // fill tracks the ACTUAL video position
                if (_videoPlayStarted && (DateTime.UtcNow - _videoStartedAt).TotalSeconds > 62) { NextStory(); return; }   // safety cap
                Invalidate(new Rectangle(0, 0, ClientSize.Width, Px(VideoTopH)));
                return;
            }
            // photo OR video-fallback (VLC unavailable / unsupported): the fixed dwell timer
            _progress += TickMs / (float)StoryMs;
            if (_progress >= 1f) { _progress = 1f; NextStory(); }
            else Invalidate(new Rectangle(0, 0, ClientSize.Width, Px(_videoStory ? VideoTopH : 20)));
        }

        // ── mark-seen ──────────────────────────────────────────────────────────
        private void FlushSeen()
        {
            if (_peerIdx < 0 || _peerIdx >= _peers.Count) { _pendingViews.Clear(); return; }
            var p = _peers[_peerIdx];
            if (p.Input != null && _peerMaxViewed > 0) { var _ = _service.ReadStoriesAsync(p.Input, _peerMaxViewed); }   // seen → ring dims
            if (p.Input != null && _pendingViews.Count > 0)
            {
                // VIEW-REGISTER: tell the poster we viewed these (batched per peer; deduped once per story per session).
                var newIds = new List<int>();
                foreach (var id in _pendingViews) if (_registeredViews.Add(p.PeerId + ":" + id)) newIds.Add(id);
                if (newIds.Count > 0) { var __ = _service.IncrementStoryViewsAsync(p.Input, newIds.ToArray()); }   // fire-and-forget
            }
            _peerMaxViewed = 0;
            _pendingViews.Clear();
        }

        // ── input (shared by OnMouse* for photos and the tap filter for videos) ──
        internal bool VideoActive { get { return _videoStory; } }

        internal void PointerDown(int x, int y)
        {
            _down = true; _swiping = false; _downX = x; _downY = y; _downAt = DateTime.UtcNow;
            _paused = true; if (_videoStory) PauseVideo();   // hold pauses immediately; a quick tap un-pauses on release
        }

        internal void PointerMove(int x, int y)
        {
            // swipe down to close; finger distances are PHYSICAL (the monitor's raw DPI, never the interface scale)
            int mon = _windowDpi.MonitorDpi;
            if (_down && y - _downY > Dpi.Px(120, mon) && Math.Abs(x - _downX) < Dpi.Px(160, mon)) _swiping = true;
        }

        internal void PointerUp(int x, int y)
        {
            if (!_down) return;
            _down = false; _paused = false;
            if (_swiping) { Close(); return; }
            if (_closeRect.Contains(x, y)) { Close(); return; }
            double held = (DateTime.UtcNow - _downAt).TotalMilliseconds;
            if (held < 260) { if (x < ClientSize.Width / 3) PrevStory(); else NextStory(); }   // tap: left third = prev, else next
            else if (_videoStory) ResumeVideo();   // a hold → resume playback
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!_videoStory) PointerDown(e.X, e.Y); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!_videoStory) PointerMove(e.X, e.Y); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (!_videoStory) PointerUp(e.X, e.Y); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Left) PrevStory();
            else if (e.KeyCode == Keys.Right) NextStory();
            else if (e.KeyCode == Keys.Space) { _paused = !_paused; if (_videoStory) { if (_paused) PauseVideo(); else ResumeVideo(); } }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { Application.RemoveMessageFilter(_tapFilter); } catch { }
            FlushSeen();
            _timer.Stop(); _timer.Dispose();
            if (_video != null) { try { _video.StopPlayback(); } catch { } try { _video.Dispose(); } catch { } _video = null; }
            _caption?.Dispose();
            foreach (var im in _photoCache.Values) { try { im?.Dispose(); } catch { } }
            _photoCache.Clear();
            base.OnFormClosed(e);
        }

        // ── paint ────────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int cw = ClientSize.Width, ch = ClientSize.Height, dpi = CurDpi;

            if (!_videoStory)
            {
                if (_curImage != null)
                {
                    var r = FitRect(_curImage.Size, new Rectangle(0, 0, cw, ch));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_curImage, r);
                }
                else if (_loading) DrawCenteredText(g, cw, ch, "Loading…", 12f, ThemeHelper.Palette.StoryLoadingText, dpi);
                int scrimH = Px(96);
                using (var scrim = new LinearGradientBrush(new Rectangle(0, 0, cw, scrimH),
                           ThemeHelper.Palette.StoryScrimTop, ThemeHelper.Palette.StoryScrimClear, LinearGradientMode.Vertical))
                    g.FillRectangle(scrim, 0, 0, cw, scrimH);
            }
            else
            {
                // VIDEO: the VLC child covers the inset middle; draw the chrome in the uncovered top/bottom strips.
                g.Clear(Color.Black);
                if (_loading) DrawCenteredText(g, cw, ch, "Loading…", 12f, ThemeHelper.Palette.StoryLoadingText, dpi);
                else if (_videoFallback) DrawCenteredText(g, cw, ch, "Video story", 13f, ThemeHelper.Palette.StoryMutedText, dpi);
            }

            DrawProgress(g, cw);
            DrawHeader(g, cw);
            DrawNavIndicators(g, cw, ch);
            if (_caption != null && e.ClipRectangle.Bottom > ch - (_captionH + Px(40))) DrawCaption(g, cw, ch);
        }

        /// <summary>The tappable-zone HINTS: a ‹ on the left (previous) and a › on the right (next), vertically
        /// centred, each in a translucent disc for contrast on bright media. The ‹ is hidden at the very first story
        /// (nothing before it). The whole left third / right area stays the actual tap target — these just show where.</summary>
        private void DrawNavIndicators(Graphics g, int cw, int ch)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = ch / 2, inset = Px(26), dpi = CurDpi;
            if (_peerIdx > 0 || _storyIdx > 0) DrawChevron(g, inset, cy, true, dpi);   // has a previous story/peer
            DrawChevron(g, cw - inset, cy, false, dpi);                               // next always exists (story, peer, or close)
        }

        private static void DrawChevron(Graphics g, int cx, int cy, bool left, int dpi)
        {
            int r = Dpi.Px(17, dpi), d = Dpi.Px(6, dpi);
            using (var b = new SolidBrush(ThemeHelper.Palette.StoryChevronBg))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(Color.White, Dpi.F(2.4f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                Point[] pts = left
                    ? new[] { new Point(cx + d, cy - d), new Point(cx - d, cy), new Point(cx + d, cy + d) }
                    : new[] { new Point(cx - d, cy - d), new Point(cx + d, cy), new Point(cx - d, cy + d) };
                g.DrawLines(pen, pts);
            }
        }

        private void DrawProgress(Graphics g, int cw)
        {
            int n = _stories.Count; if (n <= 0) return;
            int pad = Px(8), gap = Px(4), y = Px(10), h = Px(3);
            float segW = (cw - 2 * pad - gap * (n - 1)) / (float)n;
            if (segW <= 0) return;
            for (int i = 0; i < n; i++)
            {
                float x = pad + i * (segW + gap);
                using (var bg = new SolidBrush(ThemeHelper.Palette.StoryProgressTrack)) g.FillRectangle(bg, x, y, segW, h);
                float f = i < _storyIdx ? 1f : (i == _storyIdx ? _progress : 0f);
                if (f > 0f) using (var fb = new SolidBrush(Color.White)) g.FillRectangle(fb, x, y, segW * f, h);
            }
        }

        private void DrawHeader(Graphics g, int cw)
        {
            if (_peerIdx < 0 || _peerIdx >= _peers.Count) return;
            var p = _peers[_peerIdx];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dpi = CurDpi;
            int ax = Px(12), ay = Px(22), d = Px(30);
            var avr = new Rectangle(ax, ay, d, d);
            var img = _avatar != null ? _avatar(p.PeerId) : null;
            if (img != null)
                using (var clip = new GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(img, avr); g.ResetClip(); }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(p.PeerId))) g.FillEllipse(b, avr);
                string ini = Initial(p.Name);
                DrawHelper.DrawInitial(g, ini, FontHelper.ForAt(ini, 12f, dpi, FontStyle.Bold), avr, Color.White);   // ink-centred (owner report 2026-10-14); shared font: never dispose
            }
            string time = _storyIdx >= 0 && _storyIdx < _stories.Count ? RelTime(_stories[_storyIdx].date) : "";
            int tx = ax + d + Px(8);
            TextRenderer.DrawText(g, p.Name ?? "", FontHelper.ForAt(p.Name ?? "", 10.5f, dpi, FontStyle.Bold),   // shared: never dispose
                new Rectangle(tx, ay, cw - tx - Px(60), Px(18)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, time, FontHelper.UiAt(8.5f, dpi), new Rectangle(tx, ay + Px(16), Px(200), Px(14)), ThemeHelper.Palette.StoryMutedText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            _closeRect = new Rectangle(cw - Px(46), Px(16), Px(34), Px(34));
            int k = Px(9);   // the ✕'s inset in its tap square
            using (var pen = new Pen(Color.White, Dpi.F(2f, dpi)))
            {
                g.DrawLine(pen, _closeRect.Left + k, _closeRect.Top + k, _closeRect.Right - k, _closeRect.Bottom - k);
                g.DrawLine(pen, _closeRect.Right - k, _closeRect.Top + k, _closeRect.Left + k, _closeRect.Bottom - k);
            }
        }

        private void DrawCaption(Graphics g, int cw, int ch)
        {
            int maxW = Math.Max(Px(40), cw - Px(24));
            var f = FontHelper.ForAt(" ", 10.5f, CurDpi);   // shared pixel font (the caption's LayoutDpi): never dispose
            _caption.Measure(g, maxW, f);
            _caption.Position(maxW, IsRtlCaption());
            int h = _caption.Height;
            int y = ch - h - Px(26);
            int top = y - Px(40), sh = h + Px(66);
            using (var scrim = new LinearGradientBrush(new Rectangle(0, top, cw, sh),
                       ThemeHelper.Palette.StoryScrimClear, ThemeHelper.Palette.StoryScrimBottom, LinearGradientMode.Vertical))
                g.FillRectangle(scrim, 0, top, cw, sh);
            _caption.Paint(g, Px(12), y, Color.White, ThemeHelper.Palette.StoryCaptionLink, f, true, _accent, false);
        }

        private void MeasureCaption(string capText)
        {
            _captionH = 0;
            if (_caption == null) return;
            try
            {
                using (var g = CreateGraphics())   // pixel-unit font: the measure does not depend on this DC's DPI
                {
                    var f = FontHelper.ForAt(capText ?? " ", 10.5f, CurDpi);   // shared: never dispose
                    int maxW = Math.Max(Px(40), ClientSize.Width - Px(24));
                    _caption.Measure(g, maxW, f);
                    _caption.Position(maxW, IsRtlCaption());
                    _captionH = _caption.Height;
                }
            }
            catch { _captionH = 0; }
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private static Image DecodeBytes(byte[] b)
        {
            try { using (var ms = new MemoryStream(b)) using (var t = Image.FromStream(ms)) return new Bitmap(t); }
            catch { return null; }
        }

        private static Rectangle FitRect(Size img, Rectangle bounds)
        {
            if (img.Width <= 0 || img.Height <= 0) return bounds;
            float scale = Math.Min(bounds.Width / (float)img.Width, bounds.Height / (float)img.Height);
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            return new Rectangle(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
        }

        private static void DrawCenteredText(Graphics g, int cw, int ch, string text, float size, Color color, int dpi)
        {
            TextRenderer.DrawText(g, text, FontHelper.UiAt(size, dpi), new Rectangle(0, 0, cw, ch), color,   // shared: never dispose
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static string Initial(string name)
        {
            return !string.IsNullOrEmpty(name) ? DrawHelper.Initial(name) : "?";
        }

        private static string RelTime(DateTime dateUtc)
        {
            var span = DateTime.UtcNow - dateUtc;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalMinutes < 1) return "now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + "m";
            if (span.TotalHours < 24) return (int)span.TotalHours + "h";
            return (int)span.TotalDays + "d";
        }

        private bool IsRtlCaption()
        {
            var st = (_storyIdx >= 0 && _storyIdx < _stories.Count) ? _stories[_storyIdx] : null;
            string s = st != null ? st.caption : null;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
            {
                if (c >= 0x0590 && c <= 0x08FF) return true;      // Hebrew/Arabic/Persian block → RTL
                if (char.IsLetter(c) && c < 0x0590) return false; // a Latin letter first → LTR
            }
            return false;
        }

        /// <summary>Routes pointer/mouse taps that land on the native VLC surface (which swallows WinForms mouse
        /// events) to the zone logic. Self-gates to video stories; photos use OnMouse* directly. Screen→client via
        /// the cursor position (updated for touch too), so no lParam parsing.</summary>
        private sealed class VideoTapFilter : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200,
                              WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERUPDATE = 0x0245;
            private readonly StoryViewerForm _f;
            public VideoTapFilter(StoryViewerForm f) { _f = f; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                if (_f.IsDisposed || !_f.VideoActive) return false;
                Point pt;
                try { pt = _f.PointToClient(Cursor.Position); } catch { return false; }
                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN: case WM_POINTERDOWN: _f.PointerDown(pt.X, pt.Y); break;
                    case WM_MOUSEMOVE: case WM_POINTERUPDATE: _f.PointerMove(pt.X, pt.Y); break;
                    case WM_LBUTTONUP: case WM_POINTERUP: _f.PointerUp(pt.X, pt.Y); break;
                }
                return false;   // observe only
            }
        }
    }
}
