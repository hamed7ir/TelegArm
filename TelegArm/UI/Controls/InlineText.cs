using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TL;

namespace TelegArm.UI.Controls
{
    public enum InlineKind { Plain, Url, Mention, Hashtag, BotCommand }

    /// <summary>Telegram text-styling flags folded onto a token (they STACK — OR together).</summary>
    [Flags]
    public enum InlineStyle
    {
        None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8,
        Code = 16, Pre = 32, Spoiler = 64, Blockquote = 128
    }

    /// <summary>A clickable segment hit by a tap (url / @mention / #hashtag / /command).</summary>
    public sealed class InlineHit
    {
        public InlineKind Kind;
        public string Url;
        public string Username;
        public long UserId;
        public string Data;        // hashtag/cashtag text or bot command
    }

    /// <summary>
    /// Builds a wrapping, RTL-aware run of inline tokens from message text + TL entities and
    /// paints/hit-tests it. Beyond url/mention/hashtag/emoji, it renders the text-styling entities
    /// (bold/italic/underline/strike/code/pre/spoiler/blockquote) by carrying a style mask on each
    /// token — styles compose, and a styled link is both styled and clickable. Entity offsets are
    /// UTF-16 code units used directly as .NET string indices. Text draws via TextRenderer (script
    /// fallback); italic is a GDI+ shear (the embedded families ship no italic face). RTL lays runs
    /// right→left and puts decorations (quote bar, indent) on the right.
    /// </summary>
    public sealed class InlineText : IDisposable
    {
        private sealed class Tok
        {
            public string Text;
            public Image Image;
            public bool Space;
            public bool Newline;
            public int W;
            public Rectangle Rect;
            public InlineKind Kind;
            public InlineStyle Style;
            public string Url;
            public string Username;
            public long UserId;
            public string Data;
            public long DocId;
            public int SelStart;   // offset of this token's text in the selectable string
            public int SelLen;     // length of this token's selectable text (emoji = its source codepoints)
        }

        private sealed class Line
        {
            public readonly List<Tok> Toks = new List<Tok>();
            public int Y;
            public int Indent;
            public bool Quote;
            public bool Pre;
            public int SelStart;   // selectable-string range covered by this line's chars
            public int SelEnd;
        }

        private const int QuoteIndent = 12, BarW = 3;

        private readonly List<Tok> _toks = new List<Tok>();
        private readonly List<Line> _lines = new List<Line>();
        private readonly Func<long, Image> _resolver;
        private int _lineH = 16, _emojiSz = 14;
        private bool _rtl;
        private bool _spoilerRevealed;

        // Selection model (built after Position): one linear "selectable string" + per-token offsets, so the
        // shared InlineTextSelection engine can char-hit-test, highlight a [start,end] range, and copy its text.
        private readonly StringBuilder _selText = new StringBuilder();
        private int _selLen;
        public int SelLength => _selLen;
        // Layout cache: text is fixed per InlineText, so the line-breaking is a pure function of (width, font)
        // and the X positions + selection model of (width, rtl). Measure/Position return the cached layout when
        // called with the same key — eliminating the ~100%-redundant per-paint remeasure (PERF-fix1).
        private string _measureKey;       // (maxWidth | font) of the cached MeasureCore
        private int _measuredMaxLine;     // cached MeasureCore return (content width)
        private string _positionKey;      // (width | rtl) of the cached PositionCore
        private Bitmap _bmp;              // cached opaque text render (PERF-fix2) — blitted instead of re-rendering
        private string _bmpKey;           // (size | colors) of the cached bitmap

        public int Width { get; private set; }
        public int Height { get; private set; }

        public InlineText(string text, MessageEntity[] entities, Func<long, Image> customEmojiResolver = null)
        {
            _resolver = customEmojiResolver;
            BuildTokens(text ?? "", entities);
        }

        // ── Segmentation ─────────────────────────────────────────────────────
        private struct Span { public int Off, Len; public InlineKind Kind; public string Url, Username, Data; public long UserId, DocId; }

        private static InlineStyle StyleOf(MessageEntity e)
        {
            if (e is MessageEntityBold) return InlineStyle.Bold;
            if (e is MessageEntityItalic) return InlineStyle.Italic;
            if (e is MessageEntityUnderline) return InlineStyle.Underline;
            if (e is MessageEntityStrike) return InlineStyle.Strike;
            if (e is MessageEntityCode) return InlineStyle.Code;
            if (e is MessageEntityPre) return InlineStyle.Pre;
            if (e is MessageEntitySpoiler) return InlineStyle.Spoiler;
            if (e is MessageEntityBlockquote) return InlineStyle.Blockquote;
            return InlineStyle.None;
        }

