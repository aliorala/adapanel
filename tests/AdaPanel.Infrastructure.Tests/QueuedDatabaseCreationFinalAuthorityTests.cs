using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Shares the real controller/provider/SQLite/JSON fixture used for the equivalent DROP gate.
// The account lease is observed directly; there are no external DB or account operations.
public sealed partial class QueuedDatabaseActorLifecycleTests
{
    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Administrator")]
    [InlineData("SubUser")]
    public async Task FinalCreateGateRejectsPasswordRevokedDuringAccountWait(string kind)
    {
        using var fixture = new Fixture();
        var actor = fixture.Actor(kind);
        await fixture.SeedCurrentPassword(actor);
        var controller = fixture.Controller(actor);
        await fixture.StampPrincipal(controller.User, actor);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        var request = fixture.RequestWithAccount(accountName);
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var account = await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token);
        var creating = Capture(() => controller.CreateAsync(request, timeout.Token));
        try
        {
            await WaitForCreateAccount(accountName, creating, timeout.Token);
            // A real password change revokes credentials independently of the subscription
            // mutation lease. The original cookie remains attached to the queued request.
            await fixture.ChangeOwnPassword(actor, timeout.Token);
            Assert.Equal(1, fixture.PasswordTokenRevocations);
        }
        finally { account.Dispose(); }

        var failure = await creating.WaitAsync(Deadline);
        Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Empty(await fixture.Ownership.GetAllAsync());
        Assert.False(await fixture.Context().AuditLogs.AnyAsync(item => item.Action == "database.create.completed"));
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", accountName));
        Assert.False(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("SubUser")]
    public async Task FinalCreateGateRemeasuresQuotaAfterAccountWait(string kind)
    {
        using var fixture = new Fixture();
        await fixture.Scopes.SetExtraLimitsAsync(fixture.Customer, 2, 0, 0);
        await fixture.Ownership.RecordAsync("mysql", "existing_db", fixture.Customer, null);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        var controller = fixture.Controller(fixture.Actor(kind));
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var account = await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token);
        var creating = Capture(() => controller.CreateAsync(fixture.RequestWithAccount(accountName), timeout.Token));
        try
        {
            await WaitForCreateAccount(accountName, creating, timeout.Token);
            await fixture.Scopes.SetExtraLimitsAsync(fixture.Customer, 1, 0, 0, timeout.Token);
        }
        finally { account.Dispose(); }

        var failure = await creating.WaitAsync(Deadline);
        Assert.Equal("database.quota_exceeded", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Null(await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", "existing_db"));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Administrator")]
    [InlineData("SubUser")]
    public async Task FinalCreateGateStillAllowsUnchangedActorAfterAccountWait(string kind)
    {
        using var fixture = new Fixture();
        var actor = fixture.Actor(kind);
        await fixture.SeedCurrentPassword(actor);
        var controller = fixture.Controller(actor);
        await fixture.StampPrincipal(controller.User, actor);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var account = await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token);
        var creating = Capture(() => controller.CreateAsync(fixture.RequestWithAccount(accountName), timeout.Token));
        try { await WaitForCreateAccount(accountName, creating, timeout.Token); }
        finally { account.Dispose(); }

        Assert.Null(await creating.WaitAsync(Deadline));
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Equal(kind == "SubUser" ? fixture.Customer : actor,
            await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", accountName));
        Assert.False(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
    }

    [Fact]
    public async Task FinalCreateGateCancellationDuringAccountWaitDoesNotMutateOrLeakLease()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller(fixture.Customer);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var account = await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var creating = Capture(() => controller.CreateAsync(fixture.RequestWithAccount(accountName), cancelled.Token));
        try
        {
            await WaitForCreateAccount(accountName, creating, timeout.Token);
            cancelled.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await creating.WaitAsync(Deadline));
            Assert.Equal(1, DatabaseLifecycle.AccountLeaseReferences("mysql", accountName));
        }
        finally { account.Dispose(); }
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Empty(await fixture.Ownership.GetAllAsync());
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", accountName));
        Assert.False(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
        Assert.False(DatabaseLifecycle.HasSubscriptionGate(fixture.Customer));
    }

