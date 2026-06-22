using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cache;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Cache;

public sealed class CacheService(AdaPanelDbContext dbContext) : ICacheService
{
    private static readonly CacheDescriptor RedisDescriptor = new("redis", "Redis", "AdaRedis", 6379, "redis-server.exe");
    private static readonly CacheDescriptor MemcachedDescriptor = new("memcached", "Memcached", "AdaMemcached", 11211, "memcached.exe");

    public Task<CacheStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetStatusInternalAsync(cancellationToken);
    }

    public async Task<CacheRuntimeDto> StartRedisAsync(CancellationToken cancellationToken = default)
    {
        return await StartAsync(RedisDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StopRedisAsync(CancellationToken cancellationToken = default)
    {
        return await StopAsync(RedisDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StartMemcachedAsync(CancellationToken cancellationToken = default)
    {
        return await StartAsync(MemcachedDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StopMemcachedAsync(CancellationToken cancellationToken = default)
    {
        return await StopAsync(MemcachedDescriptor, cancellationToken);
    }

    private async Task<CacheStatusDto> GetStatusInternalAsync(CancellationToken cancellationToken)
    {
        var redis = await BuildRuntimeAsync(RedisDescriptor, cancellationToken);
        var memcached = await BuildRuntimeAsync(MemcachedDescriptor, cancellationToken);
        return new CacheStatusDto
        {
            Redis = redis,
            Memcached = memcached
        };
    }

    private async Task<CacheRuntimeDto> StartAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtime = await BuildRuntimeAsync(descriptor, cancellationToken);
        if (!runtime.IsInstalled)
        {
            throw new AppException("cache.component_not_installed", 404, 404);
        }

        if (!runtime.IsServiceRegistered)
        {
            await InstallServiceAsync(descriptor, runtime, cancellationToken);
        }

        var result = await ProcessRunner.RunPowerShellAsync($"Start-Service -Name \"{descriptor.ServiceName}\" -ErrorAction Stop", cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.start_failed", 500, 500);
        }

        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    private async Task<CacheRuntimeDto> StopAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtime = await BuildRuntimeAsync(descriptor, cancellationToken);
        if (!runtime.IsServiceRegistered)
        {
            return runtime;
        }

        var result = await ProcessRunner.RunPowerShellAsync($"Stop-Service -Name \"{descriptor.ServiceName}\" -ErrorAction SilentlyContinue", cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.stop_failed", 500, 500);
        }

        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    private async Task<CacheRuntimeDto> BuildRuntimeAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var installedComponent = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        var installDir = installedComponent?.InstallDir ?? string.Empty;
        var executablePath = string.IsNullOrWhiteSpace(installDir)
            ? string.Empty
            : Directory.Exists(installDir)
                ? Directory.GetFiles(installDir, descriptor.ExecutableName, SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty
                : string.Empty;

        var serviceStatus = await ReadServiceStatusAsync(descriptor.ServiceName, cancellationToken);

        // Redis çalışıyorsa canlı metrikleri (RESP INFO) oku — salt-okunur, best-effort.
        CacheMetricsDto? metrics = null;
        if (descriptor.ComponentId == "redis" &&
            string.Equals(serviceStatus, "Running", StringComparison.OrdinalIgnoreCase))
        {
            metrics = await ReadRedisInfoAsync(descriptor.DefaultPort, cancellationToken);
        }

        return new CacheRuntimeDto
        {
            ComponentId = descriptor.ComponentId,
            DisplayName = descriptor.DisplayName,
            ServiceName = descriptor.ServiceName,
            DefaultPort = descriptor.DefaultPort,
            InstallDir = installDir,
            ExecutablePath = executablePath,
            IsInstalled = installedComponent is not null,
            IsServiceRegistered = serviceStatus is not null,
            Status = serviceStatus ?? (installedComponent is null ? "NotInstalled" : "Stopped"),
            Metrics = metrics
        };
    }

    // Redis'e RESP ile bağlanıp INFO komutunu çalıştırır, metrikleri ayrıştırır. Şifre korumalıysa (NOAUTH)
    // veya erişilemezse null döner (panel kendi AdaRedis'inde parola yok). Sadece localhost, 2.5 sn timeout.
    private static async Task<CacheMetricsDto?> ReadRedisInfoAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(2500);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);

            await using var stream = client.GetStream();
            var command = "INFO\r\n"u8.ToArray();
            await stream.WriteAsync(command, cts.Token);

            var buffer = new byte[16384];
            var builder = new StringBuilder();
            while (builder.Length < 131072)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var current = builder.ToString();
                if (current.Contains("uptime_in_seconds", StringComparison.Ordinal) &&
                    current.Contains("used_memory_human", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var text = builder.ToString();
            return text.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) ? null : ParseRedisInfo(text);
        }
        catch
        {
            return null;
        }
    }

    private static CacheMetricsDto ParseRedisInfo(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                map[line[..separator]] = line[(separator + 1)..];
            }
        }

        long keys = 0;
        foreach (var pair in map)
        {
            if (pair.Key.StartsWith("db", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(pair.Value, "keys=(\\d+)");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var dbKeys))
                {
                    keys += dbKeys;
                }
            }
        }

        return new CacheMetricsDto
        {
            Version = map.GetValueOrDefault("redis_version", string.Empty),
            UsedMemory = map.GetValueOrDefault("used_memory_human", string.Empty),
            ConnectedClients = int.TryParse(map.GetValueOrDefault("connected_clients"), out var clients) ? clients : 0,
            Hits = long.TryParse(map.GetValueOrDefault("keyspace_hits"), out var hits) ? hits : 0,
            Misses = long.TryParse(map.GetValueOrDefault("keyspace_misses"), out var misses) ? misses : 0,
            UptimeSeconds = long.TryParse(map.GetValueOrDefault("uptime_in_seconds"), out var uptime) ? uptime : 0,
            Keys = keys
        };
    }

    private async Task InstallServiceAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            throw new AppException("cache.executable_not_found", 404, 404);
        }

        string command = descriptor.ComponentId switch
        {
            "redis" => BuildRedisInstallCommand(runtime.ExecutablePath, runtime.InstallDir, descriptor.ServiceName, descriptor.DefaultPort),
            "memcached" => $"& '{runtime.ExecutablePath}' -d install -n {descriptor.ServiceName}",
            _ => string.Empty
        };

        var result = await ProcessRunner.RunPowerShellAsync(command, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.service_install_failed", 500, 500);
        }
    }

    private static string BuildRedisInstallCommand(string executablePath, string installDir, string serviceName, int port)
    {
        var configPath = Path.Combine(installDir, "adapanel-redis.conf");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, $"""
                bind 127.0.0.1
                protected-mode yes
                port {port}
                dir {installDir}
                """);
        }

        return $"& '{executablePath}' --service-install '{configPath}' --service-name {serviceName}";
    }

    private static async Task<string?> ReadServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $@"$service = Get-Service -Name ""{serviceName}"" -ErrorAction SilentlyContinue
if ($service) {{ [pscustomobject]@{{ Status = $service.Status.ToString() }} | ConvertTo-Json -Depth 2 }}",
            cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.TryGetProperty("Status", out var statusElement)
            ? statusElement.GetString()
            : null;
    }

    private sealed record CacheDescriptor(
        string ComponentId,
        string DisplayName,
        string ServiceName,
        int DefaultPort,
        string ExecutableName);
}