        private void BuildTokens(string text, MessageEntity[] entities)
        {
            // Per-character style mask (styling entities OR together; orthogonal to the kind spans).
            var styleAt = new InlineStyle[text.Length];
            var spans = new List<Span>();
            if (entities != null)
                foreach (var e in entities)
                {
                    if (e == null) continue;
                    var st = StyleOf(e);
                    if (st != InlineStyle.None)
                    {
                        int a = Math.Max(0, e.offset), b = Math.Min(text.Length, e.offset + e.length);
                        for (int i = a; i < b; i++) styleAt[i] |= st;
                        continue;
                    }
                    var s = new Span { Off = e.offset, Len = e.length };
                    if (e is MessageEntityUrl) { s.Kind = InlineKind.Url; s.Url = Sub(text, e.offset, e.length); }
                    else if (e is MessageEntityTextUrl tu) { s.Kind = InlineKind.Url; s.Url = tu.url; }
                    else if (e is MessageEntityMention) { s.Kind = InlineKind.Mention; s.Username = Sub(text, e.offset, e.length); }
                    else if (e is MessageEntityMentionName mn) { s.Kind = InlineKind.Mention; s.UserId = mn.user_id; }
                    else if (e is MessageEntityHashtag || e is MessageEntityCashtag) { s.Kind = InlineKind.Hashtag; s.Data = Sub(text, e.offset, e.length); }
                    else if (e is MessageEntityBotCommand) { s.Kind = InlineKind.BotCommand; s.Data = Sub(text, e.offset, e.length); }
                    else if (e is MessageEntityCustomEmoji ce) { s.Kind = InlineKind.Plain; s.DocId = ce.document_id; }
                    else continue;
                    if (s.Len > 0 && s.Off >= 0 && s.Off < text.Length) spans.Add(s);
                }
            spans.Sort((a, b) => a.Off.CompareTo(b.Off));

            int pos = 0;
            foreach (var s in spans)
            {
                if (s.Off < pos) continue;
                int end = Math.Min(text.Length, s.Off + s.Len);
                if (s.Off > pos) EmitPlain(text.Substring(pos, s.Off - pos), pos, styleAt);
                if (s.DocId != 0)
                    _toks.Add(new Tok { DocId = s.DocId, Text = text.Substring(s.Off, end - s.Off), Kind = InlineKind.Plain, Style = StyleAt(styleAt, s.Off) });
                else
                    EmitTagged(text.Substring(s.Off, end - s.Off), s, s.Off, styleAt);
                pos = end;
            }
            if (pos < text.Length) EmitPlain(text.Substring(pos), pos, styleAt);
        }

        private static InlineStyle StyleAt(InlineStyle[] mask, int idx)
            => (idx >= 0 && idx < mask.Length) ? mask[idx] : InlineStyle.None;

        private static string Sub(string t, int off, int len)
        {
            if (off < 0 || off >= t.Length) return "";
            return t.Substring(off, Math.Min(len, t.Length - off));
        }

        // Emit a substring, splitting it into maximal runs of UNIFORM style so each token carries one mask.
        private void EmitPlain(string s, int absStart, InlineStyle[] styleAt)
        {
            int i = 0;
            while (i < s.Length)
            {
                InlineStyle st = StyleAt(styleAt, absStart + i);
                int j = i + 1;
                while (j < s.Length && StyleAt(styleAt, absStart + j) == st) j++;
                EmitStyled(s.Substring(i, j - i), st, InlineKind.Plain, null, null, 0, null);
                i = j;
            }
        }

        private void EmitTagged(string s, Span span, int absStart, InlineStyle[] styleAt)
        {
            int i = 0;
            while (i < s.Length)
            {
                InlineStyle st = StyleAt(styleAt, absStart + i);
                int j = i + 1;
                while (j < s.Length && StyleAt(styleAt, absStart + j) == st) j++;
                EmitStyled(s.Substring(i, j - i), st, span.Kind, span.Url, span.Username, span.UserId, span.Data);
                i = j;
            }
        }

