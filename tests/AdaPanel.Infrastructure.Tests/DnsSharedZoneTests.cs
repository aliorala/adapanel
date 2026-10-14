using System.Collections.Concurrent;
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

public sealed class DnsSharedZoneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apex_and_www_bindings_can_use_the_same_default_zone(bool aliasFirst)
    {
        using var fixture = new Fixture();
        var firstSite = aliasFirst ? Fixture.AliasSite : Fixture.MainSite;
        var secondSite = aliasFirst ? Fixture.MainSite : Fixture.AliasSite;
        var first = await fixture.Service().GetZoneAsync(firstSite, fixture.Token);
        var second = await fixture.Service().GetZoneAsync(secondSite, fixture.Token);

        Assert.Equal(Fixture.Zone, first.ZoneName);
        Assert.Equal(first.ZoneName, second.ZoneName);
        Assert.Equal(firstSite, first.SiteName);
        Assert.Equal(secondSite, second.SiteName);
        // Reading a missing zone creates only metadata, never a native zone.
        Assert.False(first.IsWindowsZonePresent);
        Assert.False(second.IsWindowsZonePresent);
        Assert.Equal(2, (await fixture.StatesAsync()).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publishing_one_site_preserves_the_other_sites_value_in_the_shared_rrset(bool batch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var peerBefore = await fixture.StateAsync(Fixture.AliasSite);
        var request = new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" };
        var service = fixture.Service();
        var result = batch
            ? await service.UpsertRecordsAsync(Fixture.MainSite, [request], fixture.Token)
            : await service.UpsertRecordAsync(Fixture.MainSite, request, fixture.Token);

        Assert.Equal(new[] { "192.0.2.13", "192.0.2.22" }, fixture.Native.Values(Fixture.Zone, "shared"));
        Assert.Equal("192.0.2.31", Assert.Single(fixture.Native.Values(Fixture.Zone, "main-only")));
        Assert.Equal("192.0.2.32", Assert.Single(fixture.Native.Values(Fixture.Zone, "alias-only")));
        Assert.DoesNotContain(result.Records, record => record.Id == "alias-shared" || record.Host == "alias-only");
        var peerAfter = await fixture.StateAsync(Fixture.AliasSite);
        Assert.Equal(peerBefore.Records.Select(RecordIdentity), peerAfter.Records.Select(RecordIdentity));
    }

    [Fact]
    public async Task Deleting_a_record_removes_only_that_sites_contribution_to_the_shared_rrset()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var result = await fixture.Service().DeleteRecordAsync(Fixture.MainSite, "main-shared", fixture.Token);

        Assert.Equal("192.0.2.22", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.DoesNotContain(result.Records, record => record.Host == "shared");
        Assert.Contains((await fixture.StateAsync(Fixture.AliasSite)).Records, record => record.Id == "alias-shared");
    }

    [Theory]
    [InlineData(Fixture.MainSite, Fixture.AliasSite, "alias-only", "192.0.2.32")]
    [InlineData(Fixture.AliasSite, Fixture.MainSite, "main-only", "192.0.2.31")]
    public async Task Deleting_one_site_reference_preserves_the_remaining_sites_native_zone(
        string deleting, string surviving, string survivingHost, string survivingValue)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var survivor = await fixture.StateAsync(surviving);
        await fixture.Service().DeleteZoneAsync(deleting, fixture.Token);

        Assert.Equal(surviving, Assert.Single(await fixture.StatesAsync()).SiteName);
        Assert.Equal(survivor.Records.Select(RecordIdentity), (await fixture.StateAsync(surviving)).Records.Select(RecordIdentity));
        Assert.True(fixture.Native.HasZone(Fixture.Zone), "Another panel site still references this actual DNS zone.");
        Assert.Equal(survivingValue, Assert.Single(fixture.Native.Values(Fixture.Zone, survivingHost)));
        Assert.Equal(0, fixture.Native.ZoneDeletes);
        Assert.DoesNotContain(fixture.Native.SyncScripts, script => script.Contains("Set-DnsServerPrimaryZone", StringComparison.Ordinal) ||
            script.Contains("ConvertTo-DnsServerPrimaryZone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_removing_the_final_reference_deletes_the_native_zone()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await fixture.Service().DeleteZoneAsync(Fixture.MainSite, fixture.Token);
        await fixture.Service().DeleteZoneAsync(Fixture.AliasSite, fixture.Token);
        Assert.Empty(await fixture.StatesAsync());
        Assert.False(fixture.Native.HasZone(Fixture.Zone));
        Assert.Equal(1, fixture.Native.ZoneDeletes);
    }

    [Theory]
    [InlineData("EXAMPLE.TEST")]
    [InlineData("example.test.")]
    public async Task Historical_zone_spelling_variants_still_count_as_the_same_native_zone(string storedAliasZone)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(aliasZone: storedAliasZone);
        await fixture.Service().DeleteZoneAsync(Fixture.MainSite, fixture.Token);
        Assert.Equal(Fixture.AliasSite, Assert.Single(await fixture.StatesAsync()).SiteName);
        Assert.True(fixture.Native.HasZone(Fixture.Zone));
        Assert.Equal("192.0.2.22", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal(0, fixture.Native.ZoneDeletes);
    }

    [Fact]
    public async Task Concurrent_site_publications_keep_both_accepted_values_in_the_native_rrset()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" }, fixture.Token);
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.Service().UpsertRecordAsync(Fixture.AliasSite,
            new UpsertDnsRecordRequest { Id = "alias-shared", Host = "shared", Value = "192.0.2.24" }, fixture.Token);
        try { await Task.WhenAny(second, Task.Delay(750, fixture.Token)); }
        finally { fixture.Native.ReleaseFirstSync.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(fixture.Token);

        Assert.Equal("192.0.2.13", Assert.Single((await fixture.StateAsync(Fixture.MainSite)).Records, record => record.Host == "shared").Value);
        Assert.Equal("192.0.2.24", Assert.Single((await fixture.StateAsync(Fixture.AliasSite)).Records, record => record.Host == "shared").Value);
        Assert.Equal(new[] { "192.0.2.13", "192.0.2.24" }, fixture.Native.Values(Fixture.Zone, "shared"));
    }

    [Fact]
    public async Task Deleting_the_other_reference_during_publication_does_not_erase_the_shared_zone()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var publish = fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" }, fixture.Token);
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var deleting = fixture.Service().DeleteZoneAsync(Fixture.AliasSite, fixture.Token);
        try { await Task.WhenAny(deleting, Task.Delay(750, fixture.Token)); }
        finally { fixture.Native.ReleaseFirstSync.TrySetResult(); }
        await Task.WhenAll(publish, deleting).WaitAsync(fixture.Token);
        Assert.Equal(Fixture.MainSite, Assert.Single(await fixture.StatesAsync()).SiteName);
        Assert.True(fixture.Native.HasZone(Fixture.Zone));
        Assert.Equal("192.0.2.13", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal(0, fixture.Native.ZoneDeletes);
    }

    [Fact]
    public async Task A_single_site_zone_retains_its_existing_delete_behavior()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includeAlias: false);
        await fixture.Service().DeleteZoneAsync(Fixture.MainSite, fixture.Token);
        Assert.Empty(await fixture.StatesAsync());
        Assert.False(fixture.Native.HasZone(Fixture.Zone));
        Assert.Equal(1, fixture.Native.ZoneDeletes);
    }

    [Fact]
    public async Task An_unrelated_native_zone_is_not_included_in_shared_record_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(aliasZone: "unrelated.test");
        await fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" }, fixture.Token);
        Assert.Equal("192.0.2.13", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal("192.0.2.22", Assert.Single(fixture.Native.Values("unrelated.test", "shared")));
        Assert.Empty(fixture.Native.Values(Fixture.Zone, "alias-only"));
    }

    [Fact]
    public async Task A_canceled_delete_preserves_both_references_and_the_native_zone()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().DeleteZoneAsync(Fixture.MainSite, canceled.Token));
        Assert.Equal(2, (await fixture.StatesAsync()).Length);
        Assert.True(fixture.Native.HasZone(Fixture.Zone));
        Assert.Equal(0, fixture.Native.ZoneDeletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renaming_a_reference_publishes_the_new_zone_then_detaches_only_its_old_contribution(bool shared)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includeAlias: shared);
        var result = await fixture.Service().UpdateZoneAsync(Fixture.MainSite,
            new UpdateDnsZoneRequest { ZoneName = "new.test" }, fixture.Token);

        Assert.Equal("new.test", result.ZoneName);
        Assert.Equal("new.test", (await fixture.StateAsync(Fixture.MainSite)).ZoneName);
        Assert.Equal("192.0.2.11", Assert.Single(fixture.Native.Values("new.test", "shared")));
        Assert.Empty(fixture.Native.Values(Fixture.Zone, "main-only"));
        Assert.Equal(shared, fixture.Native.HasZone(Fixture.Zone));
        if (shared)
        {
            Assert.Equal("192.0.2.22", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
            Assert.Equal(Fixture.Zone, (await fixture.StateAsync(Fixture.AliasSite)).ZoneName);
        }
        Assert.StartsWith("publish:new.test", fixture.Native.Events.First());
        Assert.Equal(shared ? 0 : 1, fixture.Native.ZoneDeletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_new_zone_publication_keeps_the_working_old_zone_and_persisted_name(bool cancel)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includeAlias: false);
        var before = await fixture.StateAsync(Fixture.MainSite);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        fixture.Native.BeforeSync = zone => { if (zone == "new.test" && cancel) request.Cancel(); };
        fixture.Native.FailSyncZone = "new.test";

        var error = await Record.ExceptionAsync(() => fixture.Service().UpdateZoneAsync(Fixture.MainSite,
            new UpdateDnsZoneRequest { ZoneName = "new.test" }, request.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("dns.sync_failed", Assert.IsType<AppException>(error).Message);
        var after = await fixture.StateAsync(Fixture.MainSite);
        Assert.Equal(Fixture.Zone, after.ZoneName);
        Assert.Equal(before.Records.Select(RecordIdentity), after.Records.Select(RecordIdentity));
        Assert.Equal("192.0.2.11", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal("192.0.2.31", Assert.Single(fixture.Native.Values(Fixture.Zone, "main-only")));
        Assert.Equal(0, fixture.Native.ZoneDeletes);
    }

    [Fact]
    public async Task Panel_only_rename_saves_intent_without_claiming_native_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.DnsAvailable = false;
        var result = await fixture.Service().UpdateZoneAsync(Fixture.MainSite,
            new UpdateDnsZoneRequest { ZoneName = "new.test" }, fixture.Token);
        Assert.Equal("new.test", result.ZoneName);
        Assert.Equal("new.test", (await fixture.StateAsync(Fixture.MainSite)).ZoneName);
        Assert.False(result.IsWindowsDnsAvailable);
        Assert.False(result.IsWindowsZonePresent);
        Assert.Equal("dns.windows_dns_unavailable", result.StatusMessage);
        Assert.Empty(fixture.Native.Events);
        Assert.Empty((await fixture.StateAsync(Fixture.MainSite)).PublishedGroups);
        Assert.True(fixture.Native.HasZone(Fixture.Zone));
        Assert.False(fixture.Native.HasZone("new.test"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_native_detachment_keeps_the_site_reference_available_for_retry(bool shared)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includeAlias: shared);
        if (shared) fixture.Native.FailSyncZone = Fixture.Zone;
        else fixture.Native.FailDelete = true;
        Assert.Equal("dns.sync_failed", (await Assert.ThrowsAsync<AppException>(() =>
            fixture.Service().DeleteZoneAsync(Fixture.MainSite, fixture.Token))).Message);
        Assert.Equal(shared ? 2 : 1, (await fixture.StatesAsync()).Length);
        Assert.Equal("192.0.2.11", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared"), value => value == "192.0.2.11"));
        fixture.Native.FailSyncZone = null;
        fixture.Native.FailDelete = false;
        await fixture.Service().DeleteZoneAsync(Fixture.MainSite, fixture.Token);
        Assert.DoesNotContain(await fixture.StatesAsync(), state => state.SiteName == Fixture.MainSite);
    }

    [Fact]
    public async Task Equivalent_contributions_publish_once_without_merging_private_records()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var states = await fixture.StatesAsync();
        states[0].Records = [new SavedRecord { Id = "main-cname", Type = "CNAME", Host = "Alias", Value = "TARGET.EXAMPLE.TEST." }];
        states[1].Records = [new SavedRecord { Id = "peer-cname", Type = "CNAME", Host = "alias.example.test.", Value = "target.example.test", Ttl = 7200 }];
        await fixture.SaveStatesAsync(states);

        var result = await fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-cname", Type = "CNAME", Host = "alias", Value = "target.example.test" }, fixture.Token);
        Assert.Equal("main-cname", Assert.Single(result.Records).Id);
        var script = Assert.Single(fixture.Native.SyncScripts);
        Assert.Single(Regex.Matches(script, "Add-DnsServerResourceRecordCName -ZoneName"));
        Assert.Contains("-Name 'alias' -HostNameAlias 'target.example.test'", script, StringComparison.Ordinal);
        Assert.Equal("main-cname", Assert.Single((await fixture.StateAsync(Fixture.MainSite)).Records).Id);
        Assert.Equal("peer-cname", Assert.Single((await fixture.StateAsync(Fixture.AliasSite)).Records).Id);
        Assert.Equal("CNAME|alias", Assert.Single((await fixture.StateAsync(Fixture.AliasSite)).PublishedGroups));
    }

    [Fact]
    public async Task Historical_fully_qualified_published_groups_remove_the_actual_relative_orphan()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.Add(Fixture.Zone, "retired", "192.0.2.77");
        var states = await fixture.StatesAsync();
        var peer = Assert.Single(states, state => state.SiteName == Fixture.AliasSite);
        peer.Records = [];
        peer.PublishedGroups = [.. peer.PublishedGroups, "A|RETIRED.EXAMPLE.TEST."];
        await fixture.SaveStatesAsync(states);

        await fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" }, fixture.Token);

        Assert.Empty(fixture.Native.Values(Fixture.Zone, "retired"));
        Assert.Equal("192.0.2.13", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal("192.0.2.31", Assert.Single(fixture.Native.Values(Fixture.Zone, "main-only")));
        Assert.Empty((await fixture.StateAsync(Fixture.AliasSite)).PublishedGroups);
    }

    [Fact]
    public async Task Conflicting_native_contributions_are_not_reported_as_successful_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var states = await fixture.StatesAsync();
        states[1].Records = [new SavedRecord { Id = "peer-cname", Type = "CNAME", Host = "shared", Value = "target.example.test" }];
        states[1].PublishedGroups = [];
        await fixture.SaveStatesAsync(states);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Host = "shared", Value = "192.0.2.13" }, fixture.Token));
        Assert.Equal("dns.sync_failed", error.Message);
        Assert.Empty((await fixture.StateAsync(Fixture.AliasSite)).PublishedGroups);
    }

    [Theory]
    [InlineData("CNAME", "peer.example.test", "A", "192.0.2.13")]
    [InlineData("A", "192.0.2.22", "CNAME", "requested.example.test")]
    [InlineData("CNAME", "zzz-peer.example.test", "CNAME", "aaa-requested.example.test")]
    public async Task Conflicting_contribution_is_rejected_before_native_deletion_and_preserves_the_peer(
        string peerType, string peerValue, string requestedType, string requestedValue)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var states = await fixture.StatesAsync();
        var main = Assert.Single(states, state => state.SiteName == Fixture.MainSite);
        main.Records = [];
        main.PublishedGroups = [];
        var peer = Assert.Single(states, state => state.SiteName == Fixture.AliasSite);
        peer.Records = [new SavedRecord { Id = "peer-existing", Type = peerType, Host = "shared", Value = peerValue }];
        peer.PublishedGroups = [$"{peerType}|shared"];
        await fixture.SaveStatesAsync(states);
        fixture.Native.Reset(Fixture.Zone);
        fixture.Native.Add(Fixture.Zone, "shared", peerValue, peerType);

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Type = requestedType, Host = "shared", Value = requestedValue }, fixture.Token));

        Assert.Equal("dns.sync_failed", error.Message);
        Assert.Equal(peerValue, Assert.Single(fixture.Native.Values(Fixture.Zone, "shared", peerType)));
        Assert.Equal(0, fixture.Native.CommandCount);
        Assert.Equal(peer.Records.Select(RecordIdentity), (await fixture.StateAsync(Fixture.AliasSite)).Records.Select(RecordIdentity));
        Assert.Equal(peer.PublishedGroups, (await fixture.StateAsync(Fixture.AliasSite)).PublishedGroups);
    }

    [Fact]
    public async Task Shared_address_families_are_valid_together_and_keep_both_native_values()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var states = await fixture.StatesAsync();
        var peer = Assert.Single(states, state => state.SiteName == Fixture.AliasSite);
        peer.Records = [new SavedRecord { Id = "peer-ipv6", Type = "AAAA", Host = "shared", Value = "2001:db8::22" }];
        peer.PublishedGroups = ["AAAA|shared"];
        await fixture.SaveStatesAsync(states);
        fixture.Native.Add(Fixture.Zone, "shared", "2001:db8::22", "AAAA");
        await fixture.Service().UpsertRecordAsync(Fixture.MainSite,
            new UpsertDnsRecordRequest { Id = "main-shared", Type = "A", Host = "shared", Value = "192.0.2.13" }, fixture.Token);
        Assert.Equal("192.0.2.13", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared")));
        Assert.Equal("2001:db8::22", Assert.Single(fixture.Native.Values(Fixture.Zone, "shared", "AAAA")));
        Assert.Single(fixture.Native.SyncScripts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_requires_long_txt_publication_before_detaching_the_working_old_zone(bool failLongTxt)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(includeAlias: false);
        var value = "token=" + new string('s', 280);
        var states = await fixture.StatesAsync();
        states[0].Records = [.. states[0].Records, new SavedRecord { Id = "long-proof", Type = "TXT", Host = "_proof", Value = value }];
        states[0].PublishedGroups = [.. states[0].PublishedGroups, "TXT|_proof"];
        await fixture.SaveStatesAsync(states);
        fixture.Native.Add(Fixture.Zone, "_proof", value, "TXT");
        if (failLongTxt) fixture.Native.FailLongTxtZone = "new.test";

        var error = await Record.ExceptionAsync(() => fixture.Service().UpdateZoneAsync(Fixture.MainSite,
            new UpdateDnsZoneRequest { ZoneName = "new.test" }, fixture.Token));

        Assert.Equal(1, fixture.Native.LongTxtCalls);
        if (failLongTxt)
        {
            Assert.True(fixture.Native.HasZone(Fixture.Zone), "The working native zone must survive an incomplete replacement.");
            Assert.Equal(value, Assert.Single(fixture.Native.Values(Fixture.Zone, "_proof", "TXT")));
            Assert.Equal(Fixture.Zone, (await fixture.StateAsync(Fixture.MainSite)).ZoneName);
            Assert.Equal(0, fixture.Native.ZoneDeletes);
            var failure = Assert.IsType<AppException>(error);
            Assert.Equal("dns.sync_failed", failure.Message);
            Assert.DoesNotContain(value, failure.Details, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('s', 24), failure.Details, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal("new.test", (await fixture.StateAsync(Fixture.MainSite)).ZoneName);
            Assert.Equal(value, Assert.Single(fixture.Native.Values("new.test", "_proof", "TXT")));
            Assert.False(fixture.Native.HasZone(Fixture.Zone));
            Assert.Equal(1, fixture.Native.ZoneDeletes);
        }
    }

    private static string RecordIdentity(SavedRecord record) => $"{record.Id}|{record.Type}|{record.Host}|{record.Value}|{record.Ttl}";

    private sealed class Fixture : IDisposable
    {
        internal const string MainSite = "primary-app";
        internal const string AliasSite = "alias-app";
        internal const string Zone = "example.test";
        private const string StatePath = "config/dns-zones.json";
        private const string Prefix = "adapanel-dns-shared-";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal CancellationToken Token => deadline.Token;
        internal JsonFileStore Store { get; }
        internal FakeNative Native { get; } = new();
        internal bool DnsAvailable { get; set; } = true;
        internal Fixture() => Store = NewStore();
        private JsonFileStore NewStore() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));

        internal async Task SeedAsync(bool includeAlias = true, string aliasZone = Zone)
        {
            var states = new List<SavedZone>
            {
                new() { SiteName = MainSite, ZoneName = Zone, Records = [
                    new() { Id = "main-shared", Host = "shared", Value = "192.0.2.11" },
                    new() { Id = "main-only", Host = "main-only", Value = "192.0.2.31" }] }
            };
            if (includeAlias) states.Add(new SavedZone { SiteName = AliasSite, ZoneName = aliasZone, Records = [
                new() { Id = "alias-shared", Host = "shared", Value = "192.0.2.22" },
                new() { Id = "alias-only", Host = "alias-only", Value = "192.0.2.32" }] });
            foreach (var state in states)
            {
                state.PublishedGroups = state.Records.Select(record => "A|" + record.Host).ToArray();
                foreach (var record in state.Records) Native.Add(state.ZoneName, record.Host, record.Value);
            }
            await Store.WriteAsync(StatePath, states.ToArray(), Token);
        }

        internal DnsService Service()
        {
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                Assert.Equal(nameof(IIisSiteService.GetSiteAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                var name = (string)args[0]!;
                Assert.Contains(name, new[] { MainSite, AliasSite });
                return Task.FromResult(new SiteDto { Id = name == MainSite ? 1 : 2, Name = name,
                    Bindings = [new SiteBindingDto { HostName = name == MainSite ? Zone : "www." + Zone, IpAddress = "192.0.2.10" }] });
            });
            var preferences = Stub<IPanelPreferencesService>((method, args) =>
            {
                Assert.Equal(nameof(IPanelPreferencesService.GetAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                return Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" });
            });
            return new DnsService(sites, preferences, NewStore(), NullLogger<DnsService>.Instance)
            {
                InventoryCommand = DnsTestInventory.MissingZone,
                NativeCommand = Native.RunAsync,
                AvailabilityOverride = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(DnsAvailable); }
            };
        }

        internal Task<SavedZone[]> StatesAsync() => Store.ReadAsync(StatePath, Array.Empty<SavedZone>(), Token);
        internal Task SaveStatesAsync(SavedZone[] states) => Store.WriteAsync(StatePath, states, Token);
        internal async Task<SavedZone> StateAsync(string name) => Assert.Single(await StatesAsync(), state => state.SiteName == name);
        public void Dispose()
        {
            Native.ReleaseFirstSync.TrySetResult();
            deadline.Cancel(); deadline.Dispose();
            if (!Directory.Exists(root)) return;
            var full = Path.GetFullPath(root);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup escaped the unique DNS fixture root.");
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out var path))
            {
                if (path != full && !Path.GetFullPath(path).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("DNS fixture cleanup escaped its owned root.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("DNS fixture cleanup must not follow links.");
                if (attributes.HasFlag(FileAttributes.Directory))
                    foreach (var child in Directory.GetFileSystemEntries(path)) pending.Push(child);
            }
            Directory.Delete(full, recursive: true);
        }
    }

    private sealed class FakeNative
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string Type, string Host, string Value), byte>> zones = new(StringComparer.OrdinalIgnoreCase);
        private int syncCount;
        private int zoneDeletes;
        private int commandCount;
        internal int ZoneDeletes => Volatile.Read(ref zoneDeletes);
        internal int CommandCount => Volatile.Read(ref commandCount);
        internal bool PauseFirstSync { get; set; }
        internal string? FailSyncZone { get; set; }
        internal string? FailLongTxtZone { get; set; }
        internal int LongTxtCalls { get; private set; }
        internal bool FailDelete { get; set; }
        internal Action<string>? BeforeSync { get; set; }
        internal ConcurrentQueue<string> SyncScripts { get; } = new();
        internal ConcurrentQueue<string> Events { get; } = new();
        internal TaskCompletionSource FirstSyncEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string ZoneKey(string zone) => zone.TrimEnd('.').ToLowerInvariant();
        internal bool HasZone(string zone) => zones.ContainsKey(ZoneKey(zone));
        internal void Reset(string zone) => zones[ZoneKey(zone)] = new();
        internal void Add(string zone, string host, string value, string type = "A") => zones.GetOrAdd(ZoneKey(zone), _ => new())[(type, host, value)] = 0;
        internal string[] Values(string zone, string host, string type = "A") => zones.TryGetValue(ZoneKey(zone), out var records)
            ? records.Keys.Where(key => key.Type == type && key.Host == host).Select(key => key.Value).Order(StringComparer.Ordinal).ToArray() : [];

        internal async Task<CommandResult> RunAsync(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref commandCount);
            if (script.Contains("$basarisiz = @()", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref syncCount) == 1 && PauseFirstSync)
                {
                    FirstSyncEntered.TrySetResult();
                    await ReleaseFirstSync.Task.WaitAsync(token);
                }
                token.ThrowIfCancellationRequested();
                var zone = ZoneKey(Capture(script, @"\$zoneName = '([^']+)'"));
                BeforeSync?.Invoke(zone);
                token.ThrowIfCancellationRequested();
                SyncScripts.Enqueue(script);
                if (FailSyncZone == zone) return new CommandResult { ExitCode = 22, StandardOutput = "ADAPANEL-FAIL: controlled native failure" };
                Events.Enqueue("publish:" + zone);
                var records = zones.GetOrAdd(zone, _ => new());
                var failed = false;
                // Apply the actual generated order: RRset removals happen before additions.
                // A later native error does not restore a removed peer's original RRset.
                foreach (var line in Regex.Split(script, "\\r?\\n"))
                {
                    var removal = Regex.Match(line, @"Get-DnsServerResourceRecord -ZoneName \$zoneName -Name '([^']+)' -RRType '([^']+)'");
                    if (removal.Success)
                    {
                        foreach (var key in records.Keys.Where(key => key.Host == removal.Groups[1].Value && key.Type == removal.Groups[2].Value))
                            records.TryRemove(key, out _);
                        continue;
                    }
                    var addition = Regex.Match(line, @"Add-DnsServerResourceRecord(A|AAAA|CName) -ZoneName \$zoneName -Name '([^']+)' -(?:IPv4Address|IPv6Address|HostNameAlias) '([^']+)'");
                    if (!addition.Success) continue;
                    var type = addition.Groups[1].Value.ToUpperInvariant();
                    var host = addition.Groups[2].Value;
                    var value = addition.Groups[3].Value;
                    if (records.Keys.Any(key => key.Host == host &&
                        (type == "CNAME" || key.Type == "CNAME")) || !records.TryAdd((type, host, value), 0))
                        failed = true;
                }
                return failed ? new CommandResult { ExitCode = 22, StandardOutput = "ADAPANEL-FAIL: conflicting or duplicate native record" } : new CommandResult();
            }
            if (script.Contains("$adaTam = '", StringComparison.Ordinal))
            {
                LongTxtCalls++;
                var zone = ZoneKey(Capture(script, @"\$zoneName = '([^']+)'"));
                var host = Capture(script, @"\$adaAd = '([^']+)'" );
                var value = Capture(script, @"\$adaTam = '([^']+)'" );
                if (FailLongTxtZone == zone) return new CommandResult { ExitCode = 23, StandardError = "Controlled publication failure: " + value };
                Add(zone, host, value, "TXT");
                return new CommandResult();
            }
            if (script.Contains("Remove-DnsServerZone", StringComparison.Ordinal))
            {
                if (FailDelete) return new CommandResult { ExitCode = 1, StandardError = "Controlled zone removal failure." };
                Events.Enqueue("delete:" + ZoneKey(Capture(script, @"\$zoneName = '([^']+)'")));
                Interlocked.Increment(ref zoneDeletes);
                zones.TryRemove(ZoneKey(Capture(script, @"\$zoneName = '([^']+)'")), out _);
                return new CommandResult();
            }
            if (script.Contains("[string]$z.ZoneType", StringComparison.Ordinal))
                return new CommandResult { StandardOutput = HasZone(Capture(script, @"Get-DnsServerZone -Name '([^']+)'")) ? "Primary" : "" };
            if (script.Contains("'SIGNED='", StringComparison.Ordinal)) return new CommandResult { StandardOutput = "SIGNED=False" };
            throw new InvalidOperationException("Unexpected command; no native DNS process is allowed in this fixture.");
        }
        private static string Capture(string script, string pattern)
        {
            var match = Regex.Match(script, pattern);
            Assert.True(match.Success, "Expected the generated script's explicit native zone.");
            return match.Groups[1].Value;
        }
    }
    public sealed class SavedZone
    {
        public string SiteName { get; set; } = "";
        public string ZoneName { get; set; } = "";
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
