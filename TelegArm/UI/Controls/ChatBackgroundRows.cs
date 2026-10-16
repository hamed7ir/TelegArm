using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 5.4 — the chat-background presets of Settings → Appearance: "Default" (the theme's plain chat)
    /// and the built-in backgrounds (Palette.ChatPresets), each a small tile baked by ChatBackdrop for the mode being edited.
    /// Tap or Left/Right picks; <see cref="Selected"/> is 0 for Default, i + 1 for preset i, -1 when the background is
    /// something else (a colour, a gradient, a pattern or a photo of the user's).</summary>
    public sealed class ChatPresetRow : SettingRow
    {
        private const int Tile = 44, Gap = 10, Ring = 3, Height2 = 104;
        private int _sel;
        private bool _dark;
        private Bitmap[] _tiles;
        private int _tilesDpi;
        public event EventHandler Changed;

        public ChatPresetRow(string caption, bool dark, int selected) : base(caption, true) { _dark = dark; _sel = selected; AccessibleRole = AccessibleRole.RadioButton; Refit(); }   // R6: a role

        /// <summary>The mode whose tiles are shown (Settings edits one mode at a time).</summary>
        public bool Dark { get { return _dark; } set { if (_dark == value) return; _dark = value; DropTiles(); Invalidate(); } }

        /// <summary>0 = Default, i + 1 = preset i, -1 = none of them. Setting it does not raise Changed.</summary>
        public int Selected { get { return _sel; } set { _sel = value; Invalidate(); } }

        private int Count { get { return Palette.ChatPresets.Length + 1; } }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(Height2, dpi); }

        private Rectangle TileRect(int i, int dpi)
        {
            int d = Dpi.Px(Tile, dpi), step = d + Dpi.Px(Gap, dpi);
            return new Rectangle(Dpi.Px(PadX, dpi) + Dpi.Px(Ring, dpi) + i * step, Height - Dpi.Px(14, dpi) - d, d, d);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int dpi = Dpi.Of(this);
            for (int i = 0; i < Count; i++)
                if (Rectangle.Inflate(TileRect(i, dpi), Dpi.Px(4, dpi), Dpi.Px(4, dpi)).Contains(e.Location)) { Pick(i); return; }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return ((keyData & (Keys.Alt | Keys.Control)) == 0 && (k == Keys.Left || k == Keys.Right)) || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { Pick(Math.Max(0, _sel - 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Pick(Math.Min(Count - 1, _sel + 1)); e.Handled = true; }
        }

        private void Pick(int i)
        {
            if (i == _sel) return;
            _sel = i;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override string AccessibleValueText
        {
            get { return _sel == 0 ? "Default" : _sel > 0 ? Palette.ChatPresets[_sel - 1].Name : "Custom"; }
        }

        protected override void OnDpiRescaled(int oldDpi, int newDpi) { DropTiles(); }
        protected override void OnThemeApplied() { DropTiles(); base.OnThemeApplied(); }

        private void DropTiles()
        {
            if (_tiles == null) return;
            foreach (var t in _tiles) if (t != null) t.Dispose();
            _tiles = null;
        }

        protected override void Dispose(bool disposing) { if (disposing) DropTiles(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            DrawCaption(g, p, new Rectangle(padX, Dpi.Px(4, dpi), Math.Max(1, Width - 2 * padX), Dpi.Px(SettingRow.DefaultHeight, dpi) - Dpi.Px(14, dpi)), dpi);
            int d = Dpi.Px(Tile, dpi);
            if (_tiles == null || _tilesDpi != dpi)
            {
                DropTiles();
                _tiles = new Bitmap[Count];
                _tilesDpi = dpi;
                for (int i = 0; i < Count; i++)
                {
                    var cs = new ChatBackgroundSettings { Mode = i == 0 ? "" : "preset", Preset = Math.Max(0, i - 1) };
                    try { _tiles[i] = ChatBackdrop.BakeSettings(cs, _dark, new Size(d, d), dpi); } catch { _tiles[i] = null; }
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Dpi.Px(10, dpi);
            for (int i = 0; i < Count; i++)
            {
                var r = TileRect(i, dpi);
                using (var path = DrawHelper.RoundedRect(r, radius))
                {
                    if (_tiles[i] != null) using (var tb = new TextureBrush(_tiles[i], WrapMode.Clamp)) { tb.TranslateTransform(r.X, r.Y); g.FillPath(tb, path); }
                    using (var pen = new Pen(p.Divider, Dpi.F(1f, dpi))) g.DrawPath(pen, path);
                }
                if (i == _sel)
                    using (var path = DrawHelper.RoundedRect(Rectangle.Inflate(r, Dpi.Px(Ring, dpi), Dpi.Px(Ring, dpi)), radius + Dpi.Px(Ring, dpi)))
                    using (var pen = new Pen(p.Text, Dpi.F(2f, dpi))) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues && _sel >= 0)
                using (var pen = new Pen(p.AccentGlyph, Dpi.F(1.5f, dpi)) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, Rectangle.Inflate(TileRect(_sel, dpi), Dpi.Px(Ring + 3, dpi), Dpi.Px(Ring + 3, dpi)));
        }
    }

    /// <summary>Redesign step 5.7 — the live preview tile of Settings → Appearance: the chat background of the mode being
    /// edited (ChatBackdrop's own bake, at tile size), a service pill and an incoming and an outgoing bubble in that mode's
    /// palette. <see cref="Preview"/> shows settings that are not applied yet (a slider being dragged re-bakes only this).</summary>
    public sealed class ThemePreviewRow : SettingRow
    {
        private const int Height2 = 132, Inset = 12;
        private bool _dark;
        private ChatBackgroundSettings _preview;
        private Bitmap _bake;
        private string _bakeKey;

        public ThemePreviewRow(bool dark) : base("Preview", false) { _dark = dark; AccessibleRole = AccessibleRole.Graphic; Refit(); }

        public bool Dark { get { return _dark; } set { _dark = value; Drop(); Invalidate(); } }

        /// <summary>Settings to show instead of the applied ones (null = the applied ones).</summary>
        public ChatBackgroundSettings Preview { get { return _preview; } set { _preview = value; Drop(); Invalidate(); } }

        /// <summary>Re-bake after the applied settings or the theme changed.</summary>
        public void Refresh2() { Drop(); Invalidate(); }

        public override int HeightFor(int width, int dpi) { return Dpi.Px(Height2, dpi); }

        private void Drop() { if (_bake != null) { _bake.Dispose(); _bake = null; } _bakeKey = null; }
        protected override void OnThemeApplied() { Drop(); base.OnThemeApplied(); }
        protected override void OnDpiRescaled(int oldDpi, int newDpi) { Drop(); }
        protected override void Dispose(bool disposing) { if (disposing) Drop(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var pal = ThemeHelper.PaletteFor(_dark);
            var g = e.Graphics;
            int dpi = Dpi.Of(this), inset = Dpi.Px(Inset, dpi);
            PaintBack(g, p);
            var r = new Rectangle(Dpi.Px(PadX, dpi), inset, Math.Max(1, Width - 2 * Dpi.Px(PadX, dpi)), Math.Max(1, Height - 2 * inset));
            string key = r.Size + "|" + dpi + "|" + _dark + "|" + (_preview == null ? "applied" : "preview") + "|" + pal.GetHashCode();
            if (_bake == null || _bakeKey != key)
            {
                Drop();
                var cs = _preview ?? AppSettings.Instance.Theme.For(_dark);
                try { _bake = ChatBackdrop.BakeSettings(cs, _dark, r.Size, dpi); } catch { _bake = null; }
                _bakeKey = key;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Dpi.Px(12, dpi);
            using (var path = DrawHelper.RoundedRect(r, radius))
            {
                if (_bake != null) using (var tb = new TextureBrush(_bake, WrapMode.Clamp)) { tb.TranslateTransform(r.X, r.Y); g.FillPath(tb, path); }
                else using (var b = new SolidBrush(pal.ChatBg)) g.FillPath(b, path);
                using (var pen = new Pen(p.Divider, Dpi.F(1f, dpi))) g.DrawPath(pen, path);
            }
            var cs2 = _preview ?? AppSettings.Instance.Theme.For(_dark);
            bool scrim = cs2.Mode != "";
            var small = FontHelper.UiAt(8f, dpi);   // shared: never dispose
            var text = FontHelper.UiAt(9.5f, dpi);
            // a service pill, an incoming and an outgoing bubble
            DrawPill(g, "Today", small, r.X + r.Width / 2, r.Y + Dpi.Px(8, dpi), scrim ? pal.ChatPillScrim : pal.ServicePillBg, scrim ? pal.ChatPillText : pal.ServicePillText, dpi);
            DrawBubble(g, "Good morning!", text, r.X + Dpi.Px(12, dpi), r.Y + Dpi.Px(38, dpi), pal.BubbleIn, pal.BubbleInText, false, r, dpi);
            DrawBubble(g, "Hi! This is how it looks.", text, 0, r.Y + Dpi.Px(76, dpi), pal.BubbleOut, pal.BubbleOutText, true, r, dpi);
        }

        private static void DrawPill(Graphics g, string s, Font f, int cx, int y, Color bg, Color fg, int dpi)
        {
            var sz = TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var pill = new Rectangle(cx - sz.Width / 2 - Dpi.Px(8, dpi), y, sz.Width + Dpi.Px(16, dpi), sz.Height + Dpi.Px(4, dpi));
            using (var path = DrawHelper.RoundedRect(pill, pill.Height / 2)) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            TextRenderer.DrawText(g, s, f, pill, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private static void DrawBubble(Graphics g, string s, Font f, int x, int y, Color bg, Color fg, bool right, Rectangle area, int dpi)
        {
            var sz = TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int w = sz.Width + Dpi.Px(20, dpi), h = sz.Height + Dpi.Px(12, dpi);
            if (right) x = area.Right - Dpi.Px(12, dpi) - w;
            var rb = new Rectangle(x, y, w, h);
            using (var path = DrawHelper.RoundedRect(rb, Dpi.Px(12, dpi))) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            TextRenderer.DrawText(g, s, f, rb, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}
