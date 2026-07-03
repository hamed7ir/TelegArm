using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TL;

namespace TelegArm.Core
{
    /// <summary>One country row for the picker: flag (derived from iso2) + name + dial code + phone pattern.</summary>
    public sealed class Country
    {
        public string Iso2;
        public string Name;
        public string DialCode;   // digits only, no '+'
        public string Pattern;    // e.g. "XXX XXX XX XX" (from Help_CountryCode.patterns); null = simple grouping

        /// <summary>Regional-indicator emoji string for this flag (→ EmojiRenderer, which has the Noto flags).</summary>
        public string FlagEmoji { get { return Countries.IsoToFlag(Iso2); } }
    }

    /// <summary>
    /// The country list for the login picker. Loads a bundled fallback instantly (so the picker works on
    /// first launch behind a VPN), then refreshes live from Help_GetCountriesList and caches the full list
    /// to AppData for next launch. Pure data + helpers; no UI.
    /// </summary>
    public static class Countries
    {
        private static List<Country> _all;
        private static readonly object _lock = new object();

        private static string CachePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TelegArm");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "countries.json");
            }
        }

        /// <summary>The current list (cached → else bundled fallback). Never null.</summary>
        public static List<Country> All
        {
            get { lock (_lock) { if (_all == null) _all = LoadCachedOrFallback(); return _all; } }
        }

        private static List<Country> LoadCachedOrFallback()
        {
            try
            {
                if (File.Exists(CachePath))
                {
                    var list = JsonConvert.DeserializeObject<List<Country>>(File.ReadAllText(CachePath));
                    if (list != null && list.Count > 0) { System.Diagnostics.Debug.WriteLine("[LOGIN] countries cache " + list.Count); return list; }
                }
            }
            catch { }
            System.Diagnostics.Debug.WriteLine("[LOGIN] countries fallback (bundled)");
            return ParseFallback();
        }

        /// <summary>Fetches the live list, caches it, and replaces <see cref="All"/> (best-effort, background-safe).</summary>
        public static async Task RefreshLiveAsync(TelegramService service)
        {
            if (service == null || service.Client == null) return;
            try
            {
                var res = await service.Client.Help_GetCountriesList("en", 0);
                var cl = res as Help_CountriesList;
                if (cl == null || cl.countries == null || cl.countries.Length == 0) return;
                var list = new List<Country>();
                foreach (var c in cl.countries)
                {
                    if ((c.flags & Help_Country.Flags.hidden) != 0) continue;
                    var cc = c.country_codes != null && c.country_codes.Length > 0 ? c.country_codes[0] : null;
                    if (cc == null || string.IsNullOrEmpty(cc.country_code)) continue;
                    list.Add(new Country
                    {
                        Iso2 = c.iso2,
                        Name = string.IsNullOrEmpty(c.default_name) ? c.iso2 : c.default_name,
                        DialCode = cc.country_code,
                        Pattern = cc.patterns != null && cc.patterns.Length > 0 ? cc.patterns[0] : null
                    });
                }
                if (list.Count == 0) return;
                list = list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                lock (_lock) { _all = list; }
                try { File.WriteAllText(CachePath, JsonConvert.SerializeObject(list)); } catch { }
                System.Diagnostics.Debug.WriteLine("[LOGIN] countries live " + list.Count + " (cached)");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[LOGIN] countries live failed: " + ex.Message); }
        }

        /// <summary>Best-match country for a typed number/dial code (longest matching dial code wins).</summary>
        public static Country MatchDial(string digits)
        {
            if (string.IsNullOrEmpty(digits)) return null;
            digits = new string(digits.Where(char.IsDigit).ToArray());
            Country best = null;
            foreach (var c in All)
                if (digits.StartsWith(c.DialCode, StringComparison.Ordinal) &&
                    (best == null || c.DialCode.Length > best.DialCode.Length))
                    best = c;
            return best;
        }

        /// <summary>"US" → 🇺🇸 (two regional-indicator code points). null for a bad iso2.</summary>
        public static string IsoToFlag(string iso2)
        {
            if (string.IsNullOrEmpty(iso2) || iso2.Length != 2) return null;
            iso2 = iso2.ToUpperInvariant();
            char a = iso2[0], b = iso2[1];
            if (a < 'A' || a > 'Z' || b < 'A' || b > 'Z') return null;
            return char.ConvertFromUtf32(0x1F1E6 + (a - 'A')) + char.ConvertFromUtf32(0x1F1E6 + (b - 'A'));
        }

        /// <summary>Formats national digits per a Telegram pattern ("XXX XXX XX XX"), else groups in 3s/4s.</summary>
        public static string FormatNational(string digits, string pattern)
        {
            if (string.IsNullOrEmpty(digits)) return "";
            digits = new string(digits.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(pattern))
            {
                var sb = new System.Text.StringBuilder();
                int di = 0;
                foreach (char p in pattern)
                {
                    if (di >= digits.Length) break;
                    if (p == ' ' || p == '-') { sb.Append(' '); continue; }
                    sb.Append(digits[di++]);
                }
                for (; di < digits.Length; di++) sb.Append(digits[di]);   // overflow past the pattern
                return sb.ToString();
            }
            // No pattern → group in 3s.
            var g = new System.Text.StringBuilder();
            for (int i = 0; i < digits.Length; i++) { if (i > 0 && i % 3 == 0) g.Append(' '); g.Append(digits[i]); }
            return g.ToString();
        }

        // Bundled fallback (common countries; the live fetch replaces this with the full list + patterns).
        // Format: "iso2|Name|dial" per line.
        private const string Fallback =
            "AF|Afghanistan|93;AL|Albania|355;DZ|Algeria|213;AR|Argentina|54;AM|Armenia|374;AU|Australia|61;" +
            "AT|Austria|43;AZ|Azerbaijan|994;BH|Bahrain|973;BD|Bangladesh|880;BY|Belarus|375;BE|Belgium|32;" +
            "BO|Bolivia|591;BR|Brazil|55;BG|Bulgaria|359;KH|Cambodia|855;CA|Canada|1;CL|Chile|56;CN|China|86;" +
            "CO|Colombia|57;CR|Costa Rica|506;HR|Croatia|385;CU|Cuba|53;CY|Cyprus|357;CZ|Czechia|420;" +
            "DK|Denmark|45;EC|Ecuador|593;EG|Egypt|20;EE|Estonia|372;ET|Ethiopia|251;FI|Finland|358;" +
            "FR|France|33;GE|Georgia|995;DE|Germany|49;GH|Ghana|233;GR|Greece|30;GT|Guatemala|502;" +
            "HK|Hong Kong|852;HU|Hungary|36;IS|Iceland|354;IN|India|91;ID|Indonesia|62;IR|Iran|98;IQ|Iraq|964;" +
            "IE|Ireland|353;IL|Israel|972;IT|Italy|39;JP|Japan|81;JO|Jordan|962;KZ|Kazakhstan|7;KE|Kenya|254;" +
            "KW|Kuwait|965;KG|Kyrgyzstan|996;LV|Latvia|371;LB|Lebanon|961;LY|Libya|218;LT|Lithuania|370;" +
            "LU|Luxembourg|352;MY|Malaysia|60;MX|Mexico|52;MD|Moldova|373;MA|Morocco|212;NP|Nepal|977;" +
            "NL|Netherlands|31;NZ|New Zealand|64;NG|Nigeria|234;NO|Norway|47;OM|Oman|968;PK|Pakistan|92;" +
            "PS|Palestine|970;PA|Panama|507;PY|Paraguay|595;PE|Peru|51;PH|Philippines|63;PL|Poland|48;" +
            "PT|Portugal|351;QA|Qatar|974;RO|Romania|40;RU|Russia|7;SA|Saudi Arabia|966;RS|Serbia|381;" +
            "SG|Singapore|65;SK|Slovakia|421;SI|Slovenia|386;ZA|South Africa|27;KR|South Korea|82;ES|Spain|34;" +
            "LK|Sri Lanka|94;SE|Sweden|46;CH|Switzerland|41;SY|Syria|963;TW|Taiwan|886;TJ|Tajikistan|992;" +
            "TZ|Tanzania|255;TH|Thailand|66;TN|Tunisia|216;TR|Turkey|90;TM|Turkmenistan|993;UA|Ukraine|380;" +
            "AE|United Arab Emirates|971;GB|United Kingdom|44;US|United States|1;UY|Uruguay|598;UZ|Uzbekistan|998;" +
            "VE|Venezuela|58;VN|Vietnam|84;YE|Yemen|967;ZM|Zambia|260;ZW|Zimbabwe|263";

        private static List<Country> ParseFallback()
        {
            var list = new List<Country>();
            foreach (var row in Fallback.Split(';'))
            {
                var p = row.Split('|');
                if (p.Length == 3) list.Add(new Country { Iso2 = p[0], Name = p[1], DialCode = p[2], Pattern = null });
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
