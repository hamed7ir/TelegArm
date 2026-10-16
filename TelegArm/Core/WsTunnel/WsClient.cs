using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TelegArm.Core
{
    /// <summary>A WebSocket upgrade the server refused. <see cref="Status"/> is the HTTP status (0 = no valid response);
    /// a redirect (301/302/307/308) is how the gateway says "not this DC through here".</summary>
    public sealed class WsHandshakeException : IOException
    {
        public readonly int Status;
        public WsHandshakeException(int status, string message) : base(message) { Status = status; }
        public bool IsRedirect { get { return Status == 301 || Status == 302 || Status == 303 || Status == 307 || Status == 308; } }
    }

    /// <summary>
    /// Step 6.3 — a minimal RFC 6455 client for Telegram's WebSocket gateway, over TLS 1.2 to a PINNED IPv4 address.
    ///
    /// The request is what tg-ws-proxy sends (proxy/raw_websocket.py RawWebSocket.connect): GET /apiws with
    /// Sec-WebSocket-Protocol: binary and NO Origin header. The TLS name is the kws host, so the address can be pinned
    /// (DNS for the kws names is what a censor poisons) while the certificate is still checked against the real name.
    ///
    /// ⚠ CERTIFICATES: a name mismatch is never accepted. A CHAIN error is accepted only with the user's opt-in
    ///   (AppSettings.WsTunnelAcceptChainErrors, decision D6: old RT devices have stale root stores) and only for
    ///   *.web.telegram.org, and every such acceptance is logged.
    /// ⚠ WRITES: the uplink pump sends data while the downlink pump answers pings and closes; SslStream allows ONE write
    ///   in flight, so every frame goes through <see cref="_write"/>.
    /// ⚠ The handshake reader keeps whatever arrived after the blank line: the server may send its first frame in the
    ///   same TCP segment as the 101 response, and dropping those bytes would desynchronise the stream.
    /// </summary>
    public sealed class WsClient : IDisposable
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        /// <summary>A frame longer than this is treated as a broken stream (MTProto packets are far smaller).</summary>
        public const int MaxFrame = 16 * 1024 * 1024;
        private const int MaxHeaderBytes = 16 * 1024;

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        private readonly TcpClient _tcp;
        private readonly Stream _s;
        private readonly SemaphoreSlim _write = new SemaphoreSlim(1, 1);
        private readonly byte[] _sendBuf = new byte[64 * 1024 + 14];
        private readonly byte[] _txMask = new byte[4], _rxMask = new byte[4], _hdr = new byte[8];
        private byte[] _pending;           // bytes read past the end of the handshake response
        private int _pendingPos, _pendingLen;
        private long _frameLeft, _frameOffset;
        private bool _frameMasked;
        private int _disposed;

        /// <summary>Why the server closed ("1000 normal", …), or null.</summary>
        public string CloseReason { get; private set; }

        private WsClient(TcpClient tcp, Stream s) { _tcp = tcp; _s = s; }

        /// <summary>TCP to <paramref name="ip"/>:443, TLS 1.2 as <paramref name="host"/>, then the upgrade — all within
        /// <paramref name="timeoutMs"/>. Throws <see cref="WsHandshakeException"/> when the server answers but refuses,
        /// <see cref="TimeoutException"/>, or the socket/TLS exception.</summary>
        public static async Task<WsClient> ConnectAsync(string ip, string host, string path, bool acceptChainErrors, int timeoutMs)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            var work = ConnectCoreAsync(tcp, ip, host, path, acceptChainErrors);
            Task first;
            try { first = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false); }
            catch { first = work; }
            if (first != work)
            {
                try { tcp.Close(); } catch { }
                // it may still complete a moment later: then nobody owns that client, so close it
                var orphan = work.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else { var _ = t.Exception; } },
                                  TaskContinuationOptions.ExecuteSynchronously);
                throw new TimeoutException("WebSocket connect timed out after " + timeoutMs + " ms");
            }
            try { return await work.ConfigureAwait(false); }
            catch { try { tcp.Close(); } catch { } throw; }
        }

        private static async Task<WsClient> ConnectCoreAsync(TcpClient tcp, string ip, string host, string path, bool acceptChainErrors)
        {
            await tcp.ConnectAsync(IPAddress.Parse(ip), 443).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) => Validate(host, errors, acceptChainErrors));
            try
            {
                await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, false).ConfigureAwait(false);
                var ws = new WsClient(tcp, ssl);
                await ws.HandshakeAsync(host, path).ConfigureAwait(false);
                return ws;
            }
            catch
            {
                try { ssl.Dispose(); } catch { }   // the TLS context, not only the socket (review: it waited for the finalizer)
                throw;
            }
        }

        /// <summary>The upgrade over an already-open stream (no TCP, no TLS) — for the SelfTest's local server.</summary>
        internal static async Task<WsClient> OverStreamAsync(Stream s, string host, string path)
        {
            var ws = new WsClient(null, s);
            await ws.HandshakeAsync(host, path).ConfigureAwait(false);
            return ws;
        }

        private static bool Validate(string host, SslPolicyErrors errors, bool acceptChainErrors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (errors == SslPolicyErrors.RemoteCertificateChainErrors && acceptChainErrors
                && host.EndsWith(".web.telegram.org", StringComparison.OrdinalIgnoreCase))
            {
                TelegArm.Helpers.Logger.Diag("[WSTUN] " + host + ": certificate chain error ACCEPTED (user opt-in)");
                return true;
            }
            TelegArm.Helpers.Logger.Diag("[WSTUN] " + host + ": certificate rejected (" + errors + ")");
            return false;
        }

        // ── handshake ────────────────────────────────────────────────────────────────────────────────────────────

        private async Task HandshakeAsync(string host, string path)
        {
            var keyBytes = new byte[16];
            Rng.GetBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);
            string req = "GET " + path + " HTTP/1.1\r\n"
                       + "Host: " + host + "\r\n"
                       + "Upgrade: websocket\r\n"
                       + "Connection: Upgrade\r\n"
                       + "Sec-WebSocket-Key: " + key + "\r\n"
                       + "Sec-WebSocket-Version: 13\r\n"
                       + "Sec-WebSocket-Protocol: binary\r\n"
                       + "\r\n";
            var rb = Encoding.ASCII.GetBytes(req);
            await _s.WriteAsync(rb, 0, rb.Length).ConfigureAwait(false);

            var buf = new byte[4096];
            int len = 0, end;
            while ((end = HeaderEnd(buf, len)) < 0)
            {
                if (len == buf.Length)
                {
                    if (buf.Length >= MaxHeaderBytes) throw new WsHandshakeException(0, "response headers too long");
                    var nb = new byte[buf.Length * 2]; Buffer.BlockCopy(buf, 0, nb, 0, len); buf = nb;
                }
                int n = await _s.ReadAsync(buf, len, buf.Length - len).ConfigureAwait(false);
                if (n <= 0) throw new WsHandshakeException(0, "connection closed during the upgrade");
                len += n;
            }
            int rest = len - (end + 4);
            if (rest > 0) { _pending = new byte[rest]; Buffer.BlockCopy(buf, end + 4, _pending, 0, rest); _pendingLen = rest; }

            string[] lines = Encoding.ASCII.GetString(buf, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            int status = 0;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out status))
                throw new WsHandshakeException(0, "not an HTTP response");
            if (status != 101) throw new WsHandshakeException(status, "upgrade refused: " + lines[0]);

            string accept = null;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0 && lines[i].Substring(0, c).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                    accept = lines[i].Substring(c + 1).Trim();
            }
            string expected;
            using (var sha = SHA1.Create()) expected = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            if (accept != expected) throw new WsHandshakeException(101, "bad Sec-WebSocket-Accept");
        }

        private static int HeaderEnd(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i - 3;
            return -1;
        }

        // ── receive ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reads payload bytes of the incoming binary message(s) as one byte stream — message boundaries do not
        /// matter to MTProto on the way down. Answers pings itself. Returns 0 when the server closes.</summary>
        public async Task<int> ReadAsync(byte[] dst, int index, int count)
        {
            while (true)
            {
                if (_frameLeft > 0)
                {
                    int n = await RawReadAsync(dst, index, (int)Math.Min(count, _frameLeft)).ConfigureAwait(false);
                    if (n <= 0) return 0;
                    if (_frameMasked) for (int i = 0; i < n; i++) dst[index + i] ^= _rxMask[(_frameOffset + i) & 3];
                    _frameLeft -= n;
                    _frameOffset += n;
                    return n;
                }

                int h = await RawReadAsync(_hdr, 0, 2).ConfigureAwait(false);
                if (h <= 0) return 0;
                if (h == 1) await RawReadExactAsync(_hdr, 1, 1).ConfigureAwait(false);
                bool fin = (_hdr[0] & 0x80) != 0;
                int op = _hdr[0] & 0x0F;
                bool masked = (_hdr[1] & 0x80) != 0;
                long len = _hdr[1] & 0x7F;
                if ((_hdr[0] & 0x70) != 0) throw new IOException("WebSocket frame with reserved bits set");
                if (len == 126)
                {
                    await RawReadExactAsync(_hdr, 0, 2).ConfigureAwait(false);
                    len = _hdr[0] << 8 | _hdr[1];
                }
                else if (len == 127)
                {
                    await RawReadExactAsync(_hdr, 0, 8).ConfigureAwait(false);
                    len = 0;
                    for (int i = 0; i < 8; i++) len = len << 8 | _hdr[i];
                    if (len < 0) throw new IOException("WebSocket frame length out of range");
                }
                if (masked) await RawReadExactAsync(_rxMask, 0, 4).ConfigureAwait(false);

                if (op >= 8)   // control frame: short and never fragmented
                {
                    if (len > 125 || !fin) throw new IOException("bad WebSocket control frame");
                    var payload = new byte[len];
                    await RawReadExactAsync(payload, 0, (int)len).ConfigureAwait(false);
                    if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= _rxMask[i & 3];
                    if (op == 0x9) await SendFrameAsync(0xA, payload, 0, payload.Length).ConfigureAwait(false);   // ping → pong
                    else if (op == 0x8)
                    {
                        int code = payload.Length >= 2 ? payload[0] << 8 | payload[1] : 0;
                        CloseReason = code + (payload.Length > 2 ? " " + Encoding.UTF8.GetString(payload, 2, payload.Length - 2) : "");
                        // echo the status code; a 1-byte payload is malformed, so it gets an empty close back
                        try { await SendFrameAsync(0x8, payload, 0, payload.Length >= 2 ? 2 : 0).ConfigureAwait(false); } catch { }
                        return 0;
                    }
                    continue;   // pong (or an unknown control opcode): nothing to deliver
                }
                // text (1) is read as data, as tg-ws-proxy does; the gateway sends binary, but a text frame must not kill the
                // connection. Reserved opcodes 3-7 fail it (RFC 6455 §5.2).
                if (op > 0x2) throw new IOException("unexpected WebSocket opcode " + op);
                if (len > MaxFrame) throw new IOException("WebSocket frame too large (" + len + " bytes)");
                _frameLeft = len;
                _frameOffset = 0;
                _frameMasked = masked;
            }
        }

        private async Task<int> RawReadAsync(byte[] b, int o, int c)
        {
            if (_pendingPos < _pendingLen)
            {
                int n = Math.Min(c, _pendingLen - _pendingPos);
                Buffer.BlockCopy(_pending, _pendingPos, b, o, n);
                _pendingPos += n;
                if (_pendingPos == _pendingLen) { _pending = null; _pendingPos = _pendingLen = 0; }
                return n;
            }
            return await _s.ReadAsync(b, o, c).ConfigureAwait(false);
        }

        private async Task RawReadExactAsync(byte[] b, int o, int c)
        {
            while (c > 0)
            {
                int n = await RawReadAsync(b, o, c).ConfigureAwait(false);
                if (n <= 0) throw new EndOfStreamException("WebSocket closed mid-frame");
                o += n; c -= n;
            }
        }

        // ── send ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One binary message (one MTProto packet), masked as a client must.</summary>
        public Task SendBinaryAsync(byte[] b, int o, int c) { return SendFrameAsync(0x2, b, o, c); }

        private async Task SendFrameAsync(int op, byte[] b, int o, int c)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                int h = 0;
                _sendBuf[h++] = (byte)(0x80 | op);
                if (c < 126) _sendBuf[h++] = (byte)(0x80 | c);
                else if (c <= 0xFFFF) { _sendBuf[h++] = 0x80 | 126; _sendBuf[h++] = (byte)(c >> 8); _sendBuf[h++] = (byte)c; }
                else
                {
                    _sendBuf[h++] = 0x80 | 127;
                    for (int i = 7; i >= 0; i--) _sendBuf[h++] = i >= 4 ? (byte)0 : (byte)(c >> (8 * i));
                }
                Rng.GetBytes(_txMask);
                Buffer.BlockCopy(_txMask, 0, _sendBuf, h, 4);
                h += 4;
                int done = 0;
                while (true)
                {
                    int n = Math.Min(_sendBuf.Length - h, c - done);
                    for (int i = 0; i < n; i++) _sendBuf[h + i] = (byte)(b[o + done + i] ^ _txMask[(done + i) & 3]);
                    await _s.WriteAsync(_sendBuf, 0, h + n).ConfigureAwait(false);
                    done += n;
                    h = 0;
                    if (done >= c) break;
                }
            }
            finally { _write.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _s.Dispose(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
        }

        private static void Observe(Task t)
        {
            t.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
