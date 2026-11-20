using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Ai;
using Serilog;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>Test keys are generated at run time: no key-shaped literal lives in the repository.</summary>
internal static class TestKeys
{
    public static string New() =>
        "sk-ant-api03-"
        + Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(60))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}

public class SecretRedactorTests
{
    [Fact]
    public void Redact_ReplacesEveryKeyInTheText()
    {
        var a = TestKeys.New();
        var b = TestKeys.New();

        var result = SecretRedactor.Redact($"header x-api-key: {a}\nother {b} end");

        Assert.DoesNotContain(a, result);
        Assert.DoesNotContain(b, result);
        Assert.Contains(SecretRedactor.Placeholder, result);
        Assert.EndsWith(" end", result);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Redact_LeavesOrdinaryTextAlone(string? input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Redact(input));
    }

    [Fact]
    public void LogFile_NeverContainsTheKey()
    {
        var key = TestKeys.New();
        var dir = Directory.CreateTempSubdirectory("zentra-log-");
        var path = Path.Combine(dir.FullName, "log.txt");
        try
        {
            using (var logger = App.BuildLoggerConfiguration(path).CreateLogger())
            {
                logger.Information("Plain {Value}", key);
                logger.Error(new InvalidOperationException($"request carried {key}"), "Boom");
                logger.Warning($"Interpolated {key}");
            }

            // Daily log files since F1-11 (log20261119.txt): read every one.
            var text = string.Concat(
                Directory.GetFiles(dir.FullName, "log*.txt").Select(File.ReadAllText)
            );
            Assert.DoesNotContain(key, text);
            Assert.Contains("Boom", text); // positive control: the log was written
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CrashText_NeverContainsTheKey()
    {
        var key = TestKeys.New();
        var ex = new HttpRequestException(
            $"failed {key}",
            new InvalidOperationException($"inner {key}")
        );

        var text = App.CrashText("Test", ex);

        Assert.DoesNotContain(key, text);
        Assert.Contains("HttpRequestException", text);
    }
}

public class ApiKeySecretTests
{
    [Fact]
    public void TryCreate_AcceptsAWellFormedKey_AndTrimsSpaces()
    {
        var raw = TestKeys.New();

        var error = ApiKeySecret.TryCreate($"  {raw}\r\n", out var key);

        Assert.Null(error);
        Assert.NotNull(key);
        using var request = new HttpRequestMessage();
        key!.ApplyTo(request);
        Assert.Equal(raw, Assert.Single(request.Headers.GetValues("x-api-key")));
    }

    [Theory]
    [InlineData("", ApiKeyFormatError.Empty)]
    [InlineData("   ", ApiKeyFormatError.Empty)]
    [InlineData("hello-world-not-a-key-at-all-123456", ApiKeyFormatError.WrongPrefix)]
    [InlineData("sk-ant-short", ApiKeyFormatError.BadCharacters)]
    public void TryCreate_RejectsMalformedValues(string raw, ApiKeyFormatError expected)
    {
        Assert.Equal(expected, ApiKeySecret.TryCreate(raw, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void TryCreate_RejectsAdminKeys()
    {
        var admin = TestKeys.New().Replace("sk-ant-api03-", "sk-ant-admin01-");
        Assert.Equal(ApiKeyFormatError.AdminKey, ApiKeySecret.TryCreate(admin, out _));
    }

    [Fact]
    public void TryCreate_RejectsALineBreakInsideTheKey()
    {
        var raw = TestKeys.New();
        var injected = raw[..20] + "\r\nX-Evil: 1" + raw[20..];
        Assert.Equal(ApiKeyFormatError.BadCharacters, ApiKeySecret.TryCreate(injected, out _));
    }

    [Fact]
    public void Key_HidesItsValue_FromToStringAndInterpolation()
    {
        var raw = TestKeys.New();
        ApiKeySecret.TryCreate(raw, out var key);

        Assert.Equal(SecretRedactor.Placeholder, key!.ToString());
        Assert.DoesNotContain(raw, $"{key}");
    }

    [Fact]
    public void Key_RefusesToBeSerialized()
    {
        ApiKeySecret.TryCreate(TestKeys.New(), out var key);

        Assert.Throws<InvalidOperationException>(() => JsonConvert.SerializeObject(key));
        Assert.Throws<InvalidOperationException>(() =>
            JsonConvert.SerializeObject(new { Holder = key })
        );
    }
}

public sealed class ApiKeyStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zentra-secrets-");
    private string BlobPath => Path.Combine(_dir.FullName, "claude.key.dpapi");

    public void Dispose() => _dir.Delete(recursive: true);

    private ApiKeyStore NewStore(bool technician) =>
        new(_dir.FullName, technician, NullLogger<ApiKeyStore>.Instance);

    [Fact]
    public void ClientMode_RefusesTheKey()
    {
        // Plan F1-08: the key never touches a client's PC, not even in memory.
        var store = NewStore(technician: false);

        Assert.Equal(ApiKeyFormatError.NotTechnicianPc, store.Set(TestKeys.New(), remember: true));

        Assert.False(store.HasKey);
        Assert.False(store.IsTechnicianPc);
        Assert.False(store.CanRemember);
        Assert.False(store.IsRemembered);
        Assert.False(File.Exists(BlobPath));
    }

    [Fact]
    public void TechnicianMode_RemembersEncrypted_AndLoadsOnNextStart()
    {
        var raw = TestKeys.New();
        NewStore(technician: true).Set(raw, remember: true);

        Assert.True(File.Exists(BlobPath));
        var blob = File.ReadAllBytes(BlobPath);
        Assert.DoesNotContain(raw, System.Text.Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain(raw, System.Text.Encoding.Unicode.GetString(blob));

        var next = NewStore(technician: true);
        Assert.True(next.HasKey);
        Assert.True(next.IsRemembered);
        using var request = new HttpRequestMessage();
        next.Current!.ApplyTo(request);
        Assert.Equal(raw, Assert.Single(request.Headers.GetValues("x-api-key")));
    }

    [Fact]
    public void TechnicianMode_WithoutRememberTicked_WritesNothing()
    {
        NewStore(technician: true).Set(TestKeys.New(), remember: false);
        Assert.False(File.Exists(BlobPath));
    }

    [Fact]
    public void StartingWithoutTheSwitch_DeletesARememberedKey()
    {
        NewStore(technician: true).Set(TestKeys.New(), remember: true);
        Assert.True(File.Exists(BlobPath));

        var client = NewStore(technician: false);

        Assert.False(client.HasKey);
        Assert.False(File.Exists(BlobPath));
    }

    [Fact]
    public void Forget_DeletesTheBlob_AndClearsMemory()
    {
        var store = NewStore(technician: true);
        store.Set(TestKeys.New(), remember: true);

        store.Forget();

        Assert.False(store.HasKey);
        Assert.False(File.Exists(BlobPath));
    }

    [Fact]
    public void Clear_DropsTheKeyFromMemory_ButKeepsTheRememberedCopy()
    {
        var store = NewStore(technician: true);
        store.Set(TestKeys.New(), remember: true);

        store.Clear();

        Assert.False(store.HasKey);
        Assert.True(File.Exists(BlobPath));
    }

    [Fact]
    public void ACorruptBlob_IsDeleted_AndNoKeyIsLoaded()
    {
        File.WriteAllBytes(BlobPath, [1, 2, 3, 4]);

        var store = NewStore(technician: true);

        Assert.False(store.HasKey);
        Assert.False(File.Exists(BlobPath));
    }

    [Fact]
    public void Set_RejectsAMalformedKey_AndKeepsThePreviousOne()
    {
        var store = NewStore(technician: true);
        store.Set(TestKeys.New(), remember: false);
        var before = store.Current;

        Assert.Equal(
            ApiKeyFormatError.WrongPrefix,
            store.Set("not-a-key-but-long-enough-0123", false)
        );
        Assert.Same(before, store.Current);
    }
}
