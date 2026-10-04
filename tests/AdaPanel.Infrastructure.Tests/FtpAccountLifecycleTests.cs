using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
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

/// <summary>Real controller/service, SQLite actors and JSON ownership; every native command is fake.</summary>
public sealed class FtpAccountLifecycleTests
{
    [Theory]
    [InlineData("create", "disabled")]
    [InlineData("create", "password")]
    [InlineData("create", "logout")]
    [InlineData("password", "disabled")]
    [InlineData("password", "password")]
    [InlineData("password", "logout")]
    [InlineData("delete", "disabled")]
    [InlineData("delete", "password")]
    [InlineData("delete", "logout")]
    public async Task RevokedActorsCannotMutateAccountsAfterTheNativeLookupWait(string route, string change)
    {
        using var f = await Fixture.Create(route);
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForLookup(action);
        try { await f.ChangeActor(change); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task AResellerLosingTheSubscriptionCannotUseTheEarlierSiteApproval(string route)
    {
        using var f = await Fixture.Create(route);
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForLookup(action);
        try { await f.Scopes.SetOwnerAsync(f.Owner, f.Other, f.Token); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(403, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task AChangedAccountRecordIsNotThePreviouslyAuthorizedTarget(string route)
    {
        using var f = await Fixture.Create(route);
        var action = f.Run();
        await f.WaitForLookup(action);
        byte[] replacement;
        try { await f.ReplaceAccount(); replacement = await f.StateBytes(); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.IsType<AppException>(error);
        Assert.Empty(f.Effects);
        Assert.Equal(replacement, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationCannotAdoptAReplacementSiteAfterItsFirstApproval(bool sameId)
    {
        using var f = await Fixture.Create("create");
        var action = f.Run();
        await f.WaitForLookup(action);
        try { f.ReplaceSite(sameId); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Empty(f.Effects);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task CanceledLookupsCannotContinueToTheFirstAccountWrite(string route)
    {
        using var f = await Fixture.Create(route);
        var before = await f.StateBytes();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = f.Run(canceled.Token);
        await f.WaitForLookup(action);
        try { canceled.Cancel(); }
        finally { f.Resume(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("create", "Reseller")]
    [InlineData("password", "Reseller")]
    [InlineData("delete", "Reseller")]
    [InlineData("create", "Administrator")]
    [InlineData("password", "Administrator")]
    [InlineData("delete", "Administrator")]
    public async Task CurrentActorsCanFinishTheNormalAccountOperation(string route, string role)
    {
        using var f = await Fixture.Create(route, role, pause: false);
        await f.Run();
        Assert.NotEmpty(f.Effects);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
        Assert.Equal(route != "delete", f.WindowsAccountExists);
        var users = await f.Service.GetFtpUsersAsync(f.Token);
        if (route == "delete") Assert.Empty(users);
        else Assert.Equal(Fixture.SiteName, Assert.Single(users).SiteName);
    }

    [Fact]
    public async Task APanelTokenActorDoesNotRequireCookieSessionClaims()
    {
        using var f = await Fixture.Create("create", pause: false, panelToken: true);
        await f.Run();
        Assert.True(f.WindowsAccountExists);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AnAdministratorCanStillRemoveAnUnrecordedOrphanAccount()
    {
        using var f = await Fixture.Create("delete", "Administrator", pause: false);
        await f.ClearRecords();
        await f.Run();
        Assert.False(f.WindowsAccountExists);
        Assert.Contains(f.Effects, effect => effect.Contains("/delete", StringComparison.Ordinal));
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task AnAccountWaitingForItsMutationLeaseCannotAdoptAReplacementRecord(string route)
    {
        using var f = await Fixture.Create(route, pause: false);
        using var held = await FtpAccountLifecycle.EnterAsync("fixture_ftp", f.Token);
        var action = f.Run();
        while (FtpAccountLifecycle.References("fixture_ftp") < 2)
        {
            if (action.IsCompleted) { await action; throw new InvalidOperationException("Mutation never queued for its account lease."); }
            f.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, f.Token);
        }
        await f.ReplaceAccount();
        var replacement = await f.StateBytes();
        held.Dispose();
        Assert.IsType<AppException>(await Record.ExceptionAsync(() => action));
        Assert.Empty(f.Effects);
        Assert.Equal(replacement, await f.StateBytes());
        Assert.Equal(0, FtpAccountLifecycle.References("fixture_ftp"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task ExistingTrustedServiceCallersRemainSupported(string route)
    {
        using var f = await Fixture.Create(route, pause: false);
        await f.RunTrusted();
        Assert.NotEmpty(f.Effects);
        Assert.Equal(route != "delete", f.WindowsAccountExists);
        Assert.Equal(0, FtpAccountLifecycle.References("fixture_ftp"));
    }

    [Theory]
    [InlineData("create", "disabled")]
    [InlineData("password", "password")]
    [InlineData("delete", "logout")]
    public async Task DelegatedResellersCannotOutliveTheirSourceAdministratorDuringAnAccountWait(string route, string change)
    {
        using var f = await Fixture.Create(route, delegated: true);
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForLookup(action);
        try { await f.ChangeDelegatingAdministrator(change); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ACurrentAdministratorCanStillOperateThroughAnAuthorizedReseller()
    {
        using var f = await Fixture.Create("create", pause: false, delegated: true);
        await f.Run();
        Assert.True(f.WindowsAccountExists);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RejectedRecreationDoesNotRemoveAnUntouchedExistingAccountsPermissions()
    {
        using var f = await Fixture.Create("create", existingCreate: true);
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForLookup(action);
        try { await f.ChangeActor("logout"); }
        finally { f.Resume(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.True(f.WindowsAccountExists);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RevocationAfterExpirySettingStillPreservesTheExistingAccountsUntouchedPermissions()
    {
        using var f = await Fixture.Create("create", pause: false, existingCreate: true);
        var before = await f.StateBytes();
        f.AfterCredentialSetting = () => f.ChangeActor("logout");
        var error = await Record.ExceptionAsync(() => f.Run());
        Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Equal("FTP credential setting", Assert.Single(f.Effects));
        Assert.True(f.WindowsAccountExists);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ACurrentActorCanStillRecreateItsOwnExistingAccount()
    {
        using var f = await Fixture.Create("create", pause: false, existingCreate: true);
        await f.Run();
        Assert.True(f.WindowsAccountExists);
        Assert.DoesNotContain(f.Effects, effect => effect.Contains("/add", StringComparison.Ordinal));
        Assert.Contains(f.Effects, effect => effect.StartsWith("icacls.exe", StringComparison.Ordinal));
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    private sealed class Fixture : IDisposable
    {
        internal const string SiteName = "ftp-owned.test";
        private const string UserName = "fixture_ftp";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string sessionId = SessionRevocationStore.NewSessionId();
        private readonly string delegatingSessionId = SessionRevocationStore.NewSessionId();
        private readonly string route;
        private readonly bool pause;
        private readonly int pauseQuery;
        private int queryCount;
        private readonly ServiceProvider services;
        private readonly SessionRevocationStore sessions;
        private readonly JsonFileStore store;
        private readonly FtpController controller;
        private SiteDto site;
        internal readonly Guid Actor = Guid.NewGuid(), Owner = Guid.NewGuid(), Other = Guid.NewGuid(), DelegatingAdmin = Guid.NewGuid();
        internal readonly List<string> Effects = [];
        internal AdaPanelDbContext Db { get; }
        internal CustomerScopeService Scopes { get; }
        internal FtpService Service { get; }
        internal bool WindowsAccountExists;
        internal Func<Task>? AfterCredentialSetting;
        internal CancellationToken Token => lifetime.Token;

        private Fixture(string route, string role, bool pause, bool panelToken, bool delegated, bool existingCreate)
        {
            this.route = route;
            this.pause = pause;
            pauseQuery = route == "create" && !existingCreate ? 1 : 2;
            WindowsAccountExists = route != "create" || existingCreate;
            Directory.CreateDirectory(root);
            site = new SiteDto { Id = 41, Name = SiteName, PhysicalPath = Path.Combine(root, "site"), ApplicationPoolName = "fixture-pool" };
            Directory.CreateDirectory(site.PhysicalPath);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            foreach (var id in new[] { Actor, Owner, Other, DelegatingAdmin }) Db.Users.Add(new PanelUser
            {
                Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                PasswordHash = "accepted-hash", Role = id == Actor ? Enum.Parse<UserRole>(role) : id == Owner ? UserRole.Customer :
                    id == DelegatingAdmin ? UserRole.Administrator : UserRole.Reseller
            });
            Db.SaveChanges();
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(SnapshotSite()),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([SnapshotSite()]),
                _ => throw new NotSupportedException(method.Name)
            });
            Scopes = new CustomerScopeService(store, new ServicePlanService(store),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null)
                    : throw new NotSupportedException(method.Name)), sites);
            Service = new FtpService(sites, store,
                Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync" ? Task.FromResult(new PanelPreferencesDto())
                    : throw new NotSupportedException(method.Name)),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }))
            { NativeCommand = Command, NativeScript = Script };
            sessions = new SessionRevocationStore(store);
            var audit = new AuditService(Db, Scopes, store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(sessions).AddSingleton<IConcurrencyLock, ConcurrencyLock>()
                .AddSingleton<IFtpService>(Service).AddSingleton<ICustomerScopeService>(Scopes).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IAuditService>(audit).BuildServiceProvider();
            // ActivatorUtilities resolves additional mandatory controller dependencies when the
            // authority fix lands; no dependency defaults to a fake authorization bypass.
            controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Actor.ToString()), new(ClaimTypes.Role, role) };
            if (!panelToken)
            {
                claims.Add(new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-hash")));
                claims.Add(new(SessionRevocationStore.SessionIdClaim, sessionId));
                if (delegated)
                {
                    claims.Add(new("impersonator_id", DelegatingAdmin.ToString()));
                    claims.Add(new("impersonator_name", DelegatingAdmin.ToString("N")));
                    claims.Add(new(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-hash")));
                    claims.Add(new(CookieSessionValidator.DelegatingSessionIdClaim, delegatingSessionId));
                }
            }
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity(claims,
                    panelToken ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme))
            } };
            if (delegated)
                Assert.Equal(DelegatingAdmin, CookieSessionValidator.CaptureSourceActor(controller.User).Delegation!.UserId);
        }

        internal static async Task<Fixture> Create(string route, string role = "Reseller", bool pause = true, bool panelToken = false,
            bool delegated = false, bool existingCreate = false)
        {
            var f = new Fixture(route, role, pause, panelToken, delegated, existingCreate);
            await f.Scopes.SetScopeAsync(f.Owner, [SiteName], 0, 0, f.Token);
            await f.Scopes.SetOwnerAsync(f.Owner, f.Actor, f.Token);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            if (route == "create" && !existingCreate) await f.ClearRecords();
            else await f.WriteRecord(SiteName, f.site.PhysicalPath, DateTime.UtcNow.AddDays(-1));
            return f;
        }

        private SiteDto SnapshotSite() => new() { Id = site.Id, Name = site.Name, PhysicalPath = site.PhysicalPath, ApplicationPoolName = site.ApplicationPoolName };
        internal void ReplaceSite(bool sameId) => site = new SiteDto { Id = sameId ? site.Id : site.Id + 1, Name = SiteName,
            PhysicalPath = Path.Combine(root, "replacement-site"), ApplicationPoolName = "replacement-pool" };
        internal Task ClearRecords() => store.WriteAsync("config/ftp-users.json", Array.Empty<object>(), Token);
        private Task WriteRecord(string siteName, string home, DateTime created) => store.WriteAsync("config/ftp-users.json",
            new[] { new { userName = UserName, siteName, homePath = home, isEnabled = true, createdAtUtc = created } }, Token);
        internal Task ReplaceAccount() => WriteRecord("foreign.test", Path.Combine(root, "foreign"), DateTime.UtcNow);
        internal Task<byte[]> StateBytes() => File.ReadAllBytesAsync(store.ResolvePath("config/ftp-users.json"), Token);

        internal async Task Run(CancellationToken? token = null)
        {
            var ct = token ?? Token;
            switch (route)
            {
                case "create": await controller.CreateUserAsync(new CreateFtpUserRequest { UserName = UserName, Password = "FixturePassword123!", SiteName = SiteName }, ct); break;
                case "password": await controller.SetPasswordAsync(new SetFtpPasswordRequest { UserName = UserName, Password = "FixturePassword123!" }, ct); break;
                case "delete": await controller.DeleteUserAsync(UserName, ct); break;
                default: throw new InvalidOperationException(route);
            }
        }

        internal Task RunTrusted() => route switch
        {
            "create" => Service.CreateFtpUserAsync(new CreateFtpUserRequest { UserName = UserName, Password = "FixturePassword123!", SiteName = SiteName }, Token),
            "password" => Service.SetFtpPasswordAsync(new SetFtpPasswordRequest { UserName = UserName, Password = "FixturePassword123!" }, Token),
            "delete" => Service.DeleteFtpUserAsync(UserName, Token),
            _ => throw new InvalidOperationException(route)
        };

        internal async Task WaitForLookup(Task action)
        {
            if (await Task.WhenAny(entered.Task, action).WaitAsync(Token) == action)
            { await action; throw new InvalidOperationException("FTP action ended before the expected native lookup."); }
            Assert.False(action.IsCompleted);
        }
        internal void Resume() => release.TrySetResult();
        internal Task ChangeActor(string change) => change switch
        {
            "disabled" => Db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false), Token),
            "password" => Db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "new-hash"), Token),
            "logout" => sessions.RevokeAsync(sessionId, Token),
            _ => throw new InvalidOperationException(change)
        };

        internal Task ChangeDelegatingAdministrator(string change) => change switch
        {
            "disabled" => Db.Users.Where(user => user.Id == DelegatingAdmin).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false), Token),
            "password" => Db.Users.Where(user => user.Id == DelegatingAdmin).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "changed-admin-hash"), Token),
            "logout" => sessions.RevokeAsync(delegatingSessionId, Token),
            _ => throw new InvalidOperationException(change)
        };

        private async Task<CommandResult> Command(string name, string arguments, CancellationToken token)
        {
            if (name == "net.exe" && arguments == $"user \"{UserName}\"")
            {
                var measured = WindowsAccountExists;
                if (pause && Interlocked.Increment(ref queryCount) == pauseQuery)
                {
                    entered.TrySetResult();
                    // An external probe can complete normally despite caller cancellation.
                    // Its consumer must still check the token before starting a write.
                    await release.Task.WaitAsync(Token);
                }
                return new CommandResult { ExitCode = measured ? 0 : 2 };
            }
            Effects.Add(name + " " + (name == "net.exe" ? arguments.Replace("FixturePassword123!", "[redacted]") : "fixture mutation"));
            if (arguments.Contains("/add", StringComparison.Ordinal)) WindowsAccountExists = true;
            if (arguments.Contains("/delete", StringComparison.Ordinal)) WindowsAccountExists = false;
            return new CommandResult { ExitCode = 0 };
        }

        private async Task<CommandResult> Script(string script, bool strict, CancellationToken token)
        {
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal))
                return new CommandResult { ExitCode = 0, StandardOutput = "present" };
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal))
                return new CommandResult { ExitCode = 0, StandardOutput = "up" };
            Effects.Add(strict ? "strict FTP configuration" : "FTP credential setting");
            if (script.Contains("Set-LocalUser", StringComparison.Ordinal) && AfterCredentialSetting is not null)
                await AfterCredentialSetting();
            return new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-SURESIZ" };
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
