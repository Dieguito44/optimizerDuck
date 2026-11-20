using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Revert;
using optimizerDuck.Services.State;
using Serilog;

namespace optimizerDuck.Services.Revert;

/// <summary>A run that did not finish: what it is and the revert steps it had recorded.</summary>
public sealed record JournalEntry
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public List<RevertStepData> Steps { get; init; } = [];
}

/// <summary>
///     Plan of improvements F1-03 (audit Z-09): the revert data of a run used to reach the disk only
///     when the run ended. If Zentra closed halfway (a crash, a power cut, the process killed), the
///     changes already made had no way back. Now every change with a revert step is written to the
///     journal as soon as it is recorded, sealed and flushed to disk, and the journal is deleted when
///     the run's revert data is saved. At the next start, what is left becomes revert data, so the
///     technician can revert it (or apply again to finish).
/// </summary>
public static class IntentJournal
{
    private static readonly Lock Gate = new();

    private static string PathFor(Guid id) => Path.Combine(Shared.JournalDirectory, $"{id}.json");

    /// <summary>Marks the run as started, before any change.</summary>
    public static void Begin(Guid id, string name, DateTimeOffset nowUtc) =>
        Write(
            new JournalEntry
            {
                Id = id,
                Name = name,
                StartedAtUtc = nowUtc,
            }
        );

    /// <summary>Adds one change's revert step. Never throws: the apply itself must go on.</summary>
    public static void Record(Guid id, Change change)
    {
        if (change.Revert is null)
            return;
        try
        {
            lock (Gate)
            {
                var entry = Read(PathFor(id)) ?? new JournalEntry { Id = id };
                entry.Steps.Add(
                    new RevertStepData
                    {
                        Index = entry.Steps.Count + 1,
                        Type = change.Revert.Type,
                        Data = change.Revert.ToData(),
                    }
                );
                Write(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Intent journal: could not record a step of {Id}", id);
        }
    }

    /// <summary>The run's revert data is saved: the journal is not needed any more.</summary>
    public static void Complete(Guid id)
    {
        try
        {
            lock (Gate)
                File.Delete(PathFor(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Intent journal: could not close {Id}", id);
        }
    }

    /// <summary>Runs that did not finish. An altered journal file is ignored (and logged).</summary>
    public static IReadOnlyList<JournalEntry> Pending()
    {
        if (!Directory.Exists(Shared.JournalDirectory))
            return [];
        var entries = new List<JournalEntry>();
        foreach (var file in Directory.GetFiles(Shared.JournalDirectory, "*.json"))
            if (Read(file) is { } entry)
                entries.Add(entry);
            else
                Log.Warning("Intent journal {File} is unreadable or altered: ignored", file);
        return entries;
    }

    /// <summary>
    ///     Turns every unfinished run into revert data and closes its journal. Returns what it
    ///     recovered, for the notice at startup.
    /// </summary>
    public static async Task<IReadOnlyList<JournalEntry>> RecoverAsync(RevertManager revert)
    {
        var recovered = new List<JournalEntry>();
        foreach (var entry in Pending())
        {
            if (entry.Steps.Count > 0)
                await revert
                    .SaveRecoveredStepsAsync(entry.Id, entry.Name, entry.Steps)
                    .ConfigureAwait(false);
            Complete(entry.Id);
            if (entry.Steps.Count > 0)
                recovered.Add(entry);
            Log.Warning(
                "Intent journal: {Name} did not finish; {Count} changes kept as revert data",
                entry.Name,
                entry.Steps.Count
            );
        }
        return recovered;
    }

    private static JournalEntry? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        var json = StateFile.Unseal(File.ReadAllText(path, Encoding.UTF8));
        if (json is null)
            return null;
        try
        {
            return JsonConvert.DeserializeObject<JournalEntry>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic and flushed: a torn journal would be worse than none.</summary>
    private static void Write(JournalEntry entry)
    {
        Directory.CreateDirectory(Shared.JournalDirectory);
        var path = PathFor(entry.Id);
        var temp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(
            StateFile.Seal(JsonConvert.SerializeObject(entry, Formatting.Indented))
        );
        using (
            var file = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            )
        )
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
