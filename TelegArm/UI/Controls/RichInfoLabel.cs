using System;
using System.Drawing;
using System.Windows.Forms;
using TL;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Renders a peer's "about"/description as RICH text — clickable URLs / @mentions / #hashtags (auto-detected
    /// by <c>TextEntities</c>) and standard emoji — through the SAME <see cref="InlineText"/> engine + EmojiRenderer
    /// that paint chat bubbles. RTL-aware (Persian), auto-heights to its content so it scrolls fully inside the
    /// profile's existing scroll host. SELECTABLE + COPYABLE via the shared <see cref="InlineTextSelection"/>
    /// engine (mouse drag-select; right-click / long-press → "Copy Selected Text" / "Select All"). Raises
    /// link/mention/hashtag events the host routes to the existing in-app resolvers.
    /// </summary>
    public sealed class RichInfoLabel : Control
    {
        private InlineText _rich;
        private bool _rtl;
        private int _lastW = -1;
        private readonly bool _dark;
        private readonly Color _accent, _fg, _link;
        private const int Pad = 2;

        private InlineTextSelection _sel;
        private bool _suppressClick;
        private Color SelColor => Color.FromArgb(110, _accent);

        public event Action<string> LinkClicked;
        public event Action<string, long> MentionClicked;
        public event Action<string> HashtagClicked;

        public RichInfoLabel(bool dark, Color accent, Font font)
        {
            _dark = dark; _accent = accent;
            _fg = dark ? Color.FromArgb(225, 225, 228) : Color.FromArgb(30, 30, 32);
            _link = accent;
            Font = font ?? new Font("Segoe UI", 9.75f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _sel = new InlineTextSelection(this);
        }

        public void SetText(string text, MessageEntity[] entities, Func<long, Image> customEmojiResolver)
        {
            if (_rich != null) _rich.Dispose();
            _rich = string.IsNullOrEmpty(text) ? null : new InlineText(text, entities, customEmojiResolver);
            _sel.Attach(_rich);
            _rtl = IsRtl(text);
            _lastW = -1;
            Relayout();
        }

        private void Relayout()
        {
            if (_rich == null || Width <= Pad * 2) { if (Height != 0) Height = 0; Invalidate(); return; }
            int w = Width - Pad * 2;
            using (var g = CreateGraphics()) { _rich.Measure(g, w, Font); _rich.Position(w, _rtl); }
            int h = _rich.Height + Pad * 2;
            if (Height != h) Height = h;   // FlowLayoutPanel re-lays out → the profile scroll grows
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width != _lastW) { _lastW = Width; Relayout(); }   // only re-measure on a width change (no layout loop)
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_rich == null) return;
            if (_sel != null) _sel.Paint(e.Graphics, Pad, Pad, SelColor);
            _rich.Paint(e.Graphics, Pad, Pad, _fg, _link, Font, _dark, _accent, false);
        }

        private Point Local(Point p) { return new Point(p.X - Pad, p.Y - Pad); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && _rich != null) _sel.MouseDown(Local(e.Location));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_sel != null && _sel.MouseMove(Local(e.Location))) { Cursor = Cursors.IBeam; return; }
            var hit = _rich != null ? _rich.HitTest(Local(e.Location)) : null;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_sel != null && e.Button == MouseButtons.Left) _suppressClick = _sel.MouseUp();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_rich == null) return;
            if (_suppressClick) { _suppressClick = false; return; }      // a drag-select just ended → keep it; not a click
            // (a plain LEFT click already cleared the selection in InlineTextSelection.MouseUp — see the bubble note)
            var hit = _rich.HitTest(Local(e.Location));
            if (hit == null) return;
            if (hit.Kind == InlineKind.Url && !string.IsNullOrEmpty(hit.Url)) { if (LinkClicked != null) LinkClicked(hit.Url); }
            else if (hit.Kind == InlineKind.Mention) { if (MentionClicked != null) MentionClicked(hit.Username, hit.UserId); }
            else if (hit.Kind == InlineKind.Hashtag) { if (HashtagClicked != null) HashtagClicked(hit.Data); }
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == 0x007B && _sel != null)   // WM_CONTEXTMENU (right-click / touch long-press)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1
                    ? PointToScreen(new Point(Width / 2, Height / 2))
                    : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                ShowSelectionMenu(pt);
                return;
            }
            base.WndProc(ref m);
        }

        private void ShowSelectionMenu(Point screenPt)
        {
            if (!_sel.HasSelection && !_sel.HasText) return;
            var menu = new TelegArm.UI.ThemedContextMenuStrip();
            if (_sel.HasSelection)
            {
                var copy = new ToolStripMenuItem("Copy Selected Text") { Font = Font };
                copy.Click += (s, e) => { try { Clipboard.SetText(_sel.SelectedText ?? ""); } catch { } _sel.Clear(); };
                menu.Items.Add(copy);
            }
            else
            {
                var all = new ToolStripMenuItem("Select All") { Font = Font };
                all.Click += (s, e) => _sel.SelectAll();
                menu.Items.Add(all);
            }
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screenPt);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _rich != null) { _rich.Dispose(); _rich = null; }
            base.Dispose(disposing);
        }

        private static bool IsRtl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if ((c >= 0x0590 && c <= 0x05FF) || (c >= 0x0600 && c <= 0x06FF) ||
                    (c >= 0x0750 && c <= 0x077F) || (c >= 0x08A0 && c <= 0x08FF) ||
                    (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF)) return true;
            return false;
        }
    }
}
