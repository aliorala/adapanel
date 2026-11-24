using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationAcquisitionServiceTests
{
    [Theory]
    [InlineData("adapanel")]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task Discovery_routes_to_the_authenticated_provider_and_rechecks_actor(string provider)
    {
        using var f = new Fixture();
        var result = await f.Service().DiscoverAsync(f.Owner, f.Request(provider).Connection);
        Assert.Equal(provider, result.Provider);
        Assert.Equal(provider == "adapanel" ? 1 : 0, f.NativeDiscoveries);
        Assert.Equal(provider == "adapanel" ? 0 : 1, f.SshDiscoveries);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions);
        f.Active = false;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().DiscoverAsync(f.Owner, f.Request(provider).Connection));
        Assert.Equal("migration.forbidden", error.MessageKey);
    }

    [Fact]
    public async Task Provider_mismatch_and_invalid_unit_ids_stop_before_any_source_call()
    {
        using var f = new Fixture(); var request = f.Request();
        request.Connection.Ssh = new() { Provider = "plesk", Host = "source.test", Password = "SENSITIVE_PASSWORD" };
        await Assert.ThrowsAsync<AppException>(() => f.Service().StartAsync(f.Owner, request));
        request = f.Request(); request.SourceUnitIds = ["cpanel:../../private"];
        await Assert.ThrowsAsync<AppException>(() => f.Service().StartAsync(f.Owner, request));
        Assert.Equal(0, f.SshAcquires); Assert.Equal(0, f.NativeAcquires);
    }

    [Fact]
    public async Task Owner_isolation_and_atomic_request_idempotence_keep_one_pending_job()
    {
        using var f = new Fixture(); var request = f.Request();
        var first = await f.Service().StartAsync(f.Owner, request);
        var again = await f.Service().StartAsync(f.Owner, request);
        Assert.Equal(first.Id, again.Id); Assert.Equal(1, f.Secrets.ProtectedCount);
        var conflict = await Assert.ThrowsAsync<AppException>(() => f.Service().StartAsync(f.Owner, f.Request()));
        Assert.Equal("migration.acquisition_already_running", conflict.MessageKey);
        var other = Guid.NewGuid();
        Assert.Empty(await f.Service().ListAsync(other));
        Assert.Equal("migration.acquisition_not_found", (await Assert.ThrowsAsync<AppException>(() => f.Service().GetAsync(other, first.Id))).MessageKey);
        await Assert.ThrowsAsync<AppException>(() => f.Service().CancelAsync(other, first.Id));
        Assert.Single((await f.Jobs.ReadAsync()).Acquisitions);
    }

    [Fact]
    public async Task Protected_snapshot_survives_HTTP_disconnect_and_terminal_state_has_no_credentials()
    {
        using var f = new Fixture(); var request = f.Request("adapanel");
        using var http = new CancellationTokenSource();
        var started = await f.Service().StartAsync(f.Owner, request, http.Token);
        http.Cancel(); request.Connection.AdaPanel = new() { BaseUrl = "https://changed.test/", ApiToken = "changed" };
        Assert.DoesNotContain("SENSITIVE_TOKEN", await File.ReadAllTextAsync(f.StatePath));
        Assert.NotEmpty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
        Assert.True(await f.Service().RunNextAsync());
        var status = await f.Service().GetAsync(f.Owner, started.Id);
        Assert.Equal("completed", status.State); Assert.True(status.ContentReady); Assert.Single(status.PackageIds);
        Assert.Null(status.DownloadedBytes); Assert.Equal("source.test", status.SourceHost);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().CandidatePackages); Assert.Empty(status.PackageRecoveries);
        Assert.DoesNotContain("SENSITIVE_TOKEN", JsonSerializer.Serialize(status));
        Assert.Equal(1, f.NativeAcquires);
    }

    [Fact]
    public async Task Two_SSH_units_are_receipt_verified_normalized_registered_and_owned_raw_files_cleaned()
    {
        using var f = new Fixture(); var request = f.Request(); request.SourceUnitIds = ["cpanel:alice", "cpanel:bob"];
        var job = await f.Service().StartAsync(f.Owner, request);
        Assert.True(await f.Service().RunNextAsync());
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.True(status.ContentReady); Assert.Equal("completed", status.State);
        Assert.Equal(2, status.PackageIds.Count); Assert.Equal(2, f.Registers); Assert.Equal(2, f.Normalizations);
        Assert.Equal(8, status.DownloadedBytes); Assert.Equal(2, status.NormalizedPayloadBytes);
        Assert.Equal(2, status.RemoteRecoveries.Count); Assert.True(status.RemoteCleanupComplete);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().LocalWorkspaces);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().LocalWorkspaceUnits);
        foreach (var id in status.PackageIds) Assert.Equal(PortableMigrationProvider.Cpanel, (await f.Packages.ReadManifestAsync(id)).Provider);
    }

    [Fact]
    public async Task Plesk_uses_full_receipt_only_normalizer_with_the_verified_format()
    {
        using var f = new Fixture(); var job = await f.Service().StartAsync(f.Owner, f.Request("plesk"));
        await f.Service().RunNextAsync();
        Assert.Equal("completed", (await f.Service().GetAsync(f.Owner, job.Id)).State);
        Assert.Equal("plesk-backup-tar", f.LastPleskFormat); Assert.Equal(1, f.PleskNormalizations);
    }

    [Fact]
    public async Task Later_source_failure_is_partial_and_does_not_hide_registered_packages_or_replay_commands()
    {
        using var f = new Fixture { FailUnit = "cpanel:bob" }; var request = f.Request();
        request.SourceUnitIds = ["cpanel:alice", "cpanel:bob", "cpanel:carol"];
        var job = await f.Service().StartAsync(f.Owner, request); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("partial", status.State); Assert.False(status.ContentReady); Assert.Single(status.PackageIds);
        Assert.Equal(2, f.SshAcquires); Assert.Equal(1, f.Normalizations);
        Assert.Equal("migration.acquisition_source_failed", status.ErrorCode);
        Assert.DoesNotContain("SENSITIVE_PASSWORD", JsonSerializer.Serialize(status));
        Assert.NotEmpty(status.RemoteRecoveries); Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
        Assert.False(await f.Service().RunNextAsync()); Assert.Equal(2, f.SshAcquires);
    }

    [Fact]
    public async Task Normalization_failure_retains_owned_recovery_and_never_claims_ready_content()
    {
        using var f = new Fixture { FailNormalization = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request()); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("failed", status.State); Assert.False(status.ContentReady); Assert.Empty(status.PackageIds);
        Assert.Contains("migration.acquisition_local_recovery_retained", status.Warnings);
        Assert.Equal(0, f.Registers); Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
        Assert.DoesNotContain("SENSITIVE_PASSWORD", status.ErrorCode);
    }

    [Fact]
    public async Task Unknown_remote_completion_is_not_a_full_receipt_and_never_reaches_a_normalizer()
    {
        using var f = new Fixture { UnknownCompletion = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request()); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("interrupted", status.State); Assert.False(status.ContentReady); Assert.Equal(0, f.Normalizations);
        Assert.Equal("migration.acquisition_completion_unknown", status.ErrorCode);
        var recovery = Assert.Single(status.RemoteRecoveries);
        Assert.False(recovery.ProcessCompletionConfirmed); Assert.False(recovery.CleanupComplete); Assert.NotEmpty(recovery.Files);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
    }

    [Fact]
    public async Task Restart_uses_owned_recovery_journal_and_never_replays_a_running_source_command()
    {
        using var f = new Fixture(); var job = await f.Service().StartAsync(f.Owner, f.Request());
        var local = Path.Combine(f.Root, "migration-private", "acquire", job.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(local);
        var remote = "/var/tmp/adapanel-migration-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(local, "live-source-recovery-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { RemoteWorkspace = remote, PlannedFiles = new[] { remote + "/cpmove-alice.tar.gz" } }));
        await f.Jobs.UpdateAsync(state => { var r = state.Acquisitions.Single(); r.Acquisition.State = "running"; r.LocalWorkspaces.Add(local); r.LocalWorkspaceUnits.Add(local, "cpanel:alice"); });
        await f.Service().RecoverInterruptedAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("interrupted", status.State); Assert.Equal(remote, Assert.Single(status.RemoteRecoveries).Workspace);
        Assert.Equal("cpanel:alice", status.RemoteRecoveries.Single().SourceUnitId);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
        Assert.False(await f.Service().RunNextAsync()); Assert.Equal(0, f.SshAcquires);
    }

    [Fact]
    public async Task Pending_cancel_purges_credentials_without_contacting_the_source()
    {
        using var f = new Fixture(); var job = await f.Service().StartAsync(f.Owner, f.Request());
        Assert.Equal("cancelled", (await f.Service().CancelAsync(f.Owner, job.Id)).State);
        Assert.False(await f.Service().RunNextAsync()); Assert.Equal(0, f.SshAcquires);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
    }

    [Fact]
    public async Task Another_scope_can_cancel_a_running_connector_and_terminal_status_is_reread()
    {
        using var f = new Fixture { BlockSource = true }; var job = await f.Service().StartAsync(f.Owner, f.Request());
        var worker = f.Service().RunNextAsync();
        await f.SourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await f.Service().CancelAsync(f.Owner, job.Id);
        await worker.WaitAsync(TimeSpan.FromSeconds(15));
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("cancelled", status.State); Assert.True(status.CancellationRequested); Assert.False(status.ContentReady);
        Assert.Equal("cpanel:alice", Assert.Single(status.RemoteRecoveries).SourceUnitId);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
    }

    [Fact]
    public async Task Revoked_actor_after_download_cannot_normalize_or_register_and_secrets_are_purged()
    {
        using var f = new Fixture { RevokeAfterSource = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request()); await f.Service().RunNextAsync();
        var record = (await f.Jobs.ReadAsync()).Acquisitions.Single();
        Assert.Equal("failed", record.Acquisition.State); Assert.Equal("migration.forbidden", record.Acquisition.ErrorCode);
        Assert.Equal(0, f.Normalizations); Assert.Equal(0, f.Registers); Assert.Empty(record.ProtectedRequest);
        await Assert.ThrowsAsync<AppException>(() => f.Service().GetAsync(f.Owner, job.Id));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("scope")]
    [InlineData("not-full")]
    public async Task Invalid_source_receipt_is_rejected_before_normalization(string fault)
    {
        using var f = new Fixture { ArtifactFault = fault };
        var job = await f.Service().StartAsync(f.Owner, f.Request()); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("failed", status.State); Assert.Empty(status.PackageIds); Assert.Equal(0, f.Normalizations);
        Assert.Equal("migration.acquisition_source_invalid", status.ErrorCode);
    }

    [Fact]
    public async Task Download_budget_is_job_wide_and_next_source_unit_is_not_started_when_exhausted()
    {
        using var f = new Fixture(); f.LiveLimits.MaxTotalArtifactBytes = 5;
        var request = f.Request(); request.SourceUnitIds = ["cpanel:alice", "cpanel:bob"];
        var job = await f.Service().StartAsync(f.Owner, request); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("partial", status.State); Assert.Single(status.PackageIds); Assert.Equal(1, f.SshAcquires);
        Assert.Equal("migration.acquisition_budget_exceeded", status.ErrorCode);
    }

    [Fact]
    public async Task Normalized_payload_budget_is_shared_across_all_packages()
    {
        using var f = new Fixture { PayloadBytes = 3 }; f.ServiceLimits.MaxTotalUncompressedBytes = 5;
        var request = f.Request(); request.SourceUnitIds = ["cpanel:alice", "cpanel:bob", "cpanel:carol"];
        var job = await f.Service().StartAsync(f.Owner, request); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("partial", status.State); Assert.Single(status.PackageIds); Assert.Equal(2, f.SshAcquires);
        Assert.Equal(3, status.NormalizedPayloadBytes); Assert.Equal("migration.acquisition_budget_exceeded", status.ErrorCode);
    }

    [Fact]
    public async Task Native_manifest_scope_is_rechecked_and_manual_issues_can_remain_in_verified_content()
    {
        using var f = new Fixture { ManualIssue = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request("adapanel")); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.True(status.ContentReady);
        Assert.False((await f.Packages.ReadManifestAsync(status.PackageIds.Single())).Complete);
    }

    [Fact]
    public async Task Changed_native_package_scope_is_not_registered()
    {
        using var f = new Fixture { WrongNativeScope = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request("adapanel")); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("failed", status.State); Assert.Empty(status.PackageIds); Assert.Equal(0, f.Registers);
        Assert.Equal("migration.acquisition_scope_changed", status.ErrorCode);
    }

    [Fact]
    public async Task Public_IIS_roots_are_checked_before_any_private_job_state_or_source_workspace_is_created()
    {
        using var f = new Fixture { RootIsPublic = true };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service().StartAsync(f.Owner, f.Request()));
        Assert.Equal("migration.acquisition_storage_unverified", error.MessageKey);
        Assert.False(File.Exists(f.StatePath)); Assert.Equal(0, f.SshAcquires);
    }

    [Fact]
    public async Task Actual_persisted_package_payload_hash_is_checked_before_registration()
    {
        using var f = new Fixture { TamperPackage = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request("adapanel")); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("failed", status.State); Assert.False(status.ContentReady); Assert.Empty(status.PackageIds); Assert.Equal(0, f.Registers);
    }

    [Fact]
    public async Task Protected_request_is_bound_to_job_owner_and_public_source_scope()
    {
        using var f = new Fixture(); var job = await f.Service().StartAsync(f.Owner, f.Request());
        await f.Jobs.UpdateAsync(state => state.Acquisitions.Single().Acquisition.SourceHost = "changed.test");
        await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("migration.acquisition_request_changed", status.ErrorCode); Assert.Equal(0, f.SshAcquires);
        Assert.Empty((await f.Jobs.ReadAsync()).Acquisitions.Single().ProtectedRequest);
    }

    [Fact]
    public async Task Restart_reconciles_an_atomic_registration_checkpoint_without_reexecuting_source_or_registering_an_unowned_package()
    {
        using var f = new Fixture(); var job = await f.Service().StartAsync(f.Owner, f.Request()); var produced = Guid.NewGuid();
        await f.Jobs.UpdateAsync(state =>
        {
            var record = state.Acquisitions.Single(); record.Acquisition.State = "running"; record.PendingPackageIds.Add(produced);
            state.Packages.Add(new() { OwnerId = f.Owner, PackageId = produced, CreatedAtUtc = DateTimeOffset.UtcNow });
        });
        await f.Service().RecoverInterruptedAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("interrupted", status.State); Assert.Equal(new[] { produced }, status.PackageIds);
        Assert.False(await f.Service().RunNextAsync()); Assert.Equal(0, f.SshAcquires); Assert.Equal(0, f.Registers);
    }

    [Fact]
    public async Task Production_job_store_checks_actual_IIS_app_root_before_creating_private_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "ada-acquisition-store-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            var sites = Fake<IIisSiteService>((m, _) => m.Name == nameof(IIisSiteService.GetMigrationTargetSitesAsync)
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "public.test", PhysicalPath = Path.Combine(root, "public"), ApplicationRootPath = root }]) : throw new InvalidOperationException(m.Name));
            var store = new MigrationJobStore(new JsonFileStore(runtime), sites, runtime);
            await Assert.ThrowsAnyAsync<Exception>(() => store.ReadAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => store.UpdateAsync(_ => { }));
            Assert.False(Directory.Exists(Path.Combine(root, "migration-private")));
        }
        finally { PortableMigrationStorage.DeleteOwnedDirectory(root); }
    }

    [Fact]
    public async Task Remote_recovery_parent_path_is_not_accepted_as_an_owned_source_file()
    {
        using var f = new Fixture { BadRecoveryPath = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request()); await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("failed", status.State); Assert.Equal("migration.acquisition_source_invalid", status.ErrorCode);
        Assert.Equal(0, f.Normalizations); Assert.Equal(0, f.Registers);
        Assert.DoesNotContain(status.RemoteRecoveries.SelectMany(r => r.Files), file => file.EndsWith("/..", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_native_database_uses_exporter_db_id_and_allows_no_site_relationship()
    {
        using var f = new Fixture { NativeDatabase = true };
        var request = f.Request("adapanel");
        request.AdaPanelSelection.AllSites = false;
        request.AdaPanelSelection.DatabaseIds = [AdaPanelMigrationExporter.Id("db", "mysql:standalone")];
        var job = await f.Service().StartAsync(f.Owner, request);
        await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.True(status.ContentReady); Assert.Equal(1, f.Registers);
        var db = Assert.Single((await f.Packages.ReadManifestAsync(Assert.Single(status.PackageIds))).Databases);
        Assert.Empty(db.SiteIds); Assert.Equal(request.AdaPanelSelection.DatabaseIds[0], db.Id);
    }

    [Theory]
    [InlineData("site", "db")]
    [InlineData("db", "site")]
    [InlineData("task", "site")]
    public async Task Native_resource_groups_reject_another_resource_prefix(string group, string prefix)
    {
        using var f = new Fixture(); var request = f.Request("adapanel");
        var ids = new[] { AdaPanelMigrationExporter.Id(prefix, "resource") };
        if (group == "site") request.AdaPanelSelection.SiteIds = ids;
        else if (group == "db") request.AdaPanelSelection.DatabaseIds = ids;
        else request.AdaPanelSelection.TaskIds = ids;
        await Assert.ThrowsAsync<AppException>(() => f.Service().StartAsync(f.Owner, request));
        Assert.Equal(0, f.NativeAcquires);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("scope")]
    [InlineData("hash")]
    [InlineData("budget")]
    public async Task Returned_new_package_is_journaled_before_cancel_or_validation_rejection(string fault)
    {
        using var f = new Fixture { CancelAfterPackage = fault == "cancel", WrongNativeScope = fault == "scope", TamperPackage = fault == "hash" };
        if (fault == "budget") f.ServiceLimits.MaxTotalUncompressedBytes = 0;
        // Keep storage preflight valid; the returned payload alone exceeds the aggregate budget.
        if (fault == "budget") { f.ServiceLimits.MaxTotalUncompressedBytes = 1; f.PayloadBytes = 2; }
        var service = f.Service(); var job = await service.StartAsync(f.Owner, f.Request("adapanel"));
        f.CancelService = service; f.CancelJob = job.Id;
        await service.RunNextAsync();
        var status = await service.GetAsync(f.Owner, job.Id);
        Assert.False(status.ContentReady); Assert.Empty(status.PackageIds); Assert.Equal(0, f.Registers);
        var candidate = Assert.Single(status.PackageRecoveries); Assert.False(candidate.Verified);
        Assert.Equal("adapanel", candidate.Provider); Assert.NotEmpty(candidate.RequiredAction);
        Assert.Contains("migration.acquisition_unregistered_package_recovery_required", status.Warnings);
        Assert.Single((await f.Jobs.ReadAsync()).Acquisitions.Single().CandidatePackages);
        await f.Packages.ReadManifestAsync(candidate.PackageId); // retained, never automatically deleted
    }

    [Theory]
    [InlineData("registered-same-owner")]
    [InlineData("registered-other-owner")]
    [InlineData("candidate")]
    [InlineData("pending")]
    [InlineData("accepted")]
    public async Task An_existing_package_cannot_be_claimed_as_a_new_source_result(string existing)
    {
        using var f = new Fixture();
        var initial = await f.Service().StartAsync(f.Owner, f.Request("adapanel"));
        await f.Service().RunNextAsync();
        var packageId = Assert.Single((await f.Service().GetAsync(f.Owner, initial.Id)).PackageIds);
        var manifest = await f.Packages.ReadManifestAsync(packageId);
        f.ReturnExisting = new(packageId, manifest);
        await f.Jobs.UpdateAsync(state =>
        {
            state.Packages.Clear(); state.Acquisitions.Clear();
            if (existing.StartsWith("registered")) state.Packages.Add(new() { PackageId = packageId, OwnerId = existing.EndsWith("same-owner") ? f.Owner : Guid.NewGuid() });
            else
            {
                var old = new OwnedMigrationAcquisition { OwnerId = Guid.NewGuid() };
                old.Acquisition.Id = Guid.NewGuid(); old.Acquisition.State = "failed";
                if (existing == "candidate") old.CandidatePackages.Add(new() { PackageId = packageId });
                if (existing == "pending") old.PendingPackageIds.Add(packageId);
                if (existing == "accepted") old.Acquisition.PackageIds.Add(packageId);
                state.Acquisitions.Add(old);
            }
        });
        var job = await f.Service().StartAsync(f.Owner, f.Request("adapanel"));
        await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Empty(status.PackageIds); Assert.Empty(status.PackageRecoveries);
        Assert.Equal("migration.acquisition_package_invalid", status.ErrorCode); Assert.Equal(1, f.Registers);
    }

    [Fact]
    public async Task Plesk_package_scope_is_rechecked_before_register_even_after_receipt_normalization()
    {
        using var f = new Fixture { WrongPleskScope = true };
        var job = await f.Service().StartAsync(f.Owner, f.Request("plesk"));
        await f.Service().RunNextAsync();
        var status = await f.Service().GetAsync(f.Owner, job.Id);
        Assert.Equal("migration.acquisition_scope_changed", status.ErrorCode);
        Assert.Equal(0, f.Registers); Assert.Empty(status.PackageIds);
        var candidate = Assert.Single(status.PackageRecoveries);
        Assert.Equal("plesk:example.test", candidate.SourceUnitId); Assert.False(candidate.Verified);
    }

    private static readonly string TlsFingerprint = "SHA256:" + string.Join(":", Enumerable.Repeat("A1", 32));
    private static readonly string SshFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("local-host-key-fixture"))).TrimEnd('=');

    private static async Task<(int Status, JsonElement Body, string Raw)> DiscoveryEnvelope(Fixture f, string provider, MigrationConnectionRequest? request = null)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var output = new MemoryStream(); context.Response.Body = output;
        var middleware = new ApiExceptionHandlingMiddleware(async http =>
        {
            var result = await f.Service().DiscoverAsync(f.Owner, request ?? f.Request(provider).Connection);
            await http.Response.WriteAsJsonAsync(ApiResponse<MigrationDiscoveryDto>.Ok(result));
        }, NullLogger<ApiExceptionHandlingMiddleware>.Instance, services.GetRequiredService<IServiceScopeFactory>());
        await middleware.InvokeAsync(context);
        var raw = Encoding.UTF8.GetString(output.ToArray());
        using var document = JsonDocument.Parse(raw);
        return (context.Response.StatusCode, document.RootElement.Clone(), raw);
    }

    [Fact]
    public async Task Native_TLS_trust_failure_survives_actual_API_envelope_with_only_valid_fingerprint()
    {
        using var f = new Fixture { DiscoveryFailure = new AppException("migration.tls.untrusted", 777, 500, TlsFingerprint), TrustUntilAccepted = true };
        var envelope = await DiscoveryEnvelope(f, "adapanel");
        Assert.Equal(409, envelope.Status); Assert.False(envelope.Body.GetProperty("success").GetBoolean());
        Assert.Equal("migration.tls.untrusted", envelope.Body.GetProperty("message").GetString());
        Assert.Equal(TlsFingerprint, envelope.Body.GetProperty("details").GetString()); Assert.Equal(409, envelope.Body.GetProperty("code").GetInt32());
        Assert.DoesNotContain("SENSITIVE_TOKEN", envelope.Raw);
        var request = f.Request("adapanel").Connection;
        request.AdaPanel = new() { BaseUrl = request.AdaPanel!.BaseUrl, ApiToken = request.AdaPanel.ApiToken, AcceptedCertificateFingerprint = TlsFingerprint };
        envelope = await DiscoveryEnvelope(f, "adapanel", request);
        Assert.True(envelope.Body.GetProperty("success").GetBoolean()); Assert.Equal(TlsFingerprint, f.LastDiscoveryPin);
        Assert.Equal("adapanel", envelope.Body.GetProperty("data").GetProperty("adaPanel").GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task SSH_trust_DTO_preserves_UI_key_and_fingerprint_without_partial_inventory_or_raw_details(string provider)
    {
        using var f = new Fixture { TrustUntilAccepted = true, DiscoveryOverride = new() { Supported = true, ErrorCode = "migration.hostkey.untrusted", HostFingerprint = SshFingerprint,
            SourceVersion = "SECRET_VERSION", Units = [new() { Id = provider + ":example.test", Provider = provider, Name = "SECRET_UNIT" }] } };
        if (provider == "cpanel") f.DiscoveryOverride = new() { Supported = true, ErrorCode = "migration.hostkey.untrusted", HostFingerprint = SshFingerprint, SourceVersion = "SECRET_VERSION" };
        var envelope = await DiscoveryEnvelope(f, provider); var remote = envelope.Body.GetProperty("data").GetProperty("hostingPanel");
        Assert.Equal("migration.hostkey.untrusted", remote.GetProperty("errorCode").GetString()); Assert.Equal(SshFingerprint, remote.GetProperty("hostFingerprint").GetString());
        Assert.False(remote.GetProperty("supported").GetBoolean()); Assert.Empty(remote.GetProperty("units").EnumerateArray());
        Assert.DoesNotContain("SECRET", envelope.Raw); Assert.DoesNotContain("SENSITIVE_PASSWORD", envelope.Raw);
        var request = f.Request(provider).Connection; request.Ssh = new() { Provider = provider, Host = "source.test", AcceptedSshFingerprint = SshFingerprint };
        envelope = await DiscoveryEnvelope(f, provider, request);
        Assert.Empty(envelope.Body.GetProperty("data").GetProperty("hostingPanel").GetProperty("errorCode").GetString()!); Assert.Equal(SshFingerprint, f.LastDiscoveryPin);
    }

    [Theory]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task Typed_SSH_trust_exception_is_sanitized_into_the_matching_UI_error_envelope(string provider)
    {
        using var f = new Fixture { DiscoveryFailure = new AppException("migration.hostkey.untrusted", 777, 500, SshFingerprint) };
        var envelope = await DiscoveryEnvelope(f, provider);
        Assert.Equal(409, envelope.Status); Assert.Equal("migration.hostkey.untrusted", envelope.Body.GetProperty("message").GetString());
        Assert.Equal(SshFingerprint, envelope.Body.GetProperty("details").GetString());
    }

    [Theory]
    [InlineData("adapanel", "short")]
    [InlineData("adapanel", "secret")]
    [InlineData("adapanel", "wrong-type")]
    [InlineData("adapanel", "other-key")]
    [InlineData("adapanel", "wrong-provider-key")]
    [InlineData("cpanel", "short")]
    [InlineData("cpanel", "secret")]
    [InlineData("cpanel", "wrong-type")]
    [InlineData("cpanel", "other-key")]
    [InlineData("cpanel", "wrong-provider-key")]
    public async Task Malformed_or_secret_bearing_discovery_details_never_create_a_trust_prompt(string provider, string fault)
    {
        var key = provider == "adapanel" ? "migration.tls.untrusted" : "migration.hostkey.untrusted";
        var fingerprint = provider == "adapanel" ? TlsFingerprint : SshFingerprint;
        var details = fault switch { "short" => "SHA256:1234", "secret" => fingerprint + " · opaque SECRET_DETAIL", "wrong-type" => provider == "adapanel" ? SshFingerprint : TlsFingerprint, _ => fingerprint };
        if (fault == "other-key") key = "migration.source.connection_failed";
        if (fault == "wrong-provider-key") key = provider == "adapanel" ? "migration.hostkey.untrusted" : "migration.tls.untrusted";
        using var f = new Fixture { DiscoveryFailure = new AppException(key, 777, 500, details) };
        var envelope = await DiscoveryEnvelope(f, provider);
        Assert.Equal("migration.acquisition_discovery_failed", envelope.Body.GetProperty("message").GetString());
        Assert.Empty(envelope.Body.GetProperty("details").GetString()!); Assert.DoesNotContain("SECRET_DETAIL", envelope.Raw);
    }

    [Theory]
    [InlineData("SHA256:bad", "migration.hostkey.untrusted")]
    [InlineData("SHA256:abc SECRET_DETAIL", "migration.hostkey.untrusted")]
    [InlineData("SECRET_DETAIL", "migration.live_connection_failed")]
    public async Task SSH_DTO_failures_strip_unvalidated_fingerprints_and_source_diagnostics(string details, string key)
    {
        using var f = new Fixture { DiscoveryOverride = new() { Supported = true, ErrorCode = key, HostFingerprint = details, SourceVersion = "SECRET_VERSION" } };
        var envelope = await DiscoveryEnvelope(f, "cpanel"); var remote = envelope.Body.GetProperty("data").GetProperty("hostingPanel");
        Assert.Equal("migration.acquisition_discovery_failed", remote.GetProperty("errorCode").GetString());
        Assert.Empty(remote.GetProperty("hostFingerprint").GetString()!); Assert.False(remote.GetProperty("supported").GetBoolean()); Assert.DoesNotContain("SECRET", envelope.Raw);
    }

    [Fact]
    public async Task Actor_revoked_during_trust_discovery_gets_no_fingerprint_or_success()
    {
        using var f = new Fixture { RevokeAfterDiscovery = true, DiscoveryFailure = new AppException("migration.tls.untrusted", 409, 409, TlsFingerprint) };
        var envelope = await DiscoveryEnvelope(f, "adapanel");
        Assert.Equal(403, envelope.Status); Assert.Equal("migration.forbidden", envelope.Body.GetProperty("message").GetString()); Assert.Empty(envelope.Body.GetProperty("details").GetString()!);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-acquisition-" + Guid.NewGuid().ToString("N"));
        internal string StatePath => Path.Combine(Root, "migration-private", "jobs.json");
        internal readonly Guid Owner = Guid.NewGuid();
        internal const string SiteId = "site-aaaaaaaaaaaaaaaaaaaaaaaa";
        internal readonly MigrationJobStore Jobs;
        internal readonly PortableMigrationPackageStore Packages;
        internal readonly TestSecrets Secrets = new();
        internal readonly PortableMigrationPackageOptions ServiceLimits = new();
        internal readonly LiveMigrationOptions LiveLimits = new();
        internal Exception? DiscoveryFailure;
        internal LiveMigrationDiscovery? DiscoveryOverride;
        internal bool TrustUntilAccepted, RevokeAfterDiscovery;
        internal string LastDiscoveryPin = "";
        internal bool WrongPleskScope, NativeDatabase, CancelAfterPackage;
        internal PortableMigrationPackage? ReturnExisting;
        internal MigrationAcquisitionService? CancelService;
        internal Guid CancelJob;
        internal bool Active = true, BadRecoveryPath, TamperPackage, FailNormalization, UnknownCompletion, BlockSource, RevokeAfterSource, ManualIssue, WrongNativeScope, RootIsPublic;
        internal string FailUnit = "", ArtifactFault = "", LastPleskFormat = "";
        internal int NativeDiscoveries, NativeAcquires, SshDiscoveries, SshAcquires, Normalizations, PleskNormalizations, Registers, PayloadBytes = 1;
        internal readonly TaskCompletionSource SourceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Microsoft.Extensions.Options.IOptions<PanelRuntimeOptions> runtime;
        private readonly IAdaPanelLiveMigrationSource native;
        private readonly ILiveMigrationSource ssh;
        private readonly IAuthService auth;
        private readonly IFullMigrationService full;
        private readonly IIisSiteService sites;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
            Jobs = new(new JsonFileStore(runtime)); Packages = new(runtime, Microsoft.Extensions.Options.Options.Create(new PortableMigrationPackageOptions()));
            auth = Fake<IAuthService>((m, a) => m.Name == nameof(IAuthService.GetUserByIdAsync)
                ? Task.FromResult(new CurrentUserDto { Id = (Guid)a![0]!, Role = "Administrator", IsActive = Active }) : throw new InvalidOperationException(m.Name));
            sites = Fake<IIisSiteService>((m, _) => m.Name == nameof(IIisSiteService.GetMigrationTargetSitesAsync)
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>(RootIsPublic ? [new() { Name = "public.test", PhysicalPath = Root, ApplicationRootPath = Root }] : []) : throw new InvalidOperationException(m.Name));
            full = Fake<IFullMigrationService>((m, a) => m.Name == nameof(IFullMigrationService.RegisterPackagesAsync)
                ? Register((Guid)a![0]!, (IReadOnlyList<Guid>)a[1]!) : throw new InvalidOperationException(m.Name));
            native = Fake<IAdaPanelLiveMigrationSource>((m, a) => m.Name switch
            {
                nameof(IAdaPanelLiveMigrationSource.DiscoverAsync) => NativeInventory((AdaPanelSourceConnection)a![0]!),
                nameof(IAdaPanelLiveMigrationSource.AcquireAsync) => NativePackage(),
                _ => throw new InvalidOperationException(m.Name)
            });
            ssh = Fake<ILiveMigrationSource>((m, a) => m.Name switch
            {
                nameof(ILiveMigrationSource.DiscoverAsync) => SshInventory((LiveMigrationConnection)a![0]!),
                nameof(ILiveMigrationSource.AcquireAsync) => Acquire((LiveMigrationConnection)a![0]!, ((IReadOnlyCollection<string>)a[1]!).Single(), (string)a[2]!, (IProgress<LiveMigrationProgress>?)a[3], (CancellationToken)a[4]!),
                _ => throw new InvalidOperationException(m.Name)
            });
        }
        internal MigrationAcquisitionService Service()
        {
            var limits = Microsoft.Extensions.Options.Options.Create(ServiceLimits);
            return new(Jobs, Packages, full, auth, Secrets, native, ssh, new CpanelBackupNormalizer(runtime, limits, Packages),
                new PleskBackupNormalizer(runtime, limits, Packages), sites, runtime, limits, Microsoft.Extensions.Options.Options.Create(LiveLimits))
            {
                CpanelNormalizer = (source, ct) => Normalize(source, false, ct),
                PleskNormalizer = (source, format, expectedSubscription, ct) => { LastPleskFormat = format; return Normalize(source, true, ct); }
            };
        }
        internal MigrationAcquireRequest Request(string provider = "cpanel") => new()
        {
            ClientRequestId = Guid.NewGuid(),
            Connection = new() { Provider = provider,
                AdaPanel = provider == "adapanel" ? new() { BaseUrl = "https://source.test/", ApiToken = "SENSITIVE_TOKEN" } : null,
                Ssh = provider != "adapanel" ? new() { Provider = provider, Host = "source.test", Password = "SENSITIVE_PASSWORD" } : null },
            SourceUnitIds = provider == "adapanel" ? [] : [provider + ":" + (provider == "cpanel" ? "alice" : "example.test")],
            AdaPanelSelection = new() { AllSites = true, IncludeMail = false, IncludeDns = false }
        };
        private Task<MigrationSourceInventoryDto> NativeInventory(AdaPanelSourceConnection connection)
        {
            LastDiscoveryPin = connection.AcceptedCertificateFingerprint;
            if (RevokeAfterDiscovery) Active = false;
            if (DiscoveryFailure is not null && (!TrustUntilAccepted || LastDiscoveryPin != ((AppException)DiscoveryFailure).Details)) return Task.FromException<MigrationSourceInventoryDto>(DiscoveryFailure);
            NativeDiscoveries++; return Task.FromResult(new MigrationSourceInventoryDto { Complete = true, Sites = NativeDatabase ? [] : [new() { Id = SiteId, Name = "example.test" }], Databases = NativeDatabase ? [new() { Id = AdaPanelMigrationExporter.Id("db", "mysql:standalone"), Name = "standalone", Engine = "mysql" }] : [] }); }
        private async Task<PortableMigrationPackage> NativePackage()
        {
            NativeAcquires++;
            if (ReturnExisting is not null) return ReturnExisting;
            var package = await Package(PortableMigrationProvider.Adapanel, "native", WrongNativeScope ? "site-bbbbbbbbbbbbbbbbbbbbbbbb" : SiteId);
            if (TamperPackage) File.WriteAllBytes(Path.Combine(Root, "migration-packages", package.Id.ToString("N"), "files", "site", "index.txt"), [99]);
            if (CancelAfterPackage) await CancelService!.CancelAsync(Owner, CancelJob);
            return package;
        }
        private Task<LiveMigrationDiscovery> SshInventory(LiveMigrationConnection connection)
        {
            SshDiscoveries++;
            LastDiscoveryPin = connection.AcceptedSshFingerprint;
            if (RevokeAfterDiscovery) Active = false;
            if (DiscoveryFailure is not null) return Task.FromException<LiveMigrationDiscovery>(DiscoveryFailure);
            if (DiscoveryOverride is not null && (!TrustUntilAccepted || LastDiscoveryPin != DiscoveryOverride.HostFingerprint)) return Task.FromResult(DiscoveryOverride);
            return Task.FromResult(new LiveMigrationDiscovery { Supported = true, Units = (connection.Provider == "cpanel" ? new[] { "alice", "bob", "carol" } : new[] { "example.test" })
                .Select(name => new LiveMigrationSourceUnit { Provider = connection.Provider, Id = connection.Provider + ":" + name, Name = name }).ToArray() });
        }
        private async Task<LiveMigrationAcquireResult> Acquire(LiveMigrationConnection connection, string unit, string local, IProgress<LiveMigrationProgress>? progress, CancellationToken ct)
        {
            SshAcquires++;
            var remote = "/var/tmp/adapanel-migration-" + Guid.NewGuid().ToString("N"); var file = remote + "/source.tar";
            await File.WriteAllTextAsync(Path.Combine(local, "live-source-recovery-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new { RemoteWorkspace = remote, PlannedFiles = new[] { file } }), ct);
            SourceEntered.TrySetResult();
            if (BlockSource) await Task.Delay(Timeout.Infinite, ct);
            if (FailUnit == unit) return new() { ErrorCode = "SQL SENSITIVE_PASSWORD", ProcessCompletionConfirmed = true, RemoteWorkspace = remote, AttemptedRemoteFiles = [file] };
            var bytes = Encoding.UTF8.GetBytes(unit[(connection.Provider.Length + 1)..]);
            var path = Path.Combine(local, Guid.NewGuid().ToString("N") + ".raw"); await File.WriteAllBytesAsync(path, bytes, ct);
            progress?.Report(new() { Stage = "migration.live_downloading", SourceUnitId = unit, Bytes = bytes.Length }); ct.ThrowIfCancellationRequested();
            if (RevokeAfterSource) Active = false;
            return new() { ProcessCompletionConfirmed = !UnknownCompletion, RemoteCleanupComplete = !UnknownCompletion,
                RemoteWorkspace = remote, AttemptedRemoteFiles = [BadRecoveryPath ? remote + "/.." : file],
                Artifacts = [new() { ServerGeneratedPath = ArtifactFault == "path" ? Path.Combine(Root, "outside.raw") : path,
                    Provider = connection.Provider, SourceUnitId = ArtifactFault == "scope" ? connection.Provider + ":other" : unit,
                    Full = ArtifactFault != "not-full", Bytes = bytes.Length,
                    Sha256 = ArtifactFault == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)),
                    Format = connection.Provider == "cpanel" ? "cpanel-cpmove" : "plesk-backup-tar" }] };
        }
        private async Task<PortableMigrationPackage> Normalize(Stream source, bool plesk, CancellationToken ct)
        {
            Normalizations++; if (plesk) PleskNormalizations++;
            if (FailNormalization) throw new InvalidDataException("SENSITIVE_PASSWORD invalid SQL body");
            using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
            var unit = await reader.ReadToEndAsync(ct);
            return await Package(plesk ? PortableMigrationProvider.Plesk : PortableMigrationProvider.Cpanel, plesk && WrongPleskScope ? "other.test" : unit, SiteId);
        }
        private async Task<PortableMigrationPackage> Package(PortableMigrationProvider provider, string sourceName, string id)
        {
            var payload = Enumerable.Repeat((byte)42, PayloadBytes).ToArray();
            var manifest = new PortableMigrationManifest { PackageId = Guid.NewGuid(), Provider = provider, SourceName = sourceName,
                CreatedAtUtc = DateTimeOffset.UtcNow, Complete = !ManualIssue,
                Issues = ManualIssue ? [new() { Code = "fixture.mapping_required", ResourceId = id, Detail = "Explicit mapping required." }] : [],
                Sites = NativeDatabase ? [] : [new() { Id = id, Name = "example.test", ContentPath = "site", FileCount = 1 }],
                Databases = NativeDatabase ? [new() { Id = AdaPanelMigrationExporter.Id("db", "mysql:standalone"), Name = "standalone", Engine = "mysql", SiteIds = [], DumpPath = "site/index.txt", Format = "sql" }] : [],
                Payloads = [new() { Path = "site/index.txt", Size = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)) }] };
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var target = zip.CreateEntry("site/index.txt").Open()) target.Write(payload);
                using var json = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(json, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            output.Position = 0; return await Packages.UploadAsync(output);
        }
        private async Task Register(Guid owner, IReadOnlyList<Guid> ids)
        {
            foreach (var id in ids) await Packages.ReadManifestAsync(id);
            Registers++;
            await Jobs.UpdateAsync(state => { foreach (var id in ids) state.Packages.Add(new() { OwnerId = owner, PackageId = id, CreatedAtUtc = DateTimeOffset.UtcNow }); });
        }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private sealed class TestSecrets : ISecretProtector
    {
        private readonly Dictionary<string,string> values = [];
        internal int ProtectedCount;
        public string Protect(string plaintext) { ProtectedCount++; var key = "sealed:" + Guid.NewGuid().ToString("N"); values[key] = plaintext; return key; }
        public string Unprotect(string protectedValue) => values[protectedValue];
    }
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T,Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo,object?[]?,object?> Handler = null!; protected override object? Invoke(MethodInfo? method,object?[]? args) => Handler(method!,args); }
}
