namespace FreePartition.Core.Cloning;

/// <summary>Where each partition of the copy goes on the destination disk. Offsets are in bytes, aligned to 1 MiB.</summary>
public sealed record WindowsClonePlan(
    bool Uefi,
    long BootOffset, long BootSize,
    long? MsrOffset, long MsrSize,
    long WindowsOffset, long WindowsSize,
    long? RecoveryOffset, long RecoverySize,
    long RequiredBytes)
{
    public bool FitsOn(long diskSize) => RequiredBytes <= diskSize;
    public long ShortfallOn(long diskSize) => Math.Max(0, RequiredBytes - diskSize);
}

/// <summary>
/// Lays out the destination like a standard Windows disk: boot partition (EFI system partition or "System Reserved"),
/// MSR (GPT only, when the source has one), Windows, Recovery. The Windows partition keeps the exact size of the source,
/// because it is copied sector by sector; to fit a smaller disk, shrink the source first.
/// </summary>
public static class WindowsCloneLayout
{
    public const long MiB = 1L << 20;
    public const long MinBootSize = 100 * MiB;
    public const long MsrSize = 16 * MiB;

    /// <summary>Free MiB the disk keeps at the start (partition table) and at the end (GPT backup table).</summary>
    private const long Margin = MiB;

    public static WindowsClonePlan Compute(WindowsInstall source)
    {
        ArgumentNullException.ThrowIfNull(source);

        long bootSize = Math.Max(MinBootSize, source.Boot is { } boot ? AlignUp((long)boot.Size) : 0);
        long cursor = Margin;

        long bootOffset = cursor;
        cursor += bootSize;

        long? msrOffset = null;
        long msr = 0;
        if (source.Uefi && source.HasMsr)
        {
            msrOffset = cursor;
            msr = MsrSize;
            cursor += msr;
        }

        long windowsOffset = cursor;
        long windowsSize = (long)source.Windows.Size;
        cursor += AlignUp(windowsSize);

        long? recoveryOffset = null;
        long recoverySize = 0;
        if (source.Recovery is { } recovery)
        {
            recoveryOffset = cursor;
            recoverySize = AlignUp((long)recovery.Size);
            cursor += recoverySize;
        }

        return new WindowsClonePlan(source.Uefi, bootOffset, bootSize, msrOffset, msr,
            windowsOffset, windowsSize, recoveryOffset, recoverySize, cursor + Margin);
    }

    private static long AlignUp(long value) => (value + MiB - 1) / MiB * MiB;
}
