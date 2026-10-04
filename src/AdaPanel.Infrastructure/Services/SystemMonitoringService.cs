using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class SystemMonitoringService : ISystemMonitoringService, IDisposable
{
    private readonly ILogger<SystemMonitoringService> logger;
    private readonly SystemMonitoringSources sources;
    private readonly object syncRoot = new();
    private readonly PerformanceCounter? cpuCounter;
    private readonly Dictionary<string, NetworkSample> previousNetworkSamples = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxHistorySamples = 1440; // 1 dk × 1440 ≈ son 24 saat (kalıcı izleme geçmişi, bellek-içi halka)
    private readonly LinkedList<SystemHistorySampleDto> history = new();
    private bool disposed;

    public SystemMonitoringService(ILogger<SystemMonitoringService> logger)
    {
        this.logger = logger;
        cpuCounter = CreateCpuCounter(logger);
        sources = new SystemMonitoringSources
        {
            ReadCpu = () => cpuCounter?.NextValue(),
            ReadMemory = ReadMemoryStatus,
            ReadDisks = ReadDisks,
            ReadNetwork = ReadNetworkSnapshots,
            ReadIpAddresses = ReadIpAddressSnapshots
        };
    }

    internal SystemMonitoringService(ILogger<SystemMonitoringService> logger, SystemMonitoringSources sources)
    {
        this.logger = logger;
        this.sources = sources;
    }

    public Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot) ObjectDisposedException.ThrowIf(disposed, this);

        var currentUtc = sources.Clock.GetUtcNow().UtcDateTime;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var bootTimeUtc = currentUtc - uptime;
        var memory = ReadMemory(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var disks = ReadDiskMeasurements(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var network = ReadNetworkInterfaces(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var cpu = ReadCpuUsage(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var overview = new SystemOverviewDto
        {
            MachineName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            PanelVersion = VersionInfo.Current,
            CurrentServerTimeUtc = currentUtc,
            BootTimeUtc = bootTimeUtc,
            Uptime = uptime,
            CpuUsagePercent = cpu ?? 0,
            CpuMeasured = cpu.HasValue,
            LogicalProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = memory.Total,
            AvailableMemoryBytes = memory.Available,
            UsedMemoryBytes = memory.Total - memory.Available,
            MemoryUsagePercent = memory.Total == 0 ? 0 : Math.Round((memory.Total - memory.Available) * 100d / memory.Total, 2),
            MemoryMeasured = memory.Measured,
            Disks = disks.Items,
            DisksMeasured = disks.Measured,
            NetworkInterfaces = network.Items,
            NetworkMeasured = network.Measured
        };

        return Task.FromResult(overview);
    }

    public async Task RecordSampleAsync(CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(cancellationToken);
        var sample = new SystemHistorySampleDto
        {
            TimestampUtc = overview.CurrentServerTimeUtc,
            CpuPercent = overview.CpuUsagePercent,
            CpuMeasured = overview.CpuMeasured,
            MemoryPercent = overview.MemoryUsagePercent,
            MemoryMeasured = overview.MemoryMeasured,
            NetworkMeasured = overview.NetworkMeasured,
            NetworkDownBytesPerSec = overview.NetworkMeasured ? overview.NetworkInterfaces.Sum(item => item.BytesReceivedPerSecond) : 0,
            NetworkUpBytesPerSec = overview.NetworkMeasured ? overview.NetworkInterfaces.Sum(item => item.BytesSentPerSecond) : 0
        };

        lock (syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            history.AddLast(sample);
            while (history.Count > MaxHistorySamples)
            {
                history.RemoveFirst();
            }
        }
    }

    public Task<IReadOnlyList<SystemHistorySampleDto>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            return Task.FromResult<IReadOnlyList<SystemHistorySampleDto>>(history.Select(sample => new SystemHistorySampleDto
            {
                TimestampUtc = sample.TimestampUtc, CpuPercent = sample.CpuPercent, CpuMeasured = sample.CpuMeasured,
                MemoryPercent = sample.MemoryPercent, MemoryMeasured = sample.MemoryMeasured,
                NetworkMeasured = sample.NetworkMeasured, NetworkDownBytesPerSec = sample.NetworkDownBytesPerSec,
                NetworkUpBytesPerSec = sample.NetworkUpBytesPerSec
            }).ToArray());
        }
    }

    public Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<ServerIpAddressDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var address in sources.ReadIpAddresses())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ip = address.Address;
                if (ip.IsIPv6LinkLocal || System.Net.IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                var text = ip.ToString();
                var scopeIndex = text.IndexOf('%');
                if (scopeIndex >= 0)
                {
                    text = text[..scopeIndex];
                }

                if (!seen.Add(text)) continue;
                result.Add(new ServerIpAddressDto
                {
                    Address = text,
                    Family = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                    InterfaceName = address.InterfaceName
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(exception, "Server IP addresses could not be enumerated.");
            throw new AppException("system.ip_addresses_unmeasured", httpStatusCode: 503);
        }

        var ordered = result
            .OrderBy(item => item.Family == "IPv4" ? 0 : 1)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<ServerIpAddressDto>>(ordered);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            cpuCounter?.Dispose();
            disposed = true;
        }
    }

    private double? ReadCpuUsage(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = sources.ReadCpu();
                return value.HasValue && double.IsFinite(value.Value) && value.Value is >= 0 and <= 100
                    ? Math.Round(value.Value, 2) : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "CPU usage could not be sampled.");
                return null;
            }
        }
    }

    private (long Total, long Available, bool Measured) ReadMemory(CancellationToken cancellationToken)
    {
        try
        {
            var value = sources.ReadMemory();
            return value.TotalPhysicalMemory is > 0 and <= long.MaxValue && value.AvailablePhysicalMemory <= value.TotalPhysicalMemory
                ? ((long)value.TotalPhysicalMemory, (long)value.AvailablePhysicalMemory, true) : (0, 0, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { logger.LogWarning(exception, "Memory usage could not be sampled."); return (0, 0, false); }
    }

    private (IReadOnlyCollection<DiskUsageDto> Items, bool Measured) ReadDiskMeasurements(CancellationToken cancellationToken)
    {
        try
        {
            var disks = sources.ReadDisks().Select(drive =>
            {
                var measured = drive.TotalBytes > 0 && drive.FreeBytes >= 0 && drive.FreeBytes <= drive.TotalBytes;
                return new DiskUsageDto
                {
                    Name = drive.Name, Label = drive.Label, Format = drive.Format, Measured = measured,
                    TotalBytes = measured ? drive.TotalBytes : 0, FreeBytes = measured ? drive.FreeBytes : 0,
                    UsedBytes = measured ? drive.TotalBytes - drive.FreeBytes : 0,
                    UsagePercent = measured ? Math.Round((drive.TotalBytes - drive.FreeBytes) * 100d / drive.TotalBytes, 2) : 0
                };
            }).OrderBy(drive => drive.Name).ToArray();
            return (disks, disks.All(disk => disk.Measured));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { logger.LogWarning(exception, "Disk usage could not be sampled."); return ([], false); }
    }

    private (IReadOnlyCollection<NetworkInterfaceDto> Items, bool Measured) ReadNetworkInterfaces(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            try
            {
            cancellationToken.ThrowIfCancellationRequested();
            var native = sources.ReadNetwork();
            cancellationToken.ThrowIfCancellationRequested();
            // Capture monotonic time at the counters, inside their shared sampling gate.
            // Request start time and wall-clock adjustments must not alter a traffic rate.
            var sampledAt = sources.Clock.GetTimestamp();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snapshots = native
                .Select(network =>
                {
                    var valid = !string.IsNullOrWhiteSpace(network.Id) && seen.Add(network.Id) &&
                        network.BytesReceived >= 0 && network.BytesSent >= 0;
                    var currentSample = new NetworkSample(network.BytesReceived, network.BytesSent, sampledAt);

                    previousNetworkSamples.TryGetValue(network.Id, out var previousSample);
                    if (valid) previousNetworkSamples[network.Id] = currentSample;
                    else previousNetworkSamples.Remove(network.Id);

                    var bytesReceivedPerSecond = 0d;
                    var bytesSentPerSecond = 0d;
                    var measured = false;

                    if (valid && previousSample is not null && currentSample.SampledAt > previousSample.SampledAt &&
                        currentSample.BytesReceived >= previousSample.BytesReceived && currentSample.BytesSent >= previousSample.BytesSent)
                    {
                        var elapsedSeconds = sources.Clock.GetElapsedTime(previousSample.SampledAt, currentSample.SampledAt).TotalSeconds;
                        if (double.IsFinite(elapsedSeconds) && elapsedSeconds > 0)
                        {
                            bytesReceivedPerSecond = (currentSample.BytesReceived - previousSample.BytesReceived) / elapsedSeconds;
                            bytesSentPerSecond = (currentSample.BytesSent - previousSample.BytesSent) / elapsedSeconds;
                            measured = double.IsFinite(bytesReceivedPerSecond) && double.IsFinite(bytesSentPerSecond);
                        }
                    }

                    return new NetworkInterfaceDto
                    {
                        Name = network.Name,
                        Type = network.Type,
                        Status = network.Status,
                        TotalBytesReceived = valid ? currentSample.BytesReceived : 0,
                        TotalBytesSent = valid ? currentSample.BytesSent : 0,
                        RatesMeasured = measured,
                        BytesReceivedPerSecond = measured ? Math.Round(bytesReceivedPerSecond, 2) : 0,
                        BytesSentPerSecond = measured ? Math.Round(bytesSentPerSecond, 2) : 0
                    };
                })
                .OrderByDescending(network => network.BytesReceivedPerSecond + network.BytesSentPerSecond)
                .ThenBy(network => network.Name)
                .ToArray();

            foreach (var removed in previousNetworkSamples.Keys.Where(id => !seen.Contains(id)).ToArray())
                previousNetworkSamples.Remove(removed);
            return (snapshots, snapshots.All(network => network.RatesMeasured));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                previousNetworkSamples.Clear();
                logger.LogWarning(exception, "Network usage could not be sampled.");
                return ([], false);
            }
        }
    }

    private static PerformanceCounter? CreateCpuCounter(ILogger logger)
    {
        try
        {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = counter.NextValue();
            return counter;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Performance counter for CPU usage could not be created.");
            return null;
        }
    }

    private static IReadOnlyCollection<SystemDiskSnapshot> ReadDisks()
    {
        var result = new List<SystemDiskSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!drive.IsReady && drive.DriveType == DriveType.Removable) continue;
                result.Add(new(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException) { result.Add(new(drive.Name, string.Empty, string.Empty, 0, 0)); }
            catch (UnauthorizedAccessException) { result.Add(new(drive.Name, string.Empty, string.Empty, 0, 0)); }
        }
        return result;
    }

    private static IReadOnlyCollection<SystemNetworkSnapshot> ReadNetworkSnapshots() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .Select(network =>
        {
            var statistics = network.GetIPv4Statistics();
            return new SystemNetworkSnapshot(network.Id, network.Name, network.NetworkInterfaceType.ToString(),
                network.OperationalStatus.ToString(), statistics.BytesReceived, statistics.BytesSent);
        }).ToArray();

    private static IEnumerable<SystemIpAddressSnapshot> ReadIpAddressSnapshots()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                yield return new SystemIpAddressSnapshot(unicast.Address, nic.Name);
        }
    }

    private static SystemMemorySnapshot ReadMemoryStatus()
    {
        var nativeStatus = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref nativeStatus))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx call failed.");
        }

        return new SystemMemorySnapshot(nativeStatus.TotalPhys, nativeStatus.AvailPhys);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private sealed record NetworkSample(long BytesReceived, long BytesSent, long SampledAt);
}
