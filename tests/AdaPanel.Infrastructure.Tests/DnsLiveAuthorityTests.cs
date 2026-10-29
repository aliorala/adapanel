using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsLiveAuthorityTests
{
    [Fact]
    public async Task Same_address_general_save_discovers_verified_legacy_defaults_and_repeated_save_is_idempotent()
    {
        using var f = new Fixture();
        await f.SeedLegacyDefaults();
        await f.SetNative(new Record("A", "@", "84.51.22.239"), new Record("A", "mail", "84.51.22.239"),
            new Record("A", "ns1", "84.51.22.239"), new Record("A", "ns2", "84.51.22.239"), new Record("TXT", "@", "v=spf1 a mx ip4:84.51.22.239 ~all"),
            new Record("A", "cdn", "203.0.113.77", 900));
        var ftp = new AdaPanel.Infrastructure.Ftp.FtpService(null!, f.Store(), null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = f.Root }))
        {
            NativeScript = (script, _, _) => {
                Assert.DoesNotContain("ADAPANEL-FTP-ADDRESS-WRITE", script);
                return Task.FromResult(new CommandResult { StandardOutput = JsonSerializer.Serialize(new { Exists = true, Owned = true, Address = "176.43.253.97" }) });
            }
        };
        var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "176.43.253.97" }));
        var dns = f.Service(ftp);
        var controller = new AdaPanel.Api.Controllers.PanelSettingsController(preferences, Stub<IAuditService>((_, _) => Task.CompletedTask), dns)
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        var request = new UpdatePanelPreferencesRequest { ServerAddress = "176.43.253.97", ReconcileServerAddress = true };
        await controller.UpdateAsync(request, default);
        var first = await dns.GetServerAddressDnsSyncAsync();
        Assert.False(first!.Pending); Assert.Equal("84.51.22.239", first.OldAddress);
        Assert.Equal(5, first.Zones.Single().ChangedRecords); Assert.True(first.Ftp!.Succeeded); Assert.False(first.Ftp.Changed);
        await controller.UpdateAsync(request, default);
        var second = await dns.GetServerAddressDnsSyncAsync();
        Assert.False(second!.Pending); Assert.Equal(0, second.Zones.Single().ChangedRecords);
        Assert.Equal(1, f.Publications);
        var records = (await dns.GetZoneAsync("example.test")).Records;
        Assert.Equal(4, records.Count(record => record.Type == "A" && record.Value == "176.43.253.97"));
        Assert.Contains(records, record => record.Value == "v=spf1 a mx ip4:176.43.253.97 ~all");
        Assert.Contains(records, record => record.Host == "cdn" && record.Value == "203.0.113.77" && record.Ttl == 900);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("unmanaged")]
    [InlineData("nat")]
    public async Task Same_address_reconciliation_never_guesses_custom_or_unowned_addresses(string scenario)
    {
        using var f = new Fixture();
        await f.SeedLegacyDefaults(scenario != "unmanaged", scenario == "mixed", scenario == "nat");
        var before = await File.ReadAllTextAsync(f.Store().ResolvePath("config/dns-zones.json"));
        var result = await f.Service().ReconcileServerAddressAsync("176.43.253.97");
        Assert.True(result!.Skipped); Assert.False(result.Pending); Assert.NotEmpty(result.Error);
        Assert.Equal(0, f.Publications);
        Assert.Equal(before, await File.ReadAllTextAsync(f.Store().ResolvePath("config/dns-zones.json")));
        Assert.Null(await f.Service().GetServerAddressDnsSyncAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task General_settings_save_runs_real_DNS_and_FTP_migration_and_same_IP_save_retries_FTP_failure(bool failFirstFtpWrite)
    {
        using var f = new Fixture();
        await f.SeedCache("84.51.22.239");
        await f.SetNative(new Record("A", "@", "84.51.22.239"), new Record("TXT", "@", "v=spf1 a mx ip4:84.51.22.239 ~all"));
        var ftpAddress = "84.51.22.239";
        var failFtp = failFirstFtpWrite;
        var ftp = new AdaPanel.Infrastructure.Ftp.FtpService(null!, f.Store(), null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = f.Root }))
        {
            NativeScript = (script, _, _) => {
                if (script.Contains("ADAPANEL-FTP-ADDRESS-WRITE"))
                {
                    if (failFtp) return Task.FromResult(new CommandResult { ExitCode = 1 });
                    ftpAddress = "176.43.253.97";
                    return Task.FromResult(new CommandResult());
                }
                return Task.FromResult(new CommandResult { StandardOutput = JsonSerializer.Serialize(new { Exists = true, Owned = true, Address = ftpAddress }) });
            }
        };
        var settings = new PanelPreferencesDto { ServerAddress = "84.51.22.239" };
        var preferences = Stub<IPanelPreferencesService>((method, args) => {
            if (method.Name == nameof(IPanelPreferencesService.UpdateAsync)) settings = new PanelPreferencesDto { ServerAddress = ((UpdatePanelPreferencesRequest)args![0]!).ServerAddress };
            return Task.FromResult(settings);
        });
        var dns = f.Service(ftp);
        var controller = new AdaPanel.Api.Controllers.PanelSettingsController(preferences,
            Stub<IAuditService>((_, _) => Task.CompletedTask), dns)
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        var request = new UpdatePanelPreferencesRequest { ServerAddress = "176.43.253.97" };
        var first = await controller.UpdateAsync(request, default);
        var firstResponse = Assert.IsType<AdaPanel.Core.Dtos.Common.ApiResponse<PanelPreferencesDto>>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(first.Result).Value);
        Assert.Equal(failFirstFtpWrite, firstResponse.Data!.ServerAddressDnsSync!.Pending);
        var records = (await dns.GetZoneAsync("example.test")).Records;
        Assert.Contains(records, record => record.Type == "A" && record.Value == "176.43.253.97");
        Assert.Contains(records, record => record.Value == "v=spf1 a mx ip4:176.43.253.97 ~all");
        if (failFirstFtpWrite)
        {
            Assert.False(firstResponse.Data.ServerAddressDnsSync.Ftp!.Succeeded);
            failFtp = false;
            await controller.UpdateAsync(request, default); // Ordinary Save, same server IP.
        }
        var completed = await dns.GetServerAddressDnsSyncAsync();
        Assert.False(completed!.Pending);
        Assert.True(completed.Ftp!.Succeeded);
        Assert.Equal("176.43.253.97", completed.Ftp.CurrentAddress);
        Assert.Equal("176.43.253.97", ftpAddress);
    }
    [Theory]
    [InlineData("A", "84.51.22.239", "176.43.253.97")]
    [InlineData("A", "84.51.22.2390", "84.51.22.2390")]
    [InlineData("TXT", "v=spf1 a mx include:x ip4:84.51.22.239/32 -all", "v=spf1 a mx include:x ip4:176.43.253.97/32 -all")]
    [InlineData("TXT", "v=spf1 +ip4:84.51.22.239 ip4:84.51.22.239/24 ip4:84.51.22.2390 ~all", "v=spf1 +ip4:176.43.253.97 ip4:84.51.22.239/24 ip4:84.51.22.2390 ~all")]
    [InlineData("TXT", "verification=84.51.22.239", "verification=84.51.22.239")]
    [InlineData("MX", "84.51.22.239", "84.51.22.239")]
    public void Server_address_replacement_changes_only_exact_A_and_SPF_mechanisms(string type, string value, string expected)
        => Assert.Equal(expected, DnsService.ReplaceServerAddress(type, value, "84.51.22.239", "176.43.253.97"));

    [Theory]
    [InlineData("")]
    [InlineData("mail.example.test")]
    [InlineData("2001:db8::1")]
    [InlineData("192.168.1.80")]
    [InlineData("127.1")]
    [InlineData("0.0.0.0")]
    public void Server_address_migration_rejects_non_public_or_ambiguous_addresses(string address)
        => Assert.False(DnsService.TryPublicIpv4(address, out _));

    [Fact]
    public async Task Server_address_migration_preserves_custom_records_metadata_and_shared_zone_and_updates_NAT()
    {
        using var f = new Fixture();
        await f.SeedCache("84.51.22.239");
        await f.AddPeer();
        await f.Service().UpdateTemplateAsync(new UpdateDnsTemplateRequest {
            IpMappings = [new() { PrivateAddress = "192.168.1.80", PublicAddress = "84.51.22.239" }],
            Records = [new() { Type = "A", Host = "@", Value = "84.51.22.239", Ttl = 900 },
                new() { Type = "A", Host = "mail", Value = "{ip}" },
                new() { Type = "TXT", Host = "@", Value = "v=spf1 a mx ip4:84.51.22.239 ~all" }]
        });
        await f.SetNative(new Record("A", "@", "84.51.22.239", 900), new Record("A", "cdn", "203.0.113.10"),
            new Record("TXT", "@", "v=spf1 a mx ip4:84.51.22.239/32 include:external.test ~all", 7200),
            new Record("TXT", "@", "verification=84.51.22.239"), new Record("MX", "@", "external.test", 300, 30));
        var status = await f.Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(status.Pending);
        Assert.True(Assert.Single(status.Zones).Succeeded);
        Assert.Equal(2, status.Zones.Single().ChangedRecords);
        var records = (await f.Service().GetZoneAsync("example.test")).Records;
        Assert.Contains(records, record => record.Id == "old" && record.Value == "176.43.253.97" && record.Ttl == 900);
        Assert.Contains(records, record => record.Value == "v=spf1 a mx ip4:176.43.253.97/32 include:external.test ~all" && record.Ttl == 7200);
        Assert.Contains(records, record => record.Value == "203.0.113.10");
        Assert.Contains(records, record => record.Value == "verification=84.51.22.239");
        Assert.Contains(records, record => record.Type == "MX" && record.Priority == 30 && record.Ttl == 300);
        Assert.Equal("176.43.253.97", (await f.Service().GetTemplateAsync()).IpMappings.Single().PublicAddress);
        var template = await f.Service().GetTemplateAsync();
        Assert.Contains(template.Records, record => record.Host == "@" && record.Type == "A" && record.Value == "176.43.253.97" && record.Ttl == 900);
        Assert.Contains(template.Records, record => record.Host == "mail" && record.Value == "{ip}");
        Assert.Contains(template.Records, record => record.Value == "v=spf1 a mx ip4:176.43.253.97 ~all");
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.Root, "config", "dns-address-backups")).Length);
    }

    [Fact]
    public async Task Server_address_migration_retries_partial_native_delete_from_persistent_intent()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "84.51.22.239"));
        f.FailPublication = true;
        var failed = await f.Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.True(failed.Pending);
        Assert.False(failed.Zones.Single().Succeeded);
        // Model a native failure after removal and before replacement.
        await f.SetNative();
        f.FailPublication = false;
        var retried = await f.Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(retried.Pending);
        Assert.Equal("176.43.253.97", (await f.Service().GetZoneAsync("example.test")).Records.Single().Value);
        Assert.False((await f.Service().GetServerAddressDnsSyncAsync())!.Pending);
    }

    [Fact]
    public async Task Server_address_prepare_is_durable_and_unmeasured_matching_zone_remains_pending()
    {
        using var f = new Fixture();
        await f.SeedCache("84.51.22.239");
        await f.Service().PrepareServerAddressDnsSyncAsync("84.51.22.239", "176.43.253.97");
        Assert.True((await f.Service().GetServerAddressDnsSyncAsync())!.Pending);
        var service = f.Service();
        service.AvailabilityOverride = _ => Task.FromResult(false);
        var result = await service.SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.True(result.Pending);
        Assert.False(result.Zones.Single().Skipped);
        Assert.False(result.Zones.Single().Succeeded);
        Assert.Equal(0, f.Publications);
        await Assert.ThrowsAsync<AppException>(() => service.SyncServerAddressAsync("176.43.253.97", "203.0.113.10"));
    }

    [Fact]
    public async Task Server_address_migration_keeps_existing_destination_metadata_when_old_and_new_coexist()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "84.51.22.239", 900), new Record("A", "@", "176.43.253.97", 3600));
        var result = await f.Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(result.Pending);
        var record = Assert.Single((await f.Service().GetZoneAsync("example.test")).Records);
        Assert.Equal("176.43.253.97", record.Value);
        Assert.Equal(3600, record.Ttl);
    }

    [Fact]
    public async Task Server_address_migration_isolates_missing_external_zone_as_unmeasured_warning()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.AddExternalReference();
        await f.SetNative(new Record("A", "@", "84.51.22.239"));
        var result = await f.Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(result.Pending);
        Assert.True(result.Zones.Single(zone => zone.ZoneName == "example.test").Succeeded);
        Assert.False(result.Zones.Single(zone => zone.ZoneName == "external.test").Succeeded);
        Assert.True(result.Zones.Single(zone => zone.ZoneName == "external.test").Skipped);
        Assert.Equal(1, f.Publications);
        Assert.Equal("84.51.22.239", (await f.Service().GetServerAddressDnsSyncAsync())!.OldAddress);
    }
    [Fact]
    public async Task Refresh_reads_native_values_preserves_metadata_and_backs_up_old_intent_without_native_writes()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"), new Record("TXT", "@", "verification=keep"), new Record("MX", "@", "remote.example.test", 900, 20), new Record("CAA", "@", "0 issue letsencrypt.org", 900));
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.Equal("198.51.100.8", zone.Records.Single(r => r.Type == "A").Value);
        Assert.DoesNotContain(zone.Records, r => r.Value.StartsWith("v=spf1"));
        Assert.Contains(zone.Records, r => r.Value == "verification=keep");
        Assert.Contains(zone.Records, r => r.Type == "CAA" && r.Value == "0 issue letsencrypt.org" && r.Ttl == 900);
        Assert.Equal(0, f.Publications);
        Assert.True(File.Exists(Path.Combine(f.Root, "config", "dns-zones.pre-native-authority.json")));
        Assert.Contains("192.168.1.80", await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.pre-native-authority.json")));
        Assert.Contains("198.51.100.8", await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.json")));
    }

    [Fact]
    public async Task Repeated_repair_adds_missing_defaults_using_NAT_address_and_preserves_custom_records()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"), new Record("A", "@", "198.51.100.9"),
            new Record("TXT", "@", "verification=keep"), new Record("TXT", "@", "v=spf1 include:external.example ~all"),
            new Record("TXT", "selector._domainkey", "v=DKIM1; p=custom"), new Record("TXT", "_dmarc", "v=DMARC1; p=reject"),
            new Record("SRV", "_custom._tcp", "7 1234 service.example.test", 900, 4));
        var first = await f.Service().RepairDefaultsAsync("example.test");
        var second = await f.Service().RepairDefaultsAsync("example.test");
        Assert.NotEmpty(first);
        Assert.Empty(second);
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.Equal(2, zone.Records.Count(r => r.Type == "A" && r.Host == "@"));
        Assert.Single(zone.Records, r => r.Type == "TXT" && r.Host == "@" && r.Value.StartsWith("v=spf1"));
        Assert.Contains(zone.Records, r => r.Host == "ns1" && r.Value == "198.51.100.8");
        Assert.Contains(zone.Records, r => r.Value == "v=DKIM1; p=custom");
        Assert.Contains(zone.Records, r => r.Value == "v=DMARC1; p=reject");
        Assert.Contains(zone.Records, r => r.Host == "_custom._tcp" && r.Ttl == 900 && r.Priority == 4);
        Assert.DoesNotContain(zone.Records, r => r.Value.Contains("192.168.1.80"));
    }

    [Fact]
    public async Task Explicit_full_defaults_reset_replaces_custom_records_and_keeps_a_backup()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"), new Record("A", "@", "198.51.100.9"),
            new Record("TXT", "@", "v=spf1 include:external.example ~all"), new Record("TXT", "@", "verification=keep"),
            new Record("CAA", "@", "0 issue letsencrypt.org", 900));
        var zone = await f.Service().ResetDefaultsAsync("example.test");
        Assert.Single(zone.Records, r => r.Type == "A" && r.Host == "@");
        Assert.Single(zone.Records, r => r.Value.StartsWith("v=spf1"));
        Assert.DoesNotContain(zone.Records, r => r.Value == "verification=keep");
        Assert.DoesNotContain(zone.Records, r => r.Type == "CAA");
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "config", "dns-reset-backups")));
        Assert.Contains("verification=keep", await File.ReadAllTextAsync(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_malformed_native_read_does_not_present_or_replace_stale_cache(bool malformed)
    {
        using var f = new Fixture();
        await f.SeedCache();
        var before = await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.json"));
        var service = f.Service();
        service.InventoryCommand = (_, _) => Task.FromResult(new CommandResult { ExitCode = malformed ? 0 : 5, StandardOutput = malformed ? "{}" : "", StandardError = "access denied" });
        await Assert.ThrowsAsync<AppException>(() => service.GetZoneAsync("example.test"));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.json")));
        Assert.Equal(0, f.Publications);
    }

    [Fact]
    public async Task Automatic_mail_repair_never_duplicates_existing_addresses_or_custom_policy()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "mail", "203.0.113.8"), new Record("TXT", "@", "verification=keep"),
            new Record("TXT", "@", "v=spf1 include:external.example ~all"), new Record("TXT", "selector._domainkey", "v=DKIM1; p=custom"));
        var requests = new UpsertDnsRecordRequest[] {
            new() { Type = "A", Host = "mail", Value = "198.51.100.8" },
            new() { Type = "TXT", Host = "@", Value = "v=spf1 mx a ~all" },
            new() { Type = "TXT", Host = "selector._domainkey", Value = "v=DKIM1; p=generated" }
        };
        await f.Service().EnsureMissingRecordsAsync("example.test", requests);
        var zone = await f.Service().EnsureMissingRecordsAsync("example.test", requests);
        Assert.Equal(0, f.Publications);
        Assert.Equal(4, zone.Records.Length);
        Assert.Contains(zone.Records, r => r.Host == "mail" && r.Value == "203.0.113.8");
    }

    [Fact]
    public async Task Explicit_defaults_reset_uses_public_address_and_a_single_template_SPF()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "192.168.1.80"), new Record("TXT", "@", "verification=keep"));
        var zone = await f.Service().ResetDefaultsAsync("example.test");
        Assert.DoesNotContain(zone.Records, r => r.Type == "A" && r.Value == "192.168.1.80");
        Assert.Contains(zone.Records, r => r.Host == "@" && r.Type == "A" && r.Value == "198.51.100.8");
        Assert.DoesNotContain(zone.Records, r => r.Value == "verification=keep");
    }

    [Fact]
    public async Task New_service_instance_sees_external_native_edit_after_restart()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"));
        await f.Service().GetZoneAsync("example.test");
        await f.SetNative(new Record("A", "@", "198.51.100.9"));
        Assert.Equal("198.51.100.9", (await f.Service().GetZoneAsync("example.test")).Records.Single().Value);
        Assert.Equal(0, f.Publications);
    }

    [Fact]
    public async Task Native_change_between_read_and_publish_fails_before_record_removal()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"));
        f.BeforePublication = () => f.SetNative(new Record("A", "@", "198.51.100.8"), new Record("A", "mail", "203.0.113.55"));
        await Assert.ThrowsAsync<AppException>(() => f.Service().RepairDefaultsAsync("example.test"));
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.Contains(zone.Records, r => r.Host == "mail" && r.Value == "203.0.113.55");
        Assert.DoesNotContain(zone.Records, r => r.Host == "ns1");
    }

    [Fact]
    public async Task Failed_native_publication_is_not_success_and_refresh_reconciles_actual_state()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"));
        f.FailPublication = true;
        await Assert.ThrowsAsync<AppException>(() => f.Service().RepairDefaultsAsync("example.test"));
        Assert.Single((await f.Service().GetZoneAsync("example.test")).Records);
    }

    [Theory]
    [InlineData("example.test")]
    [InlineData("Example.Test")]
    public void Web_domain_ignores_preview_and_mail_binding_order(string siteName)
    {
        var site = new SiteDto { Name = siteName, Bindings = [new() { HostName = "preview.192.0.2.1.nip.io" }, new() { HostName = "mail.example.test" }, new() { HostName = "www.example.test" }, new() { HostName = "example.test" }] };
        Assert.Equal("example.test", SiteDomainSelection.WebDomain(site));
    }

    [Fact]
    public async Task NAT_mapping_updates_only_matching_A_values_and_survives_service_restart()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "192.168.1.80"), new Record("A", "@", "198.51.100.9"),
            new Record("TXT", "@", "verification=keep"), new Record("MX", "@", "remote.example.test", 900, 20));
        var result = await f.Service().UpdateTemplateAsync(new UpdateDnsTemplateRequest {
            IpMappings = [new() { PrivateAddress = "192.168.1.80", PublicAddress = "203.0.113.10" }], ApplyIpMappingsToExistingZones = true
        });
        Assert.True(Assert.Single(result.MappingResults).Succeeded);
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.Contains(zone.Records, r => r.Type == "A" && r.Value == "203.0.113.10");
        Assert.Contains(zone.Records, r => r.Type == "A" && r.Value == "198.51.100.9");
        Assert.Contains(zone.Records, r => r.Type == "MX" && r.Value == "remote.example.test");
        Assert.Contains(zone.Records, r => r.Value == "verification=keep");
        Assert.Equal("203.0.113.10", Assert.Single((await f.Service().GetTemplateAsync()).IpMappings).PublicAddress);
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "config", "dns-nat-backups")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_mutation_requires_authority_for_every_member_of_a_shared_native_zone(bool deleting)
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.AddPeer();
        await f.SetNative(new Record("A", "@", "192.168.1.80"));
        var before = await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.json"));
        Task Deny(IReadOnlyList<string> names, CancellationToken token)
        {
            Assert.Contains("peer.test", names);
            throw new AppException("auth.site_forbidden", 403, 403);
        }
        var service = f.Service();
        await Assert.ThrowsAsync<AppException>(() => deleting
            ? service.DeleteRecordAsync("example.test", "old", Deny, CancellationToken.None)
            : service.UpsertRecordAsync("example.test", new() { Type = "A", Host = "new", Value = "198.51.100.8" }, Deny, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(f.Root, "config", "dns-zones.json")));
        Assert.Equal(0, f.Publications);
    }

    [Fact]
    public async Task NAT_mapping_controls_template_defaults_without_changing_IIS_bindings()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "198.51.100.8"));
        await f.Service().UpdateTemplateAsync(new UpdateDnsTemplateRequest { IpMappings = [new() { PrivateAddress = "192.168.1.80", PublicAddress = "203.0.113.10" }] });
        var zone = await f.Service().ResetDefaultsAsync("example.test");
        Assert.Contains(zone.Records, r => r.Host == "@" && r.Type == "A" && r.Value == "203.0.113.10");
        Assert.Contains(zone.Records, r => r.Value.StartsWith("v=spf1") && r.Value.Contains("203.0.113.10"));
    }

    [Fact]
    public async Task Changed_public_mapping_can_be_retried_after_failed_native_publication()
    {
        using var f = new Fixture();
        await f.SeedCache();
        await f.SetNative(new Record("A", "@", "203.0.113.10"));
        await f.Service().UpdateTemplateAsync(new UpdateDnsTemplateRequest { IpMappings = [new() { PrivateAddress = "192.168.1.80", PublicAddress = "203.0.113.10" }] });
        var request = new UpdateDnsTemplateRequest { IpMappings = [new() { PrivateAddress = "192.168.1.80", PublicAddress = "203.0.113.11" }], ApplyIpMappingsToExistingZones = true };
        f.FailPublication = true;
        var failed = await f.Service().UpdateTemplateAsync(request);
        Assert.False(Assert.Single(failed.MappingResults).Succeeded);
        Assert.Equal("203.0.113.10", (await f.Service().GetZoneAsync("example.test")).Records.Single().Value);
        f.FailPublication = false;
        var retried = await f.Service().UpdateTemplateAsync(request);
        Assert.True(Assert.Single(retried.MappingResults).Succeeded);
        Assert.Equal("203.0.113.11", (await f.Service().GetZoneAsync("example.test")).Records.Single().Value);
    }

    [Theory]
    [InlineData("192.168.1.80", "192.168.1.81")]
    [InlineData("bad", "203.0.113.10")]
    [InlineData("192.168.1.80", "127.0.0.1")]
    public async Task Invalid_NAT_mappings_do_not_change_configuration(string privateAddress, string publicAddress)
    {
        using var f = new Fixture();
        await f.SeedCache();
        await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateTemplateAsync(new UpdateDnsTemplateRequest {
            IpMappings = [new() { PrivateAddress = privateAddress, PublicAddress = publicAddress }]
        }));
        Assert.False(File.Exists(Path.Combine(f.Root, "config", "dns-template.json")));
        Assert.Equal(0, f.Publications);
    }

    private sealed record Record(string Type, string Host, string Value, int Ttl = 3600, int? Priority = null);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-live-dns-" + Guid.NewGuid().ToString("N"));
        private string NativePath => Path.Combine(Root, "native.json");
        internal int Publications;
        internal bool FailPublication;
        internal Func<Task>? BeforePublication;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        internal Fixture() { Directory.CreateDirectory(Root); }
        internal Task SetNative(params Record[] records) => File.WriteAllTextAsync(NativePath, JsonSerializer.Serialize(records));
        internal async Task SeedCache(string address = "192.168.1.80")
        {
            var store = Store();
            await store.WriteAsync("config/dns-zones.json", new[] { new { siteName = "example.test", zoneName = "example.test", defaultsVersion = 4, nameServers = new[] { "ns1.example.test", "ns2.example.test" }, records = new[] { new { id = "old", type = "A", host = "@", value = address, ttl = 3600, isManaged = true } } } });
        }
        internal async Task SeedLegacyDefaults(bool managed = true, bool mixed = false, bool differentNat = false)
        {
            var hosts = new[] { "@", "mail", "ns1", "ns2" };
            await Store().WriteAsync("config/dns-zones.json", new[] { new { siteName = "example.test", zoneName = "example.test", defaultsVersion = 4,
                records = hosts.Select(host => new { id = host, type = "A", host, value = "84.51.22.239", ttl = 3600, isManaged = managed && host != "mail" }).ToArray() } });
            await Store().WriteAsync("config/dns-template.json", new DnsTemplateDto {
                Records = hosts.Select(host => new DnsTemplateRecordDto { Type = "A", Host = host, Value = mixed && host == "mail" ? "203.0.113.10" : "84.51.22.239" }).ToList(),
                IpMappings = differentNat ? [new() { PrivateAddress = "192.168.1.80", PublicAddress = "203.0.113.10" }] : []
            });
        }
        internal async Task AddPeer()
        {
            var store = Store();
            var zones = await store.ReadAsync("config/dns-zones.json", Array.Empty<JsonElement>());
            await store.WriteAsync("config/dns-zones.json", zones.Append(JsonSerializer.SerializeToElement(new { siteName = "peer.test", zoneName = "example.test", records = Array.Empty<object>(), defaultsVersion = 4 })).ToArray());
        }
        internal async Task AddExternalReference()
        {
            var store = Store();
            var zones = await store.ReadAsync("config/dns-zones.json", Array.Empty<JsonElement>());
            await store.WriteAsync("config/dns-zones.json", zones.Append(JsonSerializer.SerializeToElement(new { siteName = "external.test", zoneName = "external.test", records = Array.Empty<object>(), defaultsVersion = 4 })).ToArray());
        }
        internal JsonFileStore Store() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
        internal DnsService Service(IFtpService? ftp = null)
        {
            var site = new SiteDto { Name = "example.test", Bindings = [new() { HostName = "example.test", IpAddress = "192.168.1.80" }] };
            var sites = Stub<IIisSiteService>((method, _) => method.Name == nameof(IIisSiteService.GetSitesAsync)
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([site]) : Task.FromResult(site));
            var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "198.51.100.8" }));
            return new DnsService(sites, preferences, Store(), NullLogger<DnsService>.Instance, ftp)
            {
                AvailabilityOverride = _ => Task.FromResult(true), NativeCommand = Run
            };
        }
        private async Task<CommandResult> Run(string script, CancellationToken token)
        {
            if (script.Contains("$basarisiz"))
            {
                Publications++;
                if (BeforePublication is { } action) { BeforePublication = null; await action(); }
                if (FailPublication) return new CommandResult { ExitCode = 22, StandardOutput = "ADAPANEL-FAIL: access denied" };
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var result = await ProcessRunner.RunPowerShellAsync("$taskNativePath='" + NativePath.Replace("'", "''") + "'\n" + FakeDns + "\n" + script, linked.Token);
            if (!result.Success && script.Contains("ADAPANEL-LIVE-RECORDS"))
                throw new Xunit.Sdk.XunitException(result.StandardError + result.StandardOutput);
            return result;
        }
        public void Dispose() { deadline.Dispose(); Directory.Delete(Root, true); }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private const string FakeDns = """
        function Read-Rows { $rows = Get-Content -LiteralPath $taskNativePath -Raw | ConvertFrom-Json; foreach($entry in $rows){ $entry } }
        function Save-Rows($rows) { ConvertTo-Json -InputObject @($rows) -Depth 8 | Set-Content -LiteralPath $taskNativePath -Encoding UTF8 }
        function Get-DnsServerZone { param($Name) [pscustomobject]@{ZoneName='example.test';ZoneType='Primary'} }
        function Get-DnsServerResourceRecord {
          param($ZoneName,$Name,$RRType)
          foreach($r in @(Read-Rows)) {
            if($Name -and $r.Host -ne $Name){continue}; if($RRType -and $r.Type -ne $RRType){continue}
            $d = switch($r.Type) {
              'A' { @{IPv4Address=$r.Value} }
              'TXT' { @{DescriptiveText=@($r.Value)} }
              'CNAME' { @{HostNameAlias=$r.Value} }
              'NS' { @{NameServer=$r.Value} }
              'MX' { @{MailExchange=$r.Value;Preference=$r.Priority} }
              'SRV' { $v=$r.Value.Split(' '); @{Weight=[int]$v[0];Port=[int]$v[1];DomainName=$v[2];Priority=$r.Priority} }
              'CAA' { $v=$r.Value.Split(' ',3); $bytes=[byte[]]@([byte]$v[0],[byte]$v[1].Length)+[Text.Encoding]::ASCII.GetBytes($v[1])+[Text.Encoding]::UTF8.GetBytes($v[2]); @{Type=257;Data=$bytes} }
            }
            [pscustomobject]@{RecordType=if($r.Type -eq 'CAA'){'Unknown'}else{$r.Type};HostName=$r.Host;RecordData=[pscustomobject]$d;TimeToLive=[timespan]::FromSeconds($r.Ttl)}
          }
        }
        function Add-Row($type,$name,$value,$ttl,$priority) {
          $rows=@(Read-Rows)
          if(@($rows|Where-Object{$_.Type -eq $type -and $_.Host -eq $name -and $_.Value -eq $value}).Count){throw 'duplicate'}
          $rows += [pscustomobject]@{Type=$type;Host=$name;Value=$value;Ttl=[int]$ttl.TotalSeconds;Priority=$priority}
          Save-Rows $rows
        }
        function Add-DnsServerResourceRecordA { param($ZoneName,$Name,$IPv4Address,$TimeToLive) Add-Row 'A' $Name $IPv4Address $TimeToLive $null }
        function Add-DnsServerResourceRecordCName { param($ZoneName,$Name,$HostNameAlias,$TimeToLive) Add-Row 'CNAME' $Name $HostNameAlias $TimeToLive $null }
        function Add-DnsServerResourceRecordMX { param($ZoneName,$Name,$MailExchange,$Preference,$TimeToLive) Add-Row 'MX' $Name $MailExchange $TimeToLive $Preference }
        function Add-DnsServerResourceRecord { param($ZoneName,$Name,[switch]$Txt,[switch]$NS,[switch]$Srv,$DescriptiveText,$NameServer,$DomainName,$Weight,$Port,$Priority,$TimeToLive)
          if($Txt){Add-Row 'TXT' $Name ($DescriptiveText -join '') $TimeToLive $null}
          elseif($NS){Add-Row 'NS' $Name $NameServer $TimeToLive $null}
          elseif($Srv){Add-Row 'SRV' $Name "$Weight $Port $DomainName" $TimeToLive $Priority}
        }
        function Remove-DnsServerResourceRecord { param($ZoneName,[Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Force) process {
          $a=Convert-AdaDnsRecord $InputObject
          Save-Rows @(Read-Rows|Where-Object{!($_.Type -eq $a.Type -and $_.Host -eq $a.Host -and $_.Value -eq $a.Value -and $_.Priority -eq $a.Priority -and $_.Ttl -eq $a.Ttl)})
        } }
        """;
}
