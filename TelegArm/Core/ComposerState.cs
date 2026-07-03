using System;
using TL;

namespace TelegArm.Core
{
    /// <summary>The composer-footer states, in tdesktop precedence order.</summary>
    public enum ComposerKind { Compose, Join, MuteUnmute, BotStart, Blocked, Restricted, SlowmodeWait }

    /// <summary>Resolved footer state + payload (mute flag, restriction/slow-mode expiry).</summary>
    public struct ComposerState
    {
        public ComposerKind Kind;
        public bool Muted;         // MuteUnmute → "Unmute" when muted, else "Mute"
        public DateTime Until;     // Restricted / SlowmodeWait expiry (default = none)
        public bool HasCountdown;  // Restricted → show a timer instead of a flat "can't send" label
    }

    /// <summary>
    /// Pure resolver for the composer footer. No UI, no I/O: given the resolved peer (User/Channel),
    /// optional full info, the mute flag, and whether history is empty, it returns exactly one state.
    /// Precedence (matches Telegram Desktop): Blocked &gt; Join &gt; MuteUnmute &gt; BotStart &gt; Restricted
    /// &gt; SlowmodeWait &gt; Compose. (User states and Channel states are disjoint by peer type, so the
    /// cross-type precedence is naturally satisfied.)
    /// </summary>
    public static class ComposerResolver
    {
        public static ComposerState Resolve(IPeerInfo peerInfo, ChannelFull channelFull, UserFull userFull,
                                            bool muted, bool historyEmpty)
        {
            // ── User / bot ─────────────────────────────────────────────
            if (peerInfo is User u)
            {
                // 1. BLOCKED — UserFull.blocked (highest precedence).
                if (userFull != null && (userFull.flags & UserFull.Flags.blocked) != 0)
                    return Of(ComposerKind.Blocked);
                // 4. BOT_START — a bot whose history is empty (never started).
                if ((u.flags & User.Flags.bot) != 0 && historyEmpty)
                    return Of(ComposerKind.BotStart);
                return Of(ComposerKind.Compose);
            }

            // ── Channel / supergroup ───────────────────────────────────
            if (peerInfo is Channel ch)
            {
                bool creator = (ch.flags & Channel.Flags.creator) != 0;

                // 2. JOIN — opened (via link/search) but not a member.
                if ((ch.flags & Channel.Flags.left) != 0)
                    return Of(ComposerKind.Join);

                // 3. MUTE_UNMUTE — a broadcast channel you follow but can't post to.
                if ((ch.flags & Channel.Flags.broadcast) != 0)
                {
                    bool canPost = creator
                        || (ch.admin_rights != null && (ch.admin_rights.flags & ChatAdminRights.Flags.post_messages) != 0);
                    return canPost ? Of(ComposerKind.Compose)
                                   : new ComposerState { Kind = ComposerKind.MuteUnmute, Muted = muted };
                }

                // 5. RESTRICTED — supergroup where sending is banned (per-user OR for everyone); creators exempt.
                if (!creator)
                {
                    DateTime until;
                    if (BannedSend(ch.banned_rights, out until) || BannedSend(ch.default_banned_rights, out until))
                    {
                        // until_date sentinel: 0 → epoch / far-future → "permanent" (flat, no timer).
                        // Only a genuine future date within ~a year shows a countdown.
                        bool countdown = until.Year > 1971 && until > DateTime.UtcNow
                                         && (until - DateTime.UtcNow).TotalDays <= 366;
                        return new ComposerState { Kind = ComposerKind.Restricted, Until = until, HasCountdown = countdown };
                    }
                }

                // 6. SLOWMODE_WAIT — v1 resolves as Compose; the post-send countdown
                //    (channelFull?.slowmode_next_send_date) is a follow-up.
                return Of(ComposerKind.Compose);
            }

            // Basic legacy groups (Chat) and unknown peers → normal composer.
            return Of(ComposerKind.Compose);
        }

        private static ComposerState Of(ComposerKind k) { return new ComposerState { Kind = k }; }

        /// <summary>True when the send_messages action is banned by these rights; out the until-date.</summary>
        private static bool BannedSend(ChatBannedRights r, out DateTime until)
        {
            until = default(DateTime);
            if (r == null) return false;
            until = r.until_date;
            return (r.flags & ChatBannedRights.Flags.send_messages) != 0;
        }
    }
}
