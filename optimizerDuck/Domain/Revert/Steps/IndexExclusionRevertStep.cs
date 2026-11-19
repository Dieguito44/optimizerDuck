using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Domain.Revert.Steps;

/// <summary>Removes a Windows Search exclusion Zentra added (ACT-INDEX-EXCLUDE).</summary>
public class IndexExclusionRevertStep : IRevertStep
{
    public string Folder { get; set; } = string.Empty;

    /// <inheritdoc />
    public string Type => "IndexExclusion";

    /// <inheritdoc />
    public string Description => Loc.Instance["Revert.IndexExclusion.Description", Folder];

    /// <inheritdoc />
    public Task<bool> ExecuteAsync(RevertContext context, ILogger logger)
    {
        SearchIndexService.RemoveExclusion(Folder);
        logger.LogInformation("[INDEX][REVERT] {Folder} follows the default scope again", Folder);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public JObject ToData() => new() { [nameof(Folder)] = Folder };

    public static IndexExclusionRevertStep FromData(JObject data) =>
        new() { Folder = data.Value<string>(nameof(Folder)) ?? string.Empty };
}
