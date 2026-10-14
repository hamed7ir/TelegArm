using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// "Forward to…" picker — a <see cref="DialogForm"/> (redesign step 2.6b; was a plain Form + ThemedChrome) holding a
    /// <see cref="ThemedTextBox"/> search and an RT-dark-scrollbar ThemedListBox of the user's chats with REAL avatars
    /// (the chat-list cached path, async + initials fallback), MULTI-SELECT (forward to several chats at once), and ONLY
    /// chats the user can POST to (read-only broadcast channels excluded). The selected set is exposed via
    /// <see cref="SelectedChats"/> with DialogResult.OK. <paramref name="dark"/> and <paramref name="accent"/> are kept for
    /// the callers and no longer read: the palette follows the theme live.
    /// </summary>
    public sealed class ForwardPickerDialog : DialogForm
    {
        private const int RowLogical = 56;   // one row, logical px — the list takes it at the window's DPI

        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;
        private readonly List<ChatEntry> _all;
        private List<ChatEntry> _view;
        private readonly HashSet<long> _checked = new HashSet<long>();
        private readonly HashSet<long> _avatarRequested = new HashSet<long>();

        private readonly Panel _host, _searchHost;
        private readonly ThemedTextBox _search;
        private readonly ThemedListBox _list;
        private readonly RoundedButton _confirm;
        private readonly bool _built;   // ApplyDialogTheme is first called from the base constructor, before the controls exist

        public List<ChatEntry> SelectedChats { get; private set; } = new List<ChatEntry>();

        public ForwardPickerDialog(List<ChatEntry> chats, bool dark, Color accent,
                                   Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
            : base("Forward to…", 380, 580)
        {
            _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            var p = ThemeHelper.Palette;

            _all = (chats ?? new List<ChatEntry>()).Where(CanPost).ToList();   // only chats you can post to
            _view = _all;

            _search = new ThemedTextBox(false) { Hint = "Search", AccessibleName = "Search" };
            _search.Inner.AccessibleName = "Search";
            _search.TextChanged += (s, e) => ApplyFilter();
            _searchHost = new Panel { Dock = DockStyle.Top, BackColor = p.Window };
            _searchHost.Controls.Add(_search);
            _searchHost.SizeChanged += (s, e) => LayoutSearch();

            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;

            _host = new Panel { BackColor = p.Window };
            _host.Controls.Add(_list);
            _host.Controls.Add(_searchHost);    // added last = docked first: the Top strip
            UseOwnBody(_host);
            LayoutSearch();

            _confirm = AddFooterButton("Forward (0)", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            _confirm.Enabled = false;
            _confirm.Click += (s, e) => { SelectedChats = _all.Where(c => _checked.Contains(c.PeerId)).ToList(); DialogResult = DialogResult.OK; Close(); };
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = _confirm;
            FocusOnShow(_search.Inner);

            _list.SetItems(_view.Count);
            _built = true;
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
        }

        private void ApplyFilter()
        {
            string q = (_search.Text ?? "").Trim();
            _view = string.IsNullOrEmpty(q) ? _all
                : _all.Where(c => (c.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            _list.SetItems(_view.Count);
        }

        private void OnRowClicked(int i)
        {
            if (i < 0 || i >= _view.Count) return;
            long id = _view[i].PeerId;
            if (!_checked.Remove(id)) _checked.Add(id);
            _list.InvalidateRow(i);
            _confirm.Text = "Forward (" + _checked.Count + ")";
            _confirm.Enabled = _checked.Count > 0;
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _view.Count) return;
            var e = _view[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(p.Window)) g.FillRectangle(bb, r);

            int d = Px(40), ax = r.Left + Px(10), ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            Image av = _cachedAvatar != null ? _cachedAvatar(e.PeerId) : null;
            if (av != null) DrawHelper.DrawCircularImage(g, avatarRect, av);
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(e.PeerId))) g.FillEllipse(ab, avatarRect);
                string nm = e.Title ?? "";
                string initial = nm.Length > 0 ? DrawHelper.Initial(nm) : "?";
                DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 14f, dpi, FontStyle.Bold), avatarRect, Color.White);   // ink-centred (owner report 2026-10-14)
                EnsureAvatar(e);
            }

            // Font without the text argument = the Roboto face, as in 1.x: GDI substitutes Persian glyphs, and the taller
            // Vazirmatn line would not fit the fixed title band.
            int tx = ax + d + Px(12), tw = r.Right - tx - Px(40), th = Px(22);
            TextRenderer.DrawText(g, e.Title ?? "", TextRoles.Font(TextRole.Section, dpi),
                new Rectangle(tx, r.Top + (r.Height - th) / 2, tw, th), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (_checked.Contains(e.PeerId))
            {
                int cd = Px(22), cx = r.Right - cd - Px(10), cy = r.Top + (r.Height - cd) / 2;
                DrawCheckBadge(g, new Rectangle(cx, cy, cd, cd), p.Accent, p.OnAccent, dpi);
            }

            using (var sep = new Pen(p.Divider))
                g.DrawLine(sep, r.Left + Px(10), r.Bottom - 1, r.Right - Px(10), r.Bottom - 1);
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

        // Request the real avatar once per peer (bounded/async in MainForm); repaint the row when it arrives.
        private async void EnsureAvatar(ChatEntry e)
        {
            if (_loadAvatar == null || e.PeerInfo == null || !_avatarRequested.Add(e.PeerId)) return;
            Image img = await _loadAvatar(e.PeerId, e.PeerInfo);
            if (img == null || IsDisposed) return;
            int idx = _view.FindIndex(x => x.PeerId == e.PeerId);
            if (idx >= 0) _list.InvalidateRow(idx);
        }

        /// <summary>Can the user post here? Broadcast channels need creator/post-rights; a megagroup excludes you
        /// if you're banned from sending or sending is default-off for non-admins. Groups/users are always OK.</summary>
        private static bool CanPost(ChatEntry e)
        {
            var ch = e.PeerInfo as Channel;
            if (ch != null)
            {
                bool creator = (ch.flags & Channel.Flags.creator) != 0;
                bool admin = ch.admin_rights != null;
                if ((ch.flags & Channel.Flags.broadcast) != 0)
                    return creator || (admin && (ch.admin_rights.flags & ChatAdminRights.Flags.post_messages) != 0);
                if (ch.banned_rights != null && (ch.banned_rights.flags & (ChatBannedRights.Flags.view_messages | ChatBannedRights.Flags.send_messages)) != 0) return false;
                if (!creator && !admin && ch.default_banned_rights != null && (ch.default_banned_rights.flags & ChatBannedRights.Flags.send_messages) != 0) return false;
                return true;
            }
            return true;
        }
    }
}
