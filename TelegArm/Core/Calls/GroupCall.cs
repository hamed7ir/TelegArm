using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TelegArm.Helpers;
using TL;

namespace TelegArm.Core.Calls
{
    public enum GroupCallPhase { Joining, Connecting, Connected, Ended }

    /// <summary>A chat's current group call (what the header button and the join bar show).</summary>
    public sealed class GroupCallInfo
    {
        public InputGroupCall Call;
        public int Count;
        public string Title;
        public bool Rtmp, Conference;
    }

    /// <summary>The group call we are in, as the UI sees it. Changed only on the UI thread.</summary>
    public sealed class GroupCallSession
    {
        public long PeerId { get; internal set; }          // the chat's id
        public InputPeer ChatPeer { get; internal set; }
        public string Title { get; internal set; }
        public InputGroupCall Call { get; internal set; }
        public GroupCallPhase Phase { get; internal set; }
        public bool Ended { get { return Phase == GroupCallPhase.Ended; } }
        public string EndText { get; internal set; }
        public DateTime? ConnectedUtc { get; internal set; }
        public int SignalBars { get; internal set; } = -1;
        /// <summary>We are muted (joined muted, as Telegram Desktop).</summary>
        public bool Muted { get; internal set; } = true;
        /// <summary>An admin muted us: we cannot unmute, only raise a hand.</summary>
        public bool ForceMuted { get; internal set; }
        public bool HandRaised { get; internal set; }
        public bool CanManage { get; internal set; }
        public float MyLevel { get; internal set; }
        internal int MySpeakingUntil;
        public bool MeSpeaking { get { return !Muted && MySpeakingUntil != 0 && unchecked(MySpeakingUntil - Environment.TickCount) > 0; } }
        public string Note { get; internal set; }
        public readonly GroupParticipants Participants = new GroupParticipants();
        public int DurationSeconds { get { return ConnectedUtc == null ? 0 : (int)(DateTime.UtcNow - ConnectedUtc.Value).TotalSeconds; } }

        internal NativeGroupCall Native;
        internal int Ssrc;
        internal int Version = -1;
        internal bool Joined, Rejoining, Checking, Reloading, ReloadAgain;
    }

    /// <summary>
    /// Telegram group calls (voice chats) over MTProto (WTelegramClient's phone.* methods) and tgcalls' group engine
    /// (NativeGroupCall), following Telegram Desktop 7.3.1 (calls_group_call.cpp): join MUTED with tgcalls' join payload in
    /// phone.joinGroupCall; the answer's updateGroupCallConnection JSON goes back into tgcalls, then the RTC mode; while it
    /// connects, phone.checkGroupCall every 4 s (our ssrc gone = rejoin: mode none, a new payload, join again); participants
    /// from phone.getGroupCall (+ pages) and updateGroupCallParticipants (a version gap reloads them); who speaks from the
    /// 100 ms audio levels; mute through phone.editGroupCallParticipant; leave with our ssrc. One call at a time, and not
    /// during a 1:1 call. Everything public runs on the UI thread, and every event is raised there.
    /// </summary>
    public sealed class GroupCallManager
    {
        private readonly TelegramService _svc;
        private readonly SynchronizationContext _ui;
        private readonly Dictionary<long, GroupCallInfo> _info = new Dictionary<long, GroupCallInfo>();
        private readonly Dictionary<long, string> _names = new Dictionary<long, string>();   // key as GroupParticipants.Key
        private readonly HashSet<long> _gone = new HashSet<long>();   // chats whose call we saw end (their call_active flag lags)
        private Task _teardown;

        public GroupCallSession Current { get; private set; }
        public TelegramService Service { get { return _svc; } }
        /// <summary>A 1:1 call is on (set by the window): no group call meanwhile.</summary>
        public Func<bool> OtherBusy;

        /// <summary>The session changed (phase, mute, participants, end).</summary>
        public event Action<GroupCallSession> Changed;
        /// <summary>New audio levels (every 100 ms while in a call): repaint who is speaking.</summary>
        public event Action<GroupCallSession> LevelsChanged;
        /// <summary>A chat's call appeared, changed or ended (peer id).</summary>
        public event Action<long> InfoChanged;

