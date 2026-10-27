using System.Buffers.Binary;

namespace FreePartition.Core.Ntfs;

/// <summary>Parameters read from the boot sector of an NTFS volume.</summary>
public sealed record NtfsBootSector(
    int BytesPerSector,
    int SectorsPerCluster,
    long TotalSectors,
    long MftStartCluster,
    int MftRecordSize)
{
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public long TotalClusters => TotalSectors / SectorsPerCluster;

    public static NtfsBootSector Read(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512)
            throw new InvalidDataException("Incomplete boot sector.");

        // OEM ID "NTFS    " at offset 0x03
        if (sector[3] != (byte)'N' || sector[4] != (byte)'T' || sector[5] != (byte)'F' || sector[6] != (byte)'S')
            throw new InvalidDataException("The volume is not NTFS (the MFT scan only works on NTFS).");

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        int sectorsPerCluster = sector[0x0D];
        if (sectorsPerCluster > 0x80)                    // values > 0x80 represent 2^(256 - value)
            sectorsPerCluster = 1 << (256 - sectorsPerCluster);

        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);
        long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        sbyte clustersPerRecord = (sbyte)sector[0x40];

        if (bytesPerSector < 512 || sectorsPerCluster <= 0 || mftCluster <= 0)
            throw new InvalidDataException("NTFS boot sector with invalid values.");

        // Positive: number of clusters. Negative: the size is 2^(-value) bytes.
        int recordSize = clustersPerRecord > 0
            ? clustersPerRecord * bytesPerSector * sectorsPerCluster
            : 1 << -clustersPerRecord;

        if (recordSize < 512 || recordSize > 65536)
            throw new InvalidDataException("Invalid MFT record size.");

        return new NtfsBootSector(bytesPerSector, sectorsPerCluster, totalSectors, mftCluster, recordSize);
    }
}
