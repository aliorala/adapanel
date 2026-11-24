using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationSiteTargetTests
{
    [Fact]
    public async Task RealFilesAreCopiedAndVerifiedBeforeStoppedSiteCreation()
    {
        using var area = new Area();
        var sourceBytes = area.Payloads.Bytes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.True(result.Verified, result.ErrorCode);
        Assert.Equal("fixture-secret-in-dotenv", await File.ReadAllTextAsync(Path.Combine(area.TargetPath, ".env")));
        Assert.Equal("fixture web page", await File.ReadAllTextAsync(Path.Combine(area.TargetPath, "public", "index.html")));
        Assert.True(Directory.Exists(Path.Combine(area.TargetPath, "empty")));
        Assert.Equal(1, area.ProvisionCalls);
        Assert.NotNull(area.Request);
        Assert.False(area.Request.AutoStart);
        Assert.True(area.Request.MigrationFreshOnly);
        Assert.False(area.Request.InstallRuntimeIfMissing);
        Assert.False(area.Request.ProvisionDefaultServices);
        Assert.Equal(Path.Combine(area.TargetPath, "public"), area.Request.PhysicalPath);
        Assert.Equal(new[] { "one.example", "www.one.example" }, area.Request.MigrationHostNames);
        Assert.False(Directory.Exists(area.Stage));
        foreach (var (path, bytes) in sourceBytes) Assert.Equal(bytes, area.Payloads.Bytes[path]);
    }

    [Fact]
    public async Task ExistingTargetDirectoryIsNotMergedOrDeleted()
    {
        using var area = new Area();
        Directory.CreateDirectory(area.TargetPath);
        await File.WriteAllTextAsync(Path.Combine(area.TargetPath, "existing.txt"), "keep");
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.Equal("migration.target_path_exists", result.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(area.TargetPath, "existing.txt")));
        Assert.Equal(0, area.Payloads.Reads);
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("count")]
    public async Task IncompleteOrChangedPayloadCannotCreateSite(string corruption)
    {
        using var area = new Area();
        if (corruption == "bytes") area.Payloads.Bytes["sites/one/files/.env"] = Encoding.UTF8.GetBytes("changed");
        else area.Manifest.Payloads.RemoveAt(0);
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.Equal(0, area.ProvisionCalls);
        Assert.False(Directory.Exists(area.TargetPath));
        Assert.False(Directory.Exists(area.Stage));
    }

    [Fact]
    public async Task TargetCreatedWhileFilesCopyIsPreservedWithoutReplacement()
    {
        using var area = new Area();
        area.Payloads.OnRead = () =>
        {
            Directory.CreateDirectory(area.TargetPath);
            File.WriteAllText(Path.Combine(area.TargetPath, "foreign.txt"), "another operation");
        };
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.Equal("migration.target_path_exists", result.ErrorCode);
        Assert.Equal("another operation", await File.ReadAllTextAsync(Path.Combine(area.TargetPath, "foreign.txt")));
        Assert.False(File.Exists(Path.Combine(area.TargetPath, ".env")));
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Fact]
    public async Task NativeProvisioningFailureRetainsCopiedTargetAndReportsPartialState()
    {
        using var area = new Area();
        area.FailProvision = true;
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.True(result.PartialStatePossible);
        Assert.Equal("migration.fixture_provision_failed", result.ErrorCode);
        Assert.Contains("directory:" + area.TargetPath, result.CreatedResources);
        Assert.True(File.Exists(Path.Combine(area.TargetPath, ".env")));
        Assert.False(Directory.Exists(area.Stage));
        var second = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.Equal("migration.target_path_exists", second.ErrorCode);
        Assert.Equal(1, area.ProvisionCalls);
    }

    [Theory]
    [InlineData("permissions")]
    [InlineData("started")]
    [InlineData("alias")]
    [InlineData("app-root")]
    [InlineData("app-acl")]
    public async Task UnverifiedNativeStateIsNotReportedSuccessful(string failure)
    {
        using var area = new Area();
        area.VerificationFailure = failure;
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.True(result.PartialStatePossible);
        Assert.NotEmpty(result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(area.TargetPath, ".env")));
    }

    [Fact]
    public async Task AConflictingHostIsDetectedBeforePayloadReads()
    {
        using var area = new Area();
        area.Current = new() { Name = "other.example", PhysicalPath = Path.Combine(area.Root, "other"), Bindings = [new() { HostName = "www.one.example", Port = 80, Protocol = "http" }] };
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.Equal("migration.target_binding_conflict", result.ErrorCode);
        Assert.Equal(0, area.Payloads.Reads);
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Fact]
    public async Task HiddenInfrastructureRootIsProtectedEvenWhenCustomerSiteListIsEmpty()
    {
        using var area = new Area();
        area.Current = new() { Name = "AdaPanel.WebDb", PhysicalPath = area.Selection.TargetBasePath };
        var result = await area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default);
        Assert.Contains("migration.target_path_overlaps_site", result.Blockers);
        Assert.Equal(0, area.Payloads.Reads);
    }

    [Fact]
    public async Task SiteNameIsNotInventedAsAnAdditionalHostname()
    {
        using var area = new Area(siteName: "web-one.example");
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.True(result.Verified, result.ErrorCode);
        Assert.DoesNotContain("web-one.example", area.Request!.MigrationHostNames);
        Assert.Equal("one.example", area.Request.HostName);
    }

    [Fact]
    public async Task TargetOutsideSelectedBasePathIsRejectedBeforeReadingAnyPayload()
    {
        using var area = new Area();
        area.Item.Target.TargetPath = Path.Combine(area.Root, "outside");
        var error = await Assert.ThrowsAsync<AppException>(() => area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default));
        Assert.Equal("migration.target_path_invalid", error.MessageKey);
        Assert.Equal(0, area.Payloads.Reads);
        Assert.False(Directory.Exists(area.Item.Target.TargetPath));
    }

    [Fact]
    public async Task GenericDocumentRootSeparationIsBlockedBeforeAnyFilesArePublished()
    {
        using var area = new Area();
        area.Manifest.Payloads.RemoveAll(payload => payload.Path.EndsWith("/artisan", StringComparison.Ordinal));
        var check = await area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default);
        Assert.Contains("migration.application_root_mapping_required", check.Blockers);
        Assert.Equal(0, area.ProvisionCalls);
        Assert.False(Directory.Exists(area.TargetPath));
    }

    [Theory]
    [InlineData("*.example")]
    [InlineData("bad host.example")]
    [InlineData("")]
    public async Task UnsupportedSourceBindingCannotBeSilentlyDropped(string host)
    {
        using var area = new Area();
        area.Manifest.Sites[0].HostNames.Add(host);
        var check = await area.Service.CheckAsync(area.Item, area.Manifest, area.Selection, default);
        Assert.Contains("migration.source_binding_mapping_required", check.Blockers);
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("panel-data")]
    public async Task UserSelectedTargetCannotEnterSystemOrPanelDataDirectories(string location)
    {
        using var area = new Area();
        var forbidden = location == "windows" ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : Path.Combine(area.Root, "panel-data");
        area.Selection.TargetBasePath = forbidden;
        area.Item.Target.TargetPath = Path.Combine(forbidden, "migration-should-not-create-" + Guid.NewGuid().ToString("N"));
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.Equal("migration.target_path_forbidden", result.ErrorCode);
        Assert.Equal(0, area.Payloads.Reads);
        Assert.Equal(0, area.ProvisionCalls);
        Assert.False(Directory.Exists(area.Item.Target.TargetPath));
    }

    [Fact]
    public async Task ExistingHttpsOnlyHostPreventsConflictingHttpMigration()
    {
        using var area = new Area();
        area.Current = new() { Name = "existing-other-name", PhysicalPath = Path.Combine(area.Root, "existing"),
            Bindings = [new() { Protocol = "https", Port = 443, HostName = "one.example" }] };
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.Equal("migration.target_binding_conflict", result.ErrorCode);
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Fact]
    public async Task StageCannotLiveInsideAnotherApplicationsPrivateRoot()
    {
        using var area = new Area();
        area.Current = new() { Name = "internal", PhysicalPath = Path.Combine(area.Root, "elsewhere", "public"), ApplicationRootPath = area.Stage };
        await Assert.ThrowsAnyAsync<Exception>(() => area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default));
        Assert.False(Directory.Exists(area.Stage));
        Assert.Equal(0, area.Payloads.Reads);
        Assert.Equal(0, area.ProvisionCalls);
    }

    [Fact]
    public async Task UnmeasuredNativeRootsCannotBeTreatedAsAnEmptyProtectionList()
    {
        using var area = new Area();
        area.Current = new() { Name = "internal" };
        var result = await area.Service.ApplyAsync(area.Item, area.Manifest, area.Selection, [], default);
        Assert.False(result.Verified);
        Assert.Equal("migration.private_storage_unverified", result.ErrorCode);
        Assert.False(Directory.Exists(area.Stage));
        Assert.Equal(0, area.Payloads.Reads);
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ada-target-site-" + Guid.NewGuid().ToString("N"));
        public string TargetPath => Path.Combine(Root, "sites", "one.example");
        public string Stage => Path.Combine(Root, "private-stage");
        public FakePayloads Payloads { get; } = new();
        public PortableMigrationManifest Manifest { get; }
        public MigrationPlanItemDto Item { get; }
        public MigrationPlanRequest Selection { get; }
        public MigrationSiteTarget Service { get; }
        public SiteDto? Current;
        public CreateSiteRequest? Request;
        public int ProvisionCalls;
        public bool FailProvision;
        public string VerificationFailure = "";
        public Area(string siteName = "one.example")
        {
            PortableMigrationStorage.CreatePrivateDirectory(Root);
            var gates = new ConcurrencyLock();
            Payloads.Bytes.Add("sites/one/files/.env", Encoding.UTF8.GetBytes("fixture-secret-in-dotenv"));
            Payloads.Bytes.Add("sites/one/files/public/index.html", Encoding.UTF8.GetBytes("fixture web page"));
            Payloads.Bytes.Add("sites/one/files/artisan", Encoding.UTF8.GetBytes("fixture artisan"));
            Payloads.Bytes.Add("sites/one/files/composer.json", Encoding.UTF8.GetBytes("{}"));
            Manifest = new()
            {
                PackageId = Guid.NewGuid(), Provider = PortableMigrationProvider.Adapanel, Complete = true,
                Sites = [new() { Id = "site", Name = siteName, ContentPath = "sites/one/files", DocumentRootRelativePath = "public",
                    FileCount = 4, EmptyDirectories = ["empty"], HostNames = ["one.example", "www.one.example"] }],
                Payloads = Payloads.Bytes.Select(pair => new PortableMigrationPayload
                { Path = pair.Key, Size = pair.Value.Length, Sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)) }).ToList()
            };
            Item = new() { Id = "fixture-site", PackageId = Guid.NewGuid(), Kind = "site", SourceId = "site", SourceName = siteName,
                Target = new() { TargetName = siteName, TargetPath = TargetPath, RuntimeProfile = "static" } };
            Selection = new() { TargetBasePath = Path.Combine(Root, "sites") };
            var sites = Stub<IIisSiteService>((method, args) => method.Name switch
            {
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([]),
                nameof(IIisSiteService.GetMigrationTargetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>(Current is null ? [] : [Current]),
                nameof(IIisSiteService.GetSiteAsync) => Task.FromResult(Current ?? throw new AppException("sites.not_found", 404, 404)),
                _ => throw new InvalidOperationException("Unexpected IIS operation " + method.Name)
            });
            var provisioning = Stub<ISiteProvisioningService>((method, args) => method.Name switch
            {
                nameof(ISiteProvisioningService.CreateAndProvisionAsync) => Provision((CreateSiteRequest)args![0]!, (CancellationToken)args[3]!),
                nameof(ISiteProvisioningService.RepairPermissionsAsync) => Permission(args!),
                _ => throw new InvalidOperationException("Unexpected provisioning operation " + method.Name)
            });
            Service = new(sites, provisioning, Payloads, gates, Microsoft.Extensions.Options.Options.Create(new AdaPanel.Infrastructure.Options.PanelRuntimeOptions
                { DataRoot = Path.Combine(Root, "panel-data") }))
            {
                StagePathFactory = _ => Stage,
                ProbeApplicationRoot = (path, principal) =>
                {
                    Assert.Equal(TargetPath, path);
                    Assert.Equal("IIS AppPool\\fixture-pool", principal);
                    return new(VerificationFailure == "app-acl" ? FolderWriteAccessProbe.Access.NotGranted : FolderWriteAccessProbe.Access.Granted, "Fixture ACL measurement");
                }
            };
            async Task<SiteDto> Provision(CreateSiteRequest request, CancellationToken token)
            {
                ProvisionCalls++;
                Request = request;
                Assert.True(File.Exists(Path.Combine(TargetPath, ".env")));
                if (FailProvision) throw new AppException("migration.fixture_provision_failed", 409, 409);
                return await SiteMutationGuard.RunMutationAsync(gates, request.Name, true, () =>
                {
                    Current = new()
                    {
                        Id = 42, Name = request.Name, State = VerificationFailure == "started" ? "Started" : "Stopped",
                        AutoStart = request.AutoStart, ApplicationPoolName = "fixture-pool", PhysicalPath = request.PhysicalPath,
                        ApplicationRootPath = VerificationFailure == "app-root" ? request.PhysicalPath : TargetPath,
                        Bindings = request.MigrationHostNames.Where(host => VerificationFailure != "alias" || !host.StartsWith("www.", StringComparison.Ordinal))
                            .Select(host => new SiteBindingDto { HostName = host, Protocol = "http", Port = 80 }).ToList()
                    };
                    return Task.FromResult(Current);
                }, token);
            }
            async Task<SitePermissionRepairResultDto> Permission(object?[] args)
            {
                if (args.Length == 3) await ((Func<SiteDto, CancellationToken, Task>)args[1]!)(Current!, (CancellationToken)args[2]!);
                return new() { Site = Current!, State = VerificationFailure == "permissions" ? SitePermissionRepairResultDto.StateUnmeasured : SitePermissionRepairResultDto.StateGranted };
            }
        }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private sealed class FakePayloads : IMigrationPackageStore
    {
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public Action? OnRead;
        public int Reads;
        public Task<Stream> OpenPayloadAsync(Guid id, string payloadPath, CancellationToken token = default)
        { Reads++; OnRead?.Invoke(); return Task.FromResult<Stream>(new MemoryStream(Bytes[payloadPath], writable: false)); }
        public Task<PortableMigrationManifest> ReadManifestAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortableMigrationPackage> UploadAsync(Stream input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
