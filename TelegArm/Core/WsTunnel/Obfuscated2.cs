using System;
using System.Security.Cryptography;

namespace TelegArm.Core
{
    /// <summary>
    /// Step 6.3 — reads WTelegramClient's own obfuscated2 stream, without changing a byte of it.
    ///
    /// WHY NOTHING IS RE-ENCRYPTED: on the direct path WTC already speaks obfuscated2 with NO proxy secret — a 64-byte
    /// init, then AES-256-CTR (WTC src/Encryption.cs:346 InitObfuscation(secret: null, 0xEE, dcId), called from
    /// src/Client.cs:976). That is exactly what Telegram's WebSocket gateway expects as its first message (tg-ws-proxy
    /// generates the same secret-less init and sends it as the first frame, proxy/tg_ws_proxy.py _generate_relay_init).
    /// tg-ws-proxy re-encrypts only because ITS clients talk to it with a proxy secret; ours do not. So the relay
    /// forwards the init verbatim and only needs to READ two things: the DC in the init, and each packet's length.
    ///
    /// THE INIT (core.telegram.org/mtproto/mtproto-transports#transport-obfuscation): the client's encryption key is
    /// bytes 8..40 and its IV bytes 40..56 AS SENT; bytes 56..64 are encrypted with that keystream from position 0 and
    /// hold the protocol tag (4 bytes) and the DC as a little-endian int16 — negative for a media-only connection.
    /// </summary>
    public static class Obfuscated2
    {
        public const int InitLength = 64;
        public const uint TagAbridged = 0xEFEFEFEF, TagIntermediate = 0xEEEEEEEE, TagPadded = 0xDDDDDDDD;

        /// <summary>The keystream of the CLIENT→SERVER direction of a connection that began with <paramref name="init"/>.
        /// Its position 0 is the init's first byte, so the first byte after the init is at offset 64.</summary>
        public static CtrKeystream UplinkKeystream(byte[] init)
        {
            if (init == null || init.Length < InitLength) throw new ArgumentException("init");
            var key = new byte[32]; Buffer.BlockCopy(init, 8, key, 0, 32);
            var iv = new byte[16]; Buffer.BlockCopy(init, 40, iv, 0, 16);
            return new CtrKeystream(key, iv);
        }

        /// <summary>Decrypts the init's tail. <paramref name="dcIdx"/> is signed: negative = media. False when the tag is
        /// not one of the three MTProto transports (then this is not an obfuscated2 stream we understand).</summary>
        public static bool TryParseInit(byte[] init, CtrKeystream uplink, out int dcIdx, out uint tag)
        {
            var tail = new byte[8];
            for (int i = 0; i < 8; i++) tail[i] = (byte)(init[56 + i] ^ uplink.At(56 + i));
            tag = (uint)(tail[0] | tail[1] << 8 | tail[2] << 16 | tail[3] << 24);
            dcIdx = (short)(tail[4] | tail[5] << 8);
            return tag == TagIntermediate || tag == TagPadded || tag == TagAbridged;
        }
    }

    /// <summary>AES-256-CTR keystream readable at ANY offset: block n = AES-ECB(key, iv + n), the counter a 128-bit
    /// big-endian number (the same increment as WTC's AesCtr, src/Encryption.cs:336). Seekable on purpose: the splitter
    /// needs only the 4 keystream bytes under each packet's length, not the megabytes of payload between them.</summary>
    public sealed class CtrKeystream : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _iv, _ctr = new byte[16], _block = new byte[16];
        private long _blockIndex = -1;

