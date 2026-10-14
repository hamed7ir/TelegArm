using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        /// <summary>ProxyStatus is static and can be raised from a background continuation — marshal.</summary>
        private void OnProxyStatusChangedForVisibility()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)RefreshProxyPill); } catch { /* handle gone */ }
        }

        /// <summary>BATCH-TA-16d/D3+D4 — decides whether the pill is on screen at all.
        ///
        /// D4 VISIBILITY: on MainForm it appears ONLY when a proxy is configured. Permanent chrome over the
        /// chat list would be clutter for the majority who never use one. (LoginForm is the opposite: there
        /// it is ALWAYS shown, because someone who cannot connect has no other way to reach the setting.)
        ///
        /// D3 COEXISTENCE — the pill DEFERS to the existing "Connecting…" overlay. MainForm already has one
        /// connection indicator, and two of them disagreeing is worse than either alone: the overlay would
        /// say "Waiting for network" while the pill still said "Proxy connected" from the previous session.
        /// So while the overlay is up it owns connection state entirely and the pill hides; once the overlay
        /// goes away, the pill reports the PROXY dimension only — which the overlay never covered.
        /// The two can therefore never contradict each other, because they are never both on screen.</summary>
        private bool _proxyPillShown;

        private void RefreshProxyPill()
        {
            if (_proxyPill == null) return;
            bool proxied = false;
            try { proxied = AppSettings.Instance.ActiveProxyUrl != null; } catch { }
            var st = ProxyStatus.State;
            bool overlayUp = _connectingPanel != null && !_connectingPanel.IsDisposed && _connectingPanel.Visible;

            // The pill is a CONNECTION indicator, not merely a proxy badge (revised after field feedback):
            //   · Connecting / Failed → ALWAYS show, proxy or not. A mid-session drop on a DIRECT
            //     connection previously produced no visible signal at all except a window-title change,
            //     so the user just saw a chat list that had quietly stopped updating.
            //   · Connected VIA A PROXY → show (worth stating).
            //   · Connected directly (state Off) → hide. That is the healthy default and permanent chrome
            //     over the chat list would be clutter for the majority who never use a proxy.
            bool show = (st == ProxyConnectState.Connecting || st == ProxyConnectState.Failed || proxied)
                        && !overlayUp;
            // ⚠ Compare with what we last asked for, NEVER with Control.Visible: that reads false whenever the window is
            // hidden (in the tray), so a Connecting -> Off change while hidden was skipped and the pill came back with
            // the window, saying "Connect via proxy" (found by the 2.7b rig shot, whose form is never shown).
            if (_proxyPillShown != show)
            {
                _proxyPillShown = show;
                _proxyPill.Visible = show;
                Bar.PerformLayout(); Bar.Invalidate();   // 2.7b: the title bar places its slot and shortens the title for it
            }
        }

        /// <summary>Opens the shared ProxyForm — the SAME form the login screen's pill opens (TA-15/X7:
        /// one form, two doors). Applying a change to the already-connected client and the warm pool is
        /// deliberately NOT done here; that is TA-16c.</summary>
        private async void OpenProxySettings()
        {
            bool changed = false;
            try
            {
                using (var dlg = new ProxyForm(_service)) { dlg.ShowDialog(this); changed = dlg.ConnectionSettingsChanged; }
            }
            catch (Exception ex) { Logger.Diag("[PROXY] settings form failed: " + ex.Message); }   // never echoes a link

            ProxyStatus.Reset();     // new proxy → fresh grace period for the failure clock
            RefreshProxyPill();
            if (changed) await ApplyProxyChangeAsync();
        }

        /// <summary>BATCH-TA-17 — make a proxy change take effect NOW, on the running app.
        ///
        /// Previously the setting only reached the NEXT client, so switching proxies (or switching to
        /// direct) left the app connected through the old one — a proxy the user had just watched fail
        /// stayed in use, which is the opposite of what the UI implied.
        ///
        /// ORDER MATTERS AND IS DELIBERATE:
        ///   1. WARM POOL FIRST, AWAITED. Warm clients were built with the OLD transport. Leaving them
        ///      would mean a later account switch silently moves the user between proxied and direct —
        ///      the "half-proxied state" this design has warned about since TA-15/X7. The AWAITED
        ///      DisposeWarmServiceAsync is used, never the sync DisposeWarmService: the sync one does not
        ///      wait for the socket abort or the session-handle release, and dropping a warm client whose
        ///      handle is still releasing is the documented account-loss race (ACCOUNT-RECOVERY-SAFETY
        ///      Bug 1). Tear them down BEFORE the active client reconnects so the two can't overlap.
        ///   2. Then the ACTIVE client, via the existing ForceReconnectAsync.
        ///   3. Then re-warm. WarmOthersAsync is idempotent and skips active/already-warm, and the
        ///      rebuilt clients pick up the new transport through ApplyProxyTo in CreateWarmClientAsync.
        ///
        /// ⚠ THIS IS THE DANGER ZONE (HANDOFF §5.10/§5.11). A proxy change is now a deliberate way to
        ///   enter the warm-teardown-then-rebuild window — the same window as Bug 3b. It is INSTRUMENTED,
        ///   not fixed: the [SESSPATH] probes already log every client-open with its path, so a
        ///   same-path double-open with no teardown between is visible in the log. Verify on the device
        ///   that no [RECOVERY] or [ACCT] DELETE line appears and both account dirs survive.</summary>
        private async System.Threading.Tasks.Task ApplyProxyChangeAsync()
        {
            string via = "(direct)";
            try { var u = AppSettings.Instance.ActiveProxyUrl; via = u == null ? "(direct)" : ProxyUrl.SafeForLog(u); } catch { }
            Logger.Diag("[PROXY] APPLY-LIVE start → " + via + "  warm=" + _warm.Count);

            ProxyStatus.NoteAttempt();   // the pill says "Connecting…" for the whole swap
            RefreshProxyPill();

            try
            {
                // 1 — warm pool, awaited, before anything reconnects.
                var warm = _warm.Values.ToList();
                _warm.Clear();
                foreach (var svc in warm)
                {
                    try { await svc.DisposeWarmServiceAsync(); }
                    catch (Exception ex) { Logger.Diag("[PROXY] APPLY-LIVE warm teardown failed: " + ex.Message); }
                }
                Logger.Diag("[PROXY] APPLY-LIVE warm pool torn down (" + warm.Count + ")");

                // 2 — the active client.
                if (_service != null) await _service.ApplyProxyChangeAsync();
                Logger.Diag("[PROXY] APPLY-LIVE active client reconnected → " + via);
            }
            catch (Exception ex)
            {
                Logger.Diag("[PROXY] APPLY-LIVE FAILED: " + ex.Message);   // never echoes a link
            }

            // 3 — re-warm with the new transport (fire-and-forget, as at startup).
            try { var _ = WarmOthersAsync(); } catch { }
            RefreshProxyPill();
        }

    }
}
