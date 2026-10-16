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
            ArmMuteExpiry();
        }

        private System.Windows.Forms.Timer _muteExpiry;

        /// <summary>Redesign T1 — a timed mute (1 h / 8 h / 2 days, here or on another device) ends on its own: the
        /// notifications already read MuteUntil, but the bell in the list and the composer's Mute/Unmute were computed
        /// once. Wakes at the next end time (within the timer's ~24-day reach; later ones are re-armed by any relist or
        /// notify update before then) and recomputes.</summary>
        private void ArmMuteExpiry()
        {
            DateTime now = DateTime.UtcNow, next = DateTime.MaxValue;
            foreach (var e in _allChats)
                if (e.MuteUntil.HasValue && e.MuteUntil.Value > now && e.MuteUntil.Value < next) next = e.MuteUntil.Value;
            if (_muteExpiry == null)
            {
                _muteExpiry = new System.Windows.Forms.Timer();
                _muteExpiry.Tick += (s, e) =>
                {
                    _muteExpiry.Stop();
                    if (IsDisposed) return;
                    if (LogOn) Logger.Diag("[NOTIFY] a timed mute ended: recomputing the list's mutes");
                    ReapplyEffectiveMutes();   // re-arms for the next one
                    if (_selectedChat != null) ResolveAndApplyComposer(_selectedChat);
                };
            }
            _muteExpiry.Stop();
            if (next == DateTime.MaxValue) return;
            double ms = (next - now).TotalMilliseconds + 1000;
            if (ms >= int.MaxValue) return;
            _muteExpiry.Interval = (int)Math.Max(1000, ms);
            _muteExpiry.Start();
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
            ArmMuteExpiry();                              // T1: a timed mute set on another device ends here too
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
            // TA-33/A4 — read on ANOTHER device retires this chat's Action Center entries too. Reusing the
            // read signals that already exist rather than inventing a second notion of "read" is the point;
            // this is the one that fires when you read on your phone, which is when a stale desktop entry
            // is most annoying. RemoveGroup, not per-message Remove: the signal is a peer + a watermark.
            if (entry.UnreadCount == 0) ShellNotify.RemoveGroup(peerId);
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
            // OWNER BUG 2026-10-15 ("older messages keep coming one by one"): a getDifference catch-up arrives through this
            // same path, oldest first. The chat list already holds each chat's newest message and unread count from the
            // dialogs, so an incoming message that is not newer than that is HISTORY, not news: it must not rewrite the
            // preview, bump a badge or land at the bottom of the open chat. Ids only grow within a chat (per channel; per
            // account for the rest), so the id decides. Own messages are left alone: a send's echo can trail a newer
            // incoming message, and its pending bubble must still be swapped.
            if (!outgoing && IsReplayedHistory(peerId, m.ID))
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UM] incoming id=" + m.ID + " peer=" + peerId + " -> replayed history, skipped");
                return;
            }
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

        /// <summary>True when the chat list already shows a message at least as new as <paramref name="messageId"/> for
        /// this chat — i.e. the message is a catch-up of history (see HandleIncomingMessage).</summary>
        private bool IsReplayedHistory(long peerId, int messageId)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            return entry != null && entry.TopMessageId > 0 && messageId <= entry.TopMessageId;
        }

        private void UpdateChatListForMessageCore(long peerId, MessageBase m, bool outgoing)
        {
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            // Owner bug 2026-10-15: an OLDER message never becomes the preview (a catch-up, or a send's echo that trails a
            // newer message) — the row keeps its newest message, date, position and badge.
            if (entry != null && entry.TopMessageId > 0 && m.ID < entry.TopMessageId)
            {
                if (LogOn) System.Diagnostics.Debug.WriteLine("[UPDATE] chat-list keep peer=" + peerId + " id=" + m.ID + " < top=" + entry.TopMessageId);
                return;
            }
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

    }
}
