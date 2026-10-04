using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Security;
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

public sealed class CookieIssuanceAuthorityTests
{
    [Theory]
    [InlineData("login", "password")]
    [InlineData("confirm", "password")]
    [InlineData("disable", "password")]
    [InlineData("login", "role")]
    [InlineData("confirm", "role")]
    [InlineData("disable", "role")]
    [InlineData("login", "inactive")]
    [InlineData("confirm", "inactive")]
    [InlineData("disable", "inactive")]
    [InlineData("login", "deleted")]
    [InlineData("confirm", "deleted")]
    [InlineData("disable", "deleted")]
    public async Task AcceptedServiceResultCannotMintACookieForChangedIdentity(string operation, string change)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            await fixture.ChangeIdentity(change, deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            var error = Assert.IsType<AppException>(await request);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task SigningOutAfterFactorCommitCannotBeUndoneByIssuingANewSessionId(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            // The service has accepted/committed the factor mutation. This is later than
            // the R31 user lease and SQL/audit publication checks.
            Assert.Equal(operation == "confirm", await fixture.TwoFactorEnabled());
            await fixture.Revocations.RevokeAsync(fixture.OriginalSessionId, deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            var error = Assert.IsType<AppException>(await request);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
            Assert.True(await fixture.Revocations.IsRevokedAsync(fixture.OriginalSessionId));
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task ChangedSecondFactorCannotBeAdoptedByAnAlreadyAcceptedResult(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(operation, deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            await fixture.ChangeSecondFactor(deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            var error = Assert.IsType<AppException>(await request);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Fact]
    public async Task ReplacingAnActiveFactorCannotBeHiddenByKeepingTwoFactorEnabledTrue()
    {
        await using var fixture = await Fixture.Create("confirm");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke("confirm", deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            Assert.True(await fixture.TwoFactorEnabled());
            await fixture.ChangeSecondFactor(deadline.Token); // disable the accepted secret
            await fixture.ChangeSecondFactor(deadline.Token); // confirm a different secret
            Assert.True(await fixture.TwoFactorEnabled());
            fixture.Authentication.Release.TrySetResult();
            Assert.Equal(401, Assert.IsType<AppException>(await request).HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task UnchangedAcceptedResultIssuesOneValidCookieWithoutExposingCredentialEvidence(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = fixture.Invoke(operation, deadline.Token);
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            var response = Assert.IsType<OkObjectResult>(await request);
            Assert.Equal(1, fixture.Authentication.Issued);
            var principal = fixture.Authentication.IssuedPrincipal!;
            Assert.Equal(fixture.UserId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal("Administrator", principal.FindFirstValue(ClaimTypes.Role));
            Assert.Equal(CookieSessionValidator.ComputeStamp(await fixture.PasswordHash()),
                Assert.Single(principal.FindAll(CookieSessionValidator.CredentialStampClaim)).Value);
            var sessionId = Assert.Single(principal.FindAll(SessionRevocationStore.SessionIdClaim)).Value;
            Assert.NotEqual(fixture.OriginalSessionId, sessionId);
            Assert.False(await fixture.Revocations.IsRevokedAsync(sessionId));
            Assert.True(await fixture.Authentication.IsIssuedCookieValid(fixture.Controller.HttpContext));
            Assert.Empty(fixture.Authentication.RemainingParameters);
            var json = JsonSerializer.Serialize(response.Value);
            Assert.DoesNotContain(await fixture.PasswordHash(), json, StringComparison.Ordinal);
            Assert.DoesNotContain(CookieSessionValidator.ComputeStamp(await fixture.PasswordHash()), json, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.OriginalSessionId, json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("Data");
            var user = operation == "disable" ? data : data.GetProperty("User");
            Assert.Equal(new[] { "Email", "Id", "IsActive", "IsTwoFactorEnabled", "LastLoginAtUtc", "PreferredLanguage", "Role", "UserName" },
                user.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("sso")]
    [InlineData("impersonate")]
    [InlineData("return")]
    public async Task TrustedSsoAndAdministrativeSignInPathsCarryAcceptedIdentityEvidence(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = fixture.Invoke(operation, deadline.Token);
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            await request;
            Assert.Equal(1, fixture.Authentication.Issued);
            var expectedUserId = operation == "impersonate" ? fixture.TargetUserId : fixture.UserId;
            var principal = fixture.Authentication.IssuedPrincipal!;
            Assert.Equal(expectedUserId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal(operation == "return" ? "Administrator" : "Customer", principal.FindFirstValue(ClaimTypes.Role));
            Assert.Equal(operation == "impersonate" ? fixture.UserId.ToString() : null, principal.FindFirstValue("impersonator_id"));
            Assert.True(await fixture.Authentication.IsIssuedCookieValid(fixture.Controller.HttpContext));
            Assert.Empty(fixture.Authentication.RemainingParameters);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationEvidenceIsNeitherSerializedNorAcceptedFromJson(bool webDefaults)
    {
        var options = webDefaults ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions();
        var accepted = new CurrentUserDto { UserName = "account", AuthenticationEvidence = "private-proof" };
        var serialized = JsonSerializer.Serialize(accepted, options);
        Assert.DoesNotContain("private-proof", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationEvidence", serialized, StringComparison.OrdinalIgnoreCase);
        var parsed = JsonSerializer.Deserialize<CurrentUserDto>("{\"AuthenticationEvidence\":\"forged-proof\",\"authenticationEvidence\":\"forged-proof\"}", options)!;
        Assert.Equal(string.Empty, parsed.AuthenticationEvidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged-proof")]
    public async Task CookiePublicationRequiresTrustedAcceptedEvidence(string? evidence)
    {
        await using var fixture = await Fixture.Create("login");
        fixture.Authentication.BeforeEvent = properties =>
        {
            properties.Parameters.Remove(CookieSessionValidator.AcceptedIdentityParameter);
            if (evidence is not null) properties.Parameters[CookieSessionValidator.AcceptedIdentityParameter] = evidence;
        };
        fixture.Authentication.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Invoke("login", default));
        Assert.Equal(401, error.HttpStatusCode);
        Assert.Equal(0, fixture.Authentication.Issued);
    }

    [Fact]
    public async Task AnotherNormalLoginDoesNotRevokeAnAcceptedIdentityByUpdatingLoginTimestamps()
    {
        await using var fixture = await Fixture.Create("login");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = fixture.Invoke("login", deadline.Token);
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            await fixture.AnotherLogin(deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            Assert.IsType<OkObjectResult>(await request);
            Assert.Equal(1, fixture.Authentication.Issued);
            Assert.True(await fixture.Authentication.IsIssuedCookieValid(fixture.Controller.HttpContext));
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("confirm")]
    [InlineData("disable")]
    public async Task CanceledRequestCannotPublishACookieAfterItsServiceResultWasAccepted(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var request = Record.ExceptionAsync(() => fixture.Invoke(operation, cancellation.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            cancellation.Cancel();
            fixture.Authentication.Release.TrySetResult();
            Assert.IsAssignableFrom<OperationCanceledException>(await request.WaitAsync(deadline.Token));
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Password = "Accepted!Password123";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-cookie-issuance-" + Guid.NewGuid().ToString("N"));
        private readonly PasswordHasher<PanelUser> hasher = new();
        private readonly ServiceProvider services;
        private readonly Totp totp = new();
        internal AdaPanelDbContext Db { get; }
        internal JsonFileStore Store { get; }
        internal SessionRevocationStore Revocations { get; }
        internal AuthenticationBoundary Authentication { get; } = new();
        internal AuthController Controller { get; }
        internal Guid UserId { get; } = Guid.NewGuid();
        internal Guid TargetUserId { get; } = Guid.NewGuid();
        private string ssoToken = string.Empty;
        internal string OriginalSessionId { get; } = SessionRevocationStore.NewSessionId();

        private Fixture()
        {
            Directory.CreateDirectory(root);
            Db = NewContext();
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Revocations = new SessionRevocationStore(Store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(Revocations)
                .AddSingleton<IAuthenticationService>(Authentication).BuildServiceProvider();
            Controller = new AuthController(null!, Service(Db), new AuditService(Db, null!, Store), new FixedSecurity(), Revocations)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "auth.db")};Pooling=False").Options);

        private AuthService Service(AdaPanelDbContext database) => new(database, hasher, new Protector(), totp,
            new AuditService(database, null!, Store), new CountingBruteForceProtection(), null!, null!, null!, new ApiTokenService(database), Store,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, NullLogger<AuthService>.Instance);

        internal static async Task<Fixture> Create(string operation)
        {
            var fixture = new Fixture();
            await fixture.Db.Database.EnsureCreatedAsync();
            var user = new PanelUser
            {
                Id = fixture.UserId, UserName = "cookie-admin", Email = "cookie@example.test", Role = operation == "sso" ? UserRole.Customer : UserRole.Administrator,
                IsActive = true, IsTwoFactorEnabled = operation == "disable",
                TwoFactorSecretProtected = operation == "disable" ? "protected:active-secret" : null,
                PendingTwoFactorSecretProtected = operation == "confirm" ? "protected:pending-secret" : null
            };
            user.PasswordHash = fixture.hasher.HashPassword(user, Password);
            fixture.Db.Users.Add(user);
            PanelUser? customer = null;
            if (operation is "impersonate" or "return")
            {
                customer = new PanelUser { Id = fixture.TargetUserId, UserName = "cookie-customer", Email = "customer@example.test", Role = UserRole.Customer };
                customer.PasswordHash = fixture.hasher.HashPassword(customer, Password);
                fixture.Db.Users.Add(customer);
            }
            await fixture.Db.SaveChangesAsync();
            if (operation != "login")
                fixture.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(user.PasswordHash)),
                    new Claim(SessionRevocationStore.SessionIdClaim, fixture.OriginalSessionId)
                ], CookieAuthenticationDefaults.AuthenticationScheme));
            if (operation == "return")
                fixture.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, customer!.Id.ToString()), new Claim(ClaimTypes.Name, customer.UserName),
                    new Claim(ClaimTypes.Role, customer.Role.ToString()),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(customer.PasswordHash)),
                    new Claim(SessionRevocationStore.SessionIdClaim, fixture.OriginalSessionId),
                    new Claim("impersonator_id", user.Id.ToString()), new Claim("impersonator_name", user.UserName),
                    new Claim(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp(user.PasswordHash)),
                    new Claim(CookieSessionValidator.DelegatingSessionIdClaim, SessionRevocationStore.NewSessionId())
                ], CookieAuthenticationDefaults.AuthenticationScheme));
            if (operation == "sso")
            {
                fixture.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
                (fixture.ssoToken, _) = await SingleUseTokenStore.IssueAsync(fixture.Store, "config/sso-tokens.json", user.Id, TimeSpan.FromMinutes(1), default);
            }
            await fixture.Store.WriteAsync(TwoFactorRecoveryCodes.PathFor(user.Id), new TwoFactorRecoveryCodes.Store());
            return fixture;
        }

        internal async Task<IActionResult> Invoke(string operation, CancellationToken token)
        {
            Controller.HttpContext.RequestAborted = token;
            return operation switch
            {
                "login" => (await Controller.LoginAsync(new LoginRequest { UserNameOrEmail = "cookie-admin", Password = Password }, token)).Result!,
                "confirm" => (await Controller.ConfirmTwoFactorSetupAsync(new VerifyTwoFactorRequest { Code = "123456" }, token)).Result!,
                "disable" => (await Controller.DisableTwoFactorAsync(new DisableTwoFactorRequest { Password = Password }, token)).Result!,
                "sso" => await Controller.RedeemSsoLinkAsync(ssoToken, token),
                "impersonate" => (await Controller.ImpersonateAsync(TargetUserId, token)).Result!,
                "return" => (await Controller.StopImpersonateAsync(token)).Result!,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        internal async Task AnotherLogin(CancellationToken token)
        {
            await using var database = NewContext();
            Assert.True((await Service(database).LoginAsync(new LoginRequest { UserNameOrEmail = "cookie-admin", Password = Password }, null, token)).IsAuthenticated);
        }

        internal async Task ChangeIdentity(string change, CancellationToken token)
        {
            await using var database = NewContext();
            if (change == "password")
            {
                await Service(database).ChangePasswordAsync(UserId,
                    new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "Changed!Password456" }, null, token);
                return;
            }
            var user = await database.Users.SingleAsync(row => row.Id == UserId, token);
            switch (change)
            {
                case "role": user.Role = UserRole.Customer; break;
                case "inactive": user.IsActive = false; break;
                case "deleted": database.Users.Remove(user); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            await database.SaveChangesAsync(token);
        }

        internal async Task ChangeSecondFactor(CancellationToken token)
        {
            await using var database = NewContext();
            var service = Service(database);
            if (await database.Users.Where(row => row.Id == UserId).Select(row => row.IsTwoFactorEnabled).SingleAsync(token))
                await service.DisableTwoFactorAsync(UserId, Password, null, token);
            else
            {
                await service.BeginTwoFactorSetupAsync(UserId, token);
                await service.ConfirmTwoFactorSetupAsync(UserId, "654321", null, token);
            }
        }

        internal async Task<bool> TwoFactorEnabled()
        {
            await using var database = NewContext();
            return await database.Users.Where(row => row.Id == UserId).Select(row => row.IsTwoFactorEnabled).SingleAsync();
        }
        internal async Task<string> PasswordHash()
        {
            await using var database = NewContext();
            return await database.Users.Where(row => row.Id == UserId).Select(row => row.PasswordHash).SingleAsync();
        }
        public async ValueTask DisposeAsync()
        {
            Authentication.Release.TrySetResult();
            await services.DisposeAsync();
            await Db.DisposeAsync();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-cookie-issuance-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    private sealed class AuthenticationBoundary : IAuthenticationService
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Issued { get; private set; }
        internal ClaimsPrincipal? IssuedPrincipal { get; private set; }
        internal string[] RemainingParameters { get; private set; } = [];
        internal Action<AuthenticationProperties>? BeforeEvent { get; set; }
        private AuthenticationProperties? issuedProperties;
        private static AuthenticationScheme Scheme() => new(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(context.User, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme)));

        public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var signingIn = new CookieSigningInContext(context, Scheme(), new CookieAuthenticationOptions(), principal,
                properties ?? new AuthenticationProperties(), new CookieOptions());
            BeforeEvent?.Invoke(signingIn.Properties);
            // This is the actual registered cookie event. Count publication only after it
            // succeeds, as CookieAuthenticationHandler does before serializing the ticket.
            await CookieSessionValidator.OnSigningInAsync(signingIn);
            IssuedPrincipal = signingIn.Principal;
            issuedProperties = signingIn.Properties;
            RemainingParameters = signingIn.Properties.Parameters.Keys.ToArray();
            Issued++;
        }

        internal async Task<bool> IsIssuedCookieValid(HttpContext context)
        {
            var validation = new CookieValidatePrincipalContext(context, Scheme(), new CookieAuthenticationOptions(),
                new AuthenticationTicket(IssuedPrincipal!, issuedProperties!, CookieAuthenticationDefaults.AuthenticationScheme));
            await CookieSessionValidator.OnValidatePrincipalAsync(validation);
            return validation.Principal?.Identity?.IsAuthenticated == true;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
    private sealed class FixedSecurity : IPanelSecurityService
    {
        public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default) => Task.FromResult("panel");
        public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value["protected:".Length..];
    }
    private sealed class Totp : ITotpService
    {
        public string GenerateSecret() => "replacement-secret";
        public string BuildProvisioningUri(string issuer, string accountName, string secret) => "otpauth://fixture/" + secret;
        public long? VerifyCode(string secret, string code) => (secret, code) switch
        {
            ("pending-secret", "123456") => 100,
            ("replacement-secret", "654321") => 200,
            _ => null
        };
    }
}
