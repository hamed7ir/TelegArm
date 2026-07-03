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
    /// TIER 1 admin: edit a group/channel NAME, DESCRIPTION and PHOTO. Each change is saved via a bounded
    /// service call (off-UI-thread + timeout + a clear "VPN" message on timeout). Themed chrome + RT-dark
    /// scrollbar (the form scrolls on small RT screens). Only changed fields are pushed.
    /// </summary>
    public sealed class EditChatInfoForm : Form
    {
        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly InputPeer _peer;
        private readonly string _origTitle, _origAbout;

        private readonly TextBox _name, _desc;
        private readonly Label _status;
        private readonly Button _save;
        private string _photoPath;

        public EditChatInfoForm(TelegramService service, Channel channel, InputPeer peer, string title, string about, bool dark, Color accent)
        {
            _service = service; _channel = channel; _peer = peer;
            _origTitle = title ?? ""; _origAbout = about ?? "";

            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            Color fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = "Edit info";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(400, 420 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Edit info", accent, dark);

            var scroll = ScrollHost.Wrap(content, dark, accent);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = bg };
            _save = new Button { Text = "Save", Left = content.Width - 104, Top = 10, Width = 92, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Font = FontHelper.Ui(10.5f, FontStyle.Bold) };
            _save.FlatAppearance.BorderSize = 0; _save.Click += OnSave;
            var cancel = new Button { Text = "Cancel", Left = content.Width - 200, Top = 10, Width = 86, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg };
            cancel.FlatAppearance.BorderSize = 1; cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(_save); bottom.Controls.Add(cancel);
            content.Controls.Add(bottom);

            int y = 14;
            scroll.Controls.Add(new Label { Text = "Name", Left = 16, Top = y, Width = 200, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _name = new TextBox { Left = 16, Top = y, Width = 352, Height = 28, Text = _origTitle, BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            scroll.Controls.Add(_name); y += 46;

            scroll.Controls.Add(new Label { Text = "Description", Left = 16, Top = y, Width = 200, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _desc = new TextBox { Left = 16, Top = y, Width = 352, Height = 92, Multiline = true, Text = _origAbout, BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(10.5f) };
            scroll.Controls.Add(_desc); y += 108;

            var photoBtn = new Button { Text = "Change photo…", Left = 16, Top = y, Width = 160, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg, Font = FontHelper.Ui(9.5f) };
            photoBtn.FlatAppearance.BorderSize = 1; photoBtn.Click += PickPhoto;
            scroll.Controls.Add(photoBtn);
            _status = new Label { Left = 184, Top = y + 9, Width = 200, Height = 24, ForeColor = sub, Font = FontHelper.Ui(9f), Text = "", AutoEllipsis = true };
            scroll.Controls.Add(_status);
        }

        private void PickPhoto(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png", Title = "Choose a photo" })
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _photoPath = ofd.FileName;
                    _status.Text = System.IO.Path.GetFileName(_photoPath);
                }
        }

        private async void OnSave(object sender, EventArgs e)
        {
            _save.Enabled = false;
            try
            {
                if (_name.Text.Trim().Length > 0 && _name.Text.Trim() != _origTitle)
                {
                    if (!await _service.EditChatTitleAsync(_channel, _name.Text.Trim())) { Vpn("name"); return; }
                    System.Diagnostics.Debug.WriteLine("[ADMIN] title updated");
                }
                if (_desc.Text.Trim() != _origAbout)
                {
                    if (!await _service.EditChatAboutAsync(_peer, _desc.Text.Trim())) { Vpn("description"); return; }
                    System.Diagnostics.Debug.WriteLine("[ADMIN] about updated");
                }
                if (!string.IsNullOrEmpty(_photoPath))
                {
                    _status.Text = "Uploading photo…";
                    if (!await _service.EditChatPhotoAsync(_channel, _photoPath)) { Vpn("photo"); return; }
                    System.Diagnostics.Debug.WriteLine("[ADMIN] photo updated");
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { _save.Enabled = true; ThemedDialog.Show(this, "Edit info", "Couldn't save: " + ex.Message, "OK"); }
        }

        private void Vpn(string what)
        {
            _save.Enabled = true;
            ThemedDialog.Show(this, "Edit info", "Couldn't update the " + what + " — make sure your VPN is on.", "OK");
        }
    }
}