        public GroupCallManager(TelegramService svc, SynchronizationContext ui) { _svc = svc; _ui = ui; }

        public static bool Available { get { return TgCalls.Available && TgCalls.GroupAvailable; } }
        public bool Busy { get { return Current != null && !Current.Ended; } }
        private WTelegram.Client Client { get { return _svc.Client; } }

        /// <summary>Every subscriber, each guarded: a throwing one must not leave a session half-ended (review 2026-10-16).</summary>
        private void Raise(GroupCallSession s)
        {
            var h = Changed;
            if (h == null) return;
            foreach (Action<GroupCallSession> d in h.GetInvocationList())
                try { d(s); } catch (Exception ex) { Logger.Diag("[GCALL] Changed handler: " + ex); }
        }
        private void Post(Action a) { _ui.Post(_ => { try { a(); } catch (Exception ex) { Logger.Diag("[GCALL] UI handler: " + ex); } }, null); }

        public GroupCallInfo InfoFor(long peerId) { GroupCallInfo i; return _info.TryGetValue(peerId, out i) ? i : null; }
        /// <summary>We saw this chat's call end (since then nothing says another began).</summary>
        public bool KnownEnded(long peerId) { return _gone.Contains(peerId); }

        // ── names ───────────────────────────────────────────────────────────────────────────────────────────────────

        private void Learn(IDictionary<long, User> users, IDictionary<long, ChatBase> chats)
        {
            if (users != null) foreach (var u in users.Values) if (u != null) _names[u.id] = UserName(u);
            if (chats != null) foreach (var c in chats.Values) if (c != null) _names[-c.ID] = c.Title;
        }

        private static string UserName(User u)
        {
            string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            return n.Length > 0 ? n : (u.username ?? "Deleted account");
        }

        private string Name(long id, bool isUser)
        {
            string n;
            if (_names.TryGetValue(isUser ? id : -id, out n)) return n;
            var up = _svc.Updates;
            if (isUser && up != null && up.Users != null) { User u; if (up.Users.TryGetValue(id, out u) && u != null) return UserName(u); }
            if (!isUser && up != null && up.Chats != null) { ChatBase c; if (up.Chats.TryGetValue(id, out c) && c != null) return c.Title; }
            return null;
        }

        // ── a chat's call ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reads a chat's current call (full chat info, then the call itself); null when there is none.</summary>
        public async Task<GroupCallInfo> RefreshInfoAsync(InputPeer peer, long peerId)
        {
            var full = await Client.GetFullChat(peer);   // WTC's helper: channels.getFullChannel or messages.getFullChat
            InputGroupCallBase call = null;
            var cf = full != null ? full.full_chat as ChannelFull : null;
            if (cf != null && (cf.flags & ChannelFull.Flags.has_call) != 0) call = cf.call;
            var bf = full != null ? full.full_chat as ChatFull : null;
            if (bf != null && (bf.flags & ChatFull.Flags.has_call) != 0) call = bf.call;
            var input = call as InputGroupCall;
            if (input == null) { _gone.Add(peerId); if (_info.Remove(peerId)) Raise(peerId); return null; }
            var r = await Client.Phone_GetGroupCall(input, 3);
            Learn(r.users, r.chats);
            var g = r.call as GroupCall;
            if (g == null) { _gone.Add(peerId); if (_info.Remove(peerId)) Raise(peerId); return null; }
            var info = Remember(peerId, g);
            return info;
        }

        private void Raise(long peerId)
        {
            var h = InfoChanged;
            if (h == null) return;
            foreach (Action<long> d in h.GetInvocationList())
                try { d(peerId); } catch (Exception ex) { Logger.Diag("[GCALL] InfoChanged handler: " + ex); }
        }

        private GroupCallInfo Remember(long peerId, GroupCall g)
        {
            GroupCallInfo i;
            _gone.Remove(peerId);
            if (!_info.TryGetValue(peerId, out i)) { i = new GroupCallInfo(); _info[peerId] = i; }
            i.Call = new InputGroupCall { id = g.id, access_hash = g.access_hash };
            i.Count = g.participants_count;
            if ((g.flags & GroupCall.Flags.has_title) != 0) i.Title = g.title;
            i.Rtmp = (g.flags & GroupCall.Flags.rtmp_stream) != 0;
            i.Conference = (g.flags & GroupCall.Flags.conference) != 0;
            Raise(peerId);
            return i;
        }

