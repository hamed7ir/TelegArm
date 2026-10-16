using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using TelegArm.Core.Calls;

namespace TelegArm.SelfTest
{
    /// <summary>
    /// 2026-10-16 — Telegram calls (tgcalls + tg_owt through native\tgcalls_native), in this process's architecture (x86 on the
    /// dev box). No server and no account: the DH is checked by itself, and two calls are connected to each other in this
    /// process (signaling handed over directly, ICE over this machine's own addresses), each sending a test tone.
    /// </summary>
    internal static class CallTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("calls: the DH: both sides derive the same 256-byte key and fingerprint; the public values pass the range checks", Dh));
            t.Add(new KeyValuePair<string, Action>("calls: the built-in prime is a 2048-bit safe prime (proved the slow way); a changed one is refused", Prime));
            t.Add(new KeyValuePair<string, Action>("calls: tgcalls_native loads; versions newest first, max layer 92", Loads));
            t.Add(new KeyValuePair<string, Action>("calls: two calls in this process connect and each hears the other's tone (native loopback)", NativeLoopback));
            t.Add(new KeyValuePair<string, Action>("calls: the same through C# (NativeCall marshalling, callbacks, signaling relayed by C#)", ManagedLoopback));
        }

        private static void Dh()
        {
            var pBytes = CallCrypto.KnownPrime();
            var p = CallCrypto.FromBigEndian(pBytes);
            var g = new BigInteger(3);
            var serverRandom = new byte[256];
            new Random(7).NextBytes(serverRandom);
            var a = CallCrypto.NewSecret(serverRandom);
            var b = CallCrypto.NewSecret(serverRandom);
            var ga = BigInteger.ModPow(g, a, p);
            var gb = BigInteger.ModPow(g, b, p);
            Check.True(CallCrypto.CheckPublic(ga, p) && CallCrypto.CheckPublic(gb, p), "g_a and g_b in range");
            Check.True(!CallCrypto.CheckPublic(BigInteger.One, p) && !CallCrypto.CheckPublic(p - 1, p) && !CallCrypto.CheckPublic(new BigInteger(12345), p), "1, p-1 and a small value refused");
            var keyA = CallCrypto.ToBigEndian256(BigInteger.ModPow(gb, a, p));
            var keyB = CallCrypto.ToBigEndian256(BigInteger.ModPow(ga, b, p));
            Check.Equal(256, keyA.Length, "key size");
            Check.Equal(CallCrypto.Hex(keyA), CallCrypto.Hex(keyB), "both sides derive the same key");
            Check.Equal(CallCrypto.Fingerprint(keyA), CallCrypto.Fingerprint(keyB), "and the same fingerprint");
            var gaBytes = CallCrypto.ToBigEndian256(ga);
            Check.True(CallCrypto.FromBigEndian(gaBytes) == ga, "big-endian round trip");
            Check.Equal(32, CallCrypto.Sha256(gaBytes).Length, "g_a_hash is SHA-256");
        }

        private static void Prime()
        {
            var p = CallCrypto.KnownPrime();
            string why;
            Check.True(CallCrypto.CheckPrime(p, 3, 4, out why), "known prime accepted (" + why + ")");
            var n = CallCrypto.FromBigEndian(p);
            var sw = Stopwatch.StartNew();
            Check.True(CallCrypto.ProbablyPrime(n, 4) && CallCrypto.ProbablyPrime((n - 1) / 2, 4), "p and (p-1)/2 prime (Miller-Rabin, " + sw.ElapsedMilliseconds + " ms)");
            var bad = (byte[])p.Clone();
            bad[255] ^= 2;   // still odd, no longer prime
            Check.True(!CallCrypto.CheckPrime(bad, 3, 4, out why), "a changed prime refused (" + why + ")");
            Check.True(!CallCrypto.CheckPrime(p, 9, 4, out why), "an unsupported g refused (" + why + ")");
        }

        private static void Loads()
        {
            Check.True(TgCalls.Available, "tgcalls_native: " + TgCalls.Status);
            string v = TgCalls.Versions();
            Check.True(v.StartsWith("13.0.0;"), "newest first: " + v);
            Check.Equal(92, TgCalls.MaxLayer(), "max layer");
            var api = TgCalls.ApiVersions();
            Check.True(api.Length >= 5, "library_versions for the API: " + string.Join(",", api));
        }

        private static void NativeLoopback()
        {
            Check.True(TgCalls.Available, "tgcalls_native: " + TgCalls.Status);
            var report = new byte[4096];
            int rc = TgCalls.tgn_selftest_loopback(25, report, report.Length);
            string r = TgCalls.Z(report).Trim();
            Console.WriteLine("        " + r.Replace("\n", "\n        "));
            Check.Equal(0, rc, "loopback");
        }

        private static void ManagedLoopback()
        {
            Check.True(TgCalls.Available, "tgcalls_native: " + TgCalls.Status);
            var key = new byte[256];
            new Random(11).NextBytes(key);
            string version = TgCalls.Versions().Split(';')[0];
            NativeCall a = null, b = null;
            var inboxA = new List<byte[]>();
            var inboxB = new List<byte[]>();
            int stateA = -1, stateB = -1, barsA = -1;
            try
            {
                a = NativeCall.Create(version, key, true, null, null, true, 92, null, TgCalls.AudioTest, 440);
                b = NativeCall.Create(version, key, false, null, null, true, 92, null, TgCalls.AudioTest, 1000);
                Check.True(a != null && b != null, "both calls created (" + version + ")");
                // handed over off the callback thread, as the app does (never re-enter tgcalls from its own callback)
                a.SignalingEmitted += d => { lock (inboxB) inboxB.Add(d); };
                b.SignalingEmitted += d => { lock (inboxA) inboxA.Add(d); };
                a.StateChanged += s => stateA = s;
                b.StateChanged += s => stateB = s;
                a.SignalBarsChanged += s => barsA = s;
                var sw = Stopwatch.StartNew();
                TgCalls.AudioStats sa = default(TgCalls.AudioStats), sb = default(TgCalls.AudioStats);
                bool heard = false;
                while (sw.ElapsedMilliseconds < 20000)
                {
                    byte[][] forA, forB;
                    lock (inboxA) { forA = inboxA.ToArray(); inboxA.Clear(); }
                    lock (inboxB) { forB = inboxB.ToArray(); inboxB.Clear(); }
                    foreach (var d in forA) a.ReceiveSignaling(d);
                    foreach (var d in forB) b.ReceiveSignaling(d);
                    sa = a.Stats(); sb = b.Stats();
                    if (stateA == TgCalls.StateEstablished && stateB == TgCalls.StateEstablished && sa.FramesAudible > 50 && sb.FramesAudible > 50
                        && Math.Abs(sa.RenderedHz - 1000) < 60 && Math.Abs(sb.RenderedHz - 440) < 30) { heard = true; break; }
                    Thread.Sleep(20);
                }
                Console.WriteLine("        after " + sw.ElapsedMilliseconds + " ms: A " + stateA + " hears " + sa.RenderedHz + " Hz, B " + stateB + " hears " + sb.RenderedHz + " Hz, A bars " + barsA);
                Check.True(heard, "connected and each heard the other");
                a.SetMute(true);   // muting one side: the other stops hearing it
                Thread.Sleep(800);
                int before = b.Stats().FramesAudible;
                Thread.Sleep(600);
                Check.True(b.Stats().FramesAudible - before < 15, "muted A: B hears (almost) nothing (" + (b.Stats().FramesAudible - before) + " audible frames in 600 ms)");
                string logA = a.Stop(5000);
                b.Stop(5000);
                Check.True(logA != null, "stopped (debug log " + (logA ?? "").Length + " chars)");
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
            }
        }
    }
}
