using System.Text;

namespace optimizerDuck.Services.Ai;

/// <summary>
///     The Claude API key built into Zentra (Diego's decision, 20/11/2026: Zentra is only his, for
///     his own service, and the report should be written on the client's PC without carrying files
///     to his PC). The key is never in the source or in git: <c>tools/embed-api-key.ps1</c> writes
///     <c>EmbeddedApiKey.g.cs</c> under <c>obj/</c> only when Zentra is published on his PC, from a
///     DPAPI-protected file outside the repository. It is stored masked (XOR with a random mask)
///     so a plain text search of the .exe does not find it; that is not encryption, anyone who
///     takes the .exe apart can recover it. Builds without that file (GitHub, tests) carry no key.
/// </summary>
internal static partial class EmbeddedApiKey
{
    /// <summary>Implemented only in the generated file; without it, nothing is loaded.</summary>
    static partial void Load(ref byte[]? masked, ref byte[]? mask);

    /// <summary>The built-in key, or null when this build has none.</summary>
    public static ApiKeySecret? TryGet()
    {
        byte[]? masked = null;
        byte[]? mask = null;
        Load(ref masked, ref mask);
        if (masked is null || mask is null || masked.Length == 0 || masked.Length != mask.Length)
            return null;

        var plain = new byte[masked.Length];
        try
        {
            for (var i = 0; i < plain.Length; i++)
                plain[i] = (byte)(masked[i] ^ mask[i]);
            return ApiKeySecret.TryCreate(Encoding.UTF8.GetString(plain), out var key) is null
                ? key
                : null;
        }
        finally
        {
            Array.Clear(plain);
        }
    }
}
