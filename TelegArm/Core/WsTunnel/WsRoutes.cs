using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TelegArm.Core
{
    /// <summary>
    /// Step 6.3 — where each DC's WebSocket gateway is reached, and which routes recently failed.
    ///
    /// FACTS (tg-ws-proxy v1.11.2, proxy/utils.py ws_domains + utils/default_config.py): the gateway names are
    /// kws{dc}.web.telegram.org, media connections try kws{dc}-1.web.telegram.org first, DC 203 is served as DC 2, and
    /// the shipped pins are DC2 and DC4 → 149.154.167.220. Other DCs have NO default pin here: their gateway addresses
    /// are UNVERIFIED, so an account on DC1/3/5 connects directly unless the user pins an address (or turns on strict).
    /// The pin is the TCP target; TLS still names the kws host, so a wrong pin fails the certificate check, never
    /// silently connects somewhere else.
    /// </summary>
    public static class WsRoutes
    {
        public const string Path = "/apiws";
        public const string DefaultPins = "2=149.154.167.220;4=149.154.167.220";
        /// <summary>How long a failure the GATEWAY gave (an HTTP status, a TLS refusal, a hang-up without data) keeps a
        /// route on direct TCP before the gateway is tried again.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);
        /// <summary>The same for a timeout or a socket error, which may be our own network blinking rather than the
        /// gateway: remembered briefly, so a burst of connections does not pay the timeout each, without parking a
        /// censored user on direct TCP for a minute after a blip (step 6.3 review).</summary>
        public static readonly TimeSpan RetryAfterNetwork = TimeSpan.FromSeconds(20);

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Failure> Failures = new Dictionary<long, Failure>();

        private struct Failure { public DateTime Until; public string Reason; }

        /// <summary>The DC used for the gateway name and the pin: 203 is DC 2's.</summary>
        public static int GatewayDc(int dc) { return dc == 203 ? 2 : dc; }

        /// <summary>The kws host names to try, in order.</summary>
        public static string[] Hosts(int dc, bool media)
        {
            dc = GatewayDc(dc);
            string main = "kws" + dc.ToString(CultureInfo.InvariantCulture) + ".web.telegram.org";
            return media ? new[] { "kws" + dc.ToString(CultureInfo.InvariantCulture) + "-1.web.telegram.org", main } : new[] { main };
        }

        /// <summary>Parses "2=149.154.167.220;4=…" (also ':' and ',' / newlines). Bad entries are skipped and reported in
        /// <paramref name="errors"/>; IPv4 only; DCs 1..1000. Empty or null input means the defaults.</summary>
        public static Dictionary<int, string> ParsePins(string text, out string errors)
        {
            var map = new Dictionary<int, string>();
            var err = new StringBuilder();
            if (string.IsNullOrWhiteSpace(text)) text = DefaultPins;
            foreach (var raw in text.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string e = raw.Trim();
                if (e.Length == 0) continue;
                int sep = e.IndexOfAny(new[] { '=', ':' });
                int dc;
                IPAddress ip;
                if (sep <= 0 || !int.TryParse(e.Substring(0, sep).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out dc)
                    || dc < 1 || dc > 1000
                    || !IPAddress.TryParse(e.Substring(sep + 1).Trim(), out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    if (err.Length > 0) err.Append("; ");
                    err.Append("\"").Append(e).Append("\"");
                    continue;
                }
                map[dc] = ip.ToString();
            }
            errors = err.Length == 0 ? null : "Not understood: " + err;
            return map;
        }

        /// <summary>Canonical text of a pin map ("2=…;4=…", sorted).</summary>
        public static string FormatPins(Dictionary<int, string> map)
        {
            var keys = new List<int>(map.Keys);
            keys.Sort();
            var sb = new StringBuilder();
            foreach (int k in keys) { if (sb.Length > 0) sb.Append(';'); sb.Append(k.ToString(CultureInfo.InvariantCulture)).Append('=').Append(map[k]); }
            return sb.ToString();
        }

        private static long Key(int dc, bool media) { return (long)GatewayDc(dc) << 1 | (media ? 1L : 0L); }

        /// <summary>Why this route is on direct TCP right now (a recent failure), or null.</summary>
        public static string Blocked(int dc, bool media)
        {
            lock (Gate)
            {
                Failure f;
                if (!Failures.TryGetValue(Key(dc, media), out f)) return null;
                if (DateTime.UtcNow < f.Until) return f.Reason;
                Failures.Remove(Key(dc, media));
                return null;
            }
        }

        /// <summary>A redirect is the gateway's answer for a DC it does not serve: remembered for the rest of the run.
        /// Anything else (timeout, reset, a 5xx) for <see cref="RetryAfter"/>.</summary>
        public static void Fail(int dc, bool media, Exception ex)
        {
            var hs = ex as WsHandshakeException;
            bool permanent = hs != null && hs.IsRedirect;
            bool network = ex is TimeoutException || ex is SocketException;
            string reason = hs != null && hs.Status != 0 ? hs.Status.ToString(CultureInfo.InvariantCulture)
                          : ex is TimeoutException ? "timeout" : ex == null ? "failed" : ex.GetType().Name;
            Remember(dc, media, reason, permanent ? DateTime.MaxValue : DateTime.UtcNow + (network ? RetryAfterNetwork : RetryAfter));
        }

        /// <summary>A failure found after the upgrade (e.g. the gateway hung up without sending anything).</summary>
        public static void Fail(int dc, bool media, string reason)
        {
            Remember(dc, media, reason, DateTime.UtcNow + RetryAfter);
        }

        private static void Remember(int dc, bool media, string reason, DateTime until)
        {
            lock (Gate) Failures[Key(dc, media)] = new Failure { Until = until, Reason = reason };
        }

        public static void Succeeded(int dc, bool media) { lock (Gate) Failures.Remove(Key(dc, media)); }

        /// <summary>Forget every failure — after the user changes the tunnel's settings.</summary>
        public static void Reset() { lock (Gate) Failures.Clear(); }
    }
}
