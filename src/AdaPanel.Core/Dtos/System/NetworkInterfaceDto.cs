namespace AdaPanel.Core.Dtos.System;

public sealed class NetworkInterfaceDto
{
    public bool RatesMeasured { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public long TotalBytesReceived { get; init; }

    public long TotalBytesSent { get; init; }

    public double BytesReceivedPerSecond { get; init; }

    public double BytesSentPerSecond { get; init; }
}
