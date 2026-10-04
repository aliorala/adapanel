using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupArchiveCollisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RepeatedSameSecondArchives_PreserveEveryCompletedArtifact(bool subscription, bool incremental)
    {
        using var fixture = new Fixture();
        var artifacts = new List<(BackupRecordDto Record, string Hash, string Contents)>();
        for (var i = 0; i < 3; i++)
        {
            var contents = new string((char)('a' + i), 10 + i);
            await File.WriteAllTextAsync(Path.Combine(fixture.SiteRoot, "sample.txt"), contents);
            var record = await fixture.Backup(subscription, incremental);
            artifacts.Add((record, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(record.Path))), contents));
        }

        Assert.Equal(3, artifacts.Select(item => item.Record.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, (await fixture.Service.GetBackupsAsync()).Count);
        foreach (var artifact in artifacts)
        {
            Assert.Equal(artifact.Hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifact.Record.Path))));
            using var zip = ZipFile.OpenRead(artifact.Record.Path);
            using var reader = new StreamReader(zip.GetEntry(subscription ? "sites/site/files/sample.txt" : "files/sample.txt")!.Open());
            Assert.Equal(artifact.Contents, await reader.ReadToEndAsync());
        }
        if (incremental)
            Assert.Equal(new[] { "full", "incremental", "incremental" }, artifacts.Select(item => item.Record.BackupMode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSameSecondArchive_IsNeverDeleted(bool subscription)
    {
        using var fixture = new Fixture();
        var placement = subscription
            ? BackupLayout.Ensure(fixture.BackupsRoot, "subscriptions", Fixture.Owner.ToString("N"))
            : BackupLayout.Ensure(fixture.BackupsRoot, "sites", "site");
        var label = subscription ? $"abonelik-{Fixture.Owner:N}"[..17] : "site";
        var oldPath = Path.Combine(placement.LocalDirectory, $"{label}-20400102030405.zip");
        await File.WriteAllTextAsync(oldPath, "existing archive sentinel");

        var result = await fixture.Backup(subscription, false);

        Assert.Equal("existing archive sentinel", await File.ReadAllTextAsync(oldPath));
        Assert.NotEqual(oldPath, result.Path, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000013");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-archive-collision-{Guid.NewGuid():N}");
        public string SiteRoot => Path.Combine(Root, "site");
        public string BackupsRoot => Path.Combine(Root, "backups");
        public BackupService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(SiteRoot);
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(new SiteDto { Name = "site", PhysicalPath = SiteRoot }) : throw new NotSupportedException(method.Name));
            var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = BackupsRoot }) : throw new NotSupportedException(method.Name));
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name switch
            {
                "GetScopeAsync" => Task.FromResult(new CustomerScope(["site"], 0, 0, 0, 0, 0)),
                "GetSiteOwnerUserIdAsync" => Task.FromResult<Guid?>(Owner),
                _ => throw new NotSupportedException(method.Name)
            });
            var ownership = Fake<IDatabaseOwnershipService>((method, _) => throw new NotSupportedException(method.Name));
            Service = new BackupService(sites, null!, null!, preferences, null!, store, null!, customerScopes: scopes, databaseOwnership: ownership, timeProvider: new FixedClock());
        }

        public Task<BackupRecordDto> Backup(bool subscription, bool incremental) => subscription
            ? Service.BackupSubscriptionAsync(new BackupSubscriptionRequest { UserId = Owner, IncludeFiles = true, IncludeDatabases = false, IncludeMail = false, IncludeFtp = false, Incremental = incremental })
            : Service.BackupSiteAsync(new BackupSiteRequest { SiteName = "site", IncludeFiles = true, Incremental = incremental });

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-archive-collision-"), absolute, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
