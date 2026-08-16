using AdaPanel.Core.Dtos.Databases;
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
