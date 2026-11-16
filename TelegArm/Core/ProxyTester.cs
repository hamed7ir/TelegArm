using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TL;

namespace TelegArm.Core
{
    /// <summary>Outcome of a REAL proxy test — an actual MTProto handshake, not a TCP poke.</summary>
    public sealed class ProxyTestResult
    {
        public bool Ok;
        public long Ms;
        public string Error;      // secret-free, user-facing
        /// <summary>BATCH-TA-16f/F3 — the RAW exception type + message, for the log only. Never shown to
        /// the user; its whole purpose is that a misclassification by Friendly() can be spotted after the
        /// fact instead of being invisible.</summary>
        public string RawError;
        /// <summary>Step 6.4 — tunnel tests only: the route the main connection actually took
        /// (<see cref="WsTunnelStats.Summary"/>), e.g. "DC2 → WebSocket".</summary>
        public string Route;
    }

    /// <summary>
    /// BATCH-TA-16e/E2 — tests whether a proxy ACTUALLY WORKS, by standing up a throwaway
    /// WTelegramClient against it and completing a real connection.
    ///
    /// WHY THIS EXISTS ALONGSIDE THE CHEAP PROBE. They measure different layers and neither replaces the
    /// other. The TCP probe answers "does something accept a socket here" — cheap, instant, and on this
    /// network it cannot even measure latency honestly (TA-16e/E1: a control connection to example.com
    /// also returned 0 ms). THIS answers "can Telegram actually be reached through it", which is the only
    /// question that matters, and it is the ONLY thing entitled to say the secret is right — a wrong
    /// secret fails inside WTC's obfuscated handshake with no distinct signal anywhere else.
    ///
    /// ⚠ R5 DOES NOT APPLY HERE, AND THAT IS DELIBERATE — DO NOT "FIX" THIS BY REUSING THE REAL SESSION.
    /// Rail R5 says one live client per session file, because two clients on one session trigger
    /// AUTH_KEY_DUPLICATED and Telegram revokes it. This test never opens an account session: it points
    /// session_pathname at a FRESH file under the OS temp folder, which is deleted afterwards. That is why
    /// running it while the app is connected is safe. Pointing it at AccountContext.SessionPath to "save
    /// an auth key" would be a session-revoking bug.
    ///
    /// ⚠ EVERY TEST NEGOTIATES A NEW AUTH KEY with the DC. That is real work for Telegram and for the
    /// proxy, so <see cref="TestAllAsync"/> runs STRICTLY SEQUENTIALLY, is capped, and results are cached
    /// by the caller. Never fan these out in parallel.
    /// </summary>
    public static class ProxyTester
    {
        /// <summary>Per-proxy budget. A working MTProxy handshake is seconds; beyond this it is not usable
        /// even if it would eventually answer.</summary>
        public const int TestTimeoutMs = 15000;

        /// <summary>Safety cap for "Test all", so a long pasted list can't turn into a handshake storm.</summary>
        public const int MaxBatch = 20;