        // ── join / start / leave ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Joins the chat's voice chat (muted). <paramref name="canManage"/>: we may end it for everyone.</summary>
        public async Task JoinAsync(InputPeer peer, long peerId, string title, bool canManage)
        {
            if (Busy) throw new InvalidOperationException("You are already in a voice chat.");
            if (OtherBusy != null && OtherBusy()) throw new InvalidOperationException("End the call first.");
            if (!Available) throw new InvalidOperationException("Voice chats are unavailable here (" + TgCalls.Status + ").");
            var s = new GroupCallSession { PeerId = peerId, ChatPeer = peer, Title = title, CanManage = canManage, Phase = GroupCallPhase.Joining };
            Current = s;
            Raise(s);
            try
            {
                var info = await RefreshInfoAsync(peer, peerId);
                if (s.Ended) return;
                if (info == null || info.Call == null) throw new InvalidOperationException("There is no voice chat here now.");
                if (info.Conference || info.Rtmp) throw new InvalidOperationException("This kind of voice chat (a live stream or an encrypted call) isn't supported yet.");
                s.Call = info.Call;
                if (!string.IsNullOrEmpty(info.Title)) s.Title = info.Title;
                await LoadParticipantsAsync(s);
                if (s.Ended) return;
                StartNative(s);
            }
            catch (Exception ex)
            {
                Logger.Diag("[GCALL] join failed: " + ex.Message);
                End(s, Friendly(ex, "Couldn't join the voice chat"));
            }
        }

        /// <summary>Starts a voice chat in the chat (an admin who may manage calls), then joins it.</summary>
        public async Task StartAsync(InputPeer peer, long peerId, string title)
        {
            if (Busy) throw new InvalidOperationException("You are already in a voice chat.");
            if (OtherBusy != null && OtherBusy()) throw new InvalidOperationException("End the call first.");
            var r = await Client.Phone_CreateGroupCall(peer, new Random().Next());
            foreach (var u in r.UpdateList) { var ugc = u as UpdateGroupCall; var g = ugc != null ? ugc.call as GroupCall : null; if (g != null) Remember(peerId, g); }
            await JoinAsync(peer, peerId, title, true);
        }

        /// <summary>The call and its participants from the server. Built into a FRESH list and swapped in at the end (the
        /// version with it), so updates applied meanwhile are never half-overwritten by older pages; one at a time (a
        /// request during it runs once more after it).</summary>
        private async Task LoadParticipantsAsync(GroupCallSession s)
        {
            if (s.Reloading) { s.ReloadAgain = true; return; }
            s.Reloading = true;
            try
            {
                do
                {
                    s.ReloadAgain = false;
                    var r = await Client.Phone_GetGroupCall(s.Call, 100);
                    if (s.Ended) return;
                    Learn(r.users, r.chats);
                    var g = r.call as GroupCall;
                    if (g == null) { End(s, "The voice chat has ended"); return; }   // GroupCallDiscarded
                    var fresh = new GroupParticipants();
                    fresh.Apply(r.participants, Name);
                    string next = r.participants_next_offset;
                    for (int page = 0; !string.IsNullOrEmpty(next) && page < 5; page++)   // up to 600 listed; the count says the rest
                    {
                        var pr = await Client.Phone_GetGroupParticipants(s.Call, null, null, next, 100);
                        if (s.Ended) return;
                        Learn(pr.users, pr.chats);
                        fresh.Apply(pr.participants, Name);
                        next = pr.next_offset;
                    }
                    s.Participants.ReplaceWith(fresh);
                    s.Participants.ServerCount = g.participants_count;
                    if ((g.flags & GroupCall.Flags.has_title) != 0 && !string.IsNullOrEmpty(g.title)) s.Title = g.title;
                    if ((g.flags & GroupCall.Flags.creator) != 0) s.CanManage = true;
                    s.Version = g.version;
                    ReconcileSelf(s);
                    ApplyVolumes(s);
                    Raise(s);
                } while (s.ReloadAgain && !s.Ended);
            }
            finally { s.Reloading = false; }
        }

