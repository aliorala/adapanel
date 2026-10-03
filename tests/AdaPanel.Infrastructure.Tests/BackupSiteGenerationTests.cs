using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupSiteGenerationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("application")]
    [InlineData("pool")]
    [InlineData("missing")]
    [InlineData("same_identity")]
    public async Task ChangedSiteDuringSourceWaitCannotBeArchivedAsTheOriginal(string change)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await BackupSourceLifecycle.EnterAsync(f.IndexPath, "site", f.Name, cancellation.Token);
        var backup = Capture(() => f.Service.BackupSiteAsync(f.Request(), cancellation.Token));
        try
        {
            await f.WaitForSourceWaiter(backup, cancellation.Token);
            if (change == "same_identity") await f.NativeMutation(true);
            else f.Change(change); // Also cover observable changes made outside the native wrapper.
        }
        finally { lease.Dispose(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await backup.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(0, f.Preferences.Calls);
        Assert.Empty(await f.Service.GetBackupsAsync());
        Assert.Empty(Directory.GetFiles(f.ArchiveRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StateAndBindingsChangesDuringTheWaitDoNotInvalidateTheSameSite()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await BackupSourceLifecycle.EnterAsync(f.IndexPath, "site", f.Name, cancellation.Token);
        var backup = f.Service.BackupSiteAsync(f.Request(), cancellation.Token);
        try
        {
            await f.WaitForSourceWaiter(backup, cancellation.Token);
            f.Change("state");
        }
        finally { lease.Dispose(); }
        var record = await backup.WaitAsync(Deadline);
        Assert.Equal(f.Name, record.Source);
        using var archive = ZipFile.OpenRead(record.Path);
        using var reader = new StreamReader(Assert.Single(archive.Entries, item => item.FullName == "files/index.txt").Open());
        Assert.Equal("original site content", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("cascade")]
    public async Task BackupReservesTheSiteUntilItsArchiveIsPublished(string mutation)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var entered = Signal();
        var release = Signal();
        f.Preferences.BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var backup = f.Service.BackupSiteAsync(f.Request(), cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Exception? error;
        try
        {
            error = await Capture(async () =>
            {
                if (mutation == "cascade")
                {
                    using var deleting = await SiteMutationGuard.BeginDeletionAsync(f.Gates, f.Name, cancellation.Token);
                }
                else await f.NativeMutation(mutation == "create");
            }).WaitAsync(Deadline);
            // Another site must remain usable while this site's archive is being assembled.
            await SiteMutationGuard.RunMutationAsync(f.Gates, f.Name + ".other", true, () => Task.FromResult(true), cancellation.Token);
        }
        finally { release.TrySetResult(); }
        var record = await backup.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.True(File.Exists(record.Path));
        Assert.Single(await f.Service.GetBackupsAsync());
        await f.NativeMutation(true); // Completion releases the reservation.
    }

    [Fact]
    public async Task CancelingASourceWaitReleasesTheCapturedSiteTicket()
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await BackupSourceLifecycle.EnterAsync(f.IndexPath, "site", f.Name, default);
        var backup = Capture(() => f.Service.BackupSiteAsync(f.Request(), cancellation.Token));
        try
        {
            await f.WaitForSourceWaiter(backup, cancellation.Token);
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await backup.WaitAsync(Deadline));
        }
        finally { lease.Dispose(); }
        await f.NativeMutation(true);
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(f.IndexPath));
        Assert.Empty(await f.Service.GetBackupsAsync());
        Assert.Equal(0, f.Preferences.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrCanceledBackupReleasesTheSiteReservation(bool canceled)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        f.Preferences.BeforeRead = token =>
        {
            if (canceled) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("simulated preference read failure");
        };
        var error = await Capture(() => f.Service.BackupSiteAsync(f.Request(), cancellation.Token)).WaitAsync(Deadline);
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        await f.NativeMutation(true);
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(f.IndexPath));
        f.Preferences.BeforeRead = null;
        Assert.True(File.Exists((await f.Service.BackupSiteAsync(f.Request())).Path));
    }

    [Fact]
    public async Task ADeletingSiteCannotStartAnotherArchive()
    {
        using var f = new Fixture();
        using (await SiteMutationGuard.BeginDeletionAsync(f.Gates, f.Name, default))
        {
            var error = await Capture(() => f.Service.BackupSiteAsync(f.Request())).WaitAsync(Deadline);
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
            Assert.Equal(0, f.Preferences.Calls);
            Assert.Empty(await f.Service.GetBackupsAsync());
        }
        Assert.True(File.Exists((await f.Service.BackupSiteAsync(f.Request())).Path));
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
        internal readonly string Name = "snapshot-" + Guid.NewGuid().ToString("N") + ".example";
        internal readonly Preferences Preferences;
        internal readonly BackupService Service;
        internal SiteDto? Site;
        internal string IndexPath => Area.DosyaDeposu.ResolvePath("config/backups.json");
        internal string ArchiveRoot => Area.DosyaDeposu.EnsureDirectory("backups");
        internal Fixture()
        {
            File.WriteAllText(Path.Combine(Area.SiteKoku, "index.txt"), "original site content");
            Change("initial");
            Preferences = new Preferences(ArchiveRoot);
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Read = () => Site ?? throw new AppException("sites.not_found", 404, 404);
            Service = new BackupService(sites, null!, null!, Preferences, null!, Area.DosyaDeposu,
                new SahteIstemciFabrikasi(new SahteBulut()), secretProtector: new SahteKoruyucu());
        }
        internal BackupSiteRequest Request() => new() { SiteName = Name, IncludeFiles = true, IncludeConfig = false };
        internal void Change(string change)
        {
            Site = change == "missing" ? null : new SiteDto
            {
                Id = change == "id" ? 19 : 7,
                Name = Name,
                PhysicalPath = change == "path" ? Path.Combine(Area.Kok, "replacement") : Area.SiteKoku,
                ApplicationRootPath = change == "application" ? Path.Combine(Area.Kok, "replacement") : Area.SiteKoku,
                ApplicationPoolName = change == "pool" ? "NewPool" : "OriginalPool",
                State = change == "state" ? "Stopped" : "Started",
                Bindings = change == "state" ? [new SiteBindingDto { Protocol = "https", HostName = Name, Port = 443 }] : []
            };
        }
        internal Task NativeMutation(bool creating) => SiteMutationGuard.RunMutationAsync(Gates, Name, creating,
            () => Task.FromResult(true), default);
        internal async Task WaitForSourceWaiter(Task operation, CancellationToken token)
        {
            while (BackupSourceLifecycle.ReferencesForSource(IndexPath, "site", Name) < 2)
            {
                Assert.False(operation.IsCompleted, "Backup did not reach its source wait.");
                await Task.Delay(1, token);
            }
        }
        public void Dispose() => Area.Dispose();
    }

    private sealed class Preferences(string archiveRoot) : IPanelPreferencesService
    {
        internal int Calls;
        internal Func<CancellationToken, Task>? BeforeRead;
        public async Task<PanelPreferencesDto> GetAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls);
            if (BeforeRead is not null) await BeforeRead(token);
            return new PanelPreferencesDto { DefaultBackupRoot = archiveRoot };
        }
        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken token = default) => throw new NotSupportedException();
    }

    public class SiteProxy : DispatchProxy
    {
        internal Func<SiteDto> Read = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == "GetSiteAsync"
            ? Task.FromResult(Read()) : throw new NotSupportedException(targetMethod.Name);
    }
}
