using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Themed popup listing every pinned message (newest→oldest). A row tap raises
    /// <see cref="JumpRequested"/> (the owner jumps + closes); right-click / long-press on a row raises
    /// <see cref="ContextRequested"/> so the owner can show its themed Jump/Unpin menu. RT-safe: each row
    /// is a single owner-painted control, scrolled by a themed AutoScroll panel.
    ///
    /// Redesign step 3.3: a plain-Form popup laid out at the DPI of WHERE IT OPENS (the caller passes Dpi.ForPopup), sized
    /// before Show. It is an IDpiHost, so each PinnedRowControl adopts that DPI when it is parented (Dpi.Of). Every length
    /// is LOGICAL through Px; fonts are shared FontHelper.UiAt fonts (never disposed). WM_DPICHANGED is swallowed (no base:
    /// WinForms must not scale a plain Form) and the popup re-SETS its metrics at the new DPI (<see cref="Relayout"/>).
    /// At 96 DPI every value is the 1.x one, except the row width (deliberate, see <see cref="SizeRows"/>).
    /// </summary>
    public sealed class PinnedListForm : Form, IDpiHost
    {
        public event Action<int> JumpRequested;
        public event Action<int, Point> ContextRequested;

        private const int DesignW = 380, DesignH = 460, RowMargin = 3, MinRowW = 40;   // logical px

        private readonly bool _dark;
        private readonly Color _accent;
        private readonly FlowLayoutPanel _list;
        private readonly Panel _header;
        private readonly Label _title;
        private readonly Button _close;
        private int _dpi;   // step 3.3: the DPI this popup lays out at — where it opened, then WM_DPICHANGED's

        int IDpiHost.Dpi { get { return _dpi; } }

        private int Px(int n) { return Dpi.Px(n, _dpi); }

        /// <param name="dpi">Step 3.3 — the effective DPI where the popup opens: the caller's Dpi.ForPopup(anchor).
        /// 0 = <see cref="Dpi.DefaultDpi"/>.</param>
        public PinnedListForm(bool dark, Color accent, int dpi)
        {
            _dark = dark;
            _accent = accent;
            _dpi = dpi > 0 ? dpi : Dpi.DefaultDpi;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(Px(DesignW), Px(DesignH));   // sized at its DPI BEFORE Show: the caller positions it by this size
            TelegArm.Helpers.AppIcon.Set(this);   // app icon in Alt-Tab
            BackColor = ThemeHelper.PaletteFor(dark).PinnedBg;

            _header = new Panel { Dock = DockStyle.Top, BackColor = ThemeHelper.PaletteFor(dark).PinnedHeaderBg };
            _title = new Label
            {
                Text = "Pinned messages",
                Dock = DockStyle.Fill,
                ForeColor = ThemeHelper.PaletteFor(dark).PinnedTitle,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _close = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                ForeColor = ThemeHelper.PaletteFor(dark).PinnedSub
            };
            _close.FlatAppearance.BorderSize = 0;
            _close.Click += (s, e) => Close();
            _header.Controls.Add(_title);
            _header.Controls.Add(_close);
            ApplyMetrics();   // header height, title/close fonts, title inset, close width — at _dpi

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = ThemeHelper.PaletteFor(dark).PinnedBg,
                Padding = new Padding(0)
            };
            _list.Resize += (s, e) => SizeRows();

            Controls.Add(_list);
            Controls.Add(_header);
            ScrollbarTheme.Apply(_list, dark);
            TelegArm.UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan the pinned-messages list (RT touch)
        }

        /// <summary>Step 3.3 — SETS the header's metrics from their logical values at <see cref="_dpi"/> (never multiplies).</summary>
        private void ApplyMetrics()
        {
            _header.Height = Px(38);
            _title.Font = FontHelper.UiAt(10f, _dpi, FontStyle.Bold);   // shared: never dispose
            _title.Padding = new Padding(Px(14), 0, 0, 0);
            _close.Width = Px(40);
            _close.Font = FontHelper.UiAt(11f, _dpi);                  // shared: never dispose
        }

        /// <summary>Step 3.3 — WM_DPICHANGED (per-monitor only; inert while the process is system-aware). Swallowed —
        /// Result 0, no base (WinForms 4.7 must not scale a plain Form), never the suggested rectangle — and the popup
        /// re-lays itself out at the new effective DPI, posted (idempotent: a repeat for the same DPI is a no-op).</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WindowDpi.WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                int d = Dpi.Effective((int)(m.WParam.ToInt64() & 0xFFFF));
                if (d > 0 && d != _dpi)
                    try { BeginInvoke((Action)(() => Relayout(d))); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>Re-SETS every metric at <paramref name="dpi"/>: the header, each row's margin and (through the row's own
        /// IDpiAware) its height, the row widths, and the popup's size — keeping its top-right corner (it opens under the
        /// pinned bar's right end), clamped into its screen's work area (physical screen maths: raw).</summary>
        private void Relayout(int dpi)
        {
            if (IsDisposed || dpi <= 0 || dpi == _dpi) return;
            int old = _dpi;
            _dpi = dpi;
            SuspendLayout();
            ApplyMetrics();
            foreach (Control c in _list.Controls)
            {
                c.Margin = new Padding(Px(RowMargin));
                var a = c as IDpiAware;
                if (a != null) a.OnDpiChanged(old, dpi);   // PinnedRowControl SETS its height from its logical value
            }
            int w = Px(DesignW), h = Px(DesignH);
            var wa = Screen.FromControl(this).WorkingArea;
            Bounds = new Rectangle(Math.Max(wa.Left, Math.Min(Right - w, wa.Right - w)),
                                   Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h)), w, h);
            ResumeLayout();
            SizeRows();
            Invalidate(true);
        }

        /// <summary>Esc closes the popup.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>(Re)builds the list from the ordered pinned set.</summary>
        public void SetPins(List<(int id, string label, string preview)> rows)
        {
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Dispose();
            _list.Controls.Clear();
            if (rows != null)
                foreach (var r in rows)
                {
                    // Margin: the default 3 px, made logical (SizeRows sizes the row by it). Once parented the row adopts
                    // this popup's DPI (Dpi.Of -> IDpiHost) and sets its own height.
                    var row = new PinnedRowControl(r.id, r.label, r.preview) { AccentColor = _accent, IsDark = _dark, Margin = new Padding(Px(RowMargin)) };
                    row.Clicked += id => JumpRequested?.Invoke(id);
                    row.ContextRequested += (id, pt) => ContextRequested?.Invoke(id, pt);
                    _list.Controls.Add(row);
                }
            _list.ResumeLayout();
            SizeRows();
        }

        /// <summary>Each row exactly fills the list's client width, less its own margins.
        /// Step 3.3 — DELIBERATE CHANGE AT 96 DPI. FACT (a WinForms probe of this exact layout — 380-wide Dock-Fill
        /// FlowLayoutPanel, TopDown, AutoScroll, 54-px rows with the default 3 px margins): ClientSize ALREADY EXCLUDES a
        /// visible native vertical bar (380 -> 363 with the 17-px bar), so the 1.x "- VerticalScrollBarWidth when visible"
        /// took it off twice (rows 346 wide: a 14-px dead strip), and with no bar the rows plus their 3 + 3 px margins were
        /// 6 px wider than the client and raised a HORIZONTAL scrollbar. "Client width - margins" measured clean both ways
        /// (374 / 357, no horizontal bar). No system metric is read: the client area already is whatever width Windows drew
        /// the native bar at (which per-monitor DPI does not scale).</summary>
        private void SizeRows()
        {
            int cw = _list.ClientSize.Width;
            foreach (Control c in _list.Controls) c.Width = Math.Max(Px(MinRowW), cw - c.Margin.Horizontal);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(ThemeHelper.PaletteFor(_dark).PinnedDivider))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);   // subtle border for the borderless popup
        }
    }
}
