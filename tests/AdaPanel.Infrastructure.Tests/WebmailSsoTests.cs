using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailSsoTests
{
    private static readonly ClaimsPrincipal Actor = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
    private static string Issue(WebmailSsoTickets tickets) => tickets.Issue(Actor, "authority", "example.test", "selected@example.test", "opaque-ID", "webmail.example.test", "https://panel.example.test:8888");

    [Fact]
    public async Task ParallelReplayHasExactlyOneWinner()
    {
        var tickets = new WebmailSsoTickets();
        var ticket = Issue(tickets);
        Assert.Equal(64, ticket.Length);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            try { tickets.Take(ticket, "webmail.example.test", "https://panel.example.test:8888"); return 1; }
            catch (AppException) { return 0; }
        })));
        Assert.Equal(1, results.Sum());
    }

    [Theory]
    [InlineData("webmail.other.test", "https://panel.example.test:8888")]
    [InlineData("webmail.example.test", "https://attacker.test")]
    [InlineData("webmail.example.test:443", "https://panel.example.test:8888")]
    public void WrongHostOrOriginBurnsTheGrant(string host, string origin)
    {
        var tickets = new WebmailSsoTickets();
        var ticket = Issue(tickets);
        Assert.Throws<AppException>(() => tickets.Take(ticket, host, origin));
        Assert.Throws<AppException>(() => tickets.Take(ticket, "webmail.example.test", "https://panel.example.test:8888"));
    }

    [Fact]
    public void ExpiryAndUnknownTicketsFailClosed()
    {
        var clock = new Clock();
        var tickets = new WebmailSsoTickets(clock);
        var ticket = Issue(tickets);
        clock.Now = clock.Now.AddSeconds(60);
        Assert.Throws<AppException>(() => tickets.Take(ticket, "webmail.example.test", "https://panel.example.test:8888"));
        Assert.Throws<AppException>(() => tickets.Take(new string('A', 64), "webmail.example.test", "https://panel.example.test:8888"));
    }

    [Fact]
    public void RateCapPrecedesExpensivePreparationsAndRecoversAfterWindow()
    {
        var clock = new Clock();
        var tickets = new WebmailSsoTickets(clock);
        for (var i = 0; i < 10; i++) tickets.Reserve(Actor);
        Assert.Equal(429, Assert.Throws<AppException>(() => tickets.Reserve(Actor)).HttpStatusCode);
        clock.Now = clock.Now.AddMinutes(1);
        tickets.Reserve(Actor);
    }

    [Fact]
    public void TicketStorageContainsDigestAndPrincipalSnapshotOnly()
    {
        var tickets = new WebmailSsoTickets();
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("scope", "before")], "test");
        var actor = new ClaimsPrincipal(identity);
        var ticket = tickets.Issue(actor, "authority", "example.test", "selected@example.test", "opaque-ID", "webmail.example.test", "https://panel.example.test:8888");
        identity.RemoveClaim(identity.FindFirst("scope")!);
        var field = typeof(WebmailSsoTickets).GetField("grants", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var storage = Assert.IsType<Dictionary<string, WebmailSsoTickets.Grant>>(field.GetValue(tickets));
        Assert.DoesNotContain(ticket, storage.Keys);
        Assert.Equal("before", tickets.Take(ticket, "webmail.example.test", "https://panel.example.test:8888").Actor.FindFirst("scope")?.Value);
    }

    [Theory]
    [InlineData("*.example.test")]
    [InlineData("example.test/path")]
    [InlineData("webmail.example.test")]
    [InlineData("example.test:443")]
    [InlineData("example.test' ; evil")]
    public void DomainInputCannotIntroduceAliasWildcardPortOrCommand(string domain) => Assert.Throws<AppException>(() => WebmailRequestValidation.Domain(domain));

    [Fact]
    public async Task JmapUsesCanonicalPrimaryAddressAndOpaqueStableId()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-selected", "selected@example.test"), new("opaque-wrong", "other@example.test")];
        Assert.Equal("opaque-selected", await f.Client.ResolveAccountAsync("selected@example.test", default));
        await Assert.ThrowsAsync<AppException>(() => f.Client.ResolveAccountAsync("alias@example.test", default));
        await Assert.ThrowsAsync<AppException>(() => f.Client.ResolveAccountAsync("selected@other.test", default));
        f.Cloud.Accounts = [];
        await Assert.ThrowsAsync<AppException>(() => f.Client.ResolveAccountAsync("selected@example.test", default));
    }

    [Fact]
    public async Task AppPasswordHasNarrowPermissionsAbsoluteExpiryAndLoopbackIps()
    {
        using var f = await Fixture.Create();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        var result = await f.Client.CreateAsync("opaque-selected", "correlation", expiry, default);
        Assert.Equal("app_fixture_only", result.Secret);
        var args = f.Cloud.Requests.Single().GetProperty("methodCalls")[0][1];
        Assert.Equal("opaque-selected", args.GetProperty("accountId").GetString());
        var creation = args.GetProperty("create").GetProperty("sso");
        Assert.Equal("Replace", creation.GetProperty("permissions").GetProperty("@type").GetString());
        Assert.Equal(2, creation.GetProperty("allowedIps").EnumerateObject().Count());
        Assert.True(creation.GetProperty("allowedIps").GetProperty("127.0.0.1/32").GetBoolean());
        Assert.True(creation.GetProperty("allowedIps").GetProperty("::1/128").GetBoolean());
        Assert.InRange(DateTimeOffset.Parse(creation.GetProperty("expiresAt").GetString()!), expiry.AddSeconds(-1), expiry.AddSeconds(1));
        Assert.All(creation.GetProperty("permissions").GetProperty("permissions").EnumerateObject(), x =>
        {
            Assert.False(x.Name.StartsWith("sys", StringComparison.Ordinal));
            Assert.DoesNotContain(x.Name, new[] { "impersonate", "authenticateWithAlias", "unlimitedRequests", "unlimitedUploads" });
        });
        Assert.DoesNotContain("secret", creation.EnumerateObject().Select(x => x.Name));
    }

    [Theory]
    [InlineData("overQuota")]
    [InlineData("forbidden")]
    [InlineData("invalidProperties")]
    public async Task CredentialCreateFailuresNeverReturnASecret(string failure)
    {
        using var f = await Fixture.Create();
        f.Cloud.Failure = failure;
        Assert.Equal(503, (await Assert.ThrowsAsync<AppException>(() => f.Client.CreateAsync("opaque", "own", DateTimeOffset.UtcNow.AddMinutes(30), default))).HttpStatusCode);
    }

    [Fact]
    public async Task TimeoutRecoveryDestroysOnlyMatchingOwnedCorrelation()
    {
        using var f = await Fixture.Create();
        f.Cloud.Passwords = [new("own-id", "own-correlation"), new("other-id", "other-app")];
        Assert.True(await f.Client.RevokeOwnedAsync("opaque-selected", null, "own-correlation", default));
        var destroy = f.Cloud.Requests.Last().GetProperty("methodCalls")[0][1];
        Assert.Equal("opaque-selected", destroy.GetProperty("accountId").GetString());
        Assert.Equal(new[] { "own-id" }, destroy.GetProperty("destroy").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.False(await f.Client.RevokeOwnedAsync("opaque-selected", "other-id", "own-correlation", default));
    }

    [Fact]
    public async Task MalformedOrErrorJmapResponseFailsClosed()
    {
        using var f = await Fixture.Create();
        f.Cloud.ReturnError = true;
        await Assert.ThrowsAsync<AppException>(() => f.Client.ResolveAccountAsync("selected@example.test", default));
    }

    [Fact]
    public async Task SuccessKeepsOnlyNonsecretOwnedLeaseAndLogoutRevokesOnlyThatLease()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        f.Cloud.Passwords = [new("other-id", "other-app")];
        var sso = f.Service();
        var grant = Grant();
        var login = await sso.ConsumeAsync(grant, _ => Task.CompletedTask, default);
        Assert.Equal("selected@example.test", login.Address);
        Assert.Equal("app_fixture_only", login.Password);
        var ledger = await File.ReadAllTextAsync(f.Store.ResolvePath(WebmailSsoService.LedgerPath));
        Assert.DoesNotContain(login.Password, ledger);
        Assert.DoesNotContain("ADMIN-FIXTURE-ONLY", ledger);
        Assert.Equal("example.test", await sso.GetLeaseDomainAsync(login.Lease, default));
        await sso.RevokeAsync(login.Lease, default);
        Assert.Null(await sso.GetLeaseDomainAsync(login.Lease, default));
        Assert.Equal("other-id", Assert.Single(f.Cloud.Passwords).Id);
        Assert.Empty(await f.Store.ReadAsync(WebmailSsoService.LedgerPath, new List<WebmailSsoService.Lease>()));
    }

    [Fact]
    public async Task CreatedButTimedOutCredentialIsFoundByCorrelationAndCleaned()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        f.Cloud.Passwords = [new("other-id", "other-app")];
        f.Cloud.ThrowAfterCreate = true;
        var sso = f.Service();
        await Assert.ThrowsAsync<HttpRequestException>(() => sso.ConsumeAsync(Grant(), _ => Task.CompletedTask, default));
        Assert.Equal("other-id", Assert.Single(f.Cloud.Passwords).Id);
        Assert.Empty(await f.Store.ReadAsync(WebmailSsoService.LedgerPath, new List<WebmailSsoService.Lease>()));
    }

    [Fact]
    public async Task UnobservedTimedOutCreateRetainsNonsecretRecoveryRecord()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        f.Cloud.ThrowBeforeCreate = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service().ConsumeAsync(Grant(), _ => Task.CompletedTask, default));
        var lease = Assert.Single(await f.Store.ReadAsync(WebmailSsoService.LedgerPath, new List<WebmailSsoService.Lease>()));
        Assert.Null(lease.CredentialId);
        Assert.Equal("opaque-ID", lease.AccountId);
    }

    [Fact]
    public async Task RevokedAuthorityAfterCredentialCreateRevokesTheNewCredential()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        var calls = 0;
        Task Authorize(CancellationToken _)
        {
            if (++calls == 3) throw new AppException("auth.unauthorized", 401, 401);
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<AppException>(() => f.Service().ConsumeAsync(Grant(), Authorize, default));
        Assert.Empty(f.Cloud.Passwords);
        Assert.Empty(await f.Store.ReadAsync(WebmailSsoService.LedgerPath, new List<WebmailSsoService.Lease>()));
    }

    [Fact]
    public async Task FailedAuditRevokesCredentialBeforeAnySecretCanBeReturned()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().ConsumeAsync(Grant(), _ => Task.CompletedTask,
            default, _ => throw new InvalidOperationException("fixture audit publication failed")));
        Assert.Empty(f.Cloud.Passwords);
        Assert.Empty(await f.Store.ReadAsync(WebmailSsoService.LedgerPath, new List<WebmailSsoService.Lease>()));
    }

    [Fact]
    public async Task StableAccountReplacedBetweenIssueAndConsumeCannotCreateCredential()
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("replacement-ID", "selected@example.test")];
        await Assert.ThrowsAsync<AppException>(() => f.Service().ConsumeAsync(Grant(), _ => Task.CompletedTask, default));
        Assert.DoesNotContain(f.Cloud.Requests, x => x.GetProperty("methodCalls")[0][0].GetString() == "x:AppPassword/set");
    }

    [Theory]
    [InlineData("selected@other.test", null, true)]
    [InlineData("selected@example.test", false, true)]
    [InlineData("selected@example.test", null, false)]
    [InlineData("alias@example.test", null, true)]
    public async Task CrossDomainDisabledUnmeasuredAndAliasMailboxAreRejected(string address, bool? active, bool measured)
    {
        using var f = await Fixture.Create();
        f.Cloud.Accounts = [new("opaque-ID", "selected@example.test")];
        await Assert.ThrowsAsync<AppException>(() => f.Service(active, measured).ResolveAsync("example.test", address, default));
    }

    private static WebmailSsoTickets.Grant Grant() => new(Actor, "authority", "example.test", "selected@example.test", "opaque-ID",
        "webmail.example.test", "https://panel.example.test", DateTimeOffset.UtcNow.AddSeconds(60));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly AdaPanelDbContext db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal readonly Cloud Cloud = new();
        internal StalwartWebmailCredentials Client = null!;
        internal AdaPanel.Infrastructure.Support.JsonFileStore Store => area.DosyaDeposu;
        internal WebmailSsoService Service(bool? active = null, bool measured = true)
        {
            var mail = DispatchProxy.Create<IMailService, WebmailSsoAuthorityTests.ScopeProxy>();
            ((WebmailSsoAuthorityTests.ScopeProxy)(object)mail).Handler = (method, _) => method.Name == "GetWorkspaceAsync"
                ? Task.FromResult(new MailWorkspaceDto { AccountsMeasured = measured, Accounts = [new() { Address = "selected@example.test", Active = active }] })
                : throw new NotSupportedException(method.Name);
            return new(Client, mail, area.DosyaDeposu, new MailProviderSelection(new ConfigurationBuilder().Build()));
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.area.DosyaDeposu.WriteAsync("config/mail.json", new { adminPasswordProtected = SahteKoruyucu.Onek + "ADMIN-FIXTURE-ONLY" });
            var factory = new Factory(fixture.Cloud);
            fixture.Client = new(new StalwartRuntime(fixture.area.DosyaDeposu, new SahteKoruyucu(), fixture.db, factory), factory);
            return fixture;
        }
        public void Dispose() { db.Dispose(); area.Dispose(); }
    }
    private sealed record Account(string Id, string Address);
    private sealed record Password(string Id, string Description);
    private sealed class Factory(Cloud cloud) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(cloud, false);
    }
    private sealed class Cloud : HttpMessageHandler
    {
        internal Account[] Accounts = [];
        internal Password[] Passwords = [];
        internal readonly List<JsonElement> Requests = [];
        internal string? Failure;
        internal bool ReturnError;
        internal bool ThrowAfterCreate, ThrowBeforeCreate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("http://localhost:8080/jmap/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(body.RootElement.Clone());
            var call = body.RootElement.GetProperty("methodCalls")[0];
            var method = call[0].GetString()!;
            if (method == "x:AppPassword/set" && call[1].TryGetProperty("create", out var creation) && Failure is null)
            {
                if (ThrowBeforeCreate) throw new HttpRequestException("Fixture create outcome unknown");
                Passwords = [.. Passwords, new("owned-id", creation.GetProperty("sso").GetProperty("description").GetString()!)];
                if (ThrowAfterCreate) throw new HttpRequestException("Fixture created but response lost");
            }
            object result = method switch
            {
                "x:Account/query" => new { ids = Accounts.Select(x => x.Id).ToArray() },
                "x:Account/get" => new { list = Accounts.Select(x => new Dictionary<string, object> { ["id"] = x.Id, ["emailAddress"] = x.Address, ["@type"] = "User" }).ToArray() },
                "x:AppPassword/get" => new { list = Passwords.Select(x => new { id = x.Id, description = x.Description }).ToArray() },
                "x:AppPassword/set" when call[1].TryGetProperty("destroy", out var destroy) => new { destroyed = destroy.EnumerateArray().Select(x => x.GetString()).ToArray() },
                "x:AppPassword/set" when Failure is not null => new { notCreated = new { sso = new { type = Failure } } },
                "x:AppPassword/set" => new { created = new { sso = new { id = "owned-id", secret = "app_fixture_only" } } },
                _ => throw new InvalidOperationException(method)
            };
            if (method == "x:AppPassword/set" && call[1].TryGetProperty("destroy", out var destroyedIds))
            {
                var removed = destroyedIds.EnumerateArray().Select(x => x.GetString()).ToHashSet();
                Passwords = Passwords.Where(x => !removed.Contains(x.Id)).ToArray();
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { methodResponses = new object[][] { [ReturnError ? "error" : method, result, "sso"] } }), Encoding.UTF8, "application/json")
            };
        }
    }
}
