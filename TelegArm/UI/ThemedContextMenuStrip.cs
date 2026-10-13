using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    public class ThemedContextMenuStrip : ContextMenuStrip
    {
        /// <summary>Redesign step 1.6 — the 2.x menu: colours from <see cref="ThemeHelper.Palette"/> and the row padding /
        /// icon slot in logical pixels at the DPI of the control the menu opens over. ON since step 2.0 (built in 1.6, off
        /// until 1.7's zero-diff check had run). Read when a menu opens and when it paints.</summary>
        public static bool Restyled { get; set; } = true;

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
            Opening += (s, e) => Prepare(SourceControl);
        }

        /// <summary>What every opening does: colours, the icon slot and touch-sized rows at the DPI of the control the menu
        /// opens over. Internal so the --shoot rig can render a menu without opening one on the user's screen.</summary>
        internal void Prepare(Control over)
        {
            int dpi = Restyled ? Dpi.Of(over ?? (Control)this) : Dpi.Logical;
            if (Restyled) { BackColor = ThemedMenuColors.Background; ForeColor = ThemedMenuColors.Text; }
            ImageScalingSize = new Size(Dpi.Px(MenuIcons.IconSize, dpi), Dpi.Px(MenuIcons.IconSize, dpi));
            MenuIcons.Decorate(this, dpi);
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
        private static readonly Padding RowPad = new Padding(4, 8, 12, 8);
        private static readonly Dictionary<string, Image> _icons = new Dictionary<string, Image>();

        /// <summary>Touch-size + icon every item in a menu (recursively into sub-menus). Idempotent per open.
        /// <paramref name="dpi"/> is Dpi.Logical (= today's pixels) unless the menu is Restyled.</summary>
        public static void Decorate(ToolStrip strip, int dpi = Dpi.Logical)
        {
            if (strip == null) return;
            foreach (ToolStripItem it in strip.Items) DecorateItem(it, dpi);
        }

        private static void DecorateItem(ToolStripItem it, int dpi)
        {
            var mi = it as ToolStripMenuItem;
            if (mi == null) return;                            // separators etc. — leave alone

            mi.Padding = new Padding(Dpi.Px(RowPad.Left, dpi), Dpi.Px(RowPad.Top, dpi), Dpi.Px(RowPad.Right, dpi), Dpi.Px(RowPad.Bottom, dpi));   // touch-size the row

            if (mi.Image == null && !string.IsNullOrEmpty(mi.Text))
            {
                string clean = StripEmojiPrefix(mi.Text);
                if (clean != mi.Text) mi.Text = clean;         // ALWAYS drop a decorative text prefix → consistent LEFT alignment for every item
                string emoji = EmojiFor(clean);
                if (emoji != null)
                {
                    var icon = RenderIcon(emoji, Dpi.Px(IconSize, dpi));
                    if (icon != null) mi.Image = icon;          // mapped action → its emoji in the icon slot
                }
                else if (IsPureEmoji(clean))                    // a lone emoji (a reaction) → render via the SAME Noto engine, icon-only
                {
                    var icon = RenderIcon(clean, Dpi.Px(IconSize, dpi));
                    if (icon != null) { mi.AccessibleName = clean; mi.Image = icon; mi.Text = ""; }
                }
            }

            if (mi.HasDropDownItems)                           // sub-menus get the same treatment
            {
                mi.DropDown.ImageScalingSize = new Size(Dpi.Px(IconSize, dpi), Dpi.Px(IconSize, dpi));
                foreach (ToolStripItem sub in mi.DropDownItems) DecorateItem(sub, dpi);
            }
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
            string key = size == IconSize ? emoji : emoji + "@" + size;   // one bitmap per emoji per slot size (Restyled menus scale)
            if (_icons.TryGetValue(key, out var cached)) return cached;
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
            _icons[key] = icon;   // cache misses (null) too — don't retry
            return icon;
        }
    }

    /// <summary>Menu palette derived from ThemeHelper (no hard-coded theme colors).</summary>
    internal static class ThemedMenuColors
    {
        // Restyled (redesign 1.6, on in 2.0): the cached Palette — no registry read per menu paint. Otherwise 1.x's values.
        private static bool R => ThemedContextMenuStrip.Restyled;
        public static bool Dark => R ? ThemeHelper.Palette.Dark : ThemeHelper.IsDark;
        public static Color Background => R ? ThemeHelper.Palette.Card : Dark ? Color.FromArgb(43, 43, 46) : Color.FromArgb(245, 245, 247);
        public static Color Text => R ? ThemeHelper.Palette.Text : Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(25, 25, 25);
        public static Color Disabled => R ? ThemeHelper.Palette.TextDisabled : Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(165, 165, 165);
        public static Color Accent => R ? ThemeHelper.Palette.Accent : ThemeHelper.GetWindowsAccentColor();
        public static Color Highlight => R ? ThemeHelper.Palette.AccentSoft : Blend(Accent, Background, Dark ? 0.42f : 0.28f);
        public static Color Border => R ? ThemeHelper.Palette.Divider : Blend(Background, Dark ? Color.White : Color.Black, 0.16f);

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
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
                float sz = e.TextFont != null ? e.TextFont.SizeInPoints : 10.5f;
                using (var f = FontHelper.For(text, sz))
                    EmojiRenderer.DrawLine(e.Graphics, text, f, color, e.TextRectangle);
                return;
            }
            e.TextColor = color;
            base.OnRenderItemText(e);
        }
    }
}
