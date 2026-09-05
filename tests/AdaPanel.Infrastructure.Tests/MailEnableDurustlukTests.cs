using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 3 -- <c>MailEnableMailService</c>: COM cagrilarinda <c>[void]</c> + bos <c>catch</c>,
/// ve UYDURULAN donus degerleri.
/// </summary>
/// <remarks>
/// <para>
/// <b>BU SINIF DI'DA CANLI:</b> <c>ServiceCollectionExtensions</c> + <c>MailServiceRouter</c>,
/// Stalwart yoksa ve MailEnable varsa BU sinif kosar. Yani asagidaki yalanlar musteri
/// sunucusunda gecerliydi.
/// </para>
/// <para>
/// <b>OLCULEMEYEN, ACIKCA.</b> Bu makinede MailEnable KURULU DEGIL ve 32-bit COM nesneleri
/// (MEAOPO/MEAOAU/MEAOAM) YOK. Bu yuzden COM cagrilarinin gercek davranisi -- ozellikle durum
/// kodu konvansiyonu ve <c>RemoveLogin</c>'in eksik bir giriste ne yaptigi -- OLCULMEDI.
/// TAM DA BU YUZDEN kapatma, durum kodu konvansiyonunu TAHMIN ETMEK yerine SONUCU GERI OKUMAK
/// uzerine kuruldu: geri okuma zaten yapiliyordu (<c>GetWorkspaceAsync</c>), yalniz sonucuna
/// BAKILMIYORDU.
/// </para>
/// <para>
/// <b>BURADA OLCULEN.</b> (a) COM'a hic dokunmayan metotlarin GERCEK donusleri -- bunlar
/// tamamen olculebilir; (b) geri okuma kapisinin UC DEGERLI karar kurali.
/// </para>
/// </remarks>
public sealed class MailEnableDurustlukTests
{
    private static MailEnableMailService Servis()
        => new(NullLogger<MailEnableMailService>.Instance);

    // =====================================================================================
    // 1) UYDURULAN DEGERLER -- COM'a hic dokunmadan GERCEKTEN kosturulur
    // =====================================================================================

    /// <summary>
    /// <c>GetCertificateStatusAsync</c> sabit <c>Active = true</c> donuyordu: hicbir sey
    /// olculmeden "sertifika etkin". Sertifikasiz bir posta sunucusu panelde YESIL gorunurdu.
    /// </summary>
    [Fact]
    public async Task Sertifika_durumu_OLCULMEDEN_etkin_denmez()
    {
        var durum = await Servis().GetCertificateStatusAsync();

        Assert.False(durum.Active);
        Assert.Contains("unmeasured", durum.Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>IssueCertificateAsync</c> HIC issuance yapmadan <c>Active = true</c> donuyordu:
    /// kullanici "Sertifika al" diyor, panel basarili diyor, sunucuda hicbir sey degismiyordu.
    /// </summary>
    [Fact]
    public async Task Sertifika_ALIMI_yapilmiyorsa_BASARILI_denmez()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            Servis().IssueCertificateAsync(new IssueMailCertificateRequest { Domain = "ornek.com" }));

        Assert.Equal("mail.certificate_issue_not_supported", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    /// <summary>
    /// <c>SetDomainQuotaAsync</c> hicbir kota uygulamiyordu ama cagiran uc denetim kaydina
    /// "Domain mail quota set to N MB" yaziyordu: kotasiz alan adi panelde KOTALI gorunurdu.
    /// </summary>
    [Fact]
    public async Task Alan_adi_kotasi_UYGULANMIYORSA_uygulandi_denmez()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            Servis().SetDomainQuotaAsync(new SetMailDomainQuotaRequest { Domain = "ornek.com", MaxSizeMB = 500 }));

        Assert.Equal("mail.domain_quota_not_supported", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    /// <summary><c>UpdateSpamSettingsAsync</c> istegi YANKILIYORDU.</summary>
    [Fact]
    public async Task Spam_ayari_YAZILMIYORSA_istek_yankilanmaz()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() =>
            Servis().UpdateSpamSettingsAsync(new UpdateMailSpamSettingsRequest { Enabled = true }));

        Assert.Equal("mail.spam_settings_not_supported", hata.MessageKey);
    }