        public CtrKeystream(byte[] key, byte[] iv)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _ecb = _aes.CreateEncryptor(key, null);
            _iv = (byte[])iv.Clone();
        }

        /// <summary>The keystream byte at stream position <paramref name="offset"/>.</summary>
        public byte At(long offset)
        {
            long b = offset >> 4;
            if (b != _blockIndex) Load(b);
            return _block[offset & 15];
        }

        /// <summary>XORs the keystream from stream position <paramref name="offset"/> into the buffer (encrypt = decrypt).</summary>
        public void Xor(long offset, byte[] buf, int index, int count)
        {
            for (int i = 0; i < count; i++) buf[index + i] ^= At(offset + i);
        }

        private void Load(long b)
        {
            Buffer.BlockCopy(_iv, 0, _ctr, 0, 16);
            ulong add = (ulong)b;
            int carry = 0;
            for (int i = 15; i >= 0; i--)
            {
                int sum = _ctr[i] + (int)(add & 0xFF) + carry;
                _ctr[i] = (byte)sum;
                carry = sum >> 8;
                add >>= 8;
            }
            _ecb.TransformBlock(_ctr, 0, 16, _block, 0);
            _blockIndex = b;
        }

        public void Dispose()
        {
            try { _ecb.Dispose(); } catch { }
            try { _aes.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Cuts WTC's outgoing byte stream into whole MTProto transport packets, so each goes out as ONE WebSocket message —
    /// what Telegram Web does and what tg-ws-proxy's MsgSplitter does (proxy/bridge.py). The bytes are not changed.
    /// Intermediate and padded-intermediate framing: a 4-byte little-endian length (top bit = quick-ack request), then
    /// that many bytes. A zero or absurd length — or the abridged transport, which WTC never uses — switches the
    /// splitter off for the rest of the connection: everything is then forwarded as it arrives (tg-ws-proxy's fallback).
    /// Usage: Append, then Next until it returns false (each result is valid until the next Append), then Compact.
    /// </summary>
    public sealed class PacketSplitter
    {
        public const int MaxPacket = 16 * 1024 * 1024;
        private const int StartSize = 64 * 1024;

        private readonly CtrKeystream _ks;
        private byte[] _buf = new byte[StartSize];
        private int _len, _pos;
        private int _end = -1;   // end of the current packet in _buf; -1 = its length is not read yet
        private long _abs;       // stream position of _buf[0]

        /// <param name="ks">The uplink keystream (<see cref="Obfuscated2.UplinkKeystream"/>).</param>
        /// <param name="streamOffset">Stream position of the first byte that will be appended (64: right after the init).</param>
        public PacketSplitter(CtrKeystream ks, long streamOffset, uint tag)
        {
            _ks = ks;
            _abs = streamOffset;
            Disabled = ks == null || (tag != Obfuscated2.TagIntermediate && tag != Obfuscated2.TagPadded);
        }

        /// <summary>True once packets are no longer cut (unknown transport, or a length that cannot be right).</summary>
        public bool Disabled { get; private set; }

        /// <summary>The buffer the indexes from <see cref="Next"/> point into.</summary>
        public byte[] Data { get { return _buf; } }

        public void Append(byte[] src, int index, int count)
        {
            if (count <= 0) return;
            if (_len + count > _buf.Length)
            {
                int size = _buf.Length;
                while (size < _len + count) size *= 2;
                var nb = new byte[size];
                Buffer.BlockCopy(_buf, 0, nb, 0, _len);
                _buf = nb;
            }
            Buffer.BlockCopy(src, index, _buf, _len, count);
            _len += count;
        }

        /// <summary>The next whole packet in <see cref="Data"/> — or, once disabled, whatever is buffered.</summary>
        public bool Next(out int index, out int count)
        {
            index = _pos; count = 0;
            if (_pos >= _len) return false;
            if (Disabled) { count = _len - _pos; _pos = _len; return true; }
            if (_end < 0)
            {
                if (_len - _pos < 4) return false;
                uint n = 0;
                for (int i = 0; i < 4; i++) n |= (uint)(_buf[_pos + i] ^ _ks.At(_abs + _pos + i)) << (8 * i);
                n &= 0x7FFFFFFF;
                if (n == 0 || n > MaxPacket)
                {
                    Disabled = true;
                    count = _len - _pos; _pos = _len;
                    return true;
                }
                _end = _pos + 4 + (int)n;
            }
            if (_len < _end) return false;
            count = _end - _pos;
            _pos = _end;
            _end = -1;
            return true;
        }

        /// <summary>Drops what <see cref="Next"/> handed out; a buffer grown for a big upload part shrinks back once empty.</summary>
        public void Compact()
        {
            if (_pos == 0) return;
            int rest = _len - _pos;
            if (rest > 0) Buffer.BlockCopy(_buf, _pos, _buf, 0, rest);
            _abs += _pos;
            if (_end >= 0) _end -= _pos;
            _len = rest;
            _pos = 0;
            if (_len == 0 && _end < 0 && _buf.Length > 1024 * 1024) _buf = new byte[StartSize];
        }
    }
}
