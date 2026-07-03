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
    /// </summary>
    public sealed class DownloadIndicator : Control
    {
        private readonly TelegramService _service;
        private readonly Timer _tick;         // smooth ring while transfers run (paint-only; state via events)
        private bool _dark;

        public DownloadIndicator(TelegramService service)
        {
            _service = service;
            Size = new Size(40, 40);
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : (_dark ? Color.FromArgb(38, 38, 42) : Color.White));
            var all = _service.SnapshotTransfers().Where(t => t.PanelVisible).ToArray();   // badge/ring = visible rows only
            long got = 0, total = 0; int activeOrPaused = 0;
            foreach (var t in all)
            {
                if (t.Handle == null) continue;
                if (t.Handle.State == DownloadHandle.DState.Downloading || t.Handle.State == DownloadHandle.DState.Paused)
                { got += t.Handle.Transmitted; total += t.Handle.Total; activeOrPaused++; }
            }
            float frac = total > 0 ? Math.Min(1f, (float)got / total) : (all.Length > 0 && activeOrPaused == 0 ? 1f : 0f);
            var accent = ThemeHelper.GetWindowsAccentColor();
            var track = _dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(205, 205, 210);
            var rect = new Rectangle(7, 7, 26, 26);
            using (var pen = new Pen(track, 3f)) g.DrawEllipse(pen, rect);
            if (frac > 0)
                using (var pen = new Pen(accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rect, -90, frac * 360f);
            // down-arrow glyph
            var fg = _dark ? Color.FromArgb(225, 225, 230) : Color.FromArgb(60, 60, 66);
            using (var pen = new Pen(fg, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = 20, cy = 20;
                g.DrawLine(pen, cx, cy - 5, cx, cy + 3);
                g.DrawLine(pen, cx - 3.5f, cy - 0.5f, cx, cy + 3);
                g.DrawLine(pen, cx + 3.5f, cy - 0.5f, cx, cy + 3);
            }
            if (activeOrPaused > 0)   // badge count
            {
                string n = activeOrPaused.ToString();
                using (var bb = new SolidBrush(accent))
                    g.FillEllipse(bb, Width - 16, 0, 15, 15);
                using (var f = FontHelper.Ui(7f, FontStyle.Bold))
                    TextRenderer.DrawText(g, n, f, new Rectangle(Width - 16, 0, 15, 15), Color.White,
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
    /// </summary>
    public sealed class DownloadsPanel : Form
    {
        private readonly TelegramService _service;
        private readonly bool _dark;
        private readonly Color _accent;
        private readonly FlowLayoutPanel _list;
        private readonly List<Row> _rows = new List<Row>();

        public DownloadsPanel(TelegramService service, bool dark, Color accent)
        {
            _service = service; _dark = dark; _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Size = new Size(420, 380);
            BackColor = dark ? Color.FromArgb(40, 40, 43) : Color.FromArgb(248, 248, 250);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += (s, e) => { try { Close(); } catch { } };
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);

            var header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = dark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(236, 236, 240) };
            header.Controls.Add(MakeLabel("Downloads", 12, 10, 120, true));
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

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = header.BackColor };
            var clear = HeaderButton("Clear finished", Pic.Clear, false, 0);
            clear.Size = new Size(126, 30); clear.Left = Width - 12 - 126; clear.Top = 5;   // wider for the longer label
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

        private Label MakeLabel(string text, int x, int y, int w, bool bold)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, 22),
                ForeColor = _dark ? Color.FromArgb(230, 230, 235) : Color.FromArgb(40, 40, 45),
                Font = FontHelper.Ui(bold ? 10f : 8.5f, bold ? FontStyle.Bold : FontStyle.Regular)
            };
        }

        private PillButton HeaderButton(string text, Pic glyph, bool primary, int slotFromRight)
        {
            return new PillButton(text, glyph, primary, _dark, _accent)
            {
                Size = new Size(88, 30),
                Location = new Point(Width - 12 - 88 - slotFromRight * 94, 6)
            };
        }

        // ── Rounded, themed "pill" buttons with GDI+ glyphs (matches the accent Start button) ──
        private enum Pic { None, Play, Pause, Cancel, Clear }

        private static readonly Color CancelTint = Color.FromArgb(228, 100, 100);   // destructive glyph tint

        private static Color Lighten(Color c, int by = 22)
            => Color.FromArgb(Math.Min(255, c.R + by), Math.Min(255, c.G + by), Math.Min(255, c.B + by));

        /// <summary>Draws a rounded pill (accent-filled if primary, else a themed pill) with a centered
        /// glyph+label group. Shared by the header/footer PillButtons and the per-row buttons.</summary>
        private static void DrawPill(Graphics g, Rectangle r, string text, Pic glyph, bool primary, bool hover, bool dark, Color accent)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, fg;
            if (primary) { fill = hover ? Lighten(accent) : accent; fg = Color.White; }
            else
            {
                fill = dark ? Color.FromArgb(hover ? 78 : 64, hover ? 78 : 64, hover ? 84 : 70)
                            : Color.FromArgb(hover ? 214 : 228, hover ? 214 : 228, hover ? 220 : 234);
                fg = dark ? Color.FromArgb(228, 228, 233) : Color.FromArgb(45, 45, 50);
            }
            Color gc = primary ? Color.White : (glyph == Pic.Cancel ? CancelTint : accent);
            using (var b = new SolidBrush(fill))
            using (var path = DrawHelper.RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), (r.Height - 1) / 2))
                g.FillPath(b, path);

            int glyphW = 13, gap = 5;
            using (var f = FontHelper.Ui(8f, primary ? FontStyle.Bold : FontStyle.Regular))
            {
                int tw = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, f).Width;
                int groupW = (glyph == Pic.None ? 0 : glyphW + gap) + tw;
                int left = r.X + Math.Max(6, (r.Width - groupW) / 2);
                if (glyph != Pic.None)
                {
                    DrawGlyph(g, glyph, left + glyphW / 2f, r.Y + r.Height / 2f, gc, glyphW);
                    left += glyphW + gap;
                }
                if (tw > 0)
                    TextRenderer.DrawText(g, text, f, new Rectangle(left, r.Y, tw + 4, r.Height), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private static void DrawGlyph(Graphics g, Pic glyph, float cx, float cy, Color color, int size)
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
                    using (var p = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(p, cx - s * 0.7f, cy - s * 0.7f, cx + s * 0.7f, cy + s * 0.7f);
                        g.DrawLine(p, cx + s * 0.7f, cy - s * 0.7f, cx - s * 0.7f, cy + s * 0.7f);
                    }
                    break;
                case Pic.Clear:   // small trash can
                    using (var p = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        float w = s * 1.3f, top = cy - s * 0.85f, bot = cy + s;
                        g.DrawLine(p, cx - w * 0.62f, top, cx + w * 0.62f, top);                 // lid
                        g.DrawLine(p, cx - w * 0.22f, top - s * 0.32f, cx + w * 0.22f, top - s * 0.32f); // handle
                        g.DrawLines(p, new[] {                                                   // body
                            new PointF(cx - w * 0.46f, top + 1),
                            new PointF(cx - w * 0.36f, bot),
                            new PointF(cx + w * 0.36f, bot),
                            new PointF(cx + w * 0.46f, top + 1) });
                    }
                    break;
            }
        }

        /// <summary>An owner-drawn rounded pill button (header/footer). Hover-aware; raises Click as normal.</summary>
        private sealed class PillButton : Control
        {
            private readonly Pic _glyph;
            private readonly bool _primary, _dark;
            private readonly Color _accent;
            private bool _hover;

            public PillButton(string text, Pic glyph, bool primary, bool dark, Color accent)
            {
                Text = text; _glyph = glyph; _primary = primary; _dark = dark; _accent = accent;
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
                g.Clear(Parent != null ? Parent.BackColor : (_dark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(236, 236, 240)));
                DrawPill(g, new Rectangle(0, 0, Width, Height), Text, _glyph, _primary, _hover, _dark, _accent);
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
                empty.Margin = new Padding(16);
                _list.Controls.Add(empty);
            }
            else foreach (var t in all)
            {
                var row = new Row(_service, t, _dark, _accent) { Width = _list.ClientSize.Width - 6 };
                _rows.Add(row);
                _list.Controls.Add(row);
            }
            _list.ResumeLayout();
        }

        /// <summary>One transfer row: name/chat/progress + Pause|Resume|Cancel (or tap-to-open when done).</summary>
        private sealed class Row : Control
        {
            private readonly TelegramService _svc;
            private readonly TelegramService.TransferInfo _ti;
            private readonly bool _dark;
            private readonly Color _accent;
            private int _tick;

            public Row(TelegramService svc, TelegramService.TransferInfo ti, bool dark, Color accent)
            {
                _svc = svc; _ti = ti; _dark = dark; _accent = accent;
                Height = 58;
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
            private Rectangle Btn1 { get { return new Rectangle(Width - 154, 14, 70, 30); } }
            private Rectangle Btn2 { get { return new Rectangle(Width - 78, 14, 64, 30); } }

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
                g.Clear(_dark ? Color.FromArgb(48, 48, 52) : Color.White);
                var fg = _dark ? Color.FromArgb(228, 228, 233) : Color.FromArgb(40, 40, 45);
                var sub = _dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(120, 120, 126);
                var st = St;
                var h = _ti.Handle;

                string name = _ti.FileName ?? ("doc " + _ti.DocId);
                using (var f = FontHelper.Ui(9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, name, f, new Rectangle(10, 6, Width - 170, 18), fg,
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
                using (var f = FontHelper.Ui(7.5f))
                    TextRenderer.DrawText(g, line, f, new Rectangle(10, 24, Width - 170, 14), sub,
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // progress bar
                float frac = h != null ? h.Fraction : 0f;
                if (st == DownloadHandle.DState.Done) frac = 1f;
                var bar = new Rectangle(10, 44, Width - 170, 5);
                using (var bb = new SolidBrush(_dark ? Color.FromArgb(70, 70, 76) : Color.FromArgb(215, 215, 220))) g.FillRectangle(bb, bar);
                if (frac > 0)
                    using (var ab = new SolidBrush(st == DownloadHandle.DState.Paused ? sub : _accent))
                        g.FillRectangle(ab, new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height));

                // buttons — rounded, themed pills with glyphs (Resume = accent/primary, like the Start button)
                if (st == DownloadHandle.DState.Downloading || st == DownloadHandle.DState.Paused
                    || st == DownloadHandle.DState.Failed || st == DownloadHandle.DState.Cancelled)
                {
                    bool resume = st != DownloadHandle.DState.Downloading;
                    DrawPill(g, Btn1, resume ? "Resume" : "Pause", resume ? Pic.Play : Pic.Pause, resume, _hoverBtn == 1, _dark, _accent);
                    if (st == DownloadHandle.DState.Downloading || st == DownloadHandle.DState.Paused)
                        DrawPill(g, Btn2, "Cancel", Pic.Cancel, false, _hoverBtn == 2, _dark, _accent);
                }
                using (var pen = new Pen(_dark ? Color.FromArgb(60, 60, 66) : Color.FromArgb(228, 228, 233)))
                    g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }
}
