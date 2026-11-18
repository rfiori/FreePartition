using FreePartition.Core.Disks;
using FreePartition.Core.Ntfs;

namespace FreePartition.Tests;

public class VolumeNamesTests
{
    private const string Guid = "67bfcaf4-0000-0000-0000-000100000000";
    private const string Canonical = @"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}\";

    [Theory]
    [InlineData(@"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}\")]
    [InlineData(@"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}")]
    [InlineData(@"\\.\Volume{67bfcaf4-0000-0000-0000-000100000000}")]
    [InlineData(@"  \\?\VOLUME{67BFCAF4-0000-0000-0000-000100000000}\  ")]
    public void NormalizeGuidPath_AcceptsTheUsualSpellings(string text) =>
        Assert.Equal(Canonical, VolumeNames.NormalizeGuidPath(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData(@"D:\")]
    [InlineData(@"\\?\Volume{not-a-guid}\")]
    [InlineData(@"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}\Windows")]   // a folder inside the volume
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}\' ; calc ; '")]
    public void NormalizeGuidPath_RefusesAnythingElse(string? text)
    {
        Assert.Null(VolumeNames.NormalizeGuidPath(text));
        Assert.False(VolumeNames.IsGuidPath(text));
    }

    [Fact]
    public void Describe_KeepsTheLetterForm_AndHidesTheGuid()
    {
        Assert.Equal("D:", VolumeNames.Describe("D"));
        Assert.Equal("the selected volume", VolumeNames.Describe(Canonical));
    }

    [Fact]
    public void IsOnVolume_ForALetter_ComparesTheRootOfTheFolder()
    {
        Assert.True(VolumeNames.IsOnVolume(@"E:\Recovered", "E"));
        Assert.True(VolumeNames.IsOnVolume(@"e:\Recovered", "E"));
        Assert.False(VolumeNames.IsOnVolume(@"F:\Recovered", "E"));
    }

    [Fact]
    public void IsOnVolume_ForAGuid_ComparesTheVolumeTheFolderIsOn()
    {
        Assert.True(VolumeNames.IsOnVolume(@"C:\Mounts\Data", Canonical, _ => Canonical));
        Assert.True(VolumeNames.IsOnVolume(@"C:\Mounts\Data", Canonical, _ => Canonical.ToUpperInvariant().Replace("VOLUME", "Volume")));
        Assert.False(VolumeNames.IsOnVolume(@"D:\Recovered", Canonical, _ => @"\\?\Volume{11111111-2222-3333-4444-555555555555}\"));
    }

    [Fact]
    public void IsOnVolume_WhenTheFolderVolumeIsUnknown_ErrsOnTheSafeSide()
    {
        Assert.True(VolumeNames.IsOnVolume(@"D:\Recovered", Canonical, _ => null));
        Assert.True(VolumeNames.IsOnVolume(@"D:\Recovered", Canonical, _ => "garbage"));
    }

    [Fact]
    public void IsOnVolume_RealFolder_IsNotOnAVolumeThatDoesNotExist()
    {
        // The real Win32 lookup: the temp folder's volume is not this made-up one.
        Assert.False(VolumeNames.IsOnVolume(Path.GetTempPath(), @"\\?\Volume{11111111-2222-3333-4444-555555555555}\"));
    }

    [Fact]
    public void TotalBytes_OfAVolumeThatDoesNotExist_IsZero()
    {
        Assert.Equal(0, VolumeNames.TotalBytes(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\"));
        Assert.Equal(0, VolumeNames.TotalBytes("D"));
    }

    [Fact]
    public void RawVolume_RefusesAnInvalidName()
    {
        Assert.Throws<ArgumentException>(() => RawVolume.Open(@"\\?\Volume{bad}\"));
        Assert.Throws<ArgumentException>(() => RawVolume.Open(@"\\?\Volume{67bfcaf4-0000-0000-0000-000100000000}\Windows"));
        Assert.Throws<ArgumentException>(() => RawVolume.Open(""));
    }

    [Fact]
    public void VolumeOfFolder_ReturnsAGuidPath_ForARealFolder() =>
        Assert.True(VolumeNames.IsGuidPath(VolumeNames.VolumeOfFolder(Path.GetTempPath())));

    [Fact]
    public void VolumeOfFolder_WorksForAFolderThatDoesNotExistYet() =>
        Assert.Equal(VolumeNames.VolumeOfFolder(Path.GetTempPath()),
            VolumeNames.VolumeOfFolder(Path.Combine(Path.GetTempPath(), "does-not-exist-" + System.Guid.NewGuid().ToString("N"), "deeper")));

    [Fact]
    public void Recover_RefusesAFolderOnTheScannedVolume_WhenItIsNamedByGuidPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "FreePartitionTests-" + System.Guid.NewGuid().ToString("N"));
        var file = new DeletedFile(1, "a.bin", @"\", 3, null, RecoveryChance.High, 1.0, [], [1, 2, 3]);
        var scan = new ScanResult(VolumeNames.VolumeOfFolder(root)!, 4096, 0, TimeSpan.Zero, []);

        var error = Assert.Throws<InvalidOperationException>(() => new FileRecoverer().Recover(scan, file, root));

        Assert.Contains("ANOTHER disk", error.Message);
        Assert.Contains("the selected volume", error.Message);
        Assert.False(Directory.Exists(root));   // refused before creating anything
    }

    [Fact]
    public void Recover_AllowsAFolderOnAnotherVolume_WhenTheScannedOneIsNamedByGuidPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "FreePartitionTests-" + System.Guid.NewGuid().ToString("N"));
        var file = new DeletedFile(1, "a.bin", @"\", 3, null, RecoveryChance.High, 1.0, [], [1, 2, 3]);
        var scan = new ScanResult(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", 4096, 0, TimeSpan.Zero, []);
        try
        {
            string path = new FileRecoverer().Recover(scan, file, root);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
