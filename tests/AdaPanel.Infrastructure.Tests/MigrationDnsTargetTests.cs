using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationDnsTargetTests
{
    [Fact]
    public async Task Plan_resolves_future_selected_site_without_reading_IIS_and_apply_pins_the_actual_identity()
    {
        var f = new Fixture();
        var check = await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default);
        Assert.Empty(check.Blockers); Assert.Equal(0, f.SiteState.Reads); Assert.Null(f.Dns.Imported);
        var applied = await f.Apply(); Assert.True(applied.Verified);
        Assert.Equal(f.SiteState.Current.Id, f.Dns.Imported!.SiteId); Assert.Equal(f.Path, f.Dns.Imported.ApplicationRootPath);
    }
    [Fact]
    public async Task Explicit_address_maps_transform_typed_values_and_SPF_mechanisms_without_touching_verification_MX_DMARC_or_external_DKIM()
    {
        var f = new Fixture(); f.Selection.AddressMappings.Add("192.0.2.10", "203.0.113.20"); f.Selection.AddressMappings.Add("old.source.test", "new.target.test");
        f.Zone.Records.AddRange([
            Record("A", "@", "192.0.2.10"), Record("CNAME", "www", "old.source.test."),
            Record("MX", "@", "external.brevo.test.", priority: 20),
            Record("TXT", "@", "  v=spf1  ip4:192.0.2.10/32 include:old.source.test -all  "),
            Record("TXT", "_verify", "  192.0.2.10 old.source.test \"preserve\"  "),
            Record("TXT", "_dmarc", "v=DMARC1; p=reject; rua=mailto:a@old.source.test"),
            Record("CNAME", "brevo._domainkey", "selector.brevo.test."), Record("TXT", "old._domainkey", "v=DKIM1; p=SOURCEPUBLICKEY"),
            Record("NS", "delegated", "external.ns.test.")]);
        var result = await f.Apply(); Assert.True(result.Verified); var records = f.Dns.Imported!.Records;
        Assert.Equal("203.0.113.20", records.Single(record => record.Type == "A").Value);
        Assert.Equal("new.target.test", records.Single(record => record.Name == "www").Value);
        Assert.Equal("  v=spf1  ip4:203.0.113.20/32 include:new.target.test -all  ", records.Single(record => record.Type == "TXT" && record.Name == "@").Value);
        Assert.Equal("  192.0.2.10 old.source.test \"preserve\"  ", records.Single(record => record.Name == "_verify").Value);
        Assert.Equal("external.brevo.test", records.Single(record => record.Type == "MX").Value);
        Assert.Equal(20, records.Single(record => record.Type == "MX").Priority);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:a@old.source.test", records.Single(record => record.Name == "_dmarc").Value);
        Assert.Equal("selector.brevo.test", records.Single(record => record.Name == "brevo._domainkey").Value);
        Assert.Equal("v=DKIM1; p=SOURCEPUBLICKEY", records.Single(record => record.Name == "old._domainkey").Value);
        Assert.Equal("external.ns.test", records.Single(record => record.Type == "NS").Value);
        Assert.Contains("migration.dns_dkim_public_records_copied_signing_unverified", result.RequiredActions);
        Assert.Contains("migration.dns_target_authorities_and_registrar_cutover_required", result.RequiredActions);
    }
    [Fact]
    public async Task Source_authorities_and_DNSSEC_keys_are_explicit_cutover_actions_and_no_default_MX_is_created()
    {
        var f = new Fixture(); f.Zone.Records.AddRange([Record("SOA", "@", "source soa"), Record("NS", "@", "old.ns.test"), Record("DNSKEY", "@", "source key"), Record("DS", "@", "source ds")]);
        var result = await f.Apply(); Assert.True(result.Verified); Assert.Empty(f.Dns.Imported!.Records);
        Assert.Contains("migration.dns_source_authorities_not_copied", result.RequiredActions);
        Assert.Contains("migration.dns_dnssec_keys_and_ds_require_review", result.RequiredActions);
    }
    [Theory]
    [InlineData("v=spf1 include:%{i}.source.test ip4:192.0.2.10 -all")]
    [InlineData("v=spf1 notamechanism:192.0.2.10 -all")]
    public async Task Unknown_or_macro_SPF_keeps_complete_source_bytes_and_explicit_mapping_review(string value)
    {
        var f = new Fixture(); f.Zone.Records.Add(Record("TXT", "@", value)); f.Selection.AddressMappings.Add("192.0.2.10", "203.0.113.20");
        var result = await f.Apply(); Assert.True(result.Verified); Assert.Equal(value, f.Dns.Imported!.Records.Single().Value);
        Assert.Contains("migration.dns_spf_mapping_requires_review", result.RequiredActions);
    }
    [Theory]
    [InlineData("existing")]
    [InlineData("configuration")]
    [InlineData("unmeasured")]
    public async Task Existing_or_unmeasured_zone_does_not_become_a_fresh_target(string failure)
    {
        var f = new Fixture(); f.Dns.Fault = failure;
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers); Assert.Null(f.Dns.Imported);
    }
    [Fact]
    public async Task Unselected_site_foreign_link_and_renamed_zone_are_rejected_before_native_queries()
    {
        var f = new Fixture(); f.Selection.AllSites = false;
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers); Assert.Equal(0, f.Dns.Reads);
        f.Selection.AllSites = true; f.Item.Target.LinkedSiteId = MigrationResourceKey.Create(f.Item.PackageId, "foreign");
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        f.Item.Target.LinkedSiteId = ""; f.Item.Target.TargetName = "other.test";
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
    }
    [Theory]
    [InlineData("path")]
    [InlineData("binding")]
    public async Task Actual_site_identity_must_match_planned_path_and_zone_before_import(string fault)
    {
        var f = new Fixture(); f.SiteState.Current = new() { Id = 1, Name = "target-site", ApplicationRootPath = fault == "path" ? f.Path + "-replacement" : f.Path, Bindings = [new() { HostName = fault == "binding" ? "foreign.test" : "source.test" }] };
        Assert.False((await f.Apply()).Verified); Assert.Null(f.Dns.Imported);
    }
    [Fact]
    public async Task Lost_native_acknowledgement_and_cancelled_import_preserve_partial_resource_evidence()
    {
        var f = new Fixture(); f.Dns.Fault = "partial";
        var result = await f.Apply(); Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.Contains("dns-zone:source.test", result.CreatedResources);
        f.Dns.Fault = "cancel"; var cancelled = await f.Apply(); Assert.False(cancelled.Verified); Assert.True(cancelled.PartialStatePossible); Assert.Equal("migration.dns_cancelled", cancelled.ErrorCode);
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("CNAME-collision")]
    [InlineData("outside-owner")]
    [InlineData("negative-ttl")]
    [InlineData("invalid-priority")]
    [InlineData("unknown-type")]
    [InlineData("cross-family-map")]
    public async Task Invalid_record_or_address_mapping_is_rejected_before_reading_or_mutating_native_DNS(string fault)
    {
        var f = new Fixture();
        f.Zone.Records.Add(fault switch
        {
            "owner" => Record("A", "x';$(throw 'INJECTED')", "192.0.2.1"),
            "outside-owner" => Record("A", "foreign.test.", "192.0.2.1"),
            "negative-ttl" => Record("A", "@", "192.0.2.1", ttl: -1),
            "invalid-priority" => Record("MX", "@", "mx.external.test", priority: 65536),
            "unknown-type" => Record("UNRECOGNIZED", "@", "payload"),
            "CNAME-collision" => Record("CNAME", "www", "external.test"),
            _ => Record("A", "@", "192.0.2.1")
        });
        if (fault == "CNAME-collision") f.Zone.Records.Add(Record("TXT", "www", "verification"));
        if (fault == "cross-family-map") f.Selection.AddressMappings.Add("192.0.2.1", "2001:db8::1");
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        Assert.Equal(0, f.Dns.Reads); Assert.Null(f.Dns.Imported);
    }
    internal static PortableMigrationDnsRecord Record(string type, string name, string value, int ttl = 300, int priority = 0) => new() { Type = type, Name = name, Value = value, Ttl = ttl, Priority = priority };
    [Theory]
    [InlineData("long-txt")]
    [InlineData("control-txt")]
    [InlineData("control-caa")]
    [InlineData("srv-port-zero")]
    [InlineData("srv-null-target")]
    [InlineData("ptr-underscore")]
    [InlineData("owner-trailing-underscore")]
    [InlineData("ttl-zero")]
    [InlineData("ttl-low")]
    [InlineData("ttl-high")]
    public async Task Wire_valid_but_unmanageable_records_block_plan_and_apply_before_native_mutation(string fault)
    {
        var f = new Fixture(); var record = fault switch
        {
            "long-txt" => Record("TXT", "@", new string('x', 736)),
            "control-txt" => Record("TXT", "@", "source\tvalue"),
            "control-caa" => Record("CAA", "@", "0 issue source\tvalue"),
            "srv-port-zero" => Record("SRV", "_service._tcp", "0 0 server.source.test"),
            "srv-null-target" => Record("SRV", "_service._tcp", "0 443 ."),
            "ptr-underscore" => Record("PTR", "one", "_not-a-host.source.test"),
            "owner-trailing-underscore" => Record("A", "owner_", "192.0.2.1"),
            _ => new PortableMigrationDnsRecord { Type = "A", Name = "@", Value = "192.0.2.1", Ttl = fault == "ttl-zero" ? 0 : fault == "ttl-low" ? 30 : 86401 }
        };
        f.Zone.Records.Add(record); var original = System.Text.Json.JsonSerializer.Serialize(f.Manifest);
        Assert.Contains("migration.dns_management_format_unsupported", (await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        var result = await f.Apply(); Assert.False(result.Verified); Assert.False(result.PartialStatePossible);
        Assert.Equal("migration.dns_management_format_unsupported", result.ErrorCode); Assert.Null(f.Dns.Imported); Assert.Equal(0, f.Dns.Reads);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(f.Manifest));
    }

    private sealed class Fixture
    {
        internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AdaPanel.DnsTarget", Guid.NewGuid().ToString("N"));
        internal readonly MigrationPlanItemDto Item = new() { Id = "dns-item", Kind = "dns", SourceId = "dns:source.test", PackageId = Guid.NewGuid(), Target = new() { TargetName = "source.test" } };
        internal readonly MigrationPlanRequest Selection = new() { AllSites = true };
        internal readonly PortableMigrationDnsZone Zone = new() { Id = "dns:source.test", Name = "source.test", SiteIds = ["source-site"] };
        internal readonly PortableMigrationManifest Manifest;
        internal readonly DnsBoundary Dns = new(); internal readonly MigrationDnsSiteProxy SiteState;
        internal readonly MigrationDnsTarget Target;
        internal Fixture()
        {
            var sites = DispatchProxy.Create<IIisSiteService, MigrationDnsSiteProxy>(); SiteState = (MigrationDnsSiteProxy)(object)sites;
            SiteState.Current = new() { Id = 1, Name = "target-site", ApplicationRootPath = Path, PhysicalPath = Path, Bindings = [new() { HostName = "source.test" }] };
            Selection.Mappings.Add(new() { ResourceId = MigrationResourceKey.Create(Item.PackageId, "source-site"), TargetName = "target-site", TargetPath = Path });
            Manifest = new() { DnsZones = [Zone], Sites = [new() { Id = "source-site", Name = "source.test" }] };
            Target = new(Dns, sites, new MigrationDnsTestGates());
        }
        internal Task<MigrationComponentResult> Apply() => Target.ApplyAsync(Item, Manifest, Selection, [], default);
    }
    private sealed class DnsBoundary : IMigrationDnsProvisioner
    {
        internal int Reads; internal string Fault = ""; internal MigrationDnsImportRequest? Imported;
        public Task<MigrationDnsSnapshot> InspectAsync(string zone, CancellationToken token) { Reads++; return Task.FromResult(new MigrationDnsSnapshot { Measured = Fault != "unmeasured", Available = true, Exists = Fault == "existing", ConfigurationExists = Fault == "configuration" }); }
        public Task<MigrationDnsImportResult> ImportFreshAsync(MigrationDnsImportRequest request, CancellationToken token)
        {
            Imported = request; var result = new MigrationDnsImportResult { Attempted = true, ZoneCreated = true, Verified = Fault is not ("partial" or "cancel"), PartialStatePossible = Fault is "partial" or "cancel", ErrorCode = Fault == "cancel" ? "migration.dns_cancelled" : Fault == "partial" ? "migration.dns_native_create_unconfirmed" : "" };
            result.CreatedResources.Add("dns-zone:" + request.ZoneName); return Task.FromResult(result);
        }
    }
}
public class MigrationDnsSiteProxy : DispatchProxy
{
    public SiteDto Current = new(); public int Reads;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    { if (method?.Name != nameof(IIisSiteService.GetSiteAsync)) throw new InvalidOperationException("Unexpected IIS mutation/inventory"); Reads++; return Task.FromResult(Current); }
}
internal sealed class MigrationDnsTestGates : IConcurrencyLock
{
    public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => action();
    public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => action();
    public Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default) => action();
    public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default) => action();
}
