using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DatabaseAccountLeaseTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NestedAccountDoesNotReenterEngineBehindQueuedWriter()
    {
        var engine = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(Deadline);
        var resource = await DatabaseLifecycle.EnterResourceAsync(engine, "db", cancellation.Token);
        var writer = DatabaseLifecycle.EnterEngineAsync(engine, cancellation.Token);
        try
        {
            Assert.False(writer.IsCompleted);
            using var account = await DatabaseLifecycle.EnterAccountAsync(resource, engine, "shared", cancellation.Token);
            Assert.False(writer.IsCompleted);
        }
        finally { resource.Dispose(); }
        (await writer).Dispose();
        Assert.False(DatabaseLifecycle.HasAccountGate(engine, "shared"));
        Assert.False(DatabaseLifecycle.HasEngineGate(engine));
    }

    [Theory]
    [InlineData("postgres", "postgresql")]
    [InlineData("mysql", "mysql")]
    [InlineData("mariadb", "mariadb")]
    public async Task FamilyAliasIsExclusiveButCaseDistinctAccountsAreIndependent(string engine, string alias)
    {
        var name = "user_" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(Deadline);
        using var firstParent = await DatabaseLifecycle.EnterResourceAsync(engine, name + "a", cancellation.Token);
        using var secondParent = await DatabaseLifecycle.EnterResourceAsync(alias, name + "b", cancellation.Token);
        var first = await DatabaseLifecycle.EnterAccountAsync(firstParent, engine, name, cancellation.Token);
        var waiting = DatabaseLifecycle.EnterAccountAsync(secondParent, alias, name, cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
            using var independent = await DatabaseLifecycle.EnterAccountAsync(secondParent, alias, name.ToUpperInvariant(), cancellation.Token);
        }
        finally { first.Dispose(); }
        (await waiting).Dispose();
        Assert.False(DatabaseLifecycle.HasAccountGate(alias, name));
        Assert.False(DatabaseLifecycle.HasAccountGate(alias, name.ToUpperInvariant()));
    }

    [Fact]
    public async Task CancelledAccountWaiterDoesNotReleaseHolderOrLeakGate()
    {
        var engine = Guid.NewGuid().ToString("N");
        using var parent = await DatabaseLifecycle.EnterSharedEngineAsync(engine);
        var first = await DatabaseLifecycle.EnterAccountAsync(parent, engine, "account");
        using var cancellation = new CancellationTokenSource();
        var waiting = DatabaseLifecycle.EnterAccountAsync(parent, engine, "account", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(DatabaseLifecycle.HasAccountGate(engine, "account"));
        first.Dispose(); first.Dispose();
        Assert.False(DatabaseLifecycle.HasAccountGate(engine, "account"));
    }

    [Fact]
    public async Task ParentMustBeLiveAndBelongToTheSameEngine()
    {
        var engine = Guid.NewGuid().ToString("N");
        var parent = await DatabaseLifecycle.EnterSharedEngineAsync(engine);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLifecycle.EnterAccountAsync(parent, "other", "user"));
        parent.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLifecycle.EnterAccountAsync(parent, engine, "user"));
        Assert.False(DatabaseLifecycle.HasAccountGate(engine, "user"));
    }
}
