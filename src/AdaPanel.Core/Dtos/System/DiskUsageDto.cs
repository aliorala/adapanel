namespace AdaPanel.Core.Dtos.System;

public sealed class DiskUsageDto
{
    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes { get; init; }

    public double UsagePercent { get; init; }
}

