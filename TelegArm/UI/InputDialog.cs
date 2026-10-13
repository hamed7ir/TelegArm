using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.4 — a one-field prompt (DroidFinestra's InputDialog on <see cref="DialogForm"/>).
    /// Enter = OK, Esc / ✕ = Cancel. Returns the trimmed text, or null when cancelled or left blank. The field gets the
    /// caret on open only with a hardware keyboard (DialogForm.FocusOnShow) — with touch the user taps it.
    /// Not used by the live app yet.</summary>
    public sealed class InputDialog : DialogForm
    {
        private const int W = 420;
        private readonly InputRow _row;

        public static string Ask(IWin32Window owner, string title, string caption, string initial = "", int maxLength = 0)
        {
            using (var d = new InputDialog(title, caption, initial, maxLength))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
        }

        /// <summary>For the gallery and the probe: the dialog without showing it.</summary>
        internal static InputDialog Create(string title, string caption, string initial) { return new InputDialog(title, caption, initial, 0); }

        /// <summary>The trimmed text, or null when it is blank.</summary>
        public string Value
        {
            get { string t = (_row.Box.Text ?? "").Trim(); return t.Length == 0 ? null : t; }
        }

        /// <summary>The field itself (the probe types into it).</summary>
        internal TextBox Field { get { return _row.Box; } }

        private InputDialog(string title, string caption, string initial, int maxLength)
            : base(title, W, 200)
        {
            _row = new InputRow(caption, initial) { Font = FontHelper.ForAt((caption ?? "") + (initial ?? ""), 10.5f, WindowDpiValue) };
            if (maxLength > 0) _row.Box.MaxLength = maxLength;
            PopulateBody(_row);
            AcceptButton = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.OK);
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            FocusOnShow(_row.Box);
            Size = new Size(Width, Bar.Height + _row.Height + Px(RowGap) * 2 + Px(FooterHeight) + 1);
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (_row != null) _row.Recolor();
        }
    }
}
