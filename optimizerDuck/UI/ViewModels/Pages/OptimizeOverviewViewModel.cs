using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.System;
using optimizerDuck.UI.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>One category of settings shown as a pack: how many are applied, and one button for the safe ones.</summary>
public sealed partial class OptimizePackItem : ObservableObject
{
    public required IOptimizationCategory Category { get; init; }
    public required Type PageType { get; init; }
    public required string Key { get; init; }
    public required SymbolRegular Icon { get; init; }

    public string Name => Category.Name;
    public string Description => Loc.Instance[$"Optimize.Pack.{Key}.Description"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(Percent))]
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(Percent))]
    private int _applied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText), nameof(CanApply))]
    private int _safePending;

    public string CountText => Loc.Instance["Optimize.Pack.Count", Applied, Total];
    public double Percent => Total == 0 ? 0 : 100.0 * Applied / Total;
    public string ApplyText =>
        SafePending > 0
            ? Loc.Instance["Optimize.Pack.ApplySafe", SafePending]
            : Loc.Instance["Optimize.Pack.NothingSafe"];
    public bool CanApply => SafePending > 0;

    /// <summary>The safe settings not applied yet: what "Aplicar seguros" applies.</summary>
    public List<Guid> SafePendingIds { get; set; } = [];
}

/// <summary>A finding of the last diagnosis, with the page where it is fixed.</summary>
public sealed record OptimizeFindingItem(
    string SeverityText,
    Brush SeverityBrush,
    string Title,
    string Description,
    string Who,
    string ActionText,
    Type Target
);

