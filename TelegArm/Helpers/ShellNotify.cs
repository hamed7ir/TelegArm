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

        /// <summary>True when Action Center / tile delivery is actually possible. False on RT 8.1, on
        /// Windows 7, and on any copy whose AUMID has no Start-Menu shortcut.</summary>
        public static bool Available { get; private set; }

        /// <summary>Why <see cref="Available"/> is false, for the log and nothing else.</summary>
        public static string Reason { get; private set; }

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
                // The AUMID is set even when WinRT is unavailable: it also controls taskbar grouping, and
                // it is what a future shortcut would have to match.
                SetCurrentProcessExplicitAppUserModelID(Aumid);

                var api = WinRT("Windows.Foundation.Metadata.ApiInformation");
                if (api == null) { Fail("no WinRT ApiInformation (Windows 7 / RT 8.1) — Action Center and tile skipped"); return; }

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

                // Tiles are best-effort on top: a Win11 box has no tile surface at all, which is not a failure.
                try
                {
                    var mkTile = _tTileMgr == null ? null : _tTileMgr.GetMethod("CreateTileUpdaterForApplication",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (mkTile != null) _tileUpdater = mkTile.Invoke(null, new object[] { Aumid });
                    if (_tileUpdater != null)
                        _tTileUpd.GetMethod("EnableNotificationQueue").Invoke(_tileUpdater, new object[] { true });
                    var mkBadge = _tBadgeMgr == null ? null : _tBadgeMgr.GetMethod("CreateBadgeUpdaterForApplication",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (mkBadge != null) _badgeUpdater = mkBadge.Invoke(null, new object[] { Aumid });
                }
                catch { _tileUpdater = null; _badgeUpdater = null; }

                Available = true;
                Logger.Diag("[SHELL] Action Center ON aumid=" + Aumid + " setting=" + setting
                            + " tile=" + (_tileUpdater != null) + " badge=" + (_badgeUpdater != null));
            }
            catch (Exception ex) { Fail("init threw: " + ex.Message); }
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
            if (!Available || _tileUpdater == null || items == null) return;
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
            if (!Available || _tileUpdater == null) return;
            try { _tTileUpd.GetMethod("Clear").Invoke(_tileUpdater, null); } catch { }
        }

        /// <summary>The numeric badge on the START TILE (distinct from the taskbar badge below, which works
        /// everywhere). 0 clears it.</summary>
        public static void SetTileBadge(int count)
        {
            if (!Available || _badgeUpdater == null) return;
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

        private static ITaskbarList3 _taskbar;
        private static bool _taskbarTried;
        private static IntPtr _lastOverlay = IntPtr.Zero;
        private static int _lastBadge = -1;

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

                IntPtr icon = count > 0 ? BuildBadgeIcon(count) : IntPtr.Zero;
                _taskbar.SetOverlayIcon(form.Handle, icon,
                                        count > 0 ? count + " unread" : null);
                // ⚠ The shell COPIES the icon, so ours must be destroyed or every recount leaks a GDI
                //   handle — on a long session that is thousands.
                if (_lastOverlay != IntPtr.Zero) DestroyIcon(_lastOverlay);
                _lastOverlay = icon;
            }
            catch (Exception ex) { Logger.Diag("[SHELL] taskbar badge failed: " + ex.Message); }
        }

        /// <summary>A filled accent circle with the count, ">99" past three digits. 16x16 is the overlay
        /// size the shell asks for at 100%; it scales it for higher DPI itself.</summary>
        private static IntPtr BuildBadgeIcon(int count)
        {
            string text = count > 99 ? "99+" : count.ToString();
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(232, 62, 62)))
                        g.FillEllipse(b, 0, 0, 15, 15);
                    using (var p = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
                        g.DrawEllipse(p, 0.5f, 0.5f, 14.5f, 14.5f);
                    float size = text.Length >= 3 ? 6.5f : text.Length == 2 ? 8f : 9.5f;
                    using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, f, Brushes.White, new RectangleF(0, 0, 16, 16), sf);
                }
                return bmp.GetHicon();
            }
        }
    }
}
