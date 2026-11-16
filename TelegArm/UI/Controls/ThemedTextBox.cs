using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 1.5 — a text field: a host that paints a rounded frame around a PLAIN native TextBox
    /// (<see cref="Inner"/>, BorderStyle.None). It is always the chat-list search box, and the composer when
    /// AppSettings.ComposerEngine = "Plain" (the default composer is the colour-emoji EmojiComposer).
    ///
    /// THE RT CONTRACT (plan §4.2 step 1.5 — every point is about the dead-input / touch-keyboard state on RT 8.1):
    ///  • Multiline, AcceptsReturn, WordWrap, ScrollBars, BorderStyle and RightToLeft are fixed in the constructor and
    ///    NEVER changed afterwards: changing them after the handle exists recreates the EDIT window, re-attaches the
    ///    input probe and may flip the dead-input state.
    ///  • NEVER Enabled = false on the edit. <see cref="InputEnabled"/> = false is the disabled state WITHOUT it: ReadOnly,
    ///    not a tab stop, refuses focus (clicks and Focus() are ignored, focus held at the time is released), and is
    ///    painted dimmed — the EDIT keeps its handle and its input state (review 1.5: ReadOnly alone still took focus
    ///    and programmatic writes, and looked enabled).
    ///  • Key and text events are the EDIT's own, re-raised on the host with the SAME event-args object, so a host
    ///    KeyDown handler's SuppressKeyPress still stops the character (Enter-to-send relies on it). The host raises
    ///    Control's own events — it hides none (MaterialTextBox2 HID TextChanged/KeyDown/GotFocus/…, so a subscriber
    ///    holding it as a plain Control silently heard nothing; this control must not repeat that).
    ///  • The cue (placeholder) is drawn inside the EDIT only while it is EMPTY AND UNFOCUSED — EM_SETCUEBANNER does not
    ///    work on a multiline EDIT, and the composer's cue changes for bot keyboards. While the field is focused nothing
    ///    is drawn over it at all (1.x reverted an overpaint of the focused composer in case it broke WM_CHAR on RT).
    ///  • A tap on the frame (outside the EDIT) raises MouseDown FIRST — MainForm records it as a genuine touch there, or
    ///    the post-dismiss suppression bounces the focus (review 1.5) — then focuses the EDIT. It never launches the
    ///    touch keyboard (that kills native typing on RT, device result 2026-10-14); Windows opens it for the EDIT.
    ///  • The EDIT's colours and font are OURS. The EDIT accepts BackColor/ForeColor/Font writes only from this control
    ///    (<see cref="Recolor"/>, the font from <see cref="FontPoints"/> at the window's DPI) and ignores everyone else's —
    ///    DpiScaler's too: the font is re-resolved from points on a DPI change, so it is never scaled twice. (History:
    ///    MaterialSkin's form-wide re-skin once turned it into a grey Roboto box, review 1.5; MaterialSkin is gone since 2.8.)
    ///  • Text inset inside the EDIT is EM_SETMARGINS, re-applied when the handle or font changes.</summary>
    public sealed class ThemedTextBox : Control, IDpiAware
    {
        private const int Radius = 8, FramePadX = 10, FramePadY = 6, TextMargin = 2;

        public TextBox Inner { get { return _inner; } }
        private readonly CueTextBox _inner;
        private readonly bool _multiline;
        private bool _inputEnabled = true;
        private float _points = 10.5f;
        private FontStyle _style = FontStyle.Regular;

        /// <param name="multiline">Fixed for the life of the control (see the class notes).</param>
        public ThemedTextBox(bool multiline)
        {
            _multiline = multiline;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;   // the EDIT is the tab stop
            _inner = new CueTextBox
            {
                BorderStyle = BorderStyle.None,
                Multiline = multiline,
                AcceptsReturn = multiline,
                WordWrap = multiline,
                ScrollBars = ScrollBars.None,   // a native bar cannot be themed (white on RT); a multiline EDIT scrolls to the caret anyway
            };
            _inner.KeyDown += (s, e) => OnKeyDown(e);         // same args object: SuppressKeyPress reaches the EDIT
            _inner.KeyUp += (s, e) => OnKeyUp(e);
            _inner.KeyPress += (s, e) => OnKeyPress(e);
            _inner.TextChanged += (s, e) => { _inner.InvalidateCue(); OnTextChanged(e); };
            _inner.GotFocus += (s, e) => Invalidate();
            _inner.LostFocus += (s, e) => Invalidate();
            _inner.HandleCreated += (s, e) => ApplyMargins();
            Controls.Add(_inner);
            ApplyFont();
            Recolor();
        }

        // ── the field ────────────────────────────────────────────────────────────────────────────────────────

        public override string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value ?? ""; }
        }

        /// <summary>The placeholder, shown while the field is empty and unfocused.</summary>
        public string Hint
        {
            get { return _inner.Cue; }
            set
            {
                _inner.Cue = value ?? ""; _inner.InvalidateCue(); AccessibleDescription = value;
                // R6: the EDIT (the tab stop a screen reader lands on) is named by its hint unless the caller named it
                // (a managed property: no window style is touched)
                if (string.IsNullOrEmpty(_inner.AccessibleName) || _inner.AccessibleName == _autoName) { _autoName = value ?? ""; _inner.AccessibleName = _autoName; }
            }
        }

        private string _autoName = "";

        public int SelectionStart { get { return _inner.SelectionStart; } set { _inner.SelectionStart = value; } }
        public int SelectionLength { get { return _inner.SelectionLength; } set { _inner.SelectionLength = value; } }
        public int MaxLength { get { return _inner.MaxLength; } set { _inner.MaxLength = value; } }
        public void SelectAll() { _inner.SelectAll(); }

        /// <summary>Focuses the EDIT (the host is never the focus). Refused while the field is disabled.</summary>
        public new bool Focus() { return _inputEnabled && _inner.Focus(); }

        public override ContextMenuStrip ContextMenuStrip
        {
            get { return _inner.ContextMenuStrip; }
            set { _inner.ContextMenuStrip = value; }
        }

        /// <summary>False = the field takes no input: ReadOnly, not a tab stop, refuses focus, dimmed. NEVER Enabled = false
        /// (class notes).</summary>
        public bool InputEnabled
        {
            get { return _inputEnabled; }
            set
            {
                if (_inputEnabled == value) return;
                _inputEnabled = value;
                _inner.ReadOnly = !value;
                _inner.TabStop = value;
                _inner.Dead = !value;
                if (!value && _inner.Focused)
                {
                    // Release a focus held when the field dies (a chat closed mid-typing) — what Enabled = false did by itself.
                    var f = FindForm();
                    if (f != null) try { f.ActiveControl = null; } catch { }
                }
                Recolor();
            }
        }

        /// <summary>The EDIT's font size in points (the UI font), resolved at the window's DPI — set this, not Font: the
        /// host's Font property is not used, and the EDIT ignores font writes that do not come from here (class notes).</summary>
        public float FontPoints
        {
            get { return _points; }
            set { if (value > 0 && Math.Abs(value - _points) > 0.001f) { _points = value; ApplyFont(); } }
        }

        public FontStyle FontStyle
        {
            get { return _style; }
            set { if (value != _style) { _style = value; ApplyFont(); } }
        }

        private void ApplyFont()
        {
            _inner.SetOwnFont(FontHelper.UiAt(_points, Dpi.Of(this), _style));   // shared cached font: never dispose
            ApplyMargins();
            PerformLayout();
        }

        // ── theme, layout, paint ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Re-reads the palette (the owner calls it on a theme change).</summary>
        public void Recolor()
        {
            var p = ThemeHelper.Palette;
            _inner.SetOwnColors(_inputEnabled ? p.Card : p.Window, _inputEnabled ? p.Text : p.TextDisabled);
            _inner.CueColor = _inputEnabled ? p.TextFaint : p.TextDisabled;
            _inner.InvalidateCue();
            Invalidate();
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { ApplyFont(); Invalidate(); }

        // The window's DPI is only known once the control is placed in one: re-resolve the font then.
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplyFont(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyFont(); }

        private Rectangle Frame { get { return new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int dpi = Dpi.Of(this), px = Dpi.Px(FramePadX, dpi), py = Dpi.Px(FramePadY, dpi);
            int w = Math.Max(1, Width - 2 * px);
            if (_multiline)
            {
                // Never shorter than ONE line (review 1.5: the composer row is a fixed 64 px; at 175% the padding left an EDIT
                // shorter than the typed line). If the host is too short for line + padding, the padding gives way.
                int h = Math.Max(_inner.PreferredHeight, Height - 2 * py);
                _inner.SetBounds(px, Math.Max(0, (Height - h) / 2), w, h);
            }
            else _inner.SetBounds(px, Math.Max(0, (Height - _inner.Height) / 2), w, _inner.Height);   // single-line: the EDIT sizes itself to its font
        }

        /// <summary>The height a single-line field wants at its font: the EDIT's own height plus the frame padding.</summary>
        public int PreferredFieldHeight
        {
            get { return _inner.PreferredHeight + 2 * Dpi.Px(FramePadY, Dpi.Of(this)); }
        }

        private void ApplyMargins()
        {
            if (!_inner.IsHandleCreated) return;
            int m = Dpi.Px(TextMargin, Dpi.Of(this));
            try { SendMessage(_inner.Handle, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), (IntPtr)((m & 0xFFFF) | (m << 16))); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, p.Window);   // step 5.9: the rounded frame's corners
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool focused = _inner.Focused && _inputEnabled;
            using (var path = DrawHelper.RoundedRect(Frame, Dpi.Px(Radius, dpi)))
            {
                // Disabled: the flat window colour inside a plain hairline — visibly not a field you can type into.
                using (var b = new SolidBrush(_inputEnabled ? p.Card : p.Window)) g.FillPath(b, path);
                using (var pen = new Pen(focused ? p.AccentGlyph : p.Divider, focused ? Dpi.F(1.5f, dpi) : 1f)) g.DrawPath(pen, path);
            }
        }

        /// <summary>A tap or click on the frame around the EDIT. MouseDown is raised FIRST (the owner records a genuine touch
        /// there), then the EDIT is focused. ⚠ The keyboard is NOT launched from here any more: TelegArm starting TabTip
        /// itself kills native EDIT typing on RT (device result 2026-10-14). A tap on the EDIT itself gets Windows' own
        /// keyboard; a tap on the thin frame only focuses.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_inputEnabled) return;
            _inner.Focus();
        }

        private const int EM_SETMARGINS = 0x00D3, EC_LEFTMARGIN = 0x0001, EC_RIGHTMARGIN = 0x0002;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>The EDIT. Additions to a plain TextBox: the cue after its own WM_PAINT (only while empty and unfocused);
        /// colours and font that only its owner may set; and, while <see cref="Dead"/>, no mouse focus or context menu.</summary>
        private sealed class CueTextBox : TextBox
        {
            public string Cue = "";
            public Color CueColor = ThemeHelper.Palette.TextFaint;   // (the owner sets it from the palette before the first paint)
            public bool Dead;
            private bool _own;
            private const int WM_PAINT = 0x000F, WM_CONTEXTMENU = 0x007B;
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
                              WM_RBUTTONDBLCLK = 0x0206, WM_MBUTTONDOWN = 0x0207;

            public void InvalidateCue() { if (IsHandleCreated && TextLength == 0) Invalidate(); }

            public void SetOwnColors(Color back, Color fore)
            {
                _own = true;
                try { base.BackColor = back; base.ForeColor = fore; }
                finally { _own = false; }
            }

            public void SetOwnFont(Font f)
            {
                if (ReferenceEquals(base.Font, f)) return;
                _own = true;
                try { base.Font = f; }
                finally { _own = false; }
            }

            // Writes from anyone else (a form-wide theme walk, DpiScaler) are ignored — see the class notes.
            public override Color BackColor { get { return base.BackColor; } set { if (_own) base.BackColor = value; } }
            public override Color ForeColor { get { return base.ForeColor; } set { if (_own) base.ForeColor = value; } }
            public override Font Font { get { return base.Font; } set { if (_own) base.Font = value; } }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); InvalidateCue(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); InvalidateCue(); }

            protected override void WndProc(ref Message m)
            {
                if (Dead)
                {
                    switch (m.Msg)
                    {
                        case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: case WM_RBUTTONDOWN: case WM_RBUTTONUP:
                        case WM_RBUTTONDBLCLK: case WM_MBUTTONDOWN: case WM_CONTEXTMENU:
                            return;   // a disabled field takes no click focus and opens no menu
                    }
                }
                base.WndProc(ref m);
                if (m.Msg != WM_PAINT || Cue.Length == 0 || Focused || TextLength != 0) return;
                try
                {
                    using (var g = Graphics.FromHwnd(Handle))
                    {
                        var r = ClientRectangle;
                        int inset = (int)(SendMessage(Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xFFFF);
                        r = new Rectangle(r.X + inset, r.Y, Math.Max(1, r.Width - inset), r.Height);
                        TextRenderer.DrawText(g, Cue, Font, r, CueColor, BackColor,
                            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    }
                }
                catch { }
            }

            private const int EM_GETMARGINS = 0x00D4;
        }
    }
}
