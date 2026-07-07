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

    /// <summary>STORIES-BUILD-2/3: the full-screen story VIEWER. Owner-draws the photo (or hosts a VlcVideoControl
    /// for a video story) + a segmented progress bar + top/bottom overlays; auto-advances (photos ~5s, videos over
    /// their real duration), tap-left/right steps, hold pauses, swipe-down/✕/Esc closes, advances peer-to-peer.
    /// Marks stories seen on view (dims the ring).</summary>
    internal sealed class StoryViewerForm : Form
    {
        private const int StoryMs = 5000;   // photo dwell time
        private const int TickMs = 50;      // ~20fps progress fill
        private const int VideoTopH = 56;   // video is inset below this chrome strip (progress + header live here)

        private readonly TelegramService _service;
        private readonly List<StoryPeerRef> _peers;
        private readonly Func<long, Image> _avatar;
        private readonly Color _accent;

        private int _peerIdx;
        private List<StoryItem> _stories = new List<StoryItem>();
        private int _storyIdx;

        private readonly Dictionary<int, Image> _photoCache = new Dictionary<int, Image>();
        private Image _curImage;
        private bool _loading, _videoStory;
        private InlineText _caption;
        private int _captionH;

        // video (BUILD-3)
        private VlcVideoControl _video;
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
                               Func<long, Image> avatarGetter, Color accent)
        {
            _service = service;
            _peers = peers ?? new List<StoryPeerRef>();
            _avatar = avatarGetter;
            _accent = accent;
            _peerIdx = Math.Max(0, Math.Min(startIdx, _peers.Count - 1));

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
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
            base.OnShown(e);
            EnterPeer(_peerIdx, false);
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

            if (IsDisposed || _peerIdx != idx) return;                 // superseded mid-fetch
            if (items == null || items.Count == 0) { EnterPeer(idx + 1, false); return; }   // nothing active → skip peer
            _stories = items;
            ShowStory(atEnd ? _stories.Count - 1 : 0);
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
            _caption = string.IsNullOrEmpty(st.caption) ? null : new InlineText(st.caption, st.entities, null);
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
            _video = new VlcVideoControl { Visible = false };
            Controls.Add(_video);
        }

        private void LayoutVideo()
        {
            if (_video == null) return;
            int botH = _caption != null ? Math.Min(_captionH + 30, ClientSize.Height / 3) : 0;
            _video.Bounds = new Rectangle(0, VideoTopH, ClientSize.Width, Math.Max(1, ClientSize.Height - VideoTopH - botH));
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
                Invalidate(new Rectangle(0, 0, ClientSize.Width, VideoTopH));
                return;
            }
            // photo OR video-fallback (VLC unavailable / unsupported): the fixed dwell timer
            _progress += TickMs / (float)StoryMs;
            if (_progress >= 1f) { _progress = 1f; NextStory(); }
            else Invalidate(new Rectangle(0, 0, ClientSize.Width, _videoStory ? VideoTopH : 20));
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
            if (_down && y - _downY > 120 && Math.Abs(x - _downX) < 160) _swiping = true;   // swipe down to close
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
            int cw = ClientSize.Width, ch = ClientSize.Height;

            if (!_videoStory)
            {
                if (_curImage != null)
                {
                    var r = FitRect(_curImage.Size, new Rectangle(0, 0, cw, ch));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_curImage, r);
                }
                else if (_loading) DrawCenteredText(g, cw, ch, "Loading…", 12f, Color.FromArgb(180, 180, 180));
                using (var scrim = new LinearGradientBrush(new Rectangle(0, 0, cw, 96),
                           Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(scrim, 0, 0, cw, 96);
            }
            else
            {
                // VIDEO: the VLC child covers the inset middle; draw the chrome in the uncovered top/bottom strips.
                g.Clear(Color.Black);
                if (_loading) DrawCenteredText(g, cw, ch, "Loading…", 12f, Color.FromArgb(180, 180, 180));
                else if (_videoFallback) DrawCenteredText(g, cw, ch, "Video story", 13f, Color.FromArgb(200, 200, 200));
            }

            DrawProgress(g, cw);
            DrawHeader(g, cw);
            if (_caption != null && e.ClipRectangle.Bottom > ch - (_captionH + 40)) DrawCaption(g, cw, ch);
        }

        private void DrawProgress(Graphics g, int cw)
        {
            int n = _stories.Count; if (n <= 0) return;
            const int pad = 8, gap = 4, y = 10, h = 3;
            float segW = (cw - 2 * pad - gap * (n - 1)) / (float)n;
            if (segW <= 0) return;
            for (int i = 0; i < n; i++)
            {
                float x = pad + i * (segW + gap);
                using (var bg = new SolidBrush(Color.FromArgb(90, 255, 255, 255))) g.FillRectangle(bg, x, y, segW, h);
                float f = i < _storyIdx ? 1f : (i == _storyIdx ? _progress : 0f);
                if (f > 0f) using (var fb = new SolidBrush(Color.White)) g.FillRectangle(fb, x, y, segW * f, h);
            }
        }

        private void DrawHeader(Graphics g, int cw)
        {
            if (_peerIdx < 0 || _peerIdx >= _peers.Count) return;
            var p = _peers[_peerIdx];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int ax = 12, ay = 22, d = 30;
            var avr = new Rectangle(ax, ay, d, d);
            var img = _avatar != null ? _avatar(p.PeerId) : null;
            if (img != null)
                using (var clip = new GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(img, avr); g.ResetClip(); }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(p.PeerId))) g.FillEllipse(b, avr);
                using (var f = FontHelper.Ui(12f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Initial(p.Name), f, avr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            string time = _storyIdx >= 0 && _storyIdx < _stories.Count ? RelTime(_stories[_storyIdx].date) : "";
            using (var nf = FontHelper.For(p.Name ?? "", 10.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, p.Name ?? "", nf, new Rectangle(ax + d + 8, ay, cw - (ax + d + 8) - 60, 18), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var tf = FontHelper.Ui(8.5f))
                TextRenderer.DrawText(g, time, tf, new Rectangle(ax + d + 8, ay + 16, 200, 14), Color.FromArgb(200, 200, 200),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            _closeRect = new Rectangle(cw - 46, 16, 34, 34);
            using (var pen = new Pen(Color.White, 2f))
            {
                g.DrawLine(pen, _closeRect.Left + 9, _closeRect.Top + 9, _closeRect.Right - 9, _closeRect.Bottom - 9);
                g.DrawLine(pen, _closeRect.Right - 9, _closeRect.Top + 9, _closeRect.Left + 9, _closeRect.Bottom - 9);
            }
        }

        private void DrawCaption(Graphics g, int cw, int ch)
        {
            int maxW = Math.Max(40, cw - 24);
            using (var f = FontHelper.For(" ", 10.5f))
            {
                _caption.Measure(g, maxW, f);
                _caption.Position(maxW, IsRtlCaption());
                int h = _caption.Height;
                int y = ch - h - 26;
                using (var scrim = new LinearGradientBrush(new Rectangle(0, y - 40, cw, h + 66),
                           Color.FromArgb(0, 0, 0, 0), Color.FromArgb(180, 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(scrim, 0, y - 40, cw, h + 66);
                _caption.Paint(g, 12, y, Color.White, Color.FromArgb(130, 190, 255), f, true, _accent, false);
            }
        }

        private void MeasureCaption(string capText)
        {
            _captionH = 0;
            if (_caption == null) return;
            try
            {
                using (var g = CreateGraphics())
                using (var f = FontHelper.For(capText ?? " ", 10.5f))
                {
                    int maxW = Math.Max(40, ClientSize.Width - 24);
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

        private static void DrawCenteredText(Graphics g, int cw, int ch, string text, float size, Color color)
        {
            using (var f = FontHelper.Ui(size))
                TextRenderer.DrawText(g, text, f, new Rectangle(0, 0, cw, ch), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static string Initial(string name)
        {
            return !string.IsNullOrEmpty(name) ? name.Substring(0, 1).ToUpper() : "?";
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
