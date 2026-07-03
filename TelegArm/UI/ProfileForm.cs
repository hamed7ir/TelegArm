using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed profile dialog. View mode (others) mimics Telegram Desktop: big avatar, name + status,
    /// an action-tile row (Message / Mute / Call / More), contact info, a counted media row-list
    /// (Photos / Videos / Files / Audio / Links / Voice / GIFs) that opens a category gallery, and a
    /// bottom action list (Share / Edit / Delete / Block). Edit mode (self) lets you change name + bio.
    /// </summary>
    public class ProfileForm : MaterialForm
    {
        private readonly TelegramService _service;
        private readonly bool _editable;
        private readonly ChatEntry _entry;
        private Image _avatar;
        private readonly bool _ownsAvatar;
        // Not readonly: re-derived by OnThemeChanged if the theme flips while the profile is open (UI-FIX-T1).
        private bool _dark = ThemeHelper.IsDark;
        private Color _accentColor = ThemeHelper.GetWindowsAccentColor();

        // Edit-mode controls.
        private Panel _pic;
        private MaterialLabel _titleLbl;
        private MaterialTextBox2 _first, _last, _aboutBox;
        private TextBox _contactBox;

        // View-mode controls.
        private FlowLayoutPanel _flow;
        private MaterialLabel _nameLbl, _statusLbl;
        private TextBox _idBox;
        private Controls.RichInfoLabel _details;
        private ActionTile _muteTile;
        private bool _muted;
        private bool _blocked;

        /// <summary>Set when the user left this channel/group (caller may refresh the chat list).</summary>
        public bool LeftChat { get; private set; }

        /// <summary>Set when the user clicked "Message" (caller should open a 1:1 chat).</summary>
        public bool SendMessageRequested { get; private set; }

        // Rich-info link activation — set when a description URL/@mention/#hashtag is tapped; the form closes
        // and MainForm routes the request (so navigation happens in front of the now-closed modal profile).
        public string PendingLink { get; private set; }
        public string PendingMentionUser { get; private set; }
        public long PendingMentionId { get; private set; }
        public string PendingHashtag { get; private set; }

        /// <summary>Raised when a gallery item asks to be forwarded: (source peer, message id).</summary>
        public event Action<InputPeer, int> ForwardRequested;
        private void RaiseForward(int messageId) { if (_entry?.Peer != null) ForwardRequested?.Invoke(_entry.Peer, messageId); }

        /// <summary>Raised when a gallery item asks to jump to it in the conversation: (peer, message id).</summary>
        public event Action<InputPeer, int> ShowInChatRequested;
        private void RaiseShowInChat(int messageId)
        {
            if (_entry?.Peer == null) return;
            ShowInChatRequested?.Invoke(_entry.Peer, messageId);
            // Close the profile (the gallery closes itself) so the conversation becomes the visible view.
            try { BeginInvoke((Action)Close); } catch { Close(); }
        }

        public ProfileForm(TelegramService service) : this(service, null, null, true) { }
        public ProfileForm(TelegramService service, ChatEntry entry, Image avatar) : this(service, entry, avatar, false) { }

        private ProfileForm(TelegramService service, ChatEntry entry, Image avatar, bool editable)
        {
            _service = service;
            _entry = entry;
            _avatar = avatar;
            _editable = editable;
            _ownsAvatar = editable;
            _muted = entry?.Muted ?? false;

            var skin = MaterialSkinManager.Instance;
            skin.AddFormToManage(this);
            skin.Theme = _dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
            var accent = (Primary)(uint)_accentColor.ToArgb();
            // Same singleton as MainForm — keep the MaterialSkin accent on the Windows accent (not the default
            // light blue) so opening a profile doesn't flip the whole app's text-box accent back to blue.
            var msAccent = (Accent)(uint)_accentColor.ToArgb();
            skin.ColorScheme = new ColorScheme(accent, accent, accent, msAccent, TextShade.WHITE);

            FormStyle = FormStyles.ActionBar_None;
            Text = editable ? "My Profile" : "Profile";
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; Sizable = false;
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

            if (_editable) BuildEditMode();
            else BuildViewMode();

            // UI-FIX-T1: retheme live if the theme flips while the profile is open. The in-app switchers
            // (Settings / drawer) are unreachable behind this modal, so the only live path is a System-mode OS
            // switch — MainForm's handler re-sets the global MaterialSkin theme first (subscribed earlier), so
            // by the time our BeginInvoke runs the skin (and this form's BackColor) are already current.
            ThemeHelper.ThemeChanged += OnThemeChanged;
            Disposed += (s, e) => ThemeHelper.ThemeChanged -= OnThemeChanged;   // event-leak discipline (E-3)

            Load += (s, e) => LoadDetails();
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _dark = ThemeHelper.IsDark;
                    _accentColor = ThemeHelper.GetWindowsAccentColor();
                    RepushThemed(this);
                    Invalidate(true);   // paint-time readers (PaintAvatar etc.) pick up the re-derived fields
                }));
            }
            catch { /* form tearing down mid-switch — reopen is fresh anyway */ }
        }

        /// <summary>Re-pushes construction-time theme state into the owner-drawn children (tiles, list rows)
        /// and the read-only text boxes; MaterialSkin controls follow the global skin on their own.</summary>
        private void RepushThemed(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ActionTile tile) tile.SetTheme(_dark, _accentColor);
                else if (c is ProfileRow row) row.SetTheme(_dark, _accentColor);
                else if (c is TextBox tb && tb.ReadOnly)
                {
                    tb.BackColor = BackColor;   // ReadOnlyBox pairs — same derivation as the builder
                    tb.ForeColor = _dark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(35, 35, 35);
                }
                if (c.Controls.Count > 0) RepushThemed(c);
            }
        }

        private User SelfUser => _service.Me;
        private User OtherUser => _entry?.PeerInfo as User;

        private string DisplayLetter()
        {
            string t = _editable ? (SelfUser?.first_name ?? "?") : (_entry?.Title ?? "?");
            return string.IsNullOrEmpty(t) ? "?" : t.Substring(0, 1).ToUpper();
        }

        private long AvatarColorKey => _editable ? (SelfUser?.id ?? 0) : (_entry?.PeerId ?? 0);

        // ── Edit mode (self) — unchanged behaviour ───────────────────────────
        private void BuildEditMode()
        {
            ClientSize = new Size(420, 510);

            _pic = new Panel { Size = new Size(96, 96), Location = new Point((420 - 96) / 2, 24), BackColor = Color.Transparent, Cursor = Cursors.Hand };
            _pic.Paint += (s, e) => PaintAvatar(e.Graphics, new Rectangle(0, 0, 96, 96), 34f);
            _pic.Click += (s, e) => ViewPhoto();
            Controls.Add(_pic);

            _titleLbl = new MaterialLabel
            {
                Text = FullName(SelfUser),
                Location = new Point(20, 130),
                AutoSize = false,
                Size = new Size(380, 28),
                FontType = MaterialSkinManager.fontType.H6,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(_titleLbl);

            _contactBox = ReadOnlyBox(24, 166, 372, 22, false);
            Controls.Add(_contactBox);

            Controls.Add(SmallLabel("First name", 24, 196));
            _first = new MaterialTextBox2 { Text = SelfUser?.first_name ?? "", Location = new Point(24, 218), Width = 372 };
            Controls.Add(_first);

            Controls.Add(SmallLabel("Last name", 24, 272));
            _last = new MaterialTextBox2 { Text = SelfUser?.last_name ?? "", Location = new Point(24, 294), Width = 372 };
            Controls.Add(_last);

            Controls.Add(SmallLabel("Bio", 24, 348));
            _aboutBox = new MaterialTextBox2 { Hint = "A few words about you", Location = new Point(24, 370), Width = 372 };
            Controls.Add(_aboutBox);

            var save = new MaterialButton { Text = "Save", Location = new Point(214, 452), Width = 90, Type = MaterialButton.MaterialButtonType.Contained };
            save.Click += OnSave;
            Controls.Add(save);
            var cancel = new MaterialButton { Text = "Cancel", Location = new Point(310, 452), Width = 90, Type = MaterialButton.MaterialButtonType.Outlined };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
        }

        // ── View mode (others) — Telegram-style scrollable layout ────────────
        private const int ContentW = 392;   // inner content width (within the 16px side margins)

        private void BuildViewMode()
        {
            ClientSize = new Size(440, 620);

            var outer = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            var host = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = 424,
                BackColor = BackColor,
                Padding = new Padding(0, 0, 0, 16)
            };
            host.Controls.Add(_flow);
            outer.Controls.Add(host);
            outer.Controls.Add(new Controls.ThemedScrollBar(host, _dark, _accentColor) { Dock = DockStyle.Right });
            Controls.Add(outer);
            TelegArm.UI.Controls.TouchScroller.Enable(host, horizontal: false);

            // Avatar.
            var avi = new Panel { Width = ContentW, Height = 112, BackColor = BackColor, Cursor = Cursors.Hand };
            avi.Paint += (s, e) => PaintAvatar(e.Graphics, new Rectangle((ContentW - 100) / 2, 8, 100, 100), 36f);
            avi.Click += (s, e) => ViewPhoto();
            AddFlow(avi, 8);

            _nameLbl = new MaterialLabel
            {
                Text = _entry?.Title ?? "",
                AutoSize = false,
                Size = new Size(ContentW, 28),
                FontType = MaterialSkinManager.fontType.H6,
                TextAlign = ContentAlignment.MiddleCenter
            };
            AddFlow(_nameLbl, 2);

            _statusLbl = new MaterialLabel
            {
                Text = StatusText(),
                AutoSize = false,
                Size = new Size(ContentW, 20),
                FontType = MaterialSkinManager.fontType.Caption,
                TextAlign = ContentAlignment.MiddleCenter
            };
            AddFlow(_statusLbl, 0);

            // Action-tile row — branches on peer type: a USER gets Message / Mute / Call / More; a GROUP or
            // CHANNEL gets only Mute / More (Message & Call are user-only — you can't DM or call a group/channel).
            // Whatever set applies is laid out centred.
            var actions = new Panel { Width = ContentW, Height = 66, BackColor = BackColor };
            bool isUser = OtherUser != null;
            var tiles = new List<ActionTile>();
            if (isUser)
            {
                var msg = NewTile("✉", "Message", 0);
                msg.Clicked += () => { SendMessageRequested = true; DialogResult = DialogResult.OK; Close(); };
                tiles.Add(msg);
            }
            _muteTile = NewTile("🔔", _muted ? "Unmute" : "Mute", 0); _muteTile.Clicked += ToggleMute;
            _muteTile.Glyph = _muted ? "🔕" : "🔔";
            tiles.Add(_muteTile);
            if (isUser)
            {
                var call = NewTile("📞", "Call", 0); call.Enabled2 = false; call.ToolTipText = "Voice calls aren't implemented yet";
                tiles.Add(call);
            }
            var more = NewTile("⋯", "More", 0); more.Clicked += () => ShowMoreMenu(more);
            tiles.Add(more);

            const int tileW = 92, tileGap = 100;
            int startX = Math.Max(0, (ContentW - ((tiles.Count - 1) * tileGap + tileW)) / 2);
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Location = new Point(startX + i * tileGap, 0);
                actions.Controls.Add(tiles[i]);
            }
            AddFlow(actions, 12);

            // Contact info + bio (selectable / copyable).
            AddFlow(SectionLabel("INFO"), 14);
            _details = new Controls.RichInfoLabel(_dark, _accentColor, new Font("Segoe UI", 9.75f))
            {
                Width = ContentW,
                Margin = new Padding(16, 2, 16, 0)
            };
            _details.LinkClicked += url => { PendingLink = url; DialogResult = DialogResult.OK; Close(); };
            _details.MentionClicked += (un, uid) => { PendingMentionUser = un; PendingMentionId = uid; DialogResult = DialogResult.OK; Close(); };
            _details.HashtagClicked += tag => { PendingHashtag = tag; DialogResult = DialogResult.OK; Close(); };
            _flow.Controls.Add(_details);

            // Secondary, selectable ID line (every peer type).
            _idBox = ReadOnlyBox(0, 0, ContentW, 20, false);
            _idBox.Margin = new Padding(16, 0, 16, 0);
            _idBox.Font = new Font("Segoe UI", 8.25f);
            _idBox.ForeColor = _dark ? Color.FromArgb(140, 140, 145) : Color.FromArgb(140, 140, 145);
            _flow.Controls.Add(_idBox);

            // Media + bottom actions are appended in LoadDetails (counts are async).
        }

        private void AddFlow(Control c, int topGap)
        {
            c.Margin = new Padding(16, topGap, 16, 0);
            _flow.Controls.Add(c);
        }

        private ActionTile NewTile(string glyph, string label, int x)
        {
            return new ActionTile(_dark, _accentColor) { Glyph = glyph, Label = label, Location = new Point(x, 0) };
        }

        private MaterialLabel SectionLabel(string text)
        {
            return new MaterialLabel
            {
                Text = text,
                AutoSize = false,
                Size = new Size(ContentW, 20),
                FontType = MaterialSkinManager.fontType.Caption,
                ForeColor = _accentColor
            };
        }

        private string StatusText()
        {
            var u = OtherUser;
            if (u != null)
            {
                if (u.IsBot) return "bot";
                switch (u.status)
                {
                    case UserStatusOnline _: return "online";
                    case UserStatusRecently _: return "last seen recently";
                    case UserStatusLastWeek _: return "last seen within a week";
                    case UserStatusLastMonth _: return "last seen within a month";
                    case UserStatusOffline off: return "last seen " + off.was_online.ToLocalTime().ToString("g");
                    default: return "last seen a long time ago";
                }
            }
            if (_entry != null) return _entry.Peer is InputPeerChannel ? "channel" : "group";
            return "";
        }

        // ── Action handlers ──────────────────────────────────────────────────
        private async void ToggleMute()
        {
            bool target = !_muted;
            bool ok;
            try { ok = await _service.ToggleMuteAsync(_entry.Peer, target); }
            catch (Exception ex) { ThemedDialog.Show(this, "Mute", "Couldn't change mute: " + ex.Message, "OK"); return; }
            if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[NOTIFY] mute write peer=" + (_entry?.PeerId ?? 0) + " mute=" + target + (ok ? " ok" : " FAILED (server returned false)"));
            if (!ok) { ThemedDialog.Show(this, "Mute", "Telegram didn't accept the change — try again.", "OK"); return; }
            _muted = target;
            if (_entry != null) _entry.Muted = target;
            if (_muteTile != null && !_muteTile.IsDisposed)
            {
                _muteTile.Glyph = _muted ? "🔕" : "🔔";
                _muteTile.Label = _muted ? "Unmute" : "Mute";
                _muteTile.Invalidate();
            }
        }

        private void ShowMoreMenu(Control anchor)
        {
            var menu = new ThemedContextMenuStrip();
            if (OtherUser != null)               // USER → contact actions
            {
                MenuItem(menu, "Share contact", ShareContact);
                MenuItem(menu, "Edit contact", EditContact);
                MenuItem(menu, "Delete contact", DeleteContact);
                menu.Items.Add(new ToolStripSeparator());
                MenuItem(menu, _blocked ? "Unblock user" : "Block user", BlockUser);
            }
            else                                 // GROUP / CHANNEL → group/channel actions (no contact items, no block)
            {
                if (AdminChannel != null)        // only for groups/channels this user can administer
                {
                    MenuItem(menu, "Manage", OpenManage);
                    menu.Items.Add(new ToolStripSeparator());
                }
                MenuItem(menu, _muted ? "Unmute" : "Mute", ToggleMute);
                menu.Items.Add(new ToolStripSeparator());
                MenuItem(menu, LeaveLabel(), LeaveChat);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private void MenuItem(ContextMenuStrip menu, string text, Action action)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = _dark ? Color.White : Color.FromArgb(30, 30, 30) };
            item.Click += (s, e) => BeginInvoke(action);
            menu.Items.Add(item);
        }

        /// <summary>The peer as an admin-able Channel (creator or has admin_rights), else null — gates the admin UI.</summary>
        private Channel AdminChannel
        {
            get
            {
                var ch = _entry?.PeerInfo as Channel;
                if (ch == null) return null;
                bool can = (ch.flags & Channel.Flags.creator) != 0 || ch.admin_rights != null;
                return can ? ch : null;
            }
        }

        private void OpenManage()
        {
            var ch = AdminChannel;
            if (ch == null || _entry?.Peer == null) return;
            using (var f = new Admin.ManageChatForm(_service, ch, _entry.Peer, _entry.Title ?? "", "", _dark, _accentColor))
                f.ShowDialog(this);
        }

        private void ShareContact()
            => ThemedDialog.Show(this, "Share contact", "Sharing a contact card isn't implemented yet.", "OK");

        private void EditContact()
            => ThemedDialog.Show(this, "Edit contact", "Editing the saved contact name isn't implemented yet.", "OK");

        private async void DeleteContact()
        {
            // Destructive + not part of an existing path → confirm and stub.
            await System.Threading.Tasks.Task.Yield();
            ThemedDialog.Show(this, "Delete contact", "Deleting a contact isn't implemented yet.", "OK");
        }

        private async void BlockUser()
        {
            if (OtherUser == null) { ThemedDialog.Show(this, "Block", "Only users can be blocked.", "OK"); return; }
            bool target = !_blocked;
            try { await _service.SetBlockedAsync(_entry.Peer, target); }
            catch (Exception ex) { ThemedDialog.Show(this, "Block", "Couldn't change block state: " + ex.Message, "OK"); return; }
            _blocked = target;
            ThemedDialog.Show(this, "Block", target ? "User blocked." : "User unblocked.", "OK");
        }

        // ── Media counts → counted rows ──────────────────────────────────────
        private struct MediaCat { public string Glyph, Label; public MessagesFilter Filter; public bool ListMode; }

        private static List<MediaCat> Categories() => new List<MediaCat>
        {
            new MediaCat { Glyph = "🖼", Label = "Photos",         Filter = new InputMessagesFilterPhotos() },
            new MediaCat { Glyph = "🎬", Label = "Videos",         Filter = new InputMessagesFilterVideo() },
            new MediaCat { Glyph = "📄", Label = "Files",          Filter = new InputMessagesFilterDocument() },
            new MediaCat { Glyph = "🎵", Label = "Audio",          Filter = new InputMessagesFilterMusic(),  ListMode = true },
            new MediaCat { Glyph = "🔗", Label = "Shared links",   Filter = new InputMessagesFilterUrl(),    ListMode = true },
            new MediaCat { Glyph = "🎤", Label = "Voice messages", Filter = new InputMessagesFilterVoice(),  ListMode = true },
            new MediaCat { Glyph = "🎞", Label = "GIFs",           Filter = new InputMessagesFilterGif() },
        };

        private async void LoadMediaCounts()
        {
            if (_entry?.Peer == null) return;
            var cats = Categories();
            Messages_SearchCounter[] counters;
            try { counters = await _service.GetMediaCountsAsync(_entry.Peer, cats.Select(c => c.Filter).ToArray()); }
            catch { return; }
            if (IsDisposed || _flow == null) return;

            var byType = new Dictionary<Type, int>();
            foreach (var c in counters)
                if (c?.filter != null) byType[c.filter.GetType()] = c.count;

            bool any = false;
            foreach (var cat in cats)
            {
                byType.TryGetValue(cat.Filter.GetType(), out int n);
                if (n <= 0) continue;
                if (!any) { AddFlow(SectionLabel("SHARED MEDIA"), 14); any = true; }
                var captured = cat;
                var row = new ProfileRow(_dark, _accentColor) { Glyph = cat.Glyph, Label = cat.Label, Trailing = n.ToString("N0"), Width = ContentW };
                row.Clicked += () => OpenCategory(captured);
                AddFlow(row, 0);
            }
        }

        private void OpenCategory(MediaCat cat)
        {
            if (_entry?.Peer == null) return;
            using (var f = new MediaCategoryForm(this, _service, _entry.Peer, cat.Filter, cat.Label, cat.ListMode, _dark, _accentColor))
                f.ShowDialog(this);
        }

        // ── Bottom action list (adapts by peer type) ─────────────────────────
        private void AddBottomActions()
        {
            AddFlow(SectionLabel("ACTIONS"), 16);
            if (OtherUser != null)   // user contact → Share/Edit/Delete + Block
            {
                AddActionRow("📤", "Share contact", false, ShareContact);
                AddActionRow("✏", "Edit contact", false, EditContact);
                AddActionRow("🗑", "Delete contact", true, DeleteContact);
                AddActionRow("🚫", _blocked ? "Unblock user" : "Block user", true, BlockUser);
            }
            else                     // channel / group → Leave (destructive)
            {
                AddActionRow("🚪", LeaveLabel(), true, LeaveChat);
            }
        }

        /// <summary>"Leave channel" for a broadcast, "Leave group" for a megagroup/basic group, else "Leave".</summary>
        private string LeaveLabel()
        {
            if (_entry?.PeerInfo is Channel c)
                return (c.flags & Channel.Flags.broadcast) != 0 ? "Leave channel"
                     : (c.flags & Channel.Flags.megagroup) != 0 ? "Leave group" : "Leave";
            return "Leave group";   // basic (legacy) group — InputPeerChat
        }

        private async void LeaveChat()
        {
            string what = LeaveLabel();
            int r = ThemedDialog.Show(this, what + "?",
                "You'll stop receiving messages from this chat.", what, "Cancel");
            if (r != 0) return;   // 0 = destructive primary
            try { await _service.LeaveChatAsync(_entry.Peer); }
            catch (Exception ex) { ThemedDialog.Show(this, "Leave", "Couldn't leave: " + ex.Message, "OK"); return; }
            LeftChat = true;
            DialogResult = DialogResult.OK;
            Close();   // close the profile after leaving
        }

        private void AddActionRow(string glyph, string label, bool danger, Action action)
        {
            var row = new ProfileRow(_dark, _accentColor) { Glyph = glyph, Label = label, Danger = danger, Width = ContentW };
            row.Clicked += () => action();
            AddFlow(row, 0);
        }

        // ── Details load (status + contact/bio + counts) ─────────────────────
        private async void LoadDetails()
        {
            if (_editable)
            {
                _contactBox.Text = ContactLine(SelfUser);
                if (_avatar == null && SelfUser != null)
                {
                    try { var bytes = await _service.DownloadAvatarAsync(SelfUser); if (bytes != null && bytes.Length > 0) { _avatar = ToBitmap(bytes); _pic.Invalidate(); } } catch { }
                }
                string about = await _service.GetSelfAboutAsync();
                if (_aboutBox != null && !IsDisposed) _aboutBox.Text = about;
                return;
            }

            // View mode.
            var (about2, members, _, groupUsers) = await _service.GetPeerDetailsAsync(_entry.Peer, _entry?.PeerInfo);
            if (IsDisposed) return;

            var u = OtherUser;
            // PEER-PRESENTATION wording: broadcasts have subscribers, groups have members.
            if (members > 0 && _statusLbl != null)
                _statusLbl.Text = members.ToString("N0")
                    + (_entry?.PeerInfo is Channel sc && (sc.flags & Channel.Flags.broadcast) != 0 ? " subscribers" : " members");

            // Identifier section per peer type (all fields already live on the resolved peer — no round-trip).
            var sb = new System.Text.StringBuilder();
            string un = ActiveUsername(_entry?.PeerInfo);
            if (u != null)
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(un)) parts.Add("@" + un);
                if (!string.IsNullOrEmpty(u.phone)) parts.Add("+" + u.phone);
                if (parts.Count > 0) sb.AppendLine(string.Join("    ", parts));
            }
            else if (!string.IsNullOrEmpty(un))   // public channel / group
            {
                sb.AppendLine("@" + un);
                sb.AppendLine("t.me/" + un);
            }
            if (!string.IsNullOrEmpty(about2)) { if (sb.Length > 0) sb.AppendLine(); sb.AppendLine(about2); }
            string detailsText = sb.ToString().TrimEnd();
            if (_details != null)
            {
                if (detailsText.Length == 0) _details.Visible = false;
                else { _details.Visible = true; _details.SetText(detailsText, Helpers.TextEntities.Detect(detailsText), null); }
            }

            if (_idBox != null) _idBox.Text = "ID: " + RawPeerId();

            // PROFILE-MEMBERS: groups/megagroups only — broadcasts never list subscribers. The section
            // label + container append SYNCHRONOUSLY (fixed flow order above SHARED MEDIA); rows fill async.
            if (IsGroupProfile) BuildMembersSection(members, groupUsers);

            LoadMediaCounts();
            AddBottomActions();
        }

        // ── Members section (PROFILE-MEMBERS) ────────────────────────────────
        /// <summary>MainForm hands its AvatarStore in at construction so member rows get cached avatars +
        /// demand fetches; null = initials-only fallback.</summary>
        internal Core.AvatarStore Avatars { get; set; }

        private FlowLayoutPanel _membersPanel;
        private readonly List<MemberRow> _memberRows = new List<MemberRow>();
        private bool _membersClosed;

        private bool IsGroupProfile =>
            _entry?.PeerInfo is Chat
            || (_entry?.PeerInfo is Channel gch && (gch.flags & Channel.Flags.broadcast) == 0);

        private void BuildMembersSection(int totalMembers, List<User> basicUsers)
        {
            AddFlow(SectionLabel(totalMembers > 0 ? totalMembers.ToString("N0") + " MEMBERS" : "MEMBERS"), 14);
            _membersPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Width = ContentW,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(16, 0, 16, 0)
            };
            _flow.Controls.Add(_membersPanel);

            // Live status rows (the ChatMembersForm Part-4 pattern, verbatim: subscribe on open,
            // unsubscribe on Disposed) + avatar arrivals repaint their row.
            MainForm.UserStatusChanged += OnMemberStatusChanged;
            if (Avatars != null) Avatars.AvatarLoaded += OnMemberAvatarLoaded;
            Disposed += (s, e) =>
            {
                _membersClosed = true;
                MainForm.UserStatusChanged -= OnMemberStatusChanged;
                if (Avatars != null) Avatars.AvatarLoaded -= OnMemberAvatarLoaded;
            };

            if (_entry.PeerInfo is Channel mg) FillMegagroupMembersAsync(mg, totalMembers);
            else FillMemberRows(basicUsers, totalMembers);   // basic group: free with the details fetch
        }

        private async void FillMegagroupMembersAsync(Channel mg, int totalMembers)
        {
            try
            {
                // ONE page per profile open, bounded + off the UI thread inside the service helper.
                var res = await _service.GetParticipantsAsync(mg, new ChannelParticipantsRecent(), 0, 20);
                if (_membersClosed || IsDisposed)
                {
                    if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[PRESENCE] members fetch discarded (profile closed)");
                    return;
                }
                if (res?.participants == null || res.users == null || res.participants.Length == 0)
                {
                    if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[PRESENCE] members hidden");
                    return;   // hidden/forbidden list → count header only, no rows, no error UI
                }
                var users = new List<User>();
                foreach (var p in res.participants)
                    if (res.users.TryGetValue(p.UserId, out var ub) && ub is User mu) users.Add(mu);
                FillMemberRows(users, totalMembers);
            }
            catch (Exception ex)
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[PRESENCE] members hidden (" + ex.Message + ")");
            }
        }

        private void FillMemberRows(List<User> users, int totalMembers)
        {
            if (users == null || users.Count == 0)
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[PRESENCE] members hidden");
                return;
            }
            foreach (var u in users.Take(20))
            {
                var row = new MemberRow(u, _dark, _accentColor) { Width = ContentW - 32 };
                row.Avatar = Avatars?.GetCached(u.id);
                if (row.Avatar == null) Avatars?.Request(u.id, u);
                var captured = u;
                row.Clicked += () => OpenMemberProfile(captured);
                _memberRows.Add(row);
                _membersPanel.Controls.Add(row);
            }
            if (totalMembers > users.Count && _entry.PeerInfo is Channel mgAll)
            {
                var all = new ProfileRow(_dark, _accentColor)
                { Glyph = "👥", Label = "Show all " + totalMembers.ToString("N0") + " members", Width = ContentW - 32 };
                all.Clicked += () =>
                {
                    using (var f = new Admin.ChatMembersForm(_service, mgAll, Admin.ChatMembersForm.Mode.Members, _dark, _accentColor))
                        f.ShowDialog(this);
                };
                _membersPanel.Controls.Add(all);
            }
        }

        private void OnMemberStatusChanged(long userId, UserStatus status)
        {
            if (IsDisposed) return;
            foreach (var r in _memberRows)
                if (r.UserId == userId) { r.RefreshStatus(status); break; }
        }

        private void OnMemberAvatarLoaded(long peerId)
        {
            if (IsDisposed) return;
            foreach (var r in _memberRows)
                if (r.UserId == peerId)
                {
                    var img = Avatars?.GetCached(peerId);
                    if (img != null) { r.Avatar = img; try { BeginInvoke((Action)r.Invalidate); } catch { } }
                    break;
                }
        }

        private void OpenMemberProfile(User u)
        {
            string name = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
            if (name.Length == 0) name = !string.IsNullOrEmpty(u.username) ? u.username : "User " + u.id;
            var e = new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = name, IsGroup = false, PeerInfo = u };
            using (var f = new ProfileForm(_service, e, Avatars?.GetCached(u.id)))
            {
                f.Avatars = Avatars;
                f.ShowDialog(this);
            }
        }

        /// <summary>One member row: avatar (store-cached or initials), name, status sublabel via the SAME
        /// formatter the chat header uses ("online" in accent). Owner-drawn, no per-row RPCs.</summary>
        private sealed class MemberRow : Control
        {
            private readonly User _u;
            private readonly bool _dark;
            private readonly Color _accent;
            public Image Avatar;
            public long UserId => _u.id;
            public event Action Clicked;

            public MemberRow(User u, bool dark, Color accent)
            {
                _u = u; _dark = dark; _accent = accent;
                Height = 52;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Click += (s, e) => Clicked?.Invoke();
            }

            public void RefreshStatus(UserStatus status)
            {
                _u.status = status;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Parent?.BackColor ?? (_dark ? Color.FromArgb(40, 40, 44) : Color.White));

                const int d = 40, ax = 0;
                int ay = (Height - d) / 2;
                if (Avatar != null)
                {
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        clip.AddEllipse(ax, ay, d, d);
                        var old = g.Clip;
                        g.SetClip(clip);
                        g.DrawImage(Avatar, ax, ay, d, d);
                        g.Clip = old;
                    }
                }
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_u.id))) g.FillEllipse(b, ax, ay, d, d);
                    string name0 = ((_u.first_name ?? "") + (_u.username ?? "?"));
                    string initial = name0.Length > 0 ? name0.Substring(0, 1).ToUpperInvariant() : "?";
                    TextRenderer.DrawText(g, initial, FontHelper.Ui(13f, FontStyle.Bold), new Rectangle(ax, ay, d, d),
                        Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }

                string nm = ((_u.first_name ?? "") + " " + (_u.last_name ?? "")).Trim();
                if (nm.Length == 0) nm = !string.IsNullOrEmpty(_u.username) ? _u.username : "User " + _u.id;
                Color fg = _dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
                Color sub = _dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
                int tx = ax + d + 12, tw = Width - tx;
                TextRenderer.DrawText(g, nm, FontHelper.Ui(10.5f, FontStyle.Bold), new Rectangle(tx, 7, tw, 20), fg,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                string st = MainForm.StatusText(_u);
                if (st.Length == 0) st = !string.IsNullOrEmpty(_u.username) ? "@" + _u.username : "member";
                TextRenderer.DrawText(g, st, FontHelper.Ui(8.5f), new Rectangle(tx, 28, tw, 18),
                    st == "online" ? _accent : sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>Active @username for any peer: collection-aware (active entry) → legacy `.username` → null.</summary>
        private static string ActiveUsername(IPeerInfo info)
        {
            if (info is Channel c) return PickUsername(c.usernames, c.username);
            if (info is User u) return PickUsername(u.usernames, u.username);
            return null;
        }

        private static string PickUsername(Username[] list, string legacy)
        {
            if (list != null)
                foreach (var un in list)
                    if (un != null && (un.flags & Username.Flags.active) != 0 && !string.IsNullOrEmpty(un.username))
                        return un.username;
            return legacy;
        }

        /// <summary>Raw peer id (channel_id / chat_id / user_id), never the -100… bot-API form.</summary>
        private long RawPeerId()
        {
            switch (_entry?.Peer)
            {
                case InputPeerUser pu: return pu.user_id;
                case InputPeerChannel pc: return pc.channel_id;
                case InputPeerChat pch: return pch.chat_id;
                default: return _entry?.PeerId ?? 0;
            }
        }

        // ── Shared helpers ───────────────────────────────────────────────────
        private TextBox ReadOnlyBox(int x, int y, int w, int h, bool multiline)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Width = w,
                Height = h,
                Multiline = multiline,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                TabStop = false,
                BackColor = BackColor,
                ForeColor = _dark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(35, 35, 35),
                Font = new Font("Segoe UI", 9.75f)
            };
        }

        private static string FullName(User u)
        {
            if (u == null) return "";
            return string.Join(" ", new[] { u.first_name, u.last_name }).Trim();
        }

        private MaterialLabel SmallLabel(string text, int x, int y)
        {
            return new MaterialLabel { Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(372, 20), FontType = MaterialSkinManager.fontType.Caption };
        }

        private void PaintAvatar(Graphics g, Rectangle rect, float fontSize)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_avatar != null)
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(rect);
                    g.SetClip(clip);
                    g.DrawImage(_avatar, rect);
                    g.ResetClip();
                }
            }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(AvatarColorKey)))
                    g.FillEllipse(b, rect);
                using (var f = new Font("Segoe UI", fontSize, FontStyle.Bold))
                    TextRenderer.DrawText(g, DisplayLetter(), f, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private static string ContactLine(User u)
        {
            if (u == null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(u.MainUsername)) parts.Add("@" + u.MainUsername);
            if (!string.IsNullOrEmpty(u.phone)) parts.Add("+" + u.phone);
            return string.Join("    ", parts);
        }

        private async void ViewPhoto()
        {
            IPeerInfo who = _editable ? (IPeerInfo)SelfUser : _entry?.PeerInfo;
            if (who == null) return;

            var images = new List<Image>();
            var u = who as User;
            if (u != null)
            {
                foreach (var b in await _service.GetUserPhotosAsync(u))
                {
                    var im = ToBitmapSafe(b);
                    if (im != null) images.Add(im);
                }
            }
            if (images.Count == 0)
            {
                var big = await _service.DownloadProfilePhotoBigAsync(who);
                var im = big != null ? ToBitmapSafe(big) : null;
                if (im != null) images.Add(im);
            }
            if (images.Count == 0 || IsDisposed) { foreach (var i in images) i.Dispose(); return; }

            using (var v = new ProfilePhotoViewer(images))
                v.ShowDialog(this);
        }

        private static Image ToBitmap(byte[] bytes)
        {
            using (var ms = new System.IO.MemoryStream(bytes))
            using (var tmp = Image.FromStream(ms))
                return new Bitmap(tmp);
        }

        private static Image ToBitmapSafe(byte[] bytes)
        {
            try { return bytes != null && bytes.Length > 0 ? ToBitmap(bytes) : null; } catch { return null; }
        }

        private async void OnSave(object sender, EventArgs e)
        {
            try
            {
                await _service.UpdateProfileAsync(_first.Text.Trim(), _last.Text.Trim(), _aboutBox.Text.Trim());
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Profile", "Couldn't save: " + ex.Message, "OK");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsAvatar && _avatar != null) { _avatar.Dispose(); _avatar = null; }
            base.Dispose(disposing);
        }

        // ── Owner-painted action tile (icon over label) ──────────────────────
        private sealed class ActionTile : Control
        {
            private bool _dark;
            private Color _accent;
            private bool _hover;
            private readonly ToolTip _tip = new ToolTip();

            public string Glyph, Label;
            public bool Enabled2 = true;
            public string ToolTipText { set { if (!string.IsNullOrEmpty(value)) _tip.SetToolTip(this, value); } }
            public event Action Clicked;

            /// <summary>Live retheme (UI-FIX-T1) — everything else is derived at paint time.</summary>
            public void SetTheme(bool dark, Color accent) { _dark = dark; _accent = accent; Invalidate(); }

            public ActionTile(bool dark, Color accent)
            {
                _dark = dark; _accent = accent;
                Size = new Size(92, 64);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (Enabled2) Clicked?.Invoke(); }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent?.BackColor ?? BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Color fill = _hover && Enabled2
                    ? (_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(232, 236, 240))
                    : (_dark ? Color.FromArgb(42, 42, 46) : Color.FromArgb(243, 245, 248));
                using (var b = new SolidBrush(fill))
                using (var p = DrawHelper.RoundedRect(r, 10))
                    g.FillPath(b, p);

                Color fg = Enabled2 ? _accent : (_dark ? Color.FromArgb(110, 110, 114) : Color.FromArgb(170, 170, 174));
                using (var gf = FontHelper.Ui(14f))
                    TextRenderer.DrawText(g, Glyph, gf, new Rectangle(0, 7, Width, 24), fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Color lc = Enabled2 ? (_dark ? Color.FromArgb(220, 220, 224) : Color.FromArgb(60, 60, 64)) : fg;
                using (var lf = FontHelper.Ui(8f))
                    TextRenderer.DrawText(g, Label, lf, new Rectangle(0, 36, Width, 20), lc,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // ── Owner-painted list row (icon + label + trailing count) ───────────
        private sealed class ProfileRow : Control
        {
            private bool _dark;
            private Color _accent;
            private bool _hover;

            public string Glyph, Label, Trailing;
            public bool Danger;
            public event Action Clicked;

            /// <summary>Live retheme (UI-FIX-T1) — everything else is derived at paint time.</summary>
            public void SetTheme(bool dark, Color accent) { _dark = dark; _accent = accent; Invalidate(); }

            /// <summary>Right-click / touch-and-hold / menu key (screen point). Only fires if subscribed.</summary>
            public event EventHandler<Point> ContextMenuRequested;
            private const int WM_CONTEXTMENU = 0x007B;

            public ProfileRow(bool dark, Color accent)
            {
                _dark = dark; _accent = accent;
                Height = 48;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_CONTEXTMENU)
                {
                    int lp = m.LParam.ToInt32();
                    Point pt = lp == -1
                        ? PointToScreen(new Point(Width / 2, Height / 2))
                        : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                    ContextMenuRequested?.Invoke(this, pt);
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Clicked?.Invoke(); }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent?.BackColor ?? BackColor);
                if (_hover)
                    using (var b = new SolidBrush(_dark ? Color.FromArgb(46, 46, 50) : Color.FromArgb(236, 239, 243)))
                        g.FillRectangle(b, 0, 0, Width, Height);

                Color danger = Color.FromArgb(222, 74, 74);
                Color glyphC = Danger ? danger : _accent;
                using (var gf = FontHelper.Ui(12f))
                    TextRenderer.DrawText(g, Glyph, gf, new Rectangle(12, 0, 28, Height), glyphC,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                Color labelC = Danger ? danger : (_dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(35, 35, 38));
                using (var lf = FontHelper.Ui(10f))
                    TextRenderer.DrawText(g, Label, lf, new Rectangle(50, 0, Width - 120, Height), labelC,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (!string.IsNullOrEmpty(Trailing))
                    using (var tf = FontHelper.Ui(9.5f))
                        TextRenderer.DrawText(g, Trailing, tf, new Rectangle(Width - 72, 0, 60, Height),
                            _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(135, 135, 140),
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>One owner-painted media thumbnail (image, or a file icon + name).</summary>
        private sealed class MediaThumb : Control
        {
            private readonly int _index;
            private readonly bool _dark;
            private Image _img;
            private string _file;

            public event Action<int> Clicked;

            /// <summary>Right-click / touch-and-hold / menu key (screen point).</summary>
            public event EventHandler<Point> ContextMenuRequested;
            private const int WM_CONTEXTMENU = 0x007B;

            public MediaThumb(int index, bool dark)
            {
                _index = index; _dark = dark;
                Size = new Size(88, 88);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public void SetImage(Image img) { _img = img; if (!IsDisposed) Invalidate(); }
            public void SetFile(string name) { _file = name; if (!IsDisposed) Invalidate(); }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_CONTEXTMENU)
                {
                    int lp = m.LParam.ToInt32();
                    Point pt = lp == -1
                        ? PointToScreen(new Point(Width / 2, Height / 2))
                        : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                    ContextMenuRequested?.Invoke(this, pt);
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Clicked?.Invoke(_index); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(_dark ? Color.FromArgb(50, 50, 53) : Color.FromArgb(228, 228, 232));
                if (_img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    double scale = Math.Max((double)Width / _img.Width, (double)Height / _img.Height);
                    int w = (int)(_img.Width * scale), h = (int)(_img.Height * scale);
                    g.DrawImage(_img, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));   // cover-crop
                }
                else if (_file != null)
                {
                    DrawHelper.DrawFileIcon(g, new Rectangle(20, 12, 48, 48), _file);
                    TextRenderer.DrawText(g, _file, FontHelper.Ui(7.5f), new Rectangle(2, 64, 84, 22),
                        _dark ? Color.FromArgb(210, 210, 215) : Color.FromArgb(60, 60, 65),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _img != null) { try { _img.Dispose(); } catch { } _img = null; }
                base.Dispose(disposing);
            }
        }

        // ── Category gallery (opened from a counted row) ─────────────────────
        private sealed class GalleryEntry
        {
            public Control Ctl;
            public MediaItem Item;     // null for link rows
            public int MessageId;
            public string Type;        // photo/video/gif/document/audio/voice/link
            public string Url;         // link rows only
        }

        private sealed class MediaCategoryForm : Form
        {
            private readonly ProfileForm _owner;
            private readonly TelegramService _service;
            private readonly InputPeer _peer;
            private readonly MessagesFilter _filter;
            private readonly bool _listMode, _dark;
            private readonly Color _accent;

            private readonly FlowLayoutPanel _grid;
            private readonly Controls.NoNativeScrollPanel _scroll;
            private readonly List<MediaItem> _items = new List<MediaItem>();
            private int _offsetId;
            private bool _loading, _end;

            public MediaCategoryForm(ProfileForm owner, TelegramService service, InputPeer peer, MessagesFilter filter,
                                     string title, bool listMode, bool dark, Color accent)
            {
                _owner = owner; _service = service; _peer = peer; _filter = filter; _listMode = listMode; _dark = dark; _accent = accent;

                Text = title;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(listMode ? 420 : 380, 560);
                BackColor = dark ? Color.FromArgb(34, 34, 37) : Color.FromArgb(248, 248, 250);
                KeyPreview = true;
                KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

                _scroll = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
                _grid = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    WrapContents = !listMode,
                    FlowDirection = listMode ? FlowDirection.TopDown : FlowDirection.LeftToRight,
                    BackColor = BackColor,
                    Padding = new Padding(8)
                };
                _scroll.Controls.Add(_grid);
                Controls.Add(_scroll);
                Controls.Add(new Controls.ThemedScrollBar(_scroll, dark, accent) { Dock = DockStyle.Right });
                _scroll.Scroll += (s, e) => MaybeMore();
                TelegArm.UI.Controls.TouchScroller.Enable(_scroll, horizontal: false);
                // Tiles/rows live in the inner _grid (not the surface), so register each for WM_TOUCH
                // ourselves — long-press over a thumb then synthesizes WM_CONTEXTMENU (pan still pans _scroll).
                _grid.ControlAdded += (s, e) => TelegArm.UI.Controls.TouchScroller.RegisterControl(e.Control);

                Load += (s, e) => LoadPage();
            }

            private void MaybeMore()
            {
                if (_loading || _end) return;
                if (-_scroll.AutoScrollPosition.Y + _scroll.ClientSize.Height >= _scroll.DisplayRectangle.Height - 60)
                    LoadPage();
            }

            private async void LoadPage()
            {
                if (_loading || _end) return;
                _loading = true;
                try
                {
                    var res = await _service.SearchPeerMediaAsync(_peer, _filter, _offsetId, 30);
                    if (IsDisposed) return;
                    var msgs = res?.Messages?.OfType<TL.Message>().ToList() ?? new List<TL.Message>();
                    if (msgs.Count == 0) { _end = true; return; }
                    foreach (var m in msgs)
                    {
                        _offsetId = m.ID;
                        if (_listMode) AddListRow(m);
                        else AddTile(m);
                    }
                }
                catch { _end = true; }
                finally { _loading = false; }
            }

            private void AddTile(TL.Message m)
            {
                var mi = MediaClassifier.FromMessage(m);
                if (mi == null) return;
                int index = _items.Count;
                _items.Add(mi);
                var tile = new MediaThumb(index, _dark) { Margin = new Padding(2) };
                var entry = new GalleryEntry { Ctl = tile, Item = mi, MessageId = m.ID, Type = mi.Type };
                tile.Clicked += i => OpenEntry(entry);
                tile.ContextMenuRequested += (s, pt) => ShowItemMenu(entry, pt);
                _grid.Controls.Add(tile);
                if (_filter is InputMessagesFilterDocument)   // plain files → icon + name (no thumb)
                {
                    tile.SetFile(mi.FileName ?? "File");
                    return;
                }
                LoadThumb(tile, m);
            }

            private async void LoadThumb(MediaThumb tile, TL.Message m)
            {
                try
                {
                    byte[] bytes = null;
                    if (m.media is MessageMediaPhoto mp && mp.photo is Photo ph)
                        bytes = await _service.DownloadPhotoThumbAsync(ph);
                    else if ((m.media as MessageMediaDocument)?.document is Document doc)
                        bytes = await _service.DownloadThumbAsync(doc);
                    if (bytes != null && !tile.IsDisposed) tile.SetImage(ToBitmapSafe(bytes));
                }
                catch (Exception ex) { CrashLog.RecordThrottled("async-void:ProfileForm.LoadThumb", ex); }
            }

            private void AddListRow(TL.Message m)
            {
                var row = new ProfileRow(_dark, _accent) { Glyph = "•", Label = ListRowText(m), Width = 392 };
                var mi = MediaClassifier.FromMessage(m);
                var entry = new GalleryEntry { Ctl = row, Item = mi, MessageId = m.ID };
                if (mi != null) { _items.Add(mi); entry.Type = mi.Type; }
                else { entry.Type = "link"; entry.Url = ExtractUrl(m); }
                row.Clicked += () => OpenEntry(entry);
                row.ContextMenuRequested += (s, pt) => ShowItemMenu(entry, pt);
                _grid.Controls.Add(row);
            }

            private static string ExtractUrl(TL.Message m)
            {
                if (m.entities != null)
                    foreach (var e in m.entities)
                    {
                        if (e is MessageEntityTextUrl tu) return tu.url;
                        if (e is MessageEntityUrl && !string.IsNullOrEmpty(m.message)
                            && e.offset >= 0 && e.offset + e.length <= m.message.Length)
                            return m.message.Substring(e.offset, e.length);
                    }
                if ((m.media as MessageMediaWebPage)?.webpage is WebPage wp) return wp.url;
                return m.message;
            }

            // ── Open / context menu / actions ────────────────────────────────
            private void OpenEntry(GalleryEntry e)
            {
                if (e.Type == "link")
                {
                    if (!string.IsNullOrEmpty(e.Url)) try { System.Diagnostics.Process.Start(e.Url); } catch { }
                    return;
                }
                if (e.Item == null) return;
                if (e.Type == "audio" || e.Type == "voice") { PlayAudio(e); return; }
                OpenViewer(_items.IndexOf(e.Item));
            }

            private async void PlayAudio(GalleryEntry e)
            {
                if (!await MediaSaver.EnsureLocalAsync(e.Item, _service)) return;
                if (IsDisposed) return;
                AudioPlayer.Toggle(e.Item.Id, e.Item.LocalPath, e.Item.FileName ?? "Audio");
            }

            private void ShowItemMenu(GalleryEntry e, Point screenPt)
            {
                var menu = new ThemedContextMenuStrip();
                if (e.Type == "link")
                {
                    MenuItem(menu, "Open link", () => OpenEntry(e));
                    MenuItem(menu, "Show in chat", () => { _owner.RaiseShowInChat(e.MessageId); Close(); });
                    MenuItem(menu, "Copy link", () => { if (!string.IsNullOrEmpty(e.Url)) try { Clipboard.SetText(e.Url); } catch { } });
                    MenuItem(menu, "Forward", () => _owner.RaiseForward(e.MessageId));
                    MenuItem(menu, "Delete", () => DeleteEntry(e));
                }
                else
                {
                    MenuItem(menu, "Open", () => OpenEntry(e));
                    MenuItem(menu, "Show in chat", () => { _owner.RaiseShowInChat(e.MessageId); Close(); });
                    MenuItem(menu, "Save", () => SaveEntry(e, false));
                    MenuItem(menu, "Save As…", () => SaveEntry(e, true));
                    MenuItem(menu, "Forward", () => _owner.RaiseForward(e.MessageId));
                    MenuItem(menu, "Delete", () => DeleteEntry(e));
                }
                menu.Closed += (s, ev) => BeginInvoke((Action)menu.Dispose);
                menu.Show(screenPt);
            }

            private void MenuItem(ContextMenuStrip menu, string text, Action action)
            {
                var it = new ToolStripMenuItem(text) { ForeColor = _dark ? Color.White : Color.FromArgb(30, 30, 30) };
                it.Click += (s, e) => BeginInvoke(action);
                menu.Items.Add(it);
            }

            private async void SaveEntry(GalleryEntry e, bool saveAs)
            {
                if (e.Item == null) return;
                if (!await MediaSaver.EnsureLocalAsync(e.Item, _service))
                { ThemedDialog.Show(this, "Save", "Couldn't download the media.", "OK"); return; }
                if (IsDisposed) return;
                var item = e.Item;
                try
                {
                    if (saveAs)
                    {
                        using (var dlg = new SaveFileDialog
                        {
                            FileName = MediaSaver.SafeName(item.FileName ?? ("media_" + item.Id)),
                            Filter = MediaSaver.FilterFor(item.Type),
                            InitialDirectory = AppSettings.Instance.DefaultSaveFolder
                        })
                        {
                            if (dlg.ShowDialog(this) != DialogResult.OK) return;
                            if (MediaSaver.Write(item, dlg.FileName)) ThemedDialog.Show(this, "Saved", "Saved to:\n" + dlg.FileName, "OK");
                        }
                    }
                    else
                    {
                        string folder = MediaCache.EnsureFolder(AppSettings.Instance.DefaultSaveFolder);
                        string target = System.IO.Path.Combine(folder, MediaSaver.SafeName(item.FileName ?? ("media_" + item.Id)));
                        if (MediaSaver.Write(item, target)) ThemedDialog.Show(this, "Saved", "Saved to:\n" + target, "OK");
                    }
                }
                catch (Exception ex) { ThemedDialog.Show(this, "Save failed", ex.Message, "OK"); }
            }

            private async void DeleteEntry(GalleryEntry e)
            {
                bool revoke = true;
                if (_peer is InputPeerChannel)   // channel delete is admin-for-everyone → simple confirm
                {
                    if (ThemedDialog.Show(this, "Delete", "Delete this item?", "Delete", "Cancel") != 0) return;
                }
                else
                {
                    int r = ThemedDialog.Show(this, "Delete", "Delete this item?", "Delete for everyone", "Delete for me", "Cancel");
                    if (r < 0 || r == 2) return;
                    revoke = r == 0;
                }
                try { await _service.DeleteMessagesAsync(_peer, new[] { e.MessageId }, revoke); }
                catch (Exception ex) { ThemedDialog.Show(this, "Delete failed", ex.Message, "OK"); return; }
                if (IsDisposed) return;
                if (e.Ctl != null && !e.Ctl.IsDisposed)   // remove the owner-painted control in place
                {
                    _grid.Controls.Remove(e.Ctl);
                    e.Ctl.Dispose();
                }
            }

            private static string ListRowText(TL.Message m)
            {
                var doc = (m.media as MessageMediaDocument)?.document as Document;
                if (doc != null)
                {
                    var audio = doc.attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
                    if (audio != null && !string.IsNullOrEmpty(audio.title))
                        return audio.title + (string.IsNullOrEmpty(audio.performer) ? "" : " — " + audio.performer);
                    var fn = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
                    if (fn != null) return fn.file_name;
                    if (audio != null) return (audio.flags & DocumentAttributeAudio.Flags.voice) != 0 ? "Voice message" : "Audio";
                }
                if (!string.IsNullOrEmpty(m.message)) return m.message;
                return "(item)";
            }

            private void OpenViewer(int index)
            {
                if (index < 0 || index >= _items.Count) return;
                try { using (var v = new MediaViewerForm(_items, index, _service)) v.ShowDialog(this); }
                catch { }
            }
        }
    }
}
