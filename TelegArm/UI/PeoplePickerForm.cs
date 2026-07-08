using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Reusable, searchable people list — the shared foundation for Contacts (single-tap → chat) and New
    /// Group/Channel (multi-select → members). Owner-drawn rows (an initials-circle avatar + name + @username),
    /// RTL-aware (Persian names via TextRenderer), themed, touch-sized. Contacts are fetched via the injected
    /// async fetcher (off-thread + bounded in the service) with a loading state; NO per-contact avatar download
    /// (VPN-safe — colored initials instead).
    /// </summary>
    public sealed class PeoplePickerForm : Form
    {
        private readonly bool _multi, _dark;
        private readonly Color _accent, _fg, _sub, _field, _bg;
        private readonly Func<Task<List<User>>> _fetch;
        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;
        private List<User> _all = new List<User>();
        private List<User> _view = new List<User>();
        private readonly HashSet<long> _checked = new HashSet<long>();
        private readonly HashSet<long> _avatarRequested = new HashSet<long>();

        private TextBox _search;
        private ThemedListBox _list;
        private Label _status;
        private Button _confirm, _cancel;

        public User SelectedUser { get; private set; }
        public List<User> SelectedUsers { get; private set; } = new List<User>();

        public PeoplePickerForm(Func<Task<List<User>>> fetch, bool multiSelect, bool dark, Color accent, string title,
                                Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
        {
            _fetch = fetch; _multi = multiSelect; _dark = dark; _accent = accent;
            _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            _bg = _dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = _dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = _dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            _field = _dark ? Color.FromArgb(54, 54, 58) : Color.White;
            BuildUi(title);
            Shown += async (s, e) => await LoadAsync();
        }

        private void BuildUi(string title)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(380, 520 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, title, _accent, _dark);   // accent title bar + dark chrome (matches the app)

            _search = new TextBox { Left = 12, Top = 12, Width = 356, Height = 28, BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(10f) };
            _search.TextChanged += (s, e) => ApplyFilter();
            content.Controls.Add(_search);

            _list = new ThemedListBox(_dark, _accent)
            {
                Left = 12, Top = 48, Width = 356, Height = _multi ? 414 : 458, RowHeight = 54, CanvasBackColor = _bg
            };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;
            content.Controls.Add(_list);   // owner-drawn list with a themed scrollbar that's DARK on RT 8.1 too

            _status = new Label { Left = 12, Top = 230, Width = 356, Height = 28, ForeColor = _sub, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading contacts…", Font = FontHelper.Ui(10f) };
            content.Controls.Add(_status); _status.BringToFront();

            if (_multi)
            {
                _confirm = new RoundedButton { Text = "Add (0)", Left = 196, Top = 470, Width = 86, Height = 36, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(10f, FontStyle.Bold), Enabled = false };
                _confirm.Click += (s, e) => { SelectedUsers = _all.Where(u => _checked.Contains(u.id)).ToList(); DialogResult = DialogResult.OK; Close(); };
                _cancel = new RoundedButton { Text = "Cancel", Left = 288, Top = 470, Width = 80, Height = 36, Kind = RoundedButtonKind.Secondary };
                _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                content.Controls.Add(_confirm); content.Controls.Add(_cancel);
            }
        }

        private async Task LoadAsync()
        {
            _status.Visible = true; _status.Text = "Loading contacts…"; _list.Visible = false;
            List<User> users = null;
            try { users = await _fetch(); } catch { users = null; }
            if (users == null) { _status.Text = "Couldn't load contacts — make sure your VPN is on."; return; }
            _all = users.OrderBy(u => PersonName(u), StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_all.Count == 0) { _status.Text = "No contacts."; return; }
            _status.Visible = false; _list.Visible = true;
            ApplyFilter();
            UpdateConfirm();
        }

        private void ApplyFilter()
        {
            string q = (_search.Text ?? "").Trim();
            _view = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(u => PersonName(u).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                               || (u.username ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _list.SetItems(_view.Count);
        }

        private void OnRowClicked(int i)
        {
            if (i < 0 || i >= _view.Count) return;
            var u = _view[i];
            if (_multi)
            {
                if (!_checked.Remove(u.id)) _checked.Add(u.id);
                _list.InvalidateRow(i);
                UpdateConfirm();
            }
            else { SelectedUser = u; DialogResult = DialogResult.OK; Close(); }
        }

        private void UpdateConfirm()
        {
            if (_confirm == null) return;
            _confirm.Text = "Add (" + _checked.Count + ")";
            _confirm.Enabled = _checked.Count > 0;
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _view.Count) return;
            var u = _view[index];
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(_bg)) g.FillRectangle(bb, r);

            int d = 40, ax = r.Left + 8, ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            string name = PersonName(u);
            Image av = _cachedAvatar != null ? _cachedAvatar(u.id) : null;
            if (av != null) DrawHelper.DrawCircularImage(g, avatarRect, av);
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(u.id))) g.FillEllipse(ab, avatarRect);
                string initial = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, initial, FontHelper.Ui(14f, FontStyle.Bold), avatarRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                EnsureAvatar(u);
            }

            int tx = ax + d + 12, tw = r.Right - tx - (_multi ? 36 : 12);
            TextRenderer.DrawText(g, name, FontHelper.Ui(11f, FontStyle.Bold), new Rectangle(tx, r.Top + 8, tw, 20), _fg,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            string sub = !string.IsNullOrEmpty(u.username) ? "@" + u.username : (!string.IsNullOrEmpty(u.phone) ? "+" + u.phone : "");
            if (sub.Length > 0)
                TextRenderer.DrawText(g, sub, FontHelper.Ui(8.5f), new Rectangle(tx, r.Top + 28, tw, 18), _sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (_multi && _checked.Contains(u.id))
            {
                int cx = r.Right - 30, cy = r.Top + (r.Height - 20) / 2;
                using (var cbk = new SolidBrush(_accent)) g.FillEllipse(cbk, cx, cy, 20, 20);
                TextRenderer.DrawText(g, "✓", FontHelper.Ui(9f, FontStyle.Bold), new Rectangle(cx, cy, 20, 20), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            using (var sep = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(230, 230, 232)))
                g.DrawLine(sep, r.Left + 8, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
        }

        // Real profile photo: requested once per contact (bounded/async in the host), repaint the row on arrival.
        private async void EnsureAvatar(User u)
        {
            if (_loadAvatar == null || u == null || !_avatarRequested.Add(u.id)) return;
            Image img = await _loadAvatar(u.id, u);
            if (img == null || IsDisposed) return;
            int idx = _view.FindIndex(x => x.id == u.id);
            if (idx >= 0) _list.InvalidateRow(idx);
        }

        private static string PersonName(User u)
        {
            string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            if (n.Length > 0) return n;
            if (!string.IsNullOrEmpty(u.username)) return u.username;
            if (!string.IsNullOrEmpty(u.phone)) return "+" + u.phone;
            return "User " + u.id;
        }
    }
}
