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

}
