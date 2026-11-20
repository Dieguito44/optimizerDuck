using System.Globalization;
using System.Runtime.CompilerServices;
using optimizerDuck.Resources.Languages;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// The suite touches the real registry, filesystem and child processes, and shares
// process-wide statics (Loc.Instance's culture, ReflectionHelper's cache). Parallel
// test classes race on those, so the assembly runs serialized.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace optimizerDuck.Test;

internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Translations.Culture = culture;

        // The build machine's console user must never change where HKCU tests write.
        optimizerDuck.Services.System.InteractiveUser.Source = () => null;

        // Plan F1-02: the suite keeps its revert state in its own temporary folder (the real
        // one is %ProgramData%\Zentra\Estado, which needs administrator rights and an ACL).
        optimizerDuck.Common.Helpers.Shared.StateRootOverride = TestStateRoot;
        optimizerDuck.Services.State.StateStore.Prepare();
    }

    /// <summary>One folder per test run, deleted when the run ends.</summary>
    internal static readonly string TestStateRoot = global::System.IO.Path.Combine(
        global::System.IO.Path.GetTempPath(),
        $"zentra-test-state-{Environment.ProcessId}-{Guid.NewGuid():N}"
    );
}
