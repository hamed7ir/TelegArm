using System;
using System.Drawing;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed replacement for <see cref="MessageBox"/> — a MaterialForm styled from
    /// <see cref="ThemeHelper"/> (dark/light + Windows accent) so confirmations/alerts
    /// match the rest of the app instead of looking like a native Windows dialog.
    /// <see cref="Show"/> returns the index of the clicked button, or -1 if dismissed.
    /// </summary>
    public static class ThemedDialog
    {
        /// <summary>
        /// Shows a modal themed dialog with <paramref name="buttons"/> (button 0 is the
        /// accented/primary action). Returns the clicked index, or -1 on Esc/close.
        /// </summary>
        public static int Show(IWin32Window owner, string title, string message, params string[] buttons)
        {
            if (buttons == null || buttons.Length == 0) buttons = new[] { "OK" };
            using (var dlg = new Dialog(title, message, buttons))
            {
                dlg.ShowDialog(owner);
                return dlg.Result;
            }
        }

        private sealed class Dialog : MaterialForm
        {
            public int Result = -1;

            private readonly string _title;
            private readonly Color _accent;
            private const int BarH = 44;   // accent title-bar height

            public Dialog(string title, string message, string[] buttons)
            {
                _title = title ?? "";
                _accent = ThemeHelper.GetWindowsAccentColor();

                var skin = MaterialSkinManager.Instance;
                skin.AddFormToManage(this);
                skin.Theme = ThemeHelper.IsDark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
                var accent = (Primary)(uint)_accent.ToArgb();
                var msAccent = (Accent)(uint)_accent.ToArgb();   // accent slot = Windows accent (shared singleton — no blue re-poison)
                skin.ColorScheme = new ColorScheme(accent, accent, accent, msAccent, TextShade.WHITE);

                FormStyle = FormStyles.ActionBar_None;
                Text = title;
                TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
                AutoScaleMode = AutoScaleMode.Font;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                Sizable = false;
                KeyPreview = true;

                const int W = 440;
                int msgLeft = 22, msgTop = BarH + 18, msgW = W - 44;

                // Title is owner-painted onto the accent bar (OnPaint); message sits below it.
                // COPYABLE message: a read-only, borderless TextBox styled like the old MaterialLabel — so the text
                // can be SELECTED + copied (drag-select, right-click Copy, or Ctrl+C for the whole thing). Measured +
                // sized exactly as before (long bodies still size to fit — the logout/probe-result case).
                Font msgFont;
                using (var fp = new MaterialLabel()) msgFont = fp.Font ?? new Font("Segoe UI", 10f);
                Size measured = TextRenderer.MeasureText(message ?? "", msgFont,
                    new Size(msgW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                int msgH = Math.Max(44, measured.Height + 8);
                var txtMsg = new TextBox
                {
                    Text = message ?? "",
                    Location = new Point(msgLeft, msgTop),
                    Size = new Size(msgW, msgH),
                    Multiline = true, ReadOnly = true, WordWrap = true,
                    BorderStyle = BorderStyle.None, TabStop = false,
                    Font = msgFont,
                    BackColor = skin.BackgroundColor,
                    ForeColor = ThemeHelper.IsDark ? Color.FromArgb(230, 230, 234) : Color.FromArgb(40, 40, 44),
                    Cursor = Cursors.IBeam
                };
                Controls.Add(txtMsg);

                int btnTop = msgTop + msgH + 20;
                ClientSize = new Size(W, btnTop + 36 + 18);

                // Right-aligned buttons; index 0 is the primary (Contained) action.
                int x = W - 16;
                for (int i = buttons.Length - 1; i >= 0; i--)
                {
                    int idx = i;
                    int w = Math.Max(92, buttons[i].Length * 9 + 36);
                    x -= w;
                    var btn = new MaterialButton
                    {
                        Text = buttons[i],
                        Type = i == 0 ? MaterialButton.MaterialButtonType.Contained
                                      : MaterialButton.MaterialButtonType.Outlined,
                        AutoSize = false,
                        Width = w,
                        Height = 36,
                        Location = new Point(x, btnTop)
                    };
                    btn.Click += (s, e) => { Result = idx; Close(); };
                    Controls.Add(btn);
                    x -= 8;
                }

                KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Escape) { Result = -1; Close(); }
                    else if (e.Control && e.KeyCode == Keys.C)   // Ctrl+C copies the WHOLE message (not just a selection)
                    { try { Clipboard.SetText(message ?? ""); } catch { } e.Handled = e.SuppressKeyPress = true; }
                };
                Shown += (s, e) => Invalidate();   // ensure the accent bar paints
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                // Accent title bar (matches the app's other themed headers), title in white, dark+light alike.
                using (var b = new SolidBrush(_accent))
                    e.Graphics.FillRectangle(b, 0, 0, ClientSize.Width, BarH);
                using (var f = new Font("Segoe UI", 11.5f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, _title, f, new Rectangle(20, 0, ClientSize.Width - 40, BarH),
                        Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left
                                     | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
