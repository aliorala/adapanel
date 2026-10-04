using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Dtos.Auth;
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

public sealed class ImpersonationCookieAuthorityTests
{
    [Theory]
    [InlineData("impersonate", "logout")]
    [InlineData("impersonate", "password")]
    [InlineData("impersonate", "role")]
    [InlineData("impersonate", "inactive")]
    [InlineData("impersonate", "deleted")]
    [InlineData("return", "logout")]
    [InlineData("return", "password")]
    [InlineData("return", "role")]
    [InlineData("return", "inactive")]
    [InlineData("return", "deleted")]
    public async Task AcceptedTargetDoesNotAuthorizeAChangedSourceActor(string operation, string change)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            Assert.NotEqual(fixture.SourceId, fixture.TargetId);
            await fixture.Change(fixture.SourceId, change, deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            var error = Assert.IsType<AppException>(await request);
            Assert.Equal(401, error.HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
            Assert.Empty(await fixture.Db.AuditLogs.AsNoTracking().Where(row =>
                row.Action == "auth.impersonate.started" || row.Action == "auth.impersonate.stopped").ToArrayAsync());
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("password")]
    [InlineData("role")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    public async Task ReturningAlsoRequiresTheAcceptedImpersonatorTargetToRemainUnchanged(string change)
    {
        await using var fixture = await Fixture.Create("return");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            // In the return operation the administrator is the TARGET. Checking only this
            // row cannot authorize the customer cookie that requested the return.
            await fixture.Change(fixture.TargetId, change, deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            Assert.Equal(401, Assert.IsType<AppException>(await request).HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("impersonate")]
    [InlineData("return")]
    public async Task UnchangedSourceAndTargetPublishOneUsableCookie(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = fixture.Invoke(deadline.Token);
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            fixture.Authentication.Release.TrySetResult();
            Assert.IsType<OkObjectResult>(await request);
            Assert.Equal(1, fixture.Authentication.Issued);
            var principal = fixture.Authentication.Principal!;
            Assert.Equal(fixture.TargetId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal(operation == "return" ? "Administrator" : "Customer", principal.FindFirstValue(ClaimTypes.Role));
            Assert.Equal(operation == "impersonate" ? fixture.SourceId.ToString() : null,
                principal.FindFirstValue("impersonator_id"));
            Assert.NotEqual(fixture.SourceSessionId, principal.FindFirstValue(SessionRevocationStore.SessionIdClaim));
            Assert.Empty(fixture.Authentication.Properties!.Parameters);
            Assert.True(await fixture.Authentication.Validate(fixture.Controller.HttpContext));
            Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().Where(row =>
                row.Action == "auth.impersonate.started" || row.Action == "auth.impersonate.stopped").ToArrayAsync());
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    [Theory]
    [InlineData("impersonate")]
    [InlineData("return")]
    public async Task CancellationAfterTargetAcceptanceDoesNotPublishACookie(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var request = Record.ExceptionAsync(() => fixture.Invoke(cancellation.Token));
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

    [Theory]
    [InlineData("impersonate", "stamp")]
    [InlineData("return", "stamp")]
    [InlineData("impersonate", "session")]
    [InlineData("return", "session")]
    [InlineData("impersonate", "token")]
    [InlineData("return", "token")]
    public async Task DelegationDoesNotUpgradeAnUnboundOrNonCookiePrincipal(string operation, string missing)
    {
        await using var fixture = await Fixture.Create(operation);
        var identity = (ClaimsIdentity)fixture.Controller.User.Identity!;
        if (missing == "token")
            fixture.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(identity.Claims, PanelTokenAuthenticationHandler.SchemeName));
        else
            identity.RemoveClaim(identity.FindFirst(missing == "stamp"
                ? CookieSessionValidator.CredentialStampClaim : SessionRevocationStore.SessionIdClaim)!);
        fixture.Authentication.Release.TrySetResult();

        Assert.Equal(401, (await Assert.ThrowsAsync<AppException>(() => fixture.Invoke(default))).HttpStatusCode);
        Assert.Equal(0, fixture.Authentication.Issued);
        Assert.False(fixture.Authentication.Reached.Task.IsCompleted);
    }

    [Theory]
    [InlineData("impersonate")]
    [InlineData("return")]
    public async Task PublicationRetainsTheOriginalSourceSnapshotInsteadOfReadingAMutatedPrincipal(string operation)
    {
        await using var fixture = await Fixture.Create(operation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Record.ExceptionAsync(() => fixture.Invoke(deadline.Token));
        try
        {
            await fixture.Authentication.Reached.Task.WaitAsync(deadline.Token);
            await fixture.Revocations.RevokeAsync(fixture.SourceSessionId, deadline.Token);
            var identity = (ClaimsIdentity)fixture.Controller.User.Identity!;
            identity.RemoveClaim(identity.FindFirst(SessionRevocationStore.SessionIdClaim)!);
            identity.AddClaim(new(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId()));
            fixture.Authentication.Release.TrySetResult();

            Assert.Equal(401, Assert.IsType<AppException>(await request).HttpStatusCode);
            Assert.Equal(0, fixture.Authentication.Issued);
        }
        finally { fixture.Authentication.Release.TrySetResult(); await request; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Password = "Source!Password123";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-impersonation-cookie-" + Guid.NewGuid().ToString("N"));
        private readonly PasswordHasher<PanelUser> hasher = new();
        private readonly ServiceProvider services;
        private readonly string operation;
        private readonly Guid adminId = Guid.NewGuid();
        private readonly Guid customerId = Guid.NewGuid();
        internal Guid SourceId => operation == "impersonate" ? adminId : customerId;
        internal Guid TargetId => operation == "impersonate" ? customerId : adminId;
        internal string SourceSessionId { get; } = SessionRevocationStore.NewSessionId();
        internal AdaPanelDbContext Db { get; }
        internal JsonFileStore Store { get; }
        internal SessionRevocationStore Revocations { get; }
        internal Boundary Authentication { get; } = new();
        internal AuthController Controller { get; }

        private Fixture(string operation)
        {
            this.operation = operation;
            Directory.CreateDirectory(root);
            Db = NewContext();
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Revocations = new SessionRevocationStore(Store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(Revocations)
                .AddSingleton<IAuthenticationService>(Authentication).BuildServiceProvider();
            Controller = new AuthController(null!, Service(Db), new AuditService(Db, null!, Store), null!, Revocations)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "auth.db")};Pooling=False").Options);

        private AuthService Service(AdaPanelDbContext database) => new(database, hasher, null!, null!,
            new AuditService(database, null!, Store), new CountingBruteForceProtection(), null!, null!, null!, new ApiTokenService(database), Store,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, NullLogger<AuthService>.Instance);

        internal static async Task<Fixture> Create(string operation)
        {
            var fixture = new Fixture(operation);
            await fixture.Db.Database.EnsureCreatedAsync();
            var admin = new PanelUser { Id = fixture.adminId, UserName = "source-admin", Email = "admin@example.test", Role = UserRole.Administrator, IsActive = true };
            var customer = new PanelUser { Id = fixture.customerId, UserName = "target-customer", Email = "customer@example.test", Role = UserRole.Customer, IsActive = true };
            foreach (var user in new[] { admin, customer })
            {
                user.PasswordHash = fixture.hasher.HashPassword(user, Password);
                fixture.Db.Users.Add(user);
            }
            await fixture.Db.SaveChangesAsync();
            var source = operation == "impersonate" ? admin : customer;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, source.Id.ToString()), new(ClaimTypes.Name, source.UserName),
                new(ClaimTypes.Role, source.Role.ToString()),
                new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(source.PasswordHash)),
                new(SessionRevocationStore.SessionIdClaim, fixture.SourceSessionId)
            };
            if (operation == "return")
            {
                claims.Add(new("impersonator_id", admin.Id.ToString()));
                claims.Add(new("impersonator_name", admin.UserName));
                claims.Add(new(CookieSessionValidator.DelegatingCredentialStampClaim, CookieSessionValidator.ComputeStamp(admin.PasswordHash)));
                claims.Add(new(CookieSessionValidator.DelegatingSessionIdClaim, SessionRevocationStore.NewSessionId()));
            }
            fixture.Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            return fixture;
        }

        internal async Task<IActionResult> Invoke(CancellationToken token)
        {
            Controller.HttpContext.RequestAborted = token;
            return operation == "impersonate"
                ? (await Controller.ImpersonateAsync(TargetId, token)).Result!
                : (await Controller.StopImpersonateAsync(token)).Result!;
        }

        internal async Task Change(Guid userId, string change, CancellationToken token)
        {
            if (change == "logout")
            {
                await Revocations.RevokeAsync(SourceSessionId, token);
                return;
            }
            await using var database = NewContext();
            if (change == "password")
            {
                await Service(database).ChangePasswordAsync(userId,
                    new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "Changed!Password456" }, null, token);
                return;
            }
            var user = await database.Users.SingleAsync(row => row.Id == userId, token);
            switch (change)
            {
                case "role": user.Role = user.Role == UserRole.Administrator ? UserRole.Customer : UserRole.Reseller; break;
                case "inactive": user.IsActive = false; break;
                case "deleted": database.Users.Remove(user); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            await database.SaveChangesAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            Authentication.Release.TrySetResult();
            await services.DisposeAsync();
            await Db.DisposeAsync();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-impersonation-cookie-"), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    private sealed class Boundary : IAuthenticationService
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Issued { get; private set; }
        internal ClaimsPrincipal? Principal { get; private set; }
        internal AuthenticationProperties? Properties { get; private set; }
        private static AuthenticationScheme Scheme() => new(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

        public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var signing = new CookieSigningInContext(context, Scheme(), new CookieAuthenticationOptions(), principal,
                properties ?? new AuthenticationProperties(), new CookieOptions());
            await CookieSessionValidator.OnSigningInAsync(signing);
            Principal = signing.Principal;
            Properties = signing.Properties;
            Issued++;
        }
        internal async Task<bool> Validate(HttpContext context)
        {
            var validation = new CookieValidatePrincipalContext(context, Scheme(), new CookieAuthenticationOptions(),
                new AuthenticationTicket(Principal!, Properties!, CookieAuthenticationDefaults.AuthenticationScheme));
            await CookieSessionValidator.OnValidatePrincipalAsync(validation);
            return validation.Principal?.Identity?.IsAuthenticated == true;
        }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(context.User, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme)));
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
