using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>Redesign step 3.5 — the caches that grow per DPI (scaled emoji images, menu icons), trimmed after a DPI
    /// change to the sizes the open windows still use. Called at the end of every WindowDpi rescale; never throws.</summary>
    public static class DpiCaches
    {
        /// <summary>After a window's DPI changed: drop what no open window can still use, and log the cache sizes.</summary>
        public static void AfterDpiChange()
        {
            try
            {
                var dpis = LiveDpis();
                var keep = new HashSet<int>();
                var keepIcons = new HashSet<int>();
                int[] logical = EmojiRenderer.LogicalSizes;
                foreach (int d in dpis)
                {
                    foreach (int n in logical) keep.Add(Dpi.Px(n, d));
                    keepIcons.Add(Dpi.Px(TelegArm.UI.MenuIcons.IconSize, d));
                }
                int dropped = EmojiRenderer.TrimScaled(keep);
                TelegArm.UI.MenuIcons.Trim(keepIcons);
                if (!Logger.Enabled) return;
                uint gdi, usr;
                PerfLog.GuiHandles(out gdi, out usr);
                Logger.Diag("[DPI] caches scaled=" + EmojiRenderer.ScaledCount + " (dropped " + dropped + ", keep " + Join(keep) + ")"
                            + " icons=keep " + Join(keepIcons) + " fonts=" + FontCache.Count
                            + " ws=" + (Environment.WorkingSet / (1024 * 1024)) + "MB gdi=" + gdi + " usr=" + usr
                            + " dpis=" + Join(dpis));
            }
            catch (Exception ex) { Logger.Diag("[DPI] cache trim failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>The effective DPI of every open window (an IDpiHost's own, any other form's DefaultDpi), plus
        /// DefaultDpi itself: menus and drop-downs are not in OpenForms, and Dpi.Of answers DefaultDpi without a host.</summary>
        private static HashSet<int> LiveDpis()
        {
            var set = new HashSet<int> { Dpi.DefaultDpi };
            foreach (Form f in Application.OpenForms)
            {
                if (f == null || f.IsDisposed) continue;
                var host = f as IDpiHost;
                int d = host != null ? host.Dpi : 0;
                set.Add(d > 0 ? d : Dpi.DefaultDpi);
            }
            return set;
        }

        private static string Join(IEnumerable<int> values)
        {
            var list = new List<int>(values);
            list.Sort();
            return "[" + string.Join(",", list) + "]";
        }
    }
}
