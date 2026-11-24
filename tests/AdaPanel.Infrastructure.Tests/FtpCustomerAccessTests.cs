using System.Reflection;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// A6 (musteri kendi sitesinin FTP hesaplarini yonetir) + hesap guncelleme/etkin-pasif + kota.
/// Gercek denetleyici/servis, gercek CustomerScopeService ve SQLite aktorleri; her yerel komut sahte.
/// Musteri A, musteri B'nin sitesine/hesabina DOKUNAMAZ: reddedilen istek sunucuda iz birakmaz.
/// </summary>
public sealed class FtpCustomerAccessTests
{
    // ---------- sahiplik: baska musterinin sitesi/hesabi ----------

    [Fact]
    public async Task CustomerSeesOnlyOwnAccountsInTheList()
    {
        using var f = await Fixture.Create();
        var result = await f.Controller.GetUsersAsync(f.Token);
        var users = ((ApiResponse<IReadOnlyCollection<FtpUserDto>>)((OkObjectResult)result.Result!).Value!).Data!;
        Assert.Equal(["a_ftp"], users.Select(user => user.UserName).ToArray());
    }

    [Fact]
    public async Task CustomerCannotCreateAnAccountOnAnotherCustomersSite()
    {
        using var f = await Fixture.Create();
        var before = await f.StateBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.CreateUserAsync(
            new CreateFtpUserRequest { UserName = "intruder_ftp", Password = "FixturePassword123!", SiteName = Fixture.SiteB }, f.Token));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
    }

    [Fact]
    public async Task CustomerCanCreateAnAccountOnOwnSiteAndTheCreationIsAudited()
    {
        using var f = await Fixture.Create();
        var result = await f.Controller.CreateUserAsync(
            new CreateFtpUserRequest { UserName = "own2_ftp", Password = "FixturePassword123!", SiteName = Fixture.SiteA }, f.Token);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Contains(await f.Db.AuditLogs.ToListAsync(), log => log.Action == "ftp.user.created" && !log.Details.Contains("FixturePassword123!"));
    }

    [Fact]
    public async Task CustomerCannotCreateWithAHomeOutsideTheOwnSite()
    {
        using var f = await Fixture.Create();
        var before = await f.StateBytes();
        var result = await f.Controller.CreateUserAsync(new CreateFtpUserRequest { UserName = "own2_ftp", Password = "FixturePassword123!",
            SiteName = Fixture.SiteA, HomePath = f.SiteBPath }, f.Token);
        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("delete")]
    [InlineData("update")]
    [InlineData("enable")]
    [InlineData("quota")]
    public async Task CustomerCannotMutateAnotherCustomersAccount(string route)
    {
        using var f = await Fixture.Create();
        var before = await f.StateBytes();
        Task Act() => route switch
        {
            "password" => f.Controller.SetPasswordAsync(new SetFtpPasswordRequest { UserName = "b_ftp", Password = "FixturePassword123!" }, f.Token),
            "delete" => f.Controller.DeleteUserAsync("b_ftp", f.Token),
            "update" => f.Controller.UpdateUserAsync("b_ftp", new UpdateFtpUserRequest { ReadOnly = true }, f.Token),
            "enable" => f.Controller.SetEnabledAsync("b_ftp", new SetFtpUserEnabledRequest { Enabled = false }, f.Token),
            _ => f.Controller.SetQuotaAsync("b_ftp", f.Token)
        };
        var error = await Assert.ThrowsAsync<AppException>(Act);
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    // ---------- hesap guncelleme ----------

    [Fact]
    public async Task CustomerCanMakeOwnAccountReadOnlyAndTheResultIsMeasured()
    {
        using var f = await Fixture.Create();
        f.IisPermissions = 1;
        var result = await f.Controller.UpdateUserAsync("a_ftp", new UpdateFtpUserRequest { ReadOnly = true }, f.Token);
        var user = ((ApiResponse<FtpUserDto>)((OkObjectResult)result.Result!).Value!).Data!;
        Assert.True(user.ReadOnly);
        // NTFS: salt-okunur hak DEGISTIRILEREK (/grant:r) ve ReadAndExecute ile verilir.
        Assert.Contains(f.Effects, effect => effect.StartsWith("icacls.exe", StringComparison.Ordinal) && effect.Contains("/grant:r", StringComparison.Ordinal) && effect.Contains("RX", StringComparison.Ordinal));
        Assert.Contains(f.Effects, effect => effect == "iis-permissions-read");
        var users = await f.Service.GetFtpUsersAsync(f.Token);
        Assert.True(users.Single(item => item.UserName == "a_ftp").ReadOnly);
        Assert.Contains(await f.Db.AuditLogs.ToListAsync(), log => log.Action == "ftp.user.updated");
    }

    [Fact]
    public async Task ReadOnlyIsNotReportedWhenIisStillHasWritePermission()
    {
        using var f = await Fixture.Create();
        f.IisPermissions = 3; // IIS kurali salt-okunura inmedi
        var before = await f.StateText();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.UpdateUserAsync("a_ftp", new UpdateFtpUserRequest { ReadOnly = true }, f.Token));
        Assert.Equal("ftp.permission_unverified", error.MessageKey);
        Assert.Equal(before, await f.StateText());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CustomerCanMoveTheHomeInsideTheSiteAndTheOldHomeLosesTheGrant()
    {
        using var f = await Fixture.Create();
        var sub = Directory.CreateDirectory(Path.Combine(f.SiteAPath, "uploads")).FullName;
        f.IisPermissions = 3;
        var result = await f.Controller.UpdateUserAsync("a_ftp", new UpdateFtpUserRequest { HomePath = sub }, f.Token);
        var user = ((ApiResponse<FtpUserDto>)((OkObjectResult)result.Result!).Value!).Data!;
        Assert.Equal(sub, user.HomePath);
        Assert.Contains(f.Effects, effect => effect.StartsWith("icacls.exe", StringComparison.Ordinal) && effect.Contains(sub, StringComparison.OrdinalIgnoreCase) && effect.Contains("/grant:r", StringComparison.Ordinal));
        // Yeni ev eski kokun ICINDE: eski agactan hak kaldirma yeni evi (ve altini) atlar.
        Assert.DoesNotContain(f.Effects, effect => effect.StartsWith("icacls.exe", StringComparison.Ordinal) && effect.Contains("/remove:g", StringComparison.Ordinal) && effect.Contains(sub, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(sub, (await f.Service.GetFtpUsersAsync(f.Token)).Single(item => item.UserName == "a_ftp").HomePath);
    }

    [Fact]
    public async Task CustomerCannotMoveTheHomeOutsideOwnSite()
    {
        using var f = await Fixture.Create();
        var before = await f.StateText();
        foreach (var outside in new[] { f.SiteBPath, Path.GetDirectoryName(f.SiteAPath)!, Path.Combine(f.SiteAPath, "..", "site-b") })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.UpdateUserAsync("a_ftp", new UpdateFtpUserRequest { HomePath = outside }, f.Token));
            Assert.Equal("ftp.home_outside_site", error.MessageKey);
            Assert.Equal(403, error.HttpStatusCode);
        }
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateText());
    }

    [Fact]
    public async Task AFailedHomeMoveRestoresTheOldHomeAndMode()
    {
        using var f = await Fixture.Create();
        var sub = Directory.CreateDirectory(Path.Combine(f.SiteAPath, "uploads")).FullName;
        f.IisPermissions = 1; // salt-okunur istenmedi: beklenen 3 ama 1 okunur -> dogrulama patlar
        var before = await f.StateText();
        await Assert.ThrowsAsync<AppException>(() => f.Controller.UpdateUserAsync("a_ftp", new UpdateFtpUserRequest { HomePath = sub }, f.Token));
        Assert.Equal(before, await f.StateText());
        // Geri alma: IIS kurali/junction eski eve doner ve eski dizine eski hak (Modify) yeniden verilir.
        var afterMeasure = f.Effects.Skip(f.Effects.IndexOf("iis-permissions-read") + 1).ToArray();
        Assert.Contains("strict FTP configuration", afterMeasure);
        Assert.Contains(afterMeasure, effect => effect.StartsWith("icacls.exe", StringComparison.Ordinal) && effect.Contains("/grant:r", StringComparison.Ordinal)
            && effect.Contains("(OI)(CI)M", StringComparison.Ordinal) && effect.Contains(f.SiteAPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadOnlyMeasurementTellsWritableFromReadOnly()
    {
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var none = new DirectorySecurity();
        var ro = new DirectorySecurity();
        ro.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var rw = new DirectorySecurity();
        rw.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Assert.Null(FtpService.MeasureReadOnly(none, sid));
        Assert.True(FtpService.MeasureReadOnly(ro, sid));
        Assert.False(FtpService.MeasureReadOnly(rw, sid));
        Assert.True(FtpService.HomePermissionChangeNeeded(rw, sid, true, grant: true, readOnly: true));
        Assert.False(FtpService.HomePermissionChangeNeeded(ro, sid, true, grant: true, readOnly: true));
    }

    // ---------- etkin/pasif ----------

    [Fact]
    public async Task CustomerCanDisableAndEnableOwnAccountWithMeasuredState()
    {
        using var f = await Fixture.Create();
        await f.Controller.SetEnabledAsync("a_ftp", new SetFtpUserEnabledRequest { Enabled = false }, f.Token);
        Assert.False((await f.Service.GetFtpUsersAsync(f.Token)).Single(user => user.UserName == "a_ftp").IsEnabled);
        await f.Controller.SetEnabledAsync("a_ftp", new SetFtpUserEnabledRequest { Enabled = true }, f.Token);
        Assert.True((await f.Service.GetFtpUsersAsync(f.Token)).Single(user => user.UserName == "a_ftp").IsEnabled);
        Assert.Equal(["ftp.user.disabled", "ftp.user.enabled"], (await f.Db.AuditLogs.OrderBy(log => log.Id).ToListAsync()).Select(log => log.Action).ToArray());
    }

    [Fact]
    public async Task AnUnverifiedEnabledStateIsAnErrorNotASuccess()
    {
        using var f = await Fixture.Create();
        f.EnabledReadBack = "ADAPANEL-ENABLED=True"; // kapatildi ama Windows hala acik diyor
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.SetEnabledAsync("a_ftp", new SetFtpUserEnabledRequest { Enabled = false }, f.Token));
        Assert.Equal("ftp.enable_state_unverified", error.MessageKey);
        Assert.True((await f.Service.GetFtpUsersAsync(f.Token)).Single(user => user.UserName == "a_ftp").IsEnabled);
    }

    // ---------- askiya alinan abonelik ----------

    [Theory]
    [InlineData("POST", "/api/v1/ftp/users/a_ftp/enabled")]
    [InlineData("PUT", "/api/v1/ftp/users/a_ftp")]
    [InlineData("POST", "/api/v1/ftp/users")]
    [InlineData("DELETE", "/api/v1/ftp/users/a_ftp")]
    [InlineData("POST", "/api/v1/ftp/users/password")]
    [InlineData("PUT", "/api/v1/ftp/users/a_ftp/quota")]
    public async Task ASuspendedCustomerCannotWriteFtpAccountsNotEvenToReEnable(string method, string path)
    {
        using var f = await Fixture.Create();
        await f.Scopes.SuspendAsync(f.CustomerA, "manual", f.Token);
        var error = await Assert.ThrowsAsync<AppException>(() => SubscriptionSuspension.EnsureAllowedAsync(f.Scopes, f.Controller.User, method, path, f.Token));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal(SubscriptionSuspension.MessageKey, error.MessageKey);
        // Okuma serbest; yonetici/bayi askidan etkilenmez.
        await SubscriptionSuspension.EnsureAllowedAsync(f.Scopes, f.Controller.User, "GET", "/api/v1/ftp/users", f.Token);
    }

    [Fact]
    public void TheSuspensionFilterIsRegisteredGloballyForTheMvcPipeline()
    {
        var program = File.ReadAllText(Path.Combine(Fixture.RepoRoot(), "src/AdaPanel.Api/Program.cs"));
        Assert.Contains("options.Filters.Add<SubscriptionSuspensionFilter>();", program, StringComparison.Ordinal);
    }

    // ---------- kota ----------

    [Theory]
    [InlineData(2, 2, "Customer", true)]
    [InlineData(3, 2, "Customer", false)]
    [InlineData(0, 9, "Customer", false)]
    public async Task CustomerFtpAccountQuotaFollowsThePlanLimit(int limit, int existing, string role, bool rejected)
    {
        var subscription = Guid.NewGuid();
        var scopes = Fixture.Fake<ICustomerScopeService>((method, _) => method.Name switch
        {
            nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(subscription),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope(["a.test"], 0, 0, 0, 0, 0, MaxFtpUsers: limit)),
            _ => throw new NotSupportedException(method.Name)
        });
        var accounts = Enumerable.Range(0, existing).Select(i => new FtpUserDto { UserName = "h" + i, SiteName = "a.test" }).ToArray();
        var ftp = Fixture.Fake<IFtpService>((method, _) => method.Name == nameof(IFtpService.GetFtpUsersAsync)
            ? Task.FromResult<IReadOnlyCollection<FtpUserDto>>(accounts) : throw new NotSupportedException(method.Name));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "t"));
        var task = FtpQuota.EnsureAsync(scopes, ftp, principal, "a.test", default);
        if (rejected)
        {
            var error = await Assert.ThrowsAsync<AppException>(() => task);
            Assert.Equal("ftp.quota_exceeded", error.MessageKey);
            Assert.Equal(403, error.HttpStatusCode);
        }
        else await task;
    }

    [Fact]
    public async Task AFullQuotaBlocksTheCustomerCreateEndpointAndLeavesNoTrace()
    {
        using var f = await Fixture.Create(maxFtpUsers: 1); // a_ftp zaten var
        var before = await f.StateBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.CreateUserAsync(
            new CreateFtpUserRequest { UserName = "own2_ftp", Password = "FixturePassword123!", SiteName = Fixture.SiteA }, f.Token));
        Assert.Equal("ftp.quota_exceeded", error.MessageKey);
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
    }

    [Fact]
    public async Task PerUserQuotaIsHonestlyUnsupported()
    {
        using var f = await Fixture.Create();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.SetQuotaAsync("a_ftp", f.Token));
        Assert.Equal("ftp.quota_unsupported", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Empty(f.Effects);
    }

    // ---------- rol/kapi yapisi ----------

    [Fact]
    public void ControllerAdmitsCustomersAndGatesSubUsersBySitesSection()
    {
        var type = typeof(FtpController);
        var roles = type.GetCustomAttribute<AuthorizeAttribute>()!.Roles!;
        Assert.Contains("Customer", roles.Split(','));
        Assert.NotNull(type.GetCustomAttribute<RequireSubUserSectionAttribute>());
        var source = File.ReadAllText(Path.Combine(Fixture.RepoRoot(), "src/AdaPanel.Api/Controllers/FtpController.cs"));
        Assert.Contains("[RequireSubUserSection(\"sites\")]", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(FtpController.EnsureServerAsync))]
    [InlineData(nameof(FtpController.GetStatusAsync))]
    [InlineData(nameof(FtpController.GetTlsAsync))]
    [InlineData(nameof(FtpController.SetTlsAsync))]
    [InlineData(nameof(FtpController.ApplyCertificateAsync))]
    public void ServerLevelEndpointsStayAdministratorOnly(string method)
    {
        var info = typeof(FtpController).GetMethod(method)!;
        var attribute = info.GetCustomAttributes<AuthorizeAttribute>().Single();
        Assert.Equal("Administrator", attribute.Roles);
    }

    [Fact]
    public async Task ASubUserWithoutTheSitesSectionIsRefusedAndOneWithItActsOnTheParentSubscription()
    {
        using var f = await Fixture.Create();
        var sub = Guid.NewGuid();
        await f.Scopes.SetParentSubscriptionAsync(sub, f.CustomerA, f.Token);
        await f.Scopes.SetSubUserSectionsAsync(sub, ["mail"], f.Token);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sub.ToString()), new Claim(ClaimTypes.Role, "Customer")], "t"));
        var denied = await Assert.ThrowsAsync<AppException>(() => CustomerAccess.EnsureSectionAsync(f.Scopes, principal, "sites", f.Token));
        Assert.Equal(403, denied.HttpStatusCode);
        await f.Scopes.SetSubUserSectionsAsync(sub, ["sites"], f.Token);
        await CustomerAccess.EnsureSectionAsync(f.Scopes, principal, "sites", f.Token);
        // Alt-kullanici ebeveynin sitelerini gorur, baskasinin sitesini degil; kota sahibi ebeveyn aboneligi.
        var sites = await CustomerAccess.GetEffectiveSitesAsync(f.Scopes, principal, f.Token);
        Assert.Equal([Fixture.SiteA], sites!.ToArray());
        Assert.Equal(f.CustomerA, await CustomerAccess.GetSubscriptionUserIdAsync(f.Scopes, principal, f.Token));
        Assert.Equal(f.CustomerA, await f.Scopes.GetSiteOwnerUserIdAsync(Fixture.SiteA, f.Token));
    }

    [Fact]
    public async Task TheActorAuthorityAcceptsCustomersButNotOtherRolesAndDropsASubUserWhoseParentIsDisabled()
    {
        using var f = await Fixture.Create();
        Assert.Throws<AppException>(() => new FtpActorAuthority(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Operator")], "t"))));
        var sub = Guid.NewGuid();
        f.Db.Users.Add(new PanelUser { Id = sub, UserName = "sub", Email = "sub@example.test", IsActive = true, PasswordHash = "accepted-hash", Role = UserRole.Customer });
        await f.Db.SaveChangesAsync(f.Token);
        await f.Scopes.SetParentSubscriptionAsync(sub, f.CustomerA, f.Token);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sub.ToString()), new Claim(ClaimTypes.Role, "Customer")],
            PanelTokenAuthenticationHandler.SchemeName));
        var actor = new FtpActorAuthority(principal);
        var sessions = new SessionRevocationStore(f.Store);
        await actor.ValidateAsync(f.Db, sessions, f.Token, f.Scopes);
        await f.Db.Users.Where(user => user.Id == f.CustomerA).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false), f.Token);
        var error = await Assert.ThrowsAsync<AppException>(() => new FtpActorAuthority(principal).ValidateAsync(f.Db, sessions, f.Token, f.Scopes));
        Assert.Equal(401, error.HttpStatusCode);
    }

    // =====================================================================================

    private sealed class Fixture : IDisposable
    {
        internal const string SiteA = "a.test", SiteB = "b.test";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-customer-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
        private readonly ServiceProvider services;
        private readonly Dictionary<string, bool> windowsEnabled = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Guid CustomerA = Guid.NewGuid(), CustomerB = Guid.NewGuid();
        internal readonly List<string> Effects = [];
        internal readonly string SiteAPath, SiteBPath;
        internal JsonFileStore Store { get; }
        internal AdaPanelDbContext Db { get; }
        internal CustomerScopeService Scopes { get; }
        internal FtpService Service { get; }
        internal FtpController Controller { get; }
        internal int IisPermissions = 3;
        internal string? EnabledReadBack;
        internal CancellationToken Token => lifetime.Token;

        private Fixture(int maxFtpUsers)
        {
            SiteAPath = Path.Combine(root, "site-a");
            SiteBPath = Path.Combine(root, "site-b");
            Directory.CreateDirectory(SiteAPath);
            Directory.CreateDirectory(SiteBPath);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            foreach (var id in new[] { CustomerA, CustomerB }) Db.Users.Add(new PanelUser
            {
                Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                PasswordHash = "accepted-hash", Role = UserRole.Customer
            });
            Db.SaveChanges();
            SiteDto Site(string name) => new() { Id = name == SiteA ? 1 : 2, Name = name, PhysicalPath = name == SiteA ? SiteAPath : SiteBPath, ApplicationPoolName = "pool-" + name };
            var sites = Fake<IIisSiteService>((method, args) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(Site((string)args[0]!)),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site(SiteA), Site(SiteB)]),
                _ => throw new NotSupportedException(method.Name)
            });
            var plans = new ServicePlanService(Store);
            Scopes = new CustomerScopeService(Store, plans,
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null)
                    : throw new NotSupportedException(method.Name)), sites);
            Service = new FtpService(sites, Store,
                Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync" ? Task.FromResult(new PanelPreferencesDto())
                    : throw new NotSupportedException(method.Name)),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }))
            { NativeCommand = Command, NativeScript = Script, NativeAccountStatus = (name, _) => Task.FromResult(name is "a_ftp" or "b_ftp" ? 0u : 2221u) };
            var audit = new AuditService(Db, Scopes, Store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(new SessionRevocationStore(Store)).AddSingleton<IConcurrencyLock, ConcurrencyLock>()
                .AddSingleton<IFtpService>(Service).AddSingleton<ICustomerScopeService>(Scopes).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IAuditService>(audit).BuildServiceProvider();
            Controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, CustomerA.ToString()), new Claim(ClaimTypes.Role, "Customer")],
                    PanelTokenAuthenticationHandler.SchemeName))
            } };
            MaxFtpUsers = maxFtpUsers;
        }

        private int MaxFtpUsers { get; }

        internal static async Task<Fixture> Create(int maxFtpUsers = 0)
        {
            var f = new Fixture(maxFtpUsers);
            await f.Scopes.SetScopeAsync(f.CustomerA, [SiteA], 0, 0, f.Token);
            await f.Scopes.SetScopeAsync(f.CustomerB, [SiteB], 0, 0, f.Token);
            if (maxFtpUsers > 0)
            {
                var plan = await new ServicePlanService(f.Store).CreatePlanAsync(new ServicePlanRequest { Name = "limited", MaxFtpUsers = maxFtpUsers }, null, f.Token);
                await f.Scopes.SetPlanAsync(f.CustomerA, plan.Id, f.Token);
            }
            await f.Store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            await f.Store.WriteAsync("config/ftp-users.json", new[]
            {
                new { userName = "a_ftp", siteName = SiteA, homePath = f.SiteAPath, isEnabled = true, createdAtUtc = DateTime.UtcNow.AddDays(-1) },
                new { userName = "b_ftp", siteName = SiteB, homePath = f.SiteBPath, isEnabled = true, createdAtUtc = DateTime.UtcNow.AddDays(-1) }
            }, f.Token);
            return f;
        }

        internal static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "AdaPanel.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Repository root not found.");
        }

        internal Task<byte[]> StateBytes() => File.ReadAllBytesAsync(Store.ResolvePath("config/ftp-users.json"), Token);
        internal Task<string> StateText() => File.ReadAllTextAsync(Store.ResolvePath("config/ftp-users.json"), Token);

        private Task<CommandResult> Command(string name, string arguments, CancellationToken token)
        {
            Effects.Add(name + " " + (name == "net.exe" ? arguments.Replace("FixturePassword123!", "[redacted]") : arguments));
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }

        private Task<CommandResult> Script(string script, bool strict, CancellationToken token)
        {
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "present" });
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "up" });
            if (script.Contains("ADAPANEL-PERMS", StringComparison.Ordinal))
            {
                Effects.Add("iis-permissions-read");
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-PERMS=" + IisPermissions });
            }
            if (script.Contains("ADAPANEL-ENABLED", StringComparison.Ordinal))
            {
                var enable = script.Contains("Enable-LocalUser", StringComparison.Ordinal);
                Effects.Add(enable ? "enable-localuser" : "disable-localuser");
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = EnabledReadBack ?? "ADAPANEL-ENABLED=" + (enable ? "True" : "False") });
            }
            Effects.Add(strict ? "strict FTP configuration" : "FTP credential setting");
            return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-SURESIZ" });
        }

        public void Dispose()
        {
            lifetime.Cancel(); services.Dispose(); Db.Dispose(); connection.Dispose(); lifetime.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        internal static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
        { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Action = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []);
    }
}
