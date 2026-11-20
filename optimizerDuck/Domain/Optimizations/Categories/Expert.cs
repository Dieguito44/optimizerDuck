using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.UI.Pages.Optimize.Categories;

namespace optimizerDuck.Domain.Optimizations.Categories;

/// <summary>
///     The "Experto" tab (20/11/2026). Diego took these out of his blacklist, on the condition
///     that they stay apart: every one is Risky, none is in a profile or a "Aplicar seguros"
///     pack, and the hard-to-undo ones ask for confirmation. Ideas taken from WinUtil
///     (github.com/ChrisTitusTech/winutil, MIT); the code is Zentra's. Removing OneDrive and
///     blocking Adobe stay out, and WebView2 is never touched (BlacklistTests).
/// </summary>
[OptimizationCategory(typeof(ExpertOptimizerPage))]
public class Expert : LocalizedObject, IOptimizationCategory
{
    public string Name => Loc.Instance[$"Optimizer.{nameof(Expert)}"];
    public OptimizationCategoryOrder Order { get; init; } = OptimizationCategoryOrder.Expert;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    /// <summary>A PowerShell single-quoted string: a quote inside is doubled.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void RestartPending()
    {
        if (Application.Current is App app)
            app.HasPendingChanges = true;
    }

    /// <summary>
    ///     Uninstalls Microsoft Edge with its own uninstaller. Windows only allows it when the
    ///     legacy Edge stub exists, so an empty one is created for the run and deleted after.
    ///     The user's Edge profile (favorites, passwords) is kept, and WebView2, which other apps
    ///     need, is not touched. Windows Update may put Edge back; winget reinstalls it.
    /// </summary>
    [Optimization(
        Id = "2321EE75-1A8F-4421-A4AA-A19C5D160D6F",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.System,
        Confirmation = OptimizationConfirmation.Irreversible
    )]
    public class RemoveEdge : BaseOptimization
    {
        private static string EdgeApplicationFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft",
                "Edge",
                "Application"
            );

