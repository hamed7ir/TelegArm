using System.Drawing;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Wraps a host container with a vertically-scrolling body whose scrollbar is DARK on every Windows version
    /// including RT 8.1 — a <see cref="NoNativeScrollPanel"/> (AutoScroll, native bars suppressed) + the
    /// owner-drawn <see cref="ThemedScrollBar"/> overlay. Add your (top-anchored, growing-downward) child
    /// controls to the returned panel; it scrolls when they exceed the height. Used inside a ThemedChrome
    /// content panel so every long admin form satisfies the scrollable + RT-dark-scrollbar checklist for free.
    /// </summary>
    public static class ScrollHost
    {
        public static NoNativeScrollPanel Wrap(Control host, bool dark, Color accent)
        {
            var scroll = new NoNativeScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
            host.Controls.Add(scroll);   // Fill — added first so it docks last and fills the leftover space
            host.Controls.Add(new ThemedScrollBar(scroll, dark, accent) { Dock = DockStyle.Right });
            TouchScroller.Enable(scroll, false);   // finger-pan scrolling (RT touch) for every ScrollHost body (admin forms)
            return scroll;
        }
    }
}
