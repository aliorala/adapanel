using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileReadPostPolicyTests
{
    [Theory]
    [InlineData("/api/v1/files/search", false, "Customer")]
    [InlineData("/api/v1/files/size", false, "Customer")]
    [InlineData("/api/v1/files/search", true, "Customer")]
    [InlineData("/api/v1/files/size", true, "Customer")]
    [InlineData("/api/v1/files/search", false, "Reseller")]
    [InlineData("/api/v1/files/size", true, "Reseller")]
    [InlineData("/API/V1/FILES/SEARCH/", true, "Customer")]
    [InlineData("/API/V1/FILES/SIZE/", false, "Customer")]
    public async Task SuspendedTenantsAndReadOnlyTokensCanReachExactFileInspectionPosts(string path, bool token, string role)
    {
        using var services = Services();
        var context = Context(services, token, role, "POST", path);
        await new SubscriptionSuspensionFilter().OnAuthorizationAsync(context);
        new PanelTokenCapabilityFilter().OnAuthorization(context);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/files/search")]
    [InlineData("PATCH", "/api/v1/files/search")]
    [InlineData("DELETE", "/api/v1/files/search")]
    [InlineData("PUT", "/api/v1/files/size")]
    [InlineData("PATCH", "/api/v1/files/size")]
    [InlineData("DELETE", "/api/v1/files/size")]
    [InlineData("POST", "/api/v1/files/search/extra")]
    [InlineData("POST", "/api/v1/files/size/extra")]
    [InlineData("POST", "/api/v1/files/search-replace")]
    [InlineData("POST", "/api/v1/files/resize")]
    [InlineData("POST", "/api/v1/files/extract")]
    [InlineData("POST", "/api/v1/files/acl")]
    [InlineData("POST", "/api/v1/files/write")]
    [InlineData("POST", "/api/v1/files/compress")]
    public async Task TheReadExceptionDoesNotAdmitOtherMethodsOrNeighboringMutations(string method, string path)
    {
        using var services = Services();
        var context = Context(services, true, "Customer", method, path);
        var suspended = await Assert.ThrowsAsync<AppException>(() => new SubscriptionSuspensionFilter().OnAuthorizationAsync(context));
        Assert.Equal(403, suspended.HttpStatusCode);
        Assert.Equal("subscription.suspended_readonly", suspended.MessageKey);
        var readOnly = Assert.Throws<AppException>(() => new PanelTokenCapabilityFilter().OnAuthorization(context));
        Assert.Equal(403, readOnly.HttpStatusCode);
        Assert.Equal(PanelTokenCapabilities.ReadOnlyReason, readOnly.Details);
    }

    [Theory]
    [InlineData("/api/v1/files/search")]
    [InlineData("/api/v1/files/size")]
    public async Task CookieInspectionPostsStillRequireCsrfValidation(string path)
    {
        using var services = Services();
        var context = Context(services, false, "Customer", "POST", path);
        var validator = DispatchProxy.Create<IAntiforgery, AntiforgeryProxy>();
        await Assert.ThrowsAsync<AntiforgeryValidationException>(() => new PanelAntiforgeryFilter(validator).OnAuthorizationAsync(context));
        Assert.Equal(1, ((AntiforgeryProxy)(object)validator).Calls);
        Assert.True(PanelAuthentication.IsStateChanging("POST"));
    }

    [Fact]
    public async Task ExistingSelectedDownloadRemainsReadableAndTerminalRemainsSeparatelyRestricted()
    {
        using var services = Services();
        var context = Context(services, true, "Customer", "POST", "/api/v1/backup/download-items");
        await new SubscriptionSuspensionFilter().OnAuthorizationAsync(context);
        new PanelTokenCapabilityFilter().OnAuthorization(context);
        context.HttpContext.Request.Path = "/api/v1/terminal/execute";
        var error = Assert.Throws<AppException>(() => new PanelTokenCapabilityFilter().OnAuthorization(context));
        Assert.Equal(PanelTokenCapabilities.TerminalReason, error.Details);
    }

    private static readonly Guid UserId = Guid.NewGuid();
    private static ServiceProvider Services() => new ServiceCollection()
        .AddSingleton(DispatchProxy.Create<ICustomerScopeService, ScopeProxy>()).BuildServiceProvider();
    private static AuthorizationFilterContext Context(IServiceProvider services, bool token, string role, string method, string path)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), new Claim(ClaimTypes.Role, role),
            new Claim(PanelTokenAuthenticationHandler.AllowDestructiveClaim, "false"),
            new Claim(PanelTokenAuthenticationHandler.AllowTerminalClaim, "false")
        ], token ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme));
        var http = new DefaultHttpContext { User = principal, RequestServices = services };
        http.Request.Method = method; http.Request.Path = path;
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
    }
    public class ScopeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GetScopeAsync", method!.Name);
            Assert.Equal(UserId, Assert.IsType<Guid>(args![0]));
            return Task.FromResult(new CustomerScope(["tenant-site"], 1, 100, 1, 0, 0,
                SuspendedAtUtc: DateTime.UtcNow, SuspendReason: "manual"));
        }
    }
    public class AntiforgeryProxy : DispatchProxy
    {
        public int Calls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("ValidateRequestAsync", method!.Name);
            Calls++;
            return Task.FromException(new AntiforgeryValidationException("Fixture requires the normal CSRF validation."));
        }
    }
}
