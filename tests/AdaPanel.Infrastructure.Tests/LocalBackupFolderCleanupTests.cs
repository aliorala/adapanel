using AdaPanel.Core.Dtos.Backup;
using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class LocalBackupFolderCleanupTests
{
    [Theory]
    [InlineData("sites/shop")]
    [InlineData("sites/shop/databases/mysql-shop")]
    [InlineData("databases/mysql-shop")]
    [InlineData("subscriptions/customer")]
    [InlineData("mail")]
    [InlineData("panel")]
    [InlineData("")]
    public async Task Deleting_last_backup_removes_empty_day_but_preserves_root(string levels)
    {
        using var area = new BulutAlani();
        var record = Create(area, "old", "2026-09-01", levels);
        await area.KayitlariYazAsync(record);
        await area.ServisKur(new SahteBulut()).DeleteBackupAsync(record.Id, area.Iptal);
        Assert.False(File.Exists(record.Path));
        Assert.False(Directory.Exists(Path.Combine(area.Kok, "backups", "2026-09-01")));
        Assert.True(Directory.Exists(Path.Combine(area.Kok, "backups")));
    }

    [Fact]
    public async Task Retention_removes_empty_old_day_and_keeps_new_backup()
    {
        using var area = new BulutAlani();
        var old = Create(area, "old", "2026-09-01", "sites/shop");
        old.CreatedAtUtc = DateTime.UtcNow.AddDays(-10);
        var current = Create(area, "new", "2026-09-17", "sites/shop");
        await area.KayitlariYazAsync(old, current);
        var result = await area.ServisKur(new SahteBulut()).PruneAsync(5, cancellationToken: area.Iptal);
        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(Path.Combine(area.Kok, "backups", "2026-09-01")));
        Assert.True(File.Exists(current.Path));
    }

    [Fact]
    public async Task Other_files_and_unknown_layout_are_preserved()
    {
        using var area = new BulutAlani();
        var record = Create(area, "old", "2026-09-01", "sites/shop");
        var other = Path.Combine(Path.GetDirectoryName(record.Path)!, "keep.txt");
        File.WriteAllText(other, "keep");
        var unknown = Create(area, "custom", "2026-09-02", "custom/user");
        await area.KayitlariYazAsync(record, unknown);
        var service = area.ServisKur(new SahteBulut());
        await service.DeleteBackupAsync(record.Id, area.Iptal);
        await service.DeleteBackupAsync(unknown.Id, area.Iptal);
        Assert.Equal("keep", File.ReadAllText(other));
        Assert.True(Directory.Exists(Path.GetDirectoryName(unknown.Path)));
    }

    private static BackupRecordDto Create(BulutAlani area, string id, string day, string levels)
    {
        var directory = Path.Combine(area.Kok, "backups", day, levels.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".zip");
        File.WriteAllText(path, "backup fixture");
        return new BackupRecordDto { Id = id, Name = id + ".zip", Type = "site", Source = BulutAlani.Site,
            Path = path, Format = "zip", CreatedAtUtc = DateTime.UtcNow, TargetType = "local" };
    }

    [Fact]
    public void Dated_root_and_encoded_folder_names_are_preserved_correctly()
    {
        using var area = new BulutAlani();
        var root = Path.Combine(area.Kok, "2026-09-01");
        var parent = Path.Combine(root, "2026-09-02", "sites", "Türkçe %20 # site");
        Directory.CreateDirectory(parent);
        BackupLocalCleanup.RemoveEmptyParents(Path.Combine(parent, "gone.zip"), root);
        Assert.False(Directory.Exists(Path.Combine(root, "2026-09-02")));
        Assert.True(Directory.Exists(root));
        BackupLocalCleanup.RemoveEmptyParents(Path.Combine(root, "gone.zip"), root);
        Assert.True(Directory.Exists(root));
    }

    [Theory]
    [InlineData("backups-other")]
    [InlineData("outside")]
    public void Folders_outside_configured_root_are_untouched(string sibling)
    {
        using var area = new BulutAlani();
        var parent = Path.Combine(area.Kok, sibling, "2026-09-01", "panel");
        Directory.CreateDirectory(parent);
        BackupLocalCleanup.RemoveEmptyParents(Path.Combine(parent, "gone.zip"), Path.Combine(area.Kok, "backups"));
        Assert.True(Directory.Exists(parent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Junction_in_root_or_parent_does_not_remove_external_folders(bool rootLink)
    {
        using var area = new BulutAlani();
        var outside = Path.Combine(area.Kok, "outside");
        var backups = Path.Combine(area.Kok, "backups");
        var link = rootLink ? backups : Path.Combine(backups, "2026-09-01");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var parent = rootLink ? Path.Combine(link, "2026-09-01", "panel") : Path.Combine(link, "panel");
            Directory.CreateDirectory(parent);
            Assert.Throws<AppException>(() => BackupLocalCleanup.RemoveEmptyParents(Path.Combine(parent, "gone.zip"), backups));
            Assert.True(Directory.Exists(parent));
        }
        finally { Directory.Delete(link); }
    }
}
