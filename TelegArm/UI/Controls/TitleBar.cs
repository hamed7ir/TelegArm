using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 1.3 — the accent title bar of an <see cref="AppForm"/>: an optional Menu or Back button
    /// on the left, the title, a slot for a control (the proxy pill, step 2.7b), and minimise / maximise-restore /
    /// close on the right.
    ///
    /// ★ IT IS NOT A DRAG HANDLE. The bar answers WM_NCHITTEST with HTTRANSPARENT, so Windows asks the form beneath,
    ///   and AppForm answers HTCAPTION (or HTTOP on the top edge). Dragging, double-click-to-maximise, Aero Snap and
    ///   touch drag then come from Windows itself — never from assigning Location (DroidFinestra's manual drag
    ///   cannot Snap and fights WM_DPICHANGED). Only the buttons and the slot's control are hit as themselves.
    /// ★ The title is drawn, not a Label (a Label would be hit as itself): script-aware font (Vazirmatn for
    ///   Persian), inline colour emoji, right-aligned when the title is RTL — WITHOUT RightToLeftLayout, which
    ///   mirrors every child and has broken RT input before.
    /// Height: 46 logical px, or 36 in <see cref="Compact"/> mode (plan D11 — RT's 1366×768 always gets 36).</summary>
    public sealed class TitleBar : Control, IDpiAware
    {
        public const int FullHeight = 46, CompactHeight = 36, ButtonWidth = 52;

        public enum LeftButton { None, Menu, Back }

        private readonly GlyphButton _left = new GlyphButton(GlyphButton.Glyph.Menu) { Visible = false };
        private readonly GlyphButton _min = new GlyphButton(GlyphButton.Glyph.Minimize);
        private readonly GlyphButton _max = new GlyphButton(GlyphButton.Glyph.Maximize);
        private readonly GlyphButton _close = new GlyphButton(GlyphButton.Glyph.Close) { DangerHover = true };
        private Control _slot;
        private string _title = "";
        private bool _compact, _canMaximize = true, _canMinimize = true;
        private LeftButton _leftKind = LeftButton.None;

        /// <summary>Raised by the left button (Menu or Back).</summary>
        public event EventHandler LeftClick;

        public TitleBar()
        {
            TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.TitleBar;
            _left.Click += (s, e) => { var h = LeftClick; if (h != null) h(this, EventArgs.Empty); };
            _min.Click += (s, e) => { var f = FindForm(); if (f != null) f.WindowState = FormWindowState.Minimized; };
            _max.Click += (s, e) => ToggleMaximize();
            _close.Click += (s, e) => { var f = FindForm(); if (f != null) f.Close(); };
            Controls.Add(_left); Controls.Add(_min); Controls.Add(_max); Controls.Add(_close);
            ApplyHeight();
            Recolor();
        }

        public string Title
        {
            get { return _title; }
            set { _title = value ?? ""; AccessibleName = _title; Invalidate(); }
        }

        public bool Compact
        {
            get { return _compact; }
            set { if (_compact == value) return; _compact = value; ApplyHeight(); Invalidate(); }
        }

        public LeftButton LeftKind
        {
            get { return _leftKind; }
            set
            {
                _leftKind = value;
                _left.Visible = value != LeftButton.None;
                if (value != LeftButton.None) _left.Kind = value == LeftButton.Back ? GlyphButton.Glyph.Back : GlyphButton.Glyph.Menu;
                PerformLayout(); Invalidate();
            }
        }

        public bool CanMaximize { get { return _canMaximize; } set { _canMaximize = value; _max.Visible = value; PerformLayout(); } }
        public bool CanMinimize { get { return _canMinimize; } set { _canMinimize = value; _min.Visible = value; PerformLayout(); } }

        /// <summary>A control shown between the title and the window buttons (the proxy pill). Its own size is kept;
        /// it is vertically centred. Null removes it.</summary>
        public Control Slot
        {
            get { return _slot; }
            set
            {
                if (_slot != null) Controls.Remove(_slot);
                _slot = value;
                if (_slot != null) Controls.Add(_slot);
                PerformLayout();
            }
        }

        public void ToggleMaximize()
        {
            var f = FindForm();
            if (f == null || !_canMaximize) return;
            f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        /// <summary>Called by the form on resize: the maximise button shows Restore while maximised.</summary>
        internal void SyncWindowState(FormWindowState s)
        {
            _max.Kind = s == FormWindowState.Maximized ? GlyphButton.Glyph.Restore : GlyphButton.Glyph.Maximize;
        }

        /// <summary>Re-reads the palette (accent bar, readable glyphs). AppForm calls it on ThemeChanged.</summary>
        public void Recolor()
        {
            var p = ThemeHelper.Palette;
            BackColor = p.TitleBar;
            foreach (var b in new[] { _left, _min, _max, _close }) { b.BarColor = p.TitleBar; b.Invalidate(); }
            Invalidate();
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { ApplyHeight(); PerformLayout(); Invalidate(); }

        private void ApplyHeight() { Height = Dpi.Px(_compact ? CompactHeight : FullHeight, this); }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplyHeight(); }

        /// <summary>⚠ LAYOUT FROM THE BAR'S OWN FLAGS, NEVER FROM Control.Visible. Visible reports false for every child
        /// of a form that is not shown yet, so a layout pass before Show placed NO window buttons (found by the step
        /// 1.4 rig shots: dialogs without their ✕), and nothing guaranteed another pass after Show.</summary>
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int bw = Dpi.Px(ButtonWidth, this), h = Height, x = Width;
            foreach (var b in new[] { _close, _max, _min })
            {
                if ((b == _max && !_canMaximize) || (b == _min && !_canMinimize)) continue;
                x -= bw;
                b.SetBounds(x, 0, bw, h);
            }
            // The slot is the caller's control and the caller hides it by Visible; while the bar itself is hidden that
            // cannot be read, so the slot keeps its place (OnVisibleChanged lays out again once the bar is shown).
            if (_slot != null && (_slot.Visible || !Visible))
            {
                x -= _slot.Width + Dpi.Px(8, this);
                _slot.Location = new Point(x, (h - _slot.Height) / 2);
            }
            _titleRight = x;
            if (_leftKind != LeftButton.None) _left.SetBounds(0, 0, bw, h);
        }

        private int _titleRight = -1;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { PerformLayout(); Invalidate(); }
        }

        /// <summary>The area the title may use: between the left button and the slot / window buttons.</summary>
        private Rectangle TitleRect()
        {
            int bw = Dpi.Px(ButtonWidth, this);
            int left = _leftKind != LeftButton.None ? bw : Dpi.Px(16, this);
            int right = _titleRight >= 0 ? _titleRight : Width;
            return new Rectangle(left, 0, Math.Max(0, right - left - Dpi.Px(8, this)), Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            e.Graphics.Clear(p.TitleBar);
            if (_title.Length == 0) return;
            int dpi = Dpi.Of(this);
            var font = FontHelper.ForAt(_title, _compact ? 11.5f : 13f, dpi, FontStyle.Bold);   // shared: never dispose
            var r = TitleRect();
            if (FontHelper.IsPersian(_title))
            {
                int w = EmojiRenderer.MeasureLineWidth(e.Graphics, _title, font);
                if (w < r.Width) r = new Rectangle(r.Right - w, r.Y, w, r.Height);
            }
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            EmojiRenderer.DrawLine(e.Graphics, _title, font, p.OnAccent, r);
        }

        private const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;

        protected override void WndProc(ref Message m)
        {
            // Everything that is not a button or the slot belongs to the form's caption (see the class summary).
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }
    }
}
