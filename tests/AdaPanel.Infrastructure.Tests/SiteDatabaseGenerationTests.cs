using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Actual controllers and JSON link/ownership stores; mutable IIS evidence, no native IIS calls.
public sealed class SiteDatabaseGenerationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData("move", "id")]
    [InlineData("link", "id")]
    [InlineData("unlink", "id")]
    [InlineData("move", "path")]
    [InlineData("link", "path")]
    [InlineData("unlink", "path")]
    [InlineData("move", "missing")]
    [InlineData("link", "missing")]
    [InlineData("unlink", "missing")]
    public async Task QueuedOperationRejectsReplacedOrMissingSite(string operation, string change)
    {
        using var fixture = new Fixture();
        await fixture.Seed(operation == "unlink" ? fixture.SiteName : "previous-site");
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgres", fixture.Database, cancellation.Token);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            fixture.CurrentSite = change switch
            {
                "id" => fixture.Site(id: 2),
                "path" => fixture.Site(path: @"D:\replacement-site"),
                _ => null
            };
        }
        finally { lease.Dispose(); }
        Assert.IsType<AppException>(await running.WaitAsync(Deadline));
        Assert.Equal(operation == "unlink" ? fixture.SiteName : "previous-site", await fixture.LinkedSite());
        Assert.Equal("previous-site", (await fixture.Ownership.GetAllAsync()).Single().SiteName);
        Assert.Equal(0, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task SiteScopeIsRemeasuredAfterWaitingForDatabase(string operation)
    {
        using var fixture = new Fixture(customer: true);
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database, cancellation.Token);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            fixture.AssignedSites = [];
        }
        finally { lease.Dispose(); }
        Assert.Equal("auth.site_forbidden", Assert.IsType<AppException>(await running.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
        Assert.Equal(0, fixture.AuditWrites);
    }

    [Fact]
    public async Task UnlinkDoesNotRemoveAConnectionMovedToAnotherSiteWhileQueued()
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database, cancellation.Token);
        var running = Capture(() => fixture.Run("unlink", cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            await fixture.Links.LinkAsync("postgresql", fixture.Database, "new-site");
        }
        finally { lease.Dispose(); }
        Assert.IsType<AppException>(await running.WaitAsync(Deadline));
        Assert.Equal("new-site", await fixture.LinkedSite());
        Assert.Equal(0, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task UnchangedSiteStillAcceptsQueuedOperation(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(operation == "unlink" ? fixture.SiteName : "previous-site");
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database, cancellation.Token);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try { await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token); }
        finally { lease.Dispose(); }
        Assert.Null(await running.WaitAsync(Deadline));
        Assert.Equal(operation == "unlink" ? null : fixture.SiteName, await fixture.LinkedSite());
        Assert.Equal(operation == "move" ? fixture.SiteName : "previous-site", (await fixture.Ownership.GetAllAsync()).Single().SiteName);
        Assert.Equal(1, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task SiteStateAndBindingChangesDoNotChangeItsIdentity(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database, cancellation.Token);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            fixture.CurrentSite = fixture.Site(state: "Stopped", port: 8443);
        }
        finally { lease.Dispose(); }
        Assert.Null(await running.WaitAsync(Deadline));
        Assert.Equal(operation == "unlink" ? null : fixture.SiteName, await fixture.LinkedSite());
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task CanceledWaitDoesNotWriteAndReleasesItsReservation(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await running.WaitAsync(Deadline));
            Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
            Assert.Equal(0, fixture.AuditWrites);
        }
        finally { lease.Dispose(); }
        await fixture.Run(operation, default).WaitAsync(Deadline);
        Assert.Equal(1, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task RecreatingSameNameIdAndPathsInvalidatesQueuedTarget(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            await fixture.NativeMutation(fixture.SiteName, creating: false);
            await fixture.NativeMutation(fixture.SiteName, creating: true);
        }
        finally { lease.Dispose(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await running.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
        Assert.Equal(0, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task AnotherSitesGenerationDoesNotInvalidateQueuedTarget(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            await fixture.NativeMutation("unrelated-" + fixture.SiteName, creating: true);
        }
        finally { lease.Dispose(); }
        Assert.Null(await running.WaitAsync(Deadline));
        Assert.Equal(1, fixture.AuditWrites);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task CascadeRejectsNewMetadataOperationsUntilCleanupFinishes(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.BeforeSiteRead = async token =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = fixture.DeleteSite(cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Exception? error;
        try { error = await Capture(() => fixture.Run(operation, cancellation.Token)).WaitAsync(Deadline); }
        finally { release.TrySetResult(); }
        await deleting.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(0, fixture.AuditWrites);
        // End of cascade releases its deleting marker; an explicitly recreated site is usable.
        await fixture.NativeMutation(fixture.SiteName, creating: true);
        await fixture.Run("link", cancellation.Token);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
    }

    [Fact]
    public async Task CascadeRejectsRecreationAndSecondCascadeUntilItFinishes()
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeSiteRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = fixture.DeleteSite(cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        try
        {
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await Capture(() =>
                fixture.NativeMutation(fixture.SiteName, creating: true)).WaitAsync(Deadline)).MessageKey);
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await Capture(() =>
                fixture.DeleteSite(cancellation.Token)).WaitAsync(Deadline)).MessageKey);
            Assert.NotNull(fixture.CurrentSite);
        }
        finally { release.TrySetResult(); }
        await deleting.WaitAsync(Deadline);
        await fixture.NativeMutation(fixture.SiteName, creating: true);
        await fixture.Run("link", cancellation.Token);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
    }

    [Fact]
    public async Task CanceledCascadeReleasesTheSiteReservation()
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeSiteRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = Capture(() => fixture.DeleteSite(cancellation.Token));
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await deleting.WaitAsync(Deadline));
        fixture.BeforeSiteRead = null;
        Assert.NotNull(fixture.CurrentSite);
        await fixture.NativeMutation(fixture.SiteName, creating: true);
        await fixture.Run("link", default).WaitAsync(Deadline);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
    }

    [Theory]
    [InlineData("move")]
    [InlineData("link")]
    [InlineData("unlink")]
    public async Task UncertainNativeMutationInvalidatesOldQueuedEvidence(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Seed(fixture.SiteName);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("postgresql", fixture.Database);
        var running = Capture(() => fixture.Run(operation, cancellation.Token));
        try
        {
            await WaitForDatabaseWaiter(fixture.Database, running, cancellation.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FailedNativeMutation());
        }
        finally { lease.Dispose(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await running.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(fixture.SiteName, await fixture.LinkedSite());
        Assert.Equal(0, fixture.AuditWrites);
        await fixture.Run(operation, cancellation.Token);
        Assert.Equal(1, fixture.AuditWrites);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task WaitForDatabaseWaiter(string database, Task operation, CancellationToken token)
    {
        var gates = typeof(DatabaseLifecycle).GetField("Resources", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var references = gates.GetType().GetMethod("References", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while ((int)references.Invoke(gates, [DatabaseResourceIdentity.Key("postgresql", database)])! < 2)
        {
            if (operation.IsCompleted) { await operation; Assert.Fail("Operation never waited on its database resource."); }
            await Task.Delay(1, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.SiteGeneration." + Guid.NewGuid().ToString("N"));
        private readonly IConcurrencyLock gates = new ConcurrencyLock();
        private readonly ClaimsPrincipal principal;
        private readonly IIisSiteService sites;
        private readonly ICustomerScopeService scopes;
        private readonly IAuditService audit;
        private readonly JsonFileStore files;
        public string SiteName { get; } = "site-" + Guid.NewGuid().ToString("N");
        public string Database { get; } = "db_" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; } = Guid.NewGuid();
        public string[] AssignedSites { get; set; }
        public SiteDto? CurrentSite { get; set; }
        public SiteDatabaseLinkService Links { get; }
        public DatabaseOwnershipService Ownership { get; }
        public int AuditWrites;
        public Func<CancellationToken, Task>? BeforeSiteRead;
        public Fixture(bool customer = false)
        {
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Links = new SiteDatabaseLinkService(files);
            Ownership = new DatabaseOwnershipService(files);
            CurrentSite = Site();
            AssignedSites = [SiteName];
            principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner.ToString()),
                new Claim(ClaimTypes.Role, customer ? "Customer" : "Administrator")], "test"));
            sites = Fake<IIisSiteService>((method, args) => method.Name switch
            {
                "GetSiteAsync" => ReadSite((CancellationToken)args[1]!),
                "DeleteSiteAsync" => NativeMutation((string)args[0]!, creating: false),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetAssignedSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(AssignedSites),
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(null),
                _ => throw new NotSupportedException(method.Name)
            });
            audit = Fake<IAuditService>((_, _) => { Interlocked.Increment(ref AuditWrites); return Task.CompletedTask; });
        }
        public SiteDto Site(long id = 1, string? path = null, string state = "Started", int port = 80) => new()
        {
            Id = id, Name = SiteName, PhysicalPath = path ?? @"D:\fixture-site", ApplicationRootPath = path ?? @"D:\fixture-site",
            ApplicationPoolName = "fixture-pool", State = state, Bindings = [new() { Protocol = "http", Port = port }]
        };
        private async Task<SiteDto> ReadSite(CancellationToken token)
        {
            if (BeforeSiteRead is not null) await BeforeSiteRead(token);
            return CurrentSite ?? throw new AppException("sites.not_found", 404, 404);
        }
        public Task NativeMutation(string name, bool creating) => SiteMutationGuard.RunMutationAsync(gates, name, creating, () =>
        {
            if (name == SiteName) CurrentSite = creating ? Site() : null;
            return Task.FromResult(0);
        }, default);
        public Task FailedNativeMutation() => SiteMutationGuard.RunMutationAsync<int>(gates, SiteName, false,
            () => throw new InvalidOperationException("Uncertain native commit"), default);
        public async Task DeleteSite(CancellationToken token)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            using var container = new ServiceCollection().AddSingleton(sites).AddSingleton(gates)
                .AddSingleton<IDnsService>(Fake<IDnsService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<IFtpService>(Fake<IFtpService>((_, _) => Task.FromResult<IReadOnlyCollection<FtpUserDto>>([])))
                .AddSingleton<IMailService>(Fake<IMailService>((_, _) => Task.FromResult(new MailWorkspaceDto())))
                .AddSingleton<IDatabaseService>(Fake<IDatabaseService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<ISiteDatabaseLinkService>(Links).AddSingleton<IDatabaseOwnershipService>(Ownership)
                .AddSingleton<IReverseProxyService>(Fake<IReverseProxyService>((_, _) => Task.FromResult(new ReverseProxySiteConfigurationDto())))
                .AddSingleton<IDiskQuotaService>(Fake<IDiskQuotaService>((_, _) => Task.FromResult(new SiteDiskQuotaDto())))
                .AddSingleton<ICustomerScopeService>(Fake<ICustomerScopeService>((_, _) => Task.CompletedTask))
                .AddSingleton<ISslService>(Fake<ISslService>((_, _) => Task.CompletedTask))
                .AddSingleton<ISiteHostingSettingsService>(Fake<ISiteHostingSettingsService>((_, _) => Task.CompletedTask))
                .AddSingleton<IWafService>(Fake<IWafService>((_, _) => Task.CompletedTask))
                .AddSingleton<IDirectoryProtectionService>(Fake<IDirectoryProtectionService>((_, _) => Task.CompletedTask))
                .AddSingleton<ISchedulerService>(Fake<ISchedulerService>((m, _) => m.Name == "RemoveTasksUnderRootAsync" ? Task.FromResult(0) : throw new NotSupportedException(m.Name)))
                .AddSingleton<IPanelPreferencesService>(Fake<IPanelPreferencesService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<Microsoft.Extensions.Options.IOptions<PanelRuntimeOptions>>(options).AddSingleton(files)
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<SiteCascadeDeletionService>>(NullLogger<SiteCascadeDeletionService>.Instance).BuildServiceProvider();
            await ActivatorUtilities.CreateInstance<SiteCascadeDeletionService>(container).DeleteAsync(SiteName, false, false, token);
        }
        public async Task Seed(string linkedSite)
        {
            await Ownership.RecordAsync("postgresql", Database, Owner, "previous-site");
            await Links.LinkAsync("postgresql", Database, linkedSite);
        }
        public async Task<string?> LinkedSite() => (await Links.GetAllAsync()).SingleOrDefault()?.SiteName;
        public async Task Run(string operation, CancellationToken token)
        {
            if (operation == "move")
            {
                using var container = new ServiceCollection().AddSingleton<IDatabaseService>(Fake<IDatabaseService>((m, _) => throw new NotSupportedException(m.Name)))
                    .AddSingleton<IRemoteDbAccessService>(Fake<IRemoteDbAccessService>((m, _) => throw new NotSupportedException(m.Name)))
                    .AddSingleton<IDatabaseOwnershipService>(Ownership).AddSingleton<ISiteDatabaseLinkService>(Links)
                    .AddSingleton(sites).AddSingleton(audit).AddSingleton(gates).BuildServiceProvider();
                var controller = ActivatorUtilities.CreateInstance<DatabasesController>(container);
                controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
                await controller.MoveDatabaseAsync(Database, "postgres", new MoveDatabaseRequest { TargetSiteName = SiteName }, token);
                return;
            }
            var siteController = new SitesController(sites, null!, null!, null!, scopes, Ownership, Links, null!, null!, audit,
                gates, null!, NullLogger<SitesController>.Instance)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } } };
            if (operation == "link") await siteController.LinkSiteDatabaseAsync(SiteName, new LinkSiteDatabaseRequest { Engine = "postgres", Name = Database }, token);
            else await siteController.UnlinkSiteDatabaseAsync(SiteName, "postgres", Database, token);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = call; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
}
