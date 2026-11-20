using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Test.TestDoubles;

/// <summary>The run lifecycle as the application's graph builds it, logging nothing.</summary>
internal static class TestRunner
{
    /// <summary>Builds a run lifecycle over the given revert store.</summary>
    /// <param name="revertManager">The revert store the runs persist into.</param>
    /// <param name="logger">The logger the runs report to, or nothing when omitted.</param>
    /// <returns>The run lifecycle the application's graph would inject.</returns>
    /// <summary>One scan of the PC for every test run: the runner loads it before each setting.</summary>
    private static readonly SystemInfoService SharedSystemInfo = new(
        NullLogger<SystemInfoService>.Instance
    );

    internal static OperationRunner New(
        RevertManager revertManager,
        ILogger<OperationRunner>? logger = null
    )
    {
        return new OperationRunner(
            revertManager,
            SharedSystemInfo,
            new StreamService(NullLogger<StreamService>.Instance),
            new ShellService(new ProcessRunner(120_000)),
            new PowerPlanService(NullLogger<PowerPlanService>.Instance),
            logger ?? NullLogger<OperationRunner>.Instance
        );
    }
}
