using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>The ink (and weight) of a <see cref="ThemedLabel"/>. A role names INTENT, never a colour: the colour
    /// comes from <see cref="ThemeHelper.Palette"/>, so a label follows the theme and the user's accent by itself.
    ///   Body   = Palette.Text         Dim    = Palette.TextDim      Faint  = Palette.TextFaint
    ///   Title  = Palette.Text, BOLD   Accent = Palette.AccentFg     Danger = Palette.Danger
    /// A disabled label is always Palette.TextDisabled, whatever its role.</summary>
    public enum LabelRole { Body, Dim, Faint, Title, Accent, Danger }

    /// <summary>Redesign 2.x — the label that replaces the MaterialSkin one: owner-drawn from the cached palette (a field
    /// read per paint, never the registry), measured in LOGICAL points at its window's DPI, and aware of Persian text
    /// and colour emoji.
    ///
    /// ★ THE FONT IS NEVER THE CONTROL'S Font. It is re-resolved at every paint and measure with
    ///   FontHelper.ForAt(Text, PointSize, Dpi.Of(this), style): Vazirmatn for Persian/Arabic text, Roboto otherwise, a
    ///   SHARED pixel-unit font that is never disposed. Had it been stored in Font, DpiScaler's generic pass (which
    ///   rescales every explicitly set Font) would scale it a second time. So Font, ForeColor and BackColor are IGNORED
    ///   here: <see cref="PointSize"/>, <see cref="Style"/> and <see cref="Role"/> say what the label looks like, and
    ///   the backdrop is the parent's BackColor.
    /// ★ TWO PAINT PATHS.
    ///     Single line with emoji  -> EmojiRenderer.DrawLine: the Noto colour images, the same on Win11 and RT 8.1.
    ///     Everything else         -> TextRenderer (GDI, ClearType). Wrapped text goes this way even when it holds
    ///       emoji: those come out MONOCHROME through the font on Windows 10+ and may be boxes on RT 8.1, which has no
    ///       astral glyphs (e6f7108, 2fafcce). Accepted for now; a wrapped emoji-aware layout is a later step.
    /// ★ PERSIAN. Text with an Arabic-script character is right-aligned when <see cref="TextAlign"/> is Left (what
    ///   Telegram does) and read right-to-left (TextFormatFlags.RightToLeft, the app's convention), WITHOUT touching
    ///   RightToLeft/RightToLeftLayout, which mirror every child and have broken RT input before.
    ///   ⚠ The colour-emoji path lays its runs out left-to-right (EmojiRenderer has no bidi), so a Persian line that
    ///   holds an emoji keeps the emoji after the text on screen. TitleBar has the same limit.
    /// ★ HEIGHT FOLLOWS WIDTH when <see cref="Wrap"/> is set, like MessageRow: set the width (Dock = Top, Anchor, or an
    ///   explicit Width) and the height is the wrapped text's. A docked-Fill/Left/Right label cannot take our height.
    /// ★ LENGTHS ARE LOGICAL. This control scales nothing but its font: Bounds and Padding are scaled by DpiScaler's
    ///   generic pass, after which <see cref="IDpiAware.OnDpiChanged"/> refits the height or size from the new font.
    /// ⚠ AutoSize is OFF by default (it is Control's default, not Label's). With it on and Wrap off, the label sizes
    ///   itself to its text, honouring Padding and MaximumSize (combine with Ellipsis to cap the width).</summary>
    public sealed class ThemedLabel : Control, IDpiAware
    {
        /// <summary>The size a new label starts at, in logical points.</summary>
        public const float DefaultPointSize = 10f;

        private const float MinPointSize = 1f, MaxPointSize = 200f;
        private const string Dots = "...";   // ASCII on purpose: U+2026 is one more glyph a font might lack

        // NoPadding: GDI otherwise insets the text by a sixth of an em on each side, which would put a TextRenderer
        // label 2-3 px off the same text drawn by EmojiRenderer, and would make Padding a second, hidden inset.
        private const TextFormatFlags BaseFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private float _pointSize = DefaultPointSize;
        private FontStyle _style = FontStyle.Regular;
        private LabelRole _role = LabelRole.Body;
        private HorizontalAlignment _align = HorizontalAlignment.Left;
        private bool _wrap, _ellipsis;

        // What the current Text is, worked out once per change (not per paint).
        private bool _infoValid, _persian, _hasEmoji;
        private string _single = "";     // Text with line breaks flattened: what a single-line label shows

        private int _fitWidth = -1;      // the width the wrapped height was last fitted to (stops a layout feedback loop)

        // The ellipsized form of a single emoji line, valid for one (width, dpi).
        private string _elideText;
        private int _elideWidth = -1, _elideDpi;

        private string _autoName = "";   // the AccessibleName we set ourselves, so a caller's own is never overwritten
        private bool _hooked;
        private static bool _faultLogged;

        public ThemedLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // a label takes no focus on click
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        // ── the public surface ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The text. A change re-measures (AutoSize / Wrap), repaints and updates AccessibleName.</summary>
        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value ?? ""; }
        }

        /// <summary>The size in LOGICAL points (96-dpi); the pixel size is worked out at the window's DPI each time.
        /// Clamped to 1..200 so a bad setting cannot make FontCache throw inside a paint.</summary>
        public float PointSize
        {
            get { return _pointSize; }
            set
            {
                if (float.IsNaN(value)) value = DefaultPointSize;
                value = Math.Min(MaxPointSize, Math.Max(MinPointSize, value));
                if (value == _pointSize) return;
                _pointSize = value;
                LayoutChanged("PointSize");
            }
        }

        /// <summary>Bold / Italic / Underline / Strikeout, ORed over what the role implies.</summary>
        public FontStyle Style
        {
            get { return _style; }
            set { if (_style == value) return; _style = value; LayoutChanged("Style"); }
        }

        /// <summary>Which palette ink the label uses (and, for Title, a bold weight). See <see cref="LabelRole"/>.</summary>
        public LabelRole Role
        {
            get { return _role; }
            set { if (_role == value) return; _role = value; LayoutChanged("Role"); }
        }

        /// <summary>Word-wrap onto as many lines as the width needs; the HEIGHT then follows the width. Ellipsis is ignored.</summary>
        public bool Wrap
        {
            get { return _wrap; }
            set { if (_wrap == value) return; _wrap = value; LayoutChanged("Wrap"); }
        }

        /// <summary>Single line that ends in "..." instead of being cut off when the text is wider than the label.
        /// Ignored when <see cref="Wrap"/> is set.</summary>
        public bool Ellipsis
        {
            get { return _ellipsis; }
            set { if (_ellipsis == value) return; _ellipsis = value; _elideText = null; Invalidate(); }
        }

        /// <summary>Left, Center or Right. Left becomes Right by itself for Persian text (see the class summary).</summary>
        public HorizontalAlignment TextAlign
        {
            get { return _align; }
            set { if (_align == value) return; _align = value; Invalidate(); }
        }

        /// <summary>The outer height this label needs at outer width <paramref name="width"/> when wrapped (Padding
        /// included) — for sizing a dialog before it is shown. Uses the DPI of the label's window.</summary>
        public int HeightFor(int width) { return HeightFor(width, Dpi.Of(this)); }

        /// <summary>As <see cref="HeightFor(int)"/> at an explicit DPI (a label not yet parented has no window to ask).
        /// Empty text is 0 tall plus Padding, so an empty message row collapses, as MessageRow does.</summary>
        public int HeightFor(int width, int dpi)
        {
            var pad = Padding;
            int avail = Math.Max(1, width - pad.Horizontal);
            int h = 0;
            string text = Text;
            if (text.Length > 0)
                h = TextRenderer.MeasureText(text, ResolveFont(dpi), new Size(avail, 0), WrapFlags()).Height;
            return h + pad.Vertical;
        }

        /// <summary>The size that fits the text: the one-line extent, or — when wrapped — the proposed width (else the
        /// current one) with the height that width needs. Padding is included either way.</summary>
        public override Size GetPreferredSize(Size proposedSize)
        {
            int dpi = Dpi.Of(this);
            var pad = Padding;
            if (_wrap)
            {
                int w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Width;
                if (w > 0) return new Size(w, HeightFor(w, dpi));
            }
            int tw, th;
            MeasureLine(dpi, out tw, out th);
            return new Size(tw + pad.Horizontal, th + pad.Vertical);
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            // Bounds and Padding have been scaled by ratio; the font has not (it is not the control's Font). Dpi.Of(this)
            // already answers newDpi here (WindowDpi sets it before DpiScaler runs), so the fit below is exact.
            ResetFit();
            AdjustAll();
            Invalidate();
        }

        // ── size ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A change that can alter the size: forget what was fitted, refit, repaint, and tell the parent.</summary>
        private void LayoutChanged(string property)
        {
            ResetFit();
            AdjustAll();
            Invalidate();
            NotifyParent(property);
        }

        private void ResetFit() { _fitWidth = -1; _elideText = null; }

        /// <summary>Wrapped: the height follows the width. Else, with AutoSize: the size follows the text.</summary>
        private void AdjustAll()
        {
            if (_wrap) FitHeight();
            else if (AutoSize) FitToContent();
        }

        private void FitHeight()
        {
            if (Width <= 0) return;
            _fitWidth = Width;
            int h = HeightFor(Width, Dpi.Of(this));
            if (Height != h) Height = h;
        }

        private void FitToContent()
        {
            Size want = GetPreferredSize(Size.Empty);
            if (want != Size) Size = want;
        }

        private void NotifyParent(string property)
        {
            var parent = Parent;
            if (AutoSize && parent != null) parent.PerformLayout(this, property);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Skipped during a DpiScaler pass (Dpi.Busy): the bounds are interim and IDpiAware.OnDpiChanged fits once at the end.
            // _fitWidth also ends the loop a docked parent would otherwise start by overriding the Height we set.
            if (_wrap && Width != _fitWidth && !Dpi.Busy) FitHeight();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            _infoValid = false;
            string text = Text;
            if (string.IsNullOrEmpty(AccessibleName) || AccessibleName == _autoName) { _autoName = text; AccessibleName = text; }
            LayoutChanged("Text");
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            ResetFit();   // the window — and so the DPI — may be a different one now
            AdjustAll();
            Invalidate();
        }

        protected override void OnPaddingChanged(EventArgs e)
        {
            base.OnPaddingChanged(e);
            if (!Dpi.Busy) LayoutChanged("Padding");   // DpiScaler scales Padding mid-pass; OnDpiChanged fits once at the end
        }

        protected override void OnAutoSizeChanged(EventArgs e)
        {
            base.OnAutoSizeChanged(e);
            LayoutChanged("AutoSize");
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        // ── theme ────────────────────────────────────────────────────────────────────────────────────────────
        // Subscribed while the control has a window handle, not from the constructor: a label that is built but never
        // shown (or never disposed) must not be pinned in memory by a static event. Colours are read from the palette at
        // paint time, so a label that missed a change while it had no handle still paints right when it gets one.

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_hooked) return;
            _hooked = true;
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Unhook();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Unhook();
            base.Dispose(disposing);
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            ThemeHelper.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!InvokeRequired) { Invalidate(); return; }
            try { BeginInvoke((Action)Invalidate); } catch (InvalidOperationException) { }   // the window went away meanwhile
        }

        // ── what the text is ─────────────────────────────────────────────────────────────────────────────────

        private void EnsureInfo()
        {
            if (_infoValid) return;
            string t = Text;
            _persian = FontHelper.IsPersian(t);
            _hasEmoji = EmojiRenderer.ContainsEmoji(t);
            _single = t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0 ? t.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') : t;
            _infoValid = true;
        }

        /// <summary>The shared pixel-unit font for the current text. ⚠ Never dispose it, never assign it to Font.</summary>
        private Font ResolveFont(int dpi)
        {
            FontStyle style = _role == LabelRole.Title ? _style | FontStyle.Bold : _style;
            return FontHelper.ForAt(Text, _pointSize, dpi, style);
        }

        private Color InkOf(Palette p)
        {
            switch (_role)
            {
                case LabelRole.Dim: return p.TextDim;
                case LabelRole.Faint: return p.TextFaint;
                case LabelRole.Accent: return p.AccentFg;
                case LabelRole.Danger: return p.Danger;
                default: return p.Text;   // Body and Title
            }
        }

        /// <summary>The parent's BackColor — or, if that is not opaque (Color.Transparent clears to nothing), the nearest
        /// opaque ancestor's, or the palette's window colour for a label with no parent yet.</summary>
        private Color Backdrop(Palette p)
        {
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return p.Window;
        }

        private HorizontalAlignment EffectiveAlign
        {
            get
            {
                EnsureInfo();
                return _align == HorizontalAlignment.Left && _persian ? HorizontalAlignment.Right : _align;
            }
        }

        private Rectangle ContentRect()
        {
            var pad = Padding;
            return new Rectangle(pad.Left, pad.Top, Math.Max(0, ClientSize.Width - pad.Horizontal), Math.Max(0, ClientSize.Height - pad.Vertical));
        }

        // ── TextRenderer flags ───────────────────────────────────────────────────────────────────────────────

        private TextFormatFlags AlignFlag()
        {
            switch (EffectiveAlign)
            {
                case HorizontalAlignment.Center: return TextFormatFlags.HorizontalCenter;
                case HorizontalAlignment.Right: return TextFormatFlags.Right;
                default: return TextFormatFlags.Left;
            }
        }

        private TextFormatFlags WrapFlags()
        {
            EnsureInfo();   // _persian below
            return BaseFlags | TextFormatFlags.WordBreak | TextFormatFlags.Top | AlignFlag() | (_persian ? TextFormatFlags.RightToLeft : 0);
        }

        /// <summary>DT_VCENTER only works with DT_SINGLELINE, so a single line is vertically centred in its rectangle
        /// (as EmojiRenderer.DrawLine centres its line), unlike wrapped text, which starts at the top.</summary>
        private TextFormatFlags SingleFlags()
        {
            EnsureInfo();   // _persian below
            var f = BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | AlignFlag();
            if (_ellipsis) f |= TextFormatFlags.EndEllipsis;   // on the left for RightToLeft text, as ChatListItemControl does
            if (_persian) f |= TextFormatFlags.RightToLeft;
            return f;
        }

        // ── measuring ────────────────────────────────────────────────────────────────────────────────────────

        private static int LineHeight(Font font)
        {
            return TextRenderer.MeasureText("Ag", font, Size.Empty, BaseFlags).Height;
        }

        /// <summary>The extent of the single line (emoji-aware), WITHOUT Padding. An empty line is 0 wide but still one
        /// line tall, so a label that is empty now does not change height when it is given text.</summary>
        private void MeasureLine(int dpi, out int width, out int height)
        {
            EnsureInfo();
            Font font = ResolveFont(dpi);
            height = LineHeight(font);
            width = 0;
            if (_single.Length == 0) return;
            if (_hasEmoji)
            {
                try
                {
                    // Fonts are pixel-unit, so a screen DC measures the same as the window's.
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        width = EmojiRenderer.MeasureLineWidth(g, _single, font);
                    }
                    return;
                }
                catch (Exception ex) { LogFault(ex); }
            }
            width = TextRenderer.MeasureText(_single, font, Size.Empty, BaseFlags | TextFormatFlags.SingleLine).Width;
        }

        // ── painting ─────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            WindowBackdrop.ClearBehind(g, this, Parent, ClientRectangle, p.Window);   // step 5.9: the parent's colour, or the backdrop
            string text = Text;
            if (text.Length == 0) return;
            Rectangle rc = ContentRect();
            if (rc.Width < 1 || rc.Height < 1) return;

            EnsureInfo();
            int dpi = Dpi.Of(this);
            Font font = ResolveFont(dpi);   // shared: never dispose
            Color ink = Enabled ? InkOf(p) : p.TextDisabled;

            if (_wrap)
            {
                TextRenderer.DrawText(g, text, font, rc, ink, WrapFlags());
                return;
            }
            if (_hasEmoji && PaintEmojiLine(g, font, ink, rc, dpi)) return;
            TextRenderer.DrawText(g, _single, font, rc, ink, SingleFlags());
        }

        /// <summary>One line with colour emoji. False if the emoji renderer failed, so the caller draws plain text instead
        /// of leaving the label blank.</summary>
        private bool PaintEmojiLine(Graphics g, Font font, Color ink, Rectangle rc, int dpi)
        {
            try
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                string line = _single;
                HorizontalAlignment align = EffectiveAlign;
                int x = rc.X;
                // The width is only needed to align or to ellipsize — the common Left, no-ellipsis label skips the measure.
                if (_ellipsis || align != HorizontalAlignment.Left)
                {
                    int w = EmojiRenderer.MeasureLineWidth(g, line, font);
                    if (w > rc.Width && _ellipsis)
                    {
                        line = Elide(g, font, rc.Width, dpi);
                        w = EmojiRenderer.MeasureLineWidth(g, line, font);
                    }
                    if (w < rc.Width)
                        x = align == HorizontalAlignment.Center ? rc.X + (rc.Width - w) / 2
                          : align == HorizontalAlignment.Right ? rc.Right - w
                          : rc.X;
                }
                // The rectangle runs on to the content edge: EmojiRenderer clips to it, and a rectangle trimmed to the
                // measured width would shave the last pixel of an overhanging glyph.
                EmojiRenderer.DrawLine(g, line, font, ink, new Rectangle(x, rc.Y, rc.Right - x, rc.Height));
                return true;
            }
            catch (Exception ex)
            {
                LogFault(ex);
                return false;
            }
        }

        /// <summary>The longest prefix of the single line that fits <paramref name="maxWidth"/> together with "...",
        /// cut only where it is safe: never inside a surrogate pair, a combining sequence or an emoji sequence.
        /// Cached for one (width, DPI), because a paint must not search every time.</summary>
        private string Elide(Graphics g, Font font, int maxWidth, int dpi)
        {
            if (_elideText != null && _elideWidth == maxWidth && _elideDpi == dpi) return _elideText;
            string s = _single;
            string result = Dots;
            int dots = EmojiRenderer.MeasureLineWidth(g, Dots, font);
            if (maxWidth > dots)
            {
                List<int> cuts = CutPoints(s);
                int lo = 0, hi = cuts.Count - 1, best = -1;
                while (lo <= hi)   // widths grow with the prefix, so binary search
                {
                    int mid = (lo + hi) / 2;
                    if (EmojiRenderer.MeasureLineWidth(g, s.Substring(0, cuts[mid]), font) + dots <= maxWidth) { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (best >= 0) result = s.Substring(0, cuts[best]).TrimEnd() + Dots;
            }
            _elideText = result;
            _elideWidth = maxWidth;
            _elideDpi = dpi;
            return result;
        }

        /// <summary>The UTF-16 offsets (ascending) after which <paramref name="s"/> may be cut: the end of every emoji
        /// (a whole sequence, so a family emoji is never left half there) and of every text element in between.</summary>
        private static List<int> CutPoints(string s)
        {
            var cuts = new List<int>();
            int off = 0;
            foreach (EmojiRenderer.Run run in EmojiRenderer.Segment(s))
            {
                string t = run.Text;
                if (run.Emoji != null)
                {
                    off += t.Length;
                    cuts.Add(Math.Min(off, s.Length));
                    continue;
                }
                int[] starts = StringInfo.ParseCombiningCharacters(t);
                for (int i = 1; i <= starts.Length; i++)
                {
                    int end = i < starts.Length ? starts[i] : t.Length;
                    cuts.Add(Math.Min(off + end, s.Length));
                }
                off += t.Length;
            }
            return cuts;
        }

        /// <summary>R7: Logger.Diag, never Debug.WriteLine. Once per process — a failing renderer would otherwise log on
        /// every paint of every label.</summary>
        private static void LogFault(Exception ex)
        {
            if (_faultLogged) return;
            _faultLogged = true;
            Logger.Diag("[LABEL] emoji line failed, drew plain text: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
