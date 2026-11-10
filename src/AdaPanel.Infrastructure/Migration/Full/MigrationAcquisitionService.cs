using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class MigrationAcquisitionService(
    MigrationJobStore jobs, IMigrationPackageStore packages, IFullMigrationService full,
    IAuthService auth, ISecretProtector secrets, IAdaPanelLiveMigrationSource nativeSource,
    ILiveMigrationSource sshSource, CpanelBackupNormalizer cpanel, PleskBackupNormalizer plesk,
    IIisSiteService sites, IOptions<PanelRuntimeOptions> runtime, IOptions<PortableMigrationPackageOptions> packageLimits,
    IOptions<LiveMigrationOptions> liveLimits) : IMigrationAcquisitionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();
    internal Func<Stream, CancellationToken, Task<PortableMigrationPackage>>? CpanelNormalizer { get; init; }
    internal Func<Stream, string, string, CancellationToken, Task<PortableMigrationPackage>>? PleskNormalizer { get; init; }

    public async Task<MigrationDiscoveryDto> DiscoverAsync(Guid owner, MigrationConnectionRequest connection, CancellationToken ct = default)
    {
        await AuthorizeAsync(owner, ct);
        connection = Clone(connection ?? throw Error("migration.acquisition_connection_invalid"));
        ValidateConnection(connection);
        try
        {
            MigrationDiscoveryDto result;
            if (connection.Provider == "adapanel")
            {
                var inventory = await nativeSource.DiscoverAsync(connection.AdaPanel!, ct);
                if (inventory.Provider != "adapanel") throw Error("migration.acquisition_source_invalid");
                result = new() { Provider = "adapanel", AdaPanel = inventory };
            }
            else
            {
                var inventory = await sshSource.DiscoverAsync(connection.Ssh!, ct);
                if (inventory.Units.Any(unit => unit.Provider != connection.Provider || !ValidUnitId(connection.Provider, unit.Id))) throw Error("migration.acquisition_source_invalid");
                var fingerprint = TrustFingerprintOnly("migration.hostkey.untrusted", inventory.HostFingerprint);
                var hasError = inventory.ErrorCode.Length != 0;
                result = new() { Provider = connection.Provider, HostingPanel = new()
                {
                    Supported = !hasError && inventory.Supported,
                    ErrorCode = !hasError ? "" : inventory.ErrorCode == "migration.hostkey.untrusted" && fingerprint is not null
                        ? "migration.hostkey.untrusted" : "migration.acquisition_discovery_failed",
                    HostFingerprint = hasError && inventory.ErrorCode != "migration.hostkey.untrusted" ? "" : fingerprint ?? "",
                    SourceVersion = hasError ? "" : inventory.SourceVersion, Units = hasError ? [] : inventory.Units
                } };
            }
            await AuthorizeAsync(owner, ct);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (AppException error) when (error.MessageKey == "migration.forbidden") { throw Error("migration.forbidden", 403); }
        catch (AppException error) when ((connection.Provider == "adapanel" && error.MessageKey == "migration.tls.untrusted"
            || connection.Provider is "cpanel" or "plesk" && error.MessageKey == "migration.hostkey.untrusted")
            && TrustFingerprintOnly(error.MessageKey, error.Details) is not null)
        {
            await AuthorizeAsync(owner, ct);
            // Preserve only an exact, provider-specific SHA-256 identity. Never relay remote diagnostics.
            throw new AppException(error.MessageKey, 409, 409, error.Details);
        }
        catch { throw Error("migration.acquisition_discovery_failed"); }

        static string? TrustFingerprintOnly(string key, string? value)
        {
            if (value is null || value.Length > 102 || !value.StartsWith("SHA256:", StringComparison.Ordinal)) return null;
            var digest = value[7..];
            if (key == "migration.tls.untrusted")
                return Regex.IsMatch(digest, @"\A(?:[0-9a-fA-F]{64}|[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){31})\z", RegexOptions.CultureInvariant) ? value : null;
            if (key != "migration.hostkey.untrusted" || !Regex.IsMatch(digest, @"\A[A-Za-z0-9+/]{43}=?\z", RegexOptions.CultureInvariant)) return null;
            try
            {
                var bytes = Convert.FromBase64String(digest.TrimEnd('=') + "=");
                return bytes.Length == 32 && Convert.ToBase64String(bytes).TrimEnd('=') == digest.TrimEnd('=') ? value : null;
            }
            catch (FormatException) { return null; }
        }
    }

    public async Task<MigrationAcquisitionDto> StartAsync(Guid owner, MigrationAcquireRequest request, CancellationToken ct = default)
    {
        await AuthorizeAsync(owner, ct);
        if (request is null || request.ClientRequestId == Guid.Empty) throw Error("migration.acquisition_request_invalid");
        var prior = (await jobs.ReadAsync(ct)).Acquisitions.SingleOrDefault(r => r.OwnerId == owner && r.Acquisition.ClientRequestId == request.ClientRequestId);
        if (prior is not null) { await AuthorizeAsync(owner, ct); return Clone(prior.Acquisition); }
        request = Clone(request);
        var host = ValidateConnection(request.Connection);
        ValidateSelection(request);
        var id = Guid.NewGuid();
        var plain = JsonSerializer.Serialize(new ProtectedAcquisition(owner, id, request.ClientRequestId, request), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(plain) > 1024 * 1024) throw Error("migration.acquisition_request_invalid");
        string protectedValue;
        try { protectedValue = secrets.Protect(plain); if (string.IsNullOrWhiteSpace(protectedValue)) throw new InvalidOperationException(); }
        catch { throw Error("migration.acquisition_credentials_unavailable"); }
        await EnsurePrivateStorageAsync(ct);
        await AuthorizeAsync(owner, ct);
        var state = await jobs.UpdateAsync(current =>
        {
            if (current.Acquisitions.Any(r => r.OwnerId == owner && r.Acquisition.ClientRequestId == request.ClientRequestId)) return;
            if (current.Acquisitions.Any(r => r.Acquisition.State is "pending" or "running")) throw Error("migration.acquisition_already_running");
            if (current.Acquisitions.Count >= 1000) throw Error("migration.history_limit");
            current.Acquisitions.Add(new()
            {
                OwnerId = owner, ProtectedRequest = protectedValue,
                Acquisition = new() { Id = id, ClientRequestId = request.ClientRequestId, Provider = request.Connection.Provider,
                    SourceHost = host, SourceUnitIds = PublicSelection(request), CreatedAtUtc = DateTimeOffset.UtcNow,
                    DownloadedBytes = request.Connection.Provider == "adapanel" ? null : 0 }
            });
        }, ct);
        return Clone(state.Acquisitions.Single(r => r.OwnerId == owner && r.Acquisition.ClientRequestId == request.ClientRequestId).Acquisition);
    }

    public async Task<IReadOnlyList<MigrationAcquisitionDto>> ListAsync(Guid owner, CancellationToken ct = default)
    {
        await AuthorizeAsync(owner, ct);
        var result = (await jobs.ReadAsync(ct)).Acquisitions.Where(r => r.OwnerId == owner).Select(r => Clone(r.Acquisition)).ToArray();
        await AuthorizeAsync(owner, ct); return result;
    }
    public async Task<MigrationAcquisitionDto> GetAsync(Guid owner, Guid acquisitionId, CancellationToken ct = default)
    {
        await AuthorizeAsync(owner, ct);
        var result = Clone(MigrationJobStore.RequireAcquisition(await jobs.ReadAsync(ct), owner, acquisitionId).Acquisition);
        await AuthorizeAsync(owner, ct); return result;
    }
    public async Task<MigrationAcquisitionDto> CancelAsync(Guid owner, Guid acquisitionId, CancellationToken ct = default)
    {
        await AuthorizeAsync(owner, ct);
        await jobs.UpdateAsync(state =>
        {
            var record = MigrationJobStore.RequireAcquisition(state, owner, acquisitionId);
            if (record.Acquisition.State == "pending")
            {
                record.Acquisition.CancellationRequested = true; record.Acquisition.State = "cancelled";
                record.Acquisition.Stage = "cancelled"; record.Acquisition.FinishedAtUtc = DateTimeOffset.UtcNow; record.ProtectedRequest = "";
            }
            else if (record.Acquisition.State == "running") record.Acquisition.CancellationRequested = true;
        }, ct);
        if (Running.TryGetValue(acquisitionId, out var cancellation))
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        // A concurrent terminal checkpoint wins; do not return a stale cancellation snapshot.
        return await GetAsync(owner, acquisitionId, ct);
    }

    private async Task AuthorizeAsync(Guid owner, CancellationToken ct)
    {
        if (owner == Guid.Empty) throw Error("migration.forbidden", 403);
        var user = await auth.GetUserByIdAsync(owner, ct);
        if (user.Id != owner || !user.IsActive || user.Role != "Administrator") throw Error("migration.forbidden", 403);
    }
    private static bool ValidUnitId(string provider, string id) => id.StartsWith(provider + ":", StringComparison.Ordinal)
        && LiveMigrationSource.ValidUnit(provider, id[(provider.Length + 1)..]);
    private static string ValidateConnection(MigrationConnectionRequest connection)
    {
        if (connection is null) throw Error("migration.acquisition_connection_invalid");
        if (connection.Provider == "adapanel")
        {
            if (connection.AdaPanel is null || connection.Ssh is not null) throw Error("migration.acquisition_connection_invalid");
            return AdaPanelLiveMigrationSource.Origin(connection.AdaPanel.BaseUrl).Authority;
        }
        if (connection.Provider is not ("cpanel" or "plesk") || connection.Ssh is null || connection.AdaPanel is not null
            || connection.Ssh.Provider != connection.Provider || connection.Ssh.Platform != "linux") throw Error("migration.acquisition_connection_invalid");
        var host = connection.Ssh.Host;
        if (string.IsNullOrEmpty(host) || host != host.Trim() || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw Error("migration.acquisition_connection_invalid");
        return host;
    }
    private static void ValidateSelection(MigrationAcquireRequest request)
    {
        if (request.SourceUnitIds is null || request.SourceUnitIds.Length > 1000 || request.SourceUnitIds.Distinct(StringComparer.Ordinal).Count() != request.SourceUnitIds.Length)
            throw Error("migration.acquisition_selection_invalid");
        if (request.Connection.Provider != "adapanel")
        {
            if (request.SourceUnitIds.Length == 0 || request.SourceUnitIds.Any(id => id is null || !ValidUnitId(request.Connection.Provider, id))) throw Error("migration.acquisition_selection_invalid");
            return;
        }
        var selection = request.AdaPanelSelection;
        if (selection is null || request.SourceUnitIds.Length != 0 || selection.SiteIds is null || selection.DatabaseIds is null || selection.TaskIds is null)
            throw Error("migration.acquisition_selection_invalid");
        foreach (var (ids, prefix) in new[] { (selection.SiteIds, "site"), (selection.DatabaseIds, "db"), (selection.TaskIds, "task") })
            if (ids.Length > 10000 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => id is null || !Regex.IsMatch(id, @"\A" + prefix + @"-[0-9a-f]{24}\z", RegexOptions.CultureInvariant))) throw Error("migration.acquisition_selection_invalid");
        if (!selection.AllSites && !selection.AllDatabases && selection.SiteIds.Length == 0 && selection.DatabaseIds.Length == 0 && (!selection.IncludeTasks || selection.TaskIds.Length == 0))
            throw Error("migration.acquisition_selection_invalid");
    }
    private static string[] PublicSelection(MigrationAcquireRequest request) => request.Connection.Provider != "adapanel"
        ? request.SourceUnitIds.ToArray() : request.AdaPanelSelection.SiteIds.Concat(request.AdaPanelSelection.DatabaseIds).Concat(request.AdaPanelSelection.TaskIds)
            .Concat(request.AdaPanelSelection.AllSites ? new[] { "all-sites" } : []).Concat(request.AdaPanelSelection.AllDatabases ? new[] { "all-databases" } : []).ToArray();
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static AppException Error(string code, int status = 409) => new(code, status, status);
    private sealed record ProtectedAcquisition(Guid OwnerId, Guid JobId, Guid ClientRequestId, MigrationAcquireRequest Request);
}
