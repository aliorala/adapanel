using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GRANT ifadesinde veritabani adinin kacirilmasi.
/// <para>
/// GUVENLIK ACIGI (arastirmayla bulundu, kodda mevcuttu): GRANT ifadesinin VERITABANI ADI
/// bolumunde "_" ve "%" karakterleri TERS TIRNAK ICINDE BILE joker olarak yorumlanir. Yani
/// </para>
/// <code>GRANT ALL ON `musteri1_db`.* TO 'u'@'localhost';</code>
/// <para>
/// yalnizca musteri1_db'ye degil, musteri1Xdb / musteri1-db gibi desene uyan HER veritabanina
/// yetki verir.
/// </para>
/// <para>
/// Panelin veritabani adi kurali alt cizgiye izin veriyor ve alt cizgi hosting adlarinda
/// neredeyse kural (site_wp, musteri_db). Cok kiraciil bir panelde sonuc yatay yetki
/// yukseltmesidir: bir musterinin veritabani kullanicisi, adi desene uyan BASKA bir musterinin
/// veritabanini okuyup yazabilir.
/// </para>
/// <para>
/// Siradan tanimlayici kacirmasi (yalnizca ters tirnak) bu sorunu COZMEZ -- bu yuzden GRANT'a
/// ozgu ayri bir kacirma gerekir.
/// </para>
/// </summary>
public sealed class GrantWildcardTests
{
    private static string Escape(string value) =>
        AdaPanel.Infrastructure.Database.MySqlDatabaseProvider.EscapeGrantDatabaseName(value);

    // Alt cizgi TEK KARAKTER jokeridir; kacirilmazsa komsu veritabanlarini kapsar.
    [Fact]
    public void Underscore_IsEscapedSoItMatchesOnlyItself()
    {
        Assert.Equal(@"`musteri1\_db`", Escape("musteri1_db"));
    }

    // Yuzde COK KARAKTER jokeridir; bir DB adinda gecerse yetki cok daha genis olur.
    [Fact]
    public void Percent_IsEscaped()
    {
        Assert.Equal(@"`site\%veri`", Escape("site%veri"));
    }

    // Birden cok joker karakterin hepsi kacirilmali.
    [Fact]
    public void EveryWildcardInTheNameIsEscaped()
    {
        Assert.Equal(@"`a\_b\_c\%d`", Escape("a_b_c%d"));
    }

    // Ters bolu kendisi de kacirilmali, yoksa kullanici kacirmayi bozabilir.
    [Fact]
    public void BackslashIsEscapedFirstSoItCannotBreakTheEscaping()
    {
        // Girdi: a \ _ b  ->  ONCE ters bolu ikilenir (a \\ _ b), SONRA alt cizgi kacirilir.
        // Sira onemli: alt cizgi once kacirilsaydi, uretilen "\" ikinci adimda tekrar ikilenir
        // ve kacirma bozulurdu.
        Assert.Equal("`a" + @"\\" + @"\_" + "b`", Escape(@"a\_b"));
    }

    // Joker icermeyen adlar degismeden kalmali (gereksiz kacirma yok).
    [Theory]
    [InlineData("wordpress")]
    [InlineData("SiteVeri")]
    [InlineData("db123")]
    public void NamesWithoutWildcards_AreLeftAlone(string ad)
    {
        Assert.Equal($"`{ad}`", Escape(ad));
    }

    /// <summary>
    /// ACIGIN OZU: kacirilmamis bir ad, komsu bir veritabani adiyla LIKE olarak ESLESIR.
    /// Bu test, kacirmanin gercekten bu eslesmeyi kirdigini gosterir.
    /// </summary>
    [Fact]
    public void WithoutEscaping_TheGrantWouldAlsoCoverAnotherCustomersDatabase()
    {
        const string kurban = "musteri1Xdb";   // baska bir musterinin veritabani
        const string verilen = "musteri1_db";  // yetki verilen veritabani

        // Kacirilmamis hali LIKE deseni olarak kurbani da kapsar.
        Assert.Matches(
            "^" + System.Text.RegularExpressions.Regex.Escape(verilen).Replace("_", ".") + "$",
            kurban);

        // Kacirilmis halde "_" artik joker degil, harfi harfine kendisi.
        Assert.Contains(@"\_", Escape(verilen), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// ONARIM RAPORU, CAPRAZ ERISIMIN KALDIRILMADIGINI SOYLEMELI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yalitilmis MariaDB 11.8.3'te SURULDU: kacirilmamis bir yetki
    /// (<c>adapanel_test_kiraciA_db</c>) komsu <c>adapanel_test_kiraciAXdb</c>'yi de kapsiyordu ve
    /// o kiraci komsunun verisini OKUDU. <c>RepairAsync</c> calistiktan sonra desen daraltildi
    /// (yeni acilan eslesen adli veritabani artik KAPSAM DISI, olculdu) -- ama zaten kapsanan IKI
    /// veritabanina da dar yetki verildigi icin capraz erisim OLDUGU GIBI KALDI.
    /// </para>
    /// <para>
    /// Bu bilincli: erisimi kormelesine kesmek calisan bir siteyi durdururdu. Ama rapor bunu
    /// SOYLEMEZSE, "sertlestir" dugmesine basan yonetici acigin kapandigini sanir. Nobetci tam
    /// olarak o yanilgiyi olcer.
    /// </para>
    /// </remarks>
    [Fact]
    public void RepairReport_FlagsGrantsThatStillCoverMoreThanOneDatabase()
    {
        var uyarili = AdaPanel.Infrastructure.Database.GrantWildcardRepair.Rapor("musteri1", "localhost", ["musteri1_db", "musteri1Xdb"]);
        Assert.Contains("DIKKAT", uyarili, System.StringComparison.Ordinal);
        Assert.Contains("musteri1Xdb", uyarili, System.StringComparison.Ordinal);

        // FAZLA-UYARI NOBETCISI: tek veritabanini kapsayan temiz daraltma UYARI URETMEZ.
        // Her satiri "DIKKAT" ile isaretlemek, uyariyi hicbir sey soylemeyen bir gurultuye
        // cevirir ve gercek olani gizlerdi.
        var temiz = AdaPanel.Infrastructure.Database.GrantWildcardRepair.Rapor("musteri2", "localhost", ["musteri2_db"]);
        Assert.DoesNotContain("DIKKAT", temiz, System.StringComparison.Ordinal);
        Assert.Contains("musteri2_db", temiz, System.StringComparison.Ordinal);
    }
}
