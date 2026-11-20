using optimizerDuck.Services.Cli;
using optimizerDuck.Services.Measurements;

namespace optimizerDuck.Test.Services.Measurements;

/// <summary>Version 1.1.2: "Reiniciar y continuar", and switches that open the window.</summary>
public class ContinueAfterRestartTests
{
    [Theory]
    [InlineData(new[] { "--equipo-tecnico" }, false)] // it never opened the window before 1.1.2
    [InlineData(new[] { "--continuar-sesion" }, false)]
    [InlineData(new[] { "--diagnose" }, true)]
    [InlineData(new[] { "--equipo-tecnico", "--diagnose" }, true)]
    [InlineData(new string[0], false)]
    public void WindowSwitches_AreNotCommandLineRuns(string[] args, bool cli) =>
        Assert.Equal(cli, CliOptions.IsCliInvocation(args));

    [Fact]
    public void TheTask_OpensZentraOnce_ElevatedAtTheTechniciansSignIn()
    {
        var cmd = ContinueAfterRestart.RegisterCommand(@"D:\Zentra's USB\Zentra.exe", @"PC\diego");

        Assert.Contains(@"-Execute 'D:\Zentra''s USB\Zentra.exe'", cmd); // quotes doubled
        Assert.Contains("-Argument '--continuar-sesion'", cmd);
        Assert.Contains(@"-AtLogOn -User 'PC\diego'", cmd);
        Assert.Contains("-RunLevel Highest", cmd);
    }
}
