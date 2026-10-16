using System;
using System.Collections.Generic;
using System.Linq;
using TL;

namespace TelegArm.Core.Calls
{
    /// <summary>One person (or a chat speaking as itself) in a group call, as the window shows it.</summary>
    public sealed class GroupParticipant
    {
        public long PeerId { get; internal set; }         // a user id, or a chat / channel id (IsUser false)
        public bool IsUser { get; internal set; }
        public string Name { get; internal set; }
        public int Source { get; internal set; }           // the audio ssrc (TL int; tgcalls has its uint form)
        public bool Muted { get; internal set; }
        public bool CanSelfUnmute { get; internal set; }
        public bool MutedByYou { get; internal set; }
        public int Volume { get; internal set; } = 10000;  // 10000 = 100 %
        public long RaiseHandRating { get; internal set; }
        public string About { get; internal set; }
        public bool IsSelf { get; internal set; }
        public DateTime Date { get; internal set; }
        public DateTime ActiveDate { get; internal set; }
        public GroupCallParticipantVideo Video { get; internal set; }
        public float Level { get; internal set; }
        internal int SpeakingUntil;                       // Environment.TickCount
        internal int AppliedVolume = -1;                  // what tgcalls was told for this ssrc (permille), -1 = nothing yet
        public bool Speaking { get { return SpeakingUntil != 0 && unchecked(SpeakingUntil - Environment.TickCount) > 0; } }   // 0 = never (TickCount goes negative after 24.9 days up)
        public bool HandRaised { get { return RaiseHandRating > 0 && Muted && !CanSelfUnmute; } }
    }

    /// <summary>
    /// The participants of one group call: TL entries applied in order (left removes; a min entry keeps the volume and
    /// "muted by you"), looked up by ssrc for the levels, sorted the way the list shows them. No UI, no network.
    /// </summary>
    public sealed class GroupParticipants
    {
        private readonly Dictionary<long, GroupParticipant> _byPeer = new Dictionary<long, GroupParticipant>();
        private readonly Dictionary<int, GroupParticipant> _bySource = new Dictionary<int, GroupParticipant>();

        /// <summary>The server's count (participants_count): it includes those not loaded.</summary>
        public int ServerCount { get; set; }
        public int Count { get { return _byPeer.Count; } }

        /// <summary>Users positive, chats and channels negative (one key space).</summary>
        public static long Key(Peer p)
        {
            var u = p as PeerUser; if (u != null) return u.user_id;
            var c = p as PeerChannel; if (c != null) return -c.channel_id;
            var g = p as PeerChat; if (g != null) return -g.chat_id;
            return 0;
        }

        public void Clear() { _byPeer.Clear(); _bySource.Clear(); }

        /// <summary>Takes a freshly loaded list's entries (a reload), keeping what only this side knows for the same person
        /// on the same ssrc: what tgcalls was told about their volume, and whether they are speaking.</summary>
        public void ReplaceWith(GroupParticipants fresh)
        {
            foreach (var kv in fresh._byPeer)
            {
                GroupParticipant old;
                if (_byPeer.TryGetValue(kv.Key, out old) && old.Source == kv.Value.Source)
                {
                    kv.Value.AppliedVolume = old.AppliedVolume;
                    kv.Value.SpeakingUntil = old.SpeakingUntil;
                    kv.Value.Level = old.Level;
                    if (old.IsSelf) kv.Value.IsSelf = true;
                }
            }
            _byPeer.Clear(); _bySource.Clear();
            foreach (var kv in fresh._byPeer) _byPeer[kv.Key] = kv.Value;
            foreach (var kv in fresh._bySource) _bySource[kv.Key] = kv.Value;
        }

        public GroupParticipant BySource(int source) { GroupParticipant p; return _bySource.TryGetValue(source, out p) ? p : null; }
        public GroupParticipant ByPeer(long key) { GroupParticipant p; return _byPeer.TryGetValue(key, out p) ? p : null; }
        public GroupParticipant Self { get { return _byPeer.Values.FirstOrDefault(p => p.IsSelf); } }
        public IEnumerable<GroupParticipant> All { get { return _byPeer.Values; } }

        /// <summary>Applies server entries. <paramref name="name"/> names a peer (id, isUser), or null when unknown.</summary>
        public void Apply(IEnumerable<GroupCallParticipant> list, Func<long, bool, string> name)
        {
            if (list == null) return;
            foreach (var p in list)
            {
                if (p == null || p.peer == null) continue;
                long key = Key(p.peer);
                if (key == 0) continue;
                GroupParticipant x;
                if ((p.flags & GroupCallParticipant.Flags.left) != 0)
                {
                    if (_byPeer.TryGetValue(key, out x)) { _byPeer.Remove(key); if (_bySource.TryGetValue(x.Source, out var bs) && bs == x) _bySource.Remove(x.Source); }
                    continue;
                }
                if (!_byPeer.TryGetValue(key, out x)) { x = new GroupParticipant(); _byPeer[key] = x; }
                bool min = (p.flags & GroupCallParticipant.Flags.min) != 0;
                if (x.Source != 0 && x.Source != p.source && _bySource.TryGetValue(x.Source, out var old) && old == x) _bySource.Remove(x.Source);
                if (x.Source != p.source) x.AppliedVolume = -1;
                x.PeerId = Math.Abs(key);
                x.IsUser = key > 0;
                x.Source = p.source;
                _bySource[p.source] = x;
                x.Muted = (p.flags & GroupCallParticipant.Flags.muted) != 0;
                x.CanSelfUnmute = !x.Muted || (p.flags & GroupCallParticipant.Flags.can_self_unmute) != 0;
                bool hasVolume = (p.flags & GroupCallParticipant.Flags.has_volume) != 0;
                if (!min)
                {
                    x.MutedByYou = (p.flags & GroupCallParticipant.Flags.muted_by_you) != 0;
                    x.Volume = hasVolume ? p.volume : 10000;
                }
                else if (hasVolume && (p.flags & GroupCallParticipant.Flags.volume_by_admin) != 0) x.Volume = p.volume;
                x.RaiseHandRating = (p.flags & GroupCallParticipant.Flags.has_raise_hand_rating) != 0 ? p.raise_hand_rating : 0;
                if ((p.flags & GroupCallParticipant.Flags.has_about) != 0) x.About = p.about;
                if ((p.flags & GroupCallParticipant.Flags.self) != 0) x.IsSelf = true;
                x.Date = p.date;
                if ((p.flags & GroupCallParticipant.Flags.has_active_date) != 0) x.ActiveDate = p.active_date;
                x.Video = (p.flags & GroupCallParticipant.Flags.has_video) != 0 ? p.video : null;
                string n = name != null ? name(x.PeerId, x.IsUser) : null;
                if (!string.IsNullOrEmpty(n)) x.Name = n;
                else if (x.Name == null) x.Name = x.IsUser ? "Participant" : "Chat";
            }
        }

        /// <summary>The list order: us first, then who is speaking, raised hands, the recently active, the longest there.</summary>
        public List<GroupParticipant> Sorted()
        {
            return _byPeer.Values
                .OrderByDescending(p => p.IsSelf)
                .ThenByDescending(p => p.Speaking)
                .ThenByDescending(p => p.HandRaised ? p.RaiseHandRating : 0)
                .ThenByDescending(p => p.ActiveDate)
                .ThenBy(p => p.Date)
                .ToList();
        }
    }
}
