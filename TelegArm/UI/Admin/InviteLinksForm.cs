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
    /// list (ThemedListBox), touch-sized rows + a big "Create link" button. Tap a row → Copy / Revoke (revoke
    /// confirms). Every RPC bounded. Expiry / usage-limit options are deferred (reported) — links are created
    /// with defaults (no expiry / unlimited), which covers the common case.
    /// </summary>
    public sealed class InviteLinksForm : Form
    {
        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly bool _dark;
        private readonly Color _accent, _bg, _fg, _sub;

        private readonly ThemedListBox _list;
        private readonly Label _status;
        private readonly List<ChatInviteExported> _invites = new List<ChatInviteExported>();

        public InviteLinksForm(TelegramService service, InputPeer peer, bool dark, Color accent)
        {
            _service = service; _peer = peer; _dark = dark; _accent = accent;
            _bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = dark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(120, 120, 120);
            Color field = dark ? Color.FromArgb(54, 54, 58) : Color.White;

            Text = "Invite links";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(390, 520 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Invite links", accent, dark);

            var create = new RoundedButton { Text = "+  Create invite link", Left = 8, Top = 8, Width = 374, Height = 44, Kind = RoundedButtonKind.Primary, Font = FontHelper.Ui(10.5f, FontStyle.Bold) };
            create.Click += CreateClick;
            content.Controls.Add(create);

            _list = new ThemedListBox(dark, accent) { Left = 8, Top = 60, Width = 374, Height = 452, RowHeight = 60, CanvasBackColor = _bg };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += OnRowClicked;
            content.Controls.Add(_list);

            _status = new Label { Left = 8, Top = 250, Width = 374, Height = 28, ForeColor = _sub, TextAlign = ContentAlignment.MiddleCenter, Text = "Loading…", Font = FontHelper.Ui(10f) };
            content.Controls.Add(_status); _status.BringToFront();

            Shown += async (s, e) => await LoadInvites();
        }

        private async Task LoadInvites()
        {
            _status.Visible = true; _status.Text = "Loading…"; _list.Visible = false;
            Messages_ExportedChatInvites res;
            try { res = await _service.GetInvitesAsync(_peer, false); }
            catch (Exception ex) { _status.Text = "Couldn't load: " + ex.Message; return; }
            if (res == null) { _status.Text = "Couldn't load — make sure your VPN is on."; return; }
            _invites.Clear();
            if (res.invites != null)
                foreach (var inv in res.invites) { var ci = inv as ChatInviteExported; if (ci != null) _invites.Add(ci); }
            if (_invites.Count == 0) { _status.Text = "No invite links yet."; return; }
            _status.Visible = false; _list.Visible = true; _list.SetItems(_invites.Count);
        }

        private async void CreateClick(object sender, EventArgs e)
        {
            ChatInviteExported inv;
            try { inv = await _service.ExportInviteAsync(_peer, null, null); }
            catch (Exception ex) { ThemedDialog.Show(this, "Invite links", "Couldn't create: " + ex.Message, "OK"); return; }
            if (inv == null) { ThemedDialog.Show(this, "Invite links", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; }
            _invites.Insert(0, inv);
            _status.Visible = false; _list.Visible = true; _list.SetItems(_invites.Count);
            System.Diagnostics.Debug.WriteLine("[ADMIN] invite created " + inv.link);
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _invites.Count) return;
            var inv = _invites[index];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(_bg)) g.FillRectangle(bb, r);

            int tx = r.Left + 12, tw = r.Right - tx - 12;
            TextRenderer.DrawText(g, inv.link ?? "", FontHelper.Ui(10f, FontStyle.Bold), new Rectangle(tx, r.Top + 10, tw, 20), _fg,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            string meta;
            if ((inv.flags & ChatInviteExported.Flags.revoked) != 0) meta = "revoked";
            else
            {
                meta = inv.usage + " joined";
                if ((inv.flags & ChatInviteExported.Flags.has_usage_limit) != 0) meta += " / " + inv.usage_limit;
                if ((inv.flags & ChatInviteExported.Flags.permanent) != 0) meta = "primary · " + meta;
            }
            TextRenderer.DrawText(g, meta, FontHelper.Ui(8.5f), new Rectangle(tx, r.Top + 32, tw, 18), _sub,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (var sep = new Pen(_dark ? Color.FromArgb(54, 54, 58) : Color.FromArgb(230, 230, 232)))
                g.DrawLine(sep, r.Left + 10, r.Bottom - 1, r.Right - 10, r.Bottom - 1);
        }

        private void OnRowClicked(int i)
        {
            if (i < 0 || i >= _invites.Count) return;
            var inv = _invites[i];
            var menu = new ThemedContextMenuStrip();
            var copy = new ToolStripMenuItem("Copy link") { Font = FontHelper.Ui(10.5f), Padding = new Padding(2, 6, 2, 6) };
            copy.Click += (s, e) => { try { Clipboard.SetText(inv.link ?? ""); } catch { } };
            menu.Items.Add(copy);
            if ((inv.flags & ChatInviteExported.Flags.revoked) == 0)
            {
                var revoke = new ToolStripMenuItem("Revoke link") { Font = FontHelper.Ui(10.5f), Padding = new Padding(2, 6, 2, 6) };
                revoke.Click += (s, e) => RevokeFlow(inv);
                menu.Items.Add(revoke);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(Cursor.Position);
        }

        private async void RevokeFlow(ChatInviteExported inv)
        {
            if (ThemedDialog.Show(this, "Revoke link", "Revoke this invite link? It will stop working.", "Revoke", "Cancel") != 0) return;
            try { if (!await _service.RevokeInviteAsync(_peer, inv.link)) { ThemedDialog.Show(this, "Invite links", "Couldn't reach Telegram — make sure your VPN is on.", "OK"); return; } }
            catch (Exception ex) { ThemedDialog.Show(this, "Invite links", "Couldn't revoke: " + ex.Message, "OK"); return; }
            _invites.Remove(inv);
            _list.SetItems(_invites.Count);
            if (_invites.Count == 0) { _status.Visible = true; _status.Text = "No invite links yet."; _list.Visible = false; }
        }
    }
}
