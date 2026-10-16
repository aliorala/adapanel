using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslIssueLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static void Authority(Exception? error) =>
        Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);

    public static IEnumerable<object[]> ActorWaits() =>
        from boundary in new[] { "scope", "domain", "site", "resolver" }
        from change in new[] { "disabled", "deleted", "password", "role", "scope" }
        select new object[] { boundary, change };
    [Theory, MemberData(nameof(ActorWaits))]
    public async Task ActorChangesBeforeTheFirstAcmeEffectAreRejected(string boundary, string change)
    {
        using var f = await Fixture.Create(); f.Wait = boundary;
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeActor(change); } finally { f.Release.TrySetResult(); }
        Authority(await action.WaitAsync(Deadline));
        await f.Unpublished(); Assert.Equal(0, f.Acme); Assert.Equal(0, f.Fallbacks); Assert.Equal(0, f.Persists);
        Assert.False(File.Exists(f.Area.DosyaDeposu.ResolvePath(AcmeContactSettings.Path)));
    }
    public static IEnumerable<object[]> SiteWaits() =>
        from boundary in new[] { "scope", "domain", "site", "resolver" }
        from replacement in new[] { "id", "same-id" }
        select new object[] { boundary, replacement };
    [Theory, MemberData(nameof(SiteWaits))]
    public async Task EarlierReadsCannotAdoptARecreatedSite(string boundary, string replacement)
    {
        using var f = await Fixture.Create(); f.Wait = boundary;
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeSite(replacement); } finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        Assert.Equal(0, f.Acme); Assert.Equal(0, f.Persists); await f.Unpublished();
    }
    public static IEnumerable<object[]> EffectChanges() =>
        from boundary in new[] { "acme", "dns", "persist", "binding", "inventory" }
        from cancel in new[] { false, true }
        select new object[] { boundary, cancel };
    [Theory, MemberData(nameof(EffectChanges))]
    public async Task AfterExternalEffectsAuthorityAndCancellationStopTheNextEffect(string boundary, bool cancel)
    {
        using var f = await Fixture.Create(boundary == "dns" ? UserRole.Administrator : UserRole.Customer);
        f.Request.Wildcard = boundary == "dns";
        f.After[boundary] = async () => { if (cancel) f.Cancellation.Cancel(); else await f.ChangeActor("disabled"); };
        var error = await Capture(() => f.Invoke());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Authority(error);
        if (boundary == "dns")
        {
            Assert.Equal(0, f.Acme);
            Assert.False(File.Exists(f.Area.DosyaDeposu.ResolvePath("tools/acme-dns01.ps1")));
        }
        if (boundary is "dns" or "acme") { Assert.Equal(0, f.Fallbacks); Assert.Equal(0, f.Persists); }
        if (boundary is "persist" or "binding") Assert.Equal(0, f.Redirects);
        await f.Unpublished(); await f.ChangeSite("id");
    }
    [Theory, InlineData("parent-disabled"), InlineData("section")]
    public async Task SubUserParentAndSitesSectionAreRecheckedAfterResolver(string change)
    {
        using var f = await Fixture.Create(subUser: true); f.Wait = "resolver";
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeActor(change); } finally { f.Release.TrySetResult(); }
        Authority(await action.WaitAsync(Deadline)); Assert.Equal(0, f.Acme); await f.Unpublished();
    }
    [Theory, InlineData(UserRole.Administrator), InlineData(UserRole.Reseller), InlineData(UserRole.Customer)]
    public async Task AuthorizedHttpIssueStillPublishesAMeasuredCertificate(UserRole role)
    {
        using var f = await Fixture.Create(role); var result = await f.Invoke();
        Assert.Equal(f.Name, result.SiteName); Assert.Equal(f.Name, result.HostName);
        Assert.True(result.IsCertificateBound); Assert.True(result.IsPresentInStore);
        Assert.Equal(1, f.Acme); Assert.Equal(1, f.Persists); Assert.Equal(1, f.Bindings);
        Assert.Single(f.Audits); Assert.Single(await f.Records()); await f.ChangeSite("id");
    }
    [Fact]
    public async Task DirectOperatorCallCannotBypassTheControllerRoleBoundary()
    {
        using var f = await Fixture.Create(UserRole.Operator);
        Authority(await Capture(() => f.Invoke())); Assert.Equal(0, f.Acme); await f.Unpublished();
    }
    [Theory, InlineData(UserRole.Customer), InlineData(UserRole.Reseller)]
    public async Task WildcardStillRequiresAnAdministrator(UserRole role)
    {
        using var f = await Fixture.Create(role); f.Request.Wildcard = true;
        Authority(await Capture(() => f.Invoke())); Assert.Equal(0, f.Dns); Assert.Equal(0, f.Acme); await f.Unpublished();
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task TrustedServiceCallSupportsBothHttpAndWildcard(bool wildcard)
    {
        using var f = await Fixture.Create(); f.Request.Wildcard = wildcard;
        var result = await f.Service.IssueLetsEncryptAsync(f.Request, f.Token);
        Assert.Equal(wildcard ? "*." + f.Name : f.Name, result.HostName);
        Assert.True(result.IsCertificateBound); Assert.Equal(1, f.Acme); Assert.Equal(1, f.Persists);
        Assert.Empty(f.Audits); await f.ChangeSite("same-id");
    }
    [Fact]
    public async Task AcmeFailureRetainsExplicitSelfSignedFallbackWithoutHidingItsReason()
    {
        using var f = await Fixture.Create(); f.AcmeFails = true;
        var result = await f.Invoke(); Assert.Equal("self-signed", result.Source);
        Assert.Equal("ssl.fallback.acme_failed", result.FallbackReason);
        Assert.Equal(1, f.Fallbacks); Assert.Equal(1, f.Persists); Assert.True(result.IsCertificateBound);
    }
    [Fact]
    public async Task CancellationBeforeEntryCannotCreateAKeyOrPublish()
    {
        using var f = await Fixture.Create(); f.Cancellation.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke()));
        Assert.Equal(0, f.Acme); Assert.Equal(0, f.Fallbacks); await f.Unpublished();
    }
    [Theory, InlineData("rewrite-probe", false), InlineData("rewrite-probe", true), InlineData("install", false), InlineData("install", true)]
    public async Task RewriteInstallRechecksAuthorityBeforeAndAfterTheGlobalEffect(string boundary, bool cancel)
    {
        using var f = await Fixture.Create(); f.NeedRewrite = true;
        f.After[boundary] = async () => { if (cancel) f.Cancellation.Cancel(); else await f.ChangeActor("disabled"); };
        var error = await Capture(() => f.Invoke());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Authority(error);
        Assert.Equal(boundary == "install" ? 1 : 0, f.Installs); Assert.Equal(1, f.Redirects);
        await f.Unpublished(); await f.ChangeSite("id");
    }
    [Fact]
    public async Task AuthorizedRewriteInstallationPreservesTheNormalIssuePath()
    {
        using var f = await Fixture.Create(); f.NeedRewrite = true;
        Assert.True((await f.Invoke()).IsCertificateBound);
        Assert.Equal(1, f.Installs); Assert.Equal(2, f.Redirects); Assert.Single(f.Audits);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task ActiveIssuancePinsItsSiteUntilSuccessOrCancellation(bool cancel)
    {
        using var f = await Fixture.Create(); f.Wait = "acme";
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        var replacement = await Capture(() => f.ChangeSite("same-id"));
        if (cancel) f.Cancellation.Cancel();
        f.Release.TrySetResult(); var result = await action.WaitAsync(Deadline);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(replacement).MessageKey);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(result); else Assert.Null(result);
        await f.ChangeSite("same-id");
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task IssuanceAlsoSynchronizesMailForExactAndWildcardCertificates(bool wildcard)
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        f.Request.Wildcard = wildcard;
        f.NonSelfSigned = true;
        await f.Invoke();
        Assert.Equal(wildcard ? "*." + f.Name : f.Name, Assert.Single(f.MailHosts));
        Assert.Equal(1, f.MailApprovals);
    }

    [Fact]
    public async Task WildcardMailFailureDoesNotReturnASuccessResponse()
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        f.Request.Wildcard = true;
        f.NonSelfSigned = true;
        f.FailMail = true;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Invoke());
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.Empty(f.Audits);
        Assert.Equal(1, f.MailApprovals);
        await f.ChangeSite("same-id"); // reservation released even when mail activation fails
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task AutomaticHttpsSetupAlsoChecksMailAndCannotHideItsFailure(bool failMail)
    {
        using var f = await Fixture.Create();
        f.UseExistingCertificate = f.NonSelfSigned = true;
        f.FailMail = failMail;
        var result = await f.Service.TrySecureHostAsync(f.Name, f.Name, "owner@example.test", f.Token);
        Assert.Equal(!failMail, result);
        Assert.Equal(f.Name, Assert.Single(f.MailHosts));
        Assert.Equal(0, f.Acme);
    }

    [Theory, InlineData("store"), InlineData("binding"), InlineData("unreadable-binding")]
    public async Task FailedAcmeDoesNotReplaceAnExistingHttpsCertificate(string existing)
    {
        using var f = await Fixture.Create();
        f.AcmeFails = true;
        f.UseExistingCertificate = existing == "store";
        f.ExistingBindingThumbprint = existing == "binding" ? "working-cert" : "";
        f.BindingUnreadable = existing == "unreadable-binding";
        var error = await Assert.ThrowsAsync<AppException>(() => f.Invoke());
        Assert.Equal("ssl.issue_failed_existing_preserved", error.MessageKey);
        Assert.Equal(0, f.Fallbacks);
        Assert.Equal(0, f.Persists);
        Assert.Equal(0, f.Bindings);
        await f.Unpublished();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly BulutAlani Area = new();
        private readonly SqliteConnection keeper = new($"Data Source=SslIssue-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly List<X509Certificate2> certificates = [];
        private readonly Guid actor = Guid.NewGuid(), parent = Guid.NewGuid();
        private ServiceProvider services = null!;
        private SslController controller = null!;
        private bool scoped = true, sectionsAllowed = true, subUser, paused, issued;
        private byte[] original = [];
        private string thumbprint = "";
        public string Name { get; } = "issue-" + Guid.NewGuid().ToString("N") + ".example.test";
        public SiteDto Site = null!;
        public IssueLetsEncryptRequest Request = null!;
        public WinAcmeSslService Service = null!;
        public readonly ConcurrencyLock Gates = new();
        public readonly CancellationTokenSource Cancellation = new(Deadline);
        public CancellationToken Token => Cancellation.Token;
        public string Wait = "";
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Dictionary<string, Func<Task>> After = [];
        public readonly List<string> Audits = [];
        public int Acme, Dns, Fallbacks, Persists, Bindings, Redirects, Webmail, Installs;
        public bool AcmeFails, NeedRewrite;
        public bool NonSelfSigned, FailMail, UseExistingCertificate;
        public bool BindingUnreadable;
        public string ExistingBindingThumbprint = "";
        public int MailApprovals;
        public readonly List<string> MailHosts = [];
        public static async Task<Fixture> Create(UserRole role = UserRole.Customer, bool subUser = false)
        {
            var f = new Fixture { subUser = subUser }; await f.keeper.OpenAsync();
            var db = f.Context(); await db.Database.EnsureCreatedAsync();
            db.Users.Add(new PanelUser { Id = f.actor, UserName = "actor", Email = "actor@example.test", Role = role, IsActive = true, PasswordHash = "original" });
            if (subUser) db.Users.Add(new PanelUser { Id = f.parent, UserName = "parent", Email = "parent@example.test", Role = UserRole.Customer, IsActive = true, PasswordHash = "parent" });
            var toolDir = f.Area.DosyaDeposu.EnsureDirectory("fake-acme");
            await File.WriteAllTextAsync(Path.Combine(toolDir, "wacs.exe"), "fixture only, never executable");
            db.InstalledComponents.Add(new InstalledComponent { ComponentId = "win-acme", Status = ComponentInstallStatus.Installed, InstallDir = toolDir });
            await db.SaveChangesAsync();
            f.Site = new SiteDto { Id = 81, Name = f.Name, PhysicalPath = f.Area.SiteKoku, ApplicationRootPath = f.Area.SiteKoku,
                ApplicationPoolName = "issue-pool", Bindings = [new() { Protocol = "http", HostName = f.Name, Port = 80 }] };
            f.Request = new IssueLetsEncryptRequest { SiteName = f.Name, HostName = f.Name, ContactEmail = "owner@example.test" };
            var sites = Fake<IIisSiteService>((method, args) => method.Name switch
            { "GetSiteAsync" => f.ReadSite((CancellationToken)args[^1]!), "GetSitesAsync" => f.ReadSites((CancellationToken)args[^1]!), _ => throw new InvalidOperationException(method.Name) });
            var scopes = Fake<ICustomerScopeService>((method, args) => method.Name switch
            {
                "GetAssignedSitesAsync" or "GetResellerSitesAsync" => f.ReadScope((CancellationToken)args[^1]!),
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(f.subUser && (Guid)args[0]! == f.actor ? f.parent : null),
                "GetSubUserSectionsAsync" => Task.FromResult<IReadOnlyCollection<string>?>(f.sectionsAllowed ? null : []),
                "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(f.subUser ? f.parent : f.actor),
                "GetAllowedMailDomainsAsync" or "GetResellerMailDomainsAsync" => Task.FromResult<IReadOnlyCollection<string>>([]),
                _ => throw new InvalidOperationException(method.Name)
            });
            var components = Fake<IComponentBootstrapper>((method, _) => method.Name == "InstallAsync"
                ? f.InstallRewrite() : throw new InvalidOperationException("Unexpected component call: " + method.Name));
            f.Service = new WinAcmeSslService(f.Context(intercept: true), sites, null!, f.Area.DosyaDeposu, components,
                NullLogger<WinAcmeSslService>.Instance, f.ReadBindings, f.Gates,
                Fake<IMailService>((method, args) => method.Name == "SyncWebCertificateAsync"
                    ? f.SyncMail(args) : throw new InvalidOperationException(method.Name)))
            {
                IssueCommand = async (_, _, token) =>
                {
                    f.Acme++; await f.Pause("acme", token); f.issued = !f.AcmeFails;
                    await f.Boundary("acme"); return new CommandResult { ExitCode = f.AcmeFails ? 1 : 0, StandardOutput = "fixture", StandardError = "" };
                },
                IssueCertificateLookup = _ => f.issued || f.UseExistingCertificate ? f.Certificate() : null,
                IssueFallbackCertificate = (_, _) => { f.Fallbacks++; return f.Certificate(); },
                PersistIssuedCertificate = cert =>
                { f.Persists++; f.thumbprint = cert.Thumbprint; f.Boundary("persist").GetAwaiter().GetResult(); },
                DnsPreflightCommand = async (_, _) =>
                { f.Dns++; await f.Boundary("dns"); return new CommandResult { ExitCode = 0, StandardOutput = "{\"zone\":\"example.test\",\"nsNames\":[\"ns.example.test\"],\"publicVisible\":true}" }; },
                HttpsBindingCommand = async (_, _) =>
                { f.Bindings++; await f.Boundary("binding"); return new CommandResult { ExitCode = 0, StandardOutput = "OK" }; },
                RedirectCommand = async (_, _) =>
                {
                    f.Redirects++; var needsInstall = f.NeedRewrite && f.Installs == 0;
                    if (needsInstall) await f.Boundary("rewrite-probe");
                    return new CommandResult { ExitCode = 0, StandardOutput = needsInstall ? "NOREWRITE" : "OK" };
                }
            };
            f.services = new ServiceCollection().AddSingleton(f.Context()).AddSingleton(sites).AddSingleton<IConcurrencyLock>(f.Gates).AddSingleton(scopes).BuildServiceProvider();
            f.controller = new SslController(f.Service, scopes, Fake<IWebmailService>((_, _) => { f.Webmail++; return Task.CompletedTask; }),
                Fake<IAuditService>((_, _) => { f.Audits.Add("success"); return Task.CompletedTask; }))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = f.services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original"))], "fixture"))
                } }
            };
            await f.Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { new SslCertificateDto { SiteName = f.Name, HostName = f.Name, Port = 443, Thumbprint = "previous-working-certificate" } }, f.Token);
            f.original = await File.ReadAllBytesAsync(f.Area.DosyaDeposu.ResolvePath("config/ssl.json")); return f;
        }
        private X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=" + Name, key, HashAlgorithmName.SHA256);
            using var publicCertificate = NonSelfSigned
                ? request.Create(new X500DistinguishedName("CN=Isolated fixture issuer"), X509SignatureGenerator.CreateForECDsa(key),
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(16)) : null;
            using var originalCertificate = publicCertificate is null
                ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10))
                : publicCertificate.CopyWithPrivateKey(key);
            var cert = new X509Certificate2(originalCertificate.Export(X509ContentType.Pfx), (string?)null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            certificates.Add(cert); return cert;
        }
        private async Task SyncMail(object?[] args)
        {
            MailHosts.Add((string)args[0]!);
            if (args[1] is Func<CancellationToken, Task> approve)
            {
                await approve((CancellationToken)args[2]!);
                MailApprovals++;
            }
            if (FailMail) throw new AppException("mail.certificate_not_activated", 502, 502);
        }
        private AdaPanelDbContext Context(bool intercept = false)
        {
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString);
            if (intercept) options.AddInterceptors(new ResolverBarrier(this));
            var db = new AdaPanelDbContext(options.Options); contexts.Add(db); return db;
        }
        private sealed class ResolverBarrier(Fixture f) : DbCommandInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            { if (command.CommandText.Contains("installed_components", StringComparison.Ordinal)) await f.Pause("resolver", cancellationToken); return result; }
        }
        private async Task<ComponentInstallResultDto> InstallRewrite()
        { Installs++; await Boundary("install"); return new ComponentInstallResultDto { ComponentId = "url-rewrite", Status = "Installed" }; }
        private Task Boundary(string name) => After.TryGetValue(name, out var action) ? action() : Task.CompletedTask;
        private async Task Pause(string boundary, CancellationToken token)
        { if (!paused && Wait == boundary) { paused = true; Entered.TrySetResult(); await Release.Task.WaitAsync(token); } }
        private async Task<SiteDto> ReadSite(CancellationToken token)
        { var snapshot = Site; await Pause("site", token); token.ThrowIfCancellationRequested(); return snapshot; }
        private async Task<IReadOnlyCollection<SiteDto>> ReadSites(CancellationToken token)
        { await Pause("domain", token); return [Site]; }
        private async Task<IReadOnlyCollection<string>> ReadScope(CancellationToken token)
        { var allowed = scoped; await Pause("scope", token); return allowed ? [Name] : []; }
        private async Task<IReadOnlyCollection<IisHttpsBinding>?> ReadBindings(CancellationToken token)
        {
            await Boundary("inventory");
            return [new IisHttpsBinding { SiteName = Name, BindingInformation = "*:443:" + Name,
                Thumbprint = thumbprint.Length == 0 ? ExistingBindingThumbprint : thumbprint,
                CertificateReadable = !BindingUnreadable, StoreName = "My" }];
        }
        public async Task ChangeActor(string change)
        {
            if (change == "scope") { scoped = false; return; }
            if (change == "section") { sectionsAllowed = false; return; }
            var db = Context(); var id = change.StartsWith("parent-", StringComparison.Ordinal) ? parent : actor;
            var user = await db.Users.SingleAsync(row => row.Id == id);
            if (change.EndsWith("disabled", StringComparison.Ordinal)) user.IsActive = false;
            else if (change.EndsWith("deleted", StringComparison.Ordinal)) db.Users.Remove(user);
            else if (change == "role") user.Role = UserRole.Administrator;
            else user.PasswordHash = "changed";
            await db.SaveChangesAsync();
        }
        public Task ChangeSite(string change) => SiteMutationGuard.RunMutationAsync(Gates, Name, SiteMutationGuard.MutationKind.Create, () =>
        {
            Site = new SiteDto { Id = change == "id" ? Site.Id + 1 : Site.Id, Name = Name, PhysicalPath = Site.PhysicalPath,
                ApplicationRootPath = Site.ApplicationRootPath, ApplicationPoolName = Site.ApplicationPoolName, Bindings = Site.Bindings };
            return Task.FromResult(true);
        }, CancellationToken.None);
        public async Task Reached(Task<Exception?> action)
        { var first = await Task.WhenAny(Entered.Task, action).WaitAsync(Deadline); Assert.True(first == Entered.Task, "Issue ended before the intended boundary: " + (action.IsCompleted ? await action : null)); }
        public async Task<SslCertificateDto> Invoke()
        {
            var result = await controller.IssueLetsEncryptAsync(Request, services.GetRequiredService<IIisSiteService>(),
                Fake<IMailService>((method, _) => method.Name == "GetWorkspaceAsync" ? Task.FromResult(new MailWorkspaceDto { Installed = false }) : throw new InvalidOperationException(method.Name)),
                NullLogger<SslController>.Instance, Token);
            return Assert.IsType<ApiResponse<SslCertificateDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public async Task Unpublished()
        { Assert.Equal(original, await File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/ssl.json"))); Assert.Empty(Audits); Assert.Equal(0, Webmail); }
        public Task<SslCertificateDto[]> Records() => Area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>());
        public void Dispose()
        { Release.TrySetResult(); foreach (var cert in certificates) cert.Dispose(); services?.Dispose(); foreach (var db in contexts) db.Dispose(); keeper.Dispose(); Cancellation.Dispose(); Area.Dispose(); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = invoke; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
}
