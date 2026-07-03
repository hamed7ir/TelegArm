using System;
using System.Drawing;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// The admin hub — a themed, SCROLLABLE, touch-sized menu of management sections for a supergroup/channel
    /// the user can administer. Opened from the info screen's "Manage" action. Each row opens the relevant
    /// tier-1/2/3 form. Sections adapt to the peer type (default permissions only for supergroups).
    /// </summary>
    public sealed class ManageChatForm : Form
    {
        private readonly bool _dark;
        private readonly Color _accent, _fg, _field;

        public ManageChatForm(TelegramService service, Channel channel, InputPeer peer, string title, string about, bool dark, Color accent)
        {
            _dark = dark; _accent = accent;
            _field = dark ? Color.FromArgb(48, 48, 52) : Color.White;
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            bool isMega = (channel.flags & Channel.Flags.megagroup) != 0;
            bool isBroadcast = (channel.flags & Channel.Flags.broadcast) != 0;

            Text = "Manage";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(360, 460 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Manage", accent, dark);
            var scroll = ScrollHost.Wrap(content, dark, accent);

            int y = 10;
            Row(scroll, ref y, "✏   Edit info", () => Open(new EditChatInfoForm(service, channel, peer, title, about, dark, accent)));
            Row(scroll, ref y, "👥   Members", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Members, dark, accent)));
            Row(scroll, ref y, "⭐   Administrators", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Admins, dark, accent)));
            if (isMega)
                Row(scroll, ref y, "🔒   Default permissions", async () => await DefaultPermsForm.OpenAsync(this, service, peer, channel.default_banned_rights, dark, accent));
            Row(scroll, ref y, "🚫   Banned users", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Banned, dark, accent)));
            Row(scroll, ref y, "🔗   Invite links", () => Open(new InviteLinksForm(service, peer, dark, accent)));
            Row(scroll, ref y, isBroadcast ? "📢   Channel settings" : "🌐   Public link", () => Open(new ChannelExtrasForm(service, channel, dark, accent)));
        }

        private void Row(Control host, ref int y, string text, Action act)
        {
            var b = new Button
            {
                Text = text, Left = 10, Top = y, Width = 326, Height = 48,
                FlatStyle = FlatStyle.Flat, BackColor = _field, ForeColor = _fg,
                TextAlign = ContentAlignment.MiddleLeft, Font = FontHelper.Ui(11f), Padding = new Padding(10, 0, 0, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => act();
            host.Controls.Add(b);
            y += 54;
        }

        private void Open(Form f) { using (f) f.ShowDialog(this); }
    }
}
