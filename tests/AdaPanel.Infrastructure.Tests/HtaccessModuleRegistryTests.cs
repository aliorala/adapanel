using System.Reflection;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// MODÜLÜN IIS KAYDI: PANEL ARTIK ÖLÇÜYOR, TAHMİN ETMİYOR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu dosyanın var olma sebebi ölçülmüş bir yalandır.</b> Kurulu bir sunucuda
/// (<c>test.local</c>) şu tablo aynı anda doğruydu: panel <c>support: supported</c>,
/// <c>engineLoadedInIis: true</c>, <c>hasRootFile: true</c> diyordu; havuz
/// <c>Integrated</c>/<c>v4.0</c> idi (yani Classic kip ihtimali ELENDİ); panelin kendi benzetimi
/// <c>302</c> üretiyordu (motor DOĞRU çalışıyor); ama gerçek HTTP isteği <c>404</c> dönüyordu.
/// Motor doğru, TESLİM HATTI kopuktu — modül isteğin içine hiç girmiyordu.
/// </para>
/// <para>
/// Sebep <c>HtaccessPanelService</c>'te tek satırdı: <c>EngineLoadedInIis = destek == "supported"</c>.
/// <c>destek</c> yalnız uygulama havuzunun CLR sürümüne bakıyordu, yani panel modülün KAYITLI olup
/// olmadığını HİÇ ÖLÇMEDEN "yüklü" diyordu. Buradaki testler o ölçümü ve ondan doğan engelleri
/// sabitler; hepsinin ortak iddiası tek cümledir: <b>panel ölçmediği şeyi söylemez.</b>
/// </para>
/// </remarks>
public sealed class HtaccessModuleRegistryTests
{
    // ------------------------------------------------------------------ ÖLÇÜM → RAPOR

    /// <summary>
    /// KAYIT OKUNAMADIYSA "yüklü" DENMEZ ve sebep AYRI bir engel olarak bildirilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Okunamadı" ile "kayıt yok" AYNI ŞEY DEĞİL</b> ve ikisini tek anahtara toplamak
    /// yöneticiye yanlış işi yaptırırdı: biri "modülü kur", diğeri "panelin IIS'i okumasını
    /// düzelt". Bu yüzden ayrı anahtar.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): <c>MotorEngelleri</c>'ndeki <c>!kayit.Readable</c> dalı silinip
    /// akış <c>!Present</c> dalına bırakıldığında bu test kızarıyor (engel
    /// <c>module_not_registered</c> oluyor). <c>Confirmed</c>'daki <c>Readable</c> koşulu
    /// düşürüldüğünde de kızarıyor (<c>Present</c> zaten false olduğu için değil — o hâli ayrıca
    /// aşağıdaki testler ölçüyor).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KAYIT_OKUNAMAZSA_MOTOR_YUKLU_SAYILMAZ_ve_ENGEL_BILDIRILIR()
    {
        var rapor = await Rapor(new HtaccessModuleRegistrationState { ReadError = "IIS yok" });

        Assert.False(rapor.EngineLoadedInIis);
        Assert.Contains("htaccess.blocker.registration_unreadable", rapor.EngineBlockers);

        // "Okunamadi" kayit YOK demek DEGIL: yonetici yanlis ise yonlendirilmemeli.
        Assert.DoesNotContain("htaccess.blocker.module_not_registered", rapor.EngineBlockers);
    }

    /// <summary>
    /// KAYIT YOKSA — bugünkü canlı arızanın tam karşılığı — panel "yüklü" DEMEZ.
    /// </summary>
    /// <remarks>
    /// Havuz bilerek <c>v4.0</c>: eski kod bu durumda <c>true</c> diyordu, çünkü baktığı tek şey
    /// havuzun sürümüydü. Bu test o satırın geri gelmesini imkânsız kılar.
    /// <para>
    /// MUTASYON (denendi): <c>EngineLoadedInIis = destek == "supported"</c> geri yazıldığında
    /// bu test kızarıyor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KAYIT_YOKSA_MOTOR_YUKLU_SAYILMAZ_ve_ENGEL_BILDIRILIR()
    {
        var rapor = await Rapor(new HtaccessModuleRegistrationState { Readable = true });

        // Havuz UYGUN ve rapor bunu soyluyor: yani false olmasinin sebebi havuz DEGIL.
        Assert.Equal("supported", rapor.Support);
        Assert.False(rapor.EngineLoadedInIis);
        Assert.Contains("htaccess.blocker.module_not_registered", rapor.EngineBlockers);
    }

