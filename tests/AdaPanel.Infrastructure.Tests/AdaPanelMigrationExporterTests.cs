using System.Reflection;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AdaPanelMigrationExporterTests
{
    [Fact]
    public async Task SelectedExportRoundTripsRealPayloadsAndPreservesApplicationRoot()
    {
        using var area = new Area();
        var inventory = await area.Service.GetInventoryAsync();
        var site = Assert.Single(inventory.Sites, item => item.Name == "first.example");
        Assert.Equal("public", site.DocumentRootRelativePath);
        Assert.Equal(area.FirstRoot, site.SourcePhysicalPath);
        Assert.Equal(area.FirstRoot, Assert.Single(inventory.Tasks).WorkingDirectory);
        Assert.Equal(new[] { site.Id }, Assert.Single(inventory.Tasks).SiteIds);
        var db = Assert.Single(inventory.Databases);
        var artifact = await area.Service.ExportAsync(new()
        {
            SiteIds = [site.Id], DatabaseIds = [db.Id], IncludeMail = true, IncludeDns = true,
            IncludeTasks = true, TaskIds = [Assert.Single(inventory.Tasks).Id]
        });
        await using var input = File.OpenRead(artifact.FilePath);
        var imported = await area.Store.UploadAsync(input);
        var manifest = imported.Manifest;
        Assert.True(manifest.Complete);
        Assert.Equal(0, area.LegacyDnsReads); Assert.Equal(1, area.SnapshotDnsReads);
        Assert.Equal("first.example", Assert.Single(manifest.Sites).Name);
        Assert.Equal(2, manifest.Sites[0].FileCount);
        Assert.Equal("public", manifest.Sites[0].DocumentRootRelativePath);
        Assert.Contains("empty", manifest.Sites[0].EmptyDirectories);
        Assert.DoesNotContain(manifest.Payloads, payload => payload.Path.Contains("second"));
        var env = Assert.Single(manifest.Payloads, payload => payload.Path.EndsWith("/.env"));
        await using (var contents = await area.Store.OpenPayloadAsync(imported.Id, env.Path))
        using (var reader = new StreamReader(contents)) Assert.Equal("APP_ENV=fixture", await reader.ReadToEndAsync());
        Assert.Equal("demo", Assert.Single(manifest.Databases).Name);
        Assert.Equal(site.Id, Assert.Single(manifest.Databases[0].SiteIds));
        var mailbox = Assert.Single(Assert.Single(manifest.MailDomains).Accounts);
        Assert.Equal("info@first.example", mailbox.Email);
        Assert.Equal("vandelay-sqlite", mailbox.ArchiveFormat);
        Assert.Equal(4, mailbox.EmailCount);
        Assert.Single(manifest.DnsZones);
        Assert.Single(manifest.Tasks);
        Assert.DoesNotContain("ADMIN_FIXTURE_SECRET", JsonSerializer.Serialize(manifest));
        Assert.Equal("APP_ENV=fixture", await File.ReadAllTextAsync(Path.Combine(area.FirstRoot, ".env")));
        Assert.Equal(0, area.SourceMutations);
    }

    [Fact]
    public async Task SelectionDoesNotPullDatabasesMailDnsOrTasksWhenNotRequested()
    {
        using var area = new Area();
        var inventory = await area.Service.GetInventoryAsync();
        var artifact = await area.Service.ExportAsync(new()
        {
            SiteIds = [inventory.Sites.Single(site => site.Name == "second.example").Id],
            IncludeMail = false, IncludeDns = false
        });
        await using var input = File.OpenRead(artifact.FilePath);
        var package = await area.Store.UploadAsync(input);
        Assert.Equal("second.example", Assert.Single(package.Manifest.Sites).Name);
        Assert.Empty(package.Manifest.Databases);
        Assert.Empty(package.Manifest.MailDomains);
        Assert.Empty(package.Manifest.DnsZones);
        Assert.Empty(package.Manifest.Tasks);
        Assert.Equal(0, area.ExportDumpCalls);
        Assert.Equal(0, area.ArchiveMailCalls);
    }

    [Theory]
    [InlineData("missing-account")]
    [InlineData("failed-account")]
    [InlineData("empty-account")]
    public async Task IncompleteMailNeverProducesSuccessfulPackage(string failure)
    {
        using var area = new Area { MailFailure = failure };
        var error = await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new()
        { AllSites = true, IncludeMail = true, IncludeDns = false }));
        Assert.Equal("migration.export.mail_payload_incomplete", error.MessageKey);
        Assert.Empty(Directory.EnumerateFiles(area.DataRoot, "migration.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UnknownSourceSelectionAndUnreadableAllDatabaseInventoryStopBeforeWriting()
    {
        using var area = new Area();
        await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new() { SiteIds = ["gone"] }));
        area.DatabaseReadable = false;
        var inventory = await area.Service.GetInventoryAsync();
        Assert.False(inventory.Complete);
        Assert.Contains(inventory.Issues, issue => issue.Code == "migration.export.database_access_required");
        var error = await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new()
        { AllSites = true, AllDatabases = true, IncludeMail = false, IncludeDns = false }));
        Assert.Equal("migration.export.inventory_incomplete", error.MessageKey);
        Assert.Equal(0, area.ExportDumpCalls);
    }

    [Fact]
    public async Task CancelledDumpLeavesSourceUntouchedAndNoPartialDownload()
    {
        using var area = new Area();
        using var cancellation = new CancellationTokenSource();
        area.DumpAction = () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => area.Service.ExportAsync(new()
        { AllDatabases = true, IncludeMail = false, IncludeDns = false }, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(area.DataRoot, "migration.zip", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(area.FirstRoot, ".env")));
    }

    [Fact]
    public async Task PayloadLimitAndUnsupportedExternalTaskFilesAreExplicitFailures()
    {
        using var area = new Area();
        area.Limits.MaxPayloadBytes = 1;
        var error = await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new()
        { AllSites = true, IncludeMail = false, IncludeDns = false }));
        Assert.Equal("migration.export.size_limit", error.MessageKey);
        area.Limits.MaxPayloadBytes = 1024 * 1024;
        area.ExternalTask = true;
        error = await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new()
        { AllSites = true, IncludeMail = false, IncludeDns = false, IncludeTasks = true }));
        Assert.Equal("migration.export.task_files_required", error.MessageKey);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("missing")]
    [InlineData("panel-fallback")]
    [InlineData("mode-conflict")]
    [InlineData("transfer-conflict")]
    [InlineData("secondary")]
    [InlineData("native-secondary")]
    [InlineData("unknown-native-type")]
    public async Task Export_rejects_unmeasured_fallback_or_conflicting_dns_instead_of_claiming_completeness(string failure)
    {
        using var area = new Area();
        switch (failure)
        {
            case "unavailable": area.DnsAvailable = false; break;
            case "missing": area.DnsPresent = false; break;
            case "panel-fallback": area.DnsSource = "panel-managed"; break;
            case "mode-conflict": area.DnsModeConflict = true; break;
            case "transfer-conflict": area.DnsTransferConflict = true; break;
            case "secondary": area.DnsSecondary = true; break;
            case "native-secondary": area.DnsType = "Secondary"; break;
            case "unknown-native-type": area.DnsType = ""; break;
        }
        var error = await Assert.ThrowsAsync<AppException>(() => area.Service.ExportAsync(new()
            { AllSites = true, IncludeDns = true, IncludeMail = false }));
        Assert.Equal("migration.export.dns_zone_unreadable", error.MessageKey);
        Assert.Equal(0, area.LegacyDnsReads); Assert.Equal(1, area.SnapshotDnsReads);
        Assert.Empty(Directory.EnumerateFiles(area.DataRoot, "migration.zip", SearchOption.AllDirectories));
        Assert.Equal("APP_ENV=fixture", await File.ReadAllTextAsync(Path.Combine(area.FirstRoot, ".env")));
        Assert.Equal(0, area.SourceMutations);
    }

    [Fact]
    public async Task Unsupported_readonly_dns_snapshot_never_falls_back_to_the_mutating_GET()
    {
        using var area = new Area { SnapshotUnsupported = true };
        await Assert.ThrowsAsync<NotSupportedException>(() => area.Service.ExportAsync(new()
            { AllSites = true, IncludeDns = true, IncludeMail = false }));
        Assert.Equal(0, area.LegacyDnsReads); Assert.Equal(1, area.SnapshotDnsReads);
        Assert.Empty(Directory.EnumerateFiles(area.DataRoot, "migration.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportWorkspaceCannotBeInsideAHiddenPanelToolWebRoot()
    {
        using var area = new Area { HiddenRootIsData = true };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => area.Service.ExportAsync(new()
            { AllSites = true, IncludeMail = false, IncludeDns = false }));
        Assert.Equal("migration.package.public_root", error.Message);
        Assert.Empty(Directory.EnumerateFiles(area.DataRoot, "migration.zip", SearchOption.AllDirectories));
        Assert.Equal(0, area.ExportDumpCalls);
    }

    [Fact]
    public async Task IndependentDatabaseCanBeExportedWithoutInventingSiteAssociation()
    {
        using var area = new Area { DatabaseSite = null };
        var inventory = await area.Service.GetInventoryAsync();
        Assert.Contains(inventory.Issues, issue => issue.Code == "migration.export.database_site_unknown");
        var artifact = await area.Service.ExportAsync(new() { AllDatabases = true, IncludeMail = false, IncludeDns = false });
        await using var input = File.OpenRead(artifact.FilePath);
        var package = await area.Store.UploadAsync(input);
        Assert.Empty(package.Manifest.Sites);
        Assert.Empty(Assert.Single(package.Manifest.Databases).SiteIds);
        Assert.Equal(1, area.ExportDumpCalls);
    }

    [Fact]
    public async Task NativeTaskSecretsOnlyTravelInsideSelectedPrivatePayload()
    {
        using var area = new Area { TaskSecret = "fixture-TASK-SECRET" };
        var inventory = await area.Service.GetInventoryAsync();
        Assert.DoesNotContain(area.TaskSecret, JsonSerializer.Serialize(inventory));
        Assert.Empty(Assert.Single(inventory.Tasks).Arguments);
        Assert.Empty(inventory.Tasks[0].Url);
        var artifact = await area.Service.ExportAsync(new() { AllSites = true, IncludeMail = false, IncludeDns = false, IncludeTasks = true });
        await using var input = File.OpenRead(artifact.FilePath);
        var package = await area.Store.UploadAsync(input);
        Assert.DoesNotContain(area.TaskSecret, JsonSerializer.Serialize(package.Manifest));
        var task = Assert.Single(package.Manifest.Tasks);
        var path = Assert.Single(task.ArtifactPaths);
        await using var raw = await area.Store.OpenPayloadAsync(package.Id, path);
        using var json = await JsonDocument.ParseAsync(raw);
        Assert.Equal("adapanel-task-v1", json.RootElement.GetProperty("format").GetString());
        Assert.Contains(area.TaskSecret, json.RootElement.GetProperty("task").GetProperty("url").GetString());
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ada-export-fixture-" + Guid.NewGuid().ToString("N"));
        public string DataRoot => Path.Combine(Root, "data");
        public string FirstRoot => Path.Combine(Root, "source-first");
        public PortableMigrationPackageOptions Limits { get; } = new();
        public AdaPanelMigrationExporter Service { get; }
        public PortableMigrationPackageStore Store { get; }
        public string MailFailure = "";
        public bool DatabaseReadable = true;
        public string? DatabaseSite = "first.example";
        public string TaskSecret = "";
        public bool ExternalTask;
        public bool HiddenRootIsData;
        public bool DnsAvailable = true, DnsPresent = true, DnsModeConflict, DnsTransferConflict, DnsSecondary, SnapshotUnsupported;
        public string DnsSource = "windows-dns", DnsType = "Primary";
        public int LegacyDnsReads, SnapshotDnsReads;
        public int SourceMutations;
        public int ExportDumpCalls;
        public int ArchiveMailCalls;
        public Action? DumpAction;

        public Area()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(Path.Combine(FirstRoot, "public"));
            Directory.CreateDirectory(Path.Combine(FirstRoot, "empty"));
            File.WriteAllText(Path.Combine(FirstRoot, ".env"), "APP_ENV=fixture");
            File.WriteAllText(Path.Combine(FirstRoot, "public", "index.php"), "<?php echo 'fixture';");
            var second = Path.Combine(Root, "source-second");
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(second, "index.html"), "second");
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = DataRoot });
            var limits = Microsoft.Extensions.Options.Options.Create(Limits);
            var siteService = Stub<IIisSiteService>((method, args) => method.Name switch
            {
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([
                    new() { Name = "first.example", PhysicalPath = Path.Combine(FirstRoot, "public"), ApplicationRootPath = FirstRoot,
                        Bindings = [new() { Protocol = "http", HostName = "first.example", Port = 80 }] },
                    new() { Name = "second.example", PhysicalPath = second, ApplicationRootPath = second }
                ]),
                nameof(IIisSiteService.GetMigrationTargetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([
                    new() { Name = "first.example", PhysicalPath = Path.Combine(FirstRoot, "public"), ApplicationRootPath = FirstRoot },
                    new() { Name = "second.example", PhysicalPath = second, ApplicationRootPath = second },
                    new() { Name = "AdaPanel.WebDb.Adminer", PhysicalPath = HiddenRootIsData ? DataRoot : Path.Combine(Root, "private-tool-web") }
                ]),
                _ => throw Unexpected(method.Name)
            });
            var dbService = Stub<IDatabaseService>((method, args) => method.Name switch
            {
                nameof(IDatabaseService.GetEnginesAsync) => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([
                    new() { Engine = "mariadb", Installed = true, Ready = DatabaseReadable }]),
                nameof(IDatabaseService.GetWorkspaceAsync) => Task.FromResult(new DatabaseWorkspaceDto
                {
                    Engine = "mariadb", AdminPassword = "ADMIN_FIXTURE_SECRET",
                    Databases = [new() { Name = "demo", Engine = "mariadb", Encoding = "utf8mb4" }]
                }),
                nameof(IDatabaseService.GetDetailAsync) => Task.FromResult(new DatabaseDetailDto { SiteName = DatabaseSite }),
                nameof(IDatabaseService.ExportDumpAsync) => Export((DatabaseDumpRequest)args![0]!),
                _ => throw Unexpected(method.Name)
            });
            var mailService = Stub<IMailService>((method, args) => method.Name switch
            {
                nameof(IMailService.GetWorkspaceAsync) => Task.FromResult(new MailWorkspaceDto
                {
                    Installed = true, Configured = true, AccountsMeasured = args![0] is not null,
                    Domains = [new() { Name = "first.example", AccountCount = 1, AccountCountMeasured = true }],
                    Accounts = args[0] is null ? [] : [new() { Address = "info@first.example", MaxSizeMB = 1024 }]
                }),
                _ => throw Unexpected(method.Name)
            });
            var archiver = Stub<IMailAccountArchiver>((method, args) => method.Name switch
            {
                nameof(IMailAccountArchiver.ArchiveDomainAsync) => Archive((string)args![1]!),
                _ => throw Unexpected(method.Name)
            });
            var dns = Stub<IDnsService>((method, args) => method.Name switch
            {
                nameof(IDnsService.GetZoneAsync) => LegacyDns((string)args![0]!),
                nameof(IDnsService.GetMigrationSnapshotAsync) => SnapshotDns((string)args![0]!),
                _ => throw Unexpected(method.Name)
            });
            var scheduler = Stub<ISchedulerService>((method, args) => method.Name switch
            {
                nameof(ISchedulerService.GetTasksAsync) => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>([
                    new() { Name = "fixture-task", TaskType = "url", Url = "https://first.example/cron?token=" + TaskSecret, Arguments = TaskSecret,
                        WorkingDirectory = ExternalTask ? Path.GetTempPath() : FirstRoot,
                        ScheduleType = "daily", TimeOfDay = "03:00" }]),
                _ => throw Unexpected(method.Name)
            });
            Service = new(siteService, dbService, mailService, archiver, dns, scheduler, options, limits);
            Store = new(options, limits);
        }

        private Task<SiteDnsZoneDto> LegacyDns(string name) { LegacyDnsReads++; return DnsResult(name); }
        private Task<SiteDnsZoneDto> SnapshotDns(string name)
        {
            SnapshotDnsReads++;
            if (SnapshotUnsupported) throw new NotSupportedException("readonly snapshot unavailable");
            return DnsResult(name);
        }
        private Task<SiteDnsZoneDto> DnsResult(string name) => Task.FromResult(new SiteDnsZoneDto
        {
            ZoneName = name, Records = [new() { Host = "@", Type = "A", Value = "192.0.2.1" }],
            IsWindowsDnsAvailable = DnsAvailable, IsWindowsZonePresent = DnsPresent, Source = DnsSource,
            WindowsZoneType = DnsType, ModeConfigurationConflict = DnsModeConflict,
            TransferConfigurationConflict = DnsTransferConflict, IsSecondary = DnsSecondary
        });

        private Task<DatabaseDumpResultDto> Export(DatabaseDumpRequest request)
        {
            ExportDumpCalls++;
            Assert.True(request.PortableDefiners);
            DumpAction?.Invoke();
            File.WriteAllText(request.FilePath, "CREATE TABLE fixture (id int);");
            return Task.FromResult(new DatabaseDumpResultDto { FilePath = request.FilePath, Engine = request.Engine, DatabaseName = request.DatabaseName });
        }
        private Task<MailArchiveResultDto> Archive(string directory)
        {
            ArchiveMailCalls++;
            Directory.CreateDirectory(directory);
            if (MailFailure == "missing-account") return Task.FromResult(new MailArchiveResultDto());
            if (MailFailure == "failed-account") return Task.FromResult(new MailArchiveResultDto { Failures = ["fixture account denied"] });
            var path = Path.Combine(directory, "info@first.example.sqlite");
            File.WriteAllBytes(path, MailFailure == "empty-account" ? [] : Encoding.UTF8.GetBytes("fixture mailbox payload"));
            return Task.FromResult(new MailArchiveResultDto
            { Accounts = [new() { Address = "info@first.example", FilePath = path, EmailCount = 4 }] });
        }
        private Exception Unexpected(string method) { SourceMutations++; return new InvalidOperationException("Unexpected source call: " + method); }
        public void Dispose() { if (Directory.Exists(Root)) PortableMigrationStorage.DeleteOwnedDirectory(Root); }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