/// <summary>
///     The "Resumen" tab of Optimize (redesign approved by Diego, 19/11/2026): what the diagnosis
///     found, then one pack per category with a button that applies only its safe settings,
///     through the same preview, confirmation and restore point as the "Servicio estándar".
///     The full list of settings stays in the other tabs.
/// </summary>
public sealed partial class OptimizeOverviewViewModel(
    OptimizationRegistry registry,
    DiagnosticService diagnostics,
    SystemInfoService systemInfo,
    ServiceProfileViewModel profile,
    INavigationService navigation,
    ILogger<OptimizeOverviewViewModel> logger
) : ViewModel
{
    public const int MaxFindings = 4;

    public ObservableCollection<OptimizePackItem> Packs { get; } = [];
    public ObservableCollection<OptimizeFindingItem> Findings { get; } = [];

    /// <summary>The profile flow, for its busy state and status line.</summary>
    public ServiceProfileViewModel Profile { get; } = profile;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private string _findingsSummary = string.Empty;

    /// <summary>Raised to open a category tab; the Optimize page listens.</summary>
    public event Action<Type>? OpenCategoryRequested;

    private static readonly Dictionary<string, SymbolRegular> Icons = new()
    {
        ["Performance"] = SymbolRegular.Flash24,
        ["SecurityAndPrivacy"] = SymbolRegular.ShieldLock24,
        ["Gpu"] = SymbolRegular.Games24,
        ["PowerManagement"] = SymbolRegular.Battery524,
        ["BloatwareAndServices"] = SymbolRegular.AppsList24,
        ["UserExperience"] = SymbolRegular.Window24,
        ["AI"] = SymbolRegular.Sparkle24,
        ["Storage"] = SymbolRegular.HardDrive20,
    };

    /// <summary>Where each kind of action is done in Zentra; anything else, the Diagnostic page.</summary>
    internal static Type TargetFor(IReadOnlyList<string> actionIds)
    {
        foreach (var id in actionIds)
        {
            switch (id)
            {
                case ActionIds.RepairWindows:
                case ActionIds.DismCleanup:
                case ActionIds.DefenderFull:
                case ActionIds.DefenderOffline:
                    return typeof(RepairPage);
                case ActionIds.DiskCleanup:
                    return typeof(DiskCleanupPage);
                case ActionIds.StartupReview:
                    return typeof(StartupManagerPage);
                case ActionIds.PrivacyStandard:
                case ActionIds.BloatStandard:
                case ActionIds.BackupSetup:
                    return typeof(SessionPage);
                case ActionIds.WingetUpgrade:
                    return typeof(SoftwarePage);
                case ActionIds.BiosXmp:
                case ActionIds.BiosUma2G:
                case ActionIds.BiosUpdate:
                case ActionIds.BiosSecureBoot:
                    return typeof(BiosGuidePage);
            }
        }
        return typeof(DiagnosticPage);
    }

    public override void OnNavigatedTo() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            await registry.EnsurePreloadedAsync();
            if (Packs.Count == 0)
                BuildPacks();
            var snapshot = await systemInfo.EnsureSnapshotAsync();
            var all = Packs.SelectMany(p => p.Category.Optimizations).ToArray();
            foreach (var o in all)
                o.ConditionResult = ConditionEvaluator.Evaluate(o.ConditionType, snapshot, logger);
            await OptimizationService.UpdateOptimizationStateAsync(all);
            foreach (var pack in Packs)
            {
                var usable = pack
                    .Category.Optimizations.Where(o => !o.ConditionResult.IsBlocking)
                    .ToList();
                pack.Total = usable.Count;
                pack.Applied = usable.Count(o => o.State.IsApplied);
                pack.SafePendingIds = usable
                    .Where(o =>
                        !o.State.IsApplied
                        && o.Risk == OptimizationRisk.Safe
                        && o.Confirmation == OptimizationConfirmation.None
                    )
                    .Select(o => o.Id)
                    .ToList();
                pack.SafePending = pack.SafePendingIds.Count;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refresh the Optimize overview");
        }
        ShowFindings();
    }

    private void BuildPacks()
    {
        foreach (var category in registry.OptimizationCategories)
        {
            var key = category.GetType().Name;
            var pageType = category
                .GetType()
                .GetCustomAttribute<OptimizationCategoryAttribute>()
                ?.PageType;
            if (pageType is null || !Icons.TryGetValue(key, out var icon))
                continue; // the Debug category, and anything without a page
            Packs.Add(
                new OptimizePackItem
                {
                    Category = category,
                    PageType = pageType,
                    Key = key,
                    Icon = icon,
                }
            );
        }
    }

    private void ShowFindings()
    {
        Findings.Clear();
        var report = diagnostics.LastReport;
        HasReport = report is not null;
        if (report is null)
            return;
        var findings = report.Findings.OrderByDescending(f => f.Severity).ToList();
        foreach (var finding in findings.Take(MaxFindings))
        {
            var view = FindingTextRenderer.Render(finding);
            var actions = finding.ActionIds.Where(ActionCatalog.Contains).ToList();
            var who =
                actions.Count > 0
                    ? Loc.Instance[$"Ai.Who.{ActionCatalog.All[actions[0]].Kind}"]
                    : string.Empty;
            var target = TargetFor(actions);
            Findings.Add(
                new OptimizeFindingItem(
                    view.SeverityText,
                    UI.Styles.ZentraBrushes.ForSeverity(finding.Severity),
                    view.Title,
                    view.Description,
                    who,
                    Loc.Instance[$"Optimize.Finding.Go.{target.Name}"],
                    target
                )
            );
        }
        FindingsSummary = Loc.Instance[
            "Optimize.Findings.Summary",
            report.Score.Score,
            findings.Count,
            Math.Min(findings.Count, MaxFindings)
        ];
    }

    [RelayCommand]
    private void OpenCategory(OptimizePackItem? pack)
    {
        if (pack is not null)
            OpenCategoryRequested?.Invoke(pack.PageType);
    }

    [RelayCommand]
    private async Task ApplySafeAsync(OptimizePackItem? pack)
    {
        if (pack is null || pack.SafePendingIds.Count == 0)
            return;
        var preset = new ServicePreset
        {
            Name = pack.Name,
            Created = DateOnly.FromDateTime(DateTime.Today),
            Settings = pack.SafePendingIds,
        };
        await Profile.ApplyPresetAsync(preset);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ApplyStandardAsync()
    {
        await Profile.ApplyPresetAsync(ServicePreset.Standard);
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFinding(OptimizeFindingItem? finding)
    {
        if (finding is not null)
            navigation.Navigate(finding.Target);
    }

    [RelayCommand]
    private void Diagnose() => navigation.Navigate(typeof(DiagnosticPage));
}
