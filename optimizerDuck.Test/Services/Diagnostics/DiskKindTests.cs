using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>
///     Version 1 test (20/11/2026): on a PC that took 55 s to boot, "Windows en un disco mecánico"
///     came out "no medible" because Windows answered "unspecified". Two more sources now: the
///     rotation speed of the disk and the physical disk that holds Windows.
/// </summary>
public class DiskKindTests
{
    [Theory]
    [InlineData(3, 11, null, StorageMediaType.Hdd)]
    [InlineData(4, 11, 0L, StorageMediaType.Ssd)]
    [InlineData(4, 17, 0L, StorageMediaType.Nvme)]
    [InlineData(0, 11, 7200L, StorageMediaType.Hdd)] // unspecified, but it spins
    [InlineData(0, 11, 5400L, StorageMediaType.Hdd)]
    [InlineData(0, 11, 0L, StorageMediaType.Unknown)] // unspecified and no speed: still unknown
    [InlineData(0, 11, 4294967295L, StorageMediaType.Unknown)] // 0xFFFFFFFF = unknown
    [InlineData(0, 11, null, StorageMediaType.Unknown)]
    public void MediaType_UsesTheRotationSpeed_WhenWindowsSaysUnspecified(
        int mediaType,
        int busType,
        long? spindle,
        StorageMediaType expected
    ) =>
        Assert.Equal(expected, DiagnosticSnapshotBuilder.MapMediaType(mediaType, busType, spindle));

    [Fact]
    public void SystemKind_FallsBackToThePhysicalDiskThatHoldsWindows()
    {
        IReadOnlyList<DiskSnapshot> disks =
        [
            Disk(0, "ST1000DM010-2EP102", StorageMediaType.Hdd, isSystem: true),
            Disk(1, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd),
        ];

        Assert.Equal(
            StorageMediaType.Hdd,
            DiagnosticSnapshotBuilder.SystemKind(StorageMediaType.Unknown, disks)
        );
        // The volume's own answer wins when it has one.
        Assert.Equal(
            StorageMediaType.Ssd,
            DiagnosticSnapshotBuilder.SystemKind(StorageMediaType.Ssd, disks)
        );
        Assert.Equal(
            StorageMediaType.Unknown,
            DiagnosticSnapshotBuilder.SystemKind(StorageMediaType.Unknown, [])
        );
    }
}
