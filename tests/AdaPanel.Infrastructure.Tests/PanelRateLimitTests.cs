using System.Reflection;
using System.Threading.RateLimiting;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// HIZ SINIRI — hem "istismar KESILDI" hem "mesru kullanim HALA CALISIYOR".
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN ACIK:</b> kuresel sinirlayici yalnizca <c>Authorization: Bearer adap_…</c> tasiyan
/// istekleri bolumluyor, geri kalan HER SEY icin <c>RateLimitPartition.GetNoLimiter("cookie")</c>
/// donuyordu. Yani 66 denetleyicinin ~64'unde ve KIMLIKSIZ uclarin besinde hicbir tavan yoktu;
/// bunlarin ikisi (abonelikten cikma istegi, site kurucu iletisim formu) POSTA GONDERIYOR.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA EN BUYUK RISK.</b> Panel arayuzu bazi uclari saniyede bir yokluyor; onlara
/// dar bir sinir koymak paneli kirar ve kullanici bunu "panel bozuldu" diye gorur. Bu yuzden her
/// sinir icin IKI test var: birisi istismari kesiyor mu, otekisi OLCULEN mesru kullanim hala
/// geciyor mu. Yalniz birincisini yazmak, paneli kiran bir duzeltmeyi yesil gosterirdi.
/// </para>
/// <para>
/// <b>OLCUM (frontend/src, dosya:satir):</b>
/// <c>MonitoringView.vue:495</c> islemler 2 sn = 30/dk ·
/// <c>ResourceConsumers.vue:465</c> tuketiciler 3 sn, tur basina iki istek = 40/dk ·
/// <c>MonitoringView.vue:492</c> gecmis 60 sn = 1/dk. Tek sekmede en kotu hal <b>71/dk</b>.
/// </para>
/// </remarks>
public sealed class PanelRateLimitTests
{
    private const string CerezAdi = "AdaPanel.Auth";

    // -------------------------------------------------------------------------------------
    // 1) KURESEL TAVAN — cerez yolunda artik "sinirsiz" YOK
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Cerezli_istek_artik_SINIRSIZ_degil()
    {
        // Bu, duzeltmenin cekirdegi. Eski davranista bu dongu sonsuza kadar gecerdi.
        using var sinirlayici = KureselSinirlayici();
        var istek = Istek("/api/v1/sites", cerez: "oturum-a");

        var gecen = KacTaneGecti(sinirlayici, istek, PanelRateLimits.SessionPermitPerMinute + 50);

        Assert.Equal(PanelRateLimits.SessionPermitPerMinute, gecen);
    }

    [Fact]
    public void MESRU_arayuz_kullanimi_genel_kovada_RAHAT_siger()
    {
        // FAZLA KAPATMA NOBETCISI. Olculen en yogun kullanici eylemi tek sayfa acilisidir
        // (SPA + statik varliklar + ekranin ilk yuklemeleri ~40 istek). Ust uste on sayfa
        // acan bir yonetici bile tavani gormemeli.
        using var sinirlayici = KureselSinirlayici();
        var istek = Istek("/api/v1/sites", cerez: "oturum-a");

        Assert.Equal(400, KacTaneGecti(sinirlayici, istek, 400));
    }

    [Fact]
    public void OLCULEN_izleme_yoklamasi_bir_saat_boyunca_ENGELLENMEZ()
    {
        // FAZLA KAPATMA NOBETCISI — bu testin sayilari OLCUMDEN geliyor, tahminden degil:
        // MonitoringView.vue:495 (2 sn) + ResourceConsumers.vue:465 (3 sn, tur basina 2 istek)
        // + MonitoringView.vue:492 (60 sn) = 71 istek/dk. Pencere bir dakika oldugu icin
        // bir dakikalik hacmi gecirmek, saatlerce acik duran ekranin gecmesiyle aynidir.
        using var sinirlayici = KureselSinirlayici();
        var olculenDakikalikHacim = 30 + 40 + 1;

        var islemler = Istek("/api/v1/monitoring/processes", cerez: "oturum-a");
        var tuketiciler = Istek("/api/v1/monitoring/consumers", cerez: "oturum-a");
        var gecmis = Istek("/api/v1/system/history", cerez: "oturum-a");

        Assert.Equal(30, KacTaneGecti(sinirlayici, islemler, 30));
        Assert.Equal(40, KacTaneGecti(sinirlayici, tuketiciler, 40));
        Assert.Equal(1, KacTaneGecti(sinirlayici, gecmis, 1));

        // Ayni bolum, olculen hacmin BIR KATINDAN fazlasini daha kaldirmali: iki sekme acik.
        Assert.True(PanelRateLimits.PolledPermitPerMinute >= olculenDakikalikHacim * 4);
    }

