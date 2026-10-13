using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// New Group / New Channel creation form — a name (+ description for a channel) and a member selection that
    /// reuses <see cref="PeoplePickerForm"/> (multi-select). Returns the title/about/members; the caller does
    /// the bounded Channels_CreateChannel (+ invite) RPC. Themed; no network here (the picker fetches contacts).
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). Rows: the name
    /// (<see cref="TextRow"/>), for a channel the description (a multi-line field), and the member selection. Create is
    /// the rightmost footer button and stays disabled until the name is non-blank; Enter = Create, Esc = Cancel.
    /// <paramref name="dark"/> and <paramref name="accent"/> are kept for the caller and handed on to the member picker
    /// (its constructor still takes them); this form itself follows the palette live.
    /// </summary>
    public sealed class CreateChatForm : DialogForm
    {
        public enum Kind { Group, Channel }

        private readonly TextRow _nameRow;
        private readonly DescriptionRow _descRow;   // channel only; null for a group
        private readonly MembersRow _membersRow;
        private readonly RoundedButton _create;

        public string ChatTitle { get; private set; }
        public string ChatAbout { get; private set; }
        public List<User> Members { get; private set; } = new List<User>();

        public CreateChatForm(Kind kind, Func<Task<List<User>>> fetch, bool dark, Color accent,
                              Func<long, Image> cachedAvatar = null, Func<long, IPeerInfo, Task<Image>> loadAvatar = null)
            : base(kind == Kind.Group ? "New Group" : "New Channel", 380, 300)
        {
            _nameRow = new TextRow(kind == Kind.Group ? "Group name" : "Channel name", "");
            _membersRow = new MembersRow();
            _membersRow.SelectClicked += (s, e) =>
            {
                using (var p = new PeoplePickerForm(fetch, true, dark, accent, "Add members", cachedAvatar, loadAvatar))
                    if (p.ShowDialog(this) == DialogResult.OK)
                    {
                        Members = p.SelectedUsers ?? new List<User>();
                        _membersRow.Text = Members.Count == 0 ? "No members selected" : (Members.Count + (Members.Count == 1 ? " member" : " members") + " selected");
                    }
            };

            if (kind == Kind.Channel)
            {
                _descRow = new DescriptionRow("Description (optional)");
                PopulateBody(_nameRow, _descRow, _membersRow);
            }
            else PopulateBody(_nameRow, _membersRow);

            _create = AddFooterButton("Create", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            _create.Enabled = false;
            _create.Click += (s, e) =>
            {
                ChatTitle = _nameRow.Value.Trim();
                ChatAbout = _descRow != null ? (_descRow.Value ?? "").Trim() : "";
                DialogResult = DialogResult.OK;
            };
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = _create;
            _nameRow.Changed += (s, e) => { _create.Enabled = _nameRow.Value.Trim().Length > 0; };
            FocusOnShow(_nameRow.Field.Inner);
            FitToContent();
        }

        // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>"No members selected" / "N members selected" on the left, the "Select members" button on the right.
        /// The caption is the SettingRow caption (set <c>Text</c> to change it); the button's click is
        /// <c>SelectClicked</c>, so the form (the picker's owner) opens the picker.</summary>
        private sealed class MembersRow : SettingRow
        {
            private const int ButtonW = 140, ButtonH = 34, Gap = 12;
            private const float ButtonPt = 10f;
            private readonly RoundedButton _button;

            public event EventHandler SelectClicked;

            public MembersRow() : base("No members selected", false)
            {
                _button = new RoundedButton { Text = "Select members", Kind = RoundedButtonKind.Secondary, Modern = true };
                _button.Click += (s, e) => { var h = SelectClicked; if (h != null) h(this, EventArgs.Empty); };
                AccessibleRole = AccessibleRole.Grouping;
                Controls.Add(_button);
                Refit();
            }

            // The button's font is the row's to set, at the window's DPI (an absolute assignment, so it is safe whenever
            // DpiScaler's generic pass has also rescaled it): known once the row is parented, shown, or rescaled.
            private void ApplyButtonFont() { if (_button != null) _button.Font = FontHelper.UiAt(ButtonPt, Dpi.Of(this), FontStyle.Bold); }

            protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplyButtonFont(); }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyButtonFont();
                PerformLayout();
            }

            protected override void OnDpiRescaled(int oldDpi, int newDpi) { ApplyButtonFont(); }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (_button == null) return;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), bw = Dpi.Px(ButtonW, dpi), bh = Dpi.Px(ButtonH, dpi);
                _button.SetBounds(Width - padX - bw, (Height - bh) / 2, bw, bh);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                PaintBack(g, p);
                int right = _button.Left - Dpi.Px(Gap, dpi);
                DrawCaption(g, p, new Rectangle(padX, 0, Math.Max(1, right - padX), Height), dpi);
            }
        }

        /// <summary>A caption over an editable multi-line <see cref="ThemedTextBox"/> (no scroll bar — a native one is white on
        /// RT; a long text scrolls with the caret). Laid out like <see cref="TextRow"/>; the box is a fixed logical height.</summary>
        private sealed class DescriptionRow : SettingRow
        {
            private const float SmallPt = 9.5f, FieldPt = 10.5f;
            private const int TopPad = 8, Gap = 6, BottomPad = 10, BoxHeight = 72, MinBox = 24;
            private readonly ThemedTextBox _box;

            public DescriptionRow(string caption) : base(caption, false)
            {
                _box = new ThemedTextBox(true) { AccessibleName = caption ?? "" };
                _box.Inner.AccessibleName = caption ?? "";
                _box.FontPoints = FieldPt;   // ThemedTextBox resolves it at the window's DPI; the EDIT ignores direct font writes
                AccessibleRole = AccessibleRole.Grouping;
                Controls.Add(_box);
                Refit();
            }

            /// <summary>The text.</summary>
            public string Value { get { return _box.Text; } }

            private int CaptionBand(int dpi)
            {
                if (Text.Length == 0) return 0;
                return LineHeight(TextRoles.Font(TextRole.Field, dpi, Text)) + Dpi.Px(Gap, dpi);
            }

            public override int HeightFor(int width, int dpi)
            {
                if (_box == null) return Dpi.Px(DefaultHeight, dpi);
                return Dpi.Px(TopPad, dpi) + CaptionBand(dpi) + Dpi.Px(BoxHeight, dpi) + Dpi.Px(BottomPad, dpi);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                _box.Recolor();
                PerformLayout();
            }

            protected override void OnThemeApplied()
            {
                _box.Recolor();
                base.OnThemeApplied();
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (_box == null) return;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), top = Dpi.Px(TopPad, dpi) + CaptionBand(dpi);
                _box.SetBounds(padX, top, Math.Max(1, Width - 2 * padX), Math.Max(Dpi.Px(MinBox, dpi), Height - top - Dpi.Px(BottomPad, dpi)));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                PaintBack(g, p);
                if (Text.Length == 0) return;
                var font = TextRoles.Font(TextRole.Field, dpi, Text);
                DrawTextLine(g, Text, font, p.TextDim, new Rectangle(padX, Dpi.Px(TopPad, dpi), Math.Max(1, Width - 2 * padX), LineHeight(font)), false, true);
            }
        }
    }
}
