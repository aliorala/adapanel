using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Reuses the real SQLite/JSON/controller/service fixture from the queued create tests.
public sealed partial class QueuedDatabaseActorLifecycleTests
{
    [Theory]
    [InlineData("deleted")]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("password")]
    [InlineData("parent_deleted")]
    [InlineData("parent_disabled")]
    [InlineData("parent_role")]
    [InlineData("reparented")]
    [InlineData("detached")]
    [InlineData("section")]
    public async Task QueuedDeleteRemeasuresAuthorityAfterResourceWait(string change)
    {
        using var fixture = new Fixture();
        var child = change.StartsWith("parent_") || change is "reparented" or "detached" or "section";
        var actor = child ? fixture.Child : fixture.Customer;
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        var controller = fixture.Controller(actor);
        await fixture.ControllerContext.Users.SingleAsync(user => user.Id == actor);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Name, cancellation.Token);
        var deleting = Capture(() => controller.DeleteAsync(fixture.Name, "mysql", cancellation.Token));
        try
        {
            await WaitUntil(() => GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2 || deleting.IsCompleted, cancellation.Token);
            Assert.False(deleting.IsCompleted);
            await fixture.Change(change, actor);
        }
        finally { lease.Dispose(); }
        var error = await deleting.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.Contains(Assert.IsType<AppException>(error).MessageKey,
            new[] { "auth.unauthorized", "auth.forbidden", "auth.section_forbidden", "database.not_owned" });
    }

    [Fact]
    public async Task QueuedDeleteRemeasuresAuthorityAfterSubscriptionWait()
    {
        using var fixture = new Fixture();
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        var controller = fixture.Controller(fixture.Child);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterSubscriptionAsync(fixture.Customer, cancellation.Token);
        var deleting = Capture(() => controller.DeleteAsync(fixture.Name, "mysql", cancellation.Token));
        try
        {
            await WaitForReferences(fixture.Customer, 2, deleting, cancellation.Token);
            await fixture.Change("parent_disabled", fixture.Child);
        }
        finally { lease.Dispose(); }
        var error = await deleting.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.IsType<AppException>(error);
    }

    [Theory]
    [InlineData("disable_parent")]
    [InlineData("disable_child")]
    [InlineData("delete_child")]
    [InlineData("admin_disable_child")]
    [InlineData("admin_delete_child")]
    [InlineData("delete_parent")]
    public async Task AccountMutationsWaitUntilAuthorizedDeleteFinishes(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        var entered = Signal();
        var release = Signal();
        fixture.Provider.BeforeDelete = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = fixture.Controller(fixture.Child).DeleteAsync(fixture.Name, "mysql", cancellation.Token);
        if (await Task.WhenAny(entered.Task, deleting).WaitAsync(Deadline) == deleting) await deleting;
        await entered.Task.WaitAsync(Deadline);
        var changing = Capture(() => fixture.Mutate(operation, cancellation.Token));
        bool waited;
        bool actorStillActive;
        try
        {
            // Parent deletion already waits on the DB resource in the baseline. Observe
            // either legitimate wait; a test must not hang merely because it chose a gate.
            await WaitUntil(() => References(fixture.Customer) >= 2 || changing.IsCompleted ||
                GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2, cancellation.Token);
            waited = !changing.IsCompleted;
            using var observer = fixture.Context();
            actorStillActive = await observer.Users.AnyAsync(user => user.Id == fixture.Child && user.IsActive);
        }
        finally { release.TrySetResult(); }
        await deleting.WaitAsync(Deadline);
        Assert.Null(await changing.WaitAsync(Deadline));
        Assert.True(actorStillActive);
        Assert.True(waited, "Account changes must wait for the authorized database delete.");
        Assert.Equal(1, fixture.Provider.DeleteCalls);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Administrator")]
    [InlineData("SubUser")]
    public async Task ActiveActorStillDeletesOwnedDatabase(string kind)
    {
        using var fixture = new Fixture();
        var actor = kind switch { "SubUser" => fixture.Child, "Administrator" => fixture.Admin, "Reseller" => fixture.Reseller, _ => fixture.Customer };
        var owner = kind == "SubUser" ? fixture.Customer : actor;
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, owner, null);
        await fixture.Controller(actor).DeleteAsync(fixture.Name, "mysql", default);
        Assert.Equal(1, fixture.Provider.DeleteCalls);
        Assert.Null(await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
    }

    [Fact]
    public async Task PasswordRevokedDuringDatabaseAccountWaitPreventsDrop()
    {
        using var fixture = new Fixture();
        var account = "shared_" + Guid.NewGuid().ToString("N");
        await fixture.Ownership.RecordAsync("mysql", fixture.Name, fixture.Customer, null);
        await fixture.Credentials.RecordCreatedAsync("mysql", account, "%", fixture.Name, "fixture-secret", default);
        using var cancellation = new CancellationTokenSource(Deadline);
        using var engineLease = await DatabaseLifecycle.EnterSharedEngineAsync("mysql", cancellation.Token);
        var accountLease = await DatabaseLifecycle.EnterAccountAsync(engineLease, "mysql", account, cancellation.Token);
        var deleting = Capture(() => fixture.Controller(fixture.Customer).DeleteAsync(fixture.Name, "mysql", cancellation.Token));
        try
        {
            await WaitUntil(() => DatabaseLifecycle.AccountLeaseReferences("mysql", account) >= 2 || deleting.IsCompleted, cancellation.Token);
            Assert.False(deleting.IsCompleted);
            // AuthService password changes are independent of subscription mutation leases.
            await fixture.Change("password", fixture.Customer);
        }
        finally { accountLease.Dispose(); }
        var error = await deleting.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.DeleteCalls);
        Assert.Equal(fixture.Customer, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
        Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(error).MessageKey);
    }
}
