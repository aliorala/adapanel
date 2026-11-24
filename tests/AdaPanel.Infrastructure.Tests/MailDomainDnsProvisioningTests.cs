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

    [Fact]
    public async Task UnpublishedDnsIsReportedAsWarningNotSilentSuccess()
    {
        using var f = new Fixture { Unresolved = true };
        var workspace = await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });
        Assert.Contains(MailDomainWarnings.DnsNotPublished, workspace.Warnings);
    }

    [Fact]
    public async Task PublishedDnsProducesNoDnsWarning()
    {
        using var f = new Fixture();
        var workspace = await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });
        Assert.DoesNotContain(MailDomainWarnings.DnsNotPublished, workspace.Warnings);
    }

    [Fact]
    public async Task ConcurrentAutoResponderAndListPolicyUpdatesDoNotLoseWrites()
    {
        using var f = new Fixture();
        var tasks = new List<Task>();
        for (var i = 0; i < 25; i++)
        {
            var n = i;
            tasks.Add(Task.Run(() => f.Service.UpdateAutoRespondersAsync(
                store => { store[$"u{n}@example.test"] = new MailService.AutoResponderEntry { Enabled = true, Message = "m" }; return true; },
                CancellationToken.None)));
            tasks.Add(Task.Run(() => f.Service.UpdateListPoliciesAsync(
                store => { store[$"l{n}@example.test"] = new MailService.ListPolicyEntry(); return true; },
                CancellationToken.None)));
        }

        await Task.WhenAll(tasks);
        var responders = await f.Store.ReadAsync<Dictionary<string, MailService.AutoResponderEntry>>("config/mail-autoresponders.json", new(), CancellationToken.None);
        var policies = await f.Store.ReadAsync<Dictionary<string, MailService.ListPolicyEntry>>("config/mail-list-policies.json", new(), CancellationToken.None);
        Assert.Equal(25, responders!.Count);
        Assert.Equal(25, policies!.Count);
    }

    [Fact]
    public async Task DeletingAnAccountDropsItsAutoResponderSoARecreatedAddressStartsClean()
    {
        using var f = new Fixture { DomainCreated = true };
        await f.Store.WriteAsync("config/mail-autoresponders.json", new Dictionary<string, MailService.AutoResponderEntry>
        {
            ["a@example.test"] = new() { Enabled = true, Message = "old" },
            ["b@example.test"] = new() { Enabled = true, Message = "keep" }
        });
        await f.Service.DeleteAccountAsync("a@example.test");
        var left = await f.Store.ReadAsync<Dictionary<string, MailService.AutoResponderEntry>>("config/mail-autoresponders.json", new(), CancellationToken.None);
        Assert.DoesNotContain("a@example.test", left!.Keys);
        Assert.Contains("b@example.test", left.Keys);
    }

    [Fact]
    public async Task DeletingADomainDropsItsAutoRespondersAndListPoliciesOnly()
    {
        using var f = new Fixture { DomainCreated = true };
        await f.Store.WriteAsync("config/mail-autoresponders.json", new Dictionary<string, MailService.AutoResponderEntry>
        {
            ["a@example.test"] = new() { Enabled = true, Message = "old" },
            ["b@other.test"] = new() { Enabled = true, Message = "keep" }
        });
        await f.Store.WriteAsync("config/mail-list-policies.json", new Dictionary<string, MailService.ListPolicyEntry>
        {
            ["l@example.test"] = new(),
            ["l@other.test"] = new()
        });
        await f.Service.DeleteDomainAsync("example.test");
        var responders = await f.Store.ReadAsync<Dictionary<string, MailService.AutoResponderEntry>>("config/mail-autoresponders.json", new(), CancellationToken.None);
        var policies = await f.Store.ReadAsync<Dictionary<string, MailService.ListPolicyEntry>>("config/mail-list-policies.json", new(), CancellationToken.None);
        Assert.Equal(["b@other.test"], responders!.Keys);
        Assert.Equal(["l@other.test"], policies!.Keys);
    }

    [Fact]
    public async Task DomainDeletionFailingMidwayReportsPartialResultWithWhatWasRemoved()
    {
        using var f = new Fixture { DomainCreated = true };
        f.Override = command => command switch
        {
            "query account --json" => """[{"id":"acc-1","emailAddress":"a@example.test"}]""",
            "get account acc-1 --json" => """{"id":"acc-1","domainId":"domain-id","emailAddress":"a@example.test"}""",
            _ => null
        };
        f.FailDomainDelete = true;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteDomainAsync("example.test"));
        Assert.Equal("mail.domain_delete_partial", error.MessageKey);
        Assert.Contains("account:a@example.test", error.Details);
    }

    [Fact]
    public async Task SenderPolicyFailureOnDomainCreationIsReportedAsWarning()
    {
        using var f = new Fixture { FailSenderPolicy = true };
        var workspace = await f.Service.CreateDomainAsync(new() { DomainName = "example.test" });
        Assert.Contains(MailDomainWarnings.SenderPolicyNotApplied, workspace.Warnings);
    }

    [Fact]
    public async Task AccountDeleteSucceedsWithLeftoverWarningWhenStoreCleanupFails()
    {
        using var f = new Fixture { DomainCreated = true };
        // Dosya yolunu klasor yaparak depo okumasini/yazmasini bozar.
        var path = Path.Combine(f.StoreRoot, "config", "mail-autoresponders.json");
        Directory.CreateDirectory(path);
        var workspace = await f.Service.DeleteAccountAsync("a@example.test");
        Assert.Contains(MailDomainWarnings.StateCleanupFailed, workspace.Warnings);
    }

    [Fact]
    public async Task DeletingAListMemberDropsItsPolicyEvenIfDomainDeleteFailsMidway()
    {
        using var f = new Fixture { DomainCreated = true, FailDomainDelete = true };
        await f.Store.WriteAsync("config/mail-list-policies.json", new Dictionary<string, MailService.ListPolicyEntry>
        {
            ["l@example.test"] = new(),
            ["l@other.test"] = new()
        });
        f.Override = command => command switch
        {
            "query MailingList --json" => """[{"id":"list-1","emailAddress":"l@example.test"}]""",
            "get MailingList list-1 --json" => """{"id":"list-1","domainId":"domain-id","emailAddress":"l@example.test"}""",
            _ => null
        };
        await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteDomainAsync("example.test"));
        var policies = await f.Store.ReadAsync<Dictionary<string, MailService.ListPolicyEntry>>("config/mail-list-policies.json", new(), CancellationToken.None);
        Assert.Equal(["l@other.test"], policies!.Keys);
    }

    [Fact]
    public void ControllerWarningMergeAndExternalDnsClassification()
    {
        // Servis DNS uyarisi verdi ama controller'in ikinci denemesi yazdi -> uyari duser; sender uyarisi korunur.
        var merged = AdaPanel.Api.Controllers.MailController.MergeCreateWarnings(
            [MailDomainWarnings.DnsNotPublished, MailDomainWarnings.SenderPolicyNotApplied], dnsApplied: true);
        Assert.Equal([MailDomainWarnings.SenderPolicyNotApplied], merged);
        var notApplied = AdaPanel.Api.Controllers.MailController.MergeCreateWarnings([], dnsApplied: false);
        Assert.Equal([MailDomainWarnings.DnsNotPublished], notApplied);

        var left = AdaPanel.Api.Controllers.MailController.MergeDeleteLeftovers(
            [MailDomainWarnings.StateCleanupFailed], ["dns_records_not_removed"]);
        Assert.Equal(["dns_records_not_removed", MailDomainWarnings.StateCleanupFailed], left);

        Assert.True(AdaPanel.Api.Controllers.MailController.IsNoManagedZone(new AppException("mail.dns_zone_missing", 409, 409)));
        Assert.True(AdaPanel.Api.Controllers.MailController.IsNoManagedZone(new AppException("mail.invalid_domain", 400, 400)));
        Assert.False(AdaPanel.Api.Controllers.MailController.IsNoManagedZone(new AppException("mail.invalid_domain", 409, 409)));
        Assert.False(AdaPanel.Api.Controllers.MailController.IsNoManagedZone(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task SiteCreationDnsPublishReportsFailureInsteadOfSwallowingIt()
    {
        var mail = MailServerIdentityTests.IdentityProxy.Make<IMailService>((method, _) => method == "GetDnsRecordsAsync"
            ? Task.FromResult(new MailDnsDto { Domain = "example.test", Records = [new MailDnsRecordDto { Type = "MX", Host = "@", Value = "mail.example.test", Priority = 10 }] })
            : throw new InvalidOperationException(method));
        var failing = MailServerIdentityTests.IdentityProxy.Make<IDnsService>((method, _) => throw new AppException("dns.write_failed", 500, 500));
        var (published, detail) = await MailDnsPublication.PublishForSiteAsync(mail, failing, "web-example.test", "example.test", CancellationToken.None);
        Assert.False(published);
        Assert.Equal("dns.write_failed", detail);

        var notWritten = MailServerIdentityTests.IdentityProxy.Make<IDnsService>((method, _) =>
            Task.FromResult(new SiteDnsZoneDto { SiteName = "web-example.test", IsWindowsDnsAvailable = false }));
        var second = await MailDnsPublication.PublishForSiteAsync(mail, notWritten, "web-example.test", "example.test", CancellationToken.None);
        Assert.False(second.Published);
        Assert.NotEmpty(second.Detail);
    }

    [Fact]
    public void ControllersReportHonestOutcomesForMailDomainCreateAndDelete()
    {
        var controller = Oku("src", "AdaPanel.Api", "Controllers", "MailController.cs");
        Assert.Contains("mail.domain.completed_with_warnings", controller, StringComparison.Ordinal);
        Assert.Contains("mail.domain.delete_completed_with_leftovers", controller, StringComparison.Ordinal);
        foreach (var lang in new[] { "tr", "en" })
        {
            var json = Oku("frontend", "src", "i18n", lang + ".json");
            foreach (var key in new[] { "mail.domain.completed_with_warnings", "mail.domain.delete_completed_with_leftovers", "mail.domain_delete_partial" })
                Assert.Contains("\"" + key + "\"", json, StringComparison.Ordinal);
        }
    }

    private static string Oku(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday)) return File.ReadAllText(aday);
            klasor = Path.Combine(klasor, "..");
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    // The CLI, IIS, DNS and preferences all use in-process fakes. No real process,
    // Windows user, service, certificate or DNS provider is touched by these cases.
    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly AdaPanelDbContext db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal bool DomainCreated, Unresolved, Ambiguous, FailDomainDelete, FailSenderPolicy;
        internal string StoreRoot => area.Kok;
        internal Func<string, string?>? Override;
        internal JsonFileStore Store => area.DosyaDeposu;
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
                    if (FailSenderPolicy && command.Contains("MtaStageMail", StringComparison.Ordinal))
                        return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "boom" });
                    if (FailDomainDelete && command.StartsWith("delete domain ", StringComparison.Ordinal))
                        return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "boom" });
                    var output = Override?.Invoke(command) ?? command switch
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