    [Fact]
    public void Yoklama_kullanicinin_TIKLAMA_butcesini_YEMEZ()
    {
        // Bolunmenin butun sebebi bu. Tek kovada olsaydi, Izleme ekranini acik birakan bir
        // yonetici hicbir kotuye kullanim olmadan kendi tavanini tuketirdi.
        using var sinirlayici = KureselSinirlayici();
        var yoklama = Istek("/api/v1/monitoring/processes", cerez: "oturum-a");
        var eylem = Istek("/api/v1/sites", cerez: "oturum-a");

        // Yoklama kovasini SONUNA kadar tuket.
        KacTaneGecti(sinirlayici, yoklama, PanelRateLimits.PolledPermitPerMinute);
        Assert.False(sinirlayici.AttemptAcquire(yoklama).IsAcquired);

        // Genel kova ETKILENMEMIS olmali.
        Assert.True(sinirlayici.AttemptAcquire(eylem).IsAcquired);
    }

    [Fact]
    public void Iki_ayri_oturum_birbirini_KILITLEMEZ()
    {
        // NAT arkasindaki ofis: on yonetici tek IP'den girer. Bolumleme IP'ye gore olsaydi
        // biri otekileri kilitlerdi.
        using var sinirlayici = KureselSinirlayici();
        var a = Istek("/api/v1/sites", cerez: "oturum-a");
        var b = Istek("/api/v1/sites", cerez: "oturum-b");

        KacTaneGecti(sinirlayici, a, PanelRateLimits.SessionPermitPerMinute);
        Assert.False(sinirlayici.AttemptAcquire(a).IsAcquired);

        Assert.True(sinirlayici.AttemptAcquire(b).IsAcquired);
    }

    [Fact]
    public void Anahtarli_isteklerin_kendi_tavani_KORUNDU()
    {
        using var sinirlayici = KureselSinirlayici();
        var istek = Istek("/api/v1/sites");
        istek.Request.Headers.Authorization = "Bearer adap_test_anahtari";

        Assert.Equal(
            PanelRateLimits.TokenPermitPerMinute,
            KacTaneGecti(sinirlayici, istek, PanelRateLimits.TokenPermitPerMinute + 10));
    }

    // -------------------------------------------------------------------------------------
    // 2) KIMLIKSIZ POSTA GONDEREN UCLAR — panelin en pahali anonim eylemi
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Kimliksiz_POSTA_ucu_toplu_istismarda_KESILIR()
    {
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.PublicMailSend);
        var istek = Istek("/api/v1/public/mail-lists/unsubscribe", ip: "203.0.113.9");

