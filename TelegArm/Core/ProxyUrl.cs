using System;
using System.Text;

namespace TelegArm.Core
{
    /// <summary>
    /// BATCH-TA-16/P1 — parse, VALIDATE and normalise an MTProxy link before it can reach
    /// WTelegramClient. Everything here mirrors the library's own rules, read from its source at
    /// <c>D:\repo\WTelegramClient @ telegarm/shipped-4.4.6</c>, <c>src/Client.cs:880-896</c>.
    ///
    /// WHY VALIDATE AT ALL, when WTC parses the URL itself:
    /// WTC does <c>MTProxyUrl[MTProxyUrl.IndexOf('?')..]</c> — with no '?' that index is -1 and the range
    /// expression THROWS. It then does <c>int.Parse(parms["port"])</c>, which throws on a missing or
    /// non-numeric port, and finally <c>throw new ArgumentException("Invalid/unsupported secret")</c> on a
    /// bad secret. All three happen INSIDE DoConnectAsync, i.e. a typo would surface as a mysterious
    /// connect failure minutes later rather than as a message next to the box the user just pasted into.
    /// So we reject at paste, using the same rules, and only ever hand WTC something it can parse.
    ///
    /// WHAT WTC ACCEPTS (verbatim behaviour, not a guess):
    ///   · Only the query string after the FIRST '?' is read — scheme/host/path are never inspected, so
    ///     tg://proxy?… and https://t.me/proxy?… are identical to it. We still normalise to tg:// for
    ///     display and de-duplication.
    ///   · secret may be HEX or URL-safe BASE64 (it maps '_'→'/', '-'→'+' and re-pads).
    ///   · decoded secret: 16 bytes plain · 17 bytes starting 0xDD (padded) · >= 21 bytes starting 0xEE
    ///     (FakeTLS, remainder is the SNI host). Anything else is rejected by the library.
    ///
    /// ⚠⚠ THE SECRET NEVER REACHES A LOG. ⚠⚠
    /// A proxy link is a credential: anyone holding it can use that proxy, and a log file is the thing
    /// users paste into chats when asking for help. Every diagnostic in this app must therefore log
    /// <see cref="SafeForLog"/> (host:port only) and NEVER the raw link, the secret, or an exception
    /// message that might contain either. That applies to Logger.Diag, crash.log, [WTC] tee lines we
    /// author, and any future dump. If you add a log line on a proxy path, use SafeForLog.
    /// </summary>
    public static class ProxyUrl
    {
        private const string HexChars = "0123456789ABCDEFabcdef";

        /// <summary>Validates and normalises an MTProxy link. Returns false with a user-facing
        /// <paramref name="error"/> that is safe to display (it never quotes the secret).</summary>
        public static bool TryNormalize(string input, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(input)) { error = "Paste a proxy link first."; return false; }
            string s = input.Trim();

            int q = s.IndexOf('?');
            if (q < 0)
            {
                // WTC would throw here (range on IndexOf == -1), inside ConnectAsync.
                error = "That doesn't look like a proxy link — it has no \"?\" and no settings after it.";
                return false;
            }

            string server = null, port = null, secret = null;
            foreach (var pair in s.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = pair.Substring(0, eq);
                string v = pair.Substring(eq + 1);
                try { v = Uri.UnescapeDataString(v); } catch { }
                if (k.Equals("server", StringComparison.OrdinalIgnoreCase)) server = v;
                else if (k.Equals("port", StringComparison.OrdinalIgnoreCase)) port = v;
                else if (k.Equals("secret", StringComparison.OrdinalIgnoreCase)) secret = v;
            }

            if (string.IsNullOrWhiteSpace(server)) { error = "The link has no server address."; return false; }
            if (server.IndexOf(' ') >= 0) { error = "The server address contains a space."; return false; }

            int portNum;
            if (string.IsNullOrWhiteSpace(port)) { error = "The link has no port."; return false; }
            if (!int.TryParse(port, out portNum) || portNum < 1 || portNum > 65535)
            {
                // WTC would throw here (int.Parse), inside ConnectAsync.
                error = "The port must be a number between 1 and 65535.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(secret)) { error = "The link has no secret."; return false; }
            string secretError;
            if (DecodeSecret(secret, out secretError) == null) { error = secretError; return false; }

            normalized = "tg://proxy?server=" + server + "&port=" + portNum + "&secret=" + secret;
            return true;
        }

        /// <summary>Decodes a secret exactly the way WTC does, and applies the same length/prefix rules.
        /// Returns null with a reason on failure. The reason NEVER contains the secret itself.</summary>
        public static byte[] DecodeSecret(string str, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(str)) { error = "The link has no secret."; return null; }

            byte[] bytes = null;
            bool looksHex = true;
            foreach (char c in str) if (HexChars.IndexOf(c) < 0) { looksHex = false; break; }

