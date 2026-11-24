using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class MigrationDnsTarget(IMigrationDnsProvisioner dns, IIisSiteService sites, IConcurrencyLock gates) : IMigrationTargetAdapter
{
    public string Kind => "dns";
    public async Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest, MigrationPlanRequest selection, CancellationToken cancellationToken)
    {
        var check = new MigrationTargetCheck();
        try
        {
            var converted = Prepare(item, manifest, selection);
            check.Warnings.AddRange(converted.Actions);
            // The selected target site does not exist until the earlier site component runs.
            // Fresh native DNS inventory is independent of that future IIS identity.
            var snapshot = await dns.InspectAsync(converted.ZoneName, cancellationToken);
            if (!snapshot.Measured || !snapshot.Available) check.Blockers.Add("migration.dns_native_inventory_unmeasured");
            if (snapshot.Exists) check.Blockers.Add("migration.dns_target_zone_exists");
            if (snapshot.ConfigurationExists) check.Blockers.Add("migration.dns_target_configuration_exists");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { check.Blockers.Add(Safe(error)); }
        return check;
    }
    public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest) => [];
    public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest, MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken)
    {
        var result = new MigrationComponentResult();
        try
        {
            if (credentials.Count != 0) throw Error("unexpected_credentials");
            var converted = Prepare(item, manifest, selection); result.RequiredActions.AddRange(converted.Actions);
            using var target = await SiteMutationGuard.CaptureAsync(sites, gates, converted.SiteName, cancellationToken);
            using var reservation = await target.ReserveAsync((actual, _) => { ValidateSite(actual, converted); return Task.CompletedTask; }, cancellationToken);
            var actual = target.Site;
            var imported = await dns.ImportFreshAsync(new()
            {
                SiteName = converted.SiteName, SiteId = actual.Id, ApplicationRootPath = converted.Path,
                ZoneName = converted.ZoneName, Records = converted.Records
            }, cancellationToken);
            result.CreatedResources.AddRange(imported.CreatedResources); result.Evidence.AddRange(imported.Evidence);
            result.PartialStatePossible = imported.PartialStatePossible || imported.Attempted || imported.ZoneCreated;
            if (!imported.Verified || imported.ErrorCode.Length > 0)
            { result.ErrorCode = imported.ErrorCode.Length > 0 ? imported.ErrorCode : "migration.dns_verification_failed"; return result; }
            await target.ValidateAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            result.Verified = true; result.PartialStatePossible = false;
        }
        catch (OperationCanceledException) { result.ErrorCode = "migration.dns_cancelled"; }
        catch (Exception error) { result.ErrorCode = Safe(error); }
        return result;
    }
    private static void ValidateSite(SiteDto site, Prepared converted)
    {
        if (!Path.GetFullPath(site.ApplicationRootPath).Equals(Path.GetFullPath(converted.Path), StringComparison.OrdinalIgnoreCase)
            || !site.Bindings.Any(binding => binding.HostName.TrimEnd('.').Equals(converted.ZoneName, StringComparison.OrdinalIgnoreCase)
                || binding.HostName.TrimEnd('.').EndsWith("." + converted.ZoneName, StringComparison.OrdinalIgnoreCase))) throw Error("target_site_changed");
    }
    private sealed record Prepared(string SiteName, string Path, string ZoneName, IReadOnlyList<MigrationDnsRecord> Records, IReadOnlyList<string> Actions);
    private static string Safe(Exception error) => error is AppException app && app.MessageKey.StartsWith("migration.", StringComparison.Ordinal) ? app.MessageKey : "migration.dns_operation_failed";
    private static AppException Error(string code) => new("migration.dns_" + code, 409, 409);
}
