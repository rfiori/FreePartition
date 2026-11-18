using FreePartition.Core.Disks;

namespace FreePartition.Tests;

public class VolumeGuidPathTests
{
    private const string VolumePath = @"\\?\Volume{3f1c2a4e-0000-0000-0000-100000000000}\";

    [Fact]
    public void PicksTheGuidPath_AmongLettersAndMountFolders() =>
        Assert.Equal(VolumePath, DiskService.VolumeGuidPath([@"D:\", @"C:\Mounts\Data\", VolumePath]));

    [Fact]
    public void AddsTheTrailingBackslash_SoItMatchesWin32VolumeDeviceId() =>
        Assert.Equal(VolumePath, DiskService.VolumeGuidPath([VolumePath.TrimEnd('\\')]));

    [Fact]
    public void IgnoresCase()
    {
        const string lower = @"\\?\volume{3f1c2a4e-0000-0000-0000-100000000000}\";
        Assert.Equal(lower, DiskService.VolumeGuidPath([lower]));
    }

    [Fact]
    public void WithoutAMountedVolume_ReturnsNull()
    {
        Assert.Null(DiskService.VolumeGuidPath(null));
        Assert.Null(DiskService.VolumeGuidPath([]));
        Assert.Null(DiskService.VolumeGuidPath([@"E:\"]));
    }

    [Fact]
    public void SkipsNullEntries() =>
        Assert.Equal(VolumePath, DiskService.VolumeGuidPath([null, VolumePath]));
}
