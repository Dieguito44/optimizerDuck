using System.IO;
using Newtonsoft.Json.Linq;
using optimizerDuck.Services.State;

namespace optimizerDuck.Test.TestDoubles;

/// <summary>
///     Plan F1-02: Zentra only trusts state files it sealed. Tests that write a revert file by
///     hand seal it the same way when it is valid JSON; content that is broken on purpose is
///     written as it is, so it still tests the "unreadable" path.
/// </summary>
internal static class TestState
{
    public static Task WriteAsync(string path, string content, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, IsJson(content) ? StateFile.Seal(content) : content, ct);

    /// <summary>What a sealed file holds after its HMAC line.</summary>
    public static string Content(string fileText) => StateFile.Unseal(fileText) ?? fileText;

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
}
