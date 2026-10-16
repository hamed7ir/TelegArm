using System;
using System.Drawing;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Redesign R1 — where a saved window may come back. Pure (rectangles in, a rectangle out) so the SelfTest can cover
    /// the edge cases: a monitor that is gone, a window larger than the screen it lands on, a title bar off the top.
    /// </summary>
    public static class WindowPlacementMath
    {
        /// <summary>How much of the title-bar strip must be on a screen (px) for the saved position to count as reachable.</summary>
        public const int MinVisible = 120;

        /// <summary>The saved bounds at <paramref name="size"/> (the saved logical size already turned into pixels), moved
        /// and shrunk to fit the working area that holds its top-left corner, else the one holding most of the title bar — or null when no working area holds a
        /// usable part of it (the monitor is gone): then the caller centres the window as before.</summary>
        /// <param name="captionHeight">Height of the title-bar strip in pixels, the part a user needs to grab.</param>
        public static Rectangle? Fit(Point location, Size size, Rectangle[] workAreas, int captionHeight)
        {
            if (workAreas == null || workAreas.Length == 0 || size.Width <= 0 || size.Height <= 0) return null;
            var caption = new Rectangle(location.X, location.Y, size.Width, Math.Max(1, captionHeight));
            Rectangle best = Rectangle.Empty;
            long bestArea = 0;
            foreach (var wa in workAreas)
            {
                var i = Rectangle.Intersect(caption, wa);
                long a = (long)i.Width * i.Height;
                if (i.Width < Math.Min(MinVisible, size.Width) || i.Height <= 0) continue;
                // the screen that holds the saved top-left corner wins: the window stays where it was, even when it is
                // too big and more of its title bar happens to cross a neighbouring screen
                if (wa.Contains(location)) a = long.MaxValue;
                if (a > bestArea) { bestArea = a; best = wa; }
            }
            if (bestArea == 0) return null;

            int w = Math.Min(size.Width, best.Width), h = Math.Min(size.Height, best.Height);
            int x = Math.Max(best.Left, Math.Min(location.X, best.Right - w));
            int y = Math.Max(best.Top, Math.Min(location.Y, best.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }
}
