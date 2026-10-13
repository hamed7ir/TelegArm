using System;
using System.Drawing;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// TIER 1 admin: edit a group/channel NAME, DESCRIPTION and PHOTO. Each change is saved via a bounded
    /// service call (off-UI-thread + timeout + a clear "VPN" message on timeout). Themed chrome + RT-dark
    /// scrollbar (the form scrolls on small RT screens). Only changed fields are pushed.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply + ScrollHost). Rows: the name
    /// (<see cref="TextRow"/>), the description (a multi-line field), and the photo (the "Change photo…" button with the
    /// chosen file's name beside it); Save / Cancel are the footer (Enter = Save, Esc = Cancel). The save flow, the
    /// change detection and every service call are exactly the 1.x ones. <paramref name="dark"/> and
    /// <paramref name="accent"/> are kept for the caller and no longer read: the palette follows the theme live.
    /// </summary>
    public sealed class EditChatInfoForm : DialogForm
    {
        private readonly TelegramService _service;
        private readonly Channel _channel;
        private readonly InputPeer _peer;
        private readonly string _origTitle;
        private string _origAbout;   // mutable: pre-filled from ChannelFull.about after the form opens (see LoadCurrentAboutAsync)

        private readonly TextRow _nameRow;
        private readonly DescriptionRow _descRow;
        private readonly PhotoRow _photoRow;
        private readonly RoundedButton _save;
        private string _photoPath;

        public EditChatInfoForm(TelegramService service, Channel channel, InputPeer peer, string title, string about, bool dark, Color accent)
            : base("Edit info", 400, 420)
        {
            _service = service; _channel = channel; _peer = peer;
            _origTitle = title ?? ""; _origAbout = about ?? "";

            _nameRow = new TextRow("Name", _origTitle);
            _descRow = new DescriptionRow("Description", _origAbout);
            _photoRow = new PhotoRow();
            _photoRow.ChangeClicked += PickPhoto;
            PopulateBody(_nameRow, _descRow, _photoRow);

            _save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            _save.Click += OnSave;
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = _save;
            FocusOnShow(_nameRow.Field.Inner);
            FitToContent();

            var _ = LoadCurrentAboutAsync();   // pre-fill the CURRENT description so a save doesn't blank it
        }

        /// <summary>Pre-fill FIX: the caller opens this form without the description (it isn't cheaply available at
        /// the call site), so the Description field starts empty even when the channel HAS an about. Load the CURRENT
        /// about (ChannelFull.about) on open and drop it in — so editing (or re-saving unchanged) PRESERVES it rather
        /// than wiping it. Only fills if the user hasn't already started typing; failure keeps the passed-in value.</summary>
        private async System.Threading.Tasks.Task LoadCurrentAboutAsync()
        {
            string about;
            try
            {
                var mcf = await _service.GetChannelFullAsync(_channel);
                about = (mcf?.full_chat as ChannelFull)?.about ?? "";
            }
            catch { return; }
            if (IsDisposed || _descRow == null || _descRow.IsDisposed) return;
            if (string.IsNullOrEmpty(about)) return;      // nothing to pre-fill
            if (_descRow.Value != _origAbout) return;     // the user already edited the field → respect their text
            _origAbout = about;                           // so OnSave's change-detection compares against the REAL current value
            _descRow.Value = about;
        }

        private void PickPhoto(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png", Title = "Choose a photo" })
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _photoPath = ofd.FileName;
                    _photoRow.Status = System.IO.Path.GetFileName(_photoPath);
                }
        }

        private async void OnSave(object sender, EventArgs e)
        {
            _save.Enabled = false;
            try
            {
                if (_nameRow.Value.Trim().Length > 0 && _nameRow.Value.Trim() != _origTitle)
                {
                    string newTitle = _nameRow.Value.Trim();
                    if (!await _service.EditChatTitleAsync(_channel, newTitle)) { Vpn("name"); return; }
                    Logger.Diag("[ADMIN] title updated");
                    _channel.title = newTitle;   // keep the shared Channel entity consistent
                    // RELEASE-FIXES-V11 (H1): refresh the chat-list row + header + open profile INSTANTLY (no reload).
                    TelegArm.UI.MainForm.RaisePeerTitleChanged(_channel.id, newTitle);
                }
                if (_descRow.Value.Trim() != _origAbout)
                {
                    if (!await _service.EditChatAboutAsync(_peer, _descRow.Value.Trim())) { Vpn("description"); return; }
                    Logger.Diag("[ADMIN] about updated");
                }
                if (!string.IsNullOrEmpty(_photoPath))
                {
                    _photoRow.Status = "Uploading photo…";
                    if (!await _service.EditChatPhotoAsync(_channel, _photoPath)) { Vpn("photo"); return; }
                    Logger.Diag("[ADMIN] photo updated");
                    // CHANNEL-PHOTO-REFRESH: EditChatPhotoAsync updated _channel.photo → drop the STALE cached avatar and
                    // re-fetch the new one so the chat-list row / header / bubbles repaint instantly (via AvatarLoaded),
                    // no app-reload/switch needed. Same mechanism the self-avatar edit uses (PROFILE-EDIT-SELF).
                    AvatarStore.Current?.Invalidate(_channel.id);
                    AvatarStore.Current?.Request(_channel.id, _channel);
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { _save.Enabled = true; ThemedDialog.Show(this, "Edit info", "Couldn't save: " + ex.Message, "OK"); }
        }

        private void Vpn(string what)
        {
            _save.Enabled = true;
            ThemedDialog.Show(this, "Edit info", "Couldn't update the " + what + " — make sure your VPN is on.", "OK");
        }

        // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A caption over an editable multi-line <see cref="ThemedTextBox"/> (no scroll bar — a native one is white on
        /// RT; a long text scrolls with the caret). Laid out like <see cref="TextRow"/>; the box is a fixed logical height.</summary>
        private sealed class DescriptionRow : SettingRow
        {
            private const float FieldPt = 10.5f;
            private const int TopPad = 8, Gap = 6, BottomPad = 10, BoxHeight = 92, MinBox = 24;
            private readonly ThemedTextBox _box;

            public DescriptionRow(string caption, string value) : base(caption, false)
            {
                _box = new ThemedTextBox(true) { AccessibleName = caption ?? "" };
                _box.Inner.AccessibleName = caption ?? "";
                _box.FontPoints = FieldPt;   // ThemedTextBox resolves it at the window's DPI; the EDIT ignores direct font writes
                _box.Text = value ?? "";
                AccessibleRole = AccessibleRole.Grouping;
                Controls.Add(_box);
                Refit();
            }

            /// <summary>The text, as typed (not trimmed).</summary>
            public string Value
            {
                get { return _box.Text; }
                set { _box.Text = value ?? ""; }
            }

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

        /// <summary>"Change photo…" at the left and, beside it, a quiet line that says what was chosen (the file's name) or
        /// what is happening ("Uploading photo…"). The line is <see cref="Status"/> — the SettingRow caption — drawn with the
        /// emoji-aware single line and an ellipsis; the button's click is <see cref="ChangeClicked"/>, so the form (the
        /// file dialog's owner) opens the picker.</summary>
        private sealed class PhotoRow : SettingRow
        {
            private const int ButtonW = 150, ButtonH = 38, PadY = 8, Gap = 12;
            private const float ButtonPt = 10f, StatusPt = 9.5f;
            private readonly RoundedButton _button;

            public event EventHandler ChangeClicked;

            public PhotoRow() : base("", false)
            {
                _button = new RoundedButton { Text = "Change photo…", Kind = RoundedButtonKind.Secondary, Modern = true };
                _button.Click += (s, e) => { var h = ChangeClicked; if (h != null) h(this, EventArgs.Empty); };
                AccessibleRole = AccessibleRole.Grouping;
                AccessibleName = "Chat photo";   // set, so a Status change (the caption) never replaces it
                Controls.Add(_button);
                Refit();
            }

            /// <summary>The line beside the button; empty = nothing chosen yet.</summary>
            public string Status { get { return Text; } set { Text = value; } }

            public override int HeightFor(int width, int dpi) { return Dpi.Px(ButtonH + 2 * PadY, dpi); }

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
                int dpi = Dpi.Of(this), bw = Dpi.Px(ButtonW, dpi), bh = Dpi.Px(ButtonH, dpi);
                _button.SetBounds(Dpi.Px(PadX, dpi), (Height - bh) / 2, bw, bh);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = ThemeHelper.Palette;
                var g = e.Graphics;
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                PaintBack(g, p);
                if (Text.Length == 0) return;
                int left = _button.Right + Dpi.Px(Gap, dpi);
                DrawTextLine(g, Text, FontHelper.ForAt(Text, StatusPt, dpi), p.TextDim, new Rectangle(left, 0, Math.Max(1, Width - padX - left), Height), false, true);
            }
        }
    }
}
