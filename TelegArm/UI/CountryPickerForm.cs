using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Searchable country list for login: type to filter by name or dial code; tap a row to choose. Flags
    /// render via the bundled Noto set (EmojiRenderer); if a flag image is missing, a 2-letter ISO2 chip is
    /// drawn instead — so nothing shows a broken emoji box on RT. An owner-drawn list whose scrollbar is themed
    /// on every Windows version including RT 8.1.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The search box and the
    /// list are the dialog's OWN body (<c>UseOwnBody</c>): the list scrolls by itself (ThemedListBox = NoNativeScrollPanel
    /// + ThemedScrollBar + TouchScroller), so there is no stacked-row body. <paramref name="dark"/> and
    /// <paramref name="accent"/> are kept for the caller and no longer read: the palette follows the theme live.
    /// Esc / Cancel / the title bar's close = no choice; tapping a row, or Enter in the search box (first match) = chosen.
    /// </summary>
    public sealed class CountryPickerForm : DialogForm
    {
        private const int RowLogical = 38;

        private readonly ThemedTextBox _search;
        private readonly ThemedListBox _list;
        private readonly PickerBody _picker;
        private List<Country> _shown;

        public Country SelectedCountry { get; private set; }

        public CountryPickerForm(bool dark, Color accent) : base("Choose a country", 360, 540)
        {
            var p = ThemeHelper.Palette;

            _search = new ThemedTextBox(false) { Hint = "Search country or dial code", AccessibleName = "Search countries" };
            _search.Inner.AccessibleName = "Search countries";
            _search.TextChanged += (s, e) => Filter(_search.Text);
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && _shown != null && _shown.Count > 0) { Choose(0); e.SuppressKeyPress = true; } };

            _list = new ThemedListBox(p.Dark, p.Accent) { RowHeight = Px(RowLogical), CanvasBackColor = p.Window };
            _list.DrawRow += DrawRow;
            _list.ItemClicked += Choose;
            _list.AttachSearchBox(_search);   // R6: Down in the search box moves into the list

            _picker = new PickerBody(_search, _list) { BackColor = p.Window };
            UseOwnBody(_picker);

            Filter("");
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            FocusOnShow(_search.Inner);
        }

        private void Filter(string q)
        {
            q = (q ?? "").Trim();
            string digits = new string(q.Where(char.IsDigit).ToArray());
            IEnumerable<Country> src = Countries.All;
            if (q.Length > 0)
                src = Countries.All.Where(c =>
                    (c.Name != null && c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (digits.Length > 0 && c.DialCode != null && c.DialCode.StartsWith(digits, StringComparison.Ordinal)));
            _shown = src.ToList();
            _list.SetItems(_shown.Count);
        }

        private void Choose(int index)
        {
            if (_shown == null || index < 0 || index >= _shown.Count) return;
            SelectedCountry = _shown[index];
            DialogResult = DialogResult.OK;
        }

        private void DrawRow(Graphics g, int index, Rectangle r)
        {
            if (index < 0 || _shown == null || index >= _shown.Count) return;
            var c = _shown[index];
            var p = ThemeHelper.Palette;
            int dpi = WindowDpiValue;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(p.Window)) g.FillRectangle(b, r);

            int flagW = Px(30), flagH = Px(24);
            var flagRect = new Rectangle(r.X + Px(12), r.Y + (r.Height - flagH) / 2, flagW, flagH);
            Image flag = EmojiRenderer.Available ? EmojiRenderer.Get(c.FlagEmoji) : null;
            if (flag != null)
                g.DrawImage(flag, flagRect);
            else
            {
                using (var chip = new SolidBrush(p.Card))
                using (var path = DrawHelper.RoundedRect(flagRect, Px(4))) g.FillPath(chip, path);
                TextRenderer.DrawText(g, (c.Iso2 ?? "?").ToUpperInvariant(), FontHelper.UiAt(8f, dpi, FontStyle.Bold), flagRect, p.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tx = flagRect.Right + Px(12);
            var nameRect = new Rectangle(tx, r.Y, r.Width - tx - Px(58), r.Height);
            TextRenderer.DrawText(g, c.Name ?? "", TextRoles.Font(TextRole.Body, dpi, c.Name ?? ""), nameRect, p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var dialRect = new Rectangle(r.Right - Px(56), r.Y, Px(48), r.Height);
            TextRenderer.DrawText(g, "+" + c.DialCode, TextRoles.Font(TextRole.Body, dpi), dialRect, p.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // ── theme + DPI ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs from the base constructor too, before the fields exist — hence the null check.</summary>
        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (_picker == null) return;
            var p = ThemeHelper.Palette;
            _picker.BackColor = p.Window;
            _list.CanvasBackColor = p.Window;
            _search.Recolor();
            _picker.Invalidate(true);
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_list == null) return;
            // The list's row height is in pixels (not a Bounds DpiScaler would scale): re-derive it at the new DPI.
            _list.RowHeight = Px(RowLogical);
            _list.SetItems(_shown != null ? _shown.Count : 0);
        }

        /// <summary>The search field over the list, laid out at the window's DPI. The list is full width (it carries its own
        /// scrollbar); the field is inset by <c>Pad</c> on the top, left and right and sits <c>Pad</c> above the list.</summary>
        private sealed class PickerBody : Panel
        {
            private const int Pad = 12;
            private readonly ThemedTextBox _field;
            private readonly ThemedListBox _rows;

            public PickerBody(ThemedTextBox field, ThemedListBox rows)
            {
                _field = field;
                _rows = rows;
                Controls.Add(rows);
                Controls.Add(field);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int pad = Dpi.Px(Pad, Dpi.Of(this));
                int fh = _field.PreferredFieldHeight;
                _field.SetBounds(pad, pad, Math.Max(1, Width - 2 * pad), fh);
                int top = pad + fh + pad;
                _rows.SetBounds(0, top, Width, Math.Max(1, Height - top));
            }
        }
    }
}
