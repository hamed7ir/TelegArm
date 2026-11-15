using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace TelegArm.Core
{
    /// <summary>The newest TelegArm release on GitHub, as the update check reads it.</summary>
    public sealed class ReleaseInfo
    {
        public string Tag;            // "v2.1.0"
        public Version Version;       // 2.1.0
        public string PageUrl;        // the release page (html_url)
        public string Notes;          // the release notes (markdown)
        public string SetupName, SetupUrl;
        public long SetupSize;
        public string PortableName, PortableUrl;
        public long PortableSize;
        public string SumsUrl;        // SHA256SUMS.txt of the same release
    }

    /// <summary>
    /// Owner 2026-11-15: "check the repo for updates in background and warn user there is a update, and when user clicks it
    /// download the update and install." The PURE part of it (no network, no UI; linked into SelfTest): read GitHub's
    /// "latest release" answer, compare versions, find a file's checksum in SHA256SUMS.txt. Drafts and pre-releases are
    /// never offered (GitHub's /releases/latest skips them; a pre-release that reaches here anyway is refused). An asset
    /// is only taken from TelegArm's own release download address, so an answer that points elsewhere is ignored.
    /// </summary>
    public static class UpdateCheck
    {
        public const string Owner = "hamed7ir", Repo = "TelegArm";
        public const string LatestApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        public const string DownloadPrefix = "https://github.com/" + Owner + "/" + Repo + "/releases/download/";

        /// <summary>"v2.1.0", "2.1", "V2.1.0-beta.1" -> 2.1.0 (major.minor.build; a missing part is 0). Null when it is not a
        /// version at all.</summary>
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            var parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return null;
            var n = new int[3];
            for (int i = 0; i < parts.Length && i < 3; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
            return new Version(n[0], n[1], n[2]);
        }

        /// <summary>Is <paramref name="latest"/> newer than <paramref name="current"/> (major.minor.build only)?</summary>
        public static bool IsNewer(Version latest, Version current)
        {
            if (latest == null) return false;
            if (current == null) return true;
            var a = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            var b = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            return a > b;
        }

        /// <summary>Reads GitHub's release JSON. Null when it is not a usable release (a draft, a pre-release, no version in
        /// the tag). <paramref name="allowedPrefix"/>: the only address an asset may come from.</summary>
        public static ReleaseInfo Parse(string json, string allowedPrefix)
        {
            if (string.IsNullOrEmpty(json)) return null;
            JObject o;
            try { o = JObject.Parse(json); } catch { return null; }
            if ((bool?)o["draft"] == true || (bool?)o["prerelease"] == true) return null;
            var r = new ReleaseInfo
            {
                Tag = (string)o["tag_name"],
                PageUrl = (string)o["html_url"],
                Notes = (string)o["body"] ?? "",
            };
            r.Version = ParseVersion(r.Tag);
            if (r.Version == null) return null;
            var assets = o["assets"] as JArray;
            if (assets != null)
                foreach (var a in assets)
                {
                    string name = (string)a["name"] ?? "", url = (string)a["browser_download_url"] ?? "";
                    long size = (long?)a["size"] ?? 0;
                    if (string.IsNullOrEmpty(allowedPrefix) || !url.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("TelegArm-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    { r.SetupName = name; r.SetupUrl = url; r.SetupSize = size; }
                    else if (name.StartsWith("TelegArm-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase))
                    { r.PortableName = name; r.PortableUrl = url; r.PortableSize = size; }
                    else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        r.SumsUrl = url;
                }
            return r;
        }

        /// <summary>The SHA-256 (lower-case hex) SHA256SUMS.txt gives for <paramref name="fileName"/>, or null. Lines are
        /// "hash  name" or "hash *name"; anything else (a title, a "built from" line) is skipped.</summary>
        public static string SumFor(string sums, string fileName)
        {
            if (string.IsNullOrEmpty(sums) || string.IsNullOrEmpty(fileName)) return null;
            foreach (var raw in sums.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 66) continue;
                string hash = line.Substring(0, 64);
                if (!IsHex(hash) || (line[64] != ' ' && line[64] != '\t')) continue;
                string name = line.Substring(65).Trim().TrimStart('*').Trim();
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
            }
            return null;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }
}
