using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Askıda veritabanı hesabı kilitleme kararı: hesap YALNIZ aboneliğin veritabanlarına mı yetkili (münhasır)?
/// SHOW GRANTS çıktısının sahte satırlarıyla ölçülür; belirsizlik her zaman "münhasır değil" (kilitleme) yönündedir.
/// </summary>
public sealed class MySqlAccountLockClassificationTests
{
    private static readonly HashSet<string> Owned = new(["uye_db"], StringComparer.OrdinalIgnoreCase);

    private static (bool Touches, bool Exclusive) Classify(params string[] lines) => MySqlDatabaseProvider.ClassifyGrants(lines, Owned);

    [Fact]
    public void Yalniz_abonelik_veritabani_ve_USAGE_munhasirdir()
    {
        var verdict = Classify(
            "GRANT USAGE ON *.* TO `u`@`localhost` IDENTIFIED BY PASSWORD '*X'",
            "GRANT ALL PRIVILEGES ON `uye_db`.* TO `u`@`localhost`");
        Assert.Equal((true, true), verdict);
    }

    [Fact]
    public void Baska_veritabanindaki_TABLO_yetkisi_munhasirligi_bozar()
    {
        var verdict = Classify(
            "GRANT USAGE ON *.* TO `u`@`localhost`",
            "GRANT ALL PRIVILEGES ON `uye_db`.* TO `u`@`localhost`",
            "GRANT SELECT ON `baska_db`.`tablo` TO `u`@`localhost`");
        Assert.Equal((true, false), verdict);
    }

    [Fact]
    public void SUTUN_ve_YORDAM_yetkisi_baska_veritabaninda_munhasirligi_bozar()
    {
        Assert.Equal((true, false), Classify("GRANT ALL ON `uye_db`.* TO `u`@`h`", "GRANT SELECT (`kolon`) ON `baska`.`t` TO `u`@`h`"));
        Assert.Equal((true, false), Classify("GRANT ALL ON `uye_db`.* TO `u`@`h`", "GRANT EXECUTE ON PROCEDURE `baska`.`p` TO `u`@`h`"));
    }

    [Fact]
    public void Abonelik_veritabanindaki_tablo_yetkisi_munhasirligi_bozmaz()
    {
        Assert.Equal((true, true), Classify("GRANT USAGE ON *.* TO `u`@`h`", "GRANT SELECT ON `uye_db`.`t` TO `u`@`h`"));
    }

    [Theory]
    [InlineData("GRANT SELECT ON *.* TO `u`@`h`")]
    [InlineData("GRANT PROCESS, RELOAD ON *.* TO `u`@`h`")]
    [InlineData("GRANT ALL PRIVILEGES ON *.* TO `u`@`h` WITH GRANT OPTION")]
    public void GLOBAL_yetki_USAGE_disindaysa_munhasir_degildir(string global)
    {
        Assert.Equal((true, false), Classify(global, "GRANT ALL ON `uye_db`.* TO `u`@`h`"));
    }

    [Fact]
    public void Rol_atamasi_munhasirligi_bozar()
    {
        Assert.Equal((true, false), Classify("GRANT `rol` TO `u`@`h`", "GRANT ALL ON `uye_db`.* TO `u`@`h`"));
    }

    [Fact]
    public void Joker_desen_TAM_AD_sayilmaz_ve_munhasir_degildir()
    {
        // `uye_db` icindeki kacissiz _ joker: uyeXdb gibi baska veritabanlarini da kapsar.
        Assert.Equal((true, false), Classify("GRANT ALL ON `uye_db`.* TO `u`@`h`".Replace("`uye_db`", "`uye%`")));
        Assert.Equal((false, false), Classify("GRANT ALL ON `baska%`.* TO `u`@`h`"));
    }

    [Fact]
    public void Kacisli_alt_cizgi_tam_ad_sayilir()
    {
        var owned = new HashSet<string>(["uye_db"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal((true, true), MySqlDatabaseProvider.ClassifyGrants(["GRANT ALL ON `uye\_db`.* TO `u`@`h`"], owned));
    }

    [Fact]
    public void Hic_dokunmayan_hesap_listeye_girmez()
    {
        Assert.Equal((false, false), Classify("GRANT ALL ON `baska_db`.* TO `u`@`h`"));
    }

    [Theory]
    [InlineData("localhost", "localhost", true)]
    [InlineData("localhost", "127.0.0.1:50000", true)]
    [InlineData("10.0.%", "10.0.5.7:3306", true)]
    [InlineData("10.0.%", "192.168.1.5:3306", false)]
    [InlineData("%", "203.0.113.9:1234", true)]
    [InlineData("203.0.113.9", "203.0.113.10:1234", false)]
    public void Oturum_yalniz_ayni_USER_ve_HOST_hesabina_aitse_kapatilir(string accountHost, string processHost, bool expected)
    {
        Assert.Equal(expected, MySqlDatabaseProvider.HostMatches(accountHost, processHost));
    }

    [Fact]
    public void Escape_ters_bolu_ve_tirnagi_kacirir()
    {
        Assert.Equal("a\\b''c", MySqlDatabaseProvider.Escape("a\b'c"));
        Assert.Equal("plain", MySqlDatabaseProvider.Escape("plain"));
    }
}
