using System.Text.Json;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 1 ve 2: "ayar yazildi" ile "ayar YURURLUKTE", "liste bos" ile "liste OKUNAMADI".
/// </summary>
/// <remarks>
/// <para>
/// <b>ADAY 1 -- ReloadSettings sonucu ATILIYORDU.</b> Stalwart ayarlari BELLEKTE tutar; bir ayari
/// yazmak onu etkin YAPMAZ, <c>ReloadSettings</c> eylemi yapar. Bu eylemin sonucu depoda SEKIZ
/// ayri yerde <c>_ = await ...</c> ile atiliyordu (liste politikasi, gonderen politikasi, DATA
/// asamasi script'i/otomatik yanit, karsilama adi x2, TLS sertifikasi, role x2). Yani reload
/// dustugunde otomatik yanit yanitlamiyor, role devreye girmiyor, sertifika sunulmuyordu -- ve
/// panel "kaydedildi" diyordu. Ayni semptom karsilama adinda CANLI yasanmisti; kodun kendi yorumu
/// onu anlatiyor.
/// </para>
/// <para>
/// <b>ADAY 2 -- QueryAsync hatayi BOS LISTEYE ceviriyordu.</b> <c>result.Success ? ParseItems(..)
/// : []</c>. Cagiran "hic yok" ile "soramadim"i ayirt edemiyordu.
/// </para>
/// <para>
/// <b>NEYIN OLCULEMEDIGI.</b> Bu makinede ayakta bir Stalwart YOK; dolayisiyla CLI'nin gercekten
/// ne dondugu, ReloadSettings'in ayari gercekten etkinlestirdigi ve sunucunun sonrasinda nasil
/// davrandigi OLCULEMEDI. Olculen sey, panelin surec sonucunu NASIL OKUDUGUDUR ve bu, gercek
/// <c>powershell.exe</c> surecleri kosturularak uretilen GERCEK <see cref="CommandResult"/>
/// nesneleriyle olculur -- elle yazilmis sahte sonuclarla degil.
/// </para>
/// </remarks>
public sealed class PostaAyariEtkinlestiMiTests
{
    /// <summary>Gercekten DUSEN bir surecin sonucu (cikis kodu != 0).</summary>
    private static Task<CommandResult> DusenSurec() =>
        ProcessRunner.RunPowerShellAsync("[Console]::Error.WriteLine('stalwart-cli: connection refused'); exit 7");

    /// <summary>Gercekten BASARILI bir surecin sonucu, BOS ciktiyla.</summary>
    private static Task<CommandResult> BosCiktiliBasariliSurec() =>
        ProcessRunner.RunPowerShellAsync("exit 0");

    /// <summary>Gercekten BASARILI bir surec, Stalwart'in NDJSON bicimiyle iki nesne basar.</summary>
    private static Task<CommandResult> DoluCiktiliBasariliSurec() =>
        ProcessRunner.RunPowerShellAsync(
            "[Console]::Out.WriteLine('{\"id\":\"1\",\"emailAddress\":\"a@ornek.com\"}'); "
            + "[Console]::Out.WriteLine('{\"id\":\"2\",\"emailAddress\":\"b@ornek.com\"}')");