    /// <summary>
    /// Kayıt yokken derleme de GAC'ta yoksa BU AYRICA söylenir — onarım ucu o hâlde REDDEDER.
    /// </summary>
    /// <remarks>
    /// Ayrı anahtar, çünkü yapılacak iş farklı: kayıt eksikse panelden onarılabilir, derleme de
    /// eksikse önce kurulum betiği çalışmak zorundadır (GAC kurulumu .NET 8'den YAPILAMAZ —
    /// gerekçesi <c>IisHtaccessModuleRegistry</c> sınıf yorumunda ölçülmüş).
    /// </remarks>
    [Fact]
    public async Task KAYIT_YOKSA_ve_DERLEME_GACTA_YOKSA_AYRI_ENGEL_BILDIRILIR()
    {
        var rapor = await Rapor(new HtaccessModuleRegistrationState
        {
            Readable = true,
            Assembly = new HtaccessModuleAssemblyState { Presence = HtaccessModuleAssemblyPresence.Missing }
        });

        Assert.Contains("htaccess.blocker.module_assembly_missing", rapor.EngineBlockers);
    }

    /// <summary>
    /// GAC OKUNAMADIYSA "derleme yok" DENMEZ.
    /// </summary>
    /// <remarks>
    /// Bu testin olmaması, üç değerli ölçümü sessizce iki değerliye indirirdi: <c>Unknown</c> da
    /// <c>Missing</c> gibi ele alınsaydı, GAC kökünü okuyamayan bir makinede panel "modül kurulu
    /// değil" diye KESİN bir şey söylerdi — düzeltmeye çalıştığımız hatanın aynısı.
    /// </remarks>
    [Fact]
    public async Task GAC_OKUNAMADIYSA_DERLEME_YOK_DENMEZ()
    {
        var rapor = await Rapor(new HtaccessModuleRegistrationState
        {
            Readable = true,
            Assembly = new HtaccessModuleAssemblyState { Presence = HtaccessModuleAssemblyPresence.Unknown }
        });

        Assert.Contains("htaccess.blocker.module_not_registered", rapor.EngineBlockers);
        Assert.DoesNotContain("htaccess.blocker.module_assembly_missing", rapor.EngineBlockers);
    }

    /// <summary>
    /// <c>managedHandler</c> ÖN KOŞULU AYRI BİR ENGELDİR — en sinsi hâl.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kayıt yerinde, modül yükleniyor, panel "kurulu" diyor — ama IIS modülü yalnız yönetilen
    /// işleyicilerin hizmet ettiği isteklerde çalıştırıyor. <c>.htaccess</c>'in bütün amacı PHP ve
    /// STATİK dosyalar olduğu için özellik hiçbir şey yapmaz ve HİÇBİR hata da vermez. Belirtisi
    /// bugün ölçülenle birebir aynıdır: panel "her şey yolunda" der, istek 404 döner.
    /// </para>
    /// <para>
    /// Üç yazım birden ölçülüyor: tek başına, doğru ön koşulun önüne ve arkasına eklenmiş hâlleri.
    /// Tek bir dize ölçülseydi "başlıyor mu" gibi kırılgan bir denetim testi geçerdi.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): <c>PreConditionExcludesUnmanagedRequests</c> içindeki karşılaştırma
    /// <c>Equals</c>'a çevrildiğinde iki birleşik hâl kızarıyor; koşul tamamen kaldırıldığında
    /// üçü de kızarıyor.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("managedHandler")]
    [InlineData("managedHandler,runtimeVersionv4.0")]
    [InlineData("runtimeVersionv4.0,managedHandler")]
    public async Task managedHandler_ON_KOSULU_AYRI_ENGEL_URETIR(string onKosul)
    {
        var rapor = await Rapor(Kayitli(onKosul: onKosul));

        Assert.False(rapor.EngineLoadedInIis);
        Assert.Contains("htaccess.blocker.precondition_managed_handler", rapor.EngineBlockers);

        // KAYIT VAR: yonetici "modulu kur" diye yanlis yola sokulmamali.
        Assert.DoesNotContain("htaccess.blocker.module_not_registered", rapor.EngineBlockers);
    }

