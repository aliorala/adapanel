using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// POSTGRESQL KIRACI YALITIMI — panelin urettigi iki ifade.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCUM (yalitilmis PostgreSQL 16.14, 127.0.0.1:15432, panelin components.json'da sevk
/// ettigi surum).</b> Panel bir kiraciyi veritabanina baglarken yalnizca
/// <c>GRANT ALL PRIVILEGES ON DATABASE x TO u</c> yaziyordu. Iki sey birden olculdu:
/// </para>
/// <list type="number">
/// <item>
/// <b>ACIK:</b> A kiracisinin rolu, B kiracisinin veritabanina BAGLANABILIYORDU
/// (<c>current_database() = adapanel_test_pg_b</c>). PostgreSQL <c>CONNECT</c> yetkisini her
/// veritabaninda <c>PUBLIC</c> rolune VERILMIS olarak dogurur; panelin kurdugu
/// <c>pg_hba.conf</c> ise <c>host all all 127.0.0.1/32</c> yazar, yani "hangi veritabani"
/// sorusuna hic karismaz. Baglanan A, <c>pg_class</c>/<c>pg_attribute</c> uzerinden B'nin TUM
/// tablo ve SUTUN adlarini okudu. (Satir verisi gelmedi: tablo yetkisi PUBLIC'e verilmemis.
/// <c>information_schema</c> ve <c>CREATE</c> de yetkiye gore suzuldu.) Yani veri degil ama
/// kurbanin SEMASI siziyordu.
/// </item>
/// <item>
/// <b>KIRIK OZELLIK:</b> ayni kiraci KENDI veritabaninda <c>CREATE TABLE</c> yapamiyordu
/// ("public semasina erisim engellendi"). PostgreSQL 15, <c>PUBLIC</c>'in <c>public</c> semasi
/// uzerindeki <c>CREATE</c> hakkini kaldirdi; veritabani duzeyi yetki sema duzeyine inmiyor.
/// Yani panelin PostgreSQL saglama yolu sevk edilen surumde zaten kullanilamaz durumdaydi.
/// </item>
/// </list>
/// <para>
/// Iki ifade eklendikten sonra ayni sunucuda yeniden olculdu: A -> B baglantisi
/// <c>FATAL: ... veritabanina erisim engellendi</c> ile dustu, kiraci KENDI veritabaninda
/// tablo olusturup okudu, panelin <c>postgres</c> super-kullanici baglantisi etkilenmedi.
/// </para>
/// <para>
/// Bu testler o ifadelerin METNINI sabitler. Davranisin kaniti yukaridaki canli olcumdur;
/// buradaki nobetci, ifadelerden birinin ileride sessizce dusurulmesini yakalar — ozellikle
/// ikincisinin, cunku onu dusurmek bir guvenlik duzeltmesinin kiraciyi kendi verisinden
/// koparmasi demektir.
/// </para>
/// </remarks>
public sealed class PostgreSqlTenantIsolationTests
{
    [Fact]
    public void Ortak_PUBLIC_erisimi_KALDIRILIR()
    {
        Assert.Equal(
            "REVOKE ALL ON DATABASE \"musteri_wp\" FROM PUBLIC;",
            PostgreSqlDatabaseProvider.RevokePublicAccessSql("musteri_wp"));
    }

    [Fact]
    public void Kiraci_KENDI_semasini_kullanabilir_kalir()
    {
        // FAZLA-KAPATMA NOBETCISI: yalitim tek basina kiraciyi kendi veritabanindan da kovar.
        Assert.Equal(
            "GRANT ALL ON SCHEMA public TO \"musteri_kul\";",
            PostgreSqlDatabaseProvider.GrantPublicSchemaSql("musteri_kul"));
    }

    /// <summary>
    /// Motor, sertlestirme YETENEGINI kendisi bildirmeli.
    /// </summary>
    /// <remarks>
    /// Arayuz dugmeyi <c>engine === "mysql" || engine === "mariadb"</c> sabitiyle gosteriyordu;
    /// PostgreSQL sertlestirmesi bu yuzden hicbir yerden CAGRILAMIYORDU -- uc vardi, kapisi
    /// yoktu. Bu nobetci, yetenegi uygulayan bir saglayicinin bayragi bildirmeyi unutmasini
    /// (yani ozelligin sessizce olu kalmasini) yakalar.
    /// </remarks>
    [Fact]
    public void Sertlestirme_yetenegi_MOTORDAN_okunur()
    {
        Assert.True(new PostgreSqlDatabaseProvider().SupportsHardening);
        Assert.True(new MySqlDatabaseProvider().SupportsHardening);

        // FAZLA-BILDIRME NOBETCISI: uygulamayan motor "sertlestirdim" demez. MSSQL saglayicisi
        // HardenAdminAccountsAsync'i uygulamiyor; bayragi true olsaydi arayuz hicbir sey yapmayan
        // bir dugme gosterir ve basana "sertlestirildi" derdi (bu hata bir kez yapildi).
        Assert.False(((IDatabaseEngineProvider)new MsSqlDatabaseProvider()).SupportsHardening);
    }

    [Theory]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("normal", "\"normal\"")]
    public void Tanimlayici_cift_tirnagi_IKILENIR(string girdi, string beklenen)
    {
        // Ust katman adlari kati bir desenle dogruluyor; kacirma yine de BURADA durmali, cunku
        // panel disindan (tasima, betik, ileride eklenecek bir yol) gelen bir ad tirnak kapatip
        // kendi SQL'ini yazabilirdi.
        Assert.Equal(
            $"REVOKE ALL ON DATABASE {beklenen} FROM PUBLIC;",
            PostgreSqlDatabaseProvider.RevokePublicAccessSql(girdi));
        Assert.Equal(
            $"GRANT ALL ON SCHEMA public TO {beklenen};",
            PostgreSqlDatabaseProvider.GrantPublicSchemaSql(girdi));
    }
}
