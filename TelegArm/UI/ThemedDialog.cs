using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed replacement for <see cref="MessageBox"/>. <see cref="Show"/> returns the index of the clicked button
    /// (0 = the first), or -1 when dismissed (Esc, ✕).
    ///
    /// Redesign step 2.1: the dialog behind the facade is a <see cref="DialogForm"/> (was a MaterialForm); the 157
    /// callers are unchanged, and so is what they rely on:
    ///  • the button ORDER: index 0, the primary action, is the LEFTMOST — 1.x's layout, kept for muscle memory;
    ///  • the INITIAL FOCUS: the LAST button (1.x's first tab stop), so Enter on ("Delete", "Cancel") is Cancel;
    ///  • Esc = -1; Ctrl+C copies the WHOLE message; the message itself is selectable text.
    /// What changed: sizes are logical (Px), so the dialog is right at 125/150% instead of 1.x's fixed pixels; a long
    /// message scrolls in the body (themed bar, touch momentum) instead of growing past the screen; a destructive first
    /// action ("Delete", "Leave", …) is drawn red; and it no longer registers with MaterialSkinManager — 1.x re-skinned
    /// every managed window (BackColor/ForeColor/Font of every control) each time a dialog opened.
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

        /// <summary>Where Ctrl+C sends the message; the probe swaps it so a test never touches the user's clipboard.</summary>
        internal static Func<string, bool> CopyToClipboard = text =>
        {
            try { Clipboard.SetText(text.Length > 0 ? text : " "); return true; } catch { return false; }
        };

        // A first action that destroys or leaves something is drawn Danger-red, never accent.
        private static readonly Regex Destructive = new Regex(@"^(delete|remove|leave|log ?out|sign out|block|ban|kick|clear|discard|revoke|unlink|terminate|reset|stop)\b",
                                                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private sealed class Dialog : DialogForm
        {
            public int Result = -1;

            private const int W = 440, MaxBody = 420, MinButton = 96, ButtonPad = 40;
            private readonly string _message;
            private readonly TextBox _text;

            public Dialog(string title, string message, string[] buttons) : base(title ?? "", W, 200)
            {
                _message = message ?? "";
                var p = ThemeHelper.Palette;

                // COPYABLE message: a read-only, borderless TextBox (drag-select, right-click Copy, Ctrl+C for all) — as in
                // 1.x, now sized by the body (it scrolls there when longer than the dialog may grow).
                int dpi = WindowDpiValue;
                _text = new TextBox
                {
                    Text = _message.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    Multiline = true, ReadOnly = true, WordWrap = true,
                    BorderStyle = BorderStyle.None, TabStop = false, ScrollBars = ScrollBars.None,
                    Font = FontHelper.ForAt(_message, 10.5f, dpi),
                    BackColor = p.Window, ForeColor = p.Text, Cursor = Cursors.IBeam,
                };
                var row = new MessageHost(_text);
                PopulateBody(row);

                // First added = rightmost: add the LAST button first, so index 0 ends up leftmost (1.x's order).
                RoundedButton last = null;
                for (int i = buttons.Length - 1; i >= 0; i--)
                {
                    int idx = i;
                    var kind = i == 0 ? (Destructive.IsMatch(buttons[i] ?? "") ? RoundedButtonKind.Danger : RoundedButtonKind.Primary)
                                      : RoundedButtonKind.Neutral;
                    var b = AddFooterButton(buttons[i] ?? "", kind, DialogResult.None, ButtonWidthFor(buttons[i] ?? ""));
                    b.Click += (s, e) => { Result = idx; DialogResult = DialogResult.OK; };
                    if (last == null) last = b;
                }
                ActiveControl = last;   // 1.x: the last button had focus, so Enter is the safe choice

                // Height from the wrapped message at this width, capped (a longer message scrolls in the body).
                int bodyWidth = Width - 2 - ThemedScrollBar.LaneWidth(dpi);
                int bodyH = Math.Min(Px(MaxBody), row.HeightFor(bodyWidth) + Px(RowGap) * 2);
                Size = new Size(Width, Bar.Height + bodyH + Px(FooterHeight) + 1);
            }

            /// <summary>Wide enough for the label (button font, logical px), at least <see cref="MinButton"/>.</summary>
            private static int ButtonWidthFor(string text)
            {
                var f = FontHelper.UiAt(10f, Dpi.Logical, FontStyle.Bold);
                int w = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                return Math.Max(MinButton, w + ButtonPad);
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == (Keys.Control | Keys.C))   // the WHOLE message, as 1.x (a selection is not needed)
                {
                    CopyToClipboard(_message);
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void ApplyDialogTheme()
            {
                base.ApplyDialogTheme();
                if (_text == null) return;
                var p = ThemeHelper.Palette;
                _text.BackColor = p.Window; _text.ForeColor = p.Text;
            }

            /// <summary>The message TextBox inside a row whose height follows the wrapped text at the row's width.</summary>
            private sealed class MessageHost : Control
            {
                private const int PadX = 14, PadY = 8;
                private readonly TextBox _box;
                private int _measuredWidth = -1;

                public MessageHost(TextBox box)
                {
                    _box = box;
                    TabStop = false;
                    Controls.Add(box);
                    BackColor = ThemeHelper.Palette.Window;
                }

                public int HeightFor(int width)
                {
                    int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
                    int textW = Math.Max(1, width - 2 * padX);
                    var sz = TextRenderer.MeasureText(_box.Text, _box.Font, new Size(textW, 0),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                    return sz.Height + 2 * padY + Dpi.Px(4, dpi);   // a little slack: the EDIT's own line spacing
                }

                protected override void OnSizeChanged(EventArgs e)
                {
                    base.OnSizeChanged(e);
                    if (Width != _measuredWidth)
                    {
                        _measuredWidth = Width;
                        int h = HeightFor(Width);
                        if (Height != h) Height = h;
                    }
                    int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
                    _box.SetBounds(padX, padY, Math.Max(1, Width - 2 * padX), Math.Max(1, Height - 2 * padY));
                }
            }
        }
    }
}
