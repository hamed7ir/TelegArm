using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// The admin hub — a themed, SCROLLABLE, touch-sized menu of management sections for a supergroup/channel
    /// the user can administer. Opened from the info screen's "Manage" action. Each row opens the relevant
    /// tier-1/2/3 form. Sections adapt to the peer type (default permissions only for supergroups).
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply + ScrollHost). The menu is a
    /// column of <see cref="MenuRow"/>s in the dialog's scrolling body — a caption and a drawn chevron per section; the
    /// 1.x captions' leading symbol glyphs / emoji (tofu on RT 8.1) are gone with the rounded buttons they sat on. A Close
    /// button sits in the footer; Esc closes. <paramref name="dark"/> and <paramref name="accent"/> are kept for the
    /// caller and handed on to the section forms (still 1.x forms that want them); this form follows the palette live.
    /// </summary>
    public sealed class ManageChatForm : DialogForm
    {
        public ManageChatForm(TelegramService service, Channel channel, InputPeer peer, string title, string about, bool dark, Color accent)
            : base("Manage", 360, 460)
        {
            bool isMega = (channel.flags & Channel.Flags.megagroup) != 0;
            bool isBroadcast = (channel.flags & Channel.Flags.broadcast) != 0;

            var rows = new List<MenuRow>();
            Row(rows, "Edit info", () => Open(new EditChatInfoForm(service, channel, peer, title, about, dark, accent)));
            Row(rows, "Members", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Members, dark, accent)));
            Row(rows, "Administrators", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Admins, dark, accent)));
            if (isMega)
                Row(rows, "Default permissions", async () => await DefaultPermsForm.OpenAsync(this, service, peer, channel.default_banned_rights, dark, accent));
            Row(rows, "Banned users", () => Open(new ChatMembersForm(service, channel, ChatMembersForm.Mode.Banned, dark, accent)));
            Row(rows, "Invite links", () => Open(new InviteLinksForm(service, peer, dark, accent)));
            Row(rows, isBroadcast ? "Channel settings" : "Public link", () => Open(new ChannelExtrasForm(service, channel, dark, accent)));

            rows[rows.Count - 1].IsLast = true;   // no hairline under the last row
            PopulateBody(rows.ToArray());

            CancelButton = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
            FitToContent();
        }

        private static void Row(List<MenuRow> rows, string text, Action act)
        {
            var r = new MenuRow(text);
            r.Click += (s, e) => act();
            rows.Add(r);
        }

        private void Open(Form f) { using (f) f.ShowDialog(this); }

        /// <summary>One section of the hub: the caption, a drawn chevron at the right and a hairline under it. The whole row
        /// is the touch target; Enter / Space open it from the keyboard (an accent ring marks the focus). It raises the
        /// ordinary <c>Click</c>.</summary>
        private sealed class MenuRow : SettingRow
        {
            private const int ChevronReach = 20;
            private bool _hot, _last;

            public MenuRow(string caption) : base(caption, true)
            {
                AccessibleRole = AccessibleRole.PushButton;
                Refit();
            }

            /// <summary>True for the final row of the menu: it draws no divider.</summary>
            public bool IsLast { get { return _last; } set { if (_last == value) return; _last = value; Invalidate(); } }

            protected override string AccessibleAction { get { return "Open"; } }
            protected override void AccessibleInvoke() { OnClick(EventArgs.Empty); }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            // A tap opens a modal form from inside Click: drop the hover wash first, or a touch tap leaves the row tinted.
            protected override void OnClick(EventArgs e)
            {
                if (_hot) { _hot = false; Invalidate(); }
                base.OnClick(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (Enabled) OnClick(EventArgs.Empty);
                }
            }

            protected override bool IsInputKey(Keys keyData)
            {
                return ((keyData & Keys.KeyCode) == Keys.Enter && (keyData & (Keys.Alt | Keys.Control)) == 0) || base.IsInputKey(keyData);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                PaintBack(g, p);
                if (_hot && Enabled) using (var wash = new SolidBrush(p.Hover)) g.FillRectangle(wash, ClientRectangle);

                // The chevron: two round-capped strokes, not a font arrow.
                float cx = Width - padX - Dpi.F(5f, dpi), cy = Height / 2f, u = Dpi.F(4f, dpi);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Enabled ? p.TextDim : p.TextDisabled, Dpi.F(1.6f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - u / 2, cy - u, cx + u / 2, cy);
                    g.DrawLine(pen, cx + u / 2, cy, cx - u / 2, cy + u);
                }
                g.SmoothingMode = old;

                int right = Width - padX - Dpi.Px(ChevronReach, dpi);
                DrawCaption(g, p, new Rectangle(padX, 0, Math.Max(1, right - padX), Height), dpi);

                if (!_last)
                    using (var pen = new Pen(p.Divider, 1f)) g.DrawLine(pen, padX, Height - 1, Width - padX, Height - 1);

                int inset = Dpi.Px(4, dpi);
                PaintFocusRing(g, p, new Rectangle(inset, inset, Width - 2 * inset, Height - 2 * inset), Dpi.Px(6, dpi), dpi);
            }
        }
    }
}
