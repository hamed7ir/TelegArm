using System;
using System.Drawing;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Owner 2026-11-15 (device trip 5: a resize step on the Surface spent 182 ms painting). Every control that paints
    /// with OptimizedDoubleBuffer (nearly all of ours) draws through ONE shared off-screen bitmap,
    /// <see cref="BufferedGraphicsManager.Current"/>. Its limit is the .NET default of 225x96 pixels (FACT, read on the
    /// dev box): a control bigger than that in area (a message bubble is 641x85, a chat row 300x64, a Settings row) got a
    /// NEW bitmap for each paint, created and freed again (CreateDIBSection / DeleteObject: seen in the dev-box profile
    /// of a resize). With the limit at the screen's size the shared bitmap grows once, to the biggest control painted,
    /// and every later paint reuses it. A paint inside a paint still gets its own bitmap (the context sees it is busy).
    /// Memory: one bitmap of at most the screen's size (1366x768: 4 MB on the Surface); capped at 2560x1600 (16 MB).
    /// TELEGARM_PAINTBUF=0 keeps the .NET default (the A/B switch of the resize rig).
    /// </summary>
    internal static class PaintBuffers
    {
        public static void Init()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("TELEGARM_PAINTBUF") == "0") return;
                int w = 0, h = 0;
                foreach (var s in Screen.AllScreens) { w = Math.Max(w, s.Bounds.Width); h = Math.Max(h, s.Bounds.Height); }
                if (w <= 0 || h <= 0) return;
                var ctx = BufferedGraphicsManager.Current;
                var now = ctx.MaximumBuffer;
                ctx.MaximumBuffer = new Size(Math.Max(now.Width, Math.Min(w, 2560)), Math.Max(now.Height, Math.Min(h, 1600)));
            }
            catch (Exception ex) { Logger.Diag("[PAINT] shared paint buffer left at the default: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