    /// <summary>
    /// <c>GetSpamSettingsAsync</c> ucu de sabit <c>true</c> donuyordu; spam filtresi hic
    /// kurulmamis bir sunucu panelde korumali gorunuyordu.
    /// </summary>
    [Fact]
    public async Task Spam_durumu_OLCULMEDEN_yapilandirildi_denmez()
    {
        var ayar = await Servis().GetSpamSettingsAsync();

        Assert.False(ayar.Configured);
        Assert.False(ayar.Enabled);
    }

    // =====================================================================================
    // 1b) LISTE / KUYRUK / ROLE / DINLEYICI -- "OLCULMEDI" ile "YOK" ayrimi
    //
    // Bu alt kume onceki turda HIC INCELENMEMISTI. Ikisi acik cikti (dinleyiciler, tek kuyruk
    // iletisi); gerisi (dagitim listeleri, kuyruk, role, giden limiti, istemci ayarlari) zaten
    // "yapilandirilmadi"/409 diyordu ve nobetcileri asagida.
    // =====================================================================================

    /// <summary>
    /// <c>GetListenersAsync</c> BOS KOLEKSIYON donuyordu; arayuz bos listeyi
    /// <c>panel.mail.noListeners</c> ("dinleyici yok") diye yaziyordu. Hicbir sey olculmeden
    /// verilen bu cevap, 25/587/143'u GERCEKTEN dinleyen bir sunucuda yoneticiye "posta hic
    /// dinlenmiyor" gosteriyordu.
    /// </summary>
    [Fact]
    public async Task Dinleyiciler_OKUNMUYORSA_dinleyici_YOK_denmez()
    {
        var goruntu = await Servis().GetListenersAsync();

        Assert.False(goruntu.Measured);
        Assert.Empty(goruntu.Listeners);
    }

