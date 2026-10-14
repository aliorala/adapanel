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

public sealed class DnsMutationLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Two_record_additions_survive_the_earlier_publication_finishing_last(bool firstBatch, bool secondBatch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first", firstBatch);
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.AddAsync(fixture.Service(), "second", secondBatch);
        await fixture.CompleteOverlapAsync(first, second);

        var zone = await fixture.ReadZoneAsync();
        Assert.Equal(new[] { "first", "second", "seed" }, zone.Records.Select(record => record.Host).Order().ToArray());
        Assert.Equal(new[] { "A|first", "A|second", "A|seed" }, zone.PublishedGroups.Order().ToArray());
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "first"));
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "second"));
    }

    [Fact]
    public async Task A_record_deleted_during_an_older_sync_is_not_resurrected()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.Service().DeleteRecordAsync(Fixture.Site, "seed-id", fixture.Token);
        await fixture.CompleteOverlapAsync(first, second);

        var zone = await fixture.ReadZoneAsync();
        Assert.Equal("first", Assert.Single(zone.Records).Host);
        Assert.Equal("A|first", Assert.Single(zone.PublishedGroups));
        Assert.Null(fixture.Native.Record(Fixture.Site, "seed"));
    }

    [Fact]
    public async Task The_later_edit_remains_in_both_saved_and_published_records()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.Service().UpsertRecordAsync(Fixture.Site,
            new UpsertDnsRecordRequest { Id = "seed-id", Host = "seed", Value = "192.0.2.11" }, fixture.Token);
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.Service().UpsertRecordAsync(Fixture.Site,
            new UpsertDnsRecordRequest { Id = "seed-id", Host = "seed", Value = "192.0.2.12" }, fixture.Token);
        await fixture.CompleteOverlapAsync(first, second);

        Assert.Equal("192.0.2.12", Assert.Single((await fixture.ReadZoneAsync()).Records).Value);
        Assert.Equal("192.0.2.12", fixture.Native.Record(Fixture.Site, "seed"));
    }

    [Fact]
    public async Task Deleting_the_zone_during_an_older_sync_does_not_recreate_it()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.Service().DeleteZoneAsync(Fixture.Site, fixture.Token);
        await fixture.CompleteOverlapAsync(first, second);

        Assert.Empty(await fixture.ReadZonesAsync());
        Assert.False(fixture.Native.HasZone(Fixture.Site));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_canceled_waiter_does_not_save_or_publish_its_records(bool batch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        fixture.Native.CancelFollowingSync = true;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var waiting = fixture.AddAsync(fixture.Service(), "canceled", batch, token: canceled.Token);
        SavedZone whileFirstSyncIsStillBlocked;
        try
        {
            await Task.WhenAny(fixture.Native.FollowingSyncEntered.Task, Task.Delay(750, fixture.Token));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            whileFirstSyncIsStillBlocked = await fixture.ReadZoneAsync();
        }
        finally
        {
            fixture.Native.ReleaseFirstSync.TrySetResult();
            await first;
        }
        fixture.Native.CancelFollowingSync = false;

        // The first publication must not incidentally hide a canceled waiter's stored change.
        Assert.DoesNotContain(whileFirstSyncIsStillBlocked.Records, record => record.Host == "canceled");
        Assert.Null(fixture.Native.Record(Fixture.Site, "canceled"));
        await fixture.AddAsync(fixture.Service(), "after-cancel");
        Assert.Contains((await fixture.ReadZoneAsync()).Records, record => record.Host == "after-cancel");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_zones_can_publish_while_another_zone_is_waiting(bool batch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(Fixture.Site, "other.test");
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        try
        {
            await fixture.AddAsync(fixture.Service(), "independent", batch, "other.test").WaitAsync(TimeSpan.FromSeconds(5), fixture.Token);
            Assert.False(first.IsCompleted);
            Assert.Equal("192.0.2.10", fixture.Native.Record("other.test", "independent"));
        }
        finally
        {
            fixture.Native.ReleaseFirstSync.TrySetResult();
            await first;
        }
        Assert.Equal(2, (await fixture.ReadZonesAsync()).Length);
        Assert.Contains((await fixture.ReadZoneAsync("other.test")).Records, record => record.Host == "independent");
    }

    [Fact]
    public async Task An_identically_named_zone_in_another_data_root_is_independent()
    {
        using var firstRoot = new Fixture();
        using var secondRoot = new Fixture();
        await firstRoot.SeedAsync();
        await secondRoot.SeedAsync();
        firstRoot.Native.PauseFirstSync = true;
        var first = firstRoot.AddAsync(firstRoot.Service(), "first");
        await firstRoot.Native.FirstSyncEntered.Task.WaitAsync(firstRoot.Token);
        try
        {
            await secondRoot.AddAsync(secondRoot.Service(), "independent").WaitAsync(TimeSpan.FromSeconds(5), secondRoot.Token);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            firstRoot.Native.ReleaseFirstSync.TrySetResult();
            await first;
        }
        Assert.DoesNotContain((await firstRoot.ReadZoneAsync()).Records, record => record.Host == "independent");
        Assert.DoesNotContain((await secondRoot.ReadZoneAsync()).Records, record => record.Host == "first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_native_failure_retains_desired_records_and_a_later_retry_can_publish_them(bool batch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.FailNextSync = true;
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.AddAsync(fixture.Service(), "retry", batch));
        Assert.Equal("dns.sync_failed", error.MessageKey);
        var pending = await fixture.ReadZoneAsync();
        Assert.Contains(pending.Records, record => record.Host == "retry");
        Assert.DoesNotContain("A|retry", pending.PublishedGroups);
        Assert.Null(fixture.Native.Record(Fixture.Site, "retry"));

        await fixture.AddAsync(fixture.Service(), "retry", batch);
        Assert.Single((await fixture.ReadZoneAsync()).Records, record => record.Host == "retry");
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "retry"));
    }

    [Fact]
    public async Task An_already_canceled_request_does_not_change_the_store_or_start_a_native_command()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var before = await File.ReadAllBytesAsync(fixture.Store.ResolvePath(Fixture.StatePath), fixture.Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.AddAsync(fixture.Service(), "canceled", token: canceled.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.ResolvePath(Fixture.StatePath), fixture.Token));
        Assert.Equal(0, fixture.Native.SyncCount);
    }

    [Fact]
    public async Task Different_casing_of_the_same_site_uses_the_same_mutation_lifecycle()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first", site: Fixture.Site.ToUpperInvariant());
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var second = fixture.AddAsync(fixture.Service(), "second");
        await fixture.CompleteOverlapAsync(first, second);
        Assert.Contains((await fixture.ReadZoneAsync()).Records, record => record.Host == "second");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_does_not_publish_default_upgrades_or_erase_a_later_mutation(bool publishedKeys)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var initial = await fixture.ReadZoneAsync();
        initial.DefaultsVersion = DnsService.CurrentDefaultsVersion - 1;
        await fixture.Store.WriteAsync(Fixture.StatePath, new[] { initial }, fixture.Token);
        var service = fixture.Service();
        Task first = publishedKeys ? service.GetPublishedRecordKeysAsync(Fixture.Site, fixture.Token)
            : service.GetZoneAsync(Fixture.Site, fixture.Token);
        var second = fixture.AddAsync(fixture.Service(), "later");
        await Task.WhenAll(first, second);
        var saved = await fixture.ReadZoneAsync();
        Assert.Equal(DnsService.CurrentDefaultsVersion - 1, saved.DefaultsVersion);
        Assert.Contains(saved.Records, record => record.Host == "later");
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "later"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queued_mutation_preserves_the_accepted_record_input(bool batch)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        var request = new UpsertDnsRecordRequest { Host = "accepted", Value = "192.0.2.10" };
        var requests = new List<UpsertDnsRecordRequest> { request };
        var service = fixture.Service();
        var second = batch ? service.UpsertRecordsAsync(Fixture.Site, requests, fixture.Token)
            : service.UpsertRecordAsync(Fixture.Site, request, fixture.Token);
        request.Host = "changed";
        request.Value = "192.0.2.99";
        requests.Clear();
        await fixture.CompleteOverlapAsync(first, second);
        var saved = await fixture.ReadZoneAsync();
        Assert.Equal("192.0.2.10", Assert.Single(saved.Records, record => record.Host == "accepted").Value);
        Assert.DoesNotContain(saved.Records, record => record.Host == "changed");
    }

    [Fact]
    public async Task Subdomain_automation_can_call_the_private_record_core_without_reentering_the_lease()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var result = await fixture.Service().EnsureSubdomainRecordAsync(Fixture.Site, "blog", fixture.Token);
        Assert.True(result.Created);
        Assert.Empty(result.PublishWarningKey);
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "blog"));
        Assert.Contains((await fixture.ReadZoneAsync()).Records, record => record.Host == "blog");
    }

    [Fact]
    public async Task Canceled_best_effort_publication_propagates_and_releases_the_lifecycle()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var first = fixture.Service().EnsureZonePublishedAsync(Fixture.Site, canceled.Token);
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await fixture.AddAsync(fixture.Service(), "after-cancel");
        Assert.Equal("192.0.2.10", fixture.Native.Record(Fixture.Site, "after-cancel"));
    }

    [Fact]
    public async Task Dry_run_remains_read_only_and_does_not_wait_for_an_in_flight_publication()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Native.PauseFirstSync = true;
        var first = fixture.AddAsync(fixture.Service(), "first");
        await fixture.Native.FirstSyncEntered.Task.WaitAsync(fixture.Token);
        try
        {
            var before = await File.ReadAllBytesAsync(fixture.Store.ResolvePath(Fixture.StatePath), fixture.Token);
            var missing = await fixture.Service().RepairDefaultsAsync(Fixture.Site, false, fixture.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), fixture.Token);
            Assert.NotEmpty(missing);
            Assert.False(first.IsCompleted);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.ResolvePath(Fixture.StatePath), fixture.Token));
        }
        finally
        {
            fixture.Native.ReleaseFirstSync.TrySetResult();
            await first;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Site = "example.test";
        internal const string StatePath = "config/dns-zones.json";
        private const string Prefix = "adapanel-dns-lifecycle-";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        internal CancellationToken Token => deadline.Token;
        internal JsonFileStore Store { get; }
        internal FakeNative Native { get; } = new();

        internal Fixture()
        {
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        }

        internal async Task SeedAsync(params string[] names)
        {
            if (names.Length == 0) names = [Site];
            var states = names.Select(name => new SavedZone
            {
                SiteName = name, ZoneName = name, DefaultsVersion = DnsService.CurrentDefaultsVersion,
                Records = [new SavedRecord { Id = "seed-id", Host = "seed", Value = "192.0.2.1" }],
                PublishedGroups = ["A|seed"]
            }).ToArray();
            await Store.WriteAsync(StatePath, states, Token);
            foreach (var name in names) Native.Seed(name);
        }

        internal DnsService Service()
        {
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                Assert.Equal(nameof(IIisSiteService.GetSiteAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                return Task.FromResult(new SiteDto { Id = 1, Name = (string)args[0]! });
            });
            var preferences = Stub<IPanelPreferencesService>((method, args) =>
            {
                Assert.Equal(nameof(IPanelPreferencesService.GetAsync), method.Name);
                ((CancellationToken)args![^1]!).ThrowIfCancellationRequested();
                return Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" });
            });
            // Each request has a separate store/service instance, as scoped DI does.
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            return new DnsService(sites, preferences, store, NullLogger<DnsService>.Instance)
            {
                AvailabilityOverride = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(true); },
                InventoryCommand = DnsTestInventory.MissingZone,
                NativeCommand = Native.RunAsync
            };
        }

        internal Task<SiteDnsZoneDto> AddAsync(DnsService service, string host, bool batch = false,
            string site = Site, CancellationToken? token = null)
        {
            var record = new UpsertDnsRecordRequest { Host = host, Value = "192.0.2.10" };
            return batch ? service.UpsertRecordsAsync(site, [record], token ?? Token)
                : service.UpsertRecordAsync(site, record, token ?? Token);
        }

        internal async Task CompleteOverlapAsync(Task first, Task second)
        {
            try
            {
                // An unprotected second request completes against the saved first snapshot.
                // A serialized implementation may instead wait until the first is released.
                await Task.WhenAny(second, Task.Delay(750, Token));
            }
            finally { Native.ReleaseFirstSync.TrySetResult(); }
            await Task.WhenAll(first, second).WaitAsync(Token);
        }

        internal Task<SavedZone[]> ReadZonesAsync() => Store.ReadAsync(StatePath, Array.Empty<SavedZone>(), Token);
        internal async Task<SavedZone> ReadZoneAsync(string site = Site) => Assert.Single(await ReadZonesAsync(), state => state.SiteName == site);

        public void Dispose()
        {
            Native.ReleaseFirstSync.TrySetResult();
            deadline.Cancel();
            deadline.Dispose();
            if (!Directory.Exists(root)) return;
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("DNS fixture cleanup escaped its unique owned root.");
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out var path))
            {
                if (!string.Equals(path, full, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFullPath(path).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("DNS cleanup path escaped its root.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("DNS cleanup must not follow links.");
                if (attributes.HasFlag(FileAttributes.Directory))
                    foreach (var child in Directory.GetFileSystemEntries(path)) pending.Push(child);
            }
            Directory.Delete(full, recursive: true);
        }
    }

    private sealed class FakeNative
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> zones = new(StringComparer.OrdinalIgnoreCase);
        private int syncCount;
        internal int SyncCount => Volatile.Read(ref syncCount);
        internal bool PauseFirstSync { get; set; }
        internal bool CancelFollowingSync { get; set; }
        internal bool FailNextSync { get; set; }
        internal TaskCompletionSource FirstSyncEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FollowingSyncEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Seed(string zone) => zones[zone] = new(StringComparer.OrdinalIgnoreCase) { ["seed"] = "192.0.2.1" };
        internal bool HasZone(string zone) => zones.ContainsKey(zone);
        internal string? Record(string zone, string host) => zones.TryGetValue(zone, out var records) && records.TryGetValue(host, out var value) ? value : null;

        internal async Task<CommandResult> RunAsync(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.Contains("$basarisiz = @()", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref syncCount);
                if (call == 1 && PauseFirstSync)
                {
                    FirstSyncEntered.TrySetResult();
                    await ReleaseFirstSync.Task.WaitAsync(token);
                }
                else if (call > 1 && CancelFollowingSync)
                {
                    FollowingSyncEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                token.ThrowIfCancellationRequested();
                if (FailNextSync)
                {
                    FailNextSync = false;
                    return new CommandResult { ExitCode = 22, StandardOutput = "ADAPANEL-FAIL: A retry: controlled failure" };
                }
                var zone = Capture(script, @"\$zoneName = '([^']+)'");
                var records = zones.GetOrAdd(zone, _ => new(StringComparer.OrdinalIgnoreCase));
                foreach (Match match in Regex.Matches(script, @"Get-DnsServerResourceRecord -ZoneName \$zoneName -Name '([^']+)' -RRType 'A'"))
                    records.TryRemove(match.Groups[1].Value, out _);
                foreach (Match match in Regex.Matches(script, @"Add-DnsServerResourceRecordA -ZoneName \$zoneName -Name '([^']+)' -IPv4Address '([^']+)'"))
                    records[match.Groups[1].Value] = match.Groups[2].Value;
                return new CommandResult();
            }
            if (script.Contains("Remove-DnsServerZone", StringComparison.Ordinal))
            {
                zones.TryRemove(Capture(script, @"\$zoneName = '([^']+)'"), out _);
                return new CommandResult();
            }
            if (script.Contains("[string]$z.ZoneType", StringComparison.Ordinal))
                return new CommandResult { StandardOutput = HasZone(Capture(script, @"Get-DnsServerZone -Name '([^']+)'")) ? "Primary" : "" };
            if (script.Contains("'SIGNED='", StringComparison.Ordinal)) return new CommandResult { StandardOutput = "SIGNED=False" };
            if (script.StartsWith("Get-DnsServerResourceRecord -ZoneName '", StringComparison.Ordinal))
            {
                var zone = Capture(script, @"Get-DnsServerResourceRecord -ZoneName '([^']+)'");
                return new CommandResult { StandardOutput = zones.TryGetValue(zone, out var records)
                    ? string.Join('\n', records.Keys.Select(host => "A|" + host)) : "" };
            }
            throw new InvalidOperationException("Unexpected DNS command; this fixture never launches a native process.");
        }

        private static string Capture(string script, string pattern)
        {
            var match = Regex.Match(script, pattern);
            Assert.True(match.Success, "Expected a generated DNS command with a measured zone name.");
            return match.Groups[1].Value;
        }
    }

    public sealed class SavedZone
    {
        public string SiteName { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public int DefaultsVersion { get; set; }
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
