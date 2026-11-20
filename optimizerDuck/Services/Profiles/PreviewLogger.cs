using Microsoft.Extensions.Logging;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     The logger a preview runs with. The preview calls each setting's own ApplyAsync, and those
///     end with lines such as "Disabled advertising ID" even though the primitives only described
///     the change. Found on Diego's PC (19/11/2026): the log read as if the profile had been
///     applied. Every line now says it was a preview.
/// </summary>
internal sealed class PreviewLogger(ILogger inner) : ILogger
{
    public const string Prefix = "[Preview, nothing changed] ";

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (!IsEnabled(logLevel))
            return;
        // A plain string as the state, so every provider (Serilog too) writes the prefix.
        inner.Log(
            logLevel,
            eventId,
            Prefix + formatter(state, exception),
            exception,
            static (message, _) => message
        );
    }
}
