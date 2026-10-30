using FreePartition.Core.Disks;

namespace FreePartition.Tests;

public class PartitionAlignmentTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1_048_576UL)]                 // 1 MB, where Windows puts the first partition
    [InlineData(16_777_216UL)]                // 16 MB
    [InlineData(1_000_204_140_544UL)]         // beyond 4 GB, still a multiple of 1 MB
    public void Classify_OnA1MbBoundary_IsAligned(ulong offset) =>
        Assert.Equal(AlignmentStatus.Aligned, PartitionAlignment.Classify(offset));

    [Theory]
    [InlineData(4_096UL)]
    [InlineData(65_536UL)]                    // 64 KB, used by some older tools
    [InlineData(1_052_672UL)]                 // 1 MB + 4 KB
    public void Classify_On4KbButNot1Mb_IsAlignedTo4K(ulong offset) =>
        Assert.Equal(AlignmentStatus.AlignedTo4K, PartitionAlignment.Classify(offset));

    [Theory]
    [InlineData(32_256UL)]                    // sector 63, the Windows XP default
    [InlineData(512UL)]
    [InlineData(1_049_088UL)]                 // 1 MB + 512
    public void Classify_NotOn4Kb_IsMisaligned(ulong offset) =>
        Assert.Equal(AlignmentStatus.Misaligned, PartitionAlignment.Classify(offset));

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1_048_576UL, 1_048_576UL)]
    [InlineData(32_256UL, 1_048_576UL)]
    [InlineData(1_048_577UL, 2_097_152UL)]
    [InlineData(2_097_151UL, 2_097_152UL)]
    public void AlignUp_ReturnsTheFirst1MbBoundaryAtOrAfterTheOffset(ulong offset, ulong expected) =>
        Assert.Equal(expected, PartitionAlignment.AlignUp(offset));

    [Fact]
    public void AlignUp_NearTheTopOfTheRange_Throws() =>
        Assert.Throws<OverflowException>(() => PartitionAlignment.AlignUp(ulong.MaxValue));

    [Fact]
    public void Describe_HasATextForEveryStatus()
    {
        foreach (var status in Enum.GetValues<AlignmentStatus>())
            Assert.False(string.IsNullOrWhiteSpace(PartitionAlignment.Describe(status)));
    }
}
