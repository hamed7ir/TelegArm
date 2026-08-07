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
        // PROFILE-EDIT-SELF: username editor + availability gate.
        private MaterialTextBox2 _username;
        private Label _usernameStatus;
        private System.Windows.Forms.Timer _usernameCheckTimer;
        private bool _usernameOk = true;   // empty/current username = ok; a CHANGED one must pass the availability check

        // View-mode controls.
        private FlowLayoutPanel _flow;
        private Panel _viewAvatarPanel;   // CHANNEL-PHOTO-REFRESH: the big avatar panel (repaint after a photo change)
        private MaterialLabel _nameLbl, _statusLbl;
        private TextBox _idBox;
        private Controls.RichInfoLabel _details;
        private ActionTile _muteTile;
        private Panel _actionsPanel;   // PROFILE-CHANNEL: the personal-channel card slots right after this in the flow
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

        /// <summary>PROFILE-CHANNEL: set when the user taps their attached personal-channel card — open that channel.</summary>
        public Channel PendingOpenChannel { get; private set; }

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
            // Embedded there is nothing to close — the pane stays and the chat scrolls behind it.
            if (_embedded) return;
            try { BeginInvoke((Action)Close); } catch { Close(); }
        }

        /// <summary>BATCH-TA-18 — a proxy link tapped in the shared-links gallery.
        /// The gallery is the ONE link seam that shells out on its own (OpenEntry -> Process.Start) instead
        /// of going through MainForm's router, so without this the proxy sheet would open from a message
        /// body but not from the same link listed under shared media. Rather than give the gallery its own
        /// copy of the sheet — which would also mean a second live-apply path, in a modal that does not own
        /// the warm pool — it reuses the EXISTING PendingLink hand-off: the profile closes and MainForm
        /// routes it into the same ResolveLinkAsync -> OpenExternalUrl interception everything else uses.
        /// ⚠ ONLY proxy links come through here. Every other link still opens exactly as before.</summary>
        private void RaiseProxyLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            PendingLink = url;
            FinishOrRoute();
        }

        public ProfileForm(TelegramService service) : this(service, null, null, true) { }
        public ProfileForm(TelegramService service, ChatEntry entry, Image avatar) : this(service, entry, avatar, false) { }

        /// <summary>BATCH-TA-24 — the SAME profile, hosted inside the right-side dock instead of shown as a
        /// modal. The dock's Info pane IS this form; there is no second, thinner "info panel" to keep in
        /// sync, and nothing has to be re-implemented to make the pane look complete.
        ///
        /// ⚠ TWO THINGS MAKE IT EMBEDDABLE, AND BOTH WERE MEASURED RATHER THAN GUESSED:
        ///  1. WIDTH. Every row was built at a hardcoded ContentW = 392 with a hardcoded _flow.Width = 424.
        ///     Those are now DERIVED from <paramref name="contentWidth"/>, so the identical layout code
        ///     serves a 440-wide dialog and a ~300-wide dock.
        ///  2. CHROME. MaterialForm reserves a caption strip — measured: STATUS_BAR_HEIGHT_DEFAULT = 24,
        ///     and the form's Dock=Fill child sits at Top = 24 because Padding.Top holds that band. Clearing
        ///     Padding when embedded lets the opaque content cover it.
        /// The caller does: `new ProfileForm(svc, entry, avatar, width) { TopLevel = false, Dock = Fill }`,
        /// adds it, calls Show(), and handles <see cref="EmbeddedRoute"/> instead of a DialogResult.</summary>
        public ProfileForm(TelegramService service, ChatEntry entry, Image avatar, int contentWidth)
            : this(service, entry, avatar, false, contentWidth) { }

        private ProfileForm(TelegramService service, ChatEntry entry, Image avatar, bool editable,
                            int contentWidth = 0)
        {
            _service = service;
            _entry = entry;
            _avatar = avatar;
            _editable = editable;
            _ownsAvatar = editable;
            _muted = entry?.Muted ?? false;
            _embedded = contentWidth > 0;
            if (_embedded) ContentW = Math.Max(180, contentWidth - 32);   // 16px margin each side, as before

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
            ClientSize = new Size(420, 588);

            _pic = new Panel { Size = new Size(96, 96), Location = new Point((420 - 96) / 2, 20), BackColor = Color.Transparent, Cursor = Cursors.Hand };
            _pic.Paint += (s, e) => PaintAvatar(e.Graphics, new Rectangle(0, 0, 96, 96), 34f);
            _pic.Click += (s, e) => ShowEditAvatarMenu();   // PROFILE-EDIT-SELF: tap avatar → View / Set new / Remove
            Controls.Add(_pic);

            _titleLbl = new MaterialLabel
            {
                Text = FullName(SelfUser),
                Location = new Point(20, 120),
                AutoSize = false,
                Size = new Size(380, 24),
                FontType = MaterialSkinManager.fontType.H6,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(_titleLbl);
            Controls.Add(new Label { Text = "Tap your photo to change it", Location = new Point(20, 146), AutoSize = false, Size = new Size(380, 16), Font = new Font("Segoe UI", 8f), ForeColor = _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(140, 140, 145), TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent });

            _contactBox = ReadOnlyBox(24, 166, 372, 22, false);
            Controls.Add(_contactBox);

            Controls.Add(SmallLabel("First name", 24, 194));
            _first = new MaterialTextBox2 { Text = SelfUser?.first_name ?? "", Location = new Point(24, 216), Width = 372 };
            Controls.Add(_first);

            Controls.Add(SmallLabel("Last name", 24, 266));
            _last = new MaterialTextBox2 { Text = SelfUser?.last_name ?? "", Location = new Point(24, 288), Width = 372 };
            Controls.Add(_last);

            Controls.Add(SmallLabel("Bio", 24, 338));
            _aboutBox = new MaterialTextBox2 { Hint = "A few words about you", Location = new Point(24, 360), Width = 372 };
            Controls.Add(_aboutBox);

            _usernameCheckTimer = new System.Windows.Forms.Timer { Interval = 500 };   // debounce the availability check
            _usernameCheckTimer.Tick += (s, e) => CheckUsernameNow();
            Controls.Add(SmallLabel("Username", 24, 410));
            _username = new MaterialTextBox2 { Hint = "username", Text = SelfUser?.MainUsername ?? "", Location = new Point(24, 432), Width = 372 };
            _username.TextChanged += OnUsernameChanged;
            Controls.Add(_username);
            _usernameStatus = new Label { Location = new Point(26, 484), AutoSize = false, Size = new Size(368, 18), Font = new Font("Segoe UI", 8.5f), BackColor = Color.Transparent, Text = "" };
            Controls.Add(_usernameStatus);

            var save = new MaterialButton { Text = "Save", Location = new Point(214, 530), Width = 90, Type = MaterialButton.MaterialButtonType.Contained };
            save.Click += OnSave;
            Controls.Add(save);
            var cancel = new MaterialButton { Text = "Cancel", Location = new Point(310, 530), Width = 90, Type = MaterialButton.MaterialButtonType.Outlined };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
        }

        // ── View mode (others) — Telegram-style scrollable layout ────────────
        /// <summary>Inner content width (within the 16px side margins). WAS a const 392; it is now
        /// per-instance so the SAME layout code can serve the 440-wide dialog and the narrower dock pane
        /// (BATCH-TA-24). Every one of its ~19 uses is a read at build time, so setting it in the ctor
        /// before Build*Mode() runs is sufficient.</summary>
        private readonly int ContentW = 392;
        private readonly bool _embedded;

        /// <summary>Embedded only: raised INSTEAD of closing, when something the host must handle was
        /// tapped (a link, a mention, "show in chat", a leave). The modal path sets DialogResult and closes;
        /// a docked pane has no DialogResult and must not vanish, so the host reads the same Pending*
        /// properties and routes them.</summary>
        public event Action<ProfileForm> EmbeddedRoute;

        /// <summary>Closes (modal) or notifies the host (embedded). EVERY view-mode exit goes through here,
        /// so the two modes cannot drift — link, mention, hashtag, personal-channel card and leave.
        /// ⚠ The one remaining `DialogResult = OK; Close();` in this file is the EDIT-mode save, and it is
        ///   correct there: edit mode is only ever reached through the `ProfileForm(service)` ctor, which
        ///   cannot be embedded (the embedded ctor passes editable: false).</summary>
        private void FinishOrRoute()
        {
            if (_embedded)
            {
                var h = EmbeddedRoute;
                if (h != null) h(this);
                // ⚠ CLEAR THE PENDING FIELDS. The modal path never needs this because the form dies right
                //   after the host reads them; a docked pane LIVES ON, and RouteProfilePending checks
                //   PendingLink FIRST — so a stale link would swallow the next mention or hashtag tap.
                PendingLink = null; PendingMentionUser = null; PendingMentionId = 0;
                PendingHashtag = null; PendingOpenChannel = null;
                return;
            }
            DialogResult = DialogResult.OK;
            try { BeginInvoke((Action)Close); } catch { Close(); }
        }

        private void BuildViewMode()
        {
            if (_embedded)
            {
                // ⚠ MEASURED: MaterialForm reserves a caption strip via Padding — its Dock=Fill child sits
                //   at Top = 24 (STATUS_BAR_HEIGHT_DEFAULT). Clearing Padding lets the opaque content cover
                //   the band, which is what makes this look like a pane instead of a windowless dialog.
                Padding = new Padding(0);
                Sizable = false;
            }
            else ClientSize = new Size(440, 620);

            var outer = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            var host = new Controls.NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = ContentW + 32,   // was a second hardcoded 424; 392 + 32 == 424, so the dialog is unchanged
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
            _viewAvatarPanel = avi;
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
            _actionsPanel = actions;   // PROFILE-CHANNEL: personal-channel card inserts directly after this
            bool isUser = OtherUser != null;
            var tiles = new List<ActionTile>();
            // ⚠ EMBEDDED, "Message" IS OMITTED, NOT SHOWN-AND-IGNORED. The dock's profile is built for the
            //   chat that is ALREADY OPEN behind it, so the tile would ask to open the conversation the user
            //   is looking at. Its handler is also the modal exit (DialogResult + Close), which on a
            //   TopLevel=false form disposes the pane and leaves the dock blank. Omitting an unavailable
            //   action rather than grey-out is this app's convention (TA-20/S0, and the dock's own Emoji tab).
            if (isUser && !_embedded)
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
            _details.LinkClicked += url => { PendingLink = url; FinishOrRoute(); };
            _details.MentionClicked += (un, uid) => { PendingMentionUser = un; PendingMentionId = uid; FinishOrRoute(); };
            _details.HashtagClicked += tag => { PendingHashtag = tag; FinishOrRoute(); };
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

        // ── PROFILE-CHANNEL: the user's attached personal channel, as a tappable card right below the action row ──
        private async void LoadPersonalChannelAsync(User u)
        {
            try
            {
                var res = await _service.GetPersonalChannelAsync(u);
                if (res == null || IsDisposed || _flow == null || _actionsPanel == null) return;
                var val = res.Value;
                var ch = val.channel;
                string sub = "Channel" + (val.subs > 0 ? " • " + val.subs.ToString("N0") + " subscriber" + (val.subs == 1 ? "" : "s") : "");
                var card = new ProfileChannelCard(ch.title, sub, ChannelMsgPreview(val.latest), _dark, _accentColor)
                { Width = ContentW, Margin = new Padding(16, 8, 16, 0) };
                card.Clicked += () => { PendingOpenChannel = ch; FinishOrRoute(); };
                _flow.Controls.Add(card);
                int ai = _flow.Controls.GetChildIndex(_actionsPanel);   // slot the card directly after the action row
                if (ai >= 0) _flow.Controls.SetChildIndex(card, ai + 1);
                if (Avatars != null)
                {
                    var cached = Avatars.GetCached(ch.id);
                    if (cached != null) card.SetAvatar(cached);
                    else { try { var img = await Avatars.GetAsync(ch.id, ch); if (img != null && !card.IsDisposed) card.SetAvatar(img); } catch { } }
                }
            }
            catch { /* no card on any failure — profile renders exactly as before */ }
        }

        private static string ChannelMsgPreview(TL.Message m)
        {
            if (m == null) return "";
            if (!string.IsNullOrEmpty(m.message)) return m.message.Replace("\r", " ").Replace("\n", " ");
            if (m.media is MessageMediaPhoto) return "Photo";
            if (m.media is MessageMediaDocument) return "File";
            return m.media != null ? "Media" : "";
        }

        // ── PROFILE-STORIES: a peer's POSTED stories as a tappable thumbnail grid (like TG Desktop) ──────────
        private const int StoryPreviewMax = 4;   // profile shows one row; more → a "Show all" gallery (StoriesGridForm)
        private List<StoryItem> _postedStories;   // the full posted/pinned list == what the viewer navigates (indices align)

        /// <summary>Fetches THIS peer's active posted stories (SAME filter + order as the story viewer, so a tapped
        /// tile's index matches the viewer's story index) and, if any exist, inserts a "POSTED STORIES" thumbnail
        /// grid right after the INFO block. No active stories → nothing added (no empty section). Fire-and-forget.</summary>
        private async void LoadPostedStoriesAsync()
        {
            if (_entry?.Peer == null) return;
            List<StoryItem> items = null;
            try
            {
                // "POSTED STORIES" = the peer's PINNED profile stories (stories.getPinnedStories — these persist past the
                // 24h active window and are what official TG shows on a profile), MERGED with any currently-active ring
                // story (getPeerStories) not already pinned. De-duped by id, newest first. NO expire filter — pinned
                // stories are shown even after they'd have expired. (getPeerStories alone = the transient ring, which is
                // why a user with posted-but-not-currently-active stories showed nothing before.)
                var byId = new Dictionary<int, StoryItem>();
                var pin = await _service.GetPinnedStoriesAsync(_entry.Peer);
                if (pin?.stories != null)
                    foreach (var s in pin.stories.OfType<StoryItem>()) byId[s.id] = s;
                var act = await _service.GetPeerStoriesAsync(_entry.Peer);
                var actArr = act?.stories?.stories;
                if (actArr != null)
                    foreach (var s in actArr.OfType<StoryItem>()) if (!byId.ContainsKey(s.id)) byId[s.id] = s;
                items = byId.Values.OrderByDescending(s => s.id).ToList();
            }
            catch { return; }
            if (IsDisposed || _flow == null || _idBox == null || items == null || items.Count == 0) return;
            _postedStories = items;

            var label = SectionLabel(items.Count > 1 ? items.Count + " POSTED STORIES" : "POSTED STORIES");
            label.Margin = new Padding(16, 14, 16, 0);
            var grid = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(ContentW, 0),   // pin the width → the row WRAPS instead of growing horizontally
                MaximumSize = new Size(ContentW, 0),
                Margin = new Padding(16, 2, 16, 0),
                BackColor = BackColor
            };
            int shown = Math.Min(items.Count, StoryPreviewMax);   // one preview row; the rest live behind "Show all"
            for (int i = 0; i < shown; i++)
            {
                int idx = i;   // capture → the viewer deep-links to this story
                var thumb = new Controls.StoryThumb(_service, items[i], _dark) { Margin = new Padding(0, 0, 6, 6) };
                thumb.Clicked += () => OpenProfileStoryViewer(idx);
                grid.Controls.Add(thumb);
            }

            ProfileRow showAll = null;
            if (items.Count > StoryPreviewMax)
            {
                showAll = new ProfileRow(_dark, _accentColor) { Glyph = "▦", Label = "Show all " + items.Count + " stories", Width = ContentW };
                showAll.Clicked += OpenStoriesGallery;
                AddFlow(showAll, 2);
            }

            // Slot the section (label, grid, optional "Show all") directly after the INFO id line, regardless of the
            // order the async sections finished in — same technique as the personal-channel card.
            _flow.Controls.Add(label);
            _flow.Controls.Add(grid);
            int anchor = _flow.Controls.GetChildIndex(_idBox);
            if (anchor >= 0)
            {
                _flow.Controls.SetChildIndex(label, anchor + 1);
                _flow.Controls.SetChildIndex(grid, anchor + 2);
                if (showAll != null) _flow.Controls.SetChildIndex(showAll, anchor + 3);
            }
        }

        /// <summary>Opens the EXISTING full-screen viewer for THIS peer (single-peer list), deep-linked to the tapped
        /// story. The viewer does photo/video, progress, tap-nav, mark-seen + view-register as built.</summary>
        private void OpenProfileStoryViewer(int storyIdx)
        {
            if (_entry?.Peer == null || _postedStories == null) return;
            var refs = new List<StoryPeerRef> { new StoryPeerRef { PeerId = _entry.PeerId, Name = _entry.Title ?? "", Input = _entry.Peer } };
            // Hand the viewer the SAME posted list (preloaded) so it shows/navigates exactly the grid's stories at the tapped index.
            using (var viewer = new StoryViewerForm(_service, refs, 0, cid => Avatars?.GetCached(cid), _accentColor, storyIdx, _postedStories))
                viewer.ShowDialog(this);
        }

        /// <summary>"Show all N stories" → the full scrollable gallery (StoriesGridForm) of every posted story;
        /// tapping a tile there opens the same viewer (with the same preloaded list).</summary>
        private void OpenStoriesGallery()
        {
            if (_entry?.Peer == null || _postedStories == null) return;
            var peerRef = new StoryPeerRef { PeerId = _entry.PeerId, Name = _entry.Title ?? "", Input = _entry.Peer };
            using (var f = new StoriesGridForm(_service, peerRef, _postedStories, _dark, _accentColor, cid => Avatars?.GetCached(cid)))
                f.ShowDialog(this);
        }

        /// <summary>A tappable channel row (avatar + title + "Channel • N subscribers" + latest-message preview),
        /// RTL-aware. Owner-drawn like a chat-list row; avatar fills in async via SetAvatar.</summary>
        private sealed class ProfileChannelCard : Control
        {
            private readonly bool _dark;
            private readonly Color _accent;
            private readonly string _title, _sub, _preview;
            private Image _avatar;
            public event Action Clicked;
            public ProfileChannelCard(string title, string sub, string preview, bool dark, Color accent)
            {
                _title = title ?? ""; _sub = sub ?? ""; _preview = preview ?? ""; _dark = dark; _accent = accent;
                Height = 76; Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Click += (s, e) => { var h = Clicked; if (h != null) h(); };
            }
            public void SetAvatar(Image a) { _avatar = a; if (!IsDisposed) Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : (_dark ? Color.FromArgb(30, 30, 30) : Color.White));
                var card = new Rectangle(0, 1, Width - 1, Height - 4);
                using (var cb = new SolidBrush(_dark ? Color.FromArgb(46, 46, 50) : Color.FromArgb(238, 238, 241)))
                using (var p = DrawHelper.RoundedRect(card, 10))
                    g.FillPath(cb, p);
                const int d = 48; int ax = 12, ay = (Height - d) / 2;
                var ar = new Rectangle(ax, ay, d, d);
                if (_avatar != null)
                {
                    using (var clip = new GraphicsPath()) { clip.AddEllipse(ar); var old = g.Clip; g.SetClip(clip); g.DrawImage(_avatar, ar); g.Clip = old; }
                }
                else
                {
                    using (var b = new SolidBrush(_accent)) g.FillEllipse(b, ar);
                    string letter = string.IsNullOrEmpty(_title) ? "#" : _title.Substring(0, 1).ToUpper();
                    using (var af = FontHelper.For(_title, 15f, FontStyle.Bold))
                        TextRenderer.DrawText(g, letter, af, ar, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                int tx = ax + d + 12, tw = Width - tx - 14;
                Color titleC = _dark ? Color.White : Color.FromArgb(20, 20, 24);
                Color subC = _dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
                DrawLine(g, _title, tx, 10, tw, titleC, 10.5f, FontStyle.Bold);
                DrawLine(g, _sub, tx, 31, tw, subC, 8.5f, FontStyle.Regular);
                DrawLine(g, _preview, tx, 50, tw, subC, 9f, FontStyle.Regular);
            }
            private static void DrawLine(Graphics g, string text, int x, int y, int w, Color c, float size, FontStyle st)
            {
                if (string.IsNullOrEmpty(text) || w < 20) return;
                bool fa = FontHelper.IsPersian(text);
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                            | (fa ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
                using (var f = FontHelper.For(text, size, st))
                    TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, 18), c, flags);
            }
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
            if (OtherUser != null)               // USER → contact actions (Share hidden for release — RELEASE-FIXES-V11)
            {
                MenuItem(menu, "Edit contact", EditContact);
                MenuItem(menu, "Delete contact", DeleteContact, danger: true);   // red destructive
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

        private void MenuItem(ContextMenuStrip menu, string text, Action action, bool danger = false)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = danger ? Color.FromArgb(222, 74, 74) : (_dark ? Color.White : Color.FromArgb(30, 30, 30)) };
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
            // RELEASE-FIXES-V11: reflect edits made inside Manage in this OPEN profile — title (updated on the shared
            // _entry by MainForm.PeerTitleChanged during the edit), avatar (CHANNEL-PHOTO-REFRESH), and description (M1).
            if (_nameLbl != null && !_nameLbl.IsDisposed && _entry != null) _nameLbl.Text = _entry.Title ?? "";
            RefreshChannelProfileAvatar();
            RefreshProfileDescriptionAsync();
        }

        /// <summary>CHANNEL-PHOTO-REFRESH: re-reads this peer's avatar after a Manage action (the channel photo may have
        /// changed, which invalidated + re-requested the cached avatar) and repaints the big profile avatar. Shares the
        /// AvatarStore image (view-mode profiles don't own _avatar), so nothing to dispose.</summary>
        private async void RefreshChannelProfileAvatar()
        {
            if (_editable || _entry?.PeerInfo == null || Avatars == null) return;
            try
            {
                var img = await Avatars.GetAsync(_entry.PeerId, _entry.PeerInfo);   // cache hit, or joins the in-flight re-fetch
                if (IsDisposed || _viewAvatarPanel == null || _viewAvatarPanel.IsDisposed) return;
                if (img != null && !ReferenceEquals(img, _avatar)) { _avatar = img; _viewAvatarPanel.Invalidate(); }
            }
            catch { }
        }

        /// <summary>Rebuilds the profile's identifier + description block (@username / phone / about) — shared by the
        /// initial load and the post-Manage refresh (M1: an edited channel/group description updates without a reload).</summary>
        private void ApplyDetailsText(string about)
        {
            if (_details == null) return;
            var u = OtherUser;
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
            if (!string.IsNullOrEmpty(about)) { if (sb.Length > 0) sb.AppendLine(); sb.AppendLine(about); }
            string detailsText = sb.ToString().TrimEnd();
            if (detailsText.Length == 0) _details.Visible = false;
            else { _details.Visible = true; _details.SetText(detailsText, Helpers.TextEntities.Detect(detailsText), null); }
        }

        /// <summary>M1: re-fetch the peer's about and refresh the shown description after a Manage edit (bounded; no-op on self/failure).</summary>
        private async void RefreshProfileDescriptionAsync()
        {
            if (_editable || _entry?.Peer == null || _details == null) return;
            try
            {
                var (about, _, _, _) = await _service.GetPeerDetailsAsync(_entry.Peer, _entry?.PeerInfo);
                if (IsDisposed) return;
                ApplyDetailsText(about);
            }
            catch { }
        }

        private async void EditContact()
        {
            var u = OtherUser;
            if (u == null) { ThemedDialog.Show(this, "Edit contact", "Only a saved user can be renamed.", "OK"); return; }

            string newFirst, newLast;
            using (var f = new EditContactForm(u.first_name ?? "", u.last_name ?? "", _dark, _accentColor))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                newFirst = f.FirstNameValue; newLast = f.LastNameValue;
            }
            if (newFirst == (u.first_name ?? "") && newLast == (u.last_name ?? "")) return;   // nothing changed

            bool ok;
            try { ok = await _service.EditContactAsync(u, newFirst, newLast); }
            catch (Exception ex) { ThemedDialog.Show(this, "Edit contact", "Couldn't rename the contact: " + ex.Message, "OK"); return; }
            if (!ok) { ThemedDialog.Show(this, "Edit contact", "Couldn't rename — make sure your VPN is on and try again.", "OK"); return; }

            // Reflect the rename optimistically: the User (== _entry.PeerInfo, shared ref), this ChatEntry's title,
            // and the on-screen header label. The server's updateUserName reconciles the rest on next sync.
            u.first_name = newFirst; u.last_name = newLast;
            string display = (newFirst + " " + newLast).Trim();
            if (_entry != null) _entry.Title = display;
            if (_nameLbl != null && !_nameLbl.IsDisposed) _nameLbl.Text = display;   // view-mode name label (was _titleLbl = edit-mode, null here)
            MainForm.RaisePeerTitleChanged(u.id, display);   // RELEASE-FIXES-V11: refresh the chat-list row + header live
            System.Diagnostics.Debug.WriteLine("[CONTACT] renamed id=" + u.id);
        }

        private async void DeleteContact()
        {
            var u = OtherUser;
            if (u == null) { ThemedDialog.Show(this, "Delete contact", "Only a saved user can be deleted.", "OK"); return; }
            if (ThemedDialog.Show(this, "Delete contact", "Delete this contact? Your chat history stays — they're just removed from your contacts.", "Delete", "Cancel") != 0) return;   // 0 = destructive primary
            bool ok;
            try { ok = await _service.DeleteContactAsync(u); }
            catch (Exception ex) { ThemedDialog.Show(this, "Delete contact", "Couldn't delete: " + ex.Message, "OK"); return; }
            if (!ok) { ThemedDialog.Show(this, "Delete contact", "Couldn't delete — make sure your VPN is on and try again.", "OK"); return; }
            System.Diagnostics.Debug.WriteLine("[CONTACT] deleted id=" + u.id);
            ThemedDialog.Show(this, "Delete contact", "Contact deleted.", "OK");
        }

        private async void BlockUser()
        {
            if (OtherUser == null) { ThemedDialog.Show(this, "Block", "Only users can be blocked.", "OK"); return; }
            bool target = !_blocked;
            bool ok;
            try { ok = await _service.SetBlockedAsync(_entry.Peer, target); }
            catch (Exception ex) { ThemedDialog.Show(this, "Block", "Couldn't change block state: " + ex.Message, "OK"); return; }

            // ⚠ TA-39 — HONOUR THE RETURN VALUE. contacts.block/unblock return Bool, and this used to
            //   ignore it and announce success unconditionally. A request Telegram declined was
            //   indistinguishable from one it honoured.
            if (!ok)
            {
                ThemedDialog.Show(this, "Block", "Telegram did not apply that change. Nothing was altered.", "OK");
                return;
            }
            _blocked = target;
            RefreshActionRows();   // the row must now read Unblock, or the next tap toggles the wrong way
            ThemedDialog.Show(this, "Block", target ? "User blocked." : "User unblocked.", "OK");
        }

        // ── Media counts → counted rows ──────────────────────────────────────
        private struct MediaCat { public string Glyph, Label; public MessagesFilter Filter; public bool ListMode; }

        private static List<MediaCat> Categories() => new List<MediaCat>
        {
            // ⚠ BATCH-TA-39 — BMP GLYPHS ONLY. THE ASTRAL ONES RENDER AS TOFU BOXES ON RT 8.1.
            //   Windows 8.1's Segoe UI Symbol covers SOME emoji above U+FFFF and not others, which is why
            //   this looked fine on the dev box and showed empty boxes on the device for Photos (U+1F5BC),
            //   GIFs (U+1F39E) and Delete contact (U+1F5D1), while the music note and microphone happened
            //   to render. "It renders here" is not evidence for RT.
            //   Everything below is now from Geometric Shapes / Misc Symbols / Dingbats (U+25xx-U+27xx),
            //   which 8.1 covers completely. Same reasoning as the drawn header glyphs — a font glyph
            //   renders inconsistently on RT — applied without hand-drawing seven icons.
            new MediaCat { Glyph = "▣", Label = "Photos",         Filter = new InputMessagesFilterPhotos() },
            new MediaCat { Glyph = "▶", Label = "Videos",         Filter = new InputMessagesFilterVideo() },
            new MediaCat { Glyph = "▤", Label = "Files",          Filter = new InputMessagesFilterDocument() },
            new MediaCat { Glyph = "♫", Label = "Audio",          Filter = new InputMessagesFilterMusic(),  ListMode = true },
            new MediaCat { Glyph = "⛓", Label = "Shared links",   Filter = new InputMessagesFilterUrl(),    ListMode = true },
            new MediaCat { Glyph = "◉", Label = "Voice messages", Filter = new InputMessagesFilterVoice(),  ListMode = true },
            new MediaCat { Glyph = "▷", Label = "GIFs",           Filter = new InputMessagesFilterGif() },
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
            if (OtherUser != null)   // user contact → Edit/Delete + Block (Share hidden for release — RELEASE-FIXES-V11)
            {
                // BMP glyphs only — see Categories() for why. U+1F5D1 (wastebasket) was a tofu box on RT.
                AddActionRow("✎", "Edit contact", false, EditContact);
                AddActionRow("✖", "Delete contact", true, DeleteContact);
                _blockRow = AddActionRow("⊘", _blocked ? "Unblock user" : "Block user", true, BlockUser);
            }
            else                     // channel / group → Leave (destructive)
            {
                AddActionRow("⇤", LeaveLabel(), true, LeaveChat);
            }
        }

        /// <summary>TA-39 — the block row, kept so its label can be corrected in place.</summary>
        private ProfileRow _blockRow;

        /// <summary>Retitles the block row after the state changes, so it reads "Unblock user" the moment
        /// the block lands. Without it the label kept saying "Block user" and the next tap toggled the
        /// wrong way. ⚠ Updated IN PLACE rather than rebuilding the ACTIONS section — rebuilding would
        /// re-add its "ACTIONS" header and duplicate it.</summary>
        private void RefreshActionRows()
        {
            try
            {
                if (_blockRow == null || _blockRow.IsDisposed) return;
                _blockRow.Label = _blocked ? "Unblock user" : "Block user";
                _blockRow.Invalidate();
            }
            catch { /* cosmetic refresh; never break the dialog */ }
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
            FinishOrRoute();   // close the profile after leaving (embedded: tell the host, stay put)
        }

        /// <summary>Returns the row so a caller can retitle it later (TA-39: the block row).</summary>
        private ProfileRow AddActionRow(string glyph, string label, bool danger, Action action)
        {
            var row = new ProfileRow(_dark, _accentColor) { Glyph = glyph, Label = label, Danger = danger, Width = ContentW };
            row.Clicked += () => action();
            AddFlow(row, 0);
            return row;
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
            if (u != null) LoadPersonalChannelAsync(u);   // PROFILE-CHANNEL: attached personal-channel card (async, no-op if none)
            LoadPostedStoriesAsync();                      // PROFILE-STORIES: posted-stories thumbnail grid (async, hidden if none)
            // PEER-PRESENTATION wording: broadcasts have subscribers, groups have members.
            if (members > 0 && _statusLbl != null)
                _statusLbl.Text = members.ToString("N0")
                    + (_entry?.PeerInfo is Channel sc && (sc.flags & Channel.Flags.broadcast) != 0 ? " subscribers" : " members");

            // Identifier section per peer type (all fields already live on the resolved peer — no round-trip).
            ApplyDetailsText(about2);

            if (_idBox != null) _idBox.Text = "ID: " + RawPeerId();

            // PROFILE-MEMBERS: groups/megagroups only — broadcasts never list subscribers. The section
            // label + container append SYNCHRONOUSLY (fixed flow order above SHARED MEDIA); rows fill async.
            if (IsGroupProfile) BuildMembersSection(members, groupUsers);

            LoadMediaCounts();
            AddBottomActions();

            // ⚠ TA-39 — LOAD THE **REAL** BLOCKED STATE. `_blocked` was a plain field that started false
            //   and was only ever written by our own toggle, so opening an already-blocked user's profile
            //   showed "Block user"; tapping it asked Telegram to block someone already blocked, which is
            //   a no-op the UI then reported as "User blocked." That is the other half of "blocking does
            //   not work" — the API call was fine, the state was fiction.
            //   Source is UserFull.flags.blocked, the same field ComposerState.Resolve trusts, so the
            //   profile and the composer cannot disagree. Async and last: it is a network round-trip and
            //   nothing above it should wait on it.
            if (OtherUser != null)
            {
                try
                {
                    bool blocked = await _service.IsBlockedAsync(OtherUser);
                    if (IsDisposed) return;
                    if (blocked != _blocked) { _blocked = blocked; RefreshActionRows(); }
                }
                catch { /* leave the optimistic default; the toggle still reports the truth */ }
            }
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
                { Glyph = "◎", Label = "Show all " + totalMembers.ToString("N0") + " members", Width = ContentW - 32 };
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
            string first = _first.Text.Trim();
            if (first.Length == 0) { ThemedDialog.Show(this, "Profile", "A first name is required.", "OK"); return; }   // name required
            string last = _last.Text.Trim();
            string about = _aboutBox.Text.Trim();

            // USERNAME gate (PART 3): a CHANGED username must be confirmed available, else block the whole save.
            string newUn = (_username?.Text ?? "").Trim();
            string curUn = SelfUser?.MainUsername ?? "";
            bool unChanged = newUn.Length > 0 && newUn != curUn;   // empty = leave username unchanged (no removal)
            if (unChanged && !_usernameOk)
            {
                ThemedDialog.Show(this, "Username", "That username isn't available. Pick an available one, or restore your current username.", "OK");
                return;
            }

            try
            {
                if (unChanged)
                {
                    try { await _service.UpdateSelfUsernameAsync(newUn); }   // only after CheckUsername said available
                    catch (Exception ux)
                    {
                        _usernameOk = false; SetUsernameStatus("✗ Taken — pick another", Color.FromArgb(210, 90, 90));
                        ThemedDialog.Show(this, "Username", "Couldn't set that username (it may have just been taken): " + ux.Message, "OK");
                        return;   // race (USERNAME_OCCUPIED) → don't save the rest, don't claim success
                    }
                }
                await _service.UpdateProfileAsync(first, last, about);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Profile", "Couldn't save: " + ex.Message, "OK");
            }
        }

        // ── PROFILE-EDIT-SELF: username availability (debounced) ─────────────
        private void OnUsernameChanged(object sender, EventArgs e)
        {
            _usernameOk = false;
            if (_usernameStatus != null) _usernameStatus.Text = "";
            _usernameCheckTimer?.Stop();
            _usernameCheckTimer?.Start();   // debounce → CheckUsernameNow after 500ms idle
        }

        private async void CheckUsernameNow()
        {
            _usernameCheckTimer?.Stop();
            string u = (_username.Text ?? "").Trim();
            string cur = SelfUser?.MainUsername ?? "";
            if (u.Length == 0) { SetUsernameStatus("", null); _usernameOk = true; return; }                 // empty = no change
            if (u == cur)      { SetUsernameStatus("This is your current username.", null); _usernameOk = true; return; }
            if (!IsValidUsernameFormat(u)) { SetUsernameStatus("Invalid — 5–32 letters/digits/_ , starting with a letter.", Color.FromArgb(210, 90, 90)); _usernameOk = false; return; }
            SetUsernameStatus("Checking…", null);
            bool free;
            try { free = await _service.CheckSelfUsernameAsync(u); }
            catch { SetUsernameStatus("Couldn't check — is your VPN on?", null); _usernameOk = false; return; }
            if (IsDisposed || (_username.Text ?? "").Trim() != u) return;   // stale: the user kept typing
            _usernameOk = free;
            SetUsernameStatus(free ? "✓ Available" : "✗ Taken or unavailable", free ? Color.FromArgb(60, 170, 90) : Color.FromArgb(210, 90, 90));
        }

        private void SetUsernameStatus(string text, Color? color)
        {
            if (_usernameStatus == null) return;
            _usernameStatus.Text = text;
            _usernameStatus.ForeColor = color ?? (_dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(130, 130, 135));
        }

        private static bool IsValidUsernameFormat(string u)
        {
            if (u.Length < 5 || u.Length > 32) return false;
            if (!((u[0] >= 'a' && u[0] <= 'z') || (u[0] >= 'A' && u[0] <= 'Z'))) return false;   // must start with a letter
            foreach (char c in u)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        // ── PROFILE-EDIT-SELF: profile photo (view / set / remove) ───────────
        private void ShowEditAvatarMenu()
        {
            var menu = new ThemedContextMenuStrip();
            if (_avatar != null) MenuItem(menu, "View photo", ViewPhoto);
            MenuItem(menu, "Set new photo…", SetProfilePhotoFlow);
            if (_avatar != null) MenuItem(menu, "Remove current photo", RemoveProfilePhotoFlow);
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(_pic, new Point(0, _pic.Height));
        }

        private async void SetProfilePhotoFlow()
        {
            string path;
            using (var ofd = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png", Title = "Choose a profile photo" })
            { if (ofd.ShowDialog(this) != DialogResult.OK) return; path = ofd.FileName; }

            string keep = _titleLbl.Text;
            _titleLbl.Text = "Uploading photo…";
            bool ok;
            try { ok = await _service.SetProfilePhotoAsync(path); }
            catch (Exception ex) { _titleLbl.Text = keep; ThemedDialog.Show(this, "Profile photo", "Couldn't set the photo: " + ex.Message, "OK"); return; }
            _titleLbl.Text = keep;
            if (!ok) { ThemedDialog.Show(this, "Profile photo", "Couldn't set the photo — is your VPN on?", "OK"); return; }
            RefreshSelfAvatar();
        }

        private async void RemoveProfilePhotoFlow()
        {
            if (ThemedDialog.Show(this, "Remove photo", "Remove your current profile photo?", "Remove", "Cancel") != 0) return;
            try
            {
                var photos = await _service.GetSelfPhotosAsync(1);
                var p = photos?.photos?.OfType<Photo>().FirstOrDefault();
                if (p == null) { ThemedDialog.Show(this, "Remove photo", "There's no profile photo to remove.", "OK"); return; }
                if (!await _service.DeleteProfilePhotoAsync(p)) { ThemedDialog.Show(this, "Remove photo", "Couldn't remove — is your VPN on?", "OK"); return; }
                RefreshSelfAvatar();
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Remove photo", "Couldn't remove: " + ex.Message, "OK"); }
        }

        /// <summary>Re-downloads the (now current) self avatar into the edit form AND invalidates the app-wide
        /// AvatarStore for self, so rows/header repaint with the new (or removed) photo.</summary>
        private async void RefreshSelfAvatar()
        {
            try
            {
                var bytes = await _service.DownloadAvatarAsync(SelfUser);
                if (IsDisposed) return;
                var old = _avatar;
                _avatar = (bytes != null && bytes.Length > 0) ? ToBitmap(bytes) : null;
                if (_ownsAvatar && old != null && !ReferenceEquals(old, _avatar)) { try { old.Dispose(); } catch { } }
                _pic?.Invalidate();
                if (Avatars != null && SelfUser != null) { Avatars.Invalidate(SelfUser.id); Avatars.Request(SelfUser.id, SelfUser); }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _usernameCheckTimer?.Dispose();   // PROFILE-EDIT-SELF: the debounce timer
                if (_ownsAvatar && _avatar != null) { _avatar.Dispose(); _avatar = null; }
            }
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
                TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab / title bar
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
                    if (string.IsNullOrEmpty(e.Url)) return;
                    // BATCH-TA-18 — this Process.Start is the app's SECOND shell-out seam. Hand a proxy link
                    // back to the owner so it reaches the same sheet a tap in the message body does; anything
                    // else falls through to the browser unchanged.
                    if (TelegArm.Core.ProxyUrl.IsProxyLink(e.Url)) { _owner.RaiseProxyLink(e.Url); Close(); return; }
                    try { System.Diagnostics.Process.Start(e.Url); } catch { }
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
