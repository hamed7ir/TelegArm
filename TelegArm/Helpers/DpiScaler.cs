using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.1 — rescales a control tree from one DPI to another: what WindowDpi (step 1.3/3.x)
    /// runs on WM_DPICHANGED, and what the gallery exercises 100 → 150 → 100.
    ///
    /// For every control it scales, by newDpi/oldDpi: bounds (docked ones too — see ScaleBounds); Padding; Margin;
    /// Minimum/MaximumSize; AutoScrollMargin/MinSize; a TableLayoutPanel's Absolute row/column styles; a
    /// SplitContainer's distance, splitter width and panel minimums; and an EXPLICITLY set Font (an inherited
    /// one follows its parent), through FontCache as a pixel-unit font. Then IDpiAware.OnDpiChanged.
    ///
    /// ★ ORDER — PARENTS FIRST FOR BOUNDS, CHILDREN FIRST FOR THE CALLBACKS. The plan says post-order; for the
    ///   callbacks it is right (a container's OnDpiChanged sees its children done). For bounds, parent-first is
    ///   the order WinForms' own Control.Scale uses, kept as the conservative choice — NOT because the opposite
    ///   was shown to break: measured in SelfTest (handle-less controls, Right|Bottom anchors with real 20 px
    ///   gaps), children-first gave identical results. Re-check with real windows in the step-1.3 gallery.
    /// ★ NO DRIFT. Each scaled integer remembers the exact float it was rounded from; if nothing changed it since,
    ///   the next rescale starts from that float, so 100 → 150 → 100 and 150 → 100 → 150 land on the original pixel.
    /// ⚠ A Form root's size is NOT ratio-scaled — pass the bounds Windows suggested (WM_DPICHANGED's RECT). It is
    ///   applied before the children, for the anchoring reason above.
    /// ⚠ Never reads Control.DeviceDpi (wrong under PMv1). The caller says which DPIs.</summary>
    public static class DpiScaler
    {
        public static void Rescale(Control root, int oldDpi, int newDpi, Rectangle? rootBounds = null)
        {
            if (root == null || oldDpi <= 0 || newDpi <= 0 || oldDpi == newDpi) return;
            float r = newDpi / (float)oldDpi;
            var scrolled = new List<KeyValuePair<ScrollableControl, Point>>();
            Dpi.EnterBusy();
            try
            {
                root.SuspendLayout();
                try
                {
                    ScaleLimits(root, r);   // before the bounds: an old MinimumSize would clamp a shrinking root
                    if (rootBounds.HasValue) root.Bounds = rootBounds.Value;
                    else if (!(root is Form)) root.Size = new Size(S(root, "w", root.Width, r), S(root, "h", root.Height, r));
                    UnScroll(root, r, scrolled);
                    ScaleOwn(root, r, oldDpi, newDpi);
                    foreach (Control c in root.Controls) ScaleTree(c, r, oldDpi, newDpi, scrolled);
                    Notify(root, oldDpi, newDpi);
                }
                finally { root.ResumeLayout(true); }
                // After the final layout, when the scroll ranges are the new ones (review: a scrolled container had its
                // offset baked into the children's scaled bounds, and its scroll position never scaled).
                foreach (var s in scrolled) try { s.Key.AutoScrollPosition = s.Value; } catch { }
            }
            finally { Dpi.ExitBusy(); }   // ⚠ ALWAYS: a stuck Busy would make every handler that defers to it skip forever (review)
        }

        private static void ScaleTree(Control c, float r, int oldDpi, int newDpi, List<KeyValuePair<ScrollableControl, Point>> scrolled)
        {
            c.SuspendLayout();
            try
            {
                ScaleLimits(c, r);
                ScaleBounds(c, r);
                UnScroll(c, r, scrolled);
                ScaleOwn(c, r, oldDpi, newDpi);
                foreach (Control child in c.Controls) ScaleTree(child, r, oldDpi, newDpi, scrolled);
            }
            // A container lays itself out here (review 1.1: the root's final pass reaches a nested container only if its
            // size changes in that pass, so one we sized ourselves might keep its children's interim bounds).
            // UNPROVEN: headless, ResumeLayout(false) also gave the right result (mutation test) — kept as the safe
            // choice at one layout pass per container; re-check with real windows in the step-1.3 gallery.
            // A leaf has nothing to lay out.
            finally { c.ResumeLayout(c.Controls.Count > 0); }
        }

        /// <summary>A scrolled AutoScroll container: scroll it home before its children are scaled (their Bounds carry
        /// the offset), and remember the scaled position to restore after the final layout.</summary>
        private static void UnScroll(Control c, float r, List<KeyValuePair<ScrollableControl, Point>> scrolled)
        {
            var sc = c as ScrollableControl;
            if (sc == null || !sc.AutoScroll) return;
            var p = sc.AutoScrollPosition;   // negative when scrolled
            if (p.X == 0 && p.Y == 0) return;
            sc.AutoScrollPosition = Point.Empty;
            scrolled.Add(new KeyValuePair<ScrollableControl, Point>(sc, new Point(S(c, "scx", -p.X, r), S(c, "scy", -p.Y, r))));
        }

        private static void Notify(Control c, int oldDpi, int newDpi)
        {
            foreach (Control child in c.Controls) Notify(child, oldDpi, newDpi);
            var a = c as IDpiAware;
            if (a == null) return;
            // One failing callback must not leave the rest of the tree at the old DPI (review).
            try { a.OnDpiChanged(oldDpi, newDpi); }
            catch (Exception ex) { Logger.Diag("[DPI] " + c.GetType().Name + ".OnDpiChanged threw: " + ex.Message); }
        }

        /// <summary>All four values, docked or not. A docked axis is the layout engine's and the final ResumeLayout
        /// sets it exactly — but until then a docked container would still be its OLD size, and everything inside it
        /// judged against that: an anchored child's distances, a SplitContainer's distance (which would even throw,
        /// out of range). Pre-scaling gives the interim the right size; layout then corrects it to the pixel.</summary>
        private static void ScaleBounds(Control c, float r)
        {
            var b = c.Bounds;
            c.SetBounds(S(c, "x", b.X, r), S(c, "y", b.Y, r), S(c, "w", b.Width, r), S(c, "h", b.Height, r));
        }

        private static void ScaleLimits(Control c, float r)
        {
            if (!c.MinimumSize.IsEmpty) c.MinimumSize = new Size(S(c, "minw", c.MinimumSize.Width, r), S(c, "minh", c.MinimumSize.Height, r));
            if (!c.MaximumSize.IsEmpty) c.MaximumSize = new Size(S(c, "maxw", c.MaximumSize.Width, r), S(c, "maxh", c.MaximumSize.Height, r));
        }

        private static void ScaleOwn(Control c, float r, int oldDpi, int newDpi)
        {
            c.Padding = Scale(c, "pad", c.Padding, r);
            c.Margin = Scale(c, "mar", c.Margin, r);

            var sc = c as ScrollableControl;
            if (sc != null)
            {
                if (!sc.AutoScrollMargin.IsEmpty) sc.AutoScrollMargin = new Size(S(c, "asmw", sc.AutoScrollMargin.Width, r), S(c, "asmh", sc.AutoScrollMargin.Height, r));
                if (!sc.AutoScrollMinSize.IsEmpty) sc.AutoScrollMinSize = new Size(S(c, "asnw", sc.AutoScrollMinSize.Width, r), S(c, "asnh", sc.AutoScrollMinSize.Height, r));
            }

            var tlp = c as TableLayoutPanel;
            if (tlp != null)
            {
                foreach (ColumnStyle cs in tlp.ColumnStyles) if (cs.SizeType == SizeType.Absolute) cs.Width *= r;
                foreach (RowStyle rs in tlp.RowStyles) if (rs.SizeType == SizeType.Absolute) rs.Height *= r;
            }

            var split = c as SplitContainer;
            if (split != null)
            {
                split.Panel1MinSize = S(c, "p1min", split.Panel1MinSize, r);
                split.Panel2MinSize = S(c, "p2min", split.Panel2MinSize, r);
                split.SplitterWidth = Math.Max(1, S(c, "spw", split.SplitterWidth, r));
                // FixedPanel.None keeps its RATIO on resize by itself; a fixed panel keeps its PIXELS, so its distance
                // is ours to scale (MainForm's chat-list split is FixedPanel.Panel1).
                if (split.FixedPanel != FixedPanel.None)
                {
                    int d = S(c, "spd", split.SplitterDistance, r);
                    try { split.SplitterDistance = d; } catch (InvalidOperationException) { }   // outside the new limits: keep WinForms' clamp
                }
            }

            if (HasOwnFont(c)) c.Font = FontCache.Rescale(c.Font, oldDpi, newDpi);
        }

        private static readonly MethodInfo ShouldSerializeFont =
            typeof(Control).GetMethod("ShouldSerializeFont", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>True when the font was set ON this control rather than inherited (an inherited font follows its
        /// parent automatically, and setting it here would freeze it).</summary>
        private static bool HasOwnFont(Control c)
        {
            try { return ShouldSerializeFont != null && (bool)ShouldSerializeFont.Invoke(c, null); }
            catch { return false; }
        }

        // ── drift-free integer scaling ───────────────────────────────────────────────────────────────────────

        private sealed class Exact { public readonly Dictionary<string, KeyValuePair<int, float>> V = new Dictionary<string, KeyValuePair<int, float>>(); }
        private static readonly ConditionalWeakTable<Control, Exact> _exact = new ConditionalWeakTable<Control, Exact>();

        /// <summary><paramref name="current"/> × <paramref name="r"/>, rounded — but computed from the exact value it
        /// was last rounded from when nothing has changed it since, so repeated rescales never accumulate error.</summary>
        private static int S(Control c, string key, int current, float r)
        {
            var ex = _exact.GetOrCreateValue(c);
            KeyValuePair<int, float> last;
            float basis = ex.V.TryGetValue(key, out last) && last.Key == current ? last.Value : current;
            float exact = basis * r;
            int result = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
            ex.V[key] = new KeyValuePair<int, float>(result, exact);
            return result;
        }

        private static Padding Scale(Control c, string key, Padding p, float r)
        {
            if (p == Padding.Empty) return p;
            return new Padding(S(c, key + "l", p.Left, r), S(c, key + "t", p.Top, r), S(c, key + "r", p.Right, r), S(c, key + "b", p.Bottom, r));
        }
    }
}
