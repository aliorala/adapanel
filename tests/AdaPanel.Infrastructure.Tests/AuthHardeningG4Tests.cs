using System.Net;
using System.Reflection;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Denetim G4/G5/G11/G25: yönetici-hedef koruması, anahtarla yönetici atama, hesap tavanı, IPv6.</summary>
public sealed class AuthHardeningG4Tests
{
    private sealed class Owner(Guid? owner) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(owner);
        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(owner == userId);
    }

    private static async Task<(SqliteConnection Conn, AdaPanelDbContext Db, PanelUser Owner, PanelUser Second, PanelUser Third, PanelUser Customer)> Seed()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        PanelUser U(string n, UserRole r) => new()
        {
            Id = Guid.NewGuid(), UserName = n, Email = n + "@ornek.test", Role = r, PreferredLanguage = "tr", IsActive = true,
            PasswordHash = "x", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        var o = U("owner", UserRole.Administrator); var s = U("second", UserRole.Administrator);
        var t = U("third", UserRole.Administrator); var c = U("cust", UserRole.Customer);
        db.Users.AddRange(o, s, t, c);
        await db.SaveChangesAsync();
        return (conn, db, o, s, t, c);
    }

    [Fact]
    public async Task SecondAdministratorCannotTakeOverOrManageTheServerOwner()
    {
        var (conn, db, owner, second, third, customer) = await Seed();
        await using var _c = conn; await using var _d = db;
        var scope = new AdminPasswordAndSsoTests.SahteKapsam();
        var owners = new Owner(owner.Id);
        // Kimlik devralma (parola belirle / SSO): HİÇBİR başka yönetici hedef olamaz.
        foreach (var target in new[] { owner, third })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => ManagedUserGuard.EnsureManagedAsync(
                db, scope, second.Id, target.Id, default, owners, ManagedUserGuard.AdminTargetRule.NoOtherAdmin));
            Assert.Equal(403, error.HttpStatusCode);
        }
        // Yönetim (güncelle/sil/kilit aç): sunucu sahibi korunur, sahip olmayan yönetici yönetilebilir.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManagedUserGuard.EnsureManagedAsync(db, scope, second.Id, owner.Id, default, owners));
        Assert.Equal(third.Id, (await ManagedUserGuard.EnsureManagedAsync(db, scope, second.Id, third.Id, default, owners)).Target.Id);
        // Kendisi ve müşteri serbest; sahip kendi hesabını değiştirebilir.
        Assert.Equal(second.Id, (await ManagedUserGuard.EnsureManagedAsync(
            db, scope, second.Id, second.Id, default, owners, ManagedUserGuard.AdminTargetRule.NoOtherAdmin)).Target.Id);
        Assert.Equal(owner.Id, (await ManagedUserGuard.EnsureManagedAsync(db, scope, owner.Id, owner.Id, default, owners)).Target.Id);
        Assert.Equal(customer.Id, (await ManagedUserGuard.EnsureManagedAsync(
            db, scope, second.Id, customer.Id, default, owners, ManagedUserGuard.AdminTargetRule.NoOtherAdmin)).Target.Id);
    }

    private static UsersController Users(string authType)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Administrator")], authType);
        return new UsersController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
    }

    [Fact]
    public async Task PersonalApiTokenCannotCreateOrPromoteAnAdministrator()
    {
        var controller = Users(PanelTokenAuthenticationHandler.SchemeName);
        var create = new CreateUserRequest { UserName = "x", Email = "x@y.z", Password = "Cok-Guclu-Parola-2026", Role = "Administrator" };
        var error = await Assert.ThrowsAsync<AppException>(() => controller.CreateUserAsync(create, default));
        Assert.Equal(403, error.HttpStatusCode);
        var update = new UpdateUserRequest { Email = "x@y.z", Role = "administrator", IsActive = true };
        await Assert.ThrowsAsync<AppException>(() => controller.UpdateUserAsync(Guid.NewGuid(), update, default));
        // Çerez oturumu için kural devreye girmez (servis null olduğundan NullReference = kapıyı geçti).
        await Assert.ThrowsAsync<NullReferenceException>(() => Users("Cookies").CreateUserAsync(create, default));
    }

    [Fact]
    public async Task ApiTokensCannotBeCreatedWhileImpersonating()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("impersonator_id", Guid.NewGuid().ToString())], "Cookies");
        var controller = new ApiTokensController(null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
        var error = await Assert.ThrowsAsync<AppException>(() => controller.CreateAsync(new CreateApiTokenRequest { Name = "t" }, default));
        Assert.Equal(403, error.HttpStatusCode);
    }

    [Fact]
    public void AccountThrottleBlocksAfterTheCeilingAndExpiresWithTheWindow()
    {
        var id = Guid.NewGuid(); var t0 = DateTime.UtcNow;
        for (var i = 0; i < AccountAttemptThrottle.MaxFailures - 1; i++) AccountAttemptThrottle.RegisterFailure(id, t0);
        Assert.False(AccountAttemptThrottle.IsBlocked(id, t0));
        AccountAttemptThrottle.RegisterFailure(id, t0);
        Assert.True(AccountAttemptThrottle.IsBlocked(id, t0));
        Assert.False(AccountAttemptThrottle.IsBlocked(id, t0 + AccountAttemptThrottle.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Ipv6IsOneSourcePerSlash64AndMappedIpv4CollapsesToIpv4()
    {
        var a = PanelRateLimits.SourceKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var b = PanelRateLimits.SourceKey(IPAddress.Parse("2001:db8:1:2:bbbb::ffff"));
        var c = PanelRateLimits.SourceKey(IPAddress.Parse("2001:db8:1:3::1"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal("203.0.113.9", PanelRateLimits.SourceKey(IPAddress.Parse("::ffff:203.0.113.9")));
    }

    // ---- ortak sahte üretici: Task dönenler tamamlanmış Task döner ----
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        public List<string> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            var handled = Handler?.Invoke(method, args);
            if (handled is not null) return handled;
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private static (T Stub, Proxy Proxy) Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var stub = DispatchProxy.Create<T, Proxy>();
        var proxy = (Proxy)(object)stub;
        proxy.Handler = handler;
        return (stub, proxy);
    }

    private static ClaimsPrincipal Principal(Guid id, string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "Cookies"));

    // ---- G22: bayi yönetici askısını ezip kaldıramaz ----
    private static (SubscriptionsController Controller, Proxy Enforcement, Guid Reseller, Guid Customer) Subscriptions(bool adminSuspended)
    {
        var reseller = Guid.NewGuid(); var customer = Guid.NewGuid();
        var scope = new AdminPasswordAndSsoTests.SahteKapsam();
        scope.SahipAta(customer, reseller);
        if (adminSuspended) scope.Askiyaal(customer); // sebep "manual" = yönetici askısı
        var (enforcement, proxy) = Stub<ISubscriptionEnforcementService>();
        var controller = new SubscriptionsController(enforcement, scope, Stub<IServicePlanService>().Stub, Stub<IResellerQuotaService>().Stub, Stub<IAuditService>().Stub)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(reseller, "Reseller") } }
        };
        return (controller, proxy, reseller, customer);
    }

    [Fact]
    public async Task ResellerCannotOverwriteOrLiftAnAdministratorSuspension()
    {
        var (controller, enforcement, _, customer) = Subscriptions(adminSuspended: true);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => controller.SuspendAsync(customer, default))).HttpStatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => controller.ResumeAsync(customer, default))).HttpStatusCode);
        Assert.DoesNotContain(enforcement.Calls, call => call is "SuspendAsync" or "ResumeAsync");
    }

    [Fact]
    public async Task ResellerSuspensionOfAnActiveCustomerGoesThrough()
    {
        var (controller, enforcement, _, customer) = Subscriptions(adminSuspended: false);
        await controller.SuspendAsync(customer, default);
        Assert.Contains("SuspendAsync", enforcement.Calls);
    }

    // ---- G11: kısıtlıyken doğru parola IP hatası SAYILMAZ, yanlış parola sayılır ----
    [Fact]
    public async Task ThrottledAccountDoesNotCountACorrectPasswordAsAnIpFailure()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var hasher = new PasswordHasher<PanelUser>();
        var user = new PanelUser { Id = Guid.NewGuid(), UserName = "kisitli", Email = "kisitli@ornek.test", Role = UserRole.Customer, PreferredLanguage = "tr", IsActive = true };
        user.PasswordHash = hasher.HashPassword(user, "Dogru-Parola-2026");
        db.Users.Add(user); await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "g4-throttle-" + Guid.NewGuid().ToString("N"));
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
        var brute = new CountingBruteForceProtection(); var audit = new AdminPasswordAndSsoTests.KayitEdenDenetim();
        var service = new AuthService(db, hasher, new ThrowingSecretProtector(), new ThrowingTotpService(), audit, brute,
            null!, null!, null!, null!, new JsonFileStore(options), options, new SahipsizSunucuSahibiServisi(), NullLogger<AuthService>.Instance);
        for (var i = 0; i < AccountAttemptThrottle.MaxFailures; i++) AccountAttemptThrottle.RegisterFailure(user.Id);

        var good = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(new LoginRequest { UserNameOrEmail = "kisitli", Password = "Dogru-Parola-2026" }, "203.0.113.5"));
        Assert.Equal("auth.invalid_credentials", good.Message);
        Assert.Equal(0, brute.Failures);
        Assert.Contains(audit.Kayitlar, row => row.Action == "auth.login.throttled");

        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoginAsync(new LoginRequest { UserNameOrEmail = "kisitli", Password = "Yanlis-Parola-2026" }, "203.0.113.5"));
        Assert.Equal("auth.invalid_credentials", bad.Message);
        Assert.Equal(1, brute.Failures);
        AccountAttemptThrottle.Reset(user.Id);
    }

    // ---- G35: 2FA talebi olmayan ya da bayat çerez reddedilir ----
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", true)]
    [InlineData("1", false)]
    public async Task CookieWithoutOrWithStaleTwoFactorStateIsRejected(string? state, bool accepted)
    {
        var (conn, db, owner, _, _, _) = await Seed();
        await using var _c = conn; await using var _d = db;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, owner.Id.ToString()), new(ClaimTypes.Role, owner.Role.ToString()),
            new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(owner.PasswordHash)),
            OturumTestOrtami.OturumKimligiTalebi()
        };
        if (state is not null) claims.Add(new Claim(CookieSessionValidator.TwoFactorStateClaim, state));
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(OturumTestOrtami.BosKaraListe())
            .AddSingleton<IAuthenticationService>(new AdminPasswordAndSsoTests.SessizKimlikServisi()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var context = new CookieValidatePrincipalContext(http,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")), new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme));
        await CookieSessionValidator.OnValidatePrincipalAsync(context);
        Assert.Equal(accepted, context.Principal is not null);
    }

    // ---- G13 / G22b / G37: kullanıcı yönetimi ----
    private static async Task<(UserManagementService Service, Guid Reseller, Guid Customer, AdaPanelDbContext Db, SqliteConnection Conn)> UsersService(
        ResellerQuotaDto? quota, string suspendReason, bool customerActive)
    {
        var (conn, db, _, _, _, _) = await Seed();
        PanelUser U(string n, UserRole r, bool active = true) => new()
        {
            Id = Guid.NewGuid(), UserName = n, Email = n + "@bayi.test", Role = r, PreferredLanguage = "tr", IsActive = active,
            PasswordHash = "x", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        var reseller = U("bayi", UserRole.Reseller); var customer = U("musteri", UserRole.Customer, customerActive);
        db.Users.AddRange(reseller, customer); await db.SaveChangesAsync();
        var scopes = Stub<ICustomerScopeService>((method, _) => method.Name switch
        {
            nameof(ICustomerScopeService.GetOwnerAsync) => Task.FromResult<Guid?>(reseller.Id),
            nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
            nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0,
                SuspendedAtUtc: suspendReason.Length > 0 ? DateTime.UtcNow : null, SuspendReason: suspendReason)),
            nameof(ICustomerScopeService.GetOwnedUserIdsAsync) => Task.FromResult<IReadOnlyCollection<Guid>>([]),
            nameof(ICustomerScopeService.GetAssignedSitesAsync) or nameof(ICustomerScopeService.GetResellerSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>([]),
            _ => null
        }).Stub;
        var quotas = Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync) ? Task.FromResult(quota) : null).Stub;
        var service = new UserManagementService(db, new PasswordHasher<PanelUser>(), scopes, Stub<INotificationService>().Stub,
            Stub<IPanelPreferencesService>().Stub, Stub<IPanelSecurityService>().Stub, NullLogger<UserManagementService>.Instance,
            Stub<IAuditService>().Stub, Stub<IIisSiteService>().Stub, Stub<IDatabaseOwnershipService>().Stub, Stub<ISchedulerService>().Stub,
            Stub<ISiteCascadeDeletionService>().Stub, Stub<IApiTokenService>().Stub, Stub<IServerOwnerService>().Stub, quotas);
        return (service, reseller.Id, customer.Id, db, conn);
    }

    [Fact]
    public async Task ResellerCreateAndUpdateOverTheQuotaAreRejected()
    {
        var (service, reseller, customer, db, conn) = await UsersService(new ResellerQuotaDto { MaxSites = 2, MaxDiskMb = 100 }, string.Empty, true);
        await using var _c = conn; await using var _d = db;
        var create = new CreateUserRequest { UserName = "yeni", Email = "yeni@bayi.test", Password = "Cok-Guclu-Parola-2026", Role = "Customer", MaxSites = 5, MaxDiskMb = 50 };
        var createError = await Assert.ThrowsAsync<AppException>(() => service.CreateUserAsync(create, reseller, null));
        Assert.Equal("reseller.site_quota_exceeded", createError.MessageKey);
        var update = new UpdateUserRequest { Email = "musteri@bayi.test", Role = "Customer", IsActive = true, MaxSites = 5, MaxDiskMb = 50 };
        var updateError = await Assert.ThrowsAsync<AppException>(() => service.UpdateUserAsync(customer, update, reseller, null));
        Assert.Equal("reseller.site_quota_exceeded", updateError.MessageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("manual")]
    [InlineData("expired")]
    public async Task ResellerCannotReactivateACustomerItDidNotDeactivate(string reason)
    {
        var (service, reseller, customer, db, conn) = await UsersService(null, reason, customerActive: false);
        await using var _c = conn; await using var _d = db;
        var update = new UpdateUserRequest { Email = "musteri@bayi.test", Role = "Customer", IsActive = true, MaxSites = 1, MaxDiskMb = 10 };
        var error = await Assert.ThrowsAsync<AppException>(() => service.UpdateUserAsync(customer, update, reseller, null));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(user => user.Id == customer)).IsActive);
    }

    [Fact]
    public async Task UserNameEqualToAnotherUsersEmailIsRejected()
    {
        var (service, reseller, _, db, conn) = await UsersService(null, string.Empty, true);
        await using var _c = conn; await using var _d = db;
        var create = new CreateUserRequest { UserName = "musteri@bayi.test", Email = "baska@bayi.test", Password = "Cok-Guclu-Parola-2026", Role = "Customer" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateUserAsync(create, reseller, null));
        Assert.Equal("users.duplicate_username", error.Message);
    }

    [Fact]
    public async Task SecondAdministratorCannotChangeAnotherAdministratorsEmail()
    {
        var (conn, db, _, second, third, _) = await Seed();
        await using var _c = conn; await using var _d = db;
        var service = new UserManagementService(db, new PasswordHasher<PanelUser>(), Stub<ICustomerScopeService>().Stub, Stub<INotificationService>().Stub,
            Stub<IPanelPreferencesService>().Stub, Stub<IPanelSecurityService>().Stub, NullLogger<UserManagementService>.Instance,
            Stub<IAuditService>().Stub, Stub<IIisSiteService>().Stub, Stub<IDatabaseOwnershipService>().Stub, Stub<ISchedulerService>().Stub,
            Stub<ISiteCascadeDeletionService>().Stub, Stub<IApiTokenService>().Stub, new Owner(Guid.NewGuid()), Stub<IResellerQuotaService>().Stub);
        var update = new UpdateUserRequest { Email = "ele-gecirilmis@kotu.test", Role = "Administrator", IsActive = true };
        var error = await Assert.ThrowsAsync<AppException>(() => service.UpdateUserAsync(third.Id, update, second.Id, null));
        Assert.Equal(403, error.HttpStatusCode);
    }

    // ---- G24 ----
    [Fact]
    public async Task AuditLogsAreClosedToSectionRestrictedSubUsers()
    {
        Assert.NotNull(typeof(AuditLogsController).GetCustomAttribute<RequireSubUserSectionAttribute>());
        async Task Check(IReadOnlyCollection<string>? sections) => await CustomerAccess.EnsureSectionAsync(
            Stub<ICustomerScopeService>((method, _) => method.Name switch
            {
                nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(Guid.NewGuid()),
                nameof(ICustomerScopeService.GetSubUserSectionsAsync) => Task.FromResult(sections),
                _ => null
            }).Stub, Principal(Guid.NewGuid(), "Customer"), "audit", default);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => Check(["sites"]))).HttpStatusCode);
        await Check(null); // kısıtsız ek kullanıcı etkilenmez
    }
}
