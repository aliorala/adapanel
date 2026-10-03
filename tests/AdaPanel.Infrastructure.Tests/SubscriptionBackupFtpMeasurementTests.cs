using AdaPanel.Core.Dtos.Ftp;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SubscriptionBackupFtpMeasurementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableInventoryProducesPartialArchiveAndDurableWarning(bool reportsUnavailable)
    {
        using var fixture = new F();
        var failure = reportsUnavailable ? new InvalidOperationException("FTP provider unavailable")
            : (Exception)new IOException("Unable to read config/ftp-users.json");
        fixture.ReadFtp = _ => Task.FromException<IReadOnlyCollection<FtpUserDto>>(failure);
        await fixture.Ownership.RecordAsync("mysql", "own_database", F.Owner, F.Site);

        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request(includeFtp: true));

        Assert.Equal(1, fixture.FtpReads);
        Assert.Equal("created_with_errors", record.Status);
        var warning = Assert.Single(record.Warnings);
        Assert.Equal("backup.ftp_inventory_unreadable", warning.MessageKey);
        Assert.Equal("ftp", warning.Target);
        Assert.Equal(failure.Message, warning.Detail);
        var persisted = Assert.Single(await fixture.Persisted());
        Assert.Equal(record.Status, persisted.Status);
        Assert.Equal(warning.MessageKey, Assert.Single(persisted.Warnings).MessageKey);
        Assert.Contains($"sites/{F.Site}/files/index.html", F.Entries(record));
        Assert.Contains("databases/mysql-own_database.sql", F.Entries(record));
        Assert.DoesNotContain("ftp/accounts.json", F.Entries(record));
        Assert.DoesNotContain(record.IncludedComponents!, part => part.Kind == "ftp");
        Assert.Equal("own_database", Assert.Single(record.IncludedDatabases!).DatabaseName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringFtpReadNeverPublishesAnArchive(bool cancelCaller)
    {
        using var fixture = new F();
        using var cancellation = new CancellationTokenSource();
        fixture.ReadFtp = token =>
        {
            if (cancelCaller) cancellation.Cancel();
            return Task.FromException<IReadOnlyCollection<FtpUserDto>>(new OperationCanceledException("FTP inventory canceled", token));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.BackupSubscriptionAsync(fixture.Request(includeFtp: true), cancellation.Token));
        Assert.Equal(1, fixture.FtpReads);
        Assert.Empty(await fixture.Persisted());
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(fixture.Exported);
    }

    [Fact]
    public async Task ExcludedFtpDoesNotReadInventoryOrWarn()
    {
        using var fixture = new F();
        fixture.ReadFtp = _ => throw new IOException("This excluded inventory must not be read");
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request(includeFtp: false));
        Assert.Equal(0, fixture.FtpReads);
        Assert.Equal("created", record.Status);
        Assert.Empty(record.Warnings);
        Assert.DoesNotContain("ftp/accounts.json", F.Entries(record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFtpInstallationOrMeasuredEmptyInventoryIsAValidEmptyResult(bool inventoryFileExists)
    {
        using var fixture = new F(realEmptyFtp: true);
        // Real FtpService only reads the local JSON: with no rows it runs no OS/account commands.
        if (inventoryFileExists) await fixture.Store.WriteAsync("config/ftp-users.json", Array.Empty<FtpUserDto>());
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request(includeFtp: true));
        Assert.Equal(1, fixture.FtpReads);
        Assert.Equal("created", record.Status);
        Assert.Empty(record.Warnings);
        Assert.Empty(Assert.Single(await fixture.Persisted()).Warnings);
        Assert.DoesNotContain(record.IncludedComponents!, part => part.Kind == "ftp");
        Assert.DoesNotContain("ftp/accounts.json", F.Entries(record));
    }

    [Fact]
    public async Task MeasuredFtpIncludesOnlyAccountsForScopedSites()
    {
        using var fixture = new F();
        fixture.ReadFtp = _ => Task.FromResult<IReadOnlyCollection<FtpUserDto>>([
            new() { UserName = "owned-ftp", SiteName = F.Site, HomePath = Path.Combine(fixture.Root, "www") },
            new() { UserName = "foreign-ftp", SiteName = F.ForeignSite },
            new() { UserName = "server-ftp", SiteName = "" }
        ]);
        var record = await fixture.Service.BackupSubscriptionAsync(fixture.Request(includeFtp: true));
        Assert.Equal("created", record.Status);
        Assert.Empty(record.Warnings);
        Assert.Equal("owned-ftp", Assert.Single(record.IncludedComponents!, part => part.Kind == "ftp").Source);
        using var accounts = F.ReadEntry(record, "ftp/accounts.json");
        Assert.Equal("owned-ftp", Assert.Single(accounts.RootElement.EnumerateArray()).GetProperty("UserName").GetString());
    }
}
