using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailDomainDnsProvisioningTests
{
    [Fact]
    public async Task NewDomainPublishesMxAndDkimUsingTheBoundTechnicalIisName()
    {
        using var f = new Fixture();
        await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });

        Assert.True(f.DomainCreated);
        Assert.Equal("web-example.test", f.PublishedSite);
        Assert.Contains(f.PublishedRecords, r => r.Type == "MX" && r.Host == "@" && r.Value == "mail.example.test");
        Assert.Contains(f.PublishedRecords, r => r.Type == "TXT" && r.Host == "selector._domainkey" && r.Value.Contains("p=FIXTUREPUBLICKEY"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedOrAmbiguousOwnerNeverWritesDnsAndKeepsTheCreatedMailDomain(bool ambiguous)
    {
        using var f = new Fixture { Unresolved = !ambiguous, Ambiguous = ambiguous };
        await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });

        Assert.True(f.DomainCreated);
        Assert.Null(f.PublishedSite);
        Assert.Empty(f.PublishedRecords);
    }

    [Fact]
    public async Task DomainAliasDoesNotPublishMailRecordsIntoTheSitesDifferentDnsZone()
    {
        using var f = new Fixture { ZoneName = "other.test" };
        await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });
        Assert.True(f.DomainCreated);
        Assert.Null(f.PublishedSite);
    }

    // The CLI, IIS, DNS and preferences all use in-process fakes. No real process,
    // Windows user, service, certificate or DNS provider is touched by these cases.
    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly AdaPanelDbContext db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal bool DomainCreated, Unresolved, Ambiguous;
        internal string? PublishedSite;
        internal string ZoneName = "example.test";
        internal IReadOnlyCollection<UpsertDnsRecordRequest> PublishedRecords = [];
        internal MailService Service { get; }

        internal Fixture()
        {
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            var cli = area.DosyaDeposu.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cli, "stalwart-cli.exe"), "fixture only; never executed");
            db.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cli, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.SaveChanges();
            area.DosyaDeposu.WriteAsync("config/mail.json", new
            { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }).GetAwaiter().GetResult();
            var preferences = MailServerIdentityTests.IdentityProxy.Make<IPanelPreferencesService>((method, _) => method == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.30" })
                : throw new InvalidOperationException(method));
            var iis = MailServerIdentityTests.IdentityProxy.Make<IIisSiteService>((method, _) =>
            {
                if (method != "GetSitesAsync") throw new InvalidOperationException(method);
                var sites = new List<SiteDto>();
                if (!Unresolved) sites.Add(new() { Name = "web-example.test", Bindings = [new() { Protocol = "http", Port = 80, HostName = "example.test" }] });
                if (Ambiguous) sites.Add(new() { Name = "other-site", Bindings = [new() { Protocol = "https", Port = 443, HostName = "example.test" }] });
                return Task.FromResult<IReadOnlyCollection<SiteDto>>(sites);
            });
            var dns = MailServerIdentityTests.IdentityProxy.Make<IDnsService>((method, args) =>
            {
                if (method == "GetZoneAsync")
                {
                    Assert.Equal("web-example.test", args![0]);
                    return Task.FromResult(new SiteDnsZoneDto { SiteName = "web-example.test", ZoneName = ZoneName });
                }
                if (method != "UpsertRecordsAsync") throw new InvalidOperationException(method);
                var siteName = (string)args![0]!;
                if (siteName != "web-example.test") throw new AppException("sites.site_not_found", 404, 404);
                PublishedSite = siteName;
                PublishedRecords = ((IEnumerable<UpsertDnsRecordRequest>)args[1]!).ToArray();
                return Task.FromResult(new SiteDnsZoneDto { SiteName = siteName, IsWindowsZonePresent = true, IsWindowsDnsAvailable = true });
            });
            var protector = new SahteKoruyucu();
            Service = new(area.DosyaDeposu, protector, preferences, iis, dns, db,
                new StalwartRuntime(area.DosyaDeposu, protector, db, new SahteIstemciFabrikasi(new SahteBulut())), NullLogger<MailService>.Instance)
            {
                CommandRunner = (_, command, _, _) =>
                {
                    if (command.StartsWith("create domain ", StringComparison.Ordinal)) DomainCreated = true;
                    var output = command switch
                    {
                        "query domain --json" when DomainCreated => """[{"id":"domain-id","name":"example.test"}]""",
                        "get domain domain-id --json" => """{"id":"domain-id","name":"example.test"}""",
                        "query DkimSignature --json" => """[{"id":"dkim-id","domainId":"domain-id","stage":"active"}]""",
                        "get DkimSignature dkim-id --json" => """{"selector":"selector","publicKey":"FIXTUREPUBLICKEY","@type":"Rsa"}""",
                        _ => "[]"
                    };
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = output });
                }
            };
        }

        public void Dispose() { db.Dispose(); area.Dispose(); }
    }
}