        /// <summary>Runs one real connection through <paramref name="proxyUrl"/>. Never throws.</summary>
        public static Task<ProxyTestResult> TestAsync(string proxyUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl)) return Task.FromResult(new ProxyTestResult { Error = "No proxy link." });
            // step 6.6: a SOCKS5 link is a TcpHandler, an MTProxy link is WTC's own MTProxyUrl
            if (ProxyUrl.IsSocks(proxyUrl))
                return RunAsync(c => c.TcpHandler = (h, p) => Socks5.ConnectAsync(proxyUrl, h, p), ProxyUrl.DisplayLabel(proxyUrl), ct);
            return RunAsync(c => c.MTProxyUrl = proxyUrl, ProxyUrl.SafeForLog(proxyUrl), ct);
        }

        /// <summary>Step 6.4 — the same throwaway handshake through the built-in WebSocket tunnel, STRICT whatever the
        /// setting says: no direct fallback, so success can only mean the gateway carried it. <paramref name="options"/>
        /// null = the saved tunnel settings. Never throws.</summary>
        public static async Task<ProxyTestResult> TestTunnelAsync(WsTunnelOptions options, CancellationToken ct)
        {
            var o = options ?? WsTunnelOptions.FromSettings();
            // The test's routes are its OWN: kept out of WsTunnelStats, which says what the LIVE connection did (the pill).
            var routes = new System.Collections.Generic.List<WsTunnelStats.Route>();
            var strict = new WsTunnelOptions
            {
                Strict = true, AcceptChainErrors = o.AcceptChainErrors, Pins = o.Pins, RecordStats = false,
                OnRoute = r => { lock (routes) routes.Add(r); },
            };
            var res = await RunAsync(c => c.TcpHandler = (h, p) => WsTunnel.ConnectAsync(h, p, strict), "WebSocket tunnel", ct).ConfigureAwait(false);
            System.Collections.Generic.List<WsTunnelStats.Route> seen;
            lock (routes) seen = new System.Collections.Generic.List<WsTunnelStats.Route>(routes);
            res.Route = WsTunnelStats.Summary(seen);
            var main = seen.FindLast(r => !r.Media);
            if (res.Ok && (main == null || main.Kind != WsRouteKind.WebSocket))   // only the route log may say "tunnelled"
            {
                res.Ok = false;
                res.Error = "Connected, but not through the gateway: " + (res.Route ?? "no route recorded");
            }
            // a connection that FAILED (not a timeout or the user's Stop) is explained by the route it was refused
            else if (!res.Ok && res.RawError != null && res.Route != null) res.Error = "The gateway didn't carry the connection: " + res.Route;
            return res;
        }

        private static async Task<ProxyTestResult> RunAsync(Action<WTelegram.Client> transport, string label, CancellationToken ct)
        {
            var res = new ProxyTestResult();

            string dir = Path.Combine(Path.GetTempPath(), "telegarm_proxytest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            string session = Path.Combine(dir, "s");
            WTelegram.Client client = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(dir);

                client = NewThrowawayClient(session);
                transport(client);
                client.MaxAutoReconnects = 1;   // a test must fail fast, not retry like the live client
                client.FloodRetryThreshold = 0; // and must never absorb a FLOOD_WAIT as a silent 60 s sleep

                // ConnectAsync completes the transport + MTProto handshake WITHOUT logging in. That is
                // exactly the layer a proxy can break, and it needs no account.
                var connect = client.ConnectAsync();
                var done = await Task.WhenAny(connect, Task.Delay(TestTimeoutMs, ct)).ConfigureAwait(false);
                sw.Stop();

                if (done != connect)
                {
                    res.Error = ct.IsCancellationRequested ? "Cancelled." : "Timed out after " + (TestTimeoutMs / 1000) + "s.";
                    Swallow(connect);
                }
                else if (connect.IsFaulted)
                {
                    var ex = connect.Exception != null ? connect.Exception.GetBaseException() : null;
                    res.Error = Friendly(ex);
                    res.RawError = Raw(ex);
                }
                else if (connect.IsCanceled)
                {
                    // ⚠ STEP 6.4 FIX — CANCELLED IS A FAILURE. When the socket drops during the first exchange, WTC's
                    //   reactor resets the client (ResetAsync cancels its token) and ConnectAsync ends CANCELLED, not
                    //   faulted. This branch used to fall through to "OK": a strict tunnel test whose route was REFUSED
                    //   reported "OK in 712 ms" (measured 2026-10-16), and an MTProxy that hung up at once could too.
                    res.Error = "The connection was dropped before Telegram answered.";
                    res.RawError = "connect task cancelled";
                }
                else
                {
                    res.Ok = true;
                    res.Ms = sw.ElapsedMilliseconds;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                res.Ok = false;
                res.Error = Friendly(ex);
                res.RawError = Raw(ex);
            }
            finally
            {
                if (client != null) { try { client.Dispose(); } catch { } }
                // Best-effort cleanup; a leftover temp session is harmless but untidy. The client is
                // disposed first so the file handle is released.
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }

            // BATCH-TA-16f/F3 — LOG THE RAW EXCEPTION ALONGSIDE THE FRIENDLY TEXT.
            // The friendly string is for the USER; the raw type+message is for US. Without it a
            // MISCLASSIFICATION IS UNDETECTABLE: the TA-16e gate logged two proxies as the generic
            // "Couldn't connect (WTException)" and there was no way to tell from the log whether the
            // wrong-secret mapping in Friendly() had simply failed to match, or whether the host really
            // was unreachable. Now the mapping can be checked against what actually happened.
            // ⚠ SAFE TO LOG: an MTProto/socket exception message never contains the proxy link or its
            //   secret — the link is only ever passed to WTC as MTProxyUrl, and Friendly()/SafeForLog
            //   remain the only things the USER sees. Never add the URL to this line.
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[PROXYTEST] " + label
                    + (res.Ok ? " OK in " + res.Ms + "ms"
                              : " FAILED: " + res.Error
                                + (string.IsNullOrEmpty(res.RawError) ? "" : "  raw=" + res.RawError)));
            return res;
        }

        /// <summary>A client on a THROWAWAY session file — never an account session (see the class remarks).</summary>
        private static WTelegram.Client NewThrowawayClient(string session)
        {
            return new WTelegram.Client(what =>
            {
                switch (what)
                {
                    case "api_id": return ApiCredentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case "api_hash": return ApiCredentials.ApiHash;
                    case "session_pathname": return session;   // ⚠ throwaway — never an account session (see remarks)
                    case "device_model": return "TelegArm proxy test";
                    case "system_version": return "test";
                    case "app_version": return Program.Version;
                    case "lang_code": return "en";
                    case "system_lang_code": return "en";
                    // Return null for anything else (notably phone_number): we never log in, so WTC must
                    // not be given credentials, and it must not block waiting for them.
                    default: return null;
                }
            });
        }

        /// <summary>Step 6.4 — a developer check of the tunnel's data path, run only by "--probe-tunnel": on a throwaway,
        /// never-logged-in session it pulls a large language pack (big downlink frames), sends a ~300 KB request (a big
        /// uplink packet — the server's answer, even an error, proves it arrived whole), and opens a media connection to
        /// DC2 and a connection to DC4. Lines of results; never throws.</summary>
        internal static async Task<System.Collections.Generic.List<string>> TunnelBulkProbeAsync(WsTunnelOptions o)
        {
            var lines = new System.Collections.Generic.List<string>();
            string dir = Path.Combine(Path.GetTempPath(), "telegarm_proxytest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            WTelegram.Client client = null;
            try
            {
                Directory.CreateDirectory(dir);
                client = NewThrowawayClient(Path.Combine(dir, "s"));
                client.TcpHandler = (h, p) => WsTunnel.ConnectAsync(h, p, o);
                client.MaxAutoReconnects = 1;
                client.FloodRetryThreshold = 0;
                await Step(lines, "connect", async () => { await client.ConnectAsync().ConfigureAwait(false); return "ok"; }).ConfigureAwait(false);
                await Step(lines, "langpack (download)", async () =>
                {
                    var pack = await client.Langpack_GetLangPack("tdesktop", "en").ConfigureAwait(false);
                    return pack.strings.Length + " strings";
                }).ConfigureAwait(false);
                await Step(lines, "300 KB request (upload)", async () =>
                {
                    var keys = new string[3000];
                    for (int i = 0; i < keys.Length; i++) keys[i] = "telegarm_tunnel_probe_key_" + i.ToString("D6") + new string('x', 70);
                    try { var r = await client.Langpack_GetStrings("tdesktop", "en", keys).ConfigureAwait(false); return r.Length + " strings back"; }
                    catch (TL.RpcException ex) { return "server answered " + ex.Code + " " + ex.Message; }
                }).ConfigureAwait(false);
                await Step(lines, "DC2 media connection", async () =>
                {
                    var alt = await client.GetClientForDC(-2, true).ConfigureAwait(false);
                    var cfg = await alt.Help_GetConfig().ConfigureAwait(false);
                    return "config this_dc=" + cfg.this_dc;
                }).ConfigureAwait(false);
                await Step(lines, "DC4 connection", async () =>
                {
                    var alt = await client.GetClientForDC(4, true).ConfigureAwait(false);
                    var cfg = await alt.Help_GetConfig().ConfigureAwait(false);
                    return "config this_dc=" + cfg.this_dc;
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { lines.Add("bulk probe failed: " + Raw(ex)); }
            finally
            {
                if (client != null) { try { client.Dispose(); } catch { } }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
            lines.Add("routes: " + (WsTunnelStats.Summary() ?? "(none)"));
            return lines;
        }

        private static async Task Step(System.Collections.Generic.List<string> lines, string name, Func<Task<string>> work)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var t = work();
                if (await Task.WhenAny(t, Task.Delay(30000)).ConfigureAwait(false) != t) { Swallow(t); lines.Add(name + ": TIMEOUT"); return; }
                lines.Add(name + ": " + await t.ConfigureAwait(false) + " in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { lines.Add(name + ": FAILED " + Raw(ex) + " after " + sw.ElapsedMilliseconds + " ms"); }
        }

        /// <summary>Turns a WTC/socket exception into something a user can act on. NEVER echoes the link.</summary>
        private static string Friendly(Exception ex)
        {
            if (ex == null) return "Couldn't connect.";
            if (ex is Socks5Exception) return ex.Message;   // already user-facing, never quotes credentials
            string m = ex.Message ?? "";
            if (ex is System.Net.Sockets.SocketException) return "Can't reach that server.";
            if (m.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0) return "The secret isn't accepted by that server.";
            // MEASURED (TA-16e/E2): a real host with the WRONG SECRET reaches the obfuscated handshake and
            // then dies exactly here — "Could not read payload length : Connection shut down", 3.3 s in.
            // TA-15/X4 predicted there would be no distinct signal for a bad secret; this is as close as it
            // gets, and it is worth naming because "wrong secret" is the single most likely user error.
            if (m.IndexOf("payload length", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("Connection shut down", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The server closed the connection. The secret is probably wrong for it.";
            if (m.IndexOf("OBFUSCATION", StringComparison.OrdinalIgnoreCase) >= 0) return "This build can't use MTProto proxies.";
            // Anything else: the type is more useful than a long MTProto message, and can't leak a link.
            return "Couldn't connect (" + ex.GetType().Name + ").";
        }

        /// <summary>BATCH-TA-16f/F3 — exception type + message for the LOG. Flattens the inner chain,
        /// because a WTException's own message is often the useful part while an AggregateException's is
        /// not. Newlines collapsed so one failure stays one log line.</summary>
        private static string Raw(Exception ex)
        {
            if (ex == null) return null;
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append("  <- ");
                sb.Append(e.GetType().Name).Append(": ").Append((e.Message ?? "").Replace('\r', ' ').Replace('\n', ' '));
                if (sb.Length > 400) break;   // a stack-trace-length message must not swamp the log
            }
            return sb.ToString();
        }

        private static void Swallow(Task t)
        {
            if (t == null) return;
            t.ContinueWith(x => { var _ = x.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
