using System.Net;
using System.Net.Sockets;

namespace optimizerDuck.Domain.Diagnostics.Reference;

/// <summary>
///     Public DNS resolvers and search engines that are widely used and documented (plan of
///     improvements F3-09 and F3-10). Not being in these lists does not mean bad: it means the
///     technician should look. Lists like this go out of date (Sophia 8.0 dropped one), so they
///     only decide "known" versus "review", never "malicious".
/// </summary>
public static class KnownServices
{
    /// <summary>Addresses from each provider's own setup page.</summary>
    private static readonly Dictionary<string, string> DnsByAddress = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Google Public DNS: developers.google.com/speed/public-dns/docs/using
        ["8.8.8.8"] = "Google",
        ["8.8.4.4"] = "Google",
        ["2001:4860:4860::8888"] = "Google",
        ["2001:4860:4860::8844"] = "Google",
        // Cloudflare 1.1.1.1 (and 1.1.1.1 for Families): developers.cloudflare.com/1.1.1.1/ip-addresses/
        ["1.1.1.1"] = "Cloudflare",
        ["1.0.0.1"] = "Cloudflare",
        ["1.1.1.2"] = "Cloudflare",
        ["1.0.0.2"] = "Cloudflare",
        ["1.1.1.3"] = "Cloudflare",
        ["1.0.0.3"] = "Cloudflare",
        ["2606:4700:4700::1111"] = "Cloudflare",
        ["2606:4700:4700::1001"] = "Cloudflare",
        ["2606:4700:4700::1112"] = "Cloudflare",
        ["2606:4700:4700::1002"] = "Cloudflare",
        ["2606:4700:4700::1113"] = "Cloudflare",
        ["2606:4700:4700::1003"] = "Cloudflare",
        // Quad9: quad9.net/service/service-addresses-and-features
        ["9.9.9.9"] = "Quad9",
        ["149.112.112.112"] = "Quad9",
        ["9.9.9.10"] = "Quad9",
        ["149.112.112.10"] = "Quad9",
        ["9.9.9.11"] = "Quad9",
        ["149.112.112.11"] = "Quad9",
        ["2620:fe::fe"] = "Quad9",
        ["2620:fe::9"] = "Quad9",
        ["2620:fe::10"] = "Quad9",
        ["2620:fe::fe:10"] = "Quad9",
        ["2620:fe::11"] = "Quad9",
        ["2620:fe::fe:11"] = "Quad9",
        // OpenDNS (Cisco): opendns.com/setupguide
        ["208.67.222.222"] = "OpenDNS",
        ["208.67.220.220"] = "OpenDNS",
        ["208.67.222.123"] = "OpenDNS",
        ["208.67.220.123"] = "OpenDNS",
        ["2620:119:35::35"] = "OpenDNS",
        ["2620:119:53::53"] = "OpenDNS",
        // AdGuard DNS: adguard-dns.io/en/public-dns.html
        ["94.140.14.14"] = "AdGuard",
        ["94.140.15.15"] = "AdGuard",
        ["94.140.14.15"] = "AdGuard",
        ["94.140.15.16"] = "AdGuard",
        ["94.140.14.140"] = "AdGuard",
        ["94.140.14.141"] = "AdGuard",
        ["2a10:50c0::ad1:ff"] = "AdGuard",
        ["2a10:50c0::ad2:ff"] = "AdGuard",
        // CleanBrowsing: cleanbrowsing.org/filters
        ["185.228.168.9"] = "CleanBrowsing",
        ["185.228.169.9"] = "CleanBrowsing",
        ["185.228.168.10"] = "CleanBrowsing",
        ["185.228.169.11"] = "CleanBrowsing",
        ["185.228.168.168"] = "CleanBrowsing",
        ["185.228.169.168"] = "CleanBrowsing",
    };

    /// <summary>Registrable domains of the usual search engines. Google has one per country.</summary>
    private static readonly string[] SearchDomains =
    [
        "bing.com",
        "duckduckgo.com",
        "yahoo.com",
        "ecosia.org",
        "search.brave.com",
        "startpage.com",
        "qwant.com",
        "yandex.com",
        "yandex.ru",
        "baidu.com",
        "naver.com",
        "ask.com",
    ];

    /// <summary>Where a DNS server is: this PC, the local network, a known provider, or elsewhere.</summary>
    public static (DnsKind Kind, string? Provider) ClassifyDns(string address)
    {
        if (!IPAddress.TryParse(address.Split('%')[0], out var ip))
            return (DnsKind.Unknown, null);
        if (IPAddress.IsLoopback(ip))
            return (DnsKind.Local, null);
        if (IsPrivate(ip))
            return (DnsKind.Private, null);
        return DnsByAddress.TryGetValue(ip.ToString(), out var provider)
            ? (DnsKind.KnownPublic, provider)
            : (DnsKind.Unknown, null);
    }

    /// <summary>RFC 1918, CGNAT (100.64/10), link-local and IPv6 unique-local and site-local.</summary>
    internal static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
                return IsPrivate(ip.MapToIPv4());
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC; // fc00::/7
        }
        var v4 = ip.GetAddressBytes();
        return v4[0] == 10
            || (v4[0] == 172 && v4[1] is >= 16 and <= 31)
            || (v4[0] == 192 && v4[1] == 168)
            || (v4[0] == 169 && v4[1] == 254)
            || (v4[0] == 100 && v4[1] is >= 64 and <= 127);
    }

    /// <summary>True for the usual search engines (any Google country domain included).</summary>
    public static bool IsKnownSearchHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        var labels = host.Split('.');
        // google.com, google.com.co, google.es, google.co.uk: "google" plus one or two short
        // country/generic labels. google.evil.com is not Google.
        if (
            labels.Length is 2 or 3
            && labels[0] == "google"
            && labels.Skip(1).All(l => l.Length is 2 or 3)
        )
            return true;
        return SearchDomains.Any(d =>
            host == d || host.EndsWith("." + d, StringComparison.Ordinal)
        );
    }
}
