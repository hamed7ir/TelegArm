using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.3 — the borderless window's WM_NCHITTEST decision, kept pure (no handle, no WinForms) so
    /// SelfTest can check it as a table. AppForm feeds it the client size, the cursor, the DPI-scaled gutter and corner,
    /// whether the window may be resized right now, and the title bar's bounds.</summary>
    public static class WindowHit
    {
        public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                         HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>Resize edges inside the <paramref name="edge"/>-pixel gutter, widened to <paramref name="corner"/>
        /// pixels along each side near a corner so corners are easy to grab; the caption over the title bar; client
        /// elsewhere. The gutter wins over the caption, so the top edge stays a resize handle above the title bar.</summary>
        public static int Compute(Size client, Point p, int edge, int corner, bool canResize, Rectangle caption)
        {
            if (canResize)
            {
                int w = client.Width, h = client.Height;
                bool l = p.X < edge, r = p.X >= w - edge, t = p.Y < edge, b = p.Y >= h - edge;
                bool cl = p.X < corner, cr = p.X >= w - corner, ct = p.Y < corner, cb = p.Y >= h - corner;
                if ((t && cl) || (l && ct)) return HTTOPLEFT;
                if ((t && cr) || (r && ct)) return HTTOPRIGHT;
                if ((b && cl) || (l && cb)) return HTBOTTOMLEFT;
                if ((b && cr) || (r && cb)) return HTBOTTOMRIGHT;
                if (l) return HTLEFT;
                if (r) return HTRIGHT;
                if (t) return HTTOP;
                if (b) return HTBOTTOM;
            }
            return caption.Contains(p) ? HTCAPTION : HTCLIENT;
        }
    }
}
