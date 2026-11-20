using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Repair;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Software;

/// <summary>Which PCs a package is offered on.</summary>
public enum SoftwareTarget
{
    Any,

    /// <summary>Only when the CPU or a GPU is Intel (Intel Driver &amp; Support Assistant).</summary>
    Intel,
}

/// <summary>One program of the closed catalog. The id is the exact winget id.</summary>
public sealed record SoftwarePackage(
    string WingetId,
    string Key,
    string License,
    SoftwareTarget Target
)
{
    public string Name => Loc.Instance[$"Software.{Key}.Name"];
    public string Description => Loc.Instance[$"Software.{Key}.Description"];
}

/// <summary>
///     The free programs Zentra installs with winget (plan phase 6, step 3). A closed list: a
///     program reaches clients only by being named here. Ids checked with `winget show` on
///     19/11/2026. AMD and NVIDIA graphics drivers are not in winget; the page links their
///     official download pages instead.
/// </summary>
public static class SoftwareCatalog
{
    public static readonly IReadOnlyList<SoftwarePackage> Packages =
    [
        new("7zip.7zip", "SevenZip", "LGPL-2.1", SoftwareTarget.Any),
        new("VideoLAN.VLC", "Vlc", "GPL-2.0", SoftwareTarget.Any),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", "MPL-2.0", SoftwareTarget.Any),
        // Browser: Google Chrome, Diego's choice (19/11/2026).
        new("Google.Chrome", "Chrome", "Freeware", SoftwareTarget.Any),
        new(
            "Intel.IntelDriverAndSupportAssistant",
            "IntelDsa",
            "Proprietary",
            SoftwareTarget.Intel
        ),
    ];

    /// <summary>The packages offered on this PC.</summary>
    public static IReadOnlyList<SoftwarePackage> For(SystemInfo system) =>
        Packages.Where(p => p.Target == SoftwareTarget.Any || IsIntel(system)).ToList();

    private static bool IsIntel(SystemInfo system) =>
        system.Cpu.Vendor == CpuVendor.Intel || system.Gpus.Any(g => g.Vendor == GpuVendor.Intel);

    /// <summary>The exact install command of the plan.</summary>
    public static string InstallArguments(SoftwarePackage p) =>
        $"install --id {p.WingetId} -e --silent --accept-package-agreements --accept-source-agreements";

    public static string UninstallArguments(SoftwarePackage p) =>
        $"uninstall --id {p.WingetId} -e --silent --accept-source-agreements";

    public static string ListArguments(SoftwarePackage p) =>
        $"list --id {p.WingetId} -e --accept-source-agreements --disable-interactivity";

    /// <summary>ACT-WINGET-UPGRADE, the exact command of the plan. It cannot be undone.</summary>
    public const string UpgradeAllArguments =
        "upgrade --all --silent --accept-package-agreements --accept-source-agreements";

    /// <summary>
    ///     The PawnIO driver LibreHardwareMonitorLib needs to read CPU temperatures. Not in the
    ///     visible catalog: Zentra installs it only for a temperature measurement and removes it.
    /// </summary>
    public static readonly SoftwarePackage PawnIo = new(
        "namazso.PawnIO",
        "PawnIo",
        "Freeware",
        SoftwareTarget.Any
    );

    /// <summary>Stands for "every program" in the progress text of the upgrade run.</summary>
    public static readonly SoftwarePackage AllPrograms = new(
        "--all",
        "UpgradeAll",
        "—",
        SoftwareTarget.Any
    );
}

/// <summary>
///     Installs and uninstalls the catalog through the app's single execution path, so each run
///     shows the progress dialog and lands in the history. Installing is undone by uninstalling.
/// </summary>
public sealed class SoftwareService(ILogger<SoftwareService> logger)
{
    /// <summary>Permanent record key of the installer tool. Never change it.</summary>
    public static readonly OperationSubject Subject = new(
        Guid.Parse("5A7C9E1B-3D5F-4A6B-8C0D-2E4F6A8B0C1D"),
        "SoftwareInstaller",
        "Software installer"
    );

