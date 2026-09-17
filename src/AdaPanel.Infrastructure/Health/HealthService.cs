using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Health;

/// <summary>
/// panel "Server Health / Repair Kit" benzeri: kritik Windows servislerinin durumunu gösterir ve
/// tek tıkla yeniden başlatır. Yalnız BİLİNEN servisler (çekirdek IIS/FTP + kurulu bileşenlerin
/// servisleri) hedeflenir — keyfi servis durdurma/başlatma engellenir.
/// </summary>
public sealed class HealthService(AdaPanelDbContext dbContext) : IHealthService
{
    private static readonly Target[] CoreServices =
    [
        new("W3SVC", "IIS — Web (W3SVC)"),
        new("WAS", "IIS — Süreç Etkinleştirme (WAS)"),
        new("FTPSVC", "FTP (Microsoft FTP Service)"),
        // DNS Server: panel bölge barındırıyorsa bu servis durduğunda TÜM alan adları çözülemez.
        // Kurulu değilse listede "NotInstalled" görünür (istemci OS'ta rol yoktur).
        // DİKKAT: bu servisin "Running" olması yetmez, genel IP'ye bind olması da gerekir —
        // onu DnsService.GetServerHealthAsync ölçer, Sunucu Sağlığı ekranı ayrı kartta gösterir.
        new("DNS", "DNS Sunucusu (DNS Server)")
    ];

    public async Task<IReadOnlyCollection<ServiceHealthDto>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var targets = await ResolveTargetsAsync(cancellationToken);
        var statuses = await QueryStatusesAsync(targets.Select(target => target.Name).ToArray(), cancellationToken);
        return targets
            .Select(target => new ServiceHealthDto
            {
                Name = target.Name,
                Label = target.Label,
                Status = statuses.TryGetValue(target.Name, out var status) ? status : "Unknown"
            })
            .ToArray();
    }

    public async Task<ServiceHealthDto> RestartServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        var targets = await ResolveTargetsAsync(cancellationToken);
        var target = targets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("health.unknown_service", 404, 404);

        var result = await ProcessRunner.RunPowerShellStrictAsync(
            BuildRestartScript(target.Name),
            cancellationToken);
        if (!result.Success)
        {
            throw new AppException("health.restart_failed", 500, 500, result.StandardError);
        }

        var statuses = await QueryStatusesAsync([target.Name], cancellationToken);
        return new ServiceHealthDto
        {
            Name = target.Name,
            Label = target.Label,
            Status = statuses.TryGetValue(target.Name, out var status) ? status : "Unknown"
        };
    }

    private async Task<List<Target>> ResolveTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = new List<Target>(CoreServices);
        var components = await dbContext.InstalledComponents
            .Where(item => item.Status == ComponentInstallStatus.Installed && item.ServiceName != "")
            .Select(item => new { item.ServiceName, item.ComponentId })
            .ToListAsync(cancellationToken);

        foreach (var component in components)
        {
            if (!string.IsNullOrWhiteSpace(component.ServiceName) &&
                !targets.Any(target => string.Equals(target.Name, component.ServiceName, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(new Target(component.ServiceName, component.ComponentId));
            }
        }

        return targets;
    }

    private static async Task<Dictionary<string, string>> QueryStatusesAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return map;
        }

        var script = BuildStatusScript(names);
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.Trim());
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    AddEntry(map, element);
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddEntry(map, document.RootElement);
            }
        }
        catch
        {
            // Çözümlenemezse boş döner; UI 'Unknown' gösterir.
        }

        return map;

        static void AddEntry(Dictionary<string, string> target, JsonElement element)
        {
            if (element.TryGetProperty("name", out var nameProp) &&
                element.TryGetProperty("status", out var statusProp) &&
                nameProp.GetString() is { } name)
            {
                target[name] = statusProp.GetString() ?? "Unknown";
            }
        }
    }

    internal static string BuildStatusScript(IReadOnlyCollection<string> names)
    {
        var list = string.Join(",", names.Select(name => "'" + name.Replace("'", "''") + "'"));
        return $$"""
            @({{list}}) | ForEach-Object {
                $name = $_
                $service = Get-Service -Name $name -ErrorAction SilentlyContinue
                $status = 'NotInstalled'
                if ($service) { $status = $service.Status.ToString() }
                else {
                    $task = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
                    if ($task) {
                        $status = switch ($task.State.ToString()) {
                            'Running' { 'Running' }
                            'Queued' { 'StartPending' }
                            'Ready' { 'Stopped' }
                            'Disabled' { 'Stopped' }
                            default { 'Unknown' }
                        }
                    }
                }
                [pscustomobject]@{ name = $name; status = $status }
            } | ConvertTo-Json -Compress
            """;
    }

    internal static string BuildRestartScript(string name)
    {
        var safe = name.Replace("'", "''");
        return $$"""
            $name = '{{safe}}'
            $service = Get-Service -Name $name -ErrorAction SilentlyContinue
            if ($service) { Restart-Service -Name $name -Force -ErrorAction Stop }
            else {
                $task = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
                Stop-ScheduledTask -InputObject $task -ErrorAction Stop
                Start-ScheduledTask -InputObject $task -ErrorAction Stop
            }
            """;
    }

    private sealed record Target(string Name, string Label);
}
