using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.Measurements;

/// <summary>
///     "Reiniciar y continuar" (version 1.1.2). The Session page asks for a restart between the
///     profile and "Medir después". This schedules Zentra to open once at the technician's next
///     sign-in, elevated, on the Session page (the unfinished session is picked up there), and
///     restarts the PC. The task deletes itself when Zentra opens.
/// </summary>
public static class ContinueAfterRestart
{
    /// <summary>The window switch the scheduled start passes; not a command-line run.</summary>
    public const string Switch = "--continuar-sesion";

    internal const string TaskPath = @"\Zentra\";
    internal const string TaskName = "Continuar sesion";

    /// <summary>Whether this run was started by the scheduled task.</summary>
    public static bool StartedForIt =>
        Environment.GetCommandLineArgs().Contains(Switch, StringComparer.OrdinalIgnoreCase);

    /// <summary>The PowerShell that registers the one-time start (pure, for tests).</summary>
    internal static string RegisterCommand(string exePath, string user)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $"$a = New-ScheduledTaskAction -Execute {Q(exePath)} -Argument '{Switch}'; "
            + $"$t = New-ScheduledTaskTrigger -AtLogOn -User {Q(user)}; "
            + $"$p = New-ScheduledTaskPrincipal -UserId {Q(user)} -LogonType Interactive -RunLevel Highest; "
            + "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 0); "
            + $"Register-ScheduledTask -TaskPath {Q(TaskPath)} -TaskName {Q(TaskName)} -Action $a -Trigger $t -Principal $p -Settings $s "
            + "-Description 'Zentra: abre la sesión de servicio una vez después del reinicio. Se borra sola.' -Force | Out-Null";
    }

    /// <summary>Registers the one-time start and restarts the PC in 15 seconds.</summary>
    public static bool ScheduleAndRestart(ILogger logger)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return false;
        var user = WindowsIdentity.GetCurrent().Name;
        var encoded = Convert.ToBase64String(
            global::System.Text.Encoding.Unicode.GetBytes(RegisterCommand(exe, user))
        );
        if (
            Run("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", logger)
            != 0
        )
            return false;
        logger.LogInformation("Continue-after-restart scheduled; restarting");
        return Run(
                "shutdown.exe",
                "/r /t 15 /c \"Zentra: el PC se reinicia y la sesión continúa al volver.\"",
                logger
            ) == 0;
    }

    /// <summary>Deletes the one-time start (called when Zentra opens with <see cref="Switch" />).</summary>
    public static void Remove(ILogger logger)
    {
        var cmd =
            $"Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue";
        var encoded = Convert.ToBase64String(global::System.Text.Encoding.Unicode.GetBytes(cmd));
        Run("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", logger);
    }

    private static int Run(string tool, string arguments, ILogger logger)
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo(
                    Path.Combine(
                        Environment.SystemDirectory,
                        tool == "powershell.exe" ? @"WindowsPowerShell\v1.0\powershell.exe" : tool
                    ),
                    arguments
                )
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (p is null)
                return -1;
            p.WaitForExit(60_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
            when (ex is global::System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("{Tool} failed: {Error}", tool, ex.Message);
            return -1;
        }
    }
}
