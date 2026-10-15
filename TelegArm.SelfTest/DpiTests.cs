using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 1.1: the DPI arithmetic, FontCache and DpiScaler. Plain WinForms controls, no window
    /// handles — the layout engine runs without them, which is all DpiScaler needs.</summary>
    internal static class DpiTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            Add(t, "dpi: Px(n, 96) == n for every n in -10000..10000", PxIdentityAt96);
            Add(t, "dpi: Px rounds half away from zero and never decreases with n", PxRounding);
            Add(t, "dpi: Of(control) is the system DPI when no window tracks its own", OfFallsBackToSystem);
            Add(t, "fonts: FontCache shares one pixel-unit font per (family, pt, style, dpi)", FontCacheShares);
            Add(t, "fonts: Rescale keeps the point size across DPIs (Point and Pixel sources)", FontRescale);
            Add(t, "scaler: 96 -> 144 scales bounds, padding, anchors, fonts, docked sizes", ScaleUp);
            Add(t, "scaler: 96 -> 144 -> 96 returns every value exactly", RoundTripUpDown);
            Add(t, "scaler: 144 -> 96 -> 144 returns every value exactly (no drift)", RoundTripDownUp);
            Add(t, "scaler: a FixedPanel SplitContainer's distance and minimums scale and return", SplitContainerScales);
            Add(t, "scaler: IDpiAware is told, children before parents, after the generic scaling", CallbacksAfterAndChildrenFirst);
            Add(t, "scaler: a throwing OnDpiChanged neither stops the others nor leaves Dpi.Busy set (review 1.1)", ThrowingCallbackIsContained);
            Add(t, "scaler: a container we size ourselves lays out its docked child again (review 1.1)", NestedContainerRelayout);
            Add(t, "scaler: a scrolled AutoScroll panel keeps its place, scaled (review 1.1)", ScrolledPanelKeepsPlace);
            Add(t, "fonts: PersianAt and a Rescale round trip meet on the same cached font (review 1.1)", FontKeysNormalised);
            Add(t, "scale: Effective(raw, 100) == raw (the RT's 100% is untouched by step 3.8a)", EffectiveIdentity);
            Add(t, "scale: Effective is exact for 7 monitor DPIs x 7 detents (50-200%)", EffectiveExact);
            Add(t, "scale: --scale, then TELEGARM_SCALE, then the setting; anything invalid is ignored", ScaleResolution);
            Add(t, "scale: 144 -> 150% (216) -> 144 and 96 -> 50% (48) -> 96 return every value exactly", ScaleRoundTrips);
            Add(t, "scale: FitCap keeps the main window on screen (RT 1366x728 @96 -> 150%, 1920x1040 @144 -> 125%)", FitCapCases);
            Add(t, "scale: ScaleOn caps only above 100% (never below it); SetUserScale refuses off-step values", ScaleOnCases);
        }

        private sealed class Thrower : Panel, IDpiAware
        {
            public void OnDpiChanged(int oldDpi, int newDpi) { throw new InvalidOperationException("boom"); }
        }

        private static void ThrowingCallbackIsContained()
        {
            var log = new List<string>();
            var root = new Aware(log) { Name = "root", Size = new Size(100, 100) };
            root.Controls.Add(new Thrower { Bounds = new Rectangle(0, 0, 10, 10) });
            root.Controls.Add(new Aware(log) { Name = "sibling", Bounds = new Rectangle(20, 0, 10, 10) });
            DpiScaler.Rescale(root, 96, 144);
            Check.True(log.Exists(s => s.StartsWith("sibling")) && log.Exists(s => s.StartsWith("root")), "callbacks after the thrower were skipped: " + string.Join("|", log));
            Check.True(!Dpi.Busy, "Dpi.Busy stuck after a throwing callback");
        }

        private static void NestedContainerRelayout()
        {
            var root = new Panel { Size = new Size(400, 300) };
            var box = new Panel { Bounds = new Rectangle(10, 10, 200, 100) };   // not docked: we size it ourselves
            var inner = new Panel { Dock = DockStyle.Fill };
            box.Controls.Add(inner);
            root.Controls.Add(box);
            root.PerformLayout();
            DpiScaler.Rescale(root, 96, 144);
            Check.Equal(new Size(300, 150), box.Size, "box");
            Check.Equal(box.ClientSize, inner.Size, "the Fill child after the rescale");
        }

        private static void ScrolledPanelKeepsPlace()
        {
            var root = new Panel { Size = new Size(200, 100), AutoScroll = true };
            var tall = new Panel { Bounds = new Rectangle(0, 0, 150, 1000) };
            root.Controls.Add(tall);
            root.CreateControl();   // scrolling needs a handle
            var h = root.Handle;
            root.AutoScrollPosition = new Point(0, 300);
            Check.Equal(-300, root.AutoScrollPosition.Y, "setup: scrolled to 300");
            DpiScaler.Rescale(root, 96, 144);
            Check.Equal(1500, tall.Height, "content height at 144");
            Check.Equal(-450, root.AutoScrollPosition.Y, "scroll position at 144 (300 x 1.5)");
            Check.Equal(-450, tall.Top, "content top at 144 (scrolled, not double-offset)");
            root.Dispose();
        }

        private static void FontKeysNormalised()
        {
            var a = FontHelper.PersianAt(12f, 96);
            var back = FontCache.Rescale(FontCache.Rescale(a, 96, 144), 144, 96);
            Check.True(ReferenceEquals(a, back), "PersianAt(12, 96) and its 96 -> 144 -> 96 rescale are different fonts (" + a.Size + " vs " + back.Size + ")");
        }

        private static void Add(List<KeyValuePair<string, Action>> t, string name, Action a) { t.Add(new KeyValuePair<string, Action>(name, a)); }

        private static void PxIdentityAt96()
        {
            for (int n = -10000; n <= 10000; n++)
                if (Dpi.Px(n, 96) != n) throw new CheckFailed("Px(" + n + ", 96) = " + Dpi.Px(n, 96));
        }

        private static void PxRounding()
        {
            Check.Equal(15, Dpi.Px(10, 144), "Px(10,144)");
            Check.Equal(2, Dpi.Px(1, 144), "Px(1,144) (1.5 rounds up)");
            Check.Equal(-2, Dpi.Px(-1, 144), "Px(-1,144) (-1.5 rounds away from zero)");
            Check.Equal(80, Dpi.Px(64, 120), "Px(64,120)");
            Check.Equal(4, Dpi.Px(3, 120), "Px(3,120) (3.75)");
            Check.Equal(0, Dpi.Px(0, 192), "Px(0,192)");
            foreach (int dpi in new[] { 72, 120, 144, 168, 192, 288 })
                for (int n = -500; n < 500; n++)
                    if (Dpi.Px(n + 1, dpi) < Dpi.Px(n, dpi)) throw new CheckFailed("Px decreases at n=" + n + " dpi=" + dpi);
            Check.Equal(15f, Dpi.F(10f, 144), "F(10,144)");
        }

        private static void OfFallsBackToSystem()
        {
            using (var p = new Panel())
                Check.Equal(Dpi.DefaultDpi, Dpi.Of(p), "Of(an unparented panel)");
            Check.Equal(Dpi.DefaultDpi, Dpi.Of(null), "Of(null)");
            Check.Equal(Dpi.Effective(Dpi.SystemDpi), Dpi.DefaultDpi, "DefaultDpi = the system DPI at the user's scale");
        }

        private static readonly int[] MonitorDpis = { 96, 120, 144, 168, 192, 216, 240 };
        private static readonly int[] Detents = { 50, 75, 100, 125, 150, 175, 200 };

        private static void EffectiveIdentity()
        {
            for (int r = 1; r <= 1000; r++)
                if (Dpi.Effective(r, 100) != r) throw new CheckFailed("Effective(" + r + ", 100) = " + Dpi.Effective(r, 100));
            Check.Equal(0, Dpi.Effective(0, 150), "Effective(0, 150)");
        }

        private static void EffectiveExact()
        {
            foreach (int raw in MonitorDpis)
            {
                int prev = 0;
                foreach (int pct in Detents)
                {
                    if (raw * pct % 100 != 0) throw new CheckFailed("not exact: " + raw + " x " + pct + "%");
                    int e = Dpi.Effective(raw, pct);
                    Check.Equal(raw * pct / 100, e, "Effective(" + raw + ", " + pct + ")");
                    if (e <= prev) throw new CheckFailed("not increasing at " + raw + " x " + pct + "%");
                    prev = e;
                }
            }
        }

        private static void ScaleResolution()
        {
            string src;
            Check.Equal(150, Dpi.ResolveScale(new[] { "app.exe", "--scale=150" }, "75", 125, out src), "--scale wins");
            Check.Equal("--scale", src, "source");
            Check.Equal(75, Dpi.ResolveScale(new[] { "app.exe" }, "75", 125, out src), "then TELEGARM_SCALE");
            Check.Equal("TELEGARM_SCALE", src, "source");
            Check.Equal(125, Dpi.ResolveScale(null, null, 125, out src), "then the setting");
            Check.Equal("setting", src, "source");
            Check.Equal(100, Dpi.ResolveScale(new[] { "--scale=110" }, "250", 130, out src), "off-step and out-of-range values are ignored");
            Check.Equal("default", src, "source");
            Check.Equal(200, Dpi.ResolveScale(new[] { "--scale=200%" }, null, 100, out src), "a trailing % is accepted");
        }

        private static void FitCapCases()
        {
            Check.Equal(150, Dpi.FitCap(new Rectangle(0, 0, 1366, 728), 96), "Surface RT 1366x728 at 96");
            Check.Equal(125, Dpi.FitCap(new Rectangle(0, 0, 1920, 1040), 144), "1920x1040 at 144");
            Check.Equal(200, Dpi.FitCap(new Rectangle(0, 0, 3840, 2080), 96), "4K at 96");
            Check.Equal(100, Dpi.FitCap(new Rectangle(0, 0, 800, 560), 96), "a small screen never caps below 100");
            Check.Equal(200, Dpi.FitCap(Rectangle.Empty, 96), "no screen: no cap");
        }

        private static void ScaleOnCases()
        {
            int old = Dpi.UserScalePercent;
            try
            {
                var rt = new Rectangle(0, 0, 1366, 728);
                Check.True(Dpi.SetUserScale(200), "SetUserScale(200)");
                Check.Equal(150, Dpi.ScaleOn(rt, 96), "200% on the RT's screen is capped at 150%");
                Check.Equal(144, Dpi.EffectiveOn(rt, 96), "so its effective DPI is 144");
                Check.True(Dpi.SetUserScale(75), "SetUserScale(75)");
                Check.Equal(75, Dpi.ScaleOn(rt, 96), "below 100% is never capped");
                Check.Equal(72, Dpi.EffectiveOn(rt, 96), "75% of 96");
                Check.True(!Dpi.SetUserScale(110), "an off-step value is refused");
                Check.Equal(75, Dpi.UserScalePercent, "and changes nothing");
            }
            finally { Dpi.SetUserScale(old); }
        }

        private static void ScaleRoundTrips()
        {
            var t = Build();
            DpiScaler.Rescale(t.Root, 96, 144);
            var at144 = Snapshot(t);
            DpiScaler.Rescale(t.Root, 144, Dpi.Effective(144, 150));
            DpiScaler.Rescale(t.Root, Dpi.Effective(144, 150), 144);
            Same(at144, Snapshot(t), "after 144 -> 216 -> 144");

            var u = Build();
            var at96 = Snapshot(u);
            DpiScaler.Rescale(u.Root, 96, Dpi.Effective(96, 50));
            DpiScaler.Rescale(u.Root, Dpi.Effective(96, 50), 96);
            var back = Snapshot(u);
            back[4] = back[4].Replace("12Pixel", "9Point");   // the font comes back as the same size in pixel units
            Same(at96, back, "after 96 -> 48 -> 96");
        }

        private static void FontCacheShares()
        {
            var a = FontCache.Get("Segoe UI", 9f, FontStyle.Regular, 144);
            var b = FontCache.Get("segoe ui", 9f, FontStyle.Regular, 144);
            Check.True(ReferenceEquals(a, b), "the same key returned two fonts");
            Check.Equal(GraphicsUnit.Pixel, a.Unit, "unit");
            Check.Equal(18f, a.Size, "size of 9 pt at 144 dpi, in px");
            Check.True(!ReferenceEquals(a, FontCache.Get("Segoe UI", 9f, FontStyle.Regular, 96)), "a different DPI shared the font");
            Check.True(!ReferenceEquals(a, FontCache.Get("Segoe UI", 9f, FontStyle.Bold, 144)), "a different style shared the font");
            Check.Equal(12f, FontHelper.UiAt(9f, 96).Size, "UiAt(9 pt, 96 dpi) in px");
            Check.Equal(10f * 0.85f * 96 / 72f, FontHelper.PersianAt(10f, 96).Size, "PersianAt applies the Persian scale inside");
        }

        private static void FontRescale()
        {
            using (var pt = new Font("Segoe UI", 9f))
            {
                var up = FontCache.Rescale(pt, 96, 144);
                Check.Equal(GraphicsUnit.Pixel, up.Unit, "unit after rescale");
                Check.Equal(18f, up.Size, "9 pt at 144 dpi, in px");
                var down = FontCache.Rescale(up, 144, 96);
                Check.Equal(12f, down.Size, "back at 96 dpi, in px");
                Check.True(ReferenceEquals(down, FontCache.Get("Segoe UI", 9f, FontStyle.Regular, 96)), "the rescaled font is not the shared one");
            }
        }

        // ── the tree under test ──────────────────────────────────────────────────────────────────────────────

        private sealed class Tree
        {
            public Panel Root, Top, Fill;
            public Button Anchored, Inner;
            public Label Odd;
            public TableLayoutPanel Table;
        }

        private static Tree Build()
        {
            var t = new Tree();
            t.Root = new Panel { Size = new Size(400, 300), Padding = new Padding(8) };
            t.Fill = new Panel { Dock = DockStyle.Fill };
            t.Top = new Panel { Dock = DockStyle.Top, Height = 40 };
            t.Anchored = new Button { Bounds = new Rectangle(300, 250, 80, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(3) };
            t.Odd = new Label { AutoSize = false, Bounds = new Rectangle(7, 11, 13, 17), Font = new Font("Segoe UI", 9f) };
            t.Table = new TableLayoutPanel { Bounds = new Rectangle(10, 60, 200, 100), ColumnCount = 2 };
            t.Table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));
            t.Table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.Root.Controls.Add(t.Fill);      // Fill first: docking lays out in reverse add order, Top then Fill
            t.Root.Controls.Add(t.Top);
            t.Root.PerformLayout();
            // Added AFTER the layout, so its anchor distances are recorded against Fill's real size (376 x 244), not
            // the 200 x 100 a fresh Panel starts at — which would give negative, meaningless gaps.
            t.Anchored.Bounds = new Rectangle(t.Fill.Width - 100, t.Fill.Height - 50, 80, 30);   // gaps 20, 20
            t.Fill.Controls.Add(t.Anchored);
            t.Fill.Controls.Add(t.Odd);
            t.Fill.Controls.Add(t.Table);
            t.Root.PerformLayout();
            t.Inner = t.Anchored;
            return t;
        }

        /// <summary>Everything the scaler is responsible for, as one comparable string per control.</summary>
        private static List<string> Snapshot(Tree t)
        {
            return new List<string>
            {
                "root " + t.Root.Bounds + " pad " + t.Root.Padding,
                "top " + t.Top.Bounds,
                "fill " + t.Fill.Bounds,
                "anchored " + t.Anchored.Bounds + " mar " + t.Anchored.Margin,
                "odd " + t.Odd.Bounds + " font " + t.Odd.Font.Size + t.Odd.Font.Unit,
                "table " + t.Table.Bounds + " col0 " + t.Table.ColumnStyles[0].Width,
            };
        }

        private static void Same(List<string> a, List<string> b, string what)
        {
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) throw new CheckFailed(what + ": " + a[i] + "  ->  " + b[i]);
        }

        private static void ScaleUp()
        {
            var t = Build();
            int gapR = t.Fill.Width - t.Anchored.Right, gapB = t.Fill.Height - t.Anchored.Bottom;
            Check.True(gapR == 20 && gapB == 20, "setup: the anchored gaps are " + gapR + "," + gapB + ", expected 20,20");
            DpiScaler.Rescale(t.Root, 96, 144);
            Check.Equal(new Size(600, 450), t.Root.Size, "root size");
            Check.Equal(new Padding(12), t.Root.Padding, "root padding");
            Check.Equal(60, t.Top.Height, "docked Top height");
            Check.Equal(new Size(120, 45), t.Anchored.Size, "anchored size");
            Check.Equal(new Padding(5, 5, 5, 5), t.Anchored.Margin, "anchored margin (3 -> 4.5 -> 5)");
            int newGapR = t.Fill.Width - t.Anchored.Right, newGapB = t.Fill.Height - t.Anchored.Bottom;
            Check.True(Math.Abs(newGapR - gapR * 1.5) <= 1 && Math.Abs(newGapB - gapB * 1.5) <= 1,
                       "anchored Right|Bottom gaps " + gapR + "," + gapB + " became " + newGapR + "," + newGapB + " (want x1.5)");
            Check.Equal(new Rectangle(11, 17, 20, 26), t.Odd.Bounds, "odd label bounds");
            Check.Equal(18f, t.Odd.Font.Size, "explicit font, px at 144");
            Check.Equal(180f, t.Table.ColumnStyles[0].Width, "absolute column");
        }

        private static void RoundTripUpDown()
        {
            var t = Build();
            var before = Snapshot(t);
            DpiScaler.Rescale(t.Root, 96, 144);
            DpiScaler.Rescale(t.Root, 144, 96);
            var after = Snapshot(t);
            after[4] = after[4].Replace("12Pixel", "9Point");   // the font comes back as the same size in pixel units
            Same(before, after, "after 96 -> 144 -> 96");
        }

        private static void RoundTripDownUp()
        {
            var t = Build();
            DpiScaler.Rescale(t.Root, 96, 144);
            var at144 = Snapshot(t);
            DpiScaler.Rescale(t.Root, 144, 96);
            DpiScaler.Rescale(t.Root, 96, 144);
            Same(at144, Snapshot(t), "after 144 -> 96 -> 144");
        }

        private static void SplitContainerScales()
        {
            var root = new Panel { Size = new Size(800, 400) };
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 1 };
            root.Controls.Add(split);
            root.PerformLayout();
            split.Panel1MinSize = 100; split.Panel2MinSize = 200; split.SplitterDistance = 300;
            DpiScaler.Rescale(root, 96, 144);
            Check.Equal(450, split.SplitterDistance, "splitter distance at 144");
            Check.Equal(150, split.Panel1MinSize, "Panel1MinSize at 144");
            Check.Equal(300, split.Panel2MinSize, "Panel2MinSize at 144");
            DpiScaler.Rescale(root, 144, 96);
            Check.Equal(300, split.SplitterDistance, "splitter distance back at 96");
            Check.Equal(100, split.Panel1MinSize, "Panel1MinSize back at 96");
        }

        private sealed class Aware : Panel, IDpiAware
        {
            public readonly List<string> Log;
            public Aware(List<string> log) { Log = log; }
            public void OnDpiChanged(int oldDpi, int newDpi) { Log.Add(Name + ":" + oldDpi + "->" + newDpi + " w=" + Width); }
        }

        private static void CallbacksAfterAndChildrenFirst()
        {
            var log = new List<string>();
            var parent = new Aware(log) { Name = "parent", Size = new Size(100, 100) };
            var child = new Aware(log) { Name = "child", Bounds = new Rectangle(0, 0, 40, 40) };
            parent.Controls.Add(child);
            DpiScaler.Rescale(parent, 96, 192);
            Check.Equal("child:96->192 w=80|parent:96->192 w=200", string.Join("|", log), "callback order and timing");
            Check.True(!Dpi.Busy, "Dpi.Busy still set after the rescale");
        }
    }
}
