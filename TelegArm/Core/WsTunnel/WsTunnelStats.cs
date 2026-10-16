using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TelegArm.Core
{
    /// <summary>How the last connection of a (DC, media) pair actually went.</summary>
    public enum WsRouteKind { WebSocket, Direct, Failed }

    /// <summary>
    /// Step 6.3 — what the tunnel actually did, per (DC, media) route, for the status pill and Settings. This is the
    /// ONLY source entitled to say "tunnelled": a connection that fell back to direct TCP is reported as direct, with
    /// the reason (decision for 6.5: never claim the tunnel when the last route was a fallback).
    /// </summary>
    public static class WsTunnelStats
    {
        public sealed class Route
        {
            public int Dc;
            public bool Media;
            public WsRouteKind Kind;
            public string Host;       // the kws name, or "ip:port" for direct
            public string Reason;     // why it went direct / failed; null when tunnelled
            public long Ms;           // connect time
            public long Up, Down;     // bytes of the last finished connection
            public DateTime When;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Route> Routes = new Dictionary<long, Route>();

        /// <summary>Raised from a relay thread whenever a route changes; UI subscribers marshal themselves.</summary>
        public static event Action Changed;

        public static void Record(int dc, bool media, WsRouteKind kind, string host, string reason, long ms)
        {
            lock (Gate)
                Routes[(long)dc << 1 | (media ? 1L : 0L)] = new Route
                {
                    Dc = dc, Media = media, Kind = kind, Host = host, Reason = reason, Ms = ms, When = DateTime.UtcNow
                };
            Raise();
        }

        public static void Closed(int dc, bool media, long up, long down)
        {
            lock (Gate)
            {
                Route r;
                if (Routes.TryGetValue((long)dc << 1 | (media ? 1L : 0L), out r)) { r.Up = up; r.Down = down; }
            }
        }

        public static List<Route> Snapshot()
        {
            lock (Gate)
            {
                var list = new List<Route>(Routes.Values);
                list.Sort((a, b) => a.Dc != b.Dc ? a.Dc.CompareTo(b.Dc) : a.Media.CompareTo(b.Media));
                return list;
            }
        }

        /// <summary>"DC2 → WebSocket · DC4 media → direct (302)", or null before the first connection.</summary>
        public static string Summary() { return Summary(Snapshot()); }

        public static string Summary(IEnumerable<Route> routes)
        {
            var sb = new StringBuilder();
            foreach (var r in routes)
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append("DC").Append(r.Dc.ToString(CultureInfo.InvariantCulture)).Append(r.Media ? " media" : "").Append(" → ");
                sb.Append(r.Kind == WsRouteKind.WebSocket ? "WebSocket" : r.Kind == WsRouteKind.Direct ? "direct" : "failed");
                if (!string.IsNullOrEmpty(r.Reason)) sb.Append(" (").Append(r.Reason).Append(')');
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>True when the last main (non-media) connection to <paramref name="dc"/> went through the gateway.
        /// <paramref name="dc"/> = the ACTIVE account's DC (ProxyStatus.MainDc): background accounts on other DCs
        /// connect through the same tunnel and must not decide what the active one's pill says (step 6.3 review).
        /// 0 = unknown: the newest main route, whichever DC.</summary>
        public static bool Tunnelled(int dc)
        {
            lock (Gate)
            {
                Route r;
                if (dc > 0) return Routes.TryGetValue((long)dc << 1, out r) && r.Kind == WsRouteKind.WebSocket;
                DateTime best = DateTime.MinValue; bool ws = false;
                foreach (var x in Routes.Values)
                    if (!x.Media && x.When > best) { best = x.When; ws = x.Kind == WsRouteKind.WebSocket; }
                return ws;
            }
        }

        public static void Reset() { lock (Gate) Routes.Clear(); Raise(); }

        private static void Raise()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); } catch { }
        }
    }
}
