using System;

namespace TelegArm.Core
{
    /// <summary>What the proxy indicator may claim. ⚠ WORDING DISCIPLINE: WTelegramClient exposes NO
    /// "am I proxied / did the proxy handshake succeed" flag — MTProxyUrl is only what we set
    /// (TA-15/X4). So <see cref="Connected"/> means exactly: WE AUTHORISED WHILE A PROXY WAS CONFIGURED.
    /// Nothing here may imply the library confirmed anything, because it cannot.</summary>
    public enum ProxyConnectState
    {
        /// <summary>No proxy configured, or proxying switched off — we are connecting directly.</summary>
        Off,
        /// <summary>A proxy is configured and a connect attempt is in flight.</summary>
        Connecting,
        /// <summary>Authorised while a proxy was configured.</summary>
        Connected,
        /// <summary>Attempts have been failing longer than <see cref="FailAfterSeconds"/>.
        /// ⚠ DISPLAY ONLY — retries continue underneath and this can still flip to Connected.</summary>
        Failed
    }

    /// <summary>
    /// BATCH-TA-16b/B1+B2 — the one place that decides what the proxy indicator shows. Both connect paths
    /// report into it (MainForm's resilient resume loop, and LoginForm's interactive login), and the pill
    /// reads it, so the two can never disagree.
    ///
    /// B2 — THE FAILURE THRESHOLD IS WALL-CLOCK, NOT ATTEMPT COUNT. Attempt count is meaningless here:
    /// TA-12 established that WTC retries a dead socket on a fixed 5 s reactor timer and only surfaces the
    /// error every MaxAutoReconnects'th time (we set 1000, and it is a MODULO, not a ceiling), while a
    /// FLOOD_WAIT under FloodRetryThreshold (default 60) is absorbed as a SILENT Task.Delay inside the
    /// awaited call. So one visible "attempt" can hide a minute of invisible ones, and ten attempts can
    /// pass in a second. Elapsed time is the only honest measure of "this isn't working".
    ///
    /// ⚠ FAILED IS A DISPLAY STATE ONLY. Nothing here stops, cancels or throttles a retry — the connect
    /// loop is untouched and keeps going. If a late attempt authorises, this flips straight to Connected.
    /// That matters because the user must never be trapped: the pill stays clickable in every state so a
    /// failing proxy can always be changed or switched off.
    /// </summary>
    public static class ProxyStatus
    {
        /// <summary>How long attempts may keep failing before the indicator says so. Long enough to cover
        /// a slow MTProto handshake over a distant proxy plus one absorbed FLOOD_WAIT-ish stall, short
        /// enough that a dead proxy does not look like a hang forever.</summary>
        public static int FailAfterSeconds = 45;

        private static readonly object _gate = new object();
        private static ProxyConnectState _state = ProxyConnectState.Off;
        private static DateTime _firstAttemptUtc = DateTime.MinValue;
        private static string _detail;
        private static bool _viaProxy;

        /// <summary>True when a proxy was configured at the last transition. The pill words itself from
        /// this: the SAME Connecting/Failed states mean different things with and without a proxy, and a
        /// user in a blocked country needs the no-proxy wording to point at the fix.</summary>
        public static bool ViaProxy { get { lock (_gate) return _viaProxy; } }

        /// <summary>Raised whenever the state or detail changes. Subscribers are UI controls, so they must
        /// marshal themselves — this can be raised from a background continuation.</summary>
        public static event Action Changed;

        public static ProxyConnectState State { get { lock (_gate) return _state; } }

        /// <summary>Secret-free "host:port" of the proxy in use, or null. Never the link.</summary>
        public static string Detail { get { lock (_gate) return _detail; } }

        /// <summary>Call when the configured proxy changes (or at startup) — restarts the wall clock so a
        /// newly-chosen proxy gets a full grace period instead of inheriting the previous one's failure.</summary>
        public static void Reset()
        {
            lock (_gate)
            {
                string url = null;
                try { url = AppSettings.Instance.ActiveProxyUrl; } catch { }
                _viaProxy = url != null;
                _detail = _viaProxy ? ProxyUrl.SafeForLog(url) : null;
                _firstAttemptUtc = DateTime.MinValue;   // a newly-chosen proxy gets a full grace period
                // ⚠ The STATE is deliberately NOT forced to Off here. Turning a proxy off does not mean the
                // app is connected — the connect loop owns that, and clobbering it would make the pill lie.
            }
            Raise();
        }

        /// <summary>A connect attempt has begun. Starts the wall clock on the first one.</summary>
        public static void NoteAttempt()
        {
            bool changed = false;
            lock (_gate)
            {
                // ⚠ NO EARLY-OUT WHEN THERE IS NO PROXY. This used to `return` when the state was Off,
                // which meant a user in a country where Telegram is BLOCKED — i.e. someone with no proxy
                // yet, the exact person who needs one — saw nothing at all while the app failed to
                // connect. The indicator reports CONNECTION state; the proxy is only a modifier on the
                // wording. Failed stays sticky until a success clears it, so a retry does not flicker.
                if (_firstAttemptUtc == DateTime.MinValue) _firstAttemptUtc = DateTime.UtcNow;
                if (_state != ProxyConnectState.Failed && _state != ProxyConnectState.Connecting)
                { _state = ProxyConnectState.Connecting; changed = true; }
            }
            if (changed) Raise();
        }

        /// <summary>We authorised. If a proxy is configured this is the only evidence we will ever have
        /// that it works, so it clears a prior Failed outright.</summary>
        public static void NoteAuthorized()
        {
            bool changed;
            lock (_gate)
            {
                string url = null;
                try { url = AppSettings.Instance.ActiveProxyUrl; } catch { }
                _viaProxy = url != null;
                // Connected DIRECTLY is the ordinary healthy case and needs no chrome, so it maps to Off
                // (MainForm hides the pill there). Connected VIA A PROXY is worth stating.
                var next = _viaProxy ? ProxyConnectState.Connected : ProxyConnectState.Off;
                changed = next != _state;
                _state = next;
                _detail = _viaProxy ? ProxyUrl.SafeForLog(url) : null;
                _firstAttemptUtc = DateTime.MinValue;                // a success resets the budget
            }
            if (changed) Raise();
        }

        /// <summary>An attempt failed. Flips to Failed only once the WALL CLOCK is past the threshold —
        /// and does not touch the retry loop, which keeps running.</summary>
        public static void NoteAttemptFailed()
        {
            bool changed = false;
            lock (_gate)
            {
                // Also no early-out (see NoteAttempt): a DIRECT connection that keeps failing is exactly
                // the state that should say so and offer a proxy.
                if (_firstAttemptUtc == DateTime.MinValue) _firstAttemptUtc = DateTime.UtcNow;
                if (_state != ProxyConnectState.Failed
                    && (DateTime.UtcNow - _firstAttemptUtc).TotalSeconds >= FailAfterSeconds)
                { _state = ProxyConnectState.Failed; changed = true; }
            }
            if (changed)
            {
                if (TelegArm.Helpers.Logger.Enabled)
                    TelegArm.Helpers.Logger.Diag("[PROXY] state=FAILED after " + FailAfterSeconds
                        + "s of failing attempts " + (ViaProxy ? "via " + (Detail ?? "(none)") : "on a DIRECT connection")
                        + " — retries CONTINUE; this is a display state only");
                Raise();
            }
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) { try { h(); } catch { } }
        }
    }
}
