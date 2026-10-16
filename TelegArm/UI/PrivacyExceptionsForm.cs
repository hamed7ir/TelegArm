using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T22 — the people a privacy setting makes an exception for: "Always allow" and "Never allow", one list at
    /// a time under a segmented switch. Add picks from the contacts; tapping a person selects them for Remove. The caller
    /// saves (TelegramService.SetPrivacyAsync with the lists; every other exception rule is kept there).
    /// </summary>
    public sealed class PrivacyExceptionsForm : DialogForm
    {
        private const int RowLogical = 48;

        private readonly Func<Task<List<User>>> _contacts;
        private readonly SegmentedRow _which;
        private readonly ActionRow _add;
        private readonly Panel _host;
        private readonly ThemedListBox _list;
        private readonly RoundedButton _remove;
        private int _selected = -1;

        public List<User> AlwaysAllow { get; private set; }
        public List<User> NeverAllow { get; private set; }

        private List<User> Current { get { return _which.SelectedIndex == 1 ? NeverAllow : AlwaysAllow; } }

        public PrivacyExceptionsForm(string setting, IEnumerable<User> always, IEnumerable<User> never, Func<Task<List<User>>> contacts)
            : base("Exceptions: " + setting, 380, 520)
        {
            _contacts = contacts;
            AlwaysAllow = new List<User>(always ?? Enumerable.Empty<User>());
            NeverAllow = new List<User>(never ?? Enumerable.Empty<User>());
            var p = ThemeHelper.Palette;

            _host = new Panel { BackColor = p.Window };
            _which = new SegmentedRow("List", new[] { "Always allow", "Never allow" }, 0) { Dock = DockStyle.Top };
            _which.SelectedIndexChanged += (s, e) => { _selected = -1; Refill(); };
            _add = new ActionRow("People", "Add") { Dock = DockStyle.Top };
            _add.Click += (s, e) => AddPeople();
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += i => { _selected = i; _list.Invalidate(true); _remove.Enabled = _selected >= 0; };
            _host.Controls.Add(_list);
            _host.Controls.Add(_add);
            _host.Controls.Add(_which);   // added last = docked first: the top row
            _host.SizeChanged += (s, e) => LayoutRows();
            UseOwnBody(_host);

            AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.OK, 90);   // first added = rightmost
            _remove = AddFooterButton("Remove", RoundedButtonKind.Secondary, DialogResult.None, 96);
            _remove.Click += (s, e) => RemoveSelected();
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            LayoutRows();
            Refill();
            Shown += (s, e) => { LayoutRows(); Refill(); };   // the window's DPI is known once it is placed
        }

        private void LayoutRows()
        {
            int w = Math.Max(1, _host.ClientSize.Width), dpi = WindowDpiValue;
            _which.Height = _which.HeightFor(w, dpi);
            _add.Height = _add.HeightFor(w, dpi);
        }

        private void Refill()
        {
            _add.Subtitle = Current.Count == 0 ? "No one" : Current.Count == 1 ? "1 person" : Current.Count + " people";
            _list.SetItems(Current.Count);
            _list.Invalidate(true);
            _remove.Enabled = _selected >= 0 && _selected < Current.Count;
        }

        private void RemoveSelected()
        {
            if (_selected < 0 || _selected >= Current.Count) return;
            Current.RemoveAt(_selected);
            _selected = -1;
            Refill();
        }

        private void AddPeople()
        {
            using (var f = new PeoplePickerForm(_contacts, true, ThemeHelper.IsDark, ThemeHelper.Palette.Accent, "Add people"))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.SelectedUsers == null) return;
                var other = _which.SelectedIndex == 1 ? AlwaysAllow : NeverAllow;
                foreach (var u in f.SelectedUsers)
                {
                    if (u == null) continue;
                    other.RemoveAll(x => x.id == u.id);   // one person is in one list
                    if (!Current.Any(x => x.id == u.id)) Current.Add(u);
                }
            }
            Refill();
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            var list = Current;
            if (index < 0 || index >= list.Count) return;
            var u = list[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(index == _selected ? p.AccentSoft : p.Window)) g.FillRectangle(bb, r);
            string name = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            if (name.Length == 0) name = u.username ?? ("User " + u.id);
            int d = Px(32), ax = r.Left + Px(12), ay = r.Top + (r.Height - d) / 2;
            var ar = new Rectangle(ax, ay, d, d);
            using (var ab = new SolidBrush(DrawHelper.AvatarColor(u.id))) g.FillEllipse(ab, ar);
            string initial = DrawHelper.Initial(name);
            DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 12f, dpi, FontStyle.Bold), ar, Color.White);
            int tx = ax + d + Px(12);
            TextRenderer.DrawText(g, name, TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top, r.Right - tx - Px(12), r.Height), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_list == null) return;
            _list.RowHeight = Px(RowLogical);
            LayoutRows();
            Refill();
        }
    }
}
