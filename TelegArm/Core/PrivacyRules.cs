using System;
using System.Collections.Generic;
using System.Linq;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// Redesign T22 — a privacy key's rules, read and rebuilt. Pure (no client), so SelfTest links this file and checks the
    /// rebuild: the v1 setter sent only the main rule, which wiped every exception list set on another device.
    /// </summary>
    internal static class PrivacyRules
    {
        /// <summary>The rule list TelegramService.SetPrivacyAsync sends. <paramref name="primary"/>: 0 everybody, 1 my
        /// contacts, 2 nobody (TelegramService.PrivacyPrimary).</summary>
        public static InputPrivacyRule[] Build(int primary, Account_PrivacyRules current, IEnumerable<User> alwaysAllow, IEnumerable<User> neverAllow)
        {
            var rules = new List<InputPrivacyRule>();
            var users = current != null ? current.users : null;
            Func<long[], InputUserBase[]> inputs = ids => ids.Select(id => { User u; return users != null && users.TryGetValue(id, out u) ? (InputUserBase)new InputUser(u.id, u.access_hash) : null; })
                                                             .Where(x => x != null).ToArray();
            if (current != null && current.rules != null)
                foreach (var r in current.rules)
                {
                    var au = r as PrivacyValueAllowUsers; var du = r as PrivacyValueDisallowUsers;
                    var ac = r as PrivacyValueAllowChatParticipants; var dc = r as PrivacyValueDisallowChatParticipants;
                    if (au != null && alwaysAllow == null && au.users != null) rules.Add(new InputPrivacyValueAllowUsers { users = inputs(au.users) });
                    else if (du != null && neverAllow == null && du.users != null) rules.Add(new InputPrivacyValueDisallowUsers { users = inputs(du.users) });
                    else if (ac != null) rules.Add(new InputPrivacyValueAllowChatParticipants { chats = ac.chats });
                    else if (dc != null) rules.Add(new InputPrivacyValueDisallowChatParticipants { chats = dc.chats });
                    else if (r is PrivacyValueAllowCloseFriends) rules.Add(new InputPrivacyValueAllowCloseFriends());
                    else if (r is PrivacyValueAllowPremium) rules.Add(new InputPrivacyValueAllowPremium());
                    else if (r is PrivacyValueAllowBots) rules.Add(new InputPrivacyValueAllowBots());
                    else if (r is PrivacyValueDisallowBots) rules.Add(new InputPrivacyValueDisallowBots());
                }
            if (alwaysAllow != null) { var a = alwaysAllow.Select(u => (InputUserBase)new InputUser(u.id, u.access_hash)).ToArray(); if (a.Length > 0) rules.Add(new InputPrivacyValueAllowUsers { users = a }); }
            if (neverAllow != null) { var n = neverAllow.Select(u => (InputUserBase)new InputUser(u.id, u.access_hash)).ToArray(); if (n.Length > 0) rules.Add(new InputPrivacyValueDisallowUsers { users = n }); }
            rules.RemoveAll(x => (x is InputPrivacyValueAllowUsers && ((InputPrivacyValueAllowUsers)x).users.Length == 0)
                              || (x is InputPrivacyValueDisallowUsers && ((InputPrivacyValueDisallowUsers)x).users.Length == 0));
            // the primary rule goes last: the exceptions above it are what it is an exception to
            if (primary == 0) rules.Add(new InputPrivacyValueAllowAll());
            else if (primary == 1) rules.Add(new InputPrivacyValueAllowContacts());
            else rules.Add(new InputPrivacyValueDisallowAll());
            return rules.ToArray();
        }

        /// <summary>Redesign T22 — the people of a key's "always" (allow) and "never" (disallow) lists, as User objects.</summary>
        public static void People(Account_PrivacyRules rules, out List<User> always, out List<User> never)
        {
            always = new List<User>(); never = new List<User>();
            if (rules == null || rules.rules == null) return;
            foreach (var r in rules.rules)
            {
                var au = r as PrivacyValueAllowUsers; var du = r as PrivacyValueDisallowUsers;
                long[] ids = au != null ? au.users : du != null ? du.users : null;
                if (ids == null) continue;
                foreach (var id in ids) { User u; if (rules.users != null && rules.users.TryGetValue(id, out u)) (au != null ? always : never).Add(u); }
            }
        }
    }
}
