using Microsoft.Extensions.Logging;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>Found on Diego's PC: a preview must never log as if it had changed something.</summary>
public class PreviewLoggerTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void EveryLine_SaysItWasAPreview()
    {
        var inner = new Capture();
        ILogger logger = new PreviewLogger(inner);

        logger.LogInformation("Disabled {What}", "advertising ID");
        logger.LogWarning("Could not read {Key}", "HKCU");

        Assert.Equal(
            [
                (LogLevel.Information, PreviewLogger.Prefix + "Disabled advertising ID"),
                (LogLevel.Warning, PreviewLogger.Prefix + "Could not read HKCU"),
            ],
            inner.Lines
        );
    }
}
