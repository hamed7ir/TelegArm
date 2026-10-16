using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TelegArm.Core;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 6.3 — the WebSocket tunnel's pieces, offline: WTC's REAL init (captured through a test
    /// TcpHandler on loopback, never sent anywhere), the seekable keystream, the packet splitter, and WsClient against a
    /// local WebSocket server. The live half (the gateway, a full handshake, big frames both ways, media and DC4) is
    /// "TelegArm.exe --probe-tunnel out.txt", which needs the network.</summary>
    internal static class WsTunnelTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("tunnel: WTC's real init parses as DC 2, intermediate (0xEE)", WtcInit));
            t.Add(new KeyValuePair<string, Action>("tunnel: the seekable keystream matches sequential AES-CTR, carries included", KeystreamMatchesCtr));
            t.Add(new KeyValuePair<string, Action>("tunnel: the splitter cuts concatenated packets at any chunking, bytes untouched", SplitterCuts));
            t.Add(new KeyValuePair<string, Action>("tunnel: a zero length switches the splitter to pass-through", SplitterZeroLength));
            t.Add(new KeyValuePair<string, Action>("tunnel: gateway names, pins and the route memory", Routes));
            t.Add(new KeyValuePair<string, Action>("tunnel: WsClient against a local server (101 + frame in one segment, 126/127, fragments, ping, close)", WsClientLocal));
        }

        // ── WTC's init ──────────────────────────────────────────────────────────────────────────────────────────────

        private static byte[] CaptureWtcInit()
        {
            WTelegram.Helpers.Log = (level, text) => { };   // WTC logs to the console by default
            string dir = Path.Combine(Program.DataRoot, "wtc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new WTelegram.Client(what =>
            {
                switch (what)
                {
                    case "api_id": return "1";
                    case "api_hash": return "0123456789abcdef0123456789abcdef";
                    case "session_pathname": return Path.Combine(dir, "s");
                    default: return null;
                }
            });
            try
            {
                client.MaxAutoReconnects = 1;
                client.TcpHandler = async (host, p) => { var c = new TcpClient(AddressFamily.InterNetwork); await c.ConnectAsync(IPAddress.Loopback, port); return c; };
                var connect = client.ConnectAsync();
                var accept = listener.AcceptTcpClientAsync();
                Check.True(accept.Wait(10000), "WTC connected to the test handler");
                using (var server = accept.Result)
                {
                    var s = server.GetStream();
                    var init = new byte[64];
                    int got = 0;
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (got < 64 && DateTime.UtcNow < deadline)
                    {
                        var r = s.ReadAsync(init, got, 64 - got);
                        if (!r.Wait(10000) || r.Result <= 0) break;
                        got += r.Result;
                    }
                    Check.Equal(64, got, "init bytes");
                    connect.ContinueWith(x => { var _ = x.Exception; });
                    return init;
                }
            }
            finally
            {
                try { client.Dispose(); } catch { }
                listener.Stop();
            }
        }

        private static void WtcInit()
        {
            var init = CaptureWtcInit();
            using (var ks = Obfuscated2.UplinkKeystream(init))
            {
                int dc; uint tag;
                Check.True(Obfuscated2.TryParseInit(init, ks, out dc, out tag), "tag recognised");
                Check.Equal(Obfuscated2.TagIntermediate, tag, "WTC's direct transport");
                Check.Equal(2, dc, "WTC's first connection goes to DC 2");
            }
        }

        // ── keystream ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>WTC's own sequential AES-CTR (src/Encryption.cs:329-342), as the reference.</summary>
        private static byte[] SequentialCtr(byte[] key, byte[] ivIn, int length)
        {
            var iv = (byte[])ivIn.Clone();
            var outp = new byte[length];
            var ecount = new byte[16];
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor(key, null))
                {
                    int num = 0;
                    for (int i = 0; i < length; i++)
                    {
                        if (num == 0)
                        {
                            enc.TransformBlock(iv, 0, 16, ecount, 0);
                            for (int n = 15; n >= 0; n--) if (++iv[n] != 0) break;
                        }
                        outp[i] = ecount[num];
                        num = (num + 1) % 16;
                    }
                }
            }
            return outp;
        }

        private static void KeystreamMatchesCtr()
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7 + 3);
            var iv = new byte[16];
            for (int i = 0; i < 16; i++) iv[i] = 0xFF;   // the low bytes overflow on the first increment
            iv[0] = 0x12; iv[7] = 0xFE;
            var reference = SequentialCtr(key, iv, 4096);
            using (var ks = new CtrKeystream(key, iv))
            {
                foreach (int off in new[] { 0, 15, 16, 17, 255, 256, 1000, 4095, 3, 2048, 0 })   // forwards and back
                    Check.Equal(reference[off], ks.At(off), "keystream byte " + off);
                var buf = new byte[4096];
                ks.Xor(0, buf, 0, buf.Length);
                for (int i = 0; i < buf.Length; i++) if (buf[i] != reference[i]) Check.True(false, "Xor differs at " + i);
            }
        }

        // ── splitter ────────────────────────────────────────────────────────────────────────────────────────────────

        private static void SplitterCuts()
        {
            var init = new byte[64];
            new Random(7).NextBytes(init);
            var sizes = new[] { 4, 1, 300, 65536 + 13, 8, 520 * 1024, 12 };
            var plain = new MemoryStream();
            var packetBounds = new List<int>();
            var rnd = new Random(11);
            foreach (int n in sizes)
            {
                packetBounds.Add((int)plain.Length);
                plain.Write(BitConverter.GetBytes(n), 0, 4);
                var payload = new byte[n];
                rnd.NextBytes(payload);
                plain.Write(payload, 0, n);
            }
            packetBounds.Add((int)plain.Length);
            var cipher = plain.ToArray();
            using (var ks = Obfuscated2.UplinkKeystream(init)) ks.Xor(64, cipher, 0, cipher.Length);

            foreach (int seed in new[] { 1, 2, 3 })
            {
                var chunkRnd = new Random(seed);
                using (var ks = Obfuscated2.UplinkKeystream(init))
                {
                    var sp = new PacketSplitter(ks, 64, Obfuscated2.TagIntermediate);
                    var got = new List<byte[]>();
                    int pos = 0;
                    while (pos < cipher.Length)
                    {
                        int take = Math.Min(cipher.Length - pos, seed == 1 ? 1 + chunkRnd.Next(7) : 1 + chunkRnd.Next(100000));
                        sp.Append(cipher, pos, take);
                        pos += take;
                        int i, c;
                        while (sp.Next(out i, out c)) { var p = new byte[c]; Buffer.BlockCopy(sp.Data, i, p, 0, c); got.Add(p); }
                        sp.Compact();
                    }
                    Check.True(!sp.Disabled, "splitter stays on (seed " + seed + ")");
                    Check.Equal(sizes.Length, got.Count, "packets out (seed " + seed + ")");
                    for (int k = 0; k < got.Count; k++)
                    {
                        int start = packetBounds[k], len = packetBounds[k + 1] - start;
                        Check.Equal(len, got[k].Length, "packet " + k + " length (seed " + seed + ")");
                        for (int b = 0; b < len; b++)
                            if (got[k][b] != cipher[start + b]) Check.True(false, "packet " + k + " byte " + b + " changed");
                    }
                }
            }
        }

        private static void SplitterZeroLength()
        {
            var init = new byte[64];
            new Random(5).NextBytes(init);
            var plain = new byte[4 + 10 + 4 + 20];
            BitConverter.GetBytes(10).CopyTo(plain, 0);
            BitConverter.GetBytes(0).CopyTo(plain, 14);   // a zero length after the first packet
            using (var ks = Obfuscated2.UplinkKeystream(init))
            {
                var cipher = (byte[])plain.Clone();
                ks.Xor(64, cipher, 0, cipher.Length);
                var sp = new PacketSplitter(ks, 64, Obfuscated2.TagIntermediate);
                sp.Append(cipher, 0, cipher.Length);
                int i, c;
                Check.True(sp.Next(out i, out c) && c == 14, "first packet cut");
                Check.True(sp.Next(out i, out c) && c == 24 && sp.Disabled, "the rest passes through once disabled");
                Check.True(!sp.Next(out i, out c), "nothing left");
            }
            var abridged = new PacketSplitter(null, 64, Obfuscated2.TagAbridged);
            Check.True(abridged.Disabled, "abridged is never cut");
        }

        // ── routes ──────────────────────────────────────────────────────────────────────────────────────────────────

        private static void Routes()
        {
            Check.Equal("kws2.web.telegram.org", WsRoutes.Hosts(2, false)[0], "main name");
            var media = WsRoutes.Hosts(4, true);
            Check.True(media.Length == 2 && media[0] == "kws4-1.web.telegram.org" && media[1] == "kws4.web.telegram.org", "media tries -1 first");
            Check.Equal("kws2.web.telegram.org", WsRoutes.Hosts(203, false)[0], "DC 203 is served as DC 2");

            string err;
            var def = WsRoutes.ParsePins("", out err);
            Check.True(err == null && def.Count == 2 && def[2] == "149.154.167.220" && def[4] == "149.154.167.220", "defaults");
            var mixed = WsRoutes.ParsePins(" 1:149.154.175.50 ,\n5=1.2.3.4; 9=::1; x=1.1.1.1; 3=999.1.1.1", out err);
            Check.True(mixed.Count == 2 && mixed[1] == "149.154.175.50" && mixed[5] == "1.2.3.4", "valid entries kept");
            Check.True(err != null && err.Contains("9=::1") && err.Contains("x=1.1.1.1") && err.Contains("3=999.1.1.1"), "bad entries reported: " + err);
            Check.Equal("1=149.154.175.50;5=1.2.3.4", WsRoutes.FormatPins(mixed), "canonical text");

            WsRoutes.Reset();
            Check.True(WsRoutes.Blocked(2, false) == null, "nothing remembered");
            WsRoutes.Fail(2, true, new WsHandshakeException(302, "redirect"));
            Check.Equal("302", WsRoutes.Blocked(2, true), "a redirect is remembered");
            Check.True(WsRoutes.Blocked(2, false) == null, "per (DC, media)");
            WsRoutes.Fail(4, false, new TimeoutException());
            Check.Equal("timeout", WsRoutes.Blocked(4, false), "a timeout is remembered for a while");
            WsRoutes.Succeeded(4, false);
            Check.True(WsRoutes.Blocked(4, false) == null, "success clears it");
            WsRoutes.Reset();
        }

        // ── WsClient against a local server ─────────────────────────────────────────────────────────────────────────

        private sealed class ServerLog { public string Request; public List<byte[]> Received = new List<byte[]>(); public byte[] Pong; public byte[] CloseReply; }

        private static void WsClientLocal()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var log = new ServerLog();
            var rnd = new Random(3);
            var d126 = new byte[300]; rnd.NextBytes(d126);
            var d127 = new byte[70000]; rnd.NextBytes(d127);
            var fragA = Encoding.ASCII.GetBytes("frag-one|"); var fragB = Encoding.ASCII.GetBytes("frag-two");
            var first = Encoding.ASCII.GetBytes("hello");
            var ping = Encoding.ASCII.GetBytes("are you there");
            var up = new[] { new byte[5], new byte[300], new byte[70000] };
            foreach (var u in up) rnd.NextBytes(u);

            var server = Task.Run(() => RunServer(listener, log, first, d126, d127, fragA, fragB, ping, up.Length));
            try
            {
                using (var tcp = new TcpClient(AddressFamily.InterNetwork))
                {
                    tcp.ConnectAsync(IPAddress.Loopback, port).Wait(5000);
                    var ws = WsClient.OverStreamAsync(tcp.GetStream(), "kws2.web.telegram.org", "/apiws").Result;
                    foreach (var u in up) ws.SendBinaryAsync(u, 0, u.Length).Wait(5000);

                    var all = new MemoryStream();
                    var buf = new byte[8192];
                    while (true)
                    {
                        var r = ws.ReadAsync(buf, 0, buf.Length);
                        Check.True(r.Wait(10000), "read finished in time");
                        if (r.Result == 0) break;
                        all.Write(buf, 0, r.Result);
                    }
                    var expect = new MemoryStream();
                    foreach (var part in new[] { first, d126, d127, fragA, fragB }) expect.Write(part, 0, part.Length);
                    Check.True(Same(expect.ToArray(), all.ToArray()), "downlink bytes, in order (" + all.Length + " of " + expect.Length + ")");
                    Check.True(ws.CloseReason != null && ws.CloseReason.StartsWith("1000"), "close reason: " + ws.CloseReason);
                    ws.Dispose();
                }
                Check.True(server.Wait(10000), "server finished");
                if (server.Exception != null) throw server.Exception.GetBaseException();
                Check.True(log.Request.StartsWith("GET /apiws HTTP/1.1\r\n"), "request line");
                Check.True(log.Request.Contains("\r\nSec-WebSocket-Protocol: binary\r\n"), "binary subprotocol");
                Check.True(log.Request.IndexOf("Origin:", StringComparison.OrdinalIgnoreCase) < 0, "no Origin header");
                Check.True(log.Request.Contains("\r\nHost: kws2.web.telegram.org\r\n"), "Host header");
                Check.Equal(up.Length, log.Received.Count, "uplink messages");
                for (int i = 0; i < up.Length; i++) Check.True(Same(up[i], log.Received[i]), "uplink message " + i + " (" + up[i].Length + " bytes)");
                Check.True(log.Pong != null && Same(ping, log.Pong), "ping answered with its payload");
                Check.True(log.CloseReply != null && log.CloseReply.Length == 2 && log.CloseReply[0] == 0x03 && log.CloseReply[1] == 0xE8, "close echoed (1000)");
            }
            finally { listener.Stop(); }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void RunServer(TcpListener listener, ServerLog log, byte[] first, byte[] d126, byte[] d127,
                                      byte[] fragA, byte[] fragB, byte[] ping, int expectUp)
        {
            using (var c = listener.AcceptTcpClient())
            {
                c.NoDelay = true;
                var s = c.GetStream();
                var req = new StringBuilder();
                while (!req.ToString().EndsWith("\r\n\r\n")) { int b = s.ReadByte(); if (b < 0) throw new IOException("eof in request"); req.Append((char)b); }
                log.Request = req.ToString();
                string key = null;
                foreach (var line in log.Request.Split(new[] { "\r\n" }, StringSplitOptions.None))
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
                string accept;
                using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
                                                   + accept + "\r\nSec-WebSocket-Protocol: binary\r\n\r\n");
                // the 101 AND the first frame in ONE write: the client must not lose bytes read past the blank line
                var one = new MemoryStream();
                one.Write(head, 0, head.Length);
                WriteFrame(one, 0x82, first);
                s.Write(one.ToArray(), 0, (int)one.Length);

                for (int i = 0; i < expectUp; i++) { int op; byte[] p = ReadFrame(s, out op); log.Received.Add(p); }

                var rest = new MemoryStream();
                WriteFrame(rest, 0x82, d126);            // 126: 16-bit length
                WriteFrame(rest, 0x82, d127);            // 127: 64-bit length
                WriteFrame(rest, 0x02, fragA);           // binary, not final
                WriteFrame(rest, 0x89, ping);            // a ping between the fragments
                WriteFrame(rest, 0x80, fragB);           // continuation, final
                s.Write(rest.ToArray(), 0, (int)rest.Length);
                int pongOp; log.Pong = ReadFrame(s, out pongOp);
                if (pongOp != 0xA) throw new IOException("expected a pong, got opcode " + pongOp);
                var close = new MemoryStream();
                WriteFrame(close, 0x88, new byte[] { 0x03, 0xE8, (byte)'b', (byte)'y', (byte)'e' });
                s.Write(close.ToArray(), 0, (int)close.Length);
                int closeOp; log.CloseReply = ReadFrame(s, out closeOp);
                if (closeOp != 0x8) throw new IOException("expected a close, got opcode " + closeOp);
            }
        }

        private static void WriteFrame(Stream s, int firstByte, byte[] p)
        {
            s.WriteByte((byte)firstByte);
            if (p.Length < 126) s.WriteByte((byte)p.Length);
            else if (p.Length <= 0xFFFF) { s.WriteByte(126); s.WriteByte((byte)(p.Length >> 8)); s.WriteByte((byte)p.Length); }
            else { s.WriteByte(127); for (int i = 7; i >= 0; i--) s.WriteByte(i >= 4 ? (byte)0 : (byte)(p.Length >> (8 * i))); }
            s.Write(p, 0, p.Length);
        }

        private static byte[] ReadFrame(Stream s, out int op)
        {
            var h = ReadN(s, 2);
            op = h[0] & 0x0F;
            if ((h[1] & 0x80) == 0) throw new IOException("client frame not masked");
            long len = h[1] & 0x7F;
            if (len == 126) { var e = ReadN(s, 2); len = e[0] << 8 | e[1]; }
            else if (len == 127) { var e = ReadN(s, 8); len = 0; for (int i = 0; i < 8; i++) len = len << 8 | e[i]; }
            var mask = ReadN(s, 4);
            var p = ReadN(s, (int)len);
            for (int i = 0; i < p.Length; i++) p[i] ^= mask[i & 3];
            return p;
        }

        private static byte[] ReadN(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) throw new IOException("eof"); got += r; }
            return b;
        }
    }
}
