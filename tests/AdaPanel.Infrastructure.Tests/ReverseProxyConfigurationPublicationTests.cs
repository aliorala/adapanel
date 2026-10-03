using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.ReverseProxy;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ReverseProxyConfigurationPublicationTests
{
    public static IEnumerable<object[]> Operations() =>
        from operation in new[] { "enable", "remove", "rewrite" }
        from exists in new[] { true, false }
        select new object[] { operation, exists };
    public static IEnumerable<object[]> Failures() =>
        from operation in new[] { "enable", "remove", "rewrite" }
        from exists in new[] { true, false }
        from canceled in new[] { false, true }
        select new object[] { operation, exists, canceled };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task InterruptedXmlWritePreservesThePreviousConfigAndDoesNotAuditSuccess(string operation, bool exists, bool canceled)
    {
        using var f = new Fixture(exists);
        InterruptedXmlWriteStream? interrupted = null;
        f.ConfigureStream = stream => interrupted = new InterruptedXmlWriteStream(stream, canceled);

        var error = await Record.ExceptionAsync(() => f.Invoke(operation));

        Assert.NotNull(interrupted); Assert.True(interrupted.BytesWritten > 0);
        Assert.NotNull(error);
        f.AssertOriginal();
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task NativeCommitFailureCannotLeavePublishedXmlOrSuccessAudit(string operation, bool exists, bool canceled)
    {
        using var f = new Fixture(exists) { CommitFailure = canceled ? "cancel" : "reject" };

        var error = await Record.ExceptionAsync(() => f.Invoke(operation));

        Assert.True(f.CommitAttempts > 0, "The real service must reach the native commit boundary.");
        Assert.NotNull(error);
        f.AssertOriginal();
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CancellationAfterXmlSerializationDoesNotCommitOrPublish(string operation, bool exists)
    {
        using var f = new Fixture(exists);
        f.ConfigureStream = stream => new ObservedXmlWriteStream(stream, f.Cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke(operation));

        f.AssertOriginal();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IgnoredNativeArrCommitCannotPublishAWorkingProxyClaim(bool exists)
    {
        using var f = new Fixture(exists) { CommitFailure = "ignore" };

        Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Invoke("enable")));

        f.AssertOriginal();
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task ACompleteNativeAndXmlOperationStillReturnsMeasuredRulesAndAudits(string operation, bool exists)
    {
        using var f = new Fixture(exists);

        var result = await f.Invoke(operation);

        Assert.Single(f.Audits);
        var document = XDocument.Load(f.Configuration);
        var rules = document.Descendants("rule").ToArray();
        if (operation == "enable")
        {
            Assert.True(f.ArrEnabled); Assert.True(result.IsArrEnabled); Assert.True(result.IsProxyEnabled);
            Assert.Equal("http://127.0.0.1:3000", result.DestinationUrl);
            Assert.Contains(rules, rule => ((string?)rule.Attribute("name"))?.StartsWith("AdaPanel.ReverseProxy.Inbound|") == true);
        }
        else if (operation == "remove")
        {
            Assert.False(result.IsProxyEnabled);
            Assert.DoesNotContain(rules, rule => ((string?)rule.Attribute("name"))?.StartsWith("AdaPanel.ReverseProxy.Inbound|") == true);
        }
        else
        {
            Assert.Contains(result.RewriteRules, rule => rule.Name == "new-route");
            Assert.Contains(rules, rule => (string?)rule.Attribute("name") == "AdaPanel.CustomRewrite|new-route");
        }
        if (exists)
        {
            Assert.Contains(rules, rule => (string?)rule.Attribute("name") == "owner-rule");
            Assert.Equal("keep-private", document.Descendants("appSettings").Single().Element("add")!.Attribute("value")!.Value);
        }
        f.AssertNoTemporaryFiles();
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CancellationBeforeTheWriteLeavesAllStateUntouched(string operation, bool exists)
    {
        using var f = new Fixture(exists); f.Cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke(operation));

        f.AssertOriginal(); Assert.Equal(0, f.CommitAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAlreadyEnabledArrSettingSurvivesXmlFailure(bool canceled)
    {
        using var f = new Fixture(true) { ArrEnabled = true };
        f.ConfigureStream = stream => new InterruptedXmlWriteStream(stream, canceled);
        var error = await Record.ExceptionAsync(() => f.Invoke("enable"));
        Assert.NotNull(error); f.AssertOriginal(expectedArr: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAlreadyEnabledArrSettingStillAllowsSuccessfulPublication(bool exists)
    {
        using var f = new Fixture(exists) { ArrEnabled = true };
        Assert.True((await f.Invoke("enable")).IsProxyEnabled);
        Assert.True(f.ArrEnabled); Assert.Single(f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterACommittedArrChangeRestoresItBeforeReturning(bool exists)
    {
        using var f = new Fixture(exists);
        f.AfterCommit = () => { f.AfterCommit = null; f.Cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke("enable"));
        f.AssertOriginal(); Assert.Equal(2, f.CommitAttempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ACommitWhichAppliedThenThrewIsAlsoCompensated(bool exists, bool canceled)
    {
        using var f = new Fixture(exists);
        f.AfterCommit = () =>
        {
            f.AfterCommit = null;
            if (canceled) throw new OperationCanceledException("fixture commit completed before cancellation");
            throw new IOException("fixture commit completed before reporting failure");
        };
        var error = await Record.ExceptionAsync(() => f.Invoke("enable"));
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
        f.AssertOriginal(); Assert.Equal(2, f.CommitAttempts);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("ignore")]
    public async Task AnUnverifiedNativeRecoveryIsExplicitlyReported(string recoveryFailure)
    {
        using var f = new Fixture(true) { RecoveryFailure = recoveryFailure };
        f.ConfigureStream = stream => new InterruptedXmlWriteStream(stream, false);
        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Invoke("enable")));
        Assert.Contains("could not be restored", error.Details);
        f.AssertOriginal(expectedArr: true); Assert.Equal(2, f.CommitAttempts);
    }

    [Theory]
    [InlineData("preserveHostHeader")]
    [InlineData("reverseRewriteHostInResponseHeaders")]
    public async Task RecoveryDoesNotOverwriteAnArrSnapshotChangedByAnotherWriter(string setting)
    {
        using var f = new Fixture(true);
        f.ConfigureStream = stream =>
        {
            if (setting == "preserveHostHeader") f.PreserveHostHeader = true;
            else f.ReverseRewriteHost = true;
            return new InterruptedXmlWriteStream(stream, false);
        };
        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Invoke("enable")));
        Assert.Contains("could not be restored", error.Details);
        f.AssertOriginal(expectedArr: true); Assert.Equal(1, f.CommitAttempts);
        Assert.True(setting == "preserveHostHeader" ? f.PreserveHostHeader : f.ReverseRewriteHost);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task TheOldXmlIsStillPublishedAtTheActualNativeCommitBoundary(string operation, bool exists)
    {
        using var f = new Fixture(exists); var observed = false;
        f.BeforeCommit = () => { f.AssertFileOriginal(); observed = true; };
        await f.Invoke(operation);
        Assert.True(observed); Assert.Single(f.Audits);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] Original = Encoding.UTF8.GetBytes("<configuration><!-- exact original --><appSettings><add key=\"secret\" value=\"keep-private\" /></appSettings><system.webServer><rewrite><rules><rule name=\"owner-rule\"><match url=\"^old$\"/><action type=\"Rewrite\" url=\"old.php\"/></rule><rule name=\"AdaPanel.ReverseProxy.Inbound|root\"><match url=\"(.*)\"/><action type=\"Rewrite\" url=\"http://old.internal/{R:1}\"/></rule></rules></rewrite></system.webServer></configuration>\r\n");
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.ProxyPublication." + Guid.NewGuid().ToString("N"));
        private readonly bool existed;
        private readonly ProxyController controller;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext actorDb;
        private readonly ServiceProvider services;
        public string Configuration => Path.Combine(root, "web.config");
        public CancellationTokenSource Cancellation { get; } = new();
        public bool ArrEnabled;
        public bool PreserveHostHeader, ReverseRewriteHost;
        public int CommitAttempts;
        public string CommitFailure = "";
        public string RecoveryFailure = "";
        public Action? BeforeCommit, AfterCommit;
        public Func<Stream, Stream>? ConfigureStream;
        public List<string> Audits { get; } = [];
        public Fixture(bool exists)
        {
            existed = exists; Directory.CreateDirectory(root);
            connection.Open();
            actorDb = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            actorDb.Database.EnsureCreated();
            var actor = Guid.NewGuid();
            actorDb.Users.Add(new PanelUser { Id = actor, UserName = "proxy-admin", Email = "admin@example.test",
                IsActive = true, Role = UserRole.Administrator, PasswordHash = "proxy-fixture-password" });
            actorDb.SaveChanges();
            if (exists) File.WriteAllBytes(Configuration, Original);
            var site = new SiteDto { Id = 31, Name = "proxy.test", PhysicalPath = root, ApplicationPoolName = "AdaPanel.proxy.test" };
            var iis = Fake<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(site) : throw new InvalidOperationException("Unexpected IIS call: " + method.Name));
            var bootstrapper = Fake<IComponentBootstrapper>((method, _) => method.Name == "GetCatalogAsync"
                ? Task.FromResult(new SoftwareCatalogDto { Installed = new[] { "url-rewrite", "iis-arr" }
                    .Select(id => new InstalledComponentDto { ComponentId = id, Status = "Installed" }).ToArray() })
                : throw new InvalidOperationException("Unexpected install/process call: " + method.Name));
            var service = new ReverseProxyService(iis, bootstrapper, new ConcurrencyLock(), NullLogger<ReverseProxyService>.Instance)
            { ManagerFactory = () => new MemoryManager(this), ConfigurationStream = stream => ConfigureStream?.Invoke(stream) ?? stream };
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name == "GetSiteOwnerUserIdAsync"
                ? Task.FromResult<Guid?>(null) : throw new InvalidOperationException("Unexpected scope call: " + method.Name));
            var audit = Fake<IAuditService>((method, args) =>
            {
                Assert.Equal("WriteForSubscriptionAsync", method.Name);
                ((CancellationToken)args[^1]!).ThrowIfCancellationRequested();
                Audits.Add((string)args[0]!); return Task.CompletedTask;
            });
            services = new ServiceCollection().AddSingleton(actorDb).AddSingleton(iis)
                .AddSingleton<IConcurrencyLock>(new ConcurrencyLock()).AddSingleton(scopes).BuildServiceProvider();
            controller = new ProxyController(service, scopes, audit)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                        new Claim(ClaimTypes.Role, "Administrator"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("proxy-fixture-password"))], "fixture"))
                } }
            };
        }
        public async Task<ReverseProxySiteConfigurationDto> Invoke(string operation)
        {
            var result = operation switch
            {
                "enable" => await controller.EnableProxyAsync("proxy.test", new EnableReverseProxyRequest { DestinationUrl = "http://127.0.0.1:3000", MountPath = "/app" }, Cancellation.Token),
                "remove" => await controller.RemoveProxyAsync("proxy.test", Cancellation.Token),
                _ => await controller.AddRewriteRuleAsync("proxy.test", new AddRewriteRuleRequest { Name = "new-route", MatchPattern = "^new$", RewriteUrl = "new.php" }, Cancellation.Token)
            };
            return Assert.IsType<ApiResponse<ReverseProxySiteConfigurationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public void AssertOriginal(bool expectedArr = false)
        {
            Assert.Empty(Audits); Assert.Equal(expectedArr, ArrEnabled);
            AssertFileOriginal(); AssertNoTemporaryFiles();
        }
        public void AssertFileOriginal()
        {
            if (existed) Assert.Equal(Original, File.ReadAllBytes(Configuration));
            else Assert.False(File.Exists(Configuration), "No partial or uncommitted configuration may be published.");
        }
        public void AssertNoTemporaryFiles() => Assert.DoesNotContain(Directory.GetFiles(root), path => Path.GetFileName(path) != "web.config");
        public sealed class MemoryManager(Fixture fixture) : IDisposable
        {
            public Section Proxy { get; } = new(fixture.ArrEnabled, fixture.PreserveHostHeader, fixture.ReverseRewriteHost);
            public MemoryConfiguration GetApplicationHostConfiguration() => new(Proxy);
            public MemoryConfiguration GetWebConfiguration(string siteName) => new(Proxy);
            public void CommitChanges()
            {
                fixture.CommitAttempts++;
                fixture.BeforeCommit?.Invoke();
                if (fixture.CommitFailure == "reject") throw new IOException("fixture native commit rejected");
                if (fixture.CommitFailure == "cancel") throw new OperationCanceledException("fixture native commit canceled");
                if (fixture.CommitAttempts > 1 && fixture.RecoveryFailure == "reject") throw new IOException("fixture native recovery rejected");
                if (fixture.CommitFailure != "ignore" && !(fixture.CommitAttempts > 1 && fixture.RecoveryFailure == "ignore"))
                {
                    fixture.ArrEnabled = Proxy.Enabled;
                    fixture.PreserveHostHeader = Proxy.PreserveHostHeader;
                    fixture.ReverseRewriteHost = Proxy.ReverseRewriteHost;
                }
                fixture.AfterCommit?.Invoke();
            }
            public void Dispose() { }
        }
        public sealed class MemoryConfiguration(Section proxy)
        { public Section GetSection(string path) => proxy; }
        public sealed class Section(bool enabled, bool preserveHostHeader, bool reverseRewriteHost)
        {
            public bool Enabled { get; private set; } = enabled;
            public bool PreserveHostHeader { get; } = preserveHostHeader;
            public bool ReverseRewriteHost { get; } = reverseRewriteHost;
            public object GetAttributeValue(string name) => name switch
            { "enabled" => Enabled, "preserveHostHeader" => PreserveHostHeader, "reverseRewriteHostInResponseHeaders" => ReverseRewriteHost, _ => false };
            public void SetAttributeValue(string name, object value) { if (name == "enabled") Enabled = Convert.ToBoolean(value); }
        }
        public void Dispose() { Cancellation.Dispose(); services.Dispose(); actorDb.Dispose(); connection.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Call = invoke; return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
