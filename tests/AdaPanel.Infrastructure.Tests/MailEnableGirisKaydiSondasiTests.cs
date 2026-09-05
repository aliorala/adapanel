using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>SetAccountPasswordAsync</c>, <c>DeleteAccountAsync</c> ve <c>CreateAccountAsync</c>:
/// MEAOAU.Login kaydinin GERCEKTEN yazildigi/silindigi OLCULUYOR mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN ACIK.</b> Silme yolunda <c>try { [void]$lg.RemoveLogin() } catch {}</c> vardi:
/// posta kutusu gidiyor, GIRIS KAYDI kaliyor ve panel HTTP 200 donuyordu. Giris kaydi duran bir
/// hesap POSTAYA ERISMEYE DEVAM EDER -- yani "silinen musteri" hala posta okuyabilir. Parola
/// yolunda ise hicbir sey olculmuyordu.
/// </para>
/// <para>
/// <b>NEDEN KONVANSIYON TAHMIN EDILMEDI.</b> Bu makinede MailEnable YOK; COM metotlarinin
/// tamsayi durum kodu konvansiyonu OLCULEMEZ ve yanlis tahmin CANLIDA SAHTE HATA uretir.
/// Onun yerine sonda KENDINI KALIBRE EDER: ayni olcum (a) islemden ONCE var olan kayit uzerinde
/// (pozitif kontrol), (b) var olmasi IMKANSIZ bir kullanici adi uzerinde (negatif kontrol),
/// (c) islemden SONRA kosar. Ilk ikisi sondanin AYIRT EDIP EDEMEDIGINI olcer; ayiramiyorsa
/// karar "silindi" degil <b>OLCULEMEDI</b> olur.
/// </para>
/// <para>
/// <b>OLUSTURMADA KALIBRASYON BASKADIR (bkz. 1b bolumu).</b> Orada kayit islemden once ZATEN
/// YOKTUR: pozitif kontrol KURULAMAZ. Bu yuzden "sonra bos" dali "olusturulmadi" DEGIL,
/// OLCULEMEDI'dir -- silmenin kurali oraya kopyalanamaz.
/// </para>
/// <para>
/// <b>BURADA NE GERCEKTEN KOSUYOR.</b> Asagidaki sinamalar URETIM BETIGININ KENDISINI
/// (<see cref="MailEnableMailService.SilmeBetigi"/> / <see cref="MailEnableMailService.ParolaBetigi"/>)
/// GERCEK <c>powershell.exe</c>'de kosturur; yalnizca COM nesnesinin yerine, betikteki
/// OLCULEBILIRLIK KANCASI (<c>$MeGirisFabrikasi</c>/<c>$MeKutuFabrikasi</c>) uzerinden bir
/// stand-in nesne verilir. Kaynak taramasi YOK: bu oturumda kaynak tarayan sinamalar YEDI kez
/// yanildi.
/// </para>
/// </remarks>
public sealed class MailEnableGirisKaydiSondasiTests
{
    private const string Alan = "ornek.com";
    private const string Yerel = "info";
    private const string Adres = "info@ornek.com";
    private const string YeniParola = "Y3ni-Parola!42";

    // OLUSTURMA icin: sunucuda HENUZ OLMAYAN bir hesap (kalibrasyon farkinin kaynagi).
    private const string YeniYerel = "yeni";
    private const string YeniAdres = "yeni@ornek.com";

    private static MailEnableMailService Servis()
        => new(NullLogger<MailEnableMailService>.Instance);

    /// <summary>
    /// COM yerine gecen stand-in. Davranislar, GERCEK sunucuda hangisinin gecerli oldugunu
    /// BILMEDIGIMIZ dallardir: kaydi bulunca dolduran, bulamayinca sessizce basarisiz olan,
    /// bulamayinca FIRLATAN, ve HER DURUMDA dolduran (sondayi kor eden) sunucu.
    /// </summary>
    private static string SahteCom(
        string girisDavranisi = "normal",
        string silmeDavranisi = "normal",
        string duzenlemeDavranisi = "normal",
        string kutuDavranisi = "normal",
        string kayitliParola = "eski-parola",
        string eklemeDavranisi = "normal",
        string oncedenVarOlanKayit = "")
        => $$"""
            $ErrorActionPreference = 'Stop'
            $script:Kayitlar = @{}
            $script:Kayitlar['{{Adres}}'] = @{ Rights = 'USER'; Password = '{{kayitliParola}}' }
            $script:GirisDavranisi = '{{girisDavranisi}}'
            $script:SilmeDavranisi = '{{silmeDavranisi}}'
            $script:DuzenlemeDavranisi = '{{duzenlemeDavranisi}}'
            $script:KutuDavranisi = '{{kutuDavranisi}}'
            $script:EklemeDavranisi = '{{eklemeDavranisi}}'
            if ('{{oncedenVarOlanKayit}}') {
              $script:Kayitlar['{{oncedenVarOlanKayit}}'] = @{ Rights = 'USER'; Password = 'oksuz-eski-parola' }
            }
            $MeGirisFabrikasi = {
              $n = New-Object psobject -Property @{ Account = ''; UserName = ''; Rights = ''; Password = ''; Status = 0 }
              $n | Add-Member -MemberType ScriptMethod -Name GetLogin -Value {
                if ($script:GirisDavranisi -eq 'hep-doldurur') { $this.Rights = 'USER'; return 0 }
                if ($script:GirisDavranisi -eq 'hep-bos-doner') { return 0 }
                if ($script:Kayitlar.ContainsKey($this.UserName)) {
                  $this.Rights = $script:Kayitlar[$this.UserName].Rights
                  $this.Password = $script:Kayitlar[$this.UserName].Password
                  return 0
                }
                if ($script:GirisDavranisi -eq 'eksikte-firlatir') { throw "giris kaydi yok: $($this.UserName)" }
                return 1
              }
              $n | Add-Member -MemberType ScriptMethod -Name RemoveLogin -Value {
                if ($script:SilmeDavranisi -eq 'firlatir') { throw 'RemoveLogin: erisim reddedildi' }
                if ($script:SilmeDavranisi -eq 'inatci') { return 1 }
                $script:Kayitlar.Remove($this.UserName)
                return 0
              }
              $n | Add-Member -MemberType ScriptMethod -Name AddLogin -Value {
                if ($script:EklemeDavranisi -eq 'firlatir') { throw 'AddLogin: erisim reddedildi' }
                if ($script:EklemeDavranisi -eq 'yazmaz') { return 1 }
                $script:Kayitlar[$this.UserName] = @{ Rights = $this.Rights; Password = $this.Password }
                return 0
              }
              $n | Add-Member -MemberType ScriptMethod -Name EditLogin -Value {
                if ($script:DuzenlemeDavranisi -eq 'yazmaz') { $script:Kayitlar.Remove($this.UserName); return 1 }
                if (-not $script:Kayitlar.ContainsKey($this.UserName)) { return 1 }
                $deger = $this.Password
                if ($script:DuzenlemeDavranisi -eq 'ozetler') { $deger = 'OZET:' + $this.Password }
                $script:Kayitlar[$this.UserName].Password = $deger
                return 0
              }
              $n
            }
            $MeKutuFabrikasi = {
              $k = New-Object psobject -Property @{ Postoffice = ''; Mailbox = ''; Limit = 0; Status = 0; RedirectAddress = ''; RedirectStatus = 0 }
              $k | Add-Member -MemberType ScriptMethod -Name RemoveMailbox -Value {
                if ($script:KutuDavranisi -eq 'firlatir') { throw 'RemoveMailbox: kutu kilitli' }
                return 0
              }
              $k | Add-Member -MemberType ScriptMethod -Name AddMailbox -Value {
                if ($script:KutuDavranisi -eq 'firlatir') { throw 'AddMailbox: kutu zaten var' }
                return 0
              }
              $k
            }
            """;

