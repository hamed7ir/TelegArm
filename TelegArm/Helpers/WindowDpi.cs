using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.3 — one window's DPI and its WM_DPICHANGED handling (plan §6.1), shared by AppForm
    /// and DialogForm. INERT while the process is system-aware: Windows then never sends WM_DPICHANGED and
    /// <see cref="Dpi.ForWindow"/> always answers the system DPI.
    ///
    /// The window starts at the SYSTEM DPI, because its controls were built before it had a handle — and
    /// Dpi.Of(control) answers SystemDpi then. <see cref="Resync"/> (on handle creation, activation, restore,
    /// display/setting changes) corrects that the moment the real monitor is known: a window created on a
    /// 250% monitor by a 150% system is rescaled 144 → 240 before the user sees it.</summary>
    public sealed class WindowDpi
    {
        public const int WM_DPICHANGED = 0x02E0;

        private readonly Form _form;
        private int _pending;
        private bool _busy;

        /// <summary>The EFFECTIVE DPI the window lays out at: its monitor's DPI × the user's interface scale (step 3.8a).</summary>
        public int Dpi { get; private set; }

        /// <summary>The RAW DPI of the window's monitor (for what must stay physical: slop, the keyboard rectangle).</summary>
        public int MonitorDpi { get; private set; }

        public WindowDpi(Form form)
        {
            _form = form;
            MonitorDpi = Helpers.Dpi.SystemDpi;
            Dpi = Helpers.Dpi.Effective(MonitorDpi);
        }

        /// <summary>Step 3.8a — an embedded pane (AppForm.UseAsPane) takes its host's DPI without a rescale of its own.</summary>
        internal void Adopt(int effectiveDpi) { if (effectiveDpi > 0) Dpi = effectiveDpi; }

        /// <summary>Handles WM_DPICHANGED; true when the message was consumed (it returns 0 WITHOUT the base
        /// handler: WinForms 4.7's own handling must not rescale a second time).</summary>
        public bool OnMessage(ref Message m)
        {
            if (m.Msg != WM_DPICHANGED) return false;
            m.Result = IntPtr.Zero;
            int raw = (int)(m.WParam.ToInt64() & 0xFFFF);
            if (raw <= 0) return true;
            // Back to the DPI the window already has: any change stored while minimised/hidden is void (review 1.3 — it
            // used to survive and be applied on restore, at a DPI the window was no longer on). Compared EFFECTIVE to
            // effective (step 3.8a: Dpi holds the scaled value, the message the raw one).
            if (Helpers.Dpi.Effective(raw) == Dpi) { MonitorDpi = raw; _pending = 0; return true; }
            var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            if (_form.WindowState == FormWindowState.Minimized || !_form.Visible) { _pending = raw; return true; }   // applied on restore
            Apply(raw, Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom), "WM_DPICHANGED");
            return true;
        }

        /// <summary>Re-reads the window's DPI and rescales if it differs (or applies one stored while minimised).</summary>
        public void Resync(string why)
        {
            if (!_form.IsHandleCreated || _form.IsDisposed || _form.WindowState == FormWindowState.Minimized) return;
            // The window's real DPI now is the truth; a value stored while minimised is only the fallback.
            int raw = Helpers.Dpi.ForWindow(_form.Handle);
            if (raw <= 0) raw = _pending;
            _pending = 0;
            if (raw <= 0) return;
            if (Helpers.Dpi.Effective(raw) != Dpi) Apply(raw, null, why);
            else MonitorDpi = raw;
        }

        /// <summary>Rescales to the effective DPI of the RAW monitor DPI <paramref name="raw"/>.</summary>
        private void Apply(int raw, Rectangle? suggested, string source)
        {
            if (_busy) { _pending = raw; return; }   // re-entrancy guard: a rescale that moves the window can trigger another
            _busy = true;
            int old = Dpi, nw = Helpers.Dpi.Effective(raw);
            var sw = Stopwatch.StartNew();
            // Maximised: Windows sizes it. Normal with no rectangle from Windows (Resync — created, restored or activated on
            // another-DPI monitor): the FORM must still be resized — DpiScaler never ratio-scales a Form root — or the
            // content is laid out for the new DPI inside a window still at the old pixel size (review 1.3, measured:
            // created on the 240-DPI monitor → 1400x1050 instead of 2250x1600, content clipped).
            Rectangle? bounds = null;
            if (_form.WindowState == FormWindowState.Normal) bounds = suggested ?? ScaledInPlace(old, nw);
            try
            {
                MonitorDpi = raw;
                Dpi = nw;   // first: OnDpiChanged handlers and Px(n, control) must already see the new DPI
                DpiScaler.Rescale(_form, old, nw, bounds);
            }
            finally { _busy = false; }
            Logger.Diag("[DPI] win=" + _form.GetType().Name + " old=" + old + " new=" + nw + " mon=" + raw
                        + " scale=" + Helpers.Dpi.UserScalePercent + "% src=" + source
                        + " sugg=" + (suggested.HasValue ? suggested.Value.ToString() : "-") + " applied=" + _form.Bounds
                        + " rescale=" + sw.ElapsedMilliseconds + "ms");
            if (_pending > 0 && Helpers.Dpi.Effective(_pending) != Dpi) { int p = _pending; _pending = 0; Apply(p, null, "pending"); }
            else _pending = 0;
        }

        /// <summary>The form's bounds rescaled old → new about its top-left, kept inside its monitor's work area.</summary>
        private Rectangle ScaledInPlace(int old, int nw)
        {
            var b = _form.Bounds;
            var size = new Size((int)Math.Round(b.Width * (double)nw / old), (int)Math.Round(b.Height * (double)nw / old));
            Rectangle wa;
            try { wa = Screen.FromRectangle(b).WorkingArea; } catch { return new Rectangle(b.Location, size); }
            size = new Size(Math.Min(size.Width, wa.Width), Math.Min(size.Height, wa.Height));
            int x = Math.Max(wa.Left, Math.Min(b.Left, wa.Right - size.Width));
            int y = Math.Max(wa.Top, Math.Min(b.Top, wa.Bottom - size.Height));
            return new Rectangle(x, y, size.Width, size.Height);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    }
}
