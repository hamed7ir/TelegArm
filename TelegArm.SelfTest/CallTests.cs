using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
            t.Add(new KeyValuePair<string, Action>("calls: video (native): B sees A's camera the right way round, A's preview mirrored; camera off and on again", NativeVideo));
            t.Add(new KeyValuePair<string, Action>("calls: video (native) on the old library (5.0.0) sending VP8, as ARM32 does", NativeVideoOldVp8));
            t.Add(new KeyValuePair<string, Action>("calls: video through C# (camera pushes, preview and remote pictures into CallVideoSink, camera off/on)", ManagedVideo));
            t.Add(new KeyValuePair<string, Action>("voice chats: the group engine (native): join payload, levels, our tone once unmuted, rejoin, the response checked", NativeGroup));
            t.Add(new KeyValuePair<string, Action>("voice chats: the same through C# (NativeGroupCall: payload JSON, levels, refused responses, rejoin, stop)", ManagedGroup));
            t.Add(new KeyValuePair<string, Action>("voice chats: participants (join, a min entry keeps your volume, an ssrc moves, left removes, the order)", Participants));
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

        private static void NativeVideo() { RunNativeVideo(null, 0); }
        private static void NativeVideoOldVp8() { RunNativeVideo("5.0.0", TgCalls.SelftestNoH264); }

        private static void RunNativeVideo(string version, int flags)
        {
            Check.True(TgCalls.Available, "tgcalls_native: " + TgCalls.Status);
            var report = new byte[4096];
            int rc = TgCalls.tgn_selftest_video(version == null ? null : System.Text.Encoding.UTF8.GetBytes(version + "\0"), 20, flags, report, report.Length);
            string r = TgCalls.Z(report).Trim();
            Console.WriteLine("        " + r.Replace("\n", "\n        "));
            Check.Equal(0, rc, "video loopback");
            if ((flags & TgCalls.SelftestNoH264) != 0) Check.True(r.Contains("VP8"), "VP8 encoders made");
        }

        // a 640x480 NV12 test picture: left half red, right half blue, a white square moving along the top
        private static void Pattern(byte[] nv12, int w, int h, int i)
        {
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) nv12[y * w + x] = (byte)(x < w / 2 ? 81 : 41);
            int uv = w * h;
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w / 2; x++)
                {
                    nv12[uv + y * w + x * 2] = (byte)(x < w / 4 ? 90 : 240);
                    nv12[uv + y * w + x * 2 + 1] = (byte)(x < w / 4 ? 240 : 110);
                }
            int sx = (i * 8) % (w - 64);
            for (int y = 32; y < 96; y++) for (int x = sx; x < sx + 64; x++) nv12[y * w + x] = 235;
            for (int y = 16; y < 48; y++) for (int x = sx / 2; x < (sx + 64) / 2; x++) { nv12[uv + y * w + x * 2] = 128; nv12[uv + y * w + x * 2 + 1] = 128; }
        }

        /// <summary>Red at a quarter of the width and blue at three quarters (lower half), or the mirror of that.</summary>
        private static bool Looks(CallVideoSink sink, bool redLeft)
        {
            var st = sink.Store;
            lock (st.Sync)
            {
                if (st.Front == null) return false;
                int w = st.FrontWidth, h = st.FrontHeight, y = h * 3 / 4;
                int l = (y * w + w / 4) * 4, r = (y * w + w * 3 / 4) * 4;
                Func<int, bool> red = i => st.Front[i + 2] > 150 && st.Front[i + 1] < 100 && st.Front[i] < 100;
                Func<int, bool> blue = i => st.Front[i] > 150 && st.Front[i + 2] < 100 && st.Front[i + 1] < 110;
                return redLeft ? red(l) && blue(r) : blue(l) && red(r);
            }
        }

        private static void ManagedVideo()
        {
            Check.True(TgCalls.Available, "tgcalls_native: " + TgCalls.Status);
            var key = new byte[256];
            new Random(13).NextBytes(key);
            string version = TgCalls.Versions().Split(';')[0];
            var preview = new CallVideoSink();
            var remote = new CallVideoSink();
            IntPtr cam = TgCalls.tgn_camera_create(1280, 720);
            Check.True(cam != IntPtr.Zero, "camera made");
            TgCalls.tgn_camera_set_preview(cam, preview.Callback, IntPtr.Zero, 160, 120, 15, 1);
            var inboxA = new List<byte[]>();
            var inboxB = new List<byte[]>();
            int stateA = -1, stateB = -1, remoteVideo = -1;
            NativeCall a = null, b = null;
            bool run = true;
            var pusher = new Thread(() =>   // the camera's pump thread: 30 pictures a second, planes in pinned memory
            {
                var nv12 = new byte[640 * 480 * 3 / 2];
                var pin = GCHandle.Alloc(nv12, GCHandleType.Pinned);
                try
                {
                    var p0 = pin.AddrOfPinnedObject();
                    for (int i = 0; run; i++)
                    {
                        Pattern(nv12, 640, 480, i);
                        var f = new TgCalls.FrameIn { Format = TgCalls.PixNv12, Width = 640, Height = 480, Plane0 = p0, Stride0 = 640, Plane1 = p0 + 640 * 480, Stride1 = 640 };
                        TgCalls.tgn_camera_push(cam, ref f);
                        Thread.Sleep(33);
                    }
                }
                finally { pin.Free(); }
            }) { IsBackground = true, Name = "test camera" };
            try
            {
                pusher.Start();
                a = NativeCall.Create(version, key, true, null, null, true, 92, null, TgCalls.AudioTest, 440, cam, n =>
                {
                    n.SignalingEmitted += d => { lock (inboxB) inboxB.Add(d); };
                    n.StateChanged += s => stateA = s;
                });
                b = NativeCall.Create(version, key, false, null, null, true, 92, null, TgCalls.AudioTest, 1000, wire: n =>
                {
                    n.SignalingEmitted += d => { lock (inboxA) inboxA.Add(d); };
                    n.StateChanged += s => stateB = s;
                    n.RemoteMediaChanged += (au, v) => remoteVideo = v;
                });
                Check.True(a != null && b != null, "both calls created (" + version + ")");
                b.SetRemoteVideo(remote, 320, 240);
                Func<Func<bool>, int, bool> pump = (done, ms) =>
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < ms)
                    {
                        byte[][] forA, forB;
                        lock (inboxA) { forA = inboxA.ToArray(); inboxA.Clear(); }
                        lock (inboxB) { forB = inboxB.ToArray(); inboxB.Clear(); }
                        foreach (var d in forA) a.ReceiveSignaling(d);
                        foreach (var d in forB) b.ReceiveSignaling(d);
                        if (done()) return true;
                        Thread.Sleep(20);
                    }
                    return false;
                };
                var t0 = Stopwatch.StartNew();
                bool seen = pump(() => stateA == TgCalls.StateEstablished && stateB == TgCalls.StateEstablished && remote.Frames >= 20
                                       && remoteVideo == 2 && Looks(remote, true) && preview.Frames >= 5 && Looks(preview, false), 20000);
                var vs = b.VideoStats();
                Console.WriteLine("        after " + t0.ElapsedMilliseconds + " ms: states " + stateA + "/" + stateB + ", B " + remote.Frames + " pictures (" +
                                  remote.Store.FrontWidth + "x" + remote.Store.FrontHeight + " shown, " + vs.RemoteWidth + "x" + vs.RemoteHeight + " decoded), remote video " +
                                  remoteVideo + "; preview " + preview.Frames + " (" + preview.Store.FrontWidth + "x" + preview.Store.FrontHeight + ")");
                Check.True(seen, "B sees A's picture the right way round; A's preview is mirrored");
                a.SetCamera(IntPtr.Zero);
                Check.True(pump(() => remoteVideo == 0, 8000), "camera off: B's remote video goes inactive");
                int before = remote.Frames;
                a.SetCamera(cam);
                Check.True(pump(() => remoteVideo == 2 && remote.Frames >= before + 10, 10000), "camera on again: pictures come back (" + (remote.Frames - before) + ")");
                a.Stop(5000);
                b.Stop(5000);
                int after = remote.Frames;
                Thread.Sleep(300);
                Check.Equal(after, remote.Frames, "no picture after stop");
            }
            finally
            {
                run = false;
                pusher.Join(2000);
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
                TgCalls.tgn_camera_destroy(cam);
            }
            GC.KeepAlive(preview);
            GC.KeepAlive(remote);
        }

        private static void NativeGroup()
        {
            Check.True(TgCalls.Available && TgCalls.GroupAvailable, "tgcalls_native with groups: " + TgCalls.Status);
            var report = new byte[8192];
            int rc = TgCalls.tgn_selftest_group(0, report, report.Length);
            string r = TgCalls.Z(report).Trim();
            Console.WriteLine("        " + r.Replace("\n", "\n        "));
            Check.Equal(0, rc, "group self-test");
        }

        private static void ManagedGroup()
        {
            Check.True(TgCalls.Available && TgCalls.GroupAvailable, "tgcalls_native with groups: " + TgCalls.Status);
            var payloads = new List<KeyValuePair<uint, string>>();
            var levels = new List<TgCalls.GroupLevel[]>();
            NativeGroupCall g = null;
            try
            {
                g = NativeGroupCall.Create(true, true, TgCalls.AudioTest, 440, wire: n =>
                {
                    n.JoinPayload += (ssrc, json) => { lock (payloads) payloads.Add(new KeyValuePair<uint, string>(ssrc, json)); };
                    n.Levels += l => { lock (levels) levels.Add(l); };
                });
                Check.True(g != null, "created");
                g.EmitJoin();
                var first = WaitPayload(payloads, 1);
                Console.WriteLine("        payload: ssrc " + first.Key + ", " + first.Value.Length + " chars");
                Check.True(first.Key != 0 && first.Value.Contains("\"ufrag\"") && first.Value.Contains("\"fingerprints\"") && first.Value.Contains("\"ssrc\""), "the join JSON (ufrag, fingerprints, ssrc)");
                Check.True(first.Value.Contains("\"ssrc-groups\""), "video: the simulcast / FID ssrc groups");
                Check.True(!g.SetJoinResponse("{}") && !g.SetJoinResponse("not json") && !g.SetJoinResponse(""), "malformed responses refused");
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000) { lock (levels) if (levels.Count >= 5) break; Thread.Sleep(20); }
                lock (levels) Check.True(levels.Count >= 5, "levels every ~100 ms (" + levels.Count + " in " + sw.ElapsedMilliseconds + " ms)");
                g.SetMute(false);
                sw.Restart();
                int mine = 0;
                while (sw.ElapsedMilliseconds < 3000 && mine == 0)
                {
                    lock (levels) foreach (var l in levels) foreach (var x in l) if (x.Ssrc == 0 && x.LevelMilli > 50) mine++;
                    Thread.Sleep(50);
                }
                Check.True(mine > 0, "unmuted: our tone shows as our own level");
                g.SetMode(TgCalls.GroupModeNone);
                g.EmitJoin();
                var second = WaitPayload(payloads, 2);
                Check.True(second.Key != first.Key, "rejoin: a new ssrc (" + first.Key + " -> " + second.Key + ")");
                sw.Restart();
                g.Stop(5000);
                Check.True(sw.ElapsedMilliseconds < 5000, "stopped in " + sw.ElapsedMilliseconds + " ms");
                g.EmitJoin();   // after Stop: ignored, no crash
            }
            finally { if (g != null) g.Dispose(); }
        }

        private static KeyValuePair<uint, string> WaitPayload(List<KeyValuePair<uint, string>> list, int count)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000) { lock (list) if (list.Count >= count) return list[count - 1]; Thread.Sleep(10); }
            Check.True(false, "join payload " + count + " within 5 s");
            return default(KeyValuePair<uint, string>);
        }

        private static void Participants()
        {
            Func<long, int, TL.GroupCallParticipant.Flags, TL.GroupCallParticipant> u = (id, src, fl) =>
                new TL.GroupCallParticipant { peer = new TL.PeerUser { user_id = id }, source = src, flags = fl, date = new DateTime(2026, 10, 16, 12, 0, (int)(id % 60)) };
            const TL.GroupCallParticipant.Flags Muted = TL.GroupCallParticipant.Flags.muted, CanUnmute = TL.GroupCallParticipant.Flags.can_self_unmute;
            var names = new Dictionary<long, string> { { 1, "Me" }, { 2, "Ada" }, { 3, "Charles" }, { 4, "The club" } };
            var ps = new GroupParticipants();
            var vol = u(2, 20, TL.GroupCallParticipant.Flags.muted_by_you | TL.GroupCallParticipant.Flags.has_volume); vol.volume = 5000;
            var chan = new TL.GroupCallParticipant { peer = new TL.PeerChannel { channel_id = 4 }, source = unchecked((int)0xF0000001u), flags = Muted };
            ps.Apply(new[] { u(1, 10, Muted | CanUnmute | TL.GroupCallParticipant.Flags.self), vol, u(3, 30, Muted), chan }, (id, isUser) => names[id]);
            Check.Equal(4, ps.Count, "four in");
            Check.True(ps.Self != null && ps.Self.PeerId == 1 && ps.Self.Muted && ps.Self.CanSelfUnmute, "us, muted, may unmute");
            var c = ps.BySource(30);
            Check.True(c != null && c.Name == "Charles" && c.Muted && !c.CanSelfUnmute, "Charles by ssrc, muted by an admin");
            var club = ps.BySource(unchecked((int)0xF0000001u));
            Check.True(club != null && !club.IsUser && club.PeerId == 4 && club.Name == "The club", "a channel speaking as itself (a uint ssrc as int)");
            long clubKey = GroupParticipants.Key(new TL.PeerChannel { channel_id = 4 });
            Check.True(clubKey < 0 && ps.ByPeer(clubKey) == club, "chats keyed negative");
            var ada = ps.BySource(20);
            Check.True(ada.MutedByYou && ada.Volume == 5000, "Ada: muted by you at 50 %");
            // a min entry (someone else's view) must not reset what we set for Ada
            ps.Apply(new[] { u(2, 20, TL.GroupCallParticipant.Flags.min) }, (id, isUser) => names[id]);
            Check.True(ada.MutedByYou && ada.Volume == 5000 && !ada.Muted, "min entry: our volume and mute kept, her own mute updated");
            // Ada rejoins with a new ssrc
            ps.Apply(new[] { u(2, 21, 0) }, (id, isUser) => null);
            Check.True(ps.BySource(20) == null && ps.BySource(21) == ada && ada.Name == "Ada", "a new ssrc moves her (and an unknown name keeps the old)");
            Check.True(!ada.MutedByYou && ada.Volume == 10000, "a full entry without them: no longer muted by you, 100 %");
            // a raised hand
            var hand = u(3, 30, Muted | TL.GroupCallParticipant.Flags.has_raise_hand_rating); hand.raise_hand_rating = 9;
            ps.Apply(new[] { hand }, (id, isUser) => names[id]);
            Check.True(c.HandRaised, "Charles raised his hand");
            // the order: us, the speaker, the raised hand, the rest
            ada.SpeakingUntil = Environment.TickCount + 60000;
            var order = ps.Sorted();
            Check.Equal("Me,Ada,Charles,The club", string.Join(",", order.ConvertAll(x => x.Name)), "the order");
            // left
            ps.Apply(new[] { u(3, 30, TL.GroupCallParticipant.Flags.left) }, (id, isUser) => names[id]);
            Check.True(ps.Count == 3 && ps.BySource(30) == null && ps.ByPeer(3) == null, "Charles left");
            ps.Apply(new[] { u(9, 90, TL.GroupCallParticipant.Flags.left) }, (id, isUser) => null);
            Check.Equal(3, ps.Count, "an unknown one leaving changes nothing");
            ps.Clear();
            Check.True(ps.Count == 0 && ps.BySource(21) == null && ps.Self == null, "cleared");
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
                // subscribed before the instance starts (as the app does); handed over off the callback thread (never re-enter
                // tgcalls from its own callback)
                a = NativeCall.Create(version, key, true, null, null, true, 92, null, TgCalls.AudioTest, 440, wire: n =>
                {
                    n.SignalingEmitted += d => { lock (inboxB) inboxB.Add(d); };
                    n.StateChanged += s => stateA = s;
                    n.SignalBarsChanged += s => barsA = s;
                });
                b = NativeCall.Create(version, key, false, null, null, true, 92, null, TgCalls.AudioTest, 1000, wire: n =>
                {
                    n.SignalingEmitted += d => { lock (inboxA) inboxA.Add(d); };
                    n.StateChanged += s => stateB = s;
                });
                Check.True(a != null && b != null, "both calls created (" + version + ")");
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
