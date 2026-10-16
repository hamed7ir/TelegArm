using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// "Create Poll" composer: a question + 2–10 option fields (add/remove) and the Anonymous /
    /// Multiple-choice toggles. Sends a regular or anonymous or multiple-choice poll — or, since T18, a QUIZ: one correct
    /// answer (picked from the options, sent as its index) and an optional explanation.
    /// </summary>
    public sealed class CreatePollForm : Form, IDpiHost
    {
        private const int MaxOptions = 10;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly Color _bg, _fg, _field, _sub;

        /// <summary>Step 3.3 — the EFFECTIVE DPI this dialog lays out at: its owner's (the caller passes Dpi.Of(owner); the
        /// dialog opens centred on it), re-set by WM_DPICHANGED. Its controls read it through Dpi.Of (IDpiHost). Every
        /// length here is LOGICAL (96-DPI) px through P() = Dpi.Px at this DPI, so at 96 nothing moves by a pixel.</summary>
        private int _dpi;
        int IDpiHost.Dpi { get { return _dpi; } }
        private int P(int n) { return Dpi.Px(n, _dpi); }

        private readonly Label _questionLabel, _optionsLabel;
        private readonly TextBox _question;
        private readonly Panel _optionsPanel;
        private readonly List<TextBox> _optionBoxes = new List<TextBox>();
        private readonly Button _addBtn, _ok, _cancel;
        private readonly CheckBox _anon, _multi, _quiz;   // T18: quiz
        private readonly Label _correctLabel;
        private readonly RoundedButton _correct;   // a themed picker: a native ComboBox draws white in dark mode
        private string _correctText;               // the chosen option's text
        private readonly TextBox _solution;

        public string Question { get; private set; }
        public string[] Options { get; private set; }
        public bool Anonymous { get; private set; }
        public bool Multiple { get; private set; }
        /// <summary>T18: a quiz — <see cref="CorrectIndex"/> into <see cref="Options"/>, <see cref="Solution"/> optional.</summary>
        public bool Quiz { get; private set; }
        public int CorrectIndex { get; private set; } = -1;
        public string Solution { get; private set; }

        /// <param name="dpi">Step 3.3 — the DPI to lay out at: the owner's (Dpi.Of(owner)). 0 = <see cref="Dpi.DefaultDpi"/>.</param>
        public CreatePollForm(bool dark, Color accent, int dpi = 0)
        {
            _dark = dark; _accent = accent;
            _dpi = dpi > 0 ? dpi : Dpi.DefaultDpi;
            _bg = ThemeHelper.PaletteFor(dark).DialogBg;
            _fg = ThemeHelper.PaletteFor(dark).DialogText;
            _field = ThemeHelper.PaletteFor(dark).DialogField;
            _sub = ThemeHelper.PaletteFor(dark).DialogSub;

            Text = "Create Poll";
            TelegArm.Helpers.AppIcon.Set(this);   // app icon in the taskbar / Alt-Tab
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = _bg; ForeColor = _fg;

            // Step 3.3: the controls are built bare, in the 1.x order (so the tab order is unchanged), and ApplyMetrics
            // SETS the font, the size and every bound at _dpi — before Show (CenterParent centres by the size), and again
            // on WM_DPICHANGED.
            _questionLabel = MakeLabel("Question");
            Controls.Add(_questionLabel);
            _question = MakeField();
            _question.Multiline = true;
            Controls.Add(_question);

            _optionsLabel = MakeLabel("Options");
            Controls.Add(_optionsLabel);
            _optionsPanel = new Panel { BackColor = _bg, AutoScroll = true };
            Controls.Add(_optionsPanel);
            TelegArm.UI.Controls.TouchScroller.Enable(_optionsPanel, horizontal: false);   // finger-pan the options list (RT touch)

            _addBtn = new RoundedButton
            {
                Text = "+ Add option",
                Kind = RoundedButtonKind.Secondary
            };
            _addBtn.Click += (s, e) => { AddOption(); };
            Controls.Add(_addBtn);

            _anon = MakeCheck("Anonymous voting"); _anon.Checked = true;
            _multi = MakeCheck("Multiple answers");
            Controls.Add(_anon); Controls.Add(_multi);
            // T18: quiz — one correct answer; Telegram does not allow it together with multiple answers
            _quiz = MakeCheck("Quiz (one correct answer)");
            Controls.Add(_quiz);
            _correctLabel = MakeLabel("Correct answer");
            Controls.Add(_correctLabel);
            _correct = new RoundedButton { Text = "Pick the correct answer…", Kind = RoundedButtonKind.Secondary };
            _correct.Click += (s, e) => PickCorrect();
            Controls.Add(_correct);
            _solution = MakeField();   // single-line: EM_SETCUEBANNER draws its cue (it does not on a multiline EDIT)
            _solution.HandleCreated += (s, e) => { try { SendMessage(_solution.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Explanation shown after answering (optional)"); } catch { } };
            Controls.Add(_solution);
            _quiz.CheckedChanged += (s, e) =>
            {
                if (_quiz.Checked) _multi.Checked = false;
                _multi.Enabled = !_quiz.Checked;
                _correctLabel.Visible = _correct.Visible = _solution.Visible = _quiz.Checked;
            };
            _correctLabel.Visible = _correct.Visible = _solution.Visible = false;

            _ok = new RoundedButton { Text = "Create", Kind = RoundedButtonKind.Primary };
            _ok.Click += OnCreate;
            _cancel = new RoundedButton { Text = "Cancel", Kind = RoundedButtonKind.Secondary, DialogResult = DialogResult.Cancel };
            Controls.Add(_ok); Controls.Add(_cancel);
            AcceptButton = _ok; CancelButton = _cancel;

            ApplyMetrics();
            AddOption(); AddOption();   // start with two (after ApplyMetrics: a row takes the options panel's width)
        }

        /// <summary>Step 3.3 — SETS the form's font and size and every control's bounds from LOGICAL values at _dpi (never
        /// multiplies), so the first layout and a re-layout after WM_DPICHANGED are one code path. At 96 these are the 1.x
        /// numbers exactly. Fonts are SHARED (FontCache): never dispose.</summary>
        private void ApplyMetrics()
        {
            Font = FontHelper.UiAt(9.5f, _dpi);   // shared: never dispose (the fields, checks and buttons inherit it)
            ClientSize = new Size(P(360), P(560));

            int x = P(16), w = ClientSize.Width - P(32);

            PlaceLabel(_questionLabel, P(14));
            _question.Bounds = new Rectangle(x, P(34), w, P(56));
            PlaceLabel(_optionsLabel, P(100));
            _optionsPanel.Bounds = new Rectangle(x, P(120), w, P(180));
            _addBtn.Bounds = new Rectangle(x, P(306), P(140), P(28));
            _anon.Bounds = new Rectangle(x, P(344), P(300), P(22));
            _multi.Bounds = new Rectangle(x, P(372), P(300), P(22));
            _quiz.Bounds = new Rectangle(x, P(400), P(300), P(22));                      // T18
            PlaceLabel(_correctLabel, P(430));
            _correct.Bounds = new Rectangle(x, P(448), w, P(28));
            _solution.Bounds = new Rectangle(x, P(480), w, P(26));
            _ok.Bounds = new Rectangle(ClientSize.Width - P(222), P(514), P(100), P(32));
            _cancel.Bounds = new Rectangle(ClientSize.Width - P(116), P(514), P(100), P(32));

            // The option rows (none yet on the first call): scrolled home first — Relayout places them in client
            // coordinates — then each re-sized to the panel and re-placed, with the panel's layout held so its scrollbar
            // cannot come or go part-way and give the rows two widths.
            _optionsPanel.AutoScrollPosition = Point.Empty;
            _optionsPanel.SuspendLayout();
            try
            {
                foreach (var tb in _optionBoxes) SizeRow(tb);
                Relayout();
            }
            finally { _optionsPanel.ResumeLayout(true); }
        }

        private Label MakeLabel(string text)
        {
            return new Label { Text = text, ForeColor = _sub };
        }

        /// <summary>A section label at <paramref name="top"/> (px at _dpi) in the shared bold 8.5 pt font.</summary>
        private void PlaceLabel(Label label, int top)
        {
            label.Font = FontHelper.UiAt(8.5f, _dpi, FontStyle.Bold);   // shared: never dispose
            label.Bounds = new Rectangle(P(16), top, P(200), P(18));
        }

        private TextBox MakeField()
        {
            return new TextBox
            {
                BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle
            };
        }

        /// <summary>T18: the options as typed right now, in a themed menu under the picker; the choice is kept by its text
        /// (OnCreate finds it among the non-empty options that are sent).</summary>
        private void PickCorrect()
        {
            var menu = new ThemedContextMenuStrip();
            int n = 0;
            foreach (var tb in _optionBoxes)
            {
                string t = (tb.Text ?? "").Trim();
                if (t.Length == 0) continue;
                string label = (++n) + ". " + t;
                var it = new ToolStripMenuItem(label) { Checked = t == _correctText };
                it.Click += (s, e) => { _correctText = t; _correct.Text = label; };
                menu.Items.Add(it);
            }
            if (n == 0) { menu.Dispose(); ThemedDialog.Show(this, "Create Poll", "Type the options first.", "OK"); return; }
            menu.Closed += (s, e) => { try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)menu.Dispose); else menu.Dispose(); } catch { } };
            menu.ShowAt(_correct, new Point(0, _correct.Height));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private CheckBox MakeCheck(string text)
        {
            return new CheckBox { Text = text, ForeColor = _fg, BackColor = _bg };
        }

        /// <summary>One option row's size at _dpi: the field spans the options panel less P(40) (the remove button and its
        /// gap), measured NOW — as in 1.x, a row added after the panel grew its scrollbar is that much narrower.</summary>
        private void SizeRow(TextBox tb)
        {
            tb.Size = new Size(_optionsPanel.ClientSize.Width - P(40), P(26));
            var del = tb.Tag as Button;
            if (del != null) del.Size = new Size(P(26), P(24));
        }

        private void AddOption()
        {
            if (_optionBoxes.Count >= MaxOptions) return;
            var tb = new TextBox { BackColor = _field, ForeColor = _fg, BorderStyle = BorderStyle.FixedSingle };
            var del = new RoundedButton { Text = "✕", Kind = RoundedButtonKind.Neutral, Radius = 6 };
            tb.Tag = del;   // first: SizeRow reads it
            SizeRow(tb);
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
            Relayout();
            if (_addBtn != null) _addBtn.Enabled = _optionBoxes.Count < MaxOptions;
        }

        private void Relayout()
        {
            int y = P(4);
            foreach (var tb in _optionBoxes)
            {
                tb.Left = P(4); tb.Top = y;
                var del = tb.Tag as Button;
                if (del != null) { del.Left = tb.Right + P(6); del.Top = y; }
                y += P(32);
            }
        }

        /// <summary>Step 3.3 — WM_DPICHANGED (per-monitor only: never sent while the process is system-aware, the build
        /// default). Swallowed — Result 0, no base: WinForms' own handling must not scale this plain Form — then re-laid
        /// out from logical values at the new effective DPI (<see cref="ApplyMetrics"/>), at the top-left Windows suggests
        /// (its rectangle keeps a window dragged across monitors on the one it entered). Nothing is rebuilt: what the user
        /// typed survives.</summary>
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
                    Logger.Diag("[DPI] win=CreatePollForm old=" + old + " new=" + nw + " mon=" + raw + " applied=" + Bounds);
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void OnCreate(object sender, EventArgs e)
        {
            string q = (_question.Text ?? "").Trim();
            var opts = _optionBoxes.Select(t => (t.Text ?? "").Trim()).Where(t => t.Length > 0).ToList();
            if (q.Length == 0) { ThemedDialog.Show(this, "Create Poll", "Please enter a question.", "OK"); return; }
            if (opts.Count < 2) { ThemedDialog.Show(this, "Create Poll", "Please enter at least two options.", "OK"); return; }

            // T18: the picked option, counted among the NON-EMPTY options (the ones that are sent)
            int correct = -1;
            if (_quiz.Checked)
            {
                if (_correctText != null) correct = opts.IndexOf(_correctText);
                if (correct < 0) { ThemedDialog.Show(this, "Create Poll", "Pick the correct answer for the quiz.", "OK"); return; }
            }

            Question = q;
            Options = opts.ToArray();
            Anonymous = _anon.Checked;
            Multiple = _multi.Checked && !_quiz.Checked;
            Quiz = _quiz.Checked;
            CorrectIndex = correct;
            Solution = _quiz.Checked ? (_solution.Text ?? "").Trim() : null;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
