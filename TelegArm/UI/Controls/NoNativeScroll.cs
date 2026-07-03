using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Structurally suppresses the native scrollbars of an AutoScroll control by hiding BOTH bars
    /// from inside WndProc on the non-client messages (WM_NCCALCSIZE / WM_NCPAINT) — synchronously
    /// with the paint cycle, so the native bar never paints (no flicker) and its reserved space is
    /// reclaimed (so a horizontal strip can't ghost an unwanted vertical bar).
    ///
    /// This deliberately does NOT strip WS_VSCROLL/WS_HSCROLL: AutoScroll's own scroll state is left
    /// untouched, so mouse-wheel and AutoScrollPosition-driven scrolling (via <c>ThemedScrollBar</c>)
    /// keep working on every Windows version including RT 8.1. The themed scrollbar stays; only the
    /// native ghost is gone.
    /// </summary>
    internal static class NativeScrollSuppressor
    {
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        private const int SB_BOTH = 3;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCPAINT = 0x0085;

        /// <summary>Call at the top of WndProc (before base) for non-client messages.</summary>
        public static void Suppress(Control c, int msg, ref bool busy)
        {
            if (busy) return;
            if (msg != WM_NCCALCSIZE && msg != WM_NCPAINT) return;
            if (!c.IsHandleCreated) return;
            busy = true;                                    // ShowScrollBar can re-enter NCCALCSIZE
            try { ShowScrollBar(c.Handle, SB_BOTH, false); } catch { }
            finally { busy = false; }
        }
    }

    /// <summary>AutoScroll FlowLayoutPanel with native scrollbars suppressed (themed bar drives it).</summary>
    public sealed class NoNativeScrollFlowPanel : FlowLayoutPanel
    {
        private bool _busy;

        public NoNativeScrollFlowPanel()
        {
            // SCROLL-SMOOTH-T1 Part 1: buffer the HOST's own painting so the strip exposed by a scroll
            // blit fills from a buffer instead of flashing raw BackColor. This cannot synchronize the
            // CHILD controls' repaints — that is what WS_EX_COMPOSITED (the A/B toggle below) attempts.
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }

        /// <summary>SCROLL-SMOOTH-T1 Part 2: opt this surface into WS_EX_COMPOSITED — Windows then paints
        /// the host + ALL children bottom-up into one composed surface per pass, giving coherent frames
        /// during pans/coasts (no overlapping half-moved rows) at extra paint cost. Set at construction on
        /// the chat-list/message hosts; ALSO gated by AppSettings.CompositedScroll (the dev/RT A/B switch —
        /// restart to apply, because the style participates in handle creation).</summary>
        public bool ForceComposited { get; set; }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (ForceComposited && Core.AppSettings.Instance.CompositedScroll)
                    cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            NativeScrollSuppressor.Suppress(this, m.Msg, ref _busy);
            base.WndProc(ref m);
        }
    }

    /// <summary>AutoScroll Panel with native scrollbars suppressed (themed bar drives it).</summary>
    public sealed class NoNativeScrollPanel : Panel
    {
        private bool _busy;

        public NoNativeScrollPanel()
        {
            // SCROLL-SMOOTH-T1 Part 1 (same rationale as NoNativeScrollFlowPanel).
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            NativeScrollSuppressor.Suppress(this, m.Msg, ref _busy);
            base.WndProc(ref m);
        }
    }
}