        private void StartNative(GroupCallSession s)
        {
            bool arm = string.Equals(System.IO.Path.GetFileName(TgCalls.Folder), "ARM", StringComparison.OrdinalIgnoreCase);
            var native = NativeGroupCall.Create(true, arm, wire: n =>
            {
                n.JoinPayload += (ssrc, json) => Post(() => { var t = SendJoinAsync(s, ssrc, json); });
                n.NetworkChanged += (connected, mode) => Post(() => OnNetwork(s, connected));
                n.Levels += levels => Post(() => OnLevels(s, levels));
                n.SignalBarsChanged += b => Post(() => { if (!s.Ended) { s.SignalBars = b; Raise(s); } });
            });
            if (native == null) throw new InvalidOperationException("Voice chats need a newer call library (" + TgCalls.Status + ").");
            s.Native = native;
            native.SetMute(s.Muted || s.ForceMuted);   // an Unmute while it was still joining (it starts muted)
            native.EmitJoin();
        }

        /// <summary>phone.joinGroupCall with tgcalls' payload; the answer's connection JSON goes back into tgcalls. The answer
        /// comes in the RPC's own result (WTC does not pump our own RPC's updates).</summary>
        private async Task SendJoinAsync(GroupCallSession s, uint ssrc, string json)
        {
            if (s.Ended || s.Native == null) return;
            s.Ssrc = unchecked((int)ssrc);
            bool sent = false, sentMuted = s.Muted || s.ForceMuted;
            try
            {
                var r = await Client.Phone_JoinGroupCall(s.Call, new InputPeerSelf(), new DataJSON { data = json }, muted: sentMuted, video_stopped: true);
                sent = true;   // the server has us now: any failure below must leave
                if (s.Ended) { var t = LeaveQuietlyAsync(s.Call, s.Ssrc); return; }
                Learn(r.Users, r.Chats);
                bool connection = false;
                foreach (var u in r.UpdateList)
                {
                    var c = u as UpdateGroupCallConnection;
                    if (c != null && (c.flags & UpdateGroupCallConnection.Flags.presentation) == 0 && c.params_ != null)
                        connection = s.Native.SetJoinResponse(c.params_.data);
                    else OnUpdate(u);
                }
                if (!connection) throw new InvalidOperationException("The server didn't answer the join.");
                s.Native.SetMode(TgCalls.GroupModeRtc);
                ApplyVolumes(s);
                s.Joined = true;
                s.Rejoining = false;
                bool wantMuted = s.Muted || s.ForceMuted;
                if (wantMuted != sentMuted) { var e = EditSelfAsync(s, wantMuted, null); }   // toggled while the join was in flight
                if (s.Phase != GroupCallPhase.Connected) s.Phase = GroupCallPhase.Connecting;
                Logger.Diag("[GCALL] joined, ssrc " + s.Ssrc);
                Raise(s);
                var check = CheckJoinedAsync(s);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("GROUPCALL_SSRC_DUPLICATE_MUCH")) { Logger.Diag("[GCALL] ssrc taken: rejoin"); s.Rejoining = false; Rejoin(s); return; }
                Logger.Diag("[GCALL] join: " + ex.Message);
                if (sent) { var l = LeaveQuietlyAsync(s.Call, s.Ssrc); }   // accepted, then failed here: no ghost participant
                End(s, Friendly(ex, "Couldn't join the voice chat"));
            }
        }

