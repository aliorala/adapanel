using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GranÃ¼ler geri yÃ¼kleme: zip iÃ§eriÄŸine dizin bazlÄ± gÃ¶zatma ve seÃ§ilen
/// dosya/dizinlerin hedefe Ã§Ä±karÄ±lmasÄ± (zip-slip korumasÄ± dahil).
/// </summary>
public sealed class BackupGranularRestoreTests
{
    [Fact]
    public async Task GetEntries_And_RestoreItems_WorkOnRealZip()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-granular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            var fileStore = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

            // GerÃ§ek bir site yedeÄŸi gibi: kÃ¶k klasÃ¶r adÄ± dahil (includeBaseDirectory).
            var siteRoot = Path.Combine(dataRoot, "source", "webroot");
            Directory.CreateDirectory(Path.Combine(siteRoot, "wp-content", "uploads"));
            await File.WriteAllTextAsync(Path.Combine(siteRoot, "index.php"), "<?php echo 'merhaba';");
            await File.WriteAllTextAsync(Path.Combine(siteRoot, "wp-config.php"), "<?php // config");
            await File.WriteAllTextAsync(Path.Combine(siteRoot, "wp-content", "uploads", "logo.png"), "PNG");

            var zipPath = Path.Combine(dataRoot, "site.zip");
            ZipFile.CreateFromDirectory(siteRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);

            await fileStore.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto
                {
                    Id = "b1",
                    Name = "site.zip",
                    Type = "site",
                    Source = "example.com",
                    Path = zipPath,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow
                }
            });

            var service = new BackupService(null!, null!, null!, null!, null!, fileStore, null!);

            // KÃ¶k listeleme: tek dizin (webroot).
            var root = await service.GetEntriesAsync("b1", null);
            Assert.Single(root.Entries);
            Assert.True(root.Entries[0].IsDirectory);
            Assert.Equal("webroot/", root.Entries[0].Path);

            // webroot altÄ±nda: wp-content dizini + 2 dosya.
            var level1 = await service.GetEntriesAsync("b1", "webroot/");
            Assert.Contains(level1.Entries, entry => entry.IsDirectory && entry.Name == "wp-content");
            Assert.Contains(level1.Entries, entry => !entry.IsDirectory && entry.Name == "index.php");
            Assert.Contains(level1.Entries, entry => !entry.IsDirectory && entry.Name == "wp-config.php");

            // Tek dosya + bir dizini hedefe geri yÃ¼kle.
            var restoreTarget = Path.Combine(dataRoot, "restored");
            var result = await service.RestoreItemsAsync(new RestoreBackupItemsRequest
            {
                BackupId = "b1",
                Items = ["webroot/index.php", "webroot/wp-content/"],
                TargetPath = restoreTarget
            });

            Assert.Equal(2, result.RestoredCount);
            Assert.True(File.Exists(Path.Combine(restoreTarget, "webroot", "index.php")));
            Assert.True(File.Exists(Path.Combine(restoreTarget, "webroot", "wp-content", "uploads", "logo.png")));
            Assert.False(File.Exists(Path.Combine(restoreTarget, "webroot", "wp-config.php")));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreItems_UnknownItem_Throws()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-granular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            var fileStore = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

            var sourceDir = Path.Combine(dataRoot, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "a");
            var zipPath = Path.Combine(dataRoot, "x.zip");
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);

            await fileStore.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto { Id = "b2", Path = zipPath, Format = "zip", Type = "site", Source = "x", CreatedAtUtc = DateTime.UtcNow }
            });

            var service = new BackupService(null!, null!, null!, null!, null!, fileStore, null!);

            var exception = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() =>
                service.RestoreItemsAsync(new RestoreBackupItemsRequest
                {
                    BackupId = "b2",
                    Items = ["yok/boyle-bir-dosya.txt"],
                    TargetPath = Path.Combine(dataRoot, "out")
                }));

            Assert.Equal("backup.items_not_found", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}

