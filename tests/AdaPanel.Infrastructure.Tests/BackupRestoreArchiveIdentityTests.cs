using System.IO.Compression;
using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupRestoreArchiveIdentityTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("chain")]
    [InlineData("prune")]
    public async Task DeletingARestoredBackupNeverDeletesTheRestoredTarget(string operation)
    {
        using var f = new Fixture();
        var original = f.Archive("original", DateTime.UtcNow.AddDays(-30));
        await f.Area.KayitlariYazAsync(original);
        await f.Service.RestoreAsync(new RestoreBackupRequest { BackupId = original.Id, TargetPath = f.Target }, f.Area.Iptal);
        if (operation == "prune")
        {
            var restored = (await f.Service.GetBackupsAsync(f.Area.Iptal)).Single();
            await f.Area.KayitlariYazAsync(restored, f.Archive("replacement", DateTime.UtcNow));
        }
        switch (operation)
        {
            case "single": await f.Service.DeleteBackupAsync(original.Id, f.Area.Iptal); break;
            case "chain": Assert.Equal([original.Id], (await f.Service.DeleteBackupChainAsync(original.Id, [original.Id], f.Area.Iptal)).DeletedIds); break;
            default: Assert.Equal(1, (await f.Service.PruneAsync(14, cancellationToken: f.Area.Iptal)).Removed); break;
        }
        Assert.Equal("unrelated live data", File.ReadAllText(f.Sentinel));
        Assert.Equal("restored payload", File.ReadAllText(Path.Combine(f.Target, "payload.txt")));
        Assert.False(File.Exists(original.Path));
        Assert.DoesNotContain(await f.Service.GetBackupsAsync(f.Area.Iptal), item => item.Id == original.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreKeepsTheOriginalArchiveIdentityForAnotherRestore(bool encrypted)
    {
        using var f = new Fixture();
        var original = f.Archive("original", DateTime.UtcNow);
        const string password = "fixture-archive-password";
        if (encrypted)
        {
            var plainPath = original.Path;
            original.Path += ".enc";
            await BackupArchiveCipher.EncryptFileAsync(plainPath, original.Path, password, f.Area.Iptal);
            File.Delete(plainPath);
            original.Encrypted = true;
            original.Name += ".enc";
            original.Size = new FileInfo(original.Path).Length;
        }
        var archiveBytes = File.ReadAllBytes(original.Path);
        await f.Area.KayitlariYazAsync(original);
        var restored = await f.Service.RestoreAsync(new RestoreBackupRequest
        {
            BackupId = original.Id, TargetPath = f.Target, EncryptionPassword = encrypted ? password : ""
        }, f.Area.Iptal);
        Assert.Equal(original.Path, restored.Path);
        Assert.Equal(f.Target, restored.RestoredPath);
        Assert.Equal(encrypted, restored.Encrypted);
        var stored = (await f.Service.GetBackupsAsync(f.Area.Iptal)).Single();
        Assert.Equal(original.Path, stored.Path);
        Assert.Equal(f.Target, stored.RestoredPath);
        Assert.Equal(encrypted, stored.Encrypted);
        Assert.Equal(encrypted, BackupArchiveCipher.IsEncryptedFile(stored.Path));
        Assert.Equal(archiveBytes, File.ReadAllBytes(original.Path));
        var secondTarget = Path.Combine(f.Target, "second-restore");
        var second = await f.Service.RestoreAsync(new RestoreBackupRequest
        {
            BackupId = original.Id, TargetPath = secondTarget, EncryptionPassword = encrypted ? password : ""
        }, f.Area.Iptal);
        Assert.Equal(secondTarget, second.RestoredPath);
        Assert.Equal(secondTarget, (await f.Service.GetBackupsAsync(f.Area.Iptal)).Single().RestoredPath);
        Assert.Equal(original.Path, second.Path);
        Assert.Equal(archiveBytes, File.ReadAllBytes(second.Path));
        Assert.Equal("restored payload", File.ReadAllText(Path.Combine(secondTarget, "payload.txt")));
        Assert.Equal("unrelated live data", File.ReadAllText(f.Sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalDirectoryRecordsCannotAuthorizeRecursiveDeletion(bool prune)
    {
        using var f = new Fixture();
        var historical = f.Archive("historical", DateTime.UtcNow.AddDays(-30));
        historical.Path = f.Target;
        historical.Status = "restored";
        historical.TargetType = "webdav";
        historical.RemoteUri = "https://backup.example.test/historical.zip";
        var replacement = f.Archive("replacement", DateTime.UtcNow);
        replacement.TargetType = "webdav";
        replacement.RemoteUri = "https://backup.example.test/replacement.zip";
        replacement.Status = "created_remote_synced";
        await f.Area.UzakHedefiYazAsync("webdav", "https://backup.example.test", "fixture", SahteKoruyucu.Onek + "fixture-password");
        await f.Area.KayitlariYazAsync(historical, replacement);
        if (prune)
        {
            var result = await f.Service.PruneAsync(14, new BackupPruneRemoteOptions
            {
                RemoteUrl = "https://backup.example.test", UserName = "fixture", Password = "fixture-password"
            }, f.Area.Iptal);
            Assert.Equal("unrelated live data", File.ReadAllText(f.Sentinel));
            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.LocalDeleteFailed);
        }
        else await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteBackupAsync(historical.Id, f.Area.Iptal));
        Assert.Equal("unrelated live data", File.ReadAllText(f.Sentinel));
        Assert.Contains(await f.Service.GetBackupsAsync(f.Area.Iptal), item => item.Id == historical.Id);
        Assert.True(File.Exists(replacement.Path));
        Assert.Empty(f.Cloud.Istekler);
    }

    [Fact]
    public async Task ADirectoryCannotBeEvidenceThatANewerBackupReplacesTheLastArchive()
    {
        using var f = new Fixture();
        var old = f.Archive("old", DateTime.UtcNow.AddDays(-30));
        var directory = f.Archive("directory", DateTime.UtcNow);
        directory.Path = f.Target;
        await f.Area.KayitlariYazAsync(old, directory);
        Assert.True((await f.Service.GetBackupsAsync(f.Area.Iptal)).Single(item => item.Id == directory.Id).LocalMissing);
        Assert.True((await f.Service.GetChainAsync(directory.Id, f.Area.Iptal)).Broken);
        Assert.Equal(0, (await f.Service.PruneAsync(14, cancellationToken: f.Area.Iptal)).Removed);
        Assert.True(File.Exists(old.Path));
        Assert.Equal("unrelated live data", File.ReadAllText(f.Sentinel));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        internal readonly SahteBulut Cloud = new() { VarsayilanDurum = HttpStatusCode.NoContent };
        internal readonly string Target = Path.Combine(Path.GetTempPath(), "adapanel-restore-target-" + Guid.NewGuid().ToString("N"));
        internal string Sentinel => Path.Combine(Target, "live-sentinel.txt");
        internal BackupService Service => Area.ServisKur(Cloud);
        internal Fixture()
        {
            Directory.CreateDirectory(Target);
            File.WriteAllText(Sentinel, "unrelated live data");
        }
        internal BackupRecordDto Archive(string id, DateTime created)
        {
            var path = Path.Combine(Area.DosyaDeposu.EnsureDirectory("backups"), id + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open())) writer.Write("restored payload");
            return new BackupRecordDto
            {
                Id = id, Name = id + ".zip", Type = "site", Source = "legacy.example", Path = path,
                Format = "zip", Status = "created", TargetType = "local", CreatedAtUtc = created,
                Size = new FileInfo(path).Length
            };
        }
        public void Dispose()
        {
            if (Directory.Exists(Target)) Directory.Delete(Target, true);
            Area.Dispose();
        }
    }
}