            if (looksHex)
            {
                if ((str.Length & 1) != 0) { error = "The secret has an odd number of hex characters."; return null; }
                bytes = new byte[str.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    int hi = HexVal(str[i * 2]), lo = HexVal(str[i * 2 + 1]);
                    if (hi < 0 || lo < 0) { error = "The secret isn't valid hex."; return null; }
                    bytes[i] = (byte)((hi << 4) | lo);
                }
            }
            else
            {
                // Same URL-safe base64 handling as WTC: '_'->'/', '-'->'+', then re-pad to a multiple of 4.
                try
                {
                    string b = str.Replace('_', '/').Replace('-', '+');
                    bytes = Convert.FromBase64String(b + new string('=', (4 - (b.Length % 4)) % 4));
                }
                catch { error = "The secret isn't valid hex or base64."; return null; }
            }

            // WTC's rules, in WTC's order (src/Client.cs:891-895).
            bool tls = bytes.Length >= 21 && bytes[0] == 0xEE;
            if (tls) return bytes;                                    // FakeTLS: secret[1..17] + SNI host
            if (bytes.Length == 17 && bytes[0] == 0xDD) return bytes; // padded ("dd") form
            if (bytes.Length == 16) return bytes;                     // plain 32-hex

            if (bytes.Length == 17 && bytes[0] != 0xDD)
                error = "That 17-byte secret doesn't start with \"dd\", so Telegram can't use it.";
            else if (bytes[0] == 0xEE)
                error = "That looks like a TLS secret but it's too short (it needs at least 21 bytes).";
            else
                error = "The secret is " + bytes.Length + " bytes; it must be 16, or 17 starting with \"dd\", "
                      + "or 21+ starting with \"ee\".";
            return null;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>⚠ THE ONLY FORM OF A PROXY LINK THAT MAY BE LOGGED: "host:port". Never the secret.
        /// Returns "(none)" for null/blank and "(unparsable)" rather than echoing something unexpected —
        /// echoing the input would defeat the whole point of this method.</summary>
        public static string SafeForLog(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(none)";
            string host = HostOf(url), port = PortOf(url);
            if (host == null) return "(unparsable)";
            return port == null ? host : host + ":" + port;
        }

        /// <summary>BATCH-TA-18 — is this URL a link to an MTProto PROXY, whatever form it arrived in?
        /// SHAPE ONLY: it answers "should this be intercepted instead of shelled out to a browser", and
        /// deliberately does NOT parse the settings. The moment the answer is yes, the WHOLE url goes to
        /// <see cref="TryNormalize"/> — the same call the paste box makes — so there is exactly ONE parser
        /// and a link that pastes fine can never behave differently when it is clicked.
        ///
        /// The three forms Telegram itself emits (tg://proxy, t.me/proxy, telegram.me/proxy) all mean the
        /// same thing; telegram.dog is included because <c>MainForm.ParseTgLink</c> already treats it as a
        /// Telegram host in the same reserved-segment list this mirrors.
        /// ⚠ "socks" IS DELIBERATELY NOT HERE. t.me/socks?… is a SOCKS5 proxy, which this app cannot use —
        /// WTC's <c>MTProxyUrl</c> is MTProto-only. Intercepting one would mean offering to connect through
        /// something that cannot work, so socks links keep falling through to the browser unchanged.</summary>
        public static bool IsProxyLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string s = url.Trim();
            if (s.StartsWith("tg://proxy", StringComparison.OrdinalIgnoreCase)) return true;

            // Message links are often scheme-less ("t.me/proxy?…"), exactly as MainForm.NormalizeUrl handles.
            bool hasScheme = System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:");
            Uri uri;
            if (!Uri.TryCreate(hasScheme ? s : "https://" + s, UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (host != "t.me" && host != "telegram.me" && host != "telegram.dog") return false;
            return uri.AbsolutePath.Trim('/').Equals("proxy", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Server host of a normalised link, or null.</summary>
        public static string HostOf(string url) { return Field(url, "server"); }

        /// <summary>The raw secret of a link, or null. ⚠ FOR ON-SCREEN DISPLAY ONLY — the confirmation
        /// sheet shows it so a user can compare it against the channel post they tapped. It must NEVER be
        /// passed to a logger; see the rule in this class's remarks.</summary>
        public static string SecretOf(string url) { return Field(url, "secret"); }

        /// <summary>Port of a normalised link as text, or null.</summary>
        public static string PortOf(string url) { return Field(url, "port"); }

        /// <summary>Port as an int, or 0 when absent/invalid.</summary>
        public static int PortNumberOf(string url)
        {
            int p;
            return int.TryParse(PortOf(url) ?? "", out p) ? p : 0;
        }

        private static string Field(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            int q = url.IndexOf('?');
            if (q < 0) return null;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (!pair.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = pair.Substring(eq + 1);
                try { v = Uri.UnescapeDataString(v); } catch { }
                return v.Length == 0 ? null : v;
            }
            return null;
        }

        /// <summary>A short label for the UI list: "MTPROTO host:port". Secret-free by construction.</summary>
        public static string DisplayLabel(string url)
        {
            var sb = new StringBuilder("MTPROTO ");
            sb.Append(SafeForLog(url));
            return sb.ToString();
        }
    }
}
