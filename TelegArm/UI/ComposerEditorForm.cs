using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 2.7d (plan R8, owner request 2026-10-13, as in Telegram Desktop) — the composer's editor
    /// window: a resizable AppForm with a large colour-emoji editor, opened from the composer's expand glyph or its
    /// "Edit in a window…" menu item.
    ///
    /// ★ MODAL, ON PURPOSE. MainForm shows it with ShowDialog, so while it is open the user cannot switch chat or account
    ///   from MainForm's own UI — the draft paths (OpenChat, SwitchAccountAsync) that would otherwise save an empty
    ///   composer over the server draft cannot run. The text comes back into the composer on close (Esc, ✕, or Send),
    ///   and Send goes through MainForm's own SendCurrentMessage, so edit mode, replies, threads and entities behave
    ///   exactly as from the composer. The paths a modal cannot block (a tray Exit, a notification click) read the
    ///   window's text through MainForm.CurrentComposerText, and MainForm checks the chat did not change before writing
    ///   the text back.
    /// ★ The editor is an EmojiComposer — the control that owns WM_CHAR and is immune to RT 8.1's dead native EDIT. It
    ///   opens TabTip itself on a touch tap; nothing here launches the keyboard. The window moves up out of the touch
    ///   keyboard's way through KeyboardInset, like a DialogForm (MainForm publishes the keyboard rect while it ticks).
    /// Not taken from Telegram's window: AI, tables, formulas, image generation (no Telegram send path for them).</summary>
    internal sealed class ComposerEditorForm : AppForm
    {
        public const int DefaultWidth = 640, DefaultHeight = 480, MinWidth = 420, MinHeight = 320;

        private readonly TelegramService _service;
        private readonly EmojiComposer _editor;
        private readonly InputBox _box;            // the same adapter type as MainForm's composer, for ComposerFormat
        private readonly ToolTip _tips = new ToolTip();
        private readonly Panel _tools, _footer, _editorHost;
        private readonly ThemedLabel _keyHint;
        private readonly RoundedButton _send, _clear;
        private ToolButton _undo, _redo, _emoji;

        /// <summary>True when the window closed through Send (the button or the send key).</summary>
        public bool SendRequested { get; private set; }

        /// <summary>The text being edited ("\n" line breaks, as the composer keeps it).</summary>
        public string EditorText { get { return _editor.Text; } }

        /// <summary>A sticker or GIF picked from the window's emoji picker; MainForm sends it as the composer's picker does.</summary>
        public event Action<TL.Document> StickerPicked;

        public ComposerEditorForm(string title, string text, TelegramService service)
        {
            _service = service;
            Text = string.IsNullOrEmpty(title) ? "Compose" : title;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Bar.CanMinimize = false;   // an owned modal window: minimising it alone would only hide it behind its owner
            var s = AppSettings.Instance;
            int w = s.ComposerEditorWidth >= MinWidth ? s.ComposerEditorWidth : DefaultWidth;
            int h = s.ComposerEditorHeight >= MinHeight ? s.ComposerEditorHeight : DefaultHeight;
            Size = new Size(Px(w), Px(h));
            LogicalMinimumSize = new Size(MinWidth, MinHeight);

            // ── the editor ──
            _editor = new EmojiComposer { Dock = DockStyle.Fill, Hint = "Write a message…", FontPoints = 11f };
            _editor.Text = (text ?? "").Replace("\r\n", "\n");
            _editor.ClearUndo();   // loading the text is not an edit: the first Undo must not empty the window
            _box = InputBox.CreateRich(_editor);
            _editor.KeyDown += OnEditorKeyDown;
            _editor.TextChanged += (snd, ev) => SyncButtons();
            _editorHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(12), Px(10), Px(12), Px(10)) };
            _editorHost.Controls.Add(_editor);

            // ── toolbar: Undo / Redo | Bold, Italic, Strike, Mono, Link | Emoji ──
            _tools = new Panel { Dock = DockStyle.Top, Height = Px(44) };
            _tools.Paint += (snd, ev) =>
            {
                using (var pen = new Pen(ThemeHelper.Palette.Divider)) ev.Graphics.DrawLine(pen, 0, _tools.Height - 1, _tools.Width, _tools.Height - 1);
            };
            int x = Px(8);
            _undo = AddTool(ref x, "Undo (Ctrl+Z)", null, FontStyle.Regular, (g, r, c) => DrawUndo(g, r, c, false), () => { _editor.Undo(); _editor.Focus(); });
            _redo = AddTool(ref x, "Redo (Ctrl+Y)", null, FontStyle.Regular, (g, r, c) => DrawUndo(g, r, c, true), () => { _editor.Redo(); _editor.Focus(); });
            x += Px(10);
            AddTool(ref x, "Bold (Ctrl+B)", "B", FontStyle.Bold, null, () => Format("**", "**"));
            AddTool(ref x, "Italic (Ctrl+I)", "I", FontStyle.Italic, null, () => Format("__", "__"));
            AddTool(ref x, "Strikethrough", "S", FontStyle.Regular, null, () => Format("~~", "~~"), strike: true);
            AddTool(ref x, "Monospace", "{ }", FontStyle.Regular, null, () => Format("`", "`"), mono: true);
            AddTool(ref x, "Link (Ctrl+K)", "Link", FontStyle.Regular, null, () => ComposerFormat.InsertLink(_box));
            x += Px(10);
            _emoji = AddTool(ref x, "Emoji", null, FontStyle.Regular, DrawEmojiGlyph, OpenEmojiPicker);

            // ── footer: the send-key hint, Clear, Send ──
            _footer = new Panel { Dock = DockStyle.Bottom, Height = Px(60) };
            _send = new RoundedButton
            {
                Text = "Send", Kind = RoundedButtonKind.Primary, Modern = true,
                Size = new Size(Px(104), Px(40)), Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold)
            };
            _send.Click += (snd, ev) => Finish(true);
            _clear = new RoundedButton
            {
                Text = "Clear", Kind = RoundedButtonKind.Neutral, Modern = true,
                Size = new Size(Px(104), Px(40)), Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold)
            };
            _clear.Click += (snd, ev) => ClearText();
            _keyHint = new ThemedLabel
            {
                Text = SendKeyHint(), PointSize = 9f, Role = LabelRole.Dim, Ellipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _footer.Controls.Add(_send);
            _footer.Controls.Add(_clear);
            _footer.Controls.Add(_keyHint);
            _footer.Resize += (snd, ev) => LayoutFooter();
            _footer.Paint += (snd, ev) =>
            {
                using (var pen = new Pen(ThemeHelper.Palette.Divider)) ev.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
            };

            // Fill first: docking takes the last-added first (toolbar and footer claim their edges, the editor the rest).
            Content.Controls.Add(_editorHost);
            Content.Controls.Add(_footer);
            Content.Controls.Add(_tools);
            LayoutFooter();
            ApplyColors();
            SyncButtons();
        }

        // ── the tool buttons ───────────────────────────────────────────────────────────────────────────────────────

        private ToolButton AddTool(ref int x, string tip, string label, FontStyle style, Action<Graphics, Rectangle, Color> draw,
                                   Action click, bool strike = false, bool mono = false)
        {
            int w = label != null && label.Length > 2 ? Px(48) : Px(36);
            var b = new ToolButton(label, style, draw, tip, _tips) { Strike = strike, Mono = mono };
            b.SetBounds(x, Px(6), w, Px(32));
            b.Click += (snd, ev) => click();
            _tools.Controls.Add(b);
            x += w + Px(2);
            return b;
        }

        private void SyncButtons()
        {
            if (_undo != null) _undo.Enabled = _editor.CanUndo;
            if (_redo != null) _redo.Enabled = _editor.CanRedo;
            if (_send != null) _send.Enabled = _editor.Text.Trim().Length > 0;
        }

        private void Format(string open, string close)
        {
            ComposerFormat.Wrap(_box, open, close);
            SyncButtons();
        }

        private void LayoutFooter()
        {
            int m = Px(12), top = (_footer.Height - _send.Height) / 2;
            _send.Location = new Point(_footer.Width - m - _send.Width, top);
            _clear.Location = new Point(_send.Left - Px(8) - _clear.Width, top);
            _keyHint.SetBounds(m + Px(4), 0, Math.Max(0, _clear.Left - m - Px(12)), _footer.Height);
        }

        private static string SendKeyHint()
        {
            switch (AppSettings.Instance.SendKey)
            {
                case "CtrlEnter": return "Ctrl+Enter sends · Enter adds a new line";
                case "AltEnter": return "Alt+Enter sends · Enter adds a new line";
                default: return "Enter sends · Shift+Enter adds a new line";
            }
        }

        // ── keys ───────────────────────────────────────────────────────────────────────────────────────────────────

        private void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            // The same send-key rule as the composer (MainForm.SendKeyPressed): Shift+Enter is always a newline.
            if (e.KeyCode == Keys.Enter && !e.Shift && MainForm.SendKeyPressed(e)) { e.SuppressKeyPress = true; Finish(true); }
            else if (e.Control && e.KeyCode == Keys.B) { e.SuppressKeyPress = true; Format("**", "**"); }
            else if (e.Control && e.KeyCode == Keys.I) { e.SuppressKeyPress = true; Format("__", "__"); }
            else if (e.Control && e.KeyCode == Keys.K) { e.SuppressKeyPress = true; ComposerFormat.InsertLink(_box); }
        }

        /// <summary>Esc closes and KEEPS the text (it goes back into the composer).</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Finish(false); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>MainForm: the chat or account is changing under the window. Closes it as a plain close (text kept).</summary>
        internal void CloseKeepingText() { if (!IsDisposed) Finish(false); }

        /// <summary>Closes the window, as Send or as a plain close. Deferred when not modal, so a key handler of the
        /// editor never destroys the editor under its own WndProc.</summary>
        private void Finish(bool send)
        {
            if (send && _editor.Text.Trim().Length == 0) return;
            SendRequested = send;
            if (Modal) DialogResult = send ? DialogResult.OK : DialogResult.Cancel;
            else BeginInvoke((Action)Close);
        }

        private void ClearText()
        {
            if (_editor.Text.Length > 0 && ThemedDialog.Show(this, "Clear", "Clear the whole message?", "Clear", "Cancel") != 0) return;
            _editor.Text = "";   // one undo step: Ctrl+Z brings it back
            _editor.Focus();
            SyncButtons();
        }

        // ── emoji ──────────────────────────────────────────────────────────────────────────────────────────────────

        private void OpenEmojiPicker()
        {
            var p = ThemeHelper.Palette;
            // Step 3.1: a popup takes the DPI of WHERE IT OPENS (under the button), not this window's; built at that DPI.
            var anchor = _emoji.PointToScreen(new Point(0, _emoji.Height));
            Rectangle wa;
            try { wa = Screen.FromControl(this).WorkingArea; } catch { wa = Screen.PrimaryScreen.WorkingArea; }
            // The DPI of where it LANDS: the anchor clamped into the work area it is placed in (review of step 3.1).
            int dpi = Dpi.ForPopup(new Point(Math.Max(wa.Left, Math.Min(anchor.X, wa.Right - 1)),
                                             Math.Max(wa.Top, Math.Min(anchor.Y, wa.Bottom - 1))));
            var picker = new EmojiPicker(_service, p.Dark, p.Accent, dpi: dpi);
            picker.Picked += emoji =>
            {
                if (string.IsNullOrEmpty(emoji)) return;
                _editor.SelectedText = emoji;   // insert at the caret: one undo step
                _editor.Focus();
                SyncButtons();
            };
            picker.DocumentPicked += doc => { var h = StickerPicked; if (h != null) h(doc); };
            var at = new Point(anchor.X, anchor.Y + Dpi.Px(4, dpi));
            int left = Math.Max(wa.Left, Math.Min(at.X, wa.Right - picker.Width));
            int top = at.Y + picker.Height <= wa.Bottom ? at.Y : Math.Max(wa.Top, at.Y - _emoji.Height - Dpi.Px(8, dpi) - picker.Height);
            picker.Location = new Point(left, top);
            picker.Show(this);
            picker.Activate();
        }

        // ── lifecycle ──────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
            KeyboardInset.KeyboardRectChanged += OnKeyboardRect;
            // Posted: AppForm's WM_ACTIVATE fix clears the focus during the show (see DialogForm.OnShown). Focusing the
            // EmojiComposer never opens the touch keyboard — only a tap on it does.
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    _editor.Focus();
                    _editor.SelectionStart = _editor.Text.Length;
                }));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            // Remember the size (logical px) — only a normal window's: a maximised one would come back maximised-sized.
            try
            {
                if (WindowState == FormWindowState.Normal && !_kbShifted)
                {
                    var s = AppSettings.Instance;
                    s.ComposerEditorWidth = Math.Max(MinWidth, Width * Dpi.Logical / Math.Max(1, WindowDpiValue));
                    s.ComposerEditorHeight = Math.Max(MinHeight, Height * Dpi.Logical / Math.Max(1, WindowDpiValue));
                    s.Save();
                }
            }
            catch (Exception ex) { Logger.Diag("[EDITOR] size save failed: " + ex.Message); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
                try { _tips.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        protected override void OnThemeChanged()
        {
            base.OnThemeChanged();
            ApplyColors();
        }

        private void ApplyColors()
        {
            var p = ThemeHelper.Palette;
            _tools.BackColor = p.Window;
            _editorHost.BackColor = p.Window;
            _footer.BackColor = p.Footer;
            foreach (Control c in _tools.Controls) c.Invalidate();
            foreach (Control c in _footer.Controls) c.Invalidate();
            _tools.Invalidate(); _footer.Invalidate(); _editor.Invalidate();
        }

        // ── the touch keyboard (the DialogForm shift, for a resizable window) ──────────────────────────────────────

        private bool _kbShifted;
        private Rectangle _kbSavedBounds;

        /// <summary>The touch keyboard covers the window's lower part: move it up by the overlap, and shrink it only when
        /// the title bar would leave the work area. The exact original bounds come back when the keyboard goes.</summary>
        private void OnKeyboardRect(Rectangle kb)
        {
            try
            {
                if (IsDisposed || !Visible || WindowState != FormWindowState.Normal) return;
                if (kb.IsEmpty)
                {
                    if (_kbShifted) { _kbShifted = false; Bounds = _kbSavedBounds; }
                    return;
                }
                if (!ContainsFocus) return;   // the keyboard is for another window
                int need = Bounds.Bottom - kb.Top;
                if (need <= 0 || !Bounds.IntersectsWith(kb)) return;
                if (!_kbShifted) { _kbSavedBounds = Bounds; _kbShifted = true; }
                Rectangle wa;
                try { wa = Screen.FromHandle(Handle).WorkingArea; } catch { wa = Screen.PrimaryScreen.WorkingArea; }
                int top = Math.Max(wa.Top, Top - need);
                int remain = need - (Top - top);
                int h = remain > 0 ? Math.Max(Px(220), Height - remain) : Height;
                SetBounds(Left, top, Width, h);
            }
            catch (Exception ex) { Logger.Diag("[KBD] editor shift failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ── drawn glyphs ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A curved arrow: a half-ring on the right with the head on its upper-left end (Undo), mirrored for Redo.</summary>
        private static void DrawUndo(Graphics g, Rectangle r, Color c, bool redo)
        {
            var state = g.Save();
            if (redo) { g.TranslateTransform(r.X + r.Width / 2f, 0); g.ScaleTransform(-1, 1); g.TranslateTransform(-(r.X + r.Width / 2f), 0); }
            float s = Math.Min(r.Width, r.Height) * 0.44f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            var ring = new RectangleF(cx - s * 0.30f, cy - s * 0.55f, s * 0.95f, s * 1.10f);
            using (var pen = new Pen(c, Math.Max(1.5f, s * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawArc(pen, ring, 270, 180);
                float ty = ring.Top, bx = ring.Left + ring.Width / 2f;
                g.DrawLine(pen, bx, ty, cx - s * 0.62f, ty);                                   // the shaft, to the head
                float hx = cx - s * 0.62f, hh = s * 0.30f;
                g.DrawLines(pen, new[] { new PointF(hx + hh, ty - hh), new PointF(hx, ty), new PointF(hx + hh, ty + hh) });
                g.DrawLine(pen, bx, ring.Bottom, cx - s * 0.30f, ring.Bottom);                 // the tail
            }
            g.Restore(state);
        }

        private static void DrawEmojiGlyph(Graphics g, Rectangle r, Color c)
        {
            float s = Math.Min(r.Width, r.Height) * 0.52f;
            var face = new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
            using (var pen = new Pen(c, Math.Max(1.5f, s * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, face);
                g.DrawArc(pen, face.X + s * 0.25f, face.Y + s * 0.30f, s * 0.50f, s * 0.45f, 20, 140);   // the smile
            }
            float e = Math.Max(2f, s * 0.12f);
            using (var b = new SolidBrush(c))
            {
                g.FillEllipse(b, face.X + s * 0.30f, face.Y + s * 0.30f, e, e);
                g.FillEllipse(b, face.Right - s * 0.30f - e, face.Y + s * 0.30f, e, e);
            }
        }

        /// <summary>A toolbar button: drawn, flat, hover-tinted. NOT selectable, so a click leaves the focus — and the
        /// selection the format applies to — in the editor.</summary>
        private sealed class ToolButton : Control
        {
            private readonly string _label;
            private readonly FontStyle _style;
            private readonly Action<Graphics, Rectangle, Color> _draw;
            private bool _hot, _down;

            public bool Strike { get; set; }
            public bool Mono { get; set; }

            public ToolButton(string label, FontStyle style, Action<Graphics, Rectangle, Color> draw, string tip, ToolTip tips)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                Cursor = Cursors.Hand;
                _label = label; _style = style; _draw = draw;
                AccessibleName = tip;
                AccessibleRole = AccessibleRole.PushButton;
                try { tips.SetToolTip(this, tip); } catch { }
            }

            protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = _down = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, p.Window);   // step 5.9
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                int dpi = Dpi.Of(this);
                if (Enabled && (_hot || _down))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(_down ? p.Selected : p.Hover))
                    using (var path = DrawHelper.RoundedRect(r, Dpi.Px(6, dpi)))
                        g.FillPath(b, path);
                }
                var ink = Enabled ? p.Text : p.TextDisabled;
                if (_draw != null)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    _draw(g, r, ink);
                    return;
                }
                // Segoe UI for the labels: the bundled Roboto has no italic face (FontCache would fall back to Regular and
                // the "I" would lose its slant). Courier New for the monospace sample. Both ship with Windows RT 8.1.
                var font = FontCache.Get(Mono ? "Courier New" : "Segoe UI", Mono ? 10.5f : 11f, Mono ? FontStyle.Bold : _style, dpi);
                TextRenderer.DrawText(g, _label, font, r, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (Strike)
                {
                    // Drawn, not FontStyle.Strikeout: the line is the whole point of the glyph, so it must not depend on a face.
                    var sz = TextRenderer.MeasureText(g, _label, font, r.Size, TextFormatFlags.NoPadding);
                    float y = r.Y + r.Height / 2f + Dpi.F(0.5f, dpi), x0 = r.X + (r.Width - sz.Width) / 2f - Dpi.F(2f, dpi);
                    using (var pen = new Pen(ink, Math.Max(1f, Dpi.F(1.2f, dpi)))) g.DrawLine(pen, x0, y, x0 + sz.Width + Dpi.F(4f, dpi), y);
                }
            }
        }

        // ── probe (gallery probe, step 2.7d) ───────────────────────────────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        /// <summary>The window's contract, on the dev box: it opens with the text, formats the selection as one undo step,
        /// the send key closes it as Send, Esc closes it keeping the text, Clear is undoable, and the size is remembered.</summary>
        internal static void RunProbe(IWin32Window owner, Action<bool, string, string> check, Action<string> info)
        {
            var s = AppSettings.Instance;
            int oldW = s.ComposerEditorWidth, oldH = s.ComposerEditorHeight;
            string oldKey = s.SendKey;
            try
            {
                s.SendKey = "Enter";
                using (var f = new ComposerEditorForm("Probe", "hello world", null))
                {
                    f.Show(owner);
                    Application.DoEvents();
                    var ed = f._editor;
                    check(ed.Text == "hello world", "editor: opens with the composer's text", ed.Text);

                    ed.SelectionStart = 6; ed.SelectionLength = 5;
                    f.Format("**", "**");
                    bool wrapped = ed.Text == "hello **world**";
                    ed.Undo();
                    bool undone = ed.Text == "hello world";
                    ed.Redo();
                    check(wrapped && undone && ed.Text == "hello **world**", "editor: Bold wraps the selection, as one undo step",
                          "wrapped=" + wrapped + " undone=" + undone + " now='" + ed.Text + "'");
                    check(f._undo.Enabled, "editor: the Undo button follows the editor's history", "enabled=" + f._undo.Enabled);
                    ed.Undo();
                    bool undoOff = !f._undo.Enabled && f._redo.Enabled;   // the last Undo: the button must turn off with it
                    ed.Redo();
                    bool redoOff = f._undo.Enabled && !f._redo.Enabled;
                    check(undoOff && redoOff, "editor: Undo / Redo turn off with the last step (TextChanged sees the new history)",
                          "after last undo: undoOff=" + undoOff + "; after last redo: redoOff=" + redoOff);

                    f.ClearTextNoConfirm();
                    bool cleared = ed.Text.Length == 0 && !f._send.Enabled;
                    ed.Undo();
                    check(cleared && ed.Text == "hello **world**", "editor: Clear empties it (Send disabled), and Undo brings it back",
                          "cleared=" + cleared + " now='" + ed.Text + "'");

                    // The send key: WM_KEYDOWN VK_RETURN to the editor (no modifier held) → Send, then the window closes.
                    SendMessage(ed.Handle, 0x0100, (IntPtr)(int)Keys.Return, (IntPtr)1);
                    for (int i = 0; i < 20 && f.Visible; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    check(f.SendRequested && !f.Visible && f.EditorText == "hello **world**", "editor: the send key closes it as Send, text intact",
                          "send=" + f.SendRequested + " visible=" + f.Visible + " text='" + f.EditorText + "'");
                }

                using (var f = new ComposerEditorForm("Probe", "keep me", null))
                {
                    f.Show(owner);
                    Application.DoEvents();
                    f.Size = new Size(f.Px(700), f.Px(500));
                    Application.DoEvents();
                    var m = Message.Create(f.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    f.ProcessCmdKey(ref m, Keys.Escape);
                    for (int i = 0; i < 20 && f.Visible; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    check(!f.SendRequested && !f.Visible && f.EditorText == "keep me", "editor: Esc closes it, keeping the text, not as Send",
                          "send=" + f.SendRequested + " visible=" + f.Visible + " text='" + f.EditorText + "'");
                    check(Math.Abs(s.ComposerEditorWidth - 700) <= 1 && Math.Abs(s.ComposerEditorHeight - 500) <= 1,
                          "editor: remembers its size, in logical px", s.ComposerEditorWidth + "x" + s.ComposerEditorHeight);
                }

                using (var f = new ComposerEditorForm("Probe", "", null))
                {
                    f.Show(owner);
                    Application.DoEvents();
                    var want = new Size(f.Px(700), f.Px(500));
                    check(Math.Abs(f.Width - want.Width) <= 2 && Math.Abs(f.Height - want.Height) <= 2,
                          "editor: reopens at the remembered size", f.Size + " want " + want);
                    check(!f._send.Enabled, "editor: Send is disabled while the text is empty", "enabled=" + f._send.Enabled);
                    f.Close();
                }
            }
            catch (Exception ex) { check(false, "editor: probe threw", ex.ToString()); }
            finally
            {
                s.ComposerEditorWidth = oldW; s.ComposerEditorHeight = oldH; s.SendKey = oldKey;
                try { s.Save(); } catch { }
            }
        }

        /// <summary>Probe seam: Clear without its confirmation dialog.</summary>
        private void ClearTextNoConfirm() { _editor.Text = ""; SyncButtons(); }
    }

    /// <summary>Step 2.7d: the composer's markdown helpers, shared by MainForm's composer and the editor window. They wrap
    /// the selection in the marker that MarkdownEntities.Parse turns into a MessageEntity on send; one Text set each, so
    /// on the colour-emoji composer each is one undo step.</summary>
    internal static class ComposerFormat
    {
        public static void Wrap(InputBox tb, string open, string close)
        {
            if (tb == null || !tb.Enabled) return;   // review 1.5: never write into a disabled composer
            try
            {
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                string sel = t.Substring(start, len);
                tb.Text = t.Substring(0, start) + open + sel + close + t.Substring(start + len);
                tb.SelectionStart = len > 0 ? start + open.Length + len + close.Length : start + open.Length;
                tb.Focus();
            }
            catch { }
        }

        public static void InsertLink(InputBox tb)
        {
            if (tb == null || !tb.Enabled) return;   // review 1.5: never write into a disabled composer
            try
            {
                string t = tb.Text ?? "";
                int start = tb.SelectionStart, len = tb.SelectionLength;
                if (start < 0) start = 0; if (start > t.Length) start = t.Length;
                if (len < 0 || start + len > t.Length) len = 0;
                string label = len > 0 ? t.Substring(start, len) : "text";
                tb.Text = t.Substring(0, start) + "[" + label + "](url)" + t.Substring(start + len);
                tb.SelectionStart = start + 1 + label.Length + 2;   // caret at the "url" placeholder
                tb.SelectionLength = 3;                              // select "url" so the user overtypes it
                tb.Focus();
            }
            catch { }
        }
    }
}
