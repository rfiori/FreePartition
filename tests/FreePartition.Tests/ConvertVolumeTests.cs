using FreePartition.Core.Operations;

namespace FreePartition.Tests;

public class ConvertVolumeTests
{
    [Fact]
    public void ToNtfs_UsesInPlaceConvert_AndChecksTheResult()
    {
        string script = new ConvertVolume('e', FileSystemFormat.NTFS).GenerateScript();

        int check = script.IndexOf("-ne 'FAT32'", StringComparison.Ordinal);
        int convert = script.IndexOf("convert.exe E: /fs:ntfs /x", StringComparison.Ordinal);
        int verify = script.IndexOf("-ne 'NTFS'", StringComparison.Ordinal);

        Assert.True(check >= 0 && check < convert && convert < verify);
        Assert.DoesNotContain("Format-Volume", script);
    }

    [Fact]
    public void ToFat32_CopiesAndVerifiesBeforeFormatting_ThenVerifiesBeforeDeletingTheCopy()
    {
        string script = new ConvertVolume('e', FileSystemFormat.FAT32, 'd').GenerateScript();

        int copyOut = script.IndexOf("robocopy 'E:\\' $stage", StringComparison.Ordinal);
        int verifyOut = script.IndexOf("Verification failed: source has", StringComparison.Ordinal);
        int format = script.IndexOf("Format-Volume -DriveLetter E -FileSystem FAT32", StringComparison.Ordinal);
        int copyBack = script.IndexOf("robocopy $stage 'E:\\'", StringComparison.Ordinal);
        int verifyBack = script.IndexOf("Verification failed after copying back", StringComparison.Ordinal);
        int cleanup = script.IndexOf("Remove-Item -LiteralPath $stage", StringComparison.Ordinal);

        Assert.Contains("$stage = 'D:\\FreePartition_Convert_E'", script);
        Assert.True(copyOut >= 0 && copyOut < verifyOut && verifyOut < format
                    && format < copyBack && copyBack < verifyBack && verifyBack < cleanup);
    }

    [Fact]
    public void ToFat32_RefusesFilesOf4GbOrMore_AndVolumesOver32Gb()
    {
        string script = new ConvertVolume('E', FileSystemFormat.FAT32, 'D').GenerateScript();

        Assert.Contains("$_.Length -gt 4294967295", script);
        Assert.Contains("$v.Size -gt 34359738368", script);
    }

    [Fact]
    public void InvalidParameters_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ConvertVolume('1', FileSystemFormat.NTFS).Validate());
        Assert.Throws<ArgumentException>(() => new ConvertVolume('E', FileSystemFormat.exFAT).Validate());
        Assert.Throws<ArgumentException>(() => new ConvertVolume('E', FileSystemFormat.FAT32).Validate());
        Assert.Throws<ArgumentException>(() => new ConvertVolume('E', FileSystemFormat.FAT32, 'e').Validate());
        Assert.Throws<ArgumentException>(() => new ConvertVolume('E', FileSystemFormat.FAT32, '1').Validate());
    }

    [Fact]
    public void IsDestructiveWithAWarning_AndRefusedOnTheSystemDrive()
    {
        var toNtfs = new ConvertVolume('E', FileSystemFormat.NTFS);
        var toFat32 = new ConvertVolume('E', FileSystemFormat.FAT32, 'D');
        Assert.True(toNtfs.Destructive);
        Assert.NotNull(toNtfs.Warning);
        Assert.NotNull(toFat32.Warning);

        var queue = new OperationQueue(protectedDrive: 'C');
        Assert.Throws<InvalidOperationException>(() => queue.Add(new ConvertVolume('c', FileSystemFormat.NTFS)));
        Assert.Throws<InvalidOperationException>(() => queue.Add(new ConvertVolume('c', FileSystemFormat.FAT32, 'D')));
        queue.Add(new ConvertVolume('E', FileSystemFormat.FAT32, 'C'));   // staging on the Windows drive is only a copy
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void RequiredStagingBytes_AddsFivePercentAnd64Mb()
    {
        Assert.Equal(64UL << 20, ConvertVolume.RequiredStagingBytes(0));
        Assert.Equal(1_050_000_000UL + (64UL << 20), ConvertVolume.RequiredStagingBytes(1_000_000_000));
    }
}
