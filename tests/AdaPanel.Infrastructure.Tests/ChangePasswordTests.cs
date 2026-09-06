using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Dtos.Auth;
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
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kullanıcının kendi parolasını değiştirmesi.
/// <para>
/// Sabitlenen üç davranış: (1) mevcut parola bilinmeden değişiklik olmaz — aksi hâlde çalınmış bir
/// oturum çerezi hesabı kalıcı olarak devralmaya yeterdi; (2) aynı parola "değiştirildi" sayılmaz;
/// (3) değişiklik API anahtarlarını DÜŞÜRÜR — anahtar paroladan bağımsız bir kimlik bilgisidir ve
/// düşürülmezse erişimi kesmek isteyen kullanıcı bunu başaramaz.
/// </para>
/// </summary>
public sealed class ChangePasswordTests
{
    private const string CurrentPassword = "EskiParola!2026";
    private const string NewPassword = "YeniParola!2026";

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_DoesNotChangeAnything()
    {
        await using var harness = await Harness.CreateAsync();
        var originalHash = harness.User.PasswordHash;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ChangePasswordAsync(
            harness.User.Id,
            new ChangePasswordRequest { CurrentPassword = "YanlisParola!2026", NewPassword = NewPassword },
            "203.0.113.10"));

        Assert.Equal(originalHash, harness.User.PasswordHash);
        // Basarisiz denemede anahtarlar da DUSMEMELI: aksi halde parolayi bilmeyen biri, sadece
        // deneyerek kurbanin yapay zeka baglantisini kesebilirdi (hizmet engelleme).
        Assert.Equal(0, harness.Tokens.RevokeAllCallCount);
    }

    [Fact]
    public async Task ChangePassword_WithSamePassword_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ChangePasswordAsync(
            harness.User.Id,
            new ChangePasswordRequest { CurrentPassword = CurrentPassword, NewPassword = CurrentPassword },
            null));

        Assert.Equal(0, harness.Tokens.RevokeAllCallCount);
    }

    [Fact]
    public async Task ChangePassword_WithCorrectCurrentPassword_UpdatesHashAndRevokesApiTokens()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Tokens.ActiveTokens = 3;
        var originalHash = harness.User.PasswordHash;

        var result = await harness.Service.ChangePasswordAsync(
            harness.User.Id,
            new ChangePasswordRequest { CurrentPassword = CurrentPassword, NewPassword = NewPassword },
            "203.0.113.10");

        Assert.NotEqual(originalHash, harness.User.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<PanelUser>().VerifyHashedPassword(harness.User, harness.User.PasswordHash, NewPassword));

        // Kac anahtarin dustugu CAGIRANA bildirilir; sessizce kesilirse kullanici yapay zeka
        // baglantisinin neden calismadigini anlamaz.
        Assert.Equal(3, result.RevokedApiTokens);
        Assert.Equal(1, harness.Tokens.RevokeAllCallCount);
    }

    [Fact]
    public async Task ChangePassword_ClearsAnActiveLockout()
    {
        await using var harness = await Harness.CreateAsync();
        harness.User.FailedLoginCount = 4;
        harness.User.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.ChangePasswordAsync(
            harness.User.Id,
            new ChangePasswordRequest { CurrentPassword = CurrentPassword, NewPassword = NewPassword },
            null);

        // Kullanici kimligini mevcut parolayla zaten kanitladi; kilidi surdurmek onu kendi
        // hesabindan disarida birakmaktan baska ise yaramaz.
        Assert.Null(harness.User.LockedUntilUtc);
        Assert.Equal(0, harness.User.FailedLoginCount);
    }

    /// <summary>Anahtar servisinin sahtesi: kac kez ve kac anahtarla cagrildigini sayar.</summary>
    private sealed class CountingApiTokenService : IApiTokenService
    {
        public int ActiveTokens { get; set; }

        public int RevokeAllCallCount { get; private set; }

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            RevokeAllCallCount++;
            return Task.FromResult(ActiveTokens);
        }

        public Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string dataRoot;

        private Harness(SqliteConnection connection, AdaPanelDbContext dbContext, AuthService service, PanelUser user, CountingApiTokenService tokens, string dataRoot)
        {
            this.connection = connection;
            DbContext = dbContext;
            Service = service;
            User = user;
            Tokens = tokens;
            this.dataRoot = dataRoot;
        }

        public AdaPanelDbContext DbContext { get; }

        public AuthService Service { get; }

        public PanelUser User { get; }

        public CountingApiTokenService Tokens { get; }

        public static async Task<Harness> CreateAsync()
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
                Email = "admin@ornek.test",
                Role = UserRole.Administrator,
                PreferredLanguage = "tr",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            user.PasswordHash = hasher.HashPassword(user, CurrentPassword);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-pwd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataRoot);
            var runtimeOptions = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot });

            var tokens = new CountingApiTokenService();
            var service = new AuthService(
                dbContext,
                hasher,
                new ThrowingSecretProtector(),
                new ThrowingTotpService(),
                new RecordingAuditService(),
                new CountingBruteForceProtection(),
                new ThrowingPreferencesService(),
                new SayanIsKuyrugu(),
                new ThrowingCustomerScopeService(),
                tokens,
                new JsonFileStore(runtimeOptions),
                runtimeOptions,
                new SahipsizSunucuSahibiServisi(),
                NullLogger<AuthService>.Instance);

            return new Harness(connection, dbContext, service, user, tokens, dataRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await connection.DisposeAsync();
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Gecici klasor silinemezse test sonucunu etkilemez.
            }
        }
    }
}
