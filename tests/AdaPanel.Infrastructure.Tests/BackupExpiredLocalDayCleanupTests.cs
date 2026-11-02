using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupExpiredLocalDayCleanupTests
{
    [Fact]
    public async Task Entire_expiry_day_is_removed_including_unindexed_files_and_empty_folders()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var old = Path.Combine(root, "2026-10-27");
        var current = Path.Combine(root, "2026-10-28", "panel");
        Directory.CreateDirectory(Path.Combine(old, "sites", "shop", "left-empty"));
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "sites", "shop", "old.zip"), "old");
        File.WriteAllText(Path.Combine(old, "manifest.json"), "{}");
        var keep = Path.Combine(current, "new.zip");
        File.WriteAllText(keep, "new");
        var checks = 0;
        var report = await BackupExpiredLocalDayCleanup.SweepAsync(root, new DateTime(2026, 10, 27), [],
            _ => { checks++; return Task.CompletedTask; }, area.Iptal);
        Assert.Equal(1, report.RemovedDays);
        Assert.Empty(report.Failures);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(keep));
        Assert.True(Directory.Exists(root));
        Assert.Equal(6, checks); // Two files and four directories, each independently approved.
    }

    [Fact]
    public async Task Retained_chain_member_protects_the_entire_day()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var day = Path.Combine(root, "2026-10-27");
        Directory.CreateDirectory(day);
        var archive = Path.Combine(day, "base.zip");
        File.WriteAllText(archive, "base");
        var report = await BackupExpiredLocalDayCleanup.SweepAsync(root, new DateTime(2026, 10, 27), [archive], null, area.Iptal);
        Assert.Equal(0, report.RemovedDays);
        Assert.Empty(report.Failures);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Changed_authority_or_cancellation_stops_mutations()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var day = Path.Combine(root, "2026-10-27");
        Directory.CreateDirectory(day);
        var file = Path.Combine(day, "old.zip");
        File.WriteAllText(file, "old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupExpiredLocalDayCleanup.SweepAsync(root,
            new DateTime(2026, 10, 27), [], _ => throw new InvalidOperationException("revoked"), area.Iptal));
        Assert.True(File.Exists(file));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupExpiredLocalDayCleanup.SweepAsync(root,
            new DateTime(2026, 10, 27), [], null, cancelled.Token));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Cancellation_in_mutation_gate_prevents_file_and_directory_deletion()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var day = Path.Combine(root, "2026-10-27");
        Directory.CreateDirectory(day);
        var file = Path.Combine(day, "old.zip");
        File.WriteAllText(file, "keep");
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupExpiredLocalDayCleanup.SweepAsync(root,
            new DateTime(2026, 10, 27), [], _ => { cancelled.Cancel(); return Task.CompletedTask; }, cancelled.Token));
        Assert.True(File.Exists(file));
        File.Delete(file);
        using var cancelledDirectory = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupExpiredLocalDayCleanup.SweepAsync(root,
            new DateTime(2026, 10, 27), [], _ => { cancelledDirectory.Cancel(); return Task.CompletedTask; }, cancelledDirectory.Token));
        Assert.True(Directory.Exists(day));
    }

    [Fact]
    public async Task Server_prune_cleans_an_old_day_even_without_index_records()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var day = Path.Combine(root, DateTime.Today.AddDays(-5).ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(Path.Combine(day, "panel"));
        File.WriteAllText(Path.Combine(day, "panel", "unindexed.txt"), "leftover");
        var result = await area.ServisKur(new SahteBulut()).PruneAsync(5, cancellationToken: area.Iptal);
        Assert.False(result.HasFailures);
        Assert.False(Directory.Exists(day));
        Assert.Contains("local_expired_day_folders_removed=1", result.RetentionNotes);
    }

    [Fact]
    public async Task Server_prune_uses_the_persisted_custom_root_and_preserves_default_and_sibling_roots()
    {
        using var area = new BulutAlani();
        var custom = Path.Combine(area.Kok, "custom-backups");
        var oldDay = DateTime.Today.AddDays(-5).ToString("yyyy-MM-dd");
        var day = Path.Combine(custom, oldDay);
        var defaultDay = Path.Combine(area.Kok, "backups", oldDay);
        var siblingDay = Path.Combine(area.Kok, "custom-backups-other", oldDay);
        foreach (var path in new[] { day, defaultDay, siblingDay })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "leftover.txt"), "backup");
        }
        await area.DosyaDeposu.WriteAsync("config/panel-preferences.json",
            new AdaPanel.Core.Dtos.Settings.PanelPreferencesDto { DefaultBackupRoot = custom }, area.Iptal);
        var result = await area.ServisKur(new SahteBulut()).PruneAsync(5, cancellationToken: area.Iptal);
        Assert.False(result.HasFailures);
        Assert.False(Directory.Exists(day));
        Assert.True(Directory.Exists(defaultDay));
        Assert.True(Directory.Exists(siblingDay));
    }

    [Fact]
    public async Task Subscription_and_disabled_retention_do_not_sweep_shared_day_folders()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "backups");
        var day = Path.Combine(root, "2020-01-01");
        Directory.CreateDirectory(day);
        var service = area.ServisKur(new SahteBulut());
        await service.PruneAsync(0, cancellationToken: area.Iptal);
        await service.PruneSubscriptionAsync(Guid.NewGuid(), 5, 0, cancellationToken: area.Iptal);
        Assert.True(Directory.Exists(day));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Link_in_day_or_root_is_never_followed(bool rootLink)
    {
        using var area = new BulutAlani();
        var outside = Path.Combine(area.Kok, "precious");
        var root = Path.Combine(area.Kok, "backups");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var link = rootLink ? root : Path.Combine(root, "2026-10-27", "panel");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        // Windows junction creation needs no symlink privilege.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{link}\" \"{outside}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var report = await BackupExpiredLocalDayCleanup.SweepAsync(root, new DateTime(2026, 10, 27), [], null, area.Iptal);
            Assert.NotEmpty(report.Failures);
            Assert.Equal(0, report.RemovedDays);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }
}