        Assert.Equal(
            PanelRateLimits.PublicMailSendPermitPerWindow,
            KacTaneGecti(sinirlayici, istek, 200));
    }

    [Fact]
    public void GERCEK_bir_abone_abonelikten_CIKABILIR()
    {
        // FAZLA KAPATMA NOBETCISI. Abonelikten cikmak MESRU bir kullanici eylemidir; sinir
        // toplu istismari durduracak kadar dar ama gercek kullaniciyi engellemeyecek kadar
        // genis olmali. Olculen mesru akis: adresini yanlis yazip birkac kez dener.
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.PublicMailSend);
        var istek = Istek("/api/v1/public/mail-lists/unsubscribe", ip: "203.0.113.9");

        Assert.Equal(4, KacTaneGecti(sinirlayici, istek, 4));
    }

    [Fact]
    public void Butcesini_tuketen_kullanici_ONAY_adimindan_DISLANMAZ()
    {
        // FAZLA KAPATMA NOBETCISI — ve bu ayrimin tek sebebi. Onay adimi posta GONDERMEZ ve
        // 256 bitlik belirtec ister; ayni dar kovaya konsaydi, istegini yenileyerek butcesini
        // tuketen kullanici kendi onay baglantisina tiklayamazdi.
        var gonderim = PolitikaSinirlayici(PanelRateLimits.PublicMailSend);
        var okuma = PolitikaSinirlayici(PanelRateLimits.PublicRead);
        using (gonderim)
        using (okuma)
        {
            var gonder = Istek("/api/v1/public/mail-lists/unsubscribe", ip: "203.0.113.9");
            var onayla = Istek("/api/v1/public/mail-lists/unsubscribe/confirm", ip: "203.0.113.9");

            KacTaneGecti(gonderim, gonder, PanelRateLimits.PublicMailSendPermitPerWindow);
            Assert.False(gonderim.AttemptAcquire(gonder).IsAcquired);

            Assert.True(okuma.AttemptAcquire(onayla).IsAcquired);
        }
    }

    [Fact]
    public void Posta_sinirinin_site_kurucudaki_IC_siniri_EZMEDIGI_olculdu()
    {
        // Site kurucu formunun servis katmaninda zaten IP basina 5/15dk sinir var
        // (SiteBuilderContactRateLimiter.MaxPerIp). Buradaki dis tavan ondan GEVSEK olmali;
        // daha dar olsaydi reddi dis tavan verirdi ve kullanici, ic katmanin yazdigi anlamli
        // mesaj yerine ham bir 429 gorurdu.
        Assert.True(
            PanelRateLimits.PublicMailSendPermitPerWindow
                > AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactRateLimiter.MaxPerIp,
            "Dis tavan ic sinirdan dar; ic katmanin mesaji artik kullaniciya ulasmaz.");
    }

    /// <summary>
    /// Sinir degerleri iki taraftan da CIVILI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu test, otekilerin OLCEMEDIGI seyi olcer.</b> Yukaridaki davranis testleri sabitin
    /// KENDISINI okuyor; yani "sinirlayici sabite uyuyor mu" sorusunu kapatiyorlar, "sabit makul
    /// mu" sorusunu DEGIL. Bir katkici <c>PublicMailSendPermitPerWindow</c>'u 10'dan 10.000'e
    /// cikarsa o testlerin HEPSI yesil kalirdi ve koruma sessizce yok olurdu — bu depoda
    /// defalarca olculmus "dogustan olu test" sinifi.
    /// </para>
    /// <para>
    /// Ust sinirlar kaba bilerek: amac degeri dondurmak degil, BUYUKLUK MERTEBESINDEN
    /// kaymasini gorunur kilmak.
    /// </para>
    /// </remarks>
    [Fact]
    public void Sinir_degerleri_BUYUKLUK_MERTEBESI_olarak_da_civili()
    {
        // Kimliksiz posta: gercek abone 1-3 istek yapar. Ust sinir, bir botun 15 dakikada
        // gonderebilecegi posta sayisidir; "onlarca" olursa sinirin anlami kalmaz.
        Assert.InRange(PanelRateLimits.PublicMailSendPermitPerWindow, 6, 20);
        Assert.InRange(PanelRateLimits.PublicMailSendWindowMinutes, 5, 60);

        // Kimliksiz okuma: vekil arkasindaki autodiscover yuzunden comert, ama sinirsiz degil.
        Assert.InRange(PanelRateLimits.PublicReadPermitPerMinute, 120, 1000);

        // Webhook: saglayici push basina bir istek gonderir.
        Assert.InRange(PanelRateLimits.GitWebhookPermitPerMinute, 10, 120);

        // Oturum kovalari: olculen 71/dk'nin altina INMEMELI (paneli kirar), ama tavan da
        // olmali (sizmis bir oturumla veri kazima olculebilir bicimde yavaslasin).
        Assert.InRange(PanelRateLimits.SessionPermitPerMinute, 200, 2000);
        Assert.InRange(PanelRateLimits.PolledPermitPerMinute, 300, 3000);

        // Degismemesi gereken mevcut degerler (gerileme nobetcisi).
        Assert.Equal(5, PanelRateLimits.LoginPermitPerMinute);
        Assert.Equal(5, PanelRateLimits.PasswordChangePermitPerWindow);
        Assert.Equal(3, PanelRateLimits.SetupPermitPerWindow);
        Assert.Equal(120, PanelRateLimits.McpPermitPerMinute);
        Assert.Equal(300, PanelRateLimits.TokenPermitPerMinute);
    }

    // -------------------------------------------------------------------------------------
    // 3) KIMLIKSIZ OKUMA — vekil arkasindaki autodiscover'i KIRMAMALI
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Autodiscover_vekil_arkasinda_TEK_kovada_bile_bogulmaz()
    {
        // FAZLA KAPATMA NOBETCISI — ve olculmus bir vekil etkisi: autodiscover istekleri
        // musterinin IIS sitesindeki ARR kuraliyla panele ILETILIYOR
        // (MailAutodiscoverRewriteRules.cs:27), yani hepsinin adresi 127.0.0.1'dir ve
        // sunucudaki BUTUN posta istemcileri TEK bolumu paylasir.
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.PublicRead);
        var vekilden = Istek("/autodiscover/autodiscover.xml", ip: "127.0.0.1");

        // Ayni anda 50 istemcinin hesap kurmasi (istemci basina ~4 yoklama) gecmeli.
        Assert.Equal(200, KacTaneGecti(sinirlayici, vekilden, 200));
    }

    [Fact]
    public void Kimliksiz_okuma_yine_de_TAVANLI()
    {
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.PublicRead);
        var istek = Istek("/api/v1/branding", ip: "198.51.100.7");

        Assert.Equal(
            PanelRateLimits.PublicReadPermitPerMinute,
            KacTaneGecti(sinirlayici, istek, PanelRateLimits.PublicReadPermitPerMinute + 25));
    }

    // -------------------------------------------------------------------------------------
    // 4) GIT WEBHOOK
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Webhook_saglayicinin_MESRU_hizini_engellemez()
    {
        // FAZLA KAPATMA NOBETCISI: GitHub/GitLab push basina BIR istek gonderir. Cok hareketli
        // bir depoda bile dakikada birkac push olur; 10 kat pay birakiliyor.
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.GitWebhook);
        var istek = Istek("/api/v1/git/ornek/webhook", ip: "140.82.115.1");

        Assert.Equal(10, KacTaneGecti(sinirlayici, istek, 10));
    }

    [Fact]
    public void Webhook_sel_altinda_KESILIR()
    {
        using var sinirlayici = PolitikaSinirlayici(PanelRateLimits.GitWebhook);
        var istek = Istek("/api/v1/git/ornek/webhook", ip: "140.82.115.1");

        Assert.Equal(
            PanelRateLimits.GitWebhookPermitPerMinute,
            KacTaneGecti(sinirlayici, istek, 500));
    }

    // -------------------------------------------------------------------------------------
    // 5) BOLUMLEME ANAHTARI — sir tasimaz, kimlikleri karistirmaz
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Bolumleme_anahtari_CEREZIN_kendisini_TASIMAZ()
    {
        // Bolumleme anahtarlari tanilama/gunluk yuzeylerinde gorunebilir; oraya gecerli bir
        // oturum cerezi tasimak, hiz sinirlamak icin oturum calinabilir hale getirmek olurdu.
        var context = Istek("/api/v1/sites", cerez: "cok-gizli-oturum-degeri");

        var bolum = PanelRateLimits.SessionPartitionKey(context, CerezAdi);

        Assert.StartsWith("session:", bolum, StringComparison.Ordinal);
        Assert.DoesNotContain("gizli", bolum, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cerez_yoksa_IP_kullanilir()
    {
        var context = Istek("/api/v1/public/mail-lists/unsubscribe", ip: "203.0.113.4");

        Assert.Equal("ip:203.0.113.4", PanelRateLimits.SessionPartitionKey(context, CerezAdi));
    }

    [Theory]
    [InlineData("/api/v1/monitoring/processes", true)]   // MonitoringView.vue:495 — 2 sn
    [InlineData("/api/v1/monitoring/consumers", true)]   // ResourceConsumers.vue:465 — 3 sn
    [InlineData("/api/v1/system/history", true)]         // MonitoringView.vue:492 — 60 sn
    [InlineData("/api/v1/system/overview", true)]        // SetupWizardView.vue:1698 — 15 sn
    [InlineData("/api/v1/sites", false)]
    [InlineData("/api/v1/monitoringXYZ", false)]         // onek "segment" olarak eslesmeli
    public void Yoklama_yolu_ayrimi_dogru(string yol, bool beklenen)
    {
        Assert.Equal(beklenen, PanelRateLimits.IsPolledPath(yol));
    }

    // -------------------------------------------------------------------------------------
    // 6) KAPSAM NOBETCISI — kimliksiz her uc bir politikaya bagli mi (YANSIMA ile, metin degil)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// KIMLIKSIZ HER UC BIR HIZ SINIRI POLITIKASI TASIMALI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Olcum METINLE degil YANSIMAYLA yapiliyor: kaynak taramasi, ozniteligin gercekten o
    /// eyleme bagli olup olmadigini bilemez (sinif duzeyi/eylem duzeyi, miras, yorum satiri).
    /// Yansima, ASP.NET Core'un calisma zamaninda gordugu seyin AYNISINI gorur.
    /// </para>
    /// <para>
    /// <b>"KIMLIKSIZ" TANIMI BU TESTIN ILK HALINDE EKSIKTI ve eksiklik GERCEK bir bosluk
    /// gizliyordu.</b> Ilk hal yalniz <c>[AllowAnonymous]</c> arayordu; oysa panelde KURESEL bir
    /// yetkilendirme ilkesi yok (<c>AddAuthorization()</c> cagrisinda <c>FallbackPolicy</c>
    /// verilmemis), yani <c>[Authorize]</c> HIC TASIMAYAN bir denetleyici de tamamen aciktir.
    /// Olculdu: <c>HealthController</c> ve <c>SetupController</c> tam olarak bu durumdaydi ve ilk
    /// nobetci onlari YESIL geciriyordu — kurulum sihirbazinin 1,5 saniyede bir yokladigi
    /// <c>setup/progress</c> ucu dahil.
    /// </para>
    /// <para>
    /// Muafiyet listesi YOK ve bu bilincli: bugun butun kimliksiz uclar kapali. Bir uc gerekce
    /// ile disarida kalacaksa, listeyi BURAYA eklemek ve gerekcesini yazmak gerekir — sessizce
    /// eksik kalmasi mumkun degil.
    /// </para>
    /// </remarks>
    [Fact]
    public void Kimliksiz_UCLARIN_HEPSI_bir_politikaya_bagli()
    {
        var acikta = new List<string>();
        var sayilan = 0;

        foreach (var (tip, eylem) in Eylemler())
        {
            if (!Kimliksiz(tip, eylem))
            {
                continue;
            }

            sayilan++;
            var sinirli = eylem.GetCustomAttribute<EnableRateLimitingAttribute>() is not null
                          || tip.GetCustomAttribute<EnableRateLimitingAttribute>() is not null;
            if (!sinirli)
            {
                acikta.Add($"{tip.Name}.{eylem.Name}");
            }
        }

        // OLCEMEDIK ile SORUN YOK ayrimi: yansima bozulup hicbir eylem bulunmazsa test sessizce
        // gecerdi ve bir daha hicbir seyi korumazdi. Esik OLCULEN sayidan (14) turedi.
        Assert.True(sayilan >= 14, $"Kimliksiz uc taramasi bozulmus olabilir: yalniz {sayilan} eylem bulundu.");
        Assert.True(
            acikta.Count == 0,
            "Hiz siniri OLMAYAN kimliksiz uclar:\n  " + string.Join("\n  ", acikta));
    }

    [Fact]
    public void Denetleyicilerin_kullandigi_her_politika_ADI_KAYITLI()
    {
        // "Tanimli ama kayitsiz politika" derlenir ama CALISMA ZAMANINDA istisna atar; yani hata
        // ancak canlida gorunurdu. Burada kagit uzerinde degil, kayit sozlugunde araniyor.
        var eksik = new List<string>();

        foreach (var (tip, eylem) in Eylemler())
        {
            foreach (var oznitelik in eylem.GetCustomAttributes<EnableRateLimitingAttribute>()
                         .Concat(tip.GetCustomAttributes<EnableRateLimitingAttribute>()))
            {
                if (oznitelik.PolicyName is null || !PanelRateLimits.Policies.ContainsKey(oznitelik.PolicyName))
                {
                    eksik.Add($"{tip.Name}.{eylem.Name} -> '{oznitelik.PolicyName}'");
                }
            }
        }

        Assert.True(eksik.Count == 0, "Kayitli olmayan politika adlari:\n  " + string.Join("\n  ", eksik));
    }

    /// <summary>
    /// Taramanin KENDISI dogru mu: <c>[Authorize]</c> TASIMAYAN bir uc de kimliksiz sayiliyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu test bir MUTASYONUN sag kalmasindan dogdu.</b> Kapsam nobetcisindeki
    /// <c>&amp;&amp; kapiVar</c> kosulu silindiginde — yani tarama yeniden yalniz
    /// <c>[AllowAnonymous]</c> arar hale getirildiginde — hicbir test kirmizi yanmadi: acikca
    /// isaretli 16 kimliksiz uc hala sayiliyor, "en az N eylem bulundu" esigi hala saglaniyor ve
    /// bulunanlarin hepsi sinirli. Yani nobetcinin KAPSAMI sessizce daralabiliyordu ve daraldigi
    /// anda <c>HealthController</c> ile <c>SetupController</c> — kimliksiz ve 1,5 saniyede bir
    /// yoklanan uclar — gorunmez oluyordu.
    /// </para>
    /// <para>
    /// Cozum sayiyi buyutmek DEGIL, kapsami DOGRUDAN olcmek: asagidaki iki uc kaynakta
    /// <c>[AllowAnonymous]</c> TASIMIYOR (denetleyicilerinde hic <c>[Authorize]</c> olmadigi icin
    /// aciklar), dolayisiyla yalniz genisletilmis tanim onlari yakalar.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("HealthController", "GetAsync")]
    [InlineData("SetupController", "GetProgressAsync")]
    [InlineData("SetupController", "GetStatusAsync")]
    public void Tarama_Authorize_TASIMAYAN_ucu_da_KIMLIKSIZ_sayar(string denetleyici, string eylem)
    {
        var (tip, yontem) = Eylemler().Single(x => x.Tip.Name == denetleyici && x.Eylem.Name == eylem);

        // On kosul: bu uc GERCEKTEN [AllowAnonymous] tasimiyor olmali, yoksa test yanlis seyi olcer.
        Assert.Null(yontem.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(tip.GetCustomAttribute<AllowAnonymousAttribute>());

        Assert.True(Kimliksiz(tip, yontem), $"{denetleyici}.{eylem} kimliksiz sayilmiyor.");
    }

    // -------------------------------------------------------------------------------------
    // duzenek
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Bu uc kimlik dogrulamasi ISTIYOR mu?
    /// </summary>
    /// <remarks>
    /// IKI durum da kimliksizdir: acikca <c>[AllowAnonymous]</c> olmasi VE hic <c>[Authorize]</c>
    /// olmamasi. Ikincisi bu panelde gecerli cunku kuresel bir <c>FallbackPolicy</c> YOK
    /// (<c>Program.cs</c>: <c>AddAuthorization()</c> parametresiz cagriliyor).
    /// </remarks>
    private static bool Kimliksiz(Type tip, MethodInfo eylem)
    {
        var acikcaAnonim = eylem.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                           || tip.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
        var kapiVar = eylem.GetCustomAttributes<AuthorizeAttribute>().Any()
                      || tip.GetCustomAttributes<AuthorizeAttribute>().Any();

        return acikcaAnonim || !kapiVar;
    }

    private static IEnumerable<(Type Tip, MethodInfo Eylem)> Eylemler()
    {
        var derleme = typeof(PanelRateLimits).Assembly;
        foreach (var tip in derleme.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            foreach (var eylem in tip.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (eylem.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
                {
                    yield return (tip, eylem);
                }
            }
        }
    }

    /// <summary>
    /// Ara katmanin kullandigi KODUN AYNISI: <c>Configure</c> icindeki kuresel bolumleyici
    /// burada da <c>PartitionedRateLimiter.Create</c> ile sariliyor.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> KureselSinirlayici() =>
        PartitionedRateLimiter.Create<HttpContext, string>(
            context => PanelRateLimits.GlobalPartition(context, CerezAdi));

    private static PartitionedRateLimiter<HttpContext> PolitikaSinirlayici(string politika) =>
        PartitionedRateLimiter.Create(PanelRateLimits.Policies[politika]);

    private static int KacTaneGecti(PartitionedRateLimiter<HttpContext> sinirlayici, HttpContext istek, int deneme)
    {
        var gecen = 0;
        for (var i = 0; i < deneme; i++)
        {
            using var kiralama = sinirlayici.AttemptAcquire(istek);
            if (kiralama.IsAcquired)
            {
                gecen++;
            }
        }

        return gecen;
    }

    private static DefaultHttpContext Istek(string yol, string? cerez = null, string ip = "192.0.2.10")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = yol;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        if (cerez is not null)
        {
            context.Request.Headers.Cookie = $"{CerezAdi}={cerez}";
        }

        return context;
    }
}
