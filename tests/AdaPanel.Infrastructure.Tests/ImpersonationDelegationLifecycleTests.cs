using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ImpersonationDelegationLifecycleTests
{
    [Theory]
    [InlineData("logout", false, false)]
    [InlineData("logout", true, false)]
    [InlineData("password", false, false)]
    [InlineData("password", true, true)]
    [InlineData("role", false, false)]
    [InlineData("role", true, true)]
    [InlineData("inactive", false, false)]
    [InlineData("inactive", true, true)]
    [InlineData("deleted", false, false)]
    [InlineData("deleted", true, true)]
    public async Task IssuedDelegationRevalidatesItsOriginalAdministrator(string change, bool freshCache, bool expectedValid)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Change(fixture.AdminId, change);

        Assert.Equal(expectedValid, await fixture.Validate(freshCache));
        Assert.Equal(expectedValid ? 0 : 1, fixture.Authentication.ValidationSignOuts);
        Assert.Equal(1, fixture.Authentication.Issued);
    }

    [Theory]
    [InlineData("logout", false, false)]
    [InlineData("logout", true, false)]
    [InlineData("password", false, false)]
    [InlineData("password", true, true)]
    [InlineData("role", false, false)]
    [InlineData("role", true, true)]
    [InlineData("inactive", false, false)]
    [InlineData("inactive", true, true)]
    [InlineData("deleted", false, false)]
    [InlineData("deleted", true, true)]
    public async Task TargetAccountRetainsTheExistingCookieValidationInterval(string change, bool freshCache, bool expectedValid)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Change(fixture.CustomerId, change);

        Assert.Equal(expectedValid, await fixture.Validate(freshCache));
        Assert.Equal(expectedValid ? 0 : 1, fixture.Authentication.ValidationSignOuts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedDelegationRemainsUsableAcrossValidationWindows(bool freshCache)
    {
        await using var fixture = await Fixture.Create();
        Assert.True(await fixture.Validate(freshCache));
        Assert.Equal(0, fixture.Authentication.ValidationSignOuts);
        Assert.Equal(fixture.AdminId.ToString(), fixture.Authentication.Principal!.FindFirstValue("impersonator_id"));
        Assert.Equal(fixture.CustomerId.ToString(), fixture.Authentication.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnrelatedAdministratorDoesNotOwnThisDelegation(bool freshCache)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Change(fixture.OtherAdminId, "inactive");
        Assert.True(await fixture.Validate(freshCache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDelegationWithoutItsOriginalAuthorityCannotContinue(bool freshCache)
    {
        await using var fixture = await Fixture.Create();
        // R33 and earlier tickets carried only the original administrator's id/name,
        // with no credential/session evidence for that administrator.
        var legacyTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Email, ClaimTypes.Role,
            "preferred_language", "two_factor_enabled", "impersonator_id", "impersonator_name",
            CookieSessionValidator.CredentialStampClaim, SessionRevocationStore.SessionIdClaim
        };
        var identity = (ClaimsIdentity)fixture.Authentication.Principal!.Identity!;
        foreach (var claim in identity.Claims.Where(claim => !legacyTypes.Contains(claim.Type)).ToArray()) identity.RemoveClaim(claim);

        Assert.False(await fixture.Validate(freshCache));
        Assert.Equal(1, fixture.Authentication.ValidationSignOuts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidDelegationCanReturnToItsOriginalAdministrator(bool freshCache)
    {
        await using var fixture = await Fixture.Create();
        Assert.True(await fixture.ValidateThenReturn(freshCache));
        Assert.Equal(2, fixture.Authentication.Issued);
        Assert.Equal(fixture.AdminId.ToString(), fixture.Authentication.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Null(fixture.Authentication.Principal!.FindFirstValue("impersonator_id"));
        Assert.True(await fixture.Validate(false));
    }

    [Theory]
    [InlineData("logout", false)]
    [InlineData("logout", true)]
    [InlineData("password", false)]
    [InlineData("password", true)]
    public async Task OldDelegationCannotMintANewAdministratorSessionAfterRevocation(string change, bool freshCache)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Change(fixture.AdminId, change);

        Assert.False(await fixture.ValidateThenReturn(freshCache));
        Assert.Equal(1, fixture.Authentication.Issued);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task FactorCookieRenewalPreservesItsOriginalDelegation(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        var result = Assert.IsType<OkObjectResult>(await fixture.RenewFactor(operation));
        Assert.Equal(2, fixture.Authentication.Issued);
        Assert.Equal(fixture.CustomerId.ToString(), fixture.Authentication.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(fixture.AdminId.ToString(), fixture.Authentication.Principal!.FindFirstValue("impersonator_id"));
        Assert.DoesNotContain("impersonator", JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);

        await fixture.Change(fixture.AdminId, "logout");
        Assert.False(await fixture.Validate(true));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task FactorCookieRenewalCannotEscapeAnAdministratorRevokedAfterFactorCommit(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        fixture.Authentication.BeforeSigningIn = () => fixture.Change(fixture.AdminId, "logout");
        var error = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => fixture.RenewFactor(operation));
        Assert.Equal(401, error.HttpStatusCode);
        Assert.Equal(1, fixture.Authentication.Issued);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Password = "Delegation!Password123";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-delegation-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly PasswordHasher<PanelUser> hasher = new();
        private readonly JsonFileStore store;
        private readonly SessionRevocationStore sessions;
        private ClaimsPrincipal originalAdmin = null!;
        internal Guid AdminId { get; } = Guid.NewGuid();
        internal Guid CustomerId { get; } = Guid.NewGuid();
        internal Guid OtherAdminId { get; } = Guid.NewGuid();
        internal Boundary Authentication { get; } = new();

        private Fixture()
        {
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            sessions = new SessionRevocationStore(store);
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "auth.db")};Pooling=False").Options);

        private AuthService Service(AdaPanelDbContext db) => new(db, hasher, new Protector(), new Totp(),
            new AuditService(db, null!, store), new CountingBruteForceProtection(), null!, null!, null!, new ApiTokenService(db), store,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, NullLogger<AuthService>.Instance);

        private ServiceProvider Services(AdaPanelDbContext db) => new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
            .AddSingleton<IAuthenticationService>(Authentication).BuildServiceProvider();

        private AuthController Controller(AdaPanelDbContext db, IServiceProvider services, ClaimsPrincipal principal) =>
            new(null!, Service(db), new AuditService(db, null!, store), null!, sessions)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services, User = principal } } };

        internal static async Task<Fixture> Create(string? factorOperation = null)
        {
            var fixture = new Fixture();
            await using var db = fixture.NewContext();
            await db.Database.EnsureCreatedAsync();
            PanelUser? admin = null;
            foreach (var id in new[] { fixture.AdminId, fixture.CustomerId, fixture.OtherAdminId })
            {
                var user = new PanelUser
                {
                    Id = id, UserName = "user-" + id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                    Role = id == fixture.CustomerId ? UserRole.Customer : UserRole.Administrator
                };
                user.PasswordHash = fixture.hasher.HashPassword(user, Password);
                if (id == fixture.CustomerId)
                {
                    user.IsTwoFactorEnabled = factorOperation == "disable";
                    user.TwoFactorSecretProtected = factorOperation == "disable" ? "protected:active-secret" : null;
                    user.PendingTwoFactorSecretProtected = factorOperation == "confirm" ? "protected:pending-secret" : null;
                }
                db.Users.Add(user);
                if (id == fixture.AdminId) admin = user;
            }
            await db.SaveChangesAsync();
            fixture.originalAdmin = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, admin!.Id.ToString()), new Claim(ClaimTypes.Name, admin.UserName),
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(admin.PasswordHash)),
                new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            await using var services = fixture.Services(db);
            Assert.IsType<OkObjectResult>((await fixture.Controller(db, services, fixture.originalAdmin)
                .ImpersonateAsync(fixture.CustomerId, default)).Result);
            Assert.Equal(1, fixture.Authentication.Issued);
            Assert.True(await fixture.Validate(false)); // create the actual validation-cache timestamp
            return fixture;
        }

        internal async Task Change(Guid userId, string change)
        {
            await using var db = NewContext();
            if (change == "logout")
            {
                // Run the public logout action against the relevant real cookie identity.
                await using var services = Services(db);
                var principal = userId == AdminId ? originalAdmin : Authentication.Principal!;
                await Controller(db, services, principal).LogoutAsync(default);
                Authentication.ValidationSignOuts = 0;
                return;
            }
            if (change == "password")
            {
                await Service(db).ChangePasswordAsync(userId,
                    new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "Changed!Password456" }, null);
                return;
            }
            var user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
            switch (change)
            {
                case "role": user.Role = user.Role == UserRole.Administrator ? UserRole.Customer : UserRole.Reseller; break;
                case "inactive": user.IsActive = false; break;
                case "deleted": db.Users.Remove(user); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            await db.SaveChangesAsync();
        }

        internal async Task<bool> Validate(bool freshCache)
        {
            if (!freshCache)
                Authentication.Properties!.Items["adapanel_validated_utc"] = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(2))
                    .ToString("o", CultureInfo.InvariantCulture);
            await using var db = NewContext();
            await using var services = Services(db);
            var context = new DefaultHttpContext { RequestServices = services, User = Authentication.Principal! };
            var validation = new CookieValidatePrincipalContext(context, Boundary.Scheme(), new CookieAuthenticationOptions(),
                new AuthenticationTicket(Authentication.Principal!, Authentication.Properties!, CookieAuthenticationDefaults.AuthenticationScheme));
            await CookieSessionValidator.OnValidatePrincipalAsync(validation);
            return validation.Principal?.Identity?.IsAuthenticated == true;
        }

        internal async Task<bool> ValidateThenReturn(bool freshCache)
        {
            if (!await Validate(freshCache)) return false;
            // A fresh DbContext models the next HTTP request, rather than accidentally
            // rejecting because the prior request's tracked administrator row is stale.
            await using var db = NewContext();
            await using var services = Services(db);
            try
            {
                Assert.IsType<OkObjectResult>((await Controller(db, services, Authentication.Principal!)
                    .StopImpersonateAsync(default)).Result);
                return true;
            }
            catch (AdaPanel.Core.Common.AppException error) when (error.HttpStatusCode == 401) { return false; }
        }

        internal async Task<IActionResult> RenewFactor(string operation)
        {
            await using var db = NewContext();
            await using var services = Services(db);
            var controller = Controller(db, services, Authentication.Principal!);
            return operation == "confirm"
                ? (await controller.ConfirmTwoFactorSetupAsync(new VerifyTwoFactorRequest { Code = "123456" }, default)).Result!
                : (await controller.DisableTwoFactorAsync(new DisableTwoFactorRequest { Password = Password }, default)).Result!;
        }

        public ValueTask DisposeAsync()
        {
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-delegation-lifecycle-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Boundary : IAuthenticationService
    {
        internal int Issued { get; private set; }
        internal int ValidationSignOuts { get; set; }
        internal ClaimsPrincipal? Principal { get; private set; }
        internal AuthenticationProperties? Properties { get; private set; }
        internal Func<Task>? BeforeSigningIn { get; set; }
        internal static AuthenticationScheme Scheme() => new(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

        public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            if (BeforeSigningIn is { } beforeSigningIn) await beforeSigningIn();
            var signing = new CookieSigningInContext(context, Scheme(), new CookieAuthenticationOptions(), principal,
                properties ?? new AuthenticationProperties(), new CookieOptions());
            await CookieSessionValidator.OnSigningInAsync(signing);
            Principal = signing.Principal;
            Properties = signing.Properties;
            Issued++;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        { ValidationSignOuts++; return Task.CompletedTask; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(context.User, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value["protected:".Length..];
    }

    private sealed class Totp : ITotpService
    {
        public string GenerateSecret() => "pending-secret";
        public string BuildProvisioningUri(string issuer, string accountName, string secret) => "otpauth://fixture/" + secret;
        public long? VerifyCode(string secret, string code) => secret == "pending-secret" && code == "123456" ? 100 : null;
    }
}
