using System.Text.Json;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>Plan of improvements F3-10: DNS review, read-only.</summary>
public class DnsReviewTests
{
    [Theory]
    [InlineData("8.8.8.8", DnsKind.KnownPublic, "Google")]
    [InlineData("2001:4860:4860::8888", DnsKind.KnownPublic, "Google")]
    [InlineData("1.1.1.1", DnsKind.KnownPublic, "Cloudflare")]
    [InlineData("9.9.9.9", DnsKind.KnownPublic, "Quad9")]
    [InlineData("192.168.1.1", DnsKind.Private, null)]
    [InlineData("10.0.0.1", DnsKind.Private, null)]
    [InlineData("172.20.0.1", DnsKind.Private, null)]
    [InlineData("100.64.0.1", DnsKind.Private, null)] // CGNAT
    [InlineData("fe80::1%12", DnsKind.Private, null)]
    [InlineData("127.0.0.1", DnsKind.Local, null)]
    [InlineData("203.0.113.5", DnsKind.Unknown, null)] // documentation range
    [InlineData("not an ip", DnsKind.Unknown, null)]
    public void Servers_AreClassified(string address, DnsKind kind, string? provider)
    {
        Assert.Equal((kind, provider), KnownServices.ClassifyDns(address));
    }

    [Theory]
    [InlineData("8.8.8.8,1.1.1.1", new[] { "8.8.8.8", "1.1.1.1" })]
    [InlineData("8.8.8.8 1.1.1.1", new[] { "8.8.8.8", "1.1.1.1" })]
    [InlineData("", new string[0])] // empty = from the network
    [InlineData("garbage", new string[0])]
    public void NameServer_IsParsed(string value, string[] expected)
    {
        Assert.Equal(expected, NetworkProbes.ParseNameServer(value));
    }

    private static DiagnosticSnapshot With(params DnsServer[] servers) =>
        DiegoPc() with
        {
            Network = new NetworkSnapshot
            {
                DnsServers = Measured<IReadOnlyList<DnsServer>>.Of(servers),
            },
        };

    private static RuleResult Eval(DiagnosticSnapshot s) => new DnsReviewRule().Evaluate(s, Refs);

    [Fact]
    public void StaticUnknownServer_IsReviewed_AndTheAddressStaysLocal()
    {
        var result = Eval(
            With(
                new DnsServer("Wi-Fi", "203.0.113.5", true, DnsKind.Unknown, null),
                new DnsServer("Wi-Fi", "8.8.8.8", true, DnsKind.KnownPublic, "Google")
            )
        );

        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Equal(1, result.Finding.Evidence[0].Number);
        var address = Assert.Single(result.Finding.Evidence, e => e.Key == "network.dns_server");
        Assert.Equal("203.0.113.5", address.Text);
        Assert.True(address.LocalOnly);
    }

    [Fact]
    public void StaticLoopback_IsReviewedToo()
    {
        Assert.Equal(
            RuleOutcome.Finding,
            Eval(With(new DnsServer("Ethernet", "127.0.0.1", true, DnsKind.Local, null))).Outcome
        );
    }

    [Theory]
    [InlineData("203.0.113.5", false, DnsKind.Unknown)] // the provider's, from the network
    [InlineData("1.1.1.1", true, DnsKind.KnownPublic)] // set by hand, but well known
    [InlineData("192.168.0.1", true, DnsKind.Private)] // the router, set by hand
    public void Normal_Setups_Pass(string address, bool isStatic, DnsKind kind)
    {
        Assert.Equal(
            RuleOutcome.Pass,
            Eval(With(new DnsServer("Ethernet", address, isStatic, kind, null))).Outcome
        );
    }

    [Fact]
    public void Unread_IsNotMeasurable_NoAdapters_IsNotApplicable()
    {
        var unread = DiegoPc() with
        {
            Network = new NetworkSnapshot
            {
                DnsServers = Measured<IReadOnlyList<DnsServer>>.Missing(
                    NotMeasuredReason.QueryFailed
                ),
            },
        };
        Assert.Equal(RuleOutcome.NotMeasurable, Eval(unread).Outcome);
        Assert.Equal(RuleOutcome.NotApplicable, Eval(With()).Outcome);
    }
}

