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

    // -----------------------------------------------------------------------------------------
    // 2) POSTA SUNUCUSUNU YENIDEN BASLATMA
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Yedekleme/geri yukleme posta sunucusunu KENDISI durdurur; acamadigini SOYLEMELI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eski hal iki cagriyi da <c>finally</c> icinde, DONUSUNU ATARAK yapiyordu. Schtasks
    /// acamazsa posta KAPALI kalir ama kayit "created"/"restored" der: musteri gece yedeginden
    /// sonra posta almamaya baslar ve sebebini panelde bulamaz.
    /// </para>
    /// <para>
    /// Cagrilar <c>ProcessRunner</c> uzerinden gercek <c>schtasks.exe</c>'ye gittigi icin bu
    /// davranis birim testiyle CALISTIRILARAK olculemez; olculen sey KODUN SEKLIDIR. Bu, bu
    /// depoda kabul edilmis bir kalip (bkz. WriteThenVerifyContractTests) ama sinirini bilerek
    /// yaziyorum: bu test baglantinin varligini kanitlar, schtasks'in davranisini degil.
    /// </para>
    /// </remarks>
    [Fact]
    public void Posta_sunucusu_acilamazsa_kayit_bunu_tasir()
    {
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Backup", "BackupService.cs");

        /*
         * `(?m)` COK SATIR KIPI ZORUNLU -- ve bu satiri MUTASYON yazdirdi.
         *
         * Ilk yazdigim hal kipsizdi: `^` ve `$` butun METNIN basina/sonuna capalanir, yani
         * "satir basinda donusu atilan bir cagri" desenini bir kaynak dosyasinda ASLA
         * eslestiremez. DoesNotMatch her zaman gecerdi -- BOS BIR GUVENCE. Korumayi kasten
         * bozdum (geri yuklemede donusu tekrar attim) ve test KIRMIZI YANMADI; hata boyle
         * gorundu. Ayni tuzaga bu oturumda ikinci kez dusuldugu icin gerekcesi buraya yazildi.
         */
        Assert.Matches(@"postaUyarisi\s*=\s*await StartStalwartAsync\(cancellationToken\)", kaynak);
        Assert.DoesNotMatch(@"(?m)^\s*await StartStalwartAsync\(cancellationToken\);\s*$", kaynak);

        // Iki cagri var (yedek + geri yukleme); IKISI de donusu almali. Sayi denetimi olmadan,
        // birinin geri alinmasi ustteki `Matches` tarafindan gizlenirdi -- mutasyonda tam olarak
        // bu oldu: biri bozuldu, oteki hala eslestigi icin test yesil kaldi.
        Assert.Equal(
            2,
            Regex.Matches(kaynak, @"postaUyarisi\s*=\s*await StartStalwartAsync\(cancellationToken\)").Count);

        // Cikis kodu TEK BASINA yetmez: tetiklemeden sonra durum GERI OKUNUYOR.
        Assert.Contains("/Query /TN", kaynak, StringComparison.Ordinal);

        // Yarim ise "restored" DENMIYOR.
        Assert.DoesNotMatch(@"RestoreMailAsync\(content\.FilePath, cancellationToken\);\s*\r?\n\s*record\.Status = ""restored"";", kaynak);
    }

    /// <summary>
    /// Yukaridaki sekil denetiminin GERCEKTEN yakaladiginin kaniti — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Bu oturumda yazilan bir koruma BOS CIKTI: aradigi satir ayni dosyada zaten bes kez
    /// geciyordu, yani duzeltme geri alinsa bile yesil kalirdi. Bir testin var olmasi, bir sey
    /// olctugu anlamina gelmez.
    /// </remarks>
    [Fact]
    public void Sekil_denetimi_gercekten_yakaliyor()
    {
        const string Eski = "        finally\n        {\n            await StartStalwartAsync(cancellationToken);\n        }";
        const string Yeni = "        finally\n        {\n            postaUyarisi = await StartStalwartAsync(cancellationToken);\n        }";

        Assert.Matches(@"(?m)^\s*await StartStalwartAsync\(cancellationToken\);\s*$", Eski);
        Assert.DoesNotMatch(@"(?m)^\s*await StartStalwartAsync\(cancellationToken\);\s*$", Yeni);
        Assert.Matches(@"postaUyarisi\s*=\s*await StartStalwartAsync\(cancellationToken\)", Yeni);
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
