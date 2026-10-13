using System;
using System.Drawing;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Gives a plain Form the app's themed chrome — borderless + the accent (purple) title bar (title + ✕ +
    /// drag), exactly like the login form / themed dialogs — and returns a dark CONTENT panel below the bar
    /// that the caller adds its controls to. Reuses the existing accent-header pattern (no new theming).
    /// Bump the form's ClientSize.Height by <see cref="BarH"/> before calling so the content area is preserved.
    /// </summary>
    public static class ThemedChrome
    {
        public const int BarH = 44;

        private static Icon _appIcon;
        private static bool _appIconTried;

        /// <summary>Redesign step 1.8 (DroidFinestra's pattern) — the app icon at ALL its sizes: img-icon/icon.ico embedded
        /// as the resource "app.ico" (16/20/24/28/32/40/48/64/128/256). Form.Icon takes the small title-bar icon from it at
        /// SystemInformation.SmallIconSize and the big (taskbar / Alt-Tab) one at SM_CXICON, so both are a real frame at
        /// 100/125/150% instead of 1.x's ExtractAssociatedIcon, which hands back ONE size for Windows to stretch.
        /// Falls back to the exe's associated icon, then null. Shared by every window: never dispose it.</summary>
        public static Icon AppIcon
        {
            get
            {
                if (_appIconTried) return _appIcon;
                _appIconTried = true;
                try
                {
                    using (var s = typeof(ThemedChrome).Assembly.GetManifestResourceStream("app.ico"))
                        if (s != null) _appIcon = new Icon(s);
                }
                catch { }
                if (_appIcon == null)
                    try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                return _appIcon;
            }
        }

        /// <summary>Sets the app icon on a form — so every window (taskbar / Alt-Tab / title bar) bears it instead of the
        /// WinForms default. RT-safe: a no-op if there is no icon (the form still opens). Call from any form's setup.</summary>
        public static void SetAppIcon(Form form)
        {
            if (form == null) return;
            var ico = AppIcon;
            if (ico != null) try { form.Icon = ico; } catch { }
        }

        /// <param name="onOverflow">BATCH-TA-18a — OPTIONAL. When supplied, a "⋮" overflow button is placed
        /// in the title bar to the LEFT of ✕, and this is called with the SCREEN point to drop a menu at.
        /// Defaults to null, so every existing caller is untouched — the button simply doesn't exist for
        /// them. Added here rather than sniffing for the header panel from outside: Apply returns only the
        /// CONTENT panel, so a caller wanting a title-bar button would otherwise have to find the header by
        /// geometry and depend on this method's internal control order.</param>
        public static Panel Apply(Form form, string title, Color accent, bool dark, Action<Point> onOverflow = null)
        {
            SetAppIcon(form);   // every themed form bears the app icon (Alt-Tab / taskbar)
            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            form.FormBorderStyle = FormBorderStyle.None;
            form.BackColor = bg;
            int w = form.ClientSize.Width, h = form.ClientSize.Height;

            var header = new Panel { Left = 0, Top = 0, Width = w, Height = BarH, BackColor = accent };
            bool dragging = false; Point start = Point.Empty;
            header.MouseDown += (s, e) => { dragging = true; start = e.Location; };
            header.MouseMove += (s, e) => { if (dragging) form.Location = new Point(form.Location.X + e.X - start.X, form.Location.Y + e.Y - start.Y); };
            header.MouseUp += (s, e) => dragging = false;
            header.Paint += (s, e) => TextRenderer.DrawText(e.Graphics, title ?? "", FontHelper.Ui(12.5f, FontStyle.Bold),
                new Rectangle(16, 0, w - 60, BarH), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Redesign step 1.3: drawn glyphs (GlyphButton), no longer the "✕" / "⋮" font characters — symbol glyphs
            // are font-dependent and have come out as tofu boxes on RT 8.1. Same place, same size, same behaviour.
            var close = new TelegArm.UI.Controls.GlyphButton(TelegArm.UI.Controls.GlyphButton.Glyph.Close) { Left = w - 42, Top = 0, Width = 38, Height = BarH, BarColor = accent, GlyphColor = Color.White, DangerHover = true };
            close.Click += (s, e) => { if (form.DialogResult == DialogResult.None) form.DialogResult = DialogResult.Cancel; form.Close(); };
            header.Controls.Add(close);

            if (onOverflow != null)
            {
                // Same white-on-accent treatment as ✕, immediately to its left. Deliberately NOT accent-
                // themed or restyled: this is title-bar chrome and must read as part of the bar.
                var more = new TelegArm.UI.Controls.GlyphButton(TelegArm.UI.Controls.GlyphButton.Glyph.More) { Left = w - 80, Top = 0, Width = 34, Height = BarH, BarColor = accent, GlyphColor = Color.White };
                more.Click += (s, e) =>
                {
                    // Screen point at the button's bottom-left, so the menu hangs below it like every other
                    // menu in the app rather than under the cursor.
                    var pt = more.PointToScreen(new Point(0, more.Height));
                    onOverflow(pt);
                };
                header.Controls.Add(more);
            }

            var content = new Panel { Left = 0, Top = BarH, Width = w, Height = h - BarH, BackColor = bg };
            form.Controls.Add(header);
            form.Controls.Add(content);
            return content;
        }
    }
}
