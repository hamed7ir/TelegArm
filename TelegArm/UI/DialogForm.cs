using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.4 — the base of the 2.x modal dialogs: DroidFinestra's ThemedDialog, ported onto
    /// <see cref="AppForm"/>(resizable: false). The accent <see cref="AppForm.Bar"/> with only ✕, a scrolling
    /// <see cref="Body"/> and a right-aligned <see cref="Footer"/> of action buttons.
    ///
    /// What is NOT DroidFinestra's, on purpose (plan §2.2):
    ///  • the body is TelegArm's scroll stack (<see cref="ScrollHost"/>: NoNativeScrollPanel + ThemedScrollBar +
    ///    TouchScroller), not ThemedScrollPanel — it keeps WM_TOUCH panning and momentum;
    ///  • dragging is the title bar's HTCAPTION (AppForm), never Location +=;
    ///  • the touch-keyboard shift listens to <see cref="KeyboardInset"/>, which MainForm's Part B feeds — no
    ///    second keyboard poller;
    ///  • every length is logical and goes through Px, so the dialog is right at any DPI and follows a monitor move.
    ///
    /// Keys: Esc = <see cref="Form.CancelButton"/> (or plain Cancel when there is none), Enter = <see
    /// cref="Form.AcceptButton"/> — both native WinForms, which works because RoundedButton is a Button.
    /// The static ThemedDialog facade is NOT switched to this yet (step 2.1); nothing in the live app opens one.</summary>
    public class DialogForm : AppForm
    {
        /// <summary>Rows stack top-down in here (<see cref="PopulateBody"/>); it scrolls when they overflow.</summary>
        protected readonly NoNativeScrollPanel Body;
        /// <summary>Right-aligned action buttons; the first added is the rightmost (<see cref="AddFooterButton"/>).</summary>
        protected readonly FlowLayoutPanel Footer;
        private readonly ThemedScrollBar _scrollBar;
        private Control _initialFocus;

        protected const int FooterHeight = 60, RowGap = 6, ButtonWidth = 116, ButtonHeight = 38;

        /// <param name="width">Logical (96-DPI) width of the whole dialog, title bar included.</param>
        /// <param name="height">Logical height of the whole dialog.</param>
        public DialogForm(string title, int width, int height) : base(false)
        {
            Text = title ?? "";
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Bar.CanMinimize = false;
            Font = FontHelper.UiAt(9.75f, WindowDpiValue);
            Size = new Size(Px(width), Px(height));

            var p = ThemeHelper.Palette;
            Footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Px(FooterHeight),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, Px(11), Px(14), Px(11)),
            };
            var bodyHost = new Panel { Dock = DockStyle.Fill };
            Content.Controls.Add(bodyHost);   // Fill first: docking takes the last-added (the footer) first
            Content.Controls.Add(Footer);
            Body = ScrollHost.Wrap(bodyHost, p.Dark, p.Accent);
            Body.ClientSizeChanged += (s, e) => Restack();
            _scrollBar = bodyHost.Controls.OfType<ThemedScrollBar>().FirstOrDefault();

            ApplyDialogTheme();
        }

        // ── building ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Adds a right-aligned action button (first added = rightmost). A <paramref name="result"/> other than
        /// None is set as the button's DialogResult, so clicking it closes the dialog with that result.</summary>
        protected RoundedButton AddFooterButton(string text, RoundedButtonKind kind, DialogResult result, int logicalWidth = ButtonWidth)
        {
            var b = new RoundedButton
            {
                Text = text,
                Kind = kind,
                Modern = true,
                DialogResult = result,
                Width = Px(logicalWidth),
                Height = Px(ButtonHeight),
                Margin = new Padding(Px(8), 0, 0, 0),
                Font = FontHelper.UiAt(10f, WindowDpiValue, FontStyle.Bold),
            };
            Footer.Controls.Add(b);
            return b;
        }

        /// <summary>Stacks <paramref name="rows"/> top-down in the body, each stretched to its width.</summary>
        protected void PopulateBody(params Control[] rows)
        {
            foreach (var r in rows)
            {
                r.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                Body.Controls.Add(r);
                r.SizeChanged += (s, e) => Restack();   // a wrapped row grows when the dialog narrows, and so on
            }
            Restack();
        }

        /// <summary>The control that gets the caret when the dialog opens — only with a hardware keyboard attached.
        /// With touch only, focusing a text field would pop the touch keyboard over a dialog the user has not even
        /// read yet; AppForm's WM_ACTIVATE fix deliberately clears that auto-focus.</summary>
        protected void FocusOnShow(Control c) { _initialFocus = c; }

        /// <summary>Stretch every row to the body width, then stack them top-down — a row whose height depends on its
        /// width (wrapped text) gets its real height before the next one is placed.</summary>
        protected void Restack()
        {
            // Re-entrant by design: setting a row's width can change its height, whose SizeChanged lands back here. That
            // nested call only asks for another pass; heights follow widths, so the second pass changes nothing.
            if (_restacking) { _restackAgain = true; return; }
            _restacking = true;
            try
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    _restackAgain = false;
                    int w = Body.ClientSize.Width, y = Px(RowGap) + Body.AutoScrollPosition.Y;
                    foreach (Control r in Body.Controls)
                    {
                        if (w > Px(10) && r.Width != w) r.Width = w;
                        if (r.Top != y) r.Top = y;
                        y += r.Height;
                    }
                    if (!_restackAgain) break;
                }
            }
            finally { _restacking = false; }
        }
        private bool _restacking, _restackAgain;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Restack();   // the body had no width when the rows were added
            // Listen only while shown: KeyboardInset.Listening is what makes Part B look for the keyboard at all.
            KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
            KeyboardInset.KeyboardRectChanged += OnKeyboardRect;
            if (_initialFocus != null && Helpers.TouchKeyboard.HasHardwareKeyboard())
            {
                // Posted: AppForm's WM_ACTIVATE fix posts its focus-clear during the show, so focusing synchronously here
                // would be undone. Posted from OnShown, this runs after that clear in either order of the two posts.
                var c = _initialFocus;
                try { BeginInvoke((Action)(() => { if (!IsDisposed && c.CanFocus) { c.Focus(); if (c is TextBoxBase t) t.SelectAll(); } })); } catch { }
            }
        }

        protected override void OnDpiChanged(int oldDpi, int newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Restack();
        }

        /// <summary>Esc with no CancelButton still cancels (a dialog whose buttons are all positive actions).</summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape && CancelButton == null)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        // ── theme ────────────────────────────────────────────────────────────────────────────────────────────

        protected override void OnThemeChanged()
        {
            base.OnThemeChanged();
            ApplyDialogTheme();
        }

        protected virtual void ApplyDialogTheme()
        {
            var p = ThemeHelper.Palette;
            Footer.BackColor = p.Footer;
            Body.BackColor = p.Window;
            if (Body.Parent != null) Body.Parent.BackColor = p.Window;
            if (_scrollBar != null) { _scrollBar.IsDark = p.Dark; _scrollBar.AccentColor = p.Accent; _scrollBar.Invalidate(); }
            foreach (Control b in Footer.Controls) b.Invalidate();
        }

        // ── the touch keyboard (DroidFinestra's FIN-KEYBOARD) ───────────────────────────────────────────────────

        private bool _kbShifted;            // the bounds are saved once per keyboard cycle, so a re-fire cannot drift them
        private Rectangle _kbSavedBounds;

        /// <summary>A short centred dialog can sit under the touch keyboard. Shift it up by the overlap (shrinking only if
        /// the title bar would leave the work area) and put the exact original bounds back when the keyboard goes.</summary>
        private void OnKeyboardRect(Rectangle kb)
        {
            try
            {
                if (IsDisposed || !Visible || WindowState != FormWindowState.Normal) return;
                if (kb.IsEmpty)
                {
                    if (_kbShifted) { _kbShifted = false; Bounds = _kbSavedBounds; }
                    return;
                }
                if (!ContainsFocus) return;   // the keyboard is for another window
                int need = Bounds.Bottom - kb.Top;
                if (need <= 0 || !Bounds.IntersectsWith(kb)) return;
                if (!_kbShifted) { _kbSavedBounds = Bounds; _kbShifted = true; }
                Rectangle wa;
                try { wa = Screen.FromHandle(Handle).WorkingArea; } catch { wa = Screen.PrimaryScreen.WorkingArea; }
                int top = Math.Max(wa.Top, Top - need);
                int remain = need - (Top - top);
                int h = remain > 0 ? Math.Max(Px(220), Height - remain) : Height;
                SetBounds(Left, top, Width, h);
                Restack();
            }
            catch (Exception ex) { Logger.Diag("[KBD] dialog shift failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) KeyboardInset.KeyboardRectChanged -= OnKeyboardRect;
            base.Dispose(disposing);
        }
    }
}
