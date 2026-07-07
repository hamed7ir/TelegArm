using System;
using TL;

namespace TelegArm.Core
{
    /// <summary>A single row in the chat list: a resolved peer plus its preview metadata.</summary>
    public class ChatEntry
    {
        public InputPeer Peer { get; set; }
        public long PeerId { get; set; }
        public string Title { get; set; }
        public string Preview { get; set; }
        public DateTime Date { get; set; }
        public int UnreadCount { get; set; }

        /// <summary>MENTION-REACTION: unread @mentions/replies-to-you (Dialog.unread_mentions_count). &gt;0 lights the
        /// "@" badge on the row AND makes a new mention BREAK THROUGH a mute to notify (client-side rule).</summary>
        public int UnreadMentions { get; set; }

        /// <summary>MENTION-REACTION: unread reactions to YOUR messages (Dialog.unread_reactions_count). &gt;0 lights a
        /// passive heart glyph on the row — indicator ONLY, never a notification.</summary>
        public int UnreadReactions { get; set; }

        /// <summary>True for groups/channels (used to show sender names on incoming messages).</summary>
        public bool IsGroup { get; set; }

        /// <summary>For search-result rows: the message id to scroll to when opened (0 = none).</summary>
        public int FocusMessageId { get; set; }

        /// <summary>Highest id among MY outgoing messages the peer has read (read ✓✓ watermark).</summary>
        public int ReadOutboxMaxId { get; set; }

        /// <summary>Highest incoming message id I've read (inbox read watermark; synced across devices).</summary>
        public int ReadInboxMaxId { get; set; }

        /// <summary>The dialog's top (most recent) message id — used to mark the whole chat read.</summary>
        public int TopMessageId { get; set; }

        /// <summary>Resolved User/ChatBase, used to download the profile photo (null = no avatar fetch).</summary>
        public IPeerInfo PeerInfo { get; set; }

        /// <summary>True when the chat is muted (no notifications) per its notify settings.</summary>
        public bool Muted { get; set; }

        /// <summary>The peer's EXPLICIT mute_until (null = no explicit setting → the peer inherits its
        /// category default: users / group chats / broadcasts). A PAST value = explicitly unmuted, which
        /// OVERRIDES a muted category (Telegram semantics) — that's why null and past differ (NOTIFY-FIX).</summary>
        public DateTime? MuteUntil { get; set; }

        /// <summary>Pin rank in the MAIN list (folder 0): &gt;=0 = pinned (pin order), -1 = not pinned there.</summary>
        public int MainPinOrder { get; set; } = -1;

        /// <summary>Pin rank in the ARCHIVE (folder 1): &gt;=0 = pinned (pin order), -1 = not pinned there.</summary>
        public int ArchivePinOrder { get; set; } = -1;

        /// <summary>True when the chat is in the Archive (folder_id = 1).</summary>
        public bool Archived { get; set; }

        /// <summary>Cached member/subscriber count for the header subtitle (PEER-PRESENTATION);
        /// 0 = not known (the subtitle stays empty — never "0 members").</summary>
        public int ParticipantsCount { get; set; }

        /// <summary>True once the ONE lazy full-chat count fetch ran for this entry — a count that's
        /// still missing after it stays missing quietly (no refetch loops on later opens).</summary>
        public bool CountFetchTried { get; set; }

        /// <summary>PRESENCE: user peers only — UTC instant until which the peer counts as online
        /// (UserStatusOnline.expires). The chat-list dot paints iff this is in the future; default = no dot.
        /// HOT-PATH LAW: the row painter reads THIS field only, never the TL status object.</summary>
        public DateTime OnlineUntil { get; set; }

        /// <summary>PRESENCE: group/megagroup online member count for the header ("N members, M online");
        /// 0 = unknown/none (the ", M online" suffix is omitted). Broadcasts never use this.</summary>
        public int OnlineCount { get; set; }
    }
}
