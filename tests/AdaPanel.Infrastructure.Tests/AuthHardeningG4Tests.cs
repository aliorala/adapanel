using System.Net;
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
}