        /// <summary>While it connects: every 4 s ask the server whether our ssrc is still in the call (Telegram Desktop's
        /// checkJoined); gone = rejoin.</summary>
        private async Task CheckJoinedAsync(GroupCallSession s)
        {
            if (s.Checking) return;
            s.Checking = true;
            try
            {
                while (!s.Ended && s.Joined && s.Phase != GroupCallPhase.Connected)
                {
                    await Task.Delay(4000);
                    if (s.Ended || !s.Joined || s.Phase == GroupCallPhase.Connected || s.Rejoining) break;
                    int[] alive = null;
                    try { alive = await Client.Phone_CheckGroupCall(s.Call, new[] { s.Ssrc }); }
                    catch (Exception ex)
                    {
                        Logger.Diag("[GCALL] check: " + ex.Message);
                        string m = ex.Message ?? "";
                        if (s.Ended) break;
                        if (m.Contains("GROUPCALL_INVALID") || m.Contains("GROUPCALL_ALREADY_DISCARDED")) { End(s, "The voice chat has ended"); break; }
                        if (m.Contains("GROUPCALL_JOIN_MISSING") || m.Contains("GROUPCALL_FORBIDDEN")) { Rejoin(s); break; }
                        // anything else (the network, FLOOD_WAIT): ask again in 4 s
                    }
                    if (alive != null && !alive.Contains(s.Ssrc)) { Logger.Diag("[GCALL] our ssrc is gone: rejoin"); Rejoin(s); break; }
                }
            }
            finally { s.Checking = false; }
        }

        /// <summary>A new ssrc, keys and payload, and phone.joinGroupCall again.</summary>
        private void Rejoin(GroupCallSession s)
        {
            if (s.Ended || s.Native == null || s.Rejoining) return;
            s.Rejoining = true;
            s.Joined = false;
            s.Phase = GroupCallPhase.Joining;
            Raise(s);
            s.Native.SetMode(TgCalls.GroupModeNone);
            s.Native.EmitJoin();   // -> SendJoinAsync
        }

        private void OnNetwork(GroupCallSession s, bool connected)
        {
            if (s.Ended || !s.Joined) return;
            if (connected)
            {
                if (s.Phase != GroupCallPhase.Connected) Logger.Diag("[GCALL] connected");
                s.Phase = GroupCallPhase.Connected;
                if (s.ConnectedUtc == null) s.ConnectedUtc = DateTime.UtcNow;
            }
            else if (s.Phase == GroupCallPhase.Connected)
            {
                Logger.Diag("[GCALL] connection lost");
                s.Phase = GroupCallPhase.Connecting;
                var t = CheckJoinedAsync(s);
            }
            Raise(s);
        }

        private void OnLevels(GroupCallSession s, TgCalls.GroupLevel[] levels)
        {
            if (s.Ended) return;
            int now = Environment.TickCount;
            foreach (var l in levels)
            {
                float level = l.LevelMilli / 1000f;
                if (l.Ssrc == 0)
                {
                    s.MyLevel = level;
                    if (l.Voice != 0 && level > 0.05f) s.MySpeakingUntil = now + 800;
                    var me = s.Participants.Self;
                    if (me != null && !s.Muted) { me.Level = level; if (l.Voice != 0 && level > 0.05f) me.SpeakingUntil = now + 800; }
                    continue;
                }
                var p = s.Participants.BySource(unchecked((int)l.Ssrc));
                if (p == null) continue;
                p.Level = level;
                if (l.Voice != 0 && level > 0.2f) p.SpeakingUntil = now + 1500;   // Telegram Desktop's 0.2 and 1.5 s
            }
            var h = LevelsChanged;
            if (h != null) h(s);
        }

        /// <summary>Mutes or unmutes us (here and for the others).</summary>
        public void SetMuted(bool muted)
        {
            var s = Current;
            if (s == null || s.Ended) return;
            if (!muted && s.ForceMuted) { s.Note = "An admin muted you: raise your hand to ask to speak."; Raise(s); return; }
            s.Muted = muted;
            s.Note = null;
            if (s.Native != null) s.Native.SetMute(muted);
            Raise(s);
            if (s.Joined) { var t = EditSelfAsync(s, muted, null); }
        }

        /// <summary>Raises (or lowers) our hand: an admin muted us and we ask to speak.</summary>
        public Task RaiseHandAsync(bool raise)
        {
            var s = Current;
            if (s == null || s.Ended) return Task.FromResult(0);
            s.HandRaised = raise;
            Raise(s);
            return EditSelfAsync(s, null, raise);
        }

        private async Task EditSelfAsync(GroupCallSession s, bool? muted, bool? raiseHand)
        {
            try { await Client.Phone_EditGroupCallParticipant(s.Call, new InputPeerSelf(), muted, null, raiseHand, null, null, null); }
            catch (Exception ex) { Logger.Diag("[GCALL] edit self: " + ex.Message); }
        }

