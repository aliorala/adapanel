using System.IO.Compression;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteBackupOwnershipTests
{
    [Fact]
    public async Task TheRealSiteProducerPersistsTheSubscriptionThatOwnedTheArchivedDatabase()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(F.Owner.ToString("N"), fixture.Record.SubscriptionUserId);
        Assert.Equal(F.Owner.ToString("N"), Assert.Single(await fixture.Backup.Persisted()).SubscriptionUserId);
        Assert.Equal(F.Owner, Assert.Single(await fixture.Backup.Ownership.GetAllAsync()).OwnerUserId);
        Assert.Contains("databases/mysql-private_ledger.sql", F.Entries(fixture.Record));
    }

    [Theory]
    [InlineData("Customer", "list")]
    [InlineData("Customer", "download")]
    [InlineData("Customer", "entries")]
    [InlineData("Customer", "chain")]
    [InlineData("Customer", "restore")]
    [InlineData("Customer", "delete")]
    [InlineData("Customer", "delete-chain")]
    [InlineData("Reseller", "list")]
    [InlineData("Reseller", "download")]
    [InlineData("Reseller", "entries")]
    [InlineData("Reseller", "chain")]
    [InlineData("Reseller", "restore")]
    [InlineData("Reseller", "delete")]
    [InlineData("Reseller", "delete-chain")]
    public async Task ReassigningTheSiteDoesNotGiveItsNewTenantThePreviousTenantsArchive(string role, string operation)
    {
        using var fixture = await Fixture.Create();
        var bytes = await File.ReadAllBytesAsync(fixture.Record.Path);
        var index = await fixture.Index();
        await fixture.ReassignSite();
        var controller = fixture.Controller(role, transferred: true);

        // This is the supported scope reassignment, not an invented inconsistent record.
        // It moves the site but leaves database ownership with the original subscription.
        Assert.Equal(F.Owner, Assert.Single(await fixture.Backup.Ownership.GetAllAsync()).OwnerUserId);
        await Assert.ThrowsAsync<AppException>(() => CustomerAccess.EnsureDatabaseAsync(
            fixture.Backup.Scopes, fixture.Backup.Ownership, controller.User, "mysql", "private_ledger", default));

        await fixture.AssertDenied(controller, operation);

        Assert.True(File.Exists(fixture.Record.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Record.Path));
        Assert.Equal(index, await fixture.Index());
        Assert.Empty(Directory.EnumerateFiles(fixture.Backup.Root, "mysql-private_ledger.sql", SearchOption.AllDirectories));
        Assert.DoesNotContain(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync(), row =>
            row.Action is "backup.deleted" or "backup.chain_deleted" or "backup.items_restored");
    }

    [Theory]
    [InlineData("Customer", "download")]
    [InlineData("Customer", "restore")]
    [InlineData("Customer", "delete")]
    [InlineData("Reseller", "download")]
    [InlineData("Reseller", "restore")]
    [InlineData("Reseller", "delete")]
    [InlineData("Administrator", "download")]
    [InlineData("Administrator", "restore")]
    [InlineData("Administrator", "delete")]
    public async Task TheUnchangedOwnerAndItsResellerKeepWorkingBackupAccess(string role, string operation)
    {
        using var fixture = await Fixture.Create();
        var controller = fixture.Controller(role);
        Assert.Equal(fixture.Record.Id, Assert.Single(Data(await controller.GetBackupsAsync(default))).Id);
        Assert.NotEmpty(Data(await controller.GetEntriesAsync(fixture.Record.Id, "databases/", default)).Entries);
        Assert.False(Data(await controller.GetChainAsync(fixture.Record.Id, default)).Broken);
        if (operation == "download")
        {
            var file = Assert.IsType<PhysicalFileResult>(await controller.DownloadAsync(fixture.Record.Id, default));
            Assert.Equal(fixture.Record.Path, file.FileName);
            using var archive = ZipFile.OpenRead(file.FileName);
            using var reader = new StreamReader(archive.GetEntry("databases/mysql-private_ledger.sql")!.Open());
            Assert.Contains("mysql:private_ledger", await reader.ReadToEndAsync());
        }
        else if (operation == "restore")
        {
            if (role == "Administrator")
            {
                var result = Data(await controller.RestoreItemsAsync(fixture.RestoreRequest(), default));
                Assert.Equal(1, result.RestoredCount);
                var extracted = Assert.Single(Directory.EnumerateFiles(fixture.Backup.Root, "mysql-private_ledger.sql", SearchOption.AllDirectories));
                Assert.Contains("mysql:private_ledger", await File.ReadAllTextAsync(extracted));
            }
            else
            {
                var result = Assert.IsType<FileStreamResult>(await controller.DownloadItemsAsync(new DownloadBackupItemsRequest
                { BackupId = fixture.Record.Id, Items = fixture.RestoreRequest().Items }, default));
                await using var stream = result.FileStream;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                using var reader = new StreamReader(Assert.Single(archive.Entries).Open());
                Assert.Contains("mysql:private_ledger", await reader.ReadToEndAsync());
            }
        }
        else
        {
            Assert.IsType<OkObjectResult>((await controller.DeleteAsync(fixture.Record.Id, default)).Result);
            Assert.False(File.Exists(fixture.Record.Path));
            Assert.Empty(await fixture.Backup.Persisted());
        }
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task LegacyTenantExtractionRequiresTheUsableSelectionDownload(string role)
    {
        using var fixture = await Fixture.Create();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Controller(role).RestoreItemsAsync(fixture.RestoreRequest(), default));
        Assert.Equal("backup.selection_download_required", error.MessageKey);
        Assert.Empty(Directory.EnumerateFiles(fixture.Backup.Root, "mysql-private_ledger.sql", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AdministratorKeepsTheMeasuredExtractionDestination()
    {
        using var fixture = await Fixture.Create();
        var result = Data(await fixture.Controller("Administrator").RestoreItemsAsync(fixture.RestoreRequest(), default));
        Assert.Equal(1, result.RestoredCount);
        Assert.True(Directory.Exists(result.TargetRoot));
        Assert.True(File.Exists(Path.Combine(result.TargetRoot, "databases", "mysql-private_ledger.sql")));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task AnUnknownHistoricalOwnerCannotBeInferredFromTheCurrentSiteName(string role)
    {
        using var fixture = await Fixture.Create();
        fixture.Record.SubscriptionUserId = string.Empty;
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        var controller = fixture.Controller(role);
        Assert.Empty(Data(await controller.GetBackupsAsync(default)));
        await fixture.AssertDenied(controller, "download");
        await fixture.AssertDenied(controller, "entries");
        await fixture.AssertDenied(controller, "delete");
        Assert.True(File.Exists(fixture.Record.Path));
    }

    [Fact]
    public async Task AdministratorCanStillInspectAndDownloadAnUnknownHistoricalOwner()
    {
        using var fixture = await Fixture.Create();
        fixture.Record.SubscriptionUserId = string.Empty;
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        var controller = fixture.Controller("Administrator");
        Assert.Equal(fixture.Record.Id, Assert.Single(Data(await controller.GetBackupsAsync(default))).Id);
        Assert.IsType<PhysicalFileResult>(await controller.DownloadAsync(fixture.Record.Id, default));
        Assert.NotEmpty(Data(await controller.GetEntriesAsync(fixture.Record.Id, "databases/", default)).Entries);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task ANewSiteOwnerStartsAFullChainWithoutThePreviousOwnersBase(string role)
    {
        using var fixture = await Fixture.Create(incremental: true);
        var previous = fixture.Record;
        Assert.Equal("full", previous.BackupMode);
        await fixture.ReassignSite();
        var current = await fixture.CreateSiteBackup(role, true, transferred: true);

        Assert.Equal("full", current.BackupMode);
        Assert.Equal(string.Empty, current.BaseBackupId);
        Assert.NotEqual(previous.ChainId, current.ChainId);
        Assert.Equal(F.Other.ToString("N"), current.SubscriptionUserId);
        Assert.Contains("files/index.html", F.Entries(current));
        Assert.DoesNotContain(Data(await fixture.Controller(role, transferred: true).GetChainAsync(current.Id, default)).Links,
            link => link.BackupId == previous.Id);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unknown")]
    public async Task AdministratorMixedScopeArchiveRemainsSuccessfulButCannotBeSharedWithTheSiteOwner(string ownership)
    {
        using var fixture = await Fixture.Create();
        var database = await fixture.RestrictedDatabase(ownership);
        var record = await fixture.CreateSiteBackup("Administrator", false, databases: [database]);
        Assert.Equal("created", record.Status);
        Assert.Equal(database.DatabaseName, Assert.Single(record.IncludedDatabases!).DatabaseName);
        Assert.Contains("databases/mysql-" + database.DatabaseName + ".sql", F.Entries(record));
        Assert.Equal(string.Empty, record.SubscriptionUserId);

        var customer = fixture.Controller("Customer");
        Assert.DoesNotContain(Data(await customer.GetBackupsAsync(default)), item => item.Id == record.Id);
        Assert.IsType<NotFoundObjectResult>(await customer.DownloadAsync(record.Id, default));
        await Assert.ThrowsAsync<AppException>(() => customer.GetEntriesAsync(record.Id, "databases/", default));
        Assert.IsType<PhysicalFileResult>(await fixture.Controller("Administrator").DownloadAsync(record.Id, default));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unknown")]
    public async Task RemovingRestrictedDatabasesFromANewRequestDoesNotAdoptTheirAdministratorOnlyChain(string ownership)
    {
        using var fixture = await Fixture.Create(incremental: true);
        var tenantBase = fixture.Record;
        var database = await fixture.RestrictedDatabase(ownership);
        var restricted = await fixture.CreateSiteBackup("Administrator", true, databases: [database]);
        var tenant = await fixture.CreateSiteBackup("Customer", true);

        Assert.NotEqual(restricted.Id, tenant.BaseBackupId);
        Assert.NotEqual(restricted.ChainId, tenant.ChainId);
        Assert.Equal(tenantBase.Id, tenant.BaseBackupId);
        Assert.Equal(tenantBase.ChainId, tenant.ChainId);
        Assert.Equal("incremental", tenant.BackupMode);
        Assert.DoesNotContain(Data(await fixture.Controller("Customer").GetChainAsync(tenant.Id, default)).Links,
            link => link.BackupId == restricted.Id);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Administrator")]
    public async Task AMeasuredUnchangedOwnerKeepsItsUsableIncrementalBase(string role)
    {
        using var fixture = await Fixture.Create(incremental: true);
        var previous = fixture.Record;
        var current = await fixture.CreateSiteBackup(role, true,
            databases: [new BackupDatabaseRef { Engine = "mysql", DatabaseName = "private_ledger" }]);
        Assert.Equal("incremental", current.BackupMode);
        Assert.Equal(previous.Id, current.BaseBackupId);
        Assert.Equal(previous.ChainId, current.ChainId);
        Assert.Equal(F.Owner.ToString("N"), current.SubscriptionUserId);
        Assert.False(Data(await fixture.Controller("Customer").GetChainAsync(current.Id, default)).Broken);
        Assert.IsType<PhysicalFileResult>(await fixture.Controller("Customer").DownloadAsync(current.Id, default));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("foreign")]
    public async Task NonAdminCannotCreateAnArchiveWhoseTenantOwnershipCannotBeConfirmed(string ownership)
    {
        using var fixture = await Fixture.Create();
        var role = ownership == "unknown" ? "Customer" : "Reseller";
        var database = ownership == "unknown"
            ? new BackupDatabaseRef { Engine = "mysql", DatabaseName = ResourceNaming.DatabaseNameFromSite(F.Site) }
            : await fixture.RestrictedDatabase(ownership);
        if (ownership == "foreign") await fixture.AllowResellerToManageOtherTenant();
        var controller = fixture.Controller(role);
        // Both are supported selections: legacy site-name fallback, or a reseller's other
        // customer's explicit database. Neither proves that the SITE owner may read it.
        await CustomerAccess.EnsureDatabaseAsync(fixture.Backup.Scopes, fixture.Backup.Ownership,
            controller.User, database.Engine, database.DatabaseName, default);
        var index = await fixture.Index();
        var archives = Directory.GetFiles(fixture.Backup.Root, "*.zip", SearchOption.AllDirectories).Order().ToArray();
        var audits = await fixture.Db.AuditLogs.CountAsync();

        var error = await Assert.ThrowsAsync<AppException>(() => controller.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = F.Site, IncludeFiles = true, IncludeConfig = false, IncludeMail = false,
            Databases = [database]
        }, default));

        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("backup.ownership_unconfirmed", error.MessageKey);
        Assert.Equal(index, await fixture.Index());
        Assert.Equal(archives, Directory.GetFiles(fixture.Backup.Root, "*.zip", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(audits, await fixture.Db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task AdministratorOnlyCreationAuditDoesNotShareItsForeignDatabaseWithTheSiteOwner()
    {
        using var fixture = await Fixture.Create();
        var database = await fixture.RestrictedDatabase("foreign");
        var record = await fixture.CreateSiteBackup("Administrator", false, databases: [database]);
        Assert.Equal(string.Empty, record.SubscriptionUserId);
        var audit = Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync(),
            row => row.Action == "backup.created" && row.Resource == record.Name);
        Assert.Null(audit.SubscriptionUserId);
    }

    [Fact]
    public async Task DuplicateSiteAssignmentCannotCreateAnArchiveForAnInaccessibleSubscription()
    {
        using var fixture = await Fixture.Create();
        // The real scope store permits this legacy/shared assignment. Do not infer that
        // its second customer may create an archive stamped for the first customer.
        await fixture.Backup.Scopes.SetScopeAsync(F.Other, [F.Site], 0, 0);
        Assert.Equal(F.Owner, await fixture.Backup.Scopes.GetSiteOwnerUserIdAsync(F.Site));
        var controller = fixture.Controller("Customer", transferred: true);
        await CustomerAccess.EnsureSiteAsync(fixture.Backup.Scopes, controller.User, F.Site, default);
        var index = await fixture.Index();
        var archives = Directory.GetFiles(fixture.Backup.Root, "*.zip", SearchOption.AllDirectories).Order().ToArray();
        var audits = await fixture.Db.AuditLogs.CountAsync();

        var error = await Assert.ThrowsAsync<AppException>(() => controller.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = F.Site, IncludeFiles = true, IncludeConfig = false, IncludeMail = false
        }, default));

        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal("auth.subscription_forbidden", error.MessageKey);
        Assert.Equal(index, await fixture.Index());
        Assert.Equal(archives, Directory.GetFiles(fixture.Backup.Root, "*.zip", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(audits, await fixture.Db.AuditLogs.CountAsync());
    }

    [Theory]
    [InlineData("controller")]
    [InlineData("service")]
    public async Task HistoricalArchiveAuditDoesNotInferTheSitesNewTenantAsItsOwner(string route)
    {
        using var fixture = await Fixture.Create();
        fixture.Record.SubscriptionUserId = string.Empty;
        await fixture.ReassignSite();
        if (route == "service")
        {
            fixture.Record.TargetType = "webdav";
            fixture.Record.RemoteUri = "https://backup.example.test/private-archive.zip";
        }
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        if (route == "controller")
            await fixture.Controller("Administrator").DeleteAsync(fixture.Record.Id, default);
        else
        {
            // No saved remote credentials: the real deletion flow refuses any network
            // call, preserves its remote record, and writes the failure audit locally.
            var service = new BackupService(null!, null!, null!, null!, null!, fixture.Backup.Store, null!,
                customerScopes: fixture.Backup.Scopes,
                auditService: new AuditService(fixture.Db, fixture.Backup.Scopes, fixture.Backup.Store));
            var error = await Assert.ThrowsAsync<AppException>(() => service.DeleteBackupAsync(fixture.Record.Id));
            Assert.Equal("backup.remote_delete_failed", error.MessageKey);
        }
        var action = route == "controller" ? "backup.deleted" : "backup.remote_delete_failed";
        var audit = Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync(), row => row.Action == action);
        Assert.Null(audit.SubscriptionUserId);
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> response) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;

    private sealed class Fixture : IDisposable
    {
        internal F Backup { get; } = new();
        internal AdaPanelDbContext Db { get; }
        internal BackupRecordDto Record { get; private set; } = null!;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly Guid resellerA = Guid.NewGuid(), resellerB = Guid.NewGuid(), admin = Guid.NewGuid();
        private Fixture()
        {
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            foreach (var id in new[] { F.Owner, F.Other, resellerA, resellerB, admin })
                Db.Users.Add(new PanelUser
                {
                    Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                    Role = id == admin ? UserRole.Administrator : id == resellerA || id == resellerB ? UserRole.Reseller : UserRole.Customer,
                    PasswordHash = "measured-fixture-password-hash"
                });
            Db.SaveChanges();
        }
        internal static async Task<Fixture> Create(bool incremental = false)
        {
            var fixture = new Fixture();
            await fixture.Backup.Scopes.SetOwnerAsync(F.Owner, fixture.resellerA);
            await fixture.Backup.Scopes.SetOwnerAsync(F.Other, fixture.resellerB);
            await fixture.Backup.Ownership.RecordAsync("mysql", "private_ledger", F.Owner, F.Site);
            fixture.Record = await fixture.CreateSiteBackup("Customer", incremental,
                databases: [new BackupDatabaseRef { Engine = "mysql", DatabaseName = "private_ledger" }]);
            Assert.Contains("databases/mysql-private_ledger.sql", F.Entries(fixture.Record));
            return fixture;
        }
        internal async Task<BackupRecordDto> CreateSiteBackup(string role, bool incremental, bool transferred = false,
            BackupDatabaseRef[]? databases = null)
        {
            var response = Data(await Controller(role, transferred).BackupSiteAsync(new BackupSiteRequest
            {
                SiteName = F.Site, IncludeFiles = true, IncludeConfig = false, IncludeMail = false,
                Incremental = incremental, Databases = [.. (databases ?? [])]
            }, default));
            return Assert.Single(await Backup.Persisted(), record => record.Id == response.Id);
        }
        internal async Task<BackupDatabaseRef> RestrictedDatabase(string ownership)
        {
            var name = ownership == "foreign" ? "foreign_ledger" : "untracked_ledger";
            if (ownership == "foreign") await Backup.Ownership.RecordAsync("mysql", name, F.Other, F.ForeignSite);
            return new BackupDatabaseRef { Engine = "mysql", DatabaseName = name };
        }
        internal BackupController Controller(string role, bool transferred = false)
        {
            var actor = role == "Administrator" ? admin : role == "Reseller" ? transferred ? resellerB : resellerA : transferred ? F.Other : F.Owner;
            return new BackupController(Backup.Service, Backup.Scopes, Backup.Ownership, null!, new Jobs(),
                new AuditService(Db, Backup.Scopes, Backup.Store), Db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role),
                            new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("measured-fixture-password-hash"))
                        }, "Test"))
                    }
                }
            };
        }
        internal async Task ReassignSite()
        {
            await Backup.Scopes.SetScopeAsync(F.Owner, [], 0, 0);
            await Backup.Scopes.SetScopeAsync(F.Other, [F.Site], 0, 0);
        }
        internal Task AllowResellerToManageOtherTenant() => Backup.Scopes.SetOwnerAsync(F.Other, resellerA);
        internal RestoreBackupItemsRequest RestoreRequest() => new()
        { BackupId = Record.Id, Items = ["databases/mysql-private_ledger.sql"] };
        internal Task<byte[]> Index() => File.ReadAllBytesAsync(Backup.Store.ResolvePath("config/backups.json"));
        internal async Task AssertDenied(BackupController controller, string operation)
        {
            if (operation == "list")
            {
                Assert.Empty(Data(await controller.GetBackupsAsync(default)));
                return;
            }
            if (operation == "download")
            {
                Assert.IsType<NotFoundObjectResult>(await controller.DownloadAsync(Record.Id, default));
                return;
            }
            var error = await RecordException();
            Assert.True(error.HttpStatusCode is 403 or 404);
            return;

            async Task<AppException> RecordException() => await Assert.ThrowsAsync<AppException>(async () =>
            {
                switch (operation)
                {
                    case "entries": await controller.GetEntriesAsync(Record.Id, "databases/", default); break;
                    case "chain": await controller.GetChainAsync(Record.Id, default); break;
                    case "restore": await controller.DownloadItemsAsync(new DownloadBackupItemsRequest
                        { BackupId = Record.Id, Items = RestoreRequest().Items }, default); break;
                    case "delete": await controller.DeleteAsync(Record.Id, default); break;
                    case "delete-chain": await controller.DeleteChainAsync(Record.Id, default,
                        new DeleteBackupChainRequest { ExpectedBackupIds = [Record.Id] }); break;
                    default: throw new InvalidOperationException(operation);
                }
            });
        }
        public void Dispose()
        {
            Db.Dispose(); connection.Dispose(); Backup.Dispose();
        }
    }

    private sealed class Jobs : IRunningJobService
    {
        public Task<IAsyncDisposable> BeginAsync(string kind, string title, Guid? subjectUserId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IAsyncDisposable>(new Empty()); }
        public Task<RunningJobsStatusDto> GetStatusAsync(IReadOnlyCollection<string> kinds, IReadOnlySet<string>? allowedSubjectUserIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        private sealed class Empty : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
