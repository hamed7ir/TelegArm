using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Pops the Windows on-screen keyboard (TabTip.exe) when the user taps an editable text field
    /// BY TOUCH and no physical keyboard is attached — like a proper tablet app. A single global
    /// message filter watches touch-sourced left-button-downs on text controls; plain mouse clicks
    /// (desktop) and keyboard-equipped machines never trigger it. Best-effort: if TabTip is missing
    /// it simply does nothing (never throws).
    /// </summary>
    public static class TouchKeyboard
    {
        // A mouse message synthesized from touch/pen carries this signature in the message extra info.
        [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
        private const long MI_WP_SIGNATURE = 0xFF515700;
        private const long SIGNATURE_MASK = 0xFFFFFF00;   // 0xFFFFFF00 catches both touch and pen

        public static bool LastInputWasTouch()
            => (GetMessageExtraInfo().ToInt64() & SIGNATURE_MASK) == MI_WP_SIGNATURE;

        // Laptop-vs-tablet detection — the signal Windows itself uses on convertibles (Surface).
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CONVERTIBLESLATEMODE = 0x2003;   // 0 = slate/tablet, nonzero = laptop (keyboard usable)
        private const int SM_TABLETPC = 86;

        /// <summary>
        /// True when the device is in slate/tablet posture (keyboard folded back or detached), i.e.
        /// no usable hardware keyboard right now. On a convertible (Surface Book 2) this flips with
        /// the keyboard; on a plain desktop the call returns 0, but we never pop without a touch tap
        /// anyway, so a mouse/keyboard desktop is unaffected. Devices that aren't tablet/convertible
        /// at all report no slate mode → treated as "has keyboard" (never pop).
        /// </summary>
        public static bool IsTabletMode()
        {
            try
            {
                // Only meaningful on tablet/convertible hardware; elsewhere assume a keyboard.
                bool tabletCapable = GetSystemMetrics(SM_TABLETPC) != 0;
                if (!tabletCapable) return false;
                return GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;   // 0 ⇒ slate (no usable keyboard)
            }
            catch { return false; }
        }

        // ── Physical-keyboard presence (for the on-screen-keyboard window shrink) ──
        // WHY: on the original Surface RT (ARM) SM_CONVERTIBLESLATEMODE reports SLATE permanently even with a
        // Type Cover attached (that device has no convertible-hinge sensor), so Windows auto-pops TabTip on any
        // text-field focus. The app must then NOT shrink for it — but only if a real keyboard is attached.
        // The authoritative signal is the set of PRESENT keyboard-class devices (SetupAPI, same as Device Manager),
        // NOT WinRT KeyboardCapabilities.KeyboardPresent: on the RT a phantom "HID Keyboard Device" is ALWAYS
        // enumerated (the cover-connector's embedded controller), so KeyboardPresent is stuck at true even with no
        // cover. The cover is the SECOND keyboard ("Surface … Cover Filter Device"), present only while attached.
        private static bool _hwKbCached;
        private static int _hwKbAt;   // Environment.TickCount of the last real query (0 = never)

        /// <summary>True when a PHYSICAL keyboard (Type Cover / USB) is attached, so the user isn't relying on
        /// the on-screen keyboard. Cached ~1s (attach/detach is rare; the shrink timer polls at 350ms).</summary>
        public static bool HasHardwareKeyboard()
        {
            int now = Environment.TickCount;
            if (_hwKbAt != 0 && (uint)(now - _hwKbAt) < 1000) return _hwKbCached;
            _hwKbAt = now;
            _hwKbCached = QueryHardwareKeyboard();
            return _hwKbCached;
        }

        private static bool _verdictLogged;
        private static bool _lastVerdict;

        private static bool QueryHardwareKeyboard()
        {
            bool present; string src;
            // PRIMARY — enumerate PRESENT keyboard-class devices exactly like Device Manager (SetupAPI + DIGCF_PRESENT).
            // On the Surface RT a single phantom "HID Keyboard Device" is ALWAYS present (the cover-connector's
            // embedded controller), which is why WinRT KeyboardPresent is permanently true even with no cover. A REAL
            // keyboard is the *second* device: the Type/Touch Cover appears as an extra keyboard whose name contains
            // "Cover" (e.g. "Surface Type Cover Filter Device") only while physically attached, and a plain USB
            // keyboard pushes the present-keyboard count past 1. Either ⇒ a hardware keyboard is attached.
            int kbCount; bool cover;
            if (TryEnumPresentKeyboards(out kbCount, out cover))
            {
                present = cover || kbCount >= 2;
                src = "setupapi(kb=" + kbCount + ",cover=" + cover + ")";
            }
            else
            {
                // Fallbacks if SetupAPI is unavailable: WinRT (over-reports on the RT), then raw-input count.
                try { present = new Windows.Devices.Input.KeyboardCapabilities().KeyboardPresent != 0; src = "winrt"; }
                catch
                {
                    try { present = RawKeyboardCount() > 1; src = "rawinput"; }
                    catch { present = false; src = "none"; }
                }
            }
            if (!_verdictLogged || present != _lastVerdict)
            {
                _verdictLogged = true; _lastVerdict = present;
                Debug.WriteLine("[KBD] hardware keyboard=" + present + " via=" + src);   // permanent truth line (attach/detach change)
            }
            return present;
        }

        [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList(
            [In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, ref uint uiNumDevices, uint cbSize);
        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
        private const uint RIM_TYPEKEYBOARD = 1;

        private static int RawKeyboardCount()
        {
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return 0;
            var list = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return 0;
            int kb = 0;
            for (int i = 0; i < count; i++) if (list[i].dwType == RIM_TYPEKEYBOARD) kb++;
            return kb;
        }

        // ── SetupAPI: enumerate PRESENT keyboard-class devices (identical to what Device Manager lists) ──
        // GUID_DEVCLASS_KEYBOARD {4D36E96B-E325-11CE-BFC1-08002BE10318}
        private static readonly Guid GUID_DEVCLASS_KEYBOARD =
            new Guid(0x4d36e96b, 0xe325, 0x11ce, 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18);
        private const int DIGCF_PRESENT = 0x02;
        private const int SPDRP_DEVICEDESC = 0x00;
        private const int SPDRP_FRIENDLYNAME = 0x0C;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            int Property, out int PropertyRegDataType, byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        /// <summary>Counts present keyboard-class devices and flags whether any is a Surface cover
        /// ("… Cover Filter Device"). Returns false if SetupAPI can't be queried (so callers fall back).</summary>
        private static bool TryEnumPresentKeyboards(out int count, out bool hasCover)
        {
            count = 0; hasCover = false;
            Guid kbClass = GUID_DEVCLASS_KEYBOARD;   // local copy — can't pass a static readonly field by ref
            IntPtr set = SetupDiGetClassDevs(ref kbClass, null, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == INVALID_HANDLE_VALUE) return false;
            try
            {
                var did = new SP_DEVINFO_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
                {
                    count++;
                    string name = GetDeviceProp(set, ref did, SPDRP_FRIENDLYNAME)
                                  ?? GetDeviceProp(set, ref did, SPDRP_DEVICEDESC);
                    if (!string.IsNullOrEmpty(name) && name.IndexOf("Cover", StringComparison.OrdinalIgnoreCase) >= 0)
                        hasCover = true;
                }
                return true;
            }
            catch { return false; }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string GetDeviceProp(IntPtr set, ref SP_DEVINFO_DATA did, int prop)
        {
            int type, req;
            SetupDiGetDeviceRegistryProperty(set, ref did, prop, out type, null, 0, out req);   // size probe
            if (req <= 0) return null;
            var buf = new byte[req];
            if (!SetupDiGetDeviceRegistryProperty(set, ref did, prop, out type, buf, buf.Length, out req)) return null;
            return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }

        private static readonly string[] TabTipPaths =
        {
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\Microsoft Shared\ink\TabTip.exe"),
            @"C:\Program Files\Common Files\Microsoft Shared\ink\TabTip.exe"
        };

        /// <summary>Launches the touch keyboard (no-op if TabTip.exe is absent).</summary>
        public static void Show()
        {
            foreach (var p in TabTipPaths)
                try { if (File.Exists(p)) { Process.Start(p); return; } } catch { }
        }

        /// <summary>Install once via <c>Application.AddMessageFilter</c>.</summary>
        public sealed class Filter : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201;

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == WM_LBUTTONDOWN && LastInputWasTouch() && IsTabletMode())
                {
                    // Editable text field tapped by touch while in tablet posture → pop the OSK.
                    if (Control.FromHandle(m.HWnd) is TextBoxBase tb && !tb.ReadOnly)
                        Show();
                }
                return false;   // never consume — the normal click/focus still happens
            }
        }
    }
}