        private void EmitStyled(string piece, InlineStyle st, InlineKind kind, string url, string username, long userId, string data)
        {
            var parts = piece.Split('\n');
            for (int k = 0; k < parts.Length; k++)
            {
                if (k > 0) _toks.Add(new Tok { Newline = true });
                string seg = parts[k];
                if (seg.Length == 0) continue;
                foreach (var run in EmojiRenderer.Segment(seg))
                {
                    if (run.Emoji != null)
                        _toks.Add(new Tok { Image = run.Emoji, Text = run.Text, Kind = kind, Style = st, Url = url, Username = username, UserId = userId, Data = data });
                    else
                        foreach (var w in SplitWords(run.Text))
                            _toks.Add(new Tok { Text = w.text, Space = w.space, Kind = kind, Style = st, Url = url, Username = username, UserId = userId, Data = data });
                }
            }
        }

        private static IEnumerable<(string text, bool space)> SplitWords(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                bool sp = char.IsWhiteSpace(s[i]);
                int j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j]) == sp) j++;
                yield return (s.Substring(i, j - i), sp);
                i = j;
            }
        }

        // ── Style fonts (derived from the base script font; cached) ──────────
        private Font _fReg, _fBold, _fMono, _fMonoBold;
        private string _fKey;

        private void EnsureFonts(Font baseFont)
        {
            string key = baseFont.FontFamily.Name + "|" + baseFont.Size.ToString("0.0");
            if (key == _fKey) return;
            DisposeFonts();
            _fKey = key;
            var fam = baseFont.FontFamily; float sz = baseFont.Size;
            _fReg = new Font(fam, sz, FontStyle.Regular);
            _fBold = MakeStyled(fam, sz, FontStyle.Bold);
            float msz = Math.Max(7f, sz - 1f);
            _fMono = MakeMono(msz, FontStyle.Regular);
            _fMonoBold = MakeMono(msz, FontStyle.Bold);
        }

        private static Font MakeStyled(FontFamily fam, float sz, FontStyle st)
        {
            try { return new Font(fam, sz, st); } catch { return new Font(fam, sz, FontStyle.Regular); }
        }

        private static Font MakeMono(float sz, FontStyle st)
        {
            try { return new Font("Consolas", sz, st); }
            catch { try { return new Font("Courier New", sz, st); } catch { return new Font(FontFamily.GenericMonospace, sz, st); } }
        }

        // The face used for measuring + drawing a run (italic is a draw-time shear, not a face).
        private Font FontFor(InlineStyle s)
        {
            bool bold = (s & InlineStyle.Bold) != 0;
            if ((s & (InlineStyle.Code | InlineStyle.Pre)) != 0) return bold ? _fMonoBold : _fMono;
            return bold ? _fBold : _fReg;
        }

        // ── Layout ───────────────────────────────────────────────────────────
        public int Measure(Graphics g, int maxWidth, Font baseFont)
        {
            string key = maxWidth + "|" + baseFont.FontFamily.Name + "|" + baseFont.Size.ToString("0.0");
            if (key == _measureKey) { PerfLog.Inc(PerfLog.P.MeasureRedundant); return _measuredMaxLine; }   // cache hit — layout unchanged, skip recompute
            long __t = PerfLog.T();
            _measureKey = key;
            _positionKey = null;   // the lines changed → Position must recompute (X + selection model)
            _measuredMaxLine = MeasureCore(g, maxWidth, baseFont);
            PerfLog.Rec(PerfLog.P.Measure, __t);
            return _measuredMaxLine;
        }

        private int MeasureCore(Graphics g, int maxWidth, Font baseFont)
        {
            EnsureFonts(baseFont);
            _lineH = Math.Max(14, (int)Math.Ceiling(baseFont.GetHeight(g)) + 2);
            _emojiSz = _lineH - 2;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int slant = (int)(_lineH * 0.2f);

            foreach (var t in _toks)
            {
                if (t.Newline) { t.W = 0; continue; }
                if (t.DocId != 0) t.Image = _resolver != null ? _resolver(t.DocId) : null;
                if (t.Image != null) { t.W = _emojiSz + 1; continue; }
                if (string.IsNullOrEmpty(t.Text)) { t.W = 0; continue; }
                t.W = TextRenderer.MeasureText(g, t.Text, FontFor(t.Style), new Size(int.MaxValue, _lineH), flags).Width;
                if ((t.Style & InlineStyle.Italic) != 0 && !t.Space) t.W += slant;   // reserve the slant overhang
            }

            _lines.Clear();
            var cur = new Line();
            int x = 0, maxLine = 0, y = 0;

            void BreakLine() { cur.Y = y; Finalize(cur); _lines.Add(cur); y += _lineH; cur = new Line(); x = 0; }

            // Measured width of s[a..b) in a given face (+ the italic slant overhang).
            int SegW(string s, int a, int b, Font f, bool italic)
            {
                int w = TextRenderer.MeasureText(g, s.Substring(a, b - a), f, new Size(int.MaxValue, _lineH), flags).Width;
                if (italic) w += slant;
                return w;
            }

            // Character-level wrap for a single token WIDER than a line (long URLs / code / config strings,
            // which carry no spaces for the word-wrapper to break on). Splits into per-line sub-tokens that
            // inherit the parent's style/kind so code pills, pre panels and hit-testing keep working. The
            // chunk text stays LTR (TextRenderer, no RTL flag); chunks are one-per-line so they're never
            // mirrored inside an RTL message.
            void PlaceWide(Tok t, int avail, int indent)
            {
                var font = FontFor(t.Style);
                bool italic = (t.Style & InlineStyle.Italic) != 0;
                string s = t.Text;
                int start = 0;
                while (start < s.Length)
                {
                    int remain = avail - x;
                    if (remain <= 6 && cur.Toks.Count > 0) { BreakLine(); remain = avail; }

                    // Binary-search the longest prefix [start..best) that fits the remaining width.
                    int lo = start + 1, hi = s.Length, best = start, bestW = 0;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        int wseg = SegW(s, start, mid, font, italic);
                        if (wseg <= remain) { best = mid; bestW = wseg; lo = mid + 1; } else hi = mid - 1;
                    }
                    if (best == start)   // not even one char fits in what's left
                    {
                        if (cur.Toks.Count > 0) { BreakLine(); continue; }       // retry on a fresh line
                        best = start + 1; bestW = SegW(s, start, best, font, italic);   // force ≥1 char (no infinite loop)
                    }

                    var sub = new Tok
                    {
                        Text = s.Substring(start, best - start), W = bestW,
                        Kind = t.Kind, Style = t.Style, Url = t.Url, Username = t.Username, UserId = t.UserId, Data = t.Data
                    };
                    sub.Rect = new Rectangle(0, y, bestW, _lineH);
                    cur.Toks.Add(sub); x += bestW;
                    maxLine = Math.Max(maxLine, x + indent);
                    start = best;
                    if (start < s.Length) BreakLine();   // more characters remain → next line
                }
            }

            foreach (var t in _toks)
            {
                if (t.Newline) { BreakLine(); continue; }
                int indent = (t.Style & InlineStyle.Blockquote) != 0 ? QuoteIndent : 0;
                bool isPre = (t.Style & InlineStyle.Pre) != 0;
                int avail = Math.Max(8, maxWidth - indent);
                if (cur.Toks.Count == 0 && t.Space && !isPre) continue;       // no leading space (except pre)

                if (!t.Space && t.Image == null && !string.IsNullOrEmpty(t.Text) && t.W > avail)
                {
                    PlaceWide(t, avail, indent);   // unbreakable token wider than a line → split at characters
                    continue;
                }

                if (!t.Space && cur.Toks.Count > 0 && x + t.W > avail)        // wrap before this word
                    BreakLine();
                t.Rect = new Rectangle(0, y, t.W, _lineH);
                cur.Toks.Add(t); x += t.W;
                maxLine = Math.Max(maxLine, x + indent);
            }
            if (cur.Toks.Count > 0) { cur.Y = y; Finalize(cur); _lines.Add(cur); y += _lineH; }

            Height = y;
            Width = maxLine;
            return maxLine;
        }

        private static void Finalize(Line line)
        {
            foreach (var t in line.Toks)
            {
                if ((t.Style & InlineStyle.Blockquote) != 0) line.Quote = true;
                if ((t.Style & InlineStyle.Pre) != 0) line.Pre = true;
            }
            line.Indent = line.Quote ? QuoteIndent : 0;
        }

        public void Position(int width, bool rtl)
        {
            string pkey = width + "|" + (rtl ? 1 : 0);
            if (pkey == _positionKey) return;   // cache hit — X positions + selection model unchanged, skip
            PerfLog.Inc(PerfLog.P.Position);    // real (re)position (only on a width/direction/text change)
            _positionKey = pkey;
            _rtl = rtl;
            Width = width;
            foreach (var line in _lines)
            {
                if (!rtl)
                {
                    int x = line.Indent;
                    foreach (var t in line.Toks) { t.Rect.X = x; x += t.W; }
                }
                else
                {
                    int x = width - line.Indent;
                    foreach (var t in line.Toks) { x -= t.W; t.Rect.X = x; }
                }
            }
            BuildSelectionModel();
        }

        // ── Selection: char hit-testing, range highlight, copy-text (RTL-aware) ──────────────────
        // The shared engine over BOTH chat bubbles and the profile label. Tokens are word-granular, so a point
        // inside a token resolves to a character via substring-width measurement; in an RTL (Persian) run the
        // measurement runs from the token's RIGHT edge, so the char index is direction-correct.

        private void BuildSelectionModel()
        {
            _selText.Clear();
            int pos = 0;
            for (int li = 0; li < _lines.Count; li++)
            {
                if (li > 0) { _selText.Append('\n'); pos += 1; }   // line break between laid-out lines
                var line = _lines[li];
                line.SelStart = pos;
                foreach (var t in line.Toks)
                {
                    t.SelStart = pos;
                    string txt = t.Text ?? "";
                    t.SelLen = txt.Length;
                    _selText.Append(txt);
                    pos += t.SelLen;
                }
                line.SelEnd = pos;
            }
            _selLen = pos;
        }

        private int MeasW(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, _lineH),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        /// <summary>Maps a point (local to the paint origin) to a character index in the selectable string.</summary>
        public int HitChar(Point p)
        {
            if (_lines.Count == 0 || _selLen == 0) return 0;
            if (p.Y < _lines[0].Y) return 0;
            Line line = null;
            for (int i = 0; i < _lines.Count; i++)
                if (p.Y < _lines[i].Y + _lineH) { line = _lines[i]; break; }
            if (line == null) return _selLen;            // below the last line → end
            if (line.Toks.Count == 0) return line.SelStart;

            for (int i = 0; i < line.Toks.Count; i++)
            {
                var t = line.Toks[i];
                int left = t.Rect.X, right = t.Rect.X + t.W;
                if (p.X >= left && p.X <= right) return t.SelStart + CharInToken(t, p.X - left);
                bool beforeThis = _rtl ? (p.X > right) : (p.X < left);   // gap before this token in reading order
                if (beforeThis) return t.SelStart;
            }
            var last = line.Toks[line.Toks.Count - 1];
            return last.SelStart + last.SelLen;
        }

        // dx = pixels from the token's LEFT edge → logical char index within the token (RTL measures from right).
        private int CharInToken(Tok t, int dx)
        {
            if (t.Image != null) return dx > t.W / 2 ? t.SelLen : 0;   // emoji is atomic: snap to start/end
            if (string.IsNullOrEmpty(t.Text)) return 0;
            var f = FontFor(t.Style);
            int target = _rtl ? (t.W - dx) : dx;
            if (target <= 0) return 0;
            if (target >= t.W) return t.Text.Length;
            int prevW = 0;
            for (int k = 1; k <= t.Text.Length; k++)
            {
                int w = MeasW(t.Text.Substring(0, k), f);
                if (target <= w) return (target - prevW <= w - target) ? (k - 1) : k;
                prevW = w;
            }
            return t.Text.Length;
        }

        public string GetRangeText(int a, int b)
        {
            if (a < 0) a = 0;
            if (b > _selLen) b = _selLen;
            if (b <= a) return "";
            return _selText.ToString(a, b - a);
        }

        // x of a selectable index on a line (the boundary before that char), RTL-aware. idx is CLAMPED to the
        // line's [SelStart..SelEnd]; idx == SelEnd is the valid "after the last char" position (the line's end
        // edge), NOT an out-of-range access. An empty line (a blank line — zero tokens) has no glyphs, so it
        // returns the line's start edge and the caller's edge-extension fills it.
        private int XOfIndex(Line line, int idx)
        {
            if (line.Toks.Count == 0) return _rtl ? Width : 0;   // empty (blank) line — no tokens to index
            var first = line.Toks[0];
            var last = line.Toks[line.Toks.Count - 1];
            if (idx <= line.SelStart) return _rtl ? first.Rect.X + first.W : first.Rect.X;   // line start (reading edge)
            if (idx >= line.SelEnd) return _rtl ? last.Rect.X : last.Rect.X + last.W;          // line end (after last char)

            foreach (var t in line.Toks)
            {
                if (idx <= t.SelStart) return _rtl ? t.Rect.X + t.W : t.Rect.X;
                if (idx <= t.SelStart + t.SelLen)
                {
                    int within = idx - t.SelStart;
                    if (t.Image != null || string.IsNullOrEmpty(t.Text))
                        return _rtl ? (within > 0 ? t.Rect.X : t.Rect.X + t.W) : (within > 0 ? t.Rect.X + t.W : t.Rect.X);
                    int w = MeasW(t.Text.Substring(0, Math.Min(within, t.Text.Length)), FontFor(t.Style));
                    return _rtl ? (t.Rect.X + t.W - w) : (t.Rect.X + w);
                }
            }
            return _rtl ? last.Rect.X : last.Rect.X + last.W;   // safe fallback (idx within range was handled above)
        }

        /// <summary>Draws the selection highlight behind chars [a,b) — one rect per line, RTL-correct, with
        /// middle lines filled edge-to-edge so a multi-line selection reads continuously.</summary>
        public void PaintSelectionHighlight(Graphics g, int ox, int oy, int a, int b, Color col)
        {
            if (a < 0) a = 0;
            if (b > _selLen) b = _selLen;
            if (b <= a || _lines.Count == 0) return;
            using (var brush = new SolidBrush(col))
                foreach (var line in _lines)
                {
                    int ls = line.SelStart, le = line.SelEnd;
                    if (b <= ls || a > le) continue;            // selection doesn't touch this line
                    int sa = Math.Max(a, ls), sb = Math.Min(b, le);   // selected sub-range ON this line (clamped)
                    int xA = XOfIndex(line, sa), xB = XOfIndex(line, sb);
                    int xLeft = Math.Min(xA, xB), xRight = Math.Max(xA, xB);
                    if (a < ls) { if (_rtl) xRight = Width; else xLeft = 0; }   // started on a previous line
                    if (b > le) { if (_rtl) xLeft = 0; else xRight = Width; }   // continues to the next line
                    if (xRight > xLeft) g.FillRectangle(brush, ox + xLeft, oy + line.Y, xRight - xLeft, _lineH);
                }
        }

        // ── Paint ─────────────────────────────────────────────────────────────
        public void Paint(Graphics g, int ox, int oy, Color textColor, Color linkColor, Font baseFont,
                          bool dark, Color accent, bool outgoing)
        {
            long __t = PerfLog.T();
            PaintCore(g, ox, oy, textColor, linkColor, baseFont, dark, accent, outgoing);
            PerfLog.Rec(PerfLog.P.RichPaint, __t);
        }

        /// <summary>
        /// Like <see cref="Paint"/> but renders the text+emoji to a cached OPAQUE bitmap ONCE (keyed by layout
        /// size + colors) and blits it (DrawImageUnscaled) on repeats — turning the ~37ms per-token render into
        /// a ~1ms blit. The bitmap is opaque (GDI/TextRenderer needs an opaque bg for correct AA), so the caller
        /// bakes the bubble's interior <paramref name="bgColor"/> and draws the (translucent) selection highlight
        /// + checkmarks/timestamp OVER the blit. Re-renders when size/colors change (text change = new instance =
        /// fresh cache). Falls back to direct PaintCore if a bitmap can't be created. RTL is baked correct.
        /// </summary>
        public void PaintCached(Graphics g, int ox, int oy, Color textColor, Color linkColor, Font baseFont,
                                bool dark, Color accent, bool outgoing, Color bgColor)
        {
            long __t = PerfLog.T();
            if (Width <= 0 || Height <= 0 || (long)Width * Height > 4000000)   // empty / absurdly tall → don't cache
            {
                PaintCore(g, ox, oy, textColor, linkColor, baseFont, dark, accent, outgoing);
                PerfLog.Rec(PerfLog.P.RichPaint, __t);
                return;
            }
            string key = Width + "x" + Height + "|" +
                (textColor.ToArgb() ^ (linkColor.ToArgb() * 3) ^ (accent.ToArgb() * 5) ^ (bgColor.ToArgb() * 7) ^ (dark ? 1 : 0) ^ (outgoing ? 2 : 0))
                + "|" + (_spoilerRevealed ? 1 : 0);   // spoiler reveal changes the render → re-key
            if (_bmp == null || key != _bmpKey)
            {
                try
                {
                    if (_bmp != null) _bmp.Dispose();
                    var bmp = new Bitmap(Width, Height);
                    using (var bg = Graphics.FromImage(bmp))
                    {
                        bg.Clear(bgColor);   // opaque background → correct GDI text antialiasing
                        bg.SmoothingMode = SmoothingMode.AntiAlias;
                        bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        PaintCore(bg, 0, 0, textColor, linkColor, baseFont, dark, accent, outgoing);
                    }
                    _bmp = bmp; _bmpKey = key;
                    PerfLog.Inc(PerfLog.P.RichPaintRender);   // a real (re)render
                }
                catch
                {
                    if (_bmp != null) { _bmp.Dispose(); _bmp = null; _bmpKey = null; }
                    PaintCore(g, ox, oy, textColor, linkColor, baseFont, dark, accent, outgoing);   // fallback
                    PerfLog.Rec(PerfLog.P.RichPaint, __t);
                    return;
                }
            }
            g.DrawImageUnscaled(_bmp, ox, oy);
            PerfLog.Rec(PerfLog.P.RichPaint, __t);
        }

        private void PaintCore(Graphics g, int ox, int oy, Color textColor, Color linkColor, Font baseFont,
                          bool dark, Color accent, bool outgoing)
        {
            EnsureFonts(baseFont);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            Color codeBg = outgoing ? Color.FromArgb(46, 255, 255, 255) : (dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0));
            Color preBg = outgoing ? Color.FromArgb(55, 0, 0, 0) : (dark ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(22, 0, 0, 0));
            Color barColor = outgoing ? Color.White : accent;
            Color quoteBg = Color.FromArgb(outgoing ? 28 : (dark ? 34 : 20), barColor);
            Color spoilerBg = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(168, 168, 176);

            // Pass A — Pre panels (group consecutive pre lines into one full-width panel).
            for (int i = 0; i < _lines.Count;)
            {
                if (!_lines[i].Pre) { i++; continue; }
                int j = i; while (j < _lines.Count && _lines[j].Pre) j++;
                var pr = Rectangle.FromLTRB(ox, _lines[i].Y + oy - 1, ox + Width, _lines[j - 1].Y + _lineH + oy + 1);
                using (var b = new SolidBrush(preBg))
                using (var p = DrawHelper.RoundedRect(pr, 6)) g.FillPath(b, p);
                i = j;
            }
            // Pass B — Blockquote bar + faint tint (group consecutive quote lines); bar on the leading edge.
            for (int i = 0; i < _lines.Count;)
            {
                if (!_lines[i].Quote) { i++; continue; }
                int j = i; while (j < _lines.Count && _lines[j].Quote) j++;
                int y0 = _lines[i].Y + oy, y1 = _lines[j - 1].Y + _lineH + oy;
                using (var bg = new SolidBrush(quoteBg)) g.FillRectangle(bg, ox, y0, Width, y1 - y0);
                int barX = _rtl ? ox + Width - BarW : ox;
                using (var bb = new SolidBrush(barColor)) g.FillRectangle(bb, barX, y0, BarW, y1 - y0);
                i = j;
            }
            // Pass C — inline code pills (contiguous code tokens per line).
            foreach (var line in _lines) PaintRunBg(g, line, ox, oy, InlineStyle.Code, codeBg, 5, true);
            // Pass D — spoiler covers (hidden until revealed).
            if (!_spoilerRevealed)
                foreach (var line in _lines) PaintRunBg(g, line, ox, oy, InlineStyle.Spoiler, spoilerBg, 4, false);

            // Pass E — the tokens.
            foreach (var line in _lines)
                foreach (var t in line.Toks)
                {
                    int x = t.Rect.X + ox, y = t.Rect.Y + oy;
                    if (t.Image != null) { g.DrawImage(t.Image, x, y + (_lineH - _emojiSz) / 2, _emojiSz, _emojiSz); continue; }
                    if (t.Space || string.IsNullOrEmpty(t.Text)) continue;
                    if ((t.Style & InlineStyle.Spoiler) != 0 && !_spoilerRevealed) continue;   // hidden under the cover

                    bool link = t.Kind != InlineKind.Plain;
                    Color c = link ? linkColor : textColor;
                    Font f = FontFor(t.Style);
                    if ((t.Style & InlineStyle.Italic) != 0) DrawItalic(g, t.Text, f, x, y, c);
                    else TextRenderer.DrawText(g, t.Text, f, new Point(x, y), c, flags);

                    if (link || (t.Style & InlineStyle.Underline) != 0)
                        using (var p = new Pen(c)) g.DrawLine(p, x, y + _lineH - 2, x + t.W, y + _lineH - 2);
                    if ((t.Style & InlineStyle.Strike) != 0)
                        using (var p = new Pen(c)) g.DrawLine(p, x, y + _lineH / 2, x + t.W, y + _lineH / 2);
                }
        }

        /// <summary>Draws a rounded background behind each contiguous run of tokens carrying <paramref name="style"/>.</summary>
        private void PaintRunBg(Graphics g, Line line, int ox, int oy, InlineStyle style, Color fill, int radius, bool tight)
        {
            int i = 0;
            var toks = line.Toks;
            while (i < toks.Count)
            {
                if ((toks[i].Style & style) == 0) { i++; continue; }
                int x0 = int.MaxValue, x1 = int.MinValue, j = i;
                while (j < toks.Count && (toks[j].Style & style) != 0)
                {
                    x0 = Math.Min(x0, toks[j].Rect.X);
                    x1 = Math.Max(x1, toks[j].Rect.X + toks[j].W);
                    j++;
                }
                int pad = tight ? 2 : 0;
                var r = Rectangle.FromLTRB(x0 + ox - pad, line.Y + oy + 1, x1 + ox + pad, line.Y + oy + _lineH - 1);
                if (r.Width > 0)
                    using (var b = new SolidBrush(fill))
                    using (var p = DrawHelper.RoundedRect(r, radius)) g.FillPath(b, p);
                i = j;
            }
        }

        private void DrawItalic(Graphics g, string text, Font font, int x, int y, Color color)
        {
            var saved = g.Save();
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.TranslateTransform(x, y);
            using (var m = new Matrix(1f, 0f, -0.2f, 1f, 0.2f * _lineH, 0f))   // faux-italic shear (leans forward)
                g.MultiplyTransform(m);
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
                g.DrawString(text, font, br, 0, 0, sf);
            g.Restore(saved);
        }

        // ── Hit-test + spoiler reveal ────────────────────────────────────────
        public InlineHit HitTest(Point p)
        {
            // Iterate the laid-out tokens (which include character-wrapped sub-tokens) so their
            // positioned rects are used — a split long URL is still hit across all its pieces.
            foreach (var line in _lines)
                foreach (var t in line.Toks)
                {
                    if (t.Kind == InlineKind.Plain || t.Space) continue;
                    if (t.Rect.Contains(p))
                        return new InlineHit { Kind = t.Kind, Url = t.Url, Username = t.Username, UserId = t.UserId, Data = t.Data };
                }
            return null;
        }

        /// <summary>True if the point is over a still-hidden spoiler run (tap should reveal, not navigate).</summary>
        public bool HasHiddenSpoilerAt(Point p)
        {
            if (_spoilerRevealed) return false;
            foreach (var line in _lines)
                foreach (var t in line.Toks)
                    if ((t.Style & InlineStyle.Spoiler) != 0 && t.Rect.Contains(p)) return true;
            return false;
        }

        public void RevealSpoilers() => _spoilerRevealed = true;

        // ── Disposal ─────────────────────────────────────────────────────────
        private void DisposeFonts()
        {
            _fReg?.Dispose(); _fBold?.Dispose(); _fMono?.Dispose(); _fMonoBold?.Dispose();
            _fReg = _fBold = _fMono = _fMonoBold = null; _fKey = null;
        }

        /// <summary>Forces a full recompute on the next paint — re-measure (re-resolves async custom-emoji
        /// images the resolver now has), reposition, and re-render the bitmap. Call when something OUTSIDE the
        /// (text,width,font,colors) key changed the render, e.g. a custom emoji finished loading.</summary>
        public void InvalidateLayout()
        {
            _measureKey = null;
            _positionKey = null;
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            _bmpKey = null;
        }

        public void Dispose()
        {
            DisposeFonts();
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; _bmpKey = null; }
        }
    }
}
