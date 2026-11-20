using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.Measurements;

/// <summary>
///     USB mode and "Terminar servicio" (version 1.1.2). Diego's Zentra carries the API key inside
///     the .exe, and every client's PC where it ran kept a copy (the single file unpacks itself to
///     %TEMP%\.net). Run from a USB stick, sessions go to the stick; at the end of the service the
///     technician removes Zentra from the client's PC. Revert data (%ProgramData%\Zentra\Estado)
///     and restore points stay, so the changes can still be undone on a later visit.
/// </summary>
public static class EndOfService
{
    public const string UsbSessionsFolder = "Zentra-Sesiones";

    /// <summary>Zentra.exe runs from a removable drive (a USB stick).</summary>
    public static bool IsUsbMode => IsRemovable(Shared.ExeDir);

    internal static bool IsRemovable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root)
                && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>Where the sessions live: next to Zentra.exe on the stick, or in the user's data.</summary>
    public static string SessionsDirectory =>
        IsUsbMode
            ? Path.Combine(Shared.ExeDir, UsbSessionsFolder)
            : Path.Combine(Shared.RootDirectory, "Sessions");

    /// <summary>
    ///     An anonymous tag of this PC (12 hex characters of the SHA-256 of Windows' MachineGuid),
    ///     so a stick full of sessions never resumes another client's session.
    /// </summary>
    public static string? CurrentMachineTag => _machineTag.Value;

    private static readonly Lazy<string?> _machineTag = new(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string guid
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Zentra:" + guid)))[
                    ..12
                ]
                : null;
        }
        catch (Exception ex)
            when (ex
                    is global::System.Security.SecurityException
                        or UnauthorizedAccessException
                        or IOException
            )
        {
            return null;
        }
    });

    /// <summary>What "Terminar servicio" deletes from this PC.</summary>
    public sealed record CleanupPlan(
        string? ExtractedCopy,
        string UserData,
        string? Exe,
        string? CopySessionsTo
    );

    public static CleanupPlan Plan()
    {
        // The single file unpacks under %TEMP%\.net\Zentra\<hash>: only that folder, nothing else.
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        var temp = Path.GetTempPath().TrimEnd('\\');
        var extracted = baseDir.StartsWith(temp + @"\.net\", StringComparison.OrdinalIgnoreCase)
            ? baseDir
            : null;
        var usb = IsUsbMode;
        return new CleanupPlan(
            extracted,
            Shared.RootDirectory,
            usb ? null : Shared.ExePath,
            usb ? null : Path.Combine(Shared.ExeDir, UsbSessionsFolder)
        );
    }

    /// <summary>A path the cleanup script can quote safely in cmd.</summary>
    internal static bool IsSafeForCmd(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.IndexOfAny(['"', '%', '&', '^', '|', '<', '>', '!']) < 0;

    /// <summary>The cmd script that waits for Zentra to close and deletes the plan (pure, for tests).</summary>
    internal static string Script(CleanupPlan plan, int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine(
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)"
        );
        if (IsSafeForCmd(plan.ExtractedCopy))
            sb.AppendLine($"rmdir /s /q \"{plan.ExtractedCopy}\"");
        if (IsSafeForCmd(plan.UserData))
            sb.AppendLine($"rmdir /s /q \"{plan.UserData}\"");
        if (IsSafeForCmd(plan.Exe))
            sb.AppendLine($"del /f /q \"{plan.Exe}\"");
        sb.AppendLine("del /f /q \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>Copies the sessions off the client's PC when needed, then starts the cleanup.</summary>
    public static bool Run(ILogger logger)
    {
        var plan = Plan();
        var local = Path.Combine(Shared.RootDirectory, "Sessions");
        if (plan.CopySessionsTo is { } target && Directory.Exists(local))
        {
            foreach (var dir in Directory.EnumerateDirectories(local))
            {
                var dest = Path.Combine(target, Path.GetFileName(dir));
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.EnumerateFiles(dir))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }
            logger.LogInformation("Sessions copied next to Zentra.exe");
        }

        var script = Path.Combine(Path.GetTempPath(), $"zentra-terminar-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, Script(plan, Environment.ProcessId), Encoding.ASCII);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var p = Process.Start(
            new ProcessStartInfo(cmd, $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        );
        logger.LogInformation("End of service: cleanup started");
        return p is not null;
    }
}
