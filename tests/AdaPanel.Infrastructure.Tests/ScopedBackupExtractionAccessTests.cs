using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

[CollectionDefinition("Selected backup downloads", DisableParallelization = true)]
public sealed class SelectedBackupDownloadCollection { }

[Collection("Selected backup downloads")]
public sealed partial class ScopedBackupExtractionAccessTests
{
    [Theory]
    [InlineData("Customer", "site")]
    [InlineData("Reseller", "site")]
    [InlineData("Customer", "subscription")]
    [InlineData("Reseller", "subscription")]
    public async Task LegacyScopedExtractionCannotReportSuccessForAnInaccessiblePrivateDestination(string role, string type)
    {
        using var fixture = await Fixture.Create(role, type);
        var initialFiles = await fixture.DownloadableFiles();
        var originalSiteBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.SiteRoot, "index.html"));
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Backups.RestoreItemsAsync(new RestoreBackupItemsRequest
        {
            BackupId = fixture.Record.Id,
            Items = [fixture.SelectedEntry],
            // The HTTP controller deliberately replaces this with the safe server default.
            TargetPath = fixture.SiteRoot
        }, default));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("backup.selection_download_required", error.MessageKey);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Backup.Root, "backups"),
            "mysql-private_ledger.sql", SearchOption.AllDirectories));

        // Baseline: the scoped HTTP call returned success while this real service output
        // could not be reached through any permitted file root. Keep that diagnosis as a
        // positive sandbox test; only administrators may now request server extraction.
        await fixture.Backup.Service.RestoreItemsAsync(new RestoreBackupItemsRequest
        { BackupId = fixture.Record.Id, Items = [fixture.SelectedEntry] });
        var extracted = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Backup.Root, "backups"), "mysql-private_ledger.sql", SearchOption.AllDirectories));
        Assert.Contains("mysql:private_ledger", await File.ReadAllTextAsync(extracted));
        Assert.Equal(originalSiteBytes, await File.ReadAllBytesAsync(Path.Combine(fixture.SiteRoot, "index.html")));

        // Do not turn the private backup root into a tenant file root to make this pass.
        // Both a physical path and traversal from a permitted site must remain denied.
        var physicalError = await Assert.ThrowsAsync<AppException>(() => fixture.Files.DownloadAsync(extracted, default));
        Assert.Equal(403, physicalError.HttpStatusCode);
        var escape = $"sites/{F.Site}/{Path.GetRelativePath(fixture.SiteRoot, extracted).Replace('\\', '/')}";
        await Assert.ThrowsAsync<AppException>(() => fixture.Files.DownloadAsync(escape, default));

        var visibleFiles = await fixture.DownloadableFiles();
        Assert.NotEmpty(initialFiles);
        Assert.DoesNotContain(visibleFiles, file => file.Content.Contains("mysql:private_ledger", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Customer", "site")]
    [InlineData("Reseller", "site")]
    [InlineData("Customer", "subscription")]
    [InlineData("Reseller", "subscription")]
    public async Task ExistingSiteFilesAndTheCompleteOwnedArchiveRemainDownloadable(string role, string type)
    {
        using var fixture = await Fixture.Create(role, type);
        var files = await fixture.DownloadableFiles();
        var original = Assert.Single(files);
        Assert.Equal("owned site contents", original.Content);
        var archiveResult = Assert.IsType<PhysicalFileResult>(await fixture.Backups.DownloadAsync(fixture.Record.Id, default));
        Assert.Equal(fixture.Record.Path, archiveResult.FileName);
        using var zip = ZipFile.OpenRead(archiveResult.FileName);
        var selected = zip.GetEntry(fixture.SelectedEntry);
        Assert.NotNull(selected);
        using var reader = new StreamReader(selected.Open());
        Assert.Contains("mysql:private_ledger", await reader.ReadToEndAsync());
        Assert.True(zip.Entries.Count > 1);
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> result) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;

    private sealed class Fixture : IDisposable
    {
        internal F Backup { get; } = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        internal readonly AdaPanelDbContext Db;
        private readonly Guid reseller = Guid.NewGuid();
        private readonly Guid admin = Guid.NewGuid();
        internal Guid Actor { get; }
        internal string SessionId { get; } = SessionRevocationStore.NewSessionId();
        internal SessionRevocationStore Sessions { get; }
        private readonly ServiceProvider services;
        private readonly FilePublicationAuthorityTests.LocalGate fileGates = new();
        internal string SiteRoot => Path.Combine(Backup.Root, "www");
        internal BackupRecordDto Record { get; private set; } = null!;
        internal string SelectedEntry { get; private set; } = string.Empty;
        internal BackupController Backups { get; private set; }
        internal FilesController Files { get; }

        private Fixture(string role)
        {
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            Db.Users.AddRange(new PanelUser
            {
                Id = F.Owner, UserName = "owner", Email = "owner@example.test", Role = UserRole.Customer,
                IsActive = true, PasswordHash = "extraction-access-fixture"
            }, new PanelUser
            {
                Id = reseller, UserName = "reseller", Email = "reseller@example.test", Role = UserRole.Reseller,
                IsActive = true, PasswordHash = "extraction-access-fixture"
            }, new PanelUser
            {
                Id = admin, UserName = "admin", Email = "admin@example.test", Role = UserRole.Administrator,
                IsActive = true, PasswordHash = "extraction-access-fixture"
            });
            Db.SaveChanges();
            Actor = role == "Administrator" ? admin : role == "Reseller" ? reseller : F.Owner;
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Actor.ToString()), new Claim(ClaimTypes.Role, role),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("extraction-access-fixture")),
                new Claim(SessionRevocationStore.SessionIdClaim, SessionId)
            }, CookieAuthenticationDefaults.AuthenticationScheme));
            Sessions = new SessionRevocationStore(Backup.Store);
            var audit = new AuditService(Db, Backup.Scopes, Backup.Store);
            var site = new SiteDto { Id = 18, Name = F.Site, PhysicalPath = SiteRoot, ApplicationPoolName = "owned-pool" };
            var iis = DispatchProxy.Create<IIisSiteService, SubscriptionMeasurementProxy>();
            ((SubscriptionMeasurementProxy)(object)iis).Handler = (method, args) => method.Name switch
            {
                "GetSiteAsync" when string.Equals((string)args[0]!, F.Site, StringComparison.OrdinalIgnoreCase) => Task.FromResult(site),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            };
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(Sessions)
                .AddSingleton<IIisSiteService>(iis).AddSingleton<IConcurrencyLock>(fileGates).BuildServiceProvider();
            Backups = new BackupController(Backup.Service, Backup.Scopes, Backup.Ownership, null!, new Jobs(), audit, Db)
            { ControllerContext = Context(principal) };
            Files = new FilesController(new FileService(iis), Backup.Scopes, audit, new ConcurrentWorkGate())
            { ControllerContext = Context(principal) };
        }

        internal static async Task<Fixture> Create(string role, string type)
        {
            var fixture = new Fixture(role);
            await fixture.Backup.Scopes.SetOwnerAsync(F.Owner, fixture.reseller);
            await fixture.Backup.Ownership.RecordAsync("mysql", "private_ledger", F.Owner, F.Site);
            fixture.Record = type == "site"
                ? await fixture.Backup.Service.BackupSiteAsync(new BackupSiteRequest
                {
                    SiteName = F.Site, IncludeFiles = true, IncludeConfig = false, IncludeMail = false,
                    Databases = [new BackupDatabaseRef { Engine = "mysql", DatabaseName = "private_ledger" }]
                })
                : await fixture.Backup.Service.BackupSubscriptionAsync(fixture.Backup.Request());
            fixture.SelectedEntry = Assert.Single(F.Entries(fixture.Record), entry => entry.EndsWith("mysql-private_ledger.sql", StringComparison.Ordinal));
            Assert.Equal(F.Owner.ToString("N"), fixture.Record.SubscriptionUserId);
            return fixture;
        }

        internal async Task<List<(string Path, string Content)>> DownloadableFiles()
        {
            var files = new List<(string Path, string Content)>();
            var directories = new Queue<string>();
            directories.Enqueue(string.Empty);
            // The actual workspace supplies every virtual path; the test invents none.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (directories.TryDequeue(out var directory))
            {
                Assert.True(visited.Add(directory), "Fixture workspace must be acyclic.");
                Assert.True(visited.Count < 30, "Fixture traversal must stay bounded.");
                var workspace = Data(await Files.GetWorkspaceAsync(directory, default));
                foreach (var entry in workspace.Entries)
                    if (entry.IsDirectory) directories.Enqueue(entry.Path);
                    else
                    {
                        var response = Assert.IsType<FileStreamResult>(await Files.DownloadAsync(entry.Path, default));
                        await using var stream = response.FileStream;
                        using var reader = new StreamReader(stream, leaveOpen: true);
                        files.Add((entry.Path, await reader.ReadToEndAsync()));
                    }
            }
            return files;
        }

        internal DownloadBackupItemsRequest Selection(params string[] items) => new()
        { BackupId = Record.Id, Items = items.Length == 0 ? [SelectedEntry] : items };
        internal void AfterDownload(Func<BackupItemsDownload, Task> boundary)
        {
            var proxy = DispatchProxy.Create<IBackupService, SubscriptionMeasurementProxy>();
            ((SubscriptionMeasurementProxy)(object)proxy).Handler = (method, args) => method.Name == "DownloadItemsAsync"
                ? Deliver((DownloadBackupItemsRequest)args[0]!, (Func<BackupRecordDto, CancellationToken, Task>)args[1]!, (CancellationToken)args[2]!)
                : method.Invoke(Backup.Service, args);
            Backups = new BackupController(proxy, Backup.Scopes, Backup.Ownership, null!, new Jobs(),
                new AuditService(Db, Backup.Scopes, Backup.Store), Db) { ControllerContext = Backups.ControllerContext };
            async Task<BackupItemsDownload> Deliver(DownloadBackupItemsRequest request,
                Func<BackupRecordDto, CancellationToken, Task> approve, CancellationToken token)
            {
                var result = await Backup.Service.DownloadItemsAsync(request, approve, token);
                try { await boundary(result); return result; }
                catch { result.Dispose(); throw; }
            }
        }
        private ControllerContext Context(ClaimsPrincipal principal) => new()
        { HttpContext = new DefaultHttpContext { User = principal, RequestServices = services } };
        public void Dispose() { services.Dispose(); fileGates.Dispose(); Db.Dispose(); connection.Dispose(); Backup.Dispose(); }
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
