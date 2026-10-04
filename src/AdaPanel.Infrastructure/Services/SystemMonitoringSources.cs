namespace AdaPanel.Infrastructure.Services;

// Read-only native boundaries. Tests replace these without starting a Windows counter or
// querying the host's memory, disks, or network interfaces.
internal sealed class SystemMonitoringSources
{
    internal required Func<double?> ReadCpu { get; init; }
    internal required Func<SystemMemorySnapshot> ReadMemory { get; init; }
    internal required Func<IReadOnlyCollection<SystemDiskSnapshot>> ReadDisks { get; init; }
    internal required Func<IReadOnlyCollection<SystemNetworkSnapshot>> ReadNetwork { get; init; }
    internal Func<IEnumerable<SystemIpAddressSnapshot>> ReadIpAddresses { get; init; } = () => [];
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

internal sealed record SystemMemorySnapshot(ulong TotalPhysicalMemory, ulong AvailablePhysicalMemory);
internal sealed record SystemDiskSnapshot(string Name, string Label, string Format, long TotalBytes, long FreeBytes);
internal sealed record SystemNetworkSnapshot(string Id, string Name, string Type, string Status, long BytesReceived, long BytesSent);
internal sealed record SystemIpAddressSnapshot(global::System.Net.IPAddress Address, string InterfaceName);