        /// <summary>Leaves (the others are told), and the call window closes.</summary>
        public async Task LeaveAsync()
        {
            var s = Current;
            if (s == null || s.Ended) return;
            bool joined = s.Joined;
            End(s, "You left the voice chat");
            if (joined) await LeaveQuietlyAsync(s.Call, s.Ssrc);
        }

        /// <summary>Ends the voice chat for everyone (an admin).</summary>
        public async Task EndForAllAsync()
        {
            var s = Current;
            if (s == null || s.Ended) return;
            bool joined = s.Joined;
            try
            {
                await Client.Phone_DiscardGroupCall(s.Call);
                End(s, "The voice chat has ended");
            }
            catch (Exception ex)
            {
                Logger.Diag("[GCALL] discard: " + ex.Message);
                if (s.Ended) return;
                End(s, "Couldn't end the voice chat for everyone; you left it.");
                if (joined) await LeaveQuietlyAsync(s.Call, s.Ssrc);
            }
        }

        private async Task LeaveQuietlyAsync(InputGroupCall call, int ssrc)
        {
            try { await Client.Phone_LeaveGroupCall(call, ssrc); }
            catch (Exception ex) { Logger.Diag("[GCALL] leave: " + ex.Message); }
        }

        // ── updates ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>From the update handler (UI thread): updateGroupCall / updateGroupCallParticipants / updateGroupCallConnection.</summary>
        public void OnUpdate(Update u)
        {
            var gc = u as UpdateGroupCall;
            if (gc != null) { OnGroupCall(gc); return; }
            var gp = u as UpdateGroupCallParticipants;
            if (gp != null) { OnParticipants(gp); return; }
        }

        private void OnGroupCall(UpdateGroupCall u)
        {
            long peerId = 0;
            if ((u.flags & UpdateGroupCall.Flags.has_peer) != 0 && u.peer != null) peerId = Math.Abs(GroupParticipants.Key(u.peer));
            var g = u.call as GroupCall;
            var gone = u.call as GroupCallDiscarded;
            long callId = g != null ? g.id : gone != null ? gone.id : 0;
            if (peerId == 0) foreach (var kv in _info) if (kv.Value.Call != null && kv.Value.Call.id == callId) peerId = kv.Key;
            if (g != null && peerId != 0) Remember(peerId, g);
            if (gone != null && peerId != 0) { _gone.Add(peerId); if (_info.Remove(peerId)) Raise(peerId); }
            var s = Current;
            if (s == null || s.Ended || s.Call == null || s.Call.id != callId) return;
            if (gone != null) { End(s, "The voice chat has ended"); return; }
            if (g != null)
            {
                s.Participants.ServerCount = g.participants_count;
                if ((g.flags & GroupCall.Flags.has_title) != 0 && !string.IsNullOrEmpty(g.title)) s.Title = g.title;
                Raise(s);
            }
        }

        private void OnParticipants(UpdateGroupCallParticipants u)
        {
            var s = Current;
            var call = u.call as InputGroupCall;
            if (s == null || s.Ended || s.Call == null || call == null || call.id != s.Call.id) return;
            // Telegram's rule (core.telegram.org/api/group-calls): an update carrying a versioned, left or just_joined entry is
            // SEQUENCED - version+1 applies, older is stale, a jump is a gap (reload); any other update applies as it is and
            // does not move the version.
            const GroupCallParticipant.Flags Sequenced = GroupCallParticipant.Flags.versioned | GroupCallParticipant.Flags.left | GroupCallParticipant.Flags.just_joined;
            bool seq = u.participants != null && u.participants.Any(p => (p.flags & Sequenced) != 0);
            if (seq && s.Version >= 0)
            {
                if (u.version < s.Version) return;   // stale
                if (u.version > s.Version + 1)
                {
                    Logger.Diag("[GCALL] participants version gap " + s.Version + " -> " + u.version + ": reload");
                    var t = ReloadAsync(s);
                    return;
                }
            }
            s.Participants.Apply(u.participants, Name);
            ApplyVolumes(s);
            if (seq) s.Version = Math.Max(s.Version, u.version);
            // our own entry left while we are joined = this device was dropped: rejoin (Telegram Desktop)
            if (s.Joined && s.Ssrc != 0 && u.participants != null && u.participants.Any(p => p.source == s.Ssrc && (p.flags & GroupCallParticipant.Flags.left) != 0))
            {
                Rejoin(s);
                return;
            }
            ReconcileSelf(s);
            Raise(s);
        }