    /// <summary>
    /// ÖN KOŞULUN YOKLUĞU ENGEL DEĞİLDİR.
    /// </summary>
    /// <remarks>
    /// Ön koşul yazılmamış bir girdide modül HER istekte koşar — yani <c>.htaccess</c> çalışır.
    /// Bunu engel saymak, çalışan bir kurulumu bozuk göstermek olurdu. (Kurulumun kendi tercihi
    /// <c>runtimeVersionv4.0</c>'dır ve gerekçesi ayrıdır: yönetilen kodu olmayan havuzlarda
    /// modülün hiç denenmemesi.)
    /// </remarks>
    [Fact]
    public async Task ON_KOSUL_YOKSA_ENGEL_SAYILMAZ()
    {
        var rapor = await Rapor(Kayitli(onKosul: string.Empty));

        Assert.True(rapor.EngineLoadedInIis);
        Assert.Empty(rapor.EngineBlockers);
    }

    /// <summary>
    /// Girdi BAŞKA bir tipi gösteriyorsa kayıt "var" sayılmaz.
    /// </summary>
    /// <remarks>
    /// Yalnız ada bakmak yetmez: aynı adla eski/yanlış bir tipe işaret eden girdi (elle düzenleme,
    /// yeniden adlandırılmış bir sınıf) IIS'in bambaşka bir sınıfı yüklemesi demektir. Ad eşleşip
    /// tip eşleşmediğinde panel "yüklü" derse, kurulumun en zor teşhis edilen hâli sessizce
    /// "sağlıklı" görünürdü.
    /// <para>
    /// MUTASYON (denendi): <c>TypeMatches</c> her koşulda <c>true</c> yapıldığında bu test kızarıyor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TIP_BASKA_BIR_SINIFI_GOSTERIYORSA_ENGEL_BILDIRILIR()
    {
        var rapor = await Rapor(new HtaccessModuleRegistrationState
        {
            Readable = true,
            Present = true,
            TypeName = "Baska.Firma.HtaccessHttpModule, Baska.Firma, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef",
            PreCondition = HtaccessModuleRegistration.PreCondition
        });

        Assert.False(rapor.EngineLoadedInIis);
        Assert.Contains("htaccess.blocker.module_type_mismatch", rapor.EngineBlockers);
    }

    /// <summary>
    /// HER ŞEY YERİNDEYSE panel "yüklü" der ve HİÇBİR engel göstermez.
    /// </summary>
    /// <remarks>
    /// Ters yön ölçülmezse yukarıdaki testlerin hepsini <c>EngineLoadedInIis = false</c> sabiti
    /// geçerdi — yani eski yalanın simetriği. Bu test, düzeltmenin çalışan kurulumu bozuk
    /// göstermediğini sabitler.
    /// </remarks>
    [Fact]
    public async Task KAYIT_TAM_ve_HAVUZ_UYGUNSA_MOTOR_YUKLU_SAYILIR()
    {
        var rapor = await Rapor(Kayitli());

        Assert.True(rapor.EngineLoadedInIis);
        Assert.Empty(rapor.EngineBlockers);

        // Ham olcum de raporda: yonetici IIS'te YAZAN seyi gorebilmeli.
        Assert.True(rapor.ModuleRegistration.Present);
        Assert.Equal(HtaccessModuleRegistration.PreCondition, rapor.ModuleRegistration.PreCondition);
    }

