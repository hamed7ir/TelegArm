using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── Hamburger menu ───────────────────────────────────────────────────

        private DrawerMenu _drawer;
        private DrawerOutsideCloser _drawerCloser;   // HAMBURGER-SCRIM-FIX: closes the card-width drawer on an outside tap

        /// <summary>Opens the Telegram-style left drawer (account header + full menu + Night Mode toggle).</summary>
        private void ShowDrawer()
        {
            if (_drawer != null) { CloseDrawer(); return; }

            // HAMBURGER-FIX: the drawer used to snapshot the WHOLE window (DrawToBitmap) each open for a dimmed
            // backdrop — a synchronous full-UI render that was the DOMINANT open-lag (esp. on RT ARM32). The
            // backdrop is now a solid scrim (DrawerMenu's null-snap path), so the open never blocks on a render.

            var me = _service.Me;
            string name = me != null ? string.Join(" ", new[] { me.first_name, me.last_name }).Trim() : "TelegArm";

            var rows = new List<DrawerMenu.Row>();
            // Account switcher: the ACTIVE account is the header above — list only the OTHERS (tap to switch),
            // so the same account is never shown twice. De-duped by id (defensive; folder names are already ids).
            // HAMBURGER-FIX: DON'T decode avatars on the open path — each was a sync disk-read + JPEG-decode +
            // Bitmap-copy on the UI thread. Rows open with a letter placeholder (DrawerMenu draws it for a null
            // Avatar); the real images decode OFF-thread and swap in (LoadDrawerAvatarsAsync, below).
            var seen = new HashSet<long>();
            var pendingAv = new List<KeyValuePair<DrawerMenu.Row, string>>();
            foreach (var acc in AccountStore.ListAccounts())
            {
                if (acc.Id == AccountContext.ActiveId || !seen.Add(acc.Id)) continue;
                long accId = acc.Id; string accName = acc.Name;
                var accRow = new DrawerMenu.Row
                {
                    IsAccount = true, Label = acc.Name, AvatarKey = acc.Id, Avatar = null,
                    Action = Wrap(() => SwitchAccount(accId, accName))
                };
                rows.Add(accRow);
                if (!string.IsNullOrEmpty(acc.AvatarPath) && File.Exists(acc.AvatarPath))
                    pendingAv.Add(new KeyValuePair<DrawerMenu.Row, string>(accRow, acc.AvatarPath));
            }
            rows.Add(Row("➕", "Add Account", AddAccount));
            rows.Add(Sep());
            rows.Add(Row("👤", "My Profile", ShowProfile));
            rows.Add(Row("◎", "New Group", NewGroup));
            rows.Add(Row("📢", "New Channel", NewChannel));
            rows.Add(Row("📇", "Contacts", OpenContacts));
            // RELEASE-FIXES-V11 (H2): "Calls" hidden for release (parked for a later version — don't ship a "coming soon" dead item).
            rows.Add(Row("🔖", "Saved Messages", () => OpenSavedMessages()));
            rows.Add(Sep());
            rows.Add(Row("⚙", "Settings", OpenSettings));
            rows.Add(Row("🎨", "Appearance…", OpenAppearanceSettings));   // 2.7b: Settings, on the Appearance page
            rows.Add(Toggle("🌙", "Night Mode", () => ThemeHelper.IsDark, ToggleNightMode));
            rows.Add(Sep());
            rows.Add(Row("ℹ", "About TelegArm", ShowAbout));   // "Sticker engine…" moved into Settings → General
            rows.Add(Row("📣", "TelegArm Channel", OpenTelegArmChannel));   // opens the official channel in the chat view
            rows.Add(Sep());
            rows.Add(Danger("🚪", "Log out" + (me != null ? " (" + name + ")" : ""), LogOut));   // removes the ACTIVE account
            string letter = !string.IsNullOrEmpty(name) ? DrawHelper.Initial(name) : "?";
            var av = _avatars.GetCached(me?.id ?? 0);

            // HAMBURGER-SCRIM-FIX (Option A): the drawer is now only as WIDE AS THE CARD (not the whole window),
            // so the app content to its right stays FULLY VISIBLE + UNDIMMED — no backdrop, no black screen. (A
            // full-window control would occlude the content; WinForms transparency renders the form bg black here.)
            // Step 3.4: the drawer is built per open at the window's DPI (CardW is logical).
            _drawer = new DrawerMenu(null, _dark, _accent, name, letter, av, me?.id ?? 0, rows, Wrap(ShowProfile), WindowDpiValue)
            {
                Bounds = new Rectangle(0, 0, Px(DrawerMenu.CardW), Content.ClientSize.Height)   // 2.7a: under the title bar
            };
            _drawer.CloseRequested += () => BeginInvoke((Action)CloseDrawer);
            Content.Controls.Add(_drawer);
            _drawer.BringToFront();
            _drawer.Focus();

            // The narrow drawer no longer covers the outside, so it can't catch the tap-to-close there. A pre-
            // dispatch message filter closes it on any pointer/mouse down that lands OUTSIDE the card (it sees the
            // tap before the content control does). Removed again in CloseDrawer.
            if (_drawerCloser == null) { _drawerCloser = new DrawerOutsideCloser(this); Application.AddMessageFilter(_drawerCloser); }

            // Decode the other-account avatars OFF the UI thread and swap them into the open drawer (letters →
            // photos). Never blocks the open; guarded so a closed/replaced drawer can't get a stale image.
            LoadDrawerAvatarsAsync(_drawer, pendingAv);
        }

        /// <summary>HAMBURGER-FIX: off-thread account-avatar loader for the drawer. Each avatar is decoded on a
        /// worker thread, then marshaled back and assigned to its row (the drawer repaints). If the drawer was
        /// closed/replaced before a decode finished, the freshly-decoded image is disposed (no leak) instead of
        /// assigned. Row avatars stay owned by DrawerMenu.Dispose (per-open lifetime, unchanged).</summary>
        private void LoadDrawerAvatarsAsync(DrawerMenu drawer, List<KeyValuePair<DrawerMenu.Row, string>> pending)
        {
            if (drawer == null || pending == null || pending.Count == 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var kv in pending)
                {
                    Image img = null;
                    try { using (var fs = File.OpenRead(kv.Value)) using (var t = Image.FromStream(fs)) img = new Bitmap(t); }
                    catch { img = null; }
                    if (img == null) continue;
                    var row = kv.Key; var decoded = img;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (_drawer == drawer && !drawer.IsDisposed) { row.Avatar = decoded; drawer.Invalidate(); }
                            else { try { decoded.Dispose(); } catch { } }   // drawer gone → don't leak the decode
                        }));
                    }
                    catch { try { decoded.Dispose(); } catch { } }   // form closing → BeginInvoke unavailable
                }
            });
        }

        private void CloseDrawer()
        {
            // Drop the outside-tap filter first (always, even if the drawer ref is already gone) so it never lingers.
            if (_drawerCloser != null) { try { Application.RemoveMessageFilter(_drawerCloser); } catch { } _drawerCloser = null; }
            var d = _drawer;
            if (d == null) return;
            _drawer = null;
            try { Controls.Remove(d); d.Dispose(); } catch { }
        }

        /// <summary>HAMBURGER-SCRIM-FIX: pre-dispatch outside-tap detector for the (now card-width) drawer. Sees
        /// pointer/mouse DOWN messages before they reach the content control; if one lands on any window that isn't
        /// the drawer (or the hamburger), it closes the drawer. Never SWALLOWS the message (safe for touch — no
        /// half-consumed WM_TOUCH gesture); the tap also proceeds normally. Esc + a menu tap remain fallbacks.</summary>
        private sealed class DrawerOutsideCloser : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_NCLBUTTONDOWN = 0x00A1,
                              WM_POINTERDOWN = 0x0246, WM_TOUCH = 0x0240;
            private readonly MainForm _f;
            public DrawerOutsideCloser(MainForm f) { _f = f; }
            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN:
                    case WM_RBUTTONDOWN:
                    case WM_NCLBUTTONDOWN:
                    case WM_POINTERDOWN:
                    case WM_TOUCH:
                        var d = _f._drawer;
                        if (d != null && !_f.IsDisposed && m.HWnd != d.Handle
                            && (_f._hamburger == null || m.HWnd != _f._hamburger.Handle))
                        {
                            try { _f.BeginInvoke((Action)_f.CloseDrawer); } catch { }
                        }
                        break;
                }
                return false;   // observe only — do not consume the tap
            }
        }

        /// <summary>Owner-draws the reply/edit strip: the Noto ↩/✏️ glyph + the preview (Vazirmatn for Persian,
        /// inline Noto emoji). (History: the old MaterialLabel couldn't — it ignored .Image and forced its own font.)</summary>
        private void DrawReplyStrip(Graphics g)
        {
            if (_replyStrip == null) return;
            // Step 3.4: logical px at the window's DPI; the glyph bitmap is rendered at its Px size; the font is shared (never dispose).
            int h = _replyStrip.Height;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            int textX = Px(14), gs = Px(22);
            var glyph = EmojiRenderer.GetScaled(_replyEditing ? "✏️" : "↩️", gs);
            if (glyph != null) { g.DrawImage(glyph, Px(12), (h - gs) / 2, gs, gs); textX = Px(42); }
            int rightPad = (_replyCancelBtn != null ? _replyCancelBtn.Width : Px(44)) + Px(8);
            var rect = new Rectangle(textX, 0, Math.Max(0, _replyStrip.Width - textX - rightPad), h);
            Color fg = _dark ? Color.FromArgb(220, 220, 220) : Color.FromArgb(40, 40, 40);
            string text = _replyPreview ?? "";
            EmojiRenderer.DrawLine(g, text, FontHelper.ForAt(text, 9.5f, WindowDpiValue), fg, rect);
        }

        // Drawer row builders: each action closes the drawer first, then runs deferred.
        private DrawerMenu.Row Row(string glyph, string label, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, Action = Wrap(action) };
        private DrawerMenu.Row Danger(string glyph, string label, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, IsDanger = true, Action = Wrap(action) };
        private DrawerMenu.Row Toggle(string glyph, string label, Func<bool> isOn, Action action)
            => new DrawerMenu.Row { Glyph = glyph, Label = label, IsToggle = true, IsOn = isOn, Action = Wrap(action) };
        private static DrawerMenu.Row Sep() => new DrawerMenu.Row { Separator = true };

        // Defer to the next message-loop tick so the drawer isn't removed/disposed mid mouse-event.
        private Action Wrap(Action action) => () => BeginInvoke((Action)(() => { CloseDrawer(); action?.Invoke(); }));

        // ── Contacts / New Group / New Channel (shared people picker; all network ops bounded in the service) ──

        private async void OpenContacts()
        {
            using (var f = new PeoplePickerForm(() => _service.GetContactsAsync(), false, _dark, _accent, "Contacts", GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.SelectedUser == null) return;
                var u = f.SelectedUser;
                System.Diagnostics.Debug.WriteLine("[PEOPLE] contacts → open chat with " + u.id);
                await OpenChat(new ChatEntry { Peer = u.ToInputPeer(), PeerId = u.id, Title = DisplayName(u), IsGroup = false }, 0);
            }
        }

        private async void NewGroup()
        {
            using (var f = new CreateChatForm(CreateChatForm.Kind.Group, () => _service.GetContactsAsync(), _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Channel ch;
                try { ch = await _service.CreateSupergroupAsync(f.ChatTitle, ""); }
                catch (Exception ex) { ThemedDialog.Show(this, "New Group", "Couldn't create the group:\n" + ex.Message, "OK"); return; }
                if (ch == null) { ThemedDialog.Show(this, "New Group", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
                if (f.Members.Count > 0) await _service.InviteToChannelAsync(ch, f.Members);
                System.Diagnostics.Debug.WriteLine("[PEOPLE] open new supergroup id=" + ch.id);
                await OpenChat(new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.title, IsGroup = true }, 0);
            }
        }

        private async void NewChannel()
        {
            using (var f = new CreateChatForm(CreateChatForm.Kind.Channel, () => _service.GetContactsAsync(), _dark, _accent, GetCachedAvatar, GetAvatarBoundedAsync))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Channel ch;
                try { ch = await _service.CreateBroadcastAsync(f.ChatTitle, f.ChatAbout); }
                catch (Exception ex) { ThemedDialog.Show(this, "New Channel", "Couldn't create the channel:\n" + ex.Message, "OK"); return; }
                if (ch == null) { ThemedDialog.Show(this, "New Channel", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
                if (f.Members.Count > 0) await _service.InviteToChannelAsync(ch, f.Members);
                System.Diagnostics.Debug.WriteLine("[PEOPLE] open new channel id=" + ch.id);
                await OpenChat(new ChatEntry { Peer = ch.ToInputPeer(), PeerId = ch.id, Title = ch.title, IsGroup = false }, 0);
            }
        }

        private async void OpenSavedMessages()
        {
            var me = _service.Me;
            if (me == null) return;
            var entry = new ChatEntry
            {
                Peer = new InputPeerUser(me.id, me.access_hash),
                PeerId = me.id,
                Title = "Saved Messages",
                PeerInfo = me
            };
            await OpenChat(entry, 0);
        }


        private void AddMenuItem(ContextMenuStrip menu, string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => BeginInvoke(action);
            menu.Items.Add(item);
        }

        // ── Owner-painted left drawer (account header + menu + Night Mode toggle) ──
        // Step 3.4: every length below is LOGICAL (96-DPI) and drawn through P at the host window's DPI, given at
        // construction (the drawer is built per open); fonts are shared FontCache fonts (never dispose). IDpiAware covers a
        // DPI change while it is open: it SETS the width from CardW (DpiScaler already ratio-scaled the bounds).
        private sealed class DrawerMenu : Control, IDpiAware
        {
            public sealed class Row
            {
                public string Glyph, Label;
                public Action Action;
                public bool IsDanger, IsToggle, Separator;
                public Func<bool> IsOn;
                // Account-switcher rows (the OTHER accounts; tap to switch): an avatar or a letter from AvatarKey.
                public bool IsAccount;
                public Image Avatar; public long AvatarKey;
            }

            public const int CardW = 300;              // logical; exposed so ShowDrawer sizes the (narrow) drawer to JUST the card
            private const int HeaderH = 92, RowH = 46, SepH = 11;   // logical

            private int _dpi;                          // the host window's DPI
            private int P(int v) { return Dpi.Px(v, _dpi); }
            private int CardPx { get { return P(CardW); } }
            private int _slop = 6;                     // drag threshold, PHYSICAL px (set at press from the monitor's raw DPI)

            private readonly Bitmap _snap;
            private readonly bool _dark;
            private readonly Color _accent;
            private readonly string _name, _letter;
            private readonly Image _avatar;
            private readonly long _avatarKey;
            private readonly List<Row> _rows;
            private readonly Action _headerAction;
            private readonly Rectangle[] _rects;
            private int _hover = -2;           // -2 none, -1 header, >=0 row index
            private Rectangle _headerRect;
            private int _scrollY, _contentH;   // rows scroll under the fixed header (many accounts overflow the screen)
            private bool _pressed, _moved; private int _pressY, _pressScroll;

            public event Action CloseRequested;

            public DrawerMenu(Bitmap snap, bool dark, Color accent, string name, string letter,
                              Image avatar, long avatarKey, List<Row> rows, Action headerAction, int dpi)
            {
                _dpi = dpi;   // first: Layout2 below reads it
                _snap = snap; _dark = dark; _accent = accent; _name = name; _letter = letter;
                _avatar = avatar; _avatarKey = avatarKey; _rows = rows; _headerAction = headerAction;
                _rects = new Rectangle[rows.Count];
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                TabStop = false;
                Layout2();
            }

            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Layout2(); Invalidate(); }

            void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
            {
                if (oldDpi > 0) _scrollY = _scrollY * newDpi / oldDpi;   // keep the same rows in view (Layout2 clamps)
                _dpi = newDpi;
                _hover = -2;
                Width = CardPx;   // SET from the logical value (OnSizeChanged re-lays out when it differs)
                Layout2();
                Invalidate();
            }

            private void Layout2()
            {
                int headerH = P(HeaderH), cardW = CardPx;
                _headerRect = new Rectangle(0, 0, cardW, headerH);
                int y = headerH + P(6);
                for (int i = 0; i < _rows.Count; i++)
                {
                    int h = _rows[i].Separator ? P(SepH) : P(RowH);
                    _rects[i] = new Rectangle(0, y, cardW, h);
                    y += h;
                }
                _contentH = y + P(6);
                _scrollY = Math.Min(_scrollY, MaxScroll());
            }

            private int MaxScroll() { return Math.Max(0, _contentH - Height); }
            private int ClampScroll(int v) { return Math.Max(0, Math.Min(v, MaxScroll())); }

            protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { CloseRequested?.Invoke(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_pressed)   // touch/mouse drag-scroll over the rows
                {
                    if (Math.Abs(e.Y - _pressY) > _slop) _moved = true;
                    if (_moved) { _scrollY = ClampScroll(_pressScroll - (e.Y - _pressY)); Invalidate(); }
                    return;
                }
                int h = HitTest(e.Location);
                if (h != _hover) { _hover = h; Cursor = h != -2 ? Cursors.Hand : Cursors.Default; Invalidate(new Rectangle(0, 0, CardPx, Height)); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X > CardPx) { CloseRequested?.Invoke(); return; }
                // Finger slop is PHYSICAL: the monitor's raw DPI, never the interface scale (6 at 96, as before).
                var host = TopLevelControl as AppForm;
                _slop = Dpi.Px(6, host != null ? host.MonitorDpiValue : Dpi.SystemDpi);
                _pressed = true; _moved = false; _pressY = e.Y; _pressScroll = _scrollY;   // action fires on MouseUp if not dragged
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                bool wasPressed = _pressed, moved = _moved;
                _pressed = false; _moved = false;
                if (!wasPressed || moved || e.X > CardPx) return;   // a drag-scroll, not a tap
                int h = HitTest(e.Location);
                if (h == -1) { _headerAction?.Invoke(); return; }
                if (h >= 0 && !_rows[h].Separator) _rows[h].Action?.Invoke();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                int ns = ClampScroll(_scrollY - Math.Sign(e.Delta) * P(RowH));
                if (ns != _scrollY) { _scrollY = ns; _hover = -2; Invalidate(); }
            }

            private int HitTest(Point p)
            {
                if (p.X > CardPx) return -2;
                if (p.Y < P(HeaderH)) return _headerRect.Contains(p) ? -1 : -2;   // header is fixed (not scrolled)
                int py = p.Y + _scrollY;   // rows scroll
                for (int i = 0; i < _rows.Count; i++)
                    if (!_rows[i].Separator && _rects[i].Contains(new Point(p.X, py))) return i;
                return -2;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int dpi = _dpi, cardW = CardPx, headerH = P(HeaderH);
                // HAMBURGER-SCRIM-FIX: no backdrop. The drawer is card-width now, so there's no outside region to
                // dim — the opaque card fill below IS the whole control. (Old full-window snapshot/scrim removed.)

                // Card.
                using (var cb = new SolidBrush(_dark ? Color.FromArgb(36, 36, 39) : Color.FromArgb(250, 250, 252)))
                    g.FillRectangle(cb, 0, 0, cardW, Height);

                // Header: avatar + name + subtitle. (Fonts below are shared FontCache fonts: never dispose.)
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var avr = new Rectangle(P(20), P(22), P(52), P(52));
                if (_avatar != null)
                {
                    using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(avr); g.SetClip(clip); g.DrawImage(_avatar, avr); g.ResetClip(); }
                }
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(_avatarKey))) g.FillEllipse(b, avr);
                    DrawHelper.DrawInitial(g, _letter, FontCache.Get("Segoe UI", 18f, FontStyle.Bold, dpi), avr, Color.White);   // ink-centred (owner report 2026-10-14)
                }
                if (_hover == -1)
                    using (var hb = new SolidBrush(Color.FromArgb(_dark ? 22 : 18, _accent))) g.FillRectangle(hb, _headerRect);
                TextRenderer.DrawText(g, _name, FontHelper.UiAt(11.5f, dpi), new Rectangle(P(86), P(30), P(CardW - 96), P(24)),
                    _dark ? Color.White : Color.FromArgb(25, 25, 28), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "View profile", FontHelper.UiAt(8.5f, dpi), new Rectangle(P(86), P(54), P(CardW - 96), P(18)),
                    _dark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(135, 135, 140), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                // Rows (scroll under the fixed header).
                var rowsClip = g.Clip;
                g.SetClip(new Rectangle(0, headerH, cardW, Math.Max(0, Height - headerH)));
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    var r = new Rectangle(_rects[i].X, _rects[i].Y - _scrollY, _rects[i].Width, _rects[i].Height);
                    if (r.Bottom < headerH || r.Y > Height) continue;   // scrolled offscreen
                    if (row.Separator)
                    {
                        using (var p = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(232, 232, 236)))   // 1 px hairline: not scaled
                            g.DrawLine(p, P(16), r.Y + r.Height / 2, cardW - P(16), r.Y + r.Height / 2);
                        continue;
                    }
                    if (_hover == i)
                        using (var hb = new SolidBrush(_dark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(237, 240, 244)))
                            g.FillRectangle(hb, r);

                    if (row.IsAccount)
                    {
                        int accD = P(30);
                        var accAvr = new Rectangle(P(16), r.Y + (r.Height - accD) / 2, accD, accD);
                        if (row.Avatar != null)
                        {
                            using (var clip = new System.Drawing.Drawing2D.GraphicsPath()) { clip.AddEllipse(accAvr); var pc = g.Clip; g.SetClip(clip); g.DrawImage(row.Avatar, accAvr); g.Clip = pc; pc.Dispose(); }
                        }
                        else
                        {
                            using (var b = new SolidBrush(DrawHelper.AvatarColor(row.AvatarKey))) g.FillEllipse(b, accAvr);
                            string ltr = !string.IsNullOrEmpty(row.Label) ? DrawHelper.Initial(row.Label) : "?";
                            DrawHelper.DrawInitial(g, ltr, FontHelper.ForAt(ltr, 11f, dpi, FontStyle.Bold), accAvr, Color.White);   // ink-centred (owner report 2026-10-14)
                        }
                        TextRenderer.DrawText(g, row.Label, FontHelper.UiAt(10.5f, dpi), new Rectangle(P(56), r.Y, P(CardW - 60), r.Height),
                            _dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(35, 35, 38),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        continue;
                    }

                    Color danger = Color.FromArgb(222, 74, 74);
                    Color glyphC = row.IsDanger ? danger : _accent;
                    var gr = new Rectangle(P(16), r.Y, P(30), r.Height);
                    // Render the glyph via the Noto emoji pack (consistent + crisp on RT). The old font-char
                    // path substituted an UGLY system emoji glyph on RT (like the hamburger did). Fall back to
                    // the font glyph only if Noto lacks this emoji.
                    var glyphImg = EmojiRenderer.Get(row.Glyph);
                    if (glyphImg != null)
                    {
                        int gsz = P(22);
                        var oldIM = g.InterpolationMode;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(glyphImg, new Rectangle(gr.X + (gr.Width - gsz) / 2, gr.Y + (gr.Height - gsz) / 2, gsz, gsz));
                        g.InterpolationMode = oldIM;
                    }
                    else
                        TextRenderer.DrawText(g, row.Glyph, FontHelper.UiAt(12f, dpi), gr, glyphC, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    Color lc = row.IsDanger ? danger : (_dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(35, 35, 38));
                    TextRenderer.DrawText(g, row.Label, FontHelper.UiAt(10.5f, dpi), new Rectangle(P(56), r.Y, P(CardW - 110), r.Height), lc,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                    if (row.IsToggle)
                    {
                        int tw = P(40), th = P(20);
                        DrawToggle(g, new Rectangle(P(CardW - 64), r.Y + (r.Height - th) / 2, tw, th), row.IsOn != null && row.IsOn());
                    }
                }
                g.Clip = rowsClip; rowsClip.Dispose();

                int maxS = MaxScroll();
                if (maxS > 0 && _contentH > 0)   // thin scroll indicator
                {
                    int track = Math.Max(1, Height - headerH);
                    int thumbH = Math.Max(P(30), track * track / _contentH);
                    int thumbY = headerH + (int)((track - thumbH) * (_scrollY / (float)maxS));
                    using (var tb = new SolidBrush(Color.FromArgb(110, _dark ? Color.White : Color.Black)))
                        g.FillRectangle(tb, cardW - P(5), thumbY, P(3), thumbH);
                }
            }

            private void DrawToggle(Graphics g, Rectangle r, bool on)
            {
                using (var track = new SolidBrush(on ? _accent : (_dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(200, 200, 206))))
                using (var p = DrawHelper.RoundedRect(r, r.Height / 2))
                    g.FillPath(track, p);
                int inset = P(3), d = r.Height - 2 * inset;   // knob inset (3 logical) on every side; d = 14 at 96, as before
                int kx = on ? r.Right - d - inset : r.X + inset;
                using (var kb = new SolidBrush(Color.White))
                    g.FillEllipse(kb, kx, r.Y + inset, d, d);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_snap != null) { try { _snap.Dispose(); } catch { } }
                    foreach (var row in _rows) if (row.Avatar != null) { try { row.Avatar.Dispose(); } catch { } }
                }
                base.Dispose(disposing);
            }
        }

    }
}
