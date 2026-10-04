using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Actual FTP/controller reads, SQLite actors and temp JSON; native transports are instance fakes.</summary>
public sealed class FtpReadLifecycleTests
{
    [Theory]
    [InlineData("users", "disabled")]
    [InlineData("users", "password")]
    [InlineData("users", "logout")]
    [InlineData("users", "role")]
    [InlineData("status", "disabled")]
    [InlineData("status", "password")]
    [InlineData("status", "logout")]
    [InlineData("status", "role")]
    public async Task AReadCannotDiscloseItsResultAfterTheAcceptedActorChangesDuringNativeIO(string route, string change)
    {
        using var f = await Fixture.Create(route, pause: true);
        var files = f.ConfigBytes();
        var action = f.Read();
        await f.WaitForNative(action);
        try { await f.ChangeActor(change); }
        finally { f.Resume(); }
        Assert.Equal(401, Assert.IsType<AppException>(await Record.ExceptionAsync(() => action)).HttpStatusCode);
        Assert.Equal(files, f.ConfigBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("password")]
    [InlineData("logout")]
    public async Task ADelegatedResellersListStillRequiresItsSourceAdministratorAfterTheNativeWait(string change)
    {
        using var f = await Fixture.Create("users", pause: true, delegated: true);
        var action = f.Read();
        await f.WaitForNative(action);
        try { await f.ChangeActor(change, delegating: true); }
        finally { f.Resume(); }
        Assert.Equal(401, Assert.IsType<AppException>(await Record.ExceptionAsync(() => action)).HttpStatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("password")]
    [InlineData("logout")]
    public async Task EvenTheSavedTlsPreferenceReadRequiresACurrentAdministrator(string change)
    {
        using var f = await Fixture.Create("tls");
        await f.ChangeActor(change);
        Assert.Equal(401, Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Read())).HttpStatusCode);
        Assert.Equal(0, f.NativeCalls);
    }

    [Theory]
    [InlineData("users")]
    [InlineData("status")]
    public async Task NativeCompletionDoesNotOverrideAnAbortedRead(string route)
    {
        using var f = await Fixture.Create(route, pause: true, role: "Administrator");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = f.Read(caller.Token);
        await f.WaitForNative(action);
        caller.Cancel();
        f.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
    }

    [Fact]
    public async Task ReplacingTheHttpPrincipalCannotWidenTheOriginallyAcceptedResellerScope()
    {
        using var f = await Fixture.Create("users", pause: true);
        var action = f.Read();
        await f.WaitForNative(action);
        f.ReplacePrincipalWithAdministrator();
        f.Resume();
        var users = Data<IReadOnlyCollection<FtpUserDto>>(await action);
        Assert.Equal(Fixture.SiteName, Assert.Single(users).SiteName);
    }

    [Fact]
    public async Task AResellerLosingItsCustomerDuringNativeIOReceivesNoFormerCustomerAccounts()
    {
        using var f = await Fixture.Create("users", pause: true);
        var action = f.Read();
        await f.WaitForNative(action);
        try { await f.Scopes.SetOwnerAsync(f.Owner, f.Other, f.Token); }
        finally { f.Resume(); }
        Assert.Empty(Data<IReadOnlyCollection<FtpUserDto>>(await action));
    }