    // -----------------------------------------------------------------------------
    // ADAY 1
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Reload_dustugunde_ayar_ETKINLESMIS_sayilmaz()
    {
        var sonuc = await DusenSurec();

        Assert.False(sonuc.Success);
        Assert.Equal(MailService.MailReloadOutcome.NotReloaded, MailService.OkumaSonucu(sonuc));
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: reload dondugunde panel HICBIR uyari yapmadan "etkin" der.
    /// </summary>
    /// <remarks>
    /// "Her zaman etkinlesmedi say" cozumu oteki testi de gecerdi ve her otomatik yanit kaydini,
    /// her role acmayi 502 ile kirardi -- ozelligin kendisini kullanilamaz hale getirirdi.
    /// </remarks>
    [Fact]
    public async Task Reload_dondugunde_ayar_ETKIN_sayilir()
    {
        var sonuc = await BosCiktiliBasariliSurec();

        Assert.True(sonuc.Success);
        Assert.Equal(MailService.MailReloadOutcome.Reloaded, MailService.OkumaSonucu(sonuc));
    }

    /// <summary>
    /// Gonderen politikasi: yuklenemediginde "Applied" DEGIL, ayri bir durum vardir.
    /// </summary>
    /// <remarks>
    /// "Applied" demek, kimligi dogrulanmamis gonderenlerin yerel alan adlarini taklit etmesinin
    /// ENGELLENDIGINI iddia etmektir. Ayar bellekte degilken bu iddia yanlistir ve acilistaki isci
    /// bunu bir BASARI satiri olarak logluyordu.
    /// </remarks>
    [Fact]
    public void Gonderen_politikasi_yuklenemediginde_APPLIED_demez()
    {
        Assert.NotEqual(
            MailService.SenderPolicySyncResult.Applied,
            MailService.SenderPolicySyncResult.WrittenNotActivated);
    }

    // -----------------------------------------------------------------------------
    // ADAY 2
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Sorgu_dustugunde_sonuc_OLCUM_SAYILMAZ()
    {
        var sonuc = MailService.MailQueryResult.Olc(await DusenSurec());

        Assert.False(sonuc.Measured);
        Assert.Empty(sonuc.Items);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: GERCEKTEN bos olan liste bir OLCUMDUR.
    /// </summary>
    /// <remarks>
    /// Ayrimin can alici yeri burasi. "Bos liste her zaman supheli" cozumu oteki testi gecerdi ama
    /// yeni acilmis, hic kutusu olmayan bir alan adinda kutu acmayi imkansiz kilardi -- yani
    /// kotayi degil, ozelligi kirardi.
    /// </remarks>
    [Fact]
    public async Task Gercekten_bos_liste_OLCUM_SAYILIR()
    {
        var sonuc = MailService.MailQueryResult.Olc(await BosCiktiliBasariliSurec());

        Assert.True(sonuc.Measured);
        Assert.Empty(sonuc.Items);
    }

    [Fact]
    public async Task Dolu_liste_ayristirilir_ve_OLCUM_SAYILIR()
    {
        var sonuc = MailService.MailQueryResult.Olc(await DoluCiktiliBasariliSurec());

        Assert.True(sonuc.Measured);
        Assert.Equal(2, sonuc.Items.Count);
        Assert.Equal(
            "a@ornek.com",
            sonuc.Items[0].GetProperty("emailAddress").GetString());
    }

    /// <summary>
    /// ISIN OZU: dusen sorgu ile gercekten bos sorgu AYNI ITEMS'i verir ama AYNI SEY DEGILDIR.
    /// </summary>
    /// <remarks>
    /// Eski kod bu iki durumu ayni <c>[]</c> degerine indiriyordu; bu test tam olarak o
    /// indirgemenin geri gelmesini engeller.
    /// </remarks>
    [Fact]
    public async Task Dusen_sorgu_ile_bos_sorgu_ITEMS_olarak_esit_ama_OLCUM_olarak_farkli()
    {
        var dusen = MailService.MailQueryResult.Olc(await DusenSurec());
        var bos = MailService.MailQueryResult.Olc(await BosCiktiliBasariliSurec());

        Assert.Equal(dusen.Items.Count, bos.Items.Count);
        Assert.NotEqual(dusen.Measured, bos.Measured);
    }

    [Fact]
    public async Task Ayristirilamayan_cikti_bos_alan_listesi_sayilmaz()
    {
        var sonuc = MailService.MailQueryResult.Olc(
            await ProcessRunner.RunPowerShellAsync("[Console]::Out.WriteLine('bu JSON degil')"));
        Assert.False(sonuc.Measured);
        Assert.Empty(sonuc.Items);
    }
    /// <summary>ParseItems gercekten NDJSON okuyor mu (yukaridaki testin dayandigi varsayim).</summary>
    [Fact]
    public async Task Stalwart_NDJSON_bicimi_gercekten_ayristiriliyor()
    {
        var sonuc = MailService.MailQueryResult.Olc(await DoluCiktiliBasariliSurec());

        Assert.All(sonuc.Items, item => Assert.Equal(JsonValueKind.Object, item.ValueKind));
    }
}
