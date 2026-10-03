using System.Collections;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MemorySite = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.MemorySite;
using SiteCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.SiteCollection;
using PoolCollection = AdaPanel.Infrastructure.Tests.SiteNativeLifecycleRecoveryTests.PoolCollection;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteNativeEntryLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("success", false)]
    [InlineData("failure", false)]
    [InlineData("cancellation", false)]
    [InlineData("success", true)]
    [InlineData("failure", true)]
    [InlineData("cancellation", true)]
    public async Task FirstCaptureDuringNativeCommitCannotAdoptTheReusedIdAndPath(string outcome, bool existingWatcher)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var commit = new Barrier();
        using var watcher = existingWatcher
            ? await SiteMutationGuard.WatchCreationAsync(f.Gates, f.Name, default) : null;
        f.BeforeCommit = manager =>
        {
            f.BeforeCommit = null;
            commit.Block();
            manager.Sites.Single(site => site.Name == f.Name).Id = f.OriginalId;
            if (outcome == "failure") throw new IOException("native commit rejected after configuration was staged");
            if (outcome == "cancellation") cancellation.Cancel();
        };
        var mutation = Capture(() => Task.Run(() => f.Replace(watcher, cancellation.Token)));
        await commit.Entered.Task.WaitAsync(Deadline);
        var capture = Capture(() => f.ReadAndUse(f.Name));
        Exception? mutationError;
        Exception? captureError;
        try { commit.Release(); mutationError = await mutation.WaitAsync(Deadline); captureError = await capture.WaitAsync(Deadline); }
        finally { commit.Release(); }

        AssertOutcome(outcome, mutationError);
        Assert.Equal(f.OriginalId, f.Current.Id);
        Assert.Equal(f.SitePath, f.Current.PhysicalPath);
        Assert.Equal(outcome == "failure" ? 1 : 2, f.CommittedGenerations);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(captureError).MessageKey);

        // The original creation watcher still owns precisely this native invocation.
        if (watcher is not null && outcome != "failure")
        {
            using var created = await watcher.CaptureCreatedAsync(f.Native, watcher.CommittedSite!, default);
            using var use = await created.ReserveAsync(default);
        }
        watcher?.Dispose();
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task AtomicRootCommitAlsoRejectsCapturesUntilItsNewIdentityIsPinned(string outcome)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var commit = new Barrier();
        f.BeforeCommit = _ =>
        {
            f.BeforeCommit = null;
            commit.Block();
            if (outcome == "failure") throw new IOException("root commit failed");
            if (outcome == "cancellation") cancellation.Cancel();
        };
        var mutation = Capture(() => Task.Run(async () =>
        {
            using var target = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, cancellation.Token);
            using var use = await f.Native.UpdatePhysicalPathAndReserveAsync(target, f.SitePath,
                (_, _) => Task.CompletedTask, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
        }));
        await commit.Entered.Task.WaitAsync(Deadline);
        var capture = Capture(() => f.ReadAndUse(f.Name));
        Exception? captureError;
        try { commit.Release(); AssertOutcome(outcome, await mutation.WaitAsync(Deadline)); captureError = await capture.WaitAsync(Deadline); }
        finally { commit.Release(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(captureError).MessageKey);
        Assert.False(HasEntry(f.Name));
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    [Fact]
    public async Task DisposingTheOriginalWatcherDuringCommitCannotDropTheNativeEntry()
    {
        using var f = await Fixture.Create();
        using var commit = new Barrier();
        using var watcher = await SiteMutationGuard.WatchCreationAsync(f.Gates, f.Name, default);
        f.BeforeCommit = manager =>
        {
            f.BeforeCommit = null;
            commit.Block();
            manager.Sites.Single(site => site.Name == f.Name).Id = f.OriginalId;
        };
        var mutation = Capture(() => Task.Run(() => f.Replace(null, default)));
        await commit.Entered.Task.WaitAsync(Deadline);
        watcher.Dispose();
        var capture = Capture(() => f.ReadAndUse(f.Name));
        Exception? error;
        try { commit.Release(); Assert.Null(await mutation.WaitAsync(Deadline)); error = await capture.WaitAsync(Deadline); }
        finally { commit.Release(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.False(HasEntry(f.Name));
        await f.ReadAndUse(f.Name);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task ProvisioningIntentRemainsExclusiveAcrossNativeEntryLifetime(string outcome)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var commit = new Barrier();
        using var creation = await SiteMutationGuard.WatchCreationAsync(f.Gates, f.Name, default, provisioning: true);
        f.BeforeCommit = manager =>
        {
            f.BeforeCommit = null;
            commit.Block();
            manager.Sites.Single(site => site.Name == f.Name).Id = f.OriginalId;
            if (outcome == "failure") throw new IOException("commit failed");
            if (outcome == "cancellation") cancellation.Cancel();
        };
        var mutation = Capture(() => Task.Run(() => f.Replace(creation, cancellation.Token)));
        await commit.Entered.Task.WaitAsync(Deadline);
        try
        {
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
                await Capture(() => f.ReadAndUse(f.Name)).WaitAsync(Deadline)).MessageKey);
        }
        finally { commit.Release(); }
        AssertOutcome(outcome, await mutation.WaitAsync(Deadline));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
            await Capture(() => f.ReadAndUse(f.Name))).MessageKey);
        if (outcome != "failure")
        {
            using var created = await creation.CaptureCreatedAsync(f.Native, creation.CommittedSite!, default);
            using var use = await created.ReserveAsync(default);
            await created.RunAsync(_ => Task.CompletedTask, default);
        }
        creation.Dispose();
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task CaptureWhoseFirstSnapshotIsStillReadingDetectsAnInterveningNativeMutation(string outcome)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var read = new Barrier();
        f.AfterSnapshot = _ => { f.AfterSnapshot = null; read.Block(); };
        var capture = Capture(() => Task.Run(() => f.ReadAndUse(f.Name)));
        await read.Entered.Task.WaitAsync(Deadline);
        f.BeforeCommit = manager =>
        {
            f.BeforeCommit = null;
            manager.Sites.Single(site => site.Name == f.Name).Id = f.OriginalId;
            if (outcome == "failure") throw new IOException("commit failed");
            if (outcome == "cancellation") cancellation.Cancel();
        };
        Exception? mutationError;
        try { mutationError = await Capture(() => f.Replace(null, cancellation.Token)).WaitAsync(Deadline); }
        finally { read.Release(); }
        AssertOutcome(outcome, mutationError);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await capture.WaitAsync(Deadline)).MessageKey);
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task CaptureAlreadyQueuedAtTheGateDetectsTheEarlierReplacement(string outcome)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var blocker = f.Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
        { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); });
        await entered.Task.WaitAsync(Deadline);
        f.BeforeCommit = manager =>
        {
            f.BeforeCommit = null;
            manager.Sites.Single(site => site.Name == f.Name).Id = f.OriginalId;
            if (outcome == "failure") throw new IOException("commit failed");
            if (outcome == "cancellation") cancellation.Cancel();
        };
        var mutation = Capture(() => f.Replace(null, cancellation.Token));
        var capture = Capture(() => f.ReadAndUse(f.Name));
        release.TrySetResult();
        await blocker.WaitAsync(Deadline);
        AssertOutcome(outcome, await mutation.WaitAsync(Deadline));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await capture.WaitAsync(Deadline)).MessageKey);
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    [Fact]
    public async Task ACommitForAnotherNameDoesNotInvalidateAnUnchangedSite()
    {
        using var f = await Fixture.Create();
        var other = "other." + f.Name;
        await f.Native.CreateSiteAsync(f.Request(other));
        using var commit = new Barrier();
        f.BeforeCommit = _ => { f.BeforeCommit = null; commit.Block(); };
        var mutation = Capture(() => Task.Run(() => f.Replace(null, default)));
        await commit.Entered.Task.WaitAsync(Deadline);
        var capture = Capture(() => f.ReadAndUse(other));
        try { commit.Release(); Assert.Null(await mutation.WaitAsync(Deadline)); Assert.Null(await capture.WaitAsync(Deadline)); }
        finally { commit.Release(); }
        Assert.False(HasEntry(f.Name));
        Assert.False(HasEntry(other));
    }

    [Theory]
    [InlineData("read-failure")]
    [InlineData("read-cancellation")]
    [InlineData("gate-cancellation")]
    public async Task FailedOrCanceledCaptureReleasesItsEntryAndAllowsANewOperation(string failure)
    {
        using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var read = new Barrier();
        Exception? error;
        if (failure == "gate-cancellation")
        {
            var entered = Signal();
            var release = Signal();
            var blocker = f.Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); });
            await entered.Task.WaitAsync(Deadline);
            var capture = Capture(() => f.ReadAndUse(f.Name, cancellation.Token));
            try { cancellation.Cancel(); error = await capture.WaitAsync(Deadline); }
            finally { release.TrySetResult(); await blocker.WaitAsync(Deadline); }
        }
        else
        {
            f.AfterSnapshot = _ =>
            {
                f.AfterSnapshot = null;
                if (failure == "read-failure") throw new IOException("cannot read manager snapshot");
                read.Block();
            };
            var capture = Capture(() => Task.Run(() => f.ReadAndUse(f.Name, cancellation.Token)));
            if (failure == "read-cancellation")
            {
                await read.Entered.Task.WaitAsync(Deadline);
                cancellation.Cancel();
                read.Release();
            }
            error = await capture.WaitAsync(Deadline);
        }
        if (failure == "read-failure") Assert.IsType<AppException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(HasEntry(f.Name));
        await f.Replace(null, default);
        await f.ReadAndUse(f.Name);
        Assert.False(HasEntry(f.Name));
    }

    private static void AssertOutcome(string outcome, Exception? error)
    {
        if (outcome == "success") Assert.Null(error);
        else if (outcome == "cancellation") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("sites.write_failed", Assert.IsType<AppException>(error).MessageKey);
    }

    private static bool HasEntry(string name)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var sync = typeof(SiteMutationGuard).GetField("Sync", flags)!.GetValue(null)!;
        lock (sync)
            return ((IDictionary)typeof(SiteMutationGuard).GetField("Pending", flags)!.GetValue(null)!)
                .Contains(name.Trim().ToUpperInvariant());
    }
    private static async Task<Exception?> Capture(Func<Task> operation)
    { try { await operation(); return null; } catch (Exception error) { return error; } }

    private sealed class Barrier : IDisposable
    {
        public TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim released = new();
        public void Block()
        {
            Entered.TrySetResult();
            if (!released.Wait(Deadline)) throw new TimeoutException("fixture barrier was not released");
        }
        public void Release() => released.Set();
        public void Dispose() { released.Set(); released.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.NativeEntry." + Guid.NewGuid().ToString("N"));
        public string Name { get; } = "native-entry-" + Guid.NewGuid().ToString("N") + ".test";
        public string SitePath => Path.Combine(root, "site");
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public Dictionary<string, MemorySite> Sites { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public PoolCollection Pools { get; private set; } = [];
        public MemorySite Current => Sites[Name];
        public long OriginalId { get; private set; }
        public int CommittedGenerations;
        public WebAdministrationSiteService Native { get; }
        public Action<MemoryManager>? AfterSnapshot;
        public Action<MemoryManager>? BeforeCommit;
        private long nextId = 80;
        private Fixture()
        {
            Native = new WebAdministrationSiteService(Gates, NullLogger<WebAdministrationSiteService>.Instance,
                () => { var manager = new MemoryManager(this); AfterSnapshot?.Invoke(manager); return manager; },
                token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new CommandResult()); });
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Native.CreateSiteAsync(f.Request(f.Name));
            f.OriginalId = f.Current.Id;
            return f;
        }
        public CreateSiteRequest Request(string name) => new()
        { Name = name, HostName = name, PhysicalPath = SitePath, RuntimeProfile = "static", Port = 8080 };
        public Task<SiteDto> Replace(SiteMutationGuard.Creation? watcher, CancellationToken token) => watcher is null
            ? Native.CreateSiteAsync(Request(Name), token)
            : Native.CreateSiteAsync(Request(Name), watcher.RecordCommitted, token);
        public async Task ReadAndUse(string name, CancellationToken token = default)
        {
            using var target = await SiteMutationGuard.CaptureAsync(Native, Gates, name, token);
            using var use = await target.ReserveAsync(token);
            await target.RunAsync(_ => Task.CompletedTask, token);
        }
        public sealed class MemoryManager : IDisposable
        {
            private readonly Fixture fixture;
            public SiteCollection Sites { get; } = [];
            public PoolCollection ApplicationPools { get; } = [];
            public MemoryManager(Fixture fixture)
            {
                this.fixture = fixture;
                Sites.AddRange(fixture.Sites.Values.Select(site => site.Clone()));
                ApplicationPools.AddRange(fixture.Pools.Select(pool => pool.Clone()));
            }
            public void CommitChanges()
            {
                fixture.BeforeCommit?.Invoke(this);
                foreach (var site in Sites) if (site.Id == 0) site.Id = ++fixture.nextId;
                fixture.Sites = Sites.ToDictionary(site => site.Name, site => site.Clone(), StringComparer.OrdinalIgnoreCase);
                fixture.Pools = new PoolCollection();
                fixture.Pools.AddRange(ApplicationPools.Select(pool => pool.Clone()));
                fixture.CommittedGenerations++;
            }
            public void Dispose() { }
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
