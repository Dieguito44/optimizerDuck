using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
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
        @"C:\Users", // every profile, Public included
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
    // Fable security review, phase 7 (A2): other profiles, Public, odd path shapes.
    [InlineData(@"C:\Users\Public\Downloads")]
    [InlineData(@"C:\Users\Public\Desktop")]
    [InlineData(@"C:\Users\otro\Downloads")]
    [InlineData(@"C:\Users\otro\SteamLibrary\steamapps\common")]
    [InlineData(@"\\srv\share\juegos")]
    [InlineData(@"\\localhost\C$\Users\cliente")]
    [InlineData(@"\\?\C:\Users\cliente\Downloads\steamapps\common")]
    [InlineData(@"\\?\D:\SteamLibrary\steamapps\common")]
    [InlineData(@"C:\Users\cliente\DOWNLO~1\x")]
    [InlineData(@"D:\JUEGOS~1\steamapps\common")]
    [InlineData(@"D:\Juegos\..\Descargas")]
    [InlineData(@"D:\Juegos\.\Fortnite")]
    [InlineData(@"D:\Juegos\\Fortnite")]
    [InlineData(@"SteamLibrary\steamapps\common")]
    [InlineData(@"D:SteamLibrary\steamapps\common")]
    [InlineData(@"D:\Juegos\$env")]
    [InlineData(@"D:\Juegos\%TEMP%")]
    [InlineData(@"D:\Juegos\*")]
    [InlineData(@"D:\Juegos\a?b")]
    [InlineData(@"D:\Juegos\a;b")]
    [InlineData(@"D:\Juegos\a`b")]
    [InlineData("D:\\Juegos\\a\"b")]
    [InlineData("D:\\Juegos\\a\tb")]
    [InlineData("")]
    [InlineData("   ")]
    public void DefenderExclusion_NeverAProtectedFolder(string folder)
    {
        Assert.False(GameLibraries.IsSafeToExclude(folder, Protected));
    }

    /// <summary>Fable security review, phase 7 (A1): every quote PowerShell accepts.</summary>
    [Theory]
    [InlineData("D:\\x'\\steamapps\\common")]
    [InlineData("D:\\x\u2018;Write-Output INJECTED;\u2018\\steamapps\\common")]
    [InlineData("D:\\x\u2019;Write-Output INJECTED;\u2019\\steamapps\\common")]
    [InlineData("D:\\x\u201A;Write-Output INJECTED;\u201A\\steamapps\\common")]
    [InlineData("D:\\x\u201B;Write-Output INJECTED;\u201B\\steamapps\\common")]
    public void DefenderExclusion_NeverAPathWithAQuote(string folder)
    {
        Assert.False(GameLibraries.IsSafeToExclude(folder, Protected));
    }

    [Theory]
    [InlineData("C:\\Program Files\\Windows Defender")]
    [InlineData("C:\\Program Files\\WindowsApps")]
    [InlineData("C:\\Program Files\\Common Files")]
    [InlineData("C:\\Program Files\\Epic Games")]
    [InlineData("C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame")]
    public void DefenderExclusion_InsideProgramFilesOnlyGames(string folder)
    {
        var path = folder.Replace("C:\\Program Files", ProgramFiles);
        Assert.False(GameLibraries.IsSafeToExclude(path, Protected));
    }

    [Fact]
    public void DefenderExclusion_SteamInsideProgramFilesIsAllowed()
    {
        Assert.True(
            GameLibraries.IsSafeToExclude(
                Path.Combine(ProgramFiles, "Steam", "steamapps", "common"),
                Protected
            )
        );
    }

    [Fact]
    public void DefenderExclusion_OnDiskAFolderWithoutLibraryMarksIsRefused()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "zentra-test-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(folder);
        try
        {
            // Temp is protected anyway; and an empty folder has no Steam or Epic marks.
            Assert.False(GameLibraries.IsSafeToExclude(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("D:\\Juegos", "'D:\\Juegos'")]
    [InlineData("a'b", "'a''b'")]
    [InlineData(
        "a\u2018b\u2019c\u201Ad\u201Be",
        "'a\u2018\u2018b\u2019\u2019c\u201A\u201Ad\u201B\u201Be'"
    )]
    public void PowerShellQuote_DoublesEveryQuoteCharacter(string value, string expected)
    {
        Assert.Equal(expected, PowerShellText.Quote(value));
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

    [Fact]
    public void PauseCooldown_NoPreviousPauseIsAllowed()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(SecurityAndPrivacy.PauseWindowsUpdate7Days.BlockedUntil(now, null, null));
    }

    [Fact]
    public void PauseCooldown_ARunningPauseBlocksUntilSevenDaysAfterItEnds()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var end = now.AddDays(3);
        Assert.Equal(
            end.AddDays(SecurityAndPrivacy.PauseWindowsUpdate7Days.CooldownDays),
            SecurityAndPrivacy.PauseWindowsUpdate7Days.BlockedUntil(now, end, null)
        );
    }

    [Fact]
    public void PauseCooldown_ZentrasOwnNoteSurvivesARevert()
    {
        // Apply, revert (the registry values are gone), apply again: still refused.
        var now = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var zentraEnd = now.AddDays(6);
        Assert.NotNull(
            SecurityAndPrivacy.PauseWindowsUpdate7Days.BlockedUntil(now, null, zentraEnd)
        );
        Assert.Equal(
            zentraEnd.AddDays(7),
            SecurityAndPrivacy.PauseWindowsUpdate7Days.BlockedUntil(
                now,
                now.AddDays(-30),
                zentraEnd
            )
        );
    }

    [Fact]
    public void PauseCooldown_AnOldPauseNoLongerBlocks()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(
            SecurityAndPrivacy.PauseWindowsUpdate7Days.BlockedUntil(
                now,
                now.AddDays(-8),
                now.AddDays(-7.5)
            )
        );
    }

    [Fact]
    public void PauseCooldown_ReadsTheNativeFormat()
    {
        Assert.Equal(
            new DateTime(2026, 11, 26, 10, 30, 0, DateTimeKind.Utc),
            SecurityAndPrivacy.PauseWindowsUpdate7Days.ParseUtc("2026-11-26T10:30:00Z")
        );
        Assert.Null(SecurityAndPrivacy.PauseWindowsUpdate7Days.ParseUtc("mañana"));
        Assert.Null(SecurityAndPrivacy.PauseWindowsUpdate7Days.ParseUtc(null));
    }

    /// <summary>The owner category is what the registry assigns at startup (names, messages).</summary>
    public static TheoryData<IOptimization> ConsentGated =>
        [
            new SecurityAndPrivacy.PauseWindowsUpdate7Days
            {
                OwnerType = typeof(SecurityAndPrivacy),
            },
            new SecurityAndPrivacy.DefenderGameLibraryExclusion
            {
                OwnerType = typeof(SecurityAndPrivacy),
            },
            new Performance.AdvancedGameMode { OwnerType = typeof(Performance) },
        ];

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

        // A library that passes every check, so the Defender option reaches the consent step.
        var realSource = SecurityAndPrivacy.DefenderGameLibraryExclusion.FolderSource;
        SecurityAndPrivacy.DefenderGameLibraryExclusion.FolderSource = () =>
            [@"D:\SteamLibrary\steamapps\common"];
        try
        {
            await optimization.ApplyAsync(new Progress<ProcessingProgress>(), context);
        }
        finally
        {
            SecurityAndPrivacy.DefenderGameLibraryExclusion.FolderSource = realSource;
        }

        // Refused (no consent, another user, a recent pause) or not applicable (Memory
        // Integrity locked on this machine): never a change.
        Assert.False(context.Changes.ModifiedSystem);
        Assert.NotEmpty(context.Changes.Changes);
        Assert.All(
            context.Changes.Changes,
            c =>
                Assert.True(
                    c.Kind is ChangeKind.Refused or ChangeKind.NotApplicable,
                    c.Kind.ToString()
                )
        );
        Assert.DoesNotContain(context.Changes.Changes, c => c.Revert is not null);
    }
}
