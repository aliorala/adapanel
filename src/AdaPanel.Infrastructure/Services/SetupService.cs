using System.Text.RegularExpressions;
using AdaPanel.Core.Constants;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Setup;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Services;

public sealed partial class SetupService(
    AdaPanelDbContext dbContext,
    IPasswordHasher<PanelUser> passwordHasher,
    IServiceScopeFactory scopeFactory,
    IOperationProgressService operationProgress,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<SetupService> logger) : ISetupService
{
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var isInitialized = await dbContext.Users.AnyAsync(user => user.Role == UserRole.Administrator, cancellationToken);
        var secureEntryPath = await GetSettingAsync(PanelSettingKeys.SecureEntryPath, runtimeOptions.SecureEntryPath, cancellationToken);
        var defaultLanguage = await GetSettingAsync(PanelSettingKeys.DefaultLanguage, runtimeOptions.DefaultLanguage, cancellationToken);
        var preferredPortText = await GetSettingAsync(PanelSettingKeys.PreferredHttpsPort, runtimeOptions.PreferredHttpsPort.ToString(), cancellationToken);
        var preferredPort = int.TryParse(preferredPortText, out var parsedPort)
            ? parsedPort
            : runtimeOptions.PreferredHttpsPort;

        return new SetupStatusDto
        {
            IsInitialized = isInitialized,
            RequiresSetupWizard = !isInitialized,
            SecureEntryPath = isInitialized ? string.Empty : ToDisplayPath(secureEntryPath),
            DefaultLanguage = defaultLanguage,
            PreferredHttpsPort = preferredPort,
            ApplicationVersion = VersionInfo.Current
        };
    }

    public async Task<SetupResultDto> InitializeAsync(InitialSetupRequest request, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Users.AnyAsync(user => user.Role == UserRole.Administrator, cancellationToken))
        {
            throw new InvalidOperationException("setup.already_initialized");
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("setup.password_mismatch");
        }

        var secureEntryPath = NormalizeSecurePath(request.SecureEntryPath);
        if (!SecurePathPattern().IsMatch(secureEntryPath))
        {
            throw new InvalidOperationException("setup.invalid_secure_path");
        }

        var adminUser = new PanelUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            PreferredLanguage = request.PreferredLanguage.Trim(),
            Role = UserRole.Administrator,
            IsTwoFactorEnabled = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        adminUser.PasswordHash = passwordHasher.HashPassword(adminUser, request.Password);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Users.Add(adminUser);

        UpsertSetting(PanelSettingKeys.SecureEntryPath, secureEntryPath);
        UpsertSetting(PanelSettingKeys.DefaultLanguage, request.PreferredLanguage.Trim());
        UpsertSetting(PanelSettingKeys.PreferredHttpsPort, request.PreferredHttpsPort.ToString());
        UpsertSetting(PanelSettingKeys.AllowedIpAddresses, string.Empty);
        UpsertSetting(PanelSettingKeys.IsInitialized, bool.TrueString);
        UpsertSetting(PanelSettingKeys.InitializedAtUtc, DateTime.UtcNow.ToString("O"));

        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminUser.Id,
            Action = "system.setup.completed",
            Resource = "setup",
            Details = $"Initial setup completed for admin user '{adminUser.UserName}'.",
            IpAddress = remoteIpAddress,
            CreatedAtUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("AdaPanel initial setup completed for user {UserName}", adminUser.UserName);

        // Önerilen yazılım yığını uzun sürer (indirme + kurulum dakikalarca). İstek burada
        // BLOKLANMAZ; kurulum ARKA PLANDA (kendi DI scope'unda) yürütülür ve her adımı
        // canlı ilerleme servisine yazar. Frontend /setup/progress'i yoklayıp "şu an X
        // kuruluyor" gösterir; böylece ekran donmuş gibi görünmez.
        var installInProgress = false;
        if (request.InstallRecommendedComponents)
        {
            operationProgress.Begin("setup.installing_recommended");
            installInProgress = true;
            var adminId = adminUser.Id;
            _ = Task.Run(() => RunRecommendedInstallAsync(adminId, remoteIpAddress));
        }

        return new SetupResultDto
        {
            IsInitialized = true,
            AdminUserName = adminUser.UserName,
            PreferredLanguage = adminUser.PreferredLanguage,
            PreferredHttpsPort = request.PreferredHttpsPort,
            SecureEntryPath = ToDisplayPath(secureEntryPath),
            RecommendedComponentsRequested = request.InstallRecommendedComponents,
            RecommendedComponentsInstalled = 0,
            InstallationInProgress = installInProgress,
            Warnings = []
        };
    }

    public Task<OperationProgressDto> GetInstallProgressAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(operationProgress.Snapshot());

    /// <summary>
    /// Önerilen bileşenleri arka planda kurar (kendi DI scope'u). Her adım canlı ilerleme
    /// servisine yazılır. İstek/istemci bağlantısından bağımsız tamamlanır.
    /// </summary>
    private async Task RunRecommendedInstallAsync(Guid adminId, string? remoteIpAddress)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var componentBootstrapper = scope.ServiceProvider.GetRequiredService<IComponentBootstrapper>();
            var phpService = scope.ServiceProvider.GetRequiredService<IPhpService>();
            var webDbClientService = scope.ServiceProvider.GetRequiredService<IWebDbClientService>();

            var installResults = await componentBootstrapper.InstallDefaultsAsync(adminId, remoteIpAddress, CancellationToken.None);

            foreach (var phpResult in installResults.Where(result =>
                         result.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(result.Status, "Installed", StringComparison.OrdinalIgnoreCase)))
            {
                operationProgress.StartStep("setup.step.php_config");
                try
                {
                    await phpService.RegisterFastCgiAsync(phpResult.ComponentId, CancellationToken.None);
                    operationProgress.CompleteStep("setup.step.php_config", success: true);
                }
                catch (Exception exception)
                {
                    operationProgress.CompleteStep("setup.step.php_config", success: false, exception.Message);
                    logger.LogWarning(exception, "PHP FastCGI registration failed during initial setup.");
                }
            }

            if (installResults.Any(result =>
                    result.ComponentId.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(result.Status, "Installed", StringComparison.OrdinalIgnoreCase)))
            {
                operationProgress.StartStep("setup.step.phpmyadmin");
                try
                {
                    await webDbClientService.DeployPhpMyAdminAsync(CancellationToken.None);
                    operationProgress.CompleteStep("setup.step.phpmyadmin", success: true);
                }
                catch (Exception exception)
                {
                    operationProgress.CompleteStep("setup.step.phpmyadmin", success: false, exception.Message);
                    logger.LogWarning(exception, "phpMyAdmin IIS publication could not be completed during initial setup.");
                }
            }

            operationProgress.Complete();
            logger.LogInformation("Recommended component installation finished during initial setup.");
        }
        catch (Exception exception)
        {
            operationProgress.Fail(exception.Message);
            logger.LogWarning(exception, "Recommended component installation failed during initial setup.");
        }
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

    private static string ToDisplayPath(string secureEntryPath)
    {
        var normalized = NormalizeSecurePath(secureEntryPath);
        return $"/{normalized}";
    }

    [GeneratedRegex("^[a-z0-9-]{3,64}$", RegexOptions.Compiled)]
    private static partial Regex SecurePathPattern();
}
