using System.IO;
using Xunit;

[assembly: AssemblyFixture(typeof(optimizerDuck.Test.Infrastructure.AppDataCleanupFixture))]

namespace optimizerDuck.Test.Infrastructure;

/// <summary>
///     Removes the suite's own state folder once the whole assembly has finished. Since plan F1-02
///     the tests keep revert data, history and the intent journal in a temporary folder of their
///     own (<see cref="TestCulture.TestStateRoot" />), never in the PC's real state, so the whole
///     folder can go.
/// </summary>
public sealed class AppDataCleanupFixture : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TestCulture.TestStateRoot))
                Directory.Delete(TestCulture.TestStateRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open is left; it is in the temporary folder anyway.
        }
    }
}
