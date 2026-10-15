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
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The list is the dialog's own
    /// body (<c>UseOwnBody</c>: it scrolls by itself, themed bar and touch pan included, and pages in more rows when it
    /// reaches its end); the loading / failed / empty messages are an <see cref="EmptyState"/>; "Close" is the footer.
    /// What did not change: the Mode enum, the paging, the live presence and avatar repaints, the row actions and every
    /// service call and message. <paramref name="dark"/> and <paramref name="accent"/> are kept for the callers; this form
    /// itself follows the palette live and only hands them on to <see cref="RightsChecklistForm"/> (still a 1.x form).
    /// </summary>
    public sealed class ChatMembersForm : DialogForm
    {
        public enum Mode { Members, Admins, Banned }

        private const int PageSize = 100;
        private const int RowLogical = 56;   // one row, logical px — the list takes it at the window's DPI

        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly bool _dark, _isMega;
        private readonly Color _accent;   // _dark / _accent: only for RightsChecklistForm
        private readonly Mode _mode;

        private readonly Panel _host;
        private readonly ThemedListBox _list;
        private readonly EmptyState _status;
        private readonly AvatarStore _avatars;   // shared store (ambient); null-safe throughout
        private readonly List<User> _users = new List<User>();
        private readonly Dictionary<long, ChatAdminRights> _adminRights = new Dictionary<long, ChatAdminRights>();
        private readonly Dictionary<long, string> _ranks = new Dictionary<long, string>();
        private bool _loading, _exhausted;
        private readonly bool _built;   // ApplyDialogTheme is first called from the base constructor, before the controls exist

        public ChatMembersForm(TelegramService service, Channel channel, Mode mode, bool dark, Color accent)
            : base(TitleFor(mode), 390, 580)
        {
            _service = service; _channel = channel; _mode = mode; _dark = dark; _accent = accent;
            _isMega = (channel.flags & Channel.Flags.megagroup) != 0;
            var p = ThemeHelper.Palette;

            // Owner-drawn list with a themed scrollbar that's dark on RT 8.1 too. Hidden while the status shows (as in 1.x).
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window, Visible = false };
            _list.AccessibleName = Text;
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;
            _list.ReachedEnd += async () => await LoadMore();

            _status = new EmptyState { Dock = DockStyle.Fill, Hint = "Loading…" };

            _host = new Panel { BackColor = p.Window };
            _host.Controls.Add(_list);      // the two Fill children share the space (one is visible at a time)
            _host.Controls.Add(_status);
            UseOwnBody(_host);

            CancelButton = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);

            // Member avatars from the shared AvatarStore (cached + demand) — arrivals repaint their row.
            _avatars = AvatarStore.Current;
            if (_avatars != null)
            {
                _avatars.AvatarLoaded += OnAvatarLoaded;
                Disposed += (s, e) => _avatars.AvatarLoaded -= OnAvatarLoaded;
            }

            _built = true;
            Shown += async (s, e) => await LoadMore();

            // PRESENCE 4.2: a listed user's status change repaints that row live while the form is open.
            // Subscribe on open, unsubscribe on dispose (E-3) — the event is raised on the UI thread.
            MainForm.UserStatusChanged += OnUserStatusChanged;
            Disposed += (s, e) => MainForm.UserStatusChanged -= OnUserStatusChanged;
        }

        private static string TitleFor(Mode mode)
        {
            return mode == Mode.Admins ? "Administrators" : mode == Mode.Banned ? "Banned users" : "Members";
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_built) return;
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_users.Count);   // the canvas height follows the row height
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (!_built) return;
            var p = ThemeHelper.Palette;
            _host.BackColor = p.Window;
            _list.CanvasBackColor = p.Window;
            _list.Invalidate(true);   // the rows read the palette when they paint
            _status.Invalidate();
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
                if (IsDisposed) return;   // the dialog was closed while the page was on its way
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

        /// <summary>The loading / failed / empty message, in place of the list.</summary>
        private void ShowMessage(string text)
        {
            if (IsDisposed) return;
            _status.Hint = text;
            _status.Visible = true;
            _list.Visible = false;
        }

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
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(p.Window)) g.FillRectangle(bb, r);

            int d = Px(40), ax = r.Left + Px(10), ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            string name = DisplayName(u);
            var av = _avatars?.GetCached(u.id);   // memory-only lookup — render-hot safe
            if (av != null) DrawHelper.DrawCircularImage(g, avatarRect, av);
            else
            {
                using (var ab = new SolidBrush(DrawHelper.AvatarColor(u.id))) g.FillEllipse(ab, avatarRect);
                string initial = name.Length > 0 ? DrawHelper.Initial(name) : "?";
                DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 14f, dpi, FontStyle.Bold), avatarRect, Color.White);   // ink-centred (owner report 2026-10-14)
            }

            // Fonts without the text argument = the Roboto face, as in 1.x: GDI substitutes Persian glyphs, and the taller
            // Vazirmatn line would not fit these fixed name / status bands.
            int tx = ax + d + Px(12), tw = r.Right - tx - Px(12);
            TextRenderer.DrawText(g, name, TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top + Px(9), tw, Px(20)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            string sub = SubText(u);
            if (sub.Length > 0)
                TextRenderer.DrawText(g, sub, TextRoles.Font(TextRole.Field, dpi), new Rectangle(tx, r.Top + Px(30), tw, Px(18)),
                    sub == "online" ? p.AccentFg : p.TextDim,   // PRESENCE 4.1: "online" in accent, like the header
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (var sep = new Pen(p.Divider))
                g.DrawLine(sep, r.Left + Px(10), r.Bottom - 1, r.Right - Px(10), r.Bottom - 1);
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
            menu.FontPt = 10.5f;   // step 3.1: applied at the open point's DPI when the menu opens. The row padding is the menu's own.
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
            menu.ShowAt(_list, _list.PointToClient(Cursor.Position));   // at the cursor: the menu sizes itself at that monitor's DPI
        }

        private void AddItem(ContextMenuStrip menu, string text, Action act)
        {
            var it = new ToolStripMenuItem(text);
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
            Logger.Diag("[ADMIN] set admin " + u.id + " flags=" + flags);
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