    /// <summary>
    /// KAYIT TAM olsa bile HAVUZ modülü yükleyemiyorsa "yüklü" DENMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yeni tanımın diğer yarısı: üç ölçümün BİRLEŞİMİ. Node/Python siteleri için havuz "yönetilen
    /// kod yok"a çevriliyor (<c>AppRuntimeService</c>) ve o havuzlarda modül HİÇ yüklenmez —
    /// kaydın kusursuz olması bunu değiştirmez.
    /// </para>
    /// <para>
    /// <b>Havuz kaynaklı neden BİLEREK engel listesinde DEĞİL:</b> <c>Support</c> alanı onu kendi
    /// şeridinde zaten söylüyor; ikinci kez yazmak kullanıcıya iki ayrı sorun varmış gibi
    /// okuturdu. Bu test o kararı da sabitliyor — birileri "eksik" sanıp eklerse burada görünür.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("")]       // no_managed_code
    [InlineData("v2.0")]   // runtime_version
    public async Task HAVUZ_UYGUN_DEGILSE_KAYIT_TAM_OLSA_BILE_YUKLU_SAYILMAZ(string havuzSurumu)
    {
        var rapor = await Rapor(Kayitli(), havuzSurumu);

        Assert.False(rapor.EngineLoadedInIis);
        Assert.Empty(rapor.EngineBlockers);
    }

    // ------------------------------------------------------------------ GAC ARAYICISI