/// <summary>Plan of improvements F3-09: browser extensions and search engine, read-only.</summary>
public class BrowserReviewTests
{
    private const string ChromePreferences = """
        {
          "extensions": { "settings": {
            "store": { "location": 1, "from_webstore": true,
              "manifest": { "name": "uBlock Origin", "permissions": ["webRequest", "storage"], "host_permissions": ["<all_urls>"],
                "update_url": "https://clients2.google.com/service/update2/crx" } },
            "builtin": { "location": 5, "manifest": { "name": "Chrome PDF Viewer" } },
            "sideload": { "location": 4, "path": "C:\\adware\\ext", "manifest": { "name": "Cupones Gratis" } },
            "policy": { "location": 7, "manifest": { "name": "Search Helper", "update_url": "https://evil.example/crx" } },
            "off": { "location": 1, "from_webstore": false, "disable_reasons": [1], "manifest": { "name": "Old one" } },
            "localized": { "location": 1, "from_webstore": true, "path": "localized\\1.0", "manifest": { "name": "__MSG_appName__" } }
          } },
          "default_search_provider_data": { "template_url_data": { "url": "https://search.hijack.example/s?q={searchTerms}&src={inputEncoding}" } },
          "session": { "restore_on_startup": 4, "startup_urls": ["https://start.hijack.example/home", "chrome://newtab/"] },
          "homepage": "https://news.example.org/",
          "homepage_is_newtabpage": true
        }
        """;

    private static IReadOnlyList<BrowserExtension> ChromeExtensions() =>
        BrowserProbe.ParseChromiumExtensions(
            "Chrome (Default)",
            ChromePreferences,
            null,
            (id, path) => id == "localized" ? ("Traductor", (JsonElement?)null) : (null, null),
            new HashSet<string>()
        );

    [Fact]
    public void Chromium_ReadsOriginAndPermissions_AndSkipsBuiltIns()
    {
        var all = ChromeExtensions().ToDictionary(e => e.Id);

        Assert.DoesNotContain("builtin", all.Keys);
        Assert.True(all["store"].FromStore);
        Assert.Equal(["<all_urls>", "webRequest"], all["store"].BroadPermissions);
        Assert.True(all["sideload"].Unpacked);
        Assert.False(all["sideload"].FromStore);
        Assert.True(all["policy"].ForcedByPolicy);
        Assert.False(all["off"].Enabled);
        Assert.Equal("Traductor", all["localized"].Name); // __MSG_ resolved from the locales
    }

    [Fact]
    public void Chromium_PolicyForcelist_MarksTheExtension()
    {
        var forced = BrowserProbe
            .ParseChromiumExtensions(
                "Edge (Default)",
                ChromePreferences,
                null,
                (_, _) => (null, null),
                new HashSet<string> { "store" }
            )
            .Single(e => e.Id == "store");
        Assert.True(forced.ForcedByPolicy);
    }

    /// <summary>
    ///     Found on Diego's PC (19/11/2026): Edge and Brave keep records without a location for
    ///     things that are not installed, and Secure Preferences holds only some fields of an
    ///     extension. Read as whole entries, they became 3 false "outside the store" and 21 unnamed.
    /// </summary>
    [Fact]
    public void Chromium_LeftoverRecordsAreSkipped_AndBothFilesAreMergedFieldByField()
    {
        const string prefs = """
            { "extensions": { "settings": {
              "real": { "location": 1, "manifest": { "name": "Grammar", "update_url": "https://edge.microsoft.com/extensionwebstorebase/v1/crx" } },
              "stub": { "active_permissions": {}, "granted_permissions": {}, "needs_sync": true },
              "brave": { "active_bit": true, "allowlist": 1 }
            } } }
            """;
        const string secure = """
            { "extensions": { "settings": {
              "real": { "disable_reasons": [1] }
            } } }
            """;

        var real = Assert.Single(
            BrowserProbe.ParseChromiumExtensions(
                "Edge (Default)",
                prefs,
                secure,
                (_, _) => (null, null),
                new HashSet<string>()
            )
        );
        Assert.Equal("Grammar", real.Name); // the manifest from Preferences survived the merge
        Assert.True(real.FromStore);
        Assert.False(real.Enabled); // the disable reason from Secure Preferences too
    }

