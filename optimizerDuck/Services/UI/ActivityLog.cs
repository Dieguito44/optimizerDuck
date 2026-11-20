using System.Collections.ObjectModel;
using System.Windows;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.UI;

public enum ActivityKind
{
    Info,
    Good,
    Warning,
    Error,
}

/// <summary>One line of the activity log: when, what, and whether it went well.</summary>
public sealed record ActivityEntry(DateTime Time, string Text, ActivityKind Kind)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>
///     The "Bitácora" at the bottom of the window (redesign, 19/11/2026): what Zentra is doing and
///     how each run ended, in the technician's words. It is not the log file: only short, readable
///     lines, kept in memory for this run of the app and never written anywhere. Texts go through
///     <see cref="SecretRedactor" /> anyway.
/// </summary>
public static class ActivityLog
{
    public const int MaxEntries = 300;

    public static ObservableCollection<ActivityEntry> Entries { get; } = [];

    /// <summary>Raised on the UI thread after a line is added.</summary>
    public static event Action<ActivityEntry>? Added;

    public static void Add(string text, ActivityKind kind = ActivityKind.Info)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var entry = new ActivityEntry(DateTime.Now, SecretRedactor.Redact(text.Trim()), kind);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Append(entry);
        else
            dispatcher.BeginInvoke(() => Append(entry));
    }

    private static void Append(ActivityEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(0);
        Added?.Invoke(entry);
    }
}
