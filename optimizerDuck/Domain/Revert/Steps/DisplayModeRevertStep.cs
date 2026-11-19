using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Exceptions;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Domain.Revert.Steps;

/// <summary>
///     Puts a monitor back to the mode it had before ACT-MON-HZ. The monitor is found by its
///     device path (\\.\DISPLAYn changes between boots); if its refresh rate is no longer the one
///     Zentra set, the user changed it since and it is left alone.
/// </summary>
public class DisplayModeRevertStep : IRevertStep
{
    public string MonitorDevicePath { get; set; } = string.Empty;
    public string MonitorName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bpp { get; set; }
    public int Hz { get; set; }
    public int AppliedHz { get; set; }

    /// <inheritdoc />
    public string Type => "DisplayMode";

    /// <inheritdoc />
    public string Description => Loc.Instance["Revert.DisplayMode.Description", MonitorName, Hz];

    /// <inheritdoc />
    public Task<bool> ExecuteAsync(RevertContext context, ILogger logger)
    {
        if (!DisplayModeService.Restore(this, logger))
            throw new StepExecutionException(ServiceStrings.DisplayErrorNotApplied, MonitorName);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public JObject ToData() =>
        new()
        {
            [nameof(MonitorDevicePath)] = MonitorDevicePath,
            [nameof(MonitorName)] = MonitorName,
            [nameof(Width)] = Width,
            [nameof(Height)] = Height,
            [nameof(Bpp)] = Bpp,
            [nameof(Hz)] = Hz,
            [nameof(AppliedHz)] = AppliedHz,
        };

    public static DisplayModeRevertStep FromData(JObject data) =>
        new()
        {
            MonitorDevicePath = data.Value<string>(nameof(MonitorDevicePath)) ?? string.Empty,
            MonitorName = data.Value<string>(nameof(MonitorName)) ?? string.Empty,
            Width = data.Value<int>(nameof(Width)),
            Height = data.Value<int>(nameof(Height)),
            Bpp = data.Value<int>(nameof(Bpp)),
            Hz = data.Value<int>(nameof(Hz)),
            AppliedHz = data.Value<int>(nameof(AppliedHz)),
        };
}
