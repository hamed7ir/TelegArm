using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// DOWNLOAD-UX Part 4: the header transfers indicator — a small circular aggregate-progress button with a
    /// badge count, visible only while the session has tracked transfers. Theme-aware (ThemeChanged), 40px
    /// touch target. Tap → the DownloadsPanel popup. A consumer of TelegramService roster state only.
    ///
    /// Redesign step 3.2: every length is logical (Px / F at the indicator's DPI), the badge font is the shared UiAt
    /// font (never disposed), and the 40 px size is SET from the logical value when parented and on a DPI change.
    /// At 96 DPI every value is exactly what it was.
    ///
    /// ALIGNED WITH THE HEADER BUTTONS (owner report, 2026-10-14): it is docked Right in the chat header like the ⋮, the
    /// magnifier and the dock toggle, so docking stretches it to the header's full height — but it drew its ring at a
    /// fixed (7, 7) of a 40 x 40 box, ~8 px above the others' glyph centre, and was 40 wide against their 44. It is now
    /// 44 wide like them and paints the ring CENTRED in whatever box it is given (the badge keeps its place on the ring).
    /// </summary>
    public sealed class DownloadIndicator : Control, IDpiAware
    {
        private const int Side = 44;   // logical px — the header buttons' width (MainForm.Layout: Width = Px(44))
        private readonly TelegramService _service;
        private readonly Timer _tick;         // smooth ring while transfers run (paint-only; state via events)
        private bool _dark;
        private int _dpi;                     // the DPI this indicator is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public DownloadIndicator(TelegramService service)
        {
            _service = service;
            Size = new Size(Dpi.Px(Side, CurDpi), Dpi.Px(Side, CurDpi));
            Cursor = Cursors.Hand;
            Visible = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _dark = ThemeHelper.IsDark;
            ThemeHelper.ThemeChanged += OnTheme;
            _service.TransfersChanged += OnTransfers;
            Disposed += (s, e) =>
            {
                ThemeHelper.ThemeChanged -= OnTheme;
                _service.TransfersChanged -= OnTransfers;
                _tick.Stop(); _tick.Dispose();
            };
            _tick = new Timer { Interval = 250 };
            _tick.Tick += (s, e) => Invalidate();
            OnTransfers();
        }

        private void OnTheme() { _dark = ThemeHelper.IsDark; if (!IsDisposed) Invalidate(); }

        /// <summary>Built before it is added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Size = new Size(Dpi.Px(Side, d), Dpi.Px(Side, d)); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Size = new Size(Dpi.Px(Side, newDpi), Dpi.Px(Side, newDpi));   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
        }

        private void OnTransfers()
        {
            if (IsDisposed || !IsHandleCreated) { Refresh0(); return; }
            try { BeginInvoke((Action)Refresh0); } catch { }
        }

        private void Refresh0()
        {
            if (IsDisposed) return;
            var all = _service.SnapshotTransfers().Where(t => t.PanelVisible).ToArray();   // v3 1.2: hidden auto-downloads don't surface
            bool show = all.Length > 0;
            bool active = all.Any(t => t.Handle != null && t.Handle.State == DownloadHandle.DState.Downloading);
            Visible = show;
            if (active && !_tick.Enabled) _tick.Start();
            else if (!active && _tick.Enabled) _tick.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            float F(float v) => Dpi.F(v, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : (ThemeHelper.PaletteFor(_dark).DownloadBtnBg));
            var all = _service.SnapshotTransfers().Where(t => t.PanelVisible).ToArray();   // badge/ring = visible rows only
            long got = 0, total = 0; int activeOrPaused = 0;
            foreach (var t in all)
            {
                if (t.Handle == null) continue;
                if (t.Handle.State == DownloadHandle.DState.Downloading || t.Handle.State == DownloadHandle.DState.Paused)
                { got += t.Handle.Transmitted; total += t.Handle.Total; activeOrPaused++; }
            }
            float frac = total > 0 ? Math.Min(1f, (float)got / total) : (all.Length > 0 && activeOrPaused == 0 ? 1f : 0f);
            var accent = ThemeHelper.Palette.Accent;
            var track = ThemeHelper.PaletteFor(_dark).DownloadRingTrack;
            int ring = P(26);   // centred in the control: docked, it is as tall as the header (see the class notes)
            var rect = new Rectangle((Width - ring) / 2, (Height - ring) / 2, ring, ring);
            using (var pen = new Pen(track, F(3f))) g.DrawEllipse(pen, rect);
            if (frac > 0)
                using (var pen = new Pen(accent, F(3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rect, -90, frac * 360f);
            // down-arrow glyph
            var fg = ThemeHelper.PaletteFor(_dark).DownloadBtnGlyph;
            using (var pen = new Pen(fg, F(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;   // the ring's centre
                g.DrawLine(pen, cx, cy - F(5), cx, cy + F(3));
                g.DrawLine(pen, cx - F(3.5f), cy - F(0.5f), cx, cy + F(3));
                g.DrawLine(pen, cx + F(3.5f), cy - F(0.5f), cx, cy + F(3));
            }
            if (activeOrPaused > 0)   // badge count
            {
                string n = activeOrPaused.ToString();
                var badge = new Rectangle(rect.Right - P(9), rect.Top - P(7), P(15), P(15));   // top-right of the ring, as before
                using (var bb = new SolidBrush(accent))
                    g.FillEllipse(bb, badge);
                TextRenderer.DrawText(g, n, FontHelper.UiAt(7f, dpi, FontStyle.Bold), badge, Color.White,   // shared font: never dispose
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>
    /// DOWNLOAD-UX Part 4: the downloads-manager popup. Rows = filename, chat, progress, size, per-row
    /// Pause|Resume|Cancel; header Pause/Resume/Cancel all + Clear finished; completed rows tap-to-open.
    /// Dismisses on outside tap (Deactivate) and Esc — the EmojiPicker popup pattern. A pure CONSUMER of
    /// TelegramService roster state: it lists transfers whose chats are closed. Rows subscribe to their
    /// handle's Changed and unsubscribe on close (E-3).
    ///
    /// Redesign step 3.3: a plain-Form popup laid out at the DPI of WHERE IT OPENS (the caller passes Dpi.ForPopup), sized
    /// before Show, and an IDpiHost so anything inside that asks Dpi.Of gets that DPI. Every length is LOGICAL through Px
    /// at that DPI; the rows and pills take it explicitly. Fonts are shared FontHelper.UiAt fonts (never disposed). On
    /// WM_DPICHANGED it closes (swallowed, no base) like the emoji popup: it already closes on any outside tap, and the
    /// next open is sized where it opens. At 96 DPI every value is exactly what it was.
    /// </summary>
    public sealed class DownloadsPanel : Form, IDpiHost
    {
        private readonly TelegramService _service;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly FlowLayoutPanel _list;
        private readonly List<Row> _rows = new List<Row>();
        private readonly int _dpi;   // step 3.3: the DPI this popup lays out at (where it opened); it closes rather than rescale

        int IDpiHost.Dpi { get { return _dpi; } }

        private int Px(int n) { return Dpi.Px(n, _dpi); }

        /// <param name="dpi">Step 3.3 — the effective DPI where the panel opens: the caller's Dpi.ForPopup(anchor).
        /// 0 = <see cref="Dpi.DefaultDpi"/>.</param>
        public DownloadsPanel(TelegramService service, bool dark, Color accent, int dpi)
        {
            _service = service; _dark = dark; _accent = accent;
            _dpi = dpi > 0 ? dpi : Dpi.DefaultDpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Size = new Size(Px(420), Px(380));   // sized at its DPI BEFORE Show: the caller positions it by this size
            BackColor = ThemeHelper.PaletteFor(dark).DownloadsBg;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += (s, e) => { try { Close(); } catch { } };
            TelegArm.Helpers.AppIcon.Set(this);

            var header = new Panel { Dock = DockStyle.Top, Height = Px(42), BackColor = ThemeHelper.PaletteFor(dark).DownloadsHeaderBg };
            header.Controls.Add(MakeLabel("Downloads", 12, 10, 120, true));   // logical x, y, width (MakeLabel scales them)
            var btnCancelAll = HeaderButton("Cancel all", Pic.Cancel, false, 0);
            btnCancelAll.Click += (s, e) =>
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[DLMGR] cancel-all");
                foreach (var t in _service.SnapshotTransfers()) _service.CancelTransfer(t.DocId);
            };
            var btnResumeAll = HeaderButton("Resume all", Pic.Play, true, 1);
            btnResumeAll.Click += (s, e) =>
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[DLMGR] resume-all");
                foreach (var t in _service.SnapshotTransfers())
                    if (t.Handle != null && (t.Handle.State == DownloadHandle.DState.Paused || t.Handle.State == DownloadHandle.DState.Failed))
                        _service.ResumeDownload(t.DocId);
            };
            var btnPauseAll = HeaderButton("Pause all", Pic.Pause, false, 2);
            btnPauseAll.Click += (s, e) =>
            {
                if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[DLMGR] pause-all");
                foreach (var t in _service.SnapshotTransfers()) _service.PauseDownload(t.DocId);
            };
            header.Controls.Add(btnCancelAll); header.Controls.Add(btnResumeAll); header.Controls.Add(btnPauseAll);
            Controls.Add(header);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = Px(36), BackColor = header.BackColor };
            var clear = HeaderButton("Clear finished", Pic.Clear, false, 0);
            clear.Size = new Size(Px(126), Px(30)); clear.Left = Width - Px(12) - Px(126); clear.Top = Px(5);   // wider for the longer label
            clear.Click += (s, e) => _service.ClearFinishedTransfers();
            footer.Controls.Add(clear);
            Controls.Add(footer);

            _list = new Controls.NoNativeScrollFlowPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = BackColor
            };
            Controls.Add(_list);
            _list.BringToFront();
            UI.Controls.TouchScroller.Enable(_list, horizontal: false);   // finger-pan on RT

            _service.TransfersChanged += OnTransfers;
            FormClosed += (s, e) =>
            {
                _service.TransfersChanged -= OnTransfers;
                foreach (var r in _rows) r.Detach();   // E-3: unsubscribe every row's handle
            };
            Rebuild();
            if (Logger.Enabled) System.Diagnostics.Debug.WriteLine("[DLMGR] open n=" + _rows.Count);
        }

        /// <summary><paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/> are LOGICAL; scaled here.</summary>
        private Label MakeLabel(string text, int x, int y, int w, bool bold)
        {
            return new Label
            {
                Text = text, Location = new Point(Px(x), Px(y)), AutoSize = false, Size = new Size(Px(w), Px(22)),
                ForeColor = ThemeHelper.PaletteFor(_dark).DownloadsHeaderText,
                Font = FontHelper.UiAt(bold ? 10f : 8.5f, _dpi, bold ? FontStyle.Bold : FontStyle.Regular)   // shared: never dispose
            };
        }

        private PillButton HeaderButton(string text, Pic glyph, bool primary, int slotFromRight)
        {
            return new PillButton(text, glyph, primary, _dark, _accent, _dpi)
            {
                Size = new Size(Px(88), Px(30)),
                Location = new Point(Width - Px(12) - Px(88) - slotFromRight * Px(94), Px(6))   // Width is already Px(420)
            };
        }

        /// <summary>Step 3.3 — WM_DPICHANGED (per-monitor only; inert while the process is system-aware). Swallowed —
        /// Result 0, no base (WinForms must not scale a plain Form), never the suggested rectangle — and the popup closes,
        /// as it does on any outside tap; the next open is built at the DPI of where it opens.</summary>
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WindowDpi.WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                try { Close(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        // ── Rounded, themed "pill" buttons with GDI+ glyphs (matches the accent Start button) ──
        private enum Pic { None, Play, Pause, Cancel, Clear }

        private static Color Lighten(Color c, int by = 22)
            => ColorMath.Rgb(Math.Min(255, c.R + by), Math.Min(255, c.G + by), Math.Min(255, c.B + by));

        /// <summary>Draws a rounded pill (accent-filled if primary, else a themed pill) with a centered
        /// glyph+label group. Shared by the header/footer PillButtons and the per-row buttons.
        /// Step 3.3: the glyph box, gaps and font are LOGICAL at <paramref name="dpi"/> (the popup's); the pill's own shape
        /// comes from <paramref name="r"/>, which the caller has already scaled.</summary>
        private static void DrawPill(Graphics g, Rectangle r, string text, Pic glyph, bool primary, bool hover, bool dark, Color accent, int dpi)
        {
            int P(int v) => Dpi.Px(v, dpi);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, fg;
            if (primary) { fill = hover ? Lighten(accent) : accent; fg = Color.White; }
            else
            {
                fill = hover ? ThemeHelper.PaletteFor(dark).DownloadPillHover : ThemeHelper.PaletteFor(dark).DownloadPill;
                fg = ThemeHelper.PaletteFor(dark).DownloadPillText;
            }
            Color gc = primary ? Color.White : (glyph == Pic.Cancel ? ThemeHelper.PaletteFor(dark).DownloadCancelTint : accent);
            using (var b = new SolidBrush(fill))
            using (var path = DrawHelper.RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), (r.Height - 1) / 2))
                g.FillPath(b, path);

            int glyphW = P(13), gap = P(5);
            var f = FontHelper.UiAt(8f, dpi, primary ? FontStyle.Bold : FontStyle.Regular);   // shared: never dispose
            int tw = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, f).Width;
            int groupW = (glyph == Pic.None ? 0 : glyphW + gap) + tw;
            int left = r.X + Math.Max(P(6), (r.Width - groupW) / 2);
            if (glyph != Pic.None)
            {
                DrawGlyph(g, glyph, left + glyphW / 2f, r.Y + r.Height / 2f, gc, glyphW, dpi);
                left += glyphW + gap;
            }
            if (tw > 0)
                TextRenderer.DrawText(g, text, f, new Rectangle(left, r.Y, tw + P(4), r.Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>The glyph's shape scales from <paramref name="size"/> (already scaled); the stroke widths and the trash
        /// can's 1 px lid offset are LOGICAL at <paramref name="dpi"/>.</summary>
        private static void DrawGlyph(Graphics g, Pic glyph, float cx, float cy, Color color, int size, int dpi)
        {
            float s = size / 2f;
            switch (glyph)
            {
                case Pic.Play:
                    using (var b = new SolidBrush(color))
                        g.FillPolygon(b, new[] {
                            new PointF(cx - s * 0.62f, cy - s * 0.9f),
                            new PointF(cx - s * 0.62f, cy + s * 0.9f),
                            new PointF(cx + s * 0.95f, cy) });
                    break;
                case Pic.Pause:
                    using (var b = new SolidBrush(color))
                    {
                        g.FillRectangle(b, cx - s * 0.72f, cy - s * 0.9f, s * 0.5f, s * 1.8f);
                        g.FillRectangle(b, cx + s * 0.22f, cy - s * 0.9f, s * 0.5f, s * 1.8f);
                    }
                    break;
                case Pic.Cancel:
                    using (var p = new Pen(color, Dpi.F(2f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(p, cx - s * 0.7f, cy - s * 0.7f, cx + s * 0.7f, cy + s * 0.7f);
                        g.DrawLine(p, cx + s * 0.7f, cy - s * 0.7f, cx - s * 0.7f, cy + s * 0.7f);
                    }
                    break;
                case Pic.Clear:   // small trash can
                    using (var p = new Pen(color, Dpi.F(1.5f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        float w = s * 1.3f, top = cy - s * 0.85f, bot = cy + s, one = Dpi.F(1f, dpi);
                        g.DrawLine(p, cx - w * 0.62f, top, cx + w * 0.62f, top);                 // lid
                        g.DrawLine(p, cx - w * 0.22f, top - s * 0.32f, cx + w * 0.22f, top - s * 0.32f); // handle
                        g.DrawLines(p, new[] {                                                   // body
                            new PointF(cx - w * 0.46f, top + one),
                            new PointF(cx - w * 0.36f, bot),
                            new PointF(cx + w * 0.36f, bot),
                            new PointF(cx + w * 0.46f, top + one) });
                    }
                    break;
            }
        }

        /// <summary>An owner-drawn rounded pill button (header/footer). Hover-aware; raises Click as normal.
        /// Step 3.3: drawn at the popup's DPI, passed in (the panel closes rather than rescale, so it never changes).</summary>
        private sealed class PillButton : Control
        {
            private readonly Pic _glyph;
            private readonly bool _primary, _dark;
            private readonly Color _accent;
            private readonly int _dpi;
            private bool _hover;

            public PillButton(string text, Pic glyph, bool primary, bool dark, Color accent, int dpi)
            {
                Text = text; _glyph = glyph; _primary = primary; _dark = dark; _accent = accent; _dpi = dpi;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                         | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent != null ? Parent.BackColor : (ThemeHelper.PaletteFor(_dark).DownloadsHeaderBg));
                DrawPill(g, new Rectangle(0, 0, Width, Height), Text, _glyph, _primary, _hover, _dark, _accent, _dpi);
            }
        }

        private void OnTransfers()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)Rebuild); } catch { }
        }

        private void Rebuild()
        {
            if (IsDisposed) return;
            foreach (var r in _rows) r.Detach();
            _rows.Clear();
            _list.SuspendLayout();
            foreach (Control c in _list.Controls.OfType<Control>().ToArray()) { _list.Controls.Remove(c); c.Dispose(); }
            var all = _service.SnapshotTransfers()
                .Where(t => t.PanelVisible)   // v3 1.2: small policy auto-downloads stay off the panel
                .OrderByDescending(t => t.Handle != null && t.Handle.State == DownloadHandle.DState.Downloading)
                .ToArray();
            if (all.Length == 0)
            {
                var empty = MakeLabel("No active downloads", 16, 16, 300, false);
                empty.Margin = new Padding(Px(16));
                _list.Controls.Add(empty);
            }
            else foreach (var t in all)
            {
                var row = new Row(_service, t, _dark, _accent, _dpi);
                row.Width = _list.ClientSize.Width - row.Margin.Horizontal;   // 1.x: "- 6", the default 3 + 3 margin (Row sets it at its DPI)
                _rows.Add(row);
                _list.Controls.Add(row);
            }
            _list.ResumeLayout();
        }

        /// <summary>One transfer row: name/chat/progress + Pause|Resume|Cancel (or tap-to-open when done).
        /// Step 3.3: every length LOGICAL through P at the popup's DPI, passed in (the panel closes rather than rescale);
        /// fonts are shared UiAt fonts (never disposed). At 96 DPI every value is exactly what it was.</summary>
        private sealed class Row : Control
        {
            private readonly TelegramService _svc;
            private readonly TelegramService.TransferInfo _ti;
            private readonly bool _dark;
            private readonly Color _accent;
            private readonly int _dpi;
            private int _tick;

            private int P(int v) { return Dpi.Px(v, _dpi); }

            public Row(TelegramService svc, TelegramService.TransferInfo ti, bool dark, Color accent, int dpi)
            {
                _svc = svc; _ti = ti; _dark = dark; _accent = accent; _dpi = dpi;
                Height = P(58);
                Margin = new Padding(P(3));   // the default 3 px margin, made logical (the panel sizes the row by it)
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                if (_ti.Handle != null) _ti.Handle.Changed += OnChanged;
            }

            public void Detach() { if (_ti.Handle != null) _ti.Handle.Changed -= OnChanged; }

            private void OnChanged(DownloadHandle h)
            {
                int now = Environment.TickCount;
                if (h.State == DownloadHandle.DState.Downloading && now - _tick < 200) return;
                _tick = now;
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)(() => { if (!IsDisposed) Invalidate(); })); } catch { }
            }

            private DownloadHandle.DState St
            {
                get { return _ti.Handle != null ? _ti.Handle.State : DownloadHandle.DState.Failed; }
            }

            // Button zones (right-aligned): [pause/resume] [cancel]
            private int _hoverBtn;   // 0 none, 1 = Btn1, 2 = Btn2
            private Rectangle Btn1 { get { return new Rectangle(Width - P(154), P(14), P(70), P(30)); } }
            private Rectangle Btn2 { get { return new Rectangle(Width - P(78), P(14), P(64), P(30)); } }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = Btn1.Contains(e.Location) ? 1 : Btn2.Contains(e.Location) ? 2 : 0;
                if (h != _hoverBtn) { _hoverBtn = h; Cursor = h != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hoverBtn != 0) { _hoverBtn = 0; Invalidate(); }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                var st = St;
                if (Btn2.Contains(e.Location) && (st == DownloadHandle.DState.Downloading || st == DownloadHandle.DState.Paused))
                { _svc.CancelTransfer(_ti.DocId); return; }
                if (Btn1.Contains(e.Location))
                {
                    if (st == DownloadHandle.DState.Downloading) { _svc.PauseDownload(_ti.DocId); return; }
                    if (st == DownloadHandle.DState.Paused || st == DownloadHandle.DState.Failed || st == DownloadHandle.DState.Cancelled)
                    { _svc.ResumeDownload(_ti.DocId); return; }
                }
                if (st == DownloadHandle.DState.Done)   // tap-to-open the finished file
                {
                    try { if (System.IO.File.Exists(_ti.Path)) System.Diagnostics.Process.Start(_ti.Path); } catch { }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(ThemeHelper.PaletteFor(_dark).DownloadRowBg);
                var fg = ThemeHelper.PaletteFor(_dark).DownloadRowText;
                var sub = ThemeHelper.PaletteFor(_dark).DownloadRowSub;
                var st = St;
                var h = _ti.Handle;

                string name = _ti.FileName ?? ("doc " + _ti.DocId);
                TextRenderer.DrawText(g, name, FontHelper.UiAt(9f, _dpi, FontStyle.Bold), new Rectangle(P(10), P(6), Width - P(170), P(18)), fg,   // shared font: never dispose
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                string status = st == DownloadHandle.DState.Downloading ? "downloading"
                              : st == DownloadHandle.DState.Paused ? "paused"
                              : st == DownloadHandle.DState.Done ? "done — tap to open"
                              : st == DownloadHandle.DState.Cancelled ? "cancelled" : "failed — tap ▶ to retry";
                // ‎ (LRM) pins the size run LTR — an RTL chat title before it otherwise bidi-reorders
                // the "x MB / y MB" segment into garbled glyph soup (DOWNLOAD-RESUME Part 3, artifact fix).
                string line = (_ti.ChatTitle != null ? _ti.ChatTitle + "‎  •  " : "")
                            + (h != null && h.Total > 0 ? "‎" + DrawHelper.FormatSize(h.Transmitted) + " / " + DrawHelper.FormatSize(h.Total) + "  •  " : "")
                            + status;
                TextRenderer.DrawText(g, line, FontHelper.UiAt(7.5f, _dpi), new Rectangle(P(10), P(24), Width - P(170), P(14)), sub,   // shared font: never dispose
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // progress bar
                float frac = h != null ? h.Fraction : 0f;
                if (st == DownloadHandle.DState.Done) frac = 1f;
                var bar = new Rectangle(P(10), P(44), Width - P(170), P(5));
                using (var bb = new SolidBrush(ThemeHelper.PaletteFor(_dark).DownloadBarTrack)) g.FillRectangle(bb, bar);
                if (frac > 0)
                    using (var ab = new SolidBrush(st == DownloadHandle.DState.Paused ? sub : _accent))
                        g.FillRectangle(ab, new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height));

                // buttons — rounded, themed pills with glyphs (Resume = accent/primary, like the Start button)
                if (st == DownloadHandle.DState.Downloading || st == DownloadHandle.DState.Paused
                    || st == DownloadHandle.DState.Failed || st == DownloadHandle.DState.Cancelled)
                {
                    bool resume = st != DownloadHandle.DState.Downloading;
                    DrawPill(g, Btn1, resume ? "Resume" : "Pause", resume ? Pic.Play : Pic.Pause, resume, _hoverBtn == 1, _dark, _accent, _dpi);
                    if (st == DownloadHandle.DState.Downloading || st == DownloadHandle.DState.Paused)
                        DrawPill(g, Btn2, "Cancel", Pic.Cancel, false, _hoverBtn == 2, _dark, _accent, _dpi);
                }
                using (var pen = new Pen(ThemeHelper.PaletteFor(_dark).DownloadRowDivider))   // a 1 px hairline at every DPI, as PinnedRowControl's
                    g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }
}
