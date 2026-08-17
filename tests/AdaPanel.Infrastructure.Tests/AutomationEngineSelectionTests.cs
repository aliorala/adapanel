using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Automation;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Otomatik yedeğin hangi veritabanı motorlarına dokunacağı KURULU OLANDAN türetilir.
/// </summary>
/// <remarks>
/// Bu testler gerçek bir arızadan doğdu: liste <c>["mysql"]</c> diye SABİT yazılmıştı, oysa
/// AdaPanel varsayılan kurulumda MariaDB kuruyor. Sonuç: gecelik yedek her gece "mysql" motorunu
/// soruyor, <c>database.engine_not_installed</c> alıyor ve SIFIR veritabanı yedekliyordu. Özette
/// "0 veritabanı yedeklendi" yazıyordu ve bu bir bilgi gibi görünüyordu; oysa sessiz bir arızaydı.
/// </remarks>
public sealed class AutomationEngineSelectionTests
{
    private static DatabaseEngineStatusDto Motor(string ad, bool kurulu, bool hazir) =>
        new() { Engine = ad, DisplayName = ad, Installed = kurulu, Ready = hazir };

    [Fact]
    public void MariaDB_kuruluysa_yedege_MariaDB_girer_mysql_DEGIL()
    {
        var secilen = AutomationService.SelectBackupEngines(
        [
            Motor("mariadb", kurulu: true, hazir: true),
            Motor("mysql", kurulu: false, hazir: false),
            Motor("postgresql", kurulu: false, hazir: false)
        ]);

        Assert.Equal(["mariadb"], secilen);
    }

    [Fact]
    public void Kurulu_ama_YONETILEMEYEN_motor_listeye_girmez()
    {
        // Motor kurulu ama panel bağlanamıyorsa (yönetici parolası yok) döküm zaten alınamaz.
        // Listeye koymak her gece bir hata satırı üretir ve gerçek arızaları gürültüye gömerdi.
        var secilen = AutomationService.SelectBackupEngines(
        [
            Motor("mariadb", kurulu: true, hazir: false)
        ]);

        Assert.Empty(secilen);
    }

    [Fact]
    public void Birden_fazla_motor_kuruluysa_HEPSI_yedeklenir()
    {
        var secilen = AutomationService.SelectBackupEngines(
        [
            Motor("mariadb", kurulu: true, hazir: true),
            Motor("postgresql", kurulu: true, hazir: true)
        ]);

        Assert.Equal(2, secilen.Count);
        Assert.Contains("mariadb", secilen);
        Assert.Contains("postgresql", secilen);
    }

    [Fact]
    public void Hicbir_motor_kurulu_degilse_bos_doner_uydurma_motor_denenmez()
    {
        var secilen = AutomationService.SelectBackupEngines(
        [
            Motor("mariadb", kurulu: false, hazir: false),
            Motor("mysql", kurulu: false, hazir: false)
        ]);

        Assert.Empty(secilen);
    }

    [Fact]
    public void Ayni_motor_iki_kez_bildirilirse_tek_kez_yedeklenir()
    {
        // Aynı motoru iki kez yedeklemek dökümü iki kez alır: yer ve süre iki katına çıkar.
        var secilen = AutomationService.SelectBackupEngines(
        [
            Motor("MariaDB", kurulu: true, hazir: true),
            Motor("mariadb", kurulu: true, hazir: true)
        ]);

        Assert.Single(secilen);
        Assert.Equal("mariadb", secilen[0]);
    }
}

/// <summary>
/// Site dışı arşivlerden (abonelik/panel/mail) seçerek geri yükleme, hedef yol verilmeden de
/// çalışmalı: kapsamlı kullanıcı güvenlik gereği hedef yol VEREMEZ, o yüzden eskiden bu yol
/// kiracıda hiç çalışmıyordu (özellik vardı, kapısı kilitliydi).
/// </summary>
public sealed class RestoreFolderNameTests
{
    private static BackupRecordDto Kayit(string tur, string kaynak) =>
        new() { Type = tur, Source = kaynak };

    [Fact]
    public void Klasor_adi_tur_ve_kaynagi_tasir()
    {
        var ad = BackupService.BuildRestoreFolderName(Kayit("subscription", "musteri-42"));

        Assert.StartsWith("subscription-musteri-42-", ad);
    }

    [Fact]
    public void Dizin_kacisi_denemesi_ada_SIZMAZ()
    {
        // Kaynak alanı kullanıcı verisidir. Temizlenmezse çıkarma klasörü yedek kökünün
        // DIŞINA taşınabilirdi.
        var ad = BackupService.BuildRestoreFolderName(Kayit("subscription", @"..\..\Windows\System32"));

        Assert.DoesNotContain("..", ad);
        Assert.DoesNotContain("/", ad);
        Assert.DoesNotContain("\\", ad);
        Assert.Equal(-1, ad.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void Bos_kaynak_ada_dusmez_sabit_karsilik_kullanilir()
    {
        var ad = BackupService.BuildRestoreFolderName(Kayit("panel", "   "));

        Assert.Contains("yedek", ad);
        Assert.Equal(-1, ad.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void Ayni_yedekten_iki_geri_yukleme_AYRI_klasorlere_gider()
    {
        // Aynı klasöre yazsalardı ikinci geri yükleme birincinin üzerine yazardı ve kullanıcı
        // neyin nereden geldiğini ayırt edemezdi.
        var kayit = Kayit("subscription", "musteri-42");
        var birinci = BackupService.BuildRestoreFolderName(kayit);
        Thread.Sleep(1100);
        var ikinci = BackupService.BuildRestoreFolderName(kayit);

        Assert.NotEqual(birinci, ikinci);
    }
}
