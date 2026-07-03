using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// TIER 1+2 admin: a themed, SCROLLABLE, RT-dark-scrollbar, touch-sized list of channel/supergroup
    /// participants. Three modes — Members (promote / ban / remove), Administrators (edit rights / dismiss),
    /// Banned (unban). Paged via Channels_GetParticipants (loads more on scroll). RTL names + initials-circle
    /// avatars (no per-row download). Destructive ops confirm via ThemedDialog; every RPC is bounded.
    /// </summary>
    public sealed class ChatMembersForm : Form
    {
        public enum Mode { Members, Admins, Banned }

        private const int PageSize = 100;

        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly bool _dark, _isMega;
        private readonly Color _accent, _bg, _fg, _sub;
        private readonly Mode _mode;

        private readonly ThemedListBox _list;
        private readonly Label _status;
        private readonly AvatarStore _avatars;   // shared store (ambient); null-safe throughout
        private readonly List<User> _users = new List<User>();
        private readonly Dictionary<long, ChatAdminRights> _adminRights = new Dictionary<long, ChatAdminRights>();
        private readonly Dictionary<long, string> _ranks = new Dictionary<long, string>();
        private bool _loading, _exhausted;

        public ChatMembersForm(TelegramService service, Channel channel, Mode mode, bool dark, Color accent)
        {
            _service = service; _channel = channel; _mode = mode; _dark = dark; _accent = accent;
            _isMega = (channel.flags & Channel.Flags.megagroup) != 0;
            _bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);

            string title = mode == Mode.Admins ? "Administrators" : mode == Mode.Banned ? "Banned users" : "Members";
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(390, 520 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, title, accent, dark);

            _list = new ThemedListBox(dark, accent) { Left = 8, Top = 8, Width = 374, Height = 504, RowHeight = 56, CanvasBackColor = _bg };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;
            _list.ReachedEnd += async () => await LoadMore();
            content.Controls.Add(_list);

            _status = new Label { Left = 8, Top = 240, Width = 374, Height = 28, ForeColor = _sub, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading…", Font = FontHelper.Ui(10f) };
            content.Controls.Add(_status); _status.BringToFront();

            // Member avatars from the shared AvatarStore (cached + demand) — arrivals repaint their row.
            _avatars = AvatarStore.Current;
            if (_avatars != null)
            {
                _avatars.AvatarLoaded += OnAvatarLoaded;
                Disposed += (s, e) => _avatars.AvatarLoaded -= OnAvatarLoaded;
            }

            Shown += async (s, e) => await LoadMore();

            // PRESENCE 4.2: a listed user's status change repaints that row live while the form is open.
            // Subscribe on open, unsubscribe on dispose (E-3) — the event is raised on the UI thread.
            MainForm.UserStatusChanged += OnUserStatusChanged;
            Disposed += (s, e) => MainForm.UserStatusChanged -= OnUserStatusChanged;
        }

        private void OnUserStatusChanged(long userId, UserStatus status)
        {
            if (IsDisposed) return;
            int i = _users.FindIndex(x => x.id == userId);
            if (i < 0) return;
            _users[i].status = status;
            _list.InvalidateRow(i);
        }

        private ChannelParticipantsFilter Filter()
        {
            if (_mode == Mode.Admins) return new ChannelParticipantsAdmins();
            if (_mode == Mode.Banned) return new ChannelParticipantsKicked { q = "" };
            return new ChannelParticipantsRecent();
        }

        private async Task LoadMore()
        {
            if (_loading || _exhausted) return;
            _loading = true;
            try
            {
                Channels_ChannelParticipants res;
                try { res = await _service.GetParticipantsAsync(_channel, Filter(), _users.Count, PageSize); }
                catch (Exception ex) { ShowError(ex); return; }
                if (res == null) { ShowMessage("Couldn't load — make sure your VPN is on."); return; }

                int added = 0;
                foreach (var p in res.participants)
                {
                    long uid = p.UserId;
                    User u = res.users != null && res.users.TryGetValue(uid, out var ub) ? ub as User : null;
                    if (u == null) continue;
                    var pa = p as ChannelParticipantAdmin;
                    if (pa != null) { _adminRights[uid] = pa.admin_rights; if (!string.IsNullOrEmpty(pa.rank)) _ranks[uid] = pa.rank; }
                    var pc = p as ChannelParticipantCreator;
                    if (pc != null) { _adminRights[uid] = pc.admin_rights; _ranks[uid] = string.IsNullOrEmpty(pc.rank) ? "owner" : pc.rank; }
                    _users.Add(u); added++;
                    // Demand the avatar for the first rows (bounded — the store is single-flight and
                    // FIFO; deeper pages keep the initials fallback, no per-row RPC storms).
                    if (_avatars != null && _users.Count <= 60 && _avatars.GetCached(uid) == null)
                        _avatars.Request(uid, u);
                }
                if (added < PageSize) _exhausted = true;

                if (_users.Count == 0) { ShowMessage(EmptyText()); }
                else { _status.Visible = false; _list.Visible = true; _list.SetItems(_users.Count); }
            }
            finally { _loading = false; }
        }

        private string EmptyText()
        {
            return _mode == Mode.Admins ? "No administrators." : _mode == Mode.Banned ? "No banned users." : "No members.";
        }

        private void ShowMessage(string text) { _status.Visible = true; _status.Text = text; _list.Visible = false; }
        private void ShowError(Exception ex) { ShowMessage("Couldn't load: " + ex.Message); }

        /// <summary>A demanded avatar landed (WORKER thread — marshal) → repaint just that row.</summary>
        private void OnAvatarLoaded(long peerId)
        {
            if (IsDisposed) return;
            int i = _users.FindIndex(x => x.id == peerId);
            if (i < 0) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) _list.InvalidateRow(i); })); } catch { }
        }

        // ── Row painting ────────────────────────────────────────────────────
        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _users.Count) return;
            var u = _users[index];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(_bg)) g.FillRectangle(bb, r);

            int d = 40, ax = r.Left + 10, ay = r.Top + (r.Height - d) / 2;
            string name = DisplayName(u);
            var av = _avatars?.GetCached(u.id);   // memory-only lookup — render-hot safe
            if (av != null)
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(ax, ay, d, d);
                    var oldClip = g.Clip;
                    g.SetClip(clip);
                    g.DrawImage(av, ax, ay, d, d);
                    g.Clip = oldClip;
                }
            }
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(u.id))) g.FillEllipse(ab, ax, ay, d, d);
                string initial = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, initial, FontHelper.Ui(14f, FontStyle.Bold), new Rectangle(ax, ay, d, d), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tx = ax + d + 12, tw = r.Right - tx - 12;
            TextRenderer.DrawText(g, name, FontHelper.Ui(11f, FontStyle.Bold), new Rectangle(tx, r.Top + 9, tw, 20), _fg,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            string sub = SubText(u);
            if (sub.Length > 0)
                TextRenderer.DrawText(g, sub, FontHelper.Ui(8.5f), new Rectangle(tx, r.Top + 30, tw, 18),
                    sub == "online" ? _accent : _sub,   // PRESENCE 4.1: "online" in accent, like the header
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (var sep = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(230, 230, 232)))
                g.DrawLine(sep, r.Left + 10, r.Bottom - 1, r.Right - 10, r.Bottom - 1);
        }

        private string SubText(User u)
        {
            if (_mode == Mode.Admins)
                return _ranks.ContainsKey(u.id) ? _ranks[u.id] : "administrator";
            if (_mode == Mode.Banned) return "banned";
            // PRESENCE 4.1: members show their status via the SAME formatter the chat header uses
            // (participants arrive with User.status populated — no per-row RPCs).
            string st = MainForm.StatusText(u);
            if (st.Length > 0) return st;
            return !string.IsNullOrEmpty(u.username) ? "@" + u.username : "member";
        }

        // ── Per-row actions ─────────────────────────────────────────────────
        private void OnRowClicked(int i)
        {
            if (i < 0 || i >= _users.Count) return;
            var u = _users[i];
            var menu = new ThemedContextMenuStrip();
            if (_mode == Mode.Members)
            {
                AddItem(menu, "Promote to admin", () => PromoteFlow(u, null));
                AddItem(menu, "Ban user", () => BanFlow(u));
                AddItem(menu, "Remove from group", () => KickFlow(u));
            }
            else if (_mode == Mode.Admins)
            {
                ChatAdminRights cur = _adminRights.ContainsKey(u.id) ? _adminRights[u.id] : null;
                AddItem(menu, "Edit admin rights", () => PromoteFlow(u, cur));
                AddItem(menu, "Dismiss admin", () => DismissFlow(u));
            }
            else
            {
                AddItem(menu, "Unban", () => UnbanFlow(u));
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(Cursor.Position);
        }

        private void AddItem(ContextMenuStrip menu, string text, Action act)
        {
            var it = new ToolStripMenuItem(text) { Font = FontHelper.Ui(10.5f), Padding = new Padding(2, 6, 2, 6) };
            it.Click += (s, e) => act();
            menu.Items.Add(it);
        }

        private async void PromoteFlow(User u, ChatAdminRights existing)
        {
            var spec = AdminRightsSpec(_isMega);
            var items = new List<RightsChecklistForm.Item>();
            foreach (var kv in spec)
            {
                bool init = existing != null
                    ? (existing.flags & kv.Value) != 0
                    : (kv.Value != ChatAdminRights.Flags.add_admins && kv.Value != ChatAdminRights.Flags.anonymous);
                items.Add(new RightsChecklistForm.Item(kv.Key, init));
            }
            bool[] result;
            using (var f = new RightsChecklistForm("Admin rights", DisplayName(u), items, _dark, _accent))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                result = f.Result;
            }
            ChatAdminRights.Flags flags = 0;
            for (int i = 0; i < spec.Count; i++) if (result[i]) flags |= spec[i].Value;
            try
            {
                if (!await _service.SetAdminAsync(_channel, u, new ChatAdminRights { flags = flags }, "")) { Vpn(); return; }
            }
            catch (Exception ex) { Err(ex); return; }
            System.Diagnostics.Debug.WriteLine("[ADMIN] set admin " + u.id + " flags=" + flags);
            ThemedDialog.Show(this, "Admin", DisplayName(u) + (existing == null ? " is now an admin." : " rights updated."), "OK");
        }

        private async void DismissFlow(User u)
        {
            if (ThemedDialog.Show(this, "Dismiss admin", "Dismiss " + DisplayName(u) + " as admin?", "Dismiss", "Cancel") != 0) return;
            try { if (!await _service.SetAdminAsync(_channel, u, new ChatAdminRights { flags = 0 }, "")) { Vpn(); return; } }
            catch (Exception ex) { Err(ex); return; }
            Remove(u);
        }

        private async void BanFlow(User u)
        {
            if (ThemedDialog.Show(this, "Ban user", "Ban " + DisplayName(u) + "? They'll be removed and can't rejoin.", "Ban", "Cancel") != 0) return;
            try { if (!await _service.BanMemberAsync(_channel, u)) { Vpn(); return; } }
            catch (Exception ex) { Err(ex); return; }
            Remove(u);
        }

        private async void KickFlow(User u)
        {
            if (ThemedDialog.Show(this, "Remove member", "Remove " + DisplayName(u) + " from the group?", "Remove", "Cancel") != 0) return;
            try { if (!await _service.KickMemberAsync(_channel, u)) { Vpn(); return; } }
            catch (Exception ex) { Err(ex); return; }
            Remove(u);
        }

        private async void UnbanFlow(User u)
        {
            if (ThemedDialog.Show(this, "Unban", "Unban " + DisplayName(u) + "?", "Unban", "Cancel") != 0) return;
            try { if (!await _service.UnbanMemberAsync(_channel, u)) { Vpn(); return; } }
            catch (Exception ex) { Err(ex); return; }
            Remove(u);
        }

        private void Remove(User u)
        {
            int idx = _users.FindIndex(x => x.id == u.id);
            if (idx >= 0) { _users.RemoveAt(idx); _list.SetItems(_users.Count); }
            if (_users.Count == 0) ShowMessage(EmptyText());
        }

        private void Vpn() { ThemedDialog.Show(this, Text, "Couldn't reach Telegram — make sure your VPN is on.", "OK"); }
        private void Err(Exception ex) { ThemedDialog.Show(this, Text, "Couldn't complete: " + ex.Message, "OK"); }

        private static string DisplayName(User u)
        {
            if (u == null) return "User";
            string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            if (n.Length > 0) return n;
            if (!string.IsNullOrEmpty(u.username)) return u.username;
            return "User " + u.id;
        }

        private static List<KeyValuePair<string, ChatAdminRights.Flags>> AdminRightsSpec(bool mega)
        {
            var L = new List<KeyValuePair<string, ChatAdminRights.Flags>>();
            Action<string, ChatAdminRights.Flags> add = (s, f) => L.Add(new KeyValuePair<string, ChatAdminRights.Flags>(s, f));
            if (mega)
            {
                add("Change group info", ChatAdminRights.Flags.change_info);
                add("Delete messages", ChatAdminRights.Flags.delete_messages);
                add("Ban users", ChatAdminRights.Flags.ban_users);
                add("Add users", ChatAdminRights.Flags.invite_users);
                add("Pin messages", ChatAdminRights.Flags.pin_messages);
                add("Manage video chats", ChatAdminRights.Flags.manage_call);
                add("Add new admins", ChatAdminRights.Flags.add_admins);
                add("Remain anonymous", ChatAdminRights.Flags.anonymous);
            }
            else
            {
                add("Change channel info", ChatAdminRights.Flags.change_info);
                add("Post messages", ChatAdminRights.Flags.post_messages);
                add("Edit messages of others", ChatAdminRights.Flags.edit_messages);
                add("Delete messages of others", ChatAdminRights.Flags.delete_messages);
                add("Add subscribers", ChatAdminRights.Flags.invite_users);
                add("Manage live streams", ChatAdminRights.Flags.manage_call);
                add("Add new admins", ChatAdminRights.Flags.add_admins);
            }
            return L;
        }
    }
}
