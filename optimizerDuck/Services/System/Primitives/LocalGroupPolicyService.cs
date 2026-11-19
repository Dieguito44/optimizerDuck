using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Revert.Steps;
using optimizerDuck.Services.Repair;

namespace optimizerDuck.Services.System.Primitives;

/// <summary>One entry of a Registry.pol file (the PReg format gpedit.msc writes).</summary>
public sealed record PolicyEntry(string Key, string ValueName, uint Type, byte[] Data)
{
    public const uint RegDword = 4;

    public static PolicyEntry Dword(string key, string valueName, uint value) =>
        new(key, valueName, RegDword, BitConverter.GetBytes(value));

    public bool Matches(string key, string valueName) =>
        string.Equals(Key, key, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ValueName, valueName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     Reads and writes the PReg format: "PReg", version 1, then entries
///     <c>[key\0;value\0;type;size;data]</c> with every character in UTF-16LE and type and size
///     as little-endian DWORDs.
/// </summary>
public static class PolicyFile
{
    private static readonly byte[] Signature = "PReg"u8.ToArray();

    public static byte[] Write(IEnumerable<PolicyEntry> entries)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write(Signature);
        w.Write(1u);
        foreach (var e in entries)
        {
            w.Write('[');
            w.Write($"{e.Key}\0".ToCharArray());
            w.Write(';');
            w.Write($"{e.ValueName}\0".ToCharArray());
            w.Write(';');
            w.Write(e.Type);
            w.Write(';');
            w.Write((uint)e.Data.Length);
            w.Write(';');
            w.Write(e.Data);
            w.Write(']');
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <exception cref="InvalidDataException">The bytes are not a PReg file.</exception>
    public static List<PolicyEntry> Read(byte[] bytes)
    {
        if (
            bytes.Length < 8
            || !bytes.AsSpan(0, 4).SequenceEqual(Signature)
            || BitConverter.ToUInt32(bytes, 4) != 1
        )
            throw new InvalidDataException("Not a PReg version 1 file.");

        var entries = new List<PolicyEntry>();
        using var r = new BinaryReader(
            new MemoryStream(bytes, 8, bytes.Length - 8),
            Encoding.Unicode
        );
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            Expect(r, '[');
            var key = ReadString(r);
            Expect(r, ';');
            var value = ReadString(r);
            Expect(r, ';');
            var type = r.ReadUInt32();
            Expect(r, ';');
            var size = r.ReadUInt32();
            Expect(r, ';');
            var data = r.ReadBytes(checked((int)size));
            if (data.Length != size)
                throw new InvalidDataException("Truncated PReg entry.");
            Expect(r, ']');
            entries.Add(new PolicyEntry(key, value, type, data));
        }
        return entries;
    }

    private static string ReadString(BinaryReader r)
    {
        var sb = new StringBuilder();
        char c;
        while ((c = r.ReadChar()) != '\0')
            sb.Append(c);
        return sb.ToString();
    }

    private static void Expect(BinaryReader r, char expected)
    {
        if (r.ReadChar() != expected)
            throw new InvalidDataException($"PReg: expected '{expected}'.");
    }
}

/// <summary>The gpt.ini of the local policy: version and extensions, as gpedit.msc writes them.</summary>
public static class GptIni
{
    /// <summary>Administrative Templates (registry) extension and its gpedit snap-in.</summary>
    public const string RegistryExtensions =
        "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]";

    /// <summary>
    ///     Returns gpt.ini with the registry extension listed, gPCFunctionalityVersion=2 (without
    ///     it Windows reports the local policy as empty and applies nothing, checked on Diego's PC)
    ///     and the machine version (low 16 bits) raised by one so the policy service sees a change.
    /// </summary>
    public static string Update(string? current)
    {
        var lines = (current ?? string.Empty)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (!lines.Any(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase)))
            lines.Insert(0, "[General]");

        uint version = 0;
        string extensions = string.Empty;
        foreach (var line in lines)
        {
            if (line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))
                uint.TryParse(line["Version=".Length..], out version);
            if (line.StartsWith("gPCMachineExtensionNames=", StringComparison.OrdinalIgnoreCase))
                extensions = line["gPCMachineExtensionNames=".Length..];
        }

        if (!extensions.Contains(RegistryExtensions, StringComparison.OrdinalIgnoreCase))
            extensions = RegistryExtensions + extensions;
        var machine = ((version & 0xFFFF) + 1) & 0xFFFF;
        version = (version & 0xFFFF0000) | (machine == 0 ? 1 : machine);

        lines.RemoveAll(l =>
            l.StartsWith("Version=", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("gPCMachineExtensionNames=", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("gPCFunctionalityVersion=", StringComparison.OrdinalIgnoreCase)
        );
        var general = lines.FindIndex(l =>
            l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase)
        );
        lines.InsertRange(
            general + 1,
            [
                "gPCFunctionalityVersion=2",
                $"gPCMachineExtensionNames={extensions}",
                $"Version={version}",
            ]
        );
        return string.Join("\r\n", lines) + "\r\n";
    }
}

/// <summary>
///     Machine policies through the local Group Policy, the way gpedit.msc sets them: the entry goes
///     in Registry.pol and the Group Policy service writes the registry. This is the supported way
///     for policies Windows protects (the User Choice Protection Driver blocks direct writes to the
///     widgets policy, even from an administrator; checked on Diego's PC, 25H2, 19/11/2026).
/// </summary>
public static class LocalGroupPolicyService
{
    public static string PolicyDirectory { get; internal set; } =
        Path.Combine(Environment.SystemDirectory, "GroupPolicy");

