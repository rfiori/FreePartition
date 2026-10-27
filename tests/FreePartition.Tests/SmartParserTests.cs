using FreePartition.Core.Disks;

namespace FreePartition.Tests;

public class SmartParserTests
{
    private static void SetEntry(byte[] table, int slot, byte id, byte flags, byte current, byte worst, ulong raw)
    {
        int o = 2 + slot * 12;
        table[o] = id;
        table[o + 1] = flags;
        table[o + 3] = current;
        table[o + 4] = worst;
        for (int i = 0; i < 6; i++) table[o + 5 + i] = (byte)(raw >> (8 * i));
    }

    private static void SetThreshold(byte[] table, int slot, byte id, byte threshold)
    {
        int o = 2 + slot * 12;
        table[o] = id;
        table[o + 1] = threshold;
    }

    [Fact]
    public void ParseAttributes_ReadsValuesRawAndThreshold()
    {
        var data = new byte[SmartParser.AttributeTableLength];
        var thresholds = new byte[SmartParser.AttributeTableLength];
        SetEntry(data, 0, id: 5, flags: 0x03, current: 100, worst: 99, raw: 0x01_0203);
        SetEntry(data, 1, id: 9, flags: 0x00, current: 97, worst: 97, raw: 12345);
        SetThreshold(thresholds, 0, id: 9, threshold: 0);
        SetThreshold(thresholds, 1, id: 5, threshold: 36);

        var attributes = SmartParser.ParseAttributes(data, thresholds);

        Assert.Equal(2, attributes.Count);
        var reallocated = attributes[0];
        Assert.Equal("Reallocated sector count", reallocated.Name);
        Assert.Equal(100, reallocated.Current);
        Assert.Equal(99, reallocated.Worst);
        Assert.Equal(36, reallocated.Threshold);
        Assert.Equal(0x01_0203UL, reallocated.Raw);
        Assert.True(reallocated.PreFailure);
        Assert.False(attributes[1].PreFailure);
        Assert.Equal(12345UL, attributes[1].Raw);
    }

    [Fact]
    public void ParseAttributes_TruncatedOrEmptyData_DoesNotThrow()
    {
        Assert.Empty(SmartParser.ParseAttributes([], []));
        Assert.Empty(SmartParser.ParseAttributes(new byte[10], new byte[3]));
    }

    [Fact]
    public void Failing_OnlyWhenThresholdIsSetAndReached()
    {
        Assert.True(new SmartAttribute(5, "x", 30, 30, 36, 0, true).Failing);
        Assert.False(new SmartAttribute(5, "x", 37, 37, 36, 0, true).Failing);
        Assert.False(new SmartAttribute(9, "x", 1, 1, 0, 0, false).Failing);
    }

    [Fact]
    public void NvmeParse_ReadsHealthLogFields()
    {
        var log = new byte[NvmeHealth.LogLength];
        log[0] = 0x04;
        BitConverter.GetBytes((ushort)(35 + 273)).CopyTo(log, 1);
        log[3] = 100;
        log[4] = 10;
        log[5] = 7;
        BitConverter.GetBytes(2_000_000UL).CopyTo(log, 48);
        BitConverter.GetBytes(321UL).CopyTo(log, 128);
        BitConverter.GetBytes(5UL).CopyTo(log, 160);

        var nvme = NvmeHealth.Parse(log)!;

        Assert.Equal(0x04, nvme.CriticalWarning);
        Assert.Equal(35, nvme.TemperatureCelsius);
        Assert.Equal(100, nvme.AvailableSparePercent);
        Assert.Equal(7, nvme.PercentageUsed);
        Assert.Equal(321UL, (ulong)nvme.PowerOnHours);
        Assert.Equal(5UL, (ulong)nvme.MediaErrors);
        Assert.Equal(1.024, NvmeHealth.UnitsToTerabytes(nvme.DataUnitsWritten), 3);
    }

    [Fact]
    public void NvmeParse_TooShort_ReturnsNull() => Assert.Null(NvmeHealth.Parse(new byte[100]));

    [Fact]
    public void Evaluate_NvmeCriticalWarning_IsCritical()
    {
        var log = new byte[NvmeHealth.LogLength];
        log[0] = 0x01;
        log[3] = 100;
        var (level, _) = SmartParser.Evaluate("Healthy", null, 30, 1, [], NvmeHealth.Parse(log));
        Assert.Equal(HealthLevel.Critical, level);
    }

    [Fact]
    public void Evaluate_NvmeHealthy_IsGood()
    {
        var log = new byte[NvmeHealth.LogLength];
        log[3] = 100;
        log[4] = 10;
        var (level, _) = SmartParser.Evaluate("Healthy", null, 30, 1, [], NvmeHealth.Parse(log));
        Assert.Equal(HealthLevel.Good, level);
    }

    [Fact]
    public void Evaluate_NoData_IsUnknown()
    {
        var (level, _) = SmartParser.Evaluate(null, null, null, null, []);
        Assert.Equal(HealthLevel.Unknown, level);
    }

    [Fact]
    public void Evaluate_PredictedFailure_IsCritical()
    {
        var (level, _) = SmartParser.Evaluate("Healthy", true, 30, 10, []);
        Assert.Equal(HealthLevel.Critical, level);
    }

    [Fact]
    public void Evaluate_FailingAttribute_IsCritical()
    {
        var failing = new SmartAttribute(5, "Reallocated sector count", 20, 20, 36, 0, true);
        var (level, _) = SmartParser.Evaluate(null, false, null, null, [failing]);
        Assert.Equal(HealthLevel.Critical, level);
    }

    [Fact]
    public void Evaluate_PendingSectors_IsWarning()
    {
        var pending = new SmartAttribute(197, "Current pending sector count", 100, 100, 0, 8, false);
        var (level, summary) = SmartParser.Evaluate("Healthy", false, 35, 5, [pending]);
        Assert.Equal(HealthLevel.Warning, level);
        Assert.Contains("pending", summary);
    }

    [Fact]
    public void Evaluate_HotOrWornOut_IsWarning()
    {
        Assert.Equal(HealthLevel.Warning, SmartParser.Evaluate("Healthy", false, 65, 5, []).Level);
        Assert.Equal(HealthLevel.Warning, SmartParser.Evaluate("Healthy", false, 35, 95, []).Level);
    }

    [Fact]
    public void Evaluate_AllClear_IsGood()
    {
        Assert.Equal(HealthLevel.Good, SmartParser.Evaluate("Healthy", false, 35, 5, []).Level);
    }
}
