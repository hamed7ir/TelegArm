using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// New Group / New Channel creation form — a name (+ description for a channel) and a member selection that
    /// reuses <see cref="PeoplePickerForm"/> (multi-select). Returns the title/about/members; the caller does
    /// the bounded Channels_CreateChannel (+ invite) RPC. Themed; no network here (the picker fetches contacts).
    /// </summary>
    public sealed class CreateChatForm : Form
    {
        public enum Kind { Group, Channel }

        private readonly Kind _kind;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly Func<Task<List<User>>> _fetch;
        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;

        private TextBox _title, _desc;
        private Label _membersLabel;
        private Button _create;

        public string ChatTitle { get; private set; }
        public string ChatAbout { get; private set; }
        public List<User> Members { get; private set; } = new List<User>();

        public CreateChatForm(Kind kind, Func<Task<List<User>>> fetch, bool dark, Color accent,
                              Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
        {
            _kind = kind; _fetch = fetch; _dark = dark; _accent = accent;
            _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            Color fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = kind == Kind.Group ? "New Group" : "New Channel";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(380, (kind == Kind.Channel ? 330 : 250) + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, Text, accent, dark);   // accent title bar + dark chrome

            int y = 16;
            content.Controls.Add(new Label { Text = kind == Kind.Group ? "Group name" : "Channel name", Left = 16, Top = y, Width = 240, ForeColor = fg, Font = FontHelper.Ui(9f) });
            _title = new TextBox { Left = 16, Top = y + 22, Width = 348, Height = 28, BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            _title.TextChanged += (s, e) => { if (_create != null) _create.Enabled = _title.Text.Trim().Length > 0; };
            content.Controls.Add(_title);
            y += 62;

            if (kind == Kind.Channel)
            {
                content.Controls.Add(new Label { Text = "Description (optional)", Left = 16, Top = y, Width = 240, ForeColor = fg, Font = FontHelper.Ui(9f) });
                _desc = new TextBox { Left = 16, Top = y + 22, Width = 348, Height = 60, Multiline = true, BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(10f) };
                content.Controls.Add(_desc);
                y += 94;
            }

            _membersLabel = new Label { Text = "No members selected", Left = 16, Top = y + 7, Width = 196, ForeColor = fg, Font = FontHelper.Ui(9f) };
            var selectBtn = new RoundedButton { Text = "Select members", Left = 224, Top = y, Width = 140, Height = 32, Kind = RoundedButtonKind.Secondary, Font = FontHelper.Ui(9f) };
            selectBtn.Click += (s, e) =>
            {
                using (var p = new PeoplePickerForm(_fetch, true, dark, accent, "Add members", _cachedAvatar, _loadAvatar))
                    if (p.ShowDialog(this) == DialogResult.OK)
                    {
                        Members = p.SelectedUsers ?? new List<User>();
                        _membersLabel.Text = Members.Count == 0 ? "No members selected" : (Members.Count + (Members.Count == 1 ? " member" : " members") + " selected");
                    }
            };
            content.Controls.Add(_membersLabel); content.Controls.Add(selectBtn);

            _create = new RoundedButton { Text = "Create", Left = 196, Top = content.Height - 48, Width = 84, Height = 36, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(10f, FontStyle.Bold), Enabled = false };
            _create.Click += (s, e) =>
            {
                ChatTitle = _title.Text.Trim();
                ChatAbout = _desc != null ? (_desc.Text ?? "").Trim() : "";
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = new RoundedButton { Text = "Cancel", Left = 288, Top = content.Height - 48, Width = 80, Height = 36, Kind = RoundedButtonKind.Secondary };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            content.Controls.Add(_create); content.Controls.Add(cancel);
        }
    }
}
