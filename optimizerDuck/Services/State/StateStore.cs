using System.IO;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using Serilog;

namespace optimizerDuck.Services.State;

/// <summary>
///     Plan of improvements F1-02: the state folder in %ProgramData%\Zentra\Estado, protected,
///     and the move of what an older Zentra kept in the user's %LOCALAPPDATA%.
/// </summary>
public static class StateStore
{
    /// <summary>
    ///     Creates or checks the folder before anything is applied. False when it is a link or has
    ///     an owner that is not Administrators or SYSTEM: then nothing must be applied.
    /// </summary>
    public static bool Prepare()
    {
        if (Shared.StateRootOverride is null)
        {
            var programData = Path.GetDirectoryName(Shared.StateDirectory)!;
            if (!ProtectedFolder.Ensure(usersCanRead: true, programData, Shared.StateDirectory))
            {
                Log.Error(
                    "State folder {Dir} is a link or has an unexpected owner: refused",
                    Shared.StateDirectory
                );
                return false;
            }
        }
        Directory.CreateDirectory(Shared.RevertDirectory);
        Directory.CreateDirectory(Shared.HistoryDirectory);
        Directory.CreateDirectory(Shared.JournalDirectory);
        Migrate(Shared.LegacyRevertDirectory, Shared.RevertDirectory);
        Migrate(Shared.LegacyHistoryDirectory, Shared.HistoryDirectory);
        return true;
    }

    /// <summary>
    ///     Moves the files an older Zentra kept in the user folder, sealing them on the way. They
    ///     are trusted this one time: before F1-02 they could not be checked. A file that is not
    ///     valid JSON is moved as it was, and the revert manager will report it as unreadable.
    /// </summary>
    internal static int Migrate(string legacy, string target)
    {
        if (!Directory.Exists(legacy) || SafeIsLink(legacy))
            return 0;
        var moved = 0;
        foreach (var file in Directory.GetFiles(legacy))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(target, name);
            if (
                File.Exists(destination)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            try
            {
                var text = File.ReadAllText(file);
                var content =
                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && IsJson(text)
                        ? StateFile.Seal(text)
                        : text;
                var temp = destination + ".tmp";
                File.WriteAllText(temp, content, new global::System.Text.UTF8Encoding(false));
                File.Move(temp, destination);
                File.Delete(file);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Could not move {File} to the protected state folder", file);
            }
        }
        if (moved > 0)
            Log.Information("Moved {Count} state files from {From} to {To}", moved, legacy, target);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return moved;
    }

    private static bool IsJson(string text)
    {
        try
        {
            JToken.Parse(text);
            return true;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    private static bool SafeIsLink(string path) =>
        new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
