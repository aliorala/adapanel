using System.Net;
using System.Text.RegularExpressions;
using AdaPanel.Core.Constants;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Services;

public sealed partial class PanelSecurityService(
    AdaPanelDbContext dbContext,
    IAuditService auditService,
    IOptions<PanelRuntimeOptions> runtimeOptions) : IPanelSecurityService
{
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    public async Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var secureEntryPath = await GetSecureEntryPathAsync(cancellationToken);
        var allowedIpAddresses = await GetAllowedIpAddressesAsync(cancellationToken);

        return new SecuritySettingsDto
        {
            SecureEntryPath = $"/{secureEntryPath}",
            AllowedIpAddresses = allowedIpAddresses,
            HasIpRestrictions = allowedIpAddresses.Length > 0
        };
    }

    public async Task<SecuritySettingsDto> UpdateSettingsAsync(
        UpdateSecuritySettingsRequest request,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var secureEntryPath = NormalizeSecurePath(request.SecureEntryPath);
        if (!SecurePathPattern().IsMatch(secureEntryPath))
        {
            throw new InvalidOperationException("security.invalid_secure_path");
        }

        var normalizedIps = request.AllowedIpAddresses
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(NormalizeIpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        UpsertSetting(PanelSettingKeys.SecureEntryPath, secureEntryPath);
        UpsertSetting(PanelSettingKeys.AllowedIpAddresses, string.Join(',', normalizedIps));

        await auditService.WriteAsync(
            "security.settings.updated",
            "security",
            $"Secure entry path updated to '/{secureEntryPath}' and {normalizedIps.Length} IP restrictions configured.",
            actorUserId,
            ipAddress,
            cancellationToken);

        return new SecuritySettingsDto
        {
            SecureEntryPath = $"/{secureEntryPath}",
            AllowedIpAddresses = normalizedIps,
            HasIpRestrictions = normalizedIps.Length > 0
        };
    }

    public async Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default)
    {
        var configuredPath = await GetSettingAsync(PanelSettingKeys.SecureEntryPath, runtimeOptions.SecureEntryPath, cancellationToken);
        return NormalizeSecurePath(configuredPath);
    }

    public async Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        var allowedIpAddresses = await GetAllowedIpAddressesAsync(cancellationToken);
        if (allowedIpAddresses.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        var normalizedIp = NormalizeIpAddress(ipAddress);
        return allowedIpAddresses.Contains(normalizedIp, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string[]> GetAllowedIpAddressesAsync(CancellationToken cancellationToken)
    {
        var rawValue = await GetSettingAsync(PanelSettingKeys.AllowedIpAddresses, string.Empty, cancellationToken);
        return rawValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeIpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string> GetSettingAsync(string key, string fallbackValue, CancellationToken cancellationToken)
    {
        return await dbContext.Settings
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken)
            ?? fallbackValue;
    }

    private void UpsertSetting(string key, string value)
    {
        var existingSetting = dbContext.Settings.Local.FirstOrDefault(setting => setting.Key == key)
            ?? dbContext.Settings.FirstOrDefault(setting => setting.Key == key);

        if (existingSetting is null)
        {
            dbContext.Settings.Add(new PanelSetting
            {
                Key = key,
                Value = value,
                UpdatedAtUtc = DateTime.UtcNow
            });

            return;
        }

        existingSetting.Value = value;
        existingSetting.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static string NormalizeSecurePath(string secureEntryPath)
    {
        return secureEntryPath.Trim().Trim('/').ToLowerInvariant();
    }

    private static string NormalizeIpAddress(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress.Trim(), out var parsedAddress))
        {
            throw new InvalidOperationException("security.invalid_ip_address");
        }

        if (parsedAddress.IsIPv4MappedToIPv6)
        {
            parsedAddress = parsedAddress.MapToIPv4();
        }

        return parsedAddress.ToString();
    }

    [GeneratedRegex("^[a-z0-9-]{3,64}$", RegexOptions.Compiled)]
    private static partial Regex SecurePathPattern();
}
