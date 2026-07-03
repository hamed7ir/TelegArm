using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// "Create Poll" composer: a question + 2–10 option fields (add/remove) and the Anonymous /
    /// Multiple-choice toggles. Sends a regular or anonymous or multiple-choice poll. QUIZ MODE is
    /// intentionally NOT offered here — its correct_answers↔option wire encoding could not be verified
    /// in this build environment (see the POLL-BTN batch report); received quizzes still render fully.
    /// </summary>
    public sealed class CreatePollForm : Form
    {
        private const int MaxOptions = 10;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly Color _bg, _fg, _field, _sub;

        private readonly TextBox _question;
        private readonly Panel _optionsPanel;
        private readonly List<TextBox> _optionBoxes = new List<TextBox>();
        private readonly Button _addBtn;
        private readonly CheckBox _anon, _multi;

        public string Question { get; private set; }
        public string[] Options { get; private set; }
        public bool Anonymous { get; private set; }
        public bool Multiple { get; private set; }

        public CreatePollForm(bool dark, Color accent)
        {
            _dark = dark; _accent = accent;
            _bg = dark ? Color.FromArgb(36, 36, 38) : Color.FromArgb(245, 245, 247);
            _fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
            _field = dark ? Color.FromArgb(54, 54, 58) : Color.White;
            _sub = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);

            Text = "Create Poll";
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = _bg; ForeColor = _fg;
            Font = FontHelper.Ui(9.5f);
            ClientSize = new Size(360, 460);

            int x = 16, w = ClientSize.Width - 32;

            Controls.Add(MakeLabel("Question", 14));
            _question = MakeField(34, w, 56);
            _question.Multiline = true;
            Controls.Add(_question);

            Controls.Add(MakeLabel("Options", 100));
            _optionsPanel = new Panel { Left = x, Top = 120, Width = w, Height = 180, BackColor = _bg, AutoScroll = true };
            Controls.Add(_optionsPanel);
            TelegArm.UI.Controls.TouchScroller.Enable(_optionsPanel, horizontal: false);   // finger-pan the options list (RT touch)
            AddOption(); AddOption();   // start with two

            _addBtn = new Button
            {
                Text = "+ Add option", Left = x, Top = 306, Width = 140, Height = 28,
                FlatStyle = FlatStyle.Flat, BackColor = _field, ForeColor = _accent
            };
            _addBtn.FlatAppearance.BorderColor = _sub;
            _addBtn.Click += (s, e) => { AddOption(); };
            Controls.Add(_addBtn);

            _anon = MakeCheck("Anonymous voting", 344); _anon.Checked = true;
            _multi = MakeCheck("Multiple answers", 372);
            // Multiple-choice + (the deferred) quiz are mutually exclusive in Telegram; with quiz absent,
            // Multiple stands alone. Kept simple here.
            Controls.Add(_anon); Controls.Add(_multi);

            var ok = new Button { Text = "Create", Width = 100, Height = 32, Left = ClientSize.Width - 222, Top = 410, FlatStyle = FlatStyle.Flat, BackColor = _accent, ForeColor = Color.White };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += OnCreate;
            var cancel = new Button { Text = "Cancel", Width = 100, Height = 32, Left = ClientSize.Width - 116, Top = 410, FlatStyle = FlatStyle.Flat, BackColor = _field, ForeColor = _fg, DialogResult = DialogResult.Cancel };
            cancel.FlatAppearance.BorderColor = _sub;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        private Label MakeLabel(string text, int top)
        {
            return new Label { Text = text, Left = 16, Top = top, Width = 200, Height = 18, ForeColor = _sub, Font = FontHelper.Ui(8.5f, FontStyle.Bold) };
        }

        private TextBox MakeField(int top, int width, int height)
        {
            return new TextBox
            {
                Left = 16, Top = top, Width = width, Height = height,
                BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle
            };
        }

        private CheckBox MakeCheck(string text, int top)
        {
            return new CheckBox { Text = text, Left = 16, Top = top, Width = 300, Height = 22, ForeColor = _fg, BackColor = _bg };
        }

        private void AddOption()
        {
            if (_optionBoxes.Count >= MaxOptions) return;
            var tb = new TextBox { Width = _optionsPanel.ClientSize.Width - 40, Height = 26, BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle };
            var del = new Button { Width = 26, Height = 24, Text = "✕", FlatStyle = FlatStyle.Flat, BackColor = _bg, ForeColor = _sub };
            del.FlatAppearance.BorderSize = 0;
            del.Click += (s, e) =>
            {
                if (_optionBoxes.Count <= 2) return;   // keep at least two
                _optionBoxes.Remove(tb);
                _optionsPanel.Controls.Remove(tb);
                _optionsPanel.Controls.Remove(del);
                Relayout();
            };
            _optionBoxes.Add(tb);
            _optionsPanel.Controls.Add(tb);
            _optionsPanel.Controls.Add(del);
            tb.Tag = del;
            Relayout();
            if (_addBtn != null) _addBtn.Enabled = _optionBoxes.Count < MaxOptions;
        }

        private void Relayout()
        {
            int y = 4;
            foreach (var tb in _optionBoxes)
            {
                tb.Left = 4; tb.Top = y;
                var del = tb.Tag as Button;
                if (del != null) { del.Left = tb.Right + 6; del.Top = y; }
                y += 32;
            }
        }

        private void OnCreate(object sender, EventArgs e)
        {
            string q = (_question.Text ?? "").Trim();
            var opts = _optionBoxes.Select(t => (t.Text ?? "").Trim()).Where(t => t.Length > 0).ToList();
            if (q.Length == 0) { ThemedDialog.Show(this, "Create Poll", "Please enter a question.", "OK"); return; }
            if (opts.Count < 2) { ThemedDialog.Show(this, "Create Poll", "Please enter at least two options.", "OK"); return; }

            Question = q;
            Options = opts.ToArray();
            Anonymous = _anon.Checked;
            Multiple = _multi.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
