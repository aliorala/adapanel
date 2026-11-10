using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Support;
using System.Text.Json;
using System.Text;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService : IMigrationDnsProvisioner
{
    public async Task<SiteDnsZoneDto> GetMigrationSnapshotAsync(string siteName, CancellationToken cancellationToken = default)
    {
        using var zoneUse = await EnterZoneAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(zoneUse.Site!, cancellationToken, persist: false);
        // Use the records and presence/type from the same native inventory. A second probe
        // could observe a newly created zone and incorrectly label cached defaults as native.
        var measured = state.LiveInventoryMeasured;
        var present = measured && state.LiveRecords is not null && !string.IsNullOrWhiteSpace(state.LiveZoneType);
        return new SiteDnsZoneDto
        {
            SiteName = state.SiteName, ZoneName = state.ZoneName,
            NameServers = state.NameServers.ToArray(), ZoneTransferIps = (state.ZoneTransferIps ?? []).ToArray(),
            MasterServers = (state.MasterServers ?? []).ToArray(), IsSecondary = state.IsSecondary,
            Records = present ? SortRecords(state.LiveRecords!).Select(ToDto).ToArray() : [],
            IsWindowsDnsAvailable = measured, IsWindowsZonePresent = present,
            WindowsZoneType = measured ? state.LiveZoneType : string.Empty,
            Source = present ? "windows-dns" : "panel-managed",
            IsSharedZone = state.IsSharedZone, ModeConfigurationConflict = state.ModeConfigurationConflict,
            TransferConfigurationConflict = state.TransferConfigurationConflict
        };
    }

    public async Task<MigrationDnsSnapshot> InspectAsync(string zoneName, CancellationToken cancellationToken)
    {
        zoneName = MigrationDnsRecords.Zone(zoneName);
        using var lease = await DnsZoneLifecycle.EnterNativeAsync(fileStore.ResolvePath(StatePath), zoneName, cancellationToken);
        var configured = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken))
            .Any(state => SameNativeZone(state.ZoneName, zoneName));
        if (!await IsWindowsDnsAvailableAsync(cancellationToken)) return new() { Measured = true, ConfigurationExists = configured };
        var live = await ReadMigrationNativeAsync(zoneName, includeRecords: false, cancellationToken);
        return new() { Measured = true, Available = true, Exists = live.Exists, ConfigurationExists = configured, ZoneType = live.ZoneType, ZoneFile = live.ZoneFile };
    }

    public async Task<MigrationDnsImportResult> ImportFreshAsync(MigrationDnsImportRequest request, CancellationToken cancellationToken)
    {
        var result = new MigrationDnsImportResult(); string? payloadFile = null;
        try
        {
            var zone = MigrationDnsRecords.Zone(request.ZoneName);
            var records = MigrationDnsRecords.Validate(request.Records, zone);
            EnsureMigrationManagementCompatible(records, zone);
            using var zoneUse = await EnterZoneAsync(request.SiteName, cancellationToken, additionalZone: zone);
            var actual = zoneUse.Site!;
            if (actual.Id != request.SiteId || !Path.GetFullPath(actual.ApplicationRootPath).Equals(Path.GetFullPath(request.ApplicationRootPath), StringComparison.OrdinalIgnoreCase)
                || !actual.Bindings.Any(binding => binding.HostName.TrimEnd('.').Equals(zone, StringComparison.OrdinalIgnoreCase) || binding.HostName.TrimEnd('.').EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))) throw MigrationDnsError("target_site_changed");
            var configured = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
            if (configured.Any(state => state.SiteName.Equals(request.SiteName, StringComparison.OrdinalIgnoreCase) || SameNativeZone(state.ZoneName, zone))) throw MigrationDnsError("target_configuration_exists");
            if (!await IsWindowsDnsAvailableAsync(cancellationToken)) throw MigrationDnsError("native_unavailable");
            if ((await ReadMigrationNativeAsync(zone, includeRecords: false, cancellationToken)).Exists) throw MigrationDnsError("target_zone_exists");
            var zoneFile = "adapanel-migration-" + Guid.NewGuid().ToString("N") + ".dns";
            payloadFile = SecureTempFile.BuildPath("adapanel-migration-dns", ".json");
            await SecureTempFile.WriteAsync(payloadFile, JsonSerializer.Serialize(new
            {
                ZoneName = zone, ZoneFile = zoneFile,
                Records = records.Select(record => new { TextRepresentation = MigrationDnsRecords.TextRepresentation(record, zone) }).ToArray()
            }), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); result.Attempted = true; result.PartialStatePossible = true;
            result.Evidence.Add("attempted:dns-zone:" + zone);
            var created = await NativeCommand(MigrationCreateScript(payloadFile), cancellationToken);
            if (!created.Success || !MigrationCreateAcknowledged(created.StandardOutput, zoneFile)) throw MigrationDnsError("native_create_unconfirmed");
            result.ZoneCreated = true; result.CreatedResources.Add("dns-zone:" + zone);
            result.Evidence.Add("attempted:dns-records:" + zone + ":" + records.Length);
            var added = await NativeCommand(MigrationAddScript(payloadFile), cancellationToken);
            if (!added.Success || !MigrationAddsAcknowledged(added.StandardOutput, records.Length)) throw MigrationDnsError("native_records_unconfirmed");
            var live = await ReadMigrationNativeAsync(zone, includeRecords: true, cancellationToken);
            VerifyMigrationNative(live, records, zone, zoneFile);
            result.Evidence.Add("migration.dns_native_values_ttl_priority_verified:" + zone + ":" + records.Length);
            // Only publish a fresh reference after native readback. The ordinary state upsert
            // replaces existing references, so migration uses its own compare-and-create update.
            var reference = new DnsZoneState
            {
                SiteName = request.SiteName, ZoneName = zone, DefaultsVersion = CurrentDefaultsVersion,
                NameServers = live.Records.Where(record => record.Type == "NS" && record.Name == "@").Select(record => record.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Records = live.Records.Select(record => new DnsRecordState { Id = Guid.NewGuid().ToString("N"), Type = record.Type, Host = record.Name, Value = record.Value, Ttl = record.Ttl, Priority = record.Type is "MX" or "SRV" ? record.Priority : null, IsManaged = false }).ToList(),
                PublishedGroups = records.Select(record => record.Type + "|" + record.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            await fileStore.UpdateAsync(StatePath, Array.Empty<DnsZoneState>(), states =>
            {
                if (states.Any(state => state.SiteName.Equals(request.SiteName, StringComparison.OrdinalIgnoreCase) || SameNativeZone(state.ZoneName, zone))) throw MigrationDnsError("target_configuration_exists");
                return states.Append(reference).ToArray();
            }, cancellationToken);
            result.CreatedResources.Add("dns-reference:" + request.SiteName);
            VerifyMigrationNative(await ReadMigrationNativeAsync(zone, includeRecords: true, cancellationToken), records, zone, zoneFile);
            cancellationToken.ThrowIfCancellationRequested(); result.Verified = true; result.PartialStatePossible = false;
        }
        catch (OperationCanceledException) { result.ErrorCode = "migration.dns_cancelled"; }
        catch (Exception error) { result.ErrorCode = error is AppException app && app.MessageKey.StartsWith("migration.dns_", StringComparison.Ordinal) ? app.MessageKey : "migration.dns_native_operation_failed"; }
        finally { if (payloadFile != null) try { File.Delete(payloadFile); } catch { result.Verified = false; result.PartialStatePossible = result.Attempted; result.ErrorCode = "migration.dns_private_payload_cleanup_failed"; } }
        return result;
    }
    private static bool MigrationCreateAcknowledged(string output, string file)
    {
        try { using var json = JsonDocument.Parse(output); return json.RootElement.GetProperty("Created").GetBoolean() && json.RootElement.GetProperty("ZoneFile").GetString() == file; }
        catch (JsonException) { return false; } catch (KeyNotFoundException) { return false; } catch (InvalidOperationException) { return false; }
    }
    private static bool MigrationAddsAcknowledged(string output, int count)
    {
        try { using var json = JsonDocument.Parse(output); return json.RootElement.GetProperty("Added").GetInt32() == count; }
        catch (JsonException) { return false; } catch (KeyNotFoundException) { return false; } catch (InvalidOperationException) { return false; }
    }
    private static void VerifyMigrationNative(MigrationNativeZone live, MigrationDnsRecord[] expected, string zone, string zoneFile)
    {
        if (!live.Exists || live.ZoneType != "Primary" || live.ZoneFile != zoneFile || live.UnsupportedRecords != 0) throw MigrationDnsError("native_identity_unverified");
        if (!live.Records.Any(record => record.Type == "NS" && record.Name == "@")) throw MigrationDnsError("native_authorities_unmeasured");
        var actual = live.Records.Where(record => record.Type != "SOA" && !(record.Type == "NS" && record.Name == "@")).Select(record => MigrationDnsRecords.Normalize(record, zone)).ToArray();
        if (!actual.Select(MigrationDnsRecords.Key).Order(StringComparer.Ordinal).SequenceEqual(expected.Select(MigrationDnsRecords.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw MigrationDnsError("native_values_unverified");
    }
    // Use the ordinary panel read/manage normalizer without files, native calls or relaxed guards.
    // A wire-valid record is not manageable when normalization would reject or change its RDATA/TTL.
    internal static void EnsureMigrationManagementCompatible(IEnumerable<MigrationDnsRecord> records, string zone)
    {
        try
        {
            if (NormalizeZoneName(zone) != zone) throw MigrationDnsError("management_format_unsupported");
            foreach (var record in records)
            {
                var priority = record.Type is "MX" or "SRV" ? (int?)record.Priority : null;
                var normalized = NormalizeRecord(new DnsRecordState { Type = record.Type, Host = record.Name,
                    Value = record.Value, Ttl = record.Ttl, Priority = priority }, zone);
                if (normalized.Type != record.Type || normalized.Host != record.Name || normalized.Value != record.Value
                    || normalized.Ttl != record.Ttl || normalized.Priority != priority)
                    throw MigrationDnsError("management_format_unsupported");
            }
        }
        catch (AppException) { throw MigrationDnsError("management_format_unsupported"); }
    }

    private static AppException MigrationDnsError(string code) => new("migration.dns_" + code, 409, 409);
}
