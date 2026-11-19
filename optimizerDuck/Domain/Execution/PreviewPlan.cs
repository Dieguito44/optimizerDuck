namespace optimizerDuck.Domain.Execution;

/// <summary>What a primitive would change: a registry value, a service, a task, a policy, a command.</summary>
public enum PlannedChangeKind
{
    Registry,
    Service,
    Task,
    Policy,
    Command,
}

/// <summary>
///     One change a primitive would make, with the value it finds now. <see cref="Current" /> is
///     null when the value does not exist (or cannot be read).
/// </summary>
public sealed record PlannedChange(
    PlannedChangeKind Kind,
    string Setting,
    string Target,
    string? Current,
    string Next
)
{
    /// <summary>The change would write what is already there.</summary>
    public bool IsAlreadyThere =>
        Current is not null && string.Equals(Current, Next, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     A dry run (plan of improvements F2-03, "WhatIf"). When an <see cref="OpCall" /> carries
///     one, the primitives that change the system (registry, services, scheduled tasks, local
///     policy, shell commands) only describe the change here and return success: nothing is
///     written and nothing is recorded for revert.
/// </summary>
public sealed class PreviewPlan
{
    private readonly List<PlannedChange> _items = [];

    /// <summary>The setting the next planned changes belong to (the optimization's name).</summary>
    public string CurrentSetting { get; set; } = string.Empty;

    public IReadOnlyList<PlannedChange> Items
    {
        get
        {
            lock (_items)
                return [.. _items];
        }
    }

    public void Add(PlannedChangeKind kind, string target, string? current, string next)
    {
        lock (_items)
            _items.Add(new PlannedChange(kind, CurrentSetting, target, current, next));
    }
}
