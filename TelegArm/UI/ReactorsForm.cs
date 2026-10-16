using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T9 — "who reacted": every reaction on a group message with the person who left it, newest first
    /// (messages.getMessageReactionsList), paged as the list reaches its end. Opened from the message menu.
    /// A DialogForm like the Forward picker: themed chrome, a ThemedListBox, rows drawn here.
    /// </summary>
    public sealed class ReactorsForm : DialogForm
    {
        private const int RowLogical = 48;

        private sealed class Row { public string Reaction; public string Name; public long PeerId; }

        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly int _msgId;
        private readonly ThemedListBox _list;
        private readonly List<Row> _rows = new List<Row>();
        private string _nextOffset = "";
        private bool _loading, _done;
        private readonly Label _status;

        public ReactorsForm(TelegramService service, InputPeer peer, int msgId, int total)
            : base("Reactions" + (total > 0 ? " (" + total + ")" : ""), 340, 480)
        {
            _service = service; _peer = peer; _msgId = msgId;
            var p = ThemeHelper.Palette;
            var host = new Panel { BackColor = p.Window };
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window };
            _list.DrawRow += DrawRow;
            _list.ReachedEnd += () => { var _ = LoadPage(); };
            _status = new Label { Dock = DockStyle.Bottom, Height = Px(28), TextAlign = ContentAlignment.MiddleCenter, ForeColor = p.TextDim, BackColor = p.Window, Text = "Loading…" };
            host.Controls.Add(_list);
            host.Controls.Add(_status);
            UseOwnBody(host);
            CancelButton = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
            Shown += (s, e) => { var _ = LoadPage(); };
        }

        private async System.Threading.Tasks.Task LoadPage()
        {
            if (_loading || _done) return;
            _loading = true;
            try
            {
                var r = await _service.GetReactionsListAsync(_peer, _msgId, 50, _nextOffset);
                if (IsDisposed) return;
                foreach (var pr in r.reactions)
                    _rows.Add(new Row { Reaction = ReactionText(pr.reaction), Name = NameOf(pr.peer_id, r), PeerId = pr.peer_id != null ? pr.peer_id.ID : 0 });
                _nextOffset = r.next_offset;
                _done = string.IsNullOrEmpty(_nextOffset);
                _list.SetItems(_rows.Count);
                _status.Visible = _rows.Count == 0;
                if (_rows.Count == 0) _status.Text = "No one to show.";
            }
            catch (Exception ex)
            {
                _done = true;
                if (!IsDisposed) { _status.Visible = true; _status.Text = "Couldn't load the reactions."; }
                Logger.Diag("[REACTIONS] list failed: " + ex.Message);
            }
            finally { _loading = false; }
        }

        private static string ReactionText(Reaction r)
        {
            var e = r as ReactionEmoji;
            if (e != null) return e.emoticon;
            return "⭐";   // a custom emoji or a paid reaction: a star stands in
        }

        private static string NameOf(Peer peer, Messages_MessageReactionsList r)
        {
            if (peer == null) return "Someone";
            IPeerInfo info = null;
            User pu; ChatBase pc;
            if (peer is PeerUser && r.users != null && r.users.TryGetValue(peer.ID, out pu)) info = pu;
            else if (r.chats != null && r.chats.TryGetValue(peer.ID, out pc)) info = pc;
            var u = info as User;
            if (u != null)
            {
                string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
                return n.Length > 0 ? n : (u.username ?? "User");
            }
            var c = info as ChatBase;
            return c != null ? c.Title : "Someone";
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _rows.Count) return;
            var row = _rows[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(p.Window)) g.FillRectangle(bb, r);

            int d = Px(32), ax = r.Left + Px(12), ay = r.Top + (r.Height - d) / 2;
            var avatarRect = new Rectangle(ax, ay, d, d);
            using (var ab = new SolidBrush(DrawHelper.AvatarColor(row.PeerId))) g.FillEllipse(ab, avatarRect);
            string initial = string.IsNullOrEmpty(row.Name) ? "?" : DrawHelper.Initial(row.Name);
            DrawHelper.DrawInitial(g, initial, FontHelper.ForAt(initial, 12f, dpi, FontStyle.Bold), avatarRect, Color.White);

            int tx = ax + d + Px(12), tw = r.Right - tx - Px(48), th = Px(22);
            TextRenderer.DrawText(g, row.Name ?? "", TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top + (r.Height - th) / 2, tw, th),
                p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            var em = EmojiRenderer.Get(row.Reaction);   // the renderer's own image: drawn, never kept or disposed
            int es = Px(22);
            var er = new Rectangle(r.Right - Px(14) - es, r.Top + (r.Height - es) / 2, es, es);
            if (em != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(em, er); }
            else TextRenderer.DrawText(g, row.Reaction ?? "", FontHelper.UiAt(12f, dpi), er, p.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_list == null) return;
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_rows.Count);
            _status.Height = Px(28);
        }
    }
}
