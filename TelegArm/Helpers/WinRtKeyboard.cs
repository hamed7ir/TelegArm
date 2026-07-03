using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.ViewManagement;

namespace TelegArm.Helpers
{
    /// <summary>
    /// WinRT <see cref="InputPane"/> access from a classic WinForms desktop process, via the HWND-based
    /// IInputPaneInterop, to read the touch keyboard's ACTUAL occluded area (OccludedRect). The legacy TabTip
    /// window rect is the wrong position for the WinRT immersive keyboard on RT, so the composer ends up behind
    /// it — OccludedRect is the authoritative covered region.
    ///
    /// ALL interop is guarded: on a device where InputPane / IInputPaneInterop isn't reachable (possibly
    /// RT 8.1 — the interop is a Win10-era addition) it reports UNAVAILABLE and the caller falls back to the
    /// TabTip window rect. [KBD] logs whether it initialized (so the RT run reveals which path is used).
    /// </summary>
    public static class WinRtKeyboard
    {
        [ComImport, Guid("75CF2C57-9195-4931-8332-F0B409E916AF"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IInputPaneInterop
        {
            InputPane GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        }

        // The activation-factory interop obtained by asking RoGetActivationFactory for the interop IID directly
        // (the built-in WindowsRuntimeMarshal factory RCW won't QI to it on .NET Framework → InvalidCastException).
        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            [MarshalAs(UnmanagedType.HString)] string activatableClassId,
            [In] ref Guid iid,
            [MarshalAs(UnmanagedType.IInspectable)] out object factory);

        private static readonly Guid IID_IInputPaneInterop = new Guid("75CF2C57-9195-4931-8332-F0B409E916AF");

        private static bool _tried;
        private static InputPane _pane;

        /// <summary>Resolves the InputPane for <paramref name="hwnd"/> once (memoized). False if unavailable.</summary>
        public static bool TryInit(IntPtr hwnd)
        {
            if (_tried) return _pane != null;
            _tried = true;

            // Approach 1: RoGetActivationFactory for the interop IID directly (canonical for the HWND interop).
            try
            {
                Guid interopIid = IID_IInputPaneInterop;
                RoGetActivationFactory("Windows.UI.ViewManagement.InputPane", ref interopIid, out object f);
                var interop = (IInputPaneInterop)f;
                Guid paneIid = typeof(InputPane).GUID;
                _pane = interop.GetForWindow(hwnd, ref paneIid);
                Debug.WriteLine("[KBD] InputPane interop OK via RoGetActivationFactory (pane=" + (_pane != null) + ")");
                if (_pane != null) return true;
            }
            catch (Exception ex) { Debug.WriteLine("[KBD] RoGetActivationFactory interop failed: " + ex.GetType().Name + ": " + ex.Message); }

            // Approach 2: the managed activation factory + QI (works on some runtimes).
            try
            {
                var factory = WindowsRuntimeMarshal.GetActivationFactory(typeof(InputPane));
                var interop = (IInputPaneInterop)factory;
                Guid paneIid = typeof(InputPane).GUID;
                _pane = interop.GetForWindow(hwnd, ref paneIid);
                Debug.WriteLine("[KBD] InputPane interop OK via GetActivationFactory (pane=" + (_pane != null) + ")");
            }
            catch (Exception ex)
            {
                _pane = null;
                Debug.WriteLine("[KBD] InputPane interop UNAVAILABLE → TabTip fallback: " + ex.GetType().Name + ": " + ex.Message);
            }
            return _pane != null;
        }

        /// <summary>Reads the keyboard's occluded rectangle (X/Y/Width/Height; window-relative DIPs). False if
        /// unavailable or empty (height ≤ 0).</summary>
        public static bool TryOccludedRect(out double x, out double y, out double w, out double h)
        {
            x = y = w = h = 0;
            try
            {
                if (_pane == null) return false;
                var r = _pane.OccludedRect;
                x = r.X; y = r.Y; w = r.Width; h = r.Height;
                return h > 0;
            }
            catch { return false; }
        }
    }
}
