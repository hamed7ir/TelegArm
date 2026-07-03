using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// One chat-list row: avatar circle, name, last-message preview, timestamp,
    /// and an optional unread badge. Fully custom-painted.
    /// </summary>
    public class ChatListItemControl : Control
    {
        // MaterialSkin overwrites Control.Font after construction, so every font
        // used in OnPaint is held in its own field and referenced directly.
        // Name/preview/avatar fonts are chosen per-paint by script (Vazirmatn for Persian,
        // Roboto otherwise). Time/badge are always Latin.
        private readonly Font _timeFont = FontHelper.Ui(8f);
        private readonly Font _badgeFont = FontHelper.Ui(8f, FontStyle.Bold);

        public ChatEntry Entry { get; }

        /// <summary>Telegram-green presence dot (theme-independent, like official clients); shared —
        /// the row painter allocates nothing for it (PRESENCE hot-path law).</summary>
        private static readonly Brush OnlineDotBrush = new SolidBrush(Color.FromArgb(76, 204, 90));
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        /// <summary>Whether this chat is pinned in the CURRENTLY-SHOWN view (set by RenderChatList per view).
        /// Drives the pin glyph — pins are per-folder, never a global flag.</summary>
        public bool PinnedInView { get; set; }

        /// <summary>Profile photo (referenced — the MainForm avatar cache owns/disposes it);
        /// null falls back to the colored letter circle.</summary>
        public Image Avatar { get; set; }

        private bool _selected;
        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        /// <summary>Right-click / touch-and-hold on the row (screen point) — for the chat menu.</summary>
        public event EventHandler<Point> ContextMenuRequested;

        private const int WM_CONTEXTMENU = 0x007B;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CONTEXTMENU)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1
                    ? PointToScreen(new Point(Width / 2, Height / 2))
                    : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                ContextMenuRequested?.Invoke(this, pt);
                return;
            }
            base.WndProc(ref m);
        }

        public ChatListItemControl(ChatEntry entry)
        {
            Entry = entry;
            Height = 64;
            Margin = new Padding(0);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color bg = IsDark ? Color.FromArgb(40, 40, 40) : Color.White;
            Color nameColor = IsDark ? Color.White : Color.FromArgb(30, 30, 30);
            Color subColor = IsDark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
            if (_selected)
                bg = IsDark ? Color.FromArgb(55, 55, 58) : Color.FromArgb(225, 235, 245);
            g.Clear(bg);

            const int d = 48, ax = 8, ay = 8;
            var avatarRect = new Rectangle(ax, ay, d, d);
            if (Avatar != null)
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(avatarRect);
                    var old = g.Clip;
                    g.SetClip(clip);
                    g.DrawImage(Avatar, avatarRect);
                    g.Clip = old;
                }
            }
            else
            {
                using (var b = new SolidBrush(DrawHelper.AvatarColor(Entry.PeerId)))
                    g.FillEllipse(b, avatarRect);
                string letter = string.IsNullOrEmpty(Entry.Title) ? "?" : Entry.Title.Substring(0, 1).ToUpper();
                using (var af = FontHelper.For(Entry.Title ?? "", 15f, FontStyle.Bold))
                    TextRenderer.DrawText(g, letter, af, avatarRect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // PRESENCE 2.1: online dot (user peers) — bottom-right of the avatar, green with a 2px
            // background-colored rim. HOT-PATH LAW: reads ONLY the cached Entry.OnlineUntil (no TL
            // object, no formatting, no RPC); the dot dies via UpdateUserStatus or the 15s sweep.
            if (Entry.OnlineUntil > DateTime.UtcNow)
            {
                using (var rim = new SolidBrush(bg))
                    g.FillEllipse(rim, ax + d - 14, ay + d - 14, 14, 14);
                g.FillEllipse(OnlineDotBrush, ax + d - 12, ay + d - 12, 10, 10);
            }

            int textLeft = ax + d + 10;
            const int rightPad = 10, timeWidth = 56;
            bool hasBadge = Entry.UnreadCount > 0;
            int muteW = Entry.Muted ? 18 : 0;

            // NAME (top row): reserve the top-right metadata (timestamp + mute glyph). Taller rect +
            // vertical-center so Persian (Vazirmatn, taller line height than Roboto) isn't clipped.
            int nameRight = Width - timeWidth - rightPad - muteW;
            var nameRect = new Rectangle(textLeft, 6, Math.Max(0, nameRight - textLeft), 26);
            using (var nf = FontHelper.For(Entry.Title ?? "", 10f, FontStyle.Bold))
                DrawRowText(g, Entry.Title ?? "", nf, nameRect, nameColor);

            // Mute glyph sits in the right metadata column (left of the time) — clear of the name rect above.
            if (Entry.Muted)
                DrawMutedIcon(g, new Rectangle(Width - timeWidth - rightPad - 16, 15, 14, 14), subColor);

            var timeRect = new Rectangle(Width - timeWidth - rightPad, 13, timeWidth, 16);
            TextRenderer.DrawText(g, DrawHelper.ShortStamp(Entry.Date), _timeFont, timeRect, subColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // PREVIEW (bottom row): reserve the bottom-right slot (badge OR pin). Taller rect + vertical center.
            int trailing = hasBadge ? 40 : (PinnedInView ? 22 : 0);
            int prevRight = Width - rightPad - trailing;
            var prevRect = new Rectangle(textLeft, 34, Math.Max(0, prevRight - textLeft), 24);
            using (var pf = FontHelper.For(Entry.Preview ?? "", 9f))
                DrawRowText(g, Entry.Preview ?? "", pf, prevRect, subColor);

            // Trailing-slot precedence: unread badge > pin glyph > nothing.
            if (hasBadge)
            {
                string cnt = Entry.UnreadCount > 99 ? "99+" : Entry.UnreadCount.ToString();
                int bw = Math.Max(20, TextRenderer.MeasureText(cnt, _badgeFont).Width + 10);
                const int bh = 20;
                var badge = new Rectangle(Width - bw - rightPad, 36, bw, bh);
                // Muted chats get a GREY badge; unmuted get the ACCENT badge.
                Color badgeColor = Entry.Muted
                    ? (IsDark ? Color.FromArgb(120, 120, 124) : Color.FromArgb(178, 178, 184))
                    : AccentColor;
                using (var b = new SolidBrush(badgeColor))
                using (var path = DrawHelper.RoundedRect(badge, bh / 2))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, cnt, _badgeFont, badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else if (PinnedInView)
                DrawPinIcon(g, new Rectangle(Width - rightPad - 16, 38, 14, 14), subColor);

            using (var p = new Pen(IsDark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(235, 235, 235)))
                g.DrawLine(p, textLeft, Height - 1, Width, Height - 1);
        }

        /// <summary>
        /// Draws one row line clipped to <paramref name="rect"/>: inline color emoji when present, else
        /// TextRenderer (script fallback) single-line, vertically-centered, ellipsized — and right-aligned
        /// RTL (Right | RightToLeft) for Persian/Arabic/Hebrew so it ellipsizes on the left, not hard-clips.
        /// </summary>
        private static void DrawRowText(Graphics g, string text, Font font, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || string.IsNullOrEmpty(text)) return;
            var oldClip = g.Clip;
            g.SetClip(rect);   // no glyph bleeds outside the row's text area
            if (EmojiRenderer.Available && EmojiRenderer.ContainsEmoji(text))
            {
                DrawInlineRow(g, text, font, rect, color);   // inline emoji, but text via TextRenderer (Persian-safe)
            }
            else
            {
                var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                          | TextFormatFlags.VerticalCenter
                          | (IsRtl(text) ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
                TextRenderer.DrawText(g, text, font, rect, color, flags);
            }
            g.Clip = oldClip;
        }

        /// <summary>
        /// Inline emoji line for a row (NAME or PREVIEW): emoji as square bitmaps + text runs via TextRenderer
        /// (script fallback → Persian marks aren't clipped). Single layout for both:
        ///  • emoji square sized to FIT the rect (capped, not font.GetHeight — the Persian font's tall line height was
        ///    clipping names), vertically centered;
        ///  • dominant direction from the first strong-directional char — RTL lays runs visually right→left
        ///    (first logical run at the RIGHT), LTR left→right; RTL text sub-runs drawn Right|RightToLeft;
        ///  • width-budgeted to rect.Width (caller already reserved the trailing slot): runs past the budget are
        ///    dropped and a single "…" is drawn at the truncation edge — LEFT for RTL, RIGHT for LTR — so runs
        ///    never collide/overflow.
        /// Mixed-bidi ordering of emoji nested in RTL text stays approximate (acceptable).
        /// </summary>
        private static void DrawInlineRow(Graphics g, string text, Font font, Rectangle rect, Color color)
        {
            int es = Math.Max(12, Math.Min(16, rect.Height - 6));   // emoji size that FITS the rect (no top/bottom clip)
            int cy = rect.Y + rect.Height / 2;
            var measFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            // Build runs in logical order with advance widths (text measured via TextRenderer).
            var runs = new List<(Image img, string txt, int w)>();
            foreach (var run in EmojiRenderer.Segment(text))
            {
                if (run.Emoji != null) runs.Add((run.Emoji, null, es + 2));
                else if (!string.IsNullOrEmpty(run.Text))
                    runs.Add((null, run.Text,
                        TextRenderer.MeasureText(g, run.Text, font, new Size(int.MaxValue, rect.Height), measFlags).Width));
            }
            if (runs.Count == 0) return;

            bool rtl = IsRtlDominant(text);
            var drawFlags = measFlags | TextFormatFlags.VerticalCenter
                          | (rtl ? (TextFormatFlags.Right | TextFormatFlags.RightToLeft) : TextFormatFlags.Left);
            var ellipsisFlags = drawFlags | TextFormatFlags.EndEllipsis;

            // Walk runs in layout order; a text run that overflows the remaining width is ELLIPSIZED in
            // that remaining width (not dropped), so a long preview fills the line + ellipsizes cleanly.
            if (!rtl)   // left → right; overflow ellipsis on the RIGHT
            {
                int x = rect.X;
                foreach (var r in runs)
                {
                    int remaining = rect.Right - x;
                    if (remaining <= 0) break;
                    if (r.img != null)
                    {
                        if (r.w > remaining) break;                      // no room for the emoji
                        g.DrawImage(r.img, x, cy - es / 2, es, es); x += r.w;
                    }
                    else if (r.w <= remaining)
                    {
                        TextRenderer.DrawText(g, r.txt, font, new Rectangle(x, rect.Y, r.w, rect.Height), color, drawFlags);
                        x += r.w;
                    }
                    else
                    {
                        TextRenderer.DrawText(g, r.txt, font, new Rectangle(x, rect.Y, remaining, rect.Height), color, ellipsisFlags);
                        break;
                    }
                }
            }
            else        // right → left; first logical run at the RIGHT, overflow ellipsis on the LEFT
            {
                int x = rect.Right;
                foreach (var r in runs)
                {
                    int remaining = x - rect.X;
                    if (remaining <= 0) break;
                    if (r.img != null)
                    {
                        if (r.w > remaining) break;
                        x -= r.w; g.DrawImage(r.img, x, cy - es / 2, es, es);
                    }
                    else if (r.w <= remaining)
                    {
                        x -= r.w;
                        TextRenderer.DrawText(g, r.txt, font, new Rectangle(x, rect.Y, r.w, rect.Height), color, drawFlags);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, r.txt, font, new Rectangle(rect.X, rect.Y, remaining, rect.Height), color, ellipsisFlags);
                        break;
                    }
                }
            }
        }

        /// <summary>True if the text contains Hebrew/Arabic/Persian letters (→ right-align the line).</summary>
        private static bool IsRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if ((c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF))
                    return true;
            return false;
        }

        /// <summary>Dominant direction by the FIRST strong-directional char (emoji/digits/punctuation are
        /// neutral and skipped): strong RTL → true, strong LTR → false, none → false (LTR).</summary>
        private static bool IsRtlDominant(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= 0x00C0 && c <= 0x024F) || (c >= 0x0370 && c <= 0x03FF) || (c >= 0x0400 && c <= 0x04FF))
                    return false;   // strong LTR (Latin / Greek / Cyrillic)
                if ((c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF))
                    return true;    // strong RTL (Hebrew / Arabic / Persian)
            }
            return false;
        }

        /// <summary>Clean Telegram-like pin/tack: round head + straight needle.</summary>
        private static void DrawPinIcon(Graphics g, Rectangle r, Color color)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var b = new SolidBrush(color))
            using (var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.FillEllipse(b, cx - 4f, cy - 6f, 8f, 8f);     // round head
                g.DrawLine(pen, cx, cy + 1.5f, cx, cy + 6.5f);  // needle
            }
        }

        private static void DrawMutedIcon(Graphics g, Rectangle r, Color color)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (var b = new SolidBrush(color))
            using (var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.FillRectangle(b, cx - 5, cy - 2, 3, 4);                 // speaker body
                g.FillPolygon(b, new[]                                     // cone
                {
                    new PointF(cx - 2, cy - 4), new PointF(cx + 1, cy - 6),
                    new PointF(cx + 1, cy + 6), new PointF(cx - 2, cy + 4)
                });
                g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);          // mute slash
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timeFont.Dispose();
                _badgeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
