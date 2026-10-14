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

    }
}
