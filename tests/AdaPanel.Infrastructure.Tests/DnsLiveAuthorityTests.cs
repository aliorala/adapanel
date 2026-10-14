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
        internal async Task SeedCache()
        {
            var store = Store();
            await store.WriteAsync("config/dns-zones.json", new[] { new { siteName = "example.test", zoneName = "example.test", defaultsVersion = 4, nameServers = new[] { "ns1.example.test", "ns2.example.test" }, records = new[] { new { id = "old", type = "A", host = "@", value = "192.168.1.80", ttl = 3600, isManaged = true } } } });
        }
        internal async Task AddPeer()
        {
            var store = Store();
            var zones = await store.ReadAsync("config/dns-zones.json", Array.Empty<JsonElement>());
            await store.WriteAsync("config/dns-zones.json", zones.Append(JsonSerializer.SerializeToElement(new { siteName = "peer.test", zoneName = "example.test", records = Array.Empty<object>(), defaultsVersion = 4 })).ToArray());
        }
        private JsonFileStore Store() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
        internal DnsService Service()
        {
            var site = new SiteDto { Name = "example.test", Bindings = [new() { HostName = "example.test", IpAddress = "192.168.1.80" }] };
            var sites = Stub<IIisSiteService>((method, _) => method.Name == nameof(IIisSiteService.GetSitesAsync)
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([site]) : Task.FromResult(site));
            var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "198.51.100.8" }));
            return new DnsService(sites, preferences, Store(), NullLogger<DnsService>.Instance)
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
