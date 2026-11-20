using System.IO;
using System.Security.Cryptography;
using System.Text;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.State;

/// <summary>
///     Plan of improvements F1-02 (audit Z-02): every state file (revert data, history, intent
///     journal) starts with an HMAC-SHA256 of its content. The key is random, made once per PC,
///     kept next to the state and protected with DPAPI (machine scope); the folder itself only
///     lets Administrators and SYSTEM write. A file whose HMAC does not match is rejected as
///     altered, so a value someone edited is never written back as administrator.
/// </summary>
public static class StateFile
{
    public const string Header = "#zentra-hmac-sha256:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zentra state integrity v1");
    private static readonly Lock KeyGate = new();
    private static byte[]? _key;
    private static string? _keyPath;

    public static string KeyPath => Path.Combine(Shared.StateDirectory, "integrity.key");

    /// <summary>The content with its HMAC line in front, ready to be written.</summary>
    public static string Seal(string json) =>
        Header + Convert.ToHexString(Mac(json)).ToLowerInvariant() + "\n" + json;

    /// <summary>The content when its HMAC matches; null when it is missing or does not match.</summary>
    public static string? Unseal(string text)
    {
        if (!text.StartsWith(Header, StringComparison.Ordinal))
            return null;
        var end = text.IndexOf('\n');
        if (end < 0)
            return null;
        var hex = text[Header.Length..end].Trim();
        var json = text[(end + 1)..];
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
        return CryptographicOperations.FixedTimeEquals(expected, Mac(json)) ? json : null;
    }

    public static bool IsSealed(string text) => text.StartsWith(Header, StringComparison.Ordinal);

    private static byte[] Mac(string json) =>
        HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(json));

    /// <summary>The PC's key: read and unprotected, or made the first time.</summary>
    private static byte[] Key()
    {
        lock (KeyGate)
        {
            var path = KeyPath;
            if (_key is not null && _keyPath == path)
                return _key;

            if (File.Exists(path))
                _key = ProtectedData.Unprotect(
                    File.ReadAllBytes(path),
                    Entropy,
                    DataProtectionScope.LocalMachine
                );
            else
            {
                var key = RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var blob = ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine);
                using (
                    var file = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                {
                    file.Write(blob);
                    file.Flush(flushToDisk: true);
                }
                _key = key;
            }
            _keyPath = path;
            return _key;
        }
    }

    /// <summary>For tests that change the state folder.</summary>
    internal static void ForgetKey()
    {
        lock (KeyGate)
        {
            _key = null;
            _keyPath = null;
        }
    }
}