        /// <summary>Our own entry -> IsSelf, and an admin's mute (we cannot unmute ourselves) -> ForceMuted (mic shut here
        /// too); lifted -> "You may speak now". After every incremental update AND every (re)load.</summary>
        private static void ReconcileSelf(GroupCallSession s)
        {
            var me = s.Participants.Self ?? (s.Ssrc != 0 ? s.Participants.BySource(s.Ssrc) : null);
            if (me == null) return;
            me.IsSelf = true;
            bool force = me.Muted && !me.CanSelfUnmute;
            if (force && !s.ForceMuted) { s.ForceMuted = true; s.Muted = true; if (s.Native != null) s.Native.SetMute(true); }
            else if (!force && s.ForceMuted) { s.ForceMuted = false; s.HandRaised = false; s.Note = "You may speak now."; }
        }

        /// <summary>"Muted for you" and the volume you set (on any of your devices) into tgcalls, per ssrc; 100 % is its default.</summary>
        private static void ApplyVolumes(GroupCallSession s)
        {
            var n = s.Native;
            if (n == null) return;
            foreach (var p in s.Participants.All)
            {
                if (p.IsSelf || p.Source == 0) continue;
                int permille = p.MutedByYou ? 0 : Math.Max(0, Math.Min(2000, p.Volume / 10));
                if (p.AppliedVolume == permille || (p.AppliedVolume == -1 && permille == 1000)) continue;
                n.SetVolume(unchecked((uint)p.Source), permille);
                p.AppliedVolume = permille;
            }
        }

        private async Task ReloadAsync(GroupCallSession s)
        {
            try { await LoadParticipantsAsync(s); }
            catch (Exception ex) { Logger.Diag("[GCALL] reload: " + ex.Message); }
        }

        // ── the end ─────────────────────────────────────────────────────────────────────────────────────────────────

        private void End(GroupCallSession s, string text)
        {
            if (s.Ended) return;
            s.Phase = GroupCallPhase.Ended;
            s.EndText = text;
            var native = s.Native;
            s.Native = null;
            Logger.Diag("[GCALL] ended: " + text + " after " + s.DurationSeconds + " s");
            if (native != null) StartTeardown(native);   // first: a throwing subscriber below must not leak the engine
            Raise(s);
        }

        private void StartTeardown(NativeGroupCall native)
        {
            _teardown = Task.Run(() =>
            {
                try
                {
                    var a = native.Stats();
                    Logger.Diag("[GCALL] audio: " + a.FramesRecorded + " mic frames, " + a.FramesRendered + " played, " +
                                a.PlaybackUnderruns + " speaker gaps, " + a.CaptureOverruns + " mic overruns");
                }
                catch { }
                try { native.Stop(5000); } catch (Exception ex) { Logger.Diag("[GCALL] stop: " + ex.Message); }
                try { native.Dispose(); } catch { }
            });
        }

        /// <summary>The app is closing: leave a live voice chat and wait, bounded, for the call library to stop.</summary>
        public void Shutdown(int timeoutMs)
        {
            var s = Current;
            if (s != null && !s.Ended) { var t = LeaveAsync(); }
            var td = _teardown;
            if (td != null) try { td.Wait(timeoutMs); } catch { }
        }

        private static string Friendly(Exception ex, string what)
        {
            if (ex is InvalidOperationException) return ex.Message;
            string m = ex.Message ?? "";
            if (m.Contains("GROUPCALL_FORBIDDEN")) return "You can't join this voice chat.";
            if (m.Contains("GROUPCALL_INVALID")) return "This voice chat has ended.";
            if (m.Contains("GROUPCALL_ALREADY_DISCARDED")) return "This voice chat has ended.";
            if (m.Contains("CHAT_ADMIN_REQUIRED")) return "Only admins can start a voice chat here.";
            return what + ": " + m;
        }
    }
}
