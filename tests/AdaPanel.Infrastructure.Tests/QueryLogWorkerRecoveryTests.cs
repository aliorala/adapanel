using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class QueryLogWorkerRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Engine_failure_does_not_skip_other_engines_or_cleanup(bool writing, bool timeout)
    {
        using var f = new Fixture();
        Exception Failure() => timeout ? new OperationCanceledException("dependency timeout") : new IOException("fixture");
        if (writing) f.Log.OnRecord = _ => throw Failure();
        else f.Attribution.OnRead = _ => throw Failure();
        var before = DateTime.UtcNow;
        var completed = await f.Worker.RunCycleAsync(before.AddHours(-2), default);
        Assert.Equal(new[] { "mariadb", "mysql", "postgresql" }, f.Attribution.Read);
        Assert.Equal(writing ? new[] { "mariadb", "mysql", "postgresql" } : new[] { "mysql", "postgresql" }, f.Log.Recorded);
        Assert.Equal(1, f.Log.Purges);
        Assert.InRange(completed, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_stops_next_engine_and_cleanup(bool dependencyThrows)
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        f.Attribution.OnRead = token =>
        {
            cancel.Cancel();
            if (dependencyThrows) token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Worker.RunCycleAsync(DateTime.UtcNow.AddHours(-2), cancel.Token));
        Assert.Equal(new[] { "mariadb" }, f.Attribution.Read);
        Assert.Empty(f.Log.Recorded);
        Assert.Equal(0, f.Log.Purges);
    }

    [Fact]
    public async Task Failed_cleanup_can_be_retried_on_next_cycle()
    {
        using var f = new Fixture();
        var last = DateTime.UtcNow.AddHours(-2);
        f.Log.FailPurge = true;
        await Assert.ThrowsAsync<IOException>(() => f.Worker.RunCycleAsync(last, default));
        f.Log.FailPurge = false;
        var completed = await f.Worker.RunCycleAsync(last, default);
        Assert.Equal(2, f.Log.Purges);
        Assert.True(completed > last);
        Assert.Equal(completed, await f.Worker.RunCycleAsync(completed, default));
        Assert.Equal(2, f.Log.Purges);
    }

    [Fact]
    public async Task Uninstalled_engine_is_skipped_and_other_engines_are_recorded()
    {
        using var f = new Fixture();
        f.Attribution.Installed = false;
        await f.Worker.RunCycleAsync(DateTime.UtcNow, default);
        Assert.Equal(new[] { "mysql", "postgresql" }, f.Log.Recorded);
        Assert.Equal(0, f.Log.Purges);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Attribution Attribution = new();
        public readonly QueryLog Log = new();
        private readonly ServiceProvider provider;
        public readonly QueryLogWorker Worker;
        public Fixture()
        {
            provider = new ServiceCollection()
                .AddSingleton<IResourceAttributionService>(Attribution)
                .AddSingleton<IQueryLogService>(Log).BuildServiceProvider();
            Worker = new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<QueryLogWorker>.Instance);
        }
        public void Dispose() { Worker.Dispose(); provider.Dispose(); }
    }

    private sealed class Attribution : IResourceAttributionService
    {
        public readonly List<string> Read = [];
        public Action<CancellationToken>? OnRead;
        public bool Installed = true;
        public Task<ResourceConsumerSnapshotDto> GetConsumersAsync(string source, int limit = 50,
            CancellationToken cancellationToken = default)
        {
            Read.Add(source);
            if (source == "mariadb") OnRead?.Invoke(cancellationToken);
            return Task.FromResult(new ResourceConsumerSnapshotDto
            {
                Source = source, Installed = source != "mariadb" || Installed, Available = true
            });
        }
    }

    private sealed class QueryLog : IQueryLogService
    {
        public readonly List<string> Recorded = [];
        public Action<CancellationToken>? OnRecord;
        public int Purges;
        public bool FailPurge;
        public Task<int> RecordSampleAsync(ResourceConsumerSnapshotDto snapshot, CancellationToken cancellationToken = default)
        {
            Recorded.Add(snapshot.Source);
            if (snapshot.Source == "mariadb") OnRecord?.Invoke(cancellationToken);
            return Task.FromResult(0);
        }
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            Purges++;
            if (FailPurge) throw new IOException("fixture cleanup failed");
            return Task.FromResult(0);
        }
        public Task<QueryLogPageDto> QueryAsync(QueryLogFilterDto filter, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
