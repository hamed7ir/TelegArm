using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TelegArm.Core
{
    /// <summary>The tunnel's settings for one connection (read when the connection starts).</summary>
    public sealed class WsTunnelOptions
    {
        /// <summary>Never connect directly: a DC without a pin, or a gateway that fails, fails the connection.</summary>
        public bool Strict;
        /// <summary>D6 opt-in: accept a certificate chain error for *.web.telegram.org (never a name mismatch).</summary>
        public bool AcceptChainErrors;
        /// <summary>DC → IPv4 of its gateway.</summary>
        public Dictionary<int, string> Pins;
        /// <summary>False = these connections stay out of <see cref="WsTunnelStats"/> (the tunnel TEST: its routes must
        /// not rewrite what the live connection's pill says); they go to <see cref="OnRoute"/> only.</summary>
        public bool RecordStats = true;
        /// <summary>Each connection's route, as it is decided (any thread).</summary>
        public Action<WsTunnelStats.Route> OnRoute;

        public static WsTunnelOptions FromSettings()
        {
            var s = AppSettings.Instance;
            string errors;
            return new WsTunnelOptions
            {
                Strict = s.WsTunnelStrict,
                AcceptChainErrors = s.WsTunnelAcceptChainErrors,
                Pins = WsRoutes.ParsePins(s.WsTunnelDcIps, out errors),
            };
        }
    }

    /// <summary>
    /// Step 6.3 — the built-in WebSocket tunnel (decision D5: built, OFF by default): WTelegramClient's connections go
    /// to Telegram through Telegram Web's WebSocket gateway, the route tg-ws-proxy (MIT, © Flowseal) takes — in-process,
    /// with no proxy secret and no re-encryption. The design follows tg-ws-proxy; no code is copied from it.
    ///
    /// WHY A LOOPBACK RELAY: WTC's only transport hook is <c>Client.TcpHandler</c>, a factory that must return a
    /// connected <see cref="TcpClient"/>, and WTC then talks to <c>TcpClient.GetStream()</c> — non-virtual, so a fake
    /// stream cannot be slipped in (WTC src/Client.cs:37-38, :898-899, :967-968). So <see cref="ConnectAsync(string,int)"/>
    /// pairs two loopback sockets, returns one to WTC and relays the other: the first 64 bytes (WTC's obfuscated2 init,
    /// written straight after the factory returns, src/Client.cs:980) say which DC and whether media; the init goes to
    /// the gateway as the first WebSocket message, then one message per MTProto packet up, a plain byte stream down.
    ///
    /// ROUTE PER CONNECTION:
    ///   · the DC has a pinned gateway address and no recent failure → WebSocket (TLS to the pin, named as the kws host);
    ///   · otherwise, or when the gateway fails → direct TCP to the address WTC asked for, replaying the init — unless
    ///     strict, when the loopback is simply closed: WTC treats it as a dropped connection and retries in 5 s
    ///     (src/Client.cs:389-399), so a strict failure cannot spin.
    ///   The DC is only known once WTC has written its init, i.e. AFTER this factory returned, so failures cannot be
    ///   reported by throwing from the factory (the plan's first idea); closing the loopback is the honest equivalent.
    /// Every outcome lands in <see cref="WsTunnelStats"/>; [WSTUN] log lines carry DC, route, ms, bytes and close reason —
    /// never a secret (there is none) and never anything but Telegram's own host names.
    /// </summary>
    public static class WsTunnel
    {
        public const int HandshakeTimeoutMs = 10000;
        private const int InitTimeoutMs = 10000;
        private const int BufferSize = 64 * 1024;

        private static int _active;

        /// <summary>Connections being relayed right now.</summary>
        public static int Active { get { return Volatile.Read(ref _active); } }

        /// <summary>WTC's TcpFactory with the current settings.</summary>
        public static Task<TcpClient> ConnectAsync(string host, int port)
        {
            return ConnectAsync(host, port, WsTunnelOptions.FromSettings());
        }

        /// <summary>WTC's TcpFactory with explicit options (the tunnel test forces strict).</summary>
        public static async Task<TcpClient> ConnectAsync(string host, int port, WsTunnelOptions options)
        {
            TcpClient app = null, relay = null;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            Task<TcpClient> accept = null;
            try
            {
                listener.Start(1);
                accept = listener.AcceptTcpClientAsync();
                app = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                await app.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
                if (await Task.WhenAny(accept, Task.Delay(5000)).ConfigureAwait(false) != accept)
                    throw new SocketException((int)SocketError.TimedOut);
                relay = await accept.ConfigureAwait(false);
                relay.NoDelay = true;
                // The listener is open to every local process for these few milliseconds: make sure the socket we accepted
                // is the one we connected, not someone else's.
                if (!relay.Client.RemoteEndPoint.Equals(app.Client.LocalEndPoint))
                    throw new SocketException((int)SocketError.AccessDenied);
            }
            catch (Exception ex)
            {
                try { if (relay != null) relay.Close(); } catch { }
                try { if (app != null) app.Close(); } catch { }
                if (accept != null) Observe(accept);
                TelegArm.Helpers.Logger.Diag("[WSTUN] loopback pairing failed: " + ex.GetType().Name + " " + ex.Message);
                if (ex is SocketException) throw;
                throw new SocketException((int)SocketError.SocketError);
            }
            finally { try { listener.Stop(); } catch { } }

            var r = relay;
            Observe(Task.Run(() => RelayAsync(r, host, port, options)));
            return app;
        }

        /// <summary>Step 6.5's "Probe DCs": TLS + upgrade only (no MTProto, no auth key) to every pinned DC, main and
        /// media, one after another. Each line: "DC2: 312 ms (kws2.web.telegram.org)" or "DC4 media: HTTP 302 (…)".</summary>
        public static async Task<List<string>> ProbeAsync(WsTunnelOptions o)
        {
            var lines = new List<string>();
            var dcs = new List<int>(o.Pins.Keys);
            dcs.Sort();
            foreach (int dc in dcs)
                foreach (bool media in new[] { false, true })
                {
                    string ip = o.Pins[dc];
                    string label = "DC" + dc + (media ? " media" : "");
                    string result = null;
                    var sw = Stopwatch.StartNew();
                    foreach (var h in WsRoutes.Hosts(dc, media))
                    {
                        try
                        {
                            using (await WsClient.ConnectAsync(ip, h, WsRoutes.Path, o.AcceptChainErrors, HandshakeTimeoutMs).ConfigureAwait(false)) { }
                            result = sw.ElapsedMilliseconds + " ms (" + h + " via " + ip + ")";
                            break;
                        }
                        catch (Exception ex) { result = Describe(ex) + " (" + h + " via " + ip + ")"; if (SameIpIsDead(ex)) break; }
                    }
                    lines.Add(label + ": " + result);
                    TelegArm.Helpers.Logger.Diag("[WSTUN] probe " + label + ": " + result);
                }
            return lines;
        }

        private sealed class Counter { public long Up, Down; }

        private static async Task RelayAsync(TcpClient local, string host, int port, WsTunnelOptions o)
        {
            Interlocked.Increment(ref _active);
            var sw = Stopwatch.StartNew();
            var count = new Counter();
            CtrKeystream ks = null;
            WsClient ws = null;
            TcpClient direct = null;
            int dc = 0;
            bool media = false;
            string route = "none", end = null;
            try
            {
                var ls = local.GetStream();
                var init = new byte[Obfuscated2.InitLength];
                if (!await ReadExactAsync(ls, init, InitTimeoutMs).ConfigureAwait(false))
                {
                    TelegArm.Helpers.Logger.Diag("[WSTUN] no init from WTC within " + InitTimeoutMs + " ms; closed");
                    return;
                }
                ks = Obfuscated2.UplinkKeystream(init);
                int dcIdx;
                uint tag;
                bool known = Obfuscated2.TryParseInit(init, ks, out dcIdx, out tag);
                dc = Math.Abs(dcIdx);
                media = dcIdx < 0;

                string ip = null, why = null, wsHost = null;
                if (!known) why = "unknown transport";
                else if (dc >= 10000) why = "test DC";
                else if (o.Pins == null || !o.Pins.TryGetValue(WsRoutes.GatewayDc(dc), out ip)) why = "no address";
                // Strict has no other route, so a remembered failure would only add outage: it always tries the gateway.
                else if (!o.Strict) why = WsRoutes.Blocked(dc, media);

                if (why == null)
                {
                    Exception last = null;
                    foreach (var h in WsRoutes.Hosts(dc, media))
                    {
                        try
                        {
                            ws = await WsClient.ConnectAsync(ip, h, WsRoutes.Path, o.AcceptChainErrors, HandshakeTimeoutMs).ConfigureAwait(false);
                            wsHost = h;
                            break;
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                            TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " " + h + " via " + ip
                                                         + " failed after " + sw.ElapsedMilliseconds + " ms: " + Describe(ex));
                            if (SameIpIsDead(ex)) break;   // the other name is on the same address: don't wait twice
                        }
                    }
                    if (ws == null)
                    {
                        WsRoutes.Fail(dc, media, last);
                        why = WsRoutes.Blocked(dc, media) ?? "failed";
                    }
                    else WsRoutes.Succeeded(dc, media);
                }

                if (ws != null)
                {
                    route = "WebSocket " + wsHost;
                    Note(o, dc, media, WsRouteKind.WebSocket, wsHost, null, sw.ElapsedMilliseconds);
                    TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " -> " + wsHost + " via " + ip
                                                 + " in " + sw.ElapsedMilliseconds + " ms");
                    await ws.SendBinaryAsync(init, 0, init.Length).ConfigureAwait(false);
                    var splitter = new PacketSplitter(ks, Obfuscated2.InitLength, tag);
                    var up = PumpUpAsync(ls, ws, splitter, count);
                    var down = PumpDownAsync(ws, ls, count);
                    var first = await Task.WhenAny(up, down).ConfigureAwait(false);
                    Observe(up); Observe(down);
                    end = EndReason(first, first == up ? "app closed" : "gateway closed");
                    // A gateway that upgrades and then hangs up before sending a byte is not a working route (a wrong
                    // pin, a DC it will not carry). Counting the 101 as success would loop WTC's reconnect on it forever.
                    if (first == down && count.Down == 0)
                    {
                        WsRoutes.Fail(dc, media, "closed without data");
                        Note(o, dc, media, WsRouteKind.Failed, wsHost, "closed without data", sw.ElapsedMilliseconds);
                    }
                }
                else if (o.Strict)
                {
                    route = "refused (" + why + ")";
                    Note(o, dc, media, WsRouteKind.Failed, null, why, sw.ElapsedMilliseconds);
                    TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " strict: no gateway (" + why + "), not connecting directly");
                }
                else
                {
                    direct = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                    var conn = direct.ConnectAsync(host, port);
                    if (await Task.WhenAny(conn, Task.Delay(HandshakeTimeoutMs)).ConfigureAwait(false) != conn)
                    {
                        Observe(conn);
                        throw new TimeoutException("direct connect timed out");
                    }
                    await conn.ConfigureAwait(false);
                    route = "direct " + host + ":" + port + " (" + why + ")";
                    Note(o, dc, media, WsRouteKind.Direct, host + ":" + port, why, sw.ElapsedMilliseconds);
                    TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " -> direct " + host + ":" + port
                                                 + " (" + why + ") in " + sw.ElapsedMilliseconds + " ms");
                    var ds = direct.GetStream();
                    await ds.WriteAsync(init, 0, init.Length).ConfigureAwait(false);
                    var up = CopyAsync(ls, ds, count, true);
                    var down = CopyAsync(ds, ls, count, false);
                    var first = await Task.WhenAny(up, down).ConfigureAwait(false);
                    Observe(up); Observe(down);
                    end = EndReason(first, first == up ? "app closed" : "server closed");
                }
            }
            catch (Exception ex)
            {
                TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " relay error (" + route + "): " + Describe(ex));
            }
            finally
            {
                string reason = ws != null ? ws.CloseReason : null;
                try { if (ws != null) ws.Dispose(); } catch { }
                try { if (direct != null) direct.Close(); } catch { }
                try { local.Close(); } catch { }
                try { if (ks != null) ks.Dispose(); } catch { }
                Interlocked.Decrement(ref _active);
                if (dc != 0)
                {
                    if (o.RecordStats) WsTunnelStats.Closed(dc, media, count.Up, count.Down);
                    TelegArm.Helpers.Logger.Diag("[WSTUN] DC" + dc + (media ? " media" : "") + " closed after "
                                                 + (sw.ElapsedMilliseconds / 1000) + " s: " + route + ", up " + count.Up + " B, down " + count.Down + " B"
                                                 + (end != null ? ", " + end : "")
                                                 + (reason != null ? ", server close " + reason : ""));
                }
            }
        }

        private static async Task PumpUpAsync(Stream from, WsClient ws, PacketSplitter sp, Counter c)
        {
            var buf = new byte[BufferSize];
            int n;
            while ((n = await from.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                sp.Append(buf, 0, n);
                int i, cnt;
                while (sp.Next(out i, out cnt))
                {
                    await ws.SendBinaryAsync(sp.Data, i, cnt).ConfigureAwait(false);
                    c.Up += cnt;
                }
                sp.Compact();
            }
        }

        private static async Task PumpDownAsync(WsClient ws, Stream to, Counter c)
        {
            var buf = new byte[BufferSize];
            int n;
            while ((n = await ws.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buf, 0, n).ConfigureAwait(false);
                c.Down += n;
            }
        }

        private static async Task CopyAsync(Stream from, Stream to, Counter c, bool upward)
        {
            var buf = new byte[BufferSize];
            int n;
            while ((n = await from.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buf, 0, n).ConfigureAwait(false);
                if (upward) c.Up += n; else c.Down += n;
            }
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            using (cts.Token.Register(() => { try { s.Dispose(); } catch { } }))
            {
                int got = 0;
                try
                {
                    while (got < buf.Length)
                    {
                        int n = await s.ReadAsync(buf, got, buf.Length - got).ConfigureAwait(false);
                        if (n <= 0) return false;
                        got += n;
                    }
                    return true;
                }
                catch (Exception) when (cts.IsCancellationRequested) { return false; }
            }
        }

        private static void Note(WsTunnelOptions o, int dc, bool media, WsRouteKind kind, string host, string reason, long ms)
        {
            if (o.RecordStats) WsTunnelStats.Record(dc, media, kind, host, reason, ms);
            var h = o.OnRoute;
            if (h != null)
                try { h(new WsTunnelStats.Route { Dc = dc, Media = media, Kind = kind, Host = host, Reason = reason, Ms = ms, When = DateTime.UtcNow }); }
                catch { }
        }

        /// <summary>Why a relay ended: the first pump's error (TLS reset, a bad frame…), or which side closed.</summary>
        private static string EndReason(Task first, string closedBy)
        {
            return first.IsFaulted && first.Exception != null ? "ended by " + Describe(first.Exception.GetBaseException()) : closedBy;
        }

        /// <summary>A timeout or a socket error is about the ADDRESS, which every host name of a route shares; only an answer
        /// (an HTTP status, a TLS failure for that name) is worth trying the next name for.</summary>
        private static bool SameIpIsDead(Exception ex) { return ex is TimeoutException || ex is SocketException; }

        /// <summary>Exception → one short line for the log (type + status/message, no stack).</summary>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return "?";
            var hs = ex as WsHandshakeException;
            if (hs != null) return "HTTP " + hs.Status + " (" + hs.Message + ")";
            var se = ex as SocketException;
            if (se != null) return "socket " + se.SocketErrorCode;
            var inner = ex.InnerException != null ? " <- " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "";
            return ex.GetType().Name + ": " + (ex.Message ?? "").Replace('\r', ' ').Replace('\n', ' ') + inner;
        }

        private static void Observe(Task t)
        {
            t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
