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
    /// initial states and reads <see cref="Result"/> on OK, then maps back to flags. Touch-sized rows; RT-dark
    /// scrollbar via ScrollHost; accent title bar via ThemedChrome. No network here (the caller saves, bounded).
    /// </summary>
    public sealed class RightsChecklistForm : Form
    {
        public sealed class Item
        {
            public readonly string Label; public readonly bool Initial;
            public Item(string label, bool initial) { Label = label; Initial = initial; }
        }

        private readonly List<CheckBox> _boxes = new List<CheckBox>();
        public bool[] Result { get; private set; }

        public RightsChecklistForm(string title, string subtitle, IList<Item> items, bool dark, Color accent)
        {
            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            Color fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(380, 460 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, title, accent, dark);

            var scroll = ScrollHost.Wrap(content, dark, accent);   // RT-dark scrollbar; scrolls when rights overflow

            // Bottom action bar (added after the scroll host so it docks first and claims the bottom strip).
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = bg };
            var save = new Button { Text = "Save", Left = content.Width - 102, Top = 10, Width = 90, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Font = FontHelper.Ui(10.5f, FontStyle.Bold) };
            save.FlatAppearance.BorderSize = 0;
            save.Click += (s, e) => { Result = _boxes.ConvertAll(b => b.Checked).ToArray(); DialogResult = DialogResult.OK; Close(); };
            var cancel = new Button { Text = "Cancel", Left = content.Width - 196, Top = 10, Width = 84, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg };
            cancel.FlatAppearance.BorderSize = 1;
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(save); bottom.Controls.Add(cancel);
            content.Controls.Add(bottom);

            int y = 12;
            if (!string.IsNullOrEmpty(subtitle))
            {
                scroll.Controls.Add(new Label { Text = subtitle, Left = 14, Top = y, Width = 332, Height = 36, ForeColor = sub, Font = FontHelper.Ui(9f) });
                y += 42;
            }
            foreach (var it in items)
            {
                var cb = new CheckBox
                {
                    Text = it.Label, Checked = it.Initial,
                    Left = 14, Top = y, Width = 338, Height = 34,
                    ForeColor = fg, BackColor = bg, FlatStyle = FlatStyle.Flat,
                    Font = FontHelper.Ui(10.5f), TextAlign = ContentAlignment.MiddleLeft
                };
                _boxes.Add(cb);
                scroll.Controls.Add(cb);
                y += 38;
            }
        }
    }
}
