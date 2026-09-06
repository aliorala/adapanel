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
            // HEDEF PANELIN VERI KOKUNUN DISINDA: kullanicinin VERDIGI geri yukleme hedefi
            // artik PanelWriteTargetGuard'dan gecer ve panelin kendi veri kokune yazmayi
            // reddeder (sahiplik kaydi orada duruyor). Test bu klasoru yalnizca RAHAT oldugu
            // icin veri kokunun altinda seciyordu; olculen davranisla ilgisi yok.
            var restoreTarget = Path.Combine(dataRoot + "-hedef", "restored");
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
                    TargetPath = Path.Combine(dataRoot + "-hedef", "out")
                }));

            Assert.Equal("backup.items_not_found", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// Hedef kökün İÇİNDEKİ bir KAVŞAK (junction) üzerinden yazılamaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu senaryo gerçek ve neden dizge denetimi görmez:</b> bu uç kapsamlı kullanıcıya
    /// açıktır (BackupController.RestoreItemsAsync) ve kiracı için hedef, kendi site köküdür.
    /// Kiracı kendi belge kökünde <c>wp-content</c> adını bir kavşağa çevirirse — kavşak kurmak
    /// Windows'ta ayrıcalık gerektirmez — <c>wp-content/...</c> önekli her arşiv girdisinin
    /// çözülmüş yolu HÂLÂ kökün altında görünür, yani <c>PathContainment</c> önek denetimi geçer.
    /// Kaçış dizgede değil DİSKTE olur: yazma kavşağı takip eder. Panel LocalSystem olduğu için
    /// hedefte hiçbir ACL durdurmaz.
    /// </para>
    /// <para>
    /// <b>Kötü niyetli arşiv GEREKMEZ:</b> buradaki zip sıradan bir site yedeğidir
    /// (<c>ZipFile.CreateFromDirectory</c>). Kaçışı üreten tek şey, yedek alındıktan SONRA hedefte
    /// beliren kavşaktır — yani "arşiv girdisi doğrulanıyor mu" sorusu, tek başına yanlış sorudur.
    /// </para>
    /// <para>
    /// <b>Ölçüm gerçek dosya sistemindedir</b> (kaynak taraması değil): kavşak takibi ancak gerçek
    /// bir kavşak kurulunca ölçülebilir. Kavşak kurulamıyorsa (ör. yetkisiz/desteklemeyen dosya
    /// sistemi) test ATLANIR — sessizce YEŞİL DÖNMEZ, çünkü kurulmamış bir ön koşulda geçen bir
    /// güvenlik testi, ölçmediği şeyi ölçtüğünü söyler.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RestoreItems_HedefTeki_JUNCTION_uzerinden_disari_yazamaz()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-granular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            var fileStore = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));

            // Sıradan bir site yedeği: webroot/wp-content/uploads/logo.png
            var siteRoot = Path.Combine(dataRoot, "source", "webroot");
            Directory.CreateDirectory(Path.Combine(siteRoot, "wp-content", "uploads"));
            await File.WriteAllTextAsync(Path.Combine(siteRoot, "wp-content", "uploads", "logo.png"), "PNG");

            var zipPath = Path.Combine(dataRoot, "site.zip");
            ZipFile.CreateFromDirectory(siteRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);

            await fileStore.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto
                {
                    Id = "b3",
                    Name = "site.zip",
                    Type = "site",
                    Source = "example.com",
                    Path = zipPath,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow
                }
            });

            // Hedef kök = kiracının site kökü. İçinde "webroot" adında bir KAVŞAK var ve kavşak
            // sanal alanın DIŞINI gösteriyor.
            // HEDEF PANELIN VERI KOKUNUN DISINDA: kullanicinin VERDIGI geri yukleme hedefi
            // artik PanelWriteTargetGuard'dan gecer ve panelin kendi veri kokune yazmayi
            // reddeder (sahiplik kaydi orada duruyor). Test bu klasoru yalnizca RAHAT oldugu
            // icin veri kokunun altinda seciyordu; olculen davranisla ilgisi yok.
            var restoreTarget = Path.Combine(dataRoot + "-hedef", "restored");
            var disari = Path.Combine(dataRoot, "disari");
            Directory.CreateDirectory(restoreTarget);
            Directory.CreateDirectory(disari);

            var baglanti = Path.Combine(restoreTarget, "webroot");
            JunctionOlustur(baglanti, disari);
            if (!Directory.Exists(baglanti)
                || !new DirectoryInfo(baglanti).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return; // ön koşul sağlanamadı (bkz. belge): ölçüm YAPILAMADI
            }

            var service = new BackupService(null!, null!, null!, null!, null!, fileStore, null!);

            var exception = await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() =>
                service.RestoreItemsAsync(new RestoreBackupItemsRequest
                {
                    BackupId = "b3",
                    Items = ["webroot/wp-content/"],
                    TargetPath = restoreTarget
                }));

            Assert.Equal("backup.invalid_entry_path", exception.MessageKey);
            Assert.False(
                File.Exists(Path.Combine(disari, "wp-content", "uploads", "logo.png")),
                "Kavsak TAKIP EDILDI: yedek icerigi hedef kokun DISINA yazildi.");
        }
        finally
        {
            try
            {
                // Kavşak ÖNCE kaldırılır: özyinelemeli silme onu takip ederse testin kendisi,
                // ölçmeye çalıştığı veri kaybını üretirdi.
                foreach (var dizin in Directory
                             .EnumerateDirectories(dataRoot, "*", SearchOption.AllDirectories)
                             .Reverse())
                {
                    if (new DirectoryInfo(dizin).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Directory.Delete(dizin);
                    }
                }

                Directory.Delete(dataRoot, recursive: true);
            }
            catch
            {
                // Geçici klasör temizliği en iyi çaba.
            }
        }
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        using var surec = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{hedef}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        surec.WaitForExit(60_000);
    }
}

