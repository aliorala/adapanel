using System.Text;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// İstatistik ekranı SIKIŞTIRILMIŞ günleri de saymalı.
///
/// NEDEN VAR: `WebStatisticsService` yalnız <c>*.log</c> topluyordu. Log döndürme eklendikten
/// sonra kapanmış günlükler ZSTD ile arşivleniyor (<c>.log.zst</c>) — yani o günler istatistik
/// ekranından SESSİZCE düşüyordu. Müşteri geçmiş trafiğini kaybediyor ve sebebini hiçbir yerde
/// göremiyordu; ekran "o gün ziyaret yok" ile "o günü okuyamadım" arasındaki farkı da söylemiyordu.
///
/// Bu, bu depoda defalarca çıkan sınıfın aynısı: panel bir şeyi ÖLÇEMEDİĞİNDE "yok" sayıyor.
/// </summary>
public class WebStatisticsCompressedLogTests
{
    /// <summary>
    /// Toplama deseni sıkıştırılmış günlükleri KAPSAMALI. Desen kaynaktan okunur — testin kendi
    /// kopyasını yazmak, kodun değiştiğini fark etmeyen bir nöbetçi üretirdi.
    /// </summary>
    [Fact]
    public void Toplama_deseni_sikistirilmis_gunlukleri_de_kapsar()
    {
        var kaynak = File.ReadAllText(KaynakYolu("Statistics", "WebStatisticsService.cs"));

        Assert.DoesNotContain("GetFiles(\"*.log\"", kaynak);
        Assert.Contains("GetFiles(\"*.log*\"", kaynak);
    }

    /// <summary>
    /// Dosya AÇMA yolu da ortak okuyucudan geçmeli. Deseni genişletip dosyayı düz
    /// <c>StreamReader</c> ile açmak, arşivlenmiş günü BOZUK METİN olarak okurdu — hiç
    /// okumamaktan kötü, çünkü sessizce sıfır satır üretirdi.
    /// </summary>
    [Fact]
    public void Dosya_acma_ORTAK_okuyucudan_gecer()
    {
        var kaynak = File.ReadAllText(KaynakYolu("Statistics", "WebStatisticsService.cs"));

        Assert.Contains("SeoLogFileSet.OpenText(path)", kaynak);
        Assert.DoesNotContain("new StreamReader(stream)", kaynak);
    }

    /// <summary>
    /// DAVRANIŞ testi: gerçekten ZSTD ile sıkıştırılmış bir günlük, düz hâliyle aynı satırları
    /// vermeli. Yukarıdaki iki test kaynağa bakıyor; bu, okuyucunun işini gerçekten yaptığını
    /// ölçüyor.
    /// </summary>
    [Fact]
    public async Task Sikistirilmis_gunluk_duz_haliyle_AYNI_satirlari_verir()
    {
        var klasor = Directory.CreateTempSubdirectory("adapanel-stat-zst");
        try
        {
            const string icerik =
                "#Fields: date time cs-method cs-uri-stem sc-status\r\n" +
                "2026-08-27 10:00:00 GET /urun/5 200\r\n" +
                "2026-08-27 10:00:01 GET /hakkinda 404\r\n";

            var duz = Path.Combine(klasor.FullName, "u_ex260827.log");
            File.WriteAllText(duz, icerik, new UTF8Encoding(false));

            // Sıkıştırmayı deponun KENDİ yardımcısıyla üret — elle zstd baytı yazmak, gerçek
            // arşivden farklı bir şey ölçme riski taşırdı.
            var arsiv = duz + ".zst";
            await BackupDumpCompression.CompressFileAsync(duz, arsiv, CancellationToken.None);
            Assert.True(File.Exists(arsiv), "sikistirilmis dosya uretilemedi");
            Assert.True(BackupDumpCompression.IsCompressedFile(arsiv), "uretilen dosya sikistirilmis sayilmadi");

            using var duzOkuyucu = SeoLogFileSet.OpenText(duz);
            using var arsivOkuyucu = SeoLogFileSet.OpenText(arsiv);

            Assert.Equal(duzOkuyucu.ReadToEnd(), arsivOkuyucu.ReadToEnd());
        }
        finally
        {
            klasor.Delete(recursive: true);
        }
    }

    private static string KaynakYolu(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return Path.Combine(new[] { kok!, "src", "AdaPanel.Infrastructure" }.Concat(parcalar).ToArray());
    }
}
