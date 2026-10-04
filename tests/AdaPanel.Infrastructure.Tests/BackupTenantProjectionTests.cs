using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupTenantProjectionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Customer", "list")]
    [InlineData("Customer", "site")]
    [InlineData("Customer", "subscription")]
    [InlineData("Reseller", "list")]
    [InlineData("Reseller", "site")]
    [InlineData("Reseller", "subscription")]
    public async Task ScopedResponsesHideServerLocationsAndSecretsButKeepBackupEvidence(string role, string endpoint)
    {
        using var f = new Fixture(role, endpoint == "subscription");
        var actual = await f.ReadRecord(endpoint);
        Assert.Equal(string.Empty, actual.Path);
        Assert.Equal(string.Empty, actual.PreservedPath);
        Assert.Equal(string.Empty, actual.RestoredPath);
        Assert.Equal("ftp://storage.example.test/tenant/archive.zip", actual.RemoteUri);
        Assert.Equal("created_with_errors", actual.Status);
        Assert.True(actual.Encrypted);
        Assert.True(actual.Compressed);
        Assert.Equal("chain-1", actual.ChainId);
        Assert.Equal("base-1", actual.BaseBackupId);
        Assert.Equal(1, actual.ChainIndex);
        Assert.Equal("sound", actual.IntegrityState);
        Assert.Equal("sha256:fixture", actual.ContentHash);
        Assert.True(actual.OffsiteCopyLost);
        Assert.True(actual.RemoteDeleteFailed);
        Assert.Equal("Foo", Assert.Single(actual.IncludedDatabases!).DatabaseName);
        Assert.Equal("site.example", Assert.Single(actual.IncludedComponents!).Source);
        Assert.Equal(4, actual.Warnings.Count);
        Assert.Equal("backup.database_dump_empty", actual.Warnings[0].MessageKey);
        Assert.Equal("postgresql/Foo", actual.Warnings[0].Target);
        Assert.Equal(string.Empty, actual.Warnings[1].Target);
        Assert.Equal("files/uploads/photo.png", actual.Warnings[2].Target);
        Assert.Equal("https://storage.example.test/archive.zip", actual.Warnings[3].Target);
        Assert.All(actual.Warnings, warning => Assert.Equal(string.Empty, warning.Detail));
        var json = JsonSerializer.Serialize(actual, WebJson);
        Assert.DoesNotContain("private-server-root", json);
        Assert.DoesNotContain("remote-secret", json);
        Assert.DoesNotContain("signed-secret", json);
        Assert.DoesNotContain("detail-secret", json);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task ScopedGetDoesNotChangeSharedServiceObjectsOrPersistedMetadata(string role)
    {
        using var f = new Fixture(role);
        var original = JsonSerializer.Serialize(f.Record, WebJson);
        var diskBefore = await File.ReadAllTextAsync(f.Store.ResolvePath("config/backups.json"));
        var scoped = await f.ReadRecord("list");
        Assert.NotSame(f.Record, scoped);
        Assert.NotSame(f.Record.Warnings, scoped.Warnings);
        Assert.Equal(original, JsonSerializer.Serialize(f.Record, WebJson));
        Assert.Equal(diskBefore, await File.ReadAllTextAsync(f.Store.ResolvePath("config/backups.json")));
        f.UseRole("Administrator");
        Assert.Equal(original, JsonSerializer.Serialize(await f.ReadRecord("list"), WebJson));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("site")]
    [InlineData("subscription")]
    [InlineData("retry")]
    public async Task AdministratorKeepsDiagnosticLocationsAndCompleteResult(string endpoint)
    {
        using var f = new Fixture("Administrator", endpoint == "subscription");
        var original = JsonSerializer.Serialize(f.Record, WebJson);
        Assert.Equal(original, JsonSerializer.Serialize(await f.ReadRecord(endpoint), WebJson));
    }

    [Theory]
    [InlineData(@"C:\private-server-root\site")]
    [InlineData(@"\\server\private-server-root\site")]
    [InlineData("/private-server-root/site")]
    [InlineData(@"\private-server-root\site")]
    [InlineData(@"C:private-server-root\site")]
    [InlineData("file:///C:/private-server-root/site")]
    public async Task WarningFilesystemTargetsAreHiddenWithoutDiscardingFailure(string target)
    {
        using var f = new Fixture("Customer");
        f.Record.Warnings = [new() { MessageKey = "files.scan_truncated_entries", Target = target, Detail = "diagnostic" }];
        var actual = await f.ReadRecord("list");
        var warning = Assert.Single(actual.Warnings);
        Assert.Equal("files.scan_truncated_entries", warning.MessageKey);
        Assert.Equal(string.Empty, warning.Target);
        Assert.Equal(string.Empty, warning.Detail);
        Assert.Equal("created_with_errors", actual.Status);
    }

    [Theory]
    [InlineData("https://operator:remote-secret@storage.example.test/archive.zip?sig=signed-secret#signed-secret", "https://storage.example.test/archive.zip")]
    [InlineData("ftp://storage.example.test/archive.zip", "ftp://storage.example.test/archive.zip")]
    [InlineData("ftp://operator:remote-secret@storage.example.test/archive.zip?sig=signed-secret#signed-secret", "ftp://storage.example.test/archive.zip")]
    [InlineData("ftp://storage.example.test/archive.zip#signed-secret", "ftp://storage.example.test/archive.zip")]
    [InlineData("ftp://storage.example.test/archive%3Fname.zip?sig=signed-secret", "ftp://storage.example.test/archive%3Fname.zip")]
    [InlineData("https://storage.example.test/archive%23name.zip#signed-secret", "https://storage.example.test/archive%23name.zip")]
    [InlineData("dropbox:/tenant/archive.zip", "dropbox:/tenant/archive.zip")]
    [InlineData("onedrive:/tenant/archive.zip", "onedrive:/tenant/archive.zip")]
    [InlineData("dropbox:/tenant/archive.zip?token=signed-secret#signed-secret", "dropbox:/tenant/archive.zip")]
    [InlineData("onedrive:/tenant/archive.zip?token=signed-secret#signed-secret", "onedrive:/tenant/archive.zip")]
    [InlineData("not-a-uri password=remote-secret?sig=signed-secret", "")]
    [InlineData("file:///C:/private-server-root/archive.zip", "")]
    public async Task LegacyRemoteLocationsAreSanitizedOrOmittedRatherThanReturnedRaw(string remote, string expected)
    {
        using var f = new Fixture("Customer");
        f.Record.RemoteUri = remote;
        Assert.Equal(expected, (await f.ReadRecord("list")).RemoteUri);
        Assert.Equal(remote, f.Record.RemoteUri);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task ProjectionRetainsVisibilityBoundariesForSitesAndSubscriptions(string role)
    {
        using var f = new Fixture(role);
        f.Records.AddRange([
            new() { Id = "own-subscription", Type = "subscription", SubscriptionUserId = Fixture.Owner.ToString("N") },
            new() { Id = "foreign-site", Type = "site", Source = "other.example" },
            new() { Id = "foreign-subscription", Type = "subscription", SubscriptionUserId = Guid.NewGuid().ToString("N") },
            new() { Id = "panel", Type = "panel", Source = "panel" }
        ]);
        var result = Data(await f.Controller.GetBackupsAsync(CancellationToken.None));
        Assert.Equal(["backup-1", "own-subscription"], result.Select(item => item.Id).ToArray());
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Administrator")]
    public async Task BrowsingKeepsArchiveRelativePathsUsable(string role)
    {
        using var f = new Fixture(role);
        var actual = Data(await f.Controller.GetEntriesAsync(f.Record.Id, "files/", CancellationToken.None));
        Assert.Equal("files/", actual.Prefix);
        Assert.Equal("files/uploads/photo.png", Assert.Single(actual.Entries).Path);
        Assert.Equal(1, f.EntryReads);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task BrowsingAnotherSitesBackupStillFailsBeforeReadingArchive(string role)
    {
        using var f = new Fixture(role);
        f.Record.Source = "other.example";
        var error = await Assert.ThrowsAsync<AppException>(() => f.Controller.GetEntriesAsync(f.Record.Id, "", CancellationToken.None));
        Assert.Equal("auth.site_forbidden", error.MessageKey);
        Assert.Equal(0, f.EntryReads);
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> response) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Owner = Guid.Parse("db758062-c49d-4e14-901c-a9c649deeb93");
        private static readonly Guid Reseller = Guid.Parse("0cb58ee1-ae3b-4464-b330-8aaec56e43a8");
        private readonly string root = Path.Combine(Path.GetTempPath(), $"adapanel-backup-projection-{Guid.NewGuid():N}");
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext database;
        private readonly ServiceProvider services;
        public BackupRecordDto Record { get; }
        public List<BackupRecordDto> Records { get; }
        public JsonFileStore Store { get; }
        public BackupController Controller { get; }
        public int EntryReads { get; private set; }

        public Fixture(string role, bool subscription = false)
        {
            connection.Open();
            database = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            database.Database.EnsureCreated();
            database.Users.AddRange(
                new PanelUser { Id = Owner, UserName = "customer", Email = "customer@example.test", PasswordHash = "fixture-hash", Role = UserRole.Customer, IsActive = true },
                new PanelUser { Id = Reseller, UserName = "reseller", Email = "reseller@example.test", PasswordHash = "fixture-hash", Role = UserRole.Reseller, IsActive = true });
            database.SaveChanges();
            services = new ServiceCollection().AddSingleton(database).BuildServiceProvider();
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Record = new BackupRecordDto
            {
                Id = "backup-1", Name = "site.zip.enc", Type = subscription ? "subscription" : "site",
                Source = subscription ? Owner.ToString("N") : "site.example", SubscriptionUserId = Owner.ToString("N"),
                Path = @"C:\private-server-root\backups\site.zip.enc", PreservedPath = @"C:\private-server-root\preserved",
                RestoredPath = @"C:\private-server-root\restored", Format = "zip", Status = "created_with_errors",
                Encrypted = true, Compressed = true, Size = 123, CreatedAtUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
                RemoteUri = "ftp://operator:remote-secret@storage.example.test/tenant/archive.zip?sig=signed-secret#signed-secret",
                TargetType = "ftp", ChainId = "chain-1", ChainIndex = 1, BackupMode = "incremental", BaseBackupId = "base-1",
                ContentHash = "sha256:fixture", IntegrityState = "sound", RemoteState = "missing", RemoteMissing = true,
                RemoteVerifiedUtc = DateTime.UtcNow, OffsiteCopyLost = true, RemoteDeleteFailed = true, ItemCount = 4,
                IncludedDatabases = [new() { Engine = "postgresql", DatabaseName = "Foo" }],
                IncludedComponents = [new() { Kind = "files", Source = "site.example" }],
                Warnings = [
                    new() { MessageKey = "backup.database_dump_empty", Target = "postgresql/Foo", Detail = "password=detail-secret C:\\private-server-root\\dump.sql" },
                    new() { MessageKey = "files.scan_truncated_entries", Target = @"C:\private-server-root\site", Detail = "scan=truncated" },
                    new() { MessageKey = "backup.invalid_entry_path", Target = "files/uploads/photo.png", Detail = "C:\\private-server-root\\site\\photo.png" },
                    new() { MessageKey = "backup.remote_delete_failed", Target = "https://operator:remote-secret@storage.example.test/archive.zip?sig=signed-secret", Detail = "remote response" }
                ]
            };
            Records = [Record];
            Store.WriteAsync("config/backups.json", Records).GetAwaiter().GetResult();
            var service = Fake<IBackupService>((method, args) => method.Name switch
            {
                "GetBackupsAsync" => Task.FromResult<IReadOnlyCollection<BackupRecordDto>>(Records),
                "BackupSiteAsync" or "BackupSubscriptionAsync" or "RetryRemoteUploadAsync" => Task.FromResult(Record),
                "GetEntriesAsync" => Entries((string)args[0]!, (string?)args[1]),
                _ => throw new NotSupportedException(method.Name)
            });
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetAssignedSitesAsync" or "GetResellerSitesAsync" => Task.FromResult<IReadOnlyCollection<string>>(["site.example"]),
                "GetOwnedUserIdsAsync" => Task.FromResult<IReadOnlyCollection<Guid>>([Owner]),
                "GetParentSubscriptionAsync" => Task.FromResult<Guid?>(null),
                "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(Owner),
                "GetOwnerAsync" => Task.FromResult<Guid?>(Reseller),
                "GetScopeAsync" => Task.FromResult(new CustomerScope(["site.example"], 0, 0, 0, 0, 0)),
                _ => throw new NotSupportedException(method.Name)
            });
            Controller = ActivatorUtilities.CreateInstance<BackupController>(services, service, scopes,
                Fake<IDatabaseOwnershipService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<ISubscriptionBackupScheduleService>((method, _) => throw new NotSupportedException(method.Name)),
                Fake<IRunningJobService>((method, _) => method.Name == "BeginAsync" ? Task.FromResult<IAsyncDisposable>(new Job()) : throw new NotSupportedException(method.Name)),
                Fake<IAuditService>((method, _) => method.Name == "WriteForSubscriptionAsync" ? Task.CompletedTask : throw new NotSupportedException(method.Name)));
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            UseRole(role);
        }

        public void UseRole(string role)
        {
            var id = role == "Reseller" ? Reseller : Owner;
            database.Users.Single(user => user.Id == id).Role = Enum.Parse<UserRole>(role);
            database.SaveChanges();
            Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)
            ], "Test", ClaimTypes.Name, ClaimTypes.Role));
        }

        public async Task<BackupRecordDto> ReadRecord(string endpoint) => endpoint switch
        {
            "list" => Assert.Single(Data(await Controller.GetBackupsAsync(CancellationToken.None))),
            "site" => Data(await Controller.BackupSiteAsync(new BackupSiteRequest { SiteName = "site.example" }, CancellationToken.None)),
            "subscription" => Data(await Controller.BackupSubscriptionAsync(new BackupSubscriptionRequest { UserId = Owner }, CancellationToken.None)),
            "retry" => Data(await Controller.RetryRemoteAsync(Record.Id, new RetryBackupRemoteRequest { RemoteUrl = "ftp://storage.example.test" }, CancellationToken.None)),
            _ => throw new NotSupportedException(endpoint)
        };

        private Task<BackupEntriesDto> Entries(string id, string? prefix)
        {
            EntryReads++;
            return Task.FromResult(new BackupEntriesDto { BackupId = id, Prefix = prefix ?? "", Entries = [new() { Name = "photo.png", Path = "files/uploads/photo.png", Size = 42 }] });
        }

        public void Dispose()
        {
            services.Dispose();
            database.Dispose();
            connection.Dispose();
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-backup-projection-"), absolute, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private sealed class Job : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Handler = handler;
        return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
