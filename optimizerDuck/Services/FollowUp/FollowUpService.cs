using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Repair;

namespace optimizerDuck.Services.FollowUp;

/// <summary>One follow-up check that ran.</summary>
public sealed record FollowUpRun(DateTimeOffset RanAtUtc, string Folder, int? Score);

/// <summary>What is scheduled, saved in seguimiento.json next to the results.</summary>
public sealed record FollowUpState
{
    public string? ClientLabel { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public IReadOnlyList<DateTimeOffset> DueAtUtc { get; init; } = [];
    public IReadOnlyList<FollowUpRun> Runs { get; init; } = [];

    public bool IsFinished => Runs.Count >= DueAtUtc.Count;
}

public enum FollowUpScheduleResult
{
    Scheduled,
    ConsentRefused,

    /// <summary>The folder exists with an owner that is not Administrators or SYSTEM, or is a link.</summary>
    UnsafeFolder,
    Failed,
}

/// <summary>
///     Plan of improvements F3-05: with the client's written consent, a scheduled task re-runs the
///     diagnosis only (no changes) 7 and 30 days after the service, and keeps the result for the
///     next visit. It runs a copy of Zentra from %ProgramData%\Zentra\Seguimiento, a folder only
///     SYSTEM and Administrators can write (Users read), so nobody without administrator rights
///     can swap what SYSTEM runs. "Quitar seguimiento" (or Zentra.exe --remove-followup) deletes
///     the task and the copy; the results stay until someone deletes them.
/// </summary>
public sealed class FollowUpService(ILogger<FollowUpService> logger)
{
    public const string TaskPath = @"\Zentra\";
    public const string TaskName = "Seguimiento";
    public static readonly int[] DueDays = [7, 30];

    /// <summary>Hour of the day the checks are due (local). The task starts later if the PC was off.</summary>
    public const int DueHour = 11;

    private static readonly TimeSpan ShellTimeout = TimeSpan.FromMinutes(2);

    public static string RootFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Zentra"
        );

    public static string Folder => Path.Combine(RootFolder, "Seguimiento");
    public static string AppFolder => Path.Combine(Folder, "app");
    public static string StateFile => Path.Combine(Folder, "seguimiento.json");
    public static string ExePath => Path.Combine(AppFolder, "Zentra.exe");

    public static FollowUpState? ReadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonConvert.DeserializeObject<FollowUpState>(File.ReadAllText(StateFile))
                : null;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The due dates: 7 and 30 days later, at <see cref="DueHour" /> local time.</summary>
    public static IReadOnlyList<DateTimeOffset> DueDates(DateTimeOffset nowLocal) =>
        DueDays
            .Select(d =>
            {
                var day = nowLocal.Date.AddDays(d).AddHours(DueHour);
                return new DateTimeOffset(day, nowLocal.Offset);
            })
            .ToList();

    public async Task<FollowUpScheduleResult> ScheduleAsync(
        string? clientLabel,
        DateTimeOffset nowLocal,
        CancellationToken ct
    )
    {
        var due = DueDates(nowLocal);
        var consent = await System.ClientConsent.RequestAsync(
            Loc.Instance["FollowUp.Consent.Title"],
            Loc.Instance[
                "FollowUp.Consent.Body",
                due[0].ToString("D", Loc.CurrentCulture),
                due[1].ToString("D", Loc.CurrentCulture),
                Folder
            ],
            logger
        );
        if (!consent)
            return FollowUpScheduleResult.ConsentRefused;

        try
        {
            if (!PrepareFolder())
                return FollowUpScheduleResult.UnsafeFolder;
            CopyApp();

            var register = await RunPowerShellAsync(RegisterCommand(ExePath, due), ct);
            if (!register)
            {
                Directory.Delete(AppFolder, recursive: true);
                return FollowUpScheduleResult.Failed;
            }

            WriteState(
                new FollowUpState
                {
                    ClientLabel = string.IsNullOrWhiteSpace(clientLabel)
                        ? null
                        : clientLabel.Trim(),
                    CreatedAtUtc = nowLocal.ToUniversalTime(),
                    DueAtUtc = due.Select(d => d.ToUniversalTime()).ToList(),
                }
            );
            logger.LogInformation("Follow-up scheduled for {Due}", string.Join(", ", due));
            return FollowUpScheduleResult.Scheduled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Follow-up could not be scheduled");
            return FollowUpScheduleResult.Failed;
        }
    }

