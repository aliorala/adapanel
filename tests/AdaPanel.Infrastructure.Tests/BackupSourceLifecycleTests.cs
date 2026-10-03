using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupSourceLifecycleTests
{
    [Theory]
    [InlineData("postgres:Foo", "postgresql:Foo")]
    [InlineData("mysql:Orders", "mysql:orders")]
    public async Task AliasesShareOneGateAndCanceledWaitersAreRemoved(string first, string second)
    {
        using var area = new BulutAlani();
        var path = area.DosyaDeposu.ResolvePath("config/backups.json");
        var lease = await BackupSourceLifecycle.EnterAsync(path, "database", first, area.Iptal);
        using var cancellation = new CancellationTokenSource();
        var waiter = BackupSourceLifecycle.EnterAsync(path, "database", second, cancellation.Token);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(2, BackupSourceLifecycle.ReferencesForSource(path, "database", first));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(path));
    }

    [Fact]
    public async Task DifferentQuotedDatabaseNamesAndDifferentStoresDoNotBlockEachOther()
    {
        using var first = new BulutAlani();
        using var second = new BulutAlani();
        var path = first.DosyaDeposu.ResolvePath("config/backups.json");
        using var upper = await BackupSourceLifecycle.EnterAsync(path, "database", "postgresql:Foo", first.Iptal);
        using var lower = await BackupSourceLifecycle.EnterAsync(path, "database", "postgresql:foo", first.Iptal);
        using var otherStore = await BackupSourceLifecycle.EnterAsync(second.DosyaDeposu.ResolvePath("config/backups.json"), "database", "postgresql:Foo", first.Iptal);
    }

    [Fact]
    public async Task ConcurrentRentAndReleaseDoesNotLoseExclusionOrRetainEntries()
    {
        using var area = new BulutAlani();
        var path = area.DosyaDeposu.ResolvePath("config/backups.json");
        var active = 0;
        var overlapping = 0;
        var workers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                using var lease = await BackupSourceLifecycle.EnterAsync(path, "site", "same.example", area.Iptal);
                if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlapping);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
        }, area.Iptal)).ToArray();
        await Task.WhenAll(workers).WaitAsync(area.Iptal);
        Assert.Equal(0, overlapping);
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(path));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("chain")]
    [InlineData("prune")]
    public async Task CancellationAfterLocalDeletionCannotPreventIndexPublication(string operation)
    {
        using var area = new BulutAlani();
        using var request = new CancellationTokenSource();
        var original = area.ServisKur(new SahteBulut());
        var first = await original.BackupSiteAsync(Request(false), area.Iptal);
        if (operation == "prune")
        {
            first.CreatedAtUtc = DateTime.UtcNow.AddDays(-30);
            await area.KayitlariYazAsync(first);
            var replacement = Request(false);
            replacement.Incremental = false;
            await original.BackupSiteAsync(replacement, area.Iptal);
        }
        var preferences = new CancelAfterDeletePreferences(area, request, first.Path);
        var service = new BackupService(new SahteSiteServisi(area.SiteKoku), null!, null!, preferences, null!,
            area.DosyaDeposu, new SahteIstemciFabrikasi(new SahteBulut()), secretProtector: new SahteKoruyucu());
        switch (operation)
        {
            case "single": await service.DeleteBackupAsync(first.Id, request.Token); break;
            case "chain": Assert.Equal([first.Id], (await service.DeleteBackupChainAsync(first.Id, [first.Id], request.Token)).DeletedIds); break;
            default: Assert.Equal(1, (await service.PruneAsync(14, cancellationToken: request.Token)).Removed); break;
        }
        Assert.True(request.IsCancellationRequested);
        Assert.Equal(1, preferences.Calls);
        Assert.DoesNotContain(await service.GetBackupsAsync(area.Iptal), item => item.Id == first.Id);
        Assert.False(File.Exists(first.Path));
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(area.DosyaDeposu.ResolvePath("config/backups.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIncrementalPublicationMakesQueuedDeleteOrPruneReevaluateItsSnapshot(bool prune)
    {
        using var area = new BulutAlani();
        using var cloud = new PausedUpload();
        var service = CreateService(area, cloud);
        var first = await service.BackupSiteAsync(Request(false), area.Iptal);
        if (prune)
        {
            first.CreatedAtUtc = DateTime.UtcNow.AddDays(-30);
            await area.KayitlariYazAsync(first);
            // Without the producer lease, retention could delete the old base while
            // its in-flight child is being uploaded: this standalone point replaces it.
            var replacementRequest = Request(false);
            replacementRequest.Incremental = false;
            await service.BackupSiteAsync(replacementRequest, area.Iptal);
        }
        var producer = service.BackupSiteAsync(Request(true), area.Iptal);
        Task? mutation = null;
        try
        {
            await cloud.Entered.Task.WaitAsync(area.Iptal);
            mutation = prune
                ? service.PruneAsync(14, cancellationToken: area.Iptal)
                : service.DeleteBackupChainAsync(first.Id, [first.Id], area.Iptal);
            var path = area.DosyaDeposu.ResolvePath("config/backups.json");
            while (!mutation.IsCompleted && BackupSourceLifecycle.ReferencesForSource(path, "site", BulutAlani.Site) < 2)
                await Task.Delay(1, area.Iptal);
            Assert.False(mutation.IsCompleted);
            cloud.Release.TrySetResult();
            var child = await producer;
            Assert.Equal(first.Id, child.BaseBackupId);
            if (prune) Assert.Equal(0, (await (Task<BackupPruneResultDto>)mutation).Removed);
            else Assert.Equal("backup.chain_changed", (await Assert.ThrowsAsync<AppException>(() => mutation)).MessageKey);
            Assert.True(File.Exists(first.Path));
            Assert.True(File.Exists(child.Path));
            Assert.False((await service.GetChainAsync(child.Id, area.Iptal)).Broken);
            Assert.Equal(0, cloud.Deletes);
            Assert.Equal(0, BackupSourceLifecycle.CountForStore(path));
        }
        finally
        {
            cloud.Release.TrySetResult();
            try { await producer; } catch { }
            if (mutation is not null) try { await mutation; } catch { }
        }
    }

    [Fact]
    public async Task CancelingAQueuedDeletionDoesNotReleaseTheActiveProducerLease()
    {
        using var area = new BulutAlani();
        using var cloud = new PausedUpload();
        var service = CreateService(area, cloud);
        var first = await service.BackupSiteAsync(Request(false), area.Iptal);
        var producer = service.BackupSiteAsync(Request(true), area.Iptal);
        try
        {
            await cloud.Entered.Task.WaitAsync(area.Iptal);
            using var canceled = new CancellationTokenSource();
            var deletion = service.DeleteBackupChainAsync(first.Id, [first.Id], canceled.Token);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
            var path = area.DosyaDeposu.ResolvePath("config/backups.json");
            Assert.Equal(1, BackupSourceLifecycle.ReferencesForSource(path, "site", BulutAlani.Site));
            cloud.Release.TrySetResult();
            var child = await producer;
            Assert.False((await service.GetChainAsync(child.Id, area.Iptal)).Broken);
            Assert.Equal(0, BackupSourceLifecycle.CountForStore(path));
        }
        finally { cloud.Release.TrySetResult(); try { await producer; } catch { } }
    }

    private static BackupService CreateService(BulutAlani area, PausedUpload cloud) => new(
        new SahteSiteServisi(area.SiteKoku), null!, null!, new SahteTercihServisi(Path.Combine(area.Kok, "backups")), null!,
        area.DosyaDeposu, cloud, secretProtector: new SahteKoruyucu(), outboundResolver: SahteAgCozumleyici.Genel);

    private static BackupSiteRequest Request(bool remote) => new()
    {
        SiteName = BulutAlani.Site, Incremental = true, FullBackupEveryDays = 365,
        IncludeFiles = true, IncludeConfig = false, IncludeMail = false,
        TargetType = remote ? "webdav" : "local", RemoteUrl = remote ? "https://backup.example.test/archives" : "",
        RemoteUserName = "fixture", RemotePassword = "fixture-password"
    };

    private sealed class PausedUpload : HttpMessageHandler, IHttpClientFactory
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Deletes;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (request.Method == HttpMethod.Delete) Interlocked.Increment(ref Deletes);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
        }
    }

    private sealed class CancelAfterDeletePreferences(BulutAlani area, CancellationTokenSource request, string archivePath)
        : IPanelPreferencesService
    {
        internal int Calls;
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(File.Exists(archivePath));
            Calls++;
            request.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(area.Kok, "backups") });
        }
        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
