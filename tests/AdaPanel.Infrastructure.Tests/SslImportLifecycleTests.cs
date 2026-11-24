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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslImportLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    public static IEnumerable<object[]> ActorWaits() =>
        from boundary in new[] { "scope", "domain", "site" }
        from change in new[] { "disabled", "deleted", "password", "role", "scope" }
        select new object[] { boundary, change };
    [Theory, MemberData(nameof(ActorWaits))]
    public async Task ImportRevalidatesAuthorityBeforeAnyPersistentKeyOrNativeEffect(string boundary, string change)
    {
        using var f = await Fixture.Create(); f.Wait = boundary;
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeActor(change); } finally { f.Release.TrySetResult(); }
        AssertAuthority(await action.WaitAsync(Deadline)); await f.AssertUnchanged();
        Assert.DoesNotContain(f.LoadFlags, flags => flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
        Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings);
    }
    public static IEnumerable<object[]> GenerationWaits() =>
        from boundary in new[] { "scope", "domain", "site" }
        from change in new[] { "id", "same-id" }
        select new object[] { boundary, change };
    [Theory, MemberData(nameof(GenerationWaits))]
    public async Task ImportCannotAdoptASiteRecreatedDuringAnEarlierRead(string boundary, string change)
    {
        using var f = await Fixture.Create(); f.Wait = boundary;
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeSite(change); } finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(Deadline)).MessageKey);
        await f.AssertUnchanged(); Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings);
    }
    [Theory]
    [InlineData("invalid-base64")]
    [InlineData("invalid-pfx")]
    [InlineData("wrong-password")]
    [InlineData("no-private-key")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("wrong-host")]
    [InlineData("cn-overridden-by-san")]
    [InlineData("deep-wildcard")]
    [InlineData("legacy-cn")]
    [InlineData("client-only")]
    public async Task UnusableCertificateIsRejectedWithoutNativeOrMetadataChanges(string problem)
    {
        using var f = await Fixture.Create(); f.SetCertificate(problem);
        var error = Assert.IsType<AppException>(await Capture(() => f.Invoke()));
        Assert.Equal(400, error.HttpStatusCode);
        Assert.DoesNotContain("secret-pfx-password", error.ToString());
        Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings); Assert.Equal(0, f.RedirectWrites);
        Assert.DoesNotContain(f.LoadFlags, flags => flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
        await f.AssertUnchanged();
    }
    [Fact]
    public async Task CertificateIsInspectedInMemoryBeforeItsAuthorizedPersistentImport()
    {
        using var f = await Fixture.Create(); await f.Invoke();
        Assert.True(f.LoadFlags[0].HasFlag(X509KeyStorageFlags.EphemeralKeySet));
        Assert.False(f.LoadFlags[0].HasFlag(X509KeyStorageFlags.PersistKeySet));
        Assert.Contains(f.LoadFlags, flags => flags.HasFlag(X509KeyStorageFlags.PersistKeySet) && flags.HasFlag(X509KeyStorageFlags.MachineKeySet));
        Assert.True(f.PersistedHadPrivateKey); Assert.True(f.PersistHeldGate);
    }
    public static IEnumerable<object[]> AfterEffects() =>
        from boundary in new[] { "persist", "binding", "inventory" }
        from cancel in new[] { false, true }
        select new object[] { boundary, cancel };
    [Theory, MemberData(nameof(AfterEffects))]
    public async Task RevocationOrCancellationAfterAnEffectStopsFurtherEffectsAndSuccessPublication(string boundary, bool cancel)
    {
        using var f = await Fixture.Create();
        async Task Change() { if (cancel) f.Cancellation.Cancel(); else await f.ChangeActor("disabled"); }
        if (boundary == "persist") f.AfterPersist = () => Change().GetAwaiter().GetResult();
        else if (boundary == "binding") f.AfterBinding = Change;
        else f.BeforeInventory = Change;
        var error = await Capture(() => f.Invoke());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else AssertAuthority(error);
        Assert.Equal(1, f.Persists);
        if (boundary == "persist") Assert.Equal(0, f.Bindings);
        if (boundary is "persist" or "binding") Assert.Equal(0, f.RedirectWrites);
        await f.AssertUnchanged();
        await f.ChangeSite("id");
    }
    public static IEnumerable<object[]> ValidCertificates() =>
        from kind in new[] { "san", "wildcard", "server-auth", "any-usage" }
        from port in new[] { 443, 8443 }
        select new object[] { kind, port };
    [Theory, MemberData(nameof(ValidCertificates))]
    public async Task UsablePrivateKeyCertificateRetainsTheRequestedTargetAndPort(string kind, int port)
    {
        using var f = await Fixture.Create(); f.SetCertificate(kind); f.Request.Port = port;
        var result = await f.Invoke();
        Assert.Equal(f.Name, result.SiteName); Assert.Equal(f.Request.HostName, result.HostName); Assert.Equal(port, result.Port);
        Assert.True(result.IsCertificateBound); Assert.True(result.IsPresentInStore);
        var records = await f.Records();
        var record = Assert.Single(records, item => item.HostName == f.Request.HostName && item.Port == port);
        Assert.Equal(result.Thumbprint, record.Thumbprint);
        if (f.Request.HostName != f.Name || port != 443)
            Assert.Contains(records, item => item.HostName == f.Name && item.Port == 443 && item.Thumbprint == "previous-working-certificate");
        Assert.Single(f.Audits); Assert.Equal(1, f.Persists); Assert.Equal(1, f.Bindings);
        await f.ChangeSite("id");
    }

    [Theory]
    [InlineData(443, "")]
    [InlineData(443, ":80")]
    [InlineData(443, ":8080")]
    [InlineData(8443, "")]
    [InlineData(8443, ":80")]
    [InlineData(8443, ":8080")]
    public async Task Http_only_site_import_redirects_to_the_installed_TLS_port_without_reusing_the_HTTP_port(int tlsPort, string incomingPort)
    {
        using var f = await Fixture.Create();
        f.HttpOnlyBeforeImport = true;
        f.Request.Port = tlsPort;
        await f.Area.DosyaDeposu.WriteAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), f.Token);
        Assert.All(f.Site.Bindings, binding => Assert.Equal("http", binding.Protocol));
        var certificate = await f.Invoke();
        Assert.True(certificate.IsCertificateBound);
        Assert.Equal(tlsPort, certificate.Port);
        var script = Assert.Single(f.RedirectScripts);
        var hostPattern = System.Text.RegularExpressions.Regex.Match(script,
            """input='\{HTTP_HOST\}'; pattern='([^']+)'""");
        Assert.True(hostPattern.Success, "The redirect must separate the incoming host from its HTTP port.");
        var incoming = System.Text.RegularExpressions.Regex.Match(f.Name + incomingPort, hostPattern.Groups[1].Value);
        Assert.True(incoming.Success);
        var action = System.Text.RegularExpressions.Regex.Match(script, """-name 'url' -value '(https://[^']+)'""");
        Assert.True(action.Success);
        var location = action.Groups[1].Value.Replace("{C:1}", incoming.Groups[1].Value).Replace("{R:1}", "folder/page");
        var expectedPort = tlsPort == 443 ? "" : $":{tlsPort}";
        Assert.Equal($"https://{f.Name}{expectedPort}/folder/page", location);
        Assert.Contains("^/\\.well-known/acme-challenge/", script);
    }

    [Theory]
    [InlineData(443, false, false)]
    [InlineData(8443, true, false)]
    [InlineData(443, true, true)]
    public async Task Import_does_not_redirect_another_HTTP_host_to_a_TLS_port_it_cannot_serve(int otherTlsPort, bool hasCommonPort, bool previousPrimary443)
    {
        using var f = await Fixture.Create(); f.Request.Port = 8443;
        f.Site = new SiteDto { Id = f.Site.Id, Name = f.Name, PhysicalPath = f.Site.PhysicalPath,
            ApplicationRootPath = f.Site.ApplicationRootPath, ApplicationPoolName = f.Site.ApplicationPoolName,
            Bindings = [new() { Protocol = "http", HostName = f.Name, Port = 80 },
                new() { Protocol = "http", HostName = "other.example.test", Port = 80 }] };
        f.AdditionalBindings = [new() { SiteName = f.Name, BindingInformation = $"*:{otherTlsPort}:other.example.test",
            Thumbprint = "other-working-certificate", CertificateReadable = true }];
        if (previousPrimary443)
            f.AdditionalBindings = [.. f.AdditionalBindings, new() { SiteName = f.Name, BindingInformation = $"*:443:{f.Name}",
                Thumbprint = "previous-primary-certificate", CertificateReadable = true }];
        var result = await f.Invoke();
        Assert.True(result.IsCertificateBound);
        Assert.Equal(8443, result.Port);
        if (hasCommonPort)
        {
            Assert.Empty(result.FallbackReason);
            Assert.Contains(otherTlsPort == 443 ? "https://{C:1}/{R:1}" : "https://{C:1}:8443/{R:1}", Assert.Single(f.RedirectScripts));
        }
        else
        {
            Assert.Equal("ssl.https_redirect_preserved", result.FallbackReason);
            Assert.Empty(f.RedirectScripts); // The existing site-wide redirect is untouched.
        }
    }
    [Theory, InlineData(UserRole.Administrator), InlineData(UserRole.Reseller), InlineData(UserRole.Customer)]
    public async Task CurrentAuthorizedRolesKeepTheNormalImportPath(UserRole role)
    {
        using var f = await Fixture.Create(role); Assert.True((await f.Invoke()).IsCertificateBound); Assert.Single(f.Audits);
    }
    [Fact]
    public async Task DirectOperatorControllerCallCannotBypassTheRoleBoundary()
    {
        using var f = await Fixture.Create(UserRole.Operator);
        AssertAuthority(await Capture(() => f.Invoke())); Assert.Equal(0, f.Persists); await f.AssertUnchanged();
    }
    [Theory, InlineData("parent-disabled"), InlineData("parent-deleted"), InlineData("section")]
    public async Task SubUserAuthorityIsFreshAtImportMutation(string change)
    {
        using var f = await Fixture.Create(subUser: true); f.Wait = "domain";
        var action = Capture(() => f.Invoke()); await f.Reached(action);
        try { await f.ChangeActor(change); } finally { f.Release.TrySetResult(); }
        AssertAuthority(await action.WaitAsync(Deadline)); Assert.Equal(0, f.Persists); await f.AssertUnchanged();
    }
    [Fact]
    public async Task APreCanceledRequestCannotPersistOrAudit()
    {
        using var f = await Fixture.Create(); f.Cancellation.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke()));
        Assert.Equal(0, f.Persists); await f.AssertUnchanged();
    }
    [Fact]
    public async Task TrustedServiceOverloadRemainsUsableWithoutHttpContext()
    {
        using var f = await Fixture.Create(); var result = await f.Service.ImportCertificateAsync(f.Request, f.Token);
        Assert.True(result.IsCertificateBound); Assert.Equal(1, f.Persists); Assert.Empty(f.Audits);
    }
    [Fact]
    public async Task BindingFailureDoesNotReplaceThePreviousMetadataOrAuditSuccess()
    {
        using var f = await Fixture.Create(); f.BindingFails = true;
        Assert.IsType<AppException>(await Capture(() => f.Invoke()));
        Assert.Equal(1, f.Persists); Assert.Equal(1, f.Bindings); await f.AssertUnchanged();
    }
    private static void AssertAuthority(Exception? error) => Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);

    [Fact]
    public async Task PfxChainIsPersistedBeforeLeafAndBindingWithOnlyPublicIntermediateMaterial()
    {
        using var f = await Fixture.Create();
        using var chain = new SslImportTestChain(f.Name);
        f.Request.Base64Content = Convert.ToBase64String(chain.Export());
        Assert.True((await f.Invoke()).IsCertificateBound);
        Assert.Equal(new[] { chain.Lower.Thumbprint, chain.Upper.Thumbprint }, f.Intermediates);
        Assert.Equal(1, f.PersistentBundleCertificateCount);
        Assert.Equal(1, f.Persists); Assert.Equal(1, f.Bindings);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task AuthorityLossAfterAnIntermediateStopsRemainingImportsAndSuccess(bool cancel)
    {
        using var f = await Fixture.Create();
        using var chain = new SslImportTestChain(f.Name);
        f.Request.Base64Content = Convert.ToBase64String(chain.Export());
        f.AfterIntermediate = () => { if (cancel) f.Cancellation.Cancel(); else f.ChangeActor("disabled").GetAwaiter().GetResult(); };
        var error = await Capture(() => f.Invoke());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else AssertAuthority(error);
        Assert.Single(f.Intermediates); Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings);
        Assert.DoesNotContain(f.LoadFlags, flags => flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
        await f.AssertUnchanged();
    }

    [Fact]
    public async Task ChainImportFailureDoesNotPersistLeafOrPublishSuccess()
    {
        using var f = await Fixture.Create();
        using var chain = new SslImportTestChain(f.Name);
        f.Request.Base64Content = Convert.ToBase64String(chain.Export());
        f.AfterIntermediate = () => throw new CryptographicException("store unavailable");
        Assert.IsType<CryptographicException>(await Capture(() => f.Invoke()));
        Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings); await f.AssertUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly BulutAlani Area = new();
        private readonly SqliteConnection keeper = new($"Data Source=SslImport-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly List<X509Certificate2> loaded = [];
        private readonly Guid actor = Guid.NewGuid(), parent = Guid.NewGuid();
        private ServiceProvider services = null!;
        private SslController controller = null!;
        private bool scoped = true, sectionsAllowed = true, subUser, paused;
        private byte[] original = [];
        private string thumbprint = "";
        public string Name { get; } = "import-" + Guid.NewGuid().ToString("N") + ".example.test";
        public SiteDto Site = null!;
        public ImportCertificateRequest Request = null!;
        public WinAcmeSslService Service = null!;
        public readonly ObservedGate Gates = new();
        public readonly CancellationTokenSource Cancellation = new(Deadline);
        public CancellationToken Token => Cancellation.Token;
        public string Wait = "";
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<X509KeyStorageFlags> LoadFlags = [];
        public readonly List<string> Audits = [];
        public int Persists, Bindings, RedirectWrites, WebmailCalls;
        public bool HttpOnlyBeforeImport;
        public readonly List<string> RedirectScripts = [];
        public IisHttpsBinding[] AdditionalBindings = [];
        public int PersistentBundleCertificateCount;
        public readonly List<string> Intermediates = [];
        public Action? AfterIntermediate;
        public bool PersistedHadPrivateKey, PersistHeldGate, BindingFails;
        public Action? AfterPersist;
        public Func<Task>? AfterBinding, BeforeInventory;
        public static async Task<Fixture> Create(UserRole role = UserRole.Customer, bool subUser = false)
        {
            var f = new Fixture { subUser = subUser }; await f.keeper.OpenAsync();
            var db = f.Context(); await db.Database.EnsureCreatedAsync();
            db.Users.Add(new PanelUser { Id = f.actor, UserName = "actor", Email = "actor@example.test", Role = role, IsActive = true, PasswordHash = "original" });
            if (subUser) db.Users.Add(new PanelUser { Id = f.parent, UserName = "parent", Email = "parent@example.test", Role = UserRole.Customer, IsActive = true, PasswordHash = "parent" });
            await db.SaveChangesAsync();
            f.Site = new SiteDto { Id = 91, Name = f.Name, PhysicalPath = f.Area.SiteKoku, ApplicationRootPath = f.Area.SiteKoku,
                ApplicationPoolName = "import-pool", Bindings = [new() { Protocol = "http", HostName = f.Name, Port = 80 }] };
            f.Request = new ImportCertificateRequest { SiteName = f.Name, HostName = f.Name, Port = 443, FileName = "fixture.pfx", Password = "secret-pfx-password" };
            f.SetCertificate("san");
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
            var components = Fake<IComponentBootstrapper>((method, _) => method.Name == "GetCatalogAsync"
                ? Task.FromResult(new SoftwareCatalogDto { Installed = [new() { ComponentId = "url-rewrite", Status = "Installed" }] }) : throw new InvalidOperationException("Live install forbidden"));
            f.Service = new WinAcmeSslService(f.Context(), sites, null!, f.Area.DosyaDeposu, components,
                NullLogger<WinAcmeSslService>.Instance, f.ReadBindings, f.Gates)
            {
                ImportCertificateLoader = (raw, password, flags) =>
                {
                    f.LoadFlags.Add(flags);
                    if (flags.HasFlag(X509KeyStorageFlags.PersistKeySet))
                    {
                        var bundle = new X509Certificate2Collection();
                        try
                        {
                            bundle.Import(raw, password, X509KeyStorageFlags.EphemeralKeySet);
                            f.PersistentBundleCertificateCount = bundle.Count;
                        }
                        finally { foreach (var item in bundle) item.Dispose(); }
                    }
                    // Exercise the real PFX parser but never persist a test key, even on the unsafe baseline.
                    var cert = new X509Certificate2(raw, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                    f.loaded.Add(cert); return cert;
                },
                PersistImportedCertificate = cert =>
                { f.Persists++; f.PersistedHadPrivateKey = cert.HasPrivateKey; f.PersistHeldGate = f.Gates.Held; f.thumbprint = cert.Thumbprint; f.AfterPersist?.Invoke(); },
                PersistImportedIntermediate = cert =>
                {
                    Assert.True(f.Gates.Held); Assert.False(cert.HasPrivateKey);
                    Assert.Equal(0, f.Persists); Assert.Equal(0, f.Bindings);
                    f.Intermediates.Add(cert.Thumbprint); f.AfterIntermediate?.Invoke();
                },
                HttpsBindingCommand = async (_, _) =>
                {
                    f.Bindings++; if (f.AfterBinding is not null) await f.AfterBinding();
                    return new CommandResult { ExitCode = f.BindingFails ? 1 : 0, StandardOutput = "OK", StandardError = f.BindingFails ? "fixture rejected binding" : "" };
                },
                RedirectCommand = (script, _) => { f.RedirectScripts.Add(script); f.RedirectWrites++; return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "OK" }); }
            };
            f.services = new ServiceCollection().AddSingleton(f.Context()).AddSingleton(sites).AddSingleton<IConcurrencyLock>(f.Gates).AddSingleton(scopes).BuildServiceProvider();
            f.controller = new SslController(f.Service, scopes, Fake<IWebmailService>((_, _) => { f.WebmailCalls++; return Task.CompletedTask; }),
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
        public void SetCertificate(string kind)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var host = kind is "wildcard" or "deep-wildcard" ? "api.example.test" : Name;
            if (kind == "deep-wildcard") host = "deep.api.example.test";
            Request.HostName = host;
            var builder = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
            if (kind is "client-only" or "server-auth" or "any-usage")
                builder.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                    { new(kind == "client-only" ? "1.3.6.1.5.5.7.3.2" : kind == "server-auth" ? "1.3.6.1.5.5.7.3.1" : "2.5.29.37.0") }, false));
            if (kind != "legacy-cn")
            {
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(kind is "wrong-host" or "cn-overridden-by-san" ? "foreign.example.test" :
                    kind is "wildcard" or "deep-wildcard" ? "*.example.test" : host);
                builder.CertificateExtensions.Add(san.Build());
            }
            var start = kind == "not-yet-valid" ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow.AddDays(-2);
            var end = kind == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(30);
            using var cert = builder.CreateSelfSigned(start, end);
            var raw = kind == "no-private-key" ? cert.Export(X509ContentType.Cert) : cert.Export(X509ContentType.Pfx, "secret-pfx-password");
            Request.Base64Content = kind == "invalid-base64" ? "%%%not-base64%%%" : Convert.ToBase64String(kind == "invalid-pfx" ? new byte[] { 1, 2, 3 } : raw);
            Request.Password = kind == "wrong-password" ? "wrong-password" : "secret-pfx-password";
        }
        private AdaPanelDbContext Context()
        { var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options); contexts.Add(db); return db; }
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
            if (BeforeInventory is not null) { var action = BeforeInventory; BeforeInventory = null; await action(); }
            if (HttpOnlyBeforeImport && Bindings == 0) return [];
            return [new IisHttpsBinding { SiteName = Name, BindingInformation = "*:" + Request.Port + ":" + Request.HostName,
                Thumbprint = thumbprint, CertificateReadable = true, StoreName = "My" }, .. AdditionalBindings];
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
        { var first = await Task.WhenAny(Entered.Task, action).WaitAsync(Deadline); Assert.True(first == Entered.Task, "Import failed before the intended read: " + (action.IsCompleted ? await action : null)); }
        public async Task<SslCertificateDto> Invoke()
        {
            var result = await controller.ImportAsync(Request, services.GetRequiredService<IIisSiteService>(),
                Fake<IMailService>((method, _) => method.Name == "GetWorkspaceAsync" ? Task.FromResult(new MailWorkspaceDto { Installed = false }) : throw new InvalidOperationException(method.Name)),
                NullLogger<SslController>.Instance, Token);
            return Assert.IsType<ApiResponse<SslCertificateDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public async Task AssertUnchanged()
        { Assert.Equal(original, await File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/ssl.json"))); Assert.Empty(Audits); Assert.Equal(0, WebmailCalls); }
        public Task<SslCertificateDto[]> Records() => Area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>());
        public void Dispose()
        { Release.TrySetResult(); foreach (var cert in loaded) cert.Dispose(); services?.Dispose(); foreach (var db in contexts) db.Dispose(); keeper.Dispose(); Cancellation.Dispose(); Area.Dispose(); }
    }
    private sealed class ObservedGate : IConcurrencyLock
    {
        private readonly ConcurrencyLock actual = new(); public bool Held;
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default) => actual.RunExclusiveAsync(key, async () => { Held = true; try { await action(); } finally { Held = false; } }, token);
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default) => actual.RunExclusiveAsync(key, async () => { Held = true; try { return await action(); } finally { Held = false; } }, token);
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = invoke; return result; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
}
