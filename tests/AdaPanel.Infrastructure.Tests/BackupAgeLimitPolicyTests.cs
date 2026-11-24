using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupAgeLimitPolicyTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("last-copy")]
    [InlineData("reduced-coverage")]
    public async Task PositiveAgeLimit_ExpiresOldPointRegardlessOfCoverageOrReplacement(string scenario)
    {
        using var area = new BulutAlani();
        var old = Record(area, "old", 40);
        if (scenario == "legacy")
        {
            old.IncludedComponents = null;
            old.IncludedDatabases = null;
        }
        var recent = Record(area, "recent", 1);
        recent.IncludedComponents = [];
        recent.IncludedDatabases = [];
        recent.Status = "created_with_errors";
        recent.Warnings.Add(new() { MessageKey = "backup.database_dump_failed" });
        await area.KayitlariYazAsync(scenario == "reduced-coverage" ? [old, recent] : [old]);
        var service = area.ServisKur(new SahteBulut());

        var result = await service.PruneAsync(14, cancellationToken: area.Iptal);

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.RetainedExpired);
        Assert.Empty(result.RetentionNotes);
        Assert.False(File.Exists(old.Path));
        Assert.DoesNotContain(await service.GetBackupsAsync(area.Iptal), record => record.Id == old.Id);
        if (scenario == "reduced-coverage") Assert.True(File.Exists(recent.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgeLimit_PrunesWholeExpiredChainButKeepsAnActiveChain(bool allExpired)
    {
        using var area = new BulutAlani();
        var full = Record(area, "full", 40);
        var delta = Record(area, "delta", allExpired ? 30 : 1);
        full.ChainId = delta.ChainId = "chain";
        delta.ChainIndex = 1;
        delta.BackupMode = "incremental";
        delta.BaseBackupId = full.Id;
        await area.KayitlariYazAsync(full, delta);
        var service = area.ServisKur(new SahteBulut());

        var result = await service.PruneAsync(14, cancellationToken: area.Iptal);

        Assert.Equal(allExpired ? 2 : 0, result.Removed);
        Assert.Equal(!allExpired, File.Exists(full.Path));
        Assert.Equal(!allExpired, File.Exists(delta.Path));
        Assert.Equal(allExpired ? 0 : 2, (await service.GetBackupsAsync(area.Iptal)).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public async Task ExplicitPositiveAgeLimit_TakesPriorityOverMinimumCount(int keepCount)
    {
        using var area = new BulutAlani();
        var owner = Guid.NewGuid();
        var old = Record(area, "old", 40);
        old.Type = "subscription";
        old.Source = old.SubscriptionUserId = owner.ToString("N");
        await area.KayitlariYazAsync(old);
        var service = area.ServisKur(new SahteBulut());

        var result = await service.PruneSubscriptionAsync(owner, 14, keepCount, cancellationToken: area.Iptal);

        Assert.Equal(1, result.Removed);
        Assert.Empty(await service.GetBackupsAsync(area.Iptal));
        Assert.False(File.Exists(old.Path));
    }

    [Fact]
    public async Task ZeroDays_DisablesAgeDeletion()
    {
        using var area = new BulutAlani();
        var old = Record(area, "old", 400);
        await area.KayitlariYazAsync(old);
        var service = area.ServisKur(new SahteBulut());

        Assert.Equal(0, (await service.PruneAsync(0, cancellationToken: area.Iptal)).Removed);
        Assert.True(File.Exists(old.Path));
        Assert.Single(await service.GetBackupsAsync(area.Iptal));
    }

    [Fact]
    public void CountOnlyPruning_StillRequiresReplacementCoverage()
    {
        var now = DateTime.UtcNow;
        var old = new BackupRecordDto { Id = "old", Type = "site", Source = "site", CreatedAtUtc = now.AddDays(-40) };
        var recent = new BackupRecordDto { Id = "recent", Type = "site", Source = "site", CreatedAtUtc = now.AddDays(-1) };

        Assert.Empty(BackupPrunePlanner.Select([old, recent], DateTime.MinValue, 1, _ => true, (_, _) => false));
    }

    [Fact]
    public async Task ExpiredLastRemoteCopy_DeleteFailureIsReportedAndRecordRetainedForRetry()
    {
        using var area = new BulutAlani();
        var old = Record(area, "old", 40);
        old.TargetType = "ftp";
        old.RemoteUri = "ftp://yedek.ornek.com/yedekler/old.zip";
        await area.KayitlariYazAsync(old);
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "Permission denied"),
            ListeYaniti = new FtpListingResult(true, ["old.zip"], 226, "OK")
        };
        var service = area.ServisKur(new SahteBulut(), ftp: ftp);

        var result = await service.PruneAsync(14, new BackupPruneRemoteOptions { UserName = "user", Password = "password" }, area.Iptal);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.RemoteDeleteFailed);
        Assert.True(result.HasFailures);
        Assert.True(Assert.Single(await service.GetBackupsAsync(area.Iptal)).RemoteDeleteFailed);
        Assert.Contains(ftp.Komutlar, command => command.StartsWith("DELE", StringComparison.Ordinal));
    }

    private static BackupRecordDto Record(BulutAlani area, string id, int daysOld)
    {
        var path = Path.Combine(area.Kok, id + ".zip");
        File.WriteAllText(path, "owned backup");
        return new BackupRecordDto
        {
            Id = id, Name = id + ".zip", Path = path, Format = "zip", Type = "site", Source = "site",
            Status = "created", IntegrityState = BackupIntegrity.Sound, CreatedAtUtc = DateTime.UtcNow.AddDays(-daysOld),
            IncludedComponents = [new() { Kind = "files", Source = "site" }],
            IncludedDatabases = [new() { Engine = "mysql", DatabaseName = "shop" }]
        };
    }
}
