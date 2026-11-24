using System.Collections;
using System.Reflection;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Php;
using AdaPanel.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpLoggingLifecycleTests
{
    [Fact]
    public async Task Tool_site_logging_is_not_adopted_or_rewritten_by_tenant_setup()
    {
        var service = new PhpService(null!, null!, null!, null!, NullLogger<PhpService>.Instance);
        var result = await service.EnsureSiteErrorLoggingAsync("AdaPanel.Roundcube");
        Assert.Equal("unverified", result.Status);
        Assert.Contains("kendi bileşeni", result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_version_assignment_configures_logging_after_commit_unless_provisioning_defers(bool defer)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-version-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = Path.Combine(root, "runtime"); Directory.CreateDirectory(runtime);
            await File.WriteAllTextAsync(Path.Combine(runtime, "php-cgi.exe"), "fixture executable never started");
            await File.WriteAllTextAsync(Path.Combine(root, ".user.ini"), "; custom preserved\nsession.name = APP\n");
            using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.InstalledComponents.Add(new InstalledComponent { ComponentId = "php-8.3", Status = ComponentInstallStatus.Installed, InstallDir = runtime });
            await db.SaveChangesAsync();
            var site = new SiteDto { Name = "fixture.test", PhysicalPath = root, ApplicationRootPath = root, ApplicationPoolName = "fixture" };
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Site = site;
            var manager = new Manager(); var loggingCalls = 0;
            var config = new PhpSiteLogConfiguration(_ =>
            {
                Assert.Equal(1, manager.Commits); loggingCalls++; return "fixture";
            }, (_, _, _, _) => null, (_, _, _, _) => Task.FromResult<string?>(null));
            var service = new PhpService(db, null!, new ConcurrencyLock(), sites, NullLogger<PhpService>.Instance)
            {
                SiteAssignmentManagerFactory = () => manager,
                SiteLogConfiguration = config,
                SiteLogProbe = new PhpSiteLogProbe { Send = (_, _, _, _) => Task.FromResult(new PhpSiteLogProbe.Reply(503, "unavailable")) }
            };
            var assignment = await service.SetSitePhpVersionAsync(site.Name, new SetSitePhpVersionRequest { ComponentId = "php-8.3", DeferErrorLogging = defer });
            Assert.Equal(1, manager.Commits); Assert.Equal(defer ? 0 : 1, loggingCalls);
            var ini = await File.ReadAllTextAsync(Path.Combine(root, ".user.ini"));
            Assert.Contains("session.name = APP", ini); Assert.Contains("; custom preserved", ini);
            if (defer) { Assert.Null(assignment.ErrorLogging); Assert.DoesNotContain("error_log =", ini); }
            else { Assert.Equal("unverified", assignment.ErrorLogging?.Status); Assert.Contains(".adapanel-logs/php-errors.log", ini.Replace('\\', '/')); }
        }
        finally { Directory.Delete(root, true); }
    }

    public class SiteProxy : DispatchProxy
    {
        public SiteDto Site = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(Site) : throw new InvalidOperationException(targetMethod.Name);
    }
    public sealed class Manager : IDisposable
    {
        public object[] Sites { get; } = [new SiteName()];
        public Collection FastCgi { get; } = new();
        public Collection Handlers { get; } = new();
        public int Commits;
        public Configuration GetApplicationHostConfiguration() => new(FastCgi);
        public Configuration GetWebConfiguration(string _) => new(Handlers);
        public void CommitChanges() => Commits++;
        public void Dispose() { }
    }
    public sealed class SiteName { public string Name => "fixture.test"; }
    public sealed class Configuration(Collection items) { public Section GetSection(string _) => new(items); }
    public sealed class Section(Collection items) { public Collection Collection => items; }
    public sealed class Element
    {
        private readonly Dictionary<string, object> attributes = [];
        public object GetAttributeValue(string name) => attributes.GetValueOrDefault(name, "");
        public void SetAttributeValue(string name, object value) => attributes[name] = value;
    }
    public sealed class Collection : IEnumerable
    {
        private readonly List<object> items = [];
        public object CreateElement(string _) => new Element();
        public void Add(object item) => items.Add(item);
        public void AddAt(int index, object item) => items.Insert(index, item);
        public void Remove(object item) => items.Remove(item);
        public IEnumerator GetEnumerator() => items.GetEnumerator();
    }
}
