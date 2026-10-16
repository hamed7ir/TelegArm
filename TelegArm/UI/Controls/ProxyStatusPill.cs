using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// BATCH-TA-16/P3 — the floating proxy pill (bottom-left of the login screen). Owner-painted,
    /// RT-safe, themed, focusable.
    ///
    /// ⚠ WHY THERE IS A WINDOW REGION HERE (this is the whole design, not decoration).
    /// TelegArm has already shipped this exact bug once: the floating scroll-to-bottom circle painted
    /// BLACK CORNERS over the message bubbles. WinForms "transparency" is not transparency — a control
    /// with BackColor = Transparent copies its IMMEDIATE PARENT's background, so over a SIBLING's content
    /// the corners paint an opaque box (see LESSONS_LEARNED.md:20/:130 and JumpToBottomButton.cs:53-59).
    /// The fix there, and here, is a window Region: the corners CEASE TO EXIST, and OnPaint clears with
    /// the control's OWN fill so the parent background can never show through anywhere.
    ///
    /// The pill's shape is deliberately a SINGLE FIGURE — one rounded rect, with the status dot painted
    /// INSIDE it. That is not cosmetic: Region(GraphicsPath) honours the path's FillMode, whose default
    /// (Alternate / even-odd) XORs overlapping figures and once punched a literal HOLE through the
    /// scroll button's hwnd where its badge overlapped the circle. Keeping one figure makes FillMode
    /// irrelevant.
    /// ⚠ IF THIS EVER BECOMES MULTI-FIGURE (e.g. a badge hanging outside the pill), you MUST use
    ///   `new GraphicsPath { FillMode = FillMode.Winding }` or the overlap will be cut out of the window.
    ///
    /// On the login screen the parent background is a flat colour, so strictly the Region is
    /// belt-and-braces THERE — but the same control over the chat list would be the original bug
    /// verbatim, so it is built correct once.
    ///
    /// Redesign step 3.2: the floating variant is per-window too — every length is logical (Px / F at the pill's DPI)
    /// and the caption font is the shared UiAt font (never disposed). Both variants re-measure on a DPI change and
    /// when parented, SETTING the size from the logical metrics; at 96 DPI every value is exactly what it was.
    /// </summary>
    public sealed class ProxyStatusPill : Control, IDpiAware
    {
        private ProxyConnectState _state = ProxyConnectState.Off;
        private string _detail;             // e.g. "velvory.co.uk.:443" — NEVER the secret (ProxyUrl.SafeForLog)
        private bool _viaProxy;             // wording modifier — see Caption()
        private bool _viaTunnel;            // step 6.5: the "proxy" is the WebSocket tunnel
        private bool _hover, _pressed;
        private Size _sizeForText = Size.Empty;
        private readonly ToolTip _tip = new ToolTip();
        private int _dpi;                                          // the DPI the floating pill is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        /// <summary>Accent for the "connected" dot and the focus ring. Set from ApplyTheme (the ThemeHelper
        /// accent).</summary>
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        private bool _onAccent;

        /// <summary>Redesign 2.7b: the pill sits in MainForm's accent title bar (TitleBar.Slot). It is drawn on the bar's
        /// colour (a fill lifted toward the bar's ink, no border, ink text) and sized in logical pixels at the window's
        /// DPI, shorter than the bar. The login screen keeps the floating variant.</summary>
        public bool OnAccent
        {
            get { return _onAccent; }
            set { if (_onAccent == value) return; _onAccent = value; _sizeForText = Size.Empty; AutoSizeToText(); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            _sizeForText = Size.Empty; AutoSizeToText(); Invalidate();   // SET from the logical metrics: DpiScaler already ratio-scaled the bounds
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent != null) _dpi = Dpi.Of(this);
            _sizeForText = Size.Empty; AutoSizeToText();   // the window's DPI is known once parented (both variants)
        }

        public ProxyStatusPill()
        {
            TabStop = true;                       // unlike JumpToBottomButton, this duplicates no gesture
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            AutoSizeToText();
            // BATCH-TA-16b/B1 — follow the ONE shared state that both connect paths report into
            // (MainForm's resilient resume loop and LoginForm's interactive login), so the pill can
            // never disagree with what the app is actually doing.
            ProxyStatus.Changed += OnProxyStatusChanged;
            SyncFromStatus();
        }

        private void OnProxyStatusChanged()
        {
            // ProxyStatus may be raised from a background continuation — marshal before touching the UI.
            if (IsDisposed) return;
            // ⚠ Step 6.5: the tunnel raises this from relay threads on every connection. Without a window handle
            //   InvokeRequired is FALSE on any thread, so the update (and the ToolTip) would run on a pool thread:
            //   wait for the handle instead — OnHandleCreated syncs then.
            if (!IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke((Action)SyncFromStatus);
                else SyncFromStatus();
            }
            catch { /* handle gone / form closing */ }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SyncFromStatus();   // whatever changed while there was no window to show it
        }

        private void SyncFromStatus()
        {
            if (IsDisposed) return;
            _viaProxy = ProxyStatus.ViaProxy;
            _viaTunnel = ProxyStatus.ViaTunnel;
            SetState(ProxyStatus.State, ProxyStatus.Detail);
        }

        /// <summary>Sets the state and the secret-free detail line, then resizes + repaints.</summary>
        public void SetState(ProxyConnectState state, string detail)
        {
            _state = state;
            _detail = detail;
            AutoSizeToText();
            try { _tip.SetToolTip(this, TooltipText()); } catch { }
            if (!IsDisposed) Invalidate();
        }

        public ProxyConnectState State { get { return _state; } }

        /// <summary>⚠ The SAME state means different things with and without a proxy, and the no-proxy
        /// wording has to point at the fix: someone in a country where Telegram is blocked is failing to
        /// connect DIRECTLY, and "Proxy not working" would be nonsense to them. That is the whole reason
        /// ProxyStatus carries ViaProxy.</summary>
        private string Caption()
        {
            if (_viaTunnel)
                switch (_state)
                {
                    case ProxyConnectState.Connecting: return "Connecting via tunnel…";
                    // only the route log may say "tunnelled": a main connection that fell back is direct
                    case ProxyConnectState.Connected: return WsTunnelStats.Tunnelled(ProxyStatus.MainDc) ? "Tunnel connected" : "Connected directly";
                    case ProxyConnectState.Failed: return "Tunnel not working";
                }
            switch (_state)
            {
                case ProxyConnectState.Connecting: return _viaProxy ? "Connecting via proxy…" : "Connecting…";
                case ProxyConnectState.Connected: return "Proxy connected";
                case ProxyConnectState.Failed: return _viaProxy ? "Proxy not working" : "Can't connect — add a proxy";
                default: return "Connect via proxy";
            }
        }

        /// <summary>The collapsed "you are protected" shield — only while the connection really goes the way the user chose.
        /// A tunnel that fell back to a direct connection (a DC without a gateway) keeps the wide pill and says so: the
        /// tooltip alone would be invisible on a touch screen (step 6.5 review).</summary>
        private bool ShowsShield()
        {
            return _state == ProxyConnectState.Connected && !(_viaTunnel && !WsTunnelStats.Tunnelled(ProxyStatus.MainDc));
        }

        private string TooltipText()
        {
            string d = string.IsNullOrEmpty(_detail) ? "" : "\n" + _detail;
            if (_viaTunnel)
                switch (_state)
                {
                    case ProxyConnectState.Connecting: return "Trying to reach Telegram through the WebSocket tunnel." + d;
                    case ProxyConnectState.Connected:
                        return (WsTunnelStats.Tunnelled(ProxyStatus.MainDc) ? "Connected to Telegram through the WebSocket tunnel."
                                                              : "Connected, but directly: the tunnel had no route for this DC.") + d;
                    case ProxyConnectState.Failed: return "Couldn't reach Telegram through the WebSocket tunnel.\nClick to change or turn it off." + d;
                }
            switch (_state)
            {
                case ProxyConnectState.Connecting:
                    return _viaProxy ? "Trying to reach Telegram through your proxy." + d
                                     : "Trying to reach Telegram.\nIf it's blocked on your network, click to set up a proxy.";
                case ProxyConnectState.Connected: return "Connected to Telegram through your proxy." + d;
                case ProxyConnectState.Failed:
                    return _viaProxy ? "Couldn't reach Telegram through your proxy.\nClick to change or turn it off." + d
                                     : "Couldn't reach Telegram.\nIt may be blocked on your network — click to set up a proxy.";
                default: return "Set up a proxy or the WebSocket tunnel if Telegram is blocked on your network.";
            }
        }

        /// <summary>Size is derived from FONT METRICS, never fixed pixels, so a DPI-scaled device does not
        /// clip the caption. Height keeps the ~40px touch-target bar the scroll-button work established.</summary>
        private void AutoSizeToText()
        {
            if (_onAccent) { AutoSizeOnAccent(); return; }
            int dpi = CurDpi;
            var t = TextRenderer.MeasureText(Caption(), FontHelper.UiAt(9f, dpi, FontStyle.Bold));   // shared font: never dispose
            int h = Math.Max(Dpi.Px(34, dpi), t.Height + Dpi.Px(16, dpi));
            // CONNECTED collapses to a CIRCLE with a shield — the steady state needs an at-a-glance
            // "you are protected" mark, not a sentence taking up the corner. Every transient state
            // (connecting / failed / off) keeps the wide pill, because those have something to SAY.
            // Still one figure, so the Region's FillMode rule is unaffected: a square fed to
            // RoundedRect with radius = h/2 IS a circle.
            int w = ShowsShield() ? h : t.Width + h + Dpi.Px(22, dpi);
            var want = new Size(w, h);
            if (want != _sizeForText) { _sizeForText = want; Size = want; }
            RebuildRegion();
        }

        /// <summary>2.7b: the title-bar size. 28 logical px high fits both bars (46, and 36 compact).</summary>
        private void AutoSizeOnAccent()
        {
            int dpi = Dpi.Of(this);
            var t = TextRenderer.MeasureText(Caption(), FontHelper.UiAt(9f, dpi, FontStyle.Bold));   // shared font: never dispose
            int h = Math.Max(Dpi.Px(28, dpi), t.Height + Dpi.Px(8, dpi));
            int w = ShowsShield() ? h : t.Width + h + Dpi.Px(14, dpi);
            var want = new Size(w, h);
            if (want != _sizeForText)
            {
                _sizeForText = want; Size = want;
                if (Parent != null) Parent.PerformLayout();   // TitleBar places its slot from the slot's size
            }
            RebuildRegion();
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RebuildRegion(); }

        private void RebuildRegion()
        {
            if (_onAccent)
            {
                // 2.7b: on the title bar the background is one flat colour, so the corners are painted (anti-aliased)
                // instead of cut by an aliased Region.
                var had = Region; if (had != null) { Region = null; had.Dispose(); }
                return;
            }
            if (Width <= 0 || Height <= 0) return;
            // ONE figure: no FillMode concern (see the class remarks). The region rect is 1px OUTSIDE the
            // painted pill on every side, so the region's aliased edge hides inside a fill-coloured halo
            // instead of showing a hard step against the form.
            var outer = new Rectangle(0, 0, Width, Height);
            using (var path = DrawHelper.RoundedRect(outer, Height / 2))
            {
                var old = Region;
                Region = new Region(path);        // Region(GraphicsPath) copies — safe to dispose the path
                if (old != null) old.Dispose();
            }
        }

        private Color DotColor()
        {
            switch (_state)
            {
                case ProxyConnectState.Connecting: return ThemeHelper.PaletteFor(IsDark).ProxyDotConnecting;   // amber
                case ProxyConnectState.Connected: return AccentColor;
                case ProxyConnectState.Failed: return ThemeHelper.PaletteFor(IsDark).ProxyDotFailed;        // red
                default: return ThemeHelper.PaletteFor(IsDark).ProxyDotIdle;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_onAccent) { PaintOnAccent(g); return; }
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);

            // BATCH-TA-16d/D5 — CONTRAST IS LOAD-BEARING HERE, and the first version failed it.
            // These colours were chosen against LoginForm's flat background (248,248,250 light /
            // 40,40,44 dark). Over the CHAT LIST the light case collapses: MainForm sets
            // _chatListPanel.BackColor to WHITE in light theme, and the pill's light fill was also white —
            // an invisible chip with only a faint 1px border to distinguish it.
            // Fixed by carrying the contrast in the BORDER rather than the fill: a floating chip over
            // arbitrary content can't rely on its fill differing from whatever is behind it, but a border
            // dark enough to read on both white and near-white always separates it. Dark theme lifts the
            // fill instead (58 vs the list's 40) because a light border there would glare.
            Color fill = ThemeHelper.PaletteFor(IsDark).ProxyPillFill;
            if (_pressed) fill = ThemeHelper.PaletteFor(IsDark).ProxyPillFillPressed;
            else if (_hover) fill = ThemeHelper.PaletteFor(IsDark).ProxyPillFillHover;

            // Clear with the pill's OWN fill — with a Region, every surviving pixel belongs to this
            // control, so the parent background must never be painted anywhere (the X1 rule).
            g.Clear(fill);

            var body = new Rectangle(P(1), P(1), Width - P(3), Height - P(3));   // 1px (logical) inside the region rim
            using (var b = new SolidBrush(fill))
            using (var path = DrawHelper.RoundedRect(body, body.Height / 2))
            {
                g.FillPath(b, path);
                // Border does the separating (see the D5 note above): dark enough to read against a WHITE
                // chat list as well as LoginForm's near-white panel.
                using (var p = new Pen(ThemeHelper.PaletteFor(IsDark).ProxyPillBorder, Dpi.F(1.2f, dpi)))
                    g.DrawPath(p, path);
            }

            if (ShowsShield())
            {
                // The collapsed steady state: an accent shield, centred, no text.
                float s = body.Height * 0.52f;
                var sr = new RectangleF(body.X + (body.Width - s) / 2f, body.Y + (body.Height - s) / 2f, s, s);
                using (var sp = ShieldPath(sr))
                using (var sb = new SolidBrush(ColorMath.Ink(AccentColor, fill, 3.0)))
                    g.FillPath(sb, sp);
                // A tick inside the shield, in the pill's own fill so it reads on any accent.
                using (var pen = new Pen(fill, Math.Max(Dpi.F(1.6f, dpi), s * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(sr.X + s * 0.30f, sr.Y + s * 0.50f),
                        new PointF(sr.X + s * 0.44f, sr.Y + s * 0.64f),
                        new PointF(sr.X + s * 0.72f, sr.Y + s * 0.34f),
                    });
            }
            else
            {
                int dot = Math.Max(P(8), Height / 3);
                var dotRect = new Rectangle(body.X + (Height - dot) / 2, body.Y + (body.Height - dot) / 2, dot, dot);
                using (var db = new SolidBrush(DotColor())) g.FillEllipse(db, dotRect);

                var textRect = new Rectangle(dotRect.Right + P(8), body.Y, body.Right - dotRect.Right - P(14), body.Height);
                TextRenderer.DrawText(g, Caption(), FontHelper.UiAt(9f, dpi, FontStyle.Bold), textRect,   // shared font: never dispose
                    ThemeHelper.PaletteFor(IsDark).ProxyPillText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix);
            }

            if (Focused)
            {
                // The ring's radius is half the INSET rect's own height (== (body.Height - 6) / 2 at 96): Px(6) can round
                // past 2 × Px(3), and RoundedRect turns a too-large radius into a square.
                var ring = Rectangle.Inflate(body, -P(3), -P(3));
                using (var fp = new Pen(ColorMath.Ink(AccentColor, fill, 3.0), Dpi.F(1.4f, dpi)) { DashStyle = DashStyle.Dot })
                using (var fr = DrawHelper.RoundedRect(ring, Math.Max(1, ring.Height / 2)))
                    g.DrawPath(fp, fr);
            }
        }

        /// <summary>2.7b: the title-bar look. The fill is the bar's colour lifted toward its ink (Palette.OnAccent), so it
        /// reads on any accent without a border; the text and the connected shield are ink. The status dot keeps its
        /// amber / red with an ink rim, so it stays distinct on a warm accent.</summary>
        private void PaintOnAccent(Graphics g)
        {
            var pal = ThemeHelper.Palette;
            Color ink = pal.OnAccent;
            Color fill = ColorMath.Mix(pal.TitleBar, ink, _pressed ? 0.30f : _hover ? 0.24f : 0.16f);
            g.Clear(pal.TitleBar);   // the bar is one flat colour: paint the corners as the bar, then the rounded fill
            int dpi = Dpi.Of(this);
            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = DrawHelper.RoundedRect(body, body.Height / 2))
            using (var fb = new SolidBrush(fill))
                g.FillPath(fb, path);

            if (ShowsShield())
            {
                float s = body.Height * 0.56f;
                var sr = new RectangleF((body.Width - s) / 2f, (body.Height - s) / 2f, s, s);
                using (var sp = ShieldPath(sr))
                using (var sb = new SolidBrush(ink))
                    g.FillPath(sb, sp);
                using (var pen = new Pen(fill, Math.Max(1.6f, s * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(sr.X + s * 0.30f, sr.Y + s * 0.50f),
                        new PointF(sr.X + s * 0.44f, sr.Y + s * 0.64f),
                        new PointF(sr.X + s * 0.72f, sr.Y + s * 0.34f),
                    });
            }
            else
            {
                int dot = Math.Max(Dpi.Px(8, dpi), Height / 3);
                var dotRect = new Rectangle((Height - dot) / 2, (Height - dot) / 2, dot, dot);
                Color dc = _state == ProxyConnectState.Off ? ColorMath.Mix(fill, ink, 0.55f) : DotColor();
                using (var db = new SolidBrush(dc)) g.FillEllipse(db, dotRect);
                using (var rp = new Pen(ink, Math.Max(1f, dpi / 96f))) g.DrawEllipse(rp, dotRect);
                var textRect = new Rectangle(dotRect.Right + Dpi.Px(6, dpi), 0, Math.Max(0, Width - dotRect.Right - Dpi.Px(12, dpi)), Height);
                TextRenderer.DrawText(g, Caption(), FontHelper.UiAt(9f, dpi, FontStyle.Bold), textRect, ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (Focused)
                using (var fp = new Pen(ink, 1.4f) { DashStyle = DashStyle.Dot })
                using (var fr = DrawHelper.RoundedRect(Rectangle.Inflate(body, -3, -3), Math.Max(1, (body.Height - 6) / 2)))
                    g.DrawPath(fp, fr);
        }

        /// <summary>A shield outline: flat-ish shoulders, straight sides, tapering to a point. Drawn rather
        /// than typed — a "🛡" glyph is not dependable on RT 8.1, and this scales cleanly with DPI.</summary>
        private static GraphicsPath ShieldPath(RectangleF r)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w / 2f, right = x + w, bottom = y + h;
            var p = new GraphicsPath();
            p.AddLine(cx, y, right, y + h * 0.20f);
            p.AddLine(right, y + h * 0.20f, right, y + h * 0.55f);
            p.AddBezier(right, y + h * 0.55f, right, y + h * 0.86f, cx + w * 0.20f, bottom, cx, bottom);
            p.AddBezier(cx, bottom, cx - w * 0.20f, bottom, x, y + h * 0.86f, x, y + h * 0.55f);
            p.AddLine(x, y + h * 0.55f, x, y + h * 0.20f);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        /// <summary>Space/Enter activate, so the pill is reachable without a pointer.</summary>
        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            // Unsubscribe FIRST: ProxyStatus is static and outlives every form, so a missed detach here
            // would keep this control (and its form) alive for the life of the process.
            if (disposing) { try { ProxyStatus.Changed -= OnProxyStatusChanged; } catch { } try { _tip.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
