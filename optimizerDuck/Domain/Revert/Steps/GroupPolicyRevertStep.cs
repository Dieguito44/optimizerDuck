using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Exceptions;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Domain.Revert.Steps;

/// <summary>
///     Puts a local Group Policy entry back as it was before Zentra set it (or removes it), and
///     removes the policy files Zentra created when nothing else is left in them.
/// </summary>
public class GroupPolicyRevertStep : IRevertStep
{
    public string Key { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;

    /// <summary>The entry that was there before, or null when there was none.</summary>
    public uint? PreviousType { get; set; }

    public byte[]? PreviousData { get; set; }
    public bool CreatedPolicyFile { get; set; }
    public bool CreatedGptIni { get; set; }

    /// <inheritdoc />
    public string Type => "GroupPolicy";

    /// <inheritdoc />
    public string Description => Loc.Instance["Revert.GroupPolicy.Description", Key, ValueName];

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(RevertContext context, ILogger logger)
    {
        if (!await RestoreAsync(logger))
            throw new StepExecutionException(
                ServiceStrings.GroupPolicyErrorNotApplied,
                string.Empty
            );
        return true;
    }

    internal Task<bool> RestoreAsync(ILogger logger) =>
        LocalGroupPolicyService.RestoreAsync(this, logger);

    /// <inheritdoc />
    public JObject ToData() =>
        new()
        {
            [nameof(Key)] = Key,
            [nameof(ValueName)] = ValueName,
            [nameof(PreviousType)] = PreviousType is { } t ? new JValue(t) : JValue.CreateNull(),
            [nameof(PreviousData)] = PreviousData is { } d
                ? Convert.ToBase64String(d)
                : JValue.CreateNull(),
            [nameof(CreatedPolicyFile)] = CreatedPolicyFile,
            [nameof(CreatedGptIni)] = CreatedGptIni,
        };

    public static GroupPolicyRevertStep FromData(JObject data) =>
        new()
        {
            Key = data.Value<string>(nameof(Key)) ?? string.Empty,
            ValueName = data.Value<string>(nameof(ValueName)) ?? string.Empty,
            PreviousType = data[nameof(PreviousType)] is { Type: not JTokenType.Null } t
                ? t.Value<uint>()
                : null,
            PreviousData = data[nameof(PreviousData)] is { Type: JTokenType.String } d
                ? Convert.FromBase64String(d.Value<string>()!)
                : null,
            CreatedPolicyFile = data.Value<bool?>(nameof(CreatedPolicyFile)) ?? false,
            CreatedGptIni = data.Value<bool?>(nameof(CreatedGptIni)) ?? false,
        };
}
