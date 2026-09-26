using System.Reflection;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Health;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DomainInspectionTests
{
    [Fact]
    public async Task Unknown_IIS_state_is_an_incomplete_inspection_and_is_retried()
    {
        var fixture = new Fixture { SiteState = "Unknown" };
        var findings = Assert.Single((await fixture.Service.InspectAllAsync()).Results).Findings;
        Assert.Contains(findings, finding => finding.Key == "repair.inspection_failed");
        Assert.DoesNotContain(findings, finding => finding.Key == "repair.site_stopped");
        fixture.SiteState = "Started";
        Assert.DoesNotContain(Assert.Single((await fixture.Service.InspectAllAsync()).Results).Findings,
            finding => finding.Key == "repair.inspection_failed");
        Assert.Equal(2, fixture.Inspections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unavailable_firewall_is_visible_not_repaired_and_not_cached(bool throws)
    {
        var fixture = new Fixture { FirewallUnavailable = true, FirewallThrows = throws };
        var first = await fixture.Service.InspectAllAsync();
        var warning = Assert.Single(first.ServerFindings);
        Assert.Equal("repair.inspection_failed", warning.Key);
        Assert.True(warning.NeedsConfirmation);
        Assert.False(warning.Repaired);
        fixture.FirewallUnavailable = false;
        Assert.Empty((await fixture.Service.InspectAllAsync()).ServerFindings);
        Assert.Equal(2, fixture.Inspections);
    }

    [Fact]
    public async Task Unavailable_IIS_read_is_visible_and_not_cached_as_healthy()
    {
        var fixture = new Fixture { SiteUnavailable = true };
        Assert.Contains(Assert.Single((await fixture.Service.InspectAllAsync()).Results).Findings,
            finding => finding.Key == "repair.inspection_failed");
        fixture.SiteUnavailable = false;
        Assert.DoesNotContain(Assert.Single((await fixture.Service.InspectAllAsync()).Results).Findings,
            finding => finding.Key == "repair.inspection_failed");
        Assert.Equal(2, fixture.Inspections);
    }

    [Fact]
    public async Task Bulk_inspection_uses_one_snapshot_and_never_calls_mutating_dns_reads()
    {
        var fixture = new Fixture(bulk: true);
        var result = await fixture.Service.InspectAllAsync();
        Assert.Equal(0, result.FailedSites);
        Assert.Equal(1, fixture.BulkReads);
        Assert.Equal(0, fixture.Inspections);
        Assert.Equal(0, fixture.PublishedReads);
        Assert.Equal(0, fixture.ZoneReads);
        Assert.Equal(1, fixture.MailReads);
        fixture.MissingMailRecord = true;
        var next = await fixture.Service.InspectAsync("example.test");
        Assert.Equal(1, next.AppliedMailRecords);
        Assert.Equal(2, fixture.BulkReads);
    }

    [Fact]
    public async Task Bulk_failure_is_visible_and_retried_but_repair_still_reads_after_writes()
    {
        var fixture = new Fixture(bulk: true) { FailDefaults = true };
        Assert.Equal(1, (await fixture.Service.InspectAllAsync()).FailedSites);
        fixture.FailDefaults = false;
        Assert.Equal(0, (await fixture.Service.InspectAllAsync()).FailedSites);
        Assert.Equal(2, fixture.BulkReads);
        fixture.MissingMailRecord = true;
        Assert.Empty((await fixture.Service.RefreshAsync("example.test")).Error);
        Assert.Equal(2, fixture.BulkReads);
        Assert.Equal(3, fixture.ZoneReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inspection_reads_each_source_once_and_next_inspection_reads_fresh_data(bool published)
    {
        var fixture = new Fixture { Published = published };
        var first = await fixture.Service.InspectAsync("example.test");
        Assert.Equal(1, fixture.MailReads);
        Assert.Equal(1, fixture.PublishedReads);
        Assert.Equal(1, fixture.ZoneReads);
        Assert.Equal(0, first.AppliedMailRecords);
        fixture.MissingMailRecord = true;
        var second = await fixture.Service.InspectAsync("example.test");
        Assert.Equal(2, fixture.MailReads);
        Assert.Equal(2, fixture.PublishedReads);
        Assert.Equal(2, fixture.ZoneReads);
        Assert.Equal(1, second.AppliedMailRecords);
    }

    [Fact]
    public async Task Repair_reads_again_after_applying_mail_and_dns_changes()
    {
        var fixture = new Fixture { MissingMailRecord = true };
        var result = await fixture.Service.RefreshAsync("example.test");
        Assert.Empty(result.Error);
        Assert.Equal(1, result.AppliedMailRecords);
        Assert.DoesNotContain(result.Findings, finding => finding.Key == "repair.records_not_published");
        Assert.Equal(2, fixture.MailReads);
        Assert.Equal(3, fixture.ZoneReads);
        Assert.Equal(1, fixture.PublishedReads);
    }

    [Fact]
    public async Task Canceled_inspection_is_not_cached_as_successful()
    {
        using var source = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.BeforeInspection = () => { source.Cancel(); return Task.FromCanceled(source.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.InspectAllAsync(source.Token));
        fixture.BeforeInspection = null;
        Assert.Equal(0, (await fixture.Service.InspectAllAsync()).FailedSites);
        Assert.Equal(2, fixture.Inspections);
    }

    [Fact]
    public async Task Shared_failed_mail_read_still_produces_the_missing_mail_finding()
    {
        var fixture = new Fixture { MailUnavailable = true };
        var result = await fixture.Service.InspectAsync("example.test");
        Assert.Equal(1, fixture.MailReads);
        Assert.Contains(result.Findings, finding => finding.Key == "repair.mail_domain_missing");
    }

    [Fact]
    public async Task Replacing_a_site_without_changing_count_invalidates_the_summary()
    {
        var fixture = new Fixture();
        await fixture.Service.InspectAllAsync();
        await fixture.Service.InspectAllAsync();
        Assert.Equal(1, fixture.Inspections);
        fixture.SiteName = "replacement.test";
        var result = await fixture.Service.InspectAllAsync();
        Assert.Equal("replacement.test", Assert.Single(result.Results).SiteName);
        Assert.Equal(2, fixture.Inspections);
        fixture.SiteState = "Stopped";
        result = await fixture.Service.InspectAllAsync();
        Assert.Contains(Assert.Single(result.Results).Findings, finding => finding.Key == "repair.site_stopped");
        Assert.Equal(3, fixture.Inspections);
    }

    [Fact]
    public async Task Failed_inspection_is_retried_instead_of_cached_for_ten_minutes()
    {
        var fixture = new Fixture { FailDefaults = true };
        Assert.Equal(1, (await fixture.Service.InspectAllAsync()).FailedSites);
        fixture.FailDefaults = false;
        Assert.Equal(0, (await fixture.Service.InspectAllAsync()).FailedSites);
        Assert.Equal(2, fixture.Inspections);
    }

    [Fact]
    public async Task Repair_during_inspection_prevents_that_inspection_from_repopulating_the_cache()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture();
        fixture.BeforeInspection = async () => { entered.TrySetResult(); await release.Task; };
        var pending = fixture.Service.InspectAllAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Service.RefreshAsync("example.test");
        }
        finally { release.TrySetResult(); }
        await pending;
        fixture.BeforeInspection = null;
        await fixture.Service.InspectAllAsync();
        Assert.Equal(2, fixture.Inspections);
    }

    private sealed class Fixture
    {
        public int MailReads, PublishedReads, ZoneReads, Inspections, BulkReads;
        public bool Published = true, MissingMailRecord, MailUnavailable, FailDefaults;
        public string SiteName = "example.test", SiteState = "Started";
        public Func<Task>? BeforeInspection;
        public bool FirewallUnavailable, FirewallThrows, SiteUnavailable;
        public DomainRefreshService Service { get; }
        private SiteDto Site => new() { Id = 1, Name = SiteName, State = SiteState };
        public Fixture(bool bulk = false)
        {
            var sites = Stub<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site]),
                "GetSiteAsync" => SiteUnavailable ? Task.FromException<SiteDto>(new IOException("IIS unavailable")) : Task.FromResult(Site),
                _ => throw new InvalidOperationException(method.Name)
            });
            var dns = Stub<IDnsService>((method, args) => method.Name switch
            {
                "RepairDefaultsAsync" => Defaults((bool)args![1]!),
                "GetPublishedRecordKeysAsync" => PublishedKeys(),
                "GetZoneAsync" => Zone(),
                "EnsureZonePublishedAsync" => Task.CompletedTask,
                "UpsertRecordsAsync" => ApplyRecords(),
                _ => throw new InvalidOperationException(method.Name)
            });
            var mail = Stub<IMailService>((method, _) => method.Name switch
            {
                "GetDnsRecordsAsync" => MailRecords(),
                "EnsureServerHostnameAsync" => Task.FromResult(false),
                "SyncCertificateAsync" => Task.FromResult<MailCertificateDto?>(null),
                _ => throw new InvalidOperationException(method.Name)
            });
            var firewall = Stub<IFirewallService>((_, _) => FirewallUnavailable && FirewallThrows
                ? Task.FromException<IReadOnlyCollection<PortStatusDto>>(new IOException("Firewall unavailable"))
                : Task.FromResult<IReadOnlyCollection<PortStatusDto>>(FirewallUnavailable
                    ? [new() { Port = 80, Required = true, FirewallAllowed = false, FirewallMeasured = false }] : []));
            // No files are created; the distinct data path also isolates static cache entries.
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
            {
                DataRoot = Path.Combine(Path.GetTempPath(), "adapanel-inspection-" + Guid.NewGuid().ToString("N"))
            }));
            var inspection = bulk ? Stub<IDnsInspectionService>((_, _) =>
            {
                BulkReads++;
                if (FailDefaults) throw new IOException("bulk unavailable");
                return Task.FromResult<IReadOnlyDictionary<string, DnsInspectionDto>>(
                    new Dictionary<string, DnsInspectionDto> { [SiteName] = new()
                    {
                        Zone = new SiteDnsZoneDto { ZoneName = SiteName, IsWindowsDnsAvailable = true,
                            IsWindowsZonePresent = true, Records = MissingMailRecord ? [] :
                                [new() { Type = "MX", Host = "@", Value = "mail.example.test" }] },
                        PublishedRecordKeys = MissingMailRecord ? [] : ["MX|@"]
                    } });
            }) : null;
            Service = new DomainRefreshService(sites, dns, mail, firewall, null!, store,
                NullLogger<DomainRefreshService>.Instance, inspection);
        }
        private async Task<IReadOnlyList<string>> Defaults(bool apply)
        {
            if (!apply)
            {
                Inspections++;
                if (BeforeInspection is { } before) await before();
                if (FailDefaults) throw new IOException("unavailable");
            }
            return [];
        }
        private Task<MailDnsDto> MailRecords()
        {
            MailReads++;
            return MailUnavailable ? Task.FromException<MailDnsDto>(new IOException("mail unavailable")) :
                Task.FromResult(new MailDnsDto { Domain = SiteName,
                    Records = [new() { Type = "MX", Host = "@", Value = "mail.example.test" }] });
        }
        private Task<IReadOnlyCollection<string>?> PublishedKeys()
        {
            PublishedReads++;
            return Task.FromResult<IReadOnlyCollection<string>?>(!Published ? null : MissingMailRecord ? [] : ["MX|@"]);
        }
        private Task<SiteDnsZoneDto> Zone()
        {
            ZoneReads++;
            return Task.FromResult(new SiteDnsZoneDto { ZoneName = SiteName, IsWindowsDnsAvailable = Published,
                IsWindowsZonePresent = Published,
                Records = MissingMailRecord ? [] : [new() { Type = "MX", Host = "@", Value = "mail.example.test" }] });
        }
        private Task<SiteDnsZoneDto> ApplyRecords()
        {
            MissingMailRecord = false;
            return Task.FromResult(new SiteDnsZoneDto());
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