    /// <summary>Deletes the task and the copy of Zentra. The results and seguimiento.json stay.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct)
    {
        var ok = await RunPowerShellAsync(
            $"Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue",
            ct
        );
        try
        {
            // The copy may be the one running (the last check removes its own task): then the
            // folder stays until "Quitar seguimiento" or the next check.
            if (Directory.Exists(AppFolder) && !IsRunningFrom(AppFolder))
                Directory.Delete(AppFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Follow-up app folder not deleted");
        }
        logger.LogInformation("Follow-up removed");
        return ok;
    }

    /// <summary>
    ///     Called by "Zentra.exe --followup": the folder for this check's results. Null when no
    ///     follow-up is scheduled (the task should not exist then).
    /// </summary>
    public static string? NextResultsFolder(DateTimeOffset nowLocal)
    {
        var state = ReadState();
        if (state is null || state.IsFinished)
            return null;
        var days = DueDays[Math.Min(state.Runs.Count, DueDays.Length - 1)];
        return Path.Combine(Folder, $"{days}-dias_{nowLocal:yyyy-MM-dd_HHmm}");
    }

    /// <summary>Records a finished check; after the last one the task removes itself.</summary>
    public async Task RecordRunAsync(FollowUpRun run, CancellationToken ct)
    {
        var state = ReadState();
        if (state is null)
            return;
        var updated = state with { Runs = [.. state.Runs, run] };
        WriteState(updated);
        if (updated.IsFinished)
            await RemoveAsync(ct);
    }

    // ---- The folder --------------------------------------------------------------------------

    /// <summary>The folders, checked or created, with the protected ACL (Users may read results).</summary>
    private bool PrepareFolder()
    {
        if (ProtectedFolder.Ensure(usersCanRead: true, RootFolder, Folder))
            return true;
        logger.LogWarning("Follow-up folder {Dir} is a link or has another owner: refused", Folder);
        return false;
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        ProtectedFolder.IsTrustedOwner(owner);

    /// <summary>SYSTEM and Administrators: full control. Users: read and run. Nothing inherited.</summary>
    internal static DirectorySecurity ProtectedAcl() => ProtectedFolder.Acl(usersCanRead: true);

    /// <summary>
    ///     The Zentra that runs now. A release is one self-contained Zentra.exe; a build folder
    ///     (development) is copied whole.
    /// </summary>
    private static void CopyApp()
    {
        if (Directory.Exists(AppFolder))
            Directory.Delete(AppFolder, recursive: true);
        Directory.CreateDirectory(AppFolder);

        var exe = Environment.ProcessPath ?? throw new IOException("Zentra's path is unknown");
        var singleFile = string.IsNullOrEmpty(typeof(FollowUpService).Assembly.Location);
        if (singleFile)
        {
            File.Copy(exe, ExePath);
            return;
        }
        var source = Path.GetDirectoryName(exe)!;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(AppFolder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static bool IsRunningFrom(string folder) =>
        Environment.ProcessPath is { } exe
        && Path.GetFullPath(exe)
            .StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

    private static void WriteState(FollowUpState state)
    {
        var temp = StateFile + ".tmp";
        File.WriteAllText(
            temp,
            JsonConvert.SerializeObject(state, Formatting.Indented),
            Encoding.UTF8
        );
        File.Move(temp, StateFile, overwrite: true);
    }

    // ---- The task ----------------------------------------------------------------------------

    /// <summary>
    ///     The PowerShell that registers the task: SYSTEM, highest rights, one trigger per due date,
    ///     started later when the PC was off, two hours at most, on battery too.
    /// </summary>
    internal static string RegisterCommand(string exe, IReadOnlyList<DateTimeOffset> due)
    {
        if (exe.IndexOfAny(['\'', '"', '%', '&', '|', '^', '<', '>']) >= 0)
            throw new ArgumentException("Unsafe path", nameof(exe));
        // A single-file Zentra extracts its DLLs before running. For SYSTEM that would be a temp
        // folder standard users can write to on older Windows: point it inside the protected one.
        var extract = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "extract");
        var argument =
            $"/d /c set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={extract}\" && \"{exe}\" --followup";
        var triggers = string.Join(
            ",",
            due.Select(d =>
                $"(New-ScheduledTaskTrigger -Once -At ([datetime]::ParseExact('{d.DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}','yyyy-MM-dd HH:mm',$null)))"
            )
        );
        return "$a = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '"
            + argument
            + "'; "
            + "$t = @("
            + triggers
            + "); "
            + "$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; "
            + "$s = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2); "
            + $"Register-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s "
            + "-Description 'Zentra: diagnóstico de seguimiento (solo lectura). Se quita con \"Quitar seguimiento\".' -Force | Out-Null";
    }

    private async Task<bool> RunPowerShellAsync(string command, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = await LongConsoleRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            ShellTimeout,
            null,
            logger,
            ct
        );
        if (result.ExitCode != 0 || result.TimedOut)
            logger.LogWarning(
                "Follow-up PowerShell failed (exit {Code}): {Output}",
                result.ExitCode,
                ConsoleOutput.Tail(result.Output)
            );
        return result.ExitCode == 0 && !result.TimedOut;
    }
}