    /// <summary>
    /// GAC kökü YOKSA sonuç <c>Missing</c> DEĞİL <c>Unknown</c>'dır.
    /// </summary>
    /// <remarks>
    /// MUTASYON (denendi): <c>Locate</c>'in kök-yok dalı <c>Missing</c> döndürecek şekilde
    /// değiştirildiğinde bu test kızarıyor — yani üç değerli sonuç gerçekten korunuyor.
    /// </remarks>
    [Fact]
    public void GAC_KOKU_YOKSA_SONUC_BILINMIYOR()
    {
        var olmayan = Path.Combine(Path.GetTempPath(), "adapanel-olmayan-gac-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HtaccessModuleAssemblyPresence.Unknown, HtaccessModuleAssemblyLocator.Locate(olmayan).Presence);
    }

    /// <summary>
    /// GAC kökü VAR ama derleme yoksa sonuç <c>Missing</c>'dir — bu KESİN bilgidir.
    /// </summary>
    [Fact]
    public void GAC_KOKU_VAR_DERLEME_YOKSA_SONUC_YOK()
    {
        Gecici(kok =>
            Assert.Equal(HtaccessModuleAssemblyPresence.Missing, HtaccessModuleAssemblyLocator.Locate(kok).Presence));
    }

    /// <summary>
    /// Derleme bulunduğunda KİMLİĞİ DERLEMEDEN okunur — klasör adından ayrıştırılmaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GAC klasör adı (<c>v4.0_1.0.0.0__be230e22b89b56b8</c>) sürümü ve anahtarı ayrıştırılabilir
    /// görünüyor, ama bu bir BİÇİM VARSAYIMIDIR ve deponun "tahmin yasak" kuralına girer. Test
    /// bunu ölçüyor: klasör adı bilerek YALAN söylüyor (<c>v4.0_9.9.9.9__ffffffffffffffff</c>) ve
    /// okunan değerlerin derlemenin GERÇEK kimliği olması bekleniyor.
    /// </para>
    /// <para>
    /// Kullanılan dosya gerçek modül derlemesidir (çözümün net48 çıktısı): kimlik okuma yolu
    /// böylece uydurma bir ikili üzerinde değil, IIS'in gerçekten yükleyeceği dosya üzerinde
    /// ölçülüyor.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): <c>Locate</c> içindeki <c>AssemblyName.GetAssemblyName</c> yerine
    /// klasör adı ayrıştırması konduğunda bu test kızarıyor (9.9.9.9 okunuyor).
    /// </para>
    /// </remarks>
    [Fact]
    public void DERLEME_BULUNDUGUNDA_KIMLIK_DERLEMEDEN_OKUNUR()
    {
        var gercekDll = ModulDerlemesi();

        Gecici(kok =>
        {
            var klasor = Path.Combine(kok, HtaccessModuleRegistration.AssemblyName, "v4.0_9.9.9.9__ffffffffffffffff");
            Directory.CreateDirectory(klasor);
            File.Copy(gercekDll, Path.Combine(klasor, HtaccessModuleRegistration.AssemblyName + ".dll"));

            var sonuc = HtaccessModuleAssemblyLocator.Locate(kok);

            Assert.Equal(HtaccessModuleAssemblyPresence.Found, sonuc.Presence);

            // csproj AssemblyVersion'i BILEREK sabitliyor (1.0.0.0); klasor adi 9.9.9.9 diyor.
            Assert.Equal("1.0.0.0", sonuc.Version);

            // GUCLU AD ZORUNLU: GAC kurulumunun on kosulu. Jeton 8 bayt = 16 onaltilik karakter.
            Assert.Equal(16, sonuc.PublicKeyToken.Length);
            Assert.NotEqual("ffffffffffffffff", sonuc.PublicKeyToken);
            Assert.Equal(sonuc.PublicKeyToken.ToLowerInvariant(), sonuc.PublicKeyToken);
        });
    }

    // ------------------------------------------------------------------ TEK KAYNAK

    /// <summary>
    /// Kayıt servisi ad/tip/ön koşulu ELLE YAZMAZ — sabitlerden okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kurulum betiği ile C# sabitlerinin eşitliği zaten nöbet altında
    /// (<c>HtaccessHostingRegistrationTests.BETIKTEKI_DEGERLER_C_SHARP_SABITLERIYLE_AYNI</c>).
    /// Ama panel artık AYNI kaydı İKİNCİ bir yoldan yazıyor; servis değerleri elle yazsaydı nöbetçi
    /// yeşil kalır, betik ile servis sessizce ayrışırdı — "panel kurdu, IIS başka ad arıyor".
    /// </para>
    /// <para>
    /// <b>YORUM RİSKİ ÖLÇÜLDÜ:</b> aranan dizeler (<c>"AdaPanelHtaccessModule"</c>,
    /// <c>runtimeVersionv4.0</c>) bu dosyanın YORUMLARINDA da geçebilir ve öyle olsaydı test her
    /// hâlükârda kırmızı/yeşil kalırdı. Bu yüzden tarama yorumlar SÖKÜLDÜKTEN sonra yapılıyor.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): <c>Girdi</c> içindeki <c>HtaccessModuleRegistration.ModuleName</c>
    /// yerine <c>"AdaPanelHtaccessModule"</c> dizesi yazıldığında bu test kızarıyor.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("AdaPanelHtaccessModule")]
    [InlineData("runtimeVersionv4.0")]
    [InlineData("AdaPanel.HtaccessModule.HtaccessHttpModule")]
    public void KAYIT_SERVISI_SABITLERI_ELLE_YAZMAZ(string sabitDeger)
    {
        var kaynak = YorumsuzKaynak(ClusterAccessGuardTests.KaynakOku(
            "src", "AdaPanel.Infrastructure", "Htaccess", "Hosting", "HtaccessModuleRegistry.cs"));

        Assert.DoesNotContain(sabitDeger, kaynak, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ ONARIM UCU

    private const string OkumaUcu = "GetModuleRegistration";
    private const string OnarimUcu = "RegisterModuleAsync";

    /// <summary>
    /// Onarım ve okuma uçları YALNIZ YÖNETİCİYE açık.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bayi de kiracıdır.</b> Bu uç <c>applicationHost.config</c>'e yazar: sunucudaki yönetilen
    /// kod çalıştıran HER havuzu bir kez geri döndürür ve modülü her siteye yükletir — bayinin
    /// kapsamının çok dışında. <c>server-mode</c> ile aynı karar.
    /// </para>
    /// <para>
    /// ÖLÇÜM YÖNTEMİ kaynak taraması değil YANSIMA: kaynakta özniteliğin YAZILI olması, onun
    /// gerçekten BAĞLANDIĞI anlamına gelmez (bir yorum satırı bile testi geçirirdi).
    /// </para>
    /// <para>
    /// MUTASYON (denendi): eylemden <c>[Authorize(Roles = "Administrator")]</c> silindiğinde bu
    /// test kızarıyor — sınıf kapısı <c>Administrator,Reseller</c> olduğu için bayi içeri girerdi
    /// (o kapının yük taşıdığını <c>HtaccessServerModeTests</c> ayrıca ölçüyor).
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(OkumaUcu)]
    [InlineData(OnarimUcu)]
    public void MODUL_UCLARI_YALNIZ_YONETICIYE_ACIK(string eylemAdi)
    {
        var yetki = Eylem(eylemAdi).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(yetki);
        Assert.Equal("Administrator", yetki!.Roles);
        Assert.Null(Eylem(eylemAdi).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    /// <summary>
    /// Rotalar SİTE AĞACININ DIŞINDA.
    /// </summary>
    /// <remarks>
    /// Denetleyicinin rotası <c>api/v1/sites</c>. Uç oraya göreli yazılsaydı adres
    /// <c>api/v1/sites/register-module</c> olurdu ve bu SABİT segment, <c>SitesController</c>'daki
    /// <c>GET {siteName}</c> parametresini yenerdi: "register-module" adlı bir site panelden
    /// görüntülenemez hâle gelirdi — üstelik hata vermeden, yanlış ekran açarak. <c>DomainNames</c>
    /// böyle bir adı REDDETMİYOR (ölçüldü, bkz. <c>HtaccessServerModeTests</c>). Kayıt zaten
    /// sunucu genelidir; site ağacında yeri de yoktur.
    /// <para>
    /// MUTASYON (denendi): rota <c>[HttpPost("register-module")]</c> (göreli) yapıldığında bu test
    /// kızarıyor.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(OkumaUcu, "~/api/v1/htaccess/module")]
    [InlineData(OnarimUcu, "~/api/v1/htaccess/register-module")]
    public void MODUL_UCLARI_SITE_ROTASININ_ALTINDA_DEGIL(string eylemAdi, string beklenenSablon)
    {
        var sablon = Eylem(eylemAdi).GetCustomAttribute<HttpMethodAttribute>()?.Template;

        Assert.NotNull(sablon);

        // `~/` MUTLAK yoldur: denetleyicinin [Route("api/v1/sites")] on eki UYGULANMAZ.
        Assert.StartsWith("~/", sablon!);
        Assert.DoesNotContain("api/v1/sites", sablon!);
        Assert.Equal(beklenenSablon, sablon);
    }

    /// <summary>
    /// Fiiller doğru: okuma GET, yazma POST.
    /// </summary>
    /// <remarks>
    /// Okuma POST olsaydı panel belirteçleri (<c>PanelTokenCapabilities</c>) yalnızca kaydın
    /// durumunu GÖRMEK için bile "yıkıcı işlem" yetkisi istemek zorunda kalırdı.
    /// </remarks>
    [Fact]
    public void OKUMA_GET_ONARIM_POST()
    {
        Assert.NotNull(Eylem(OkumaUcu).GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(Eylem(OkumaUcu).GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(Eylem(OnarimUcu).GetCustomAttribute<HttpPostAttribute>());
    }

    /// <summary>
    /// Onarım kaydı KONU TAŞIYAMAYAN aşırı yüklemeyle yazılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modül kaydı tek bir aboneliğe ait değildir: sunucudaki her siteyi kapsar. Konuya bir kiracı
    /// geçilseydi, komşularını da kapsayan bir işlem o kiracının denetim görünümüne düşerdi
    /// (çapraz-kiracı sızıntı). Gerekçe <c>AuditCoverageTests.KiraciyaAitOlmayanlar</c>'da yazılı.
    /// </para>
    /// <para>
    /// Kaynak taraması BİLEREK: ölçülen şey davranış değil, hangi AŞIRI YÜKLEMENİN seçildiği —
    /// yani kararın kendisi. Yansımayla "hangi metot çağrıldı" sorulamaz.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): çağrı <c>WriteForSubscriptionAsync(..., null, ...)</c>'a çevrildiğinde
    /// hem bu test hem de <c>AuditCoverageTests</c> kızarıyor.
    /// </para>
    /// </remarks>
    [Fact]
    public void ONARIM_KAYDI_KONUSUZ_ASIRI_YUKLEMEYLE_YAZILIR()
    {
        var govde = UcGovdesi(OnarimUcu);

        Assert.Contains("auditService.WriteAsync(", govde, StringComparison.Ordinal);
        Assert.Contains("\"htaccess.module.registered\"", govde, StringComparison.Ordinal);
        Assert.Contains("\"htaccess:server\"", govde, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteForSubscriptionAsync", govde, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ düzenek

    /// <summary>Kusursuz bir kayıt: okundu, var, doğru tip, isteği elemeyen ön koşul.</summary>
    internal static HtaccessModuleRegistrationState Kayitli(string? onKosul = null)
        => new()
        {
            Readable = true,
            Present = true,
            // NITELIKLI AD SABITTEN URETILIYOR: elle yazilsaydi tip adi degistiginde bu sahte
            // "gecerli" kalir ve TypeMatches olcumu hicbir sey olcmez hale gelirdi.
            TypeName = HtaccessModuleRegistration.QualifiedTypeName("1.0.0.0", "be230e22b89b56b8"),
            PreCondition = onKosul ?? HtaccessModuleRegistration.PreCondition,
            Assembly = new HtaccessModuleAssemblyState
            {
                Presence = HtaccessModuleAssemblyPresence.Found,
                Version = "1.0.0.0",
                PublicKeyToken = "be230e22b89b56b8"
            }
        };

    private static async Task<HtaccessSiteReport> Rapor(
        HtaccessModuleRegistrationState kayit, string havuzSurumu = "v4.0")
    {
        var taban = Directory.CreateTempSubdirectory("adapanel-htaccess-kayit");
        try
        {
            var siteKoku = Path.Combine(taban.FullName, "site");
            Directory.CreateDirectory(siteKoku);

            var site = new SiteDto
            {
                Id = 1,
                Name = "ornek.com",
                State = "Started",
                PhysicalPath = siteKoku,
                ApplicationPoolName = "ornek.com"
            };

            var servis = new HtaccessPanelService(
                new HtaccessEngine(),
                new SahteSite(site),
                new SahteAsp(havuzSurumu),
                new SahteModulKaydi(kayit),
                Microsoft.Extensions.Options.Options.Create(
                    new PanelRuntimeOptions { DataRoot = Path.Combine(taban.FullName, "data") }),
                NullLogger<HtaccessPanelService>.Instance);

            return await servis.GetReportAsync("ornek.com", CancellationToken.None);
        }
        finally
        {
            try { taban.Delete(recursive: true); } catch (IOException) { /* silinememesi sonucu degistirmez */ }
        }
    }

    private static void Gecici(Action<string> govde)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-gac");
        try
        {
            govde(dizin.FullName);
        }
        finally
        {
            try { dizin.Delete(recursive: true); } catch (IOException) { /* silinememesi sonucu degistirmez */ }
        }
    }

    /// <summary>
    /// Çözümün net48 modül çıktısı. Bulunamazsa test GÜRÜLTÜLÜ patlar (atlamaz).
    /// </summary>
    /// <remarks>
    /// Atlamak (<c>Skip</c>) burada yanlış olurdu: bu dosya çözümün bir parçası ve her derlemede
    /// üretiliyor. Sessizce atlanan bir test, kimlik okuma yolunu ölçüyormuş gibi görünüp hiçbir
    /// şey ölçmezdi — bu depoda tam olarak bu hata sınıfının bedeli ödendi.
    /// </remarks>
    private static string ModulDerlemesi()
    {
        var kok = new DirectoryInfo(AppContext.BaseDirectory);
        while (kok is not null && !File.Exists(Path.Combine(kok.FullName, "AdaPanel.sln")))
        {
            kok = kok.Parent;
        }

        Assert.NotNull(kok);

        var modulKoku = Path.Combine(kok!.FullName, "src", "AdaPanel.HtaccessModule", "bin");
        Assert.True(Directory.Exists(modulKoku), $"modul cikti klasoru yok: {modulKoku}");

        var aday = Directory
            .EnumerateFiles(modulKoku, "AdaPanel.HtaccessModule.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Assert.True(aday is not null, $"AdaPanel.HtaccessModule.dll bulunamadi: {modulKoku}");
        return aday!;
    }

    /// <summary>
    /// Kaynaktan YORUMLARI söker.
    /// </summary>
    /// <remarks>
    /// <b>Bu depoda bir mutasyon tam da bu yüzden kaçtı:</b> kaynağa bakan bir test aradığı dizeyi
    /// bir YORUM satırında bulup yeşil kalmıştı. Burada tarama yalnız GERÇEK KOD üzerinde
    /// yapılıyor — dize yorumda geçse bile test kandırılamaz.
    /// </remarks>
    private static string YorumsuzKaynak(string kaynak)
    {
        var satirlar = kaynak.Split('\n').Select(satir => satir.TrimEnd('\r'));
        var temiz = new List<string>();
        var blokIcinde = false;

        foreach (var satir in satirlar)
        {
            var kirpik = satir.TrimStart();

            if (blokIcinde)
            {
                if (kirpik.Contains("*/", StringComparison.Ordinal))
                {
                    blokIcinde = false;
                }

                continue;
            }

            if (kirpik.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (kirpik.StartsWith("/*", StringComparison.Ordinal))
            {
                blokIcinde = !kirpik.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            temiz.Add(satir);
        }

        return string.Join("\n", temiz);
    }

    private static MethodInfo Eylem(string ad)
    {
        var eylem = typeof(HtaccessController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(metot => metot.Name == ad);

        Assert.True(eylem is not null, $"HtaccessController.{ad} bulunamadi.");
        return eylem!;
    }

    private static string UcGovdesi(string eylemAdi)
    {
        var kaynak = ClusterAccessGuardTests.KaynakOku("src", "AdaPanel.Api", "Controllers", "HtaccessController.cs");
        var bas = kaynak.IndexOf(" " + eylemAdi + "(", StringComparison.Ordinal);
        Assert.True(bas > 0, $"{eylemAdi} kaynakta bulunamadi (ad degistiyse test de guncellenmeli).");

        var son = kaynak.IndexOf("    [Http", bas, StringComparison.Ordinal);
        return kaynak[bas..(son < 0 ? kaynak.Length : son)];
    }

    // ------------------------------------------------------------------ sahteler

    /// <summary>
    /// GERÇEK IIS'e DOKUNMAYAN kayıt ölçümü.
    /// </summary>
    /// <remarks>
    /// Bu sahtenin varlık sebebi, arayüzün (<c>IHtaccessModuleRegistry</c>) varlık sebebiyle
    /// aynı: kaydın okunamadığı, eksik olduğu ya da ön koşulunun yanlış olduğu hâller ancak
    /// enjekte edilerek ölçülebilir. Gerçek uygulamaya bağlı kalsaydık bu testler yalnız IIS'li
    /// bir sunucuda anlamlı olurdu — yani hiç koşmazdı.
    /// </remarks>
    internal sealed class SahteModulKaydi(HtaccessModuleRegistrationState durum) : IHtaccessModuleRegistry
    {
        public HtaccessModuleRegistrationState Read() => durum;

        // Onarim yolu bu sahtede BILEREK desteklenmiyor: rapor testleri onu cagirmamali ve
        // cagirirlarsa SESSIZCE degil GURULTULU biçimde bilinmeli.
        public HtaccessModuleRegistrationState Register() => throw new NotSupportedException();
    }

    private sealed class SahteSite(SiteDto site) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => Task.FromResult(site);
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]);
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteAsp(string havuzSurumu) : IAspNetService
    {
        public Task<AspSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AspSiteConfigurationDto
            {
                SiteName = siteName,
                ApplicationPoolName = siteName,
                ManagedRuntimeVersion = havuzSurumu
            });

        public Task<AspSiteConfigurationDto> SetClassicAspAsync(string siteName, UpdateClassicAspRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AspSiteConfigurationDto> SetDotNetVersionAsync(string siteName, UpdateDotNetRuntimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AspSiteConfigurationDto> EnableMixedHandlersAsync(string siteName, EnableMixedHandlersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
