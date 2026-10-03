using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Real controller/database/provisioning services with SQLite and temporary JSON/files.
// Native IIS and engine commands are replaced; generation mutations use the native guard boundary.
public sealed class SiteProvisioningGenerationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("missing")]
    [InlineData("same-id")]
    public async Task OptionalDatabaseRejectsChangedTargetAfterItsResourceWait(string change)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Database, cancellation.Token);
        var creating = fixture.CreateWithDatabase(cancellation.Token);
        try
        {
            await WaitForResource(fixture.Database, creating, cancellation.Token);
            if (change == "same-id") { await fixture.Native(false); await fixture.Native(true); }
            else fixture.Current = change switch { "id" => fixture.Site(2), "path" => fixture.Site(path: fixture.OtherPath), _ => null };
        }
        finally { lease.Dispose(); }
        var result = await creating.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Engine.CreateCalls);
        Assert.Empty(await fixture.Links.GetAllAsync());
        Assert.Empty(result.Created);
        Assert.Empty(result.GeneratedCredentials);
        Assert.Contains(result.Warnings, warning => warning.Contains("sites.target_changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptionalDatabaseForUnchangedTargetStillCreatesAndLinks()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Database, cancellation.Token);
        var creating = fixture.CreateWithDatabase(cancellation.Token);
        try { await WaitForResource(fixture.Database, creating, cancellation.Token); }
        finally { lease.Dispose(); }
        var result = await creating.WaitAsync(Deadline);
        Assert.Equal(1, fixture.Engine.CreateCalls);
        Assert.Equal(fixture.SiteName, (await fixture.Links.GetAllAsync()).Single().SiteName);
        Assert.Contains("mysql:" + fixture.Database, result.Created);
        Assert.Single(result.GeneratedCredentials);
    }

    [Fact]
    public async Task CanceledOptionalDatabaseWaitDoesNotLeakAReservation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Database);
        var creating = fixture.CreateWithDatabase(cancellation.Token);
        try
        {
            await WaitForResource(fixture.Database, creating, cancellation.Token);
            cancellation.Cancel();
            var result = await creating.WaitAsync(Deadline);
            Assert.Empty(result.Created);
            Assert.Equal(0, fixture.Engine.CreateCalls);
        }
        finally { lease.Dispose(); }
        await fixture.Native(false).WaitAsync(Deadline);
        await fixture.Native(true).WaitAsync(Deadline);
        Assert.NotNull(fixture.Current);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("cascade")]
    public async Task OptionalDatabasePinsSiteUntilProviderAndLinkPublicationFinish(string mutation)
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Engine.BeforeCreate = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var creating = fixture.CreateWithDatabase(cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Exception? error;
        try
        {
            error = await Capture(async () =>
            {
                if (mutation == "cascade") { using var deletion = await SiteMutationGuard.BeginDeletionAsync(fixture.Gates, fixture.SiteName, cancellation.Token); }
                else await fixture.Native(mutation == "create");
            }).WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        var result = await creating.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Contains("mysql:" + fixture.Database, result.Created);
        Assert.Equal(fixture.SiteName, (await fixture.Links.GetAllAsync()).Single().SiteName);
        await fixture.Native(false).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ExternalReplacementDuringProviderTriggersDatabaseCompensation()
    {
        using var fixture = new Fixture();
        fixture.Engine.BeforeCreate = _ => { fixture.Current = fixture.Site(2); return Task.CompletedTask; };
        var result = await fixture.CreateWithDatabase(default).WaitAsync(Deadline);
        Assert.Equal(1, fixture.Engine.CreateCalls);
        Assert.Equal(1, fixture.Engine.DeleteCalls);
        Assert.Empty(await fixture.Links.GetAllAsync());
        Assert.Empty(result.GeneratedCredentials);
        Assert.Empty(result.Created);
        Assert.Equal(2, fixture.Current!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProvisionRollsBackOnlyItsOwnNewSiteAndDirectory(bool canceled)
    {
        using var fixture = new Fixture();
        fixture.Defaults = (_, _) => { if (canceled) throw new OperationCanceledException(); throw new InvalidOperationException("defaults failed"); };
        var error = await Capture(() => fixture.Provision(default));
        Assert.NotNull(error);
        Assert.Equal(1, fixture.NativeDeletes);
        Assert.Null(fixture.Current);
        Assert.False(Directory.Exists(fixture.SitePath));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    public async Task FailedProvisionDoesNotDeleteAnExternallyReplacedSiteOrItsFiles(string change)
    {
        using var fixture = new Fixture();
        fixture.Defaults = (_, _) =>
        {
            fixture.Current = change == "id" ? fixture.Site(2) : fixture.Site(path: fixture.OtherPath);
            File.WriteAllText(Path.Combine(fixture.SitePath, "replacement.txt"), "replacement data");
            throw new InvalidOperationException("defaults failed after replacement");
        };
        Assert.NotNull(await Capture(() => fixture.Provision(default)));
        Assert.Equal(0, fixture.NativeDeletes);
        Assert.NotNull(fixture.Current);
        Assert.Equal("replacement data", File.ReadAllText(Path.Combine(fixture.SitePath, "replacement.txt")));
    }

    [Fact]
    public async Task ReplacementBeforeNativeCreateReturnsCannotBeAdoptedByProvisioningRollback()
    {
        using var fixture = new Fixture();
        fixture.AfterNativeCreate = async () =>
        {
            await fixture.Native(false);
            await fixture.Native(true);
            File.WriteAllText(Path.Combine(fixture.SitePath, "replacement.txt"), "replacement data");
        };
        fixture.Defaults = (_, _) => throw new InvalidOperationException("defaults failed");
        Assert.NotNull(await Capture(() => fixture.Provision(default)));
        Assert.Equal(1, fixture.NativeDeletes); // The deliberate replacement's delete only.
        Assert.NotNull(fixture.Current);
        Assert.Equal("replacement data", File.ReadAllText(Path.Combine(fixture.SitePath, "replacement.txt")));
    }

    [Fact]
    public async Task SuccessfulProvisionStillReturnsItsSite()
    {
        using var fixture = new Fixture();
        var site = await fixture.Provision(default).WaitAsync(Deadline);
        Assert.Equal(fixture.SiteName, site.Name);
        Assert.Equal(0, fixture.NativeDeletes);
        Assert.True(Directory.Exists(fixture.SitePath));
    }

    [Fact]
    public async Task FailedNativeRollbackDoesNotDeleteTheSurvivingSitesDirectory()
    {
        using var fixture = new Fixture();
        fixture.Defaults = (_, _) => throw new InvalidOperationException("defaults failed");
        fixture.NativeDeleteFailure = new IOException("native delete failed");
        Assert.NotNull(await Capture(() => fixture.Provision(default)));
        Assert.NotNull(fixture.Current);
        Assert.True(Directory.Exists(fixture.SitePath));
    }

    [Fact]
    public async Task NormalRollbackPreservesAPreexistingDirectory()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.SitePath);
        File.WriteAllText(Path.Combine(fixture.SitePath, "existing.txt"), "existing content");
        fixture.Defaults = (_, _) => throw new InvalidOperationException("defaults failed");
        Assert.NotNull(await Capture(() => fixture.Provision(default)));
        Assert.Null(fixture.Current);
        Assert.Equal("existing content", File.ReadAllText(Path.Combine(fixture.SitePath, "existing.txt")));
    }

    [Fact]
    public async Task CancellationAfterNativeCreateStillRollsBackItsProvenGeneration()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        fixture.AfterNativeCreate = () => { cancellation.Cancel(); return Task.CompletedTask; };
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => fixture.Provision(cancellation.Token)));
        Assert.Equal(1, fixture.NativeDeletes);
        Assert.Null(fixture.Current);
        Assert.False(Directory.Exists(fixture.SitePath));
    }

    [Theory]
    [InlineData("aspnet", "Default.aspx")]
    [InlineData("classic-asp", "default.asp")]
    public async Task RuntimeProvisioningMayUseIisGateWithoutChangingSiteIdentity(string profile, string starter)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var site = await fixture.Provision(cancellation.Token, profile).WaitAsync(Deadline);
        Assert.Equal(1, fixture.RuntimeWrites);
        Assert.Equal(fixture.SiteName, site.Name);
        Assert.Equal(fixture.SitePath, site.PhysicalPath);
        Assert.Equal(0, fixture.NativeDeletes);
        Assert.True(File.Exists(Path.Combine(fixture.SitePath, starter)));
        await fixture.Native(false).WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBackupCannotPreventFailedOrCanceledProvisionRollback(bool canceled)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var defaultsEntered = Signal();
        var defaultsRelease = Signal();
        var backupEntered = Signal();
        var backupRelease = Signal();
        fixture.Defaults = async (_, token) =>
        {
            defaultsEntered.TrySetResult();
            await defaultsRelease.Task.WaitAsync(token);
            throw new InvalidOperationException("defaults failed");
        };
        fixture.BeforeBackupPreferences = async token =>
        { backupEntered.TrySetResult(); await backupRelease.Task.WaitAsync(token); };
        var provisioning = Capture(() => fixture.Provision(cancellation.Token));
        await defaultsEntered.Task.WaitAsync(Deadline);
        var backup = Capture(() => fixture.BackupSite(default));
        await Task.WhenAny(backupEntered.Task, backup).WaitAsync(Deadline);
        Exception? provisioningError;
        int deleted;
        bool siteRemained;
        bool directoryRemained;
        try
        {
            if (canceled) cancellation.Cancel();
            else defaultsRelease.TrySetResult();
            provisioningError = await provisioning.WaitAsync(Deadline);
            deleted = fixture.NativeDeletes;
            siteRemained = fixture.Current is not null;
            directoryRemained = Directory.Exists(fixture.SitePath);
        }
        finally { defaultsRelease.TrySetResult(); backupRelease.TrySetResult(); }
        var backupError = await backup.WaitAsync(Deadline);
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(provisioningError);
        else Assert.IsType<InvalidOperationException>(provisioningError);
        Assert.True(deleted == 1 && !siteRemained && !directoryRemained,
            $"Rollback was blocked: deletes={deleted}, siteRemained={siteRemained}, directoryRemained={directoryRemained}, backupPreferences={fixture.BackupPreferenceReads}.");
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(backupError).MessageKey);
        Assert.Equal(0, fixture.BackupPreferenceReads);
        // The failed/canceled operation leaves no reservation behind for the next valid generation.
        await fixture.Native(true);
        fixture.BeforeBackupPreferences = null;
        Assert.True(File.Exists((await fixture.BackupSite(default)).Path));
    }

    [Fact]
    public async Task BackupStartsOnlyAfterSuccessfulProvisionPublishesTheSite()
    {
        using var fixture = new Fixture();
        var defaultsEntered = Signal();
        var defaultsRelease = Signal();
        var backupEntered = Signal();
        var backupRelease = Signal();
        fixture.Defaults = async (site, token) =>
        {
            defaultsEntered.TrySetResult();
            await defaultsRelease.Task.WaitAsync(token);
            return new SiteDefaultsResultDto { SiteName = site.Name };
        };
        fixture.BeforeBackupPreferences = async token =>
        { backupEntered.TrySetResult(); await backupRelease.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var provisioning = fixture.Provision(cancellation.Token);
        await defaultsEntered.Task.WaitAsync(Deadline);
        var backup = Capture(() => fixture.BackupSite(cancellation.Token));
        await Task.WhenAny(backupEntered.Task, backup).WaitAsync(Deadline);
        defaultsRelease.TrySetResult();
        backupRelease.TrySetResult();
        await provisioning.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await backup.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(0, fixture.BackupPreferenceReads);
        fixture.BeforeBackupPreferences = null;
        Assert.True(File.Exists((await fixture.BackupSite(cancellation.Token)).Path));
    }

    [Fact]
    public async Task NativeCreateReturnGapKeepsBackupAndForeignCreationWatcherOutsideProvisioning()
    {
        using var fixture = new Fixture();
        using var observer = await SiteMutationGuard.WatchCreationAsync(fixture.Gates, fixture.SiteName, default);
        var nativeCommitted = Signal();
        var returnNative = Signal();
        fixture.AfterNativeCreate = async () =>
        { nativeCommitted.TrySetResult(); await returnNative.Task.WaitAsync(Deadline); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var provisioning = fixture.Provision(cancellation.Token);
        await nativeCommitted.Task.WaitAsync(Deadline);
        try
        {
            // Mirrors the native method awaiting firewall work after its committed IIS write.
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
                await Capture(() => fixture.BackupSite(cancellation.Token)).WaitAsync(Deadline)).MessageKey);
            Assert.Equal(0, fixture.BackupPreferenceReads);
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await Capture(async () =>
            {
                using var foreignTarget = await observer.CaptureCreatedAsync(fixture.Sites, fixture.Current!, cancellation.Token);
            })).MessageKey);
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await Capture(async () =>
            {
                using var deletion = await SiteMutationGuard.BeginDeletionAsync(fixture.Gates, fixture.SiteName, cancellation.Token);
            })).MessageKey);
        }
        finally { returnNative.TrySetResult(); }
        var site = await provisioning.WaitAsync(Deadline);
        // The controller's outer observer may publish the completed site after the inner owner exits.
        using var completedTarget = await observer.CaptureCreatedAsync(fixture.Sites, site, cancellation.Token);
        await completedTarget.RunAsync(_ => Task.CompletedTask, cancellation.Token);
        Assert.True(File.Exists((await fixture.BackupSite(cancellation.Token)).Path));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("reserve")]
    public async Task PreviouslyCapturedTargetCannotEnterNewProvisioningIntent(string operation)
    {
        using var fixture = new Fixture();
        await fixture.Native(true);
        using var previous = await SiteMutationGuard.CaptureAsync(fixture.Sites, fixture.Gates, fixture.SiteName, default);
        var beforeNative = Signal();
        var releaseNative = Signal();
        fixture.BeforeNativeCreate = async () =>
        {
            beforeNative.TrySetResult();
            await releaseNative.Task.WaitAsync(Deadline);
            throw new InvalidOperationException("native duplicate validation failed");
        };
        var provisioning = Capture(() => fixture.Provision(default));
        await beforeNative.Task.WaitAsync(Deadline);
        var entered = false;
        Exception? error;
        try
        {
            error = await Capture(async () =>
            {
                if (operation == "reserve")
                {
                    using var use = await previous.ReserveAsync(default);
                    entered = true;
                }
                else await previous.RunAsync(_ => { entered = true; return Task.CompletedTask; }, default);
            }).WaitAsync(Deadline);
        }
        finally { releaseNative.TrySetResult(); }
        Assert.IsType<InvalidOperationException>(await provisioning.WaitAsync(Deadline));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.False(entered);
        Assert.Equal(0, fixture.NativeDeletes);
        // No native write occurred: releasing the failed creation intent restores this same target.
        await previous.RunAsync(_ => Task.CompletedTask, default);
        using var releasedUse = await previous.ReserveAsync(default);
    }

    [Fact]
    public async Task ProvisioningIntentCoversTheUseReleaseBeforeRollbackStarts()
    {
        using var fixture = new Fixture();
        using var creation = await SiteMutationGuard.WatchCreationAsync(fixture.Gates, fixture.SiteName, default, provisioning: true);
        await fixture.Native(true);
        using var target = await creation.CaptureCreatedAsync(fixture.Sites, fixture.Current!, default);
        var use = await target.ReserveAsync(default);
        use.Dispose();
        use.Dispose();
        // Exact rollback transition: no active use remains, but the creation owner is still alive.
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
            await Capture(() => fixture.BackupSite(default))).MessageKey);
        Assert.Equal(0, fixture.BackupPreferenceReads);
        using (await target.BeginDeletionAsync(default)) await fixture.Native(false);
        creation.Dispose();
        creation.Dispose();
        await fixture.Native(true);
        Assert.True(File.Exists((await fixture.BackupSite(default)).Path));
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static async Task WaitForResource(string database, Task running, CancellationToken token)
    {
        var gates = typeof(DatabaseLifecycle).GetField("Resources", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var references = gates.GetType().GetMethod("References", BindingFlags.NonPublic | BindingFlags.Instance)!;
        while ((int)references.Invoke(gates, [DatabaseResourceIdentity.Key("mysql", database)])! < 2)
        {
            if (running.IsCompleted) { await running; Assert.Fail("The actual database service never entered its resource queue."); }
            await Task.Delay(1, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.SiteProvisionGeneration." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new($"Data Source=SiteProvision-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly JsonFileStore files;
        private readonly IIisSiteService sites;
        public IIisSiteService Sites => sites;
        private readonly ICustomerScopeService scopes;
        private readonly Guid actor = Guid.NewGuid();
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public string SiteName { get; } = "site-" + Guid.NewGuid().ToString("N");
        public string Database { get; } = "db_" + Guid.NewGuid().ToString("N");
        public string SitePath => Path.Combine(root, "site");
        public string OtherPath => Path.Combine(root, "other");
        public SiteDto? Current;
        public int NativeDeletes;
        public int RuntimeWrites;
        public int BackupPreferenceReads;
        public Func<CancellationToken, Task>? BeforeBackupPreferences;
        public Exception? NativeDeleteFailure;
        public Provider Engine { get; } = new();
        public SiteDatabaseLinkService Links { get; }
        public Func<Task>? AfterNativeCreate;
        public Func<Task>? BeforeNativeCreate;
        public Func<SiteDto, CancellationToken, Task<SiteDefaultsResultDto>> Defaults = (site, _) => Task.FromResult(new SiteDefaultsResultDto { SiteName = site.Name });

        public Fixture()
        {
            connection.Open();
            var db = Context();
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = actor, UserName = actor.ToString("N"), Email = "fixture@example.test", Role = UserRole.Administrator, IsActive = true, PasswordHash = "fixture-hash" });
            db.InstalledComponents.Add(new InstalledComponent { ComponentId = DatabaseProfiles.ResolveDescriptor(DbEngine.MySql).ComponentId,
                Status = ComponentInstallStatus.Installed, ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = "fixture-admin" }) });
            db.SaveChanges();
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Links = new SiteDatabaseLinkService(files);
            sites = Fake<IIisSiteService>((method, args) => method.Name switch
            {
                "GetSiteAsync" => ReadSite((CancellationToken)args[1]!),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>(Current is null ? [] : [Current]),
                "CreateSiteAsync" => CreateNative(),
                "DeleteSiteAsync" => Native(false),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(null),
                _ => throw new NotSupportedException(method.Name)
            });
        }
        private AdaPanelDbContext Context()
        { var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection.ConnectionString).Options); contexts.Add(db); return db; }
        public SiteDto Site(long id = 1, string? path = null) => new() { Id = id, Name = SiteName, PhysicalPath = path ?? SitePath, ApplicationRootPath = path ?? SitePath, ApplicationPoolName = "" };
        private Task<SiteDto> ReadSite(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Current ?? throw new AppException("sites.not_found", 404, 404)); }
        public Task Native(bool creating) => SiteMutationGuard.RunMutationAsync(Gates, SiteName, creating, () =>
        {
            if (creating) { Directory.CreateDirectory(SitePath); Current = Site(); }
            else { NativeDeletes++; if (NativeDeleteFailure is not null) throw NativeDeleteFailure; Current = null; }
            return Task.FromResult(0);
        }, default);
        private async Task<SiteDto> CreateNative()
        {
            if (BeforeNativeCreate is not null) await BeforeNativeCreate();
            await Native(true);
            var created = Current!;
            if (AfterNativeCreate is not null) await AfterNativeCreate();
            return created;
        }
        public Task<BackupRecordDto> BackupSite(CancellationToken token)
        {
            var preferences = Fake<IPanelPreferencesService>((method, args) => method.Name == "GetAsync"
                ? ReadBackupPreferences((CancellationToken)args[0]!) : throw new NotSupportedException(method.Name));
            var backup = new BackupService(sites, null!, null!, preferences, null!, files,
                Fake<IHttpClientFactory>((method, _) => throw new NotSupportedException(method.Name)), concurrencyLock: Gates);
            return backup.BackupSiteAsync(new BackupSiteRequest { SiteName = SiteName, IncludeFiles = true, IncludeConfig = true }, token);
        }
        private async Task<PanelPreferencesDto> ReadBackupPreferences(CancellationToken token)
        {
            BackupPreferenceReads++;
            if (BeforeBackupPreferences is not null) await BeforeBackupPreferences(token);
            return new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(root, "backups") };
        }
        public async Task<SiteCreationResultDto> CreateWithDatabase(CancellationToken token)
        {
            var protector = new Protector();
            var database = new DatabaseService(Context(), protector, new DatabaseCredentialStore(files, protector), Links, [Engine]);
            var ownership = new DatabaseOwnershipService(files);
            var provisioning = Fake<ISiteProvisioningService>((_, _) => CreateNative());
            var audit = Fake<IAuditService>((_, _) => Task.CompletedTask);
            var controller = new SitesController(sites, provisioning, null!, null!, scopes, ownership, Links, null!, null!, audit, Gates, null!, NullLogger<SitesController>.Instance)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Administrator")], "test")) } } };
            var request = new CreateSiteRequest { Name = SiteName, PhysicalPath = "", ProvisionDefaultServices = false,
                CreateDatabase = true, DatabaseEngine = "mysql", DatabaseName = Database };
            var bootstrap = Fake<IComponentBootstrapper>((_, _) => Task.FromResult(new SoftwareCatalogDto()));
            var response = await controller.CreateSiteAsync(request, database, null!, null!, null!, bootstrap, Context(), token);
            return Assert.IsType<ApiResponse<SiteCreationResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }
        public async Task<SiteDto> Provision(CancellationToken token, string profile = "static")
        {
            using var container = new ServiceCollection().AddSingleton(sites).AddSingleton(Gates)
                .AddSingleton<IComponentBootstrapper>(Fake<IComponentBootstrapper>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<IPhpService>(Fake<IPhpService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<IAspNetService>(Fake<IAspNetService>((m, args) => m.Name is "SetDotNetVersionAsync" or "SetClassicAspAsync"
                    ? Gates.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
                    {
                        RuntimeWrites++;
                        return Task.FromResult(new AspSiteConfigurationDto { SiteName = SiteName });
                    }, (CancellationToken)args[2]!) : throw new NotSupportedException(m.Name)))
                .AddSingleton<IAppRuntimeService>(Fake<IAppRuntimeService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<ISiteDefaultsService>(Fake<ISiteDefaultsService>((_, args) => Defaults((SiteDto)args[0]!, (CancellationToken)args[1]!)))
                .AddSingleton<IWafService>(Fake<IWafService>((m, _) => throw new NotSupportedException(m.Name)))
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<SiteProvisioningService>>(NullLogger<SiteProvisioningService>.Instance).BuildServiceProvider();
            return await ActivatorUtilities.CreateInstance<SiteProvisioningService>(container).CreateAndProvisionAsync(
                new CreateSiteRequest { Name = SiteName, PhysicalPath = SitePath, RuntimeProfile = profile }, actor, null, token);
        }
        public void Dispose()
        { foreach (var db in contexts) db.Dispose(); connection.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Protector : ISecretProtector
    { public string Protect(string value) => value; public string Unprotect(string value) => value; }
    private sealed class Provider : IDatabaseEngineProvider
    {
        public DbEngine Engine => DbEngine.MySql;
        public int CreateCalls, DeleteCalls;
        public Func<CancellationToken, Task>? BeforeCreate;
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token) => Task.FromResult(new DatabaseWorkspaceDto());
        public async Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token)
        { CreateCalls++; if (BeforeCreate is not null) await BeforeCreate(token); }
        public Task DeleteDatabaseAsync(DatabaseConnectionProfile p, string n, CancellationToken t) { DeleteCalls++; return Task.CompletedTask; }
        public Task CreateUserAsync(DatabaseConnectionProfile p, string u, string password, string h, CancellationToken t) => Task.CompletedTask;
        public Task DeleteUserAsync(DatabaseConnectionProfile p, string u, string h, CancellationToken t) => Task.CompletedTask;
        public Task SetUserPasswordAsync(DatabaseConnectionProfile p, string u, string password, string h, CancellationToken t) => throw new NotSupportedException();
        public Task GrantAccessAsync(DatabaseConnectionProfile p, string d, string u, string h, IReadOnlyCollection<string> privileges, CancellationToken t) => Task.CompletedTask;
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = call; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
}
