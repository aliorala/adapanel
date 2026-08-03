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

/// <summary>
/// Hesap kilidi davranışı. Kilit, MEŞRU kullanıcıyı dışarıda bırakmamalı: doğru parola kilidi
/// kaldırır (panelde başka giriş yolu her zaman olmayabilir), yanlış parola kilidi korur ve her
/// deneme IP katmanının sayacını besler (asıl hız sınırlayıcı orası).
/// </summary>
public sealed class LoginLockoutTests
{
    [Fact]
    public async Task Login_WithCorrectPassword_ClearsAnActiveLockAndSignsIn()
    {
        await using var harness = await LoginHarness.CreateAsync(lockedForMinutes: 15);

        var result = await harness.Service.LoginAsync(
            new LoginRequest { UserNameOrEmail = "admin", Password = LoginHarness.CorrectPassword },
            "203.0.113.10");

        Assert.True(result.IsAuthenticated);

        var user = await harness.ReloadUserAsync();
        Assert.Null(user.LockedUntilUtc);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Contains("auth.login.lock_cleared", harness.Audit.Actions);
        Assert.Equal(1, harness.BruteForce.Successes);
    }

    [Fact]
    public async Task Login_WithWrongPassword_KeepsTheLockAndDoesNotExtendIt()
    {
        await using var harness = await LoginHarness.CreateAsync(lockedForMinutes: 15);
        var lockedUntilBefore = (await harness.ReloadUserAsync()).LockedUntilUtc;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.LoginAsync(
                new LoginRequest { UserNameOrEmail = "admin", Password = "wrong-password" },
                "203.0.113.10"));

        // Kilitliyken yanlış parola: kullanıcı NEDEN giremediğini görür.
        Assert.Equal("auth.user_locked", exception.Message);

        var user = await harness.ReloadUserAsync();
        Assert.Equal(lockedUntilBefore, user.LockedUntilUtc);
        // Hatalı deneme IP sayacına yazılır (10 hatada IP firewall'da bloklanır).
        Assert.Equal(1, harness.BruteForce.Failures);
    }

    [Fact]
    public async Task Login_LocksTheAccountAfterFiveConsecutiveFailures()
    {
        await using var harness = await LoginHarness.CreateAsync(lockedForMinutes: 0);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Service.LoginAsync(
                    new LoginRequest { UserNameOrEmail = "admin", Password = "wrong-password" },
                    "203.0.113.10"));
            Assert.Equal("auth.invalid_credentials", exception.Message);
        }

        var user = await harness.ReloadUserAsync();
        Assert.NotNull(user.LockedUntilUtc);
        Assert.True(user.LockedUntilUtc > DateTime.UtcNow);
        Assert.Equal(5, harness.BruteForce.Failures);
    }

    [Fact]
    public async Task Login_WithAnExpiredLock_SignsInWithoutTreatingItAsLocked()
    {
        await using var harness = await LoginHarness.CreateAsync(lockedForMinutes: -5);

        var result = await harness.Service.LoginAsync(
            new LoginRequest { UserNameOrEmail = "admin", Password = LoginHarness.CorrectPassword },
            "203.0.113.10");

        Assert.True(result.IsAuthenticated);
        // Süresi geçmiş kilit "kilitliydi" sayılmaz → denetim kaydına lock_cleared YAZILMAZ.
        Assert.DoesNotContain("auth.login.lock_cleared", harness.Audit.Actions);
    }

    private sealed class LoginHarness : IAsyncDisposable
    {
        public const string CorrectPassword = "Str0ng!Passw0rd";

        private readonly SqliteConnection connection;
        private readonly string dataRoot;

        private LoginHarness(
            SqliteConnection connection,
            AdaPanelDbContext dbContext,
            AuthService service,
            RecordingAuditService audit,
            CountingBruteForceProtection bruteForce,
            string dataRoot)
        {
            this.connection = connection;
            DbContext = dbContext;
            Service = service;
            Audit = audit;
            BruteForce = bruteForce;
            this.dataRoot = dataRoot;
        }

        public AdaPanelDbContext DbContext { get; }

        public AuthService Service { get; }

        public RecordingAuditService Audit { get; }

        public CountingBruteForceProtection BruteForce { get; }

        public static async Task<LoginHarness> CreateAsync(int lockedForMinutes)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options;
            var dbContext = new AdaPanelDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            var hasher = new PasswordHasher<PanelUser>();
            var user = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = "admin",
                Email = "admin@example.test",
                Role = UserRole.Administrator,
                PreferredLanguage = "tr",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                LockedUntilUtc = lockedForMinutes == 0 ? null : DateTime.UtcNow.AddMinutes(lockedForMinutes)
            };
            user.PasswordHash = hasher.HashPassword(user, CorrectPassword);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-auth-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);

            var audit = new RecordingAuditService();
            var bruteForce = new CountingBruteForceProtection();
            var service = new AuthService(
                dbContext,
                hasher,
                new ThrowingSecretProtector(),
                new ThrowingTotpService(),
                audit,
                bruteForce,
                new ThrowingNotificationService(),
                new ThrowingPreferencesService(),
                new ThrowingSecurityService(),
                new ThrowingCustomerScopeService(),
                new ThrowingApiTokenService(),
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot })),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot, LoginLockoutMinutes = 15 }),
                NullLogger<AuthService>.Instance);

            return new LoginHarness(connection, dbContext, service, audit, bruteForce, dataRoot);
        }

        public async Task<PanelUser> ReloadUserAsync()
        {
            DbContext.ChangeTracker.Clear();
            return await DbContext.Users.FirstAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await connection.DisposeAsync();
            try { Directory.Delete(dataRoot, recursive: true); } catch { /* geçici klasör */ }
        }
    }

    private sealed class RecordingAuditService : IAuditService
    {
        public List<string> Actions { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId = null, string? ipAddress = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<AuditLogDto>>([]);
    }

    private sealed class CountingBruteForceProtection : IBruteForceProtectionService
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
    private sealed class ThrowingSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => throw new NotSupportedException();

        public string Unprotect(string protectedValue) => throw new NotSupportedException();
    }

    private sealed class ThrowingTotpService : ITotpService
    {
        public string GenerateSecret() => throw new NotSupportedException();

        public string BuildProvisioningUri(string issuer, string accountName, string secret) => throw new NotSupportedException();

        public bool VerifyCode(string secret, string code) => throw new NotSupportedException();
    }

    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingPreferencesService : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingSecurityService : IPanelSecurityService
    {
        public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Giris testi anahtar servisine hic dokunmamali; dokunursa test patlar.</summary>
    private sealed class ThrowingApiTokenService : IApiTokenService
    {
        public Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingCustomerScopeService : ICustomerScopeService
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
    }
}