    [Theory]
    [InlineData("resource", "password")]
    [InlineData("resource", "disabled")]
    [InlineData("resource", "deleted")]
    [InlineData("resource", "role")]
    [InlineData("account", "password")]
    [InlineData("account", "disabled")]
    [InlineData("account", "deleted")]
    [InlineData("account", "role")]
    public async Task FinalCreateGateStandaloneAdminRemeasuresActorAfterWait(string boundary, string change)
    {
        using var fixture = new Fixture();
        await fixture.SeedCurrentPassword(fixture.Admin);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        var create = await fixture.AdminCreateAction(fixture.RequestWithAccount(accountName));
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var held = boundary == "account"
            ? await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token)
            : await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Name, timeout.Token);
        var creating = Capture(() => create(timeout.Token));
        try
        {
            if (boundary == "account") await WaitForCreateAccount(accountName, creating, timeout.Token);
            else
            {
                await WaitUntil(() => GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2 || creating.IsCompleted, timeout.Token);
                Assert.False(creating.IsCompleted);
            }
            if (change == "password") await fixture.ChangeOwnPassword(fixture.Admin, timeout.Token);
            else await fixture.Change(change, fixture.Admin);
        }
        finally { held.Dispose(); }

        Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(await creating.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.False(await fixture.Context().AuditLogs.AnyAsync(item => item.Action == "database.create.completed"));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("account")]
    public async Task FinalCreateGateStandaloneAdminStillCreatesAfterUnchangedWait(string boundary)
    {
        using var fixture = new Fixture();
        await fixture.SeedCurrentPassword(fixture.Admin);
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        var create = await fixture.AdminCreateAction(fixture.RequestWithAccount(accountName));
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var held = boundary == "account"
            ? await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token)
            : await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Name, timeout.Token);
        var creating = Capture(() => create(timeout.Token));
        try
        {
            if (boundary == "account") await WaitForCreateAccount(accountName, creating, timeout.Token);
            else
            {
                await WaitUntil(() => GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2 || creating.IsCompleted, timeout.Token);
                Assert.False(creating.IsCompleted);
            }
        }
        finally { held.Dispose(); }
        Assert.Null(await creating.WaitAsync(Deadline));
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.True(await fixture.Context().AuditLogs.AnyAsync(item => item.Action == "database.create.completed"));
    }

    [Fact]
    public async Task FinalCreateGateStandaloneAdminCancellationKeepsProviderUntouched()
    {
        using var fixture = new Fixture();
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        var create = await fixture.AdminCreateAction(fixture.RequestWithAccount(accountName));
        using var timeout = new CancellationTokenSource(Deadline);
        using var engine = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", timeout.Token);
        var account = await DatabaseLifecycle.EnterAccountAsync(engine, "mysql", accountName, timeout.Token);
        using var cancelled = new CancellationTokenSource(Deadline);
        var creating = Capture(() => create(cancelled.Token));
        try
        {
            await WaitForCreateAccount(accountName, creating, timeout.Token);
            cancelled.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await creating.WaitAsync(Deadline));
        }
        finally { account.Dispose(); }
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", accountName));
        Assert.False(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalCreateGateInvokesApprovalOnceAndHonorsItsCancellation(bool cancelAtApproval)
    {
        using var fixture = new Fixture();
        var accountName = "account_" + Guid.NewGuid().ToString("N")[..20];
        using var tokenSource = new CancellationTokenSource(Deadline);
        var approvals = 0;
        var publications = 0;
        var failure = await Capture(() => fixture.DatabaseForFinalGate().CreateDatabaseAsync(fixture.RequestWithAccount(accountName),
            token =>
            {
                approvals++;
                Assert.True(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
                Assert.Equal(1, DatabaseLifecycle.AccountLeaseReferences("mysql", accountName));
                if (cancelAtApproval) tokenSource.Cancel();
                return Task.CompletedTask;
            },
            token => { publications++; return Task.CompletedTask; }, tokenSource.Token));

        Assert.Equal(1, approvals);
        if (cancelAtApproval)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(0, fixture.Provider.CreateCalls);
            Assert.Equal(0, publications);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(1, fixture.Provider.CreateCalls);
            Assert.Equal(1, publications);
        }
        Assert.False(DatabaseLifecycle.HasAccountGate("mysql", accountName));
        Assert.False(DatabaseLifecycle.HasResourceGate("mysql", fixture.Name));
    }

    private static async Task WaitForCreateAccount(string account, Task creating, CancellationToken token)
    {
        await WaitUntil(() => DatabaseLifecycle.AccountLeaseReferences("mysql", account) >= 2 || creating.IsCompleted, token);
        Assert.False(creating.IsCompleted, "Create ended before the account lease boundary.");
        Assert.Equal(2, DatabaseLifecycle.AccountLeaseReferences("mysql", account));
    }

    private sealed partial class Fixture
    {
        private const string CurrentFixturePassword = "Current-Fixture-Password!42";
        private const string NextFixturePassword = "Changed-Fixture-Password!43";
        public int PasswordTokenRevocations { get; private set; }
        public Guid Actor(string kind) => kind switch
        {
            "Administrator" => Admin, "Reseller" => Reseller, "SubUser" => Child, _ => Customer
        };
        public CreateDatabaseRequest RequestWithAccount(string account) => new()
        {
            Engine = "mysql", Name = Name, UserName = account, Password = "New-Database-Password!42"
        };
        public async Task SeedCurrentPassword(Guid actor)
        {
            var db = Context();
            var user = await db.Users.SingleAsync(item => item.Id == actor);
            user.PasswordHash = new PasswordHasher<PanelUser>().HashPassword(user, CurrentFixturePassword);
            await db.SaveChangesAsync();
        }
        public async Task StampPrincipal(ClaimsPrincipal principal, Guid actor)
        {
            var hash = await Context().Users.AsNoTracking().Where(item => item.Id == actor).Select(item => item.PasswordHash).SingleAsync();
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(hash)));
        }
        public Task ChangeOwnPassword(Guid actor, CancellationToken token)
        {
            var db = Context();
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            var service = new AuthService(db, new PasswordHasher<PanelUser>(), new Protector(),
                Fake<ITotpService>((method, _) => throw new NotSupportedException(method.Name)), new AuditService(db, Scopes, files),
                Fake<IBruteForceProtectionService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<IPanelPreferencesService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<IArkaPlanIsKuyrugu>((method, _) => throw new NotSupportedException(method.Name)), Scopes,
                Fake<IApiTokenService>((method, _) => method.Name == "RevokeAllForUserAsync" ? Revoke() : throw new NotSupportedException(method.Name)),
                files, options, Fake<IServerOwnerService>((method, _) => throw new NotSupportedException(method.Name)), NullLogger<AuthService>.Instance);
            return service.ChangePasswordAsync(actor, new ChangePasswordRequest
            {
                CurrentPassword = CurrentFixturePassword, NewPassword = NextFixturePassword
            }, null, token);
        }
        public async Task<Func<CancellationToken, Task>> AdminCreateAction(CreateDatabaseRequest request)
        {
            ControllerContext = Context();
            var protector = new Protector();
            var database = new DatabaseService(Context(), protector, new DatabaseCredentialStore(files, protector), new SiteDatabaseLinkService(files), [Provider]);
            var controller = new DatabasesController(database,
                Fake<IRemoteDbAccessService>((method, _) => throw new NotSupportedException(method.Name)), Ownership,
                new SiteDatabaseLinkService(files), sites, new AuditService(ControllerContext, Scopes, files), new ConcurrencyLock())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, Admin.ToString()), new Claim(ClaimTypes.Role, "Administrator")], "test"))
                } }
            };
            await StampPrincipal(controller.User, Admin);
            var actorContext = ControllerContext;
            // The baseline action has no authority dependencies. Match the action's actual
            // parameters so the same real-controller test covers its mandatory DI fix.
            return token =>
            {
                var action = typeof(DatabasesController).GetMethod(nameof(DatabasesController.CreateDatabaseAsync))!;
                var args = action.GetParameters().Select(parameter => parameter.ParameterType == typeof(CreateDatabaseRequest) ? (object)request
                    : parameter.ParameterType == typeof(AdaPanelDbContext) ? actorContext
                    : parameter.ParameterType == typeof(ICustomerScopeService) ? Scopes
                    : parameter.ParameterType == typeof(CancellationToken) ? token
                    : throw new InvalidOperationException("Unexpected create dependency: " + parameter.Name)).ToArray();
                return (Task)action.Invoke(controller, args)!;
            };
        }
        public DatabaseService DatabaseForFinalGate() => new(Context(), new Protector(), Credentials, new SiteDatabaseLinkService(files), [Provider]);
        private Task<int> Revoke() { PasswordTokenRevocations++; return Task.FromResult(2); }
    }
}
