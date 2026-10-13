using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.4 — a themed Yes/No question or a single-OK notice (DroidFinestra's ConfirmDialog on
    /// <see cref="DialogForm"/>). Esc and ✕ answer No; Enter answers Yes — EXCEPT for a <c>danger</c> question
    /// (delete, leave, log out), where Enter and the initial focus land on No, so a reflexive Enter cannot destroy
    /// anything. Ctrl+C copies the whole message, as the 1.x ThemedDialog does.
    /// Not used by the live app until step 2.1 swaps the ThemedDialog facade.</summary>
    public sealed class ConfirmDialog : DialogForm
    {
        private const int W = 460, MaxBody = 420;
        private readonly string _message;

        /// <summary>True only when the user chose Yes.</summary>
        public static bool Ask(IWin32Window owner, string message, string title = null, string yesText = "Yes", string noText = "No", bool danger = false)
        {
            using (var d = new ConfirmDialog(title, message, yesText, noText ?? "No", danger))
                return d.ShowDialog(owner) == DialogResult.Yes;
        }

        /// <summary>A notice with one OK button (Enter and Esc both close it).</summary>
        public static void Info(IWin32Window owner, string message, string title = null)
        {
            using (var d = new ConfirmDialog(title, message, "OK", null, false))
                d.ShowDialog(owner);
        }

        /// <summary>For the gallery and the probe: the dialog without showing it.</summary>
        internal static ConfirmDialog Create(string title, string message, string yesText, string noText, bool danger)
        {
            return new ConfirmDialog(title, message, yesText, noText, danger);
        }

        private ConfirmDialog(string title, string message, string yesText, string noText, bool danger)
            : base(string.IsNullOrEmpty(title) ? "TelegArm" : title, W, 200)
        {
            _message = message ?? "";
            var row = new MessageRow(_message) { Font = FontHelper.ForAt(_message, 10.5f, WindowDpiValue) };
            PopulateBody(row);

            // First added = rightmost: Yes, then No to its left.
            var yes = AddFooterButton(yesText, danger ? RoundedButtonKind.Danger : RoundedButtonKind.Primary, DialogResult.Yes);
            RoundedButton no = null;
            if (noText != null) no = AddFooterButton(noText, RoundedButtonKind.Neutral, DialogResult.No);

            if (no == null) { AcceptButton = yes; CancelButton = yes; }
            else if (danger) { AcceptButton = no; CancelButton = no; no.TabIndex = 0; yes.TabIndex = 1; ActiveControl = no; }
            else { AcceptButton = yes; CancelButton = no; ActiveControl = yes; }

            // Height from the wrapped message at this width, capped (a longer message scrolls in the body).
            int bodyWidth = Width - 2 - Px(11);   // the 1 px outline each side, the scrollbar
            int bodyH = Math.Min(Px(MaxBody), row.HeightFor(bodyWidth, WindowDpiValue) + Px(RowGap) * 2);
            Size = new Size(Width, Bar.Height + bodyH + Px(FooterHeight) + 1);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C) && !(ActiveControl is TextBoxBase))
            {
                try { Clipboard.SetText(_message.Length > 0 ? _message : " "); } catch { }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
