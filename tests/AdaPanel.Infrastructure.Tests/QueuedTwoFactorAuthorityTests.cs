using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class QueuedTwoFactorAuthorityTests
{
    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    public async Task PasswordChangeRevokesAnAlreadyAuthorizedRequestWaitingForItsFactorLease(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        var before = await fixture.Factor();
        var recovery = await fixture.Recovery();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, deadline.Token);
        var pending = Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        try
        {
            await WaitUntilQueued(fixture, pending, deadline.Token);
            // This is the real password assignment path, including API-token revocation and audit.
            // It runs in another request/DbContext while the original request owns only a cookie.
            await fixture.ChangePassword(deadline.Token);
            var latestHash = await fixture.CurrentPasswordHash();
            Assert.NotEqual(fixture.Principal.FindFirstValue(CookieSessionValidator.CredentialStampClaim),
                CookieSessionValidator.ComputeStamp(latestHash));
            held.Dispose();
            var error = Assert.IsType<AppException>(await pending);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(before, await fixture.Factor());
            Assert.Equal(recovery, await fixture.Recovery());
            Assert.Equal(0, fixture.Authentication.SignIns);
            Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
            await fixture.AssertNoFactorAudit();
        }
        finally { held.Dispose(); await pending; }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    public async Task SigningOutRevokesAnAlreadyAuthorizedRequestWaitingForItsFactorLease(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        var before = await fixture.Factor();
        var recovery = await fixture.Recovery();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, deadline.Token);
        var pending = Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        try
        {
            await WaitUntilQueued(fixture, pending, deadline.Token);
            var session = fixture.Principal.FindFirstValue(SessionRevocationStore.SessionIdClaim)!;
            await fixture.Revocations.RevokeAsync(session, deadline.Token);
            Assert.True(await fixture.Revocations.IsRevokedAsync(session, deadline.Token));
            held.Dispose();
            var error = Assert.IsType<AppException>(await pending);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(before, await fixture.Factor());
            Assert.Equal(recovery, await fixture.Recovery());
            Assert.Equal(0, fixture.Authentication.SignIns);
            Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
            await fixture.AssertNoFactorAudit();
        }
        finally { held.Dispose(); await pending; }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    public async Task TheSameUnchangedAuthorizedRequestCompletesAfterWaiting(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, deadline.Token);
        var pending = fixture.Invoke(operation, deadline.Token);
        try
        {
            await WaitUntilQueued(fixture, pending, deadline.Token);
            held.Dispose();
            Assert.IsType<OkObjectResult>(await pending);
            var factor = await fixture.Factor();
            Assert.Equal(operation != "begin", factor.Enabled);
            Assert.Equal(operation == "begin" ? "protected:new-secret" : null, factor.Pending);
            var recovery = await fixture.Store.ReadAsync(TwoFactorRecoveryCodes.PathFor(fixture.UserId), new TwoFactorRecoveryCodes.Store());
            Assert.Equal(operation == "begin" ? 0 : 10, recovery.Codes.Count);
            Assert.Equal(operation == "confirm" ? 1 : 0, fixture.Authentication.SignIns);
            Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
        }
        finally { held.Dispose(); await pending; }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("regenerate")]
    public async Task ADisconnectedQueuedRequestCannotPublishOrRenewItsCookie(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        var before = await fixture.Factor();
        var recovery = await fixture.Recovery();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, deadline.Token);
        var pending = Record.ExceptionAsync(() => fixture.Invoke(operation, canceled.Token));
        try
        {
            await WaitUntilQueued(fixture, pending, deadline.Token);
            canceled.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await pending.WaitAsync(deadline.Token));
            Assert.Equal(1, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
            Assert.Equal(before, await fixture.Factor());
            Assert.Equal(recovery, await fixture.Recovery());
            Assert.Equal(0, fixture.Authentication.SignIns);
            await fixture.AssertNoFactorAudit();
        }
        finally { held.Dispose(); await pending; }
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
    }

    [Fact]
    public async Task DisablingWithTheOldPasswordCannotBypassTheChangedPasswordWhileQueued()
    {
        await using var fixture = await Fixture.Create("disable");
        var before = await fixture.Factor();
        var recovery = await fixture.Recovery();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = await TwoFactorLifecycle.EnterAsync(fixture.RecoveryPath, deadline.Token);
        var pending = Record.ExceptionAsync(() => fixture.Invoke("disable", deadline.Token));
        try
        {
            await WaitUntilQueued(fixture, pending, deadline.Token);
            await fixture.ChangePassword(deadline.Token);
            held.Dispose();
            var error = Assert.IsType<AppException>(await pending);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(before, await fixture.Factor());
            Assert.Equal(recovery, await fixture.Recovery());
            Assert.Equal(0, fixture.Authentication.SignIns);
            await fixture.AssertNoFactorAudit();
        }
        finally { held.Dispose(); await pending; }
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("confirm")]
    public async Task SigningOutAfterSqlWritesButBeforePublicationRollsBackTheAcceptedDraft(string operation)
    {
        var boundary = new SaveBoundary();
        await using var fixture = await Fixture.Create(operation, boundary);
        var before = await fixture.Factor();
        var recovery = await fixture.Recovery();
        var saves = 0;
        var revoked = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        boundary.AfterSave = async token =>
        {
            // Confirm saves the user and then the audit in the real SQLite transaction.
            // Begin has only the user write. Revoke after those writes, before commit/JSON
            // publication: the initial lease approval has already succeeded in both cases.
            if (++saves != (operation == "begin" ? 1 : 2)) return;
            await fixture.Revocations.RevokeAsync(fixture.Principal.FindFirstValue(SessionRevocationStore.SessionIdClaim), token);
            revoked = true;
        };
        var error = await Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        Assert.True(revoked, "The real SQL write/audit boundary was not reached.");
        Assert.Equal(operation == "begin" ? 1 : 2, saves);
        Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Equal(before, await fixture.Factor());
        Assert.Equal(recovery, await fixture.Recovery());
        Assert.Equal(0, fixture.Authentication.SignIns);
        Assert.Equal(0, TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath));
        await fixture.AssertNoFactorAudit();
    }

    private static async Task WaitUntilQueued(Fixture fixture, Task pending, CancellationToken token)
    {
        while (TwoFactorLifecycle.ReferencesForPath(fixture.RecoveryPath) != 2)
        {
            if (pending.IsCompleted)
                throw new InvalidOperationException("The controller finished before reaching the intended user lifecycle boundary.");
            await Task.Delay(5, token);
        }
        Assert.False(pending.IsCompleted);
    }

    private sealed record FactorState(bool Enabled, string? Secret, string? Pending);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Password = "Correct!Password123";
        private const string NextPassword = "Replacement!Password456";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-queued-2fa-" + Guid.NewGuid().ToString("N"));
        private readonly PasswordHasher<PanelUser> hasher = new();
        private readonly ServiceProvider services;
        private readonly IInterceptor[] interceptors;
        internal AdaPanelDbContext Db { get; }
        internal JsonFileStore Store { get; }
        internal SessionRevocationStore Revocations { get; }
        internal AuthenticationProbe Authentication { get; } = new();
        internal Guid UserId { get; } = Guid.NewGuid();
        internal ClaimsPrincipal Principal { get; private set; } = null!;
        internal AuthController Controller { get; }
        internal string RecoveryPath => Store.ResolvePath(TwoFactorRecoveryCodes.PathFor(UserId));

        private Fixture(params IInterceptor[] interceptors)
        {
            this.interceptors = interceptors;
            Directory.CreateDirectory(root);
            Db = NewContext();
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Revocations = new SessionRevocationStore(Store);
            services = new ServiceCollection().AddSingleton<IAuthenticationService>(Authentication)
                .AddSingleton(Db).AddSingleton(Revocations).BuildServiceProvider();
            Controller = new AuthController(null!, Service(Db), new AuditService(Db, null!, Store), null!, Revocations)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "auth.db")};Pooling=False").AddInterceptors(interceptors).Options);

        private AuthService Service(AdaPanelDbContext database) => new(database, hasher, new Protector(), new Totp(),
            new AuditService(database, null!, Store), null!, null!, null!, null!, new ApiTokenService(database), Store,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, NullLogger<AuthService>.Instance);

        internal static async Task<Fixture> Create(string operation, params IInterceptor[] interceptors)
        {
            var fixture = new Fixture(interceptors);
            await fixture.Db.Database.EnsureCreatedAsync();
            var enabled = operation is "regenerate" or "disable";
            var user = new PanelUser
            {
                Id = fixture.UserId, UserName = "queued-admin", Email = "queued@example.test", Role = UserRole.Administrator,
                IsActive = true, IsTwoFactorEnabled = enabled,
                TwoFactorSecretProtected = enabled ? "protected:old-secret" : null,
                PendingTwoFactorSecretProtected = operation == "confirm" ? "protected:new-secret" : null
            };
            user.PasswordHash = fixture.hasher.HashPassword(user, Password);
            fixture.Db.Users.Add(user);
            await fixture.Db.SaveChangesAsync();
            fixture.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(user.PasswordHash)),
                new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())
            }, CookieAuthenticationDefaults.AuthenticationScheme));
            fixture.Controller.HttpContext.User = fixture.Principal;
            await fixture.Store.WriteAsync(TwoFactorRecoveryCodes.PathFor(user.Id), new TwoFactorRecoveryCodes.Store
            {
                Codes = enabled ? [Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ABCDEFGHIJ"))).ToLowerInvariant()] : [],
                LastUsedTotpStep = enabled ? 42 : 0
            });
            return fixture;
        }

        internal async Task<IActionResult> Invoke(string operation, CancellationToken token) => operation switch
        {
            "begin" => (await Controller.BeginTwoFactorSetupAsync(token)).Result!,
            "confirm" => (await Controller.ConfirmTwoFactorSetupAsync(new VerifyTwoFactorRequest { Code = "123456" }, token)).Result!,
            "regenerate" => (await Controller.RegenerateRecoveryCodesAsync(token)).Result!,
            "disable" => (await Controller.DisableTwoFactorAsync(new DisableTwoFactorRequest { Password = Password }, token)).Result!,
            _ => throw new InvalidOperationException(operation)
        };

        internal async Task ChangePassword(CancellationToken token)
        {
            await using var database = NewContext();
            await Service(database).ChangePasswordAsync(UserId,
                new ChangePasswordRequest { CurrentPassword = Password, NewPassword = NextPassword }, "127.0.0.1", token);
        }

        internal async Task<FactorState> Factor()
        {
            await using var database = NewContext();
            var user = await database.Users.AsNoTracking().SingleAsync(row => row.Id == UserId);
            return new FactorState(user.IsTwoFactorEnabled, user.TwoFactorSecretProtected, user.PendingTwoFactorSecretProtected);
        }
        internal async Task<string> CurrentPasswordHash()
        {
            await using var database = NewContext();
            return await database.Users.Where(row => row.Id == UserId).Select(row => row.PasswordHash).SingleAsync();
        }
        internal Task<string> Recovery() => File.ReadAllTextAsync(RecoveryPath);
        internal async Task AssertNoFactorAudit()
        {
            await using var database = NewContext();
            Assert.DoesNotContain(await database.AuditLogs.AsNoTracking().ToListAsync(), row => row.Action.StartsWith("auth.2fa.", StringComparison.Ordinal));
        }
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Db.DisposeAsync();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-queued-2fa-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value["protected:".Length..];
    }
    private sealed class SaveBoundary : SaveChangesInterceptor
    {
        internal Func<CancellationToken, Task>? AfterSave { get; set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (AfterSave is not null) await AfterSave(cancellationToken);
            return result;
        }
    }
    private sealed class Totp : ITotpService
    {
        public string GenerateSecret() => "new-secret";
        public string BuildProvisioningUri(string issuer, string accountName, string secret) => "otpauth://totp/fixture?secret=" + secret;
        public long? VerifyCode(string secret, string code) => secret == "new-secret" && code == "123456" ? 100 : null;
    }
    private sealed class AuthenticationProbe : IAuthenticationService
    {
        internal int SignIns { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(context.User, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme)));
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        { SignIns++; return Task.CompletedTask; }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
