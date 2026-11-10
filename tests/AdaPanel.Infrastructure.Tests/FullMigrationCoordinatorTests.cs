using System.Reflection;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FullMigrationCoordinatorTests
{
    [Fact]
    public async Task MultiAccountSelectionKeepsIdenticalSourceIdsDistinctAndChecksAllTargets()
    {
        using var area = new Area();
        var first = area.Package("first.example", "shared-id");
        var second = area.Package("second.example", "shared-id");
        await area.Register(first, second);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(first, second));
        Assert.True(plan.CanApply);
        Assert.Equal(2, plan.Items.Count);
        Assert.NotEqual(plan.Items[0].Id, plan.Items[1].Id);
        Assert.Equal(2, area.Site.CheckCalls);
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        Assert.True(await area.Service.RunNextAsync());
        var finished = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.True(finished.DataTransferred);
        Assert.Equal(2, area.Site.ApplyCalls);
        Assert.All(finished.Items, item => Assert.Equal("verified", item.State));
    }

    [Fact]
    public async Task DuplicateSourceTargetNamesBlockInsteadOfMergingTwoAccounts()
    {
        using var area = new Area();
        var first = area.Package("same.example");
        var second = area.Package("same.example");
        await area.Register(first, second);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(first, second));
        Assert.False(plan.CanApply);
        Assert.All(plan.Items, item => Assert.Contains("migration.target_name_duplicate", item.Blockers));
        await Assert.ThrowsAsync<AppException>(() => area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash));
        Assert.Equal(0, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task PackageOwnershipIsRequiredAndCannotBeReassigned()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var other = Guid.NewGuid();
        await Assert.ThrowsAsync<AppException>(() => area.Service.PlanAsync(other, area.Selection(package)));
        await Assert.ThrowsAsync<AppException>(() => area.Service.RegisterPackagesAsync(other, [package]));
        Assert.Empty(await area.Service.GetPackagesAsync(other));
        Assert.Single(await area.Service.GetPackagesAsync(area.Owner));
    }

    [Fact]
    public async Task CurrentAdministratorIsRecheckedBeforeWorkerMutation()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        area.Active = false;
        Assert.True(await area.Service.RunNextAsync());
        Assert.Equal(0, area.Site.ApplyCalls);
        var record = Assert.Single((await area.Jobs.ReadAsync()).Operations);
        Assert.Equal(operation.Id, record.Operation.Id);
        Assert.Equal("failed", record.Operation.State);
        Assert.False(record.Operation.DataTransferred);
    }

    [Fact]
    public async Task DuplicateStartReturnsConsumedOperationAndNeverReplaysFailedImport()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        area.Site.Result = new() { PartialStatePossible = true, ErrorCode = "migration.fixture_failed" };
        var first = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        var duplicate = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.True(await area.Service.RunNextAsync());
        duplicate = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        Assert.Equal("partial", duplicate.State);
        Assert.False(duplicate.DataTransferred);
        Assert.True(Assert.Single(duplicate.Items).PartialStatePossible);
        Assert.False(await area.Service.RunNextAsync());
        Assert.Equal(1, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task CrashCheckpointRecoversAsInterruptedWithoutReplayingAnyResource()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Jobs.UpdateAsync(state =>
        {
            state.Operations[0].Operation.State = "running";
            state.Operations[0].Operation.Items[0].State = "running";
        });
        var restarted = area.NewCoordinator();
        await restarted.RecoverInterruptedAsync();
        Assert.False(await restarted.RunNextAsync());
        var result = await restarted.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("interrupted", result.State);
        Assert.True(Assert.Single(result.Items).PartialStatePossible);
        Assert.Equal(0, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task CancelPendingDoesNotCreateTargets()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.CancelAsync(area.Owner, operation.Id);
        Assert.True(await area.Service.RunNextAsync());
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("cancelled", result.State);
        Assert.False(result.DataTransferred);
        Assert.Equal(0, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task PreparedCredentialsPersistProtectedBeforeMutationAndNeverAppearInStatus()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        const string password = "fixture-only-secret-DoNotExpose";
        area.Site.Credentials = [new() { Kind = "fixture", UserName = "new-user", Password = password }];
        area.Site.BeforeApply = async () =>
        {
            var state = await area.Jobs.ReadAsync();
            Assert.Equal("running", Assert.Single(state.Operations).Operation.Items[0].State);
            Assert.NotEmpty(state.Operations[0].ProtectedCredentials);
            var onDisk = await File.ReadAllTextAsync(Path.Combine(area.Root, "migration-private", "jobs.json"));
            Assert.DoesNotContain(password, onDisk);
            Assert.Contains(password, area.Secret.Unprotect(state.Operations[0].ProtectedCredentials));
        };
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.RunNextAsync();
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.DoesNotContain(password, JsonSerializer.Serialize(result));
        Assert.True(result.CredentialsAvailable);
        Assert.Equal(password, Assert.Single(await area.Service.GetCredentialsAsync(area.Owner, operation.Id)).Password);
        await Assert.ThrowsAsync<AppException>(() => area.Service.GetCredentialsAsync(Guid.NewGuid(), operation.Id));
    }

    [Fact]
    public async Task FailedSitePreventsRelatedMailButIndependentDatabaseCanComplete()
    {
        using var area = new Area();
        var package = area.Package("one.example", mail: true, database: true);
        await area.Register(package);
        var selection = area.Selection(package);
        selection.IncludeMail = true;
        selection.AllDatabases = true;
        selection.AcceptNewCredentials = true;
        var plan = await area.Service.PlanAsync(area.Owner, selection);
        Assert.True(plan.CanApply);
        area.Site.Result = new() { ErrorCode = "migration.site_fixture_failed" };
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.RunNextAsync();
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("partial", result.State);
        Assert.Equal(1, area.Database.ApplyCalls);
        Assert.Equal(0, area.Mail.ApplyCalls);
        Assert.Equal("not_started", Assert.Single(result.Items, item => item.Kind == "mail").State);
    }

    [Fact]
    public async Task TargetIsRecheckedAfterPlanAndBeforeMutation()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        area.Site.CheckResult.Blockers.Add("migration.target_exists");
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.RunNextAsync();
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("blocked", Assert.Single(result.Items).State);
        Assert.Equal(0, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task ChangedImmutableManifestBlocksExecution()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        area.Packages.Manifests[package].Sites.Add(new() { Id = "unexpected", Name = "changed.example" });
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.RunNextAsync();
        Assert.Equal("failed", (await area.Service.GetOperationAsync(area.Owner, operation.Id)).State);
        Assert.Equal(0, area.Site.ApplyCalls);
    }

    [Fact]
    public async Task UnexpectedNativeErrorTextNeverEntersDurableStatus()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        area.Site.BeforeApply = () => throw new InvalidOperationException("native password=secret-fixture-value");
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        await area.Service.RunNextAsync();
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("migration.component_failed", Assert.Single(result.Items).ErrorCode);
        Assert.DoesNotContain("secret-fixture-value", await File.ReadAllTextAsync(Path.Combine(area.Root, "migration-private", "jobs.json")));
    }

    [Theory]
    [InlineData("source", "cpanel.archive_version_malformed")]
    [InlineData("sites", "cpanel.domain_inventory_malformed")]
    [InlineData("site", "fixture.site_missing")]
    public async Task IncompleteSourceCannotBecomeApplicableThroughNormalization(string resource, string code)
    {
        using var area = new Area();
        var package = area.Package("one.example");
        area.Packages.Manifests[package].Issues.Add(new() { ResourceId = resource, Code = code, Detail = "Fixture incomplete source" });
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        Assert.False(plan.CanApply);
        Assert.Contains(code, plan.Blockers.Concat(plan.Items.SelectMany(item => item.Blockers)));
    }

    [Theory]
    [InlineData("cpanel.task.schedule_unknown")]
    [InlineData("cpanel.task.command_unsupported")]
    [InlineData("plesk.task.command_mapping_required")]
    public async Task ActualProducerTaskIssuesAreResolvedOnlyByExplicitMapping(string code)
    {
        using var area = new Area();
        var package = area.Package("one.example");
        var manifest = area.Packages.Manifests[package];
        manifest.Tasks.Add(new() { Id = "task", Name = "source-cron", SiteIds = ["site"], ScheduleUnknown = true });
        manifest.Issues.Add(new() { Code = code, ResourceId = "task", Detail = "Fixture needs task mapping" });
        await area.Register(package);
        var selection = area.Selection(package);
        selection.IncludeTasks = true;
        var blocked = await area.Service.PlanAsync(area.Owner, selection);
        Assert.Contains(code, Assert.Single(blocked.Items, item => item.Kind == "task").Blockers);
        selection.Mappings.Add(new() { ResourceId = MigrationResourceKey.Create(package, "task"), TargetName = "source-cron", TaskMappingConfirmed = true });
        var mapped = await area.Service.PlanAsync(area.Owner, selection);
        var task = Assert.Single(mapped.Items, item => item.Kind == "task");
        Assert.DoesNotContain(code, task.Blockers);
        Assert.Contains(code, task.Warnings);
    }

    [Theory]
    [InlineData("cpanel.mail.quota_unknown")]
    [InlineData("cpanel.mail.active_state_unknown")]
    [InlineData("cpanel.mail.archive_missing")]
    public async Task MailboxIssueKeyedByEmailCannotDisappearFromSelectedDomain(string code)
    {
        using var area = new Area();
        var package = area.Package("one.example", mail: true);
        var manifest = area.Packages.Manifests[package];
        manifest.MailDomains[0].Accounts.Add(new() { Email = "info@one.example" });
        manifest.Issues.Add(new() { Code = code, ResourceId = "info@one.example", Detail = "Fixture mailbox issue" });
        await area.Register(package);
        var selection = area.Selection(package);
        selection.IncludeMail = true;
        selection.AcceptNewCredentials = true;
        var plan = await area.Service.PlanAsync(area.Owner, selection);
        Assert.False(plan.CanApply);
        Assert.Contains(code, Assert.Single(plan.Items, item => item.Kind == "mail").Blockers);
        selection.IncludeMail = false;
        Assert.True((await area.Service.PlanAsync(area.Owner, selection)).CanApply);
    }

    [Fact]
    public async Task ExplicitUnknownMailboxFallbackDoesNotHideMissingMessagePayload()
    {
        using var area = new Area();
        var package = area.Package("one.example", mail: true);
        var manifest = area.Packages.Manifests[package];
        manifest.MailDomains[0].Accounts.Add(new() { Email = "info@one.example" });
        foreach (var code in new[] { "plesk.mail.quota_unknown", "plesk.mail.active_state_unknown", "plesk.mail.new_password_required", "cpanel.mail.archive_missing" })
            manifest.Issues.Add(new() { Code = code, ResourceId = "info@one.example", Detail = "Fixture mailbox issue" });
        await area.Register(package);
        var selection = area.Selection(package);
        selection.IncludeMail = true;
        selection.AcceptNewCredentials = true;
        selection.Mappings.Add(new() { ResourceId = MigrationResourceKey.Create(package, "mail"), TargetName = "one.example", UnknownMailboxQuotaMb = 0, UnknownMailboxActive = false });
        var plan = await area.Service.PlanAsync(area.Owner, selection);
        var mail = Assert.Single(plan.Items, item => item.Kind == "mail");
        Assert.Equal(["cpanel.mail.archive_missing"], mail.Blockers);
        Assert.Equal(3, mail.Warnings.Count);
    }

    [Fact]
    public async Task UnselectedTaskIssueAttachedToSiteDoesNotBlockFileOnlyMigration()
    {
        using var area = new Area();
        var package = area.Package("one.example");
        area.Packages.Manifests[package].Issues.Add(new() { Code = "plesk.task.shape_unsupported", ResourceId = "site", Detail = "Unknown cron descriptor" });
        await area.Register(package);
        Assert.True((await area.Service.PlanAsync(area.Owner, area.Selection(package))).CanApply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReturnedByLastAdapterPreservesEvidenceAndFinalCancelledState(bool adapterClaimsVerified)
    {
        using var area = new Area();
        var package = area.Package("one.example");
        await area.Register(package);
        var plan = await area.Service.PlanAsync(area.Owner, area.Selection(package));
        var operation = await area.Service.StartAsync(area.Owner, plan.Id, plan.PlanHash);
        area.Site.BeforeApply = async () => await area.NewCoordinator().CancelAsync(area.Owner, operation.Id);
        area.Site.Result = new() { Verified = adapterClaimsVerified, PartialStatePossible = !adapterClaimsVerified,
            CreatedResources = ["fixture-created-resource"], ErrorCode = adapterClaimsVerified ? "" : "migration.component_cancelled" };
        Assert.True(await area.Service.RunNextAsync());
        var result = await area.Service.GetOperationAsync(area.Owner, operation.Id);
        Assert.Equal("cancelled", result.State);
        Assert.True(result.CancellationRequested);
        Assert.False(result.DataTransferred);
        Assert.Contains("fixture-created-resource", Assert.Single(result.Items).CreatedResources);
        Assert.Equal(1, area.Site.ApplyCalls);
        Assert.False(await area.Service.RunNextAsync());
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ada-full-migration-" + Guid.NewGuid().ToString("N"));
        public Guid Owner { get; } = Guid.NewGuid();
        public bool Active = true;
        public FakePackages Packages { get; } = new();
        public FakeAdapter Site { get; } = new("site");
        public FakeAdapter Database { get; } = new("database");
        public FakeAdapter Mail { get; } = new("mail");
        public FakeSecret Secret { get; } = new();
        public MigrationJobStore Jobs { get; }
        public FullMigrationCoordinator Service { get; }
        private readonly IAuthService auth;
        public Area()
        {
            PortableMigrationStorage.CreatePrivateDirectory(Root);
            var files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            Jobs = new(files);
            auth = Stub<IAuthService>((method, args) => method.Name == nameof(IAuthService.GetUserByIdAsync)
                ? Task.FromResult(new CurrentUserDto { Id = (Guid)args![0]!, IsActive = Active, Role = "Administrator" })
                : throw new InvalidOperationException("Unexpected auth call"));
            Service = NewCoordinator();
        }
        public FullMigrationCoordinator NewCoordinator() => new(Jobs, Packages, [Site, Database, Mail], auth, Secret);
        public Guid Package(string domain, string siteId = "site", bool mail = false, bool database = false)
        {
            var id = Guid.NewGuid();
            Packages.Manifests.Add(id, new()
            {
                PackageId = Guid.NewGuid(), Provider = PortableMigrationProvider.Adapanel, Complete = true,
                Sites = [new() { Id = siteId, Name = domain, RuntimeProfile = "static" }],
                MailDomains = mail ? [new() { Id = "mail", Name = domain, SiteIds = [siteId] }] : [],
                Databases = database ? [new() { Id = "db", Name = "independent", Engine = "mariadb" }] : []
            });
            return id;
        }
        public MigrationPlanRequest Selection(params Guid[] ids) => new() { PackageIds = ids, AllSites = true, IncludeMail = false, IncludeDns = false, TargetBasePath = Path.Combine(Root, "targets") };
        public Task Register(params Guid[] ids) => Service.RegisterPackagesAsync(Owner, ids);
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private sealed class FakePackages : IMigrationPackageStore
    {
        public Dictionary<Guid, PortableMigrationManifest> Manifests { get; } = [];
        public Task<PortableMigrationManifest> ReadManifestAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Manifests[id]);
        public Task<PortableMigrationPackage> UploadAsync(Stream zip, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenPayloadAsync(Guid id, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FakeAdapter(string kind) : IMigrationTargetAdapter
    {
        public string Kind => kind;
        public int CheckCalls, ApplyCalls;
        public MigrationTargetCheck CheckResult { get; } = new();
        public MigrationComponentResult Result = new() { Verified = true, Evidence = ["fixture verified"] };
        public IReadOnlyList<MigrationCreatedCredential> Credentials = [];
        public Func<Task>? BeforeApply;
        public Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest, MigrationPlanRequest selection, CancellationToken ct)
        { CheckCalls++; return Task.FromResult(CheckResult); }
        public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest) => Credentials;
        public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest, MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken ct)
        { ApplyCalls++; if (BeforeApply is not null) await BeforeApply(); return Result; }
    }
    // Deliberately test-only encoding: production resolves the existing machine-bound protector.
    private sealed class FakeSecret : ISecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[10..]));
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
