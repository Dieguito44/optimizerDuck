using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.Ai;

/// <summary>Why a pasted key was rejected. Never carries the key or its length.</summary>
public enum ApiKeyFormatError
{
    Empty,
    WrongPrefix,
    BadCharacters,
    AdminKey,

    /// <summary>Plan F1-08: the key is never pasted on a client's PC, only on the technician's.</summary>
    NotTechnicianPc,
}

/// <summary>
///     The Claude API key. A class, never a record (a record prints its members), it hides its
///     value from ToString and refuses to be serialized. The only code that reads the value is
///     <see cref="ApplyTo" /> (the HTTP header) and <see cref="ToUtf8" /> (DPAPI).
/// </summary>
[JsonConverter(typeof(RefuseToSerializeConverter))]
public sealed partial class ApiKeySecret
{
    private readonly string _value;

    private ApiKeySecret(string value) => _value = value;

    public static ApiKeyFormatError? TryCreate(string? raw, out ApiKeySecret? key)
    {
        key = null;
        var value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return ApiKeyFormatError.Empty;
        if (value.StartsWith("sk-ant-admin", StringComparison.Ordinal))
            return ApiKeyFormatError.AdminKey;
        if (!value.StartsWith("sk-ant-", StringComparison.Ordinal))
            return ApiKeyFormatError.WrongPrefix;
        // Also blocks header injection: HttpClient would accept a value with a line break.
        if (!KeyFormat().IsMatch(value))
            return ApiKeyFormatError.BadCharacters;

        key = new ApiKeySecret(value);
        return null;
    }

    internal void ApplyTo(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("x-api-key", _value);

    /// <summary>For DPAPI only. The caller zeroes the array after use.</summary>
    internal byte[] ToUtf8() => Encoding.UTF8.GetBytes(_value);

    public override string ToString() => SecretRedactor.Placeholder;

    [GeneratedRegex(@"^sk-ant-[A-Za-z0-9_\-]{20,300}$")]
    private static partial Regex KeyFormat();

    private sealed class RefuseToSerializeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(ApiKeySecret);

        public override void WriteJson(
            JsonWriter writer,
            object? value,
            JsonSerializer serializer
        ) => throw new InvalidOperationException("The API key is never serialized.");

        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer
        ) => throw new InvalidOperationException("The API key is never deserialized.");
    }
}

/// <summary>
///     Holds the Claude API key. Only on the technician's PC, the one Zentra is started on with
///     <c>--equipo-tecnico</c> (the switch Diego puts on his own shortcut): there it lives in
///     memory and can be remembered (DPAPI, current user). On a client's PC the key is refused
///     (plan F1-08, audit Z-32): the report goes through files instead (<see cref="AiExchange" />),
///     and any remembered key found there is deleted.
/// </summary>
public sealed class ApiKeyStore
{
    public const string TechnicianSwitch = "--equipo-tecnico";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zentra.ClaudeApiKey.v1");

    private readonly string _blobPath;
    private readonly ILogger<ApiKeyStore> _logger;

    public ApiKeyStore(ILogger<ApiKeyStore> logger)
        : this(
            Path.Combine(Shared.RootDirectory, "Secrets"),
            Environment
                .GetCommandLineArgs()
                .Contains(TechnicianSwitch, StringComparer.OrdinalIgnoreCase),
            logger,
            EmbeddedApiKey.TryGet()
        ) { }

    internal ApiKeyStore(
        string secretsDirectory,
        bool technicianMode,
        ILogger<ApiKeyStore> logger,
        ApiKeySecret? embedded = null
    )
    {
        _blobPath = Path.Combine(secretsDirectory, "claude.key.dpapi");
        _logger = logger;
        _embedded = embedded;
        CanRemember = technicianMode;

        if (!CanRemember && File.Exists(_blobPath))
        {
            // A remembered key on a PC that is not the technician's: remove it. The store is built
            // with the Session page, so this runs the first time that page opens.
            TryDelete();
            _logger.LogWarning("Remembered API key removed: not started in technician mode");
        }
        else if (CanRemember)
        {
            TryLoadRemembered();
        }

        // Diego's decision (20/11/2026): the key built into his own Zentra, on any PC, so the
        // report is written on the client's PC. A key typed on the technician's PC still wins.
        if (Current is null && _embedded is not null)
        {
            Current = _embedded;
            _logger.LogInformation("Built-in API key loaded in memory");
        }
    }

    private readonly ApiKeySecret? _embedded;

    /// <summary>The key in memory is the one built into this Zentra.</summary>
    public bool IsEmbedded => Current is not null && ReferenceEquals(Current, _embedded);

    /// <summary>This Zentra carries a key: the report is written here, without files.</summary>
    public bool HasEmbeddedKey => _embedded is not null;

    public ApiKeySecret? Current { get; private set; }
    public bool HasKey => Current is not null;
    public bool CanRemember { get; }

    /// <summary>Started with <see cref="TechnicianSwitch" />: the only PC where a key is accepted.</summary>
    public bool IsTechnicianPc => CanRemember;
    public bool IsRemembered => CanRemember && File.Exists(_blobPath);

    public event EventHandler? Changed;

    public ApiKeyFormatError? Set(string? raw, bool remember)
    {
        if (!CanRemember)
        {
            _logger.LogWarning("API key refused: not the technician's PC");
            return ApiKeyFormatError.NotTechnicianPc;
        }
        var error = ApiKeySecret.TryCreate(raw, out var key);
        if (error is not null)
        {
            _logger.LogWarning("API key rejected: {Reason}", error);
            return error;
        }

        Current = key;
        if (remember)
            Remember(key!);
        _logger.LogInformation(
            "API key loaded in memory{Remembered}",
            remember ? " and remembered on this PC" : ""
        );
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Drops the key from memory (on exit, or after the API says it is invalid).</summary>
    public void Clear()
    {
        if (Current is null)
            return;
        Current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the key and deletes the remembered copy.</summary>
    public void Forget()
    {
        TryDelete();
        Current = _embedded; // the built-in key stays, if this Zentra has one
        _logger.LogInformation("API key forgotten");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryLoadRemembered()
    {
        if (!CanRemember || !File.Exists(_blobPath))
            return false;

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_blobPath),
                Entropy,
                DataProtectionScope.CurrentUser
            );
            if (ApiKeySecret.TryCreate(Encoding.UTF8.GetString(plain), out var key) is null)
            {
                Current = key;
                return true;
            }
        }
        catch (Exception ex)
            when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Remembered API key could not be read; it was removed");
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }

        TryDelete();
        return false;
    }

    private void Remember(ApiKeySecret key)
    {
        var plain = key.ToUtf8();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_blobPath)!);
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = _blobPath + ".tmp";
            File.WriteAllBytes(temp, blob);
            File.Move(temp, _blobPath, overwrite: true);
        }
        catch (Exception ex)
            when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("API key could not be remembered: {Error}", ex.GetType().Name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_blobPath))
                File.Delete(_blobPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
