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
    internal Func<string, CancellationToken, Task<CommandResult>> RestartCommand { get; init; }
        = ProcessRunner.RunPowerShellStrictAsync;
    internal Func<IReadOnlyCollection<string>, CancellationToken, Task<Dictionary<string, string>>> ReadStatuses { get; init; }
        = (names, token) => WindowsServiceInspection.ReadAsync(names, token);

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
        cancellationToken.ThrowIfCancellationRequested();
        return targets
            .Select(target => new ServiceHealthDto
            {
                Name = target.Name,
                Label = target.Label,
                Status = statuses.TryGetValue(target.Name, out var status) ? status : "Unknown"
            })
            .ToArray();
    }

    public Task<ServiceHealthDto> RestartServiceAsync(string name, CancellationToken cancellationToken = default)
        => RestartCoreAsync(name, null, cancellationToken);

    public Task<ServiceHealthDto> RestartServiceAsync(string name, Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
        => RestartCoreAsync(name, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    private async Task<ServiceHealthDto> RestartCoreAsync(string name, Func<CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken)
    {
        var targets = await ResolveTargetsAsync(cancellationToken);
        var target = targets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("health.unknown_service", 404, 404);

        cancellationToken.ThrowIfCancellationRequested();
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await RestartCommand(
            BuildRestartScript(target.Name),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            throw new AppException("health.restart_failed", 500, 500, result.StandardError);
        }

        var statuses = await QueryStatusesAsync([target.Name], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var status = statuses.TryGetValue(target.Name, out var measured) ? measured : "Unknown";
        if (status != "Running")
            throw new AppException("health.restart_unverified", 500, 500, $"Service: {target.Name}; measured status: {status}.");
        return new ServiceHealthDto
        {
            Name = target.Name,
            Label = target.Label,
            Status = status
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

    private Task<Dictionary<string, string>> QueryStatusesAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
        => ReadStatuses(names, cancellationToken);

    internal static string BuildRestartScript(string name)
    {
        var safe = name.Replace("'", "''");
        return $$"""
            $name = '{{safe}}'
            # Enumerate strictly: a failed direct Get-Service lookup can hide access errors as absence.
            # Filter literal identities, then mutate the measured object; names may contain wildcard characters.
            $services = @(Get-Service -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)
            })
            if ($services.Count -gt 1) { throw 'Service identity is ambiguous.' }
            if ($services.Count -eq 1) { Restart-Service -InputObject $services[0] -Force -ErrorAction Stop }
            else {
                $tasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object {
                    [string]::Equals([string]$_.TaskName, $name, [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals([string]$_.TaskPath, '\', [System.StringComparison]::OrdinalIgnoreCase)
                })
                if ($tasks.Count -ne 1) { throw 'Scheduled task identity is missing or ambiguous.' }
                Stop-ScheduledTask -InputObject $tasks[0] -ErrorAction Stop
                Start-ScheduledTask -InputObject $tasks[0] -ErrorAction Stop
            }
            """;
    }

    private sealed record Target(string Name, string Label);
}