    /// <summary>Runs `gpupdate /target:computer /force`. Replaceable in tests.</summary>
    internal static Func<ILogger, Task<bool>> RefreshPolicy { get; set; } = RunGpUpdateAsync;

    /// <summary>Reads the machine policy value Windows applied. Replaceable in tests.</summary>
    internal static Func<string, string, object?> ReadApplied { get; set; } =
        (key, value) =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value);
        };

    private static string PolPath => Path.Combine(PolicyDirectory, "Machine", "Registry.pol");
    private static string IniPath => Path.Combine(PolicyDirectory, "gpt.ini");

    /// <summary>Sets a machine DWORD policy, refreshes policy and checks Windows applied it.</summary>
    public static async Task<OpResult> SetMachineDwordAsync(
        OpCall call,
        string key,
        string valueName,
        uint value
    )
    {
        if (call.Preview is { } plan)
        {
            using var current = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            plan.Add(
                PlannedChangeKind.Policy,
                $@"{key}\{valueName}",
                current?.GetValue(valueName)?.ToString(),
                value.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
            );
            await Task.CompletedTask.ConfigureAwait(false);
            return OpResult.Success();
        }
        var description = $@"HKLM\{key}:{valueName} = {value} (Group Policy)";
        try
        {
            var existed = File.Exists(PolPath);
            var iniExisted = File.Exists(IniPath);
            var entries = existed ? PolicyFile.Read(await File.ReadAllBytesAsync(PolPath)) : [];
            var previous = entries.FirstOrDefault(e => e.Matches(key, valueName));

            if (
                previous is { Type: PolicyEntry.RegDword } p
                && BitConverter.ToUInt32(p.Data) == value
                && Equals(ReadApplied(key, valueName), unchecked((int)value))
            )
            {
                call.Changes.AddSkip(ServiceStrings.GroupPolicyName, description);
                return OpResult.Success();
            }

            var revert = new GroupPolicyRevertStep
            {
                Key = key,
                ValueName = valueName,
                PreviousType = previous?.Type,
                PreviousData = previous?.Data,
                CreatedPolicyFile = !existed,
                CreatedGptIni = !iniExisted,
            };

            entries.RemoveAll(e => e.Matches(key, valueName));
            entries.Add(PolicyEntry.Dword(key, valueName, value));
            await SaveAsync(entries);

            if (
                !await RefreshPolicy(call.Logger)
                || !Equals(ReadApplied(key, valueName), unchecked((int)value))
            )
            {
                // Leave the policy files as they were: a policy Windows did not apply must not
                // sit there waiting for the next refresh.
                await revert.RestoreAsync(call.Logger);
                var error = ServiceStrings.GroupPolicyErrorNotApplied;
                call.Changes.Add(ServiceStrings.GroupPolicyName, description, false, error: error);
                return OpResult.Fail(error);
            }

            call.Logger.LogInformation("[GPO] {Description} applied", description);
            call.Changes.Add(ServiceStrings.GroupPolicyName, description, true, revert);
            return OpResult.Success(revert);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            call.Logger.LogWarning(ex, "[GPO] {Description} failed", description);
            call.Changes.Add(
                ServiceStrings.GroupPolicyName,
                description,
                false,
                error: ServiceStrings.GroupPolicyErrorFiles,
                errorDetail: ex.Message
            );
            return OpResult.Fail(ServiceStrings.GroupPolicyErrorFiles, ex.Message);
        }
    }

    /// <summary>Puts one entry back as it was (or removes it) and refreshes policy.</summary>
    internal static async Task<bool> RestoreAsync(GroupPolicyRevertStep step, ILogger logger)
    {
        var entries = File.Exists(PolPath)
            ? PolicyFile.Read(await File.ReadAllBytesAsync(PolPath))
            : [];
        entries.RemoveAll(e => e.Matches(step.Key, step.ValueName));
        if (step.PreviousType is { } type && step.PreviousData is { } data)
            entries.Add(new PolicyEntry(step.Key, step.ValueName, type, data));

        if (entries.Count == 0 && step.CreatedPolicyFile)
        {
            File.Delete(PolPath);
            if (step.CreatedGptIni)
                File.Delete(IniPath);
            else
                await File.WriteAllTextAsync(
                    IniPath,
                    GptIni.Update(await File.ReadAllTextAsync(IniPath)),
                    Encoding.ASCII
                );
        }
        else
        {
            await SaveAsync(entries);
        }

        var refreshed = await RefreshPolicy(logger);
        logger.LogInformation(
            "[GPO] HKLM\\{Key}:{Value} restored (refresh ok: {Ok})",
            step.Key,
            step.ValueName,
            refreshed
        );
        return refreshed;
    }

    private static async Task SaveAsync(List<PolicyEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolPath)!);
        var temp = PolPath + ".tmp";
        await File.WriteAllBytesAsync(temp, PolicyFile.Write(entries));
        File.Move(temp, PolPath, overwrite: true);
        var ini = File.Exists(IniPath) ? await File.ReadAllTextAsync(IniPath) : null;
        await File.WriteAllTextAsync(IniPath, GptIni.Update(ini), Encoding.ASCII);
    }

    private static async Task<bool> RunGpUpdateAsync(ILogger logger)
    {
        var result = await LongConsoleRunner.RunAsync(
            "gpupdate.exe",
            "/target:computer /force",
            TimeSpan.FromMinutes(3),
            null,
            logger,
            CancellationToken.None
        );
        logger.LogInformation(
            "[GPO] gpupdate exit {Code}{TimedOut}",
            result.ExitCode,
            result.TimedOut ? " (timed out)" : ""
        );
        return result.ExitCode == 0 && !result.TimedOut;
    }
}
