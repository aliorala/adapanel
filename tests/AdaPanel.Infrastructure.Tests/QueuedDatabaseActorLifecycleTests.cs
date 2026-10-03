using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Real controller, database/user services, SQLite account store and JSON scope/ownership stores.
// Only database engine and unrelated OS integrations are replaced; no external service is contacted.
public sealed partial class QueuedDatabaseActorLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("deleted")]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("parent_deleted")]
    [InlineData("parent_disabled")]
    [InlineData("parent_role")]
    [InlineData("reparented")]
    [InlineData("detached")]
    [InlineData("section")]
    [InlineData("password")]
    public async Task QueuedCreateRemeasuresActorAndOriginalSubscriptionBeforeProvider(string change)
    {
        using var fixture = new Fixture();
        var child = change.StartsWith("parent_") || change is "reparented" or "detached" or "section";
        var actor = child ? fixture.Child : fixture.Customer;
        var controller = fixture.Controller(actor);
        // Track the original row, just as earlier request services may have done. The gate must
        // reread persistent account state rather than trusting EF's already tracked instance.
        await fixture.ControllerContext.Users.SingleAsync(user => user.Id == actor);
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterSubscriptionAsync(fixture.Customer, cancellation.Token);
        var creating = Capture(() => controller.CreateAsync(fixture.Request(), cancellation.Token));
        try
        {
            await WaitForReferences(fixture.Customer, 2, creating, cancellation.Token);
            await fixture.Change(change, actor);
        }
        finally { lease.Dispose(); }
        var error = await creating.WaitAsync(Deadline);
        Assert.Contains(Assert.IsType<AppException>(error).MessageKey,
            new[] { "auth.unauthorized", "auth.forbidden", "auth.section_forbidden" });
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Empty(await fixture.Ownership.GetAllAsync());
    }

    [Fact]
    public async Task RealSubscriptionDeletionRejectsAlreadyQueuedCreate()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.BeforeSchedulerRead = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var deleting = fixture.Users().DeleteUserAsync(fixture.Customer, fixture.Admin, false, null, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        var creating = Capture(() => fixture.Controller(fixture.Customer)
            .CreateAsync(fixture.Request(), cancellation.Token));
        try { await WaitForReferences(fixture.Customer, 2, creating, cancellation.Token); }
        finally { release.TrySetResult(); }
        await deleting.WaitAsync(Deadline);
        Assert.Contains(Assert.IsType<AppException>(await creating.WaitAsync(Deadline)).MessageKey,
            new[] { "auth.unauthorized", "auth.forbidden", "auth.section_forbidden" });
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Empty(await fixture.Ownership.GetAllAsync());
    }

    [Theory]
    [InlineData("disable_parent")]
    [InlineData("disable_child")]
    [InlineData("delete_child")]
    [InlineData("admin_disable_child")]
    [InlineData("admin_delete_child")]
    [InlineData("delete_parent")]
    public async Task AccountMutationsWaitUntilAuthorizedCreateFinishes(string operation)
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        fixture.Provider.BeforeCreate = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(Deadline);
        var creating = fixture.Controller(fixture.Child).CreateAsync(fixture.Request(), cancellation.Token);
        if (await Task.WhenAny(entered.Task, creating).WaitAsync(Deadline) == creating) await creating;
        await entered.Task.WaitAsync(Deadline);
        var changing = Capture(() => fixture.Mutate(operation, cancellation.Token));
        bool waited;
        bool actorStillActive;
        try
        {
            await WaitUntil(() => References(fixture.Customer) >= 2 || changing.IsCompleted, cancellation.Token);
            waited = !changing.IsCompleted;
            // Waiting must precede modifying the persistent actor row.
            using var observer = fixture.Context();
            actorStillActive = await observer.Users.AnyAsync(user => user.Id == fixture.Child && user.IsActive);
        }
        finally { release.TrySetResult(); }
        await creating.WaitAsync(Deadline);
        Assert.Null(await changing.WaitAsync(Deadline));
        Assert.True(actorStillActive, "Account state changed during an authorized provider mutation.");
        Assert.True(waited, "Account deletion/disable bypassed the subscription lease held by a provider mutation.");
        Assert.Equal(1, fixture.Provider.CreateCalls);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Administrator")]
    [InlineData("SubUser")]
    public async Task ActiveActorCanCreateAndOwnershipUsesCurrentSubscription(string kind)
    {
        using var fixture = new Fixture();
        var actor = kind switch { "SubUser" => fixture.Child, "Administrator" => fixture.Admin, "Reseller" => fixture.Reseller, _ => fixture.Customer };
        await fixture.Controller(actor).CreateAsync(fixture.Request(), default);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Equal(kind == "SubUser" ? fixture.Customer : actor, await fixture.Ownership.GetOwnerAsync("mysql", fixture.Name));
    }

    [Fact]
    public async Task DifferentSubscriptionMutationDoesNotWaitForUnrelatedCreate()
    {
        using var fixture = new Fixture();
        using var held = await DatabaseLifecycle.EnterSubscriptionAsync(fixture.Customer);
        await fixture.Users().UpdateUserAsync(fixture.OtherCustomer, fixture.Update(fixture.OtherCustomer), fixture.Admin, null).WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActorIsRemeasuredAfterWaitingForDatabaseResource(bool siteProvisioning)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var actor = siteProvisioning ? fixture.Admin : fixture.Customer;
        var lease = await DatabaseLifecycle.EnterResourceAsync("mysql", fixture.Name, cancellation.Token);
        var creating = Capture(async () =>
        {
            if (siteProvisioning) await fixture.CreateSite(actor, cancellation.Token);
            else await fixture.Controller(actor).CreateAsync(fixture.Request(), cancellation.Token);
        });
        try
        {
            await WaitUntil(() => GateReferences("Resources", DatabaseResourceIdentity.Key("mysql", fixture.Name)) >= 2 || creating.IsCompleted, cancellation.Token);
            Assert.False(creating.IsCompleted);
            await fixture.Change("disabled", actor);
        }
        finally { lease.Dispose(); }
        var error = await creating.WaitAsync(Deadline);
        if (!siteProvisioning) Assert.Equal("auth.unauthorized", Assert.IsType<AppException>(error).MessageKey);
        else Assert.Null(error); // Site creation keeps its documented partial-result response.
        Assert.Equal(0, fixture.Provider.CreateCalls);
    }

    [Fact]
    public async Task SiteProvisioningCannotCreateDatabaseForActorDisabledWhileQueued()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        var lease = await DatabaseLifecycle.EnterSubscriptionAsync(fixture.Admin, cancellation.Token);
        var creating = fixture.CreateSite(fixture.Admin, cancellation.Token);
        try
        {
            await WaitForReferences(fixture.Admin, 2, creating, cancellation.Token);
            await fixture.Change("disabled", fixture.Admin);
        }
        finally { lease.Dispose(); }
        var result = await creating.WaitAsync(Deadline);
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.NotEmpty(result.Warnings);
        Assert.DoesNotContain(result.Created, item => item.StartsWith("mysql:"));
    }

    [Fact]
    public async Task ValidSiteProvisioningStillCreatesAndLinksDatabase()
    {
        using var fixture = new Fixture();
        var result = await fixture.CreateSite(fixture.Admin, default);
        Assert.Empty(result.Warnings);
        Assert.Contains("mysql:" + fixture.Name, result.Created);
        Assert.Equal(1, fixture.Provider.CreateCalls);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    // Observe the existing gate instead of sleeps or a production-only scheduling seam.
    private static int References(Guid id)
        => GateReferences("Subscriptions", id.ToString("N"));
    private static int GateReferences(string field, string key)
    {
        var gates = typeof(DatabaseLifecycle).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (int)gates.GetType().GetMethod("References", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gates, [key])!;
    }
    private static Task WaitUntil(Func<bool> condition, CancellationToken token) => Task.Run(async () =>
    {
        while (!condition()) await Task.Delay(1, token);
    }, token);
    private static async Task WaitForReferences(Guid id, int count, Task operation, CancellationToken token)
    {
        await WaitUntil(() => References(id) >= count || operation.IsCompleted, token);
        Assert.False(operation.IsCompleted, "Request did not reach the expected subscription wait.");
    }

    private sealed partial class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.QueuedActor." + Guid.NewGuid().ToString("N"));
        // Keep the shared in-memory database alive, but give every DbContext its own
        // connection, just as separate HTTP requests have in production. Sharing one
        // live connection races EF's function registration with another active reader.
        private readonly SqliteConnection connection = new($"Data Source=Actor-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly JsonFileStore files;
        private readonly IIisSiteService sites = Fake<IIisSiteService>((method, _) => throw new NotSupportedException(method.Name));
        public Guid Admin { get; } = Guid.NewGuid();
        public Guid Customer { get; } = Guid.NewGuid();
        public Guid OtherCustomer { get; } = Guid.NewGuid();
        public Guid Child { get; } = Guid.NewGuid();
        public Guid Reseller { get; } = Guid.NewGuid();
        public string Name { get; } = "queued_" + Guid.NewGuid().ToString("N");
        public Provider Provider { get; } = new();
        public CustomerScopeService Scopes { get; }
        public DatabaseOwnershipService Ownership { get; }
        public DatabaseCredentialStore Credentials => new(files, new Protector());
        public AdaPanelDbContext ControllerContext { get; private set; } = null!;
        public Func<Task>? BeforeSchedulerRead { get; set; }

        public Fixture()
        {
            connection.Open();
            var db = Context();
            db.Database.EnsureCreated();
            foreach (var (id, role) in new[] { (Admin, UserRole.Administrator), (Customer, UserRole.Customer), (Child, UserRole.Customer), (OtherCustomer, UserRole.Customer), (Reseller, UserRole.Reseller) })
                db.Users.Add(new PanelUser { Id = id, UserName = id.ToString("N"), Email = Email(id), Role = role, PasswordHash = "fixture-hash", IsActive = true });
            db.InstalledComponents.Add(new InstalledComponent { ComponentId = DatabaseProfiles.ResolveDescriptor(DbEngine.MySql).ComponentId, Status = ComponentInstallStatus.Installed,
                ProtectedConfigurationJson = JsonSerializer.Serialize(new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = "fixture-admin" }) });
            db.SaveChanges();
            files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Scopes = new CustomerScopeService(files, Fake<IServicePlanService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            Ownership = new DatabaseOwnershipService(files);
            Scopes.SetScopeAsync(Customer, [], 0, 0).GetAwaiter().GetResult();
            Scopes.SetParentSubscriptionAsync(Child, Customer).GetAwaiter().GetResult();
        }

        public AdaPanelDbContext Context()
        {
            var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection.ConnectionString).Options);
            contexts.Add(db); return db;
        }
        public CreateDatabaseRequest Request() => new() { Engine = "mysql", Name = Name };
        private static string Email(Guid id) => id.ToString("N") + "@example.test";
        public UpdateUserRequest Update(Guid id) => new() { Email = Email(id), Role = "Customer", IsActive = false };
        public ResellerDatabasesController Controller(Guid actor)
        {
            ControllerContext = Context();
            var protector = new Protector();
            var database = new DatabaseService(Context(), protector, new DatabaseCredentialStore(files, protector), new SiteDatabaseLinkService(files), [Provider]);
            using var container = new ServiceCollection().AddSingleton<IDatabaseService>(database)
                .AddSingleton<IDatabaseOwnershipService>(Ownership).AddSingleton<ICustomerScopeService>(Scopes)
                .AddSingleton<IAuditService>(new AuditService(ControllerContext, Scopes, files)).AddSingleton(ControllerContext).BuildServiceProvider();
            var result = ActivatorUtilities.CreateInstance<ResellerDatabasesController>(container);
            var role = ControllerContext.Users.AsNoTracking().Single(user => user.Id == actor).Role.ToString();
            result.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role)], "test")) } };
            return result;
        }
        public async Task<SiteCreationResultDto> CreateSite(Guid actor, CancellationToken token)
        {
            var principal = Controller(actor).User;
            var protector = new Protector();
            var database = new DatabaseService(Context(), protector, new DatabaseCredentialStore(files, protector), new SiteDatabaseLinkService(files), [Provider]);
            var gates = new ConcurrencyLock();
            SiteDto? currentSite = null;
            const string siteName = "fixture-site";
            var controller = new SitesController(
                Fake<IIisSiteService>((m, _) => m.Name switch
                {
                    "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>(currentSite is null ? [] : [currentSite]),
                    "GetSiteAsync" => Task.FromResult(currentSite ?? throw new AppException("sites.not_found", 404, 404)),
                    _ => throw new NotSupportedException(m.Name)
                }),
                Fake<ISiteProvisioningService>((_, _) => SiteMutationGuard.RunMutationAsync(gates, siteName, true,
                    () => Task.FromResult(currentSite = new SiteDto { Name = siteName }), token)),
                null!, null!, Scopes, Ownership, null!, null!, null!, new AuditService(Context(), Scopes, files), gates, null!, NullLogger<SitesController>.Instance)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } } };
            var request = new CreateSiteRequest { Name = "fixture-site", PhysicalPath = "", ProvisionDefaultServices = false,
                CreateDatabase = true, DatabaseEngine = "mysql", DatabaseName = Name };
            var bootstrapper = Fake<IComponentBootstrapper>((_, _) => Task.FromResult(new SoftwareCatalogDto()));
            // Accept the forthcoming mandatory action dependency without changing baseline production.
            var action = typeof(SitesController).GetMethod(nameof(SitesController.CreateSiteAsync))!;
            var args = action.GetParameters().Select(parameter => parameter.ParameterType == typeof(CreateSiteRequest) ? (object)request
                : parameter.ParameterType == typeof(IDatabaseService) ? database
                : parameter.ParameterType == typeof(IComponentBootstrapper) ? bootstrapper
                : parameter.ParameterType == typeof(AdaPanelDbContext) ? Context()
                : parameter.ParameterType == typeof(CancellationToken) ? token : null).ToArray();
            var response = await (Task<ActionResult<ApiResponse<SiteCreationResultDto>>>)action.Invoke(controller, args)!;
            return Assert.IsType<ApiResponse<SiteCreationResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }
        public UserManagementService Users()
        {
            var db = Context();
            return new UserManagementService(db, new PasswordHasher<PanelUser>(), Scopes,
                Fake<INotificationService>((m, _) => throw new NotSupportedException(m.Name)), Fake<IPanelPreferencesService>((m, _) => throw new NotSupportedException(m.Name)),
                Fake<IPanelSecurityService>((m, _) => throw new NotSupportedException(m.Name)), NullLogger<UserManagementService>.Instance,
                new AuditService(db, Scopes, files), sites, Ownership,
                Fake<ISchedulerService>((m, _) => m.Name == "GetTasksAsync" ? Tasks() : throw new NotSupportedException(m.Name)),
                Fake<ISiteCascadeDeletionService>((m, _) => throw new NotSupportedException(m.Name)), Fake<IApiTokenService>((m, _) => throw new NotSupportedException(m.Name)),
                Fake<IServerOwnerService>((m, _) => m.Name == "IsServerOwnerAsync" ? Task.FromResult(false) : throw new NotSupportedException(m.Name)));
        }
        private async Task<IReadOnlyCollection<ScheduledTaskDto>> Tasks()
        {
            if (BeforeSchedulerRead is not null) await BeforeSchedulerRead();
            return [];
        }
        public async Task Change(string change, Guid actor)
        {
            if (change == "reparented") { await Scopes.SetParentSubscriptionAsync(actor, OtherCustomer); return; }
            if (change == "detached") { await Scopes.SetParentSubscriptionAsync(actor, null); return; }
            if (change == "section") { await Scopes.SetSubUserSectionsAsync(actor, ["files"]); return; }
            using var db = Context();
            var user = await db.Users.SingleAsync(user => user.Id == (change.StartsWith("parent_") ? Customer : actor));
            if (change.EndsWith("deleted")) db.Users.Remove(user);
            if (change.EndsWith("disabled")) user.IsActive = false;
            if (change.EndsWith("role")) user.Role = UserRole.Operator;
            if (change == "password") user.PasswordHash = "changed-hash";
            await db.SaveChangesAsync();
        }
        public Task Mutate(string operation, CancellationToken token) => operation switch
        {
            "disable_parent" => Users().UpdateUserAsync(Customer, Update(Customer), Admin, null, token),
            "admin_disable_child" => Users().UpdateUserAsync(Child, Update(Child), Admin, null, token),
            "delete_parent" => Users().DeleteUserAsync(Customer, Admin, false, null, token),
            "admin_delete_child" => Users().DeleteUserAsync(Child, Admin, false, null, token),
            "disable_child" => Users().UpdateSubUserAsync(Customer, Child, new UpdateSubUserRequest { Email = Email(Child), IsActive = false }, null, token),
            "delete_child" => Users().DeleteSubUserAsync(Customer, Child, null, token),
            _ => throw new InvalidOperationException(operation)
        };
        public void Dispose()
        {
            foreach (var db in contexts) db.Dispose();
            connection.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
    private sealed class Provider : IDatabaseEngineProvider
    {
        public DbEngine Engine => DbEngine.MySql;
        public int CreateCalls;
        public int DeleteCalls;
        public Func<CancellationToken, Task>? BeforeCreate;
        public Func<CancellationToken, Task>? BeforeDelete;
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken token) => Task.FromResult(new DatabaseWorkspaceDto());
        public async Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token)
        { Interlocked.Increment(ref CreateCalls); if (BeforeCreate is not null) await BeforeCreate(token); }
        public async Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string name, CancellationToken token)
        { Interlocked.Increment(ref DeleteCalls); if (BeforeDelete is not null) await BeforeDelete(token); }
        public Task CreateUserAsync(DatabaseConnectionProfile p, string u, string password, string h, CancellationToken t) => Task.CompletedTask;
        public Task SetUserPasswordAsync(DatabaseConnectionProfile p, string u, string password, string h, CancellationToken t) => throw new NotSupportedException();
        public Task DeleteUserAsync(DatabaseConnectionProfile p, string u, string h, CancellationToken t) => throw new NotSupportedException();
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
