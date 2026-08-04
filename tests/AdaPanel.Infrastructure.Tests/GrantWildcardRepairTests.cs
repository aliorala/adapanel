using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Daha ONCE verilmis genis yetkilerin daraltilmasi.
/// <para>
/// Uretim tarafini duzeltmek YETMEZ: panel uzun sure yetkileri kacirmadan verdi ve o kayitlar
/// sunucuda oldugu gibi durur. Kod duzeltmesi gecmise dokunmaz -- yani duzeltmeden sonra bile
/// musteriler birbirinin veritabanini gormeye devam ederdi.
/// </para>
/// </summary>
public sealed class GrantWildcardRepairTests
{
    // Kacirilmamis joker iceren desenler GENISTIR: kendi adindan baskasini da kapsarlar.
    [Theory]
    [InlineData("musteri1_db")]
    [InlineData("site%")]
    [InlineData("a_b_c")]
    public void UnescapedWildcards_AreDetectedAsOverBroad(string desen)
    {
        Assert.True(GrantWildcardRepair.IsOverBroad(desen));
    }

    // Zaten kacirilmis ya da joker icermeyen desenler GUVENLIDIR; onlara dokunmak gereksiz
    // yazma islemi ve gereksiz risk olurdu.
    [Theory]
    [InlineData(@"musteri1\_db")]
    [InlineData("wordpress")]
    [InlineData(@"site\%veri")]
    public void AlreadySafePatterns_AreLeftAlone(string desen)
    {
        Assert.False(GrantWildcardRepair.IsOverBroad(desen));
    }

    // Daraltma, deseni harfi harfine eslesen bicime cevirir.
    [Theory]
    [InlineData("musteri1_db", @"musteri1\_db")]
    [InlineData("site%", @"site\%")]
    [InlineData("a_b_c", @"a\_b\_c")]
    public void Tighten_EscapesEveryUnprotectedWildcard(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, GrantWildcardRepair.Tighten(girdi));
    }

    // Zaten kacirilmis bir karakter IKINCI KEZ kacirilmamali; yoksa desen bozulur ve yetki
    // hicbir veritabanina uymaz -- yani erisim tamamen kaybolurdu.
    [Fact]
    public void Tighten_DoesNotDoubleEscapeWhatIsAlreadySafe()
    {
        Assert.Equal(@"musteri1\_db", GrantWildcardRepair.Tighten(@"musteri1\_db"));
    }

    // Karisik durum: bir kismi kacirilmis, bir kismi degil.
    [Fact]
    public void Tighten_HandlesPartiallyEscapedPatterns()
    {
        Assert.Equal(@"a\_b\_c", GrantWildcardRepair.Tighten(@"a\_b_c"));
    }

    // Daraltma FIKIRLI olmali: iki kez calistirmak ayni sonucu vermeli, yoksa her
    // sertlestirmede desen biraz daha bozulurdu.
    [Theory]
    [InlineData("musteri1_db")]
    [InlineData("site%")]
    [InlineData("wordpress")]
    public void Tighten_IsIdempotent(string desen)
    {
        var birKez = GrantWildcardRepair.Tighten(desen);

        Assert.Equal(birKez, GrantWildcardRepair.Tighten(birKez));
    }
}

/// <summary>
/// Daraltmanin CALISAN ERISIMI KOPARMAMASI.
/// <para>
/// Genis bir yetki, var olan BIRDEN COK veritabanini kapsiyor olabilir ve kullanicinin uygulamasi
/// gercekten hepsini kullaniyor olabilir. Yalnizca adi desene birebir esit olana dar yetki verip
/// genis olani geri almak, digerlerine erisimi SESSIZCE koparirdi -- yani bir guvenlik duzeltmesi
/// calisan bir siteyi bozardi.
/// </para>
/// </summary>
public sealed class GrantPatternMatchTests
{
    // "_" TEK karakter jokeridir: acigin ta kendisi budur.
    [Theory]
    [InlineData("rsmc_alarmm", "rsmc_alarmm", true)]   // kendisi
    [InlineData("rsmcXalarmm", "rsmc_alarmm", true)]   // komsu -- acigin kurbani
    [InlineData("rsmc-alarmm", "rsmc_alarmm", true)]
    [InlineData("rsmcXXalarmm", "rsmc_alarmm", false)] // iki karakter: "_" tek karakter eslesir
    [InlineData("baska_db", "rsmc_alarmm", false)]
    public void Underscore_MatchesExactlyOneCharacter(string ad, string desen, bool beklenen)
    {
        Assert.Equal(beklenen, AdaPanel.Infrastructure.Database.GrantWildcardRepair.MatchesPattern(ad, desen));
    }

    // "%" sifir veya daha cok karakter.
    [Theory]
    [InlineData("site", "site%", true)]
    [InlineData("siteveri", "site%", true)]
    [InlineData("bakasite", "site%", false)]
    public void Percent_MatchesAnyNumberOfCharacters(string ad, string desen, bool beklenen)
    {
        Assert.Equal(beklenen, AdaPanel.Infrastructure.Database.GrantWildcardRepair.MatchesPattern(ad, desen));
    }

    // Kacirilmis joker HARFI HARFINE eslesir -- zaten daraltilmis bir yetki komsuyu kapsamaz.
    [Theory]
    [InlineData("rsmc_alarmm", @"rsmc\_alarmm", true)]
    [InlineData("rsmcXalarmm", @"rsmc\_alarmm", false)]
    public void EscapedWildcards_MatchThemselvesOnly(string ad, string desen, bool beklenen)
    {
        Assert.Equal(beklenen, AdaPanel.Infrastructure.Database.GrantWildcardRepair.MatchesPattern(ad, desen));
    }

    // Windows'ta veritabani adlari buyuk/kucuk harf duyarsizdir; karsilastirma da oyle olmali,
    // yoksa gercekte kapsanan bir veritabani gozden kacar ve erisimi koparilir.
    [Fact]
    public void MatchingIsCaseInsensitiveLikeWindowsDatabaseNames()
    {
        Assert.True(AdaPanel.Infrastructure.Database.GrantWildcardRepair.MatchesPattern("RSMC_Alarmm", "rsmc_alarmm"));
    }
}
