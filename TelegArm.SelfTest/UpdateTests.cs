using System;
using System.Collections.Generic;
using TelegArm.Core;

namespace TelegArm.SelfTest
{
    /// <summary>2026-11-15 — the update check's pure part (UpdateCheck): versions, GitHub's release JSON, SHA256SUMS.txt.</summary>
    internal static class UpdateTests
    {
        internal static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("updates: tags read as versions, only a NEWER major.minor.build is offered", Versions));
            t.Add(new KeyValuePair<string, Action>("updates: the release JSON gives the version and the files; drafts, pre-releases and foreign addresses are refused", Release));
            t.Add(new KeyValuePair<string, Action>("updates: SHA256SUMS.txt gives each file's checksum; other lines are skipped", Sums));
        }

        private static void Versions()
        {
            Check.True(UpdateCheck.ParseVersion("v2.1.0") == new Version(2, 1, 0), "v2.1.0 -> 2.1.0");
            Check.True(UpdateCheck.ParseVersion("2.1") == new Version(2, 1, 0), "2.1 -> 2.1.0");
            Check.True(UpdateCheck.ParseVersion("V3.0.1-beta.2") == new Version(3, 0, 1), "a suffix is cut");
            Check.True(UpdateCheck.ParseVersion("latest") == null && UpdateCheck.ParseVersion("") == null && UpdateCheck.ParseVersion(null) == null, "not a version -> null");
            Check.True(UpdateCheck.IsNewer(new Version(2, 1, 0), new Version(2, 0, 0)), "2.1.0 is newer than 2.0.0");
            Check.True(!UpdateCheck.IsNewer(new Version(2, 0, 0), new Version(2, 0, 0)), "the same version is not newer");
            Check.True(!UpdateCheck.IsNewer(new Version(1, 9, 0), new Version(2, 0, 0)), "an older one is not newer");
            Check.True(!UpdateCheck.IsNewer(new Version(2, 0, 0), new Version(2, 0, 0, 0)), "a revision part does not count");
            Check.True(!UpdateCheck.IsNewer(null, new Version(2, 0, 0)), "nothing is not newer");
        }

        private const string Prefix = "https://github.com/hamed7ir/TelegArm/releases/download/";

        private static string Json(string tag, bool draft, bool pre, string setupUrl)
        {
            return "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false")
                 + ",\"html_url\":\"https://github.com/hamed7ir/TelegArm/releases/tag/" + tag + "\",\"body\":\"What is new\",\"assets\":["
                 + "{\"name\":\"TelegArm-2.1.0-Setup.exe\",\"size\":45000000,\"browser_download_url\":\"" + setupUrl + "\"},"
                 + "{\"name\":\"TelegArm-2.1.0-Portable.zip\",\"size\":43000000,\"browser_download_url\":\"" + Prefix + "v2.1.0/TelegArm-2.1.0-Portable.zip\"},"
                 + "{\"name\":\"SHA256SUMS.txt\",\"size\":400,\"browser_download_url\":\"" + Prefix + "v2.1.0/SHA256SUMS.txt\"}]}";
        }

        private static void Release()
        {
            var r = UpdateCheck.Parse(Json("v2.1.0", false, false, Prefix + "v2.1.0/TelegArm-2.1.0-Setup.exe"), Prefix);
            Check.True(r != null && r.Version == new Version(2, 1, 0) && r.Tag == "v2.1.0", "the version from the tag");
            Check.True(r.SetupName == "TelegArm-2.1.0-Setup.exe" && r.SetupSize == 45000000 && r.SetupUrl.EndsWith("/TelegArm-2.1.0-Setup.exe"), "the installer");
            Check.True(r.PortableName == "TelegArm-2.1.0-Portable.zip" && r.SumsUrl.EndsWith("/SHA256SUMS.txt"), "the portable zip and the checksums");
            Check.True(r.PageUrl.EndsWith("/releases/tag/v2.1.0") && r.Notes == "What is new", "the page and the notes");
            Check.True(UpdateCheck.Parse(Json("v2.1.0", true, false, Prefix + "x"), Prefix) == null, "a draft is refused");
            Check.True(UpdateCheck.Parse(Json("v2.1.0", false, true, Prefix + "x"), Prefix) == null, "a pre-release is refused");
            Check.True(UpdateCheck.Parse(Json("nightly", false, false, Prefix + "x"), Prefix) == null, "a tag that is not a version is refused");
            var foreign = UpdateCheck.Parse(Json("v2.1.0", false, false, "https://example.com/TelegArm-2.1.0-Setup.exe"), Prefix);
            Check.True(foreign != null && foreign.SetupUrl == null, "an installer from another address is ignored");
            Check.True(UpdateCheck.Parse("not json", Prefix) == null && UpdateCheck.Parse("", Prefix) == null, "garbage -> null");
        }

        private static void Sums()
        {
            string sums = "TelegArm 2.1.0 - SHA256 checksums\nbuilt from commit abc1234 (clean tree)\n\n"
                        + "BAF5C9AF8A03A256236E2E51DB29C685B9D01D49F96F9266C02567C4F5C3A037  TelegArm-2.1.0-Setup.exe\r\n"
                        + "b29183e3439183f9b42401b675af8ded41da0a3ba557ecff68a73314a58619bb *TelegArm-2.1.0-Portable.zip\n"
                        + "Verify on Windows:\n    certutil -hashfile TelegArm-2.1.0-Setup.exe SHA256\n";
            Check.True(UpdateCheck.SumFor(sums, "TelegArm-2.1.0-Setup.exe") == "baf5c9af8a03a256236e2e51db29c685b9d01d49f96f9266c02567c4f5c3a037", "the installer's line (upper case read as lower)");
            Check.True(UpdateCheck.SumFor(sums, "telegarm-2.1.0-portable.zip") == "b29183e3439183f9b42401b675af8ded41da0a3ba557ecff68a73314a58619bb", "a '*name' line, any case");
            Check.True(UpdateCheck.SumFor(sums, "TelegArm-9.9.9-Setup.exe") == null && UpdateCheck.SumFor("", "x") == null, "missing -> null");
        }
    }
}
