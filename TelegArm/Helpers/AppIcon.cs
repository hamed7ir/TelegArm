using System.Drawing;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>The app icon for every window. Redesign step 1.8 (DroidFinestra's pattern); moved here from 1.x's ThemedChrome
    /// shim when step 2.6b deleted it (its Apply/BarH title bar is DialogForm's now).</summary>
    public static class AppIcon
    {
        private static Icon _icon;
        private static bool _tried;

        /// <summary>The app icon at ALL its sizes: img-icon/icon.ico embedded as the resource "app.ico" (16/20/24/28/32/40/48/
        /// 64/128/256). Form.Icon takes the small title-bar icon from it at SystemInformation.SmallIconSize and the big
        /// (taskbar / Alt-Tab) one at SM_CXICON, so both are a real frame at 100/125/150% instead of 1.x's
        /// ExtractAssociatedIcon, which hands back ONE size for Windows to stretch. Falls back to the exe's associated icon,
        /// then null. Shared by every window: never dispose it.</summary>
        public static Icon Icon
        {
            get
            {
                if (_tried) return _icon;
                _tried = true;
                try
                {
                    using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico"))
                        if (s != null) _icon = new Icon(s);
                }
                catch { }
                if (_icon == null)
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                return _icon;
            }
        }

        /// <summary>Sets the app icon on a form — so every window (taskbar / Alt-Tab / title bar) bears it instead of the
        /// WinForms default. RT-safe: a no-op if there is no icon (the form still opens). Call from any form's setup.</summary>
        public static void Set(Form form)
        {
            if (form == null) return;
            var ico = Icon;
            if (ico != null) try { form.Icon = ico; } catch { }
        }
    }
}
