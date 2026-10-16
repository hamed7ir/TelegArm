using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public sealed class PollVotersForm : Form, IDpiHost
    {
        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly int _msgId;
        private readonly string _option;
        private readonly Color _bg, _fg, _sub, _accent;

        private readonly Panel _list;
        private readonly Button _moreBtn, _close;
        private string _offset = null;
        private bool _loading;

        /// <summary>Step 3.3 — the EFFECTIVE DPI this dialog lays out at: its owner's (the caller passes Dpi.Of(owner); the
        /// dialog opens centred on it), re-set by WM_DPICHANGED. Its controls read it through Dpi.Of (IDpiHost). Every
        /// length here is LOGICAL (96-DPI) px through P() = Dpi.Px at this DPI, so at 96 nothing moves by a pixel.</summary>
        private int _dpi;
        int IDpiHost.Dpi { get { return _dpi; } }
        private int P(int n) { return Dpi.Px(n, _dpi); }

        /// <param name="dpi">Step 3.3 — the DPI to lay out at: the owner's (Dpi.Of(owner)). 0 = <see cref="Dpi.DefaultDpi"/>.</param>
        public PollVotersForm(TelegramService service, InputPeer peer, int msgId, string option, string optionText, bool dark, Color accent, int dpi = 0)
        {
            _service = service; _peer = peer; _msgId = msgId; _option = option; _accent = accent;
            _dpi = dpi > 0 ? dpi : Dpi.DefaultDpi;
            _bg = ThemeHelper.PaletteFor(dark).DialogBg;
            _fg = ThemeHelper.PaletteFor(dark).DialogText;
            _sub = ThemeHelper.PaletteFor(dark).DialogSub;

            Text = "Voted for: " + (optionText ?? "");
            TelegArm.Helpers.AppIcon.Set(this);   // app icon in the taskbar / Alt-Tab
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = _bg; ForeColor = _fg;

            // Step 3.3: built bare, in the 1.x order; ApplyMetrics SETS the font, the size and every bound at _dpi —
            // before Show (CenterParent centres by the size), and again on WM_DPICHANGED.
            _list = new Panel { BackColor = _bg, AutoScroll = true };
            Controls.Add(_list);
            TelegArm.UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan the voters list (RT touch)

            _moreBtn = new TelegArm.UI.Controls.RoundedButton { Text = "Load more", Kind = TelegArm.UI.Controls.RoundedButtonKind.Secondary, Visible = false };
            _moreBtn.Click += (s, e) => LoadPage();
            _close = new TelegArm.UI.Controls.RoundedButton { Text = "Close", Kind = TelegArm.UI.Controls.RoundedButtonKind.Secondary, DialogResult = DialogResult.Cancel };
            Controls.Add(_moreBtn); Controls.Add(_close);
            CancelButton = _close;

            ApplyMetrics();

            Shown += (s, e) => LoadPage();
        }

        /// <summary>Step 3.3 — SETS the form's font and size and every bound from LOGICAL values at _dpi (never multiplies),
        /// so the first layout and a re-layout after WM_DPICHANGED are one code path. At 96 these are the 1.x numbers
        /// exactly. The font is SHARED (FontCache): never dispose.</summary>
        private void ApplyMetrics()
        {
            Font = FontHelper.UiAt(9.5f, _dpi);   // shared: never dispose (the rows and buttons inherit it)
            ClientSize = new Size(P(320), P(420));

            _list.Bounds = new Rectangle(0, 0, ClientSize.Width, P(372));
            _moreBtn.Bounds = new Rectangle(P(8), P(380), P(140), P(30));
            _close.Bounds = new Rectangle(ClientSize.Width - P(108), P(380), P(100), P(30));

            // The rows already loaded: scrolled home first (PlaceRow works in client coordinates), then re-placed with the
            // list's layout held, so its scrollbar cannot come or go part-way and give the rows two widths.
            _list.AutoScrollPosition = Point.Empty;
            _list.SuspendLayout();
            try { for (int i = 0; i < _list.Controls.Count; i++) PlaceRow(_list.Controls[i], i); }
            finally { _list.ResumeLayout(true); }
        }

        /// <summary>Row <paramref name="index"/> at _dpi: P(4) from the top at a P(30) pitch, P(28) tall, the list's width
        /// less P(24) measured NOW (as in 1.x, rows added after the list grew its scrollbar are that much narrower).</summary>
        private void PlaceRow(Control row, int index)
        {
            row.Bounds = new Rectangle(P(12), P(4) + index * P(30), _list.ClientSize.Width - P(24), P(28));
        }

        /// <summary>Step 3.3 — WM_DPICHANGED (per-monitor only: never sent while the process is system-aware, the build
        /// default). Swallowed — Result 0, no base: WinForms' own handling must not scale this plain Form — then re-laid
        /// out from logical values at the new effective DPI (<see cref="ApplyMetrics"/>), at the top-left Windows suggests
        /// (its rectangle keeps a window dragged across monitors on the one it entered). The loaded rows are kept.</summary>
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WindowDpi.WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                int raw = (int)(m.WParam.ToInt64() & 0xFFFF);
                int nw = raw > 0 ? Dpi.Effective(raw) : 0;
                if (nw > 0 && nw != _dpi && !IsDisposed)
                {
                    int old = _dpi;
                    _dpi = nw;   // first: P() and Dpi.Of(child) must already see it
                    SuspendLayout();
                    try { ApplyMetrics(); }
                    finally { ResumeLayout(true); }
                    if (m.LParam != IntPtr.Zero)
                    {
                        int left = Marshal.ReadInt32(m.LParam), top = Marshal.ReadInt32(m.LParam, 4);   // RECT.left, RECT.top
                        Location = new Point(left, top);
                    }
                    Logger.Diag("[DPI] win=PollVotersForm old=" + old + " new=" + nw + " mon=" + raw + " applied=" + Bounds);
                }
                return;
            }
            base.WndProc(ref m);
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
            var lbl = new Label { ForeColor = _fg, Text = name, TextAlign = ContentAlignment.MiddleLeft };
            PlaceRow(lbl, _list.Controls.Count);   // the list holds only rows: its count is this row's index
            _list.Controls.Add(lbl);
        }
    }
}
