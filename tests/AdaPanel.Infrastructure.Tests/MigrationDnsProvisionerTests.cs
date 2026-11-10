using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationDnsProvisionerTests
{
    [Fact]
    public async Task Native_fresh_create_add_and_full_values_readback_precede_new_cache_reference_without_default_DNS_or_mail_calls()
    {
        using var f = new Fixture(); var before = f.Files();
        var plan = await f.Service.InspectAsync("source.test", default);
        Assert.True(plan.Measured); Assert.False(plan.Exists); Assert.Equal(before, f.Files()); Assert.Empty(f.Commands);
        var result = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.True(result.Verified); Assert.True(result.ZoneCreated); Assert.False(result.PartialStatePossible);
        Assert.Equal(1, f.Creates); Assert.Equal(1, f.Adds); Assert.Equal(2, f.ValueReads);
        Assert.True(f.InputProtected); Assert.All(f.PayloadFiles, path => Assert.False(File.Exists(path)));
        Assert.Contains("source.test. 120 IN MX 25 mx.external.test.", f.TextRecords);
        Assert.Contains("www.source.test. 123 IN A 203.0.113.20", f.TextRecords);
        Assert.DoesNotContain("Keep source TXT", string.Join('\n', f.Commands));
        Assert.All(f.Commands, script => { Assert.DoesNotContain("Remove-Dns", script); Assert.DoesNotContain("Set-Dns", script); Assert.DoesNotContain("-LoadExisting", script); Assert.DoesNotContain("-Force", script); Assert.DoesNotContain("ZoneReload", script); });
        Assert.True(File.Exists(f.FilesStore.ResolvePath("config/dns-zones.json")));
        var replay = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.False(replay.Verified); Assert.False(replay.Attempted); Assert.Equal(1, f.Creates);
    }
    [Theory]
    [InlineData("zone")]
    [InlineData("configuration")]
    public async Task Existing_native_zone_or_panel_reference_is_never_adopted_or_overwritten(string existing)
    {
        using var f = new Fixture();
        if (existing == "zone") f.Exists = true;
        else await f.FilesStore.WriteAsync("config/dns-zones.json", new[] { new { SiteName = "old-site", ZoneName = "source.test", Records = new[] { new { Type = "TXT", Host = "@", Value = "PRESERVE" } } } });
        var before = f.Files(); var result = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.False(result.Verified); Assert.False(result.Attempted); Assert.Empty(f.Commands); Assert.Equal(before, f.Files());
    }
    [Theory]
    [InlineData("ack-loss")]
    [InlineData("race")]
    public async Task Native_CREATE_is_final_freshness_boundary_and_lost_ack_never_replays_or_adds_records(string fault)
    {
        using var f = new Fixture { Fault = fault };
        var result = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.False(result.Verified); Assert.True(result.Attempted); Assert.True(result.PartialStatePossible); Assert.False(result.ZoneCreated);
        Assert.Equal(1, f.Creates); Assert.Equal(0, f.Adds);
        Assert.False((await f.Service.ImportFreshAsync(f.Request, default)).Verified); Assert.Equal(1, f.Creates);
        Assert.False(File.Exists(f.FilesStore.ResolvePath("config/dns-zones.json")));
    }
    [Theory]
    [InlineData("value")]
    [InlineData("ttl")]
    [InlineData("priority")]
    [InlineData("zone-file")]
    [InlineData("unsupported")]
    [InlineData("add-failure")]
    [InlineData("cancel")]
    public async Task Keys_or_cache_are_not_native_success_proof_and_partial_resources_survive_failed_readback(string fault)
    {
        using var f = new Fixture { Fault = fault }; using var cancellation = new CancellationTokenSource();
        if (fault == "cancel") f.OnAdd = cancellation.Cancel;
        var result = await f.Service.ImportFreshAsync(f.Request, cancellation.Token);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.True(result.ZoneCreated);
        Assert.Contains("dns-zone:source.test", result.CreatedResources); Assert.True(f.Exists);
        Assert.False(File.Exists(f.FilesStore.ResolvePath("config/dns-zones.json")));
        Assert.All(f.PayloadFiles, path => Assert.False(File.Exists(path)));
        Assert.DoesNotContain("NATIVE-PRIVATE-FAILURE", result.ErrorCode + string.Join(',', result.Evidence));
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("path")]
    [InlineData("binding")]
    public async Task Service_rechecks_actual_site_identity_under_DNS_leases_before_native_CREATE(string fault)
    {
        using var f = new Fixture(); f.SiteState.Current = new() { Id = fault == "identity" ? 2 : 1, Name = "target-site", ApplicationRootPath = fault == "path" ? f.Root : f.Request.ApplicationRootPath, Bindings = [new() { HostName = fault == "binding" ? "other.test" : "source.test" }] };
        Assert.False((await f.Service.ImportFreshAsync(f.Request, default)).Verified); Assert.Empty(f.Commands);
    }
    [Fact]
    public async Task Unavailable_or_unreadable_native_inventory_never_becomes_a_missing_fresh_zone()
    {
        using var absent = new Fixture { Available = false };
        var snapshot = await absent.Service.InspectAsync("source.test", default);
        Assert.True(snapshot.Measured); Assert.False(snapshot.Available);
        Assert.False((await absent.Service.ImportFreshAsync(absent.Request, default)).Attempted); Assert.Empty(absent.Commands);
        using var unreadable = new Fixture { Fault = "inventory" };
        var error = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => unreadable.Service.InspectAsync("source.test", default));
        Assert.Equal("migration.dns_native_inventory_unmeasured", error.MessageKey); Assert.Empty(error.Details);
        Assert.False((await unreadable.Service.ImportFreshAsync(unreadable.Request, default)).Attempted); Assert.Empty(unreadable.Commands);
    }
    [Fact]
    public async Task Saved_cache_cannot_hide_failed_final_native_readback()
    {
        using var f = new Fixture { Fault = "post-cache" };
        var result = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible);
        Assert.True(File.Exists(f.FilesStore.ResolvePath("config/dns-zones.json")));
        Assert.Contains("dns-reference:target-site", result.CreatedResources);
        Assert.Equal("migration.dns_native_values_unverified", result.ErrorCode);
    }
    [Fact]
    public async Task Private_payload_cleanup_failure_is_explicit_and_does_not_hide_created_native_resources()
    {
        using var f = new Fixture { Fault = "cleanup" };
        var result = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible); Assert.True(result.ZoneCreated);
        Assert.Equal("migration.dns_private_payload_cleanup_failed", result.ErrorCode);
        Assert.Contains("dns-zone:source.test", result.CreatedResources);
    }
    [Theory]
    [InlineData("  Keep source TXT \"quotes\" \\ and  spaces  ")]
    [InlineData("")]
    [InlineData("é Türkçe 😀")]
    [InlineData("$(throw 'INJECTED') ` backtick; \"quotes\"")]
    public async Task Real_local_PowerShell_complete_TXT_parser_roundtrips_octets_whitespace_quotes_and_empty_values(string value)
    {
        var record = new MigrationDnsRecord { Type = "TXT", Name = "@", Value = value, Ttl = 0 };
        var wire = MigrationDnsRecords.TextRepresentation(record, "source.test");
        var function = (string)typeof(DnsService).GetField("MigrationTxtFunctions", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var script = function + "\n$text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(wire)) + "'))\n" +
            "$r=[pscustomobject]@{OwnerName='source.test.';TTL=0;TextRepresentation=$text};Convert-AdaNativeTxt $r 'source.test' | ConvertTo-Json -Compress";
        var result = await ProcessRunner.RunPowerShellAsync(script);
        Assert.True(result.Success, result.StandardError); using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(value, json.RootElement.GetProperty("Value").GetString()); Assert.Equal(0, json.RootElement.GetProperty("Ttl").GetInt32());
        // No Windows DNS commands are invoked by this test, only the pure RDATA parser.
    }
    [Fact]
    public async Task Long_TXT_chunking_roundtrips_complete_value_without_zone_reload_or_retries()
    {
        var value = new string('x', 1000); var wire = MigrationDnsRecords.TextRepresentation(new() { Type = "TXT", Name = "@", Value = value, Ttl = 42 }, "source.test");
        Assert.Equal(8, wire.Count(character => character == '"')); Assert.DoesNotContain(value, wire);
        var function = (string)typeof(DnsService).GetField("MigrationTxtFunctions", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var script = function + "\n$r=[pscustomobject]@{OwnerName='source.test.';TTL=42;TextRepresentation='" + wire + "'};Convert-AdaNativeTxt $r 'source.test' | ConvertTo-Json -Compress";
        var result = await ProcessRunner.RunPowerShellAsync(script); Assert.True(result.Success, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput); Assert.Equal(value, json.RootElement.GetProperty("Value").GetString());
    }
    [Theory]
    [InlineData("long-txt")]
    [InlineData("control-txt")]
    [InlineData("control-caa")]
    [InlineData("srv-port-zero")]
    [InlineData("srv-null-target")]
    public async Task Direct_import_cannot_report_verified_then_break_the_normal_GetZone_reload(string fault)
    {
        var record = fault switch
        {
            "long-txt" => new MigrationDnsRecord { Type = "TXT", Name = "@", Value = new string('x', 736), Ttl = 3600 },
            "control-txt" => new MigrationDnsRecord { Type = "TXT", Name = "@", Value = "source\tvalue", Ttl = 3600 },
            "control-caa" => new MigrationDnsRecord { Type = "CAA", Name = "@", Value = "0 issue source\tvalue", Ttl = 3600 },
            "srv-port-zero" => new MigrationDnsRecord { Type = "SRV", Name = "_service._tcp", Value = "0 0 server.source.test", Ttl = 3600 },
            _ => new MigrationDnsRecord { Type = "SRV", Name = "_service._tcp", Value = "0 443 .", Ttl = 3600 }
        };
        using var f = new Fixture([record]); var before = f.Files();
        var result = await f.Service.ImportFreshAsync(f.Request, default);
        // On the old production source this really imports and persists, then the ordinary reader
        // throws dns.invalid_record_value/invalid_srv_value/invalid_zone. Do not substitute a reader fake.
        if (result.Verified) { f.Available = false; await f.Service.GetZoneAsync(f.Request.SiteName); }
        Assert.False(result.Verified); Assert.False(result.Attempted); Assert.Empty(f.Commands);
        Assert.Equal("migration.dns_management_format_unsupported", result.ErrorCode); Assert.Equal(before, f.Files());
        Assert.False(File.Exists(f.FilesStore.ResolvePath("config/dns-zones.json")));
    }

    [Theory]
    [InlineData("TXT", "", "@")]
    [InlineData("TXT", "  mixed Case \"quotes\"  ", "_verify")]
    [InlineData("CAA", "0 issue letsencrypt.org", "@")]
    [InlineData("PTR", "host.source.test", "one")]
    [InlineData("SRV", "0 443 service.source.test", "_service._tcp")]
    public async Task Compatible_native_import_remains_readable_without_RDATA_loss_through_normal_GetZone(string type, string value, string owner)
    {
        using var f = new Fixture([new() { Type = type, Name = owner, Value = value, Ttl = 3600 }]);
        var imported = await f.Service.ImportFreshAsync(f.Request, default);
        Assert.True(imported.Verified, imported.ErrorCode); f.Available = false;
        var read = await f.Service.GetZoneAsync(f.Request.SiteName);
        var record = Assert.Single(read.Records, record => record.Type == type);
        Assert.Equal(value, record.Value); Assert.Equal(3600, record.Ttl);
    }

    [Fact]
    public async Task TXT_management_limit_is_512_characters_not_512_UTF8_bytes()
    {
        var value = new string('ü', 512); Assert.True(Encoding.UTF8.GetByteCount(value) > 512);
        using var f = new Fixture([new() { Type = "TXT", Name = "_unicode", Value = value, Ttl = 3600 }]);
        var imported = await f.Service.ImportFreshAsync(f.Request, default); Assert.True(imported.Verified, imported.ErrorCode);
        f.Available = false; var read = await f.Service.GetZoneAsync(f.Request.SiteName);
        Assert.Equal(value, Assert.Single(read.Records, record => record.Type == "TXT").Value);
    }

    public class ManagementPreferences : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IPanelPreferencesService.GetAsync)
            ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "203.0.113.20" }) : throw new InvalidOperationException("No preference mutation allowed");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AdaPanel.NativeDns." + Guid.NewGuid().ToString("N"));
        internal readonly JsonFileStore FilesStore; internal readonly DnsService Service; internal readonly MigrationDnsSiteProxy SiteState;
        internal readonly MigrationDnsImportRequest Request; internal readonly List<string> Commands = [], PayloadFiles = [], TextRecords = [];
        internal string Fault = "", ZoneFile = ""; internal bool Exists, InputProtected, Available = true; internal int Creates, Adds, ValueReads; internal Action? OnAdd;
        private FileStream? heldPayload;
        internal Fixture(IReadOnlyList<MigrationDnsRecord>? managementRecords = null)
        {
            PortableMigrationStorage.CreatePrivateDirectory(Root); FilesStore = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var sites = DispatchProxy.Create<IIisSiteService, MigrationDnsSiteProxy>(); SiteState = (MigrationDnsSiteProxy)(object)sites;
            SiteState.Current = new() { Id = 1, Name = "target-site", PhysicalPath = Path.Combine(Root, "www"), ApplicationRootPath = Path.Combine(Root, "www"), Bindings = [new() { HostName = "source.test" }] };
            Request = new() { SiteName = "target-site", SiteId = 1, ApplicationRootPath = SiteState.Current.ApplicationRootPath, ZoneName = "source.test", Records = managementRecords ?? [
                new() { Type = "A", Name = "www", Value = "203.0.113.20", Ttl = 123 },
                new() { Type = "MX", Name = "@", Value = "mx.external.test", Ttl = 120, Priority = 25 },
                new() { Type = "TXT", Name = "@", Value = "  Keep source TXT \"quotes\"  ", Ttl = 3600 }
            ] };
            Service = new DnsService(sites, DispatchProxy.Create<IPanelPreferencesService, ManagementPreferences>(), FilesStore, NullLogger<DnsService>.Instance) { AvailabilityOverride = _ => Task.FromResult(Available), NativeCommand = Write, InventoryCommand = Read };
        }
        internal string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).Order(StringComparer.Ordinal).ToArray();
        private Task<CommandResult> Write(string script, CancellationToken token)
        {
            Commands.Add(script); var match = Regex.Match(script, "Get-Content -LiteralPath '(?<file>[^']+)' -Raw"); Assert.True(match.Success);
            var file = match.Groups["file"].Value; PayloadFiles.Add(file); InputProtected = new FileInfo(file).GetAccessControl().AreAccessRulesProtected;
            using var json = JsonDocument.Parse(File.ReadAllText(file)); ZoneFile = json.RootElement.GetProperty("ZoneFile").GetString()!;
            if (script.Contains("# ADAPANEL-MIGRATION-DNS-CREATE"))
            {
                Creates++; Exists = true;
                return Task.FromResult(Fault is "ack-loss" or "race" ? new CommandResult { ExitCode = 1, StandardError = "NATIVE-PRIVATE-FAILURE" } : Response(new { Created = true, ZoneFile }));
            }
            Assert.Contains("# ADAPANEL-MIGRATION-DNS-ADD", script); Assert.Contains("Assert-AdaMigrationZone", script); Assert.Contains("CreateInstanceFromTextRepresentation", script);
            Adds++; TextRecords.AddRange(json.RootElement.GetProperty("Records").EnumerateArray().Select(record => record.GetProperty("TextRepresentation").GetString()!));
            if (Fault == "cleanup") heldPayload = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            OnAdd?.Invoke(); token.ThrowIfCancellationRequested();
            return Task.FromResult(Fault == "add-failure" ? new CommandResult { ExitCode = 1, StandardError = "NATIVE-PRIVATE-FAILURE" } : Response(new { Added = Request.Records.Count }));
        }
        private Task<CommandResult> Read(string script, CancellationToken token)
        {
            Assert.Contains("# ADAPANEL-MIGRATION-DNS-INVENTORY", script);
            if (Fault == "inventory") return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "NATIVE-PRIVATE-FAILURE" });
            var values = script.Contains("$raw = @(Get-DnsServerResourceRecord"); if (values) ValueReads++;
            var records = Request.Records.Select(record => new MigrationDnsRecord { Type = record.Type, Name = record.Name, Value = (Fault == "value" || Fault == "post-cache" && ValueReads > 1) && record.Type == "TXT" ? record.Value.Trim() : record.Value, Ttl = record.Ttl + (Fault == "ttl" ? 1 : 0), Priority = record.Priority + (Fault == "priority" && record.Type == "MX" ? 1 : 0) }).Append(new() { Type = "NS", Name = "@", Value = "ns.target.test", Ttl = 3600 }).ToArray();
            return Task.FromResult(Response(new { Exists, ZoneType = Exists ? "Primary" : "", ZoneFile = Fault == "zone-file" ? "FOREIGN.dns" : ZoneFile, UnsupportedRecords = Fault == "unsupported" ? 1 : 0, Records = values ? records : [] }));
        }
        private static CommandResult Response(object value) => new() { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(value) };
        public void Dispose() { heldPayload?.Dispose(); foreach (var file in PayloadFiles.Distinct()) if (File.Exists(file)) File.Delete(file); PortableMigrationStorage.DeleteOwnedDirectory(Root); }
    }
}
