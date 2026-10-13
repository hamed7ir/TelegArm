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
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply) — the reference conversion
    /// for the other ThemedChrome callers (see mdfiles/PHASE2-CONVERSION-GUIDE.md). <paramref name="dark"/> and
    /// <paramref name="accent"/> are kept for the callers and no longer read: the palette follows the theme live.
    /// </summary>
    public sealed class EditContactForm : DialogForm
    {
        private readonly TextRow _first, _last;

        public string FirstNameValue { get { return _first.Value.Trim(); } }
        public string LastNameValue { get { return _last.Value.Trim(); } }

        public EditContactForm(string first, string last, bool dark, Color accent) : base("Edit contact", 380, 260)
        {
            _first = new TextRow("First name", first ?? "");
            _last = new TextRow("Last name", last ?? "");
            PopulateBody(_first, _last);

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            save.Click += (s, e) =>
            {
                if (_first.Value.Trim().Length == 0) { ThemedDialog.Show(this, "Edit contact", "A contact needs a first name.", "OK"); return; }
                DialogResult = DialogResult.OK;
            };
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = save;
            FocusOnShow(_first.Field.Inner);
            FitToContent();
        }
    }
}
