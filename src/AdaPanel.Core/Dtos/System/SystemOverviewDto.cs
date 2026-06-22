namespace AdaPanel.Core.Dtos.System;

public sealed class SystemOverviewDto
{
    public string MachineName { get; init; } = string.Empty;

    public string OperatingSystem { get; init; } = string.Empty;

    public string OsArchitecture { get; init; } = string.Empty;

    public string FrameworkDescription { get; init; } = string.Empty;

    public string PanelVersion { get; init; } = string.Empty;

    public DateTime CurrentServerTimeUtc { get; init; }

    public DateTime BootTimeUtc { get; init; }

    public TimeSpan Uptime { get; init; }

    public double CpuUsagePercent { get; init; }

    public int LogicalProcessorCount { get; init; }

    public long TotalMemoryBytes { get; init; }

    public long AvailableMemoryBytes { get; init; }

    public long UsedMemoryBytes { get; init; }

    public double MemoryUsagePercent { get; init; }

    public IReadOnlyCollection<DiskUsageDto> Disks { get; init; } = [];

    public IReadOnlyCollection<NetworkInterfaceDto> NetworkInterfaces { get; init; } = [];
}