    [Fact]
    public void Chromium_Home_KeepsOnlyHosts()
    {
        var home = BrowserProbe.ParseChromiumHome(
            "Chrome (Default)",
            ChromePreferences,
            BrowserPolicy.None
        );

        Assert.Equal("search.hijack.example", home.SearchHost);
        Assert.False(home.SearchByPolicy);
        // chrome:// pages and the homepage (the new tab page is on) are left out; no paths.
        Assert.Equal(["start.hijack.example"], home.StartupHosts);
    }

    [Fact]
    public void Chromium_PolicySearch_WinsOverThePreferences()
    {
        var home = BrowserProbe.ParseChromiumHome(
            "Chrome (Default)",
            ChromePreferences,
            new BrowserPolicy("https://forced.example/?q={searchTerms}", [], new HashSet<string>())
        );
        Assert.Equal("forced.example", home.SearchHost);
        Assert.True(home.SearchByPolicy);
    }

    [Theory]
    [InlineData("{google:baseURL}search?q={searchTerms}", "google.com")]
    [InlineData("https://www.bing.com/search?q={searchTerms}", "www.bing.com")]
    [InlineData(null, null)]
    public void SearchTemplates_GiveTheirHost(string? template, string? host)
    {
        Assert.Equal(host, BrowserProbe.SearchHost(template));
    }

    [Theory]
    [InlineData("www.google.com.co", true)]
    [InlineData("google.es", true)]
    [InlineData("www.google.co.uk", true)]
    [InlineData("www.bing.com", true)]
    [InlineData("duckduckgo.com", true)]
    [InlineData("search.brave.com", true)]
    [InlineData("search.yahoo.com", true)]
    [InlineData("google.evil.com", false)]
    [InlineData("search.hijack.example", false)]
    [InlineData("notbing.com", false)]
    public void SearchHosts_AreKnownOrNot(string host, bool known)
    {
        Assert.Equal(known, KnownServices.IsKnownSearchHost(host));
    }

    [Fact]
    public void Firefox_SignedProfileAddonsAreFromTheStore_OthersAreNot()
    {
        const string json = """
            { "addons": [
              { "id": "ublock@raymondhill.net", "type": "extension", "location": "app-profile", "signedState": 2, "active": true,
                "defaultLocale": { "name": "uBlock Origin" },
                "userPermissions": { "permissions": ["webRequest"], "origins": ["<all_urls>"] } },
              { "id": "helper@x", "type": "extension", "location": "app-global", "signedState": 2, "active": true,
                "defaultLocale": { "name": "Ayudante de búsqueda" } },
              { "id": "formautofill@mozilla.org", "type": "extension", "location": "app-builtin", "active": true },
              { "id": "es@dictionaries", "type": "dictionary", "location": "app-profile", "active": true }
            ] }
            """;

        var all = BrowserProbe
            .ParseFirefoxExtensions("Firefox (abc.default)", json)
            .ToDictionary(e => e.Id);

        Assert.Equal(2, all.Count);
        Assert.True(all["ublock@raymondhill.net"].FromStore);
        Assert.Equal(["<all_urls>", "webRequest"], all["ublock@raymondhill.net"].BroadPermissions);
        Assert.False(all["helper@x"].FromStore); // installed by another program, outside the profile
    }

    [Fact]
    public void Firefox_Homepage_KeepsOnlyHosts()
    {
        const string prefs = """
            user_pref("browser.startup.homepage", "https://portal.example.net/inicio?id=7|about:home");
            """;
        Assert.Equal(["portal.example.net"], BrowserProbe.ParseFirefoxStartup(prefs));
    }