    [Theory]
    [InlineData("users", "Reseller", false, false)]
    [InlineData("users", "Administrator", false, false)]
    [InlineData("users", "Reseller", true, false)]
    [InlineData("users", "Reseller", false, true)]
    [InlineData("status", "Administrator", false, false)]
    [InlineData("tls", "Administrator", false, false)]
    public async Task CurrentCookieDelegationAndTokenReadersKeepTheirNormalReadOnlyPaths(string route, string role, bool delegated, bool token)
    {
        using var f = await Fixture.Create(route, role: role, delegated: delegated, panelToken: token);
        var files = f.ConfigBytes();
        var result = await f.Read();
        if (route == "users") Assert.Equal(role == "Administrator" ? 2 : 1, Data<IReadOnlyCollection<FtpUserDto>>(result).Count);
        if (route == "status") Assert.Equal("ftp.server_ready", Data<FtpServerStatusDto>(result).StatusMessage);
        if (route == "tls") { var data = Data<FtpTlsStatusDto>(result); Assert.True(data.RequireSsl); Assert.Null(data.Verified); }
        Assert.Equal(files, f.ConfigBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("port-unmeasured")]
    [InlineData("probe-failed")]
    [InlineData("malformed")]
    [InlineData("empty-object")]
    [InlineData("listeners-unmeasured")]
    [InlineData("site-unmeasured")]
    [InlineData("service-unmeasured")]
    [InlineData("invalid-pid")]
    [InlineData("invalid-services")]
    [InlineData("invalid-service-pids")]
    [InlineData("array-root")]
    public async Task IncompleteMeasurementsCannotAdvertiseTheFtpServerAsReady(string measurement)
    {
        using var f = await Fixture.Create("status");
        f.Measurement = measurement;
        var result = await f.Service.GetServerStatusAsync(f.Token);
        Assert.False(result.IsConfigured);
        Assert.Equal("ftp.server_status_unknown", result.StatusMessage);
    }

    [Theory]
    [InlineData("healthy", "ftp.server_ready", true)]
    [InlineData("singleton", "ftp.server_ready", true)]
    [InlineData("stopped", "ftp.service_not_running", false)]
    [InlineData("site-missing", "ftp.site_not_configured", false)]
    [InlineData("not-listening", "ftp.server_not_listening", false)]
    [InlineData("conflict", "ftp.control_port_conflict", false)]
    public async Task MeasuredHealthyAndFaultStatesRemainDistinct(string measurement, string expected, bool ready)
    {
        using var f = await Fixture.Create("status");
        f.Measurement = measurement;
        var result = await f.Service.GetServerStatusAsync(f.Token);
        Assert.Equal(ready, result.IsConfigured);
        Assert.Equal(expected, result.StatusMessage);
    }

    private static T Data<T>(object result) => Assert.IsType<ApiResponse<T>>(result).Data!;

    private sealed class Fixture : IDisposable
    {
        internal const string SiteName = "read-owned.test";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-read-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string sessionId = SessionRevocationStore.NewSessionId(), sourceSessionId = SessionRevocationStore.NewSessionId();
        private readonly Guid actor = Guid.NewGuid(), source = Guid.NewGuid();
        internal readonly Guid Owner = Guid.NewGuid(), Other = Guid.NewGuid();
        private readonly string route;
        private readonly bool pause;
        private readonly ServiceProvider services;
        private readonly JsonFileStore store;
        private readonly SessionRevocationStore sessions;
        private readonly FtpController controller;
        internal AdaPanelDbContext Db { get; }
        internal CustomerScopeService Scopes { get; }
        internal FtpService Service { get; }
        internal CancellationToken Token => lifetime.Token;
        internal int NativeCalls;
        internal string Measurement = "healthy";

        private Fixture(string route, string role, bool pause, bool delegated, bool panelToken)
        {
            this.route = route; this.pause = pause;
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            foreach (var id in new[] { actor, source, Owner, Other }) Db.Users.Add(new PanelUser
            {
                Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                PasswordHash = "accepted-read-hash", Role = id == actor ? Enum.Parse<UserRole>(role) : id == source ? UserRole.Administrator :
                    id == Owner ? UserRole.Customer : UserRole.Reseller
            });
            Db.SaveChanges();
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync" ? Task.FromResult<IReadOnlyCollection<SiteDto>>([])
                : throw new NotSupportedException(method.Name));
            Scopes = new CustomerScopeService(store, new ServicePlanService(store),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null)
                    : throw new NotSupportedException(method.Name)), sites);
            Service = new FtpService(sites, store,
                Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync" ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" })
                    : throw new NotSupportedException(method.Name)),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }))
            { NativeCommand = NativeCommand, NativeScript = NativeScript };
            sessions = new SessionRevocationStore(store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(sessions).AddSingleton<IConcurrencyLock, ConcurrencyLock>()
                .AddSingleton<IFtpService>(Service).AddSingleton<ICustomerScopeService>(Scopes).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IAuditService>(new AuditService(Db, Scopes, store)).BuildServiceProvider();
            controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()), new(ClaimTypes.Role, role) };
            if (!panelToken)
            {
                claims.Add(new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-read-hash")));
                claims.Add(new(SessionRevocationStore.SessionIdClaim, sessionId));
                if (delegated)
                {
                    claims.Add(new("impersonator_id", source.ToString())); claims.Add(new("impersonator_name", source.ToString("N")));
                    claims.Add(new(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-read-hash")));
                    claims.Add(new(CookieSessionValidator.DelegatingSessionIdClaim, sourceSessionId));
                }
            }
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, panelToken ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)) } };
        }

        internal static async Task<Fixture> Create(string route, bool pause = false, string? role = null, bool delegated = false, bool panelToken = false)
        {
            var f = new Fixture(route, role ?? (route == "users" ? "Reseller" : "Administrator"), pause, delegated, panelToken);
            await f.Scopes.SetScopeAsync(f.Owner, [SiteName], 0, 0, f.Token);
            await f.Scopes.SetOwnerAsync(f.Owner, f.actor, f.Token);
            await f.store.WriteAsync("config/ftp-users.json", new[]
            {
                new { userName = "account_owned", siteName = SiteName, homePath = Path.Combine(f.root, "owned"), isEnabled = true, createdAtUtc = DateTime.UtcNow },
                new { userName = "account_foreign", siteName = "foreign.test", homePath = Path.Combine(f.root, "foreign"), isEnabled = true, createdAtUtc = DateTime.UtcNow }
            }, f.Token);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            await f.store.WriteAsync("config/ftp-tls.json", new { requireSsl = true }, f.Token);
            return f;
        }

        internal async Task<object> Read(CancellationToken? token = null)
        {
            var ct = token ?? Token;
            IActionResult? result = route switch
            {
                "users" => (await controller.GetUsersAsync(ct)).Result,
                "status" => (await controller.GetStatusAsync(ct)).Result,
                "tls" => (await controller.GetTlsAsync(ct)).Result,
                _ => throw new InvalidOperationException(route)
            };
            return Assert.IsType<OkObjectResult>(result).Value!;
        }
        internal string ConfigBytes() => string.Join("\n", new[] { "ftp-users.json", "ftp-infra.json", "ftp-tls.json" }
            .Select(name => File.ReadAllText(store.ResolvePath("config/" + name))));
        internal Task ChangeActor(string change, bool delegating = false)
        {
            var id = delegating ? source : actor;
            return change switch
            {
                "disabled" => Db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false), Token),
                "password" => Db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "changed-read-hash"), Token),
                "role" => Db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, UserRole.Customer), Token),
                "logout" => sessions.RevokeAsync(delegating ? sourceSessionId : sessionId, Token),
                _ => throw new InvalidOperationException(change)
            };
        }
        internal void ReplacePrincipalWithAdministrator() => controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, source.ToString()), new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-read-hash")),
                new Claim(SessionRevocationStore.SessionIdClaim, sourceSessionId)], CookieAuthenticationDefaults.AuthenticationScheme));
        internal async Task WaitForNative(Task action)
        {
            if (await Task.WhenAny(entered.Task, action).WaitAsync(Token) == action)
            { await action; throw new InvalidOperationException("FTP read ended before the expected native boundary."); }
            Assert.False(action.IsCompleted);
        }
        internal void Resume() => release.TrySetResult();
        private async Task WaitNative()
        {
            if (Interlocked.Increment(ref NativeCalls) == 1 && pause)
            { entered.TrySetResult(); await release.Task.WaitAsync(Token); }
        }
        private async Task<CommandResult> NativeCommand(string executable, string arguments, CancellationToken token)
        {
            Assert.Equal("net.exe", executable);
            Assert.StartsWith("user \"account_", arguments);
            Assert.DoesNotContain("/", arguments);
            await WaitNative();
            return new CommandResult { ExitCode = 0 };
        }
        private async Task<CommandResult> NativeScript(string script, bool strict, CancellationToken token)
        {
            Assert.Contains("Get-NetTCPConnection", script);
            Assert.DoesNotContain("Set-ItemProperty", script);
            await WaitNative();
            if (Measurement == "probe-failed") return new CommandResult { ExitCode = 1 };
            if (Measurement is "malformed" or "empty-object" or "array-root") return new CommandResult { ExitCode = 0,
                StandardOutput = Measurement == "malformed" ? "not-json" : Measurement == "array-root" ? "[]" : "{}" };
            var values = new Dictionary<string, object?>
            {
                ["siteMeasured"] = Measurement != "site-unmeasured", ["serviceMeasured"] = Measurement != "service-unmeasured",
                ["siteExists"] = Measurement != "site-missing", ["siteState"] = "Started",
                ["serviceState"] = Measurement == "stopped" ? "Stopped" : "Running", ["servicePids"] = new[] { 100 },
                ["portMeasured"] = Measurement != "port-unmeasured",
                ["listeners"] = Measurement == "not-listening" ? Array.Empty<object>() :
                    new object[] { new { pid = Measurement == "conflict" ? 200 : 100, process = "fixture", services = new[] { Measurement == "conflict" ? "foreign" : "FTPSVC" } } }
            };
            if (Measurement == "listeners-unmeasured") values.Remove("listeners");
            if (Measurement == "invalid-pid") values["listeners"] = new[] { new { pid = "unmeasured", process = "fixture", services = new[] { "FTPSVC" } } };
            if (Measurement == "invalid-services") values["listeners"] = new[] { new { pid = 100, process = "fixture", services = new[] { 42 } } };
            if (Measurement == "invalid-service-pids") values["servicePids"] = new[] { "unmeasured" };
            if (Measurement == "singleton")
            {
                values["servicePids"] = 100;
                values["listeners"] = new { pid = 100, process = "fixture", services = "FTPSVC" };
            }
            return new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(values) };
        }
        public void Dispose()
        {
            Resume(); lifetime.Cancel(); services.Dispose(); Db.Dispose(); connection.Dispose(); lifetime.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Action = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []);
    }
}
