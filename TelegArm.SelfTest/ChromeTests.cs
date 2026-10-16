using System;
using System.Collections.Generic;
using System.Drawing;
using TelegArm.Helpers;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 1.3: the borderless window's hit-test decision (WindowHit), as a table. The real-window
    /// half — the title bar answering HTTRANSPARENT, maximise to the work area, the DPI move — is the gallery probe
    /// (TelegArm.exe --gallery-probe out.txt); it needs a desktop, which this exe must not assume.</summary>
    internal static class ChromeTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("chrome: hit-test table at 96 DPI (gutter 6, corner 10)", HitTable96));
            t.Add(new KeyValuePair<string, Action>("chrome: hit-test at 144 DPI and with the touch gutter", HitTableScaled));
            t.Add(new KeyValuePair<string, Action>("chrome: no resize edges when maximised or fixed-size", HitNoResize));
            t.Add(new KeyValuePair<string, Action>("placement (R1): a saved window comes back on screen, clamped, or not at all", Placement));
            t.Add(new KeyValuePair<string, Action>("dialogs: KeyboardInset raises on a change only, and only while listened to", InsetChangesOnly));
            t.Add(new KeyValuePair<string, Action>("dialogs: a throwing KeyboardInset listener does not stop the others", InsetIsolatesListeners));
        }

        private static void Placement()
        {
            var one = new[] { new Rectangle(0, 0, 1920, 1040) };
            var two = new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 1366, 728) };
            Check.Equal(new Rectangle(100, 80, 900, 560), WindowPlacementMath.Fit(new Point(100, 80), new Size(900, 560), one, 40), "on screen: unchanged");
            Check.True(WindowPlacementMath.Fit(new Point(-30000, -30000), new Size(900, 560), one, 40) == null, "on no screen: null (centre)");
            Check.Equal(new Rectangle(0, 0, 1920, 1040), WindowPlacementMath.Fit(new Point(0, 0), new Size(6000, 4000), one, 40), "too big: the working area");
            Check.Equal(new Rectangle(1020, 480, 900, 560), WindowPlacementMath.Fit(new Point(1500, 900), new Size(900, 560), one, 40), "hanging off the corner: moved in");
            Check.True(WindowPlacementMath.Fit(new Point(1860, 100), new Size(900, 560), one, 40) == null, "only 60 px of the title bar visible: null");
            var second = WindowPlacementMath.Fit(new Point(2000, 100), new Size(1000, 900), two, 40).Value;
            Check.True(second.Left >= 1920 && second.Right <= 3286 && second.Height == 728, "the second monitor holds most of it: fitted there " + second);
            Check.Equal(new Rectangle(0, 0, 1920, 1040), WindowPlacementMath.Fit(new Point(0, 0), new Size(6000, 4000), two, 40),
                        "too big at the primary's corner: stays on the primary, though its title bar crosses the second more");
            Check.Equal(new Rectangle(500, 0, 900, 560), WindowPlacementMath.Fit(new Point(500, -10), new Size(900, 560), one, 40), "title bar a little above the top: moved down");
        }

        // A 400x300 client with a 46-pixel title bar across the top.
        private static readonly Size Client = new Size(400, 300);
        private static readonly Rectangle Caption = new Rectangle(0, 0, 400, 46);

        private static void Expect(string what, int expected, Point p, int edge, int corner, bool canResize)
        {
            Check.Equal(expected, WindowHit.Compute(Client, p, edge, corner, canResize, Caption), what + " at " + p.X + "," + p.Y);
        }

        private static void HitTable96()
        {
            const int e = 6, c = 10;
            // corners: inside the gutter on one side and within the corner run on the other
            Expect("top-left", WindowHit.HTTOPLEFT, new Point(0, 0), e, c, true);
            Expect("top-left, along the top", WindowHit.HTTOPLEFT, new Point(9, 2), e, c, true);
            Expect("top-left, down the side", WindowHit.HTTOPLEFT, new Point(2, 9), e, c, true);
            Expect("top-right", WindowHit.HTTOPRIGHT, new Point(399, 0), e, c, true);
            Expect("top-right, along the top", WindowHit.HTTOPRIGHT, new Point(390, 5), e, c, true);
            Expect("bottom-left", WindowHit.HTBOTTOMLEFT, new Point(0, 299), e, c, true);
            Expect("bottom-left, down the side", WindowHit.HTBOTTOMLEFT, new Point(5, 290), e, c, true);
            Expect("bottom-right", WindowHit.HTBOTTOMRIGHT, new Point(399, 299), e, c, true);
            Expect("bottom-right, along the bottom", WindowHit.HTBOTTOMRIGHT, new Point(390, 294), e, c, true);
            // just past the corner run: plain edges
            Expect("top edge past the corner run", WindowHit.HTTOP, new Point(10, 0), e, c, true);
            Expect("left edge past the corner run", WindowHit.HTLEFT, new Point(0, 10), e, c, true);
            Expect("right edge", WindowHit.HTRIGHT, new Point(394, 150), e, c, true);
            Expect("bottom edge", WindowHit.HTBOTTOM, new Point(200, 294), e, c, true);
            // the gutter boundary: the 6th pixel in is the edge, the 7th is not
            Expect("left gutter, last pixel", WindowHit.HTLEFT, new Point(5, 150), e, c, true);
            Expect("left gutter, first pixel past", WindowHit.HTCLIENT, new Point(6, 150), e, c, true);
            Expect("right gutter, first pixel past", WindowHit.HTCLIENT, new Point(393, 150), e, c, true);
            // the top gutter beats the caption; below it, the title bar is the caption
            Expect("top gutter over the title bar", WindowHit.HTTOP, new Point(200, 5), e, c, true);
            Expect("title bar below the gutter", WindowHit.HTCAPTION, new Point(200, 6), e, c, true);
            Expect("title bar, last row", WindowHit.HTCAPTION, new Point(200, 45), e, c, true);
            Expect("first row under the title bar", WindowHit.HTCLIENT, new Point(200, 46), e, c, true);
            Expect("content", WindowHit.HTCLIENT, new Point(200, 150), e, c, true);
        }

        private static void HitTableScaled()
        {
            // 144 DPI: gutter Px(6) = 9, corner Px(10) = 15.
            int e = Dpi.Px(6, 144), c = Dpi.Px(10, 144);
            Check.Equal(9, e, "Px(6) at 144");
            Check.Equal(15, c, "Px(10) at 144");
            Expect("144: left gutter, last pixel", WindowHit.HTLEFT, new Point(8, 150), e, c, true);
            Expect("144: left gutter, first pixel past", WindowHit.HTCLIENT, new Point(9, 150), e, c, true);
            Expect("144: top-left corner run", WindowHit.HTTOPLEFT, new Point(14, 0), e, c, true);
            Expect("144: past the corner run", WindowHit.HTTOP, new Point(15, 0), e, c, true);
            // touch: the 12-pixel gutter
            Expect("touch: 11 in is still the edge", WindowHit.HTRIGHT, new Point(388, 150), 12, 12, true);
            Expect("touch: 12 in is content", WindowHit.HTCLIENT, new Point(387, 150), 12, 12, true);
        }

        private static void InsetChangesOnly()
        {
            var got = new List<Rectangle>();
            Action<Rectangle> h = r => got.Add(r);
            Check.True(!KeyboardInset.Listening, "nobody listens at the start");
            KeyboardInset.Publish(new Rectangle(0, 500, 800, 300));   // no listener: Part B would not even call this
            var kb = new Rectangle(0, 500, 800, 300);
            KeyboardInset.KeyboardRectChanged += h;
            try
            {
                Check.True(KeyboardInset.Listening, "listening once subscribed");
                KeyboardInset.Publish(kb);
                KeyboardInset.Publish(kb);                             // Part B polls: the same rect again is not news
                KeyboardInset.Publish(Rectangle.Empty);
                KeyboardInset.Publish(Rectangle.Empty);
                Check.Equal(new[] { kb, Rectangle.Empty }, got, "raised once per change");
            }
            finally { KeyboardInset.KeyboardRectChanged -= h; }
            Check.True(!KeyboardInset.Listening, "not listening after the last unsubscribe");
            // A new listener starts from "no keyboard": a keyboard already up is reported to it.
            KeyboardInset.Publish(kb);   // ignored, nobody listens
            got.Clear();
            KeyboardInset.KeyboardRectChanged += h;
            try { KeyboardInset.Publish(kb); }
            finally { KeyboardInset.KeyboardRectChanged -= h; }
            Check.Equal(new[] { kb }, got, "a new listener hears the current keyboard");
        }

        private static void InsetIsolatesListeners()
        {
            int heard = 0;
            Action<Rectangle> bad = r => { throw new InvalidOperationException("listener bug"); };
            Action<Rectangle> good = r => heard++;
            KeyboardInset.KeyboardRectChanged += bad;
            KeyboardInset.KeyboardRectChanged += good;
            try { KeyboardInset.Publish(new Rectangle(0, 600, 800, 200)); }
            finally { KeyboardInset.KeyboardRectChanged -= bad; KeyboardInset.KeyboardRectChanged -= good; }
            Check.Equal(1, heard, "the listener after a throwing one still ran");
            Check.True(!KeyboardInset.Listening, "both unsubscribed");
        }

        private static void HitNoResize()
        {
            Expect("maximised: the corner pixel is the caption", WindowHit.HTCAPTION, new Point(0, 0), 6, 10, false);
            Expect("maximised: the left edge is content", WindowHit.HTCLIENT, new Point(0, 150), 6, 10, false);
            Expect("maximised: the bottom-right is content", WindowHit.HTCLIENT, new Point(399, 299), 6, 10, false);
            Check.Equal(WindowHit.HTCLIENT, WindowHit.Compute(Client, new Point(200, 20), 6, 10, true, Rectangle.Empty),
                "no title bar: no caption");
        }
    }
}
