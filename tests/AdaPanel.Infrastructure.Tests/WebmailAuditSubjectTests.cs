using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailAuditSubjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAndBrokerRevocation_UseTheMeasuredMailboxTenant(bool revoke)
    {
        var owner = Guid.NewGuid(); Guid? captured = null;
        var audit = Stub<IAuditService>((method, args) =>
        {
            Assert.Equal("WriteForSubscriptionAsync", method.Name);
            captured = (Guid?)args[4];
            return Task.CompletedTask;
        });
        if (revoke)
        {
            var controller = new WebmailSsoController(null!, null!, null!, null!, Scopes(owner), null!, audit, Sites())
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            await controller.AuditRevocationAsync("example.test", default);
        }
        else
        {
            var controller = new WebmailHandoffController(null!, null!, Scopes(owner), null!, audit, Sites())
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            await controller.AuditHandoffAsync(new(new ClaimsPrincipal(), "authority", "example.test", "box@example.test",
                "account", "webmail.example.test", "https://panel.test", DateTimeOffset.UtcNow.AddMinutes(1)), default);
        }
        Assert.Equal(owner, captured);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAudit_UsesMeasuredDomainOwnerAndKeepsGenericOpeningUnscoped(bool domainSelected)
    {
        var owner = Guid.NewGuid();
        Guid? captured = Guid.Empty;
        var scopes = Scopes(owner);
        var audit = Stub<IAuditService>((method, args) =>
        {
            Assert.Equal("WriteForSubscriptionAsync", method.Name);
            captured = (Guid?)args[4];
            return Task.CompletedTask;
        });
        var controller = new MailController(null!, null!, scopes, Sites(), audit)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        await controller.AuditWebmailOpenedAsync(new WebmailOpenRequest
        { Domain = domainSelected ? "example.test" : null, Address = domainSelected ? "box@example.test" : null }, default);
        Assert.Equal(domainSelected ? owner : (Guid?)null, captured);
    }

    [Fact]
    public async Task SsoAudit_PreservesAdministratorActorAndTargetsMailboxTenant()
    {
        var owner = Guid.NewGuid(); var actor = Guid.NewGuid();
        Guid? capturedOwner = null, capturedActor = null;
        var audit = Stub<IAuditService>((method, args) =>
        {
            Assert.Equal("WriteForSubscriptionAsync", method.Name);
            capturedActor = (Guid?)args[3]; capturedOwner = (Guid?)args[4];
            return Task.CompletedTask;
        });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ToString()),
            new(ClaimTypes.Role, "Administrator")], "fixture"));
        var controller = new WebmailSsoController(null!, null!, null!, null!, Scopes(owner), null!, audit, Sites())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        await controller.AuditSignonAsync(new(principal, "authority", "example.test", "box@example.test", "account",
            "webmail.example.test", "https://panel.test", DateTimeOffset.UtcNow.AddMinutes(1)), default);
        Assert.Equal(actor, capturedActor); Assert.Equal(owner, capturedOwner);
    }

    private static ICustomerScopeService Scopes(Guid owner) => Stub<ICustomerScopeService>((method, args) =>
        method.Name == "GetSiteOwnerUserIdAsync" ? Task.FromResult((string)args[0]! == "technical-site" ? (Guid?)owner : null)
            : throw new NotSupportedException(method.Name));
    private static IIisSiteService Sites() => Stub<IIisSiteService>((method, _) =>
        method.Name == "GetSitesAsync" ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "technical-site",
            Bindings = [new() { HostName = "example.test" }] }]) : throw new NotSupportedException(method.Name));
    private static T Stub<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
