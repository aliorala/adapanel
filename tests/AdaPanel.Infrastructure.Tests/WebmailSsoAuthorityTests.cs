using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AdaPanel.Api.Middleware;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailSsoAuthorityTests
{
    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("password")]
    [InlineData("role")]
    [InlineData("domain")]
    [InlineData("section")]
    [InlineData("parent")]
    [InlineData("suspended")]
    [InlineData("expired")]
    [InlineData("session")]
    public async Task AuthorityCannotSurviveRevocationBetweenIssueAndConsume(string mutation)
    {
        using var f = await Fixture.Create();
        await f.Capture();
        switch (mutation)
        {
            case "inactive": f.Actor.IsActive = false; break;
            case "deleted": f.Db.Users.Remove(f.Actor); break;
            case "password": f.Actor.PasswordHash = "changed"; break;
            case "role": f.Actor.Role = UserRole.Administrator; break;
            case "domain": f.Domains = []; break;
            case "section": f.Sections = []; break;
            case "parent": f.Parent.IsActive = false; break;
            case "suspended": f.Suspended = true; break;
            case "expired": f.Expired = true; break;
            case "session": await f.Sessions.RevokeAsync(f.SessionId); break;
        }
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<AppException>(() => f.Capture());
    }

    [Fact]
    public async Task CrossDomainScopeFailsEvenWhenMailboxNameIsIdentical()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<AppException>(() => WebmailActorAuthority.CaptureAsync(f.Db, f.Scopes, f.Sessions, f.Principal, "other.test", default));
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("local")]
    [InlineData("hostname")]
    [InlineData("forwarded")]
    [InlineData("key")]
    public async Task WrongBrokerCannotConsumeAnyExistingTicket(string mutation)
    {
        using var f = await Fixture.Create();
        var deployment = await f.Deployment();
        var tickets = new WebmailSsoTickets();
        var ticket = tickets.Issue(f.Principal, "authority", "example.test", "selected@example.test", "opaque", "webmail.example.test", "https://panel.example.test");
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(mutation == "hostname" ? "panel.example.test" : "localhost");
        http.Connection.RemoteIpAddress = IPAddress.Parse(mutation == "remote" ? "203.0.113.2" : "127.0.0.1");
        http.Connection.LocalIpAddress = IPAddress.Parse(mutation == "local" ? "203.0.113.3" : "127.0.0.1");
        http.Request.Headers["X-AdaPanel-Webmail-Broker"] = new string(mutation == "key" ? 'B' : 'A', 64);
        if (mutation == "forwarded") http.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        var controller = new WebmailSsoController(tickets, deployment, null!, f.Db, f.Scopes, f.Sessions, null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        await Assert.ThrowsAsync<AppException>(() => controller.ConsumeAsync(new() { Ticket = ticket, Host = "webmail.example.test", Origin = "https://panel.example.test" }, default));
        Assert.Equal("selected@example.test", tickets.Take(ticket, "webmail.example.test", "https://panel.example.test").Address);
    }

    [Fact]
    public async Task BrokerKeyComparisonAcceptsOnlyExactPersistentPoolKey()
    {
        using var f = await Fixture.Create();
        var deployment = await f.Deployment();
        Assert.True(await deployment.AuthenticateBrokerAsync(new string('A', 64), default));
        Assert.False(await deployment.AuthenticateBrokerAsync(new string('B', 64), default));
        Assert.False(await deployment.AuthenticateBrokerAsync(new string('A', 63), default));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task ApiTokenRevocationIsRecheckedWithoutRetainingRawToken(string state)
    {
        using var f = await Fixture.Create();
        var token = new PanelApiToken { UserId = f.Actor.Id, TokenHash = "fixture-digest", Name = "fixture" };
        f.Db.ApiTokens.Add(token);
        await f.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, f.Actor.Id.ToString()), new(ClaimTypes.Role, "Customer"), new("adapanel_webmail_token_hash", token.TokenHash)
        ], PanelTokenAuthenticationHandler.SchemeName));
        await WebmailActorAuthority.CaptureAsync(f.Db, f.Scopes, f.Sessions, principal, "example.test", default);
        if (state == "revoked") token.RevokedAtUtc = DateTime.UtcNow;
        else token.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<AppException>(() => WebmailActorAuthority.CaptureAsync(f.Db, f.Scopes, f.Sessions, principal, "example.test", default));
    }

    [Fact]
    public async Task ManagedDeploymentPreservesConfigAndKeyWhileInstallingEmbeddedPlugin()
    {
        using var f = await Fixture.Create();
        var deployment = await f.Deployment();
        var root = f.ConfigRoot;
        var path = Path.Combine(root, "config", "config.inc.php");
        await File.WriteAllTextAsync(path, "<?php\n// Managed by AdaPanel\n$config = [];\n$config['des_key'] = 'keep-existing-key';\n$config['custom'] = 'preserve-me';\n");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "adapanel-trusted-roots.pem"), "fixture-public-roots");
        await deployment.PrepareAsync(default);
        // A setting after the managed block must survive a second preparation.
        await File.AppendAllTextAsync(path, "$config['after_sso'] = 'also-preserve';\n");
        await deployment.PrepareAsync(default);
        var config = await File.ReadAllTextAsync(path);
        Assert.Contains("keep-existing-key", config);
        Assert.Contains("preserve-me", config);
        Assert.Contains("also-preserve", config);
        Assert.Equal(1, config.Split("// AdaPanel SSO settings").Length - 1);
        Assert.DoesNotContain("ADMIN-FIXTURE-ONLY", config);
        Assert.Contains("session.cookie_domain", config);
        Assert.Contains("? 'None' : 'Lax'", config);
        Assert.Equal(new string('A', 64), await File.ReadAllTextAsync(Path.Combine(root, "config", "adapanel-sso-broker.key")));
        var plugin = await File.ReadAllTextAsync(Path.Combine(root, "plugins", "adapanel_sso", "adapanel_sso.php"));
        Assert.Contains("add_hook('authenticate'", plugin);
        Assert.Contains("add_hook('session_destroy'", plugin);
        Assert.DoesNotContain("logout_actions", plugin);
        Assert.DoesNotContain("$_GET", plugin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("https://attacker.test")]
    [InlineData("https://panel.example.test:8443")]
    public async Task HandoffRequiresExactCurrentOriginWithoutConsumingGrant(string origin)
    {
        using var f = await Fixture.Create();
        var tickets = new WebmailSsoTickets();
        var ticket = await f.Issue(tickets);
        var controller = f.Handoff(tickets, origin);
        await Assert.ThrowsAsync<AppException>(() => controller.PostAsync(ticket, default));
        Assert.Equal(f.Actor.Id.ToString(), tickets.Inspect(ticket, f.Actor.Id, "https://panel.example.test").Actor.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Theory]
    [InlineData("wrong-actor")]
    [InlineData("revoked-actor")]
    [InlineData("expired-ticket")]
    [InlineData("host-injection")]
    [InlineData("token-injection")]
    public async Task HandoffNeverProducesHtmlForInvalidAuthorityOrInjectedValues(string mode)
    {
        using var f = await Fixture.Create();
        var clock = new HandoffClock();
        var tickets = new WebmailSsoTickets(clock);
        var ticket = await f.Issue(tickets, mode == "host-injection" ? "webmail.example.test\"><script>evil</script>" : "webmail.example.test");
        var controller = f.Handoff(tickets);
        if (mode == "wrong-actor") controller.HttpContext.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "fixture"));
        if (mode == "revoked-actor") { f.Actor.IsActive = false; await f.Db.SaveChangesAsync(); }
        if (mode == "expired-ticket") clock.Now = clock.Now.AddSeconds(60);
        if (mode == "token-injection") ticket = "\"><script>evil</script>";
        await Assert.ThrowsAsync<AppException>(() => controller.PostAsync(ticket, default));
        Assert.False(controller.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task HandoffHasFreshNarrowPolicyAndOnlyPostBodyTicketWhileBrokerRemainsOneUse()
    {
        using var f = await Fixture.Create();
        var tickets = new WebmailSsoTickets();
        var ticket = await f.Issue(tickets);
        var controller = f.Handoff(tickets);
        PanelSecurityHeaders.Apply(controller.HttpContext); // Real middleware runs before the endpoint.
        var result = Assert.IsType<ContentResult>(await controller.PostAsync(ticket, default));
        Assert.Equal(f.Parent.Id, f.LastAuditSubject);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Equal("strict-origin", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        var csp = controller.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("form-action https://webmail.example.test;", csp);
        Assert.Contains("base-uri 'none'; frame-ancestors 'none'", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
        var nonce = Regex.Match(csp, "script-src 'nonce-([^']+)'\\;").Groups[1].Value;
        Assert.True(Convert.FromBase64String(nonce).Length >= 32);
        Assert.Contains("nonce=\"" + HtmlEncoder.Default.Encode(nonce) + "\"", result.Content);
        Assert.Contains("method=\"post\" action=\"" + HtmlEncoder.Default.Encode("https://webmail.example.test/") + "\"", result.Content);
        Assert.Contains("name=\"adapanel_sso\" value=\"" + HtmlEncoder.Default.Encode(ticket) + "\"", result.Content);
        Assert.Contains("window.opener=null;", result.Content);
        Assert.DoesNotContain("?", result.Content);
        Assert.DoesNotContain("app_", result.Content);
        Assert.DoesNotContain("password", result.Content);
        tickets.Take(ticket, "webmail.example.test", "https://panel.example.test");
        Assert.Throws<AppException>(() => tickets.Take(ticket, "webmail.example.test", "https://panel.example.test"));
        Assert.False(SecureEntryPathMiddleware.RequiresSecureEntryHeader("/api/v1/mail/webmail/handoff"));
        Assert.True(SecureEntryPathMiddleware.RequiresSecureEntryHeader("/api/v1/mail/webmail/handoff/extra"));
        Assert.True(SecureEntryPathMiddleware.RequiresSecureEntryHeader("/api/v1/mail/webmail/open"));
    }

    private sealed class HandoffClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        internal readonly AdaPanelDbContext Db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal readonly PanelUser Actor = new() { UserName = "fixture-actor", Email = "actor@example.test", Role = UserRole.Customer, PasswordHash = "fixture-password" };
        internal readonly PanelUser Parent = new() { UserName = "fixture-parent", Email = "parent@example.test", Role = UserRole.Customer, PasswordHash = "fixture-parent" };
        internal string[] Domains = ["example.test"];
        internal string[] Sections = ["mail"];
        internal bool Suspended;
        internal bool Expired;
        internal readonly string SessionId = SessionRevocationStore.NewSessionId();
        internal ClaimsPrincipal Principal = null!;
        internal ICustomerScopeService Scopes = null!;
        internal SessionRevocationStore Sessions = null!;
        internal string ConfigRoot = "";
        internal Guid? LastAuditSubject;

        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Db.Database.OpenConnectionAsync();
            await f.Db.Database.EnsureCreatedAsync();
            f.Db.Users.AddRange(f.Actor, f.Parent);
            await f.Db.SaveChangesAsync();
            f.Principal = new(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, f.Actor.Id.ToString()), new(ClaimTypes.Role, "Customer"),
                new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(f.Actor.PasswordHash)),
                new(SessionRevocationStore.SessionIdClaim, f.SessionId)
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            f.Sessions = new(f.area.DosyaDeposu);
            f.Scopes = DispatchProxy.Create<ICustomerScopeService, ScopeProxy>();
            ((ScopeProxy)f.Scopes).Handler = (method, args) => method.Name switch
            {
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>((Guid)args![0]! == f.Actor.Id ? f.Parent.Id : null),
                "GetSubUserSectionsAsync" => Task.FromResult<IReadOnlyCollection<string>?>(f.Sections),
                "GetAllowedMailDomainsAsync" => Task.FromResult<IReadOnlyCollection<string>>(f.Domains),
                "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>((string)args![0]! == "example.test" ? f.Parent.Id : null),
                "GetScopeAsync" => Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0, 0, SuspendedAtUtc: f.Suspended ? DateTime.UtcNow : null,
                    ExpiresAtUtc: f.Expired ? DateTime.UtcNow.AddSeconds(-1) : null)),
                _ => throw new NotSupportedException(method.Name)
            };
            return f;
        }
        internal Task<string> Capture() => WebmailActorAuthority.CaptureAsync(Db, Scopes, Sessions, Principal, "example.test", default);
        internal async Task<string> Issue(WebmailSsoTickets tickets, string host = "webmail.example.test") =>
            tickets.Issue(Principal, await Capture(), "example.test", "selected@example.test", "opaque", host, "https://panel.example.test");
        internal WebmailHandoffController Handoff(WebmailSsoTickets tickets, string origin = "https://panel.example.test")
        {
            var http = new DefaultHttpContext { User = Principal };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("panel.example.test");
            http.Request.Headers.Origin = origin;
            http.Request.Method = "POST";
            http.Request.Path = "/api/v1/mail/webmail/handoff";
            var audit = DispatchProxy.Create<IAuditService, ScopeProxy>();
            ((ScopeProxy)(object)audit).Handler = (method, args) =>
            {
                Assert.Equal("WriteForSubscriptionAsync", method.Name);
                LastAuditSubject = (Guid?)args![4];
                return Task.CompletedTask;
            };
            return new(tickets, Db, Scopes, Sessions, audit, null!) { ControllerContext = new ControllerContext { HttpContext = http } };
        }
        internal async Task<WebmailSsoDeployment> Deployment()
        {
            var root = area.DosyaDeposu.EnsureDirectory("roundcube");
            ConfigRoot = root;
            Directory.CreateDirectory(Path.Combine(root, "public_html"));
            Directory.CreateDirectory(Path.Combine(root, "program"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            await File.WriteAllTextAsync(Path.Combine(root, "config", "adapanel-sso-broker.key"), new string('A', 64));
            await area.DosyaDeposu.WriteAsync("config/webmail.json", new { entryRoot = Path.Combine(root, "public_html") });
            return new(area.DosyaDeposu, new ConfigurationBuilder().Build(), Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = area.Kok }));
        }
        public void Dispose() { Db.Dispose(); area.Dispose(); }
    }

    public class ScopeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
