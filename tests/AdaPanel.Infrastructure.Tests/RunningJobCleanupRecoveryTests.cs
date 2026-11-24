using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Infrastructure.RunningJobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class RunningJobCleanupRecoveryTests
{
    private const string StatePath = "config/running-jobs.json";
    private static RunningJobService Service(BulutAlani area) => new(area.DosyaDeposu, NullLogger<RunningJobService>.Instance);
    private static FileStream BlockPublication(BulutAlani area) => new(area.DosyaDeposu.ResolvePath(StatePath),
        FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task StatusRecoversCompletedJobAfterTransientFileLockWithoutRestart()
    {
        using var area = new BulutAlani();
        var job = await Service(area).BeginAsync(RunningJobKinds.Backup, "finished", null, area.Iptal);
        using (BlockPublication(area)) await job.DisposeAsync();
        Assert.Single((await area.DosyaDeposu.ReadAsync(StatePath, new RunningJobsFile())).Running);

        var status = await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal);
        Assert.Empty(status.Running);
        Assert.Empty(status.Interrupted);
        Assert.Empty((await area.DosyaDeposu.ReadAsync(StatePath, new RunningJobsFile())).Running);
    }

    [Fact]
    public async Task NewJobCanRecoverCleanupWithoutWaitingForStatusPoll()
    {
        using var area = new BulutAlani();
        var job = await Service(area).BeginAsync(RunningJobKinds.Restore, "old restore", null, area.Iptal);
        using (BlockPublication(area)) await job.DisposeAsync();
        await using var next = await Service(area).BeginAsync(RunningJobKinds.Restore, "new restore", null, area.Iptal);
        Assert.Equal("new restore", Assert.Single((await Service(area).GetStatusAsync([RunningJobKinds.Restore], null, area.Iptal)).Running).Title);
    }

    [Fact]
    public async Task RetryPreservesOtherTenantsActiveJobAndCannotEraseReplacement()
    {
        using var area = new BulutAlani();
        var finishedOwner = Guid.NewGuid();
        var activeOwner = Guid.NewGuid();
        var finished = await Service(area).BeginAsync(RunningJobKinds.Backup, "finished", finishedOwner, area.Iptal);
        await using var active = await Service(area).BeginAsync(RunningJobKinds.Backup, "active tenant", activeOwner, area.Iptal);
        using (BlockPublication(area)) await finished.DisposeAsync();
        await using var replacement = await Service(area).BeginAsync(RunningJobKinds.Backup, "replacement", finishedOwner, area.Iptal);
        await finished.DisposeAsync();
        var status = await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal);
        Assert.Equal(new[] { "active tenant", "replacement" }, status.Running.Select(job => job.Title).Order().ToArray());
        var conflict = await Assert.ThrowsAsync<AppException>(() => Service(area).BeginAsync(RunningJobKinds.Backup, "duplicate", activeOwner, area.Iptal));
        Assert.Equal(409, conflict.HttpStatusCode);
    }

    [Fact]
    public async Task FailedRecoveryDoesNotForgetCleanupRequest()
    {
        using var area = new BulutAlani();
        var job = await Service(area).BeginAsync(RunningJobKinds.Migration, "finished migration", null, area.Iptal);
        using (BlockPublication(area))
        {
            await job.DisposeAsync();
            var statusError = await Record.ExceptionAsync(() => Service(area).GetStatusAsync([RunningJobKinds.Migration], null, area.Iptal));
            var startError = await Record.ExceptionAsync(() => Service(area).BeginAsync(RunningJobKinds.Migration, "must not start", null, area.Iptal));
            Assert.True(statusError is IOException or UnauthorizedAccessException);
            Assert.True(startError is IOException or UnauthorizedAccessException);
        }
        Assert.Empty((await Service(area).GetStatusAsync([RunningJobKinds.Migration], null, area.Iptal)).Running);
    }

    [Fact]
    public async Task SameTimestampDoesNotAuthorizeDeletingAnotherJobIdentity()
    {
        using var area = new BulutAlani();
        var job = await Service(area).BeginAsync(RunningJobKinds.Backup, "first", null, area.Iptal);
        var file = await area.DosyaDeposu.ReadAsync(StatePath, new RunningJobsFile());
        var first = file.Running[RunningJobKinds.Backup];
        Assert.True(Guid.TryParseExact(first.JobId, "N", out _));
        var replacement = new RunningJobState
        {
            JobId = Guid.NewGuid().ToString("N"), Kind = first.Kind, Title = "replacement with identical timestamp",
            StartedAtUtc = first.StartedAtUtc, OwnerProcessId = first.OwnerProcessId,
            OwnerProcessStartedAtTicks = first.OwnerProcessStartedAtTicks
        };
        file.Running[RunningJobKinds.Backup] = replacement;
        await area.DosyaDeposu.WriteAsync(StatePath, file);
        using (BlockPublication(area)) await job.DisposeAsync();
        Assert.Equal(replacement.Title, Assert.Single((await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal)).Running).Title);
    }

    [Fact]
    public async Task CompletionEvidenceIsIsolatedByDataRoot()
    {
        using var original = new BulutAlani();
        using var other = new BulutAlani();
        var job = await Service(original).BeginAsync(RunningJobKinds.Backup, "original", null, original.Iptal);
        var file = await original.DosyaDeposu.ReadAsync(StatePath, new RunningJobsFile());
        await other.DosyaDeposu.WriteAsync(StatePath, file);
        using (BlockPublication(original)) await job.DisposeAsync();
        Assert.Single((await Service(other).GetStatusAsync([RunningJobKinds.Backup], null, other.Iptal)).Running);
        Assert.Empty((await Service(original).GetStatusAsync([RunningJobKinds.Backup], null, original.Iptal)).Running);
        Assert.Single((await Service(other).GetStatusAsync([RunningJobKinds.Backup], null, other.Iptal)).Running);
    }

    [Fact]
    public async Task RepeatedDisposalDoesNotAttemptAnotherWriteOrAffectActiveJob()
    {
        using var area = new BulutAlani();
        var old = await Service(area).BeginAsync(RunningJobKinds.Backup, "old", null, area.Iptal);
        await old.DisposeAsync();
        await using var active = await Service(area).BeginAsync(RunningJobKinds.Backup, "active", null, area.Iptal);
        using (BlockPublication(area))
        {
            await old.DisposeAsync();
            Assert.Equal("active", Assert.Single((await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal)).Running).Title);
        }
    }

    [Fact]
    public async Task CancelledRecoveryRetainsEvidenceForNextRequest()
    {
        using var area = new BulutAlani();
        var old = await Service(area).BeginAsync(RunningJobKinds.Backup, "old", null, area.Iptal);
        using (BlockPublication(area)) await old.DisposeAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(area).GetStatusAsync([RunningJobKinds.Backup], null, cancelled.Token));
        Assert.Empty((await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal)).Running);
    }

    [Fact]
    public async Task ConcurrentRetriesStillAdmitOnlyOneReplacementJob()
    {
        using var area = new BulutAlani();
        var old = await Service(area).BeginAsync(RunningJobKinds.Backup, "old", null, area.Iptal);
        using (BlockPublication(area)) await old.DisposeAsync();
        async Task<(IAsyncDisposable? Scope, AppException? Error)> Start(string title)
        {
            try { return (await Service(area).BeginAsync(RunningJobKinds.Backup, title, null, area.Iptal), null); }
            catch (AppException error) { return (null, error); }
        }
        var attempts = await Task.WhenAll(Start("first"), Start("second"));
        await using var winner = Assert.Single(attempts, item => item.Scope is not null).Scope!;
        Assert.Equal(409, Assert.Single(attempts, item => item.Error is not null).Error!.HttpStatusCode);
        var status = await Service(area).GetStatusAsync([RunningJobKinds.Backup], null, area.Iptal);
        Assert.Single(status.Running);
        Assert.Empty(status.Interrupted);
    }
}
