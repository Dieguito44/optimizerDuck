using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Plan of improvements F3-10: the DNS servers of the active adapters, and whether each one
///     was typed by hand (the adapter's NameServer value) or came from the network (DHCP or
///     router advertisements). Read-only: nothing is changed, and HOSTS is not touched.
/// </summary>
internal static class NetworkProbes
{
    private const string Tcpip4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    public static Measured<IReadOnlyList<DnsServer>> ReadDns()
    {
        try
        {
            var servers = new List<DnsServer>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (
                    adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType
                        is NetworkInterfaceType.Loopback
                            or NetworkInterfaceType.Tunnel
                )
                    continue;

                var typed = StaticServers(adapter.Id);
                foreach (var address in adapter.GetIPProperties().DnsAddresses)
                {
                    if (IsWindowsPlaceholder(address))
                        continue;
                    var text = Normalize(address);
                    var (kind, provider) = KnownServices.ClassifyDns(text);
                    servers.Add(
                        new DnsServer(adapter.Name, text, typed.Contains(text), kind, provider)
                    );
                }
            }
            return Measured<IReadOnlyList<DnsServer>>.Of(servers);
        }
        catch (NetworkInformationException)
        {
            return Measured<IReadOnlyList<DnsServer>>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    /// <summary>The servers typed by hand on the adapter (IPv4 and IPv6), normalized.</summary>
    private static HashSet<string> StaticServers(string adapterId)
    {
        var typed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Tcpip4, Tcpip6 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{adapterId}");
                foreach (var server in ParseNameServer(key?.GetValue("NameServer") as string))
                    typed.Add(server);
            }
            catch (Exception ex)
                when (ex is UnauthorizedAccessException or global::System.Security.SecurityException
                ) { }
        }
        return typed;
    }

    /// <summary>NameServer lists servers separated by commas or spaces. Empty = from the network.</summary>
    internal static IEnumerable<string> ParseNameServer(string? value) =>
        (value ?? "")
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => IPAddress.TryParse(s, out var ip) ? Normalize(ip) : null)
            .OfType<string>();

    private static string Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        var text = ip.ToString();
        var scope = text.IndexOf('%');
        return scope >= 0 ? text[..scope] : text;
    }

    /// <summary>fec0:0:0:ffff::1-3: what Windows shows when no IPv6 DNS was configured.</summary>
    internal static bool IsWindowsPlaceholder(IPAddress ip) =>
        ip.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetworkV6
        && Normalize(ip) is "fec0:0:0:ffff::1" or "fec0:0:0:ffff::2" or "fec0:0:0:ffff::3";
}