    // ---- The rules ----------------------------------------------------------------------------

    private static DiagnosticSnapshot With(
        IReadOnlyList<BrowserExtension>? extensions = null,
        IReadOnlyList<BrowserHome>? homes = null
    ) =>
        DiegoPc() with
        {
            Browsers = new BrowserSnapshot
            {
                Extensions = Measured<IReadOnlyList<BrowserExtension>>.Of(extensions ?? []),
                Homes = Measured<IReadOnlyList<BrowserHome>>.Of(homes ?? []),
            },
        };

    [Fact]
    public void ExtensionsOutsideTheStore_AreMedium_WithTheNamesKeptLocal()
    {
        var result = new BrowserExtensionsRule().Evaluate(With(ChromeExtensions()), Refs);

        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Equal(2, result.Finding.Evidence[0].Number); // sideload and policy; "off" is disabled
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "browser.extension_unpacked" && e.Text == "Cupones Gratis" && e.LocalOnly
        );
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "browser.extension_policy" && e.Text == "Search Helper"
        );
    }

    [Fact]
    public void StoreExtensionsWithWidePermissions_AreOnlyListed()
    {
        var store = ChromeExtensions().Where(e => e.Id == "store").ToList();
        var result = new BrowserExtensionsRule().Evaluate(With(store), Refs);
        Assert.Equal(Severity.Info, result.Finding!.Severity); // no points
    }

    [Fact]
    public void UnusualSearchEngine_IsMedium_WithItsStartupPages()
    {
        var home = BrowserProbe.ParseChromiumHome(
            "Chrome (Default)",
            ChromePreferences,
            BrowserPolicy.None
        );
        var result = new BrowserSearchRule().Evaluate(With(homes: [home]), Refs);

        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "browser.search" && e.Text == "search.hijack.example" && e.LocalOnly
        );
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "browser.startup" && e.Text == "start.hijack.example"
        );
    }

    [Fact]
    public void DefaultOrKnownSearch_Passes()
    {
        var homes = new[]
        {
            new BrowserHome("Edge (Default)", null, false, []),
            new BrowserHome("Chrome (Default)", "www.google.com.co", false, ["news.example.org"]),
        };
        Assert.Equal(
            RuleOutcome.Pass,
            new BrowserSearchRule().Evaluate(With(homes: homes), Refs).Outcome
        );
    }

    [Fact]
    public void LocalOnlyEvidence_NeverReachesTheAi()
    {
        var finding = new BrowserExtensionsRule().Evaluate(With(ChromeExtensions()), Refs).Finding!;

        var local = FindingTextRenderer.Render(finding);
        var ai = FindingTextRenderer.Render(finding, forAi: true);

        Assert.Contains(local.Evidence, line => line.Contains("Cupones Gratis"));
        Assert.DoesNotContain(ai.Evidence, line => line.Contains("Cupones Gratis"));
        Assert.Single(ai.Evidence); // only the count
    }
}

/// <summary>Plan of improvements F3-07: ReTrim only on fixed SSD volumes with a letter.</summary>
public class RetrimTests
{
    [Fact]
    public void OnlyFixedSsdLetters_ReachPowerShell()
    {
        var snapshot = SystemInfo.Unknown with
        {
            Storage = new StorageInfo
            {
                Volumes =
                [
                    new StorageVolume { DriveLetter = "C:", MediaType = StorageMediaType.Nvme },
                    new StorageVolume { DriveLetter = "D:", MediaType = StorageMediaType.Hdd },
                    new StorageVolume { DriveLetter = "E:\\", MediaType = StorageMediaType.Ssd },
                    new StorageVolume
                    {
                        DriveLetter = "F:",
                        MediaType = StorageMediaType.Ssd,
                        IsRemovable = true,
                    },
                    new StorageVolume { DriveLetter = "C:; rm", MediaType = StorageMediaType.Ssd },
                ],
            },
        };

        Assert.Equal(['C', 'E'], Storage.RetrimSsds.SsdLetters(snapshot));
    }
}
