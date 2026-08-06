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

        /// <summary>Sets the app's window icon (the exe's own icon) on a form — so every window (taskbar /
        /// Alt-Tab / title bar) bears the app icon instead of the WinForms default. RT-safe: a no-op if the
        /// icon can't be extracted (the form still opens). Cached once. Call from any form's setup.</summary>
        public static void SetAppIcon(Form form)
        {
            if (form == null) return;
            if (!_appIconTried)
            {
                _appIconTried = true;
                try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            }
            if (_appIcon != null) try { form.Icon = _appIcon; } catch { }
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

            var close = new Label { Text = "✕", Left = w - 42, Top = 0, Width = 38, Height = BarH, ForeColor = Color.White, Font = FontHelper.Ui(12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            close.Click += (s, e) => { if (form.DialogResult == DialogResult.None) form.DialogResult = DialogResult.Cancel; form.Close(); };
            header.Controls.Add(close);

            if (onOverflow != null)
            {
                // Same white-on-accent treatment as ✕, immediately to its left. Deliberately NOT accent-
                // themed or restyled: this is title-bar chrome and must read as part of the bar.
                var more = new Label { Text = "⋮", Left = w - 80, Top = 0, Width = 34, Height = BarH, ForeColor = Color.White, Font = FontHelper.Ui(13f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
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
