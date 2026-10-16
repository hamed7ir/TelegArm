using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TelegArm.Core
{
    /// <summary>A SOCKS5 server answered, but said no (bad credentials, or it could not reach Telegram). The message is
    /// user-facing and never contains the user name or password.</summary>
    public sealed class Socks5Exception : IOException
    {
        public Socks5Exception(string message) : base(message) { }
    }

    /// <summary>
    /// Redesign step 6.6 — a generic SOCKS5 client (RFC 1928, user/password per RFC 1929) as WTelegramClient's
    /// TcpHandler. Unlike the WebSocket tunnel no relay is needed: once the server has CONNECTed, the same TcpClient's
    /// stream IS the tunnel, so WTC gets it back and speaks its obfuscated2 straight through it.
    ///
    /// Failures: an unreachable SOCKS server is a <see cref="SocketException"/> (WTC then tries the DC's other
    /// addresses, src/Client.cs:922 — harmless, they all go through the same server); a server that answers but refuses
    /// is a <see cref="Socks5Exception"/>, which WTC does not retry per address.
    /// ⚠ Credentials never reach a log: diagnostics name the server as ProxyUrl.SafeForLog does (host:port).
    /// </summary>
    public static class Socks5
    {
        public const int TimeoutMs = 10000;

        /// <summary>WTC's TcpFactory body: through the SOCKS5 server in <paramref name="link"/> (a normalised
        /// tg://socks link) to <paramref name="host"/>:<paramref name="port"/>.</summary>
        public static async Task<TcpClient> ConnectAsync(string link, string host, int port)
        {
            string server = ProxyUrl.HostOf(link);
            int sport = ProxyUrl.PortNumberOf(link);
            if (server == null || sport <= 0) throw new Socks5Exception("The SOCKS5 link has no server or port.");
            var tcp = new TcpClient { NoDelay = true };
            var work = HandshakeAsync(tcp, server, sport, ProxyUrl.UserOf(link), ProxyUrl.PassOf(link), host, port);
            try
            {
                if (await Task.WhenAny(work, Task.Delay(TimeoutMs)).ConfigureAwait(false) != work)
                {
                    var observed = work.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new SocketException((int)SocketError.TimedOut);
                }
                await work.ConfigureAwait(false);
                return tcp;
            }
            catch (Exception ex)
            {
                try { tcp.Close(); } catch { }
                TelegArm.Helpers.Logger.Diag("[SOCKS5] " + ProxyUrl.SafeForLog(link) + " -> " + host + ":" + port + " failed: "
                                             + ex.GetType().Name + ": " + ex.Message);
                if (ex is SocketException || ex is Socks5Exception) throw;
                throw new Socks5Exception("The SOCKS5 server broke off the connection.");
            }
        }

        private static async Task HandshakeAsync(TcpClient tcp, string server, int sport, string user, string pass, string host, int port)
        {
            await tcp.ConnectAsync(server, sport).ConfigureAwait(false);
            var s = tcp.GetStream();
            bool auth = !string.IsNullOrEmpty(user);

            // greeting: the methods we can do — none, and user/password when we have one
            await WriteAsync(s, auth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }).ConfigureAwait(false);
            var r = await ReadAsync(s, 2).ConfigureAwait(false);
            if (r[0] != 5) throw new Socks5Exception("That server doesn't speak SOCKS5.");
            if (r[1] == 0xFF) throw new Socks5Exception(auth ? "The SOCKS5 server accepts none of our sign-in methods."
                                                               : "The SOCKS5 server wants a user name and password.");
            if (r[1] == 2)
            {
                if (!auth) throw new Socks5Exception("The SOCKS5 server wants a user name and password.");
                var ub = Encoding.UTF8.GetBytes(user);
                var pb = Encoding.UTF8.GetBytes(pass ?? "");
                if (ub.Length > 255 || pb.Length > 255) throw new Socks5Exception("The SOCKS5 user name or password is too long.");
                var m = new byte[3 + ub.Length + pb.Length];
                m[0] = 1; m[1] = (byte)ub.Length;
                Buffer.BlockCopy(ub, 0, m, 2, ub.Length);
                m[2 + ub.Length] = (byte)pb.Length;
                Buffer.BlockCopy(pb, 0, m, 3 + ub.Length, pb.Length);
                await WriteAsync(s, m).ConfigureAwait(false);
                var a = await ReadAsync(s, 2).ConfigureAwait(false);
                if (a[1] != 0) throw new Socks5Exception("The SOCKS5 server refused the user name or password.");
            }
            else if (r[1] != 0) throw new Socks5Exception("The SOCKS5 server asked for an unsupported sign-in method.");

            // CONNECT to the DC
            byte[] addr;
            byte atyp;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                addr = ip.GetAddressBytes();
                atyp = ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
            }
            else
            {
                var hb = Encoding.ASCII.GetBytes(host);
                addr = new byte[1 + hb.Length];
                addr[0] = (byte)hb.Length;
                Buffer.BlockCopy(hb, 0, addr, 1, hb.Length);
                atyp = 3;
            }
            var req = new byte[4 + addr.Length + 2];
            req[0] = 5; req[1] = 1; req[2] = 0; req[3] = atyp;
            Buffer.BlockCopy(addr, 0, req, 4, addr.Length);
            req[req.Length - 2] = (byte)(port >> 8);
            req[req.Length - 1] = (byte)port;
            await WriteAsync(s, req).ConfigureAwait(false);

            var head = await ReadAsync(s, 4).ConfigureAwait(false);
            if (head[0] != 5) throw new Socks5Exception("That server doesn't speak SOCKS5.");
            if (head[1] != 0) throw new Socks5Exception("The SOCKS5 server couldn't reach Telegram (" + Reply(head[1]) + ").");
            // the bound address: skip it, its length depends on its type
            int skip = head[3] == 1 ? 4 : head[3] == 4 ? 16 : head[3] == 3 ? (await ReadAsync(s, 1).ConfigureAwait(false))[0] : -1;
            if (skip < 0) throw new Socks5Exception("The SOCKS5 server sent a malformed reply.");
            await ReadAsync(s, skip + 2).ConfigureAwait(false);
        }

        private static string Reply(byte code)
        {
            switch (code)
            {
                case 1: return "server failure";
                case 2: return "not allowed by its rules";
                case 3: return "network unreachable";
                case 4: return "host unreachable";
                case 5: return "connection refused";
                case 6: return "timed out";
                case 7: return "command not supported";
                case 8: return "address type not supported";
                default: return "error " + code;
            }
        }

        private static Task WriteAsync(Stream s, byte[] b) { return s.WriteAsync(b, 0, b.Length); }

        private static async Task<byte[]> ReadAsync(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = await s.ReadAsync(b, got, n - got).ConfigureAwait(false);
                if (r <= 0) throw new Socks5Exception("The SOCKS5 server closed the connection.");
                got += r;
            }
            return b;
        }
    }
}
