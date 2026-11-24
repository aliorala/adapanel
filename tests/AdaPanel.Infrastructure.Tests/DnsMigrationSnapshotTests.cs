using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsMigrationSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_snapshot_reads_native_records_without_creating_upgrading_or_backing_up_source_json(bool existing)
    {
        using var fixture = new Fixture(existing);
        var before = fixture.Files();
        var snapshot = await ReadForMigration(fixture.Service);
        Assert.True(snapshot.IsWindowsDnsAvailable); Assert.True(snapshot.IsWindowsZonePresent);
        Assert.Equal("windows-dns", snapshot.Source);
        Assert.Equal("Primary", snapshot.WindowsZoneType);
        Assert.Equal("198.51.100.50", Assert.Single(snapshot.Records).Value);
        Assert.Equal(before, fixture.Files());
        Assert.Equal(1, fixture.InventoryReads);
        Assert.Equal(0, fixture.OtherNativeReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Unmeasured_or_missing_native_zone_never_uses_cached_default_records(bool available, bool present)
    {
        using var fixture = new Fixture(existing: true) { Available = available, Present = present };
        var before = fixture.Files();
        var snapshot = await ReadForMigration(fixture.Service);
        Assert.Equal(available, snapshot.IsWindowsDnsAvailable);
        Assert.False(snapshot.IsWindowsZonePresent);
        Assert.Empty(snapshot.Records);
        Assert.Equal("panel-managed", snapshot.Source);
        Assert.Equal(before, fixture.Files());
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("transfer")]
    public async Task Shared_zone_configuration_conflict_remains_visible_without_rewriting_source_json(string conflict)
    {
        using var fixture = new Fixture(existing: true, conflict);
        var before = fixture.Files();
        var snapshot = await ReadForMigration(fixture.Service);
        Assert.True(conflict == "mode" ? snapshot.ModeConfigurationConflict : snapshot.TransferConfigurationConflict);
        Assert.Equal(before, fixture.Files());
    }

    [Fact]
    public async Task Failed_native_inventory_propagates_and_preserves_source_bytes()
    {
        using var fixture = new Fixture(existing: true) { FailInventory = true };
        var before = fixture.Files();
        await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => ReadForMigration(fixture.Service));
        Assert.Equal(before, fixture.Files());
    }

    [Fact]
    public async Task Cancelled_snapshot_does_not_create_source_json()
    {
        using var fixture = new Fixture(existing: false);
        var before = fixture.Files();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
#if OLD_SOURCE
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetZoneAsync("example.test", cancellation.Token));
#else
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetMigrationSnapshotAsync("example.test", cancellation.Token));
#endif
        Assert.Equal(before, fixture.Files());
    }

    private static Task<SiteDnsZoneDto> ReadForMigration(DnsService service)
#if OLD_SOURCE
        => service.GetZoneAsync("example.test"); // Exact legacy call used by the pre-fix exporter in the isolated source copy.
#else
        => service.GetMigrationSnapshotAsync("example.test");
#endif

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-dns-migration-readonly-" + Guid.NewGuid().ToString("N"));
        internal readonly DnsService Service;
        internal bool Available = true, Present = true, FailInventory;
        internal int InventoryReads, OtherNativeReads;
        internal Fixture(bool existing, string? conflict = null)
        {
            Directory.CreateDirectory(Root);
            if (existing)
            {
                Directory.CreateDirectory(Path.Combine(Root, "config"));
                var states = new List<object>
                {
                    new { siteName = "example.test", zoneName = "example.test", defaultsVersion = 0,
                        isSecondary = false, masterServers = Array.Empty<string>(), zoneTransferIps = new[] { "192.0.2.1" },
                        records = new[] { new { id = "cached", type = "A", host = "@", value = "192.0.2.9", ttl = 3600, isManaged = true } } }
                };
                if (conflict is not null)
                    states.Add(new { siteName = "other.test", zoneName = "example.test", defaultsVersion = 0,
                        isSecondary = conflict == "mode", masterServers = conflict == "mode" ? new[] { "192.0.2.8" } : [],
                        zoneTransferIps = conflict == "transfer" ? new[] { "192.0.2.2" } : new[] { "192.0.2.1" }, records = Array.Empty<object>() });
                File.WriteAllText(Path.Combine(Root, "config", "dns-zones.json"), JsonSerializer.Serialize(states));
            }
            var sites = Stub<IIisSiteService>((method, _) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(new SiteDto { Id = 1, Name = "example.test" }) : throw new InvalidOperationException(method.Name));
            var preferences = Stub<IPanelPreferencesService>((method, _) => method.Name == nameof(IPanelPreferencesService.GetAsync)
                ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" }) : throw new InvalidOperationException(method.Name));
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            Service = new DnsService(sites, preferences, store, NullLogger<DnsService>.Instance)
            {
                AvailabilityOverride = _ => Task.FromResult(Available),
                InventoryCommand = (_, token) =>
                {
                    token.ThrowIfCancellationRequested(); InventoryReads++;
                    return Task.FromResult(FailInventory ? new CommandResult { ExitCode = 1, StandardError = "fixture read failed" }
                        : new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(new { Exists = Present,
                            ZoneType = Present ? "Primary" : "", Records = Present ? new[] { new { Type = "A", Host = "@", Value = "198.51.100.50", Ttl = 1800 } } : [] }) });
                },
                NativeCommand = (script, token) =>
                {
                    token.ThrowIfCancellationRequested(); OtherNativeReads++;
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = script.Contains("Get-DnsServerZone -Name", StringComparison.Ordinal) && Present ? "Primary" : "" });
                }
            };
        }
        internal string[] Files() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray();
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Handler = handler; return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
