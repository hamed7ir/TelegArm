using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed popup listing every pinned message (newest→oldest). A row tap raises
    /// <see cref="JumpRequested"/> (the owner jumps + closes); right-click / long-press on a row raises
    /// <see cref="ContextRequested"/> so the owner can show its themed Jump/Unpin menu. RT-safe: each row
    /// is a single owner-painted control, scrolled by a themed AutoScroll panel.
    /// </summary>
    public sealed class PinnedListForm : Form
    {
        public event Action<int> JumpRequested;
        public event Action<int, Point> ContextRequested;

        private readonly bool _dark;
        private readonly Color _accent;
        private readonly FlowLayoutPanel _list;

        public PinnedListForm(bool dark, Color accent)
        {
            _dark = dark;
            _accent = accent;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(380, 460);
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in Alt-Tab
            BackColor = dark ? Color.FromArgb(30, 30, 33) : Color.FromArgb(248, 248, 250);

            var header = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(238, 238, 242) };
            var title = new Label
            {
                Text = "Pinned messages",
                Dock = DockStyle.Fill,
                ForeColor = dark ? Color.FromArgb(225, 225, 230) : Color.FromArgb(35, 35, 40),
                Font = FontHelper.Ui(10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0)
            };
            var close = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 40,
                FlatStyle = FlatStyle.Flat,
                ForeColor = dark ? Color.FromArgb(210, 210, 215) : Color.FromArgb(70, 70, 75),
                Font = FontHelper.Ui(11f)
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (s, e) => Close();
            header.Controls.Add(title);
            header.Controls.Add(close);

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = dark ? Color.FromArgb(30, 30, 33) : Color.FromArgb(248, 248, 250),
                Padding = new Padding(0)
            };
            _list.Resize += (s, e) => SizeRows();

            Controls.Add(_list);
            Controls.Add(header);
            ScrollbarTheme.Apply(_list, dark);
            TelegArm.UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan the pinned-messages list (RT touch)
        }

        /// <summary>Esc closes the popup.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>(Re)builds the list from the ordered pinned set.</summary>
        public void SetPins(List<(int id, string label, string preview)> rows)
        {
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Dispose();
            _list.Controls.Clear();
            if (rows != null)
                foreach (var r in rows)
                {
                    var row = new PinnedRowControl(r.id, r.label, r.preview) { AccentColor = _accent, IsDark = _dark };
                    row.Clicked += id => JumpRequested?.Invoke(id);
                    row.ContextRequested += (id, pt) => ContextRequested?.Invoke(id, pt);
                    _list.Controls.Add(row);
                }
            _list.ResumeLayout();
            SizeRows();
        }

        private void SizeRows()
        {
            int w = _list.ClientSize.Width - (_list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            foreach (Control c in _list.Controls) c.Width = Math.Max(40, w);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(_dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(210, 210, 214)))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);   // subtle border for the borderless popup
        }
    }
}
