using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>Policies a browser is under (machine and the client's user): how hijackers force things in.</summary>
internal sealed record BrowserPolicy(
    string? SearchUrl,
    IReadOnlyList<string> StartupUrls,
    IReadOnlySet<string> ForcedExtensionIds
)
{
    public static readonly BrowserPolicy None = new(null, [], new HashSet<string>());
}

/// <summary>
///     Plan of improvements F3-09: extensions, search engine and startup pages of Chrome, Edge,
///     Brave and Firefox, for the person at the PC (also when the technician elevated with another
///     account). Read-only: files are opened for reading only, nothing is changed. Only host names
///     of pages are kept, never full addresses or the browsing history.
/// </summary>
internal static partial class BrowserProbe
{
    private static readonly (string Name, string Folder, string PolicyKey)[] Chromium =
    [
        ("Chrome", @"Google\Chrome\User Data", @"SOFTWARE\Policies\Google\Chrome"),
        ("Edge", @"Microsoft\Edge\User Data", @"SOFTWARE\Policies\Microsoft\Edge"),
        (
            "Brave",
            @"BraveSoftware\Brave-Browser\User Data",
            @"SOFTWARE\Policies\BraveSoftware\Brave"
        ),
    ];

    /// <summary>The update servers of the Chrome Web Store and Edge Add-ons.</summary>
    private static readonly string[] StoreUpdateHosts =
    [
        "clients2.google.com",
        "edge.microsoft.com",
    ];

    /// <summary>Permissions that reach every site or the browser itself. Shown, not judged.</summary>
    private static readonly HashSet<string> BroadPermissionNames = new(StringComparer.Ordinal)
    {
        "webRequest",
        "webRequestBlocking",
        "proxy",
        "debugger",
        "nativeMessaging",
        "management",
    };

    private static readonly HashSet<string> AllSites = new(StringComparer.Ordinal)
    {
        "<all_urls>",
        "*://*/*",
        "http://*/*",
        "https://*/*",
    };

    [GeneratedRegex(@"^Default$|^Profile \d+$")]
    private static partial Regex ChromiumProfile();

    public static (
        Measured<IReadOnlyList<BrowserExtension>> Extensions,
        Measured<IReadOnlyList<BrowserHome>> Homes
    ) Read()
    {
        var local = InteractiveUser.LocalAppDataFolder();
        var roaming = InteractiveUser.RoamingAppDataFolder();
        if (string.IsNullOrEmpty(local) && string.IsNullOrEmpty(roaming))
            return (
                Measured<IReadOnlyList<BrowserExtension>>.Missing(NotMeasuredReason.NotReported),
                Measured<IReadOnlyList<BrowserHome>>.Missing(NotMeasuredReason.NotReported)
            );

        var extensions = new List<BrowserExtension>();
        var homes = new List<BrowserHome>();

        foreach (var (name, folder, policyKey) in Chromium)
        {
            var data = Path.Combine(local, folder);
            if (string.IsNullOrEmpty(local) || !Directory.Exists(data))
                continue;
            var policy = ReadPolicy(policyKey);
            foreach (var profile in SafeDirectories(data))
            {
                var profileName = Path.GetFileName(profile);
                if (!ChromiumProfile().IsMatch(profileName))
                    continue;
                var prefs = TryReadText(Path.Combine(profile, "Preferences"));
                if (prefs is null)
                    continue;
                var secure = TryReadText(Path.Combine(profile, "Secure Preferences"));
                var label = $"{name} ({profileName})";
                try
                {
                    extensions.AddRange(
                        ParseChromiumExtensions(
                            label,
                            prefs,
                            secure,
                            (id, path) => ReadChromiumManifest(profile, id, path),
                            policy.ForcedExtensionIds
                        )
                    );
                    homes.Add(ParseChromiumHome(label, prefs, policy));
                }
                catch (JsonException) { }
            }
        }

        var firefox = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
        if (!string.IsNullOrEmpty(roaming) && Directory.Exists(firefox))
            foreach (var profile in SafeDirectories(firefox))
            {
                var label = $"Firefox ({Path.GetFileName(profile)})";
                try
                {
                    if (TryReadText(Path.Combine(profile, "extensions.json")) is { } json)
                        extensions.AddRange(ParseFirefoxExtensions(label, json));
                }
                catch (JsonException) { }
                if (TryReadText(Path.Combine(profile, "prefs.js")) is { } prefs)
                    // Firefox keeps its search engine in a compressed file: not read, so the
                    // search engine is "not measured" for Firefox and never flagged.
                    homes.Add(new BrowserHome(label, null, false, ParseFirefoxStartup(prefs)));
            }

        return (
            Measured<IReadOnlyList<BrowserExtension>>.Of(extensions),
            Measured<IReadOnlyList<BrowserHome>>.Of(homes)
        );
    }

    // ---- Chromium ----------------------------------------------------------------------------

    /// <summary>
    ///     Extensions from "extensions.settings" in Preferences and Secure Preferences. Chromium's
    ///     locations: 1 store/internal, 4 unpacked, 5 and 10 built in (skipped), 7 and 9 policy,
    ///     8 command line.
    /// </summary>
    internal static IReadOnlyList<BrowserExtension> ParseChromiumExtensions(
        string browser,
        string preferences,
        string? securePreferences,
        Func<string, string?, (string? Name, JsonElement? Manifest)> manifestReader,
        IReadOnlySet<string> forcedIds
    )
    {
        // The two files often hold different fields of the same extension (Secure Preferences
        // only the protected ones): merge them field by field, Secure Preferences winning.
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var json in new[] { preferences, securePreferences })
        {
            if (json is null)
                continue;
            using var doc = JsonDocument.Parse(json);
            if (
                !doc.RootElement.TryGetProperty("extensions", out var ext)
                || !ext.TryGetProperty("settings", out var all)
                || all.ValueKind != JsonValueKind.Object
            )
                continue;
            foreach (var item in all.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!merged.TryGetValue(item.Name, out var target))
                    merged[item.Name] = target = new JsonObject();
                foreach (var field in item.Value.EnumerateObject())
                    target[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
        }

        var result = new List<BrowserExtension>();
        foreach (var (id, node) in merged)
        {
            var s = JsonSerializer.SerializeToElement(node);
            // Every installed extension has a location. Entries without one are leftovers
            // (sync or permission records of something not installed): not extensions.
            if (Int(s, "location") is not { } location || location is 5 or 10)
                continue;

            JsonElement? manifest = s.TryGetProperty("manifest", out var m) ? m : null;
            string? name = manifest is { } mm ? Str(mm, "name") : null;
            if (
                manifest is null
                || name is null
                || name.StartsWith("__MSG_", StringComparison.Ordinal)
            )
            {
                var (readName, readManifest) = manifestReader(id, Str(s, "path"));
                manifest ??= readManifest;
                name = readName ?? name;
            }
            if (
                string.IsNullOrWhiteSpace(name)
                || name.StartsWith("__MSG_", StringComparison.Ordinal)
            )
                name = id;

            var updateUrl = manifest is { } u ? Str(u, "update_url") : null;
            var fromStore =
                (
                    s.TryGetProperty("from_webstore", out var fw)
                    && fw.ValueKind == JsonValueKind.True
                ) || (updateUrl is not null && StoreUpdateHosts.Contains(HostOf(updateUrl)));

            result.Add(
                new BrowserExtension(
                    browser,
                    id,
                    name.Trim(),
                    IsEnabled(s),
                    fromStore,
                    location is 7 or 9 || forcedIds.Contains(id),
                    location is 4 or 8,
                    manifest is { } p ? BroadPermissions(p) : []
                )
            );
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Disabled when it has disable reasons, or the old "state" says 0.</summary>
    private static bool IsEnabled(JsonElement s)
    {
        if (s.TryGetProperty("disable_reasons", out var reasons))
        {
            if (reasons.ValueKind == JsonValueKind.Array && reasons.GetArrayLength() > 0)
                return false;
            if (reasons.ValueKind == JsonValueKind.Number && reasons.GetInt64() != 0)
                return false;
        }
        return Int(s, "state") is not 0;
    }

    internal static IReadOnlyList<string> BroadPermissions(JsonElement manifest)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var list in new[] { "permissions", "host_permissions" })
            if (
                manifest.TryGetProperty(list, out var items)
                && items.ValueKind == JsonValueKind.Array
            )
                foreach (var item in items.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } p)
                    {
                        if (AllSites.Contains(p))
                            found.Add("<all_urls>");
                        else if (BroadPermissionNames.Contains(p))
                            found.Add(p);
                    }
        if (
            manifest.TryGetProperty("content_scripts", out var scripts)
            && scripts.ValueKind == JsonValueKind.Array
        )
            foreach (var script in scripts.EnumerateArray())
                if (
                    script.TryGetProperty("matches", out var matches)
                    && matches.ValueKind == JsonValueKind.Array
                )
                    if (
                        matches
                            .EnumerateArray()
                            .Any(x =>
                                x.ValueKind == JsonValueKind.String
                                && AllSites.Contains(x.GetString()!)
                            )
                    )
                        found.Add("<all_urls>");
        return found.ToList();
    }

    /// <summary>Search engine (policy first) and startup pages, as hosts only.</summary>
    internal static BrowserHome ParseChromiumHome(
        string browser,
        string preferences,
        BrowserPolicy policy
    )
    {
        using var doc = JsonDocument.Parse(preferences);
        var root = doc.RootElement;

        string? searchUrl = policy.SearchUrl;
        var byPolicy = searchUrl is not null;
        if (
            searchUrl is null
            && root.TryGetProperty("default_search_provider_data", out var dsp)
            && dsp.TryGetProperty("template_url_data", out var template)
        )
            searchUrl = Str(template, "url");

        var startup = new List<string>(policy.StartupUrls);
        if (root.TryGetProperty("session", out var session))
        {
            // 4 = "open a specific page or set of pages".
            if (
                Int(session, "restore_on_startup") == 4
                && session.TryGetProperty("startup_urls", out var urls)
                && urls.ValueKind == JsonValueKind.Array
            )
                startup.AddRange(
                    urls.EnumerateArray()
                        .Where(u => u.ValueKind == JsonValueKind.String)
                        .Select(u => u.GetString()!)
                );
        }
        if (
            !(
                root.TryGetProperty("homepage_is_newtabpage", out var ntp)
                && ntp.ValueKind == JsonValueKind.True
            ) && Str(root, "homepage") is { } homepage
        )
            startup.Add(homepage);

        return new BrowserHome(browser, SearchHost(searchUrl), byPolicy, Hosts(startup));
    }

    /// <summary>The host of a search template; Google's own placeholder counts as google.com.</summary>
    internal static string? SearchHost(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;
        if (template.StartsWith("{google:baseURL}", StringComparison.Ordinal))
            return "google.com";
        return HostOf(Regex.Replace(template, @"\{[^}]*\}", "x"));
    }

    /// <summary>Name and manifest of an installed extension, with its __MSG_ name resolved.</summary>
    private static (string? Name, JsonElement? Manifest) ReadChromiumManifest(
        string profile,
        string id,
        string? path
    )
    {
        try
        {
            // Store extensions: "id\version" relative to the profile's Extensions folder.
            // Unpacked ones: an absolute path.
            var dir =
                path is null ? null
                : Path.IsPathRooted(path) ? path
                : Path.Combine(profile, "Extensions", path);
            if (dir is null || !Directory.Exists(dir))
            {
                var versions = Path.Combine(profile, "Extensions", id);
                dir = Directory.Exists(versions)
                    ? SafeDirectories(versions).OrderDescending().FirstOrDefault()
                    : null;
            }
            if (dir is null || TryReadText(Path.Combine(dir, "manifest.json")) is not { } json)
                return (null, null);

            using var doc = JsonDocument.Parse(json);
            var manifest = doc.RootElement.Clone();
            var name = Str(manifest, "name");
            if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal))
                name = LocalizedName(dir, manifest, name);
            return (name, manifest);
        }
        catch (Exception ex)
            when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static string? LocalizedName(string dir, JsonElement manifest, string token)
    {
        var key = token[6..].TrimEnd('_');
        foreach (var locale in new[] { "es", "es_419", Str(manifest, "default_locale"), "en" })
        {
            if (
                locale is null
                || TryReadText(Path.Combine(dir, "_locales", locale, "messages.json"))
                    is not { } json
            )
                continue;
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (
                    string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase)
                    && Str(entry.Value, "message") is { } message
                )
                    return message;
        }
        return null;
    }

    /// <summary>Machine and client-user policies of a Chromium browser.</summary>
    private static BrowserPolicy ReadPolicy(string key)
    {
        string? search = null;
        var startup = new List<string>();
        var forced = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var open in new Func<RegistryKey?>[]
            {
                () => Registry.LocalMachine.OpenSubKey(key),
                () => InteractiveUser.OpenCurrentUserKey(key),
            }
        )
        {
            try
            {
                using var policy = open();
                if (policy is null)
                    continue;
                search ??= policy.GetValue("DefaultSearchProviderSearchURL") as string;
                startup.AddRange(Values(policy, "RestoreOnStartupURLs"));
                if (policy.GetValue("HomepageLocation") is string home)
                    startup.Add(home);
                foreach (var entry in Values(policy, "ExtensionInstallForcelist"))
                    forced.Add(entry.Split(';')[0].Trim());
            }
            catch (Exception ex)
                when (ex is UnauthorizedAccessException or global::System.Security.SecurityException
                ) { }
        }
        return new BrowserPolicy(search, startup, forced);
    }

    private static IEnumerable<string> Values(RegistryKey parent, string subKey)
    {
        using var key = parent.OpenSubKey(subKey);
        return key is null
            ? []
            : key.GetValueNames().Select(n => key.GetValue(n) as string).OfType<string>().ToList();
    }

    // ---- Firefox -----------------------------------------------------------------------------

    /// <summary>
    ///     extensions.json. From the store = installed in the profile and signed by Mozilla
    ///     (signedState 2 or more). Installed from outside the profile (another program put it
    ///     there) counts as outside the store. Built-in ones are skipped.
    /// </summary>
    internal static IReadOnlyList<BrowserExtension> ParseFirefoxExtensions(
        string browser,
        string json
    )
    {
        using var doc = JsonDocument.Parse(json);
        if (
            !doc.RootElement.TryGetProperty("addons", out var addons)
            || addons.ValueKind != JsonValueKind.Array
        )
            return [];

        var result = new List<BrowserExtension>();
        foreach (var addon in addons.EnumerateArray())
        {
            if (Str(addon, "type") != "extension")
                continue;
            var location = Str(addon, "location") ?? "";
            if (location is "app-builtin" or "app-system-defaults" or "app-system-addons")
                continue;

            var name = addon.TryGetProperty("defaultLocale", out var locale)
                ? Str(locale, "name")
                : null;
            var id = Str(addon, "id") ?? "?";
            var signed = Int(addon, "signedState") ?? 0;
            var active =
                addon.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;

            var broad = new SortedSet<string>(StringComparer.Ordinal);
            if (addon.TryGetProperty("userPermissions", out var perms))
            {
                if (
                    perms.TryGetProperty("origins", out var origins)
                    && origins.ValueKind == JsonValueKind.Array
                    && origins
                        .EnumerateArray()
                        .Any(o =>
                            o.ValueKind == JsonValueKind.String && AllSites.Contains(o.GetString()!)
                        )
                )
                    broad.Add("<all_urls>");
                if (
                    perms.TryGetProperty("permissions", out var list)
                    && list.ValueKind == JsonValueKind.Array
                )
                    foreach (var p in list.EnumerateArray())
                        if (
                            p.ValueKind == JsonValueKind.String
                            && BroadPermissionNames.Contains(p.GetString()!)
                        )
                            broad.Add(p.GetString()!);
            }

            result.Add(
                new BrowserExtension(
                    browser,
                    id,
                    string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                    active,
                    location == "app-profile" && signed >= 2,
                    false,
                    location == "app-temporary",
                    broad.ToList()
                )
            );
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [GeneratedRegex(@"user_pref\(""browser\.startup\.homepage""\s*,\s*""(?<v>[^""]*)""\)")]
    private static partial Regex FirefoxHomepage();

    /// <summary>The homepage hosts from prefs.js ("a|b" for several pages).</summary>
    internal static IReadOnlyList<string> ParseFirefoxStartup(string prefs) =>
        FirefoxHomepage().Match(prefs) is { Success: true } m
            ? Hosts(m.Groups["v"].Value.Split('|'))
            : [];

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Host names only, without browser pages (about:, chrome://, edge://).</summary>
    internal static IReadOnlyList<string> Hosts(IEnumerable<string> urls) =>
        urls.Select(HostOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static string? HostOf(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            if (!Uri.TryCreate("https://" + url.Trim(), UriKind.Absolute, out uri))
                return null;
        return uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i)
            ? i
            : null;

    private static string? TryReadText(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                return null;
            // The browser may be open: share everything, read only.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
