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
    /// "Forward to…" picker — themed accent title bar (ThemedChrome), an RT-dark-scrollbar ThemedListBox of the
    /// user's chats with REAL avatars (the chat-list cached path, async + initials fallback), MULTI-SELECT
    /// (forward to several chats at once), and ONLY chats the user can POST to (read-only broadcast channels
    /// excluded). The selected set is exposed via <see cref="SelectedChats"/> with DialogResult.OK.
    /// </summary>
    public sealed class ForwardPickerDialog : Form
    {
        private readonly bool _dark;
        private readonly Color _accent, _bg, _fg;
        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;
        private readonly List<ChatEntry> _all;
        private List<ChatEntry> _view;
        private readonly HashSet<long> _checked = new HashSet<long>();
        private readonly HashSet<long> _avatarRequested = new HashSet<long>();

        private readonly TextBox _search;
        private readonly ThemedListBox _list;
        private readonly Button _confirm;

        public List<ChatEntry> SelectedChats { get; private set; } = new List<ChatEntry>();

        public ForwardPickerDialog(List<ChatEntry> chats, bool dark, Color accent,
                                   Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
        {
            _dark = dark; _accent = accent; _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            _bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            _all = (chats ?? new List<ChatEntry>()).Where(CanPost).ToList();   // only chats you can post to
            _view = _all;

            Text = "Forward to…";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(380, 520 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Forward to…", accent, dark);

            _search = new TextBox { Left = 12, Top = 12, Width = 356, Height = 28, BackColor = field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(10f) };
            _search.TextChanged += (s, e) => ApplyFilter();
            content.Controls.Add(_search);

            _list = new ThemedListBox(dark, accent) { Left = 12, Top = 48, Width = 356, Height = 414, RowHeight = 56, CanvasBackColor = _bg };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;
            content.Controls.Add(_list);

            _confirm = new Button { Text = "Forward (0)", Left = 184, Top = 470, Width = 104, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Font = FontHelper.Ui(10f, FontStyle.Bold), Enabled = false };
            _confirm.FlatAppearance.BorderSize = 0;
            _confirm.Click += (s, e) => { SelectedChats = _all.Where(c => _checked.Contains(c.PeerId)).ToList(); DialogResult = DialogResult.OK; Close(); };
            var cancel = new Button { Text = "Cancel", Left = 294, Top = 470, Width = 74, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = _fg };
            cancel.FlatAppearance.BorderSize = 1;
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            content.Controls.Add(_confirm); content.Controls.Add(cancel);

            _list.SetItems(_view.Count);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(_bg)) g.FillRectangle(bb, r);

            int d = 40, ax = r.Left + 10, ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            Image av = _cachedAvatar != null ? _cachedAvatar(e.PeerId) : null;
            if (av != null) DrawHelper.DrawCircularImage(g, avatarRect, av);
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(e.PeerId))) g.FillEllipse(ab, avatarRect);
                string nm = e.Title ?? "";
                string initial = nm.Length > 0 ? nm.Substring(0, 1).ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, initial, FontHelper.Ui(14f, FontStyle.Bold), avatarRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                EnsureAvatar(e);
            }

            int tx = ax + d + 12, tw = r.Right - tx - 40;
            TextRenderer.DrawText(g, e.Title ?? "", FontHelper.Ui(11f, FontStyle.Bold),
                new Rectangle(tx, r.Top + (r.Height - 22) / 2, tw, 22), _fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (_checked.Contains(e.PeerId))
            {
                int cd = 22, cx = r.Right - cd - 10, cy = r.Top + (r.Height - cd) / 2;
                using (var cbk = new SolidBrush(_accent)) g.FillEllipse(cbk, cx, cy, cd, cd);
                TextRenderer.DrawText(g, "✓", FontHelper.Ui(9f, FontStyle.Bold), new Rectangle(cx, cy, cd, cd), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            using (var sep = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(230, 230, 232)))
                g.DrawLine(sep, r.Left + 10, r.Bottom - 1, r.Right - 10, r.Bottom - 1);
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
