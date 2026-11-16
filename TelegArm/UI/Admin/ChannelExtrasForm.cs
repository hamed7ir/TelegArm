using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// TIER 3 admin: a channel/supergroup's public USERNAME (with availability feedback via
    /// Channels_CheckUsername) and, for broadcast channels, the SIGN MESSAGES toggle
    /// (Channels_ToggleSignatures). Themed chrome + RT-dark scrollbar; every RPC bounded. Linked discussion
    /// group is deferred (reported) — wiring it needs a group-picker + Channels_SetDiscussionGroup, out of scope.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply with hand-coloured WinForms
    /// controls; mdfiles/PHASE2-CONVERSION-GUIDE.md). The rows are the kit's: the username a <see cref="TextRow"/>, the
    /// availability line a <see cref="StatusRow"/> (with the Check button beside it), Sign messages a
    /// <see cref="ToggleRow"/>, the discussion group a header and another StatusRow (its Link / Remove button under the
    /// state). "Save link" and "Close" are the footer. What did not change: every service call and its result, the
    /// messages, which controls a given channel gets, and that the toggle and the discussion group act at once while the
    /// link needs Save. <paramref name="dark"/> and <paramref name="accent"/> are kept for the callers and no longer read:
    /// the palette follows the theme live.
    /// </summary>
    public sealed class ChannelExtrasForm : DialogForm
    {
        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly bool _isBroadcast;

        private readonly TextRow _username;
        private readonly StatusRow _avail;
        private readonly RoundedButton _save;
        private ToggleRow _sign;
        private bool _suppress;

        // CHANNEL-LINK-UNLINK: the discussion-group control (broadcast + admin only)
        private StatusRow _discussionState;
        private RoundedButton _discussionBtn;
        private long _linkedChatId;
        private string _linkedName;

        public ChannelExtrasForm(TelegramService service, Channel channel, bool dark, Color accent)
            : base(IsBroadcast(channel) ? "Channel settings" : "Public link", 400, 420)
        {
            _service = service; _channel = channel;
            _isBroadcast = IsBroadcast(channel);

            _username = new TextRow("Public link (t.me/username)", channel.username ?? "");

            var check = new RoundedButton { Text = "Check", Kind = RoundedButtonKind.Secondary, Modern = true };
            check.Click += CheckClick;
            _avail = new StatusRow("Leave empty to make it private.", Tone.Dim, check, 76);

            var rows = new List<Control> { _username, _avail };

            if (_isBroadcast)
            {
                _sign = new ToggleRow("Sign messages with the admin's name", (channel.flags & Channel.Flags.signatures) != 0);
                _sign.Changed += SignChanged;   // after the initial value, so building the row does not call the service
                rows.Add(_sign);
            }

            // CHANNEL-LINK-UNLINK: for a broadcast channel the user administers — link/unlink the discussion group
            // (the admin side of comments). This form is already reached only via the admin-gated "Manage" action;
            // the creator/admin_rights re-check guarantees a non-admin never sees the control.
            if (_isBroadcast && IsChannelAdmin())
            {
                _discussionBtn = new RoundedButton { Text = "…", Kind = RoundedButtonKind.Secondary, Modern = true, Enabled = false };   // a11y-ok: its Text is set once loaded
                _discussionBtn.Click += DiscussionClick;
                _discussionState = new StatusRow("Loading…", Tone.Normal, _discussionBtn, 240, true, 2);
                rows.Add(new SectionHeader("Discussion group (comments)"));
                rows.Add(_discussionState);
            }
            PopulateBody(rows.ToArray());

            // AcceptButton stays null on purpose: Enter in the field must not publish a channel's link. Esc closes.
            _save = AddFooterButton("Save link", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            _save.Click += SaveClick;
            var close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
            CancelButton = close;
            close.TabIndex = 0; _save.TabIndex = 1;   // the footer flows right-to-left; Tab follows the screen
            FocusOnShow(_username.Field.Inner);
            FitToContent();

            if (_discussionState != null)
            {
                var _ld = LoadDiscussionStateAsync();   // fire-and-forget: read the current linked_chat_id → set the UI
            }
        }

        private static bool IsBroadcast(Channel channel)
        {
            return (channel.flags & Channel.Flags.broadcast) != 0;
        }

        private bool IsChannelAdmin()
        {
            return (_channel.flags & Channel.Flags.creator) != 0 || _channel.admin_rights != null;
        }

        /// <summary>Reads the current linked discussion group (ChannelFull.linked_chat_id) and updates the control.</summary>
        private async System.Threading.Tasks.Task LoadDiscussionStateAsync()
        {
            try
            {
                var mcf = await _service.GetChannelFullAsync(_channel);
                var cf = mcf?.full_chat as ChannelFull;
                _linkedChatId = cf?.linked_chat_id ?? 0;
                _linkedName = null;
                if (_linkedChatId != 0 && mcf != null && mcf.chats != null && mcf.chats.TryGetValue(_linkedChatId, out var lc))
                    _linkedName = lc.Title;
                UpdateDiscussionUi();
            }
            catch { if (_discussionState != null) _discussionState.Set("Couldn't load the discussion state.", Tone.Normal); }
        }

        private void UpdateDiscussionUi()
        {
            if (_discussionBtn == null || IsDisposed) return;
            if (_linkedChatId != 0)
            {
                _discussionState.Set("Comments on, linked to \"" + (_linkedName ?? "group") + "\".", Tone.Normal);
                _discussionBtn.Text = "Remove discussion group";
            }
            else
            {
                _discussionState.Set("No discussion group, so comments are off.", Tone.Normal);
                _discussionBtn.Text = "Link a discussion group";
            }
            _discussionBtn.Enabled = true;
        }

        private void DiscussionClick(object sender, EventArgs e)
        {
            if (_linkedChatId != 0) UnlinkFlow(); else LinkFlow();
        }

        /// <summary>Link: fetch the eligible groups → a themed picker → SetDiscussionGroup(channel, picked).</summary>
        private async void LinkFlow()
        {
            _discussionBtn.Enabled = false;
            Messages_Chats groups = null;
            try { groups = await _service.GetGroupsForDiscussionAsync(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't load groups: " + ex.Message, "OK"); }
            _discussionBtn.Enabled = true;
            var eligible = groups != null && groups.chats != null
                ? groups.chats.Values.OfType<Channel>().ToList()
                : new List<Channel>();
            if (eligible.Count == 0)
            {
                ThemedDialog.Show(this, "Discussion group",
                    "You have no eligible groups to link. Create a group you own (or make yourself its admin) first.", "OK");
                return;
            }
            var menu = new ThemedContextMenuStrip();
            menu.FontPt = 10.5f;   // step 3.1: applied at the open point's DPI when the menu opens
            foreach (var g in eligible)
            {
                var grp = g;   // capture per-iteration
                var it = menu.Items.Add(string.IsNullOrEmpty(g.Title) ? "Group" : g.Title);
                it.Click += async (s, e) => await DoLink(grp);
            }
            menu.ShowAt(_discussionBtn, new Point(_discussionBtn.ClientRectangle.Left, _discussionBtn.Height));
        }

        private async System.Threading.Tasks.Task DoLink(Channel group)
        {
            _discussionBtn.Enabled = false;
            try
            {
                bool ok = await _service.SetDiscussionGroupAsync(_channel, group);
                Logger.Diag("[ADMIN] link channel=" + _channel.id + " group=" + group.id + " " + (ok ? "ok" : "fail"));
                if (!ok) { ThemedDialog.Show(this, "Discussion group", "Couldn't link. Check your VPN, and that the group can be linked.", "OK"); _discussionBtn.Enabled = true; return; }
                await LoadDiscussionStateAsync();   // refresh → now shows "linked" + Remove
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't link: " + ex.Message, "OK"); _discussionBtn.Enabled = true; }
        }

        /// <summary>Unlink: confirm (destructive to comments), then SetDiscussionGroup(channel, null) = disable comments.</summary>
        private async void UnlinkFlow()
        {
            if (ThemedDialog.Show(this, "Remove discussion group", "Remove the discussion group? Comments will be disabled.", "Remove", "Cancel") != 0)
                return;
            _discussionBtn.Enabled = false;
            try
            {
                bool ok = await _service.SetDiscussionGroupAsync(_channel, null);   // null → inputChannelEmpty (unlink)
                Logger.Diag("[ADMIN] unlink channel=" + _channel.id + " " + (ok ? "ok" : "fail"));
                if (!ok) { ThemedDialog.Show(this, "Discussion group", "Couldn't remove. Check your VPN and your permissions.", "OK"); _discussionBtn.Enabled = true; return; }
                await LoadDiscussionStateAsync();   // refresh → now shows "Link a discussion group"
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Discussion group", "Couldn't remove: " + ex.Message, "OK"); _discussionBtn.Enabled = true; }
        }

        private async void CheckClick(object sender, EventArgs e)
        {
            string u = _username.Value.Trim();
            if (u.Length == 0) { _avail.Set("Leave empty to make it private.", Tone.Dim); return; }
            _avail.Set("Checking…", Tone.Dim);
            try
            {
                bool free = await _service.CheckUsernameAsync(_channel, u);
                _avail.Set(free ? "Available" : "Taken or invalid", free ? Tone.Ok : Tone.Bad);
            }
            catch (Exception ex) { _avail.Set("Couldn't check: " + ex.Message, Tone.Bad); }
        }

        private async void SaveClick(object sender, EventArgs e)
        {
            _save.Enabled = false;
            try
            {
                bool ok = await _service.UpdateUsernameAsync(_channel, _username.Value.Trim());
                if (!ok) { _avail.Set("Couldn't save. The username is taken, or the VPN is off.", Tone.Bad); return; }
                _avail.Set("Saved", Tone.Ok);
                Logger.Diag("[ADMIN] username updated");
            }
            catch (Exception ex) { ThemedDialog.Show(this, "Public link", "Couldn't save: " + ex.Message, "OK"); }
            finally { _save.Enabled = true; }
        }

        private async void SignChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            bool want = _sign.On;
            try
            {
                if (!await _service.ToggleSignaturesAsync(_channel, want))
                {
                    ThemedDialog.Show(this, "Sign messages", "Couldn't reach Telegram. Make sure your VPN is on.", "OK");
                    _suppress = true; _sign.On = !want; _suppress = false;
                    return;
                }
                Logger.Diag("[ADMIN] signatures=" + want);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Sign messages", "Couldn't change: " + ex.Message, "OK");
                _suppress = true; _sign.On = !want; _suppress = false;
            }
        }

        // ── the status row ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>How a <see cref="StatusRow"/> reads: the normal ink, a dim note, or a result with a drawn mark (a tick in
        /// the Success colour, a cross in the Danger colour — never a font glyph, which is tofu on RT 8.1).</summary>
        private enum Tone { Normal, Dim, Ok, Bad }

        /// <summary>A line of status text that wraps, optionally with one action button: to the RIGHT of the text (the Check
        /// button beside the availability line) or UNDER it (the discussion group's Link / Remove button, which is too
        /// wide to sit beside the state). Its height follows its width, like NoteRow. <see cref="Set"/> replaces the text
        /// and the tone together; a result tone puts a drawn mark in front of the text.</summary>
        private sealed class StatusRow : SettingRow
        {
            private const int PadY = 8, Gap = 12, MarkSize = 14, MarkGap = 8, ButtonH = 34;

            private readonly RoundedButton _button;   // null = none
            private readonly int _buttonWidth;        // logical px
            private readonly bool _below;
            private readonly int _minLines;
            private Tone _tone;

            private struct Geo { public Rectangle Mark, Text; public int Height; }

            public StatusRow(string text, Tone tone, RoundedButton button = null, int buttonLogicalWidth = 0, bool buttonBelow = false, int minLines = 1)
                : base(text, false)
            {
                _tone = tone;
                AccessibleRole = AccessibleRole.StaticText;   // R6: a status line, not a control
                _button = button;
                _buttonWidth = buttonLogicalWidth;
                _below = buttonBelow;
                _minLines = Math.Max(1, minLines);
                AccessibleRole = button == null ? AccessibleRole.StaticText : AccessibleRole.Grouping;
                if (_button != null) { _button.Modern = true; Controls.Add(_button); }
                Refit();
            }

            /// <summary>Replaces the text and the tone. Safe after the dialog was closed (an RPC can come back late).</summary>
            public void Set(string text, Tone tone)
            {
                if (IsDisposed) return;
                _tone = tone;
                Text = text;   // re-measures and repaints when the text changed
                Refit();       // a tone change alone changes the room the text gets (the mark) — and so maybe its height
                Invalidate();
            }

            private bool HasMark { get { return _tone == Tone.Ok || _tone == Tone.Bad; } }

            private TextFormatFlags TextFlags()
            {
                var f = BaseFlags | TextFormatFlags.WordBreak | TextFormatFlags.Top | TextFormatFlags.EndEllipsis;
                return FontHelper.IsPersian(Text) ? f | TextFormatFlags.Right | TextFormatFlags.RightToLeft : f | TextFormatFlags.Left;
            }

            private Geo Measure(int width, int dpi)
            {
                var geo = new Geo();
                int padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi), gap = Dpi.Px(Gap, dpi);
                int avail = Math.Max(1, width - 2 * padX);
                var font = TextRoles.Font(TextRole.Field, dpi, Text);
                int line = LineHeight(font);
                int mark = Dpi.Px(MarkSize, dpi);
                int markW = HasMark ? mark + Dpi.Px(MarkGap, dpi) : 0;
                int bh = Dpi.Px(ButtonH, dpi);
                bool beside = _button != null && !_below;

                int textW = Math.Max(1, avail - markW - (beside ? Math.Min(Dpi.Px(_buttonWidth, dpi), avail) + gap : 0));
                int textH = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, font, new Size(textW, 0), TextFlags()).Height;
                textH = Math.Max(textH, _minLines * line);

                int textTop;
                if (beside)
                {
                    int inner = Math.Max(textH, bh);   // a one-line text is centred on the button
                    textTop = padY + (inner - textH) / 2;
                    geo.Height = 2 * padY + inner;
                }
                else
                {
                    textTop = padY;
                    geo.Height = 2 * padY + textH + (_button != null ? gap + bh : 0);
                }
                geo.Text = new Rectangle(padX + markW, textTop, textW, textH);
                geo.Mark = HasMark ? new Rectangle(padX, textTop + Math.Max(0, (line - mark) / 2), mark, mark) : Rectangle.Empty;
                return geo;
            }

            public override int HeightFor(int width, int dpi) { return Measure(width, dpi).Height; }

            // The button's font is the row's to set, at the window's DPI (an absolute assignment, so it is safe whenever
            // DpiScaler's generic pass has also rescaled it): known once the row is parented, shown, or rescaled.
            private void ApplyButtonFont() { if (_button != null) _button.Font = TextRoles.Font(TextRole.Button, Dpi.Of(this)); }

            protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplyButtonFont(); }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyButtonFont();
                PerformLayout();
            }

            protected override void OnDpiRescaled(int oldDpi, int newDpi) { ApplyButtonFont(); }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (_button == null) return;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
                int bw = Math.Min(Dpi.Px(_buttonWidth, dpi), Math.Max(1, Width - 2 * padX)), bh = Dpi.Px(ButtonH, dpi);
                if (_below) _button.SetBounds(padX, Math.Max(0, Height - padY - bh), bw, bh);
                else _button.SetBounds(Width - padX - bw, Math.Max(0, (Height - bh) / 2), bw, bh);
            }

            private Color InkOf(Palette p)
            {
                switch (_tone)
                {
                    case Tone.Ok: return p.Success;
                    case Tone.Bad: return p.Danger;
                    case Tone.Dim: return p.TextDim;
                    default: return p.Text;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this);
                PaintBack(g, p);
                var geo = Measure(Width, dpi);
                Color ink = InkOf(p);
                if (HasMark) DrawMark(g, geo.Mark, ink, _tone == Tone.Ok, dpi);
                if (Text.Length == 0) return;
                TextRenderer.DrawText(g, Text, TextRoles.Font(TextRole.Field, dpi, Text), geo.Text, ink, TextFlags());
            }

            /// <summary>A tick (<paramref name="ok"/>) or a cross, drawn with round-capped lines inside <paramref name="r"/>.</summary>
            private static void DrawMark(Graphics g, Rectangle r, Color ink, bool ok, int dpi)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(ink, Math.Max(1.5f, Dpi.F(2f, dpi))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (ok)
                        g.DrawLines(pen, new[]
                        {
                            new PointF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.54f),
                            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.80f),
                            new PointF(r.X + r.Width * 0.88f, r.Y + r.Height * 0.22f),
                        });
                    else
                    {
                        g.DrawLine(pen, r.X + r.Width * 0.2f, r.Y + r.Height * 0.2f, r.X + r.Width * 0.8f, r.Y + r.Height * 0.8f);
                        g.DrawLine(pen, r.X + r.Width * 0.8f, r.Y + r.Height * 0.2f, r.X + r.Width * 0.2f, r.Y + r.Height * 0.8f);
                    }
                }
                g.SmoothingMode = old;
            }
        }
    }
}
