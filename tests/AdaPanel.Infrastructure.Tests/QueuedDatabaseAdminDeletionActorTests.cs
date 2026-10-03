using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedDatabaseActorLifecycleTests
{
    [Theory]
    [InlineData("deleted")]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task QueuedAdminDeleteRemeasuresActorAfterResourceWait(string change)
    {
        using var fixture = new Fixture();
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        var delete = fixture.AdminDeleteAction();
        await fixture.ControllerContext.Users.SingleAsync(user => user.Id == fixture.Admin);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Name, cancellation.Token);
        var deleting = Capture(() => delete(cancellation.Token));
        try
        {
            await WaitUntil(() => GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2 || deleting.IsCompleted, cancellation.Token);
            Assert.False(deleting.IsCompleted);
            await fixture.Change(change, fixture.Admin);
        }
        finally { lease.Dispose(); }
        var error = await deleting.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(error).MessageKey);
    }

    [Fact]
    public async Task ActiveAdminStillDeletesAnotherOwnersDatabaseThroughAdminRoute()
    {
        using var fixture = new Fixture();
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        await fixture.AdminDeleteAction()(default);
        Assert.Equal(1, fixture.Provider.DeleteCalls);
        Assert.Null(await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
    }

    [Fact]
    public async Task AdminPasswordRevokedDuringDatabaseAccountWaitPreventsDrop()
    {
        using var fixture = new Fixture();
        var account = "shared_" + Guid.NewGuid().ToString("N");
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        await fixture.Credentials.RecordCreatedAsync("mysql", account, "%", fixture.Name, "fixture-secret", default);
        var delete = fixture.AdminDeleteAction();
        using var cancellation = new CancellationTokenSource(Deadline);
        using var engineLease = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", cancellation.Token);
        var accountLease = await DatabaseLifecycle.EnterAccountAsync(engineLease, "mysql", account, cancellation.Token);
        var deleting = Capture(() => delete(cancellation.Token));
        try
        {
            await WaitUntil(() => DatabaseLifecycle.AccountLeaseReferences("mysql", account) >= 2 || deleting.IsCompleted, cancellation.Token);
            Assert.False(deleting.IsCompleted);
            await fixture.Change("password", fixture.Admin);
        }
        finally { accountLease.Dispose(); }
        var error = await deleting.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(error).MessageKey);
    }

    private sealed partial class Fixture
    {
        public Func<CancellationToken, Task> AdminDeleteAction()
        {
            ControllerContext = Context();
            var protector = new Protector();
            var database = new DatabaseService(Context(), protector, new DatabaseCredentialStore(files, protector), new SiteDatabaseLinkService(files), [Provider]);
            var controller = new DatabasesController(database,
                Fake<IRemoteDbAccessService>((method, _) => throw new NotSupportedException(method.Name)), Ownership,
                new SiteDatabaseLinkService(files), sites, new AuditService(ControllerContext, Scopes, files), new ConcurrencyLock())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(ClaimTypes.NameIdentifier, Admin.ToString()), new Claim(ClaimTypes.Role, "Administrator")], "test"))
                    }
                }
            };
            var actorContext = ControllerContext;
            // Keep the production signature unchanged for the red run. The same fixture
            // supplies the mandatory action dependencies once the authorization fix lands.
            return token =>
            {
                var action = typeof(DatabasesController).GetMethod(nameof(DatabasesController.DeleteDatabaseAsync))!;
                var args = action.GetParameters().Select(parameter => parameter.Name == "name" ? (object)Name
                    : parameter.Name == "engine" ? "mysql"
                    : parameter.ParameterType == typeof(AdaPanelDbContext) ? actorContext
                    : parameter.ParameterType == typeof(ICustomerScopeService) ? Scopes
                    : parameter.ParameterType == typeof(CancellationToken) ? token
                    : throw new InvalidOperationException("Unexpected delete dependency: " + parameter.Name)).ToArray();
                return (Task)action.Invoke(controller, args)!;
            };
        }
    }
}
