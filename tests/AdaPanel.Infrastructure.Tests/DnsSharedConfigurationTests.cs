using System.Reflection;
using System.Text.RegularExpressions;
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

public sealed class DnsSharedConfigurationTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("record")]
    [InlineData("publish")]
    [InlineData("upgrade")]
    public async Task Peer_activity_does_not_undo_an_explicit_secondary_configuration(string operation)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.Service().SetZoneModeAsync(Fixture.Main, Secondary("192.0.2.53"), fixture.Token);
        Assert.Equal("Secondary", fixture.Native.At(Fixture.Zone).Mode);
        if (operation == "upgrade") await fixture.EditAsync(states =>
            Assert.Single(states, state => state.SiteName == Fixture.Peer).DefaultsVersion = 0);

        if (operation == "record")
        {
            var error = await Record.ExceptionAsync(() => fixture.Service().UpsertRecordAsync(Fixture.Peer, NewRecord(), fixture.Token));
            Assert.Equal("Secondary", fixture.Native.At(Fixture.Zone).Mode);
            Assert.Equal("dns.zone_is_secondary", Assert.IsType<AppException>(error).Message);
        }
        else if (operation == "publish") await fixture.Service().EnsureZonePublishedAsync(Fixture.Peer, fixture.Token);
        else await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);

        Assert.Equal("Secondary", fixture.Native.At(Fixture.Zone).Mode);
        Assert.Equal(new[] { "192.0.2.53" }, fixture.Native.At(Fixture.Zone).Masters);
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.True(peer.IsSecondary);
        Assert.Equal(new[] { "192.0.2.53" }, peer.MasterServers);
        Assert.DoesNotContain(peer.Records, record => record.Id == "main-record");
    }

    [Fact]
    public async Task Peer_publication_does_not_restore_an_old_master_list()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(secondary: true);
        await fixture.Service().SetZoneModeAsync(Fixture.Main, Secondary("198.51.100.53"), fixture.Token);
        await fixture.Service().EnsureZonePublishedAsync(Fixture.Peer, fixture.Token);
        Assert.Equal(new[] { "198.51.100.53" }, fixture.Native.At(Fixture.Zone).Masters);
        Assert.Equal(new[] { "198.51.100.53" }, (await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token)).MasterServers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_transfer_policy_is_shared_and_record_publication_cannot_restore_the_old_acl(bool publish)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.Service().UpdateZoneAsync(Fixture.Main,
            new UpdateDnsZoneRequest { ZoneName = Fixture.Zone, ZoneTransferIps = ["198.51.100.8"] }, fixture.Token);
        if (publish) await fixture.Service().UpsertRecordAsync(Fixture.Peer, NewRecord(), fixture.Token);
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.Equal(new[] { "198.51.100.8" }, fixture.Native.At(Fixture.Zone).Transfer);
        Assert.Equal(new[] { "198.51.100.8" }, peer.ZoneTransferIps);
        Assert.Equal(new[] { "198.51.100.8" }, peer.EffectiveNotifyServers);
        Assert.DoesNotContain(peer.Records, record => record.Id == "main-record");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_alias_inherits_the_existing_native_zones_configuration(bool secondary)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includePeer: false, secondary: secondary);
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.Equal(secondary ? "Secondary" : "Primary", fixture.Native.At(Fixture.Zone).Mode);
        Assert.Equal(secondary, peer.IsSecondary);
        Assert.Equal(new[] { "198.51.100.7" }, peer.ZoneTransferIps);
        if (secondary) Assert.Equal(new[] { "192.0.2.53" }, peer.MasterServers);
        else Assert.Equal(new[] { "198.51.100.7" }, fixture.Native.At(Fixture.Zone).Transfer);
        Assert.DoesNotContain(peer.Records, record => record.Id == "main-record");
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("masters")]
    [InlineData("transfer")]
    public async Task Legacy_disagreement_is_visible_without_overwriting_native_policy_or_hiding_private_records(string conflict)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(secondary: conflict == "masters");
        await fixture.IntroduceConflictAsync(conflict);
        var before = fixture.Native.At(Fixture.Zone).Copy();
        var zone = await fixture.Service().GetZoneAsync(Fixture.Main, fixture.Token);
        Assert.Equal("dns.shared_configuration_conflict", zone.StatusMessage);
        Assert.True(zone.IsSharedZone);
        Assert.Equal(conflict is "mode" or "masters", zone.ModeConfigurationConflict);
        Assert.Equal(conflict == "transfer", zone.TransferConfigurationConflict);
        Assert.Equal("main-record", Assert.Single(zone.Records).Id);
        Assert.Equal(0, fixture.Native.Writes);
        AssertPolicy(before, fixture.Native.At(Fixture.Zone));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("transfer")]
    public async Task Record_publication_cannot_choose_one_side_of_a_legacy_shared_configuration_conflict(string conflict)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.IntroduceConflictAsync(conflict);
        var before = fixture.Native.At(Fixture.Zone).Copy();
        var error = await Record.ExceptionAsync(() => fixture.Service().UpsertRecordAsync(Fixture.Main, NewRecord(), fixture.Token));
        Assert.Equal(0, fixture.Native.Writes);
        Assert.Equal("dns.shared_configuration_conflict", Assert.IsType<AppException>(error).Message);
        AssertPolicy(before, fixture.Native.At(Fixture.Zone));
    }

    [Fact]
    public async Task Failed_native_mode_change_keeps_one_shared_pending_intent_and_reports_the_native_mismatch()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.FailNextWrite = true;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().SetZoneModeAsync(
            Fixture.Main, Secondary("192.0.2.53"), fixture.Token));
        Assert.Equal("dns.secondary_apply_failed", error.Message);
        Assert.Equal("Primary", fixture.Native.At(Fixture.Zone).Mode);
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.True(peer.IsSecondary);
        Assert.Equal(new[] { "192.0.2.53" }, peer.MasterServers);
        Assert.Equal("dns.zone_type_mismatch", peer.StatusMessage);
        Assert.All(await fixture.StatesAsync(), state => Assert.True(state.IsSecondary));
    }

    [Fact]
    public async Task Panel_only_mode_changes_are_shared_without_claiming_native_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Available = false;
        await fixture.Service().SetZoneModeAsync(Fixture.Main, Secondary("192.0.2.53"), fixture.Token);
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.True(peer.IsSecondary);
        Assert.Equal(new[] { "192.0.2.53" }, peer.MasterServers);
        Assert.False(peer.IsWindowsDnsAvailable);
        Assert.Equal("dns.windows_dns_unavailable", peer.StatusMessage);
        Assert.Equal(0, fixture.Native.Writes);
    }

    [Fact]
    public async Task Canceled_configuration_request_does_not_change_any_reference_or_native_policy()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var before = await fixture.StateBytesAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().SetZoneModeAsync(
            Fixture.Main, Secondary("192.0.2.53"), canceled.Token));
        Assert.Equal(before, await fixture.StateBytesAsync());
        Assert.Equal(0, fixture.Native.Writes);
    }

    [Fact]
    public async Task A_different_native_zone_keeps_its_own_configuration()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(peerZone: "unrelated.test");
        await fixture.Service().SetZoneModeAsync(Fixture.Main, Secondary("192.0.2.53"), fixture.Token);
        await fixture.Service().UpsertRecordAsync(Fixture.Peer, NewRecord(), fixture.Token);
        Assert.Equal("Secondary", fixture.Native.At(Fixture.Zone).Mode);
        Assert.Equal("Primary", fixture.Native.At("unrelated.test").Mode);
        Assert.False((await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token)).IsSecondary);
    }

    [Fact]
    public async Task Inactive_saved_master_lists_do_not_block_primary_record_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.EditAsync(states => { states[0].MasterServers = ["192.0.2.53"]; states[1].MasterServers = ["192.0.2.54"]; });
        var result = await fixture.Service().UpsertRecordAsync(Fixture.Main, NewRecord(), fixture.Token);
        Assert.False(result.IsSecondary);
        Assert.NotEqual("dns.shared_configuration_conflict", result.StatusMessage);
        Assert.Equal("Primary", fixture.Native.At(Fixture.Zone).Mode);
        Assert.Contains(result.Records, record => record.Host == "new-record");
    }

    [Fact]
    public async Task Equivalent_secondary_ip_sets_with_different_order_and_ipv6_spelling_are_not_conflicts()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(secondary: true);
        await fixture.EditAsync(states =>
        {
            states[0].MasterServers = ["192.0.2.53", "2001:db8::53"];
            states[1].MasterServers = ["2001:0db8:0:0:0:0:0:53", "192.0.2.53"];
            states[0].ZoneTransferIps = ["198.51.100.7", "2001:db8::7"];
            states[1].ZoneTransferIps = ["2001:0db8:0:0:0:0:0:7", "198.51.100.7"];
        });
        var result = await fixture.Service().GetZoneAsync(Fixture.Main, fixture.Token);
        Assert.True(result.IsSecondary);
        Assert.NotEqual("dns.shared_configuration_conflict", result.StatusMessage);
        Assert.Equal(0, fixture.Native.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_changes_resolve_only_the_selected_axis_until_the_shared_policy_is_complete(bool modeFirst)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.IntroduceConflictAsync("mode");
        await fixture.IntroduceConflictAsync("transfer");
        var original = await fixture.StatesAsync();
        var service = fixture.Service();
        Task<SiteDnsZoneDto> ChangeMode() => service.SetZoneModeAsync(Fixture.Main, Secondary("203.0.113.53"), fixture.Token);
        Task<SiteDnsZoneDto> ChangeTransfer() => service.UpdateZoneAsync(Fixture.Main,
            new UpdateDnsZoneRequest { ZoneName = Fixture.Zone, ZoneTransferIps = ["203.0.113.7"] }, fixture.Token);

        var partial = modeFirst ? await ChangeMode() : await ChangeTransfer();
        Assert.Equal("dns.shared_configuration_conflict", partial.StatusMessage);
        Assert.Equal(!modeFirst, partial.ModeConfigurationConflict);
        Assert.Equal(modeFirst, partial.TransferConfigurationConflict);
        Assert.Equal(0, fixture.Native.Writes);
        var pending = await fixture.StatesAsync();
        foreach (var state in pending)
        {
            var previous = Assert.Single(original, item => item.SiteName == state.SiteName);
            if (modeFirst)
            {
                Assert.True(state.IsSecondary);
                Assert.Equal(new[] { "203.0.113.53" }, state.MasterServers);
                Assert.Equal(previous.ZoneTransferIps, state.ZoneTransferIps);
            }
            else
            {
                Assert.Equal(previous.IsSecondary, state.IsSecondary);
                Assert.Equal(previous.MasterServers, state.MasterServers);
                Assert.Equal(new[] { "203.0.113.7" }, state.ZoneTransferIps);
            }
        }

        var complete = modeFirst ? await ChangeTransfer() : await ChangeMode();
        Assert.False(complete.ModeConfigurationConflict);
        Assert.False(complete.TransferConfigurationConflict);
        Assert.True(complete.IsSharedZone);
        Assert.Equal("Secondary", fixture.Native.At(Fixture.Zone).Mode);
        Assert.Equal(new[] { "203.0.113.53" }, fixture.Native.At(Fixture.Zone).Masters);
        Assert.All(await fixture.StatesAsync(), state => Assert.Equal(new[] { "203.0.113.7" }, state.ZoneTransferIps));
        var peer = await service.GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.Equal("peer-record", Assert.Single(peer.Records).Id);
        var json = System.Text.Encoding.UTF8.GetString(await fixture.StateBytesAsync());
        Assert.DoesNotContain("ConfigurationConflict", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isSharedZone", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renaming_into_an_existing_zone_inherits_its_policy_and_preserves_its_private_records(bool secondary)
    {
        using var fixture = new Fixture();
        const string destination = "destination.test";
        await fixture.SeedAsync(peerZone: destination);
        await fixture.EditAsync(states =>
        {
            var peer = Assert.Single(states, item => item.SiteName == Fixture.Peer);
            peer.IsSecondary = secondary;
            peer.MasterServers = ["203.0.113.53"];
            peer.ZoneTransferIps = ["203.0.113.7"];
        });
        fixture.Native.Seed(destination, secondary, ["203.0.113.53"], ["203.0.113.7"]);
        var result = await fixture.Service().UpdateZoneAsync(Fixture.Main,
            new UpdateDnsZoneRequest { ZoneName = destination, ZoneTransferIps = ["203.0.113.7"] }, fixture.Token);
        Assert.Equal(destination, result.ZoneName);
        Assert.True(result.IsSharedZone);
        Assert.Equal(secondary, result.IsSecondary);
        Assert.Equal(secondary ? "Secondary" : "Primary", fixture.Native.At(destination).Mode);
        if (secondary) Assert.Equal(new[] { "203.0.113.53" }, result.MasterServers);
        Assert.False(fixture.Native.Contains(Fixture.Zone));
        var peer = await fixture.Service().GetZoneAsync(Fixture.Peer, fixture.Token);
        Assert.Equal("peer-record", Assert.Single(peer.Records).Id);
        Assert.Equal(new[] { "203.0.113.7" }, peer.ZoneTransferIps);
    }

    [Fact]
    public async Task Joining_a_zone_with_a_different_requested_acl_is_rejected_without_replacing_its_policy()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(peerZone: "destination.test");
        var before = await fixture.StateBytesAsync();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().UpdateZoneAsync(Fixture.Main,
            new UpdateDnsZoneRequest { ZoneName = "destination.test", ZoneTransferIps = ["203.0.113.7"] }, fixture.Token));
        Assert.Equal("dns.shared_configuration_conflict", error.Message);
        Assert.Equal(0, fixture.Native.Writes);
        Assert.Equal(before, await fixture.StateBytesAsync());
        Assert.Equal(new[] { "198.51.100.7" }, fixture.Native.At("destination.test").Transfer);
        Assert.True(fixture.Native.Contains(Fixture.Zone));
    }

    private static SetDnsZoneModeRequest Secondary(string master) => new() { Secondary = true, MasterServers = [master] };
    private static UpsertDnsRecordRequest NewRecord() => new() { Host = "new-record", Value = "192.0.2.44" };
    private static void AssertPolicy(NativeZone expected, NativeZone actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Masters, actual.Masters);
        Assert.Equal(expected.Transfer, actual.Transfer);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Main = "primary-app";
        internal const string Peer = "alias-app";
        internal const string Zone = "example.test";
        private const string StatePath = "config/dns-zones.json";
        private const string Prefix = "adapanel-dns-policy-";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal CancellationToken Token => deadline.Token;
        internal bool Available { get; set; } = true;
        internal NativeDns Native { get; } = new();
        internal JsonFileStore Store { get; }
        internal Fixture() => Store = NewStore();
        private JsonFileStore NewStore() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        internal async Task SeedAsync(bool secondary = false, bool includePeer = true, string peerZone = Zone)
        {
            var states = new List<SavedZone> { State(Main, Zone, "main", secondary) };
            if (includePeer) states.Add(State(Peer, peerZone, "peer", secondary));
            await Store.WriteAsync(StatePath, states.ToArray(), Token);
            foreach (var state in states) Native.Seed(state.ZoneName, state.IsSecondary, state.MasterServers, state.ZoneTransferIps);
        }
        private static SavedZone State(string site, string zone, string prefix, bool secondary) => new()
        {
            SiteName = site, ZoneName = zone, NameServers = ["ns1." + zone, "ns2." + zone],
            IsSecondary = secondary, MasterServers = secondary ? ["192.0.2.53"] : [], ZoneTransferIps = ["198.51.100.7"],
            Records = [new SavedRecord { Id = prefix + "-record", Host = prefix, Value = "192.0.2.10" }],
            PublishedGroups = ["A|" + prefix]
        };
        internal async Task EditAsync(Action<SavedZone[]> edit)
        {
            var states = await StatesAsync();
            edit(states);
            await Store.WriteAsync(StatePath, states, Token);
        }
        internal Task IntroduceConflictAsync(string conflict) => EditAsync(states =>
        {
            if (conflict == "mode") { states[1].IsSecondary = true; states[1].MasterServers = ["192.0.2.53"]; }
            else if (conflict == "masters") states[1].MasterServers = ["192.0.2.54"];
            else states[1].ZoneTransferIps = ["198.51.100.8"];
        });
        internal Task<SavedZone[]> StatesAsync() => Store.ReadAsync(StatePath, Array.Empty<SavedZone>(), Token);
        internal Task<byte[]> StateBytesAsync() => File.ReadAllBytesAsync(Store.ResolvePath(StatePath), Token);
        internal DnsService Service()
        {
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                Assert.Equal(nameof(IIisSiteService.GetSiteAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                var name = (string)args[0]!;
                Assert.Contains(name, new[] { Main, Peer });
                return Task.FromResult(new SiteDto { Id = name == Main ? 1 : 2, Name = name,
                    Bindings = [new SiteBindingDto { HostName = name == Main ? Zone : "www." + Zone, IpAddress = "192.0.2.10" }] });
            });
            var preferences = Stub<IPanelPreferencesService>((method, args) =>
            {
                Assert.Equal(nameof(IPanelPreferencesService.GetAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                return Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" });
            });
            return new DnsService(sites, preferences, NewStore(), NullLogger<DnsService>.Instance)
            {
                NativeCommand = Native.RunAsync,
                AvailabilityOverride = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Available); }
            };
        }
        public void Dispose()
        {
            deadline.Cancel(); deadline.Dispose();
            if (!Directory.Exists(root)) return;
            var full = Path.GetFullPath(root);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup escaped the owned DNS configuration fixture.");
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out var path))
            {
                if (path != full && !Path.GetFullPath(path).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("DNS fixture cleanup escaped its root.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("DNS fixture cleanup must not follow links.");
                if (attributes.HasFlag(FileAttributes.Directory))
                    foreach (var child in Directory.GetFileSystemEntries(path)) pending.Push(child);
            }
            Directory.Delete(full, recursive: true);
        }
    }

    private sealed class NativeZone
    {
        internal string Mode { get; set; } = "Primary";
        internal string[] Masters { get; set; } = [];
        internal string[] Transfer { get; set; } = [];
        internal NativeZone Copy() => new() { Mode = Mode, Masters = [.. Masters], Transfer = [.. Transfer] };
    }
    private sealed class NativeDns
    {
        private readonly Dictionary<string, NativeZone> zones = new(StringComparer.OrdinalIgnoreCase);
        internal int Writes { get; private set; }
        internal bool FailNextWrite { get; set; }
        internal NativeZone At(string zone) => zones[zone.TrimEnd('.')];
        internal bool Contains(string zone) => zones.ContainsKey(zone.TrimEnd('.'));
        internal void Seed(string zone, bool secondary, string[] masters, string[] transfer)
            => zones[zone.TrimEnd('.')] = new NativeZone { Mode = secondary ? "Secondary" : "Primary", Masters = [.. masters], Transfer = [.. transfer] };
        internal Task<CommandResult> RunAsync(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.Contains("Remove-DnsServerZone -Name $zoneName -Force", StringComparison.Ordinal))
            {
                Writes++;
                zones.Remove(Capture(script, @"\$zoneName = '([^']+)'"));
                return Task.FromResult(new CommandResult());
            }
            if (script.Contains("$basarisiz = @()", StringComparison.Ordinal) || script.Contains("$masters = @(", StringComparison.Ordinal))
            {
                Writes++;
                if (FailNextWrite) { FailNextWrite = false; return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Controlled DNS policy failure." }); }
                var zoneName = Capture(script, @"\$zoneName = '([^']+)'");
                if (!zones.TryGetValue(zoneName, out var zone)) zones.Add(zoneName, zone = new NativeZone());
                if (script.Contains("$masters = @(", StringComparison.Ordinal))
                {
                    zone.Mode = "Secondary";
                    zone.Masters = Quoted(Capture(script, @"\$masters = @\(([^)]*)\)"));
                }
                else
                {
                    if (script.Contains("ConvertTo-DnsServerPrimaryZone", StringComparison.Ordinal)) zone.Mode = "Primary";
                    if (script.Contains("-SecureSecondaries NoTransfer", StringComparison.Ordinal)) zone.Transfer = [];
                    else if (script.Contains("-SecondaryServers ", StringComparison.Ordinal))
                        zone.Transfer = Quoted(Capture(script, @"-SecondaryServers (.*?) -Notify "));
                }
                return Task.FromResult(new CommandResult());
            }
            if (script.Contains("[string]$z.ZoneType", StringComparison.Ordinal))
            {
                var name = Capture(script, @"Get-DnsServerZone -Name '([^']+)'");
                return Task.FromResult(new CommandResult { StandardOutput = zones.TryGetValue(name, out var zone) ? zone.Mode : "" });
            }
            if (script.Contains("'SIGNED='", StringComparison.Ordinal)) return Task.FromResult(new CommandResult { StandardOutput = "SIGNED=False" });
            throw new InvalidOperationException("Unexpected DNS command; no native command runs in this fixture.");
        }
        private static string[] Quoted(string value) => Regex.Matches(value, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        private static string Capture(string value, string pattern)
        {
            var match = Regex.Match(value, pattern);
            Assert.True(match.Success, "The generated DNS command must carry the expected explicit policy.");
            return match.Groups[1].Value;
        }
    }
    public sealed class SavedZone
    {
        public string SiteName { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string[] NameServers { get; set; } = [];
        public bool IsSecondary { get; set; }
        public string[] MasterServers { get; set; } = [];
        public string[] ZoneTransferIps { get; set; } = [];
        public int DefaultsVersion { get; set; } = DnsService.CurrentDefaultsVersion;
        public SavedRecord[] Records { get; set; } = [];
        public string[] PublishedGroups { get; set; } = [];
    }
    public sealed class SavedRecord
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "A";
        public string Host { get; set; } = "";
        public string Value { get; set; } = "";
        public int Ttl { get; set; } = 3600;
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
