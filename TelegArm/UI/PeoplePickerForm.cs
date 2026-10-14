using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply) — the list is the dialog's
    /// own body (<c>UseOwnBody</c>), the search field a <see cref="ThemedTextBox"/>, the loading / failed / empty
    /// messages an <see cref="EmptyState"/>, the Add / Cancel buttons the footer. <paramref name="dark"/> and
    /// <paramref name="accent"/> are kept for the callers and no longer read: the palette follows the theme live.
    /// Selection semantics, the fetch flow and the result properties are exactly the 1.x ones.
    /// </summary>
    public sealed class PeoplePickerForm : DialogForm
    {
        private const int RowLogical = 54;   // one row, logical px — the list takes it at the window's DPI

        private readonly bool _multi;
        private readonly Func<Task<List<User>>> _fetch;
        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;
        private List<User> _all = new List<User>();
        private List<User> _view = new List<User>();
        private readonly HashSet<long> _checked = new HashSet<long>();
        private readonly HashSet<long> _avatarRequested = new HashSet<long>();

        private readonly Panel _host, _searchHost;
        private readonly ThemedTextBox _search;
        private readonly ThemedListBox _list;
        private readonly EmptyState _status;
        private readonly RoundedButton _confirm;
        private readonly bool _built;   // ApplyDialogTheme is first called from the base constructor, before the controls exist

        public User SelectedUser { get; private set; }
        public List<User> SelectedUsers { get; private set; } = new List<User>();

        public PeoplePickerForm(Func<Task<List<User>>> fetch, bool multiSelect, bool dark, Color accent, string title,
                                Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
            : base(title, 380, 580)
        {
            _fetch = fetch; _multi = multiSelect;
            _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            var p = ThemeHelper.Palette;

            _search = new ThemedTextBox(false) { Hint = "Search", AccessibleName = "Search" };
            _search.Inner.AccessibleName = "Search";
            _search.TextChanged += (s, e) => ApplyFilter();
            _searchHost = new Panel { Dock = DockStyle.Top, BackColor = p.Window };
            _searchHost.Controls.Add(_search);
            _searchHost.SizeChanged += (s, e) => LayoutSearch();

            // Owner-drawn list with a themed scrollbar that's dark on RT 8.1 too. Hidden while the status shows (as in 1.x).
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window, Visible = false };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;

            _status = new EmptyState { Dock = DockStyle.Fill, Hint = "Loading contacts…" };

            _host = new Panel { BackColor = p.Window };
            _host.Controls.Add(_list);          // the two Fill children share the space (one is visible at a time);
            _host.Controls.Add(_status);
            _host.Controls.Add(_searchHost);    // added last = docked first: the Top strip
            UseOwnBody(_host);
            LayoutSearch();

            if (_multi)
            {
                _confirm = AddFooterButton("Add (0)", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
                _confirm.Enabled = false;
                _confirm.Click += (s, e) => { SelectedUsers = _all.Where(u => _checked.Contains(u.id)).ToList(); DialogResult = DialogResult.OK; Close(); };
                AcceptButton = _confirm;
            }
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            FocusOnShow(_search.Inner);

            _built = true;
            Shown += async (s, e) => await LoadAsync();
        }

        /// <summary>The search field sits in a strip of its own: padded, and as tall as the field wants at the window's DPI.</summary>
        private void LayoutSearch()
        {
            if (_search == null || _searchHost == null) return;
            int padX = Px(12), padTop = Px(10), padBottom = Px(8);
            int h = _search.PreferredFieldHeight;
            _search.SetBounds(padX, padTop, Math.Max(1, _searchHost.Width - 2 * padX), h);
            int total = padTop + h + padBottom;
            if (_searchHost.Height != total) _searchHost.Height = total;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutSearch();   // the DPI of the window is only known once it is placed
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_built) return;
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_view.Count);   // the canvas height follows the row height
            LayoutSearch();
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (!_built) return;
            var p = ThemeHelper.Palette;
            _host.BackColor = p.Window;
            _searchHost.BackColor = p.Window;
            _search.Recolor();
            _list.CanvasBackColor = p.Window;
            _list.Invalidate(true);
            _status.Invalidate();
        }

        private async Task LoadAsync()
        {
            _status.Hint = "Loading contacts…"; _status.Visible = true; _list.Visible = false;
            List<User> users = null;
            try { users = await _fetch(); } catch { users = null; }
            if (IsDisposed) return;   // the dialog was closed while the contacts were on their way
            if (users == null) { _status.Hint = "Couldn't load contacts — make sure your VPN is on."; return; }
            _all = users.OrderBy(u => PersonName(u), StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_all.Count == 0) { _status.Hint = "No contacts."; return; }
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
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(p.Window)) g.FillRectangle(bb, r);

            int d = Px(40), ax = r.Left + Px(8), ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            string name = PersonName(u);
            Image av = _cachedAvatar != null ? _cachedAvatar(u.id) : null;
            if (av != null) DrawHelper.DrawCircularImage(g, avatarRect, av);
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(u.id))) g.FillEllipse(ab, avatarRect);
                string initial = name.Length > 0 ? DrawHelper.Initial(name) : "?";
                DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 14f, dpi, FontStyle.Bold), avatarRect, Color.White);   // ink-centred (owner report 2026-10-14)
                EnsureAvatar(u);
            }

            // Fonts without the text argument = the Roboto face, as in 1.x: GDI substitutes Persian glyphs, and the taller
            // Vazirmatn line would not fit these fixed name / @username bands.
            int tx = ax + d + Px(12), tw = r.Right - tx - Px(_multi ? 36 : 12);
            TextRenderer.DrawText(g, name, TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top + Px(8), tw, Px(20)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            string sub = !string.IsNullOrEmpty(u.username) ? "@" + u.username : (!string.IsNullOrEmpty(u.phone) ? "+" + u.phone : "");
            if (sub.Length > 0)
                TextRenderer.DrawText(g, sub, TextRoles.Font(TextRole.Field, dpi), new Rectangle(tx, r.Top + Px(28), tw, Px(18)), p.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (_multi && _checked.Contains(u.id))
            {
                int cd = Px(20), cx = r.Right - Px(30), cy = r.Top + (r.Height - cd) / 2;
                DrawCheckBadge(g, new Rectangle(cx, cy, cd, cd), p.Accent, p.OnAccent, dpi);
            }

            using (var sep = new Pen(p.Divider))
                g.DrawLine(sep, r.Left + Px(8), r.Bottom - 1, r.Right - Px(8), r.Bottom - 1);
        }

        /// <summary>The "selected" mark: an accent disc with a tick drawn in lines (a font tick is tofu on RT 8.1).</summary>
        private static void DrawCheckBadge(Graphics g, Rectangle b, Color fill, Color ink, int dpi)
        {
            using (var fb = new SolidBrush(fill)) g.FillEllipse(fb, b);
            using (var pen = new Pen(ink, Math.Max(1.5f, Dpi.F(2f, dpi))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[]
                {
                    new PointF(b.X + b.Width * 0.27f, b.Y + b.Height * 0.52f),
                    new PointF(b.X + b.Width * 0.44f, b.Y + b.Height * 0.69f),
                    new PointF(b.X + b.Width * 0.74f, b.Y + b.Height * 0.33f),
                });
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
