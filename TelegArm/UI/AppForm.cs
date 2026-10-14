using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 1.3 — the base window of the 2.x look: borderless, with TelegArm's own accent
    /// <see cref="TitleBar"/>, but a REAL resizable window underneath (Snap, Win+Arrow, Alt+Space, taskbar minimise,
    /// edge resize, maximise to the work area). Replaces MaterialForm (step 2.7a); <c>AppForm(resizable: false)</c>
    /// is the fixed variant for the login screen and dialogs.
    ///
    /// HOW THE FRAME IS REMOVED BUT THE WINDOW KEPT. FormBorderStyle.None, then CreateParams puts back WS_THICKFRAME
    /// (resizable only), WS_MINIMIZEBOX, WS_MAXIMIZEBOX and WS_SYSMENU — the styles Windows keys Snap, minimise and the
    /// window menu on — and WM_NCCALCSIZE hands the whole window to the client. Hit-testing is ours: a Px(6) gutter
    /// (Form.Padding, left/right/bottom, painted as the border, zero when maximised) answers the resize edges, and
    /// the title bar answers HTTRANSPARENT so this form answers HTCAPTION there (or HTTOP along the top edge).
    /// Stock TableLayoutPanels cannot answer WM_NCHITTEST, which is why the gutter is padding the form owns.
    /// ⚠ NEVER MOVE IT BY ASSIGNING Location: dragging is HTCAPTION, so Windows owns it (and Snap with it).
    /// ⚠ SET Size / Bounds, NEVER ClientSize: WinForms sizes ClientSize through the thick-frame metrics, which this
    ///   window does not have (scripts\check.ps1 enforces this for AppForm/DialogForm subclasses).
    ///
    /// ESCAPE HATCH (plan §0.4): ChromeMode = "Native" in settings, --native-caption, or TELEGARM_CHROME=native gives
    /// the ordinary Windows frame: FormBorderStyle.Sizable, title bar hidden, no NC handling. The first AppForm of a
    /// process also self-checks (window = client, caption hit-test) and falls back to native when that fails.
    ///
    /// DPI: the form is an <see cref="IDpiHost"/>; <see cref="WindowDpi"/> handles WM_DPICHANGED (inert while the
    /// process is system-aware). Build controls with <see cref="Px"/> and they scale correctly from birth.</summary>
    public class AppForm : Form, IDpiHost, IDpiAware
    {
        protected readonly TitleBar Bar;
        /// <summary>Where a subclass puts its UI: everything below the title bar, inside the resize gutter.</summary>
        protected readonly Panel Content;
        private readonly WindowDpi _windowDpi;
        private readonly bool _resizable;
        private static bool _selfChecked;

        /// <summary>True when this window draws its own chrome; false in native mode.</summary>
        public bool CustomChrome { get; private set; }

        /// <summary>The logical (96-DPI) minimum size; applied as MinimumSize at the window's DPI.</summary>
        public Size LogicalMinimumSize
        {
            get { return _logicalMin; }
            set { _logicalMin = value; MinimumSize = new Size(Px(value.Width), Px(value.Height)); }
        }
        private Size _logicalMin;

        public AppForm() : this(true) { }

        protected AppForm(bool resizable)
        {
            _resizable = resizable;
            _windowDpi = new WindowDpi(this);
            CustomChrome = !NativeChrome;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            AppIcon.Set(this);
            MaximizeBox = resizable;
            MinimizeBox = true;
            FormBorderStyle = CustomChrome ? FormBorderStyle.None : (resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle);

            Content = new Panel { Dock = DockStyle.Fill };
            Bar = new TitleBar { Dock = DockStyle.Top, CanMaximize = resizable, Visible = CustomChrome };
            Controls.Add(Content);   // Fill first: docking takes the last-added (the bar) first
            Controls.Add(Bar);
            ApplyGutter();
            Recolor();
            ThemeHelper.ThemeChanged += OnThemeChangedInternal;
        }

        /// <summary>Redesign step 2.5 — this window is EMBEDDED as a pane (TopLevel = false inside another form, e.g. the
        /// profile in MainForm's Info dock): no title bar, no custom chrome, no resize gutter, no hit-testing of our own
        /// (a pane must never answer HTCAPTION — the user would drag it around inside its host). Call in the subclass
        /// constructor, before the handle exists; the caller then sets TopLevel = false.</summary>
        protected void UseAsPane()
        {
            CustomChrome = false;
            FormBorderStyle = FormBorderStyle.None;
            if (Bar != null) Bar.Visible = false;
            Padding = Padding.Empty;
            IsPane = true;
        }

        /// <summary>True after <see cref="UseAsPane"/>.</summary>
        public bool IsPane { get; private set; }

        int IDpiHost.Dpi { get { return _windowDpi.Dpi; } }

        /// <summary>After DpiScaler's generic pass, re-derive what this form computes from its DPI itself: the gutter
        /// and the minimum size. Without this the gutter was scaled TWICE (OnResize set Px(6) at the new DPI, then
        /// the generic pass multiplied it again): 9 → 25 px at 240 DPI, and 5 px after moving back (gallery probe).</summary>
        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            ApplyGutter();
            if (!_logicalMin.IsEmpty) MinimumSize = new Size(Px(_logicalMin.Width), Px(_logicalMin.Height));
            UpdateCompact();
            OnDpiChanged(oldDpi, newDpi);
        }

        /// <summary>Called after a DPI change, once every control has been rescaled. For subclass metrics.</summary>
        protected virtual void OnDpiChanged(int oldDpi, int newDpi) { }

        /// <summary>This window's current DPI.</summary>
        public int WindowDpiValue { get { return _windowDpi.Dpi; } }

        /// <summary>A logical length at this window's DPI.</summary>
        protected int Px(int n) { return Dpi.Px(n, _windowDpi.Dpi); }

        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value; if (Bar != null) Bar.Title = value; }
        }

        // ── chrome mode ──────────────────────────────────────────────────────────────────────────────────────

        private static bool? _native;
        internal static string ChromeReason = "";

        /// <summary>Resolved once per process: TELEGARM_CHROME / --native-caption / AppSettings.ChromeMode, or a failed
        /// self-check of the first AppForm.</summary>
        public static bool NativeChrome
        {
            get
            {
                if (_native.HasValue) return _native.Value;
                string env = null;
                try { env = Environment.GetEnvironmentVariable("TELEGARM_CHROME"); } catch { }
                bool arg = false;
                foreach (var a in Environment.GetCommandLineArgs()) if (string.Equals(a, "--native-caption", StringComparison.OrdinalIgnoreCase)) arg = true;
                string setting = "";
                try { setting = AppSettings.Instance.ChromeMode ?? ""; } catch { }
                if (string.Equals(env, "native", StringComparison.OrdinalIgnoreCase)) { _native = true; ChromeReason = "TELEGARM_CHROME"; }
                else if (string.Equals(env, "custom", StringComparison.OrdinalIgnoreCase)) { _native = false; ChromeReason = "TELEGARM_CHROME"; }
                else if (arg) { _native = true; ChromeReason = "--native-caption"; }
                else if (string.Equals(setting, "Native", StringComparison.OrdinalIgnoreCase)) { _native = true; ChromeReason = "setting"; }
                else { _native = false; ChromeReason = setting.Length == 0 ? "build default" : "setting"; }
                Logger.Diag("[CHROME] " + (_native.Value ? "native" : "custom") + " (" + ChromeReason + ")");
                return _native.Value;
            }
        }

        // ── window styles, frame, hit-testing ───────────────────────────────────────────────────────────────

        private bool _creatingHandle;

        protected override void CreateHandle()
        {
            _creatingHandle = true;
            try { base.CreateHandle(); }
            finally { _creatingHandle = false; }
        }

        /// <summary>⚠ WS_THICKFRAME ONLY ONCE THE WINDOW EXISTS (or is being created). Before the handle exists,
        /// WinForms derives ClientSize from Size through AdjustWindowRectEx on these styles; with the thick frame in
        /// them it lays the form out 20 x 20 px too small at 144 DPI (measured: an anchored button came out 20 px off
        /// its designed gaps and stayed there, because anchors remember the size they were laid out against).
        /// After creation WinForms reads the real client rectangle, which WM_NCCALCSIZE makes the whole window.</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (CustomChrome)
                {
                    // Borderless, WinForms adds no border icons itself, so MinimizeBox is honoured here: a modal dialog
                    // (DialogForm: no taskbar button) must not be minimisable by Win+Down while its owner is disabled.
                    cp.Style |= WS_SYSMENU;
                    if (MinimizeBox) cp.Style |= WS_MINIMIZEBOX;
                    if (_resizable)
                    {
                        cp.Style |= WS_MAXIMIZEBOX;
                        if (_creatingHandle || IsHandleCreated) cp.Style |= WS_THICKFRAME;
                    }
                }
                return cp;
            }
        }

        /// <summary>With custom chrome the client IS the window (WM_NCCALCSIZE), but WinForms computes window size from
        /// client size through AdjustWindowRectEx on our styles — which include WS_THICKFRAME — and so adds a frame
        /// that does not exist. Measured by the gallery probe: maximise → restore grew the window by 20 x 20 px at
        /// 144 DPI. Every WinForms path that converts (ClientSize setter, restore, autoscroll) goes through here.</summary>
        protected override Size SizeFromClientSize(Size clientSize)
        {
            return CustomChrome ? clientSize : base.SizeFromClientSize(clientSize);
        }

        /// <summary>The resize gutter (left/right/bottom) — zero when maximised; a 1 px outline for the fixed variant.
        /// ⚠ THE GUTTER IS PADDING THE FORM OWNS, AND ONLY PADDING CAN BE A RESIZE EDGE. WM_NCHITTEST goes to the DEEPEST
        ///   window under the point first; a child there answers HTCLIENT and the form is never asked. So the 1.3 build's
        ///   "Px(12) for touch" in the hit test did nothing on the left/right/bottom edges (review 1.3, measured at 240 DPI:
        ///   left+0..14 HTLEFT, left+15..26 the Content panel). On a touch-capable machine the padding itself is the touch
        ///   width; elsewhere it is the mouse width.</summary>
        private void ApplyGutter()
        {
            if (!CustomChrome) { Padding = Padding.Empty; return; }
            if (WindowState == FormWindowState.Maximized) Padding = Padding.Empty;
            else if (_resizable) { int g = Px(EdgeWidth); Padding = new Padding(g, 0, g, g); }
            else Padding = new Padding(1, 0, 1, 1);
        }

        public const int Gutter = 6, Corner = 10, TouchGutter = 12, TouchCorner = 16;

        /// <summary>The resize gutter in logical px: <see cref="TouchGutter"/> on a machine with a touch digitizer
        /// (SM_MAXIMUMTOUCHES &gt; 0 — the Surface, and many laptops), <see cref="Gutter"/> otherwise.</summary>
        public static int EdgeWidth { get { return TouchCapable ? TouchGutter : Gutter; } }
        private static int CornerWidth { get { return TouchCapable ? TouchCorner : Corner; } }

        private static bool? _touchCapable;
        public static bool TouchCapable
        {
            get
            {
                if (!_touchCapable.HasValue)
                {
                    try { _touchCapable = GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0; } catch { _touchCapable = false; }
                }
                return _touchCapable.Value;
            }
        }

        private Size _normalSize;
        private int _normalDpi;
        private FormWindowState _lastState = FormWindowState.Normal;
        private bool _liveNormal = true;      // IsZoomed/IsIconic as of the last WM_MOVE/WM_SIZE — NOT WinForms' cached state
        private Point _osRestorePlace;        // where WINDOWS put the window when it last left maximised/minimised
        private Size? _sizeAfterDrag;         // a size correction owed once a drag-restore ends

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Bar != null) Bar.SyncWindowState(WindowState);
            ApplyGutter();
            if (WindowState != FormWindowState.Minimized) _windowDpi.Resync("restore");
            KeepNormalBounds();
            Invalidate();
        }

        protected override void OnMove(EventArgs e) { base.OnMove(e); KeepNormalBounds(); UpdateCompact(); }

        /// <summary>⚠ RESTORE MUST NOT GROW THE WINDOW. Measured by the gallery probe: maximise → restore came back
        /// 20 x 20 px larger at 144 DPI. WinForms' restore path converts a size it stored as a CLIENT size through an
        /// internal SizeFromClientSize(int, int) — not the overridable one — which adds the WS_THICKFRAME border this
        /// window does not draw. So the window remembers its own normal SIZE and puts it back after any return to
        /// Normal (one deferred correction; nothing to do when WinForms got it right).
        /// ⚠ AND THE LOCATION WINDOWS CHOSE — NOT WINFORMS'. WinForms remembers where the window was when it was maximised
        ///   (its restoredWindowBounds) and moves it back there on restore, after Windows has placed it. So a placement
        ///   changed by Win+Shift+Arrow, a monitor change or SetWindowPlacement was undone (review 1.3, measured: restore
        ///   landed on the old 40,40, not Windows' 160,120 — and the 1.3 build's own whole-rectangle correction did the
        ///   same). The first WM_MOVE/WM_SIZE after the LIVE state (IsZoomed/IsIconic) turns normal carries Windows' place;
        ///   it is kept and put back. WinForms' cached WindowState flips later, inside the same message, which is why the
        ///   live state is the one to watch.
        /// ⚠ NEVER DURING A DRAG. Dragging down from maximised restores inside Windows' move loop with the window under the
        ///   finger; moving it then would yank it away. The size is corrected when the drag ends (WM_EXITSIZEMOVE).
        /// ⚠ The size is kept with the DPI it was measured at, and rescaled on the way back: a DPI change while maximised
        ///   or minimised must not restore old-DPI pixels. Never larger than the work area of the window's monitor.</summary>
        private void KeepNormalBounds()
        {
            if (!CustomChrome || !IsHandleCreated) { _lastState = WindowState; return; }
            bool liveNormal = !IsZoomed(Handle) && !IsIconic(Handle);
            if (liveNormal && !_liveNormal) _osRestorePlace = Location;   // Windows' choice, before WinForms moves it back
            _liveNormal = liveNormal;
            var state = WindowState;
            if (state == FormWindowState.Normal && _lastState != FormWindowState.Normal && !_normalSize.IsEmpty)
            {
                int dpi = WindowDpiValue;
                var want = _normalDpi > 0 && _normalDpi != dpi
                    ? new Size((int)Math.Round(_normalSize.Width * (double)dpi / _normalDpi), (int)Math.Round(_normalSize.Height * (double)dpi / _normalDpi))
                    : _normalSize;
                var place = _osRestorePlace;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed || WindowState != FormWindowState.Normal) return;
                        if (InSizeMove) { _sizeAfterDrag = want; return; }   // a drag-restore: settle when it ends
                        var target = KeepReachable(new Rectangle(place, want));
                        if (Bounds != target) Bounds = target;
                    }));
                }
                catch { }
            }
            else if (state == FormWindowState.Normal) { _normalSize = Size; _normalDpi = WindowDpiValue; }
            _lastState = state;
        }

        /// <summary>A drag-restore ended: the window stays where the user let go; only WinForms' size error is corrected.</summary>
        private void SettleAfterDrag()
        {
            if (!_sizeAfterDrag.HasValue) return;
            var want = _sizeAfterDrag.Value;
            _sizeAfterDrag = null;
            if (IsDisposed || WindowState != FormWindowState.Normal) return;
            var target = KeepReachable(new Rectangle(Location, want));
            if (Bounds != target) Bounds = target;
        }

        /// <summary>No larger than the work area of the monitor the rectangle is mostly on, and left where it is unless its
        /// TITLE BAR would be out of reach (a monitor that has gone, a placement off every screen) — Windows itself lets a
        /// window hang off an edge, and that is the user's choice to keep. Unreachable: moved into that work area.</summary>
        private Rectangle KeepReachable(Rectangle r)
        {
            Rectangle wa;
            try { wa = Screen.FromRectangle(r).WorkingArea; } catch { return r; }
            int w = Math.Min(r.Width, wa.Width), h = Math.Min(r.Height, wa.Height);
            r = new Rectangle(r.Location, new Size(w, h));
            var grip = new Rectangle(r.Left + Px(TitleBar.ButtonWidth), r.Top, Math.Max(1, r.Width - 4 * Px(TitleBar.ButtonWidth)), Px(TitleBar.CompactHeight));
            foreach (var s in Screen.AllScreens)
            {
                var vis = Rectangle.Intersect(grip, s.WorkingArea);
                if (vis.Width >= Px(TitleBar.ButtonWidth) && vis.Height >= Px(TitleBar.CompactHeight) / 2) return r;   // a grabbable piece of the bar is on screen
            }
            int x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
            int y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (CustomChrome) ApplyDwmAttributes();
            _windowDpi.Resync("handle");
            UpdateCompact();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (CustomChrome && !_selfChecked) { _selfChecked = true; SelfCheck(); }
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); _windowDpi.Resync("activated"); }

        /// <summary>Compact title bar (36 instead of 46) when the work area is at most 800 logical px tall — RT's
        /// 1366×768 always qualifies (plan D11). MainForm also forces it while the keyboard has shrunk the window.</summary>
        protected virtual void UpdateCompact()
        {
            if (Bar == null || !IsHandleCreated) return;
            try
            {
                int h = Screen.FromHandle(Handle).WorkingArea.Height * Dpi.Logical / Math.Max(1, _windowDpi.Dpi);
                Bar.Compact = h <= 800;
            }
            catch { }
        }

        /// <summary>The window's background is the border: Window colour, with a 1 px Divider outline when not maximised.</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            e.Graphics.Clear(p.Window);
            if (CustomChrome && WindowState != FormWindowState.Maximized)
                using (var pen = new Pen(p.Divider)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void WndProc(ref Message m)
        {
            if (_windowDpi.OnMessage(ref m)) return;
            if (CustomChrome)
            {
                switch (m.Msg)
                {
                    case WM_NCCALCSIZE:
                        if (m.WParam != IntPtr.Zero)
                        {
                            // The whole window is client. Maximised, a thick-frame window can overhang the monitor by
                            // its frame width; clip the client to the work area so nothing is drawn off-screen.
                            // IsZoomed, not WindowState: WinForms' WindowState is still the CACHED Normal during the
                            // WM_NCCALCSIZE that maximises the window (review 1.3), so the clip never fired.
                            if (IsZoomed(Handle)) ClipToWorkArea(m.LParam);
                            m.Result = (IntPtr)WVR_REDRAW;
                            return;
                        }
                        break;
                    case WM_NCHITTEST:
                        m.Result = (IntPtr)HitTest(m.LParam);
                        return;
                    case WM_NCACTIVATE:
                        m.LParam = (IntPtr)(-1);   // DefWindowProc must not repaint a frame we do not have
                        break;
                    case WM_GETMINMAXINFO:
                        base.WndProc(ref m);       // WinForms fills MinimumSize/MaximumSize first
                        ConstrainMaximize(m.LParam);
                        return;
                }
            }
            switch (m.Msg)
            {
                case WM_ENTERSIZEMOVE: InSizeMove = true; OnSizeMoveBegin(); break;
                case WM_EXITSIZEMOVE: InSizeMove = false; SettleAfterDrag(); OnSizeMoveEnd(); break;
                case WM_DWMCOLORIZATIONCOLORCHANGED: ThemeHelper.NotifyAccentChanged(); break;
                case WM_DISPLAYCHANGE:
                case WM_SETTINGCHANGE:
                    try { BeginInvoke((Action)(() => { _windowDpi.Resync("display"); UpdateCompact(); })); } catch { }
                    break;
                case WM_ACTIVATE:
                    // DroidFinestra's (originally TelegArm's) WM_ACTIVATE fix: on a NON-click reactivation WinForms
                    // restores focus to the last text field, and Windows then re-shows the touch keyboard for it —
                    // the "tap the keyboard's ✕, it comes back" loop. A real tap arrives as a click, not WA_ACTIVE.
                    if ((m.WParam.ToInt64() & 0xFFFF) == WA_ACTIVE && InputPolicy.RefocusClear)   // device trip: off switch
                        try { BeginInvoke((Action)ClearAutoRefocusedTextField); } catch { }
                    break;
            }
            base.WndProc(ref m);
        }

        /// <summary>True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE (a drag or resize in progress).</summary>
        protected bool InSizeMove { get; private set; }
        /// <summary>A drag/resize began: suspend expensive relayout until <see cref="OnSizeMoveEnd"/>.</summary>
        protected virtual void OnSizeMoveBegin() { }
        protected virtual void OnSizeMoveEnd() { }

        private void ClearAutoRefocusedTextField()
        {
            try { if (!IsDisposed && (ActiveControl is TextBoxBase || ActiveControl is ITextInputTarget)) ActiveControl = null; } catch { }
        }

        private int HitTest(IntPtr lParam)
        {
            var screen = new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var p = PointToClient(screen);
            bool canResize = _resizable && WindowState == FormWindowState.Normal;
            var caption = Bar != null && Bar.Visible ? Bar.Bounds : Rectangle.Empty;
            // The gutter width is the padding's (see ApplyGutter): wider than that can never be reached on three edges.
            return WindowHit.Compute(ClientSize, p, Px(EdgeWidth), Px(CornerWidth), canResize, caption);
        }

        /// <summary>DroidFinestra's ConstrainMaximize: maximise to the WORK AREA of the window's own monitor, never over
        /// the taskbar. With an auto-hide taskbar, one pixel is left on its edge so it can still be summoned.</summary>
        private void ConstrainMaximize(IntPtr lParam)
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;
                RECT work = mi.rcWork, full = mi.rcMonitor;
                int x = work.Left - full.Left, y = work.Top - full.Top, w = work.Right - work.Left, h = work.Bottom - work.Top;
                int edge = AutoHideTaskbarEdge(mon);
                if (edge == ABE_LEFT) { x += 1; w -= 1; } else if (edge == ABE_RIGHT) w -= 1;
                else if (edge == ABE_TOP) { y += 1; h -= 1; } else if (edge == ABE_BOTTOM) h -= 1;
                mmi.ptMaxPosition.X = x; mmi.ptMaxPosition.Y = y;
                mmi.ptMaxSize.X = w; mmi.ptMaxSize.Y = h;
                // ⚠ AND THE MAX TRACK SIZE (review 1.3, measured): left at the default, which is sized from the PRIMARY
                //   monitor, Windows re-expands a maximised window on a LARGER secondary monitor past its work area
                //   (3240x2040 asked → 4560x2186 given). ptMaxSize alone is only honoured up to the track size.
                if (MaximumSize.IsEmpty)
                {
                    mmi.ptMaxTrackSize.X = Math.Max(w, mmi.ptMinTrackSize.X);
                    mmi.ptMaxTrackSize.Y = Math.Max(h, mmi.ptMinTrackSize.Y);
                }
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch { }
        }

        private void ClipToWorkArea(IntPtr lParam)
        {
            try
            {
                var calc = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(lParam, typeof(NCCALCSIZE_PARAMS));
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;
                RECT r = calc.rgrc0, w = mi.rcWork;
                calc.rgrc0 = new RECT { Left = Math.Max(r.Left, w.Left), Top = Math.Max(r.Top, w.Top), Right = Math.Min(r.Right, w.Right), Bottom = Math.Min(r.Bottom, w.Bottom) };
                Marshal.StructureToPtr(calc, lParam, true);
            }
            catch { }
        }

        private static int AutoHideTaskbarEdge(IntPtr monitor)
        {
            try
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
                if (((int)SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) == 0) return -1;
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) == IntPtr.Zero) return -1;
                var c = new POINT { X = (abd.rc.Left + abd.rc.Right) / 2, Y = (abd.rc.Top + abd.rc.Bottom) / 2 };
                return MonitorFromPoint(c, MONITOR_DEFAULTTONEAREST) == monitor ? abd.uEdge : -1;
            }
            catch { return -1; }
        }

        /// <summary>Windows 11: rounded corners and a border in the Divider colour. Harmless no-op elsewhere.</summary>
        private void ApplyDwmAttributes()
        {
            try
            {
                if (Environment.OSVersion.Version.Major < 6) return;
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                int border = ColorTranslator.ToWin32(ThemeHelper.Palette.Divider);
                DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch { /* dwmapi missing or attribute unknown before Windows 11: nothing to do */ }
        }

        /// <summary>Plan §1.3 first-run self-check, once per process: the window must BE its client area, and the
        /// title bar must hit-test as the caption. If not, this process falls back to native chrome — this window is
        /// rebuilt with the Windows frame and every later AppForm starts native — and the reason is logged.</summary>
        private void SelfCheck()
        {
            string why = null;
            try
            {
                RECT wr;
                GetWindowRect(Handle, out wr);
                var client = RectangleToScreen(ClientRectangle);
                if (WindowState == FormWindowState.Normal && (wr.Left != client.Left || wr.Top != client.Top || wr.Right != client.Right || wr.Bottom != client.Bottom))
                    why = "window " + Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom) + " != client " + client;
                else if (Bar != null && Bar.Visible && Bar.Width > Px(200))
                {
                    var pt = PointToScreen(new Point(Bar.Width / 3, Bar.Height / 2));
                    int hit = (int)SendMessage(Handle, WM_NCHITTEST, IntPtr.Zero, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                    if (hit != HTCAPTION) why = "caption hit-test returned " + hit;
                }
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            if (why == null) { Logger.Diag("[CHROME] self-check passed"); return; }
            Logger.Diag("[CHROME] self-check FAILED (" + why + ") — falling back to native chrome for this process");
            _native = true; ChromeReason = "self-check failed: " + why;
            CustomChrome = false;
            Bar.Visible = false;
            Padding = Padding.Empty;
            FormBorderStyle = _resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;   // recreates the handle
            OnChromeFallback();
        }

        /// <summary>The self-check just switched this window to native chrome: the title bar is hidden. A subclass that put
        /// something in the bar (MainForm's proxy pill, step 2.7b) moves it here.</summary>
        protected virtual void OnChromeFallback() { }

        // ── theme ────────────────────────────────────────────────────────────────────────────────────────────

        private void OnThemeChangedInternal()
        {
            if (IsDisposed) return;
            Recolor();
            OnThemeChanged();
        }

        private void Recolor()
        {
            var p = ThemeHelper.Palette;
            BackColor = p.Window;
            Content.BackColor = p.Window;
            if (Bar != null) Bar.Recolor();
            if (IsHandleCreated && CustomChrome) ApplyDwmAttributes();
            Invalidate();
        }

        /// <summary>Called after the palette changed (light/dark, accent). Subclasses recolour their own content.</summary>
        protected virtual void OnThemeChanged() { }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ThemeHelper.ThemeChanged -= OnThemeChangedInternal;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChangedInternal;
            base.Dispose(disposing);
        }

        // ── interop ──────────────────────────────────────────────────────────────────────────────────────────
        private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;
        private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCACTIVATE = 0x0086, WM_GETMINMAXINFO = 0x0024;
        private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        private const int WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_ACTIVATE = 0x0006, WA_ACTIVE = 1;
        private const int WVR_REDRAW = 0x0300;
        public const int HTCLIENT = WindowHit.HTCLIENT, HTCAPTION = WindowHit.HTCAPTION, HTLEFT = WindowHit.HTLEFT,
                         HTRIGHT = WindowHit.HTRIGHT, HTTOP = WindowHit.HTTOP, HTTOPLEFT = WindowHit.HTTOPLEFT,
                         HTTOPRIGHT = WindowHit.HTTOPRIGHT, HTBOTTOM = WindowHit.HTBOTTOM,
                         HTBOTTOMLEFT = WindowHit.HTBOTTOMLEFT, HTBOTTOMRIGHT = WindowHit.HTBOTTOMRIGHT;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABS_AUTOHIDE = 1, ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private const int SM_MAXIMUMTOUCHES = 95;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA data);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
