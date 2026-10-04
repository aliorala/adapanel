using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Retry has a process-wide gate; keep these controlled waits out of other FTP fixtures.
[CollectionDefinition("Queued backup authority", DisableParallelization = true)]
public sealed class QueuedBackupAuthorityCollection { }

[Collection("Queued backup authority")]
public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> AdminRevocations() =>
        from operation in new[] { "restore", "items", "delete", "chain", "retry" }
        from change in new[] { "disabled", "deleted", "role", "password" }
        select new object[] { operation, change };

    [Theory]
    [MemberData(nameof(AdminRevocations))]
    public async Task AQueuedAdministratorMutationRemeasuresThePersistentActor(string operation, string change)
    {
        using var f = new Fixture();
        await f.Prepare(operation);
        var controller = f.Controller(f.Admin);
        // Simulate a context that already tracked the authenticated actor before waiting.
        await f.ControllerDb.Users.SingleAsync(user => user.Id == f.Admin, f.Token);
        var before = await f.IndexBytes();
        var lease = await f.EnterSource();
        f.Jobs.Pause = operation == "items";
        var action = Capture(() => f.Invoke(controller, operation));
        try
        {
            await f.WaitQueued(action, operation);
            await f.Change(change, f.Admin);
        }
        finally { lease.Dispose(); f.Jobs.Release.TrySetResult(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertNoMutation(before);
    }

    public static IEnumerable<object[]> TenantRevocations() =>
        from operation in new[] { "download-items", "delete", "chain" }
        from change in new[] { "source", "section", "parent_disabled", "parent_deleted", "reparented", "detached" }
        select new object[] { operation, change };

    [Theory]
    [MemberData(nameof(TenantRevocations))]
    public async Task AQueuedTenantOperationRechecksSourceParentAndSection(string operation, string change)
    {
        using var f = new Fixture();
        await f.Prepare(operation);
        var controller = f.Controller(f.Child);
        var before = await f.IndexBytes();
        var lease = await f.EnterSource();
        f.Jobs.Pause = operation == "items";
        var action = Capture(() => f.Invoke(controller, operation));
        try
        {
            await f.WaitQueued(action, operation);
            await f.Change(change, f.Child);
        }
        finally { lease.Dispose(); f.Jobs.Release.TrySetResult(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertNoMutation(before);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("chain")]
    public async Task SubscriptionRecordOwnershipIsReadAgainAfterTheSourceWait(string operation)
    {
        using var f = new Fixture();
        await f.Prepare(operation, subscription: true);
        var lease = await f.EnterSource();
        var action = Capture(() => f.Invoke(f.Controller(f.Customer), operation));
        try
        {
            await f.WaitQueued(action, operation);
            f.Record.SubscriptionUserId = f.Other.ToString("N");
            await f.Area.KayitlariYazAsync(f.Record);
        }
        finally { lease.Dispose(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.True(File.Exists(f.Record.Path));
        Assert.Empty(f.Cloud.Istekler);
        Assert.Equal(f.Other.ToString("N"), (await f.Service.GetBackupsAsync(f.Token)).Single().SubscriptionUserId);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("items")]
    [InlineData("delete")]
    [InlineData("chain")]
    public async Task AnUnchangedAdministratorCanPerformTheRequestedMutation(string operation)
    {
        using var f = new Fixture();
        await f.Prepare(operation);
        await f.Invoke(f.Controller(f.Admin), operation);
        if (operation is "delete" or "chain")
        {
            Assert.False(File.Exists(f.Record.Path));
            Assert.Empty(await f.Service.GetBackupsAsync(f.Token));
            Assert.Single(f.Cloud.Istekler);
        }
        else Assert.NotEmpty(Directory.EnumerateFiles(operation == "restore" ? f.Target : f.Area.Kok,
            "payload.txt", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("download-items")]
    [InlineData("delete")]
    [InlineData("chain")]
    public async Task AnUnchangedSubUserKeepsItsPermittedBackupAccess(string operation)
    {
        using var f = new Fixture();
        await f.Prepare(operation);
        await f.Invoke(f.Controller(f.Child), operation);
        if (operation is "delete" or "chain") Assert.Empty(await f.Service.GetBackupsAsync(f.Token));
        else
        {
            Assert.Equal(1, f.SelectedDownloads);
            Assert.Empty(Directory.EnumerateFiles(f.Area.Kok, "payload.txt", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task AnActiveRetryActorReachesTheFakeOutboundBoundary()
    {
        using var f = new Fixture();
        await f.Prepare("retry");
        var error = await Assert.ThrowsAsync<AppException>(() => f.Invoke(f.Controller(f.Admin), "retry"));
        Assert.Equal("backup.remote_target_forbidden", error.MessageKey);
        Assert.Equal(1, f.OutboundAttempts);
        Assert.True(File.Exists(f.Record.Path));
    }

    [Fact]
    public async Task ChainDeletionAuthorizesEveryDescendantBeforeDeletingAnyCopy()
    {
        using var f = new Fixture();
        await f.Prepare("chain", subscription: true);
        f.Record.ChainId = Guid.NewGuid().ToString("N");
        f.Record.BackupMode = "full";
        var descendantPath = Path.Combine(Path.GetDirectoryName(f.Record.Path)!, "descendant.zip");
        File.Copy(f.Record.Path, descendantPath);
        var descendant = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"), Name = "descendant.zip", Path = descendantPath,
            Type = f.Record.Type, Source = f.Record.Source, Format = "zip", Status = "created",
            ChainId = f.Record.ChainId, ChainIndex = 1, BaseBackupId = f.Record.Id, BackupMode = "incremental",
            SubscriptionUserId = f.Other.ToString("N"), TargetType = "webdav",
            RemoteUri = "https://backup.example.test/archives/descendant.zip", CreatedAtUtc = DateTime.UtcNow
        };
        await f.Area.KayitlariYazAsync(f.Record, descendant);
        var before = await f.IndexBytes();
        var error = await Capture(async () => await f.Controller(f.Customer).DeleteChainAsync(f.Record.Id, f.Token,
            new DeleteBackupChainRequest { ExpectedBackupIds = [f.Record.Id, descendant.Id] }));
        AssertAuthorization(error);
        await f.AssertNoMutation(before);
        Assert.True(File.Exists(descendantPath));
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("items")]
    [InlineData("delete")]
    [InlineData("chain")]
    [InlineData("retry")]
    public async Task CancelingAQueuedMutationDoesNotTouchTheArchiveOrTarget(string operation)
    {
        using var f = new Fixture();
        await f.Prepare(operation);
        var before = await f.IndexBytes();
        using var canceled = new CancellationTokenSource();
        var lease = await f.EnterSource();
        f.Jobs.Pause = operation == "items";
        var action = Capture(() => f.Invoke(f.Controller(f.Admin), operation, canceled.Token));
        try { await f.WaitQueued(action, operation); canceled.Cancel(); }
        finally { lease.Dispose(); f.Jobs.Release.TrySetResult(); }
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        await f.AssertNoMutation(before);
    }

    [Fact]
    public async Task GranularRestoreSharesTheSourceMutationLease()
    {
        using var f = new Fixture();
        await f.Prepare("items");
        var before = await f.IndexBytes();
        using var canceled = new CancellationTokenSource();
        var lease = await f.EnterSource();
        var action = Capture(() => f.Invoke(f.Controller(f.Admin), "items", canceled.Token));
        bool queued;
        try
        {
            while (!action.IsCompleted && f.References < 2) await Task.Delay(1, f.Token);
            queued = !action.IsCompleted;
            canceled.Cancel();
        }
        finally { lease.Dispose(); }
        await action.WaitAsync(f.Token);
        Assert.True(queued, "Granular restore wrote files while the same source's archive mutation lease was held.");
        await f.AssertNoMutation(before);
    }

    private static void AssertAuthorization(Exception? exception) => Assert.Contains(
        Assert.IsType<AppException>(exception).MessageKey,
        new[] { "auth.unauthorized", "auth.forbidden", "auth.section_forbidden", "auth.site_forbidden", "auth.subscription_forbidden" });

    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception exception) { return exception; } }

    private sealed partial class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        internal readonly SahteBulut Cloud = new() { VarsayilanDurum = HttpStatusCode.NoContent };
        internal readonly PausedJobs Jobs = new();
        internal readonly Guid Admin = Guid.NewGuid(), Customer = Guid.NewGuid(), Child = Guid.NewGuid(), Other = Guid.NewGuid();
        internal readonly string Target = Path.Combine(Path.GetTempPath(), "adapanel-actor-target-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection keeper = new($"Data Source=BackupActor-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly List<ServiceProvider> providers = [];
        internal readonly CustomerScopeService Scopes;
        internal readonly BackupService Service;
        internal BackupRecordDto Record = null!;
        internal AdaPanelDbContext ControllerDb = null!;
        internal int OutboundAttempts;
        internal int ProducerBoundaryAttempts;
        internal int MailRuntimeReads;
        internal int SelectedDownloads;
        private bool retry;
        internal CancellationToken Token => Area.Iptal;
        private string IndexPath => Area.DosyaDeposu.ResolvePath("config/backups.json");
        internal int References => BackupSourceLifecycle.ReferencesForSource(IndexPath, Record.Type, Record.Source);
        internal const string Site = "actor.example";
        private const string Remote = "ftp://backup.example.test/archives";

        internal Fixture(bool stopAtProducerBoundary = false)
        {
            keeper.Open();
            var db = Context();
            db.Database.EnsureCreated();
            foreach (var id in new[] { Admin, Customer, Child, Other }) db.Users.Add(new PanelUser
            {
                Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test",
                Role = id == Admin ? UserRole.Administrator : UserRole.Customer, PasswordHash = "fixture-hash", IsActive = true
            });
            db.SaveChanges();
            var sites = new SahteSiteServisi(Area.SiteKoku);
            Scopes = new CustomerScopeService(Area.DosyaDeposu,
                Fake<IServicePlanService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync"
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            Scopes.SetScopeAsync(Customer, [Site], 0, 0).GetAwaiter().GetResult();
            Scopes.SetScopeAsync(Other, [Site], 0, 0).GetAwaiter().GetResult();
            Scopes.SetParentSubscriptionAsync(Child, Customer).GetAwaiter().GetResult();
            var preferences = stopAtProducerBoundary
                ? Fake<IPanelPreferencesService>((method, _) =>
                {
                    if (method.Name != "GetAsync") throw new NotSupportedException(method.Name);
                    ProducerBoundaryAttempts++;
                    throw new AppException("fixture.producer_boundary", 500, 500);
                })
                : new SahteTercihServisi(Path.Combine(Area.Kok, "backups"));
            var mailDirectory = Path.Combine(Area.Kok, "fake-mail-runtime");
            Directory.CreateDirectory(Path.Combine(mailDirectory, "data"));
            Service = new BackupService(sites, null!, null!, preferences, null!,
                Area.DosyaDeposu, new SahteIstemciFabrikasi(Cloud), secretProtector: new SahteKoruyucu(),
                customerScopes: Scopes, databaseOwnership: new DatabaseOwnershipService(Area.DosyaDeposu),
                mailBackupRuntime: Fake<IMailBackupRuntime>((method, _) =>
                {
                    if (method.Name == "GetInstallDirectory") { MailRuntimeReads++; return mailDirectory; }
                    throw new InvalidOperationException("A producer test must stop before any mail process operation.");
                }),
                outboundResolver: (host, token) =>
                {
                    if (retry) { OutboundAttempts++; throw new AppException("fixture.outbound_boundary", 500, 500); }
                    return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
                });
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, "sentinel.txt"), "live data");
        }

        private AdaPanelDbContext Context()
        {
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options);
            contexts.Add(db); return db;
        }

        internal async Task Prepare(string operation, bool subscription = false)
        {
            retry = operation == "retry";
            var path = Path.Combine(Area.DosyaDeposu.EnsureDirectory("backups/2026-10-03/sites/actor.example"), "fixture.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open())) writer.Write("backup payload");
            Record = new BackupRecordDto
            {
                Id = Guid.NewGuid().ToString("N"), Name = "fixture.zip", Path = path, Format = "zip",
                Type = subscription ? "subscription" : "site", Source = subscription ? Customer.ToString("N") : Site,
                SubscriptionUserId = Customer.ToString("N"),
                Status = retry ? "created_remote_failed" : "created", TargetType = retry ? "ftp" : "webdav",
                RemoteUri = retry ? "" : "https://backup.example.test/archives/fixture.zip",
                CreatedAtUtc = DateTime.UtcNow, Size = new FileInfo(path).Length
            };
            Record.ContentHash = await BackupContentHash.ComputeAsync(path, Token);
            await Area.KayitlariYazAsync(Record);
            await Area.UzakHedefiYazAsync(retry ? "ftp" : "webdav", retry ? Remote : "https://backup.example.test/archives", "fixture", SahteKoruyucu.Onek + "password");
        }

        internal BackupController Controller(Guid actor, ISubscriptionBackupScheduleService? schedules = null)
        {
            ControllerDb = Context();
            var services = new ServiceCollection().AddSingleton(ControllerDb).AddSingleton<ICustomerScopeService>(Scopes).BuildServiceProvider();
            providers.Add(services);
            return new BackupController(Service, Scopes, new DatabaseOwnershipService(Area.DosyaDeposu), schedules!, Jobs, Area.Denetim, ControllerDb)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                        new Claim(ClaimTypes.Role, actor == Admin ? "Administrator" : "Customer"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("fixture-hash"))
                    ], "Test"))
                } }
            };
        }

        internal async Task Invoke(BackupController controller, string operation, CancellationToken? token = null)
        {
            var ct = token ?? Token;
            switch (operation)
            {
                case "restore": await controller.RestoreAsync(new RestoreBackupRequest { BackupId = Record.Id, TargetPath = Target }, ct); break;
                case "items": await controller.RestoreItemsAsync(new RestoreBackupItemsRequest { BackupId = Record.Id, Items = ["payload.txt"] }, ct); break;
                case "download-items":
                    var result = Assert.IsType<FileStreamResult>(await controller.DownloadItemsAsync(
                        new DownloadBackupItemsRequest { BackupId = Record.Id, Items = ["payload.txt"] }, ct));
                    await using (var stream = result.FileStream)
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                    using (var reader = new StreamReader(Assert.Single(zip.Entries).Open()))
                        Assert.Equal("backup payload", await reader.ReadToEndAsync(ct));
                    SelectedDownloads++;
                    break;
                case "delete": await controller.DeleteAsync(Record.Id, ct); break;
                case "chain": await controller.DeleteChainAsync(Record.Id, ct, new DeleteBackupChainRequest { ExpectedBackupIds = [Record.Id] }); break;
                case "retry": await controller.RetryRemoteAsync(Record.Id, new RetryBackupRemoteRequest { RemoteUrl = Remote }, ct); break;
                default: throw new InvalidOperationException(operation);
            }
        }
        internal Task<IDisposable> EnterSource() => BackupSourceLifecycle.EnterAsync(IndexPath, Record.Type, Record.Source, Token);
        internal async Task WaitQueued(Task action, string operation)
        {
            if (operation == "items") await Jobs.Entered.Task.WaitAsync(Token);
            else while (!action.IsCompleted && References < 2) await Task.Delay(1, Token);
            Assert.False(action.IsCompleted, "The real backup operation did not reach the controlled wait.");
        }
        internal async Task Change(string change, Guid actor)
        {
            if (change == "source") { await Scopes.SetScopeAsync(Customer, [], 0, 0, Token); return; }
            if (change == "section") { await Scopes.SetSubUserSectionsAsync(Child, ["files"], Token); return; }
            if (change == "reparented") { await Scopes.SetParentSubscriptionAsync(Child, Other, Token); return; }
            if (change == "detached") { await Scopes.SetParentSubscriptionAsync(Child, null, Token); return; }
            using var db = Context();
            var id = change.StartsWith("parent_", StringComparison.Ordinal) ? Customer : actor;
            var user = await db.Users.SingleAsync(item => item.Id == id, Token);
            if (change.EndsWith("deleted", StringComparison.Ordinal)) db.Users.Remove(user);
            if (change.EndsWith("disabled", StringComparison.Ordinal)) user.IsActive = false;
            if (change == "role") user.Role = UserRole.Operator;
            if (change == "password") user.PasswordHash = "changed-password-hash";
            await db.SaveChangesAsync(Token);
        }
        internal Task<byte[]> IndexBytes() => File.ReadAllBytesAsync(IndexPath, Token);
        internal async Task AssertNoMutation(byte[] before)
        {
            Assert.Equal(before, await IndexBytes());
            Assert.True(File.Exists(Record.Path));
            Assert.Empty(Cloud.Istekler);
            Assert.Equal(0, OutboundAttempts);
            Assert.Equal("live data", File.ReadAllText(Path.Combine(Target, "sentinel.txt")));
            Assert.False(File.Exists(Path.Combine(Target, "payload.txt")));
            Assert.Empty(Directory.EnumerateFiles(Area.Kok, "payload.txt", SearchOption.AllDirectories));
        }
        public void Dispose()
        {
            foreach (var provider in providers) provider.Dispose();
            foreach (var context in contexts) context.Dispose();
            keeper.Dispose();
            Directory.Delete(Target, true);
            Area.Dispose();
        }
    }

    private sealed class PausedJobs : IRunningJobService
    {
        internal bool Pause;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IAsyncDisposable> BeginAsync(string kind, string title, Guid? subjectUserId, CancellationToken cancellationToken = default)
        {
            if (Pause) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return new EmptyJob();
        }
        public Task<RunningJobsStatusDto> GetStatusAsync(IReadOnlyCollection<string> kinds, IReadOnlySet<string>? allowedSubjectUserIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        private sealed class EmptyJob : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> callback) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Callback = callback; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Callback = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Callback(targetMethod!, args ?? []);
    }
}
