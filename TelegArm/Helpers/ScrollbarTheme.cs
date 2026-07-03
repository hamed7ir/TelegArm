using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Applies the OS dark/light scrollbar visual style to a control's non-client scrollbars
    /// (AutoScroll panels, list views, etc.) via uxtheme's SetWindowTheme. Themed scrollbars
    /// appear on Windows 10 1809+ ("DarkMode_Explorer"); on older builds (e.g. RT 8.1) the call
    /// is a harmless no-op, so the white-scrollbar-in-dark-theme issue is fixed where supported.
    /// </summary>
    public static class ScrollbarTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        public static void Apply(Control c, bool dark)
        {
            if (c == null) return;
            void DoApply()
            {
                try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
            }
            if (c.IsHandleCreated) DoApply();
            c.HandleCreated += (s, e) => DoApply();
        }
    }
}
