using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegArm.Helpers
{
    /// <summary>BATCH-TA-33 — THE ONE PLACE THAT TALKS TO THE WINDOWS SHELL.
    ///
    /// Action Center entries (N5), the taskbar icon badge, and the Start tile + tile badge (N6) all need
    /// the same three things: the app's AUMID, late-bound WinRT, and a capability probe. Keeping them in
    /// one file is the point — three copies of this reflection is three places to get the AUMID rule wrong.
    ///
    /// ══ THE RULE THAT GOVERNS EVERY WINRT CALL BELOW, MEASURED THIS SESSION ══
    /// ★ AN UNPACKAGED DESKTOP APP HAS NO PACKAGE IDENTITY, so EVERY parameterless WinRT notification
    ///   overload throws HRESULT 0x80070490 (ERROR_NOT_FOUND) — CreateToastNotifier(),
    ///   History.Remove(tag,group), History.Clear(), CreateTileUpdaterForApplication(). The overloads that
    ///   TAKE AN AUMID all work. Never call an AUMID-less overload from here.
    ///   (mdfiles/probes/AumidDiscrim.cs, HistoryOverloads.cs — every ★ row OK, every AUMID-less row threw.)
    /// ★ TA-25 RECORDED THAT A MISSING SHORTCUT MEANS A SILENT DROP. IT DOES NOT — it throws, and that is
    ///   good news: <see cref="Available"/> can therefore REPORT the reason instead of the feature just
    ///   appearing broken on a portable copy.
    /// ★ REFLECTION MUST USE MethodInfo.Invoke / PropertyInfo.SetValue. InvokeMember throws on every WinRT
    ///   RCW ("COM target does not implement IDispatch").
    /// ★ Tag, Group AND SuppressPopup are all Windows-10-only members; the csproj is pinned to the 8.1
    ///   winmd for RT, so none of them can be bound early. Hence the whole file is late-bound.
    /// ★ On RT 8.1 the ApiInformation TYPE itself does not exist, so Type.GetType(..., false) returns null
    ///   and the entire path is skipped with no exception thrown. That is the intended RT behaviour.</summary>
    internal static class ShellNotify
    {
        /// <summary>⚠ MUST MATCH THE AppUserModelID SET ON THE START-MENU SHORTCUT BY **BOTH** INSTALLERS —
        /// installer/anycpu/Setup.cs (IPropertyStore on the IShellLink) and TelegArm.iss ([Icons]
        /// AppUserModelID). Without a shortcut carrying this exact string, Windows accepts every call and
        /// then delivers nothing. Shaped after the existing publisher/product identifiers.</summary>
        public const string Aumid = "hamed7ir.TelegArm";

        [DllImport("shell32.dll")]
        private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

        private static Type _tXml, _tToast, _tToastMgr, _tHistory, _tTileMgr, _tTileUpd, _tTileNotif,
                            _tBadgeMgr, _tBadgeNotif;
        private static object _notifier, _history, _tileUpdater, _badgeUpdater;
        private static bool _init;

        /// <summary>True when ACTION CENTER delivery is possible: Windows 10/11 (SuppressPopup present)
        /// AND a Start-Menu shortcut carrying our AUMID. False on RT 8.1 and Windows 7.</summary>
        public static bool Available { get; private set; }

        /// <summary>★ TRUE WHEN THE **START TILE** IS POSSIBLE — WHICH IS A DIFFERENT QUESTION.
        ///
        /// ⚠ THIS EXISTS BECAUSE GATING THE TILE ON <see cref="Available"/> WAS A REAL BUG, AND IT
        ///   DISABLED THE TILE ON EXACTLY THE DEVICE THE TILE IS FOR. Action Center support is detected via
        ///   ApiInformation + ToastNotification.SuppressPopup, both of which are **Windows 10+**.
        ///   `ApiInformation` DOES NOT EXIST ON RT 8.1 at all — so Init returned early there, Available
        ///   stayed false, and the live tile (an 8.1/10 feature that Windows 11 has since REMOVED) was
        ///   skipped on the only OS that can show one. The two capabilities are independent:
        ///     · Action Center  → Win10/11 only, needs SuppressPopup
        ///     · Live tile      → Win8.1/10, needs only TileUpdateManager, which 8.1 has
        ///   They are probed separately now, and a failure of one never disables the other.</summary>
        public static bool TileAvailable { get; private set; }

        /// <summary>Why <see cref="Available"/> is false, for the log and nothing else.</summary>
        public static string Reason { get; private set; }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string f, int mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string f, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string f);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string f);
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr pad; }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        private interface IPropertyStore
        {
            void GetCount(out uint c);
            void GetAt(uint i, out PROPERTYKEY k);
            void GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            void Commit();
        }

        /// <summary>Is there a Start-Menu shortcut whose AppUserModelID is ours? Checks the per-user and
        /// machine-wide Programs folders, which are the ONLY places Windows indexes for app identity.
        /// Cheap: a handful of .lnk reads, once per launch, and only for files named after the app.</summary>
        private static bool HasRegisteredShortcut()
        {
            try
            {
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                                             Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                {
                    if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) continue;
                    foreach (var lnk in System.IO.Directory.GetFiles(root, "TelegArm*.lnk",
                                                                     System.IO.SearchOption.AllDirectories))
                    {
                        if (string.Equals(ReadShortcutAumid(lnk), Aumid, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string ReadShortcutAumid(string lnkPath)
        {
            try
            {
                var link = new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                PROPVARIANT v;
                ((IPropertyStore)link).GetValue(ref key, out v);
                return v.p == IntPtr.Zero ? null : Marshal.PtrToStringUni(v.p);
            }
            catch { return null; }
        }

        private static Type WinRT(string name)
        {
            try { return Type.GetType(name + ", Windows, ContentType=WindowsRuntime", false); }
            catch { return null; }   // belt and braces: Windows 7 behaviour here is untested
        }

        /// <summary>Call ONCE at startup, before any window exists. Sets the process AUMID, probes the
        /// capability, and logs exactly one line saying which branch was taken — a feature that silently
        /// does nothing is indistinguishable from a broken one, and this path does nothing on two of the
        /// three OSes we support.</summary>
        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                // ══ ⚠⚠ ONLY CLAIM THE AUMID IF A START-MENU SHORTCUT ACTUALLY CARRIES IT ══════════
                // SetCurrentProcessExplicitAppUserModelID does NOT only affect notifications: Windows
                // resolves the TASKBAR ICON (and grouping, and pinning) through the AUMID. Claiming an
                // identity that no shortcut backs means Windows cannot find an icon for it and falls back
                // to a GENERIC one — which is exactly what happened to the portable build in v1.9.0, where
                // TelegArm's taskbar icon turned into a blank document.
                //
                // ⚠ AND THIS IS WHY THE CHECK IS THE RIGHT SHAPE FOR A DELETED SHORTCUT. It is not
                //   "read the icon from the shortcut" — it is "do not claim an identity that is not
                //   registered". If the user deletes the shortcut, the next launch simply does not claim
                //   the AUMID, and the icon reverts to the exe's own. The icon can never be left broken.
                //
                // ⚠ A SHORTCUT IN THE APP'S OWN FOLDER WOULD NOT WORK. Windows only indexes the Start-Menu
                //   locations for app identity; a .lnk anywhere else is never scanned and registers nothing.
                if (HasRegisteredShortcut())
                {
                    SetCurrentProcessExplicitAppUserModelID(Aumid);
                }
                else
                {
                    Logger.Diag("[SHELL] AUMID NOT claimed — no Start-Menu shortcut carries \"" + Aumid
                                + "\". Taskbar icon stays the exe's own; Action Center and tile are off. "
                                + "This is the normal portable/uninstalled state.");
                    Reason = "no Start-Menu shortcut";
                    return;   // nothing below can work without the identity
                }

                _tXml = WinRT("Windows.Data.Xml.Dom.XmlDocument");

                // ── THE TILE IS PROBED FIRST AND INDEPENDENTLY (see TileAvailable's remarks) ──
                // It needs neither ApiInformation nor SuppressPopup, so it must not sit behind either.
                InitTile();

                var api = WinRT("Windows.Foundation.Metadata.ApiInformation");
                if (api == null) { Fail("no WinRT ApiInformation (Windows 7 / RT 8.1) — Action Center skipped (tile unaffected)"); return; }

                var isProp = api.GetMethod("IsPropertyPresent", new[] { typeof(string), typeof(string) });
                bool win10 = isProp != null && (bool)isProp.Invoke(null, new object[]
                    { "Windows.UI.Notifications.ToastNotification", "SuppressPopup" });
                if (!win10) { Fail("OS has no ToastNotification.SuppressPopup — Action Center skipped"); return; }

                _tXml = WinRT("Windows.Data.Xml.Dom.XmlDocument");
                _tToast = WinRT("Windows.UI.Notifications.ToastNotification");
                _tToastMgr = WinRT("Windows.UI.Notifications.ToastNotificationManager");
                _tHistory = WinRT("Windows.UI.Notifications.ToastNotificationHistory");
                _tTileMgr = WinRT("Windows.UI.Notifications.TileUpdateManager");
                _tTileUpd = WinRT("Windows.UI.Notifications.TileUpdater");
                _tTileNotif = WinRT("Windows.UI.Notifications.TileNotification");
                _tBadgeMgr = WinRT("Windows.UI.Notifications.BadgeUpdateManager");
                _tBadgeNotif = WinRT("Windows.UI.Notifications.BadgeNotification");
                if (_tXml == null || _tToast == null || _tToastMgr == null || _tHistory == null)
                { Fail("WinRT notification types did not resolve"); return; }

                // ⚠ AUMID overload, never the parameterless one (see the class remarks).
                var create = _tToastMgr.GetMethod("CreateToastNotifier", BindingFlags.Public | BindingFlags.Static,
                                                  null, new[] { typeof(string) }, null);
                _notifier = create.Invoke(null, new object[] { Aumid });
                _history = _tToastMgr.GetProperty("History", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);

                // ★ THE REGISTRATION TEST. CreateToastNotifier(aumid) succeeds for ANY string — even an
                //   invented one — so it proves nothing. Reading .Setting is what throws 0x80070490 when no
                //   Start-Menu shortcut carries the AUMID. Measured: unregistered AUMIDs throw here,
                //   registered ones return Enabled. This is the whole of A6's diagnosis, in one property.
                string setting;
                try { setting = _notifier.GetType().GetProperty("Setting").GetValue(_notifier, null).ToString(); }
                catch (Exception ex)
                {
                    Fail("no Start-Menu shortcut carries AUMID \"" + Aumid + "\" (hr=0x"
                         + Marshal.GetHRForException(ex.InnerException ?? ex).ToString("X8")
                         + ") — running portable/uninstalled, so NO Action Center entry and NO tile. The"
                         + " notification WINDOW is unaffected.");
                    return;
                }
                if (setting != "Enabled") { Fail("Windows reports notifications " + setting + " for this app"); return; }

                Available = true;
                Logger.Diag("[SHELL] Action Center ON aumid=" + Aumid + " setting=" + setting
                            + " tile=" + (_tileUpdater != null) + " badge=" + (_badgeUpdater != null));
            }
            catch (Exception ex) { Fail("init threw: " + ex.Message); }
        }

        /// <summary>Probes the Start tile independently of Action Center. Works on Windows 8.1/10; on
        /// Windows 11 the calls succeed and simply have nowhere to appear (tiles were removed), which is
        /// NOT a failure and is not logged as one.</summary>
        private static void InitTile()
        {
            try
            {
                _tTileMgr = WinRT("Windows.UI.Notifications.TileUpdateManager");
                _tTileUpd = WinRT("Windows.UI.Notifications.TileUpdater");
                _tTileNotif = WinRT("Windows.UI.Notifications.TileNotification");
                _tBadgeMgr = WinRT("Windows.UI.Notifications.BadgeUpdateManager");
                _tBadgeNotif = WinRT("Windows.UI.Notifications.BadgeNotification");
                if (_tXml == null || _tTileMgr == null || _tTileUpd == null || _tTileNotif == null)
                { Logger.Diag("[SHELL] tile OFF — WinRT tile types absent (pre-Windows 8)"); return; }

                // ⚠ AUMID overload only — the parameterless form assumes package identity and throws.
                var mk = _tTileMgr.GetMethod("CreateTileUpdaterForApplication",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mk == null) { Logger.Diag("[SHELL] tile OFF — no AUMID overload"); return; }
                _tileUpdater = mk.Invoke(null, new object[] { Aumid });

                // Same registration signal as ToastNotifier.Setting: it throws 0x80070490 when no
                // Start-Menu shortcut carries the AUMID. If the property is absent on this OS, proceed —
                // a tile push that fails is caught and harmless, whereas refusing to try would disable
                // the feature on a platform we simply could not interrogate.
                var setting = _tileUpdater.GetType().GetProperty("Setting");
                if (setting != null)
                {
                    try { setting.GetValue(_tileUpdater, null); }
                    catch (Exception ex)
                    {
                        _tileUpdater = null;
                        Logger.Diag("[SHELL] tile OFF — no Start-Menu shortcut carries AUMID \"" + Aumid
                                    + "\" (hr=0x" + Marshal.GetHRForException(ex.InnerException ?? ex).ToString("X8")
                                    + "). Install TelegArm, then PIN IT TO START.");
                        return;
                    }
                }

                _tTileUpd.GetMethod("EnableNotificationQueue").Invoke(_tileUpdater, new object[] { true });

                var mkBadge = _tBadgeMgr == null ? null : _tBadgeMgr.GetMethod("CreateBadgeUpdaterForApplication",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mkBadge != null) _badgeUpdater = mkBadge.Invoke(null, new object[] { Aumid });

                TileAvailable = true;
                Logger.Diag("[SHELL] tile ON aumid=" + Aumid + " queue=5 badge=" + (_badgeUpdater != null)
                            + " — pin TelegArm to Start to see it (Windows 11 has no tile surface)");
            }
            catch (Exception ex) { _tileUpdater = null; Logger.Diag("[SHELL] tile OFF — " + ex.Message); }
        }

        private static void Fail(string why)
        {
            Available = false; Reason = why;
            Logger.Diag("[SHELL] Action Center OFF — " + why);
        }

        private static object Xml(string xml)
        {
            var d = Activator.CreateInstance(_tXml);
            _tXml.GetMethod("LoadXml", new[] { typeof(string) }).Invoke(d, new object[] { xml });
            return d;
        }

        internal static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                            .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // ── N5 — the silent Action Center entry ──────────────────────────────────────────────────
        /// <summary>Adds a SILENT Action Center entry for a notification our own window is already
        /// showing. Purely additive: if anything here fails the user still got the window.
        /// ⚠ &lt;audio silent="true"/&gt; IS NOT OPTIONAL. SuppressPopup stops the BANNER, not the SOUND —
        ///   assuming otherwise is how you ship a notification that dings twice.</summary>
        public static void PushToast(string title, string body, string tag, string group)
        {
            if (!Available || _notifier == null) return;
            try
            {
                string xml =
                    "<toast><visual><binding template='ToastGeneric'>" +
                    "<text>" + Esc(title) + "</text><text>" + Esc(body) + "</text>" +
                    "</binding></visual><audio silent='true'/></toast>";
                var toast = Activator.CreateInstance(_tToast, new object[] { Xml(xml) });
                _tToast.GetProperty("Tag").SetValue(toast, tag, null);          // message id
                _tToast.GetProperty("Group").SetValue(toast, group, null);      // peer id
                _tToast.GetProperty("SuppressPopup").SetValue(toast, true, null);
                _notifier.GetType().GetMethod("Show").Invoke(_notifier, new object[] { toast });
            }
            catch (Exception ex) { Logger.Diag("[SHELL] toast failed: " + ex.Message); }
        }

        /// <summary>A4 — the chat was read, so retire its Action Center entries.
        /// ⚠ RemoveGroup, not per-message Remove: the read signals give a peer and a watermark, not a list
        ///   of message ids, and "this conversation is dealt with" is exactly one group. Action Center caps
        ///   an app at 20 entries, so retiring by group is what keeps it reflecting what is actually unread.</summary>
        public static void RemoveGroup(long peerId)
        {
            if (!Available || _history == null) return;
            try
            {
                var m = _tHistory.GetMethod("RemoveGroup", new[] { typeof(string), typeof(string) });
                m.Invoke(_history, new object[] { peerId.ToString(), Aumid });
            }
            catch { /* history is best-effort; never let it reach the caller */ }
        }

        public static void ClearAll()
        {
            if (!Available || _history == null) return;
            try { _tHistory.GetMethod("Clear", new[] { typeof(string) }).Invoke(_history, new object[] { Aumid }); }
            catch { }
        }

        // ── N6 — the Start tile ─────────────────────────────────────────────────────────────────
        /// <summary>Pushes up to 5 tile notifications; Windows cycles them itself (the queue was enabled at
        /// Init). Wide AND square150 in every push, because the user can resize the tile and a template we
        /// did not supply renders as the plain app logo.
        /// ⚠ Windows 11 has NO TILE SURFACE — these calls are correct and simply have nowhere to appear.
        ///   That is not a failure and must not be logged as one.</summary>
        public static void PushTile(System.Collections.Generic.IList<Tuple<string, string>> items)
        {
            if (!TileAvailable || _tileUpdater == null || items == null) return;
            try
            {
                var upd = _tTileUpd.GetMethod("Update");
                _tTileUpd.GetMethod("Clear").Invoke(_tileUpdater, null);
                int n = Math.Min(5, items.Count);
                for (int i = 0; i < n; i++)
                {
                    string who = Esc(items[i].Item1), what = Esc(items[i].Item2);
                    string xml =
                        "<tile><visual version='3'>" +
                        "<binding template='TileMedium' branding='name'>" +
                          "<text hint-style='caption' hint-wrap='true'>" + who + "</text>" +
                          "<text hint-style='captionSubtle' hint-wrap='true'>" + what + "</text>" +
                        "</binding>" +
                        "<binding template='TileWide' branding='nameAndLogo'>" +
                          "<text hint-style='caption'>" + who + "</text>" +
                          "<text hint-style='captionSubtle' hint-wrap='true'>" + what + "</text>" +
                        "</binding>" +
                        "</visual></tile>";
                    upd.Invoke(_tileUpdater, new object[] { Activator.CreateInstance(_tTileNotif, new object[] { Xml(xml) }) });
                }
            }
            catch (Exception ex) { Logger.Diag("[SHELL] tile failed: " + ex.Message); }
        }

        public static void ClearTile()
        {
            if (!TileAvailable || _tileUpdater == null) return;
            try { _tTileUpd.GetMethod("Clear").Invoke(_tileUpdater, null); } catch { }
        }

        /// <summary>The numeric badge on the START TILE (distinct from the taskbar badge below, which works
        /// everywhere). 0 clears it.</summary>
        public static void SetTileBadge(int count)
        {
            if (!TileAvailable || _badgeUpdater == null) return;
            try
            {
                string xml = count > 0 ? "<badge value='" + count + "'/>" : "<badge value='none'/>";
                var bn = Activator.CreateInstance(_tBadgeNotif, new object[] { Xml(xml) });
                _badgeUpdater.GetType().GetMethod("Update").Invoke(_badgeUpdater, new object[] { bn });
            }
            catch { }
        }

        // ══ THE TASKBAR ICON BADGE — the one piece of this file that works EVERYWHERE ═══════════
        // ⚠ DELIBERATELY *NOT* WinRT. ITaskbarList3::SetOverlayIcon is Windows 7+, needs no AUMID, no
        //   shortcut and no package identity — so it works on the RT 8.1 device, on Windows 7, and on
        //   Windows 11 alike, and it is the only badge a user without an installed shortcut will ever see.
        //   The WinRT badge above paints on the Start tile; this paints on the taskbar button.
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ⚠ VTABLE ORDER IS THE CONTRACT. Every inherited method must be declared, in order, even the
            //   ones we never call — omitting one silently shifts every later slot and calls the wrong
            //   function. ITaskbarList (5) then ITaskbarList2 (1) then ITaskbarList3.
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, int tbpFlags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList { }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

        /// <summary>BATCH-TA-28/T2 — the shell posts this to every top-level window when the taskbar BUTTON
        /// is created. ⚠ SetOverlayIcon BEFORE it arrives is a SILENT NO-OP, and it fires AGAIN after an
        /// explorer.exe restart — so a badge applied only on unread-change vanishes when explorer restarts
        /// and never comes back. MainForm.WndProc watches for it and calls <see cref="ReapplyTaskbarBadge"/>.</summary>
        public static readonly int WM_TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

        private static ITaskbarList3 _taskbar;
        private static bool _taskbarTried, _taskbarReady;
        private static IntPtr _lastOverlay = IntPtr.Zero;
        private static int _lastBadge = -1;

        /// <summary>The ACCENT CHANGED, so the badge must be redrawn even though the COUNT did not.
        /// SetTaskbarBadge short-circuits on an unchanged count — which is right, it runs on every unread
        /// change — so a colour-only change needs this to defeat that guard. No-op before the taskbar
        /// button exists; the TaskbarButtonCreated path paints it then.</summary>
        public static void RefreshBadgeColour(Form form)
        {
            if (!_taskbarReady) return;
            int n = _lastBadge;
            if (n <= 0) return;
            _lastBadge = -1;
            SetTaskbarBadge(form, n);
        }

        /// <summary>TA-28/T2 — the taskbar button now exists (first creation, or an explorer restart).
        /// Re-applies the current count, because everything sent before this was dropped on the floor.</summary>
        public static void ReapplyTaskbarBadge(Form form)
        {
            _taskbarReady = true;
            int n = _lastBadge;
            _lastBadge = -1;              // defeat the no-change short-circuit; this is a forced repaint
            _lastOverlay = IntPtr.Zero;   // explorer took our old icon down with it; do not destroy a stale handle
            if (n > 0) SetTaskbarBadge(form, n);
            Logger.Diag("[SHELL] taskbar button created — badge re-applied (" + Math.Max(0, n) + ")");
        }

        /// <summary>Paints an unread count onto the taskbar button. 0 removes it.
        /// Cheap-guarded: repainting the same number every time the unread total is recomputed would
        /// rebuild a GDI icon on every incoming message, and this is called from that path.</summary>
        public static void SetTaskbarBadge(Form form, int count)
        {
            if (form == null || !form.IsHandleCreated || form.IsDisposed) return;
            if (count == _lastBadge) return;
            _lastBadge = count;
            try
            {
                if (!_taskbarTried)
                {
                    _taskbarTried = true;
                    try { _taskbar = (ITaskbarList3)new CTaskbarList(); _taskbar.HrInit(); }
                    catch { _taskbar = null; Logger.Diag("[SHELL] no ITaskbarList3 — taskbar badge unavailable"); }
                }
                if (_taskbar == null) return;

                // TA-28/T2 — before the taskbar button exists the call is a silent no-op, so remember the
                // number and let ReapplyTaskbarBadge paint it when the shell says the button is ready.
                if (!_taskbarReady) return;

                IntPtr icon = count > 0 ? BuildBadgeIcon(count) : IntPtr.Zero;
                // TA-28/T2 — the description is what a SCREEN READER announces, so it is a sentence, not a
                // number: "3 unread conversations" tells you what the overlay means; "3" does not.
                string say = count <= 0 ? null
                           : count == 1 ? "1 unread conversation"
                           : count + " unread conversations";
                _taskbar.SetOverlayIcon(form.Handle, icon, say);
                // ⚠ The shell COPIES the icon, so ours must be destroyed or every recount leaks a GDI
                //   handle — on a long session that is thousands.
                if (_lastOverlay != IntPtr.Zero) DestroyIcon(_lastOverlay);
                _lastOverlay = icon;
            }
            catch (Exception ex) { Logger.Diag("[SHELL] taskbar badge failed: " + ex.Message); }
        }

        /// <summary>BATCH-TA-28/T3 — a filled circle with the count.
        ///
        /// ⚠ SIZED FROM SM_CXSMICON, NOT A HARDCODED 16. The overlay is a small icon, and the small-icon
        ///   metric is what actually scales with DPI — 16 is only correct at 100%, and at 200% a 16px icon
        ///   is upscaled by the shell into a blurry smear. Asking the system costs one call.
        /// ⚠ CLAMPED TO A SINGLE DIGIT then "9+". A taskbar overlay is roughly 16-32px across; three
        ///   characters in that space are unreadable at any DPI, so past 9 the exact number is not
        ///   information the badge can carry. The tooltip and the tray text still say the real total.
        /// ⚠ ACCENT-COLOURED, with a WHITE RIM AND WHITE GLYPH — and the rim is what makes that safe.
        ///   The taskbar's own light/dark theme is INDEPENDENT of the app theme, and an accent can be any
        ///   colour the user picked, including one close to their taskbar. The white ring separates the
        ///   disc from whatever sits behind it, so legibility does not depend on the accent being lucky.
        ///   ⚠ If the accent is very pale, the white glyph inside it can still get thin — the rim keeps
        ///     the badge VISIBLE, it cannot make a pale fill high-contrast. Worth an eye on a light accent.</summary>
        private static IntPtr BuildBadgeIcon(int count)
        {
            string text = count > 9 ? "9+" : count.ToString();
            int side = 16;
            try { side = Math.Max(16, Math.Min(GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON))); }
            catch { }
            Color fill;
            try { fill = ThemeHelper.GetWindowsAccentColor(); }
            catch { fill = Color.FromArgb(0, 120, 212); }
            using (var bmp = new Bitmap(side, side))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    float rim = Math.Max(1f, side / 16f);
                    using (var b = new SolidBrush(fill))
                        g.FillEllipse(b, 0, 0, side - 1, side - 1);
                    using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), rim))
                        g.DrawEllipse(p, rim / 2f, rim / 2f, side - 1 - rim, side - 1 - rim);
                    float size = (text.Length == 2 ? 0.58f : 0.68f) * side;
                    using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, f, Brushes.White, new RectangleF(0, 0, side, side), sf);
                }
                return bmp.GetHicon();
            }
        }
    }
}
