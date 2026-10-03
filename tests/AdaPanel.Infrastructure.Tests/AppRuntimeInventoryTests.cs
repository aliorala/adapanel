using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.AppRuntimes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AppRuntimeInventoryTests
{
    [Theory]
    [InlineData("node", ComponentInstallStatus.Installed, true)]
    [InlineData("python", ComponentInstallStatus.Installed, true)]
    [InlineData("node", ComponentInstallStatus.Failed, false)]
    [InlineData("python", ComponentInstallStatus.Pending, false)]
    public async Task InstalledStateRequiresAnEligibleRecordAndARealExecutable(string runtime, ComponentInstallStatus status, bool expected)
    {
        using var f = new Fixture();
        await f.AddRuntime(runtime, status);
        var before = await f.DatabaseSnapshot();
        var inventory = await f.Inventory().GetAsync(f.Token);
        Assert.Equal(2, inventory.Count);
        Assert.Equal(expected, Assert.Single(inventory, item => item.ComponentId == Id(runtime)).IsInstalled);
        Assert.False(Assert.Single(inventory, item => item.ComponentId != Id(runtime)).IsInstalled);
        Assert.Equal(before, await f.DatabaseSnapshot());
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task ARegisteredMissingExecutableIsMeasuredAbsent(string runtime)
    {
        using var f = new Fixture();
        await f.AddRuntime(runtime, ComponentInstallStatus.Installed, writeFile: false);
        Assert.All(await f.Inventory().GetAsync(f.Token), item => Assert.False(item.IsInstalled));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task ARealFallbackExecutableWorksWithoutWritingAComponentRecord(string runtime)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Fallback(runtime), "not executable: existence fixture only");
        var inventory = await f.Inventory().GetAsync(f.Token);
        Assert.True(Assert.Single(inventory, item => item.ComponentId == Id(runtime)).IsInstalled);
        Assert.Empty(await f.Db.InstalledComponents.ToArrayAsync(f.Token));
        Assert.Equal(f.Fallback(runtime), await AppRuntimeExecutableResolver.ResolveAsync(f.Db, Id(runtime), runtime, f.Token,
            name => [f.Fallback(name)]));
    }

    [Fact]
    public async Task AValidRegisteredExecutableDoesNotEnumerateUnneededEnvironmentFallbacks()
    {
        using var f = new Fixture();
        await f.AddRuntime("node", ComponentInstallStatus.Installed);
        var expected = Path.Combine(f.Root, "registered", "node", "node.exe");
        var resolved = await AppRuntimeExecutableResolver.ResolveAsync(f.Db, "nodejs-lts", "node", f.Token,
            _ => throw new IOException("environment enumeration is unavailable"));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task ADirectoryNamedLikeAnExecutableDoesNotCountAsInstalled()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Fallback("node"));
        Assert.All(await f.Inventory().GetAsync(f.Token), item => Assert.False(item.IsInstalled));
    }

    [Theory]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("path")]
    public async Task FailedMeasurementNeverBecomesANotInstalledResponseOrDisclosesItsPath(string failure)
    {
        using var f = new Fixture();
        Exception error = failure switch
        {
            "access" => new UnauthorizedAccessException("C:\\private\\sentinel password=inventory-secret"),
            "io" => new IOException("C:\\private\\sentinel password=inventory-secret"),
            _ => new ArgumentException("C:\\private\\sentinel password=inventory-secret")
        };
        var measured = f.Inventory(_ => throw error);
        var actual = await Assert.ThrowsAsync<AppException>(() => measured.GetAsync(f.Token));
        Assert.Equal("appruntime.inventory_unavailable", actual.MessageKey);
        Assert.Equal(503, actual.HttpStatusCode);
        Assert.Empty(actual.Details);
        Assert.DoesNotContain("sentinel", actual.Message);
    }

    [Fact]
    public async Task ADatabaseMeasurementFailureDoesNotReturnAnEmptyInventory()
    {
        using var f = new Fixture();
        await f.Db.Database.ExecuteSqlRawAsync("DROP TABLE installed_components", f.Token);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Inventory().GetAsync(f.Token));
        Assert.Equal("appruntime.inventory_unavailable", error.MessageKey);
        Assert.Empty(error.Details);
    }

    [Fact]
    public async Task CanceledMeasurementPreservesCancellationAndDoesNotProbeFiles()
    {
        using var f = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Inventory(_ => { calls++; return true; }).GetAsync(canceled.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Reseller)]
    public async Task AllowedActorsReceiveOnlyTheTwoPublicAvailabilityFields(UserRole role)
    {
        using var f = new Fixture(role);
        await f.AddRuntime("node", ComponentInstallStatus.Installed);
        await f.AddRuntime("mysql", ComponentInstallStatus.Installed);
        var result = await f.Controller(f.Inventory()).GetAsync("owned.example.test", f.Token);
        var response = Assert.IsType<ApiResponse<IReadOnlyCollection<AppRuntimeInventoryDto>>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Success);
        Assert.Equal(new[] { "nodejs-lts", "python-3" }, response.Data!.Select(item => item.ComponentId));
        var json = JsonSerializer.Serialize(response.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
            Assert.Equal(new[] { "componentId", "isInstalled" }, entry.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("inventory-secret", json);
        Assert.DoesNotContain(f.Root, json);
        Assert.DoesNotContain("mysql", json);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    [InlineData("scope")]
    public async Task InitiallyInvalidActorsOrForeignSitesCannotReadInventory(string change)
    {
        using var f = new Fixture();
        await f.Change(change);
        var inventory = new ControlledInventory();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller(inventory).GetAsync("owned.example.test", f.Token));
        Assert.Equal(change == "scope" ? "auth.site_forbidden" : "auth.unauthorized", error.MessageKey);
        Assert.Equal(0, inventory.Calls);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    [InlineData("scope")]
    public async Task AuthorityChangedDuringMeasurementCannotPublishTheResponse(string change)
    {
        using var f = new Fixture();
        var inventory = new ControlledInventory { Paused = true };
        var action = f.Controller(inventory).GetAsync("owned.example.test", f.Token);
        try
        {
            await Task.WhenAny(action, inventory.Entered.Task).WaitAsync(f.Token);
            if (action.IsCompleted) await action; // a fail-closed stub fails immediately, never hangs the baseline
            await f.Change(change);
            inventory.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<AppException>(() => action.WaitAsync(f.Token));
            Assert.Equal(change == "scope" ? "auth.site_forbidden" : "auth.unauthorized", error.MessageKey);
        }
        finally { inventory.Release.TrySetResult(); }
    }

    [Fact]
    public async Task InventoryMeasurementFailureAndCancellationAreNotConvertedIntoSuccess()
    {
        using var f = new Fixture();
        var original = new AppException("appruntime.inventory_unavailable", 503, 503);
        var failing = new ControlledInventory { Failure = original };
        Assert.Same(original, await Assert.ThrowsAsync<AppException>(() => f.Controller(failing).GetAsync("owned.example.test", f.Token)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Controller(new ControlledInventory()).GetAsync("owned.example.test", canceled.Token));
    }

    [Fact]
    public void InventoryIsReadOnlyAndDoesNotBroadenSoftwareAdministrationAuthorization()
    {
        Assert.Equal("Administrator,Reseller", typeof(AppRuntimeInventoryController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.Equal("api/v1/sites/{siteName}/apps/runtimes", typeof(AppRuntimeInventoryController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(typeof(AppRuntimeInventoryController).GetMethod("GetAsync")!.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("Administrator", typeof(SoftwareController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);
    }

    private static string Id(string runtime) => runtime switch { "node" => "nodejs-lts", "python" => "python-3", _ => "mysql-8" };

    private sealed class ControlledInventory : IAppRuntimeInventoryService
    {
        internal int Calls;
        internal bool Paused;
        internal Exception? Failure;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyCollection<AppRuntimeInventoryDto>> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Paused) await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            return [new() { ComponentId = "nodejs-lts", IsInstalled = true }, new() { ComponentId = "python-3" }];
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly Guid actor = Guid.NewGuid();
        private readonly UserRole role;
        private bool scopeAllowed = true;
        internal readonly AdaPanelDbContext Db;
        internal string Root => area.Kok;
        internal CancellationToken Token => area.Iptal;
        internal Fixture(UserRole role = UserRole.Reseller)
        {
            this.role = role;
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            Db.Users.Add(new PanelUser { Id = actor, UserName = "inventory-user", Email = "fixture@example.test",
                Role = role, IsActive = true, PasswordHash = "original-hash" });
            Db.SaveChanges();
            Directory.CreateDirectory(Path.Combine(Root, "fallback"));
        }
        internal string Fallback(string runtime) => Path.Combine(Root, "fallback", runtime + ".exe");
        internal AppRuntimeInventoryService Inventory(Func<string, bool>? probe = null) => new(Db)
        { AdditionalCandidates = runtime => [Fallback(runtime)], Probe = probe };

        internal async Task AddRuntime(string runtime, ComponentInstallStatus status, bool writeFile = true)
        {
            var path = Path.Combine(Root, "registered", runtime);
            Directory.CreateDirectory(path);
            if (writeFile) File.WriteAllText(Path.Combine(path, runtime + ".exe"), "never executed");
            Db.InstalledComponents.Add(new InstalledComponent { ComponentId = Id(runtime), Status = status, InstallDir = path,
                Type = ComponentType.Zip, ProtectedConfigurationJson = "inventory-secret", LastError = "private diagnostic", ServiceName = "private-service" });
            await Db.SaveChangesAsync(Token);
        }
        internal async Task<string> DatabaseSnapshot() => JsonSerializer.Serialize(await Db.InstalledComponents.AsNoTracking().ToArrayAsync(Token));
        internal AppRuntimeInventoryController Controller(IAppRuntimeInventoryService inventory)
        {
            var scope = DispatchProxy.Create<ICustomerScopeService, ScopeProxy>();
            ((ScopeProxy)(object)scope).Sites = () => scopeAllowed ? ["owned.example.test"] : [];
            return new AppRuntimeInventoryController(inventory, scope, Db)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original-hash"))], "test")) } }
            };
        }
        internal async Task Change(string change)
        {
            if (change == "scope") { scopeAllowed = false; return; }
            var user = await Db.Users.SingleAsync(item => item.Id == actor, Token);
            if (change == "disabled") user.IsActive = false;
            if (change == "deleted") Db.Users.Remove(user);
            if (change == "role") user.Role = UserRole.Customer;
            if (change == "password") user.PasswordHash = "changed-hash";
            await Db.SaveChangesAsync(Token);
        }
        public void Dispose() { Db.Dispose(); connection.Dispose(); area.Dispose(); }
    }

    public class ScopeProxy : DispatchProxy
    {
        internal Func<IReadOnlyCollection<string>> Sites = () => [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "GetResellerSitesAsync" => Task.FromResult(Sites()),
            "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(null),
            _ => throw new InvalidOperationException("Unexpected scope method: " + targetMethod.Name)
        };
    }
}
