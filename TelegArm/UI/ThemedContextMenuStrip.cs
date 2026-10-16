using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// A ContextMenuStrip themed entirely from <see cref="ThemeHelper"/> — background/text
    /// follow dark/light, the hover/selected highlight is the Windows accent, separators and
    /// borders are derived shades. Every context menu in the app should use this type so they
    /// all match. A single shared renderer is installed on <see cref="ToolStripManager"/> so
    /// sub-menus (which render in ManagerRenderMode) are themed too. Re-themes live on
    /// <see cref="ThemeHelper.ThemeChanged"/>; unsubscribes on dispose.
    /// </summary>
    public class ThemedContextMenuStrip : ContextMenuStrip, IDpiHost
    {
        /// <summary>Redesign step 1.6 — the 2.x menu: colours from <see cref="ThemeHelper.Palette"/> and the row padding /
        /// icon slot in logical pixels at the DPI of the control the menu opens over. ON since step 2.0 (built in 1.6, off
        /// until 1.7's zero-diff check had run). Read when a menu opens and when it paints.</summary>
        public static bool Restyled { get; set; } = true;

        /// <summary>Step 3.1 — the menu's text size in points; 0 = the system menu font (<see cref="FontHelper.MenuAt"/>).
        /// Applied at the open point's DPI on every opening — set this, never <c>Font</c> (that would be one DPI's).</summary>
        public float FontPt { get; set; }

        /// <summary>Step 3.1 — the EFFECTIVE DPI the menu was last prepared at (0 = never, or the 1.x look). Sub-menus and
        /// the renderer read it from the root menu.</summary>
        internal int MenuDpi { get; private set; }

        private Point? _openAt;   // the screen point ShowAt opens at; consumed by the opening, cleared on Closed

        static ThemedContextMenuStrip()
        {
            // One renderer for ALL menus + their sub-menus (ManagerRenderMode is the default).
            ToolStripManager.Renderer = new ThemedMenuRenderer();
        }

        public ThemedContextMenuStrip()
        {
            RenderMode = ToolStripRenderMode.ManagerRenderMode;
            BackColor = ThemedMenuColors.Background;
            ForeColor = ThemedMenuColors.Text;
            ImageScalingSize = new Size(MenuIcons.IconSize, MenuIcons.IconSize);   // bigger icon slot (emoji icons)
            ThemeHelper.ThemeChanged += OnThemeChanged;
            // Just before the menu shows: touch-size the rows + give each known action an emoji icon (moving any
            // text-emoji prefix into the icon slot). Centralized here → EVERY themed menu in the app is enhanced.
            // Step 3.1: at the DPI of WHERE it opens (a popup's own monitor), not of the window that owns it.
            Opening += (s, e) => { Point at = OpenPoint(); _openAt = null; PrepareFor(at); };
            Closed += (s, e) => _openAt = null;
        }

        /// <summary>Step 3.1 — <see cref="Dpi.Of"/> on the menu (or a control hosted in it) answers the menu's DPI.</summary>
        int IDpiHost.Dpi { get { return MenuDpi > 0 ? MenuDpi : Dpi.DefaultDpi; } }

        /// <summary>Step 3.1 — Show at a screen point, remembering it so the opening sizes the menu for that monitor.</summary>
        public void ShowAt(Point screen)
        {
            _openAt = screen;
            Show(screen);
        }

        /// <summary>Step 3.1 — Show(<paramref name="c"/>, <paramref name="clientPt"/>), remembering the screen point.</summary>
        public void ShowAt(Control c, Point clientPt)
        {
            _openAt = null;
            try { if (c != null && !c.IsDisposed && c.IsHandleCreated) _openAt = c.PointToScreen(clientPt); } catch { }
            Show(c, clientPt);
        }

        /// <summary>Where the menu opens: the ShowAt point; else (a ContextMenuStrip property, the tray, a plain Show) the
        /// cursor when it is over the source control, else the source control's centre (keyboard), else the cursor.</summary>
        private Point OpenPoint()
        {
            if (_openAt.HasValue) return _openAt.Value;
            Point cur = Cursor.Position;
            try
            {
                var src = SourceControl;
                if (src != null && !src.IsDisposed && src.IsHandleCreated)
                {
                    Rectangle r = src.RectangleToScreen(src.ClientRectangle);
                    if (r.Contains(cur)) return cur;
                    return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                }
            }
            catch { }
            return cur;
        }

        /// <summary>What every opening does: colours, the font, the icon slot and touch-sized rows at <paramref name="dpi"/>
        /// (step 3.1: the open point's). Idempotent. Internal so a caller can measure the menu before showing it.</summary>
        internal void Prepare(int dpi)
        {
            if (dpi <= 0) dpi = Dpi.DefaultDpi;
            if (Restyled)
            {
                MenuDpi = dpi;
                BackColor = ThemedMenuColors.Background; ForeColor = ThemedMenuColors.Text;
                Font = FontPt > 0 ? FontHelper.UiAt(FontPt, dpi) : FontHelper.MenuAt(dpi);   // shared: never disposed
            }
            else
            {
                // The 1.x look (the gallery's comparison): pixels as written, the system's point menu font.
                MenuDpi = 0;
                if (FontPt > 0) Font = FontHelper.UiAt(FontPt, dpi);
                dpi = Dpi.Logical;
            }
            ImageScalingSize = new Size(Dpi.Px(MenuIcons.IconSize, dpi), Dpi.Px(MenuIcons.IconSize, dpi));
            MenuIcons.Decorate(this, dpi);
        }

        /// <summary>Prepares at the DPI of the open point, then — WinForms constrains a menu into the working area of the
        /// monitor that holds MOST of it (Screen.FromRectangle), which near a monitor edge is the neighbour — once more at
        /// the DPI of the monitor it will actually land on (review of step 3.1). The landed rectangle lies inside one
        /// monitor, so a second pass settles it.</summary>
        private void PrepareFor(Point at)
        {
            int dpi = Dpi.ForPopup(at);
            Prepare(dpi);
            try
            {
                var size = GetPreferredSize(Size.Empty);
                var r = new Rectangle(at, size);
                var wa = Screen.FromRectangle(r).WorkingArea;
                int x = Math.Max(wa.Left, Math.Min(r.X, wa.Right - r.Width));
                int y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - r.Height));
                int landed = Dpi.ForPopup(new Point(x + r.Width / 2, y + r.Height / 2));
                if (landed != dpi) Prepare(landed);
            }
            catch { }
        }

        /// <summary>Prepare for the monitor at <paramref name="screen"/> — before reading PreferredSize to place the menu.</summary>
        internal void PrepareAt(Point screen) { Prepare(Dpi.ForPopup(screen)); }

        /// <summary>The --shoot rig's entry (no screen point): the DPI of <paramref name="over"/>'s window, else the menu's.</summary>
        internal void Prepare(Control over) { Prepare(Dpi.Of(over ?? (Control)this)); }

        /// <summary>Step 3.1 — WM_DPICHANGED (per-monitor only) arrives when the window is placed on the monitor it was
        /// already prepared for. Swallowed: Result 0, no base, never the suggested rectangle.</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WindowDpi.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try
            {
                // DroidFinestra's fix (redesign 1.6): a ContextMenuStrip has a handle ONLY while it is open. Closed — the
                // usual case — BeginInvoke threw "cannot call BeginInvoke until the window handle has been created",
                // the catch swallowed it, and BackColor/ForeColor stayed at the theme the menu was BUILT in, for good
                // (the composer's format menu is built once). No handle means nothing to marshal: set them directly.
                if (IsHandleCreated) BeginInvoke((Action)ApplyThemeColors);
                else ApplyThemeColors();
            }
            catch { }
        }

        private void ApplyThemeColors()
        {
            if (IsDisposed) return;
            BackColor = ThemedMenuColors.Background;
            ForeColor = ThemedMenuColors.Text;
            try { Invalidate(true); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Shared enhancement for every <see cref="ThemedContextMenuStrip"/> (applied on Opening): touch-sizes the
    /// rows (finger-tappable on RT) and gives each item that maps to a KNOWN ACTION an emoji icon rendered by
    /// the app's <see cref="EmojiRenderer"/> (Noto → crisp bitmaps that render on RT, unlike text-glyph emoji).
    /// Any leading text-emoji prefix (e.g. "📌 Unpin") is moved into the icon slot. One shared ACTION→EMOJI
    /// mapping — tune it in <see cref="EmojiFor"/>. Icons are cached (rendered once per emoji).
    /// </summary>
    internal static class MenuIcons
    {
        public const int IconSize = 22;                       // icon slot, LOGICAL px (also the rendered emoji size)
        /// <summary>THE row padding token (taller + wider rows for touch), logical px. DroidFinestra's copy had its own
        /// (4,7,14,7); one token now (redesign 1.6). Scaled to the menu's DPI only when the menu is Restyled.</summary>
        private static readonly Padding RowPad = new Padding(4, 8, 12, 8);   // logical px (Px'd in Prepare)
        private static readonly Dictionary<string, Image> _icons = new Dictionary<string, Image>();   // "emoji@size" → icon
        private static readonly object _iconsLock = new object();
        // Step 3.1: the emoji each decorated item shows, so a persistent menu reopened at another DPI re-renders its icon.
        private static readonly ConditionalWeakTable<ToolStripMenuItem, string> _itemEmoji = new ConditionalWeakTable<ToolStripMenuItem, string>();

        /// <summary>Touch-size + icon every item in a menu (recursively into sub-menus). Idempotent per open.
        /// <paramref name="dpi"/> is Dpi.Logical (= today's pixels) unless the menu is Restyled.</summary>
        public static void Decorate(ToolStrip strip, int dpi = Dpi.Logical)
        {
            if (strip == null) return;
            foreach (ToolStripItem it in strip.Items) DecorateItem(it, dpi);
        }

        /// <summary>Step 3.5 — drops the cached icons of every size NOT in <paramref name="keepSizes"/> (pixel sizes:
        /// Dpi.Px(IconSize, dpi)). Never disposes them: menu items may still show them, and the GC frees the rest.</summary>
        public static void Trim(ICollection<int> keepSizes)
        {
            if (keepSizes == null) return;
            lock (_iconsLock)
            {
                var drop = new List<string>();
                foreach (var key in _icons.Keys)
                {
                    int at = key.LastIndexOf('@'), size;
                    if (at < 0 || !int.TryParse(key.Substring(at + 1), out size) || !keepSizes.Contains(size)) drop.Add(key);
                }
                foreach (var key in drop) _icons.Remove(key);
            }
        }

        /// <summary>How many icons are cached (all sizes), for the DPI diagnostics.</summary>
        public static int Count { get { lock (_iconsLock) return _icons.Count; } }

        private static void DecorateItem(ToolStripItem it, int dpi)
        {
            var mi = it as ToolStripMenuItem;
            if (mi == null) return;                            // separators etc. — leave alone

            mi.Padding = new Padding(Dpi.Px(RowPad.Left, dpi), Dpi.Px(RowPad.Top, dpi), Dpi.Px(RowPad.Right, dpi), Dpi.Px(RowPad.Bottom, dpi));   // touch-size the row

            int size = Dpi.Px(IconSize, dpi);
            string shown;
            if (mi.Image != null && _itemEmoji.TryGetValue(mi, out shown))
            {
                // Our icon from an earlier opening: re-render it when this opening is at another size (step 3.1).
                if (mi.Image.Width != size || mi.Image.Height != size)
                {
                    var icon = RenderIcon(shown, size);
                    if (icon != null) mi.Image = icon;
                }
            }
            else if (mi.Image == null && !string.IsNullOrEmpty(mi.Text))
            {
                string clean = StripEmojiPrefix(mi.Text);
                if (clean != mi.Text) mi.Text = clean;         // ALWAYS drop a decorative text prefix → consistent LEFT alignment for every item
                string emoji = EmojiFor(clean);
                if (emoji != null)
                {
                    var icon = RenderIcon(emoji, size);
                    if (icon != null) { mi.Image = icon; Remember(mi, emoji); }   // mapped action → its emoji in the icon slot
                }
                else if (IsPureEmoji(clean))                    // a lone emoji (a reaction) → render via the SAME Noto engine, icon-only
                {
                    var icon = RenderIcon(clean, size);
                    if (icon != null) { mi.AccessibleName = clean; mi.Image = icon; mi.Text = ""; Remember(mi, clean); }
                }
            }

            if (mi.HasDropDownItems)                           // sub-menus get the same treatment
            {
                mi.DropDown.ImageScalingSize = new Size(Dpi.Px(IconSize, dpi), Dpi.Px(IconSize, dpi));
                foreach (ToolStripItem sub in mi.DropDownItems) DecorateItem(sub, dpi);
            }
        }

        private static void Remember(ToolStripMenuItem mi, string emoji)
        {
            _itemEmoji.Remove(mi);   // .NET 4.7 has no AddOrUpdate
            _itemEmoji.Add(mi, emoji);
        }

        /// <summary>Drops a leading non-ASCII/symbol prefix (+ its spaces) so "📌   Unpin" → "Unpin".
        /// Text with no ASCII letter (a lone emoji, e.g. a reaction) is left untouched.</summary>
        private static string StripEmojiPrefix(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] < 128 && char.IsLetterOrDigit(text[i]))
                    return i == 0 ? text : text.Substring(i);
            return text;
        }

        /// <summary>True when the text is a lone emoji (no ASCII letters/digits) the Noto engine can render —
        /// e.g. a reaction glyph. Such items get a Noto icon (icon-only) instead of a system-font text emoji.</summary>
        private static bool IsPureEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < 128 && char.IsLetterOrDigit(c)) return false;
            return EmojiRenderer.ContainsEmoji(s);
        }

        /// <summary>ACTION → EMOJI mapping (keyword match on the item text). Return null → no icon. TUNE HERE.</summary>
        private static string EmojiFor(string text)
        {
            string t = text.ToLowerInvariant();
            if (t.Contains("reply"))                    return "↩️";
            if (t.Contains("forward"))                  return "➡️";
            if (t.Contains("copy link") || t.Contains("copy url")) return "🔗";
            if (t.Contains("copy"))                     return "📋";
            if (t.Contains("delete"))                   return "✖";
            if (t.Contains("unpin") || t.Contains("pin")) return "📌";
            if (t.Contains("edit"))                     return "✏️";
            if (t.Contains("select"))                   return "✅";
            if (t.Contains("save") || t.Contains("download")) return "💾";
            if (t.Contains("report"))                   return "⚠️";
            if (t.Contains("revoke") || t.Contains("ban") || t.Contains("block")) return "🚫";
            if (t.Contains("unmute"))                   return "🔔";
            if (t.Contains("mute"))                     return "🔕";
            if (t.Contains("unarchive") || t.Contains("archive")) return "🗄️";
            if (t.Contains("mark as read") || t.Contains("read"))   return "👁️";
            if (t.Contains("unread"))                   return "🔵";
            if (t.Contains("clear"))                    return "🧹";
            if (t.Contains("reveal") || t.Contains("folder")) return "📁";
            if (t.Contains("photo") || t.Contains("image") || t.Contains("file")) return "▣";
            if (t.Contains("round video") || t.Contains("video")) return "🎥";
            if (t.Contains("poll"))                     return "📊";
            if (t.Contains("react"))                    return "😊";
            if (t.Contains("open"))                     return "📂";
            if (t.Contains("exit") || t.Contains("quit")) return "🚪";
            if (t.Contains("profile") || t.Contains("member")) return "👤";
            if (t.Contains("add"))                      return "➕";
            if (t.Contains("remove") || t.Contains("kick")) return "➖";
            if (t.Contains("search") || t.Contains("find")) return "🔍";
            if (t.Contains("settings") || t.Contains("manage")) return "⚙️";
            if (t.Contains("send"))                     return "📤";
            if (t.Contains("info") || t.Contains("detail")) return "ℹ️";
            return null;
        }

        /// <summary>Renders an emoji to an IconSize bitmap via the shared engine (cached; null if unavailable).</summary>
        private static Image RenderIcon(string emoji, int size)
        {
            string key = emoji + "@" + size;   // one bitmap per emoji per slot size (Restyled menus scale); Trim reads the size back
            lock (_iconsLock) { if (_icons.TryGetValue(key, out var cached)) return cached; }
            Image icon = null;
            try
            {
                var src = EmojiRenderer.Get(emoji);   // Noto bitmap (engine cache owns it — don't dispose)
                if (src != null)
                {
                    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, size, size));
                    }
                    icon = bmp;
                }
            }
            catch { icon = null; }
            lock (_iconsLock) _icons[key] = icon;   // cache misses (null) too — don't retry
            return icon;
        }
    }

    /// <summary>Menu palette derived from ThemeHelper (no hard-coded theme colors).</summary>
    internal static class ThemedMenuColors
    {
        // Restyled (redesign 1.6, on in 2.0): the cached Palette — no registry read per menu paint. Otherwise 1.x's values.
        private static bool R => ThemedContextMenuStrip.Restyled;
        public static bool Dark => R ? ThemeHelper.Palette.Dark : ThemeHelper.IsDark;
        public static Color Background => R ? ThemeHelper.Palette.Card : ThemeHelper.PaletteFor(Dark).MenuLegacyBg;
        public static Color Text => R ? ThemeHelper.Palette.Text : ThemeHelper.PaletteFor(Dark).MenuLegacyText;
        public static Color Disabled => R ? ThemeHelper.Palette.TextDisabled : ThemeHelper.PaletteFor(Dark).MenuLegacyDisabled;
        public static Color Accent => R ? ThemeHelper.Palette.Accent : ThemeHelper.GetWindowsAccentColor();
        public static Color Highlight => R ? ThemeHelper.Palette.AccentSoft : Blend(Accent, Background, Dark ? 0.42f : 0.28f);
        public static Color Border => R ? ThemeHelper.Palette.Divider : Blend(Background, Dark ? Color.White : Color.Black, 0.16f);

        public static Color Blend(Color a, Color b, float t)
        {
            return ColorMath.Rgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }
    }

    /// <summary>ProfessionalColorTable whose colors all come from <see cref="ThemedMenuColors"/>.</summary>
    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        public ThemedColorTable() { UseSystemColors = false; }

        public override Color ToolStripDropDownBackground => ThemedMenuColors.Background;
        public override Color ImageMarginGradientBegin => ThemedMenuColors.Background;
        public override Color ImageMarginGradientMiddle => ThemedMenuColors.Background;
        public override Color ImageMarginGradientEnd => ThemedMenuColors.Background;
        public override Color MenuBorder => ThemedMenuColors.Border;
        public override Color MenuItemBorder => ThemedMenuColors.Accent;
        public override Color MenuItemSelected => ThemedMenuColors.Highlight;
        public override Color MenuItemSelectedGradientBegin => ThemedMenuColors.Highlight;
        public override Color MenuItemSelectedGradientEnd => ThemedMenuColors.Highlight;
        public override Color MenuItemPressedGradientBegin => ThemedMenuColors.Highlight;
        public override Color MenuItemPressedGradientEnd => ThemedMenuColors.Highlight;
        public override Color SeparatorDark => ThemedMenuColors.Border;
        public override Color SeparatorLight => ThemedMenuColors.Border;
        public override Color CheckBackground => ThemedMenuColors.Highlight;
        public override Color CheckSelectedBackground => ThemedMenuColors.Highlight;
        public override Color CheckPressedBackground => ThemedMenuColors.Highlight;
    }

    /// <summary>Renderer that paints item text in the themed color (and dims disabled items).</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color color = e.Item.Enabled ? ThemedMenuColors.Text : ThemedMenuColors.Disabled;
            string text = e.Text ?? "";
            // Persian and/or emoji items (e.g. a bot's command list) — owner-draw via the Noto engine: inline
            // COLOR emoji + Vazirmatn for Persian. The base GDI text draw showed a system-font Persian + glyph emoji.
            if (text.Length > 0 && (EmojiRenderer.ContainsEmoji(text) || FontHelper.IsPersian(text)))
            {
                // Step 3.1: shared fonts at the MENU's DPI (never disposed). A pixel font (the menu's own) is read back to
                // points at that DPI — SizeInPoints would use the system DPI. Same sizes as before at 96.
                int dpi = MenuDpiOf(e.ToolStrip ?? (e.Item != null ? e.Item.Owner : null));
                float pt = e.TextFont != null ? PointsOf(e.TextFont, dpi) : 10.5f;
                EmojiRenderer.DrawLine(e.Graphics, text, FontHelper.ForAt(text, pt, dpi), color, e.TextRectangle);
                return;
            }
            e.TextColor = color;
            base.OnRenderItemText(e);
        }

        /// <summary>A font's size in points, its pixels read at <paramref name="dpi"/> (no SizeInPoints: system DPI).</summary>
        private static float PointsOf(Font f, int dpi)
        {
            switch (f.Unit)
            {
                case GraphicsUnit.Point: return f.Size;
                case GraphicsUnit.Inch: return f.Size * 72f;
                case GraphicsUnit.Document: return f.Size * 72f / 300f;
                case GraphicsUnit.Millimeter: return f.Size * 72f / 25.4f;
                default: return f.Size * 72f / (dpi > 0 ? dpi : Dpi.Logical);   // Pixel, World, Display: device pixels
            }
        }

        /// <summary>The DPI a strip was prepared at: its root <see cref="ThemedContextMenuStrip"/>'s (a sub-menu walks up
        /// through OwnerItem.Owner), else its window's.</summary>
        private static int MenuDpiOf(ToolStrip strip)
        {
            ToolStrip s = strip;
            for (int depth = 0; s != null && depth < 16; depth++)
            {
                var root = s as ThemedContextMenuStrip;
                if (root != null) return root.MenuDpi > 0 ? root.MenuDpi : Dpi.DefaultDpi;
                var dd = s as ToolStripDropDown;
                if (dd == null || dd.OwnerItem == null) break;
                s = dd.OwnerItem.Owner;
            }
            return Dpi.Of(strip);
        }
    }
}
