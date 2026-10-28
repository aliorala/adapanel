using System.Net;
using System.Reflection;
using AdaPanel.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailBrokerBoundaryTests
{
    private static bool Allowed(DefaultHttpContext context) => (bool)typeof(SecureEntryPathMiddleware)
        .GetMethod("IsDirectWebmailBrokerRequest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context])!;
    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "POST";
        context.Request.Host = new HostString("localhost", 8890);
        context.Request.Path = "/api/internal/webmail-sso/consume";
        return context;
    }
    [Theory]
    [InlineData("consume")]
    [InlineData("revoke")]
    public void OnlyTheTwoPrivatePostEndpointsCanUseBrokerAuthentication(string action)
    {
        var context = Request(); context.Request.Path = "/api/internal/webmail-sso/" + action;
        Assert.True(Allowed(context));
    }
    [Theory]
    [InlineData("/api/internal/webmail-sso")]
    [InlineData("/api/internal/webmail-sso/consume/extra")]
    [InlineData("/api/internal/webmail-sso/issue")]
    [InlineData("/api/v1/mail/webmail/open")]
    public void OtherRoutesRemainBehindNormalPanelChecks(string path)
    {
        var context = Request(); context.Request.Path = path; Assert.False(Allowed(context));
    }
    [Fact]
    public void ARemoteClientCannotUseALocalhostHostHeader()
    {
        var context = Request(); context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10"); Assert.False(Allowed(context));
        context = Request(); context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.11"); Assert.False(Allowed(context));
    }
    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    public void ForwardedRequestsCannotClaimThePrivateExemption(string header)
    {
        var context = Request(); context.Request.Headers[header] = "127.0.0.1"; Assert.False(Allowed(context));
    }
    [Fact]
    public void HostAndMethodAreMandatory()
    {
        var context = Request(); context.Request.Method = "GET"; Assert.False(Allowed(context));
        context = Request(); context.Request.Host = new HostString("panel.example"); Assert.False(Allowed(context));
        context = Request(); context.Connection.RemoteIpAddress = null; Assert.False(Allowed(context));
    }
}
