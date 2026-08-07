using System;
using System.Runtime.InteropServices;

namespace TelegArm.Helpers
{
    /// <summary>BATCH-TA-37/A1 — the COM interface Windows calls when an Action Center entry is clicked.
    /// IID is fixed by Windows; it is not ours to choose.</summary>
    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 3)]
                NOTIFICATION_USER_INPUT_DATA[] data,
            uint count);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFICATION_USER_INPUT_DATA
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Key;
        [MarshalAs(UnmanagedType.LPWStr)] public string Value;
    }

    /// <summary>BATCH-TA-37/A2 — the activator Windows instantiates to deliver a toast click.
    ///
    /// ⚠ IT OPENS NOTHING ITSELF. It parses the launch args and raises <see cref="Activated"/>, which
    /// MainForm wires to the SAME handler <see cref="UI.NotificationStack"/>.Clicked uses — so a click on
    /// an Action Center entry and a click on our own notification window end in one implementation
    /// (OpenNotifiedChat). A second open-chat path is the duplicate-surface bug this project keeps hitting.
    ///
    /// ⚠ THE CALL ARRIVES ON A COM/RPC THREAD, never the UI thread, and it can arrive BEFORE MainForm
    /// exists (the app was closed and Windows launched it for this activation). Both are handled by
    /// parking the result in <see cref="Pending"/> and letting MainForm drain it once it is ready.</summary>
    [ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
    public class ToastActivator : INotificationActivationCallback
    {
        /// <summary>⚠ MUST MATCH the System.AppUserModel.ToastActivatorCLSID stamped on the Start-Menu
        /// shortcut AND the HKCU\Software\Classes\CLSID\{guid}\LocalServer32 key — both written by
        /// installer/anycpu/Setup.cs. Three copies of one GUID; change one, change all three.</summary>
        public const string Clsid = "6E7B4A2C-9F31-4E58-B0D2-1C7A5E9D3F84";

        /// <summary>(accountId, peerId, messageId) from a clicked Action Center entry.</summary>
        public static event Action<long, long, int> Activated;

        /// <summary>An activation that arrived before anyone was listening — i.e. the app was launched BY
        /// the click. MainForm drains this once its handler is attached. Without it, the single most
        /// important case (click while closed) would open the app to no chat at all.</summary>
        public static Tuple<long, long, int> Pending;

        private static readonly object Gate = new object();

        public void Activate(string appUserModelId, string invokedArgs,
                             NOTIFICATION_USER_INPUT_DATA[] data, uint count)
        {
            try
            {
                Logger.Diag("[SHELL] activation received args=\"" + (invokedArgs ?? "") + "\"");
                long acct, peer; int msg;
                if (!TryParse(invokedArgs, out acct, out peer, out msg)) return;
                Raise(acct, peer, msg);
            }
            catch (Exception ex) { Logger.Diag("[SHELL] activation failed: " + ex.Message); }
        }

        /// <summary>A6 — the launch string carries the identity, so a click opens the right chat under the
        /// right ACCOUNT. Same per-window identity the notification stack already needed: a shared
        /// "last notified" slot is wrong the moment two chats have entries.</summary>
        public static string BuildArgs(long acctId, long peerId, int msgId)
        {
            return "acct=" + acctId + "&peer=" + peerId + "&msg=" + msgId;
        }

        private static bool TryParse(string args, out long acct, out long peer, out int msg)
        {
            acct = 0; peer = 0; msg = 0;
            if (string.IsNullOrEmpty(args)) return false;
            foreach (var part in args.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                if (k == "acct") long.TryParse(v, out acct);
                else if (k == "peer") long.TryParse(v, out peer);
                else if (k == "msg") int.TryParse(v, out msg);
            }
            return peer != 0;
        }

        private static void Raise(long acct, long peer, int msg)
        {
            lock (Gate)
            {
                var h = Activated;
                if (h == null) { Pending = Tuple.Create(acct, peer, msg); return; }   // app still starting
                h(acct, peer, msg);
            }
        }

        /// <summary>Called by MainForm once its handler is attached, to deliver an activation that arrived
        /// while the app was still starting.</summary>
        public static void DrainPending()
        {
            Tuple<long, long, int> p;
            lock (Gate) { p = Pending; Pending = null; }
            if (p == null) return;
            var h = Activated;
            if (h != null) h(p.Item1, p.Item2, p.Item3);
        }

        // ── A3 — registration, via RegistrationServices. No hand-written class factory. ──────────
        private static RegistrationServices _reg;
        private static int _cookie;

        /// <summary>Publishes the class object so COM can hand out activations. Must run EARLY: when the
        /// app is launched BY a click, COM waits for this registration and times out if the process does
        /// its heavy startup first.
        /// ⚠ A7 — gated by the caller on ShellNotify.Available, which requires the AUMID shortcut, so a
        /// portable copy never registers.</summary>
        public static bool Register()
        {
            try
            {
                if (_reg != null) return true;
                _reg = new RegistrationServices();
                _cookie = _reg.RegisterTypeForComClients(typeof(ToastActivator),
                    RegistrationClassContext.LocalServer,
                    RegistrationConnectionType.MultipleUse);
                Logger.Diag("[SHELL] activator registered clsid={" + Clsid + "} cookie=" + _cookie);
                return true;
            }
            catch (Exception ex)
            {
                _reg = null;
                Logger.Diag("[SHELL] activator registration FAILED: " + ex.Message);
                return false;
            }
        }

        /// <summary>A4e — revoke on shutdown, so COM does not hand out a class object belonging to a
        /// process that is going away.</summary>
        public static void Revoke()
        {
            try
            {
                if (_reg == null) return;
                _reg.UnregisterTypeForComClients(_cookie);
                Logger.Diag("[SHELL] activator revoked");
            }
            catch { }
            finally { _reg = null; _cookie = 0; }
        }
    }
}