    /// <summary>
    ///     One history record per program (found on Diego's PC, 19/11/2026: with a single key, each
    ///     install or uninstall replaced the record of the one before, and the PDF lost them). The
    ///     id comes from the winget id, so the same program always lands in the same record.
    /// </summary>
    public static OperationSubject SubjectFor(SoftwarePackage package)
    {
        var hash = global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes("Zentra.Software:" + package.WingetId)
        );
        return new OperationSubject(
            new Guid(hash.AsSpan(0, 16)),
            $"SoftwareInstaller.{package.WingetId}",
            $"Software installer ({package.WingetId})"
        );
    }

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Whether winget sees the package installed. Read-only. Null if winget failed.</summary>
    public async Task<bool?> IsInstalledAsync(SoftwarePackage package, CancellationToken ct)
    {
        try
        {
            var result = await LongConsoleRunner.RunAsync(
                "winget.exe",
                SoftwareCatalog.ListArguments(package),
                ListTimeout,
                null,
                logger,
                ct
            );
            if (result.TimedOut)
                return null;
            // 0: found. APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND: not installed.
            return result.ExitCode switch
            {
                0 => true,
                unchecked((int)0x8A150014) => false,
                _ => null,
            };
        }
        catch (Exception ex)
            when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("winget is not available: {Error}", ex.Message);
            return null;
        }
    }

    public OperationRequest BuildInstallRequest(SoftwarePackage package) =>
        new(
            SubjectFor(package),
            Loc.Instance["Software.Run.Install", package.Name],
            logger,
            RevertPersistence.Disabled,
            (progress, context) =>
                RunWingetAsync(
                    package,
                    SoftwareCatalog.InstallArguments(package),
                    progress,
                    context
                )
        );

    public OperationRequest BuildUninstallRequest(SoftwarePackage package) =>
        new(
            SubjectFor(package),
            Loc.Instance["Software.Run.Uninstall", package.Name],
            logger,
            RevertPersistence.Disabled,
            (progress, context) =>
                RunWingetAsync(
                    package,
                    SoftwareCatalog.UninstallArguments(package),
                    progress,
                    context
                )
        );

    /// <summary>ACT-WINGET-UPGRADE: updates every program winget can. Recorded as irreversible.</summary>
    public OperationRequest BuildUpgradeAllRequest() =>
        new(
            Subject,
            Loc.Instance["Software.Run.UpgradeAll"],
            logger,
            RevertPersistence.Disabled,
            (progress, context) =>
                RunWingetAsync(
                    SoftwareCatalog.AllPrograms,
                    SoftwareCatalog.UpgradeAllArguments,
                    progress,
                    context
                )
        );

    private async Task<ApplyResult> RunWingetAsync(
        SoftwarePackage package,
        string arguments,
        IProgress<ProcessingProgress> progress,
        OptimizationContext context
    )
    {
        progress.Report(
            new ProcessingProgress
            {
                Message = Loc.Instance["Software.Run.Progress", package.Name, "…"],
                IsIndeterminate = true,
            }
        );

        var result = await LongConsoleRunner.RunAsync(
            "winget.exe",
            arguments,
            InstallTimeout,
            (percent, _) =>
                progress.Report(
                    new ProcessingProgress
                    {
                        Message = Loc.Instance[
                            "Software.Run.Progress",
                            package.Name,
                            percent is { } p ? $"{p} %" : "…"
                        ],
                        IsIndeterminate = percent is null,
                        Value = percent ?? 0,
                        Total = 100,
                    }
                ),
            logger,
            context.CancellationToken
        );

        var command = $"winget {arguments}";
        logger.LogInformation(
            "{Command}: exit {Code}{TimedOut}. {Tail}",
            command,
            result.ExitCode,
            result.TimedOut ? " (timed out)" : "",
            ConsoleOutput.Tail(result.Output)
        );

        if (result.ExitCode == 0 && !result.TimedOut)
            context.Changes.AddIrreversible(ServiceStrings.ShellName, command);
        else
            context.Changes.Add(
                ServiceStrings.ShellName,
                command,
                false,
                error: result.TimedOut
                    ? Loc.Instance["Software.Run.TimedOut"]
                    : Loc.Instance["Software.Run.Failed", $"0x{result.ExitCode:X8}"],
                errorDetail: ConsoleOutput.Tail(result.Output),
                nativeErrorCode: result.ExitCode
            );

        return context.Changes.ToApplyResult();
    }
}
