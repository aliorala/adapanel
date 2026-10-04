using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using System.Data.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AuthTwoFactorLifecycleTests
{
    [Fact]
    public async Task EnabledAccountCannotBeginAReplacementWithoutDisablingItsExistingFactor()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var before = await fixture.Recovery();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.BeginTwoFactorSetupAsync(fixture.UserId));
        Assert.Equal("auth.two_factor_already_enabled", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        var user = await fixture.Reload();
        Assert.True(user.IsTwoFactorEnabled);
        Assert.Equal("protected:old-secret", user.TwoFactorSecretProtected);
        Assert.Null(user.PendingTwoFactorSecretProtected);
        Assert.Equal(before, await fixture.Recovery());
    }

    [Fact]
    public async Task EnabledAccountWithALegacyPendingSeedCannotConfirmAReplacement()
    {
        await using var fixture = await Fixture.Create(enabled: true, pending: true);
        var before = await fixture.Recovery();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null));
        Assert.Equal("auth.two_factor_already_enabled", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("protected:old-secret", (await fixture.Reload()).TwoFactorSecretProtected);
        Assert.Equal(before, await fixture.Recovery());
    }

    [Fact]
    public async Task DisabledAccountSetupPersistsItsSecretAndTheReturnedRecoveryCodes()
    {
        await using var fixture = await Fixture.Create(enabled: false);
        var setup = await fixture.Service.BeginTwoFactorSetupAsync(fixture.UserId);
        Assert.False(setup.IsTwoFactorEnabled);
        Assert.Equal("new-secret", setup.ManualEntryKey);
        Assert.Equal("protected:new-secret", (await fixture.Reload()).PendingTwoFactorSecretProtected);

        var result = await fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null);

        var user = await fixture.Reload();
        Assert.True(user.IsTwoFactorEnabled);
        Assert.True(result.User.IsTwoFactorEnabled);
        Assert.Equal("protected:new-secret", user.TwoFactorSecretProtected);
        Assert.Null(user.PendingTwoFactorSecretProtected);
        var store = await fixture.Store.ReadAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), new TwoFactorRecoveryCodes.Store());
        Assert.Equal(10, result.RecoveryCodes.Count);
        Assert.Equal(result.RecoveryCodes.Select(Hash), store.Codes);
        Assert.Equal(100, store.LastUsedTotpStep);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync(), row => row.Action == "auth.2fa.enabled");
    }

    [Fact]
    public async Task DisablingWithTheCurrentPasswordPersistsAndClearsRecoveryCodes()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var result = await fixture.Service.DisableTwoFactorAsync(fixture.UserId, Fixture.Password, null);
        var user = await fixture.Reload();
        Assert.False(result.IsTwoFactorEnabled);
        Assert.False(user.IsTwoFactorEnabled);
        Assert.Null(user.TwoFactorSecretProtected);
        var store = await fixture.Store.ReadAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), new TwoFactorRecoveryCodes.Store());
        Assert.Empty(store.Codes);
        Assert.Equal(0, store.LastUsedTotpStep);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync(), row => row.Action == "auth.2fa.disabled");
    }

    [Theory]
    [InlineData("user")]
    [InlineData("audit")]
    public async Task FailedSqlConfirmationPreservesTheRecoverySnapshotAndAllowsTheSameCodeToBeRetried(string failure)
    {
        await using var fixture = await Fixture.Create(enabled: false, pending: true);
        var before = await fixture.Recovery();
        await fixture.RejectWrites(failure);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null));

        var user = await fixture.Reload();
        Assert.False(user.IsTwoFactorEnabled);
        Assert.Equal("protected:new-secret", user.PendingTwoFactorSecretProtected);
        Assert.Equal(before, await fixture.Recovery());
        await fixture.AllowWrites();
        var result = await fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null);
        Assert.True(result.User.IsTwoFactorEnabled);
        Assert.Equal(10, result.RecoveryCodes.Count);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("audit")]
    public async Task FailedSqlDisableDoesNotDestroyTheStillEnabledAccountsRecoveryCodes(string failure)
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var before = await fixture.Recovery();
        await fixture.RejectWrites(failure);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.DisableTwoFactorAsync(fixture.UserId, Fixture.Password, null));

        var user = await fixture.Reload();
        Assert.True(user.IsTwoFactorEnabled);
        Assert.Equal("protected:old-secret", user.TwoFactorSecretProtected);
        Assert.Equal(before, await fixture.Recovery());
        await fixture.AllowWrites();
        Assert.False((await fixture.Service.DisableTwoFactorAsync(fixture.UserId, Fixture.Password, null)).IsTwoFactorEnabled);
    }

    [Fact]
    public async Task WrongPasswordCannotChangeTwoFactorOrRecoveryState()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var before = await fixture.Recovery();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DisableTwoFactorAsync(fixture.UserId, "wrong-password", null));
        Assert.True((await fixture.Reload()).IsTwoFactorEnabled);
        Assert.Equal(before, await fixture.Recovery());
    }

    [Fact]
    public async Task InvalidConfirmationCannotChangeThePendingSeedOrRecoveryState()
    {
        await using var fixture = await Fixture.Create(enabled: false, pending: true);
        var before = await fixture.Recovery();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "000000", null));
        var user = await fixture.Reload();
        Assert.False(user.IsTwoFactorEnabled);
        Assert.Equal("protected:new-secret", user.PendingTwoFactorSecretProtected);
        Assert.Equal(before, await fixture.Recovery());
    }

    [Fact]
    public async Task FailedHttpRegenerationAuditPreservesThePreviouslyIssuedRecoveryCodes()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var before = await fixture.Recovery();
        await fixture.RejectWrites("audit");
        var controller = fixture.Controller();
        await Assert.ThrowsAsync<DbUpdateException>(() => controller.RegenerateRecoveryCodesAsync(default));
        Assert.Equal(before, await fixture.Recovery());
        Assert.True((await fixture.Reload()).IsTwoFactorEnabled);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    [InlineData("disable")]
    public async Task SigningOutDuringSqlSaveCannotPublishRecoveryCodesOrCommitTwoFactorChanges(string operation)
    {
        var boundary = new SaveBoundary();
        await using var fixture = await Fixture.ForOperation(operation, boundary);
        var beforeFile = await fixture.Recovery();
        var beforeUser = await fixture.Db.Users.AsNoTracking().SingleAsync();
        var controller = fixture.Controller();
        var revokeCount = 0;
        boundary.AfterSave = async _ =>
        {
            // For recovery mutations wait until both the user UPDATE and audit INSERT have
            // run; Begin has no audit and checks immediately after its own SQL write.
            if (operation != "begin" && !fixture.Db.AuditLogs.Local.Any()) return;
            revokeCount++;
            await fixture.Revocations.RevokeAsync(Fixture.SessionId);
        };
        var error = await Assert.ThrowsAsync<AppException>(async () =>
        {
            switch (operation)
            {
                case "begin": await controller.BeginTwoFactorSetupAsync(default); break;
                case "confirm": await controller.ConfirmTwoFactorSetupAsync(new VerifyTwoFactorRequest { Code = "123456" }, default); break;
                case "regenerate": await controller.RegenerateRecoveryCodesAsync(default); break;
                case "disable": await controller.DisableTwoFactorAsync(new DisableTwoFactorRequest { Password = Fixture.Password }, default); break;
            }
        });
        Assert.Equal("auth.unauthorized", error.MessageKey);
        Assert.Equal(401, error.HttpStatusCode);
        Assert.Equal(1, revokeCount);
        Assert.Equal(beforeFile, await fixture.Recovery());
        var afterUser = await fixture.Reload();
        Assert.Equal(beforeUser.IsTwoFactorEnabled, afterUser.IsTwoFactorEnabled);
        Assert.Equal(beforeUser.TwoFactorSecretProtected, afterUser.TwoFactorSecretProtected);
        Assert.Equal(beforeUser.PendingTwoFactorSecretProtected, afterUser.PendingTwoFactorSecretProtected);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    public async Task FailedAtomicFilePublicationRollsBackSqlAndAuditWithoutLeavingTrackedChanges(string operation)
    {
        await using var fixture = await Fixture.ForOperation(operation);
        var before = await fixture.Recovery();
        // Reads and staging are permitted; replacing the destination is denied by this handle.
        using (var handle = new FileStream(fixture.RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var error = await Record.ExceptionAsync(() => fixture.Mutate(operation));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file publication failure, received {error?.GetType().Name ?? "no error"}.");
        }
        Assert.Equal(before, await fixture.Recovery());
        Assert.Equal(operation != "confirm", fixture.Db.Users.Local.Single().IsTwoFactorEnabled);
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified);
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
        Assert.Equal(operation != "confirm", (await fixture.Reload()).IsTwoFactorEnabled);
        await fixture.Mutate(operation);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    public async Task FailedSqlCommitRestoresOnlyItsOwnRecoveryGeneration(string operation)
    {
        var fault = new CommitBoundary();
        await using var fixture = await Fixture.ForOperation(operation, fault);
        var before = await fixture.Recovery();
        fault.BeforeCommit = _ => throw new IOException("fixture.commit.failed");
        await Assert.ThrowsAsync<IOException>(() => fixture.Mutate(operation));
        Assert.Equal(before, await fixture.Recovery());
        Assert.Equal(operation != "confirm", fixture.Db.Users.Local.Single().IsTwoFactorEnabled);
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
        fault.BeforeCommit = null;
        await fixture.Mutate(operation);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    public async Task LostCommitAcknowledgementDoesNotRollBackAnActuallyCommittedFactor(string operation)
    {
        var fault = new CommitBoundary();
        await using var fixture = await Fixture.ForOperation(operation, fault);
        fault.AfterCommit = _ => throw new IOException("fixture.commit.acknowledgement.lost");
        var codes = await fixture.Mutate(operation);
        var user = await fixture.Reload();
        var stored = await fixture.RecoveryStore();
        Assert.Equal(operation != "disable", user.IsTwoFactorEnabled);
        Assert.Equal(codes.Select(Hash), stored.Codes);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    public async Task CancellationBeforeFilePublicationPreservesBothStores(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new SaveBoundary();
        await using var fixture = await Fixture.ForOperation(operation, fault);
        var before = await fixture.Recovery();
        fault.BeforeSave = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Mutate(operation, cancellation.Token));
        Assert.Equal(before, await fixture.Recovery());
        Assert.Equal(operation != "confirm", (await fixture.Reload()).IsTwoFactorEnabled);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    public async Task CancellationAfterFilePublicationFinishesTheAcceptedSqlCommit(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new CommitBoundary();
        await using var fixture = await Fixture.ForOperation(operation, fault);
        fault.BeforeCommit = token =>
        {
            cancellation.Cancel();
            Assert.False(token.IsCancellationRequested);
            return Task.CompletedTask;
        };
        var codes = await fixture.Mutate(operation, cancellation.Token);
        Assert.Equal(operation != "disable", (await fixture.Reload()).IsTwoFactorEnabled);
        Assert.Equal(codes.Select(Hash), (await fixture.RecoveryStore()).Codes);
        Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CommitFailureDoesNotRestoreCodesOverAChangedRecoverySnapshot()
    {
        var fault = new CommitBoundary();
        await using var fixture = await Fixture.ForOperation("regenerate", fault);
        var external = new TwoFactorRecoveryCodes.Store { Codes = [Hash("external-code")], LastUsedTotpStep = 777 };
        fault.BeforeCommit = async _ =>
        {
            // A separate writer that does not participate in the panel's in-process lease.
            await fixture.Store.WriteAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), external);
            throw new IOException("fixture.commit.failed");
        };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Mutate("regenerate"));
        Assert.Equal("auth.two_factor_recovery_incomplete", error.MessageKey);
        Assert.Equal(external.Codes, (await fixture.RecoveryStore()).Codes);
        Assert.Equal(777, (await fixture.RecoveryStore()).LastUsedTotpStep);
        Assert.True((await fixture.Db.Users.AsNoTracking().SingleAsync()).IsTwoFactorEnabled);
        Assert.DoesNotContain(fixture.Db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("disable")]
    [InlineData("regenerate")]
    [InlineData("login")]
    public async Task AQueuedCanceledOperationCannotChangeTheAccountOrRecoveryFile(string operation)
    {
        await using var fixture = await Fixture.ForOperation(operation);
        var before = await fixture.Recovery();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, default);
        var pending = fixture.Mutate(operation, cancellation.Token);
        await WaitFor(() => TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath) == 2);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, await fixture.Recovery());
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
        Assert.Equal(1, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
        held.Dispose();
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
    }

    [Fact]
    public async Task ParallelConfirmationOnlyPublishesOneActiveFactorAndOneRecoveryGeneration()
    {
        await using var fixture = await Fixture.ForOperation("confirm");
        await using var other = fixture.NewContext();
        var secondService = fixture.CreateService(other);
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, default);
        var first = Capture(() => fixture.Service.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null));
        var second = Capture(() => secondService.ConfirmTwoFactorSetupAsync(fixture.UserId, "123456", null));
        await WaitFor(() => TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath) == 3);
        held.Dispose();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var success = Assert.Single(results, result => result.Result is not null).Result!;
        var failure = Assert.IsType<AppException>(Assert.Single(results, result => result.Error is not null).Error);
        Assert.Equal("auth.two_factor_already_enabled", failure.MessageKey);
        Assert.Equal(success.RecoveryCodes.Select(Hash), (await fixture.RecoveryStore()).Codes);
        Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
    }

    [Fact]
    public async Task QueuedLoginRechecksTheCurrentPasswordBeforeConsumingRecoveryCodes()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var before = await fixture.Recovery();
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, default);
        var pending = fixture.Mutate("login");
        await WaitFor(() => TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath) == 2);
        await using (var writer = fixture.NewContext())
        {
            var user = await writer.Users.SingleAsync();
            user.PasswordHash = new PasswordHasher<PanelUser>().HashPassword(user, "Different!Password456");
            await writer.SaveChangesAsync();
        }
        held.Dispose();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("auth.invalid_credentials", error.Message);
        Assert.Equal(before, await fixture.Recovery());
        Assert.DoesNotContain(await fixture.Db.AuditLogs.ToListAsync(), row => row.Action == "auth.login.succeeded");
    }

    [Fact]
    public async Task RegeneratedCodesBelongToTheActiveSecretAndItsAuditKeepsTheCallerAddress()
    {
        await using var fixture = await Fixture.Create(enabled: true);
        var result = await fixture.Service.RegenerateRecoveryCodesAsync(fixture.UserId, "192.0.2.45");
        var stored = await fixture.RecoveryStore();
        Assert.Equal(result.Codes.Select(Hash), stored.Codes);
        Assert.Equal(42, stored.LastUsedTotpStep);
        Assert.False(string.IsNullOrWhiteSpace(stored.SecretStamp));
        var audit = Assert.Single(await fixture.Db.AuditLogs.ToListAsync());
        Assert.Equal("auth.2fa.recovery_regenerated", audit.Action);
        Assert.Equal(fixture.UserId, audit.UserId);
        Assert.Equal("192.0.2.45", audit.IpAddress);
        Assert.Equal("auth", audit.Resource);
        Assert.DoesNotContain(result.Codes.First(), audit.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginAcceptsLegacyRecoveryCodesButRejectsCodesBoundToAnotherFactor(bool foreignGeneration)
    {
        await using var fixture = await Fixture.Create(enabled: true);
        if (foreignGeneration)
            await fixture.Store.UpdateAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), new TwoFactorRecoveryCodes.Store(), current =>
            { current.SecretStamp = "different-factor"; return current; });
        if (foreignGeneration)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Mutate("login"));
            Assert.Equal("auth.invalid_two_factor_code", error.Message);
            Assert.Single((await fixture.RecoveryStore()).Codes);
        }
        else
        {
            await fixture.Mutate("login");
            Assert.Empty((await fixture.RecoveryStore()).Codes);
            Assert.Equal(42, (await fixture.RecoveryStore()).LastUsedTotpStep);
        }
    }

    private static async Task<(TwoFactorConfirmDto? Result, Exception? Error)> Capture(Func<Task<TwoFactorConfirmDto>> action)
    {
        try { return (await action(), null); }
        catch (Exception error) { return (null, error); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray())))).ToLowerInvariant();

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Password = "Correct!Password123";
        internal const string SessionId = "fixture-auth-lifecycle-session";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-auth-2fa-" + Guid.NewGuid().ToString("N"));
        internal AdaPanelDbContext Db { get; }
        internal JsonFileStore Store { get; }
        internal AuthService Service { get; }
        internal SessionRevocationStore Revocations { get; }
        internal Guid UserId { get; } = Guid.NewGuid();
        private readonly PasswordHasher<PanelUser> hasher = new();
        internal string RecoveryPath => Store.ResolvePath(TwoFactorRecoveryCodes.PathFor(UserId));
        private readonly IInterceptor[] interceptors;
        private Fixture(params IInterceptor[] interceptors)
        {
            this.interceptors = interceptors;
            Directory.CreateDirectory(root);
            Db = NewContext();
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            Store = new JsonFileStore(options);
            Revocations = new SessionRevocationStore(Store);
            Service = CreateService(Db);
        }
        internal AuthController Controller()
        {
            var user = Db.Users.Local.Single(row => row.Id == UserId);
            return new AuthController(null!, Service, new AuditService(Db, null!, Store), null!, Revocations)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                            new Claim(ClaimTypes.Role, user.Role.ToString()),
                            new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(user.PasswordHash)),
                            new Claim(SessionRevocationStore.SessionIdClaim, SessionId)], "Cookies"))
                    }
                }
            };
        }
        internal AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "auth.db")};Pooling=False").AddInterceptors(interceptors).Options);
        internal AuthService CreateService(AdaPanelDbContext database) => new(database, hasher, new PlainProtector(), new FixtureTotp(),
            new AuditService(database, null!, Store), new CountingBruteForceProtection(), null!, null!, null!, null!, Store,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, NullLogger<AuthService>.Instance);
        internal static Task<Fixture> ForOperation(string operation, params IInterceptor[] interceptors) =>
            Create(operation is not ("confirm" or "begin"), pending: operation == "confirm", interceptors);
        internal static async Task<Fixture> Create(bool enabled, bool pending = false, params IInterceptor[] interceptors)
        {
            var fixture = new Fixture(interceptors);
            await fixture.Db.Database.EnsureCreatedAsync();
            var user = new PanelUser
            {
                Id = fixture.UserId, UserName = "fixture-admin", Email = "fixture@example.test", Role = UserRole.Administrator,
                IsActive = true, IsTwoFactorEnabled = enabled,
                TwoFactorSecretProtected = enabled ? "protected:old-secret" : null,
                PendingTwoFactorSecretProtected = pending ? "protected:new-secret" : null
            };
            user.PasswordHash = fixture.hasher.HashPassword(user, Password);
            fixture.Db.Users.Add(user);
            await fixture.Db.SaveChangesAsync();
            await fixture.Store.WriteAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), new TwoFactorRecoveryCodes.Store
            { Codes = enabled ? [Hash("ABCDE-FGHJK")] : [], LastUsedTotpStep = enabled ? 42 : 0 });
            return fixture;
        }
        internal async Task<IReadOnlyCollection<string>> Mutate(string operation, CancellationToken token = default)
        {
            switch (operation)
            {
                case "confirm": return (await Service.ConfirmTwoFactorSetupAsync(UserId, "123456", null, token)).RecoveryCodes;
                case "regenerate": return (await Service.RegenerateRecoveryCodesAsync(UserId, token)).Codes;
                case "disable": await Service.DisableTwoFactorAsync(UserId, Password, null, token); return [];
                case "begin": await Service.BeginTwoFactorSetupAsync(UserId, token); return [];
                case "login":
                    Assert.True((await Service.LoginAsync(new LoginRequest { UserNameOrEmail = "fixture-admin", Password = Password, TwoFactorCode = "ABCDE-FGHJK" }, null, token)).IsAuthenticated);
                    return [];
                default: throw new InvalidOperationException(operation);
            }
        }
        internal Task<TwoFactorRecoveryCodes.Store> RecoveryStore() => Store.ReadAsync(TwoFactorRecoveryCodes.PathFor(UserId), new TwoFactorRecoveryCodes.Store());
        internal async Task<PanelUser> Reload()
        {
            Db.ChangeTracker.Clear();
            return await Db.Users.AsNoTracking().SingleAsync(user => user.Id == UserId);
        }
        internal Task<string> Recovery() => File.ReadAllTextAsync(Store.ResolvePath(TwoFactorRecoveryCodes.PathFor(UserId)));
        internal Task RejectWrites(string target)
        {
            Assert.Equal("panel_users", Db.Model.FindEntityType(typeof(PanelUser))!.GetTableName());
            Assert.Equal("audit_logs", Db.Model.FindEntityType(typeof(AuditLog))!.GetTableName());
            return Db.Database.ExecuteSqlRawAsync(target switch
            {
                "user" => "CREATE TRIGGER deny_auth_write BEFORE UPDATE ON panel_users BEGIN SELECT RAISE(ABORT, 'fixture.auth.write.failed'); END;",
                "audit" => "CREATE TRIGGER deny_auth_write BEFORE INSERT ON audit_logs BEGIN SELECT RAISE(ABORT, 'fixture.auth.write.failed'); END;",
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            });
        }
        internal Task AllowWrites() => Db.Database.ExecuteSqlRawAsync("DROP TRIGGER deny_auth_write;");
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-auth-2fa-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }
    private sealed class CommitBoundary : DbTransactionInterceptor
    {
        internal Func<CancellationToken, Task>? BeforeCommit { get; set; }
        internal Func<CancellationToken, Task>? AfterCommit { get; set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (BeforeCommit is not null) await BeforeCommit(cancellationToken);
            return result;
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (AfterCommit is not null) await AfterCommit(cancellationToken);
        }
    }
    private sealed class SaveBoundary : SaveChangesInterceptor
    {
        internal Func<CancellationToken, Task>? BeforeSave { get; set; }
        internal Func<CancellationToken, Task>? AfterSave { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is not null) await BeforeSave(cancellationToken);
            return result;
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (AfterSave is not null) await AfterSave(cancellationToken);
            return result;
        }
    }
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }
    private sealed class FixtureTotp : ITotpService
    {
        public string GenerateSecret() => "new-secret";
        public string BuildProvisioningUri(string issuer, string accountName, string secret) => "otpauth://fixture/" + secret;
        public long? VerifyCode(string secret, string code) => secret == "new-secret" && code == "123456" ? 100L : null;
    }
}
