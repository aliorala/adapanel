using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using AdaPanel.Core.Dtos.Ai;

namespace AdaPanel.Infrastructure.Tests;

// AuthService'in bagimliliklarinin sahteleri. Once LoginLockoutTests icinde ozel ic siniflardi;
// ikinci bir test dosyasi (ChangePasswordTests) ayni servisi kurmak isteyince buraya alindi --
// alternatifi ~150 satirlik bos govdeyi kopyalamakti.
//
// "Throwing" olanlar bilerek patlar: test edilen akisin o servise DOKUNMAMASI gerekiyor; dokunursa
// sessizce gecmek yerine test kirilsin.

internal sealed class RecordingAuditService : IAuditService
{
    public List<string> Actions { get; } = [];

    public Task WriteAsync(string action, string resource, string details, Guid? userId = null, string? ipAddress = null, CancellationToken cancellationToken = default)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<AuditLogDto>>([]);

    // OKUMA/SAKLAMA uclari bu sahtede kasten patlar (dosyanin basindaki "Throwing" kurali):
    // AuthService bunlara DOKUNMAMALI; dokunursa test sessizce gecmek yerine kirilsin.
    // Denetim kaydi OKUMA davranisi AuditLogReadTests'te GERCEK AuditService ile dogrulanir.
    public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde denetim kaydi okunmamali.");

    public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde denetim kaydi disa aktarilmamali.");

    public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde kapsam cozulmemeli.");

    public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde saklama politikasi okunmamali.");

    public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde saklama politikasi degistirilmemeli.");

    public Task<int> PurgeAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bu testlerde temizlik calistirilmamali.");
}

internal sealed class CountingBruteForceProtection : IBruteForceProtectionService
{
    public int Failures { get; private set; }

    public int Successes { get; private set; }

    /// <summary>Bu testlerde IP hiç engelli değil; amaç kilitlenme sayaçlarını ölçmek.</summary>
    public bool Banned { get; set; }

    public Task<bool> IsBannedAsync(string? ipAddress, CancellationToken cancellationToken = default)
        => Task.FromResult(Banned);

    public Task RegisterFailedAttemptAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        Failures++;
        return Task.CompletedTask;
    }

    public Task RegisterSuccessAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        Successes++;
        return Task.CompletedTask;
    }

    public Task<BruteForceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<BruteForceStatusDto> UpdateSettingsAsync(UpdateBruteForceSettingsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<BruteForceStatusDto> UnbanAsync(string ipAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task CleanupExpiredBansAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

// Aşağıdakiler giriş akışında ÇAĞRILMAZ; çağrılırsa test kırmızıya döner (istenen davranış).
internal sealed class ThrowingSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => throw new NotSupportedException();

    public string Unprotect(string protectedValue) => throw new NotSupportedException();
}

internal sealed class ThrowingTotpService : ITotpService
{
    public string GenerateSecret() => throw new NotSupportedException();

    public string BuildProvisioningUri(string issuer, string accountName, string secret) => throw new NotSupportedException();

    public bool VerifyCode(string secret, string code) => throw new NotSupportedException();
}

internal sealed class ThrowingNotificationService : INotificationService
{
    public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ThrowingPreferencesService : IPanelPreferencesService
{
    public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ThrowingSecurityService : IPanelSecurityService
{
    public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Giris testi anahtar servisine hic dokunmamali; dokunursa test patlar.</summary>
internal sealed class ThrowingApiTokenService : IApiTokenService
{
    public Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ThrowingCustomerScopeService : ICustomerScopeService
{
    public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // --- Abonelik iş modeli (bu testlerde kullanılmıyor) ---
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
