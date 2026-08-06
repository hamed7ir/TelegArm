using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        public static async Task<ProxyTestResult> TestAsync(string proxyUrl, CancellationToken ct)
        {
            var res = new ProxyTestResult();
            if (string.IsNullOrWhiteSpace(proxyUrl)) { res.Error = "No proxy link."; return res; }

            string dir = Path.Combine(Path.GetTempPath(), "telegarm_proxytest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            string session = Path.Combine(dir, "s");
            WTelegram.Client client = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(dir);

                client = new WTelegram.Client(what =>
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
                client.MTProxyUrl = proxyUrl;
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
                TelegArm.Helpers.Logger.Diag("[PROXYTEST] " + ProxyUrl.SafeForLog(proxyUrl)
                    + (res.Ok ? " OK in " + res.Ms + "ms"
                              : " FAILED: " + res.Error
                                + (string.IsNullOrEmpty(res.RawError) ? "" : "  raw=" + res.RawError)));
            return res;
        }

        /// <summary>Turns a WTC/socket exception into something a user can act on. NEVER echoes the link.</summary>
        private static string Friendly(Exception ex)
        {
            if (ex == null) return "Couldn't connect.";
            string m = ex.Message ?? "";
            if (ex is System.Net.Sockets.SocketException) return "Can't reach that server.";
            if (m.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0) return "The secret isn't accepted by that server.";
            // MEASURED (TA-16e/E2): a real host with the WRONG SECRET reaches the obfuscated handshake and
            // then dies exactly here — "Could not read payload length : Connection shut down", 3.3 s in.
            // TA-15/X4 predicted there would be no distinct signal for a bad secret; this is as close as it
            // gets, and it is worth naming because "wrong secret" is the single most likely user error.
            if (m.IndexOf("payload length", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("Connection shut down", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The server closed the connection — the secret is probably wrong for it.";
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
