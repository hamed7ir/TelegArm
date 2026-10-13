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
    /// TIER 3 admin: view / create / revoke a group/channel's invite links. Themed chrome, RT-dark-scrollbar
    /// list (ThemedListBox), touch-sized rows + a big "Create invite link" button. Tap a row → Copy / Revoke (revoke
    /// confirms). Every RPC bounded. Expiry / usage-limit options are deferred (reported) — links are created
    /// with defaults (no expiry / unlimited), which covers the common case.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The list is the dialog's own
    /// body (<c>UseOwnBody</c>: it scrolls by itself, themed bar and touch pan included); its loading / failed / empty
    /// messages are an <see cref="EmptyState"/>; "Create invite link" and "Close" are the footer. The service calls, the
    /// row actions and every message are the 1.x ones. <paramref name="dark"/> and <paramref name="accent"/> are kept for
    /// the callers and no longer read: the palette follows the theme live.
    /// </summary>
    public sealed class InviteLinksForm : DialogForm
    {
        private const int RowLogical = 60;   // one row, logical px — the list takes it at the window's DPI

        private readonly TelegramService _service;
        private readonly InputPeer _peer;

        private readonly Panel _host;
        private readonly ThemedListBox _list;
        private readonly EmptyState _status;
        private readonly List<ChatInviteExported> _invites = new List<ChatInviteExported>();
        private readonly bool _built;   // ApplyDialogTheme is first called from the base constructor, before the controls exist

        public InviteLinksForm(TelegramService service, InputPeer peer, bool dark, Color accent) : base("Invite links", 390, 580)
        {
            _service = service; _peer = peer;
            var p = ThemeHelper.Palette;

            // Owner-drawn list with a themed scrollbar that's dark on RT 8.1 too. Hidden while the status shows (as in 1.x).
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window, Visible = false };
            _list.AccessibleName = "Invite links";
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;

            _status = new EmptyState { Dock = DockStyle.Fill, Hint = "Loading…" };

            _host = new Panel { BackColor = p.Window };
            _host.Controls.Add(_list);      // the two Fill children share the space (one is visible at a time)
            _host.Controls.Add(_status);
            UseOwnBody(_host);

            // AcceptButton stays null on purpose: a stray Enter must not mint an invite link. Esc closes.
            var create = AddFooterButton("Create invite link", RoundedButtonKind.Primary, DialogResult.None, 168);   // first added = rightmost
            create.Click += CreateClick;
            var close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
            CancelButton = close;
            close.TabIndex = 0; create.TabIndex = 1;   // the footer flows right-to-left; Tab follows the screen

            _built = true;
            Shown += async (s, e) => await LoadInvites();
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_built) return;
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_invites.Count);   // the canvas height follows the row height
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (!_built) return;
            var p = ThemeHelper.Palette;
            _host.BackColor = p.Window;
            _list.CanvasBackColor = p.Window;
            _list.Invalidate(true);
            _status.Invalidate();
        }

        /// <summary>The loading / failed / empty message, in place of the list.</summary>
        private void ShowStatus(string text)
        {
            if (IsDisposed) return;
            _status.Hint = text;
            _status.Visible = true;
            _list.Visible = false;
        }

        private void ShowList()
        {
            _status.Visible = false; _list.Visible = true; _list.SetItems(_invites.Count);
        }

        private async Task LoadInvites()
        {
            ShowStatus("Loading…");
            Messages_ExportedChatInvites res;
            try { res = await _service.GetInvitesAsync(_peer, false); }
            catch (Exception ex) { ShowStatus("Couldn't load: " + ex.Message); return; }
            if (IsDisposed) return;   // the dialog was closed while the links were on their way
            if (res == null) { ShowStatus("Couldn't load — make sure your VPN is on."); return; }
            _invites.Clear();
            if (res.invites != null)
                foreach (var inv in res.invites) { var ci = inv as ChatInviteExported; if (ci != null) _invites.Add(ci); }
            if (_invites.Count == 0) { ShowStatus("No invite links yet."); return; }
            ShowList();
        }

        private async void CreateClick(object sender, EventArgs e)
        {
            ChatInviteExported inv;
            try { inv = await _service.ExportInviteAsync(_peer, null, null); }
            catch (Exception ex) { ThemedDialog.Show(this, "Invite links", "Couldn't create: " + ex.Message, "OK"); return; }
            if (inv == null) { ThemedDialog.Show(this, "Invite links", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
            _invites.Insert(0, inv);
            ShowList();
            Logger.Diag("[ADMIN] invite created");   // the link itself is a secret: never logged
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _invites.Count) return;
            var inv = _invites[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(p.Window)) g.FillRectangle(bb, r);

            // Fonts without the text argument = the Roboto face, as in 1.x: the link is ASCII, and the fixed bands below
            // would not hold the taller Vazirmatn line.
            int tx = r.Left + Px(12), tw = r.Right - tx - Px(12);
            TextRenderer.DrawText(g, inv.link ?? "", TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top + Px(10), tw, Px(20)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            string meta;
            if ((inv.flags & ChatInviteExported.Flags.revoked) != 0) meta = "revoked";
            else
            {
                meta = inv.usage + " joined";
                if ((inv.flags & ChatInviteExported.Flags.has_usage_limit) != 0) meta += " / " + inv.usage_limit;
                if ((inv.flags & ChatInviteExported.Flags.permanent) != 0) meta = "primary · " + meta;
            }
            TextRenderer.DrawText(g, meta, TextRoles.Font(TextRole.Field, dpi), new Rectangle(tx, r.Top + Px(32), tw, Px(18)), p.TextDim,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (var sep = new Pen(p.Divider))
                g.DrawLine(sep, r.Left + Px(10), r.Bottom - 1, r.Right - Px(10), r.Bottom - 1);
        }

        private void OnRowClicked(int i)
        {
            if (i < 0 || i >= _invites.Count) return;
            var inv = _invites[i];
            var menu = new ThemedContextMenuStrip();
            menu.Font = FontHelper.UiAt(10.5f, WindowDpiValue);   // shared: Control never disposes its Font. The row padding is the menu's own.
            var copy = new ToolStripMenuItem("Copy link");
            copy.Click += (s, e) => { try { Clipboard.SetText(inv.link ?? ""); } catch { } };
            menu.Items.Add(copy);
            if ((inv.flags & ChatInviteExported.Flags.revoked) == 0)
            {
                var revoke = new ToolStripMenuItem("Revoke link");
                revoke.Click += (s, e) => RevokeFlow(inv);
                menu.Items.Add(revoke);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(_list, _list.PointToClient(Cursor.Position));   // over the list: the menu sizes itself at this window's DPI
        }

        private async void RevokeFlow(ChatInviteExported inv)
        {
            if (ThemedDialog.Show(this, "Revoke link", "Revoke this invite link? It will stop working.", "Revoke", "Cancel") != 0) return;
            try { if (!await _service.RevokeInviteAsync(_peer, inv.link)) { ThemedDialog.Show(this, "Invite links", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; } }
            catch (Exception ex) { ThemedDialog.Show(this, "Invite links", "Couldn't revoke: " + ex.Message, "OK"); return; }
            _invites.Remove(inv);
            _list.SetItems(_invites.Count);
            if (_invites.Count == 0) ShowStatus("No invite links yet.");
        }
    }
}
