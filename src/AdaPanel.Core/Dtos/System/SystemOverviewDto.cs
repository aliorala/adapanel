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

    /// <summary>
    /// Bu yanıt role göre DARALTILDI mı: sunucu geneli alanlar ölçülmedi, varsayılan değerdeler.
    /// </summary>
    /// <remarks>
    /// Bayrak olmadan daraltılmış yanıt "CPU %0, bellek 0 B, disk yok" der ve arayüz bunu ölçüm
    /// sanıp gösterir — yani panel yalan söyler. Daraltmanın kendisi
    /// <see cref="SystemOverviewScope"/>'ta, gerekçesiyle birlikte.
    /// </remarks>
    public bool IsRestricted { get; init; }
}
