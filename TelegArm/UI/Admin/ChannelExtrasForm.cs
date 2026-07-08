using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// TIER 3 admin: a channel/supergroup's public USERNAME (with availability feedback via
    /// Channels_CheckUsername) and, for broadcast channels, the SIGN MESSAGES toggle
    /// (Channels_ToggleSignatures). Themed chrome + RT-dark scrollbar; every RPC bounded. Linked discussion
    /// group is deferred (reported) — wiring it needs a group-picker + Channels_SetDiscussionGroup, out of scope.
    /// </summary>
    public sealed class ChannelExtrasForm : Form
    {
        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly bool _isBroadcast;

        private readonly TextBox _username;
        private readonly Label _avail;
        private readonly Button _save;
        private CheckBox _sign;
        private bool _suppress;

        // CHANNEL-LINK-UNLINK: the discussion-group control (broadcast + admin only)
        private Label _discussionState;
        private Button _discussionBtn;
        private long _linkedChatId;
        private string _linkedName;

        public ChannelExtrasForm(TelegramService service, Channel channel, bool dark, Color accent)
        {
            _service = service; _channel = channel;
            _isBroadcast = (channel.flags & Channel.Flags.broadcast) != 0;

            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            Color fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = _isBroadcast ? "Channel settings" : "Public link";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(400, (_isBroadcast ? 460 : 300) + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, Text, accent, dark);
            var scroll = ScrollHost.Wrap(content, dark, accent);

            int y = 14;
            scroll.Controls.Add(new Label { Text = "Public link (t.me/username)", Left = 16, Top = y, Width = 320, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _username = new TextBox { Left = 16, Top = y, Width = 270, Height = 28, Text = channel.username ?? "", BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            scroll.Controls.Add(_username);
            var check = new RoundedButton { Text = "Check", Left = 292, Top = y - 1, Width = 76, Height = 30, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f) };
            check.Click += CheckClick;
            scroll.Controls.Add(check);
            y += 32;
            _avail = new Label { Left = 16, Top = y, Width = 360, Height = 22, ForeColor = sub, Font = FontHelper.Ui(8.75f), Text = "Leave empty to make it private." };
            scroll.Controls.Add(_avail); y += 30;

            _save = new RoundedButton { Text = "Save link", Left = 16, Top = y, Width = 130, Height = 40, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _save.Click += SaveClick;
            scroll.Controls.Add(_save); y += 56;

            if (_isBroadcast)
            {
                _sign = new CheckBox
                {
                    Text = "Sign messages with the admin's name",
                    Left = 16, Top = y, Width = 360, Height = 36,
                    ForeColor = fg, BackColor = bg, FlatStyle = FlatStyle.Flat, Font = FontHelper.Ui(10f),
                    Checked = (channel.flags & Channel.Flags.signatures) != 0
                };
                _sign.CheckedChanged += SignChanged;
                scroll.Controls.Add(_sign);
                y += 42;
            }

            // CHANNEL-LINK-UNLINK: for a broadcast channel the user administers — link/unlink the discussion group
            // (the admin side of comments). This form is already reached only via the admin-gated "Manage" action;
            // the creator/admin_rights re-check guarantees a non-admin never sees the control.
            if (_isBroadcast && IsChannelAdmin())
            {
                scroll.Controls.Add(new Label { Text = "Discussion group (comments)", Left = 16, Top = y, Width = 360, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
                _discussionState = new Label { Left = 16, Top = y, Width = 360, Height = 22, ForeColor = fg, Font = FontHelper.Ui(9.5f), Text = "Loading…" };
                scroll.Controls.Add(_discussionState); y += 26;
                _discussionBtn = new RoundedButton { Text = "…", Left = 16, Top = y, Width = 240, Height = 38, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(10f), Enabled = false };
                _discussionBtn.Click += DiscussionClick;
                scroll.Controls.Add(_discussionBtn); y += 46;
                var _ld = LoadDiscussionStateAsync();   // fire-and-forget: read the current linked_chat_id → set the UI
            }
        }

        private bool IsChannelAdmin()
        {
            return (_channel.flags & Channel.Flags.creator) != 0 || _channel.admin_rights != null;
        }

        /// <summary>Reads the current linked discussion group (ChannelFull.linked_chat_id) and updates the control.</summary>
        private async System.Threading.Tasks.Task LoadDiscussionStateAsync()
        {
            try
            {
                var mcf = await _service.GetChannelFullAsync(_channel);
                var cf = mcf?.full_chat as ChannelFull;
                _linkedChatId = cf?.linked_chat_id ?? 0;
                _linkedName = null;
                if (_linkedChatId != 0 && mcf != null && mcf.chats != null && mcf.chats.TryGetValue(_linkedChatId, out var lc))
                    _linkedName = lc.Title;
                UpdateDiscussionUi();
            }
            catch { if (_discussionState != null) _discussionState.Text = "Couldn't load the discussion state."; }
        }

        private void UpdateDiscussionUi()
        {
            if (_discussionBtn == null) return;
            if (_linkedChatId != 0)
            {
                _discussionState.Text = "Comments ON — linked to \"" + (_linkedName ?? "group") + "\".";
                _discussionBtn.Text = "Remove discussion group";
            }
            else
            {
                _discussionState.Text = "No discussion group — comments are off.";
                _discussionBtn.Text = "Link a discussion group";
            }
            _discussionBtn.Enabled = true;
        }

        private void DiscussionClick(object sender, EventArgs e)
        {
            if (_linkedChatId != 0) UnlinkFlow(); else LinkFlow();
        }

        /// <summary>Link: fetch the eligible groups → a themed picker → SetDiscussionGroup(channel, picked).</summary>
        private async void LinkFlow()
        {
            _discussionBtn.Enabled = false;
            Messages_Chats groups = null;
            try { groups = await _service.GetGroupsForDiscussionAsync(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't load groups: " + ex.Message, "OK"); }
            _discussionBtn.Enabled = true;
            var eligible = groups != null && groups.chats != null
                ? groups.chats.Values.OfType<Channel>().ToList()
                : new List<Channel>();
            if (eligible.Count == 0)
            {
                ThemedDialog.Show(this, "Discussion group",
                    "You have no eligible groups to link. Create a group you own (or make yourself its admin) first.", "OK");
                return;
            }
            var menu = new ThemedContextMenuStrip();
            foreach (var g in eligible)
            {
                var grp = g;   // capture per-iteration
                var it = menu.Items.Add(string.IsNullOrEmpty(g.Title) ? "Group" : g.Title);
                it.Click += async (s, e) => await DoLink(grp);
            }
            menu.Show(_discussionBtn, new Point(0, _discussionBtn.Height));
        }

        private async System.Threading.Tasks.Task DoLink(Channel group)
        {
            _discussionBtn.Enabled = false;
            try
            {
                bool ok = await _service.SetDiscussionGroupAsync(_channel, group);
                System.Diagnostics.Debug.WriteLine("[ADMIN] link channel=" + _channel.id + " group=" + group.id + " " + (ok ? "ok" : "fail"));
                if (!ok) { ThemedDialog.Show(this, "Discussion group", "Couldn't link — check your VPN and that the group is eligible.", "OK"); _discussionBtn.Enabled = true; return; }
                await LoadDiscussionStateAsync();   // refresh → now shows "linked" + Remove
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't link: " + ex.Message, "OK"); _discussionBtn.Enabled = true; }
        }

        /// <summary>Unlink: confirm (destructive to comments), then SetDiscussionGroup(channel, null) = disable comments.</summary>
        private async void UnlinkFlow()
        {
            if (ThemedDialog.Show(this, "Remove discussion group", "Remove the discussion group? Comments will be disabled.", "Remove", "Cancel") != 0)
                return;
            _discussionBtn.Enabled = false;
            try
            {
                bool ok = await _service.SetDiscussionGroupAsync(_channel, null);   // null → inputChannelEmpty (unlink)
                System.Diagnostics.Debug.WriteLine("[ADMIN] unlink channel=" + _channel.id + " " + (ok ? "ok" : "fail"));
                if (!ok) { ThemedDialog.Show(this, "Discussion group", "Couldn't remove — check your VPN and permissions.", "OK"); _discussionBtn.Enabled = true; return; }
                await LoadDiscussionStateAsync();   // refresh → now shows "Link a discussion group"
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't remove: " + ex.Message, "OK"); _discussionBtn.Enabled = true; }
        }

        private async void CheckClick(object sender, EventArgs e)
        {
            string u = _username.Text.Trim();
            if (u.Length == 0) { _avail.Text = "Leave empty to make it private."; _avail.ForeColor = SystemColors.GrayText; return; }
            _avail.Text = "Checking…";
            try
            {
                bool free = await _service.CheckUsernameAsync(_channel, u);
                _avail.Text = free ? "✓ Available" : "✗ Taken or invalid";
                _avail.ForeColor = free ? Color.FromArgb(60, 170, 90) : Color.FromArgb(210, 90, 90);
            }
            catch (Exception ex) { _avail.Text = "Couldn't check: " + ex.Message; }
        }

        private async void SaveClick(object sender, EventArgs e)
        {
            _save.Enabled = false;
            try
            {
                bool ok = await _service.UpdateUsernameAsync(_channel, _username.Text.Trim());
                if (!ok) { _avail.Text = "Couldn't save — username taken, or VPN is off."; _avail.ForeColor = Color.FromArgb(210, 90, 90); return; }
                _avail.Text = "✓ Saved"; _avail.ForeColor = Color.FromArgb(60, 170, 90);
                System.Diagnostics.Debug.WriteLine("[ADMIN] username updated");
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Public link", "Couldn't save: " + ex.Message, "OK"); }
            finally { _save.Enabled = true; }
        }

        private async void SignChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            bool want = _sign.Checked;
            try
            {
                if (!await _service.ToggleSignaturesAsync(_channel, want))
                {
                    ThemedDialog.Show(this, "Sign messages", "Couldn't reach Telegram — make sure your VPN is on.", "OK");
                    _suppress = true; _sign.Checked = !want; _suppress = false;
                    return;
                }
                System.Diagnostics.Debug.WriteLine("[ADMIN] signatures=" + want);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Sign messages", "Couldn't change: " + ex.Message, "OK");
                _suppress = true; _sign.Checked = !want; _suppress = false;
            }
        }
    }
}
