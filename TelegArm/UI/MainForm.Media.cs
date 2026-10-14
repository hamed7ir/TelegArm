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
            var anchor = _dlIndicator != null   // step 3.4: logical offsets; the work-area clamp below stays raw
                ? _dlIndicator.PointToScreen(new Point(_dlIndicator.Width - p.Width, _dlIndicator.Height + Px(2)))
                : PointToScreen(new Point(ClientSize.Width - p.Width - Px(8), Px(64)));
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
            StopRemeasure();                // step 3.4 (review): a DPI re-measure queue must not touch the next chat's view

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

    }
}
