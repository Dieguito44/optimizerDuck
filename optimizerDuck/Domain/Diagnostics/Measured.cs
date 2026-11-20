using System.Diagnostics.CodeAnalysis;

namespace optimizerDuck.Domain.Diagnostics;

/// <summary>Why a value could not be measured. Shown in the report instead of a guess.</summary>
public enum NotMeasuredReason
{
    /// <summary>Windows returned nothing for it.</summary>
    NotReported = 0,

    /// <summary>Reading it needs rights the app did not have.</summary>
    AccessDenied,

    /// <summary>The query did not finish in time.</summary>
    TimedOut,

    /// <summary>The query failed.</summary>
    QueryFailed,

    /// <summary>The value does not apply to this machine.</summary>
    NotApplicable,

    /// <summary>The value needs a reference table entry that does not exist.</summary>
    NotInReferenceTable,

    /// <summary>The value needs an external tool Zentra does not ship yet.</summary>
    RequiresExternalTool,

    /// <summary>
    ///     Measured after the service, but the PC has not restarted since "Medir antes": there is
    ///     no new boot to time (version 1 test, 20/11/2026).
    /// </summary>
    NeedsRestart,
}

/// <summary>
///     A value that was either measured or not, with the reason when it was not. The default
///     value is "not measured / not reported", so an uninitialized field never looks measured.
/// </summary>
public readonly record struct Measured<T>
{
    private Measured(bool hasValue, T? value, NotMeasuredReason reason)
    {
        HasValue = hasValue;
        ValueOrDefault = value;
        Reason = reason;
    }

    public bool HasValue { get; }

    public T? ValueOrDefault { get; }

    /// <summary>Only meaningful when <see cref="HasValue" /> is false.</summary>
    public NotMeasuredReason Reason { get; }

    public static Measured<T> Of(T value) => new(true, value, default);

    public static Measured<T> Missing(NotMeasuredReason reason) => new(false, default, reason);

    public bool TryGet([MaybeNullWhen(false)] out T value)
    {
        value = ValueOrDefault!;
        return HasValue;
    }

    public override string ToString() => HasValue ? $"{ValueOrDefault}" : $"<{Reason}>";
}

/// <summary>Helpers that turn nullable reads into <see cref="Measured{T}" />.</summary>
public static class Measured
{
    public static Measured<T> FromNullable<T>(
        T? value,
        NotMeasuredReason reason = NotMeasuredReason.NotReported
    )
        where T : struct => value is { } v ? Measured<T>.Of(v) : Measured<T>.Missing(reason);

    public static Measured<string> FromText(
        string? value,
        NotMeasuredReason reason = NotMeasuredReason.NotReported
    ) =>
        string.IsNullOrWhiteSpace(value)
            ? Measured<string>.Missing(reason)
            : Measured<string>.Of(value);
}
