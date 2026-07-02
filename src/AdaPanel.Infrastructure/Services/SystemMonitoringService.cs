using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class SystemMonitoringService(ILogger<SystemMonitoringService> logger) : ISystemMonitoringService, IDisposable
{
    private readonly object syncRoot = new();
    private readonly PerformanceCounter? cpuCounter = CreateCpuCounter(logger);
    private readonly Dictionary<string, NetworkSample> previousNetworkSamples = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxHistorySamples = 1440; // 1 dk × 1440 ≈ son 24 saat (kalıcı izleme geçmişi, bellek-içi halka)
    private readonly LinkedList<SystemHistorySampleDto> history = new();
    private bool disposed;

    public Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentUtc = DateTime.UtcNow;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var bootTimeUtc = currentUtc - uptime;
        var memoryStatus = ReadMemoryStatus();
        var totalMemory = Convert.ToInt64(memoryStatus.TotalPhysicalMemory);
        var availableMemory = Convert.ToInt64(memoryStatus.AvailablePhysicalMemory);
        var usedMemory = Math.Max(0, totalMemory - availableMemory);
        var memoryUsagePercent = totalMemory == 0 ? 0 : Math.Round(usedMemory * 100d / totalMemory, 2);

        var disks = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(drive =>
            {
                var usedBytes = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
                var usagePercent = drive.TotalSize == 0 ? 0 : Math.Round(usedBytes * 100d / drive.TotalSize, 2);

                return new DiskUsageDto
                {
                    Name = drive.Name,
                    Label = drive.VolumeLabel,
                    Format = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    UsedBytes = usedBytes,
                    UsagePercent = usagePercent
                };
            })
            .OrderBy(drive => drive.Name)
            .ToArray();

        var networkInterfaces = ReadNetworkInterfaces(currentUtc);

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
            CpuUsagePercent = ReadCpuUsage(),
            LogicalProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = totalMemory,
            AvailableMemoryBytes = availableMemory,
            UsedMemoryBytes = usedMemory,
            MemoryUsagePercent = memoryUsagePercent,
            Disks = disks,
            NetworkInterfaces = networkInterfaces
        };

        return Task.FromResult(overview);
    }

    public async Task RecordSampleAsync(CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(cancellationToken);
        var sample = new SystemHistorySampleDto
        {
            TimestampUtc = DateTime.UtcNow,
            CpuPercent = overview.CpuUsagePercent,
            MemoryPercent = overview.MemoryUsagePercent,
            NetworkDownBytesPerSec = overview.NetworkInterfaces.Sum(item => item.BytesReceivedPerSecond),
            NetworkUpBytesPerSec = overview.NetworkInterfaces.Sum(item => item.BytesSentPerSecond)
        };

        lock (syncRoot)
        {
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
            return Task.FromResult<IReadOnlyList<SystemHistorySampleDto>>(history.ToList());
        }
    }

    public Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<ServerIpAddressDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
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

                    if (!seen.Add(text))
                    {
                        continue;
                    }

                    result.Add(new ServerIpAddressDto
                    {
                        Address = text,
                        Family = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                        InterfaceName = nic.Name
                    });
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Server IP addresses could not be enumerated.");
        }

        var ordered = result
            .OrderBy(item => item.Family == "IPv4" ? 0 : 1)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<ServerIpAddressDto>>(ordered);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        cpuCounter?.Dispose();
        disposed = true;
    }

    private double ReadCpuUsage()
    {
        if (cpuCounter is null)
        {
            return 0;
        }

        try
        {
            return Math.Round(cpuCounter.NextValue(), 2);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CPU usage could not be sampled.");
            return 0;
        }
    }

    private IReadOnlyCollection<NetworkInterfaceDto> ReadNetworkInterfaces(DateTime sampleUtc)
    {
        lock (syncRoot)
        {
            var snapshots = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .Select(network =>
                {
                    var statistics = network.GetIPv4Statistics();
                    var currentSample = new NetworkSample(statistics.BytesReceived, statistics.BytesSent, sampleUtc);

                    previousNetworkSamples.TryGetValue(network.Id, out var previousSample);
                    previousNetworkSamples[network.Id] = currentSample;

                    var bytesReceivedPerSecond = 0d;
                    var bytesSentPerSecond = 0d;

                    if (previousSample is not null)
                    {
                        var elapsedSeconds = Math.Max(0.001, (currentSample.SampledAtUtc - previousSample.SampledAtUtc).TotalSeconds);
                        bytesReceivedPerSecond = Math.Max(0, currentSample.BytesReceived - previousSample.BytesReceived) / elapsedSeconds;
                        bytesSentPerSecond = Math.Max(0, currentSample.BytesSent - previousSample.BytesSent) / elapsedSeconds;
                    }

                    return new NetworkInterfaceDto
                    {
                        Name = network.Name,
                        Type = network.NetworkInterfaceType.ToString(),
                        Status = network.OperationalStatus.ToString(),
                        TotalBytesReceived = currentSample.BytesReceived,
                        TotalBytesSent = currentSample.BytesSent,
                        BytesReceivedPerSecond = Math.Round(bytesReceivedPerSecond, 2),
                        BytesSentPerSecond = Math.Round(bytesSentPerSecond, 2)
                    };
                })
                .OrderByDescending(network => network.BytesReceivedPerSecond + network.BytesSentPerSecond)
                .ThenBy(network => network.Name)
                .ToArray();

            return snapshots;
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

    private static MemoryStatus ReadMemoryStatus()
    {
        var nativeStatus = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref nativeStatus))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx call failed.");
        }

        return new MemoryStatus(nativeStatus.TotalPhys, nativeStatus.AvailPhys);
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

    private sealed record MemoryStatus(ulong TotalPhysicalMemory, ulong AvailablePhysicalMemory);

    private sealed record NetworkSample(long BytesReceived, long BytesSent, DateTime SampledAtUtc);
}
