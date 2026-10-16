using System;
using System.Collections.Generic;
using TelegArm.Core;
using TL;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign T7 — the composer footer's slow-mode state (ComposerResolver is pure: peer + full info in, one
    /// state out).</summary>
    internal static class ComposerTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("composer (T7): slow mode waits until the server's next send; admins, the creator and a past date don't", SlowMode));
            t.Add(new KeyValuePair<string, Action>("composer (T8): <u>underline</u> and \"> \" quotes parse; the old markers are unchanged", Markdown));
            t.Add(new KeyValuePair<string, Action>("privacy (T22): a new main setting keeps every exception; edited people lists replace theirs", Privacy));
        }

        private static string Rules(InputPrivacyRule[] r)
        {
            var parts = new List<string>();
            foreach (var x in r)
            {
                string s = x.GetType().Name.Replace("InputPrivacyValue", "");
                var au = x as InputPrivacyValueAllowUsers; var du = x as InputPrivacyValueDisallowUsers;
                var dc = x as InputPrivacyValueDisallowChatParticipants;
                var users = au != null ? au.users : du != null ? du.users : null;
                if (users != null) { var ids = new List<string>(); foreach (var u in users) ids.Add(((InputUser)u).user_id + ":" + ((InputUser)u).access_hash); s += "[" + string.Join(",", ids.ToArray()) + "]"; }
                if (dc != null) s += "[" + string.Join(",", Array.ConvertAll(dc.chats, c => c.ToString())) + "]";
                parts.Add(s);
            }
            return string.Join(" ", parts.ToArray());
        }

        private static void Privacy()
        {
            var ada = new User { id = 1, access_hash = 11 };
            var cur = new Account_PrivacyRules
            {
                rules = new PrivacyRule[] { new PrivacyValueAllowUsers { users = new long[] { 1 } }, new PrivacyValueDisallowChatParticipants { chats = new long[] { 5 } },
                                            new PrivacyValueAllowCloseFriends(), new PrivacyValueAllowContacts() },
                users = new Dictionary<long, User> { { 1, ada } },
                chats = new Dictionary<long, ChatBase>(),
            };
            // v1 sent only "DisallowAll" here, wiping Ada and the group list
            Check.Equal("AllowUsers[1:11] DisallowChatParticipants[5] AllowCloseFriends DisallowAll",
                        Rules(PrivacyRules.Build(2, cur, null, null)), "main setting changed");
            Check.Equal("DisallowChatParticipants[5] AllowCloseFriends DisallowUsers[2:22] AllowContacts",
                        Rules(PrivacyRules.Build(1, cur, new User[0], new[] { new User { id = 2, access_hash = 22 } })),
                        "people lists edited");
            Check.Equal("AllowAll", Rules(PrivacyRules.Build(0, null, null, null)), "nothing read yet");
            System.Collections.Generic.List<User> always, never;
            PrivacyRules.People(cur, out always, out never);
            Check.Equal("1/0", always.Count + "/" + never.Count, "people of the rules");
        }

        private static string Ents(MessageEntity[] e)
        {
            if (e == null) return "";
            var parts = new List<string>();
            foreach (var x in e) parts.Add(x.GetType().Name.Replace("MessageEntity", "") + "(" + x.offset + "," + x.length + ")");
            return string.Join(" ", parts.ToArray());
        }

        private static void Expect(string input, string text, string ents)
        {
            MessageEntity[] e;
            string got = MarkdownEntities.Parse(input, out e);
            Check.Equal(text, got, "text of [" + input.Replace("\n", "|") + "]");
            Check.Equal(ents, Ents(e), "entities of [" + input.Replace("\n", "|") + "]");
        }

        private static void Markdown()
        {
            Expect("<u>under</u> x", "under x", "Underline(0,5)");
            Expect("> a\n> b\nc", "a\nb\nc", "Blockquote(0,3)");
            Expect("hi\n> **q** z", "hi\nq z", "Blockquote(3,3) Bold(3,1)");
            Expect("> to the end", "to the end", "Blockquote(0,10)");
            Expect("5 > 3", "5 > 3", "");
            Expect(">no space", ">no space", "");
            Expect("\\> not a quote", "> not a quote", "");
            Expect("<u>unclosed", "<u>unclosed", "");
            Expect("**b** __i__ ~~s~~ ||p|| `c`", "b i s p c", "Bold(0,1) Italic(2,1) Strike(4,1) Spoiler(6,1) Code(8,1)");
            Expect("[t](http://x)", "t", "TextUrl(0,1)");
        }

        private static ComposerState For(Channel ch, ChannelFull full)
        {
            return ComposerResolver.Resolve(ch, full, null, false, false);
        }

        private static void SlowMode()
        {
            var group = new Channel { id = 1, flags = Channel.Flags.megagroup | Channel.Flags.slowmode_enabled };
            var ahead = new ChannelFull
            {
                flags = ChannelFull.Flags.has_slowmode_seconds | ChannelFull.Flags.has_slowmode_next_send_date,
                slowmode_seconds = 60, slowmode_next_send_date = DateTime.UtcNow.AddSeconds(45)
            };
            var st = For(group, ahead);
            Check.Equal(ComposerKind.SlowmodeWait, st.Kind, "waiting");
            Check.True(st.HasCountdown && st.Until > DateTime.UtcNow, "with a countdown to the next send");

            var past = new ChannelFull
            {
                flags = ChannelFull.Flags.has_slowmode_seconds | ChannelFull.Flags.has_slowmode_next_send_date,
                slowmode_seconds = 60, slowmode_next_send_date = DateTime.UtcNow.AddSeconds(-5)
            };
            Check.Equal(ComposerKind.Compose, For(group, past).Kind, "the wait is over");
            Check.Equal(ComposerKind.Compose, For(group, null).Kind, "no full info: compose, as before");

            var admin = new Channel { id = 1, flags = Channel.Flags.megagroup | Channel.Flags.slowmode_enabled, admin_rights = new ChatAdminRights() };
            Check.Equal(ComposerKind.Compose, For(admin, ahead).Kind, "admins are exempt");
            var creator = new Channel { id = 1, flags = Channel.Flags.megagroup | Channel.Flags.slowmode_enabled | Channel.Flags.creator };
            Check.Equal(ComposerKind.Compose, For(creator, ahead).Kind, "the creator is exempt");
        }
    }
}
