using System.Drawing;
using System.Text;
using System.Windows.Forms;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T15 — add a contact by phone number (contacts.importContacts). The layout is EditContactForm's, with the
    /// phone on top; the number is checked here (digits, an optional leading +) so the server only sees a real one.
    /// </summary>
    public sealed class AddContactForm : DialogForm
    {
        private readonly TextRow _phone, _first, _last;

        /// <summary>The number as Telegram wants it: digits, with the leading "+" kept when typed.</summary>
        public string PhoneValue { get { return NormalizePhone(_phone.Value); } }
        public string FirstNameValue { get { return _first.Value.Trim(); } }
        public string LastNameValue { get { return _last.Value.Trim(); } }

        public AddContactForm() : base("Add contact", 380, 320)
        {
            _phone = new TextRow("Phone number (with country code)", "") { Hint = "+1 555 123 4567" };
            _first = new TextRow("First name", "");
            _last = new TextRow("Last name", "");
            PopulateBody(_phone, _first, _last);

            var add = AddFooterButton("Add", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            add.Click += (s, e) =>
            {
                if (PhoneValue.Replace("+", "").Length < 5) { ThemedDialog.Show(this, "Add contact", "Enter the phone number, with its country code.", "OK"); return; }
                if (FirstNameValue.Length == 0) { ThemedDialog.Show(this, "Add contact", "A contact needs a first name.", "OK"); return; }
                DialogResult = DialogResult.OK;
            };
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = add;
            FocusOnShow(_phone.Field.Inner);
            FitToContent();
        }

        /// <summary>Keeps the digits (Persian and Arabic-Indic ones become ASCII) and a leading "+".</summary>
        internal static string NormalizePhone(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in (raw ?? "").Trim())
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (c >= '۰' && c <= '۹') sb.Append((char)('0' + (c - '۰')));   // Persian
                else if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠')));   // Arabic-Indic
                else if (c == '+' && sb.Length == 0) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
