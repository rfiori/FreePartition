using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreePartition.Core.Disks;

/// <summary>Fields of the NVMe "SMART / Health Information" log page (log id 0x02).</summary>
public sealed record NvmeHealth(
    byte CriticalWarning,
    int TemperatureCelsius,
    int AvailableSparePercent,
    int SpareThresholdPercent,
    int PercentageUsed,
    UInt128 PowerCycles,
    UInt128 PowerOnHours,
    UInt128 UnsafeShutdowns,
    UInt128 MediaErrors,
    UInt128 DataUnitsRead,
    UInt128 DataUnitsWritten)
{
    public const int LogLength = 512;

    /// <summary>One data unit is 1000 × 512 bytes.</summary>
    public static double UnitsToTerabytes(UInt128 units) => (double)units * 512_000d / 1e12;

    public static NvmeHealth? Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < 192) return null;

        // The composite temperature is in Kelvin; 0 means "not reported".
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);

        return new NvmeHealth(
            CriticalWarning: log[0],
            TemperatureCelsius: kelvin == 0 ? 0 : kelvin - 273,
            AvailableSparePercent: log[3],
            SpareThresholdPercent: log[4],
            PercentageUsed: log[5],
            DataUnitsRead: BinaryPrimitives.ReadUInt128LittleEndian(log[32..]),
            DataUnitsWritten: BinaryPrimitives.ReadUInt128LittleEndian(log[48..]),
            PowerCycles: BinaryPrimitives.ReadUInt128LittleEndian(log[112..]),
            PowerOnHours: BinaryPrimitives.ReadUInt128LittleEndian(log[128..]),
            UnsafeShutdowns: BinaryPrimitives.ReadUInt128LittleEndian(log[144..]),
            MediaErrors: BinaryPrimitives.ReadUInt128LittleEndian(log[160..]));
    }
}

/// <summary>
/// Reads the NVMe health log page with IOCTL_STORAGE_QUERY_PROPERTY. This only asks the drive
/// for a log page — nothing is written to it. Requires running as administrator.
/// </summary>
public static partial class NvmeReader
{
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int PropertyStandardQuery = 0;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const uint NvmeLogPageHealthInfo = 2;

    private const int QueryHeaderSize = 8;       // STORAGE_PROPERTY_QUERY without AdditionalParameters
    private const int ProtocolDataSize = 40;     // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int DescriptorHeaderSize = 8;  // STORAGE_PROTOCOL_DATA_DESCRIPTOR Version + Size

    /// <summary>Returns null when the disk is not NVMe, or the driver/bridge does not pass the query through.</summary>
    public static NvmeHealth? TryRead(int diskIndex)
    {
        // Some drivers only accept the query on a handle with read access; try the least privileged first.
        foreach (uint access in new[] { 0u, GenericRead, GenericRead | GenericWrite })
        {
            using var handle = CreateFile($@"\\.\PhysicalDrive{diskIndex}", access,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;

            var log = Query(handle);
            if (log is not null) return NvmeHealth.Parse(log);
        }
        return null;
    }

    private static byte[]? Query(SafeFileHandle handle)
    {
        var input = new byte[QueryHeaderSize + ProtocolDataSize];
        var span = input.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], PropertyStandardQuery);

        var protocol = span[QueryHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(protocol, ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(protocol[4..], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[8..], NvmeLogPageHealthInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[16..], ProtocolDataSize);          // data offset
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[20..], NvmeHealth.LogLength);      // data length

        var output = new byte[DescriptorHeaderSize + ProtocolDataSize + NvmeHealth.LogLength];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length,
                output, output.Length, out uint returned, IntPtr.Zero))
            return null;

        // The descriptor echoes the request; the log page follows it.
        int dataStart = DescriptorHeaderSize + ProtocolDataSize;
        return returned >= dataStart + 192 ? output[dataStart..] : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        [In] byte[] lpInBuffer, int nInBufferSize,
        [Out] byte[] lpOutBuffer, int nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
