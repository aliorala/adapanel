using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupProducerArtifactLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionDumpFailure_PreservesPartialOutcomeButCleansCanceledArchive(bool canceled)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Export = async (request, token) =>
        {
            await File.WriteAllTextAsync(request.FilePath, "partial dump", CancellationToken.None);
            if (canceled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new IOException("export failed");
        };
        var request = new BackupSubscriptionRequest
        {
            UserId = Fixture.Owner, IncludeFiles = true, IncludeDatabases = true, IncludeMail = false, IncludeFtp = false
        };

        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.BackupSubscriptionAsync(request, cancellation.Token));
            Assert.Empty(Directory.GetFiles(fixture.Backups, "*", SearchOption.AllDirectories));
            Assert.Empty(await fixture.Service.GetBackupsAsync());
        }
        else
        {
            var record = await fixture.Service.BackupSubscriptionAsync(request);
            Assert.True(File.Exists(record.Path));
            Assert.Contains(record.Warnings, warning => warning.MessageKey == "backup.database_dump_failed");
            Assert.Single(await fixture.Service.GetBackupsAsync());
        }
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailArchiveFailure_DeletesOnlyOwnedOutput(bool collision)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Backups, "mail.zip");
        if (collision) await File.WriteAllTextAsync(output, "previous completed archive");
        using var cancellation = new CancellationTokenSource();
        if (!collision) cancellation.Cancel();
        var method = typeof(BackupService).GetMethod("CreateMailArchiveAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        var failure = await Record.ExceptionAsync(async () => await (Task)method.Invoke(null,
            [Path.GetDirectoryName(fixture.Source)!, output, cancellation.Token])!);

        if (collision)
        {
            Assert.IsType<IOException>(failure);
            Assert.Equal("previous completed archive", await File.ReadAllTextAsync(output));
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.False(File.Exists(output));
        }
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedOrCanceledProducer_RemovesOnlyItsUnpublishedOutput(bool site, bool canceled)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var sentinel = Path.Combine(fixture.Backups, "previous.zip");
        await File.WriteAllTextAsync(sentinel, "completed backup");
        fixture.Export = async (request, token) =>
        {
            await File.WriteAllTextAsync(request.FilePath, "partial dump", CancellationToken.None);
            if (canceled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new IOException("export failed");
        };

        var failure = await Record.ExceptionAsync(() => fixture.Backup(site, cancellation.Token));

        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<IOException>(failure);
        Assert.Equal(new[] { sentinel }, Directory.GetFiles(fixture.Backups, "*", SearchOption.AllDirectories));
        Assert.Equal("completed backup", await File.ReadAllTextAsync(sentinel));
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
        Assert.Empty(await fixture.Service.GetBackupsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexPublicationFailure_RemovesFinalArtifactAfterWrapping(bool encrypted)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config", "backups.json"));

        var failure = await Record.ExceptionAsync(() => fixture.Service.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mysql", DatabaseName = "shop", EncryptionPassword = encrypted ? "password" : ""
        }));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Backups, "*", SearchOption.AllDirectories));
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapperCancellation_RemovesStagingOutputAndPropagatesCancellation(bool encryption)
    {
        using var fixture = new Fixture();
        var input = Path.Combine(fixture.Backups, "dump.sql");
        await File.WriteAllTextAsync(input, "plain dump");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wrap(fixture.Service, input, encryption, cancellation.Token));

        Assert.Empty(Directory.GetFiles(fixture.Backups, "*.tmp-*"));
        Assert.False(File.Exists(input + (encryption ? ".enc" : ".zst")));
        // Compression cancellation leaves its source to the owning finalizer; encryption
        // failure removes plaintext according to its existing confidentiality contract.
        Assert.Equal(!encryption, File.Exists(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapperCollision_PreservesExistingOutput(bool encryption)
    {
        using var fixture = new Fixture();
        var input = Path.Combine(fixture.Backups, "dump.sql");
        var collision = input + (encryption ? ".enc" : ".zst");
        await File.WriteAllTextAsync(input, "plain dump");
        await File.WriteAllTextAsync(collision, "existing successful archive");

        if (encryption)
            await Assert.ThrowsAsync<AppException>(() => Wrap(fixture.Service, input, true, CancellationToken.None));
        else
            await Wrap(fixture.Service, input, false, CancellationToken.None);

        Assert.Equal("existing successful archive", await File.ReadAllTextAsync(collision));
        Assert.Empty(Directory.GetFiles(fixture.Backups, "*.tmp-*"));
        if (!encryption) Assert.Equal("plain dump", await File.ReadAllTextAsync(input));
    }

    [Fact]
    public async Task ChainIndexPublicationFailure_RemovesArchiveAndOwnedSidecar()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config", "backups.json"));

        var failure = await Record.ExceptionAsync(() => fixture.Service.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "site", IncludeFiles = true, Incremental = true
        }));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Backups, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "backups", "index"), "*"));
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
    }

    [Fact]
    public async Task SuccessfulPublication_PreservesWrappedArtifactAndSourceData()
    {
        using var fixture = new Fixture();
        var record = await fixture.Service.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mysql", DatabaseName = "shop", EncryptionPassword = "password"
        });

        Assert.True(File.Exists(record.Path));
        Assert.EndsWith(".sql.zst.enc", record.Path);
        Assert.Single(await fixture.Service.GetBackupsAsync());
        Assert.Equal(new[] { record.Path }, Directory.GetFiles(fixture.Backups, "*", SearchOption.AllDirectories));
        Assert.Equal("live source", await File.ReadAllTextAsync(fixture.Source));
    }

    private static async Task Wrap(BackupService service, string input, bool encryption, CancellationToken token)
    {
        var method = typeof(BackupService).GetMethod(encryption ? "ProtectArchiveAsync" : "CompressArchiveAsync",
            BindingFlags.NonPublic | (encryption ? BindingFlags.Static : BindingFlags.Instance))!;
        object?[] arguments = encryption
            ? [input, Path.GetFileName(input), "password", token]
            : [input, Path.GetFileName(input), "sql", new List<BackupRestoreWarningDto>(), token];
        await (Task)method.Invoke(encryption ? null : service, arguments)!;
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000014");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-producer-lifetime-" + Guid.NewGuid().ToString("N"));
        public string Backups => Path.Combine(Root, "backups");
        public string Source => Path.Combine(Root, "site", "source.txt");
        public BackupService Service { get; }
        public Func<DatabaseDumpRequest, CancellationToken, Task<DatabaseDumpResultDto>> Export { get; set; } = async (request, token) =>
        {
            await File.WriteAllTextAsync(request.FilePath, "CREATE TABLE shop (id INT);", token);
            return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath, Size = new FileInfo(request.FilePath).Length };
        };

        public Fixture()
        {
            Directory.CreateDirectory(Backups);
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
            File.WriteAllText(Source, "live source");
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(new SiteDto { Name = "site", PhysicalPath = Path.GetDirectoryName(Source)! }),
                _ => throw new NotSupportedException(method.Name)
            });
            var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Backups }) : throw new NotSupportedException(method.Name));
            var databases = Fake<IDatabaseService>((method, args) => method.Name == "ExportDumpAsync"
                ? Export((DatabaseDumpRequest)args[0]!, (CancellationToken)args[1]!) : throw new NotSupportedException(method.Name));
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetScopeAsync" => Task.FromResult(new CustomerScope(["site"], 0, 0, 0, 0, 0)),
                "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(null),
                _ => throw new NotSupportedException(method.Name)
            });
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => method.Name == "GetAllAsync"
                ? Task.FromResult<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>>([("mysql", "shop", Owner, "site")])
                : throw new NotSupportedException(method.Name));
            Service = new BackupService(sites, databases, null!, preferences, null!, store, null!, customerScopes: scopes, databaseOwnership: ownership);
        }

        public Task<BackupRecordDto> Backup(bool site, CancellationToken token) => site
            ? Service.BackupSiteAsync(new BackupSiteRequest
            {
                SiteName = "site", IncludeFiles = true,
                Databases = [new() { Engine = "mysql", DatabaseName = "shop" }]
            }, token)
            : Service.BackupDatabaseAsync(new BackupDatabaseRequest { Engine = "mysql", DatabaseName = "shop" }, token);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
