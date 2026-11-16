using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T9/T16 — a paged list of people, each with something on the right: who reacted (the reaction) and who
    /// has seen a message (when). The caller supplies the pages; this draws them in the Forward picker's row style
    /// (DialogForm + ThemedListBox) and asks for the next page when the list reaches its end.
    /// </summary>
    public sealed class PeopleListForm : DialogForm
    {
        private const int RowLogical = 48;

        public sealed class Row
        {
            public string Name;
            public long PeerId;
            public string Trailing;        // the reaction (an emoji) or a time
            public bool TrailingIsEmoji;
        }

        public sealed class Page
        {
            public readonly List<Row> Rows = new List<Row>();
            public string NextOffset;      // null/empty = this was the last page
        }

        private readonly Func<string, Task<Page>> _load;
        private readonly string _empty;
        private readonly ThemedListBox _list;
        private readonly List<Row> _rows = new List<Row>();
        private readonly Label _status;
        private string _nextOffset = "";
        private bool _loading, _done;

        public PeopleListForm(string title, Func<string, Task<Page>> load, string empty = "No one to show.")
            : base(title, 340, 480)
        {
            _load = load; _empty = empty;
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

        private async Task LoadPage()
        {
            if (_loading || _done || _load == null) return;
            _loading = true;
            try
            {
                var page = await _load(_nextOffset);
                if (IsDisposed) return;
                if (page != null) _rows.AddRange(page.Rows);
                _nextOffset = page != null ? page.NextOffset : null;
                _done = string.IsNullOrEmpty(_nextOffset);
                _list.SetItems(_rows.Count, true);   // a page more: keep the place
                _status.Visible = _rows.Count == 0;
                if (_rows.Count == 0) _status.Text = _empty;
            }
            catch (Exception ex)
            {
                _done = true;
                if (!IsDisposed) { _status.Visible = true; _status.Text = "Couldn't load the list."; }
                Logger.Diag("[PEOPLELIST] " + Text + " failed: " + ex.Message);
            }
            finally { _loading = false; }
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

            int trailW = row.TrailingIsEmoji ? Px(22) : Px(90);
            int tx = ax + d + Px(12), tw = r.Right - tx - trailW - Px(24), th = Px(22);
            TextRenderer.DrawText(g, row.Name ?? "", TextRoles.Font(TextRole.Section, dpi), new Rectangle(tx, r.Top + (r.Height - th) / 2, tw, th),
                p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (string.IsNullOrEmpty(row.Trailing)) return;
            var tr = new Rectangle(r.Right - Px(14) - trailW, r.Top + (r.Height - Px(22)) / 2, trailW, Px(22));
            var em = row.TrailingIsEmoji ? EmojiRenderer.Get(row.Trailing) : null;   // the renderer's own image: never kept
            if (em != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(em, tr); }
            else TextRenderer.DrawText(g, row.Trailing, FontHelper.UiAt(9.5f, dpi), tr, p.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
