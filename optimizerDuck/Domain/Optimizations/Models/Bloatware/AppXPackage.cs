using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using Wpf.Ui.Controls;

namespace optimizerDuck.Domain.Optimizations.Models.Bloatware;

/// <summary>
///     Specifies the risk level of removing a bloatware app.
/// </summary>
public enum AppRisk
{
    Safe,
    Caution,
    Unknown,
}

/// <summary>
///     Represents an AppX package (UWP app) that can be removed.
/// </summary>
/// <remarks>
///     Deriving from <see cref="LocalizedObject" /> makes <see cref="RiskVisual" /> re-resolve
///     when the UI language changes at runtime.
/// </remarks>
public partial class AppXPackage : LocalizedObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string? LogoImage { get; set; }

    public required string Name { get; init; }

    public required string PackageFullName { get; init; }

    public required string Publisher { get; init; }

    public required string Version { get; init; }

    public required string InstallLocation { get; init; }

    public AppRisk Risk { get; set; }

    public RiskVisual RiskVisual =>
        Risk switch
        {
            AppRisk.Safe => new RiskVisual
            {
                Display = Loc.Instance["Optimizer.UI.Risk.Safe"],
                Icon = SymbolRegular.ShieldCheckmark24,
            },
            AppRisk.Caution => new RiskVisual
            {
                Display = Loc.Instance["Optimizer.UI.Risk.Moderate"],
                Icon = SymbolRegular.Warning24,
            },
            _ => new RiskVisual
            {
                Display = Loc.Instance["Optimizer.UI.Risk.Safe"],
                Icon = SymbolRegular.ShieldCheckmark24,
            },
        };

    public bool ShouldVisibleRisk => Risk != AppRisk.Unknown;

    /// <summary>
    ///     The name without the store's publisher prefix ("5319275A.WhatsAppDesktop" shows as
    ///     "WhatsAppDesktop"). Microsoft's own packages have no such prefix.
    /// </summary>
    public string ShortName => PublisherPrefix().Replace(Name, string.Empty);

    /// <summary>
    ///     The organization or common name from the certificate subject, without the rest of it
    ///     ("CN=Adobe Inc., O=Adobe Inc., L=San Jose..." shows as "Adobe Inc."). A common name that
    ///     is only an identifier (a GUID) is left out.
    /// </summary>
    public string PublisherName
    {
        get
        {
            var organization = SubjectField(Publisher, "O");
            if (organization.Length > 0)
                return organization;
            var common = SubjectField(Publisher, "CN");
            return Guid.TryParse(common, out _) ? string.Empty : common;
        }
    }

    private static string SubjectField(string subject, string field)
    {
        foreach (Match m in SubjectPart().Matches(subject))
            if (m.Groups["k"].Value.Trim().Equals(field, StringComparison.OrdinalIgnoreCase))
                return m.Groups["v"].Value.Trim().Trim('"');
        return string.Empty;
    }

    [GeneratedRegex(@"(?<k>[A-Za-z0-9.]+)=(?<v>""[^""]*""|[^,]*)")]
    private static partial Regex SubjectPart();

    [GeneratedRegex(@"^[0-9A-F]{8}\.", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherPrefix();
}
