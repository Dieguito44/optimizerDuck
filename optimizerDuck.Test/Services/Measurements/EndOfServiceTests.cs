using optimizerDuck.Services.Measurements;

namespace optimizerDuck.Test.Services.Measurements;

/// <summary>Version 1.1.2: "Terminar servicio" removes Zentra (and the key it carries) from the client's PC.</summary>
public class EndOfServiceTests
{
    [Fact]
    public void TheScript_WaitsForZentra_ThenDeletesOnlyThePlan()
    {
        var plan = new EndOfService.CleanupPlan(
            @"C:\Users\ana\AppData\Local\Temp\.net\Zentra\abc123",
            @"C:\Users\ana\AppData\Local\Zentra",
            @"C:\Users\ana\Desktop\Zentra.exe",
            @"C:\Users\ana\Desktop\Zentra-Sesiones"
        );

        var script = EndOfService.Script(plan, 4242);

        Assert.Contains("PID eq 4242", script);
        Assert.Contains(@"rmdir /s /q ""C:\Users\ana\AppData\Local\Temp\.net\Zentra\abc123""", script);
        Assert.Contains(@"rmdir /s /q ""C:\Users\ana\AppData\Local\Zentra""", script);
        Assert.Contains(@"del /f /q ""C:\Users\ana\Desktop\Zentra.exe""", script);
        Assert.DoesNotContain("ProgramData", script); // revert data and history stay
        Assert.DoesNotContain("Zentra-Sesiones", script); // the copied sessions stay
    }

    [Fact]
    public void FromAUsbStick_TheExeIsNotDeleted()
    {
        var plan = new EndOfService.CleanupPlan(null, @"C:\Users\ana\AppData\Local\Zentra", null, null);
        Assert.DoesNotContain("del /f /q \"", EndOfService.Script(plan, 1).Replace("del /f /q \"%~f0\"", ""));
    }

    [Theory]
    [InlineData(@"C:\Users\ana\AppData\Local\Zentra", true)]
    [InlineData(@"C:\Users\a&b\AppData\Local\Zentra", false)] // cmd would split the command
    [InlineData(@"C:\Users\100%\AppData", false)]
    [InlineData(@"C:\x""y", false)]
    [InlineData("", false)]
    public void OnlyPathsCmdCanQuoteSafely_AreDeleted(string path, bool safe) =>
        Assert.Equal(safe, EndOfService.IsSafeForCmd(path));

    [Fact]
    public void TheTestRun_IsNotAnUnpackedSingleFile_SoNothingThereIsDeleted()
    {
        // Tests run from bin\, not from %TEMP%\.net: the plan must not point at it.
        Assert.Null(EndOfService.Plan().ExtractedCopy);
        Assert.NotNull(EndOfService.CurrentMachineTag);
        Assert.Equal(12, EndOfService.CurrentMachineTag!.Length);
    }
}
