using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>Redesign step 3.9 — the DPI diagnostics overlay (Ctrl+Alt+Shift+D in any AppForm toggles it). A small panel
    /// over the top-left of the ACTIVE window: its effective DPI = monitor DPI × the interface scale (and the fit cap), the
    /// process's DPI mode, the monitor and its work area, a reference font's pixel size, and a ruler of 100 LOGICAL px with
    /// a tick every 10 — the G3 matrix checks sizes against it by eye.
    /// It never takes focus (WS_EX_NOACTIVATE, ShowWithoutActivation) and follows whichever AppForm is active. It has no
    /// owner on purpose: an owned form closes with its owner, and the overlay outlives the dialogs it visits.</summary>
    internal sealed class DpiOverlay : Form
    {
        /// <summary>Logical size, and its place in the target's client area (under a full title bar).</summary>
        private const int W = 300, H = 112, X = 12, Y = 56;
        private static DpiOverlay _instance;
        private readonly Timer _timer;
        private AppForm _target;

        /// <summary>On, over <paramref name="from"/> (the window the keys were pressed in); or off.</summary>
        public static void Toggle(AppForm from)
        {
            if (_instance != null && !_instance.IsDisposed) { _instance.Close(); _instance = null; return; }
            _instance = new DpiOverlay { _target = from != null && !from.IsPane ? from : null };
            _instance.Follow();
        }

        private DpiOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _timer = new Timer { Interval = 500 };
            _timer.Tick += (s, e) => Follow();
            _timer.Start();
            FormClosed += (s, e) => { _timer.Stop(); _timer.Dispose(); if (ReferenceEquals(_instance, this)) _instance = null; };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private const int WM_DPICHANGED = 0x02E0, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }        // sized from the TARGET's DPI, not its own
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        /// <summary>Follows the active AppForm (or keeps the last one): sits over its top-left, under its title bar.</summary>
        private void Follow()
        {
            if (IsDisposed) return;
            var active = Form.ActiveForm as AppForm;
            if (active != null && !active.IsPane && !ReferenceEquals(active, this)) _target = active;
            if (_target == null || _target.IsDisposed || !_target.Visible || _target.WindowState == FormWindowState.Minimized)
            {
                if (Visible) Hide();
                return;
            }
            int dpi = _target.WindowDpiValue;
            var size = new Size(Dpi.Px(W, dpi), Dpi.Px(H, dpi));
            var at = _target.PointToScreen(new Point(Dpi.Px(X, dpi), Dpi.Px(Y, dpi)));
            var bounds = new Rectangle(at, size);
            if (Bounds != bounds) Bounds = bounds;
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(32, 32, 36));
            var t = _target;
            if (t == null || t.IsDisposed) return;
            int dpi = t.WindowDpiValue, mon = t.MonitorDpiValue, pad = Dpi.Px(8, dpi);
            Rectangle wa = Rectangle.Empty, sb = Rectangle.Empty;
            try { var scr = Screen.FromHandle(t.Handle); wa = scr.WorkingArea; sb = scr.Bounds; } catch { }
            int scale = Dpi.ScaleOn(wa, mon), cap = Dpi.FitCap(wa, mon);
            var font = FontHelper.UiAt(9f, dpi);                 // shared: never dispose
            var refFont = FontHelper.UiAt(10.5f, dpi);
            string[] lines =
            {
                t.GetType().Name + "  dpi " + dpi + "  = monitor " + mon + " x " + scale + "%",
                "scale setting " + Dpi.UserScalePercent + "% (" + Dpi.ScaleSource + ")" + (cap < 200 ? "  cap " + cap + "% here" : ""),
                "mode " + Dpi.Mode + "  system " + Dpi.SystemDpi,
                "monitor " + sb.Width + "x" + sb.Height + "  work " + wa.Width + "x" + wa.Height,
                "10.5 pt = " + refFont.Size.ToString("0.#") + " px  (UiAt)",
            };
            int y = pad, lh = TextRenderer.MeasureText("Ag", font).Height;
            foreach (var l in lines)
            {
                TextRenderer.DrawText(g, l, font, new Rectangle(pad, y, Width - 2 * pad, lh), Color.FromArgb(235, 235, 240),
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                y += lh;
            }
            // the ruler: 100 logical px at the TARGET's DPI, a tick every 10, long ticks at 0 / 50 / 100
            int rx = pad, ry = y + Dpi.Px(6, dpi), len = Dpi.Px(100, dpi);
            using (var pen = new Pen(Color.FromArgb(255, 196, 0), 1f))
            {
                g.DrawLine(pen, rx, ry, rx + len, ry);
                for (int i = 0; i <= 10; i++)
                {
                    int x = rx + Dpi.Px(i * 10, dpi), h = i % 5 == 0 ? Dpi.Px(8, dpi) : Dpi.Px(4, dpi);
                    g.DrawLine(pen, x, ry, x, ry + h);
                }
            }
            TextRenderer.DrawText(g, "100 logical px = " + len + " px", font, new Point(rx + len + Dpi.Px(8, dpi), ry - lh / 3),
                Color.FromArgb(255, 196, 0), TextFormatFlags.NoPrefix);
        }

        /// <summary>The gallery probe: on over <paramref name="f"/> without taking activation, sized and placed at its DPI,
        /// following a live interface-scale change, then off and gone. <paramref name="shot"/> gets a picture of it.</summary>
        internal static void RunProbe(AppForm f, Action<bool, string, string> check, Action<string> info, string shot)
        {
            int oldScale = Dpi.UserScalePercent;
            try
            {
                if (_instance != null) Toggle(null);
                var activeBefore = Form.ActiveForm;
                Toggle(f);
                Pump(200);
                var o = _instance;
                check(o != null && o.Visible, "overlay: Ctrl+Alt+Shift+D shows it", o == null ? "null" : "visible " + o.Visible);
                if (o == null) return;
                check(ReferenceEquals(Form.ActiveForm, activeBefore), "overlay: it did not take activation",
                      (Form.ActiveForm == null ? "null" : Form.ActiveForm.GetType().Name));
                Func<int, string> placed = dpi =>
                {
                    var want = new Rectangle(f.PointToScreen(new Point(Dpi.Px(X, dpi), Dpi.Px(Y, dpi))), new Size(Dpi.Px(W, dpi), Dpi.Px(H, dpi)));
                    return o.Bounds == want ? null : o.Bounds + " vs " + want;
                };
                string at = placed(f.WindowDpiValue);
                check(at == null, "overlay: sized and placed at the window's DPI (" + f.WindowDpiValue + ")", at);
                if (!string.IsNullOrEmpty(shot))
                    try
                    {
                        using (var bmp = new Bitmap(o.Width, o.Height)) { o.DrawToBitmap(bmp, new Rectangle(Point.Empty, o.Size)); bmp.Save(shot); }
                        info("overlay shot: " + shot);
                    }
                    catch (Exception ex) { info("overlay shot failed: " + ex.Message); }

                int cap = Dpi.FitCap(Screen.FromHandle(f.Handle).WorkingArea, f.MonitorDpiValue);
                if (cap >= 125)
                {
                    AppForm.ApplyInterfaceScale(125);
                    Pump(800);                                       // the overlay follows on its 500 ms tick
                    at = placed(f.WindowDpiValue);
                    check(at == null, "overlay: follows a live scale change (" + f.WindowDpiValue + " DPI)", at);
                    AppForm.ApplyInterfaceScale(oldScale);
                    Pump(800);
                }

                Toggle(f);
                Pump(100);
                check(_instance == null && o.IsDisposed, "overlay: the second press closes and disposes it", "instance " + (_instance != null) + ", disposed " + o.IsDisposed);
            }
            finally
            {
                if (Dpi.UserScalePercent != oldScale) AppForm.ApplyInterfaceScale(oldScale);
                if (_instance != null) { _instance.Close(); _instance = null; }
            }
        }

        private static void Pump(int ms)
        {
            var until = Environment.TickCount + ms;
            while (Environment.TickCount - until < 0) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }
    }
}
