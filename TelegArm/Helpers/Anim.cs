using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Core;

namespace TelegArm.Helpers
{
    /// <summary>What moves. Each kind has its own switch in Settings (Appearance → Animations) under the master switch,
    /// so one that misbehaves on a device can be turned off alone.</summary>
    public enum AnimKind
    {
        /// <summary>On/off switches slide.</summary>
        Toggles,
        /// <summary>Hover and press colours fade.</summary>
        Hover,
        /// <summary>The hamburger menu slides.</summary>
        Drawer,
        /// <summary>Popups and menus fade in.</summary>
        Popups,
        /// <summary>The message list glides to the bottom and to a message (stage 3).</summary>
        Scroll,
        /// <summary>In a narrow window (one column) the list and the chat slide (stage 3).</summary>
        Columns,
    }

    /// <summary>One thing on the animation clock. <see cref="Step"/> gets the milliseconds since the last tick and answers
    /// false when it has finished (it is then taken off the clock).</summary>
    internal interface IAnimStep { bool Step(float dtMs); }

    /// <summary>
    /// Owner 2026-11-13 ("cant we add animations to make it feel smooth?"): the ONE clock every small animation runs on.
    /// A single WinForms timer on the UI thread that runs only while something is moving (about 60 frames a second on a
    /// PC, 30 on ARM32: the Surface RT) and stops by itself. Time is measured, never assumed from the interval, so a busy
    /// UI thread makes an animation skip ahead instead of running long. Nothing here paints: a step changes a number and
    /// invalidates its own control.
    /// <para>Off switches: the master "Animations" setting, the kind's own setting, Windows' own "Animation effects"
    /// (also off over Remote Desktop; the "Also when Windows has animations off" setting overrides it), and
    /// <see cref="Suppressed"/> (the shot rig, whose screenshots must not catch a popup half faded). When off, every
    /// animation lands on its end state at once, so callers never branch.</para>
    /// </summary>
    public static class Anim
    {
        // How long things take, in milliseconds. Short on purpose: an animation here confirms what happened, it is not a show.
        public const int HoverInMs = 80, HoverOutMs = 170;     // a highlight arrives fast and leaves softly
        public const int PressInMs = 40, PressOutMs = 200;
        public const int ToggleMs = 160;
        public const int DrawerInMs = 190, DrawerOutMs = 140;
        public const int PopupMs = 120, MenuMs = 90;
        public const int GlideMs = 280, NewMessageMs = 200, ColumnMs = 220;

        // ── switches ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The shot rig: every animation lands on its end state at once (its screenshots are taken right after a
        /// popup shows).</summary>
        public static bool Suppressed { get; set; }

        private static bool _sysOn = true;
        private static int _sysReadAt;
        private static bool _sysRead;

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        /// <summary>Windows' "Show animations in Windows" (Ease of Access; Performance Options' "Animate controls and
        /// elements inside windows"). Asked at most once every 3 seconds, and only from an event handler: never in a
        /// paint. A failed call reads as on.</summary>
        public static bool SystemAnimationsOn
        {
            get
            {
                int now = Environment.TickCount;
                if (!_sysRead || unchecked(now - _sysReadAt) > 3000)
                {
                    _sysRead = true; _sysReadAt = now;
                    try
                    {
                        int on = 1;
                        _sysOn = !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0) || on != 0;
                    }
                    catch { _sysOn = true; }
                }
                return _sysOn;
            }
        }

        /// <summary>Is this kind of animation on right now?</summary>
        public static bool Enabled(AnimKind kind)
        {
            if (Suppressed) return false;
            AppSettings s;
            try { s = AppSettings.Instance; } catch { return false; }
            if (s == null || !s.Animations) return false;
            bool own;
            switch (kind)
            {
                case AnimKind.Toggles: own = s.AnimateToggles; break;
                case AnimKind.Hover: own = s.AnimateHover; break;
                case AnimKind.Drawer: own = s.AnimateDrawer; break;
                case AnimKind.Scroll: own = s.AnimateScroll; break;
                case AnimKind.Columns: own = s.AnimateColumns; break;
                default: own = s.AnimatePopups; break;
            }
            return own && (s.AnimateDespiteSystem || SystemAnimationsOn);
        }