    private static async Task<MailEnableMailService.GirisSondaOkumasi> KosturAsync(string sahte, string uretimBetigi)
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(sahte + "\n" + uretimBetigi);
        Assert.True(
            sonuc.ExitCode == 0,
            $"Uretim betigi GERCEK powershell.exe'de dusmemeli. Cikis={sonuc.ExitCode} Hata={sonuc.StandardError} Cikti={sonuc.StandardOutput}");
        return MailEnableMailService.SondayiOku(sonuc.StandardOutput);
    }

    private static MailEnableMailService.GirisKaydiOlcumu Karar(MailEnableMailService.GirisSondaOkumasi sonda)
        => MailEnableMailService.GirisSondasiniYorumla(sonda.Ayristirildi, sonda.OnceDoldu, sonda.KontrolDoldu, sonda.SonraDoldu);

    // =====================================================================================
    // 1) SILME -- GERCEK powershell.exe, GERCEK uretim betigi
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: her sey yolundayken sonda "gitti" der ve panel sade
    /// "silindi" diyebilir. Aksi halde her dogru silme de hata gorunurdu.
    /// </summary>
    [Fact]
    public async Task Giris_kaydi_GERCEKTEN_gidince_YOK_olculur()
    {
        var sonda = await KosturAsync(SahteCom(), MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));

        Assert.True(sonda.Ayristirildi);
        Assert.True(sonda.OnceDoldu, "POZITIF KONTROL: sonda islemden ONCE kaydi gormeliydi.");
        Assert.False(sonda.KontrolDoldu, "NEGATIF KONTROL: olmayan kullanici DOLU gorunmemeliydi.");
        Assert.False(sonda.SonraDoldu);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Yok, Karar(sonda));
        Assert.Equal(string.Empty, MailEnableMailService.SilmeHatalari(sonda));
    }

    /// <summary>
    /// ASIL ACIK: <c>RemoveLogin</c> hicbir sey yapmiyor. Posta kutusu gitse bile GIRIS KAYDI
    /// duruyor -- silinen musteri postaya erisebilir. Panel bunu artik GORUYOR.
    /// </summary>
    [Fact]
    public async Task RemoveLogin_HICBIR_SEY_yapmazsa_giris_kaydi_VAR_olculur()
    {
        var sonda = await KosturAsync(SahteCom(silmeDavranisi: "inatci"), MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));

        Assert.True(sonda.OnceDoldu);
        Assert.False(sonda.KontrolDoldu);
        Assert.True(sonda.SonraDoldu);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Var, Karar(sonda));
    }

    /// <summary>
    /// YUTULAN HATA: eski kod <c>catch {}</c> ile RemoveLogin hatasini yok ediyordu. Artik hem
    /// hata metni yuzeye cikiyor hem de kaydin durdugu olculuyor.
    /// </summary>
    [Fact]
    public async Task RemoveLogin_FIRLATIRSA_hata_YUTULMAZ_ve_kayit_VAR_olculur()
    {
        var sonda = await KosturAsync(SahteCom(silmeDavranisi: "firlatir"), MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));

        Assert.Contains("erisim reddedildi", sonda.GirisHata, StringComparison.Ordinal);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Var, Karar(sonda));
        Assert.Contains("RemoveLogin:", MailEnableMailService.SilmeHatalari(sonda), StringComparison.Ordinal);
    }

    /// <summary>Posta kutusu silme hatasi da yutuluyordu; o da yuzeye cikiyor.</summary>
    [Fact]
    public async Task RemoveMailbox_FIRLATIRSA_hata_YUTULMAZ()
    {
        var sonda = await KosturAsync(SahteCom(kutuDavranisi: "firlatir"), MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));

        Assert.Contains("kutu kilitli", sonda.KutuHata, StringComparison.Ordinal);
        Assert.Contains("RemoveMailbox:", MailEnableMailService.SilmeHatalari(sonda), StringComparison.Ordinal);
    }

    /// <summary>
    /// SONDANIN KOR OLDUGU SUNUCU: <c>GetLogin</c> kayit olmasa da alanlari dolduruyor.
    /// Boyle bir sunucuda "duruyor" demek SAHTE HATA olurdu; karar OLCULEMEDI olmali.
    /// </summary>
    [Fact]
    public async Task Sonda_AYIRT_EDEMEYEN_sunucuda_OLCULEMEDI_der()
    {
        var sonda = await KosturAsync(SahteCom(girisDavranisi: "hep-doldurur"), MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));

        Assert.True(sonda.KontrolDoldu, "Negatif kontrol DOLU gelmeliydi: sonda ayirt edemiyor.");
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, Karar(sonda));
    }

    /// <summary>
    /// BASKA BIR KONVANSIYON: <c>GetLogin</c> eksik kayitta FIRLATIYOR. Sonda bu sunucuda da
    /// dogru olcer -- cunku konvansiyon tahmin edilmiyor, kalibrasyonla OLCULUYOR.
    /// </summary>
    [Fact]
    public async Task GetLogin_eksik_kayitta_FIRLATAN_sunucuda_da_dogru_olcer()
    {
        var gitti = await KosturAsync(
            SahteCom(girisDavranisi: "eksikte-firlatir"),
            MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Yok, Karar(gitti));

        var duruyor = await KosturAsync(
            SahteCom(girisDavranisi: "eksikte-firlatir", silmeDavranisi: "inatci"),
            MailEnableMailService.SilmeBetigi(Alan, Yerel, Adres));
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Var, Karar(duruyor));
    }

    // =====================================================================================
    // 1b) OLUSTURMA -- SILMENIN KARDESI, ama KALIBRASYONU BASKA
    //
    // ACIK: CreateAccountAsync HEM kutuyu HEM giris kaydini yaziyor, ama yalniz KUTU
    // dogrulaniyordu. AddLogin sessizce basarisiz olursa panel "hesap acildi" der, kutu listede
    // GORUNUR, kullanici IMAP/SMTP'ye GIRIS YAPAMAZ.
    //
    // KALIBRASYON FARKI: silmede `once` sondasi POZITIF KONTROLDUR (kayit islemden once VARDIR).
    // Olusturmada kayit islemden once ZATEN YOKTUR -> pozitif kontrol KURULAMAZ. Bu yuzden
    // "kontrol bos + sonra bos" dali "olusturulmadi" DEGIL, OLCULEMEDI'dir.
    // =====================================================================================

    private static MailEnableMailService.GirisKaydiOlcumu OlusturmaKarari(MailEnableMailService.GirisSondaOkumasi sonda)
        => MailEnableMailService.OlusturmaSondasiniYorumla(sonda.Ayristirildi, sonda.OnceDoldu, sonda.KontrolDoldu, sonda.SonraDoldu);

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: her sey yolundayken giris kaydi VAR olculur, kapi ACILIR ve
    /// panel sade "olusturuldu" der. Bu sinama ayni zamanda "her durumda OLCULEMEDI de"
    /// cozumunu YESIL GORUNMEKTEN alikoyar.
    /// </summary>
    [Fact]
    public async Task Giris_kaydi_GERCEKTEN_yazilinca_VAR_olculur_ve_panel_sade_olusturuldu_der()
    {
        var sonda = await KosturAsync(
            SahteCom(),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.True(sonda.Ayristirildi);
        Assert.False(sonda.OnceDoldu, "Kayit islemden ONCE yoktu: sonda onu DOLU gormemeliydi.");
        Assert.False(sonda.KontrolDoldu, "NEGATIF KONTROL: olmayan kullanici DOLU gorunmemeliydi.");
        Assert.True(sonda.SonraDoldu, "AddLogin yazdi: sonda kaydi GORMELIYDI.");
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Var, OlusturmaKarari(sonda));
        Assert.Equal(string.Empty, MailEnableMailService.OlusturmaHatalari(sonda));

        // Kapi ACILIR: olculmus bir basari HATA seridi cizmez.
        MailEnableMailService.OlusturmaGirisKapisiniAc(OlusturmaKarari(sonda), YeniAdres, string.Empty);
    }

    /// <summary>
    /// ASIL ACIK: <c>AddLogin</c> hicbir sey yazmiyor (COM tamsayi durum kodu donuyor, istisna
    /// DEGIL). Kutu olusur, panel eskiden "olusturuldu" derdi; artik "OLCULEMEDI" der ve
    /// kullaniciya girisi sinamasini soyler.
    /// </summary>
    [Fact]
    public async Task AddLogin_HICBIR_SEY_yazmazsa_panel_OLUSTURULDU_DEMEZ()
    {
        var sonda = await KosturAsync(
            SahteCom(eklemeDavranisi: "yazmaz"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.False(sonda.SonraDoldu);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(sonda));

        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.OlusturmaGirisKapisiniAc(OlusturmaKarari(sonda), YeniAdres, string.Empty));
        Assert.Equal("mail.account_create_login_unverified", hata.MessageKey);
        Assert.Contains(YeniAdres, hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>KALIBRASYON KARARININ CANLI KANITI.</b> Ayni "sonra BOS" ciktisini uretmenin IKI yolu
    /// var: (a) AddLogin yazmadi, (b) sunucu <c>GetLogin</c>'de hicbir alani geri yazmiyor
    /// (kayit VARDIR ama sonda goremez). Ikisi ayirt EDILEMEZ; o yuzden ikisinde de ayni sey
    /// soylenir: OLCULEMEDI. "Olusturulmadi" deseydik (b) durumundaki CALISAN kurulumlarda
    /// SAHTE HATA uretirdik.
    /// </summary>
    [Fact]
    public async Task Sonda_HICBIR_SEY_geri_okumuyorsa_bu_da_OLCULEMEDI_dir()
    {
        var yazmadi = await KosturAsync(
            SahteCom(eklemeDavranisi: "yazmaz"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        // Kayit GERCEKTEN yazildi, ama sunucu geri okumada bos donuyor (GetLogin nobetciyi
        // degistirmiyor): sonda korlestigi icin ayni cikti.
        var korSunucu = await KosturAsync(
            SahteCom(girisDavranisi: "hep-bos-doner"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.False(yazmadi.SonraDoldu);
        Assert.False(korSunucu.SonraDoldu);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(yazmadi));
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(korSunucu));
    }

    /// <summary>Yutulan <c>AddLogin</c> hatasi ayrintiya tasinir (once tamamen kayboluyordu).</summary>
    [Fact]
    public async Task AddLogin_FIRLATIRSA_hata_YUTULMAZ()
    {
        var sonda = await KosturAsync(
            SahteCom(eklemeDavranisi: "firlatir"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.Contains("erisim reddedildi", sonda.GirisHata, StringComparison.Ordinal);
        Assert.Contains("AddLogin:", MailEnableMailService.OlusturmaHatalari(sonda), StringComparison.Ordinal);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(sonda));
    }

    /// <summary>Kutu ekleme hatasi da yutuluyordu (eski kod: <c>[void]$mb.AddMailbox()</c>).</summary>
    [Fact]
    public async Task AddMailbox_FIRLATIRSA_hata_YUTULMAZ()
    {
        var sonda = await KosturAsync(
            SahteCom(kutuDavranisi: "firlatir"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.Contains("kutu zaten var", sonda.KutuHata, StringComparison.Ordinal);
        Assert.Contains("AddMailbox:", MailEnableMailService.OlusturmaHatalari(sonda), StringComparison.Ordinal);
    }

    /// <summary>KOR SONDA: <c>GetLogin</c> olmayan kaydi da dolduruyor -> hicbir sey kanitlanamaz.</summary>
    [Fact]
    public async Task Olusturmada_AYIRT_EDEMEYEN_sunucuda_OLCULEMEDI_der()
    {
        var sonda = await KosturAsync(
            SahteCom(girisDavranisi: "hep-doldurur"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.True(sonda.KontrolDoldu, "Negatif kontrol DOLU gelmeliydi: sonda ayirt edemiyor.");
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(sonda));
    }

    /// <summary>
    /// IKINCI NEGATIF KONTROL: giris kaydi islemden ONCE de vardi (yarim silinmis bir hesabin
    /// OKSUZ giris kaydi -- bu depoda olculmus bir sinif). "Sonra dolu" o zaman BIZIM
    /// yazdigimizi GOSTERMEZ: kayit ESKI PAROLAYLA duruyor olabilir, yani yeni parolayla giris
    /// yine yapilamaz. Karar OLCULEMEDI.
    /// </summary>
    [Fact]
    public async Task Giris_kaydi_islemden_ONCE_de_varsa_VAR_denmez()
    {
        var sonda = await KosturAsync(
            SahteCom(eklemeDavranisi: "yazmaz", oncedenVarOlanKayit: YeniAdres),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.True(sonda.OnceDoldu, "Oksuz kayit islemden ONCE duruyordu.");
        Assert.True(sonda.SonraDoldu, "Kayit hala duruyor -- ama BIZIM yazdigimiz degil.");
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(sonda));
    }

    /// <summary>
    /// Baska bir konvansiyon: <c>GetLogin</c> eksik kayitta FIRLATIYOR. Sonda burada da dogru
    /// olcer, cunku konvansiyon tahmin edilmiyor.
    /// </summary>
    [Fact]
    public async Task Olusturmada_GetLogin_FIRLATAN_sunucuda_da_dogru_olcer()
    {
        var yazildi = await KosturAsync(
            SahteCom(girisDavranisi: "eksikte-firlatir"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Var, OlusturmaKarari(yazildi));

        var yazilmadi = await KosturAsync(
            SahteCom(girisDavranisi: "eksikte-firlatir", eklemeDavranisi: "yazmaz"),
            MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, OlusturmaKarari(yazilmadi));
    }

    /// <summary>
    /// SIR NOBETCISI: yeni hesabin parolasi betikte GECER ama sonda ciktisina YAZILMAZ
    /// (cikti gunluge/hata ayrintisina/StatusMessage'a dusebilir).
    /// </summary>
    [Fact]
    public async Task Olusturma_sonda_ciktisinda_parola_GORUNMEZ()
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            SahteCom() + "\n" + MailEnableMailService.OlusturmaBetigi(Alan, YeniYerel, YeniAdres, YeniParola));

        Assert.Equal(0, sonuc.ExitCode);
        Assert.DoesNotContain(YeniParola, sonuc.StandardOutput, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 2) PAROLA -- GERCEK powershell.exe, GERCEK uretim betigi
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: parola GERI OKUNABILEN bir sunucuda sonuc DOGRULANIR ve
    /// kullaniciya HICBIR uyari gosterilmez (StatusMessage panelde HATA seridi cizer).
    /// </summary>
    [Fact]
    public async Task Parola_geri_okunabiliyorsa_DOGRULANIR_ve_uyari_gosterilmez()
    {
        var sonda = await KosturAsync(SahteCom(), MailEnableMailService.ParolaBetigi(Alan, Adres, YeniParola));

        Assert.True(sonda.Eslesme);
        var olcum = MailEnableMailService.ParolaSonucunuYorumla(Karar(sonda), sonda.Eslesme);
        Assert.Equal(MailEnableMailService.ParolaOlcumu.Dogrulandi, olcum);
        Assert.Equal(string.Empty, MailEnableMailService.ParolaDurumCumlesi(olcum));
    }

    /// <summary>
    /// SUNUCU PAROLAYI OZETLIYORSA: geri okunan deger yazilanla esit degildir. Buna
    /// "uygulanmadi" demek SAHTE HATA olurdu; dogru cevap "DOGRULANMADI" -- ve firlatmaz.
    /// </summary>
    [Fact]
    public async Task Parola_OZETLENEREK_saklaniyorsa_DOGRULANMADI_denir_ama_HATA_verilmez()
    {
        var sonda = await KosturAsync(
            SahteCom(duzenlemeDavranisi: "ozetler"),
            MailEnableMailService.ParolaBetigi(Alan, Adres, YeniParola));

        Assert.False(sonda.Eslesme);
        var olcum = MailEnableMailService.ParolaSonucunuYorumla(Karar(sonda), sonda.Eslesme);
        Assert.Equal(MailEnableMailService.ParolaOlcumu.GeriOkunamadi, olcum);
        Assert.Contains("DOGRULANMADI", MailEnableMailService.ParolaDurumCumlesi(olcum), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kalibre sonda islemden SONRA kaydi bulamiyorsa parola UYGULANMAMISTIR -- ve bu bir
    /// tahmin degil olcumdur: ayni sonda islemden ONCE ayni kaydi bulmustu.
    /// </summary>
    [Fact]
    public async Task Parola_kaydi_islemden_sonra_YOKSA_UYGULANMADI_denir()
    {
        var sonda = await KosturAsync(
            SahteCom(duzenlemeDavranisi: "yazmaz"),
            MailEnableMailService.ParolaBetigi(Alan, Adres, YeniParola));

        Assert.True(sonda.OnceDoldu);
        Assert.False(sonda.SonraDoldu);
        Assert.Equal(
            MailEnableMailService.ParolaOlcumu.Uygulanmadi,
            MailEnableMailService.ParolaSonucunuYorumla(Karar(sonda), sonda.Eslesme));
    }

    /// <summary>
    /// SIR NOBETCISI: parola dogrulamasi ESITLIK olarak PowerShell tarafinda olculur; parolanin
    /// KENDISI cikti akisina (dolayisiyla gunluge, hata ayrintisina, StatusMessage'a) YAZILMAZ.
    /// </summary>
    [Fact]
    public async Task Parola_sonda_ciktisinda_GORUNMEZ()
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            SahteCom() + "\n" + MailEnableMailService.ParolaBetigi(Alan, Adres, YeniParola));

        Assert.Equal(0, sonuc.ExitCode);
        Assert.DoesNotContain(YeniParola, sonuc.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"sonraEslesme\":true", sonuc.StandardOutput.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    // =====================================================================================
    // 3) KARAR KURALI -- saf, mutasyona acik
    // =====================================================================================

    [Fact]
    public void Kalibre_edilemeyen_sonda_HER_ZAMAN_OLCULEMEDI_verir()
    {
        // Pozitif kontrol bos: sonda VAR olani goremiyor -> yokluk kaniti sayilmaz.
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
            MailEnableMailService.GirisSondasiniYorumla(true, onceDoldu: false, kontrolDoldu: false, sonraDoldu: false));

        // Negatif kontrol dolu: sonda YOK olani da dolu goruyor -> varlik kaniti sayilmaz.
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
            MailEnableMailService.GirisSondasiniYorumla(true, onceDoldu: true, kontrolDoldu: true, sonraDoldu: true));

        // Cikti ayristirilamadi: "her sey yolunda" DEGILDIR.
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
            MailEnableMailService.GirisSondasiniYorumla(false, onceDoldu: true, kontrolDoldu: false, sonraDoldu: false));
    }

    [Fact]
    public void Kalibre_sondada_sonraki_okuma_dogrudan_CEVAPTIR()
    {
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Var,
            MailEnableMailService.GirisSondasiniYorumla(true, true, false, sonraDoldu: true));
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Yok,
            MailEnableMailService.GirisSondasiniYorumla(true, true, false, sonraDoldu: false));
    }

    /// <summary>
    /// <b>KALIBRASYON ASIMETRISI, TAM TABLO.</b> Olusturmada TEK bir olumlu dal vardir:
    /// ayristirildi + once BOS + kontrol BOS + sonra DOLU. Diger 15 birlesim OLCULEMEDI'dir --
    /// ve hicbiri <see cref="MailEnableMailService.GirisKaydiOlcumu.Yok"/> DEGILDIR: olusturmada
    /// "kayit YOK" iddiasini destekleyecek pozitif kontrol kurulamaz.
    /// </summary>
    [Fact]
    public void Olusturmada_YOK_karari_ASLA_verilmez_ve_tek_olumlu_dal_vardir()
    {
        for (var maske = 0; maske < 16; maske++)
        {
            var ayristirildi = (maske & 1) != 0;
            var once = (maske & 2) != 0;
            var kontrol = (maske & 4) != 0;
            var sonra = (maske & 8) != 0;

            var karar = MailEnableMailService.OlusturmaSondasiniYorumla(ayristirildi, once, kontrol, sonra);
            var beklenen = ayristirildi && !once && !kontrol && sonra
                ? MailEnableMailService.GirisKaydiOlcumu.Var
                : MailEnableMailService.GirisKaydiOlcumu.Olculemedi;

            Assert.Equal(beklenen, karar);
            Assert.NotEqual(MailEnableMailService.GirisKaydiOlcumu.Yok, karar);
        }
    }

    /// <summary>
    /// SILMENIN KURALI OLUSTURMAYA KOPYALANAMAZ. Ayni girdide iki fonksiyon BILEREK farkli
    /// cevap verir: silmede <c>once</c> POZITIF KONTROLDUR, olusturmada boyle bir ayak YOKTUR.
    /// </summary>
    [Fact]
    public void Olusturma_kurali_SILME_kuralinin_KOPYASI_DEGIL()
    {
        // Silme: pozitif kontrol dolu + negatif kontrol bos + sonra bos = KAYIT GITTI.
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Yok,
            MailEnableMailService.GirisSondasiniYorumla(true, onceDoldu: true, kontrolDoldu: false, sonraDoldu: false));

        // Olusturma: ayni "sonra bos" -- ama pozitif kontrol YOK, dolayisiyla yokluk KANITLANMADI.
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
            MailEnableMailService.OlusturmaSondasiniYorumla(true, onceDoldu: false, kontrolDoldu: false, sonraDoldu: false));

        // Ve olusturmada "once dolu" bir KALIBRASYON degil, bir SUPHEDIR (oksuz kayit).
        Assert.Equal(
            MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
            MailEnableMailService.OlusturmaSondasiniYorumla(true, onceDoldu: true, kontrolDoldu: false, sonraDoldu: true));
    }

    /// <summary>Nobetci DEGISMEDIYSE kayit okunmamistir; hata varsa okuma zaten gecersizdir.</summary>
    [Fact]
    public void Nobetci_degismediyse_sonda_BOS_sayilir()
    {
        Assert.False(MailEnableMailService.SondaDoldu(string.Empty, MailEnableMailService.SondaNobetcisi));
        Assert.True(MailEnableMailService.SondaDoldu(string.Empty, "USER"));
        Assert.False(MailEnableMailService.SondaDoldu("getlogin: patladi", "USER"));
    }

    [Fact]
    public void Ayristirilamayan_cikti_OLCUM_SAYILMAZ()
    {
        var sonda = MailEnableMailService.SondayiOku("bu bir JSON degil");

        Assert.False(sonda.Ayristirildi);
        Assert.Equal(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, Karar(sonda));
    }

    [Fact]
    public void Parola_karari_kayit_olcumunden_TURETILIR()
    {
        Assert.Equal(
            MailEnableMailService.ParolaOlcumu.Olculemedi,
            MailEnableMailService.ParolaSonucunuYorumla(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, true));
        Assert.Equal(
            MailEnableMailService.ParolaOlcumu.Uygulanmadi,
            MailEnableMailService.ParolaSonucunuYorumla(MailEnableMailService.GirisKaydiOlcumu.Yok, false));
        Assert.Equal(
            MailEnableMailService.ParolaOlcumu.GeriOkunamadi,
            MailEnableMailService.ParolaSonucunuYorumla(MailEnableMailService.GirisKaydiOlcumu.Var, false));
        Assert.Equal(
            MailEnableMailService.ParolaOlcumu.Dogrulandi,
            MailEnableMailService.ParolaSonucunuYorumla(MailEnableMailService.GirisKaydiOlcumu.Var, true));

        // OLCULEMEDI ile GERI OKUNAMADI ayri cumlelerdir; ikisi de kullaniciya bir sey SOYLER.
        Assert.NotEqual(
            MailEnableMailService.ParolaDurumCumlesi(MailEnableMailService.ParolaOlcumu.Olculemedi),
            MailEnableMailService.ParolaDurumCumlesi(MailEnableMailService.ParolaOlcumu.GeriOkunamadi));
        Assert.NotEmpty(MailEnableMailService.ParolaDurumCumlesi(MailEnableMailService.ParolaOlcumu.Uygulanmadi));
    }

    [Fact]
    public void Yutulmus_silme_hatalari_AYRINTIYA_tasinir()
    {
        var temiz = new MailEnableMailService.GirisSondaOkumasi(true, true, false, false, false, string.Empty, string.Empty);
        Assert.Equal(string.Empty, MailEnableMailService.SilmeHatalari(temiz));

        var hatali = new MailEnableMailService.GirisSondaOkumasi(true, true, false, true, false, "kutu patladi", "giris patladi");
        var metin = MailEnableMailService.SilmeHatalari(hatali);
        Assert.Contains("kutu patladi", metin, StringComparison.Ordinal);
        Assert.Contains("giris patladi", metin, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 4) KAPILAR -- olcumun SONUCUNA BAKILIYOR mu?
    // =====================================================================================

    [Fact]
    public void Giris_kaydi_DURUYORSA_silindi_denmez()
    {
        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.GirisKapisiniAc(MailEnableMailService.GirisKaydiOlcumu.Var, Adres, " [RemoveLogin: reddedildi]"));

        Assert.Equal("mail.account_delete_login_survived", hata.MessageKey);
        Assert.Contains(Adres, hata.Details, StringComparison.Ordinal);
        Assert.Contains("RemoveLogin: reddedildi", hata.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Giris_kaydi_OLCULEMEDIYSE_ayri_bir_sey_soylenir()
    {
        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.GirisKapisiniAc(MailEnableMailService.GirisKaydiOlcumu.Olculemedi, Adres, string.Empty));

        Assert.Equal("mail.account_delete_login_unverified", hata.MessageKey);
    }

    /// <summary>FAZLA KAPATMA NOBETCISI: kayit GITTIYSE kapi ACILIR, panel sade "silindi" der.</summary>
    [Fact]
    public void Giris_kaydi_GITTIYSE_kapi_ACILIR()
        => MailEnableMailService.GirisKapisiniAc(MailEnableMailService.GirisKaydiOlcumu.Yok, Adres, string.Empty);

    [Fact]
    public void Olusturmada_giris_kaydi_OLCULEMEDIYSE_olusturuldu_denmez()
    {
        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.OlusturmaGirisKapisiniAc(
                MailEnableMailService.GirisKaydiOlcumu.Olculemedi,
                YeniAdres,
                " [AddLogin: reddedildi]"));

        Assert.Equal("mail.account_create_login_unverified", hata.MessageKey);
        Assert.Contains(YeniAdres, hata.Details, StringComparison.Ordinal);
        Assert.Contains("AddLogin: reddedildi", hata.Details, StringComparison.Ordinal);

        // Kapi, karar "Var" DEGILSE her durumda kapalidir -- "Yok" da tek cumleyle karsilanir,
        // cunku olusturmada "Yok" iddiasi zaten uretilmez (bkz. tam tablo sinamasi).
        Assert.Equal(
            "mail.account_create_login_unverified",
            Assert.Throws<AppException>(() =>
                MailEnableMailService.OlusturmaGirisKapisiniAc(MailEnableMailService.GirisKaydiOlcumu.Yok, YeniAdres, string.Empty)).MessageKey);
    }

    /// <summary>FAZLA KAPATMA NOBETCISI: kayit YAZILDIYSA kapi ACILIR, panel sade "olusturuldu" der.</summary>
    [Fact]
    public void Olusturmada_giris_kaydi_YAZILDIYSA_kapi_ACILIR()
        => MailEnableMailService.OlusturmaGirisKapisiniAc(MailEnableMailService.GirisKaydiOlcumu.Var, YeniAdres, string.Empty);

    /// <summary>Etiketler cagri yerine gore degisir; govde tek kaynaktir.</summary>
    [Fact]
    public void Yutulmus_OLUSTURMA_hatalari_Add_etiketiyle_tasinir()
    {
        var hatali = new MailEnableMailService.GirisSondaOkumasi(true, false, false, false, false, "kutu patladi", "giris patladi");
        var metin = MailEnableMailService.OlusturmaHatalari(hatali);

        Assert.Contains("AddMailbox: kutu patladi", metin, StringComparison.Ordinal);
        Assert.Contains("AddLogin: giris patladi", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove", metin, StringComparison.Ordinal);
        Assert.Equal(string.Empty, MailEnableMailService.OlusturmaHatalari(
            new MailEnableMailService.GirisSondaOkumasi(true, false, false, true, false, string.Empty, string.Empty)));
    }

    [Fact]
    public void Parola_UYGULANMADIYSA_firlatir_gerisinde_FIRLATMAZ()
    {
        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.ParolaKapisiniAc(MailEnableMailService.ParolaOlcumu.Uygulanmadi, Adres));
        Assert.Equal("mail.account_password_not_applied", hata.MessageKey);

        // FAZLA KAPATMA NOBETCISI: "dogrulanamadi" bir HATA DEGILDIR -- parola yazilmis olabilir.
        MailEnableMailService.ParolaKapisiniAc(MailEnableMailService.ParolaOlcumu.Dogrulandi, Adres);
        MailEnableMailService.ParolaKapisiniAc(MailEnableMailService.ParolaOlcumu.Olculemedi, Adres);
        MailEnableMailService.ParolaKapisiniAc(MailEnableMailService.ParolaOlcumu.GeriOkunamadi, Adres);
    }

    /// <summary>
    /// ONCEKI HALI: uyari YALNIZ StatusMessage bos oldugunda yaziliyordu; COM'a erisilemedigi
    /// (yani en kotu) durumda parola uyarisi SESSIZCE DUSUYORDU.
    /// </summary>
    [Fact]
    public void Parola_uyarisi_baska_bir_mesaj_varken_de_KAYBOLMAZ()
    {
        var dolu = new MailWorkspaceDto { StatusMessage = "COM'a erisilemedi." };
        MailEnableMailService.ParolaNotunuYaz(dolu, MailEnableMailService.ParolaOlcumu.Olculemedi);
        Assert.Contains("COM'a erisilemedi.", dolu.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("DOGRULANAMADI", dolu.StatusMessage, StringComparison.Ordinal);

        var bos = new MailWorkspaceDto();
        MailEnableMailService.ParolaNotunuYaz(bos, MailEnableMailService.ParolaOlcumu.GeriOkunamadi);
        Assert.NotEmpty(bos.StatusMessage);

        // FAZLA KAPATMA NOBETCISI: DOGRULANMIS parola SAHTE UYARI uretmez (StatusMessage
        // panelde HATA seridi cizer).
        var dogru = new MailWorkspaceDto();
        MailEnableMailService.ParolaNotunuYaz(dogru, MailEnableMailService.ParolaOlcumu.Dogrulandi);
        Assert.True(string.IsNullOrEmpty(dogru.StatusMessage));
    }

    // =====================================================================================
    // 5) SERVISIN KENDISI -- bu makinede COM YOK; "basarili" DONMEMELI
    // =====================================================================================

    /// <summary>
    /// MailEnable COM'u olmayan bir makinede silme/parola cagrilari SESSIZCE BASARILI
    /// DONMEZ. (Bu makinede gercekten kosuyor: MailEnable kurulu degil.)
    /// </summary>
    [Fact]
    public async Task COM_yokken_silme_ve_parola_BASARILI_DONMEZ()
    {
        var silme = await Assert.ThrowsAsync<AppException>(() => Servis().DeleteAccountAsync(Adres));
        Assert.Contains(silme.MessageKey, new[] { "mail.command_failed", "mail.not_installed" });

        var parola = await Assert.ThrowsAsync<AppException>(() =>
            Servis().SetAccountPasswordAsync(new SetMailAccountPasswordRequest { Address = Adres, Password = YeniParola }));
        Assert.Contains(parola.MessageKey, new[] { "mail.command_failed", "mail.not_installed" });

        var olusturma = await Assert.ThrowsAsync<AppException>(() =>
            Servis().CreateAccountAsync(new CreateMailAccountRequest { Domain = Alan, Address = YeniYerel, Password = YeniParola }));
        Assert.Contains(
            olusturma.MessageKey,
            new[] { "mail.command_failed", "mail.not_installed", "mail.account_create_unverified", "mail.account_create_login_unverified" });
    }

    // =====================================================================================
    // 6) GOVDE OLCUM DIKISI -- karar fonksiyonlarini GOVDE GERCEKTEN CAGIRIYOR MU?
    //
    // OLCULEN BOSLUK: yukaridaki bolumler sondayi (gercek powershell.exe'de) ve karar
    // kurallarini (saf fonksiyon olarak) kapliyordu. Ama DeleteAccountAsync /
    // SetAccountPasswordAsync GOVDESININ o kurallari CAGIRDIGI hicbir yerde olculmuyordu:
    // govdedeki `GirisKapisiniAc(...)` ya da `KapiAcVarlik(...)` satiri silinse bu dosyanin
    // TAMAMI YESIL kalirdi. Bu, depoda defalarca yakalanan "koruma yazildi ama kardes yol
    // cagirmiyor" sinifinin ta kendisidir.
    //
    // DIKIS: MailEnableMailService.KomutKosucusu -- PowerShell'e cikan TEK nokta
    // (RunComAsync) uretimde degismez, sinamada degistirilebilir. Betik SAHTELESTIRILMEZ:
    // govdenin URETTIGI silme/parola betigi GERCEK powershell.exe'de, yalnizca COM'un yerine
    // SahteCom stand-in'i konarak kosturulur. Ikinci cagri (calisma alani okumasi) kontrollu
    // bir JSON ile karsilanir. Kaynak taramasi YOK.
    // =====================================================================================

    /// <summary>Govdenin PowerShell cagrilarini SIRAYLA karsilayan kanca; cagri sayisini da sayar.</summary>
    private sealed class KomutKancasi(Func<int, string, Task<string>> yanit)
    {
        public List<string> Betikler { get; } = [];

        public Task<string> CalistirAsync(string betik, CancellationToken cancellationToken)
        {
            Betikler.Add(betik);
            return yanit(Betikler.Count - 1, betik);
        }
    }

    private static MailEnableMailService Servis(KomutKancasi kanca)
        => new(NullLogger<MailEnableMailService>.Instance) { KomutKosucusu = kanca.CalistirAsync };

    /// <summary>Calisma alani okumasinin (WorkspaceOkuAsync) uretecegi bicimde JSON.</summary>
    private static string CalismaAlaniJson(bool hesapDuruyor)
        => hesapDuruyor
            ? $$"""{"domains":[{"name":"{{Alan}}","status":1}],"accounts":[{"mailbox":"{{Yerel}}","status":1,"limit":-1}],"aliases":[]}"""
            : $$"""{"domains":[{"name":"{{Alan}}","status":1}],"accounts":[],"aliases":[]}""";

    /// <summary>Govdenin urettigi betigi GERCEK powershell.exe'de, SahteCom onekiyle kosturur.</summary>
    private static async Task<string> SahteComUzerindeKosturAsync(string sahte, string govdeninBetigi)
    {
        var sonuc = await ProcessRunner.RunPowerShellAsync(sahte + "\n" + govdeninBetigi);
        Assert.True(
            sonuc.ExitCode == 0,
            $"Govdenin urettigi betik GERCEK powershell.exe'de dusmemeli. Cikis={sonuc.ExitCode} Hata={sonuc.StandardError}");
        return sonuc.StandardOutput;
    }

    /// <summary>
    /// Ilk cagri = govdenin mutasyon betigi (gercek powershell + SahteCom); sonraki cagrilar =
    /// calisma alani okumasi.
    /// </summary>
    private static KomutKancasi Kanca(string sahte, bool hesapDuruyor, bool calismaAlaniOkunabilir = true)
        => new(async (sira, betik) => sira == 0
            ? await SahteComUzerindeKosturAsync(sahte, betik)
            : calismaAlaniOkunabilir
                ? CalismaAlaniJson(hesapDuruyor)
                : "MEAOPO calismadi: bu cikti JSON DEGIL");

    // --- 6a) DeleteAccountAsync GOVDESI ---

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: mesru silme (kutu gitti + giris kaydi gitti) HATA VERMEZ ve
    /// govde calisma alanini doner. Bu sinama olmadan "her seye firlat" cozumu yesil gorunurdu.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_dogrulama_gecince_calisma_alanini_DONER()
    {
        var kanca = Kanca(SahteCom(), hesapDuruyor: false);

        var alan = await Servis(kanca).DeleteAccountAsync(Adres);

        Assert.Equal(2, kanca.Betikler.Count); // mutasyon betigi + calisma alani okumasi
        Assert.Equal(Alan, alan.SelectedDomain);
        Assert.Empty(alan.Accounts);
    }

    /// <summary>
    /// GOVDE, GIRIS KAYDI KAPISINI CAGIRIYOR MU? Kutu gitti (calisma alani bos) ama
    /// <c>RemoveLogin</c> hicbir sey yapmadi. Karar fonksiyonu bunu "Var" olcuyordu; artik
    /// GOVDENIN o karari kullandigi da olculuyor.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_giris_kaydi_DURUYORSA_basarili_DONMEZ()
    {
        var kanca = Kanca(SahteCom(silmeDavranisi: "inatci"), hesapDuruyor: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).DeleteAccountAsync(Adres));

        Assert.Equal("mail.account_delete_login_survived", hata.MessageKey);
        Assert.Contains(Adres, hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// GOVDE, "OLCULEMEDI" dalini da tasiyor mu? Sonda ayirt edemeyen sunucuda panel
    /// "silindi" DEMEZ -- ama "duruyor" da demez.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_sonda_AYIRT_EDEMEYINCE_silindi_DEMEZ()
    {
        var kanca = Kanca(SahteCom(girisDavranisi: "hep-doldurur"), hesapDuruyor: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).DeleteAccountAsync(Adres));

        Assert.Equal("mail.account_delete_login_unverified", hata.MessageKey);
    }

    /// <summary>
    /// GOVDE, POSTA KUTUSU KAPISINI (<c>KapiAcVarlik</c>) CAGIRIYOR MU? Kutu calisma alaninda
    /// HALA duruyor: silme uygulanmamistir.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_posta_kutusu_DURUYORSA_basarili_DONMEZ()
    {
        var kanca = Kanca(SahteCom(), hesapDuruyor: true);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).DeleteAccountAsync(Adres));

        Assert.Equal("mail.account_delete_not_applied", hata.MessageKey);
        Assert.Contains("posta kutusu", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// GOVDE, calisma alani OKUNAMADIGINDA da "silindi" demez: okunamayan liste "hesap yok"
    /// DEGILDIR.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_calisma_alani_OKUNAMAYINCA_silindi_DEMEZ()
    {
        var kanca = Kanca(SahteCom(), hesapDuruyor: false, calismaAlaniOkunabilir: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).DeleteAccountAsync(Adres));

        Assert.Equal("mail.account_delete_unverified", hata.MessageKey);
    }

    /// <summary>
    /// GOVDE, YUTULAN COM hatasini (<c>SilmeHatalari</c>) ayrintiya tasiyor mu? Eski kod
    /// <c>catch {}</c> ile bunu yok ediyordu.
    /// </summary>
    [Fact]
    public async Task Silme_GOVDESI_yutulan_RemoveLogin_hatasini_AYRINTIYA_tasir()
    {
        var kanca = Kanca(SahteCom(silmeDavranisi: "firlatir"), hesapDuruyor: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).DeleteAccountAsync(Adres));

        Assert.Equal("mail.account_delete_login_survived", hata.MessageKey);
        Assert.Contains("RemoveLogin:", hata.Details, StringComparison.Ordinal);
        Assert.Contains("erisim reddedildi", hata.Details, StringComparison.Ordinal);
    }

    // --- 6b) SetAccountPasswordAsync GOVDESI ---
    //
    // DIKKAT -- BURADA YENI BIR DOGRULAMA ICAT EDILMEDI. Parola OKUNAMAZ; sondanin
    // gorebildigi tek sey "giris kaydi islemden SONRA hala duruyor mu"dur (artı sunucu duz
    // metin donduruyorsa esitlik). Asagidaki sinamalar govdenin MEVCUT kapiyi
    // (ParolaKapisiniAc + ParolaNotunuYaz) cagirdigini olcer; yeni bir iddia getirmez.

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: parola geri okunabilen sunucuda govde FIRLATMAZ ve panelde
    /// HICBIR uyari cizmez (StatusMessage hata seridi olarak cizilir).
    /// </summary>
    [Fact]
    public async Task Parola_GOVDESI_dogrulaninca_uyari_YAZMAZ()
    {
        var kanca = Kanca(SahteCom(), hesapDuruyor: true);

        var alan = await Servis(kanca).SetAccountPasswordAsync(
            new SetMailAccountPasswordRequest { Address = Adres, Password = YeniParola });

        Assert.Equal(2, kanca.Betikler.Count);
        Assert.True(string.IsNullOrEmpty(alan.StatusMessage));
    }

    /// <summary>
    /// GOVDE, PAROLA KAPISINI CAGIRIYOR MU? Kalibre sonda islemden SONRA kaydi bulamiyor:
    /// parola UYGULANMAMISTIR ve govde basarili DONMEZ.
    /// </summary>
    [Fact]
    public async Task Parola_GOVDESI_kayit_islemden_sonra_YOKSA_basarili_DONMEZ()
    {
        var kanca = Kanca(SahteCom(duzenlemeDavranisi: "yazmaz"), hesapDuruyor: true);

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis(kanca).SetAccountPasswordAsync(
            new SetMailAccountPasswordRequest { Address = Adres, Password = YeniParola }));

        Assert.Equal("mail.account_password_not_applied", hata.MessageKey);

        // Kapi calisma alani okumasindan ONCE aciliyor: bosuna ikinci tur atilmiyor.
        Assert.Single(kanca.Betikler);
    }

    /// <summary>
    /// GOVDE, "dogrulanmadi" cumlesini (<c>ParolaNotunuYaz</c>) calisma alanina yaziyor mu?
    /// Sunucu parolayi ozetliyorsa bu bir HATA DEGILDIR -- ama SESSIZ de kalmamalidir.
    /// </summary>
    [Fact]
    public async Task Parola_GOVDESI_ozetleyen_sunucuda_FIRLATMAZ_ama_UYARIYI_yazar()
    {
        var kanca = Kanca(SahteCom(duzenlemeDavranisi: "ozetler"), hesapDuruyor: true);

        var alan = await Servis(kanca).SetAccountPasswordAsync(
            new SetMailAccountPasswordRequest { Address = Adres, Password = YeniParola });

        Assert.Contains("DOGRULANMADI", alan.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sonda ayirt edemeyen sunucuda govde yine FIRLATMAZ (parola yazilmis olabilir) ama
    /// "DOGRULANAMADI" der -- sessizce "tamam" DEMEZ.
    /// </summary>
    [Fact]
    public async Task Parola_GOVDESI_sonda_AYIRT_EDEMEYINCE_sessiz_TAMAM_demez()
    {
        var kanca = Kanca(SahteCom(girisDavranisi: "hep-doldurur"), hesapDuruyor: true);

        var alan = await Servis(kanca).SetAccountPasswordAsync(
            new SetMailAccountPasswordRequest { Address = Adres, Password = YeniParola });

        Assert.Contains("DOGRULANAMADI", alan.StatusMessage, StringComparison.Ordinal);
    }
}
