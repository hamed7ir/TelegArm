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
    /// </summary>
    public sealed class ProxyStatusPill : Control
    {
        private ProxyConnectState _state = ProxyConnectState.Off;
        private string _detail;             // e.g. "velvory.co.uk.:443" — NEVER the secret (ProxyUrl.SafeForLog)
        private bool _viaProxy;             // wording modifier — see Caption()
        private bool _hover, _pressed;
        private Size _sizeForText = Size.Empty;
        private readonly ToolTip _tip = new ToolTip();

        /// <summary>Accent for the "connected" dot and the focus ring. Set from ApplyTheme — never read
        /// MaterialSkinManager's Accent slot directly: it is ONE app-wide singleton and writing it
        /// re-poisons every other form (LESSONS_LEARNED.md:163).</summary>
        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

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
            try
            {
                if (InvokeRequired) BeginInvoke((Action)SyncFromStatus);
                else SyncFromStatus();
            }
            catch { /* handle gone / form closing */ }
        }

        private void SyncFromStatus()
        {
            if (IsDisposed) return;
            _viaProxy = ProxyStatus.ViaProxy;
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
            switch (_state)
            {
                case ProxyConnectState.Connecting: return _viaProxy ? "Connecting via proxy…" : "Connecting…";
                case ProxyConnectState.Connected: return "Proxy connected";
                case ProxyConnectState.Failed: return _viaProxy ? "Proxy not working" : "Can't connect — add a proxy";
                default: return "Connect via proxy";
            }
        }

        private string TooltipText()
        {
            string d = string.IsNullOrEmpty(_detail) ? "" : "\n" + _detail;
            switch (_state)
            {
                case ProxyConnectState.Connecting:
                    return _viaProxy ? "Trying to reach Telegram through your proxy." + d
                                     : "Trying to reach Telegram.\nIf it's blocked on your network, click to set up a proxy.";
                case ProxyConnectState.Connected: return "Connected to Telegram through your proxy." + d;
                case ProxyConnectState.Failed:
                    return _viaProxy ? "Couldn't reach Telegram through your proxy.\nClick to change or turn it off." + d
                                     : "Couldn't reach Telegram.\nIt may be blocked on your network — click to set up a proxy.";
                default: return "Set up an MTProto proxy if Telegram is blocked on your network.";
            }
        }

        /// <summary>Size is derived from FONT METRICS, never fixed pixels, so a DPI-scaled device does not
        /// clip the caption. Height keeps the ~40px touch-target bar the scroll-button work established.</summary>
        private void AutoSizeToText()
        {
            using (var f = FontHelper.Ui(9f, FontStyle.Bold))
            {
                var t = TextRenderer.MeasureText(Caption(), f);
                int h = Math.Max(34, t.Height + 16);
                // CONNECTED collapses to a CIRCLE with a shield — the steady state needs an at-a-glance
                // "you are protected" mark, not a sentence taking up the corner. Every transient state
                // (connecting / failed / off) keeps the wide pill, because those have something to SAY.
                // Still one figure, so the Region's FillMode rule is unaffected: a square fed to
                // RoundedRect with radius = h/2 IS a circle.
                int w = _state == ProxyConnectState.Connected ? h : t.Width + h + 22;
                var want = new Size(w, h);
                if (want != _sizeForText) { _sizeForText = want; Size = want; }
            }
            RebuildRegion();
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RebuildRegion(); }

        private void RebuildRegion()
        {
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
                case ProxyConnectState.Connecting: return Color.FromArgb(230, 170, 60);   // amber
                case ProxyConnectState.Connected: return AccentColor;
                case ProxyConnectState.Failed: return Color.FromArgb(214, 78, 78);        // red
                default: return IsDark ? Color.FromArgb(130, 130, 136) : Color.FromArgb(150, 150, 156);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // BATCH-TA-16d/D5 — CONTRAST IS LOAD-BEARING HERE, and the first version failed it.
            // These colours were chosen against LoginForm's flat background (248,248,250 light /
            // 40,40,44 dark). Over the CHAT LIST the light case collapses: MainForm sets
            // _chatListPanel.BackColor to WHITE in light theme, and the pill's light fill was also white —
            // an invisible chip with only a faint 1px border to distinguish it.
            // Fixed by carrying the contrast in the BORDER rather than the fill: a floating chip over
            // arbitrary content can't rely on its fill differing from whatever is behind it, but a border
            // dark enough to read on both white and near-white always separates it. Dark theme lifts the
            // fill instead (58 vs the list's 40) because a light border there would glare.
            Color fill = IsDark ? Color.FromArgb(58, 58, 63) : Color.White;
            if (_pressed) fill = IsDark ? Color.FromArgb(46, 46, 50) : Color.FromArgb(236, 236, 240);
            else if (_hover) fill = IsDark ? Color.FromArgb(66, 66, 71) : Color.FromArgb(247, 247, 250);

            // Clear with the pill's OWN fill — with a Region, every surviving pixel belongs to this
            // control, so the parent background must never be painted anywhere (the X1 rule).
            g.Clear(fill);

            var body = new Rectangle(1, 1, Width - 3, Height - 3);   // 1px inside the region rim
            using (var b = new SolidBrush(fill))
            using (var path = DrawHelper.RoundedRect(body, body.Height / 2))
            {
                g.FillPath(b, path);
                // Border does the separating (see the D5 note above): dark enough to read against a WHITE
                // chat list as well as LoginForm's near-white panel.
                using (var p = new Pen(IsDark ? Color.FromArgb(92, 92, 98) : Color.FromArgb(186, 186, 194), 1.2f))
                    g.DrawPath(p, path);
            }

            if (_state == ProxyConnectState.Connected)
            {
                // The collapsed steady state: an accent shield, centred, no text.
                float s = body.Height * 0.52f;
                var sr = new RectangleF(body.X + (body.Width - s) / 2f, body.Y + (body.Height - s) / 2f, s, s);
                using (var sp = ShieldPath(sr))
                using (var sb = new SolidBrush(AccentColor))
                    g.FillPath(sb, sp);
                // A tick inside the shield, in the pill's own fill so it reads on any accent.
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
                int dot = Math.Max(8, Height / 3);
                var dotRect = new Rectangle(body.X + (Height - dot) / 2, body.Y + (body.Height - dot) / 2, dot, dot);
                using (var db = new SolidBrush(DotColor())) g.FillEllipse(db, dotRect);

                var textRect = new Rectangle(dotRect.Right + 8, body.Y, body.Right - dotRect.Right - 14, body.Height);
                using (var f = FontHelper.Ui(9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Caption(), f, textRect,
                        IsDark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(38, 38, 42),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.NoPrefix);
            }

            if (Focused)
                using (var fp = new Pen(AccentColor, 1.4f) { DashStyle = DashStyle.Dot })
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
