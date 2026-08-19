using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Migration;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// F6 — "göç: dosyalar çekildi" 0 dosyada da yeşildi ve sayı arayüze HİÇ ulaşmıyordu.
/// <para>
/// İki ayrı arıza vardı: (1) <c>FileMigrationService</c> hiç dosya inmediğine bakmıyordu — yanlış
/// "Uzak yol" (en sık göç hatası) istisna atmaz, sadece boş liste döner; (2)
/// <c>SiteMigrationService</c> dönen sonucu <c>return null</c> ile TAMAMEN atıyordu, yani
/// <c>FilesDownloaded</c> hesaplanıyor ama kullanıcıya hiç gösterilmiyordu.
/// </para>
/// </summary>
public sealed class MigrationFileCountHonestyTests
{
    /// <summary>
    /// SIFIR DOSYA BAŞARI DEĞİLDİR. MUTASYON: <c>EnsureFilesDownloaded</c> çağrısı kaldırılırsa
    /// (eski davranış) bu test düşer.
    /// </summary>
    [Fact]
    public void EnsureFilesDownloaded_ZeroFiles_FailsWithActionableMessage()
    {
        var sonuc = new RemoteFileMigrationResult
        {
            SiteName = "ornek.test",
            Protocol = "ftp",
            FilesDownloaded = 0,
            TargetPath = @"C:\inetpub\ornek"
        };

        var hata = Assert.Throws<AppException>(() => FileMigrationService.EnsureFilesDownloaded(sonuc, "/httpdocs", failedCount: 0));

        // Kullanıcıya NE YAPACAĞINI söyleyen anahtar: "Uzak yol'u kontrol et".
        Assert.Equal("migration.files.path_not_found", hata.MessageKey);
        Assert.Contains("/httpdocs", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>Aktarımı patlayan öğe varsa sebep ayrıntıda görünmeli (yetki/bağlantı).</summary>
    [Fact]
    public void EnsureFilesDownloaded_WithFailedTransfers_ReportsTheCount()
    {
        var sonuc = new RemoteFileMigrationResult { SiteName = "ornek.test", FilesDownloaded = 0 };

        var hata = Assert.Throws<AppException>(() => FileMigrationService.EnsureFilesDownloaded(sonuc, "/", failedCount: 12));

        Assert.Contains("12", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>Tek dosya bile indiyse göç adımı meşrudur; koruma fazla hassas olmamalı.</summary>
    [Fact]
    public void EnsureFilesDownloaded_AtLeastOneFile_Passes()
    {
        var sonuc = new RemoteFileMigrationResult { SiteName = "ornek.test", FilesDownloaded = 1 };

        FileMigrationService.EnsureFilesDownloaded(sonuc, "/httpdocs", failedCount: 0);
    }

    /// <summary>
    /// Adım detayı ÖLÇÜLEBİLİR olmalı: kaç dosya, ne kadar veri, nereye. Eskiden bu satırın yerinde
    /// <c>null</c> vardı ve kullanıcı "Dosyalar çekildi" yazısından hiçbir şey anlayamıyordu.
    /// </summary>
    [Fact]
    public void DescribeFilePull_ReportsCountSizeAndTarget()
    {
        var metin = SiteMigrationService.DescribeFilePull(new RemoteFileMigrationResult
        {
            FilesDownloaded = 1432,
            BytesDownloaded = 5L * 1024 * 1024,
            TargetPath = @"C:\inetpub\ornek"
        });

        Assert.Contains("1432", metin, StringComparison.Ordinal);
        Assert.Contains("5 MB", metin, StringComparison.Ordinal);
        Assert.Contains(@"C:\inetpub\ornek", metin, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1610612736, "1.5 GB")]
    public void DescribeFilePull_FormatsSizesReadably(long bayt, string beklenenParca)
    {
        var metin = SiteMigrationService.DescribeFilePull(new RemoteFileMigrationResult { BytesDownloaded = bayt });

        Assert.Contains(beklenenParca, metin, StringComparison.Ordinal);
    }
}
