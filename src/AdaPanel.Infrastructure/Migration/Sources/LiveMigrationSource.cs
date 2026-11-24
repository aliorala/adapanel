using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Sources;

internal sealed class LiveMigrationSource : ILiveMigrationSource
{
    private readonly LiveMigrationOptions options;
    private readonly IOptions<PanelRuntimeOptions> runtime;
    private readonly ILiveSshTransport transport;
    private readonly OutboundAddressGuard.OutboundHostResolver resolver;
    public LiveMigrationSource(IOptions<PanelRuntimeOptions> runtime, IOptions<LiveMigrationOptions> options)
        : this(runtime, options.Value, new LiveSshTransport(options.Value), OutboundAddressGuard.SystemResolver) { }
    internal LiveMigrationSource(IOptions<PanelRuntimeOptions> runtime, LiveMigrationOptions options, ILiveSshTransport transport,
        OutboundAddressGuard.OutboundHostResolver resolver)
    { this.runtime = runtime; this.options = options; this.transport = transport; this.resolver = resolver; }

    public async Task<LiveMigrationDiscovery> DiscoverAsync(LiveMigrationConnection connection, CancellationToken cancellationToken = default)
    {
        if (!Supported(connection)) return new() { ErrorCode = "migration.live_platform_unsupported" };
        try
        {
            await using var session = await ConnectAsync(connection, cancellationToken);
            return await DiscoverCoreAsync(session, connection.Provider, cancellationToken);
        }
        catch (OperationCanceledException) { return new() { Supported = true, ErrorCode = "migration.live_cancelled" }; }
        catch (AppException e) { return new() { Supported = true, ErrorCode = e.MessageKey, HostFingerprint = Fingerprint(e) }; }
        catch { return new() { Supported = true, ErrorCode = "migration.live_connection_failed" }; }
    }

