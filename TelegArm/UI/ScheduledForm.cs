using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T11 — a chat's scheduled messages (messages.getScheduledHistory), soonest first: pick one, then Send now
    /// or Delete. Scheduling itself is the send button's right-click menu (MainForm.Composer).
    /// </summary>
    public sealed class ScheduledForm : DialogForm
    {
        private const int RowLogical = 58;

        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly ThemedListBox _list;
        private readonly Label _status;
        private readonly RoundedButton _sendNow, _delete;
        private List<Message> _items = new List<Message>();
        private int _selected = -1;

        /// <summary>True when anything was sent or deleted (the caller may refresh).</summary>
        public bool Changed { get; private set; }

        public ScheduledForm(TelegramService service, InputPeer peer, string chatTitle) : base("Scheduled messages", 380, 480)
        {
            _service = service; _peer = peer;
            var p = ThemeHelper.Palette;
            var host = new Panel { BackColor = p.Window };
            _list = new ThemedListBox(p.Dark, p.Accent) { Dock = DockStyle.Fill, RowHeight = Px(RowLogical), CanvasBackColor = p.Window };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += i => { _selected = i; _list.Invalidate(true); UpdateButtons(); };
            _status = new Label { Dock = DockStyle.Bottom, Height = Px(28), TextAlign = ContentAlignment.MiddleCenter, ForeColor = p.TextDim, BackColor = p.Window, Text = "Loading…" };
            host.Controls.Add(_list);
            host.Controls.Add(_status);
            UseOwnBody(host);

            _sendNow = AddFooterButton("Send now", RoundedButtonKind.Primary, DialogResult.None, 110);   // first added = rightmost
            _sendNow.Click += (s, e) => Act(true);
            _delete = AddFooterButton("Delete", RoundedButtonKind.Secondary, DialogResult.None, 96);
            _delete.Click += (s, e) => Act(false);
            CancelButton = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
            UpdateButtons();
            Shown += async (s, e) =>
            {
                try { _items = await _service.GetScheduledAsync(_peer); }
                catch (Exception ex) { _items = new List<Message>(); Logger.Diag("[SCHEDULE] list failed: " + ex.Message); }
                if (IsDisposed) return;
                Refill();
            };
        }

        private void Refill()
        {
            if (_selected >= _items.Count) _selected = -1;
            _list.SetItems(_items.Count);
            _status.Visible = _items.Count == 0;
            _status.Text = "No scheduled messages.";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool sel = _selected >= 0 && _selected < _items.Count;
            _sendNow.Enabled = sel;
            _delete.Enabled = sel;
        }

        private async void Act(bool sendNow)
        {
            if (_selected < 0 || _selected >= _items.Count) return;
            var m = _items[_selected];
            if (!sendNow && ThemedDialog.Show(this, "Delete", "Delete this scheduled message?", "Delete", "Cancel") != 0) return;
            try
            {
                if (sendNow) await _service.SendScheduledNowAsync(_peer, m.ID);
                else await _service.DeleteScheduledAsync(_peer, m.ID);
            }
            catch (Exception ex) { ThemedDialog.Show(this, sendNow ? "Send now" : "Delete", "Couldn't do it: " + ex.Message, "OK"); return; }
            if (IsDisposed) return;
            Changed = true;
            _items.Remove(m);
            _selected = -1;
            Refill();
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || index >= _items.Count) return;
            var m = _items[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bb = new SolidBrush(index == _selected ? p.AccentSoft : p.Window)) g.FillRectangle(bb, r);

            int x = r.Left + Px(14), w = r.Width - Px(28);
            var local = m.date.ToLocalTime();
            string when = (local.Date == DateTime.Now.Date ? "Today" : local.Date == DateTime.Now.Date.AddDays(1) ? "Tomorrow"
                          : local.ToString("ddd d MMM", CultureInfo.CurrentCulture)) + ", " + local.ToString("t", CultureInfo.CurrentCulture);
            TextRenderer.DrawText(g, when, FontHelper.UiAt(9f, dpi, FontStyle.Bold), new Rectangle(x, r.Top + Px(8), w, Px(18)), p.AccentFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            string text = (m.message ?? "").Replace("\r", " ").Replace("\n", " ");
            if (text.Length == 0) text = m.media != null ? "Media" : "";
            TextRenderer.DrawText(g, text, TextRoles.Font(TextRole.Body, dpi), new Rectangle(x, r.Top + Px(28), w, Px(22)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_list == null) return;
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_items.Count);
            _status.Height = Px(28);
        }
    }
}
