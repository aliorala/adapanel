using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DatabaseLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static string Unique() => "fixture-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("postgres", "postgresql", "Foo", "Foo")]
    [InlineData("mysql", "mysql", "Foo", "foo")]
    [InlineData("sqlserver", "mssql", "Foo", "foo")]
    public async Task CanonicalResourceIsExclusive(string engine, string alias, string suffix, string otherSuffix)
    {
        var name = Unique();
        var first = await DatabaseLifecycle.EnterResourceAsync(engine, name + suffix);
        var waiting = DatabaseLifecycle.EnterResourceAsync(alias, name + otherSuffix);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting.WaitAsync(Timeout);
    }

    [Fact]
    public async Task PostgresQuotedCaseAndDifferentDatabasesCanProceedIndependently()
    {
        var name = Unique();
        using var first = await DatabaseLifecycle.EnterResourceAsync("postgres", name + "Foo");
        using var second = await DatabaseLifecycle.EnterResourceAsync("postgresql", name + "foo").WaitAsync(Timeout);
        using var third = await DatabaseLifecycle.EnterResourceAsync("postgresql", name + "other").WaitAsync(Timeout);
        using var fourth = await DatabaseLifecycle.EnterResourceAsync("mysql", name + "Foo").WaitAsync(Timeout);
    }

    [Fact]
    public async Task WholeEngineWriterWaitsForResourcesAndBlocksNewResources()
    {
        var engine = Unique();
        var first = await DatabaseLifecycle.EnterResourceAsync(engine, "first");
        var writer = DatabaseLifecycle.EnterEngineAsync(engine);
        var reader = DatabaseLifecycle.EnterResourceAsync(engine, "second");
        Assert.False(writer.IsCompleted);
        Assert.False(reader.IsCompleted);
        using var otherEngine = await DatabaseLifecycle.EnterResourceAsync(Unique(), "first").WaitAsync(Timeout);
        first.Dispose();
        var exclusive = await writer.WaitAsync(Timeout);
        Assert.False(reader.IsCompleted);
        exclusive.Dispose();
        using var last = await reader.WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancelledWriterDoesNotKeepTurnstileOrEngineReference()
    {
        var engine = Unique();
        var first = await DatabaseLifecycle.EnterResourceAsync(engine, "first");
        using var cancellation = new CancellationTokenSource();
        var writer = DatabaseLifecycle.EnterEngineAsync(engine, cancellation.Token);
        Assert.False(writer.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        var next = await DatabaseLifecycle.EnterResourceAsync(engine, "second").WaitAsync(Timeout);
        next.Dispose();
        first.Dispose();
        Assert.False(DatabaseLifecycle.HasEngineGate(engine));
    }

    [Fact]
    public async Task CancelledReaderBehindWriterDoesNotLeakReference()
    {
        var engine = Unique();
        var writer = await DatabaseLifecycle.EnterEngineAsync(engine);
        using var cancellation = new CancellationTokenSource();
        var reader = DatabaseLifecycle.EnterResourceAsync(engine, "first", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
        writer.Dispose();
        using (await DatabaseLifecycle.EnterResourceAsync(engine, "first").WaitAsync(Timeout)) { }
        Assert.False(DatabaseLifecycle.HasEngineGate(engine));
        Assert.False(DatabaseLifecycle.HasResourceGate(engine, "first"));
    }

    [Fact]
    public async Task CancelledResourceWaiterCannotReleaseCurrentHolder()
    {
        var engine = Unique();
        var holder = await DatabaseLifecycle.EnterResourceAsync(engine, "same");
        using var cancellation = new CancellationTokenSource();
        var cancelled = DatabaseLifecycle.EnterResourceAsync(engine, "same", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var waiting = DatabaseLifecycle.EnterResourceAsync(engine, "same");
        Assert.False(waiting.IsCompleted);
        holder.Dispose();
        holder.Dispose();
        (await waiting.WaitAsync(Timeout)).Dispose();
        Assert.False(DatabaseLifecycle.HasEngineGate(engine));
        Assert.False(DatabaseLifecycle.HasResourceGate(engine, "same"));
    }

    [Fact]
    public async Task SubscriptionGateSerializesOnlySameSubscriptionAndCleansCancelledWaiters()
    {
        var id = Guid.NewGuid();
        var holder = await DatabaseLifecycle.EnterSubscriptionAsync(id);
        using var other = await DatabaseLifecycle.EnterSubscriptionAsync(Guid.NewGuid()).WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var cancelled = DatabaseLifecycle.EnterSubscriptionAsync(id, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var waiting = DatabaseLifecycle.EnterSubscriptionAsync(id);
        Assert.False(waiting.IsCompleted);
        holder.Dispose();
        (await waiting.WaitAsync(Timeout)).Dispose();
        Assert.False(DatabaseLifecycle.HasSubscriptionGate(id));
    }

    [Fact]
    public async Task CompletedTransientKeysDoNotAccumulateGates()
    {
        for (var index = 0; index < 100; index++)
        {
            var engine = Unique();
            var subscription = Guid.NewGuid();
            using (await DatabaseLifecycle.EnterSubscriptionAsync(subscription))
            using (await DatabaseLifecycle.EnterResourceAsync(engine, "database")) { }
            Assert.False(DatabaseLifecycle.HasSubscriptionGate(subscription));
            Assert.False(DatabaseLifecycle.HasEngineGate(engine));
            Assert.False(DatabaseLifecycle.HasResourceGate(engine, "database"));
        }
    }

    [Fact]
    public async Task ConcurrentReleasesAndRentalsPreserveExclusionAndCleanUp()
    {
        var engine = Unique();
        using var cancellation = new CancellationTokenSource(Timeout);
        var run = new ContentionRun(engine, cancellation.Token);
        // xUnit can throttle continuations on its synchronization context while unrelated
        // test collections are running. The 100 competing workers belong on the thread pool.
        var workers = Task.Run(run.Start);
        try
        {
            await workers.WaitAsync(Timeout);
            Assert.Equal(100, run.Completed);
            Assert.Equal(0, run.Collisions);
        }
        finally
        {
            cancellation.Cancel();
            await DrainAsync(workers, cancellation.Token);
        }
        Assert.False(DatabaseLifecycle.HasEngineGate(engine));
        Assert.False(DatabaseLifecycle.HasResourceGate(engine, "database"));
    }

    [Fact]
    public async Task ContentionWorkersAreIndependentOfCallerContinuationScheduling()
    {
        var capturedEngine = Unique();
        var independentEngine = Unique();
        using var cancellation = new CancellationTokenSource(Timeout);
        var captured = new ContentionRun(capturedEngine, cancellation.Token);
        var independent = new ContentionRun(independentEngine, cancellation.Token);
        var context = new HeldContinuationsContext();
        var previous = SynchronizationContext.Current;
        Task capturedWorkers;
        Task independentWorkers;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // Exact old scheduling: the first lease holder posts its Task.Yield continuation
            // to the caller's context, so a paused context also pauses every following renter.
            capturedWorkers = captured.Start();
            independentWorkers = Task.Run(independent.Start);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        try
        {
            await independentWorkers.WaitAsync(Timeout);
            Assert.Equal(100, independent.Completed);
            Assert.Equal(0, independent.Collisions);
            Assert.False(capturedWorkers.IsCompleted);
            Assert.Equal(0, captured.Completed);
            Assert.True(context.Posts > 0);

            // The same gate/worker code completes as soon as scheduling is released. This
            // distinguishes a withheld continuation from a mutual-exclusion deadlock.
            context.Release();
            await capturedWorkers.WaitAsync(Timeout);
            Assert.Equal(100, captured.Completed);
            Assert.Equal(0, captured.Collisions);
        }
        finally
        {
            context.Release();
            cancellation.Cancel();
            await DrainAsync(Task.WhenAll(capturedWorkers, independentWorkers), cancellation.Token);
        }
        foreach (var engine in new[] { capturedEngine, independentEngine })
        {
            Assert.False(DatabaseLifecycle.HasEngineGate(engine));
            Assert.False(DatabaseLifecycle.HasResourceGate(engine, "database"));
        }
    }

    private static async Task DrainAsync(Task workers, CancellationToken cancellationToken)
    {
        try { await workers.WaitAsync(Timeout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed class ContentionRun(string engine, CancellationToken cancellationToken)
    {
        private int active;
        private int collisions;
        private int completed;
        internal int Collisions => Volatile.Read(ref collisions);
        internal int Completed => Volatile.Read(ref completed);

        internal Task Start() => Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Worker()));

        private async Task Worker()
        {
            using var lease = await DatabaseLifecycle.EnterResourceAsync(engine, "database", cancellationToken);
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref collisions);
            try { await Task.Yield(); }
            finally
            {
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref completed);
            }
        }
    }

    private sealed class HeldContinuationsContext : SynchronizationContext
    {
        private readonly object sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = new();
        private bool released;
        private int posts;
        internal int Posts => Volatile.Read(ref posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posts);
            lock (sync)
            {
                if (!released)
                {
                    pending.Enqueue((callback, state));
                    return;
                }
            }
            Dispatch(callback, state);
        }

        internal void Release()
        {
            (SendOrPostCallback Callback, object? State)[] callbacks;
            lock (sync)
            {
                released = true;
                callbacks = pending.ToArray();
                pending.Clear();
            }
            foreach (var (callback, state) in callbacks) Dispatch(callback, state);
        }

        private void Dispatch(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(this);
            try { callback(state); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
    }
}
