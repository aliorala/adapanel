using AdaPanel.Core.Dtos.System;

namespace AdaPanel.Infrastructure.Support;

// A default/legacy DTO is not a measurement. Consumers also validate its numeric
// shape, since a cluster response need not come from this service version.
internal static class SystemMetricValidity
{
    internal static bool Memory(SystemOverviewDto overview) => overview.MemoryMeasured &&
        overview.TotalMemoryBytes > 0 && overview.AvailableMemoryBytes >= 0 &&
        overview.AvailableMemoryBytes <= overview.TotalMemoryBytes &&
        overview.UsedMemoryBytes == overview.TotalMemoryBytes - overview.AvailableMemoryBytes &&
        Percent(overview.MemoryUsagePercent);

    internal static bool Disk(DiskUsageDto disk) => disk.Measured && disk.TotalBytes > 0 &&
        disk.FreeBytes >= 0 && disk.FreeBytes <= disk.TotalBytes &&
        disk.UsedBytes == disk.TotalBytes - disk.FreeBytes && Percent(disk.UsagePercent);

    private static bool Percent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;
}
