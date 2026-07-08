using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// "Who voted for X" — the public-poll voter list (Messages_GetPollVotes → Messages_VotesList),
    /// paged via next_offset. Themed; resolves voter names from the returned users/chats dictionaries.
    /// </summary>
    public sealed class PollVotersForm : Form
    {
        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly int _msgId;
        private readonly string _option;
        private readonly Color _bg, _fg, _sub, _accent;

        private readonly Panel _list;
        private readonly Button _moreBtn;
        private string _offset = null;
        private bool _loading;
        private int _y = 4;

        public PollVotersForm(TelegramService service, InputPeer peer, int msgId, string option, string optionText, bool dark, Color accent)
        {
            _service = service; _peer = peer; _msgId = msgId; _option = option; _accent = accent;
            _bg = dark ? Color.FromArgb(36, 36, 38) : Color.FromArgb(245, 245, 247);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _sub = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);

            Text = "Voted for: " + (optionText ?? "");
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = _bg; ForeColor = _fg; Font = FontHelper.Ui(9.5f);
            ClientSize = new Size(320, 420);

            _list = new Panel { Left = 0, Top = 0, Width = ClientSize.Width, Height = 372, BackColor = _bg, AutoScroll = true };
            Controls.Add(_list);
            TelegArm.UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan the voters list (RT touch)

            _moreBtn = new TelegArm.UI.Controls.RoundedButton { Text = "Load more", Left = 8, Top = 380, Width = 140, Height = 30, Kind = TelegArm.UI.Controls.RoundedButtonKind.Secondary, Visible = false };
            _moreBtn.Click += (s, e) => LoadPage();
            var close = new TelegArm.UI.Controls.RoundedButton { Text = "Close", Left = ClientSize.Width - 108, Top = 380, Width = 100, Height = 30, Kind = TelegArm.UI.Controls.RoundedButtonKind.Secondary, DialogResult = DialogResult.Cancel };
            Controls.Add(_moreBtn); Controls.Add(close);
            CancelButton = close;

            Shown += (s, e) => LoadPage();
        }

        private async void LoadPage()
        {
            if (_loading) return;
            _loading = true; _moreBtn.Enabled = false;
            try
            {
                var res = await _service.GetPollVotesAsync(_peer, _msgId, _option, _offset);
                if (res != null && res.votes != null)
                {
                    foreach (var v in res.votes)
                    {
                        var pf = v.GetType().GetField("peer");
                        var peer = pf != null ? pf.GetValue(v) as Peer : null;
                        AddRow(NameFor(peer, res));
                    }
                    _offset = res.next_offset;
                    _moreBtn.Visible = !string.IsNullOrEmpty(_offset);
                    if (_list.Controls.Count == 0) AddRow("No voters yet");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[POLL] voters err: " + ex.Message); }
            finally { _loading = false; _moreBtn.Enabled = true; }
        }

        private string NameFor(Peer peer, Messages_VotesList res)
        {
            if (peer == null) return "Unknown";
            long id = peer.ID;
            User u;
            if (res.users != null && res.users.TryGetValue(id, out u) && u != null)
            {
                string n = ((u.first_name ?? "") + " " + (u.last_name ?? "")).Trim();
                if (n.Length > 0) return n;
                if (!string.IsNullOrEmpty(u.username)) return "@" + u.username;
            }
            ChatBase c;
            if (res.chats != null && res.chats.TryGetValue(id, out c) && c != null && !string.IsNullOrEmpty(c.Title))
                return c.Title;
            return "User " + id;
        }

        private void AddRow(string name)
        {
            var lbl = new Label { Left = 12, Top = _y, Width = _list.ClientSize.Width - 24, Height = 28, ForeColor = _fg, Text = name, TextAlign = ContentAlignment.MiddleLeft };
            _list.Controls.Add(lbl);
            _y += 30;
        }
    }
}
