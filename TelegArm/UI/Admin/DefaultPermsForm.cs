using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Core;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// TIER 2 admin: a supergroup's DEFAULT member permissions. Reuses the themed/scrollable
    /// <see cref="RightsChecklistForm"/> (checked = allowed) and maps the result back to ChatBannedRights
    /// (unchecked = restricted) saved via the bounded Messages_EditChatDefaultBannedRights. A static opener so
    /// it inherits the checklist's chrome/scrollbar/touch/RTL guarantees with no duplicated UI.
    /// </summary>
    public static class DefaultPermsForm
    {
        public static async Task OpenAsync(IWin32Window owner, TelegramService service, InputPeer peer,
                                           ChatBannedRights current, bool dark, Color accent)
        {
            var spec = PermsSpec();
            var items = spec.Select(kv =>
                new RightsChecklistForm.Item(kv.Key, current == null || (current.flags & kv.Value) == 0)).ToList();

            bool[] result;
            using (var f = new RightsChecklistForm("Default permissions", "What all members can do by default.", items, dark, accent))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return;
                result = f.Result;
            }

            ChatBannedRights.Flags flags = 0;
            for (int i = 0; i < spec.Count; i++) if (!result[i]) flags |= spec[i].Value;   // unchecked → restricted

            try
            {
                if (!await service.SetDefaultPermissionsAsync(peer, new ChatBannedRights { flags = flags }))
                { ThemedDialog.Show(owner, "Permissions", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
            }
            catch (Exception ex) { ThemedDialog.Show(owner, "Permissions", "Couldn't save: " + ex.Message, "OK"); return; }
            System.Diagnostics.Debug.WriteLine("[ADMIN] default perms updated flags=" + flags);
            ThemedDialog.Show(owner, "Permissions", "Default permissions updated.", "OK");
        }

        private static List<KeyValuePair<string, ChatBannedRights.Flags>> PermsSpec()
        {
            var L = new List<KeyValuePair<string, ChatBannedRights.Flags>>();
            Action<string, ChatBannedRights.Flags> add = (s, f) => L.Add(new KeyValuePair<string, ChatBannedRights.Flags>(s, f));
            add("Send messages", ChatBannedRights.Flags.send_messages);
            add("Send media", ChatBannedRights.Flags.send_media);
            add("Send stickers & GIFs", ChatBannedRights.Flags.send_stickers | ChatBannedRights.Flags.send_gifs);
            add("Embed links", ChatBannedRights.Flags.embed_links);
            add("Send polls", ChatBannedRights.Flags.send_polls);
            add("Add users", ChatBannedRights.Flags.invite_users);
            add("Pin messages", ChatBannedRights.Flags.pin_messages);
            add("Change chat info", ChatBannedRights.Flags.change_info);
            return L;
        }
    }
}
