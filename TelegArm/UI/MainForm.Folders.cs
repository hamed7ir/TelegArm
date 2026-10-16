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

            AddFolderTab("All", AllUnread, !_showArchive && _activeFolder == null, () => SetActiveFolder(null), null);
            foreach (var f in _folders)
            {
                if (f == null) continue;
                string title;
                try { title = (f.Title?.text ?? "").Trim(); } catch { title = ""; }
                var ff = f;
                AddFolderTab(string.IsNullOrEmpty(title) ? "Folder" : title, () => FolderUnread(ff),
                    !_showArchive && ReferenceEquals(f, _activeFolder), () => SetActiveFolder(ff), ff);
            }

            // Archive tab (only when there are archived chats); its unread rides the shared badge now.
            if (_allChats.Any(c => c.Archived))
                AddFolderTab("Archive", ArchiveUnread, _showArchive, SetArchive, null);

            _folderBar.ResumeLayout();
        }

        private void AddFolderTab(string text, Func<int> unread, bool active, Action onClick, TL.DialogFilterBase folder)
        {
            var it = new FolderTabItem(text, unread(), active, _dark, _accent, WindowDpiValue);
            it.Clicked += onClick;
            it.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowFolderMenu(folder, it.PointToScreen(e.Location)); };   // T13
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
            _forumTopicBar.BackColor = ThemeHelper.PaletteFor(_dark).ListBg;
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
            var it = new FolderTabItem(text, unread, active, _dark, _accent, WindowDpiValue);
            it.Clicked += onClick;
            _forumTopicBar.Controls.Add(it);
        }

        /// <summary>Show/hide the topic bar row (row 4). Zero height when hidden → takes no space.</summary>
        private void ShowTopicBar(bool show)
        {
            SetRowPx(_rightLayout, 4, show ? 36 : 0);   // FORUM-TOPICS: 46→36 (no scrollbar row now → tighter). Step 3.4: logical px
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
            _storyTrayBar.BackColor = ThemeHelper.PaletteFor(_dark).ListBg;
            if (_storyPeers != null)
                foreach (var e in _storyPeers)
                {
                    var entry = e;
                    if (entry.PeerInfo != null && _avatars.GetCached(entry.PeerId) == null) _avatars.Request(entry.PeerId, entry.PeerInfo);
                    var chip = new StoryChip(entry.PeerId, entry.Name, entry.Unseen, _dark, _accent, cid => _avatars.GetCached(cid), WindowDpiValue);
                    chip.Clicked += () => OpenStoryViewer(entry.PeerId);
                    _storyTrayBar.Controls.Add(chip);
                }
            _storyTrayBar.ResumeLayout();
        }

        /// <summary>Shows/hides the tray row (its RowStyle height). Zero space when there are no stories.</summary>
        private void ShowStoryTray(bool show)
        {
            SetRowPx(_storyTrayLayout, 1, show ? StoryTrayHeight : 0);   // step 3.4: StoryTrayHeight is logical
        }

        private bool IsChatListAtTop() { return _chatListPanel != null && -_chatListPanel.AutoScrollPosition.Y <= Px(2); }

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
        private sealed class StoryChip : Control, IDpiAware
        {
            // Step 3.4: lengths are logical px at _dpi (the window's, given at construction; IDpiAware re-sets them);
            // fonts are shared FontCache fonts — never disposed, never in a using. Same for FolderTabItem / FolderRailItem.
            private readonly long _peerId; private readonly string _name;
            private bool _unseen; private readonly bool _dark; private readonly Color _accent;
            private readonly Func<long, Image> _avatar;
            private int _dpi;
            public event Action Clicked;
            public long PeerId => _peerId;

            private int P(int v) { return Dpi.Px(v, _dpi); }

            public StoryChip(long peerId, string name, bool unseen, bool dark, Color accent, Func<long, Image> avatarGetter, int dpi)
            {
                _peerId = peerId; _name = name ?? ""; _unseen = unseen; _dark = dark; _accent = accent; _avatar = avatarGetter; _dpi = dpi;
                ApplyMetrics(); Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
            }

            private void ApplyMetrics() { Size = new Size(P(64), P(88)); Margin = new Padding(P(4), P(2), P(4), P(2)); }

            void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { _dpi = newDpi; ApplyMetrics(); Invalidate(); }   // SET, never multiply

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, ThemeHelper.PaletteFor(_dark).ListBg);   // step 5.9
                int D = P(50); int cx = (Width - D) / 2, cy = P(6);
                var avr = new Rectangle(cx, cy, D, D);
                var ring = Rectangle.Inflate(avr, P(3), P(3));
                using (var rp = new Pen(_unseen ? ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).ListBg, 3.0) : (ThemeHelper.PaletteFor(_dark).StoryRingIdle), Dpi.F(_unseen ? 2.5f : 1.5f, _dpi)))
                    g.DrawEllipse(rp, ring);
                var img = _avatar != null ? _avatar(_peerId) : null;
                if (img != null)
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(img, avr); g.ResetClip(); }
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_peerId))) g.FillEllipse(b, avr);
                    string ltr = !string.IsNullOrEmpty(_name) ? DrawHelper.Initial(_name) : "?";
                    DrawHelper.DrawInitial(g, ltr, FontHelper.ForAt(ltr, 15f, _dpi, FontStyle.Bold), avr, Color.White);   // ink-centred (owner report 2026-10-14)
                }
                Color nc = ThemeHelper.PaletteFor(_dark).StoryName;
                TextRenderer.DrawText(g, _name, FontHelper.ForAt(_name, 8f, _dpi), new Rectangle(0, cy + D + P(3), Width, P(15)), nc,   // shared: never dispose
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

        /// <summary>FOLDER-SIDEBAR: rebuild the folder navigator on screen — the tab bar (tab mode, and side-panel mode in
        /// one column) or the side rail. Only the one shown is rebuilt: each rebuild re-registers the badge sources, so
        /// rebuilding both left the hidden one's tiles as the only badges kept current. The other is rebuilt when it is
        /// swapped in (MainForm.Columns SwapFolderNavigator).</summary>
        private void RebuildFolders()
        {
            if (FolderBarShown) RebuildFolderBar();
            else if (_folderRail != null) RebuildFolderRail();
            UpdateBackButton();
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
            _folderRail.BackColor = ThemeHelper.PaletteFor(_dark).FolderRailBg;

            AddRailItem("\U0001F4AC", "All", AllUnread, !_showArchive && _activeFolder == null, () => SetActiveFolder(null), null);
            foreach (var f in _folders)
            {
                if (f == null) continue;
                var df = f as TL.DialogFilter;
                string title; try { title = (f.Title?.text ?? "").Trim(); } catch { title = ""; }
                if (string.IsNullOrEmpty(title)) title = "Folder";
                string icon = !string.IsNullOrEmpty(df?.emoticon) ? df.emoticon : "\U0001F4C1";   // 📁 default
                var ff = f;
                AddRailItem(icon, title, () => FolderUnread(ff), !_showArchive && ReferenceEquals(f, _activeFolder), () => SetActiveFolder(ff), ff);
            }
            if (_allChats.Any(c => c.Archived))
                AddRailItem("\U0001F5C4", "Archive", ArchiveUnread, _showArchive, SetArchive, null);   // 🗄

            _folderRail.ResumeLayout();
        }

        private void AddRailItem(string icon, string label, Func<int> unread, bool active, Action onClick, TL.DialogFilterBase folder)
        {
            var it = new FolderRailItem(icon, label, unread(), active, _dark, _accent, WindowDpiValue) { Width = ContentWidth(_folderRail) };
            it.Clicked += () =>
            {
                // one column, a chat in front (the rail stays beside it): a folder means its list — close the chat first
                if (_oneColumn && _selectedChat != null && !_listInFront) CloseOpenChat();
                onClick();
            };
            it.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowFolderMenu(folder, it.PointToScreen(e.Location)); };   // T13
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
            UpdateBackButton();   // one column: the back arrow's count of the other chats
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
        private sealed class FolderTabItem : Control, IFolderBadge, IDpiAware
        {
            private readonly string _label;
            private int _unread;
            private readonly bool _active, _dark;
            private readonly Color _accent;
            private int _dpi;   // step 3.4: the window's DPI (see StoryChip)
            public event Action Clicked;

            public int Unread { set { if (_unread != value) { _unread = value; RecalcWidth(); Invalidate(); } } }

            private int P(int v) { return Dpi.Px(v, _dpi); }

            public FolderTabItem(string label, int unread, bool active, bool dark, Color accent, int dpi)
            {
                _label = label; _unread = unread; _active = active; _dark = dark; _accent = accent; _dpi = dpi;
                Height = P(28); Margin = new Padding(P(2), P(3), P(2), P(3));
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
                RecalcWidth();
            }

            void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
            {
                _dpi = newDpi;
                Height = P(28); Margin = new Padding(P(2), P(3), P(2), P(3));   // SET from logical, never multiply
                RecalcWidth();
                Invalidate();
            }

            private static string BadgeText(int n) => n > 99 ? "99+" : n.ToString();

            // Shared per-DPI fonts (FontCache): never dispose.
            private Font LabelFont { get { return FontHelper.UiAt(9.5f, _dpi, _active ? FontStyle.Bold : FontStyle.Regular); } }
            private Font BadgeFont { get { return FontHelper.UiAt(7f, _dpi, FontStyle.Bold); } }

            private void RecalcWidth()
            {
                int tw = TextRenderer.MeasureText(_label, LabelFont).Width;
                int extra = 0;
                if (_unread > 0)
                    extra = P(5) + Math.Max(P(15), TextRenderer.MeasureText(BadgeText(_unread), BadgeFont).Width + P(7));
                Width = Math.Max(P(46), P(12) + tw + extra);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, ThemeHelper.PaletteFor(_dark).ListBg);   // step 5.9

                Color fg = _active ? ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).ListBg) : (ThemeHelper.PaletteFor(_dark).FolderTabFg);
                var lf = LabelFont;
                int tw = TextRenderer.MeasureText(_label, lf).Width;
                TextRenderer.DrawText(g, _label, lf, new Rectangle(P(6), 0, tw + P(4), Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (_unread > 0)
                {
                    string t = BadgeText(_unread);
                    var bf = BadgeFont;
                    int bw = Math.Max(P(15), TextRenderer.MeasureText(t, bf).Width + P(7));
                    var badge = new Rectangle(P(6) + tw + P(5), (Height - P(15)) / 2, bw, P(15));
                    using (var bb = new SolidBrush(_accent))
                    using (var pth = DrawHelper.RoundedRect(badge, P(7))) g.FillPath(bb, pth);
                    TextRenderer.DrawText(g, t, bf, badge, ColorMath.OnColor(_accent),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        /// <summary>One folder tile in the side rail: centered icon, label under it, unread badge on the icon
        /// corner, active = accent tint + left bar. Owner-drawn, touch-sized, no per-tile RPC.</summary>
        private sealed class FolderRailItem : Control, IFolderBadge, IDpiAware
        {
            private readonly string _icon, _label;
            private int _unread;
            private bool _active;
            private readonly bool _dark;
            private readonly Color _accent;
            private int _dpi;   // step 3.4: the window's DPI (see StoryChip)
            public event Action Clicked;

            public int Unread { get { return _unread; } set { if (_unread != value) { _unread = value; Invalidate(); } } }
            public bool Active { get { return _active; } set { if (_active != value) { _active = value; Invalidate(); } } }

            private int P(int v) { return Dpi.Px(v, _dpi); }

            public FolderRailItem(string icon, string label, int unread, bool active, bool dark, Color accent, int dpi)
            {
                _icon = icon; _label = label; _unread = unread; _active = active; _dark = dark; _accent = accent; _dpi = dpi;
                Height = P(60); Margin = new Padding(0);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
            }

            // The width follows the rail (DpiScaler scales it with the rail column); the height is SET from logical.
            void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { _dpi = newDpi; Height = P(60); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color bg = ThemeHelper.PaletteFor(_dark).FolderRailBg;
                g.Clear(bg);
                if (_active)
                {
                    using (var tint = new SolidBrush(ColorMath.WithAlpha(_accent, 30))) g.FillRectangle(tint, 0, 0, Width, Height);
                    using (var bar = new SolidBrush(ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).FolderRailBg, 3.0))) g.FillRectangle(bar, 0, P(8), P(3), Height - P(16));
                }
                Color fg = _active ? ColorMath.Ink(_accent, ThemeHelper.PaletteFor(_dark).FolderRailBg) : (ThemeHelper.PaletteFor(_dark).FolderRailFg);
                // Shared per-DPI fonts (FontCache): never dispose.
                TextRenderer.DrawText(g, _icon, FontCache.Get("Segoe UI Emoji", 16f, FontStyle.Regular, _dpi), new Rectangle(0, P(5), Width, P(26)), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, _label, FontHelper.UiAt(7.25f, _dpi, _active ? FontStyle.Bold : FontStyle.Regular), new Rectangle(P(1), P(37), Width - P(2), P(16)), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (_unread > 0)
                {
                    string t = _unread > 99 ? "99+" : _unread.ToString();
                    var bf = FontHelper.UiAt(6.75f, _dpi, FontStyle.Bold);
                    int bw = Math.Max(P(15), TextRenderer.MeasureText(t, bf).Width + P(7));
                    var badge = new Rectangle(Width / 2 + P(5), P(3), bw, P(15));
                    using (var bb = new SolidBrush(_accent))
                    using (var pth = DrawHelper.RoundedRect(badge, P(7))) g.FillPath(bb, pth);
                    TextRenderer.DrawText(g, t, bf, badge, ColorMath.OnColor(_accent),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        // ── T13: make, edit, move and delete folders ───────────────────────────────────────────────────────────
        /// <summary>Right-click on a folder tab / rail item. All and Archive offer only "New folder"; a shared folder (a
        /// chatlist) can be moved but not edited or deleted here.</summary>
        private void ShowFolderMenu(TL.DialogFilterBase folder, Point screenPt)
        {
            if (_service == null || _folders == null) return;
            var menu = new ThemedContextMenuStrip();
            AddMenuItem(menu, "New folder…", () => EditFolder(null));
            if (folder != null)
            {
                var real = _folders.Where(f => f != null).ToList();
                int at = real.FindIndex(f => f.ID == folder.ID);
                if (folder is TL.DialogFilter df) AddMenuItem(menu, "Edit folder…", () => EditFolder(df));
                if (at > 0) AddMenuItem(menu, _folderRail != null && _folderRail.Visible ? "Move up" : "Move left", () => MoveFolder(folder.ID, -1));
                if (at >= 0 && at < real.Count - 1) AddMenuItem(menu, _folderRail != null && _folderRail.Visible ? "Move down" : "Move right", () => MoveFolder(folder.ID, 1));
                if (folder is TL.DialogFilter dd)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    AddMenuItem(menu, "Delete folder", () => DeleteFolder(dd));
                }
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.ShowAt(screenPt);
        }

        /// <summary>New (src null) or edit. An edit starts from a FRESH read of that folder, as TogglePinInFolderAsync does:
        /// the cached copy can be hours old, and writing it back would undo folder changes made on another device.</summary>
        private async void EditFolder(TL.DialogFilter cached)
        {
            TL.DialogFilterBase[] fresh;
            try { fresh = await _service.GetDialogFiltersFreshAsync(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Folder", "Couldn't reach Telegram: " + ex.Message, "OK"); return; }
            if (IsDisposed) return;
            TL.DialogFilter src = null;
            if (cached != null)
            {
                src = fresh.FirstOrDefault(f => f != null && f.ID == cached.id) as TL.DialogFilter;
                if (src == null) { ThemedDialog.Show(this, "Folder", "That folder no longer exists, or is now a shared folder.", "OK"); await ReloadFoldersAsync(); return; }
            }
            int newId = Math.Max(2, fresh.Where(f => f != null).Select(f => f.ID).DefaultIfEmpty(1).Max() + 1);
            TL.DialogFilter result;
            using (var form = new FolderEditForm(src, newId, _allChats, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
                result = form.Result;
            }
            try { await _service.UpdateDialogFilterAsync(result.id, result); }
            catch (Exception ex)
            {
                Logger.Diag("[FOLDER] save id=" + result.id + " FAILED — " + ex.Message);
                ThemedDialog.Show(this, "Folder", "Couldn't save the folder: " + ex.Message, "OK");
                return;
            }
            Logger.Diag("[FOLDER] saved id=" + result.id + (src == null ? " (new)" : ""));
            await ReloadFoldersAsync();
        }

        private async void DeleteFolder(TL.DialogFilter folder)
        {
            string title = "";
            try { title = folder.title?.text ?? ""; } catch { }
            if (ThemedDialog.Show(this, "Delete folder", "Delete the folder \"" + title + "\"? Its chats stay where they are.", "Delete", "Cancel") != 0) return;
            try { await _service.UpdateDialogFilterAsync(folder.id, null); }
            catch (Exception ex) { ThemedDialog.Show(this, "Folder", "Couldn't delete the folder: " + ex.Message, "OK"); return; }
            Logger.Diag("[FOLDER] deleted id=" + folder.id);
            await ReloadFoldersAsync();
        }

        /// <summary>Swaps a folder with its neighbour and sends the whole order (messages.updateDialogFiltersOrder). The
        /// default "All" element (null in WTC) is left out: the app always shows All first.</summary>
        private async void MoveFolder(int id, int delta)
        {
            var order = _folders.Where(f => f != null).Select(f => f.ID).ToList();
            int i = order.IndexOf(id), j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            order[i] = order[j]; order[j] = id;
            try { await _service.Client.Messages_UpdateDialogFiltersOrder(order.ToArray()); }
            catch (Exception ex) { ThemedDialog.Show(this, "Folder", "Couldn't move the folder: " + ex.Message, "OK"); return; }
            await ReloadFoldersAsync();
        }

        /// <summary>After a folder write: the server's list, with the open folder kept open by ID (the objects are new, and
        /// the bar marks the active tab by reference). A failed read keeps the old list rather than emptying the bar.</summary>
        private async System.Threading.Tasks.Task ReloadFoldersAsync()
        {
            TL.DialogFilterBase[] fresh;
            try { fresh = await _service.GetDialogFiltersFreshAsync(); }
            catch (Exception ex) { Logger.Diag("[FOLDER] reload failed — " + ex.Message); return; }
            if (IsDisposed) return;
            int? activeId = _activeFolder != null ? _activeFolder.ID : (int?)null;
            _folders = fresh;
            if (_showArchive) { RebuildFolders(); return; }
            SetActiveFolder(activeId == null ? null : fresh.FirstOrDefault(f => f != null && f.ID == activeId.Value));
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

        internal static long PeerIdOf(InputPeer p)
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

    }
}