    public async Task<LiveMigrationAcquireResult> AcquireAsync(LiveMigrationConnection connection, IReadOnlyCollection<string> selectedUnitIds,
        string privateLocalOutputRoot, IProgress<LiveMigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Supported(connection)) return new() { ErrorCode = "migration.live_platform_unsupported" };
        var artifacts = new List<LiveSourceArtifact>();
        var remoteFiles = new List<string>();
        var workspace = "";
        var manifestPath = "";
        var confirmed = true;
        var cleanup = false;
        ILiveSshSession? session = null;
        string? partialLocal = null;
        try
        {
            ValidateLocalRoot(privateLocalOutputRoot);
            if (selectedUnitIds.Count == 0 || selectedUnitIds.Count > options.MaxSourceUnits || selectedUnitIds.Distinct(StringComparer.Ordinal).Count() != selectedUnitIds.Count)
                throw new AppException("migration.live_selection_invalid");
            session = await ConnectAsync(connection, cancellationToken);
            var inventory = await DiscoverCoreAsync(session, connection.Provider, cancellationToken);
            var selected = selectedUnitIds.Select(id => inventory.Units.SingleOrDefault(unit => unit.Id == id)
                ?? throw new AppException("migration.live_selection_stale")).ToArray();
            workspace = "/var/tmp/adapanel-migration-" + Guid.NewGuid().ToString("N");
            manifestPath = Path.Combine(privateLocalOutputRoot, "live-source-recovery-" + Guid.NewGuid().ToString("N") + ".json");
            // Persist ownership intent before touching source storage. A crash leaves a conservative
            // recovery record, never permission to delete while a source process might still run.
            await SecureTempFile.WriteAsync(manifestPath, JsonSerializer.Serialize(new
            {
                RemoteWorkspace = workspace,
                PlannedFiles = selected.Select((unit, index) => workspace + "/" + (connection.Provider == "cpanel" ? "cpmove-" + unit.Name + ".tar.gz" : "source-" + index + ".tar")).ToArray(),
                AutomaticCleanupAllowed = false,
                Reason = "migration.live_process_stop_requires_confirmation"
            }), new UTF8Encoding(false), cancellationToken);
            await session.CreatePrivateDirectoryAsync(workspace, cancellationToken);
            for (var index = 0; index < selected.Length; index++)
            {
                var unit = selected[index];
                var fileName = connection.Provider == "cpanel" ? "cpmove-" + unit.Name + ".tar.gz" : "source-" + index + ".tar";
                var remote = workspace + "/" + fileName;
                remoteFiles.Add(remote);
                progress?.Report(new() { Stage = "migration.live_backing_up", SourceUnitId = unit.Id });
                // Once execution begins, neither cancellation nor a disconnected channel proves descendants stopped.
                confirmed = false;
                var command = await session.ExecuteAsync(BackupCommand(connection.Provider, unit.Name, workspace, remote), options.BackupTimeout, cancellationToken);
                confirmed = command.CompletionConfirmed;
                if (!confirmed || command.ExitCode != 0) throw new AppException("migration.live_backup_failed");
                var entry = (await session.ListAsync(workspace, cancellationToken)).SingleOrDefault(item => item.Name == fileName);
                if (entry == null || !entry.IsRegularFile || entry.IsSymbolicLink || entry.IsDirectory || entry.Bytes <= 0 || entry.Bytes > options.MaxArtifactBytes)
                    throw new AppException("migration.live_artifact_invalid");
                if (artifacts.Sum(item => item.Bytes) > options.MaxTotalArtifactBytes - entry.Bytes)
                    throw new AppException("migration.live_artifact_limit");
                partialLocal = Path.Combine(privateLocalOutputRoot, Guid.NewGuid().ToString("N") + (connection.Provider == "cpanel" ? ".tar.gz" : ".tar"));
                progress?.Report(new() { Stage = "migration.live_downloading", SourceUnitId = unit.Id });
                var hash = await DownloadAsync(session, remote, partialLocal, entry.Bytes, unit.Id, progress, cancellationToken);
                var after = (await session.ListAsync(workspace, cancellationToken)).SingleOrDefault(item => item.Name == fileName);
                if (after == null || after.Bytes != entry.Bytes || !after.IsRegularFile || after.IsSymbolicLink)
                    throw new AppException("migration.live_artifact_changed");
                artifacts.Add(new()
                {
                    ServerGeneratedPath = partialLocal, SourceUnitId = unit.Id, Provider = connection.Provider,
                    Format = connection.Provider == "cpanel" ? "cpanel-cpmove" : "plesk-backup-tar",
                    SourceVersion = inventory.SourceVersion, Full = true, Bytes = entry.Bytes, Sha256 = hash
                });
                partialLocal = null;
                progress?.Report(new() { Stage = "migration.live_acquired_requires_validation", SourceUnitId = unit.Id, Bytes = entry.Bytes });
            }
            cleanup = await CleanupAsync(session, workspace, remoteFiles, cancellationToken);
            return Result("");
        }
        catch (OperationCanceledException) { return Result("migration.live_cancelled", true); }
        catch (AppException e) { return Result(e.MessageKey, fingerprint: Fingerprint(e)); }
        catch { return Result("migration.live_acquire_failed"); }
        finally
        {
            if (partialLocal != null)
            {
                try { File.Delete(partialLocal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // Never remove source workspace after an interrupted/unknown backup process.
            if (session != null) { try { await session.DisposeAsync(); } catch { } }
        }
        LiveMigrationAcquireResult Result(string code, bool cancelled = false, string fingerprint = "") => new()
        {
            Artifacts = artifacts.ToArray(), ErrorCode = code, Cancelled = cancelled, HostFingerprint = fingerprint,
            ProcessCompletionConfirmed = confirmed, RemoteCleanupComplete = cleanup,
            RemoteWorkspace = workspace, RecoveryManifestPath = manifestPath, AttemptedRemoteFiles = remoteFiles.ToArray()
        };
    }

    private async Task<ILiveSshSession> ConnectAsync(LiveMigrationConnection connection, CancellationToken cancellationToken)
    {
        if (connection.SshPort is < 1 or > 65535 || connection.SshUsername != "root" ||
            (connection.Password.Length == 0) == (connection.PrivateKey.Length == 0) ||
            connection.Password.Length > 4096 || connection.PrivateKey.Length > 128 * 1024)
            throw new AppException("migration.live_connection_invalid");
        var inspection = await OutboundAddressGuard.InspectAsync(connection.Host, resolver,
            OutboundTargetPolicy.Connecting(runtime.Value.AllowInternalOutboundTargets), cancellationToken);
        if (inspection.BlockedTarget != null || inspection.Addresses.Length == 0)
            throw new AppException("migration.host_forbidden", 403, 403);
        return await transport.ConnectAsync(connection, inspection.Addresses[0], cancellationToken);
    }

    private async Task<LiveMigrationDiscovery> DiscoverCoreAsync(ILiveSshSession session, string provider, CancellationToken cancellationToken)
    {
        var identity = await session.ExecuteAsync("/usr/bin/id -u", TimeSpan.FromSeconds(30), cancellationToken);
        if (identity.ExitCode != 0 || identity.Truncated || identity.Output.Trim() != "0") throw new AppException("migration.live_root_required");
        var listing = await session.ExecuteAsync(provider == "cpanel" ? "/usr/local/cpanel/bin/whmapi1 --output=json listaccts" : "/usr/sbin/plesk bin subscription --list",
            TimeSpan.FromMinutes(2), cancellationToken);
        if (listing.ExitCode != 0 || listing.Truncated) throw new AppException("migration.live_discovery_failed");
        var names = new List<string>();
        if (provider == "cpanel")
        {
            using var json = JsonDocument.Parse(listing.Output, new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("metadata", out var metadata) || !metadata.TryGetProperty("result", out var result) || result.GetInt32() != 1 ||
                !json.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("acct", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                throw new AppException("migration.live_discovery_failed");
            foreach (var account in accounts.EnumerateArray()) names.Add(account.GetProperty("user").GetString() ?? "");
        }
        else names.AddRange(listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (names.Count > options.MaxSourceUnits || names.Distinct(StringComparer.Ordinal).Count() != names.Count || names.Any(name => !ValidUnit(provider, name)))
            throw new AppException("migration.live_source_unit_invalid");
        var version = await session.ExecuteAsync(provider == "cpanel" ? "/usr/local/cpanel/cpanel -V" : "/usr/sbin/plesk version", TimeSpan.FromSeconds(30), cancellationToken);
        var safeVersion = version.ExitCode == 0 && !version.Truncated
            ? Regex.Match(version.Output, @"\b\d{1,4}\.\d{1,4}(?:\.\d{1,4}){0,3}\b", RegexOptions.CultureInvariant).Value : "";
        return new()
        {
            Supported = true, SourceVersion = safeVersion,
            Units = names.Select(name => new LiveMigrationSourceUnit
            { Id = provider + ":" + name, Name = name, Provider = provider, Kind = provider == "cpanel" ? "account" : "subscription" }).ToArray()
        };
    }

    internal static bool ValidUnit(string provider, string value) => provider == "cpanel"
        ? value != "root" && Regex.IsMatch(value, @"\A[a-z][a-z0-9_]{0,15}\z", RegexOptions.CultureInvariant)
        : value.Length <= 253 && value.Contains('.') && value.Split('.').All(label => label.Length is > 0 and <= 63 && Regex.IsMatch(label, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\z", RegexOptions.CultureInvariant));

    internal static string BackupCommand(string provider, string unit, string workspace, string remoteFile)
    {
        if (!ValidUnit(provider, unit) || !Regex.IsMatch(workspace, @"\A/var/tmp/adapanel-migration-[a-f0-9]{32}\z") ||
            !remoteFile.StartsWith(workspace + "/", StringComparison.Ordinal) || remoteFile[(workspace.Length + 1)..].Contains('/') || remoteFile.Contains(".."))
            throw new AppException("migration.live_source_unit_invalid");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        return provider == "cpanel"
            ? "/usr/local/cpanel/scripts/pkgacct --dbbackup=all --skipapitokens --skipauthnlinks " + Quote(unit) + " " + Quote(workspace)
            : "/usr/sbin/plesk bin pleskbackup --domains-name " + Quote(unit) + " -output-file " + Quote(remoteFile) + " -compression-level fastest -v";
    }

    private async Task<string> DownloadAsync(ILiveSshSession session, string remote, string local, long expected, string id,
        IProgress<LiveMigrationProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = await session.OpenReadAsync(remote, cancellationToken);
        await using var output = SecureTempFile.Create(local);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (received > expected - count || received > options.MaxArtifactBytes - count)
                throw new AppException("migration.live_artifact_limit");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count); received += count;
            progress?.Report(new() { Stage = "migration.live_downloading", SourceUnitId = id, Bytes = received });
        }
        if (received != expected) throw new AppException("migration.live_artifact_truncated");
        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<bool> CleanupAsync(ILiveSshSession session, string workspace, IReadOnlyList<string> ownedFiles, CancellationToken cancellationToken)
    {
        var entries = await session.ListAsync(workspace, cancellationToken);
        // Refuse unknown files/directories/symlinks; never recursively delete a computed source path.
        if (entries.Any(entry => !entry.IsRegularFile || entry.IsSymbolicLink || entry.Name.Contains('/') || entry.Name is "." or ".." || !ownedFiles.Contains(workspace + "/" + entry.Name, StringComparer.Ordinal))) return false;
        foreach (var entry in entries) await session.DeleteFileAsync(workspace + "/" + entry.Name, cancellationToken);
        if ((await session.ListAsync(workspace, cancellationToken)).Count != 0) return false;
        await session.DeleteDirectoryAsync(workspace, cancellationToken);
        return true;
    }
    private static void ValidateLocalRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new AppException("migration.live_private_storage_invalid");
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new AppException("migration.live_private_storage_invalid");
    }
    private static bool Supported(LiveMigrationConnection connection) => connection.Platform == "linux" && connection.Provider is "cpanel" or "plesk";
    private static string Fingerprint(AppException error) => error.MessageKey == "migration.hostkey.untrusted" ? error.Details : "";
}
