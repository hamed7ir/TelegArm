using System;
using System.Numerics;
using System.Security.Cryptography;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// The Diffie-Hellman of a Telegram call (as tdesktop's calls_call.cpp and MTProto's end-to-end rules): messages.getDhConfig
    /// gives g and a 2048-bit safe prime p; each side picks 2048 random bits (XORed with the server's random), sends
    /// g^x mod p (the caller first only its SHA-256), and both derive the 256-byte key g^(ab) mod p. The key fingerprint is
    /// SHA-1(key) bytes 12..19, little-endian. Pure (no client): SelfTest checks it. Numbers are big-endian on the wire.
    /// </summary>
    internal static class CallCrypto
    {
        /// <summary>Telegram's well-known 2048-bit safe prime (MTProto docs). The servers have sent this one for years: a match
        /// skips the slow primality proof. Anything else is checked in full (and remembered by the caller per config version).</summary>
        private const string KnownPrimeHex =
            "C71CAEB9C6B1C9048E6C522F70F13F73980D40238E3E21C14934D037563D930F48198A0AA7C14058229493D22530F4DBFA336F6E0AC925139543AED44CCE7C37" +
            "20FD51F69458705AC68CD4FE6B6B13ABDC9746512969328454F18FAF8C595F642477FE96BB2A941D5BCD1D4AC8CC49880708FA9B378E3C4F3A9060BEE67CF9A4" +
            "A4A695811051907E162753B56B0F6B410DBA74D8A84B2A14B3144E0EF1284754FD17ED950D5965B4B9DD46582DB1178D169C6BC465B0D6FF9CA3928FEF5B9AE4" +
            "E418FC15E83EBEA0F87FA9FF5EED70050DED2849F47BF959D956850CE929851F0D8115F635B105EE2E4E15D04B2454BF6F4FADF034B10403119CD8E3B92FCC5B";

        public const int KeySize = 256;

        public static BigInteger FromBigEndian(byte[] be)
        {
            var le = new byte[be.Length + 1];   // + a zero byte: always non-negative
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        /// <summary>v as exactly 256 big-endian bytes (left-padded with zeros).</summary>
        public static byte[] ToBigEndian256(BigInteger v)
        {
            var le = v.ToByteArray();
            var be = new byte[KeySize];
            for (int i = 0; i < KeySize && i < le.Length; i++) be[KeySize - 1 - i] = le[i];
            return be;
        }

        public static byte[] Sha256(byte[] data) { using (var h = SHA256.Create()) return h.ComputeHash(data); }

        /// <summary>SHA-1(key) bytes 12..19 as a little-endian 64-bit number (tdesktop ComputeFingerprint).</summary>
        public static long Fingerprint(byte[] key)
        {
            byte[] h;
            using (var s = SHA1.Create()) h = s.ComputeHash(key);
            ulong v = 0;
            for (int i = 19; i >= 12; i--) v = (v << 8) | h[i];
            return unchecked((long)v);
        }

        /// <summary>256 random bytes XORed with the server's random (as MTProto asks), as a positive number.</summary>
        public static BigInteger NewSecret(byte[] serverRandom)
        {
            var r = new byte[KeySize];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(r);
            if (serverRandom != null)
                for (int i = 0; i < KeySize && i < serverRandom.Length; i++) r[i] ^= serverRandom[i];
            return FromBigEndian(r);
        }

        /// <summary>1 &lt; x &lt; p-1 and 2^(2048-64) &lt;= x &lt;= p - 2^(2048-64): what MTProto requires of g_a and g_b, both ways.</summary>
        public static bool CheckPublic(BigInteger x, BigInteger p)
        {
            var bound = BigInteger.One << (2048 - 64);
            return x > BigInteger.One && x < p - BigInteger.One && x >= bound && x <= p - bound;
        }

        /// <summary>Is p a 2048-bit safe prime that g generates the right subgroup for (MTProto's checks)? The known prime
        /// is accepted at once; another is proved with Miller-Rabin on p and (p-1)/2.</summary>
        public static bool CheckPrime(byte[] pBigEndian, int g, int rounds, out string why)
        {
            why = null;
            var p = FromBigEndian(pBigEndian);
            var two2047 = BigInteger.One << 2047;
            if (pBigEndian.Length != KeySize || p <= two2047 || p >= (two2047 << 1)) { why = "p is not 2048 bits"; return false; }
            if (!GeneratorOk(p, g)) { why = "g=" + g + " does not fit p"; return false; }
            if (Hex(pBigEndian) == KnownPrimeHex) return true;
            if (!ProbablyPrime(p, rounds)) { why = "p is not prime"; return false; }
            if (!ProbablyPrime((p - 1) / 2, rounds)) { why = "(p-1)/2 is not prime"; return false; }
            return true;
        }

        /// <summary>g must generate a cyclic subgroup of prime order (p-1)/2 (MTProto: per g, a condition on p).</summary>
        private static bool GeneratorOk(BigInteger p, int g)
        {
            switch (g)
            {
                case 2: return p % 8 == 7;
                case 3: return p % 3 == 2;
                case 4: return true;
                case 5: { var m = p % 5; return m == 1 || m == 4; }
                case 6: { var m = p % 24; return m == 19 || m == 23; }
                case 7: { var m = p % 7; return m == 3 || m == 5 || m == 6; }
                default: return false;
            }
        }

        public static bool ProbablyPrime(BigInteger n, int rounds)
        {
            if (n < 2) return false;
            foreach (int small in new[] { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 })
            {
                if (n == small) return true;
                if (n % small == 0) return false;
            }
            var d = n - 1;
            int s = 0;
            while (d.IsEven) { d >>= 1; s++; }
            var bytes = new byte[n.ToByteArray().Length];
            using (var rng = new RNGCryptoServiceProvider())
                for (int i = 0; i < rounds; i++)
                {
                    BigInteger a;
                    do { rng.GetBytes(bytes); bytes[bytes.Length - 1] &= 0x7F; a = new BigInteger(bytes) % (n - 3) + 2; } while (a < 2);
                    var x = BigInteger.ModPow(a, d, n);
                    if (x == 1 || x == n - 1) continue;
                    bool composite = true;
                    for (int r = 1; r < s; r++)
                    {
                        x = BigInteger.ModPow(x, 2, n);
                        if (x == n - 1) { composite = false; break; }
                    }
                    if (composite) return false;
                }
            return true;
        }

        public static string Hex(byte[] b)
        {
            var c = new char[b.Length * 2];
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < b.Length; i++) { c[2 * i] = digits[b[i] >> 4]; c[2 * i + 1] = digits[b[i] & 15]; }
            return new string(c);
        }

        public static byte[] KnownPrime()
        {
            var b = new byte[KnownPrimeHex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(KnownPrimeHex.Substring(2 * i, 2), 16);
            return b;
        }
    }
}
