using System;
using System.Security.Cryptography;
using System.Text;

namespace TelegArm.Helpers
{
    /// <summary>
    /// Lightweight credential hardening:
    ///  • The embedded Telegram api_id/api_hash are stored XOR-obfuscated so a plain
    ///    <c>strings</c> scan of the binary won't reveal them; they are decoded at runtime.
    ///  • Persisted user data (the phone number) is encrypted at rest with Windows DPAPI,
    ///    bound to the current Windows user, so the file can't be lifted to another account.
    /// NOTE: client-side obfuscation only raises the bar — credentials that must ship inside
    /// an app can never be truly secret. The WTelegram session file itself is already
    /// AES-encrypted by WTelegramClient. DPAPI/CryptProtectData is available on all Windows
    /// editions including ARM (RT 8.1 / Win10 ARM32); if it is ever unavailable the helpers
    /// degrade gracefully to plaintext rather than throwing.
    /// </summary>
    public static class Security
    {
        // Rotating XOR pad used for the embedded API credentials.
        private static readonly byte[] Pad = { 0x5A, 0x3C, 0x71, 0x19, 0xA4, 0xE2, 0x0F, 0x8B, 0x6D, 0x24, 0xC3, 0x97 };

        /// <summary>Decodes an XOR-obfuscated ASCII credential back to its string form.</summary>
        public static string Decode(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            for (int i = 0; i < data.Length; i++) sb.Append((char)(data[i] ^ Pad[i % Pad.Length]));
            return sb.ToString();
        }

        // ── DPAPI for persisted user data ───────────────────────────────────
        private const string Tag = "DPAPI:";

        /// <summary>Encrypts a string for storage (CurrentUser scope). Falls back to plaintext if DPAPI fails.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Tag + Convert.ToBase64String(bytes);
            }
            catch { return plain; }
        }

        /// <summary>Decrypts a stored string. Untagged input is treated as legacy plaintext.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(Tag, StringComparison.Ordinal)) return stored;   // legacy plaintext
            try
            {
                var bytes = Convert.FromBase64String(stored.Substring(Tag.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }
    }
}
