using FreePartition.Core.Operations;

namespace FreePartition.Tests;

/// <summary>Format, label, check and delete on a partition that has no drive letter (addressed by disk, number and offset).</summary>
public class PartitionTargetOperationTests
{
    private static readonly PartitionTarget Target = new(2, 5, 1_048_576);
    private const char None = DiskOperation.NoLetter;

    [Fact]
    public void Format_FindsThePartitionByNumber_ChecksItsOffset_AndRefusesBootOrSystem()
    {
        string script = new FormatVolume(None, FileSystemFormat.NTFS, "Data", Target).GenerateScript();

        Assert.Contains("Get-Partition -DiskNumber 2 -PartitionNumber 5", script);
        Assert.Contains("$p.Offset -ne 1048576", script);
        Assert.Contains("$p.IsBoot -or $p.IsSystem", script);
        Assert.EndsWith("Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false", script);
        Assert.DoesNotContain("-DriveLetter", script);
    }

    [Fact]
    public void Delete_RemovesThePartitionObject_AndRefusesBootOrSystem()
    {
        string script = new DeletePartition(None, Target).GenerateScript();

        Assert.Contains("$p.Offset -ne 1048576", script);
        Assert.Contains("$p.IsBoot -or $p.IsSystem", script);
        Assert.EndsWith("Remove-Partition -InputObject $p -Confirm:$false", script);
    }

    [Fact]
    public void Label_GoesThroughTheVolumeOfThePartition()
    {
        string script = new ChangeVolumeLabel(None, "New", Target).GenerateScript();

        Assert.Contains("$p.Offset -ne 1048576", script);
        Assert.EndsWith("$p | Get-Volume | Set-Volume -NewFileSystemLabel 'New'", script);
        Assert.DoesNotContain("IsBoot", script);   // renaming is harmless, so it is not refused
    }

    [Fact]
    public void Check_ScanOnlyIsAllowedOnSystemPartitions_ButRepairIsNot()
    {
        string scan = new CheckVolume(None, Partition: Target).GenerateScript();
        string repair = new CheckVolume(None, Repair: true, Partition: Target).GenerateScript();

        Assert.DoesNotContain("IsBoot", scan);
        Assert.Contains("$p | Get-Volume | Repair-Volume -Scan", scan);
        Assert.Contains("IsBoot", repair);
        Assert.Contains("Repair-Volume -OfflineScanAndFix", repair);
    }

    [Fact]
    public void Descriptions_NameTheDiskAndPartition()
    {
        Assert.Equal("Format partition 5 of disk 2 as exFAT — ERASES ALL DATA",
            new FormatVolume(None, FileSystemFormat.exFAT, Partition: Target).Description);
        Assert.Equal("Delete partition 5 of disk 2 — ERASES ALL DATA", new DeletePartition(None, Target).Description);
        Assert.Equal("Change label of partition 5 of disk 2 to \"X\"", new ChangeVolumeLabel(None, "X", Target).Description);
        Assert.Equal("Check partition 5 of disk 2 for errors (scan only)", new CheckVolume(None, Partition: Target).Description);
    }

    [Fact]
    public void WithoutALetter_NoDriveIsAffected()
    {
        Assert.Empty(new FormatVolume(None, FileSystemFormat.NTFS, Partition: Target).AffectedDrives);
        Assert.Empty(new DeletePartition(None, Target).AffectedDrives);
    }

    [Fact]
    public void WithoutALetterAndWithoutATarget_IsInvalid()
    {
        Assert.Throws<ArgumentException>(() => new FormatVolume(None, FileSystemFormat.NTFS).Validate());
        Assert.Throws<ArgumentException>(() => new DeletePartition(None).GenerateScript());
        Assert.Throws<ArgumentException>(() => new ChangeVolumeLabel(None, "X").Validate());
        Assert.Throws<ArgumentException>(() => new CheckVolume(None).Validate());
    }

    [Theory]
    [InlineData(-1, 1, 0L)]
    [InlineData(0, 0, 0L)]
    [InlineData(0, 1, -1L)]
    public void InvalidTargets_AreRefused(int disk, int partition, long offset) =>
        Assert.Throws<ArgumentException>(() => new DeletePartition(None, new PartitionTarget(disk, partition, offset)).Validate());

    [Fact]
    public void TheLabelRulesStillApply()
    {
        Assert.Throws<ArgumentException>(() => new ChangeVolumeLabel(None, "bad'; calc", Target).Validate());
        Assert.Throws<ArgumentException>(() => new FormatVolume(None, FileSystemFormat.FAT32, "VeryLongLabel", Target).Validate());
    }

    [Fact]
    public void TheQueueAcceptsThemEvenWithAProtectedSystemLetter()
    {
        var queue = new OperationQueue('C');
        queue.Add(new DeletePartition(None, Target));
        queue.Add(new FormatVolume(None, FileSystemFormat.NTFS, Partition: Target));

        Assert.Equal(2, queue.Pending.Count);
    }

    [Fact]
    public void LetteredOperationsKeepTheirScripts()
    {
        Assert.Equal("Remove-Partition -DriveLetter F -Confirm:$false", new DeletePartition('f').GenerateScript());
        Assert.Equal("Set-Volume -DriveLetter F -NewFileSystemLabel 'A'", new ChangeVolumeLabel('f', "A").GenerateScript());
    }
}
