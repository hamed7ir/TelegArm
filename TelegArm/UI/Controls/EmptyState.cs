using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign — the "nothing here yet" panel, ported from DroidFinestra's first-run screen (a bold title, a
    /// dim hint and one action button, centred): an empty chat list, a search with no hits, a folder with no chats.
    ///
    /// Everything is centred horizontally and the whole block (icon, title, hint, button) is centred vertically, so it
    /// sits in the middle of whatever surface hosts it at any size. The parts that are absent take no room: no
    /// <see cref="Icon"/>, an empty <see cref="Hint"/> or a null/empty <see cref="ActionText"/> simply leaves a
    /// shorter block.
    ///
    /// ★ OWNER-DRAWN, ONE CHILD. Icon, title and hint are painted here (script-aware fonts — Vazirmatn for Persian —
    ///   and inline colour emoji through <see cref="EmojiRenderer"/>, which is why the text is wrapped by hand: the
    ///   emoji renderer draws single lines). The action is a real <see cref="RoundedButton"/>, so it takes focus, Enter
    ///   and Space from WinForms for free and reads as a button to a screen reader.
    /// ★ THE ICON IS DRAWN WITH GDI+ LINES, never a font symbol: symbol glyphs come out as tofu boxes on RT 8.1. It
    ///   does not borrow <see cref="GlyphButton"/>'s drawing (that one is 14 px of title-bar chrome, private, and
    ///   cannot be scaled up to 48) — it only borrows its <see cref="GlyphButton.Glyph"/> vocabulary.
    /// ★ ALL LENGTHS ARE LOGICAL (96 DPI) and become pixels at the DPI of the window at layout / paint time. Fonts
    ///   come from FontHelper's shared cache and are never disposed.
    /// ★ BACKGROUND: it clears to its parent's BackColor (so it blends into any surface). The button's corners are
    ///   cleared to THIS control's BackColor, which inherits the parent's unless someone sets it explicitly — leave it
    ///   alone and the two always match.</summary>
    public sealed class EmptyState : Control, IDpiAware
    {
        /// <summary>The <see cref="Icon"/> that draws the generic chat bubble. <see cref="GlyphButton.Glyph"/> has no
        /// member for it, so every glyph without a dedicated large form (see <see cref="Icon"/>) falls back to the
        /// bubble; this names one such glyph so callers need not guess. Replace it with a real member if GlyphButton
        /// ever grows one.</summary>
        public const GlyphButton.Glyph ChatBubble = GlyphButton.Glyph.More;

        // Logical (96-DPI) metrics. The 360 / 160 x 38 / 48 sizes are the spec; the gaps follow DroidFinestra's
        // first-run screen (6 between title and hint, 18 before the button).
        private const int MaxTextWidth = 360, SidePad = 16, MinTextWidth = 48;
        private const int IconSize = 48, IconGap = 14, TitleGap = 6, ActionGap = 18;
        private const int ButtonWidth = 160, ButtonHeight = 38;
        private const float TitlePt = 15f, HintPt = 10f, ActionPt = 10f;

        private readonly RoundedButton _button;
        private string _title = "", _hint = "", _actionText;
        private GlyphButton.Glyph? _icon;
        private Metrics _m;

        /// <summary>Raised when the action button is clicked (mouse, touch, or Enter / Space on the focused button).</summary>
        public event EventHandler ActionClick;

        public EmptyState()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // the panel is not a tab stop; its button is
            TabStop = false;
            _button = new RoundedButton { Kind = RoundedButtonKind.Primary, Modern = true, Visible = false };   // a11y-ok: its Text is the ButtonText property
            _button.Click += OnButtonClick;
            Controls.Add(_button);
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        /// <summary>The bold headline. Wrapped to the text width if it is long.</summary>
        public string Title
        {
            get { return _title; }
            set
            {
                value = value ?? "";
                if (_title == value) return;
                _title = value;
                AccessibleName = value;
                Relayout();
            }
        }

        /// <summary>The dim explanation under the title, word-wrapped to at most 360 logical px. May contain emoji and
        /// line breaks; empty hides it.</summary>
        public string Hint
        {
            get { return _hint; }
            set
            {
                value = value ?? "";
                if (_hint == value) return;
                _hint = value;
                AccessibleDescription = value;
                Relayout();
            }
        }

        /// <summary>The action button's label. Null or empty means no button.</summary>
        public string ActionText
        {
            get { return _actionText; }
            set
            {
                if (_actionText == value) return;
                _actionText = value;
                _button.Text = value ?? "";
                _button.Visible = HasAction;
                Relayout();
            }
        }

        /// <summary>An optional large (about 48 logical px) icon above the title, in the faint text colour. Search and
        /// Plus (a ring with a plus) and Check (a ring with a tick) have their own form; EVERY other glyph draws the
        /// generic chat bubble (<see cref="ChatBubble"/>). Null means no icon.</summary>
        public GlyphButton.Glyph? Icon
        {
            get { return _icon; }
            set
            {
                if (_icon == value) return;
                _icon = value;
                Relayout();
            }
        }

        /// <summary>The action button itself, for focus (the host calls <c>ActionButton.Focus()</c> when it opens) and
        /// per-screen tweaks such as <see cref="RoundedButton.Kind"/>. It is an ordinary child control.</summary>
        public RoundedButton ActionButton { get { return _button; } }

        private bool HasAction { get { return !string.IsNullOrEmpty(_actionText); } }

        private void OnButtonClick(object sender, EventArgs e)
        {
            var h = ActionClick;
            if (h != null) h(this, e);
        }

        // ── events that change the layout ───────────────────────────────────────────────────────────────────

        /// <summary>DpiScaler has already rescaled our bounds and the button's; what is ours is the text layout, whose
        /// fonts and wrap width depend on the DPI.</summary>
        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { Relayout(); }

        private void Relayout()
        {
            _m = null;
            PerformLayout();
            Invalidate();
        }

        /// <summary>The DPI is the window's, and a control's window is unknown until it is parented / realised: lay out
        /// again once it is, or a panel built before it was shown keeps system-DPI metrics.</summary>
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Relayout(); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _m = null; PerformLayout(); }

        private void OnThemeChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;   // no handle: the first paint reads the current palette anyway
            try
            {
                if (InvokeRequired) BeginInvoke((Action)Invalidate);
                else Invalidate();
            }
            catch (InvalidOperationException) { }   // ObjectDisposedException derives from it: a disposed control lands here too
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;   // a static event would keep us alive otherwise
            base.Dispose(disposing);
        }

        // ── layout ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Everything the block needs at one (DPI, wrap width): the wrapped lines, their fonts and where each
        /// part sits, relative to the top of the block. Rebuilt only when the text, the DPI or the width changes —
        /// OnLayout and OnPaint share it, so the button is always where the text above it ends.</summary>
        private sealed class Metrics
        {
            public int Dpi, TextW, IconSize, Total;
            public Font TitleFont, HintFont;
            public List<string> TitleLines, HintLines;
            public int TitleLineH, HintLineH;
            public int IconTop, TitleTop, HintTop, ButtonTop;
        }

        private int TextWidthFor(int dpi)
        {
            return Math.Max(Dpi.Px(MinTextWidth, dpi), Math.Min(Dpi.Px(MaxTextWidth, dpi), Width - 2 * Dpi.Px(SidePad, dpi)));
        }

        private Metrics CachedFor(int dpi)
        {
            var m = _m;
            return m != null && m.Dpi == dpi && m.TextW == TextWidthFor(dpi) ? m : null;
        }

        private Metrics Build(Graphics g, int dpi)
        {
            var m = new Metrics { Dpi = dpi, TextW = TextWidthFor(dpi) };
            m.TitleFont = FontHelper.ForAt(_title, TitlePt, dpi, FontStyle.Bold);   // shared: never dispose
            m.HintFont = FontHelper.ForAt(_hint, HintPt, dpi);
            m.TitleLines = Wrap(g, _title, m.TitleFont, m.TextW);
            m.HintLines = Wrap(g, _hint, m.HintFont, m.TextW);
            m.TitleLineH = (int)Math.Ceiling(m.TitleFont.GetHeight(g));
            m.HintLineH = (int)Math.Ceiling(m.HintFont.GetHeight(g));

            int y = 0;
            bool any = false, afterIcon = false;
            if (_icon.HasValue)
            {
                m.IconSize = Dpi.Px(IconSize, dpi);
                m.IconTop = 0;
                y = m.IconSize;
                any = afterIcon = true;
            }
            if (m.TitleLines.Count > 0)
            {
                if (any) y += Dpi.Px(afterIcon ? IconGap : TitleGap, dpi);
                m.TitleTop = y;
                y += m.TitleLines.Count * m.TitleLineH;
                any = true; afterIcon = false;
            }
            if (m.HintLines.Count > 0)
            {
                if (any) y += Dpi.Px(afterIcon ? IconGap : TitleGap, dpi);
                m.HintTop = y;
                y += m.HintLines.Count * m.HintLineH;
                any = true; afterIcon = false;
            }
            if (HasAction)
            {
                if (any) y += Dpi.Px(ActionGap, dpi);
                m.ButtonTop = y;
                y += Dpi.Px(ButtonHeight, dpi);
            }
            m.Total = y;
            _m = m;
            return m;
        }

        /// <summary>The block's top edge: centred, but never above the control (a block taller than the control loses
        /// its bottom, not its title).</summary>
        private int BlockTop(Metrics m) { return Math.Max(0, (Height - m.Total) / 2); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Width <= 0 || Height <= 0) return;
            int dpi = Dpi.Of(this);
            var m = CachedFor(dpi);
            if (m == null)
            {
                // Measured on the screen DC rather than CreateGraphics(): that would force this control's handle (and
                // so its parents') into existence just to lay out. Same text hint as OnPaint, so widths agree.
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    m = Build(g, dpi);
                }
            }
            if (HasAction)   // from our own flag, never Control.Visible (false for every child of a form not yet shown)
            {
                var f = FontHelper.ForAt(_actionText, ActionPt, dpi, FontStyle.Bold);   // shared: never dispose
                if (!ReferenceEquals(_button.Font, f)) _button.Font = f;
                int bw = Dpi.Px(ButtonWidth, dpi), bh = Dpi.Px(ButtonHeight, dpi);
                _button.SetBounds((Width - bw) / 2, BlockTop(m) + m.ButtonTop, bw, bh);
            }
        }

        // ── word wrap ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Greedy word wrap that measures with the emoji renderer, so a line holding emoji breaks where it will
        /// really draw. Honours explicit line breaks (a blank line is kept as a paragraph gap, but never at the very
        /// start or end), and cuts a word wider than the line at a text-element boundary rather than letting it run
        /// off the panel.</summary>
        private static List<string> Wrap(Graphics g, string text, Font font, int maxW)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            string[] paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string para in paragraphs)
            {
                string cur = "";
                foreach (string word in para.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = word;
                    if (cur.Length > 0)
                    {
                        string trial = cur + " " + w;
                        if (EmojiRenderer.MeasureLineWidth(g, trial, font) <= maxW) { cur = trial; continue; }
                        lines.Add(cur);
                        cur = "";
                    }
                    while (EmojiRenderer.MeasureLineWidth(g, w, font) > maxW)
                    {
                        int cut = FitPrefix(g, w, font, maxW);
                        lines.Add(w.Substring(0, cut));
                        w = w.Substring(cut);
                    }
                    cur = w;
                }
                lines.Add(cur);
            }
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            return lines;
        }

        /// <summary>How many chars of <paramref name="s"/> fit in <paramref name="maxW"/>, cut between text elements (a
        /// surrogate pair or a letter with its combining marks is never split). At least one element, so the caller's
        /// loop always makes progress.</summary>
        private static int FitPrefix(Graphics g, string s, Font font, int maxW)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(s);
            int best = starts.Length > 1 ? starts[1] : s.Length;
            for (int i = 2; i < starts.Length; i++)
            {
                if (EmojiRenderer.MeasureLineWidth(g, s.Substring(0, starts[i]), font) > maxW) break;
                best = starts[i];
            }
            return best;
        }

        // ── painting ────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            if (Width <= 0 || Height <= 0) return;

            int dpi = Dpi.Of(this);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var m = CachedFor(dpi) ?? Build(g, dpi);
            int top = BlockTop(m), tx = (Width - m.TextW) / 2;

            if (_icon.HasValue)
                DrawIcon(g, _icon.Value, new Rectangle((Width - m.IconSize) / 2, top + m.IconTop, m.IconSize, m.IconSize), p.TextFaint);

            for (int i = 0; i < m.TitleLines.Count; i++)
                EmojiRenderer.DrawLineCentered(g, m.TitleLines[i], m.TitleFont, p.Text,
                    new Rectangle(tx, top + m.TitleTop + i * m.TitleLineH, m.TextW, m.TitleLineH));

            for (int i = 0; i < m.HintLines.Count; i++)
                EmojiRenderer.DrawLineCentered(g, m.HintLines[i], m.HintFont, p.TextDim,
                    new Rectangle(tx, top + m.HintTop + i * m.HintLineH, m.TextW, m.HintLineH));
        }

        /// <summary>Draws <paramref name="glyph"/> large, with lines, in a 48-unit design square scaled to
        /// <paramref name="box"/>. Search, Plus and Check have their own form; anything else is the chat bubble.</summary>
        private static void DrawIcon(Graphics g, GlyphButton.Glyph glyph, Rectangle box, Color color)
        {
            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = box.Width / 48f, ox = box.X, oy = box.Y;
            using (var pen = new Pen(color, Math.Max(1.5f, 2.6f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (glyph)
                {
                    case GlyphButton.Glyph.Search:
                        g.DrawEllipse(pen, ox + 7 * u, oy + 7 * u, 26 * u, 26 * u);
                        g.DrawLine(pen, ox + 30.4f * u, oy + 30.4f * u, ox + 41 * u, oy + 41 * u);
                        break;
                    case GlyphButton.Glyph.Plus:
                        g.DrawEllipse(pen, ox + 4 * u, oy + 4 * u, 40 * u, 40 * u);
                        g.DrawLine(pen, ox + 14 * u, oy + 24 * u, ox + 34 * u, oy + 24 * u);
                        g.DrawLine(pen, ox + 24 * u, oy + 14 * u, ox + 24 * u, oy + 34 * u);
                        break;
                    case GlyphButton.Glyph.Check:
                        g.DrawEllipse(pen, ox + 4 * u, oy + 4 * u, 40 * u, 40 * u);
                        g.DrawLine(pen, ox + 14 * u, oy + 25 * u, ox + 21 * u, oy + 32 * u);
                        g.DrawLine(pen, ox + 21 * u, oy + 32 * u, ox + 34 * u, oy + 17 * u);
                        break;
                    default:
                        DrawBubble(g, pen, color, ox, oy, u);
                        break;
                }
            }
            g.SmoothingMode = oldSmoothing;
        }

        /// <summary>A speech bubble (rounded body, a tail at the bottom left) with three dots inside. One closed path, so
        /// the stroke joins cleanly where the tail meets the body.</summary>
        private static void DrawBubble(Graphics g, Pen pen, Color color, float ox, float oy, float u)
        {
            const float r = 8f;   // corner radius in design units; the body is x 5..43, y 6..34
            using (var path = new GraphicsPath())
            {
                path.AddArc(ox + 5 * u, oy + 6 * u, 2 * r * u, 2 * r * u, 180, 90);                       // top left
                path.AddArc(ox + (43 - 2 * r) * u, oy + 6 * u, 2 * r * u, 2 * r * u, 270, 90);            // top right
                path.AddArc(ox + (43 - 2 * r) * u, oy + (34 - 2 * r) * u, 2 * r * u, 2 * r * u, 0, 90);   // bottom right
                path.AddLine(ox + 35 * u, oy + 34 * u, ox + 28 * u, oy + 34 * u);                         // along the bottom to the tail
                path.AddLine(ox + 28 * u, oy + 34 * u, ox + 16 * u, oy + 43 * u);                         // tail tip
                path.AddLine(ox + 16 * u, oy + 43 * u, ox + 19 * u, oy + 34 * u);                         // back up to the body
                path.AddArc(ox + 5 * u, oy + (34 - 2 * r) * u, 2 * r * u, 2 * r * u, 90, 90);             // bottom left
                path.CloseFigure();
                g.DrawPath(pen, path);
            }
            using (var dot = new SolidBrush(color))
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(dot, ox + (24 + 7 * i - 2.2f) * u, oy + (20 - 2.2f) * u, 4.4f * u, 4.4f * u);
        }
    }
}
