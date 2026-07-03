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
            ClientSize = new Size(400, 320 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, Text, accent, dark);
            var scroll = ScrollHost.Wrap(content, dark, accent);

            int y = 14;
            scroll.Controls.Add(new Label { Text = "Public link (t.me/username)", Left = 16, Top = y, Width = 320, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _username = new TextBox { Left = 16, Top = y, Width = 270, Height = 28, Text = channel.username ?? "", BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            scroll.Controls.Add(_username);
            var check = new Button { Text = "Check", Left = 292, Top = y - 1, Width = 76, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg, Font = FontHelper.Ui(9f) };
            check.FlatAppearance.BorderSize = 1; check.Click += CheckClick;
            scroll.Controls.Add(check);
            y += 32;
            _avail = new Label { Left = 16, Top = y, Width = 360, Height = 22, ForeColor = sub, Font = FontHelper.Ui(8.75f), Text = "Leave empty to make it private." };
            scroll.Controls.Add(_avail); y += 30;

            _save = new Button { Text = "Save link", Left = 16, Top = y, Width = 130, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _save.FlatAppearance.BorderSize = 0; _save.Click += SaveClick;
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
