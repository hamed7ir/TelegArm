using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Searchable country list for login: type to filter by name or dial code; tap a row to choose. Flags
    /// render via the bundled Noto set (EmojiRenderer); if a flag image is missing, a 2-letter ISO2 chip is
    /// drawn instead — so nothing shows a broken emoji box on RT. Themed chrome (accent title bar + dark
    /// content) + an owner-drawn list whose scrollbar is dark on every Windows version including RT 8.1.
    /// </summary>
    public sealed class CountryPickerForm : Form
    {
        private readonly Color _bg, _fg, _sub, _accent, _field;
        private readonly TextBox _search;
        private readonly ThemedListBox _list;
        private List<Country> _shown;

        public Country SelectedCountry { get; private set; }

        public CountryPickerForm(bool dark, Color accent)
        {
            _accent = accent;
            _bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
            _field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = "Choose a country";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = FontHelper.Ui(10f);
            ClientSize = new Size(360, 460 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, Text, _accent, dark);   // accent title bar + dark chrome (matches the login form)

            _search = new TextBox { Left = 12, Top = 12, Width = 336, Height = 28, BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle };
            _search.TextChanged += (s, e) => Filter(_search.Text);
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && _shown != null && _shown.Count > 0) { Choose(0); e.SuppressKeyPress = true; } };
            content.Controls.Add(_search);

            _list = new ThemedListBox(dark, _accent)
            {
                Left = 0, Top = 48, Width = 360, Height = content.Height - 48, RowHeight = 38, CanvasBackColor = _bg
            };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += Choose;
            content.Controls.Add(_list);

            Filter("");
            _search.Focus();
        }

        private void Filter(string q)
        {
            q = (q ?? "").Trim();
            string digits = new string(q.Where(char.IsDigit).ToArray());
            IEnumerable<Country> src = Countries.All;
            if (q.Length > 0)
                src = Countries.All.Where(c =>
                    (c.Name != null && c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (digits.Length > 0 && c.DialCode != null && c.DialCode.StartsWith(digits, StringComparison.Ordinal)));
            _shown = src.ToList();
            _list.SetItems(_shown.Count);
        }

        private void Choose(int index)
        {
            if (_shown == null || index < 0 || index >= _shown.Count) return;
            SelectedCountry = _shown[index];
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || _shown == null || index >= _shown.Count) return;
            var c = _shown[index];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_bg)) g.FillRectangle(b, r);

            var flagRect = new Rectangle(r.X + 12, r.Y + 7, 30, 24);
            Image flag = EmojiRenderer.Available ? EmojiRenderer.Get(c.FlagEmoji) : null;
            if (flag != null)
                g.DrawImage(flag, flagRect);
            else
            {
                using (var chip = new SolidBrush(_field))
                using (var p = DrawHelper.RoundedRect(flagRect, 4)) g.FillPath(chip, p);
                TextRenderer.DrawText(g, (c.Iso2 ?? "?").ToUpperInvariant(), FontHelper.Ui(8f, FontStyle.Bold), flagRect, _sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tx = flagRect.Right + 12;
            var nameRect = new Rectangle(tx, r.Y, r.Width - tx - 58, r.Height);
            TextRenderer.DrawText(g, c.Name ?? "", Font, nameRect, _fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var dialRect = new Rectangle(r.Right - 56, r.Y, 48, r.Height);
            TextRenderer.DrawText(g, "+" + c.DialCode, Font, dialRect, _sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