        /// <summary>The newest Edge installer (setup.exe), or null when Edge is not installed.</summary>
        internal static string? EdgeSetup()
        {
            if (!Directory.Exists(EdgeApplicationFolder))
                return null;
            return Directory
                .GetDirectories(EdgeApplicationFolder)
                .Select(d => Path.Combine(d, "Installer", "setup.exe"))
                .Where(File.Exists)
                .OrderBy(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))), out var v) ? v : new Version())
                .LastOrDefault();
        }

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var setup = EdgeSetup();
            if (setup is null)
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NotInstalled"]);
                return context.Changes.ToApplyResult();
            }
            const string command = "Edge setup.exe --uninstall --system-level --force-uninstall";
            if (Storage.PreviewOnly(context, command))
                return context.Changes.ToApplyResult();

            var stub = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SystemApps",
                "Microsoft.MicrosoftEdge_8wekyb3d8bbwe",
                "MicrosoftEdge.exe"
            );
            var createdStub = false;
            try
            {
                if (!File.Exists(stub))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
                    await File.WriteAllBytesAsync(stub, [], context.CancellationToken);
                    createdStub = true;
                }
                // No --delete-profile: the client's favorites and passwords stay on the PC.
                var result = await context.Shell.QueryPowerShellAsync(
                    $"$p = Start-Process -FilePath {Quote(setup)} -ArgumentList '--uninstall','--system-level','--force-uninstall' -Wait -PassThru; exit $p.ExitCode",
                    context.Logger,
                    ct: context.CancellationToken
                );
                var removed = !File.Exists(Path.Combine(EdgeApplicationFolder, "msedge.exe"));
                Storage.RecordIrreversible(
                    context,
                    command,
                    result with { ExitCode = removed ? 0 : (result.ExitCode == 0 ? 1 : result.ExitCode) }
                );
            }
            finally
            {
                if (createdStub)
                    try
                    {
                        File.Delete(stub);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        context.Logger.LogWarning("Could not delete the Edge stub: {Error}", ex.Message);
                    }
            }
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Turns BitLocker off on the Windows drive: Windows decrypts it in the background. Zentra
    ///     does not turn it back on (that needs a new recovery key the client must keep), so it
    ///     is recorded as hard to undo: Settings, Privacy and security, Device encryption.
    /// </summary>
    [Optimization(
        Id = "2E0EFBB6-521A-4C1A-9AA7-4429AB30C544",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Security | OptimizationTags.Disk,
        Confirmation = OptimizationConfirmation.HardToRevert
    )]
    public class DisableBitLocker : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var status = await context.Shell.QueryPowerShellAsync(
                "(Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop).VolumeStatus",
                context.Logger,
                ct: context.CancellationToken
            );
            var state = status.Stdout?.Trim() ?? "";
            if (status.ExitCode != 0 || state.Length == 0)
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NotAvailable"]);
                return context.Changes.ToApplyResult();
            }
            if (state.Equals("FullyDecrypted", StringComparison.OrdinalIgnoreCase))
            {
                context.Changes.AddSkip(Name, "BitLocker: FullyDecrypted");
                return context.Changes.ToApplyResult();
            }

            const string command = "Disable-BitLocker -MountPoint $env:SystemDrive";
            if (Storage.PreviewOnly(context, command))
                return context.Changes.ToApplyResult();
            var result = await context.Shell.QueryPowerShellAsync(
                command + " -ErrorAction Stop | Out-Null",
                context.Logger,
                ct: context.CancellationToken
            );
            Storage.RecordIrreversible(context, command, result);
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Turns IPv6 off: on each adapter that has it, and system-wide (DisabledComponents 0xFF,
    ///     after a restart). Each step is undone on revert, only on the adapters it changed.
    ///     Microsoft does not recommend it: some features (HomeGroup-era sharing, DirectAccess,
    ///     some games and ISPs) expect IPv6.
    /// </summary>
    [Optimization(
        Id = "8567DD4B-EB32-4E15-950B-D6670883E5DB",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Network
    )]
    public class DisableIpv6 : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var bound = await context.Shell.QueryPowerShellAsync(
                "Get-NetAdapterBinding -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | Where-Object Enabled | ForEach-Object Name",
                context.Logger,
                ct: context.CancellationToken
            );
            foreach (
                var adapter in (bound.Stdout ?? "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            )
                await context.Shell.PowerShellAsync(
                    $"Disable-NetAdapterBinding -Name {Quote(adapter)} -ComponentID ms_tcpip6 -ErrorAction Stop",
                    context,
                    $"Enable-NetAdapterBinding -Name {Quote(adapter)} -ComponentID ms_tcpip6"
                );

            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters",
                    "DisabledComponents",
                    255
                )
            );
            if (context.Preview is null)
                RestartPending();
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Stops and disables Windows Search (WSearch). Less disk activity on mechanical disks;
    ///     in exchange, searching files in Explorer and Start becomes much slower. Revert restores
    ///     the startup type and starts the service again.
    /// </summary>
    [Optimization(
        Id = "8E207029-9220-427F-ABBD-EB398FB6A91E",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Disk | OptimizationTags.Performance
    )]
    public class DisableSearchIndexing : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var status = await context.Shell.QueryPowerShellAsync(
                "(Get-Service -Name WSearch -ErrorAction SilentlyContinue).Status",
                context.Logger,
                ct: context.CancellationToken
            );
            // Stopped first, so a revert restores the startup type before starting it again.
            if (string.Equals(status.Stdout?.Trim(), "Running", StringComparison.OrdinalIgnoreCase))
                await context.Shell.PowerShellAsync(
                    "Stop-Service -Name WSearch -Force -ErrorAction Stop",
                    context,
                    "Start-Service -Name WSearch"
                );
            await ServiceProcessService.ChangeServiceStartupTypeAsync(
                context,
                new ServiceItem("WSearch", ServiceStartupType.Disabled)
            );
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Frees the 7 to 10 GB Windows keeps for updates (reserved storage). Only worth it on a
    ///     small disk, and it should be turned back on before a big Windows update. Reverted with
    ///     the same command.
    /// </summary>
    [Optimization(
        Id = "FA822C7D-6C15-4254-ABDA-02D52109637E",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Disk
    )]
    public class DisableReservedStorage : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var state = await context.Shell.QueryPowerShellAsync(
                "(Get-WindowsReservedStorageState -ErrorAction Stop).ReservedStorageState",
                context.Logger,
                ct: context.CancellationToken
            );
            var value = state.Stdout?.Trim() ?? "";
            if (state.ExitCode != 0 || value.Length == 0)
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NotAvailable"]);
                return context.Changes.ToApplyResult();
            }
            if (value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                context.Changes.AddSkip(Name, "Reserved storage: Disabled");
                return context.Changes.ToApplyResult();
            }
            await context.Shell.PowerShellAsync(
                "Set-WindowsReservedStorageState -State Disabled -ErrorAction Stop",
                context,
                "Set-WindowsReservedStorageState -State Enabled"
            );
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Removes the warning Windows shows when opening an unsigned .rdp file (added in 2025
    ///     against phishing with remote-desktop files). Only for technicians who open their own
    ///     .rdp files every day.
    /// </summary>
    [Optimization(
        Id = "0AF92330-D37E-4A99-BA75-7F4030540A07",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Security | OptimizationTags.Network
    )]
    public class DisableUnsignedRdpWarning : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client",
                    "RedirectionWarningDialogVersion",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Terminal Server Client",
                    "RdpLaunchConsentAccepted",
                    1
                )
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }
}
