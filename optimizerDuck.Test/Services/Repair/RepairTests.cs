using System.Reflection;
using System.Text;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Repair;
using optimizerDuck.Services.System;
using optimizerDuck.Test.TestDoubles;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Repair;

public class ConsoleOutputTests
{
    static ConsoleOutputTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Decode_Utf16LeLikeSfc()
    {
        const string text = "Verificación 100% completada.";
        Assert.Equal(text, ConsoleOutput.Decode(Encoding.Unicode.GetBytes(text), 850));
    }

    [Fact]
    public void Decode_OemCodePageLikeDismInSpanish()
    {
        const string text = "La operación se completó correctamente.";
        var oem = Encoding.GetEncoding(850).GetBytes(text);
        Assert.Equal(text, ConsoleOutput.Decode(oem, 850));
    }

    [Fact]
    public void Decode_Utf8()
    {
        const string text = "Reparación terminada";
        Assert.Equal(text, ConsoleOutput.Decode(Encoding.UTF8.GetBytes(text), 850));
        Assert.Equal(string.Empty, ConsoleOutput.Decode([], 850));
    }

    [Theory]
    [InlineData("[=====                      10.0%                          ]", 10)]
    [InlineData("[==========================100.0%==========================]", 100)]
    [InlineData("[===     45,3%   ]\r[======   62,8%   ]", 62)]
    [InlineData("Verificación 67% completada.", 67)]
    [InlineData("Iniciando el examen del sistema.", null)]
    public void LastPercent_ReadsTheLatestValue(string text, int? expected)
    {
        Assert.Equal(expected, ConsoleOutput.LastPercent(text));
    }

    [Fact]
    public void Tail_DropsProgressBars()
    {
        var tail = ConsoleOutput.Tail(
            "Herramienta DISM\n[=====  50.0%  ]\r[==== 100.0% ====]\nLa operación se completó correctamente.\n",
            2
        );
        Assert.Equal(
            "Herramienta DISM" + Environment.NewLine + "La operación se completó correctamente.",
            tail
        );
    }
}

public class RepairServiceTests
{
    [Theory]
    [InlineData("Healthy", WindowsImageHealth.Healthy)]
    [InlineData(" Repairable\r\n", WindowsImageHealth.Repairable)]
    [InlineData("NonRepairable", WindowsImageHealth.NonRepairable)]
    [InlineData("", null)]
    [InlineData("Acceso denegado", null)]
    public void ParseImageHealth(string text, WindowsImageHealth? expected)
    {
        Assert.Equal(expected, RepairService.ParseImageHealth(text));
    }

    [Theory]
    [InlineData(0, false, RepairOutcome.Ok)]
    [InlineData(1, false, RepairOutcome.Repaired)]
    [InlineData(2, false, RepairOutcome.Repaired)]
    [InlineData(3, false, RepairOutcome.Problem)]
    [InlineData(-1, true, RepairOutcome.Failed)]
    public void ChkdskExitCodes(int exitCode, bool timedOut, RepairOutcome expected)
    {
        Assert.Equal(expected, RepairService.ChkdskOutcome(exitCode, timedOut));
    }

    [Fact]
    public void WinCorrupt_RepairableImage_IsHighWithRepairAction()
    {
        var damaged = DiegoPc() with
        {
            ImageHealth = Measured<WindowsImageHealth>.Of(WindowsImageHealth.Repairable),
        };

        var result = new WinCorruptRule().Evaluate(damaged, Refs);

        Assert.Equal(Severity.High, result.Finding!.Severity);
        Assert.Equal([ActionIds.RepairWindows], result.Finding.ActionIds);
        Assert.Equal(RuleOutcome.Pass, new WinCorruptRule().Evaluate(DiegoPc(), Refs).Outcome);
    }
}

public class StorageCategoryTests
{
    private static OptimizationAttribute Meta<T>() =>
        typeof(T).GetCustomAttribute<OptimizationAttribute>()!;

    [Fact]
    public void RiskLevels_FollowThePlan()
    {
        Assert.Equal(OptimizationRisk.Safe, Meta<Storage.DismComponentCleanup>().Risk);
        Assert.Equal(OptimizationRisk.Safe, Meta<Storage.EnableStorageSense>().Risk);
        Assert.Equal(OptimizationRisk.Safe, Meta<Storage.EnableTrim>().Risk);
        Assert.Equal(OptimizationRisk.Moderate, Meta<Storage.EnableCompactOs>().Risk);
        Assert.Equal(OptimizationRisk.Risky, Meta<Storage.DismResetBase>().Risk);
        Assert.Equal(OptimizationRisk.Safe, Meta<Performance.TcpAutoTuningNormal>().Risk);
        Assert.Equal(typeof(SmallSystemDiskCondition), Meta<Storage.EnableCompactOs>().Condition);
    }

    [Theory]
    [InlineData(238L * 1024 * 1024 * 1024, ConditionState.Available)] // a "256 GB" SSD
    [InlineData(464L * 1024 * 1024 * 1024, ConditionState.Unsupported)] // the development PC
    public void CompactOs_OnlyOnSmallSystemDisks(long totalBytes, ConditionState expected)
    {
        var snapshot = SystemInfo.Unknown with
        {
            Storage = new StorageInfo
            {
                Volumes =
                [
                    new StorageVolume
                    {
                        DriveLetter = "C:",
                        IsSystemDrive = true,
                        TotalBytes = totalBytes,
                    },
                ],
            },
        };

        Assert.Equal(expected, new SmallSystemDiskCondition().Evaluate(snapshot).State);
    }

    [Fact]
    public void CompactOs_UnknownDisk_DoesNotBlock()
    {
        Assert.Equal(
            ConditionState.Error,
            new SmallSystemDiskCondition().Evaluate(SystemInfo.Unknown).State
        );
    }

    [Theory]
    [InlineData("Normal", "normal")]
    [InlineData("Disabled\r\n", "disabled")]
    [InlineData("HighlyRestricted", "highlyrestricted")]
    [InlineData("algo raro", null)]
    public void Tcp_ReadsTheLevelWithoutDependingOnLanguage(string level, string? expected)
    {
        Assert.Equal(expected, Performance.TcpAutoTuningNormal.NetshLevel(level));
    }

    [Fact]
    public void Tcp_RevertPutsThePreviousLevelBack()
    {
        Assert.Equal(
            "netsh int tcp set global autotuninglevel=restricted",
            Performance.TcpAutoTuningNormal.RevertCommand("restricted")
        );
    }
}
