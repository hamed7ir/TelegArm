using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.4 — the ✕ "minimise to the tray or exit?" question (DroidFinestra's CloseChoiceDialog on
    /// <see cref="DialogForm"/>), with "Remember my choice". Enter = Minimise to tray (today's behaviour, the safe
    /// default); Esc / ✕ = neither (DialogResult.Cancel: the window stays as it is).
    /// Remembering is NOT a one-way trap: the choice lands in a visible Settings row ("When I close the window",
    /// plan D8), where it can be changed back to Ask. That setting and the wiring arrive with step 2.7c; until then
    /// nothing in the live app opens this.</summary>
    public sealed class CloseChoiceDialog : DialogForm
    {
        private const int W = 440;
        private readonly CheckRow _remember;

        /// <summary>Valid when the dialog returned OK.</summary>
        public CloseAction Choice { get; private set; }
        public bool Remember { get { return _remember.Checked; } }

        public CloseChoiceDialog() : base("Close TelegArm", W, 260)
        {
            const string text = "Keep TelegArm running in the tray, or exit completely?\n\n"
                              + "In the tray it keeps receiving messages and notifications.";
            var msg = new MessageRow(text) { Font = FontHelper.UiAt(10.5f, WindowDpiValue) };
            _remember = new CheckRow("Remember my choice", false) { Font = FontHelper.UiAt(10f, WindowDpiValue) };
            PopulateBody(msg, _remember);

            var minimize = AddFooterButton("Minimise to tray", RoundedButtonKind.Primary, DialogResult.None, 150);
            minimize.Click += (s, e) => { Choice = CloseAction.MinimizeToTray; DialogResult = DialogResult.OK; };
            var exit = AddFooterButton("Exit", RoundedButtonKind.Danger, DialogResult.None);
            exit.Click += (s, e) => { Choice = CloseAction.Exit; DialogResult = DialogResult.OK; };
            AcceptButton = minimize;
            ActiveControl = minimize;

            int bodyWidth = Width - 2 - Px(11);
            int bodyH = msg.HeightFor(bodyWidth, WindowDpiValue) + _remember.Height + Px(RowGap) * 2;
            Size = new Size(Width, Bar.Height + bodyH + Px(FooterHeight) + 1);
        }
    }
}
