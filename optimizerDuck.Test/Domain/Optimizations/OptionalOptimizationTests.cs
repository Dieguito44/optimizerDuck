using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Domain.Optimizations;

/// <summary>
///     Phase 7 options (plan section 1.1, "Opcional, con consentimiento escrito"): without a
///     person ticking the consent window nothing is applied, the Windows Update pause never
///     goes past 7 days, and the Defender exclusion never reaches a drive, Windows, Program
///     Files, Downloads, Temp, the Desktop or user folders.
/// </summary>
public class OptionalOptimizationTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(
        Environment.SpecialFolder.ProgramFiles
    );

    private static readonly string[] Protected =
    [
        @"C:\Windows",
        ProgramFiles,
        @"C:\ProgramData",
        @"C:\Users\cliente",
        @"C:\Users\cliente\Desktop",
        @"C:\Users\cliente\Documents",
        @"C:\Users\cliente\AppData\Roaming",
        @"C:\Users\cliente\AppData\Local",
        @"C:\Users\cliente\AppData\Local\Temp\",
        @"C:\Users\cliente\Downloads",
        string.Empty,
    ];

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"D:")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\ProgramData\Steam")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\cliente")]
    [InlineData(@"C:\Users\cliente\Downloads")]
    [InlineData(@"C:\Users\cliente\Downloads\Juegos")]
    [InlineData(@"C:\Users\cliente\Desktop\juego")]
    [InlineData(@"C:\Users\cliente\AppData\Local\Temp\x")]
    [InlineData(@"C:\Users\cliente\AppData\Roaming\Epic")]
    public void DefenderExclusion_NeverAProtectedFolder(string folder)
    {
        Assert.False(GameLibraries.IsSafeToExclude(folder, Protected));
    }

    [Fact]
    public void DefenderExclusion_NeverProgramFilesItself()
    {
        Assert.False(GameLibraries.IsSafeToExclude(ProgramFiles, Protected));
        Assert.False(GameLibraries.IsSafeToExclude(ProgramFiles + @"\", Protected));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\")]
    [InlineData(@"E:\Epic Games\Fortnite")]
    public void DefenderExclusion_GameLibrariesAreAllowed(string folder)
    {
        Assert.True(GameLibraries.IsSafeToExclude(folder, Protected));
    }

    [Fact]
    public void DefenderExclusion_AGameInsideProgramFilesIsAllowed()
    {
        Assert.True(
            GameLibraries.IsSafeToExclude(
                Path.Combine(ProgramFiles, "Epic Games", "Fortnite"),
                Protected
            )
        );
    }

    [Fact]
    public void DefenderExclusion_RealProtectedFoldersAreRefused()
    {
        foreach (
            var folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.GetTempPath(),
                Path.GetPathRoot(Environment.SystemDirectory)!,
            }
        )
            Assert.False(GameLibraries.IsSafeToExclude(folder), folder);
    }

    [Fact]
    public void PauseValues_AreTheNativeSixValuesAndEndWithinSevenDays()
    {
        var start = new DateTime(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc);
        var end = start.AddDays(SecurityAndPrivacy.PauseWindowsUpdate7Days.MaxDays);

        var values = SecurityAndPrivacy.PauseWindowsUpdate7Days.PauseValues(start, end);

        Assert.Equal(6, values.Count);
        Assert.Equal(7, SecurityAndPrivacy.PauseWindowsUpdate7Days.MaxDays);
        Assert.Contains(("PauseUpdatesStartTime", "2026-11-19T10:30:00Z"), values);
        Assert.Contains(("PauseUpdatesExpiryTime", "2026-11-26T10:30:00Z"), values);
        foreach (var (_, value) in values)
        {
            var parsed = DateTime.ParseExact(
                value,
                "yyyy-MM-ddTHH:mm:ssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal
            );
            Assert.InRange(parsed, start, start.AddDays(7));
        }
    }

    [Fact]
    public void PauseValues_IgnoreTheUiCalendar()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); // Umm al-Qura calendar
            var start = new DateTime(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc);
            var values = SecurityAndPrivacy.PauseWindowsUpdate7Days.PauseValues(
                start,
                start.AddDays(7)
            );
            Assert.Contains(("PauseUpdatesStartTime", "2026-11-19T10:30:00Z"), values);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ClientConsent_WithoutAWindowTheAnswerIsNo()
    {
        Assert.False(await ClientConsent.Ask("title", "body"));
    }

    public static TheoryData<IOptimization> ConsentGated =>
        [new SecurityAndPrivacy.PauseWindowsUpdate7Days(), new Performance.AdvancedGameMode()];

    [Theory]
    [MemberData(nameof(ConsentGated))]
    public async Task WithoutConsent_NothingIsWritten(IOptimization optimization)
    {
        var context = new OptimizationContext
        {
            Snapshot = SystemInfo.Unknown,
            StreamService = new StreamService(NullLogger<StreamService>.Instance),
            Shell = TestShell.New(),
            PowerPlans = new PowerPlanService(NullLogger<PowerPlanService>.Instance),
            Logger = NullLogger.Instance,
        };

        await optimization.ApplyAsync(new Progress<ProcessingProgress>(), context);

        Assert.False(context.Changes.ModifiedSystem);
        Assert.Contains(context.Changes.Changes, c => c.Kind == ChangeKind.Refused);
        Assert.DoesNotContain(context.Changes.Changes, c => c.Revert is not null);
    }
}
