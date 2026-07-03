using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Finger-pan scrolling ported from the CS-Ray TouchScroller pattern: raw WM_TOUCH
    /// (RegisterTouchWindow) is CONSUMED so it isn't promoted to mouse (touch and mouse stay
    /// separate). A drag past a small threshold pans the AutoScroll surface (driving the
    /// ThemedScrollBar via AutoScrollPosition); a quick tap synthesizes a real click on the
    /// control under the finger (chat select / bubble button); a held-still press synthesizes
    /// WM_CONTEXTMENU (the existing touch-and-hold menu). Mouse wheel, the themed thumb's mouse
    /// drag, and the native-scrollbar suppression are unaffected.
    ///
    /// Enable a surface with <see cref="Enable"/>; its existing and future child controls are
    /// registered automatically. Text inputs are NOT registered, so the touch keyboard logic
    /// (TouchKeyboard) keeps working independently.
    /// </summary>
    public static class TouchScroller
    {
        [DllImport("user32.dll")] private static extern bool RegisterTouchWindow(IntPtr hWnd, uint ulFlags);
        [DllImport("user32.dll")] private static extern bool GetTouchInputInfo(IntPtr hTouchInput, uint cInputs, [In, Out] TOUCHINPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] private static extern bool CloseTouchInputHandle(IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOUCHINPUT
        {
            public int x, y;
            public IntPtr hSource;
            public int dwID, dwFlags, dwMask, dwTime;
            public IntPtr dwExtraInfo;
            public int cxContact, cyContact;
        }

        private const int WM_TOUCH = 0x0240, WM_MOUSEWHEEL = 0x020A;
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B;
        private const int MK_LBUTTON = 0x0001;
        private const int TOUCHEVENTF_MOVE = 0x0001, TOUCHEVENTF_DOWN = 0x0002, TOUCHEVENTF_UP = 0x0004;

        // UI-FIX-T1: the pan/tap threshold is a finger-slop distance, so it scales with the system DPI
        // (TOUCHINPUT coords are physical px). RT 8.1 @100% (shipping target) => exactly 10, zero behavior
        // change; a 200% dev box => 20, making dev-box touch tuning representative. LongPressMs is TIME —
        // deliberately not scaled. Evaluated once at first use (after SetProcessDpiAwareness in Program.Run).
        private static readonly int MoveThreshold = ScaledMoveThreshold();
        private const int LongPressMs = 500;

        /// <summary>The resolved finger-slop threshold, for the [DPI] startup truth line. First access
        /// evaluates ScaledMoveThreshold — callers must read it AFTER SetProcessDpiAwareness.</summary>
        internal static int MoveThresholdPx { get { return MoveThreshold; } }

        private static int ScaledMoveThreshold()
        {
            int t = 10;
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    int dpi = GetDeviceCaps(dc, LOGPIXELSX);
                    ReleaseDC(IntPtr.Zero, dc);
                    if (dpi > 96) t = 10 * dpi / 96;
                }
            }
            catch { /* keep the 96-dpi default */ }
            System.Diagnostics.Debug.WriteLine("[HITTEST] touch MoveThreshold=" + t + "px");   // one-shot init line
            return t;
        }

        private static readonly List<KeyValuePair<ScrollableControl, bool>> _surfaces
            = new List<KeyValuePair<ScrollableControl, bool>>();   // (surface, horizontal)
        private static readonly List<TouchHook> _hooks = new List<TouchHook>();   // keep subclasses alive
        private static bool _wheelFilterInstalled;

        /// <summary>Raised after a finger-pan, a momentum tick, OR a redirected mouse wheel moves a surface.
        /// These set AutoScrollPosition directly (no native Scroll event), so hosts hook this to run paging /
        /// position checks, and the ThemedScrollBar hooks it so its thumb follows wheel + touch scrolling.</summary>
        public static event Action<ScrollableControl> Scrolled;

        /// <summary>Fire <see cref="Scrolled"/> for a surface (single funnel for pan / momentum / wheel).</summary>
        private static void RaiseScrolled(ScrollableControl s) { var h = Scrolled; if (h != null) h(s); }

        /// <summary>Enable finger-pan on a scroll surface (horizontal = pan along X, else Y).</summary>
        public static void Enable(ScrollableControl surface, bool horizontal)
        {
            if (surface == null) return;
            _surfaces.Add(new KeyValuePair<ScrollableControl, bool>(surface, horizontal));
            surface.Disposed += (s, e) => _surfaces.RemoveAll(kv => ReferenceEquals(kv.Key, surface));   // don't leak short-lived pickers/dialogs
            Register(surface);   // recursive (1.4): covers existing descendants and, via ControlAdded, future ones

            // Route the mouse wheel to the VERTICAL surface under the cursor, regardless of which control
            // has focus. On RT 8.1 WM_MOUSEWHEEL goes to the FOCUSED control (no "scroll under cursor"), so
            // after a touch tap moved focus off the message panel the wheel stopped scrolling it — this makes
            // wheel + touch coexist and survive every focus change. Installed once.
            if (!_wheelFilterInstalled)
            {
                _wheelFilterInstalled = true;
                Application.AddMessageFilter(new WheelFilter());
            }
        }

        /// <summary>Wheel-follows-cursor: redirects WM_MOUSEWHEEL to the registered scroll surface under the
        /// pointer so focus changes (e.g. a touch tap) can't disable wheel scrolling. VERTICAL surfaces are
        /// scrolled by re-posting the wheel; HORIZONTAL strips (folder bar, sticker pack bar, emoji category
        /// bar) are scrolled along X directly (the native wheel only ever moves the vertical axis).</summary>
        private sealed class WheelFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                var p = Cursor.Position;
                ScrollableControl surface;
                try { surface = SurfaceOf(WindowFromPoint(new POINT(p.X, p.Y))); }
                catch { return false; }
                if (surface == null || !surface.IsHandleCreated) return false;

                // 3.2: wheel on the coasting surface → the wheel takes over; the coast stops first.
                if (_mSurface != null && ReferenceEquals(surface, _mSurface)) StopMomentum("catch");

                if (IsHorizontal(surface))
                {
                    // A horizontal strip doesn't scroll on the native wheel (WinForms wheels the vertical axis).
                    // Scroll its X by the wheel delta — the Settings tab strip's offset-by-delta*48 logic —
                    // and CONSUME it (so it can't cross-talk to a focused vertical list). Focus-independent
                    // (WindowFromPoint), so it works on RT 8.1 where the wheel would otherwise go to focus.
                    int delta = (short)(((uint)(long)m.WParam) >> 16);   // HIWORD of wParam = signed wheel delta
                    int curX = -surface.AutoScrollPosition.X;
                    surface.AutoScrollPosition = new Point(curX - Math.Sign(delta) * 48, -surface.AutoScrollPosition.Y);
                    RaiseScrolled(surface);                     // themed thumb + paging follow the wheel
                    return true;
                }

                if (surface.Handle == m.HWnd)
                {
                    // Wheel already targets the surface → let it scroll normally, then refresh the themed
                    // thumb AFTER the scroll applies (next pump iteration).
                    var s2 = surface;
                    try { s2.BeginInvoke((Action)(() => RaiseScrolled(s2))); } catch { }
                    return false;
                }
                SendMessage(surface.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);   // scroll the hovered vertical surface
                RaiseScrolled(surface);                         // themed thumb + paging follow the wheel
                return true;                                    // and suppress the focus-based delivery
            }
        }

        /// <summary>
        /// Registers a single control for WM_TOUCH without making it a pan surface — use for controls
        /// nested below the scroll surface (e.g. tiles in an inner FlowLayoutPanel) so touch-and-hold
        /// long-press fires over them; panning still resolves to the ancestor scroll surface.
        /// </summary>
        public static void RegisterControl(Control c) => Register(c);

        // TOUCH-CLASSIFY 1.4: once-per-instance guard so the recursive registration can meet the same
        // control from multiple paths (Enable loop, ControlAdded, manual RegisterControl) without stacking
        // duplicate ControlAdded subscriptions. Weak — controls age out with the GC, nothing to unhook.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _registered
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, object>();

        private static void Register(Control c)
        {
            if (c == null) return;
            if (c is TextBoxBase) return;   // text inputs stay native: the touch-keyboard flow needs real OS touch promotion
            object seen;
            if (_registered.TryGetValue(c, out seen)) return;   // once per control instance
            _registered.Add(c, null);

            void DoReg()
            {
                if (!c.IsHandleCreated) return;
                foreach (var h in _hooks) if (h.Handle == c.Handle) return;   // already hooked this handle → don't double-subclass

                try { RegisterTouchWindow(c.Handle, 0); } catch { }

                // WM_TOUCH is delivered to the window's WndProc (NOT through IMessageFilter — confirmed
                // by instrumentation), so subclass the handle with a NativeWindow to intercept it there.
                var hook = new TouchHook();
                try { hook.AssignHandle(c.Handle); _hooks.Add(hook); } catch { return; }
                c.HandleDestroyed += (s, e) => { try { hook.ReleaseHandle(); } catch { } _hooks.Remove(hook); };
            }
            if (c.IsHandleCreated) DoReg();
            c.HandleCreated += (s, e) => DoReg();

            // TOUCH-CLASSIFY 1.4: register the WHOLE subtree, present and future. Any descendant left
            // unregistered gets OS mouse-promotion — activations that bypass the tap classifier entirely
            // (settings card rows and downloads-panel row buttons were exactly that leak).
            foreach (Control child in c.Controls) Register(child);
            c.ControlAdded += (s, e) => Register(e.Control);
        }

        // ── Active gesture (single primary touch, tracked globally by id) ──
        private static bool _active, _dragging, _consumed;
        private static bool _caughtCoast;                    // this gesture's DOWN stopped a coast (tap/long-press ineligible)
        private static bool _cooldownAtDown;                 // gesture began inside a scroll-end cooldown (judged at DOWN)
        private static ScrollableControl _cooldownSurface;   // surface whose pan/coast last ended…
        private static long _cooldownEndTs;                  // …and when (Stopwatch ticks) — see InCooldown
        private static int _touchId, _startTick;
        private static ScrollableControl _surface;
        private static IntPtr _targetHwnd;
        private static Point _startScreen, _lastScreen;
        private static Timer _holdTimer;

        // SCROLL-SMOOTH-T1 Part 3: WM_TOUCH MOVEs can exceed 100Hz — each Pan() is a blit + ~N child
        // repositions, so panning per-move floods the paint pipeline with incoherent frames. Deltas are
        // COALESCED to at most one Pan per ~10ms (momentum's 16ms timer already complies); the velocity
        // ring keeps sampling RAW positions/timestamps, so flick physics are unaffected.
        private const int PanCoalesceMs = 10;
        private static int _pendDx, _pendDy;
        private static long _lastPanTs;

        // ── Momentum / flick inertia (TOUCH-MOMENTUM) ─────────────────────
        // All physics are TIME-based (closed-form exponential integral per tick): on Tegra 3 the timer drops
        // ticks, and that must change smoothness only — never the trajectory. Velocities are finger-space px/s
        // (physical px, the same space as MoveThreshold). THE TUNING SURFACE:
        private const double FlickMinVelocity = 150.0;  // below this a release is a positioning drag → no coast
        private const double FlickMaxVelocity = 5000.0; // clamp — a wild swipe must not coast for ten seconds
        private const double DecayLambda = 2.0;         // v(t)=v0·e^(−λt); total coast distance = v0/λ (≈750px for a 1500px/s flick)
        private const double StopVelocity = 30.0;       // px/s — the tail is invisible below this → stop (reason=decay)
        private const int MomentumTickMs = 16;          // timer cadence — affects smoothness only (dt is measured, not assumed)
        private const int VelocityWindowMs = 100;       // trailing sample window for the release velocity (jitter-resistant)

        // ── Tap classification (TOUCH-CLASSIFY) ───────────────────────────
        // Displacement alone cannot classify a tap: catches, fast micro-strokes and digitizer contact
        // splits all lift under MoveThreshold yet are scroll interactions — and under the tap grammar an
        // accidental tap PAUSES downloads / switches chats. Suppression reasons are logged per event.
        private const double TapMaxVelocity = 150.0;    // = FlickMinVelocity: lifting faster than a flick-start is a stroke, never a tap
        private const int ScrollEndCooldownMs = 150;    // tap blackout after a pan/coast END on the same surface (contact splits, instant re-touches)

        // Velocity ring: (screen point, Stopwatch timestamp) pushed on DOWN + every MOVE + UP of the tracked touch.
        private const int VelSamples = 8;
        private static readonly Point[] _velPt = new Point[VelSamples];
        private static readonly long[] _velTs = new long[VelSamples];
        private static int _velCount;                    // pushed this gesture (ring slot = count % VelSamples)

        // Single active-momentum slot (one finger = one coast; a new flick replaces any running one).
        private static ScrollableControl _mSurface;
        private static double _mVel;                     // signed finger-space px/s along the surface's pan axis
        private static double _mCarry;                   // fractional-px accumulator — the slow tail must not stall <1px/tick
        private static long _mLastTs;
        private static Timer _mTimer;                    // ONE shared UI-thread timer, running only while coasting

        private static ScrollableControl SurfaceOf(IntPtr hwnd)
        {
            var c = Control.FromHandle(hwnd);
            while (c != null)
            {
                foreach (var s in _surfaces) if (ReferenceEquals(s.Key, c)) return s.Key;
                c = c.Parent;
            }
            return null;
        }

        private static bool IsHorizontal(ScrollableControl s)
        {
            foreach (var su in _surfaces) if (ReferenceEquals(su.Key, s)) return su.Value;
            return false;
        }

        /// <summary>Subclasses a registered control's handle to intercept WM_TOUCH at the WndProc.</summary>
        private sealed class TouchHook : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_TOUCH)
                {
                    ProcessWmTouch(ref m);
                    return;                  // consume — don't forward → not promoted to mouse
                }
                base.WndProc(ref m);
            }
        }

        private static void ProcessWmTouch(ref Message m)
        {
            int count = (int)((long)m.WParam & 0xFFFF);
            if (count <= 0) return;
            var inputs = new TOUCHINPUT[count];
            if (!GetTouchInputInfo(m.LParam, (uint)count, inputs, Marshal.SizeOf(typeof(TOUCHINPUT)))) return;
            try { HandleTouch(inputs, m.HWnd); }
            catch (Exception ex)
            {
                // A throw mid-gesture (host Scrolled handler, disposed surface, layout) must not leave the
                // classifier armed: _active=true makes every future DOWN a no-op — touch dies for the session.
                // Release the gesture, keep scrolling alive, and NAME the culprit in the log.
                _active = false; _dragging = false; _caughtCoast = false; _surface = null; _targetHwnd = IntPtr.Zero;
                StopHoldTimer();
                if (TelegArm.Helpers.Logger.Enabled)
                    Debug.WriteLine("[HITTEST] gesture aborted by exception: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { CloseTouchInputHandle(m.LParam); }
        }

        private static bool HandleTouch(TOUCHINPUT[] inputs, IntPtr hwnd)
        {
            var surface = SurfaceOf(hwnd);
            if (surface == null && !_active) return false;   // not our surface → let it promote

            foreach (var ti in inputs)
            {
                var pt = new Point(ti.x / 100, ti.y / 100);   // hundredths of a px → screen px

                if ((ti.dwFlags & TOUCHEVENTF_DOWN) != 0)
                {
                    // 3.1 THE CATCH: a finger landing anywhere stops a running coast dead. TOUCH-CLASSIFY 1.1:
                    // catching a coast on YOUR OWN surface marks the gesture scroll-interaction — it may still
                    // pan/flick, but it can never tap or long-press (the finger came down to stop content, not to aim).
                    var caught = _mSurface;                       // capture before StopMomentum nulls it
                    if (_mSurface != null) StopMomentum("catch");
                    // Watchdog: a tracked gesture whose UP was lost (id reuse proves the old contact ended;
                    // 15s outlives any real press) would otherwise eat every future DOWN — reset and proceed.
                    if (_active && (ti.dwID == _touchId || Environment.TickCount - _startTick > 15000))
                    {
                        _active = false; _dragging = false; StopHoldTimer();
                        if (TelegArm.Helpers.Logger.Enabled) Debug.WriteLine("[HITTEST] stale gesture reset (lost UP, id=" + _touchId + ")");
                    }
                    if (_active || surface == null) continue;     // track only the primary touch
                    _active = true; _dragging = false; _consumed = false;
                    // ANY caught coast consumes — a finger stopping fast-moving content often lands on an
                    // adjacent surface (folder bar next to the coasting chat list), and it still came down
                    // to stop, not to aim. Cooldown is judged at DOWN: the re-touch is the event, not its lift.
                    _caughtCoast = caught != null;
                    _cooldownAtDown = InCooldown(surface);
                    _touchId = ti.dwID; _surface = surface; _targetHwnd = hwnd;
                    _startScreen = _lastScreen = pt; _startTick = Environment.TickCount;
                    _velCount = 0; PushVelSample(pt);             // velocity ring starts at the down point
                    _pendDx = 0; _pendDy = 0; _lastPanTs = 0;     // coalescer reset (first pan applies immediately)
                    if (!_caughtCoast) StartHoldTimer();          // 1.1: a catching gesture is long-press ineligible
                }
                else if ((ti.dwFlags & TOUCHEVENTF_MOVE) != 0)
                {
                    if (!_active || ti.dwID != _touchId) continue;
                    PushVelSample(pt);
                    if (!_dragging &&
                        (Math.Abs(pt.X - _startScreen.X) > MoveThreshold || Math.Abs(pt.Y - _startScreen.Y) > MoveThreshold))
                    {
                        _dragging = true;
                        StopHoldTimer();   // moved → it's a pan, not a long-press
                    }
                    if (_dragging)
                    {
                        _pendDx += pt.X - _lastScreen.X;
                        _pendDy += pt.Y - _lastScreen.Y;
                        long now = Stopwatch.GetTimestamp();
                        if (now - _lastPanTs >= Stopwatch.Frequency * PanCoalesceMs / 1000)
                        {
                            Pan(_surface, _pendDx, _pendDy);
                            _pendDx = 0; _pendDy = 0;
                            _lastPanTs = now;
                        }
                    }
                    _lastScreen = pt;
                }
                else if ((ti.dwFlags & TOUCHEVENTF_UP) != 0)
                {
                    if (!_active || ti.dwID != _touchId) continue;
                    StopHoldTimer();
                    // Flush any coalesced remainder BEFORE classification/flick, so the surface lands
                    // exactly where the finger ended (boundary/stop logic unchanged).
                    if (_dragging && (_pendDx != 0 || _pendDy != 0)) Pan(_surface, _pendDx, _pendDy);
                    _pendDx = 0; _pendDy = 0;
                    // Fire a tap ONLY for a genuinely stationary press: never flagged as a drag AND lifted
                    // within the threshold of the down point. The up-point check also catches a fast flick
                    // that scrolled with too few MOVE samples to trip _dragging — so a scroll never taps.
                    bool moved = _dragging
                                 || Math.Abs(pt.X - _startScreen.X) > MoveThreshold
                                 || Math.Abs(pt.Y - _startScreen.Y) > MoveThreshold;
                    PushVelSample(pt);                            // release sample — velocity feeds the tap gate AND the flick
                    double vel = ReleaseVelocity(_surface);       // signed px/s along the pan axis (0 = unmeasurable)
                    if (!moved && !_consumed)
                    {
                        // TOUCH-CLASSIFY: displacement alone doesn't make a tap — the gesture must also not be
                        // a coast-catch (1.1), not land in a scroll-end cooldown (1.3), and lift slow (1.2).
                        string suppress = _caughtCoast ? "coast-catch"
                                        : _cooldownAtDown ? "cooldown"
                                        : Math.Abs(vel) >= TapMaxVelocity ? "velocity v=" + (int)vel
                                        : null;
                        if (suppress == null)
                        {
                            if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] touch: stationary tap → synthesize click");
                            SynthesizeTap(_targetHwnd, _startScreen);
                        }
                        else if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] tap suppressed reason=" + suppress);
                    }
                    else if (_dragging)
                        if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[HITTEST] touch: scroll gesture → tap suppressed");
                    if (_dragging) { _cooldownSurface = _surface; _cooldownEndTs = Stopwatch.GetTimestamp(); }   // 1.3: pan end arms the cooldown
                    // Flick: a scroll-classified release coasts; a small-but-fast stroke (1.2) also feeds the
                    // flick path — its energy belongs to the scroll. A long-press release is _consumed (menu showing).
                    if (!_consumed && _surface != null && (moved || Math.Abs(vel) >= FlickMinVelocity)) TryFlick(_surface);
                    _active = false; _dragging = false; _caughtCoast = false; _surface = null; _targetHwnd = IntPtr.Zero;
                }
            }
            return true;
        }

        private static void Pan(ScrollableControl s, int ddx, int ddy)
        {
            if (s == null) return;
            int curX = -s.AutoScrollPosition.X, curY = -s.AutoScrollPosition.Y;
            // Natural: drag the content WITH the finger (finger down → reveal earlier content above).
            s.AutoScrollPosition = IsHorizontal(s)
                ? new Point(curX - ddx, curY)
                : new Point(curX, curY - ddy);
            RaiseScrolled(s);   // touch moved the viewport → thumb + host paging checks follow
        }

        private static void SynthesizeTap(IntPtr hwnd, Point screenPt)
        {
            try
            {
                var c = Control.FromHandle(hwnd);
                if (c == null) return;
                Point cp = c.PointToClient(screenPt);
                IntPtr lp = (IntPtr)(((cp.Y & 0xFFFF) << 16) | (cp.X & 0xFFFF));
                PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
                PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
            }
            catch { }
        }

        // ── Momentum engine ────────────────────────────────────────────────

        private static void PushVelSample(Point pt)
        {
            int i = _velCount % VelSamples;
            _velPt[i] = pt; _velTs[i] = Stopwatch.GetTimestamp();
            _velCount++;
        }

        /// <summary>Release velocity = displacement over the trailing ~100ms of ring samples (NOT last-two —
        /// jitter-resistant), signed, along the surface's pan axis. 0 when unmeasurable (dt &lt; 15ms).</summary>
        private static double ReleaseVelocity(ScrollableControl s)
        {
            if (s == null || _velCount < 2) return 0;
            int newest = (_velCount - 1) % VelSamples;
            long now = _velTs[newest];
            long window = Stopwatch.Frequency * VelocityWindowMs / 1000;
            int avail = Math.Min(_velCount, VelSamples);
            int anchor = newest;
            for (int back = 1; back < avail; back++)
            {
                anchor = (_velCount - 1 - back) % VelSamples;
                if (now - _velTs[anchor] >= window) break;   // reached the window edge → anchor there
            }
            double dt = (now - _velTs[anchor]) / (double)Stopwatch.Frequency;
            if (dt < 0.015)
            {
                // Whole gesture shorter than the honest-measurement floor: judge it by TOTAL displacement
                // over a clamped-minimum duration instead of returning "no velocity" — an unmeasurably fast
                // near-threshold stroke is a scroll stroke (≈1500px/s), not a tap. Displacement under half
                // the finger-slop threshold stays velocity-0: a genuine ultra-quick tap with tiny jitter
                // must keep tapping. (Shipping RT digitizer samples at ~60Hz, so this path is rare there.)
                int disp = IsHorizontal(s)
                    ? _velPt[newest].X - _velPt[anchor].X
                    : _velPt[newest].Y - _velPt[anchor].Y;
                if (Math.Abs(disp) < MoveThreshold / 2) return 0;
                return disp / Math.Max(dt, 0.008);
            }
            return IsHorizontal(s)
                ? (_velPt[newest].X - _velPt[anchor].X) / dt
                : (_velPt[newest].Y - _velPt[anchor].Y) / dt;
        }

        /// <summary>TOUCH-CLASSIFY 1.3: true while the surface is inside the post-scroll tap blackout —
        /// armed at pan end and coast end ONLY (never by taps, so deliberate double-taps are unaffected).</summary>
        private static bool InCooldown(ScrollableControl s)
        {
            return _cooldownSurface != null && ReferenceEquals(_cooldownSurface, s)
                && Stopwatch.GetTimestamp() - _cooldownEndTs < Stopwatch.Frequency * ScrollEndCooldownMs / 1000;
        }

        /// <summary>Starts a coast when the release velocity clears FlickMinVelocity on the surface's pan axis.</summary>
        private static void TryFlick(ScrollableControl s)
        {
            double v = ReleaseVelocity(s);
            if (Math.Abs(v) < FlickMinVelocity) return;      // positioning drag → release stays put
            if (v > FlickMaxVelocity) v = FlickMaxVelocity;
            else if (v < -FlickMaxVelocity) v = -FlickMaxVelocity;
            StartMomentum(s, v);
        }

        private static void StartMomentum(ScrollableControl s, double v)
        {
            _mSurface = s; _mVel = v; _mCarry = 0; _mLastTs = Stopwatch.GetTimestamp();
            if (_mTimer == null)
            {
                _mTimer = new Timer { Interval = MomentumTickMs };
                _mTimer.Tick += (s2, e2) => MomentumTick();
            }
            _mTimer.Start();
            if (TelegArm.Helpers.Logger.Enabled)
                Debug.WriteLine("[HITTEST] flick v=" + (int)v + " ctrl="
                    + (string.IsNullOrEmpty(s.Name) ? s.GetType().Name : s.Name));
        }

        /// <summary>True while a finger gesture is tracked or a coast is running. Hosts use it to DEFER
        /// window-mutating self-heals: rebuilding a panel disposes the control under the finger, which
        /// kills the WM_TOUCH stream mid-gesture (TOUCH-FREEZE conviction).</summary>
        public static bool GestureOrCoastActive { get { return _active || _mSurface != null; } }

        /// <summary>Hard-stops any running coast. PUBLIC for hosts: call before a programmatic scroll or a
        /// content swap (chat switch, jump-to-message, scroll-to-bottom) so momentum can't fight it. No-op
        /// (and no log line) when nothing is coasting.</summary>
        public static void StopMomentum() { StopMomentum("switch"); }

        private static void StopMomentum(string reason)
        {
            if (_mSurface == null) return;
            _cooldownSurface = _mSurface; _cooldownEndTs = Stopwatch.GetTimestamp();   // 1.3: coast end arms the tap cooldown
            _mSurface = null; _mVel = 0; _mCarry = 0;
            if (_mTimer != null) _mTimer.Stop();
            if (TelegArm.Helpers.Logger.Enabled) Debug.WriteLine("[HITTEST] coast end reason=" + reason);
        }

        private static void MomentumTick()
        {
            var s = _mSurface;
            if (s == null) { if (_mTimer != null) _mTimer.Stop(); return; }
            // 3.4: the host went away or is hidden (dialog closed, page switched) → stop, no callbacks into it.
            if (s.IsDisposed || !s.IsHandleCreated || !s.Visible) { StopMomentum("switch"); return; }

            long now = Stopwatch.GetTimestamp();
            double dt = (now - _mLastTs) / (double)Stopwatch.Frequency;
            _mLastTs = now;
            if (dt <= 0) return;

            // Closed-form integration: distance over this tick = ∫ v0·e^(−λt) dt = v0·(1−e^(−λ·dt))/λ.
            // Exact for ANY dt, so dropped/late ticks (Tegra 3) change smoothness — never the trajectory.
            double decay = Math.Exp(-DecayLambda * dt);
            double move = _mVel * (1 - decay) / DecayLambda + _mCarry;
            _mVel *= decay;

            int px = (int)move;
            _mCarry = move - px;

            if (px != 0)
            {
                Point before = s.AutoScrollPosition;
                Pan(s, px, px);   // the SAME path as live pans: axis picked by IsHorizontal, raises Scrolled
                if (s.AutoScrollPosition == before) { StopMomentum("boundary"); return; }   // clamped at the edge → stop dead
            }

            if (Math.Abs(_mVel) < StopVelocity) StopMomentum("decay");
        }

        private static void StartHoldTimer()
        {
            if (_holdTimer == null)
            {
                _holdTimer = new Timer { Interval = LongPressMs };
                _holdTimer.Tick += (s, e) =>
                {
                    _holdTimer.Stop();
                    if (_active && !_dragging && !_consumed)
                    {
                        // Held still → fire the existing touch-and-hold context menu (screen coords).
                        IntPtr lp = (IntPtr)(((_startScreen.Y & 0xFFFF) << 16) | (_startScreen.X & 0xFFFF));
                        try { PostMessage(_targetHwnd, WM_CONTEXTMENU, _targetHwnd, lp); } catch { }
                        _consumed = true;
                    }
                };
            }
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private static void StopHoldTimer() { if (_holdTimer != null) _holdTimer.Stop(); }
    }
}
