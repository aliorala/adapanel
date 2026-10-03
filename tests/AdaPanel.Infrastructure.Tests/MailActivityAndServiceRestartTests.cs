using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "ALAN YOKSA EVET" YOK — ve panel kendi kapattigi servisi acamadigini SOYLER.
///
/// <para>
/// Iki ayri bulgu, tek sinif: panel bilmedigi bir seyi biliyormus gibi gosteriyordu.
/// </para>
/// </summary>
public sealed class MailActivityAndServiceRestartTests
{
    // -----------------------------------------------------------------------------------------
    // 1) ETKINLIK OKUMASI
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Alan hic yoksa cevap <c>null</c> olmali — <c>true</c> DEGIL.
    /// </summary>
    /// <remarks>
    /// Eski okuyucu (<c>GetBoolField</c>) alan bulunmadiginda <c>true</c> donuyordu; yani
    /// "sunucu bunu soylemedi" ile "sunucu evet dedi" AYNI cevaba iniyordu. Kapali bir alan adi
    /// ya da takma ad panelde yesil "Etkin" gorunurse kullanici hatayi BASKA yerde arar. Hata
    /// posta KUTUSU icin kapatilmisti; alan adi ve takma ad geride kalmisti.
    /// </remarks>
    [Fact]
    public void Alan_YOKSA_olculemedi_denir()
    {
        Assert.Null(MailService.OkunanEtkinlik(Json("""{ "name": "ornek.com" }""")));
    }

    [Theory]
    [InlineData("""{ "isEnabled": true }""", true)]
    [InlineData("""{ "isEnabled": false }""", false)]
    [InlineData("""{ "active": false }""", false)]
    [InlineData("""{ "enabled": true }""", true)]
    public void Alan_VARSA_degeri_oldugu_gibi_okunur(string govde, bool beklenen)
    {
        Assert.Equal(beklenen, MailService.OkunanEtkinlik(Json(govde)));
    }

    /// <summary>
    /// Nesne olmayan girdi de "olculemedi"dir: dizi/metin/null icin uydurma cevap uretilmez.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"metin\"")]
    [InlineData("null")]
    public void Nesne_olmayan_girdi_olculemedi(string govde)
    {
        Assert.Null(MailService.OkunanEtkinlik(Json(govde)));
    }

    /// <summary>
    /// UC LISTE DE ayni kayipsiz okuyucudan beslenmeli; biri geride kalmasin.
    /// </summary>
    /// <remarks>
    /// Kaynaga bakilir, cunku olculen sey bir deger degil BAGLANTIDIR: posta kutusu duzeltilmis
    /// ama alan adi ve takma ad eski okuyucuda kalmisti. Ayni sinif bir daha yarim kapanmasin.
    /// </remarks>
    [Fact]
    public void Alan_adi_ve_takma_ad_da_kayipsiz_okuyucuyu_kullanir()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Mail", "MailService.cs");

        Assert.Matches(@"Active\s*=\s*OkunanEtkinlik\(item\)", kaynak);
        Assert.Matches(@"Active\s*=\s*OkunanEtkinlik\(entry\.Value\)", kaynak);

        // Etkinlik alanlari ARTIK GetBoolField'dan okunmuyor. (GetBoolField baska amaclarla
        // duruyor -- ayar bayraklari icin varsayilan `true` mesru; yasak yalniz ETKINLIK icin.)
        Assert.DoesNotMatch(@"Active\s*=\s*GetBoolField", kaynak);
    }

    private static JsonElement Json(string govde) => JsonDocument.Parse(govde).RootElement;

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(KaynakKoku(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
