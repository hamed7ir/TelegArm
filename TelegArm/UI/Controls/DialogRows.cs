using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    // Redesign step 1.4 — the three rows the 1.4 dialogs need, owner-drawn from ThemeHelper.Palette (cached, so a
    // paint reads no registry) and measured in logical pixels at the window's DPI. Step 1.6 brings DroidFinestra's
    // SettingRows (NoteRow / TextRow / ToggleRow); these are the minimal forms of three of them and fold into that kit
    // there. InputRow's TextBox becomes ThemedTextBox in step 1.5.

    /// <summary>Wrapped body text. Its height follows its width (set by DialogForm.Restack), so a long message is
    /// never clipped; the dialog's body scrolls if it is taller than the screen allows.</summary>
    internal sealed class MessageRow : Control
    {
        private const int PadX = 14, PadY = 8;
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        private int _measuredWidth = -1;

        public MessageRow(string text)
        {
            base.Text = text ?? "";
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = base.Text;
        }

        /// <summary>The height this row needs at <paramref name="width"/> (used to size a dialog before it is shown).</summary>
        public int HeightFor(int width, int dpi)
        {
            int padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
            var sz = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - 2 * padX), 0), Flags);
            return sz.Height + 2 * padY;
        }

        private void Remeasure()
        {
            if (Width <= 0) return;
            _measuredWidth = Width;
            int h = HeightFor(Width, Dpi.Of(this));
            if (Height != h) Height = h;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width != _measuredWidth) Remeasure();
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Remeasure(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), padY = Dpi.Px(PadY, dpi);
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Window);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(padX, padY, Math.Max(1, Width - 2 * padX), Math.Max(1, Height - padY)), p.Text, Flags);
        }
    }

    /// <summary>A caption over a single-line text field with a painted 1 px frame (accent while focused). The field is a
    /// plain native TextBox with no border — the kind step 1.5's ThemedTextBox hosts.</summary>
    internal sealed class InputRow : Control
    {
        private const int PadX = 14, Top0 = 6, Gap = 6, FieldH = 34, Bottom0 = 8;
        public readonly TextBox Box;

        public InputRow(string caption, string initial)
        {
            base.Text = caption ?? "";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Box = new TextBox { BorderStyle = BorderStyle.None, Text = initial ?? "", AccessibleName = base.Text };
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
            Recolor();
        }

        /// <summary>Re-reads the palette (the dialog calls this on a theme change).</summary>
        public void Recolor()
        {
            var p = ThemeHelper.Palette;
            Box.BackColor = p.Card;
            Box.ForeColor = p.Text;
            Invalidate();
        }

        private int CaptionHeight { get { return TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPrefix).Height; } }

        private Rectangle FieldRect
        {
            get
            {
                int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi);
                int top = Dpi.Px(Top0, dpi) + CaptionHeight + Dpi.Px(Gap, dpi);
                return new Rectangle(padX, top, Math.Max(1, Width - 2 * padX), Dpi.Px(FieldH, dpi));
            }
        }

        private void Remeasure()
        {
            int dpi = Dpi.Of(this);
            Height = FieldRect.Bottom + Dpi.Px(Bottom0, dpi);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // The box INHERITS this font — never copy it down: DpiScaler would scale the copy a second time (see ThemedTextBox).
            Remeasure();
            PerformLayout();
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Remeasure(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var f = FieldRect;
            int inset = Dpi.Px(8, Dpi.Of(this));
            Box.SetBounds(f.X + inset, f.Y + Math.Max(0, (f.Height - Box.Height) / 2), Math.Max(1, f.Width - 2 * inset), Box.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this);
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            TextRenderer.DrawText(g, Text, Font, new Point(Dpi.Px(PadX, dpi), Dpi.Px(Top0, dpi)), p.TextDim, TextFormatFlags.NoPrefix);
            var f = FieldRect;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DrawHelper.RoundedRect(new Rectangle(f.X, f.Y, f.Width - 1, f.Height - 1), Dpi.Px(6, dpi)))
            {
                using (var b = new SolidBrush(p.Card)) g.FillPath(b, path);
                using (var pen = new Pen(Box.Focused ? p.Accent : p.Divider, Box.Focused ? Dpi.F(1.5f, dpi) : 1f)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>A check box row: click or Space toggles it; focus ring with the keyboard. Enter is left to the dialog
    /// (AcceptButton) — it does not toggle.</summary>
    internal sealed class CheckRow : Control
    {
        private const int PadX = 14, Box = 18, H = 40;
        private bool _checked;
        public event EventHandler CheckedChanged;

        public CheckRow(string text, bool initial)
        {
            base.Text = text ?? "";
            _checked = initial;
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AccessibleRole = AccessibleRole.CheckButton;
            AccessibleName = base.Text;
            Height = H;
        }

        public bool Checked
        {
            get { return _checked; }
            set { if (_checked == value) return; _checked = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Height = Dpi.Px(H, Dpi.Of(this)); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Checked = !Checked; }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), padX = Dpi.Px(PadX, dpi), s = Dpi.Px(Box, dpi);
            g.Clear(Parent != null ? Parent.BackColor : p.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(padX, (Height - s) / 2, s, s);
            using (var path = DrawHelper.RoundedRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Dpi.Px(4, dpi)))
            {
                if (_checked) using (var b = new SolidBrush(p.Accent)) g.FillPath(b, path);
                else using (var pen = new Pen(p.TextDim, Dpi.F(1.5f, dpi))) g.DrawPath(pen, path);
            }
            if (_checked)
            {
                float u = s / 18f, cx = box.X + s / 2f, cy = box.Y + s / 2f;
                using (var pen = new Pen(p.OnAccent, Dpi.F(2f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - 5 * u, cy, cx - 1.5f * u, cy + 3.5f * u);
                    g.DrawLine(pen, cx - 1.5f * u, cy + 3.5f * u, cx + 5 * u, cy - 4 * u);
                }
            }
            var tr = new Rectangle(box.Right + Dpi.Px(10, dpi), 0, Math.Max(1, Width - box.Right - Dpi.Px(10, dpi) - padX), Height);
            TextRenderer.DrawText(g, Text, Font, tr, p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(p.Accent, 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, Dpi.Px(4, dpi), 1, Width - Dpi.Px(8, dpi), Height - 3);
        }
    }
}
