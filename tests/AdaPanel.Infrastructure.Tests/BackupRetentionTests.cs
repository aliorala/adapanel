using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupRetentionTests
{
    [Fact]
    public async Task PruneAsync_RemovesExpiredLocalBackupsAndKeepsRecentOnes()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            var fileStore = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

            var backupsDir = fileStore.EnsureDirectory("backups");
            var oldPath = Path.Combine(backupsDir, "old.zip");
            var recentPath = Path.Combine(backupsDir, "recent.zip");
            await File.WriteAllTextAsync(oldPath, "old");
            await File.WriteAllTextAsync(recentPath, "recent");

            var records = new[]
            {
                new BackupRecordDto
                {
                    Id = "old",
                    Name = "old.zip",
                    Path = oldPath,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-30)
                },
                new BackupRecordDto
                {
                    Id = "recent",
                    Name = "recent.zip",
                    Path = recentPath,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
                }
            };

            await fileStore.WriteAsync("config/backups.json", records);

            var service = new BackupService(null!, null!, null!, null!, null!, fileStore, null!);

            var removed = await service.PruneAsync(retentionDays: 7);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(recentPath));

            var survivors = await service.GetBackupsAsync();
            Assert.Single(survivors);
            Assert.Equal("recent", survivors.First().Id);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PruneAsync_WithZeroRetention_DeletesNothing()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            var fileStore = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

            await fileStore.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto { Id = "old", CreatedAtUtc = DateTime.UtcNow.AddYears(-1) }
            });

            var service = new BackupService(null!, null!, null!, null!, null!, fileStore, null!);

            var removed = await service.PruneAsync(retentionDays: 0);

            Assert.Equal(0, removed);
            Assert.Single(await service.GetBackupsAsync());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}

