using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Small themed dialog to RENAME a saved contact — First + Last name, PRE-FILLED with the contact's
    /// current names. No network here: the caller reads <see cref="FirstNameValue"/>/<see cref="LastNameValue"/>
    /// on <see cref="DialogResult.OK"/> and pushes them via <c>TelegramService.EditContactAsync</c>
    /// (contacts.addContact re-adds the existing user = a rename). A first name is required (Telegram needs one).
    /// </summary>
    public sealed class EditContactForm : Form
    {
        private readonly TextBox _first, _last;

        public string FirstNameValue { get { return _first.Text.Trim(); } }
        public string LastNameValue { get { return _last.Text.Trim(); } }

        public EditContactForm(string first, string last, bool dark, Color accent)
        {
            Color fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = "Edit contact";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(360, 210 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Edit contact", accent, dark);

            int y = 16;
            content.Controls.Add(new Label { Text = "First name", Left = 16, Top = y, Width = 320, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _first = new TextBox { Left = 16, Top = y, Width = 324, Height = 28, Text = first ?? "", BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            content.Controls.Add(_first); y += 44;

            content.Controls.Add(new Label { Text = "Last name", Left = 16, Top = y, Width = 320, ForeColor = sub, Font = FontHelper.Ui(9f) }); y += 22;
            _last = new TextBox { Left = 16, Top = y, Width = 324, Height = 28, Text = last ?? "", BackColor = field, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(11f) };
            content.Controls.Add(_last); y += 48;

            var save = new RoundedButton { Text = "Save", Left = content.Width - 104, Top = y, Width = 92, Height = 38, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(10.5f, FontStyle.Bold) };
            save.Click += (s, e) =>
            {
                if (_first.Text.Trim().Length == 0) { ThemedDialog.Show(this, "Edit contact", "A contact needs a first name.", "OK"); return; }
                DialogResult = DialogResult.OK; Close();
            };
            var cancel = new RoundedButton { Text = "Cancel", Left = content.Width - 200, Top = y, Width = 86, Height = 38, Kind = RoundedButtonKind.Secondary };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            content.Controls.Add(save); content.Controls.Add(cancel);

            AcceptButton = save; CancelButton = cancel;
            ActiveControl = _first;
        }
    }
}
