using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>
///     R-DNS-REVIEW (plan of improvements F3-10): a DNS server typed by hand that is neither a
///     known public resolver nor the local network. Malware that hijacks DNS writes exactly that
///     (static servers on the adapter). DNS given by the network (router, provider) is never
///     flagged, and an unknown server is "review", not "malicious": a company or a VPN may set it.
///     Read-only: Zentra does not change DNS.
/// </summary>
public sealed class DnsReviewRule : IDiagnosticRule
{
    public string RuleId => RuleIds.DnsReview;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.DnsReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var dns = snapshot.Network.DnsServers;
        if (!dns.TryGet(out var servers))
            return RuleResult.NotMeasurable(RuleId, dns.Reason, "network.dns");
        if (servers.Count == 0)
            return RuleResult.NotApplicable(RuleId);

        var review = servers
            .Where(s => s.IsStatic && s.Kind is DnsKind.Unknown or DnsKind.Local)
            .ToList();
        if (review.Count == 0)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact> { new("network.dns_static_review", review.Count) };
        evidence.AddRange(
            review.Select(s => new EvidenceFact(
                "network.dns_server",
                Text: s.Address,
                Subject: s.Adapter,
                LocalOnly: true
            ))
        );
        return RuleResult.Found(
            new Finding(RuleId, Severity.Medium, evidence, [ActionIds.DnsReview])
        );
    }
}

/// <summary>
///     R-BROWSER-EXT (plan of improvements F3-09): enabled extensions that did not come from the
///     Chrome Web Store, Edge Add-ons or Mozilla, that a policy forces in, or that were loaded
///     unpacked: the usual way adware gets in (Medium). Store extensions with wide permissions
///     (every site, intercepting requests) are listed for the technician without points (Info):
///     many good ones need them, such as ad blockers. The technician removes them by hand.
/// </summary>
public sealed class BrowserExtensionsRule : IDiagnosticRule
{
    public string RuleId => RuleIds.BrowserExtensions;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BrowserReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var measured = snapshot.Browsers.Extensions;
        if (!measured.TryGet(out var extensions))
            return RuleResult.NotMeasurable(RuleId, measured.Reason, "browser.extensions");

        var enabled = extensions.Where(e => e.Enabled).ToList();
        if (enabled.Count == 0)
            return RuleResult.Pass(RuleId);

        var outside = enabled.Where(e => !e.FromStore || e.ForcedByPolicy || e.Unpacked).ToList();
        if (outside.Count > 0)
        {
            var evidence = new List<EvidenceFact>
            {
                new("browser.extensions_outside_store", outside.Count),
            };
            evidence.AddRange(
                outside.Select(e => new EvidenceFact(
                    Origin(e),
                    Text: e.Name,
                    Subject: e.Browser,
                    LocalOnly: true
                ))
            );
            return RuleResult.Found(
                new Finding(RuleId, Severity.Medium, evidence, [ActionIds.BrowserReview])
            );
        }

        var broad = enabled.Where(e => e.BroadPermissions.Count > 0).ToList();
        if (broad.Count == 0)
            return RuleResult.Pass(RuleId);

        var info = new List<EvidenceFact> { new("browser.extensions_broad", broad.Count) };
        info.AddRange(
            broad.Select(e => new EvidenceFact(
                "browser.extension_broad",
                Text: $"{e.Name}: {string.Join(", ", e.BroadPermissions)}",
                Subject: e.Browser,
                LocalOnly: true
            ))
        );
        return RuleResult.Found(
            new Finding(RuleId, Severity.Info, info, [ActionIds.BrowserReview])
        );
    }

    /// <summary>The evidence key says where the extension came from.</summary>
    private static string Origin(BrowserExtension e) =>
        e.ForcedByPolicy ? "browser.extension_policy"
        : e.Unpacked ? "browser.extension_unpacked"
        : "browser.extension_outside_store";
}

/// <summary>
///     R-BROWSER-SEARCH (plan of improvements F3-09): a browser whose default search engine is not
///     one of the usual ones, the classic browser hijack. The startup pages go with it as evidence,
///     only as host names. Read-only: the technician fixes it in the browser's settings.
/// </summary>
public sealed class BrowserSearchRule : IDiagnosticRule
{
    public string RuleId => RuleIds.BrowserSearch;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BrowserReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var measured = snapshot.Browsers.Homes;
        if (!measured.TryGet(out var homes))
            return RuleResult.NotMeasurable(RuleId, measured.Reason, "browser.search");
        if (homes.Count == 0)
            return RuleResult.NotApplicable(RuleId);

        var hijacked = homes
            .Where(h => h.SearchHost is { } host && !KnownServices.IsKnownSearchHost(host))
            .ToList();
        if (hijacked.Count == 0)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact> { new("browser.search_unknown", hijacked.Count) };
        foreach (var home in hijacked)
        {
            evidence.Add(
                new(
                    home.SearchByPolicy ? "browser.search_policy" : "browser.search",
                    Text: home.SearchHost,
                    Subject: home.Browser,
                    LocalOnly: true
                )
            );
            evidence.AddRange(
                home.StartupHosts.Select(h => new EvidenceFact(
                    "browser.startup",
                    Text: h,
                    Subject: home.Browser,
                    LocalOnly: true
                ))
            );
        }
        return RuleResult.Found(
            new Finding(RuleId, Severity.Medium, evidence, [ActionIds.BrowserReview])
        );
    }
}
