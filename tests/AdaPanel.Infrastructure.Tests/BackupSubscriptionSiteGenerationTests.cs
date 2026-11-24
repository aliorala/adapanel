using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupSubscriptionSiteGenerationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task All_sites_share_one_capture_and_release_it_before_database_dump()
    {
        var runtime = new SnapshotRuntime();
        using var fixture = new Fixture(runtime);
        fixture.BeforeDump = _ => { Assert.True(runtime.Disposed); return Task.CompletedTask; };
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request());
        Assert.Equal("created", record.Status);
        Assert.Equal(1, runtime.Captures);
        Assert.Equal(2, runtime.RootCount);
        Assert.Equal(2, runtime.Resolved);
    }

    private sealed class SnapshotRuntime : IBackupSnapshotRuntime, IBackupSnapshotLease
    {
        public int Captures, RootCount, Resolved;
        public bool Disposed;
        public DateTime CapturedAtUtc => DateTime.UtcNow;
        public Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken)
        { Captures++; RootCount = roots.Count; return Task.FromResult<IBackupSnapshotLease>(this); }
        public string ResolveRoot(string root) { Assert.False(Disposed); Resolved++; return root; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData("create", 0, true)]
    [InlineData("delete", 0, true)]
    [InlineData("cascade", 0, true)]
    [InlineData("create", 1, true)]
    [InlineData("delete", 1, true)]
    [InlineData("cascade", 1, true)]
    [InlineData("create", 0, false)]
    [InlineData("delete", 0, false)]
    [InlineData("cascade", 0, false)]
    [InlineData("create", 1, false)]
    [InlineData("delete", 1, false)]
    [InlineData("cascade", 1, false)]
    public async Task EverySiteRemainsReservedUntilTheSubscriptionArchiveIsPublished(string mutation, int index, bool files)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var entered = Signal();
        var release = Signal();
        f.BeforeDump = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var backup = f.Service.BackupSubscriptionAsync(f.Request(files), cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Exception? error;
        try
        {
            error = await Capture(() => f.Mutate(f.Names[index], mutation)).WaitAsync(Deadline);
            // The archive's long database work must not hold the global IIS gate.
            await f.Mutate(f.Names[0] + ".other", "create").WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        var record = await backup.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal("created", record.Status);
        using var archive = ZipFile.OpenRead(record.Path);
        foreach (var name in f.Names)
        {
            Assert.NotNull(archive.GetEntry($"sites/{name}/config/site.json"));
            if (files)
            {
                using var reader = new StreamReader(Assert.IsType<ZipArchiveEntry>(archive.GetEntry($"sites/{name}/files/index.txt")).Open());
                Assert.Equal("original " + name, await reader.ReadToEndAsync());
            }
            else Assert.Null(archive.GetEntry($"sites/{name}/files/index.txt"));
            await f.Mutate(name, mutation).WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleting")]
    [InlineData("provisioning")]
    public async Task AnUnavailableSiteIsReportedAndExcludedWhileOtherSitesAreBackedUp(string state)
    {
        using var f = new Fixture();
        var name = f.Names[1];
        IDisposable? pending = null;
        if (state == "missing") f.ReadFailure = (site, _) => site == name ? new AppException("sites.not_found", 404, 404) : null;
        if (state == "deleting") pending = await SiteMutationGuard.BeginDeletionAsync(f.Gates, name, default);
        if (state == "provisioning") pending = await SiteMutationGuard.WatchCreationAsync(f.Gates, name, default, provisioning: true);
        try
        {
            var record = await f.Service.BackupSubscriptionAsync(f.Request()).WaitAsync(Deadline);
            Assert.Equal("created_with_errors", record.Status);
            var warning = Assert.Single(record.Warnings);
            Assert.Equal("backup.site_unavailable", warning.MessageKey);
            Assert.Equal(name, warning.Target);
            Assert.DoesNotContain(record.IncludedComponents!, item => item.Source == name);
            using var archive = ZipFile.OpenRead(record.Path);
            Assert.DoesNotContain(archive.Entries, item => item.FullName.StartsWith($"sites/{name}/", StringComparison.Ordinal));
            Assert.NotNull(archive.GetEntry($"sites/{f.Names[0]}/files/index.txt"));
            await f.Mutate(f.Names[0], "create").WaitAsync(Deadline);
        }
        finally { pending?.Dispose(); }
        await f.Mutate(name, "create").WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SiteReadCancellationCannotBePublishedAsAnIncompleteSuccessfulArchive(int index)
    {
        using var f = new Fixture();
        // A provider can signal cancellation before the caller's token is canceled.
        f.ReadFailure = (name, _) => name == f.Names[index] ? new OperationCanceledException("canceled site read") : null;
        var error = await Capture(() => f.Service.BackupSubscriptionAsync(f.Request())).WaitAsync(Deadline);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, f.Dumps);
        Assert.Empty(await f.Service.GetBackupsAsync());
        Assert.Empty(Directory.GetFiles(f.ArchiveRoot, "*.zip", SearchOption.AllDirectories));
        foreach (var name in f.Names) await f.Mutate(name, "create").WaitAsync(Deadline);
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(f.IndexPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledDatabaseWorkReleasesEverySiteReservation(bool canceled)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        f.BeforeDump = _ => throw (canceled
            ? (Exception)new OperationCanceledException("canceled database dump")
            : new IOException("unreadable database dump"));
        if (canceled)
        {
            var error = await Capture(() => f.Service.BackupSubscriptionAsync(f.Request(), cancellation.Token)).WaitAsync(Deadline);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Empty(await f.Service.GetBackupsAsync());
        }
        else
        {
            var record = await f.Service.BackupSubscriptionAsync(f.Request(), cancellation.Token).WaitAsync(Deadline);
            Assert.Equal("created_with_errors", record.Status);
            Assert.Equal("backup.database_dump_failed", Assert.Single(record.Warnings).MessageKey);
        }
        foreach (var name in f.Names) await f.Mutate(name, "create").WaitAsync(Deadline);
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(f.IndexPath));
        f.BeforeDump = null;
        Assert.Equal("created", (await f.Service.BackupSubscriptionAsync(f.Request()).WaitAsync(Deadline)).Status);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Capture(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        internal readonly IConcurrencyLock Gates = new ConcurrencyLock();
        internal readonly Guid UserId = Guid.NewGuid();
        internal readonly string[] Names = ["a-" + Guid.NewGuid().ToString("N") + ".example", "b-" + Guid.NewGuid().ToString("N") + ".example"];
        internal readonly BackupService Service;
        internal Func<string, CancellationToken, Exception?>? ReadFailure;
        internal Func<CancellationToken, Task>? BeforeDump;
        internal int Dumps;
        internal string IndexPath => Area.DosyaDeposu.ResolvePath("config/backups.json");
        internal string ArchiveRoot => Area.DosyaDeposu.EnsureDirectory("backups");
        internal Fixture(IBackupSnapshotRuntime? snapshotRuntime = null)
        {
            foreach (var name in Names)
            {
                Directory.CreateDirectory(Path.Combine(Area.SiteKoku, name));
                File.WriteAllText(Path.Combine(Area.SiteKoku, name, "index.txt"), "original " + name);
            }
            var sites = Proxy<IIisSiteService>((method, args) => method == "GetSiteAsync"
                ? ReadSite((string)args![0]!, (CancellationToken)args[1]!) : throw new NotSupportedException(method));
            var databases = Proxy<IDatabaseService>((method, args) => method == "ExportDumpAsync"
                ? Dump((DatabaseDumpRequest)args![0]!, (CancellationToken)args[1]!) : throw new NotSupportedException(method));
            var scopes = Proxy<ICustomerScopeService>((method, _) => method == "GetScopeAsync"
                ? Task.FromResult(new CustomerScope(Names, 10, 1024, 5, 5, 5)) : throw new NotSupportedException(method));
            var ownership = Proxy<IDatabaseOwnershipService>((method, _) => method == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>>(
                    [("mysql", "owned_database", UserId, Names[0])]) : throw new NotSupportedException(method));
            var preferences = Proxy<IPanelPreferencesService>((method, _) => method == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = ArchiveRoot }) : throw new NotSupportedException(method));
            Service = new BackupService(sites, databases, null!, preferences, null!, Area.DosyaDeposu,
                new SahteIstemciFabrikasi(new SahteBulut()), customerScopes: scopes, databaseOwnership: ownership,
                secretProtector: new SahteKoruyucu(), concurrencyLock: Gates, snapshotRuntime: snapshotRuntime);
        }
        internal BackupSubscriptionRequest Request(bool files = true) => new()
        { UserId = UserId, IncludeFiles = files, IncludeDatabases = true, IncludeMail = false, IncludeFtp = false };
        private Task<SiteDto> ReadSite(string name, CancellationToken token)
        {
            if (ReadFailure?.Invoke(name, token) is { } error) throw error;
            return Task.FromResult(new SiteDto
            {
                Id = Array.IndexOf(Names, name) + 7, Name = name, PhysicalPath = Path.Combine(Area.SiteKoku, name),
                ApplicationRootPath = Path.Combine(Area.SiteKoku, name), ApplicationPoolName = "OriginalPool", State = "Started"
            });
        }
        private async Task<DatabaseDumpResultDto> Dump(DatabaseDumpRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Dumps);
            if (BeforeDump is not null) await BeforeDump(token);
            await File.WriteAllTextAsync(request.FilePath, "-- original database content", token);
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath };
        }
        internal async Task Mutate(string name, string mutation)
        {
            if (mutation == "cascade")
            {
                using var deleting = await SiteMutationGuard.BeginDeletionAsync(Gates, name, default);
            }
            else await SiteMutationGuard.RunMutationAsync(Gates, name, mutation == "create", () => Task.FromResult(true), default);
        }
        public void Dispose() => Area.Dispose();
    }

    private static T Proxy<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)result).Handler = handler;
        return result;
    }
    public class ServiceProxy : DispatchProxy
    {
        internal Func<string, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