        /// <summary>A control that can show a frame: alive, with a handle, visible.</summary>
        internal static bool Alive(Control c)
        {
            try { return c != null && !c.IsDisposed && !c.Disposing && c.IsHandleCreated && c.Visible; }
            catch { return false; }
        }

        internal static bool CanAnimate(Control owner, AnimKind kind) { return Alive(owner) && Enabled(kind) && OnItsThread(owner); }

        /// <summary>Called on the control's own (UI) thread: the clock's timer lives there. (Not asked of the
        /// synchronization context: WinForms takes that away again whenever a DoEvents loop ends.)</summary>
        private static bool OnItsThread(Control c)
        {
            try { return c != null && !c.InvokeRequired; } catch { return false; }
        }

        // ── easing ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Fast start, soft landing (things that arrive: a drawer, a popup).</summary>
        public static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        /// <summary>Soft start and soft landing (things that travel between two rests: a switch knob).</summary>
        public static float EaseInOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        public static int Lerp(int a, int b, float t) { return a + (int)Math.Round((b - a) * t); }

        // ── the clock ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The timer interval. Windows rounds a timer UP to its 15.6 ms tick: 15 gives a PC about 64 frames a
        /// second; ARM32 (Tegra 3) gets 30, which lands on two ticks (31 ms, about 32 frames a second: near the sticker
        /// animator's rate). 33 would land on THREE ticks (47 ms): measured by the rig, 2026-11-13.</summary>
        public static int FrameMs { get { return Platform.IsArm32 ? 30 : 15; } }

        private static Timer _timer;
        private static readonly List<IAnimStep> _items = new List<IAnimStep>();
        private static readonly List<IAnimStep> _pass = new List<IAnimStep>();   // the tick's snapshot, reused
        private static long _last;

        // what the frame-time rig reads
        private static int _ticks;
        private static float _worstGapMs, _totalMs;
        private static readonly float[] _gaps = new float[48];

        /// <summary>Ticks, their total time and the longest gap between two since <see cref="ResetStats"/>.</summary>
        public static void ReadStats(out int ticks, out float totalMs, out float worstGapMs)
        {
            ticks = _ticks; totalMs = _totalMs; worstGapMs = _worstGapMs;
        }

        public static void ResetStats() { _ticks = 0; _totalMs = 0f; _worstGapMs = 0f; _lateStarts = 0; }

        private static int _lateStarts;
        /// <summary>Tweens since <see cref="ResetStats"/> that landed at once because their first tick came too late (the
        /// UI thread was busy): on a slow device this says an animation was skipped, not that it failed.</summary>
        public static int LateStarts { get { return _lateStarts; } }
        internal static void NoteLateStart() { _lateStarts++; }

