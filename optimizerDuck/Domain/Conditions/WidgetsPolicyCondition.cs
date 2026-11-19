using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Conditions;

/// <summary>
///     The "Allow widgets" policy (NewsAndInterests/AllowNewsAndInterests) exists on Windows 11
///     Pro, Enterprise and Education, not on Home (Microsoft's Policy CSP reference). An unknown
///     edition is let through: the setting checks that Windows applied the policy anyway.
/// </summary>
public sealed class WidgetsPolicyCondition : ConditionBase
{
    public override ConditionResult Evaluate(SystemInfo snapshot) =>
        TryGetOsBuild(snapshot, out var build)
        && build >= WindowsBuilds.Windows11
        && snapshot.Windows.Edition != WindowsEdition.Home
            ? ConditionResult.Available
            : ConditionResult.Unsupported(
                () => Loc.Instance["Condition.WidgetsPolicy.Title"],
                () => Loc.Instance["Condition.WidgetsPolicy.Description"]
            );
}
