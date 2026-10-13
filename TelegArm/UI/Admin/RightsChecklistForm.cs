using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// Generic, themed, SCROLLABLE checklist of boolean rights — reused for admin rights (ChatAdminRights) and
    /// default member permissions (ChatBannedRights-as-permissions). The caller passes labelled items with
    /// initial states and reads <see cref="Result"/> on OK, then maps back to flags. Touch-sized rows; the themed
    /// scrollbar comes with the dialog body. No network here (the caller saves, bounded).
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply + ScrollHost). Each right is a
    /// <see cref="ToggleRow"/> (a switch, drawn — the 1.x CheckBox is gone), the optional subtitle a one-line dim row above
    /// them, Save / Cancel the footer (Enter = Save, Esc = Cancel). The public surface is the 1.x one:
    /// <see cref="Item"/>, the constructor and <see cref="Result"/> (one bool per item, in order, set on Save).
    /// <paramref name="dark"/> and <paramref name="accent"/> are kept for the callers and no longer read.
    /// </summary>
    public sealed class RightsChecklistForm : DialogForm
    {
        public sealed class Item
        {
            public readonly string Label; public readonly bool Initial;
            public Item(string label, bool initial) { Label = label; Initial = initial; }
        }

        private readonly List<ToggleRow> _rows = new List<ToggleRow>();
        public bool[] Result { get; private set; }

        public RightsChecklistForm(string title, string subtitle, IList<Item> items, bool dark, Color accent)
            : base(title, 380, 460)
        {
            var body = new List<Control>();
            if (!string.IsNullOrEmpty(subtitle)) body.Add(new SubtitleRow(subtitle));
            foreach (var it in items)
            {
                var row = new ToggleRow(it.Label, it.Initial);
                _rows.Add(row);
                body.Add(row);
            }
            PopulateBody(body.ToArray());

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            save.Click += (s, e) => { Result = _rows.ConvertAll(r => r.On).ToArray(); DialogResult = DialogResult.OK; Close(); };
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = save;
            FitToContent(400);   // taller lists scroll in the body
        }

        /// <summary>One line of quiet text above the switches (the member's name, or what the list is for). Drawn through
        /// the row kit's emoji-aware line, so a display name with emoji keeps its colour images on RT 8.1; a name that
        /// is too long ends in an ellipsis.</summary>
        private sealed class SubtitleRow : SettingRow
        {
            private const int PadY = 6;

            public SubtitleRow(string text) : base(text, false)
            {
                AccessibleRole = AccessibleRole.StaticText;
                Refit();
            }

            public override int HeightFor(int width, int dpi)
            {
                return LineHeight(TextRoles.Font(TextRole.Field, dpi, Text)) + 2 * Dpi.Px(PadY, dpi);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
                PaintBack(e.Graphics, p);
                if (Text.Length == 0) return;
                var font = TextRoles.Font(TextRole.Field, dpi, Text);
                DrawTextLine(e.Graphics, Text, font, p.TextDim, new Rectangle(padX, padY, Math.Max(1, Width - 2 * padX), LineHeight(font)), false, true);
            }
        }
    }
}