    /// <summary>
    /// <c>GetQueueMessageAsync</c> <c>null</c> donuyordu ve uc bunu 404 "ileti bulunamadi"
    /// yapiyordu: hic bakilmamis bir kuyruk icin "boyle bir ileti yok" demek. Kardes metot
    /// <c>DeleteQueueMessageAsync</c> ayni durumda zaten "desteklenmiyor" diyordu.
    /// </summary>
    [Fact]
    public async Task Kuyruk_okunmuyorsa_tek_ileti_icin_BULUNAMADI_denmez()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Servis().GetQueueMessageAsync("1"));
        Assert.Equal("mail.queue_not_supported", hata.MessageKey);

        var silme = await Assert.ThrowsAsync<AppException>(() => Servis().DeleteQueueMessageAsync("1"));
        Assert.Equal(hata.MessageKey, silme.MessageKey);
    }

    /// <summary>
    /// NOBETCI: kuyruk/role/giden limiti "bos" ya da "kapali" DEGIL "yapilandirilmadi" der --
    /// bu uc metot bu turda incelendi ve TEMIZ cikti; kural geri kaymasin diye olculuyor.
    /// </summary>
    [Fact]
    public async Task Kuyruk_role_ve_giden_limiti_YAPILANDIRILMADI_der()
    {
        var servis = Servis();

        var kuyruk = await servis.GetQueueAsync(50);
        Assert.True(kuyruk.Installed);
        Assert.False(kuyruk.Configured);

        var role = await servis.GetRelayAsync();
        Assert.True(role.Installed);
        Assert.False(role.Configured);

        var limit = await servis.GetOutboundLimitAsync("ornek.com");
        Assert.False(limit.Configured);

        // Istemci ayarlari OLCULEMEDIGINDE uydurulmaz: null -> autodiscover 404.
        Assert.Null(await servis.GetClientSettingsAsync("ornek.com"));
    }

    /// <summary>NOBETCI: dagitim listeleri "yok" degil "desteklenmiyor" der (409).</summary>
    [Fact]
    public async Task Dagitim_listeleri_DESTEKLENMIYOR_der()
    {
        var olustur = await Assert.ThrowsAsync<AppException>(() =>
            Servis().CreateMailListAsync(new CreateMailListRequest { Address = "liste@ornek.com" }));

        Assert.Equal("mail.lists_unsupported", olustur.MessageKey);
        Assert.Equal(409, olustur.HttpStatusCode);
    }

    // =====================================================================================
    // 2) GERI OKUMA KAPISININ UC DEGERLI KARAR KURALI
    // =====================================================================================

    /// <summary>
    /// OLCULEMEDI, "basarili" DA "basarisiz" DA DEGILDIR -- ve bu ayri bir anahtarla soylenir.
    /// </summary>
    [Fact]
    public void Sonuc_OLCULEMEDIYSE_basarili_da_basarisiz_da_denmez()
    {
        var hata = Assert.Throws<AppException>(() =>
            MailEnableMailService.KapiAcVarlik(
                olculdu: false,
                bulundu: false,
                olmali: true,
                "mail.account_create_unverified",
                "mail.account_create_not_applied",
                "a@ornek.com"));

        Assert.Equal("mail.account_create_unverified", hata.MessageKey);
        Assert.Contains("a@ornek.com", hata.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Olculdu_ve_bekleneni_TUTMUYORSA_UYGULANMADI_denir()
    {
        var eksik = Assert.Throws<AppException>(() =>
            MailEnableMailService.KapiAcVarlik(true, false, true, "mail.account_create_unverified", "mail.account_create_not_applied", "a@ornek.com"));
        Assert.Equal("mail.account_create_not_applied", eksik.MessageKey);

        // Silme yolu: kayit HALA duruyorsa "silindi" denmez. Eskiden iki BOS catch vardi ve
        // panel kosulsuz HTTP 200 donuyordu.
        var duruyor = Assert.Throws<AppException>(() =>
            MailEnableMailService.KapiAcVarlik(true, true, false, "mail.account_delete_unverified", "mail.account_delete_not_applied", "a@ornek.com"));
        Assert.Equal("mail.account_delete_not_applied", duruyor.MessageKey);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: her sey OLCULDU ve yolundayken kapi ACILIR, panel sade
    /// "yapildi" der. Aksi halde her dogru islem de hata gorunurdu.
    /// </summary>
    [Fact]
    public void Her_sey_OLCULDU_ve_YOLUNDAYSA_kapi_ACILIR()
    {
        MailEnableMailService.KapiAcVarlik(true, true, true, "mail.account_create_unverified", "mail.account_create_not_applied", "a@ornek.com");
        MailEnableMailService.KapiAcVarlik(true, false, false, "mail.account_delete_unverified", "mail.account_delete_not_applied", "a@ornek.com");
        MailEnableMailService.KapiAcVarlik(true, true, true, "mail.domain_create_unverified", "mail.domain_create_not_applied", "ornek.com");
        MailEnableMailService.KapiAcVarlik(true, false, false, "mail.alias_delete_unverified", "mail.alias_delete_not_applied", "a@ornek.com");
    }

    // =====================================================================================
    // 3) ADRES KARSILASTIRMASI (tek kaynak)
    // =====================================================================================

    [Fact]
    public void Hesap_aramasi_BUYUK_KUCUK_harf_ayirmaz()
    {
        var workspace = new MailWorkspaceDto
        {
            Accounts = [new MailAccountDto { Address = "Info@Ornek.COM" }]
        };

        Assert.True(MailEnableMailService.HesapVarMi(workspace, "info@ornek.com"));
        Assert.False(MailEnableMailService.HesapVarMi(workspace, "baska@ornek.com"));
    }

    /// <summary>
    /// BOS liste "bulunamadi" demektir -- ama bu YALNIZ olcum yapilabildiginde anlamlidir.
    /// Ikisini ayiran <see cref="MailEnableMailService.KapiAcVarlik"/>'in ilk parametresidir.
    /// </summary>
    [Fact]
    public void Bos_liste_tek_basina_BASARISIZ_anlamina_gelmez()
    {
        var bos = new MailWorkspaceDto();

        Assert.False(MailEnableMailService.HesapVarMi(bos, "info@ornek.com"));

        // OLCULEMEDI ise ayni "bulunamadi" farkli bir cumle uretir.
        var olculemedi = Assert.Throws<AppException>(() =>
            MailEnableMailService.KapiAcVarlik(false, false, true, "mail.account_create_unverified", "mail.account_create_not_applied", "info@ornek.com"));
        var olculdu = Assert.Throws<AppException>(() =>
            MailEnableMailService.KapiAcVarlik(true, false, true, "mail.account_create_unverified", "mail.account_create_not_applied", "info@ornek.com"));

        Assert.NotEqual(olculemedi.MessageKey, olculdu.MessageKey);
    }
}
