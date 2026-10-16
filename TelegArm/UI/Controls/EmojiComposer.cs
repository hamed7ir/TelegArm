using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>A text field that is not a native EDIT — the touch-keyboard workarounds (focus checks, refocus clearing, the
    /// touch-scroll exemption) treat it like a TextBoxBase.</summary>
    internal interface ITextInputTarget
    {
        bool InputEnabled { get; }
    }

    /// <summary>The colour-emoji composer (plan "E1"). An owner-drawn editor that takes WM_CHAR itself instead of hosting a native
    /// EDIT — the device trip of 2026-10-14 showed that is what stays alive on Windows RT 8.1 under the touch keyboard (the
    /// Input Lab's L6), and it is the only way to show colour emoji while typing (GDI cannot draw colour fonts; the emoji are
    /// the bundled Noto images, <see cref="EmojiRenderer"/>).
    ///
    /// Model: one string with "\n" line breaks, a caret and a selection anchor, an undo/redo list (typing is coalesced).
    /// Layout: paragraphs word-wrapped into lines of segments — text runs drawn by TextRenderer (shaping, Persian joining)
    /// and emoji images. A paragraph whose first strong character is right-to-left is laid out from the right.
    /// ⚠ The caret inside a text run is placed by the width of its logical prefix: exact for one-direction runs,
    ///   approximate inside mixed-direction runs (digits inside Persian). Uniscribe ScriptString is the later fix.
    /// Keys the host's KeyDown handles first (Enter to send sets SuppressKeyPress, which also drops the WM_CHAR); the rest
    /// are handled here. A touch tap opens the touch keyboard itself (Windows does not do that for a non-EDIT window) —
    /// never with a hardware keyboard attached.</summary>
    internal sealed class EmojiComposer : Control, ITextInputTarget, IDpiAware
    {
        private const int WM_CHAR = 0x0102;
        private const int PadX = 12, PadY = 8, Radius = 8, CaretW = 2, MaxUndo = 100;

        private string _text = "";
        private int _caret, _anchor;
        private int _scrollY;
        private int _wantX = -1;                 // the column Up/Down keep aiming for
        private bool _inputEnabled = true;
        private string _hint = "";
        private float _pt = 11f;
        private char _high;                      // a high surrogate waiting for its low half
        private bool _dragging;
        private bool _caretOn = true;
        private readonly Timer _blink = new Timer { Interval = 530 };
        private List<Line> _lines;
        private int _layoutWidth = -1;

        private readonly List<Snap> _undo = new List<Snap>(), _redo = new List<Snap>();
        private int _lastEditTick;
        private bool _lastWasTyping;

        private struct Snap { public string Text; public int Caret, Anchor; }

        private sealed class Seg
        {
            public int Start, Length;            // in _text
            public string Text;                  // text runs; null for an emoji
            public Image Emoji;
            public int X, Width;
            public bool Rtl;
            public int[] Stops;                  // valid caret positions (absolute indexes), lazily with Xs
            public int[] Xs;
        }

        private sealed class Line
        {
            public int Start, End;               // [Start, End) in _text, the "\n" excluded
            public int Y, Height, TextHeight;
            public Font Font;
            public bool Rtl;
            public readonly List<Seg> Segs = new List<Seg>();
        }

        public EmojiComposer()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            Cursor = Cursors.IBeam;
            AccessibleRole = AccessibleRole.Text;
            _blink.Tick += (s, e) => { _caretOn = !_caretOn; InvalidateCaret(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _blink.Stop(); _blink.Dispose(); }
            base.Dispose(disposing);
        }

        // ── the API InputBox uses ────────────────────────────────────────────────────────────────────────────

        /// <summary>The text, with "\n" line breaks ("\r\n" and "\r" are normalised on the way in).</summary>
        public override string Text
        {
            get { return _text; }
            set
            {
                string v = Normalize(value);
                if (v == _text) return;
                PushUndo();   // a host edit (Ctrl+B wrapping, paste, reply fill) is one undo step; "" (after send) too
                _text = v;
                _caret = _anchor = 0;   // as a native EDIT: setting the text puts the caret at the start
                _redo.Clear(); _lastWasTyping = false;
                Changed();
            }
        }

        public string Hint { get { return _hint; } set { _hint = value ?? ""; if (_text.Length == 0) Invalidate(); } }

        /// <summary>The size in points (the font itself is chosen per paragraph: Roboto, or Vazirmatn for Persian).</summary>
        public float FontPoints { get { return _pt; } set { _pt = value; Relayout(); } }

        /// <summary>Read-only when false (the "disabled" composer): never Enabled = false, which would also drop focus rules.</summary>
        public bool InputEnabled
        {
            get { return _inputEnabled; }
            set { if (_inputEnabled == value) return; _inputEnabled = value; Cursor = value ? Cursors.IBeam : Cursors.Default; Invalidate(); }
        }

        public int SelectionStart
        {
            get { return Math.Min(_caret, _anchor); }
            set { int v = Clamp(value); _caret = _anchor = v; Moved(); }
        }

        public int SelectionLength
        {
            get { return Math.Abs(_caret - _anchor); }
            set { int s = SelectionStart; _anchor = s; _caret = Clamp(s + Math.Max(0, value)); Moved(); }
        }

        public string SelectedText
        {
            get { return _text.Substring(SelectionStart, SelectionLength); }
            set { Replace(Normalize(value), false); }
        }

        public void SelectAll() { _anchor = 0; _caret = _text.Length; Moved(); }

        // ── input ────────────────────────────────────────────────────────────────────────────────────────────

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                case Keys.Delete: case Keys.Enter: case Keys.Back:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CHAR)
            {
                // The path the device proved: the character comes to US, never to a native EDIT.
                OnChar((char)(m.WParam.ToInt64() & 0xFFFF));
                return;
            }
            base.WndProc(ref m);
        }

        private void OnChar(char ch)
        {
            if (!_inputEnabled) return;
            if (char.IsHighSurrogate(ch)) { _high = ch; return; }
            if (char.IsLowSurrogate(ch))
            {
                if (_high != '\0') { Type(new string(new[] { _high, ch })); _high = '\0'; }
                return;
            }
            _high = '\0';
            if (ch == '\b') { if ((ModifierKeys & Keys.Control) == 0) Backspace(); return; }
            if (ch == '\r' || ch == '\n') { Type("\n"); return; }
            if (ch < ' ' || ch == (char)0x7F) return;   // Ctrl+letter chords and the like
            Type(ch.ToString());
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);   // the host first: Enter-to-send, Ctrl+B/I/K, image paste
            if (e.Handled || e.SuppressKeyPress) return;
            bool shift = e.Shift, ctrl = e.Control;
            bool handled = true;
            switch (e.KeyCode)
            {
                case Keys.Left: MoveTo(ctrl ? WordLeft(_caret) : StepLeft(_caret), shift); break;
                case Keys.Right: MoveTo(ctrl ? WordRight(_caret) : StepRight(_caret), shift); break;
                case Keys.Up: MoveVertical(-1, shift); break;
                case Keys.Down: MoveVertical(1, shift); break;
                case Keys.PageUp: MoveVertical(-Math.Max(1, VisibleLines() - 1), shift); break;
                case Keys.PageDown: MoveVertical(Math.Max(1, VisibleLines() - 1), shift); break;
                case Keys.Home: MoveTo(ctrl ? 0 : LineOf(_caret).Start, shift); break;
                case Keys.End: MoveTo(ctrl ? _text.Length : LineEndStop(LineOf(_caret)), shift); break;
                case Keys.Delete: if (_inputEnabled) DeleteForward(ctrl); break;
                case Keys.Back: if (ctrl && _inputEnabled) { DeleteRange(WordLeft(_caret), _caret); } else handled = false; break;
                default:
                    handled = false;
                    if (ctrl && !e.Alt)
                    {
                        handled = true;
                        switch (e.KeyCode)
                        {
                            case Keys.A: SelectAll(); break;
                            case Keys.C: Copy(); break;
                            case Keys.X: if (_inputEnabled) { Copy(); Replace("", false); } break;
                            case Keys.V: if (_inputEnabled) Paste(); break;
                            case Keys.Z: if (_inputEnabled) { if (shift) Redo(); else Undo(); } break;
                            case Keys.Y: if (_inputEnabled) Redo(); break;
                            default: handled = false; break;
                        }
                    }
                    break;
            }
            if (handled) { e.Handled = true; if (ctrl) e.SuppressKeyPress = true; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && ExpandActive && ExpandRect.Contains(e.Location))
            {
                // Step 2.7d: the expand glyph. Not a caret move, and no touch keyboard here — the window takes the input.
                var h = ExpandClick;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
            if (!Focused) Focus();
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Location);
            if (e.Clicks >= 2) { _anchor = WordLeft(Math.Min(_text.Length, StepRight(i))); _caret = WordRight(i); Moved(); return; }
            MoveTo(i, (ModifierKeys & Keys.Shift) != 0);
            _dragging = true;
            Capture = true;
            // Windows opens the touch keyboard only for a native EDIT. This control opens it itself (proved on the device:
            // L6), but never over a hardware keyboard.
            try
            {
                if (_inputEnabled && TouchKeyboard.LastInputWasTouch() && !TouchKeyboard.HasHardwareKeyboard())
                    TouchKeyboard.Show();
            }
            catch { }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) { _caret = HitTest(e.Location); _wantX = -1; EnsureCaretVisible(); Invalidate(); return; }
            if (_showExpand) SetExpandHot(ExpandActive && ExpandRect.Contains(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetExpandHot(false);
        }

        // ── step 2.7d (plan R8): the expand glyph, top-right — opens the composer's editor window ──────────────────

        private const int ExpandBox = 24;
        private bool _showExpand, _expandHot;

        /// <summary>Step 2.7d: an expand glyph in the top-right corner (the text wraps short of it); a click or tap raises
        /// <see cref="ExpandClick"/>. Drawn and hit only while input is enabled.</summary>
        public bool ShowExpandGlyph
        {
            get { return _showExpand; }
            set { if (_showExpand == value) return; _showExpand = value; Relayout(); }
        }

        public event EventHandler ExpandClick;

        private bool ExpandActive { get { return _showExpand && _inputEnabled; } }

        /// <summary>The glyph's box, centred on the first line, so it stays put while the composer grows.</summary>
        private Rectangle ExpandRect
        {
            get
            {
                int d = Dpi_, box = Dpi.Px(ExpandBox, d);
                int top = Dpi.Px(PadY, d) + (LineHeightPx() - box) / 2;
                return new Rectangle(Width - Dpi.Px(6, d) - box, Math.Max(Dpi.Px(2, d), top), box, box);
            }
        }

        private void SetExpandHot(bool hot)
        {
            if (hot == _expandHot) return;
            _expandHot = hot;
            Cursor = hot ? Cursors.Hand : (_inputEnabled ? Cursors.IBeam : Cursors.Default);
            Invalidate();
        }

        /// <summary>Two arrows out to opposite corners — the "open bigger" mark, drawn (no font glyph: RT 8.1).</summary>
        private void PaintExpand(Graphics g, Palette p, int d)
        {
            var r = ExpandRect;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_expandHot)
                using (var hb = new SolidBrush(p.Hover))
                using (var hp = DrawHelper.RoundedRect(r, r.Width / 2))
                    g.FillPath(hb, hp);
            float inset = r.Width * 0.27f, h = r.Width * 0.17f;
            float l = r.X + inset, t = r.Y + inset, rr = r.Right - inset, bt = r.Bottom - inset;
            using (var pen = new Pen(_expandHot ? p.AccentGlyph : p.TextDim, Dpi.F(1.5f, d)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(rr - h, t), new PointF(rr, t), new PointF(rr, t + h) });
                g.DrawLine(pen, rr, t, rr - (rr - l) * 0.36f, t + (bt - t) * 0.36f);
                g.DrawLines(pen, new[] { new PointF(l, bt - h), new PointF(l, bt), new PointF(l + h, bt) });
                g.DrawLine(pen, l, bt, l + (rr - l) * 0.36f, bt - (bt - t) * 0.36f);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(_scrollY - Math.Sign(e.Delta) * LineHeightPx() * 3);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _caretOn = true; _blink.Start(); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _blink.Stop(); _high = '\0'; Invalidate(); }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int w = TextArea.Width;
            if (w != _layoutWidth) { _lines = null; CheckContentHeight(); }   // a height-only change keeps the wrap
            EnsureCaretVisible();
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Relayout(); }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { Relayout(); }

        // ── editing ──────────────────────────────────────────────────────────────────────────────────────────

        private void Type(string s)
        {
            bool coalesce = _lastWasTyping && SelectionLength == 0 && unchecked(Environment.TickCount - _lastEditTick) < 1500 && s != "\n";
            Replace(s, coalesce);
            _lastWasTyping = s != "\n";
        }

        /// <summary>Replaces the selection with <paramref name="s"/> (inserts at the caret when nothing is selected).</summary>
        private void Replace(string s, bool coalesce)
        {
            if (!_inputEnabled) return;
            int a = SelectionStart, len = SelectionLength;
            if (len == 0 && s.Length == 0) return;
            if (!coalesce) PushUndo();
            _text = _text.Substring(0, a) + s + _text.Substring(a + len);
            _caret = _anchor = a + s.Length;
            _redo.Clear();
            _lastEditTick = Environment.TickCount;
            _lastWasTyping = false;
            Changed();
        }

        private void DeleteRange(int from, int to)
        {
            if (from == to) return;
            _anchor = Math.Min(from, to); _caret = Math.Max(from, to);
            Replace("", false);
        }

        private void Backspace()
        {
            if (SelectionLength > 0) { Replace("", false); return; }
            if (_caret == 0) return;
            DeleteRange(StepLeft(_caret), _caret);
        }

        private void DeleteForward(bool word)
        {
            if (SelectionLength > 0) { Replace("", false); return; }
            if (_caret >= _text.Length) return;
            DeleteRange(_caret, word ? WordRight(_caret) : StepRight(_caret));
        }

        private void Copy()
        {
            if (SelectionLength == 0) return;
            try { Clipboard.SetText(SelectedText.Replace("\n", Environment.NewLine)); } catch { }
        }

        private void Paste()
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (!string.IsNullOrEmpty(t)) Replace(Normalize(t), false);
        }

        private void PushUndo()
        {
            _undo.Add(new Snap { Text = _text, Caret = _caret, Anchor = _anchor });
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }

        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        /// <summary>Forgets the history (the editor window loads its text without making that an undo step).</summary>
        public void ClearUndo() { _undo.Clear(); _redo.Clear(); }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(new Snap { Text = _text, Caret = _caret, Anchor = _anchor });
            var snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);   // before Restore: its TextChanged must see the history after the step
            Restore(snap);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(new Snap { Text = _text, Caret = _caret, Anchor = _anchor });
            var snap = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(snap);
        }

        private void Restore(Snap s)
        {
            _text = s.Text; _caret = Clamp(s.Caret); _anchor = Clamp(s.Anchor);
            _lastWasTyping = false;
            Changed();
        }

        private void Changed()
        {
            _lines = null;
            _wantX = -1;
            EnsureCaretVisible();
            Invalidate();
            OnTextChanged(EventArgs.Empty);
            CheckContentHeight();
        }

        private void Moved()
        {
            _wantX = -1;
            _lastWasTyping = false;
            EnsureCaretVisible();
            Invalidate();
        }

        private void MoveTo(int i, bool extend)
        {
            _caret = Clamp(i);
            if (!extend) _anchor = _caret;
            Moved();
        }

        private void MoveVertical(int lines, bool extend)
        {
            var ls = Lines();
            int li = LineIndexOf(_caret);
            if (_wantX < 0) _wantX = CaretX(_caret);
            int target = Math.Max(0, Math.Min(ls.Count - 1, li + lines));
            int keep = _wantX;
            if (target == li) _caret = lines < 0 ? ls[li].Start : ls[li].End;
            else _caret = IndexAtX(ls[target], keep);
            if (!extend) _anchor = _caret;
            EnsureCaretVisible();
            Invalidate();
            _wantX = keep;
        }

        // ── text elements: never split a surrogate pair, a combining sequence or an emoji ─────────────────────

        private int StepLeft(int i)
        {
            if (i <= 0) return 0;
            foreach (var sp in EmojiRenderer.EmojiSpans(_text))
                if (sp.CharIndex < i && i <= sp.CharIndex + sp.CharLength) return sp.CharIndex;
            int prev = 0;
            foreach (int b in StringInfo.ParseCombiningCharacters(_text)) { if (b >= i) break; prev = b; }
            return prev;
        }

        private int StepRight(int i)
        {
            if (i >= _text.Length) return _text.Length;
            foreach (var sp in EmojiRenderer.EmojiSpans(_text))
                if (sp.CharIndex <= i && i < sp.CharIndex + sp.CharLength) return sp.CharIndex + sp.CharLength;
            foreach (int b in StringInfo.ParseCombiningCharacters(_text)) if (b > i) return b;
            return _text.Length;
        }

        private int WordLeft(int i)
        {
            int j = i;
            while (j > 0 && char.IsWhiteSpace(_text[j - 1])) j--;
            while (j > 0 && !char.IsWhiteSpace(_text[j - 1])) j--;
            return j;
        }

        private int WordRight(int i)
        {
            int j = i, n = _text.Length;
            while (j < n && !char.IsWhiteSpace(_text[j])) j++;
            while (j < n && char.IsWhiteSpace(_text[j]) && _text[j] != '\n') j++;
            return j;
        }

        // ── layout ───────────────────────────────────────────────────────────────────────────────────────────

        private int Dpi_ { get { return Dpi.Of(this); } }
        private Rectangle Frame { get { return new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)); } }
        private Rectangle TextArea
        {
            get
            {
                int d = Dpi_, px = Dpi.Px(PadX, d), py = Dpi.Px(PadY, d);
                int right = _showExpand ? Dpi.Px(ExpandBox + 6, d) : px;   // step 2.7d: the text wraps short of the glyph
                return new Rectangle(px, py, Math.Max(1, Width - px - right), Math.Max(1, Height - 2 * py));
            }
        }

        private void Relayout() { _lines = null; Invalidate(); }

        /// <summary>The first line's segments as "text|x|width" (probe diagnostics).</summary>
        internal string DebugFirstLine()
        {
            var sb = new StringBuilder();
            foreach (var s in Lines()[0].Segs) sb.Append(s.Emoji != null ? "[emoji]" : "'" + s.Text + "'").Append('@').Append(s.X).Append('+').Append(s.Width).Append(' ');
            return sb.ToString();
        }

        private static bool IsRtlChar(char c) { return (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF); }

        private static bool ParagraphRtl(string p)
        {
            foreach (char c in p)
            {
                if (IsRtlChar(c)) return true;
                if (char.IsLetter(c)) return false;
            }
            return false;
        }

        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        // ⚠ MEASURE ON THE DEVICE CONTEXT WE DRAW ON. TextRenderer.MeasureText without one uses a screen DC, and a pixel-unit
        //   font measured there came out 63 px for "Hello" where the same font drawn into a bitmap inked 49 px (measured
        //   2026-10-14) — the caret and emoji then sat a word-width off. While painting, _g is the paint Graphics; otherwise a
        //   window Graphics (what the on-screen double buffer is compatible with). The layout is redone when the context's
        //   DPI changes (a DrawToBitmap capture paints into a 96-dpi bitmap).
        private Graphics _g;
        private float _layoutDcDpi = -1;
        private static readonly Bitmap OnePixel = new Bitmap(1, 1);

        private int Measure(string s, Font f, bool rtl)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (_g != null) return MeasureOn(_g, s, f, rtl);
            using (var g = IsHandleCreated ? CreateGraphics() : Graphics.FromImage(OnePixel)) return MeasureOn(g, s, f, rtl);
        }

        private static int MeasureOn(Graphics g, string s, Font f, bool rtl)
        {
            // GDI drops trailing spaces from the measurement; count them by hand.
            string core = s.TrimEnd(' ');
            int w = core.Length == 0 ? 0 : TextRenderer.MeasureText(g, core, f, Size.Empty, Flags | (rtl ? TextFormatFlags.RightToLeft : 0)).Width;
            int spaces = s.Length - core.Length;
            if (spaces > 0)
                w += spaces * Math.Max(1, TextRenderer.MeasureText(g, "a a", f, Size.Empty, Flags).Width - TextRenderer.MeasureText(g, "aa", f, Size.Empty, Flags).Width);
            return w;
        }

        private int TextHeightOf(Font f)
        {
            if (_g != null) return TextRenderer.MeasureText(_g, "Ag", f, Size.Empty, Flags).Height;
            using (var g = IsHandleCreated ? CreateGraphics() : Graphics.FromImage(OnePixel)) return TextRenderer.MeasureText(g, "Ag", f, Size.Empty, Flags).Height;
        }

        private int LineHeightPx()
        {
            return TextHeightOf(FontHelper.UiAt(_pt, Dpi_)) + Dpi.Px(2, Dpi_);
        }

        private List<Line> Lines()
        {
            int width = TextArea.Width;
            float dcDpi = _g != null ? _g.DpiX : -2;
            if (_lines != null && _layoutWidth == width && (_g == null || dcDpi == _layoutDcDpi)) return _lines;
            _layoutWidth = width;
            if (_g != null) _layoutDcDpi = dcDpi;
            var lines = new List<Line>();
            int d = Dpi_, y = 0, pos = 0;
            foreach (string para in _text.Split('\n'))
            {
                var font = FontHelper.ForAt(para.Length > 0 ? para : "a", _pt, d);
                bool rtl = ParagraphRtl(para);
                int textH = TextHeightOf(font);
                int lineH = textH + Dpi.Px(2, d);
                int emojiW = textH;
                // atoms: emoji, or a word with its trailing spaces
                var atoms = new List<Seg>();
                var spans = EmojiRenderer.EmojiSpans(para);
                int k = 0, si = 0;
                while (k < para.Length)
                {
                    if (si < spans.Count && spans[si].CharIndex == k)
                    {
                        atoms.Add(new Seg { Start = pos + k, Length = spans[si].CharLength, Emoji = spans[si].Emoji, Width = emojiW });
                        k += spans[si].CharLength; si++;
                        continue;
                    }
                    int stop = si < spans.Count ? spans[si].CharIndex : para.Length;
                    int e = k;
                    while (e < stop && !char.IsWhiteSpace(para[e])) e++;
                    while (e < stop && char.IsWhiteSpace(para[e])) e++;
                    if (e == k) e = k + 1;
                    string w = para.Substring(k, e - k);
                    atoms.Add(new Seg { Start = pos + k, Length = e - k, Text = w, Width = Measure(w, font, rtl), Rtl = rtl });
                    k = e;
                }
                // wrap greedily; a word wider than the line is split by text element
                var line = NewLine(pos, y, lineH, textH, font, rtl);
                int x = 0;
                foreach (var a0 in atoms)
                {
                    var queue = new Queue<Seg>(); queue.Enqueue(a0);
                    while (queue.Count > 0)
                    {
                        var a = queue.Dequeue();
                        if (x > 0 && x + a.Width > width && !(a.Text != null && a.Text.Trim().Length == 0))
                        {
                            line.End = a.Start; lines.Add(line); y += lineH;
                            line = NewLine(a.Start, y, lineH, textH, font, rtl); x = 0;
                        }
                        if (x == 0 && a.Width > width && a.Text != null && a.Length > 1)
                        {
                            int fit = FitChars(a.Text, font, rtl, width);
                            var head = new Seg { Start = a.Start, Length = fit, Text = a.Text.Substring(0, fit), Rtl = rtl };
                            head.Width = Measure(head.Text, font, rtl);
                            var tail = new Seg { Start = a.Start + fit, Length = a.Length - fit, Text = a.Text.Substring(fit), Rtl = rtl };
                            tail.Width = Measure(tail.Text, font, rtl);
                            Append(line, head); x += head.Width;
                            line.End = tail.Start; lines.Add(line); y += lineH;
                            line = NewLine(tail.Start, y, lineH, textH, font, rtl); x = 0;
                            queue.Enqueue(tail);
                            continue;
                        }
                        Append(line, a); x += a.Width;
                    }
                }
                line.End = pos + para.Length;
                lines.Add(line); y += lineH;
                pos += para.Length + 1;
            }
            foreach (var ln in lines) PlaceSegments(ln, width);
            _lines = lines;
            return lines;
        }

        private static Line NewLine(int start, int y, int h, int textH, Font f, bool rtl)
        {
            return new Line { Start = start, End = start, Y = y, Height = h, TextHeight = textH, Font = f, Rtl = rtl };
        }

        /// <summary>Adds an atom, merging consecutive text atoms into one run (drawn and shaped together).</summary>
        private void Append(Line line, Seg a)
        {
            var last = line.Segs.Count > 0 ? line.Segs[line.Segs.Count - 1] : null;
            if (last != null && last.Text != null && a.Text != null && last.Start + last.Length == a.Start)
            {
                last.Text += a.Text; last.Length += a.Length; last.Width = Measure(last.Text, line.Font, line.Rtl);
                return;
            }
            line.Segs.Add(new Seg { Start = a.Start, Length = a.Length, Text = a.Text, Emoji = a.Emoji, Width = a.Width, Rtl = a.Rtl });
        }

        private int FitChars(string s, Font f, bool rtl, int width)
        {
            int[] b = StringInfo.ParseCombiningCharacters(s);
            int best = b.Length > 1 ? b[1] : s.Length;
            for (int i = 1; i < b.Length; i++)
            {
                if (Measure(s.Substring(0, b[i]), f, rtl) > width) break;
                best = b[i];
            }
            return Math.Max(1, best);
        }

        /// <summary>Visual x of each segment: left to right, or from the right edge for a right-to-left paragraph.</summary>
        private static void PlaceSegments(Line line, int width)
        {
            if (!line.Rtl) { int x = 0; foreach (var s in line.Segs) { s.X = x; x += s.Width; } }
            else { int x = width; foreach (var s in line.Segs) { x -= s.Width; s.X = x; } }
        }

        private int LineIndexOf(int i)
        {
            var ls = Lines();
            for (int k = 0; k < ls.Count; k++)
            {
                bool last = k == ls.Count - 1;
                if (i >= ls[k].Start && (i < ls[k].End || i == ls[k].End && (last || ls[k + 1].Start > i))) return k;
            }
            return ls.Count - 1;
        }

        private Line LineOf(int i) { return Lines()[LineIndexOf(i)]; }

        /// <summary>End on a wrapped line stops before its last character (the next line starts at End).</summary>
        private int LineEndStop(Line line)
        {
            bool paragraphEnd = line.End >= _text.Length || _text[line.End] == '\n';
            return paragraphEnd ? line.End : Math.Max(line.Start, StepLeft(line.End));
        }

        /// <summary>Caret positions inside a segment and their x (relative to the text area), computed when first needed.</summary>
        private void EnsureStops(Line line, Seg s)
        {
            if (s.Stops != null) return;
            if (s.Text == null)
            {
                s.Stops = new[] { s.Start, s.Start + s.Length };
                s.Xs = line.Rtl ? new[] { s.X + s.Width, s.X } : new[] { s.X, s.X + s.Width };
                return;
            }
            int[] b = StringInfo.ParseCombiningCharacters(s.Text);
            s.Stops = new int[b.Length + 1];
            s.Xs = new int[b.Length + 1];
            for (int i = 0; i <= b.Length; i++)
            {
                int rel = i < b.Length ? b[i] : s.Text.Length;
                int w = rel == 0 ? 0 : Measure(s.Text.Substring(0, rel), line.Font, s.Rtl);
                s.Stops[i] = s.Start + rel;
                s.Xs[i] = s.Rtl ? s.X + s.Width - w : s.X + w;
            }
        }

        private int CaretX(int i)
        {
            var line = LineOf(i);
            foreach (var s in line.Segs)
            {
                if (i < s.Start || i > s.Start + s.Length) continue;
                EnsureStops(line, s);
                for (int k = 0; k < s.Stops.Length; k++) if (s.Stops[k] >= i) return s.Xs[k];
            }
            if (line.Segs.Count == 0) return line.Rtl ? TextArea.Width : 0;
            var lastSeg = line.Segs[line.Segs.Count - 1];
            return line.Rtl ? lastSeg.X : lastSeg.X + lastSeg.Width;
        }

        private int IndexAtX(Line line, int x)
        {
            int best = line.Start, bestD = int.MaxValue;
            if (line.Segs.Count == 0) return line.Start;
            foreach (var s in line.Segs)
            {
                EnsureStops(line, s);
                for (int k = 0; k < s.Stops.Length; k++)
                {
                    int dist = Math.Abs(s.Xs[k] - x);
                    if (dist < bestD) { bestD = dist; best = s.Stops[k]; }
                }
            }
            return Math.Min(best, line.End);
        }

        private int HitTest(Point p)
        {
            var ta = TextArea;
            var ls = Lines();
            int y = p.Y - ta.Y + _scrollY;
            int li = ls.Count - 1;
            for (int k = 0; k < ls.Count; k++) if (y < ls[k].Y + ls[k].Height) { li = k; break; }
            if (y < 0) li = 0;
            return IndexAtX(ls[li], p.X - ta.X);
        }

        private int VisibleLines() { return Math.Max(1, TextArea.Height / Math.Max(1, LineHeightPx())); }

        /// <summary>Raised when <see cref="ContentHeight"/> changes (text or width) — the host grows the composer (plan E3).</summary>
        public event EventHandler ContentHeightChanged;
        private int _lastContentHeight = -1;

        private void CheckContentHeight()
        {
            if (!IsHandleCreated) return;
            int h = ContentHeight;
            if (h == _lastContentHeight) return;
            _lastContentHeight = h;
            var ev = ContentHeightChanged;
            if (ev != null) ev(this, EventArgs.Empty);
        }

        /// <summary>The height <paramref name="lines"/> lines of the UI font need, frame padding included (the grow cap).</summary>
        public int HeightForLines(int lines) { return lines * LineHeightPx() + 2 * Dpi.Px(PadY, Dpi_); }

        /// <summary>The height the whole text needs (for auto-grow, plan E3), in pixels, frame padding included.</summary>
        public int ContentHeight
        {
            get
            {
                var ls = Lines();
                var last = ls[ls.Count - 1];
                return last.Y + last.Height + 2 * Dpi.Px(PadY, Dpi_);
            }
        }

        private void EnsureCaretVisible()
        {
            if (!IsHandleCreated) return;
            var line = LineOf(_caret);
            int view = TextArea.Height;
            if (line.Y < _scrollY) ScrollTo(line.Y);
            else if (line.Y + line.Height > _scrollY + view) ScrollTo(line.Y + line.Height - view);
        }

        private void ScrollTo(int y)
        {
            var ls = Lines();
            var last = ls[ls.Count - 1];
            int max = Math.Max(0, last.Y + last.Height - TextArea.Height);
            int v = Math.Max(0, Math.Min(max, y));
            if (v != _scrollY) { _scrollY = v; Invalidate(); }
        }

        // ── painting ─────────────────────────────────────────────────────────────────────────────────────────

        private void InvalidateCaret() { if (Focused && SelectionLength == 0) Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            _g = e.Graphics;
            try { PaintComposer(e.Graphics); }
            finally { _g = null; }
        }

        private void PaintComposer(Graphics g)
        {
            var p = ThemeHelper.Palette;
            int d = Dpi_;
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool focused = Focused && _inputEnabled;
            using (var path = DrawHelper.RoundedRect(Frame, Dpi.Px(Radius, d)))
            {
                using (var b = new SolidBrush(_inputEnabled ? p.Card : p.Window)) g.FillPath(b, path);
                using (var pen = new Pen(focused ? p.AccentGlyph : p.Divider, focused ? Dpi.F(1.5f, d) : 1f)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;

            var ta = TextArea;
            var oldClip = g.Clip;
            g.SetClip(ta);
            if (_text.Length == 0 && !Focused && _hint.Length > 0)
            {
                var hf = FontHelper.ForAt(_hint, _pt, d);
                TextRenderer.DrawText(g, _hint, hf, new Rectangle(ta.X, ta.Y, ta.Width, LineHeightPx()), p.TextDim,
                    Flags | TextFormatFlags.EndEllipsis | (ParagraphRtl(_hint) ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : 0));
            }
            else
            {
                int selA = SelectionStart, selB = selA + SelectionLength;
                var ink = _inputEnabled ? p.Text : p.TextDim;
                foreach (var line in Lines())
                {
                    int top = ta.Y + line.Y - _scrollY;
                    if (top + line.Height < ta.Y || top > ta.Bottom) continue;
                    int ty = top + (line.Height - line.TextHeight) / 2;
                    if (selB > selA && selB > line.Start && selA < line.End + 1)
                        PaintSelection(g, line, selA, selB, ta.X, top, p.AccentSoft);
                    foreach (var s in line.Segs)
                    {
                        if (s.Emoji != null)
                        {
                            int sz = Math.Min(s.Width, line.TextHeight);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(s.Emoji, new Rectangle(ta.X + s.X, ty + (line.TextHeight - sz) / 2, sz, sz));
                        }
                        else
                        {
                            var r = new Rectangle(ta.X + s.X, ty, s.Width + Dpi.Px(2, d), line.TextHeight);
                            TextRenderer.DrawText(g, s.Text, line.Font, r, ink, Flags | (s.Rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : 0));
                        }
                    }
                }
                if (Focused && _caretOn && SelectionLength == 0)
                {
                    var line = LineOf(_caret);
                    int x = ta.X + CaretX(_caret), top = ta.Y + line.Y - _scrollY;
                    using (var b = new SolidBrush(p.AccentGlyph)) g.FillRectangle(b, x, top + Dpi.Px(2, d), Dpi.Px(CaretW, d), line.Height - Dpi.Px(4, d));
                }
            }
            g.Clip = oldClip;
            if (ExpandActive) PaintExpand(g, p, d);
        }

        private void PaintSelection(Graphics g, Line line, int selA, int selB, int ox, int top, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                foreach (var s in line.Segs)
                {
                    int a = Math.Max(selA, s.Start), z = Math.Min(selB, s.Start + s.Length);
                    if (z <= a) continue;
                    EnsureStops(line, s);
                    int xa = XAt(s, a), xz = XAt(s, z);
                    g.FillRectangle(b, ox + Math.Min(xa, xz), top, Math.Abs(xz - xa), line.Height);
                }
                // a selected line break: a small block at the line's end
                if (line.End < _text.Length && selA <= line.End && selB > line.End)
                {
                    int x = line.Rtl ? (line.Segs.Count > 0 ? line.Segs[line.Segs.Count - 1].X : TextArea.Width) - Dpi.Px(6, Dpi_)
                                     : (line.Segs.Count > 0 ? line.Segs[line.Segs.Count - 1].X + line.Segs[line.Segs.Count - 1].Width : 0);
                    g.FillRectangle(b, ox + x, top, Dpi.Px(6, Dpi_), line.Height);
                }
            }
        }

        private static int XAt(Seg s, int i)
        {
            for (int k = 0; k < s.Stops.Length; k++) if (s.Stops[k] >= i) return s.Xs[k];
            return s.Xs[s.Xs.Length - 1];
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

        private int Clamp(int i) { return Math.Max(0, Math.Min(_text.Length, i)); }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Accessible(this); }

        private sealed class Accessible : ControlAccessibleObject
        {
            private readonly EmojiComposer _c;
            public Accessible(EmojiComposer c) : base(c) { _c = c; }
            public override string Value { get { return _c._text; } set { _c.Text = value; } }
            public override AccessibleRole Role { get { return AccessibleRole.Text; } }
        }
    }
}