        /// <summary>The gaps between the ticks since <see cref="ResetStats"/>, in order (the first 48), in milliseconds.</summary>
        public static string GapsText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(_ticks, _gaps.Length); i++) sb.Append(i == 0 ? "" : " ").Append(_gaps[i].ToString("0"));
            return sb.ToString();
        }

        /// <summary>How many things are moving now (0 = the clock is stopped).</summary>
        public static int Active { get { return _items.Count; } }

        /// <summary>Puts a step on the clock (UI thread). The clock starts with the first one and stops after the last.</summary>
        internal static void Add(IAnimStep step)
        {
            if (step == null) return;
            if (!_items.Contains(step)) _items.Add(step);
            if (_timer == null)
            {
                _timer = new Timer { Interval = FrameMs };
                _timer.Tick += Tick;
            }
            if (!_timer.Enabled)
            {
                _last = Stopwatch.GetTimestamp();
                _timer.Start();
            }
        }

        private static bool _inTick;

        private static void Tick(object sender, EventArgs e)
        {
            // A step that ever pumped messages (a dialog, DoEvents) would bring the next tick in here while this one is
            // still walking its snapshot (review 2026-11-13: none does today). That tick is skipped; the time is not lost.
            if (_inTick) return;
            _inTick = true;
            try { TickCore(); }
            finally { _inTick = false; }
        }

        private static void TickCore()
        {
            long now = Stopwatch.GetTimestamp();
            float dt = (now - _last) * 1000f / Stopwatch.Frequency;
            _last = now;
            if (dt < 0f) dt = 0f;
            if (_ticks < _gaps.Length) _gaps[_ticks] = dt;
            _ticks++; _totalMs += dt;
            if (dt > _worstGapMs) _worstGapMs = dt;

            _pass.Clear();
            _pass.AddRange(_items);   // a step may add or finish others
            for (int i = 0; i < _pass.Count; i++)
            {
                bool more;
                try { more = _pass[i].Step(dt); }
                catch (Exception ex) { more = false; Logger.Diag("[ANIM] a step threw, dropped: " + ex.GetType().Name + ": " + ex.Message); }
                if (!more) _items.Remove(_pass[i]);
            }
            _pass.Clear();
            if (_items.Count == 0 && _timer != null) _timer.Stop();
        }

        // ── one-shot tweens ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs <paramref name="step"/> with 0..1 (linear time: ease inside) over <paramref name="ms"/>, then
        /// <paramref name="done"/>. When this kind is off, or the owner cannot show a frame, it lands at once: step(1) and
        /// done() run before this returns. The owner going away mid-way also lands it.</summary>
        public static Tween Run(Control owner, AnimKind kind, int ms, Action<float> step, Action done = null)
        {
            return Run(owner, kind, ms, step, done, 250);
        }

        /// <param name="maxWaitMs">How late the first frame may come and the tween still play (else it lands at once). 250
        /// for a popup (it sits at opacity 0 meanwhile); longer where the start state is a picture of what was there.</param>
        public static Tween Run(Control owner, AnimKind kind, int ms, Action<float> step, Action done, int maxWaitMs)
        {
            var t = new Tween(owner, ms, step, done, maxWaitMs);
            if (ms <= 0 || !CanAnimate(owner, kind)) { t.Finish(); return t; }
            Add(t);
            return t;
        }

        /// <summary>Fades a top-level popup in. Call BEFORE the first Show: the window is then created layered at 0 and
        /// rises once it is on the screen (a constant-alpha layered window is blended by the compositor: no repaint per
        /// frame). It ends just under 1: reaching 1 would take the layered style off a live window, a visible flip.
        /// Off = nothing is touched.</summary>
        public static void FadeIn(Form popup, int ms = 120)
        {
            if (popup == null || popup.IsDisposed || !Enabled(AnimKind.Popups) || !OnItsThread(popup)) return;
            try { popup.Opacity = 0d; } catch { return; }
            // VisibleChanged, not Shown: Shown is raised only the first time a form shows. Run lands at once (fully
            // visible) when it cannot animate, so the popup can never be left invisible.
            EventHandler shown = null;
            shown = (s, e) =>
            {
                if (!popup.Visible) return;
                popup.VisibleChanged -= shown;
                Run(popup, AnimKind.Popups, ms, t => SetOpacity(popup, t));
            };
            popup.VisibleChanged += shown;
        }

        private const double Opaque = 0.996;   // 254 of 255: stays layered

        private static void SetOpacity(Form f, float t)
        {
            if (f == null || f.IsDisposed) return;
            try { f.Opacity = t >= 1f ? Opaque : Opaque * EaseOut(t); } catch { }
        }

        /// <summary>As <see cref="FadeIn(Form, int)"/> for a menu or a drop-down (a ToolStripDropDown has its own Opacity).
        /// Call before Show; the fade starts when it has opened.</summary>
        public static void FadeIn(ToolStripDropDown drop, int ms = 100)
        {
            if (drop == null || drop.IsDisposed || !Enabled(AnimKind.Popups) || !OnItsThread(drop)) return;
            try { drop.Opacity = 0d; } catch { return; }
            EventHandler opened = null;
            opened = (s, e) =>
            {
                drop.Opened -= opened;
                Run(drop, AnimKind.Popups, ms, t =>
                {
                    if (drop.IsDisposed) return;
                    try { drop.Opacity = t >= 1f ? Opaque : Opaque * EaseOut(t); } catch { }
                });
            };
            drop.Opened += opened;
        }
    }

    /// <summary>A one-shot animation started by <see cref="Anim.Run"/>.</summary>
    public sealed class Tween : IAnimStep
    {
        private readonly Control _owner;
        private readonly float _ms;
        private readonly Action<float> _step;
        private readonly Action _done;
        private float _elapsed;
        private bool _finished, _started;
        private readonly long _born = System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>The longest a tween waits for its first tick and still plays. Past it the UI thread was busy for that
        /// long with the thing on the screen at its START state (a popup at opacity 0: invisible, and it has the focus):
        /// it lands at once instead of adding its own duration to the wait (review 2026-11-13).</summary>
        private const double MaxWaitMs = 250;   // the default of _maxWaitMs (Anim.Run without one)

        internal Tween(Control owner, int ms, Action<float> step, Action done, int maxWaitMs = 250)
        {
            _owner = owner; _ms = Math.Max(1, ms); _step = step; _done = done; _maxWaitMs = maxWaitMs;
        }

        private readonly double _maxWaitMs;

        public bool Finished { get { return _finished; } }

        /// <summary>Drops it where it is: no end state, no done callback (something else takes the control over).</summary>
        public void Cancel() { _finished = true; }

        /// <summary>Lands it now: the end state, then the done callback (once).</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            try { if (_step != null) _step(1f); } catch (Exception ex) { Logger.Diag("[ANIM] tween end threw: " + ex.GetType().Name + ": " + ex.Message); }
            try { if (_done != null) _done(); } catch (Exception ex) { Logger.Diag("[ANIM] tween done threw: " + ex.GetType().Name + ": " + ex.Message); }
        }

        bool IAnimStep.Step(float dtMs)
        {
            if (_finished) return false;
            if (!Anim.Alive(_owner)) { Finish(); return false; }
            // The first tick is time zero. A timer tick is the LOWEST-priority message: it comes after the paints that
            // showing a popup or a drawer causes, and counting that wait as animation time made a popup arrive already
            // faded in (the emoji picker: 85 ms of its 120). Later gaps do count: a busy thread skips frames, never time.
            if (_started) _elapsed += dtMs;
            else
            {
                _started = true;
                double waited = (System.Diagnostics.Stopwatch.GetTimestamp() - _born) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (waited > _maxWaitMs) { Anim.NoteLateStart(); Finish(); return false; }
            }
            float t = _elapsed / _ms;
            if (t >= 1f) { Finish(); return false; }
            try { _step(t); }
            catch (Exception ex)
            {
                Logger.Diag("[ANIM] tween step threw, landing it: " + ex.GetType().Name + ": " + ex.Message);
                Finish();   // its done callback must still run
                return false;
            }
            return true;
        }
    }

    /// <summary>One 0..1 value of a control that travels to its target instead of jumping: a hover, a press, a switch
    /// position. The control keeps its own bool as the truth (hit tests, accessibility, events stay instant) and reads
    /// <see cref="T"/> only in its paint. Each tick invalidates the control (or runs <c>changed</c>, to invalidate less).</summary>
    public sealed class Fade : IAnimStep
    {
        private readonly Control _owner;
        private readonly AnimKind _kind;
        private readonly int _inMs, _outMs;
        private readonly Action _changed;
        private float _v, _target;
        private bool _queued;

        public Fade(Control owner, AnimKind kind, int inMs, int outMs, Action changed = null, bool on = false)
        {
            _owner = owner; _kind = kind; _inMs = inMs; _outMs = outMs; _changed = changed;
            _v = _target = on ? 1f : 0f;
        }

        /// <summary>Where the value is now, 0..1, linear in time (ease it at the use when it is a position).</summary>
        public float T { get { return _v; } }

        public bool Moving { get { return _queued; } }

        public void Set(bool on) { Set(on ? 1f : 0f); }

        public void Set(float target)
        {
            if (target == _target) return;
            _target = target;
            if (!Anim.CanAnimate(_owner, _kind)) { _v = target; Changed(); return; }
            if (!_queued) { _queued = true; Anim.Add(this); }
        }

        /// <summary>Jumps to a state with no animation and no repaint (the caller invalidates): a rebuilt control, a
        /// value set by code before the control shows.</summary>
        public void Snap(bool on) { _v = _target = on ? 1f : 0f; }

        private void Changed()
        {
            try
            {
                if (_changed != null) _changed();
                else if (_owner != null && !_owner.IsDisposed) _owner.Invalidate();
            }
            catch { }
        }

        bool IAnimStep.Step(float dtMs)
        {
            if (!Anim.Alive(_owner)) { _v = _target; _queued = false; return false; }
            int ms = _target > _v ? _inMs : _outMs;
            float d = ms <= 0 ? 2f : dtMs / ms;
            _v = _target > _v ? Math.Min(_target, _v + d) : Math.Max(_target, _v - d);
            Changed();
            if (_v != _target) return true;
            _queued = false;
            return false;
        }
    }

    /// <summary>Hover over a LIST that one control draws (a menu's rows, a rail's pages): one 0..1 value per item, the
    /// active one rising and the rest falling, so the highlight fades from row to row instead of jumping.</summary>
    public sealed class FadeSet : IAnimStep
    {
        private readonly Control _owner;
        private readonly AnimKind _kind;
        private readonly int _inMs, _outMs;
        private readonly Action<int> _changed;
        private float[] _v;
        private int _active = -1;
        private bool _queued;

        /// <param name="changed">Invalidates the item of that index (null = the whole control).</param>
        public FadeSet(Control owner, AnimKind kind, int count, int inMs, int outMs, Action<int> changed = null)
        {
            _owner = owner; _kind = kind; _inMs = inMs; _outMs = outMs; _changed = changed;
            _v = new float[Math.Max(0, count)];
        }

        public float this[int i] { get { return i >= 0 && i < _v.Length ? _v[i] : 0f; } }

        /// <summary>The highlighted item (-1 = none).</summary>
        public int Active
        {
            get { return _active; }
            set
            {
                if (value < 0 || value >= _v.Length) value = -1;
                if (value == _active) return;
                int old = _active;
                _active = value;
                if (!Anim.CanAnimate(_owner, _kind))
                {
                    for (int i = 0; i < _v.Length; i++)
                    {
                        float want = i == _active ? 1f : 0f;
                        if (_v[i] != want) { _v[i] = want; Changed(i); }
                    }
                    return;
                }
                if (!_queued) { _queued = true; Anim.Add(this); }
            }
        }

        /// <summary>Clears every value with no animation and no repaint (the list was rebuilt or scrolled).</summary>
        public void Reset(int count)
        {
            if (count < 0) count = 0;
            if (_v.Length != count) _v = new float[count]; else Array.Clear(_v, 0, _v.Length);
            _active = -1;
        }

        private void Changed(int i)
        {
            try
            {
                if (_changed != null) _changed(i);
                else if (_owner != null && !_owner.IsDisposed) _owner.Invalidate();
            }
            catch { }
        }

        bool IAnimStep.Step(float dtMs)
        {
            bool alive = Anim.Alive(_owner), moving = false;
            float up = _inMs <= 0 ? 2f : dtMs / _inMs, down = _outMs <= 0 ? 2f : dtMs / _outMs;
            for (int i = 0; i < _v.Length; i++)
            {
                float want = i == _active ? 1f : 0f, v = _v[i];
                if (v == want) continue;
                v = !alive ? want : want > v ? Math.Min(want, v + up) : Math.Max(want, v - down);
                _v[i] = v;
                if (alive) Changed(i);
                if (v != want) moving = true;
            }
            if (moving) return true;
            _queued = false;
            return false;
        }
    }
}
