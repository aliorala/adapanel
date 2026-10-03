using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Nginx;

public sealed class NginxService(
    AdaPanelDbContext dbContext,
    IAuditService auditService) : INginxService
{
    public async Task<NginxStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await NginxLifecycle.EnterAsync(cancellationToken);
        return await GetStatusCoreAsync(cancellationToken);
    }

    private async Task<NginxStatusDto> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ComponentId == "nginx" && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);
        if (component is null || !Directory.Exists(component.InstallDir))
        {
            return new NginxStatusDto();
        }

        var configuration = await NginxInstallationSupport.ReadConfigurationAsync(component.InstallDir, cancellationToken);
        var testResult = await NginxInstallationSupport.TestCurrentConfigurationAsync(component.InstallDir, cancellationToken);
        var isRunning = NginxInstallationSupport.IsRunning(component.InstallDir);
        return new NginxStatusDto
        {
            IsInstalled = true,
            IsRunning = isRunning,
            IsStartupTaskRegistered = await NginxInstallationSupport.IsStartupTaskRegisteredAsync(cancellationToken),
            Status = isRunning ? "Running" : "Stopped",
            Version = component.Version,
            InstallDir = component.InstallDir,
            ConfigPath = Path.Combine(component.InstallDir, "conf", "nginx.conf"),
            ListenAddress = configuration.ListenAddress,
            ListenPort = configuration.ListenPort,
            ServerName = configuration.ServerName,
            UpstreamUrl = configuration.UpstreamUrl,
            CacheEnabled = configuration.CacheEnabled,
            CacheSizeMb = configuration.CacheSizeMb,
            LastConfigTest = FormatResult(testResult),
            AccessUrl = BuildAccessUrl(configuration)
        };
    }

    public Task<NginxStatusDto> ConfigureAsync(
        NginxConfigurationRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
        => ConfigureAsync(request, actorUserId, ipAddress, NginxInstallationSupport.ConfigureAsync, GetStatusCoreAsync, cancellationToken);

    internal async Task<NginxStatusDto> ConfigureAsync(
        NginxConfigurationRequest request,
        Guid? actorUserId,
        string? ipAddress,
        Func<string, NginxConfigurationRequest, CancellationToken, Task> applyConfiguration,
        Func<CancellationToken, Task<NginxStatusDto>> readStatus,
        CancellationToken cancellationToken = default)
    {
        using var lease = await NginxLifecycle.EnterAsync(cancellationToken);
        var installDirectory = await GetInstallDirectoryAsync(cancellationToken);
        await applyConfiguration(installDirectory, request, cancellationToken);
        await auditService.WriteAsync(
            "nginx.configuration.updated",
            "nginx",
            $"Nginx configuration saved for {request.ListenAddress}:{request.ListenPort}, proxying to {request.UpstreamUrl}; reload command completed.",
            actorUserId,
            ipAddress,
            cancellationToken);
        return await readStatus(cancellationToken);
    }

    public Task<NginxStatusDto> StartAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
        => ChangeStateAsync("start", actorUserId, ipAddress, cancellationToken);

    public Task<NginxStatusDto> StopAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
        => ChangeStateAsync("stop", actorUserId, ipAddress, cancellationToken);

    public Task<NginxStatusDto> ReloadAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
        => ChangeStateAsync("reload", actorUserId, ipAddress, cancellationToken);

    public async Task<NginxStatusDto> TestConfigurationAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await NginxLifecycle.EnterAsync(cancellationToken);
        var installDirectory = await GetInstallDirectoryAsync(cancellationToken);
        var result = await NginxInstallationSupport.TestCurrentConfigurationAsync(installDirectory, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("nginx.config_invalid", 400, 400, FormatResult(result));
        }

        return await GetStatusCoreAsync(cancellationToken);
    }

    private async Task<NginxStatusDto> ChangeStateAsync(
        string action,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        using var lease = await NginxLifecycle.EnterAsync(cancellationToken);
        var installDirectory = await GetInstallDirectoryAsync(cancellationToken);
        switch (action)
        {
            case "start":
                await NginxInstallationSupport.StartAsync(installDirectory, cancellationToken);
                break;
            case "stop":
                await NginxInstallationSupport.StopAsync(installDirectory, cancellationToken);
                break;
            case "reload":
                await NginxInstallationSupport.ReloadAsync(installDirectory, cancellationToken);
                break;
        }

        await auditService.WriteAsync(
            $"nginx.{action}",
            "nginx",
            $"Nginx action '{action}' completed.",
            actorUserId,
            ipAddress,
            cancellationToken);
        return await GetStatusCoreAsync(cancellationToken);
    }

    private async Task<string> GetInstallDirectoryAsync(CancellationToken cancellationToken)
    {
        var installDirectory = await dbContext.InstalledComponents
            .Where(item => item.ComponentId == "nginx" && item.Status == ComponentInstallStatus.Installed)
            .Select(item => item.InstallDir)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            throw new AppException("nginx.not_installed", 404, 404);
        }

        return installDirectory;
    }

    private static string BuildAccessUrl(NginxConfigurationRequest configuration)
    {
        var host = configuration.ListenAddress switch
        {
            "0.0.0.0" => "127.0.0.1",
            "::" => "[::1]",
            _ when configuration.ListenAddress.Contains(':', StringComparison.Ordinal) => $"[{configuration.ListenAddress}]",
            _ => configuration.ListenAddress
        };
        return $"http://{host}:{configuration.ListenPort}/";
    }

    private static string FormatResult(CommandResult result)
        => string.Join(
            Environment.NewLine,
            new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
}
