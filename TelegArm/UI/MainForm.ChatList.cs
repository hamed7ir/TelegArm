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
            // and offered "✖ Delete chat" on your own saved notes. The same flag is what future entries
            // (Block, TA-19/S3) must consult — the check belongs to the context, not to each entry.
            if (!c.IsSelf)
                AddMenuItem(menu, "✖   " + (c.IsUser ? "Delete chat" : LeaveLabelFor(entry)),
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

    }
}
