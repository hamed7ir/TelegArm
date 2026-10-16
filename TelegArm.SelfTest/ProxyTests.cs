using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TelegArm.Core;

namespace TelegArm.SelfTest
{
    /// <summary>Redesign step 6.6 — SOCKS5 links, where they are stored (never in ProxyList: 1.9.x would feed them to
    /// MTProxyUrl), how the transports rank, and the SOCKS5 client against a local server.</summary>
    internal static class ProxyTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("proxy: t.me/socks and tg://socks links normalise; MTProto links unchanged", Links));
            t.Add(new KeyValuePair<string, Action>("proxy: SOCKS5 links live in their own list; tunnel > SOCKS5 > MTProto > direct", Storage));
            t.Add(new KeyValuePair<string, Action>("proxy: SOCKS5 client with user/password, CONNECT to the DC, then the stream is the tunnel", Socks5WithAuth));
            t.Add(new KeyValuePair<string, Action>("proxy: SOCKS5 wrong password and no-auth servers", Socks5Refusals));
        }

        private static string Norm(string raw)
        {
            string n, e;
            Check.True(ProxyUrl.TryNormalize(raw, out n, out e), "normalises: " + raw + " (" + e + ")");
            return n;
        }

        private static void Links()
        {
            string a = Norm("https://t.me/socks?server=1.2.3.4&port=1080&user=a%20b&pass=c%26d");
            Check.Equal("tg://socks?server=1.2.3.4&port=1080&user=a%20b&pass=c%26d", a, "t.me/socks with credentials");
            Check.Equal("a b", ProxyUrl.UserOf(a), "user unescaped");
            Check.Equal("c&d", ProxyUrl.PassOf(a), "password unescaped, its & intact");
            Check.True(ProxyUrl.IsSocks(a), "kind");
            Check.Equal("1.2.3.4:1080", ProxyUrl.SafeForLog(a), "the log form has no credentials");
            Check.Equal("SOCKS5 1.2.3.4:1080", ProxyUrl.DisplayLabel(a), "label");
            Check.Equal("tg://socks?server=h.example&port=1080", Norm("t.me/socks?server=h.example&port=1080"), "scheme-less, no credentials");
            Check.Equal("tg://socks?server=h&port=9050", Norm("tg://socks?server=h&port=9050"), "tg://socks");
            Check.True(ProxyUrl.IsProxyLink("https://t.me/socks?server=h&port=1"), "t.me/socks is intercepted");
            Check.True(ProxyUrl.IsProxyLink("tg://socks?server=h&port=1"), "tg://socks is intercepted");
            Check.True(!ProxyUrl.IsProxyLink("https://t.me/somebody"), "a username link is not");
            string e2, n2;
            Check.True(!ProxyUrl.TryNormalize("t.me/socks?server=h", out n2, out e2), "no port is refused");
            string mt = Norm("https://t.me/proxy?server=p.example&port=443&secret=dd0123456789abcdef0123456789abcdef");
            Check.Equal("tg://proxy?server=p.example&port=443&secret=dd0123456789abcdef0123456789abcdef", mt, "MTProto as before");
            Check.True(!ProxyUrl.IsSocks(mt), "MTProto is not SOCKS");
            Check.Equal("MTPROTO p.example:443", ProxyUrl.DisplayLabel(mt), "MTProto label as before");

            // review fixes: a standard-base64 secret is stored URL-safe (WTC would read '+' as a space and drop it)
            var raw16 = new byte[16];
            for (int i = 0; i < 15; i++) raw16[i] = (byte)(i % 3 == 0 ? 0xFB : i % 3 == 1 ? 0xEF : 0xBE);
            raw16[15] = 0xFB;
            string b64 = Convert.ToBase64String(raw16);
            Check.True(b64.IndexOf('+') >= 0, "the test secret has a '+'");
            string norm64 = Norm("tg://proxy?server=h&port=443&secret=" + Uri.EscapeDataString(b64));
            string stored = ProxyUrl.SecretOf(norm64);
            Check.True(stored.IndexOf('+') < 0 && stored.IndexOf('/') < 0, "stored URL-safe: " + stored);
            string why;
            var back = ProxyUrl.DecodeSecret(stored, out why);
            Check.True(back != null && back.Length == 16 && back[0] == 0xFB && back[15] == 0xFB, "same 16 bytes");
            Check.True(!ProxyUrl.TryNormalize("t.me/socks?server=a%26b&port=1080", out n2, out e2), "a server with '&' is refused");
            Check.True(!ProxyUrl.TryNormalize("tg://proxy?server=h%2541&port=443&secret=0123456789abcdef0123456789abcdef", out n2, out e2), "a server with '%' is refused");
        }

        private static void Storage()
        {
            const string mt1 = "tg://proxy?server=a&port=443&secret=0123456789abcdef0123456789abcdef";
            const string mt2 = "tg://proxy?server=b&port=443&secret=0123456789abcdef0123456789abcdef";
            const string sx = "tg://socks?server=c&port=1080";
            var s = new AppSettings();
            Check.Equal(ProxyKind.Direct, s.Transport, "fresh settings connect directly");
            s.SetSavedProxies(new[] { mt1, sx, mt2 }, sx, true);
            Check.True(s.ProxyList.Count == 2 && s.ProxyList[0] == mt1 && s.ProxyList[1] == mt2, "ProxyList holds MTProto only");
            Check.True(s.Socks5List.Count == 1 && s.Socks5List[0] == sx, "SOCKS5 in its own list");
            Check.True(s.Socks5Enabled && !s.ProxyEnabled && s.Socks5Active == 0, "SOCKS5 in use; ProxyEnabled false (1.9.x goes direct)");
            Check.Equal(sx, s.ActiveSocksUrl, "active SOCKS5");
            Check.True(s.ActiveProxyUrl == null, "no MTProxy");
            Check.Equal(ProxyKind.Socks5, s.Transport, "transport");
            Check.Equal(3, s.SavedProxies.Count, "combined list");
            s.WsTunnelEnabled = true;
            Check.Equal(ProxyKind.WebSocket, s.Transport, "the tunnel wins");
            Check.True(s.ActiveSocksUrl == null, "no SOCKS5 under the tunnel");
            s.WsTunnelEnabled = false;
            s.SetSavedProxies(s.SavedProxies, mt2, true);
            Check.True(s.ProxyEnabled && !s.Socks5Enabled && s.ProxyActive == 1, "MTProto selected");
            Check.Equal(ProxyKind.MtProto, s.Transport, "MTProto transport");
            s.SetSavedProxies(s.SavedProxies, sx, false);
            Check.True(!s.ProxyEnabled && !s.Socks5Enabled, "off");
            Check.Equal(sx, s.SelectedProxy, "the selection is remembered while off");
            Check.Equal(ProxyKind.Direct, s.Transport, "direct while off");
            // a hand-edited file with both on: SOCKS5 wins, so the MTProxy one is not used behind its back
            s.Socks5Enabled = true; s.Socks5Active = 0; s.ProxyEnabled = true; s.ProxyActive = 0;
            Check.Equal(ProxyKind.Socks5, s.Transport, "SOCKS5 over MTProto");
            Check.True(s.ActiveProxyUrl == null, "ActiveProxyUrl agrees");
        }

        // ── a local SOCKS5 server ──────────────────────────────────────────────────────────────────────────────────

        private sealed class Seen { public byte[] Greeting, Auth, Connect; }

        private static Task<Seen> RunServer(TcpListener l, bool wantAuth, bool acceptPassword)
        {
            return Task.Run(() =>
            {
                var seen = new Seen();
                using (var c = l.AcceptTcpClient())
                {
                    var s = c.GetStream();
                    var g = ReadN(s, 2);
                    seen.Greeting = Concat(g, ReadN(s, g[1]));
                    s.Write(new byte[] { 5, (byte)(wantAuth ? 2 : 0) }, 0, 2);
                    if (wantAuth)
                    {
                        var h = ReadN(s, 2);
                        var u = ReadN(s, h[1]);
                        var pl = ReadN(s, 1);
                        var p = ReadN(s, pl[0]);
                        seen.Auth = Concat(Concat(h, u), Concat(pl, p));
                        s.Write(new byte[] { 1, (byte)(acceptPassword ? 0 : 1) }, 0, 2);
                        if (!acceptPassword) return seen;
                    }
                    var head = ReadN(s, 4);
                    var rest = ReadN(s, head[3] == 1 ? 6 : head[3] == 4 ? 18 : ReadN(s, 1)[0] + 2);
                    seen.Connect = Concat(head, rest);
                    // the reply carries a bound address the client must skip, here a domain one (type 3)
                    var reply = new byte[] { 5, 0, 0, 3, 4, (byte)'b', (byte)'n', (byte)'d', (byte)'x', 0x12, 0x34 };
                    s.Write(reply, 0, reply.Length);
                    var hello = ReadN(s, 5);
                    if (Encoding.ASCII.GetString(hello) == "hello") s.Write(Encoding.ASCII.GetBytes("world"), 0, 5);
                }
                return seen;
            });
        }

        private static void Socks5WithAuth()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(1);
            try
            {
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                var server = RunServer(l, true, true);
                string link = Norm("t.me/socks?server=127.0.0.1&port=" + port + "&user=u%26x&pass=p%3D1");
                using (var tcp = Socks5.ConnectAsync(link, "149.154.167.50", 443).Result)
                {
                    var s = tcp.GetStream();
                    s.Write(Encoding.ASCII.GetBytes("hello"), 0, 5);
                    Check.Equal("world", Encoding.ASCII.GetString(ReadN(s, 5)), "the stream after CONNECT is the tunnel");
                }
                Check.True(server.Wait(5000), "server done");
                var seen = server.Result;
                Check.True(Same(seen.Greeting, new byte[] { 5, 2, 0, 2 }), "greeting offers none + user/password");
                Check.True(Same(seen.Auth, Concat(Concat(new byte[] { 1, 3 }, Encoding.ASCII.GetBytes("u&x")), Concat(new byte[] { 3 }, Encoding.ASCII.GetBytes("p=1")))),
                           "RFC 1929 credentials, unescaped");
                Check.True(Same(seen.Connect, new byte[] { 5, 1, 0, 1, 149, 154, 167, 50, 1, 187 }), "CONNECT 149.154.167.50:443 (IPv4)");
            }
            finally { l.Stop(); }
        }

        private static void Socks5Refusals()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(1);
            try
            {
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                var server = RunServer(l, true, false);
                string link = Norm("tg://socks?server=127.0.0.1&port=" + port + "&user=u&pass=wrong");
                try { Socks5.ConnectAsync(link, "149.154.167.50", 443).Wait(); Check.True(false, "a refused password must fail"); }
                catch (AggregateException ex)
                {
                    var b = ex.GetBaseException();
                    Check.True(b is Socks5Exception && b.Message.Contains("user name or password"), "refusal reported: " + b.Message);
                    Check.True(b.Message.IndexOf("wrong", StringComparison.Ordinal) < 0, "the password is not in the message");
                }
                server.Wait(5000);

                server = RunServer(l, false, true);
                using (var tcp = Socks5.ConnectAsync(Norm("tg://socks?server=127.0.0.1&port=" + port), "149.154.167.50", 443).Result)
                {
                    var s = tcp.GetStream();
                    s.Write(Encoding.ASCII.GetBytes("hello"), 0, 5);
                    Check.Equal("world", Encoding.ASCII.GetString(ReadN(s, 5)), "no-auth server");
                }
                Check.True(server.Wait(5000) && Same(server.Result.Greeting, new byte[] { 5, 1, 0 }), "greeting without credentials offers none only");
            }
            finally { l.Stop(); }
        }

        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
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
