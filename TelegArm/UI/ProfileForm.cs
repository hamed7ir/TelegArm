using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed profile dialog. View mode (others) mimics Telegram Desktop: big avatar, name + status,
    /// an action-tile row (Message / Mute / Call / More), contact info, a counted media row-list
    /// (Photos / Videos / Files / Audio / Links / Voice / GIFs) that opens a category gallery, and a
    /// bottom action list (Share / Edit / Delete / Block). Edit mode (self) lets you change name + bio.
    ///
    /// Redesign step 2.5: an <see cref="AppForm"/> (fixed size when modal; <see cref="AppForm.UseAsPane"/> when it is
    /// the Info dock's embedded pane) instead of the 1.x skinned form. The lists are <see cref="SettingGroup"/> cards, the
    /// edit fields <see cref="TextRow"/>s, every colour comes from <see cref="ThemeHelper.Palette"/> and every icon is
    /// DRAWN with GDI+ (a font symbol is a tofu box on RT 8.1). Public surface and behaviour are the 1.x ones.
    /// </summary>
    public class ProfileForm : AppForm
    {
        private readonly TelegramService _service;
        private readonly bool _editable;
        private readonly ChatEntry _entry;
        private Image _avatar;
        private readonly bool _ownsAvatar;

        // Logical (96-DPI) metrics; everything below goes through Px() at the window's DPI.
        // ViewW/H and EditW/H are the 1.x CLIENT area; AppForm adds its title bar and 1 px frame around it.
        private const int ViewW = 440, ViewH = 620, EditW = 420, EditH = 588;
        private const int OuterPad = 16;                                  // side margin of every flow item
        private const int FooterH = 60, FooterButtonW = 116, FooterButtonH = 38;
        private const int TileW = 92, TileH = 64, TileGap = 100;

        private static Palette Pal { get { return ThemeHelper.Palette; } }

        // Edit-mode controls.
        private AvatarCanvas _pic;
        private ThemedLabel _titleLbl;
        private TextRow _first, _last, _about;
        private TextBox _contactBox;
        // PROFILE-EDIT-SELF: username editor + availability gate.
        private TextRow _username;
        private StatusLine _usernameStatus;
        private FlowLayoutPanel _footer;
        private System.Windows.Forms.Timer _usernameCheckTimer;
        private bool _usernameOk = true;   // empty/current username = ok; a CHANGED one must pass the availability check

        // View-mode controls.
        private FlowLayoutPanel _flow;
        private AvatarCanvas _viewAvatarPanel;   // CHANNEL-PHOTO-REFRESH: the big avatar panel (repaint after a photo change)
        private ThemedLabel _nameLbl, _statusLbl;
        private TextBox _idBox;
        private RichInfoLabel _details;
        // The description's base font: the SHARED pixel-unit font at this window's DPI (step 3.2 — InlineText keeps the
        // base font's unit now, so the old point-font workaround is gone). NEVER disposed; refreshed in OnDpiChanged.
        private Font _detailsFont;
        private string _detailsText = "";
        private bool _detailsShown;
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
        ///  2. CHROME. 1.x reserved a caption strip inside the form (a 24 px Padding band) that had to be cleared by
        ///     hand when embedded. Redesign 2.5: the embedded profile calls <see cref="AppForm.UseAsPane"/> — no title
        ///     bar, no custom chrome, no resize gutter, no hit-testing of its own — so there is nothing to clear.
        /// The caller does: `new ProfileForm(svc, entry, avatar, width) { TopLevel = false, Dock = Fill }`,
        /// adds it, calls Show(), and handles <see cref="EmbeddedRoute"/> instead of a DialogResult.</summary>
        public ProfileForm(TelegramService service, ChatEntry entry, Image avatar, int contentWidth, int hostDpi)
            : this(service, entry, avatar, false, contentWidth, hostDpi) { }

        private ProfileForm(TelegramService service, ChatEntry entry, Image avatar, bool editable,
                            int contentWidth = 0, int hostDpi = 0)
            : base(false)
        {
            _service = service;
            _entry = entry;
            _avatar = avatar;
            _editable = editable;
            _ownsAvatar = editable;
            _muted = entry?.Muted ?? false;
            _embedded = contentWidth > 0;
            if (_embedded) UseAsPane(hostDpi);   // before any handle: replaces the 1.x "embed hack"; built at the host's DPI (3.8a)

            Text = editable ? "My Profile" : "Profile";
            if (!_embedded)
            {
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false; MaximizeBox = false;
                Bar.CanMinimize = false;
                // Set Size, never ClientSize (AppForm). The 1.x client area was ViewW x ViewH (EditW x EditH when editing);
                // the title bar and the 1 px frame come on top of it.
                int w = editable ? EditW : ViewW, h = editable ? EditH : ViewH;
                Size = new Size(Px(w) + Padding.Horizontal, Bar.Height + Px(h) + Padding.Bottom);
            }

            // Inner content width (within the side margins). Embedded: the host hands in the dock width MINUS its own
            // scrollbar strip. Modal: the client width minus the scrollbar lane.
            int avail = _embedded ? contentWidth : Px(editable ? EditW : ViewW) - ThemedScrollBar.LaneWidth(WindowDpiValue);
            ContentW = Math.Max(Px(180), avail - 2 * Px(OuterPad));

            if (_editable) BuildEditMode();
            else BuildViewMode();

            // UI-FIX-T1: AppForm subscribes ThemeHelper itself and calls OnThemeChanged() below on a live change.
            Load += (s, e) => LoadDetails();
        }

        /// <summary>Redesign 2.5 — recolour what the palette cannot reach by itself. The cards, rows, tiles and labels read
        /// the palette when they paint (a repaint is enough); the native read-only boxes carry their own colours; the
        /// description label bakes its colours at construction, so it is rebuilt.</summary>
        protected override void OnThemeChanged()
        {
            base.OnThemeChanged();
            if (IsDisposed) return;
            var pal = Pal;
            if (_footer != null) _footer.BackColor = pal.Footer;
            RecolorReadOnly(_contactBox, pal.Text);
            RecolorReadOnly(_idBox, pal.TextDim);
            RebuildDetails();
            Invalidate(true);
        }

        private static void RecolorReadOnly(TextBox tb, Color ink)
        {
            if (tb == null || tb.IsDisposed) return;
            tb.BackColor = Pal.Window;
            tb.ForeColor = ink;
        }

        /// <summary>Step 3.2: DpiScaler's generic pass has already moved the description's explicit Font to the shared pixel
        /// font at the new DPI (InlineText keeps that unit now, so no point font is put back), and the label re-measured
        /// itself in its own IDpiAware callback (children first). Only the stored reference is refreshed.</summary>
        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _detailsFont = FontHelper.UiAt(9.75f, newDpi);   // shared: never dispose
        }

        /// <summary>Esc closes a modal profile (1.x: KeyPreview + KeyDown). An EMBEDDED pane ignores it: closing a
        /// TopLevel = false form would dispose the pane and leave the dock blank. Edit mode has a CancelButton.</summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape && CancelButton == null && !_embedded)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        private User SelfUser => _service.Me;
        private User OtherUser => _entry?.PeerInfo as User;

        private string DisplayLetter()
        {
            string t = _editable ? (SelfUser?.first_name ?? "?") : (_entry?.Title ?? "?");
            return string.IsNullOrEmpty(t) ? "?" : DrawHelper.Initial(t);
        }

        private long AvatarColorKey => _editable ? (SelfUser?.id ?? 0) : (_entry?.PeerId ?? 0);

        // ── Layout helpers ───────────────────────────────────────────────────

        private static Padding PadPx(int dpi, int l, int t, int r, int b)
        {
            return new Padding(Dpi.Px(l, dpi), Dpi.Px(t, dpi), Dpi.Px(r, dpi), Dpi.Px(b, dpi));
        }

        /// <summary>A Padding / Margin from LOGICAL lengths at this window's DPI.</summary>
        private Padding Edge(int l, int t, int r, int b) { return PadPx(WindowDpiValue, l, t, r, b); }

        /// <summary>The scrolling column both modes share: the scroll stack (NoNativeScrollPanel + themed bar + touch
        /// momentum, from ScrollHost) holding one top-down flow of full-width items.</summary>
        private void BuildFlow(Control bodyHost)
        {
            var pal = Pal;
            var scroll = ScrollHost.Wrap(bodyHost, pal.Dark, pal.Accent);
            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = ContentW + 2 * Px(OuterPad),   // 1.x: ContentW + 32 (a second hardcoded 424)
                Padding = Edge(0, 0, 0, 16)
            };
            scroll.Controls.Add(_flow);
        }

        private ThemedLabel NewLabel(string text, TextRole role, LabelRole ink, int heightLogical)
        {
            return new ThemedLabel
            {
                Text = text ?? "",
                PointSize = TextRoles.Points(role),
                Style = TextRoles.Style(role),
                Role = ink,
                TextAlign = HorizontalAlignment.Center,
                Ellipsis = true,
                Width = ContentW,
                Height = Px(heightLogical)
            };
        }

        private SectionHeader SectionHead(string text)
        {
            return new SectionHeader(text) { Width = ContentW };
        }

        private RoundedButton NewFooterButton(string text, RoundedButtonKind kind)
        {
            return new RoundedButton
            {
                Text = text,
                Kind = kind,
                Modern = true,
                Width = Px(FooterButtonW),
                Height = Px(FooterButtonH),
                Margin = Edge(8, 0, 0, 0),
                Font = TextRoles.Font(TextRole.Button, WindowDpiValue)
            };
        }

        /// <summary>A selectable, borderless, read-only text box (the contact line, the ID line).</summary>
        private TextBox ReadOnlyBox(HorizontalAlignment align, float points, Color ink)
        {
            return new TextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                TabStop = false,
                TextAlign = align,
                BackColor = Pal.Window,
                ForeColor = ink,
                Font = FontHelper.UiAt(points, WindowDpiValue)
            };
        }

        // ── Edit mode (self) — unchanged behaviour ───────────────────────────
        private void BuildEditMode()
        {
            var pal = Pal;
            // Fill first, footer second: docking resolves the last-added control first.
            var bodyHost = new Panel { Dock = DockStyle.Fill };
            Content.Controls.Add(bodyHost);
            _footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Px(FooterH),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = Edge(0, 11, 14, 11),
                BackColor = pal.Footer
            };
            Content.Controls.Add(_footer);
            BuildFlow(bodyHost);

            _pic = new AvatarCanvas { Width = ContentW, Height = Px(108), Cursor = Cursors.Hand };   // 96 circle, 8 above, 4 below
            _pic.Paint += (s, e) => PaintAvatar(e.Graphics, AvatarRect(_pic, 96), 34f);
            _pic.Click += (s, e) => ShowEditAvatarMenu();   // PROFILE-EDIT-SELF: tap avatar → View / Set new / Remove
            AddFlow(_pic, 8);

            _titleLbl = NewLabel(FullName(SelfUser), TextRole.Title, LabelRole.Title, 28);
            AddFlow(_titleLbl, 0);
            AddFlow(NewLabel("Tap your photo to change it", TextRole.Field, LabelRole.Dim, 20), 0);

            _contactBox = ReadOnlyBox(HorizontalAlignment.Center, TextRoles.Points(TextRole.Field), pal.Text);
            _contactBox.Width = ContentW;
            AddFlow(_contactBox, 2);

            var card = new SettingGroup { Width = ContentW };
            _first = card.Add(new TextRow("First name", SelfUser?.first_name ?? ""));
            _last = card.Add(new TextRow("Last name", SelfUser?.last_name ?? ""));
            _about = card.Add(new TextRow("Bio", "") { Hint = "A few words about you" });
            _usernameCheckTimer = new System.Windows.Forms.Timer { Interval = 500 };   // debounce the availability check
            _usernameCheckTimer.Tick += (s, e) => CheckUsernameNow();
            _username = card.Add(new TextRow("Username", SelfUser?.MainUsername ?? "") { Hint = "username" });
            _username.Changed += OnUsernameChanged;   // subscribed after the initial value: no spurious availability check
            AddFlow(card, 8);

            _usernameStatus = new StatusLine { Width = ContentW, Height = Px(22) };
            AddFlow(_usernameStatus, 4);

            var save = NewFooterButton("Save", RoundedButtonKind.Primary);
            save.Click += OnSave;
            var cancel = NewFooterButton("Cancel", RoundedButtonKind.Neutral);
            cancel.DialogResult = DialogResult.Cancel;
            _footer.Controls.Add(save);     // first added = rightmost
            _footer.Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
        }

        // ── View mode (others) — Telegram-style scrollable layout ────────────
        /// <summary>Inner content width (within the side margins). WAS a const 392; it is now
        /// per-instance so the SAME layout code can serve the 440-wide dialog and the narrower dock pane
        /// (BATCH-TA-24). Every one of its ~19 uses is a read at build time, so setting it in the ctor
        /// before Build*Mode() runs is sufficient.</summary>
        private readonly int ContentW;
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
            // Embedded: UseAsPane() (ctor) already removed the chrome; the 1.x Padding/Sizable "embed hack" is gone.
            var bodyHost = new Panel { Dock = DockStyle.Fill };
            Content.Controls.Add(bodyHost);
            BuildFlow(bodyHost);

            // Avatar.
            var avi = new AvatarCanvas { Width = ContentW, Height = Px(112), Cursor = Cursors.Hand };
            avi.Paint += (s, e) => PaintAvatar(e.Graphics, AvatarRect(avi, 100), 36f);
            avi.Click += (s, e) => ViewPhoto();
            _viewAvatarPanel = avi;
            AddFlow(avi, 8);

            _nameLbl = NewLabel(_entry?.Title ?? "", TextRole.Title, LabelRole.Title, 28);
            AddFlow(_nameLbl, 2);

            _statusLbl = NewLabel(StatusText(), TextRole.Field, LabelRole.Dim, 20);
            AddFlow(_statusLbl, 0);

            // Action-tile row — branches on peer type: a USER gets Message / Mute / Call / More; a GROUP or
            // CHANNEL gets only Mute / More (Message & Call are user-only — you can't DM or call a group/channel).
            // Whatever set applies is laid out centred.
            var actions = new Panel { Width = ContentW, Height = Px(66) };
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
                var msg = NewTile(ProfileIcon.Message, "Message");
                msg.Clicked += () => { SendMessageRequested = true; DialogResult = DialogResult.OK; Close(); };
                tiles.Add(msg);
            }
            _muteTile = NewTile(_muted ? ProfileIcon.BellOff : ProfileIcon.Bell, _muted ? "Unmute" : "Mute");
            _muteTile.Clicked += ToggleMute;
            tiles.Add(_muteTile);
            if (isUser)
            {
                var call = NewTile(ProfileIcon.Phone, "Call"); call.Enabled2 = false; call.ToolTipText = "Voice calls aren't implemented yet";
                tiles.Add(call);
            }
            var more = NewTile(ProfileIcon.More, "More"); more.Clicked += () => ShowMoreMenu(more);
            tiles.Add(more);

            int tileW = Px(TileW), tileGap = Px(TileGap);
            int startX = Math.Max(0, (ContentW - ((tiles.Count - 1) * tileGap + tileW)) / 2);
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Location = new Point(startX + i * tileGap, 0);
                actions.Controls.Add(tiles[i]);
            }
            AddFlow(actions, 12);

            // Contact info + bio (selectable / copyable). The text sits on the page (no card) and lines up with the
            // section titles above it, which are inset by the rows' own padding.
            AddFlow(SectionHead("INFO"), 8);
            _details = NewDetails();
            _flow.Controls.Add(_details);

            // Secondary, selectable ID line (every peer type).
            _idBox = ReadOnlyBox(HorizontalAlignment.Left, 8.5f, Pal.TextDim);
            int idLeft = Px(OuterPad + SettingRow.PadX) - 1;
            _idBox.Width = ContentW + Px(OuterPad) - idLeft;
            _idBox.Margin = new Padding(idLeft, 0, Px(OuterPad), 0);
            _flow.Controls.Add(_idBox);

            // Media + bottom actions are appended in LoadDetails (counts are async).
        }

        private void SetFlowMargin(Control c, int topGap)
        {
            c.Margin = Edge(OuterPad, topGap, OuterPad, 0);
        }

        private void AddFlow(Control c, int topGap)
        {
            SetFlowMargin(c, topGap);
            _flow.Controls.Add(c);
        }

        // ── The description (rich text) ──────────────────────────────────────
        /// <summary>A description label in the CURRENT palette. RichInfoLabel bakes its colours when it is built, so a theme
        /// change builds a fresh one (<see cref="RebuildDetails"/>). The font is the shared one for this DPI (never disposed).</summary>
        private RichInfoLabel NewDetails()
        {
            var pal = Pal;
            _detailsFont = FontHelper.UiAt(9.75f, WindowDpiValue);   // shared: never dispose
            int left = Px(OuterPad + SettingRow.PadX) - Px(2);   // 2 = RichInfoLabel's own inner pad, so the TEXT lines up
            var rich = new RichInfoLabel(pal.Dark, pal.Accent, _detailsFont);
            rich.Width = ContentW + Px(OuterPad) - left;
            rich.Margin = new Padding(left, Px(2), Px(OuterPad), 0);
            rich.LinkClicked += url => { PendingLink = url; FinishOrRoute(); };
            rich.MentionClicked += (un, uid) => { PendingMentionUser = un; PendingMentionId = uid; FinishOrRoute(); };
            rich.HashtagClicked += tag => { PendingHashtag = tag; FinishOrRoute(); };
            return rich;
        }

        private void ShowDetailsText()
        {
            if (_details == null) return;
            if (!_detailsShown) _details.Visible = false;
            else { _details.Visible = true; _details.SetText(_detailsText, Helpers.TextEntities.Detect(_detailsText), null); }
        }

        private void RebuildDetails()
        {
            if (_details == null || _flow == null || _details.IsDisposed) return;
            var old = _details;
            int index = _flow.Controls.GetChildIndex(old);
            _details = NewDetails();   // also refreshes _detailsFont (shared: the old label's font is never disposed)
            _flow.Controls.Add(_details);
            _flow.Controls.SetChildIndex(_details, index);
            _flow.Controls.Remove(old);
            old.Dispose();
            ShowDetailsText();
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
                var card = new ProfileChannelCard(ch.title, sub, ChannelMsgPreview(val.latest))
                { Width = ContentW, Height = Px(76), Margin = Edge(OuterPad, 8, OuterPad, 0) };
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

            var label = SectionHead(items.Count > 1 ? items.Count + " POSTED STORIES" : "POSTED STORIES");
            SetFlowMargin(label, 8);
            var grid = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(ContentW, 0),   // pin the width → the row WRAPS instead of growing horizontally
                MaximumSize = new Size(ContentW, 0),
                Margin = Edge(OuterPad, 2, OuterPad, 0)
            };
            int shown = Math.Min(items.Count, StoryPreviewMax);   // one preview row; the rest live behind "Show all"

            SettingGroup showAll = null;
            if (items.Count > StoryPreviewMax)
            {
                showAll = new SettingGroup { Width = ContentW };
                var allRow = showAll.Add(new ProfileRow { Icon = ProfileIcon.Stories, Label = "Show all " + items.Count + " stories" });
                allRow.Clicked += OpenStoriesGallery;
                AddFlow(showAll, 2);
            }

            // Slot the section (label, grid, optional "Show all") directly after the INFO id line, regardless of the
            // order the async sections finished in — same technique as the personal-channel card.
            _flow.Controls.Add(label);
            _flow.Controls.Add(grid);
            // Step 3.2 (review): the tiles are added AFTER the grid is in the form — a tile adopts its window's DPI when it is
            // parented, and inside a not-yet-attached grid it could only see the system default.
            for (int i = 0; i < shown; i++)
            {
                int idx = i;   // capture → the viewer deep-links to this story
                var thumb = new StoryThumb(_service, items[i], Pal.Dark) { Margin = Edge(0, 0, 6, 6) };
                thumb.Clicked += () => OpenProfileStoryViewer(idx);
                grid.Controls.Add(thumb);
            }
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
            using (var viewer = new StoryViewerForm(_service, refs, 0, cid => Avatars?.GetCached(cid), Pal.Accent, storyIdx, _postedStories))
                viewer.ShowDialog(this);
        }

        /// <summary>"Show all N stories" → the full scrollable gallery (StoriesGridForm) of every posted story;
        /// tapping a tile there opens the same viewer (with the same preloaded list).</summary>
        private void OpenStoriesGallery()
        {
            if (_entry?.Peer == null || _postedStories == null) return;
            var peerRef = new StoryPeerRef { PeerId = _entry.PeerId, Name = _entry.Title ?? "", Input = _entry.Peer };
            using (var f = new StoriesGridForm(_service, peerRef, _postedStories, Pal.Dark, Pal.Accent, cid => Avatars?.GetCached(cid)))
                f.ShowDialog(this);
        }

        /// <summary>A tappable channel row (avatar + title + "Channel • N subscribers" + latest-message preview),
        /// RTL-aware. Owner-drawn like a chat-list row; avatar fills in async via SetAvatar. The creator sets Width and
        /// Height; colours come from the palette at paint time.</summary>
        private sealed class ProfileChannelCard : Control
        {
            private readonly string _title, _sub, _preview;
            private Image _avatar;
            public event Action Clicked;
            public ProfileChannelCard(string title, string sub, string preview)
            {
                _title = title ?? ""; _sub = sub ?? ""; _preview = preview ?? "";
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { var h = Clicked; if (h != null) h(); } };
            }
            public void SetAvatar(Image a) { _avatar = a; if (!IsDisposed) Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : pal.Window);
                var card = new Rectangle(Point.Empty, new Size(Math.Max(1, Width - 1), Math.Max(1, Height - Dpi.Px(4, dpi))));
                card.Y = Dpi.Px(1, dpi);
                using (var cb = new SolidBrush(pal.Card))
                using (var pen = new Pen(pal.Divider, 1f))
                using (var p = DrawHelper.RoundedRect(card, Dpi.Px(10, dpi)))
                {
                    g.FillPath(cb, p);
                    g.DrawPath(pen, p);
                }
                int d = Dpi.Px(48, dpi), ax = Dpi.Px(12, dpi), ay = (Height - d) / 2;
                var ar = new Rectangle(ax, ay, d, d);
                if (_avatar != null) DrawHelper.DrawCircularImage(g, ar, _avatar);
                else
                {
                    using (var b = new SolidBrush(pal.Accent)) g.FillEllipse(b, ar);
                    string letter = string.IsNullOrEmpty(_title) ? "#" : DrawHelper.Initial(_title);
                    DrawHelper.DrawInitial(g, letter, FontHelper.ForAt(_title, 15f, dpi, FontStyle.Bold), ar, pal.OnAccent);   // ink-centred (owner report 2026-10-14)
                }
                int tx = ax + d + Dpi.Px(12, dpi), tw = Width - tx - Dpi.Px(14, dpi);
                DrawLine(g, _title, tx, Dpi.Px(10, dpi), tw, pal.Text, 10.5f, FontStyle.Bold, dpi);
                DrawLine(g, _sub, tx, Dpi.Px(31, dpi), tw, pal.TextDim, 8.5f, FontStyle.Regular, dpi);
                DrawLine(g, _preview, tx, Dpi.Px(50, dpi), tw, pal.TextDim, 9f, FontStyle.Regular, dpi);
            }
            private static void DrawLine(Graphics g, string text, int x, int y, int w, Color c, float size, FontStyle st, int dpi)
            {
                if (string.IsNullOrEmpty(text) || w < Dpi.Px(20, dpi)) return;
                bool fa = FontHelper.IsPersian(text);
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                            | (fa ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
                TextRenderer.DrawText(g, text, FontHelper.ForAt(text, size, dpi, st), new Rectangle(x, y, w, Dpi.Px(18, dpi)), c, flags);
            }
        }

        private ActionTile NewTile(ProfileIcon icon, string label)
        {
            return new ActionTile { Icon = icon, Label = label, Size = new Size(Px(TileW), Px(TileH)) };
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
            if (Logger.Enabled) Logger.Diag("[NOTIFY] mute write peer=" + (_entry?.PeerId ?? 0) + " mute=" + target + (ok ? " ok" : " FAILED (server returned false)"));
            if (!ok) { ThemedDialog.Show(this, "Mute", "Telegram didn't accept the change — try again.", "OK"); return; }
            _muted = target;
            if (_entry != null) _entry.Muted = target;
            if (_muteTile != null && !_muteTile.IsDisposed)
            {
                _muteTile.Icon = _muted ? ProfileIcon.BellOff : ProfileIcon.Bell;
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
            menu.ShowAt(anchor, new Point(x: 0, y: anchor.Height));
        }

        private void MenuItem(ContextMenuStrip menu, string text, Action action, bool danger = false)
        {
            // The themed menu supplies the ordinary ink; only a destructive item sets its own.
            var item = new ToolStripMenuItem(text);
            if (danger) item.ForeColor = Pal.Danger;
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
            using (var f = new Admin.ManageChatForm(_service, ch, _entry.Peer, _entry.Title ?? "", "", Pal.Dark, Pal.Accent))
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
            _detailsText = detailsText;           // kept so a theme change can rebuild the label with the same text
            _detailsShown = detailsText.Length > 0;
            ShowDetailsText();
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
            using (var f = new EditContactForm(u.first_name ?? "", u.last_name ?? "", Pal.Dark, Pal.Accent))
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
            Logger.Diag("[CONTACT] renamed id=" + u.id);
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
            Logger.Diag("[CONTACT] deleted id=" + u.id);
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
        private struct MediaCat { public ProfileIcon Icon; public string Label; public MessagesFilter Filter; public bool ListMode; }

        private static List<MediaCat> Categories() => new List<MediaCat>
        {
            // Redesign 2.5 — every icon is DRAWN (ProfileIcons), not a font symbol. BATCH-TA-39 had to swap the astral emoji
            //   for BMP shapes because RT 8.1 renders SOME of them as tofu boxes ("it renders here" is not evidence for RT);
            //   a drawn icon looks the same on every Windows version, which is the whole point.
            new MediaCat { Icon = ProfileIcon.Photo, Label = "Photos",         Filter = new InputMessagesFilterPhotos() },
            new MediaCat { Icon = ProfileIcon.Video, Label = "Videos",         Filter = new InputMessagesFilterVideo() },
            new MediaCat { Icon = ProfileIcon.File,  Label = "Files",          Filter = new InputMessagesFilterDocument() },
            new MediaCat { Icon = ProfileIcon.Audio, Label = "Audio",          Filter = new InputMessagesFilterMusic(),  ListMode = true },
            new MediaCat { Icon = ProfileIcon.Link,  Label = "Shared links",   Filter = new InputMessagesFilterUrl(),    ListMode = true },
            new MediaCat { Icon = ProfileIcon.Voice, Label = "Voice messages", Filter = new InputMessagesFilterVoice(),  ListMode = true },
            new MediaCat { Icon = ProfileIcon.Gif,   Label = "GIFs",           Filter = new InputMessagesFilterGif() },
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
            SettingGroup group = null;
            foreach (var cat in cats)
            {
                byType.TryGetValue(cat.Filter.GetType(), out int n);
                if (n <= 0) continue;
                if (!any)
                {
                    AddFlow(SectionHead("SHARED MEDIA"), 10);
                    group = new SettingGroup { Width = ContentW };   // the counted rows are one card
                    AddFlow(group, 0);
                    any = true;
                }
                var captured = cat;
                var row = new ProfileRow { Icon = cat.Icon, Label = cat.Label, Trailing = n.ToString("N0") };
                row.Clicked += () => OpenCategory(captured);
                group.Add(row);
            }
        }

        private void OpenCategory(MediaCat cat)
        {
            if (_entry?.Peer == null) return;
            using (var f = new MediaCategoryForm(this, _service, _entry.Peer, cat.Filter, cat.Label, cat.ListMode))
                f.ShowDialog(this);
        }

        // ── Bottom action list (adapts by peer type) ─────────────────────────
        private void AddBottomActions()
        {
            AddFlow(SectionHead("ACTIONS"), 10);
            var group = new SettingGroup { Width = ContentW };
            if (OtherUser != null)   // user contact → Edit/Delete + Block (Share hidden for release — RELEASE-FIXES-V11)
            {
                AddActionRow(group, ProfileIcon.Edit, "Edit contact", false, EditContact);
                AddActionRow(group, ProfileIcon.Trash, "Delete contact", true, DeleteContact);
                _blockRow = AddActionRow(group, ProfileIcon.Block, _blocked ? "Unblock user" : "Block user", true, BlockUser);
            }
            else                     // channel / group → Leave (destructive)
            {
                AddActionRow(group, ProfileIcon.Leave, LeaveLabel(), true, LeaveChat);
            }
            AddFlow(group, 0);
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
        private ProfileRow AddActionRow(SettingGroup group, ProfileIcon icon, string label, bool danger, Action action)
        {
            var row = new ProfileRow { Icon = icon, Label = label, Danger = danger };
            row.Clicked += () => action();
            group.Add(row);
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
                if (_about != null && !IsDisposed) _about.Value = about;
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

        private SettingGroup _membersGroup;
        private readonly List<MemberRow> _memberRows = new List<MemberRow>();
        private bool _membersClosed;

        private bool IsGroupProfile =>
            _entry?.PeerInfo is Chat
            || (_entry?.PeerInfo is Channel gch && (gch.flags & Channel.Flags.broadcast) == 0);

        private void BuildMembersSection(int totalMembers, List<User> basicUsers)
        {
            AddFlow(SectionHead(totalMembers > 0 ? totalMembers.ToString("N0") + " MEMBERS" : "MEMBERS"), 10);
            _membersGroup = new SettingGroup { Width = ContentW };   // empty (0 tall) until the rows arrive
            AddFlow(_membersGroup, 0);

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
                    if (Logger.Enabled) Logger.Diag("[PRESENCE] members fetch discarded (profile closed)");
                    return;
                }
                if (res?.participants == null || res.users == null || res.participants.Length == 0)
                {
                    if (Logger.Enabled) Logger.Diag("[PRESENCE] members hidden");
                    return;   // hidden/forbidden list → count header only, no rows, no error UI
                }
                var users = new List<User>();
                foreach (var p in res.participants)
                    if (res.users.TryGetValue(p.UserId, out var ub) && ub is User mu) users.Add(mu);
                FillMemberRows(users, totalMembers);
            }
            catch (Exception ex)
            {
                if (Logger.Enabled) Logger.Diag("[PRESENCE] members hidden (" + ex.Message + ")");
            }
        }

        private void FillMemberRows(List<User> users, int totalMembers)
        {
            if (users == null || users.Count == 0)
            {
                if (Logger.Enabled) Logger.Diag("[PRESENCE] members hidden");
                return;
            }
            foreach (var u in users.Take(20))
            {
                var row = new MemberRow(u);
                row.Avatar = Avatars?.GetCached(u.id);
                if (row.Avatar == null) Avatars?.Request(u.id, u);
                var captured = u;
                row.Clicked += () => OpenMemberProfile(captured);
                _memberRows.Add(row);
                _membersGroup.Add(row);
            }
            if (totalMembers > users.Count && _entry.PeerInfo is Channel mgAll)
            {
                var all = new ProfileRow { Icon = ProfileIcon.Members, Label = "Show all " + totalMembers.ToString("N0") + " members" };
                all.Clicked += () =>
                {
                    using (var f = new Admin.ChatMembersForm(_service, mgAll, Admin.ChatMembersForm.Mode.Members, Pal.Dark, Pal.Accent))
                        f.ShowDialog(this);
                };
                _membersGroup.Add(all);
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
        /// formatter the chat header uses ("online" in accent). Owner-drawn, no per-row RPCs. A <see cref="SettingRow"/>, so
        /// it sits in the members card like any other row (the card draws its rounded ends).</summary>
        private sealed class MemberRow : SettingRow
        {
            private readonly User _u;
            public Image Avatar;
            public long UserId => _u.id;
            public event Action Clicked;

            public MemberRow(User u) : base(NameOf(u), true)
            {
                _u = u;
                SetStyle(ControlStyles.StandardDoubleClick, true);   // SettingRow turns it off for steppers; a double tap here must open ONE profile
                AccessibleRole = AccessibleRole.ListItem;
                Refit();
            }

            private static string NameOf(User u)
            {
                string nm = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
                if (nm.Length == 0) nm = !string.IsNullOrEmpty(u.username) ? u.username : "User " + u.id;
                return nm;
            }

            public void RefreshStatus(UserStatus status)
            {
                _u.status = status;
                Invalidate();
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button == MouseButtons.Left) Clicked?.Invoke();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode != Keys.Space) return;
                Clicked?.Invoke();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                PaintBack(g, pal);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int d = Dpi.Px(40, dpi), ax = Dpi.Px(PadX, dpi);
                int ay = (Height - d) / 2;
                var circle = new Rectangle(ax, ay, d, d);
                if (Avatar != null) DrawHelper.DrawCircularImage(g, circle, Avatar);
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_u.id))) g.FillEllipse(b, circle);
                    string name0 = ((_u.first_name ?? "") + (_u.username ?? "?"));
                    string initial = name0.Length > 0 ? DrawHelper.Initial(name0) : "?";
                    DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 13f, dpi, FontStyle.Bold), circle, Color.White);   // ink-centred (owner report 2026-10-14)
                }

                int tx = ax + d + Dpi.Px(12, dpi), right = Width - Dpi.Px(PadX, dpi);
                // Fonts without the text argument = the Roboto face, as in 1.x: GDI substitutes Persian glyphs, and the
                // taller Vazirmatn line would not fit these fixed name / status bands.
                TextRenderer.DrawText(g, NameOf(_u), FontHelper.UiAt(10.5f, dpi, FontStyle.Bold),
                    Rectangle.FromLTRB(tx, Dpi.Px(7, dpi), Math.Max(tx + 1, right), Dpi.Px(27, dpi)), pal.Text,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                string st = MainForm.StatusText(_u);
                if (st.Length == 0) st = !string.IsNullOrEmpty(_u.username) ? "@" + _u.username : "member";
                TextRenderer.DrawText(g, st, FontHelper.UiAt(8.5f, dpi),
                    Rectangle.FromLTRB(tx, Dpi.Px(28, dpi), Math.Max(tx + 1, right), Dpi.Px(46, dpi)),
                    st == "online" ? pal.AccentFg : pal.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                PaintFocusRing(g, pal, Rectangle.Inflate(ClientRectangle, -Dpi.Px(7, dpi), -Dpi.Px(5, dpi)), Dpi.Px(6, dpi), dpi);
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
        private static string FullName(User u)
        {
            if (u == null) return "";
            return string.Join(" ", new[] { u.first_name, u.last_name }).Trim();
        }

        /// <summary>The avatar circle inside its canvas: centred, a logical 8 px from the top.</summary>
        private Rectangle AvatarRect(Control canvas, int diameterLogical)
        {
            int d = Px(diameterLogical);
            return new Rectangle((canvas.Width - d) / 2, Px(8), d, d);
        }

        private void PaintAvatar(Graphics g, Rectangle rect, float fontSize)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_avatar != null)
            {
                DrawHelper.DrawCircularImage(g, rect, _avatar);
            }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(AvatarColorKey)))
                    g.FillEllipse(b, rect);
                string letter = DisplayLetter();
                DrawHelper.DrawInitial(g, letter, FontHelper.ForAt(letter, fontSize, WindowDpiValue, FontStyle.Bold), rect, Color.White);   // ink-centred (owner report 2026-10-14)
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
            string first = _first.Value.Trim();
            if (first.Length == 0) { ThemedDialog.Show(this, "Profile", "A first name is required.", "OK"); return; }   // name required
            string last = _last.Value.Trim();
            string about = _about.Value.Trim();

            // USERNAME gate (PART 3): a CHANGED username must be confirmed available, else block the whole save.
            string newUn = (_username?.Value ?? "").Trim();
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
                        _usernameOk = false; SetUsernameStatus("Taken — pick another", StatusTone.Bad);
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
            if (_usernameStatus != null) { _usernameStatus.Tone = StatusTone.Neutral; _usernameStatus.Text = ""; }
            _usernameCheckTimer?.Stop();
            _usernameCheckTimer?.Start();   // debounce → CheckUsernameNow after 500ms idle
        }

        private async void CheckUsernameNow()
        {
            _usernameCheckTimer?.Stop();
            string u = (_username.Value ?? "").Trim();
            string cur = SelfUser?.MainUsername ?? "";
            if (u.Length == 0) { SetUsernameStatus("", StatusTone.Neutral); _usernameOk = true; return; }                 // empty = no change
            if (u == cur)      { SetUsernameStatus("This is your current username.", StatusTone.Neutral); _usernameOk = true; return; }
            if (!IsValidUsernameFormat(u)) { SetUsernameStatus("Invalid — 5–32 letters/digits/_ , starting with a letter.", StatusTone.Bad); _usernameOk = false; return; }
            SetUsernameStatus("Checking…", StatusTone.Neutral);
            bool free;
            try { free = await _service.CheckSelfUsernameAsync(u); }
            catch { SetUsernameStatus("Couldn't check — is your VPN on?", StatusTone.Neutral); _usernameOk = false; return; }
            if (IsDisposed || (_username.Value ?? "").Trim() != u) return;   // stale: the user kept typing
            _usernameOk = free;
            SetUsernameStatus(free ? "Available" : "Taken or unavailable", free ? StatusTone.Good : StatusTone.Bad);
        }

        private void SetUsernameStatus(string text, StatusTone tone)
        {
            if (_usernameStatus == null) return;
            _usernameStatus.Tone = tone;
            _usernameStatus.Text = text;
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
            menu.ShowAt(_pic, new Point(_pic.Width / 2 - Px(48), _pic.Height));
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
            // (No _detailsFont disposal: step 3.2 — it is a shared FontCache font, never disposed.)
        }

        // ══ Icons: DRAWN with GDI+ lines/shapes, never font symbols (tofu boxes on RT 8.1) ══════════════════════════════
        private enum ProfileIcon
        {
            None, Message, Bell, BellOff, Phone, More, Photo, Video, File, Audio, Link, Voice, Gif,
            Edit, Trash, Block, Leave, Stories, Members, Dot, Check, Cross
        }

        private static class ProfileIcons
        {
            /// <summary>Draws <paramref name="icon"/> centred in the square <paramref name="area"/> in <paramref name="ink"/>.
            /// The geometry lives on a 24-unit grid and scales with the area, so one icon serves the 14 px status mark and the
            /// 26 px tile glyph at any DPI.</summary>
            public static void Draw(Graphics g, ProfileIcon icon, Rectangle area, Color ink)
            {
                if (icon == ProfileIcon.None || area.Width < 6 || area.Height < 6) return;
                var saved = g.Save();
                try
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float s = Math.Min(area.Width, area.Height) / 24f;
                    float ox = area.X + (area.Width - 24f * s) / 2f, oy = area.Y + (area.Height - 24f * s) / 2f;
                    float lw = Math.Max(1.25f, 1.7f * s);
                    using (var pen = new Pen(ink, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    using (var fill = new SolidBrush(ink))
                    {
                        PointF P(float x, float y) { return new PointF(ox + x * s, oy + y * s); }
                        RectangleF R(float x, float y, float w, float h) { return new RectangleF(ox + x * s, oy + y * s, w * s, h * s); }
                        void Ln(float x1, float y1, float x2, float y2) { g.DrawLine(pen, P(x1, y1), P(x2, y2)); }
                        void Poly(bool closed, params float[] xy)
                        {
                            var pts = new PointF[xy.Length / 2];
                            for (int i = 0; i < pts.Length; i++) pts[i] = P(xy[2 * i], xy[2 * i + 1]);
                            if (closed) g.DrawPolygon(pen, pts); else g.DrawLines(pen, pts);
                        }
                        void RBox(float x, float y, float w, float h, float r)
                        {
                            using (var path = Round(R(x, y, w, h), r * s)) g.DrawPath(pen, path);
                        }
                        void BellShape()
                        {
                            using (var path = new GraphicsPath())
                            {
                                path.AddArc(R(6.5f, 3.5f, 11f, 11f), 180f, 180f);   // the dome, left to right over the top
                                path.AddLine(P(17.5f, 9f), P(17.5f, 13f));
                                path.AddLine(P(17.5f, 13f), P(19.5f, 16.5f));
                                path.AddLine(P(19.5f, 16.5f), P(4.5f, 16.5f));
                                path.AddLine(P(4.5f, 16.5f), P(6.5f, 13f));
                                path.AddLine(P(6.5f, 13f), P(6.5f, 9f));
                                path.CloseFigure();
                                g.DrawPath(pen, path);
                            }
                            g.DrawArc(pen, R(9.5f, 14.5f, 5f, 6f), 0f, 180f);        // the clapper
                            Ln(12f, 2.3f, 12f, 3.6f);                                 // the nub
                        }

                        switch (icon)
                        {
                            case ProfileIcon.Message:
                                RBox(3f, 5.5f, 18f, 13f, 2.5f);
                                Poly(false, 3.8f, 7.2f, 12f, 13.2f, 20.2f, 7.2f);
                                break;
                            case ProfileIcon.Bell:
                                BellShape();
                                break;
                            case ProfileIcon.BellOff:
                                BellShape();
                                Ln(4f, 4f, 20f, 20f);
                                break;
                            case ProfileIcon.Phone:
                                RBox(7f, 2.5f, 10f, 19f, 2.2f);
                                Ln(10.5f, 18.4f, 13.5f, 18.4f);
                                break;
                            case ProfileIcon.More:
                                g.FillEllipse(fill, R(3.1f, 10.1f, 3.8f, 3.8f));
                                g.FillEllipse(fill, R(10.1f, 10.1f, 3.8f, 3.8f));
                                g.FillEllipse(fill, R(17.1f, 10.1f, 3.8f, 3.8f));
                                break;
                            case ProfileIcon.Photo:
                                RBox(3f, 4.5f, 18f, 15f, 2.5f);
                                g.FillEllipse(fill, R(7.2f, 8.2f, 3.2f, 3.2f));
                                Poly(false, 4.5f, 17.5f, 10f, 12.5f, 14f, 16.5f, 16.5f, 14.2f, 19.5f, 17.5f);
                                break;
                            case ProfileIcon.Video:
                                RBox(3f, 5.5f, 18f, 13f, 2.5f);
                                g.FillPolygon(fill, new[] { P(10f, 9f), P(10f, 15f), P(15.2f, 12f) });
                                break;
                            case ProfileIcon.File:
                                Poly(true, 6.5f, 3f, 14f, 3f, 18.5f, 7.5f, 18.5f, 21f, 6.5f, 21f);
                                Poly(false, 14f, 3.4f, 14f, 7.8f, 18.1f, 7.8f);
                                Ln(9.5f, 13f, 15.5f, 13f);
                                Ln(9.5f, 16.5f, 15.5f, 16.5f);
                                break;
                            case ProfileIcon.Audio:
                                g.FillEllipse(fill, R(5.5f, 15f, 5.5f, 4.2f));
                                g.FillEllipse(fill, R(14.5f, 13f, 5.5f, 4.2f));
                                Ln(10.7f, 17f, 10.7f, 6f);
                                Ln(19.7f, 15f, 19.7f, 4.5f);
                                Ln(10.7f, 6.2f, 19.7f, 4.7f);
                                break;
                            case ProfileIcon.Link:
                                {
                                    // Two pill-shaped chain links, interlocked, on a 45 degree slant.
                                    var st = g.Save();
                                    g.TranslateTransform(ox + 12f * s, oy + 12f * s);
                                    g.RotateTransform(-45f);
                                    using (var a = Round(new RectangleF(-10.5f * s, -3.6f * s, 11.5f * s, 7.2f * s), 3.6f * s)) g.DrawPath(pen, a);
                                    using (var b = Round(new RectangleF(-1f * s, -3.6f * s, 11.5f * s, 7.2f * s), 3.6f * s)) g.DrawPath(pen, b);
                                    g.Restore(st);
                                    break;
                                }
                            case ProfileIcon.Voice:
                                RBox(9.2f, 2.8f, 5.6f, 10.5f, 2.8f);
                                g.DrawArc(pen, R(6f, 7f, 12f, 10.5f), 0f, 180f);
                                Ln(12f, 17.5f, 12f, 21f);
                                Ln(9f, 21f, 15f, 21f);
                                break;
                            case ProfileIcon.Gif:
                                g.DrawEllipse(pen, R(3.2f, 3.2f, 17.6f, 17.6f));
                                g.FillPolygon(fill, new[] { P(10.3f, 8.4f), P(10.3f, 15.6f), P(16f, 12f) });
                                break;
                            case ProfileIcon.Edit:
                                Poly(true, 5f, 19f, 5.8f, 15f, 15.8f, 5f, 19f, 8.2f, 9f, 18.2f);
                                Ln(13.8f, 7f, 17f, 10.2f);
                                break;
                            case ProfileIcon.Trash:
                                Ln(5f, 7f, 19f, 7f);
                                Poly(false, 9.5f, 7f, 9.5f, 4.5f, 14.5f, 4.5f, 14.5f, 7f);
                                Poly(false, 6.8f, 7.2f, 7.8f, 20f, 16.2f, 20f, 17.2f, 7.2f);
                                Ln(10.5f, 10.5f, 10.5f, 16.8f);
                                Ln(13.5f, 10.5f, 13.5f, 16.8f);
                                break;
                            case ProfileIcon.Block:
                                g.DrawEllipse(pen, R(3.5f, 3.5f, 17f, 17f));
                                Ln(6f, 6f, 18f, 18f);
                                break;
                            case ProfileIcon.Leave:
                                Poly(false, 10f, 4f, 4.5f, 4f, 4.5f, 20f, 10f, 20f);
                                Ln(9f, 12f, 20f, 12f);
                                Poly(false, 16f, 7.5f, 20.5f, 12f, 16f, 16.5f);
                                break;
                            case ProfileIcon.Stories:
                                RBox(4f, 4f, 7f, 7f, 1.8f);
                                RBox(13f, 4f, 7f, 7f, 1.8f);
                                RBox(4f, 13f, 7f, 7f, 1.8f);
                                RBox(13f, 13f, 7f, 7f, 1.8f);
                                break;
                            case ProfileIcon.Members:
                                g.DrawEllipse(pen, R(8.6f, 4.2f, 6.8f, 6.8f));
                                g.DrawArc(pen, R(5f, 13.5f, 14f, 12f), 180f, 180f);
                                break;
                            case ProfileIcon.Dot:
                                g.FillEllipse(fill, R(8.8f, 8.8f, 6.4f, 6.4f));
                                break;
                            case ProfileIcon.Check:
                                Poly(false, 5f, 12.5f, 10f, 17.5f, 19f, 7f);
                                break;
                            case ProfileIcon.Cross:
                                Ln(6f, 6f, 18f, 18f);
                                Ln(18f, 6f, 6f, 18f);
                                break;
                        }
                    }
                }
                finally { g.Restore(saved); }
            }

            /// <summary>A rounded rectangle in float pixels (DrawHelper.RoundedRect is int-only, too coarse at icon size).</summary>
            private static GraphicsPath Round(RectangleF rc, float radius)
            {
                var path = new GraphicsPath();
                float d = Math.Min(radius, Math.Min(rc.Width, rc.Height) / 2f) * 2f;
                if (d < 0.5f) { path.AddRectangle(rc); return path; }
                path.AddArc(rc.X, rc.Y, d, d, 180f, 90f);
                path.AddArc(rc.Right - d, rc.Y, d, d, 270f, 90f);
                path.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0f, 90f);
                path.AddArc(rc.X, rc.Bottom - d, d, d, 90f, 90f);
                path.CloseFigure();
                return path;
            }
        }

        /// <summary>The big avatar's canvas: a double-buffered Panel that paints through the form's PaintAvatar. Its background
        /// is the ambient page colour (never Transparent), so it follows the theme by itself.</summary>
        private sealed class AvatarCanvas : Panel
        {
            public AvatarCanvas() { DoubleBuffered = true; }
        }

        private enum StatusTone { Neutral, Good, Bad }

        /// <summary>One line of status under a field (the username's availability): dim, or a green tick / red cross with
        /// text. Palette colours at paint time, so it follows the theme; the tick and cross are drawn, not font glyphs.</summary>
        private sealed class StatusLine : Control
        {
            private StatusTone _tone;

            public StatusLine()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            public StatusTone Tone
            {
                get { return _tone; }
                set { if (_tone == value) return; _tone = value; Invalidate(); }
            }

            public override string Text
            {
                get { return base.Text; }
                set { base.Text = value ?? ""; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                g.Clear(Parent != null ? Parent.BackColor : pal.Window);
                string text = Text;
                if (text.Length == 0) return;
                Color ink = _tone == StatusTone.Good ? pal.Success : _tone == StatusTone.Bad ? pal.Danger : pal.TextDim;
                int x = Dpi.Px(SettingRow.PadX, dpi);   // lines up with the captions inside the card above
                if (_tone != StatusTone.Neutral)
                {
                    int d = Dpi.Px(14, dpi);
                    ProfileIcons.Draw(g, _tone == StatusTone.Good ? ProfileIcon.Check : ProfileIcon.Cross, Rectangle.FromLTRB(x, (Height - d) / 2, x + d, (Height - d) / 2 + d), ink);
                    x += d + Dpi.Px(6, dpi);
                }
                TextRenderer.DrawText(g, text, FontHelper.ForAt(text, 9f, dpi), Rectangle.FromLTRB(x, 0, Math.Max(x + 1, Width), Height), ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        // ── Owner-painted action tile (icon over label) ──────────────────────
        private sealed class ActionTile : Control
        {
            private bool _hover;
            private readonly ToolTip _tip = new ToolTip();

            public ProfileIcon Icon;
            public string Label;
            public bool Enabled2 = true;
            public string ToolTipText { set { if (!string.IsNullOrEmpty(value)) _tip.SetToolTip(this, value); } }
            public event Action Clicked;

            public ActionTile()
            {
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left && Enabled2) Clicked?.Invoke(); }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _tip.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                g.Clear(Parent != null ? Parent.BackColor : pal.Window);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(Point.Empty, new Size(Math.Max(1, Width - 1), Math.Max(1, Height - 1)));
                Color fill = _hover && Enabled2 ? ColorMath.Mix(pal.Card, pal.Text, 0.07f) : pal.Card;
                using (var b = new SolidBrush(fill))
                using (var pen = new Pen(pal.Divider, 1f))
                using (var path = DrawHelper.RoundedRect(r, Dpi.Px(10, dpi)))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }

                Color ink = Enabled2 ? pal.AccentFg : pal.TextDisabled;
                int isz = Dpi.Px(26, dpi);
                ProfileIcons.Draw(g, Icon, new Rectangle((Width - isz) / 2, Dpi.Px(8, dpi), isz, isz), ink);
                Color lc = Enabled2 ? pal.Text : pal.TextDisabled;
                string label = Label ?? "";
                TextRenderer.DrawText(g, label, FontHelper.ForAt(label, 9f, dpi),
                    Rectangle.FromLTRB(0, Dpi.Px(38, dpi), Width, Dpi.Px(58, dpi)), lc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        // ── Owner-painted list row (icon + label + trailing count) ───────────
        /// <summary>A tappable row: drawn icon, label, optional trailing count, optional right-click / long-press. A
        /// <see cref="SettingRow"/>: inside a <see cref="SettingGroup"/> it takes the card's width, its hairlines and its
        /// rounded ends; outside one (the gallery's list) it clears to the page. A tab stop, Space activates it.</summary>
        private sealed class ProfileRow : SettingRow
        {
            private const int RowLogical = 48, IconBox = 22, IconGap = 14;
            private bool _hover;

            public ProfileIcon Icon;
            public string Trailing;
            public bool Danger;
            public event Action Clicked;

            /// <summary>Right-click / touch-and-hold / menu key (screen point). Only fires if subscribed.</summary>
            public event EventHandler<Point> ContextMenuRequested;
            private const int WM_CONTEXTMENU = 0x007B;

            /// <summary>The row's text (the base caption).</summary>
            public string Label { get { return Text; } set { Text = value; } }

            public ProfileRow() : base("", true)
            {
                SetStyle(ControlStyles.StandardDoubleClick, true);   // SettingRow turns it off for steppers; a double tap here must act ONCE (Block has no confirm)
                AccessibleRole = AccessibleRole.PushButton;
                Refit();
            }

            public override int HeightFor(int width, int dpi) { return Dpi.Px(RowLogical, dpi); }

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

            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) Clicked?.Invoke(); }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode != Keys.Space) return;
                Clicked?.Invoke();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                PaintBack(g, pal);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int padX = Dpi.Px(PadX, dpi);

                if (_hover && Enabled)
                {
                    // An inset pill, never the full row: a full-width fill would square off the card's rounded ends.
                    int ix = Dpi.Px(4, dpi), iy = Dpi.Px(2, dpi);
                    var hr = new Rectangle(ix, iy, Math.Max(1, Width - 2 * ix - 1), Math.Max(1, Height - 2 * iy - 1));
                    using (var path = DrawHelper.RoundedRect(hr, Dpi.Px(6, dpi)))
                    using (var b = new SolidBrush(ColorMath.Mix(Backdrop(pal), pal.Text, 0.07f)))
                        g.FillPath(b, path);
                }

                int isz = Dpi.Px(IconBox, dpi);
                ProfileIcons.Draw(g, Icon, new Rectangle(padX, (Height - isz) / 2, isz, isz), Danger ? pal.Danger : pal.AccentFg);
                int tx = padX + isz + Dpi.Px(IconGap, dpi);

                int right = Width - padX;
                string trailing = Trailing;
                if (!string.IsNullOrEmpty(trailing))
                {
                    Font tf = FontHelper.ForAt(trailing, 9.5f, dpi);
                    int tw = TextRenderer.MeasureText(trailing, tf, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
                    DrawTextLine(g, trailing, tf, pal.TextDim, Rectangle.FromLTRB(right - tw, 0, right, Height), true, false);
                    right -= tw + Dpi.Px(IconGap, dpi);
                }
                DrawTextLine(g, Text, FontHelper.ForAt(Text, CaptionPt, dpi), Danger ? pal.Danger : pal.Text,
                    Rectangle.FromLTRB(tx, 0, Math.Max(tx + 1, right), Height), false, true);

                PaintFocusRing(g, pal, Rectangle.Inflate(ClientRectangle, -Dpi.Px(7, dpi), -Dpi.Px(5, dpi)), Dpi.Px(6, dpi), dpi);
            }
        }

        /// <summary>One owner-painted media thumbnail (image, or a file icon + name). The creator sets Size.</summary>
        private sealed class MediaThumb : Control
        {
            private readonly int _index;
            private Image _img;
            private string _file;

            public event Action<int> Clicked;

            /// <summary>Right-click / touch-and-hold / menu key (screen point).</summary>
            public event EventHandler<Point> ContextMenuRequested;
            private const int WM_CONTEXTMENU = 0x007B;

            public MediaThumb(int index)
            {
                _index = index;
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
                var pal = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                g.Clear(ColorMath.Mix(pal.Window, pal.Text, 0.10f));
                if (_img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    double scale = Math.Max((double)Width / _img.Width, (double)Height / _img.Height);
                    int w = (int)(_img.Width * scale), h = (int)(_img.Height * scale);
                    g.DrawImage(_img, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));   // cover-crop
                }
                else if (_file != null)
                {
                    int isz = Dpi.Px(48, dpi);
                    DrawHelper.DrawFileIcon(g, new Rectangle((Width - isz) / 2, Dpi.Px(12, dpi), isz, isz), _file);
                    TextRenderer.DrawText(g, _file, FontHelper.UiAt(7.5f, dpi),
                        Rectangle.FromLTRB(Dpi.Px(2, dpi), Dpi.Px(64, dpi), Width - Dpi.Px(2, dpi), Dpi.Px(86, dpi)),
                        pal.Text,
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

        /// <summary>The category gallery: a fixed-size <see cref="AppForm"/> (the app's own title bar, like every other
        /// window since 2.x) holding the thumbnail grid or the list rows in the scroll stack.</summary>
        private sealed class MediaCategoryForm : AppForm
        {
            private readonly ProfileForm _owner;
            private readonly TelegramService _service;
            private readonly InputPeer _peer;
            private readonly MessagesFilter _filter;
            private readonly bool _listMode;
            private readonly int _rowWidth;

            private readonly FlowLayoutPanel _grid;
            private readonly NoNativeScrollPanel _scroll;
            private readonly List<MediaItem> _items = new List<MediaItem>();
            private int _offsetId;
            private bool _loading, _end;

            public MediaCategoryForm(ProfileForm owner, TelegramService service, InputPeer peer, MessagesFilter filter,
                                     string title, bool listMode) : base(false)
            {
                _owner = owner; _service = service; _peer = peer; _filter = filter; _listMode = listMode;

                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MinimizeBox = false; MaximizeBox = false;
                Bar.CanMinimize = false;
                int inner = Px(listMode ? 420 : 380);   // the 1.x client width; the title bar and frame come on top
                Size = new Size(inner + Padding.Horizontal, Bar.Height + Px(560) + Padding.Bottom);

                var pal = ThemeHelper.Palette;
                var host = new Panel { Dock = DockStyle.Fill };
                Content.Controls.Add(host);
                _scroll = ScrollHost.Wrap(host, pal.Dark, pal.Accent);   // NoNativeScrollPanel + themed bar + touch momentum
                _rowWidth = inner - ThemedScrollBar.LaneWidth(WindowDpiValue) - 2 * Px(8);
                _grid = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    WrapContents = !listMode,
                    FlowDirection = listMode ? FlowDirection.TopDown : FlowDirection.LeftToRight,
                    Padding = PadPx(WindowDpiValue, 8, 8, 8, 8)
                };
                _scroll.Controls.Add(_grid);
                _scroll.Scroll += (s, e) => MaybeMore();
                // Tiles/rows live in the inner _grid (not the surface), so register each for WM_TOUCH
                // ourselves — long-press over a thumb then synthesizes WM_CONTEXTMENU (pan still pans _scroll).
                _grid.ControlAdded += (s, e) => TouchScroller.RegisterControl(e.Control);

                Load += (s, e) => LoadPage();
            }

            protected override void OnThemeChanged()
            {
                base.OnThemeChanged();
                Invalidate(true);
            }

            /// <summary>Esc closes the gallery (1.x: KeyPreview + KeyDown).</summary>
            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Escape) { Close(); return true; }
                return base.ProcessDialogKey(keyData);
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
                var tile = new MediaThumb(index) { Margin = PadPx(WindowDpiValue, 2, 2, 2, 2), Size = new Size(Px(88), Px(88)) };
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
                var row = new ProfileRow { Icon = ProfileIcon.Dot, Label = ListRowText(m), Width = _rowWidth };
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
                menu.ShowAt(screenPt);
            }

            private void MenuItem(ContextMenuStrip menu, string text, Action action)
            {
                var it = new ToolStripMenuItem(text);   // the themed menu supplies the ink
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
