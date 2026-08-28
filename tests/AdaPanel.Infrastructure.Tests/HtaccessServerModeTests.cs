using System.Reflection;
using System.Security.AccessControl;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SUNUCU GENELİ <c>.htaccess</c> ANAHTARININ PANEL UCU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu ucun ölçülmüş varlık sebebi:</b> kapı etkin modu iki anahtarın EN DÜŞÜĞÜ olarak hesaplar
/// (<c>HtaccessRuntimeGate.Evaluate</c>), yani genel anahtar kapalıyken hiçbir site kendini
/// açamaz. Uzun süre panelde bu anahtarın ucu YOKTU ve gerekçesi "bedeli sıfır, motor w3wp içinde
/// zaten yüklenmiyor" idi. O gerekçe geçersizleşti (<c>HtaccessPanelService.MotorEngelleri()</c>
/// artık boş liste dönüyor, ölçümü <c>HtaccessNet48EngineLoadTests</c>), dolayısıyla ucun
/// olmaması özelliğin panelden HİÇ kullanılamaması demekti — yöneticinin
/// <c>%ProgramData%\AdaPanel\htaccess\module.switch</c> dosyasını elle yazması gerekiyordu.
/// </para>
/// <para>
/// Buradaki testler ucun iki riskini kapatır: (1) düğme sunucudaki BÜTÜN siteleri etkilediği için
/// kiracıya (bayi/müşteri) açık olmamalı, (2) yazım yarım kalırsa dosya, hiçbir koşulda istenenden
/// DAHA AÇIK bir kip üretmemeli.
/// </para>
/// </remarks>
public sealed class HtaccessServerModeTests
{
    private const string GetUcu = "GetServerMode";
    private const string PostUcu = "SetServerModeAsync";

    // ------------------------------------------------------------------ ROL KAPISI

    /// <summary>
    /// Sunucu geneli uçlar YALNIZ YÖNETİCİYE açık.
    /// </summary>
    /// <remarks>
    /// ÖLÇÜM YÖNTEMİ kaynak taraması değil YANSIMA: kaynakta <c>[Authorize(Roles = "Administrator")]</c>
    /// yazısını aramak, özniteliğin gerçekten bağlandığını değil yalnızca yazıldığını ölçerdi —
    /// bir yorum satırı bile testi geçirirdi (aynı gerekçe <c>ClusterAccessGuardTests</c>'te yazılı).
    /// </remarks>
    [Theory]
    [InlineData(GetUcu)]
    [InlineData(PostUcu)]
    public void Sunucu_geneli_uclar_YALNIZ_YONETICIYE_acik(string eylemAdi)
    {
        var yetki = Eylem(eylemAdi).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(yetki);
        Assert.Equal("Administrator", yetki!.Roles);
        Assert.Null(Eylem(eylemAdi).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    /// <summary>
    /// Sunucu geneli uçlar SİTE ROTASININ ALTINDA OLMAMALI.
    /// </summary>
    /// <remarks>
    /// Denetleyicinin kendi rotası <c>api/v1/sites</c>. Uç oraya göreli yazılırsa adres
    /// <c>api/v1/sites/server-mode</c> olur ve bu SABİT segment, <c>SitesController</c>'daki
    /// <c>GET {siteName}</c> parametresini yener: "server-mode" adlı bir site panelden
    /// görüntülenemez hâle gelir, üstelik hata da vermez — yanlış ekran açılır.
    /// <para>
    /// Bunun imkânsız olduğu VARSAYILMADI, ölçüldü: <c>DomainNames</c> yalnızca normalleştirme
    /// yapıyor (kırp, sondaki noktayı at, küçült); böyle bir adı reddeden bir biçim denetimi YOK.
    /// Sabit segmenti parametrenin yanına hiç koymamak, sınıfı olasılık tartışmadan kapatır.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(GetUcu)]
    [InlineData(PostUcu)]
    public void Sunucu_geneli_uclar_SITE_ROTASININ_altinda_DEGIL(string eylemAdi)
    {
        var sablon = Eylem(eylemAdi).GetCustomAttribute<HttpMethodAttribute>()?.Template;

        Assert.NotNull(sablon);

        // `~/` mutlak yol demektir: denetleyicinin `[Route("api/v1/sites")]` ön eki UYGULANMAZ.
        Assert.StartsWith("~/", sablon!);
        Assert.DoesNotContain("api/v1/sites", sablon!);
    }

    /// <summary>
    /// Kiracı rolleri ADIYLA dışarıda.
    /// </summary>
    /// <remarks>
    /// <b>Bayi de kiracıdır.</b> Kendi müşterilerinin sitelerini yönetir, ama bu düğme onun
    /// kapsamının DIŞINDAKİ bütün siteleri de açar/kapatır: sunucudaki her sitede
    /// <c>.htaccess</c>'i uygulamaya sokmak ya da bir anda kapatmak. Rol listesine bir gün
    /// "Reseller" eklenirse bu test kırılır — ki eklenmesi bilinçli bir karar olmalı,
    /// kopyala-yapıştır kazası değil.
    /// </remarks>
    [Theory]
    [InlineData(GetUcu, "Reseller")]
    [InlineData(GetUcu, "Customer")]
    [InlineData(PostUcu, "Reseller")]
    [InlineData(PostUcu, "Customer")]
    public void Kiraci_rolleri_sunucu_geneli_uctan_DISARIDA(string eylemAdi, string rol)
    {
        var yetki = Eylem(eylemAdi).GetCustomAttribute<AuthorizeAttribute>();

        // ÖLÇÜLDÜ (mutasyon): öznitelik TAMAMEN silindiğinde Roles boş dizeye düşer ve aşağıdaki
        // "içermiyor" iddiası KENDİLİĞİNDEN geçerdi — yani en olası kaza (özniteliği silmek) bu
        // testi kızartmazdı. Varlık denetimi bu boşluğu kapatıyor.
        Assert.NotNull(yetki);

        var roller = (yetki!.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.DoesNotContain(rol, roller, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EYLEM KAPISI GERÇEKTEN YÜK TAŞIYOR: sınıf kapısı kiracıyı içeri alıyor.
    /// </summary>
    /// <remarks>
    /// Bu ölçüm olmadan yukarıdaki testler yanıltıcı olurdu: sınıf düzeyindeki kapı zaten yalnız
    /// yöneticiye açık olsaydı, eylem üzerindeki öznitelik silinse bile hiçbir test kızarmaz ve
    /// koruma "test altında" sanılırdı. <c>HtaccessController</c> site uçları için
    /// <c>Administrator,Reseller</c> taşıyor — yani bayiyi asıl durduran şey EYLEM özniteliğidir.
    /// </remarks>
    [Fact]
    public void Sinif_kapisi_BAYIYI_iceri_aliyor_bu_yuzden_eylem_kapisi_ZORUNLU()
    {
        var sinifRolleri = typeof(HtaccessController).GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? string.Empty;

        Assert.Contains("Reseller", sinifRolleri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rotalar sabit: arayüz bu yolları çağırıyor, değişirse özellik sessizce 404'e düşer.
    /// </summary>
    /// <remarks>
    /// Fiil de ölçülüyor: okuma GET, değiştirme POST. Okuma POST olsaydı panel belirteçleri
    /// (<c>PanelTokenCapabilities</c>) yalnızca durumu GÖRMEK için bile "yıkıcı işlem" yetkisi
    /// istemek zorunda kalırdı.
    /// </remarks>
    [Fact]
    public void Rotalar_server_mode_ve_fiiller_dogru()
    {
        var okuma = Eylem(GetUcu).GetCustomAttribute<HttpGetAttribute>();
        var yazma = Eylem(PostUcu).GetCustomAttribute<HttpPostAttribute>();

        // `~/` MUTLAK yoldur: denetleyicinin `[Route("api/v1/sites")]` ön eki uygulanmaz.
        // Gerekçesi Sunucu_geneli_uclar_SITE_ROTASININ_altinda_DEGIL testinde yazılı; buradaki
        // eşitlik arayüzün çağırdığı ADRESİ sabitler (`frontend/src/api/htaccess.ts`).
        Assert.Equal("~/api/v1/htaccess/server-mode", okuma?.Template);
        Assert.Equal("~/api/v1/htaccess/server-mode", yazma?.Template);
        Assert.Null(Eylem(GetUcu).GetCustomAttribute<HttpPostAttribute>());
    }

    /// <summary>
    /// Denetim kaydı KONU TAŞIYAMAYAN aşırı yüklemeyle yazılıyor — karar GÖRÜNÜR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Genel anahtar tek bir aboneliğe ait değildir; konusu (SubscriptionUserId) yapısal olarak
    /// boştur. Kayıt <c>WriteForSubscriptionAsync</c> ile yazılıp konuya bir kiracı geçilseydi,
    /// komşularını da kapsayan bir işlem o kiracının denetim görünümüne düşerdi (çapraz-kiracı
    /// sızıntı). Bu yüzden konu taşıyamayan <c>WriteAsync</c> seçildi ve gerekçesi
    /// <c>AuditCoverageTests.KiraciyaAitOlmayanlar</c> listesinde yazılı.
    /// </para>
    /// <para>
    /// Kaynak taraması bilerek: ölçülen şey davranış değil, hangi AŞIRI YÜKLEMENİN seçildiği —
    /// yani kararın kendisi. Yansımayla "hangi metot çağrıldı" sorulamaz.
    /// </para>
    /// </remarks>
    [Fact]
    public void Sunucu_geneli_kayit_KONUSUZ_asiri_yuklemeyle_yazilir()
    {
        var govde = UcGovdesi(PostUcu);

        Assert.Contains("auditService.WriteAsync(", govde, StringComparison.Ordinal);
        Assert.Contains("\"htaccess.server_mode.changed\"", govde, StringComparison.Ordinal);
        Assert.Contains("\"htaccess:server\"", govde, StringComparison.Ordinal);

        // Site kayitlarinin kaynagi "htaccess:{siteName}"; ikisi denetim ekraninda karismamali.
        Assert.DoesNotContain("WriteForSubscriptionAsync", govde, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ EKSİK GÖVDE / EKSİK KİP

    /// <summary>
    /// KİP SÖYLENMEDİĞİNDE UÇ REDDEDER — sunucuyu SESSİZCE KAPATMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ölçülen açık:</b> uç <c>request?.Mode ?? "off"</c> yazıyordu ve istek tipinin
    /// <c>Mode</c> alanı <c>= "off"</c> ile başlıyordu. İkisi birlikte, kip TAŞIMAYAN her gövdeyi
    /// (<c>{}</c>, <c>{"note":"x"}</c>, <c>{"mode":null}</c>, gövdesiz POST) geçerli bir "kapat"
    /// isteğine çeviriyordu. <c>HtaccessPanelService.ModCozumle</c> tanınmayan kipi ZATEN
    /// reddediyor ve gerekçesi orada yazılı — ama bu gövdeler o kapıya HİÇ GELMİYORDU.
    /// </para>
    /// <para>
    /// <b>Bedeli neden büyük:</b> bu düğme sunucudaki HER siteyi etkiler; <c>off</c> anahtar
    /// dosyasını SİLER ve kapı etkin kipi iki anahtarın en düşüğü olarak hesapladığı için hiçbir
    /// site kendini geri açamaz. Üstüne denetim kaydı "set to 'off'" yazardı: yangını arayan kişi
    /// yöneticinin bunu BİLEREK yaptığını okurdu.
    /// </para>
    /// <para>
    /// <b><c>Off</c>'un kendisi yasak değil</b> — yasaklanan şey kip söylenmediğinde off
    /// varsaymak. Bunu <c>ACIK_SOYLENEN_off_KABUL_EDILIR</c> ölçüyor.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): uçta <c>request?.Mode</c> yerine <c>request?.Mode ?? "off"</c>
    /// yazıldığında bu test kızarır (istisna gelmez); istek tipindeki <c>= "off"</c> varsayılanı
    /// geri konduğunda ise <c>new HtaccessModeRequest()</c> satırı kızarır.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("bos-govde")]    // gövdesiz POST: model bağlayıcı null verir
    [InlineData("bos-nesne")]    // {} : alanın varsayılanı devreye girerdi
    [InlineData("null-kip")]     // {"mode":null}
    [InlineData("bos-kip")]      // {"mode":""}
    public async Task SUNUCU_GENELI_EKSIK_KIP_REDDEDILIR_ve_ANAHTAR_ACIK_KALIR(string govdeTuru)
    {
        await Gecici(async dataRoot =>
        {
            var denetim = new SayanDenetim();
            var denetleyici = Denetleyici(dataRoot, denetim);

            // ÖN KOŞUL: sunucu AÇIK. Kapalıyken test, "kapatmadı" iddiasını hiç ölçmezdi.
            // Site anahtarı da açılıyor, çünkü kapı etkin kipi İKİ anahtarın EN DÜŞÜĞÜ olarak
            // hesaplar; site kapalı bırakılsaydı gerçek okuyucu genel anahtar ne olursa olsun
            // "off" derdi ve aşağıdaki ölçüm hiçbir şey ölçmezdi.
            var depo = new HtaccessSwitchStore(dataRoot);
            depo.SetSite("ornek.com", HtaccessMode.Enforce, null);
            depo.SetGlobal(HtaccessMode.Enforce, "yonetici acti");
            var oncekiIcerik = File.ReadAllText(HtaccessHostingPaths.GlobalSwitchPath(dataRoot));

            var hata = await Assert.ThrowsAsync<AppException>(
                () => denetleyici.SetServerModeAsync(Govde(govdeTuru), CancellationToken.None));

            Assert.Equal("htaccess.invalid_mode", hata.MessageKey);

            // ANAHTAR YERİNDE ve DEĞİŞMEMİŞ: dosya silinmedi, içeriği aynı, modül hâlâ uyguluyor.
            Assert.Equal(oncekiIcerik, File.ReadAllText(HtaccessHostingPaths.GlobalSwitchPath(dataRoot)));
            Assert.Equal(HtaccessMode.Enforce, new HtaccessRuntimeGate(dataRoot, TimeSpan.Zero).Evaluate("ornek.com"));

            // OLMAYAN BİR KARARIN KAYDI DA BIRAKILMAZ: ret, denetim satırından ÖNCE olur.
            Assert.Empty(denetim.Kayitlar);
        });
    }

    /// <summary>
    /// AÇIK SÖYLENEN <c>off</c> KABUL EDİLİR — acil durum kapatma düğmesi çalışmaya devam eder.
    /// </summary>
    /// <remarks>
    /// Bu test olmadan yukarıdaki iddia yanıltıcı olurdu: "her <c>off</c> isteğini reddet" gibi
    /// bir değişiklik de onu yeşil geçerdi ve panelin tek acil kapatma düğmesi sessizce ölürdü.
    /// </remarks>
    [Fact]
    public async Task ACIK_SOYLENEN_off_KABUL_EDILIR()
    {
        await Gecici(async dataRoot =>
        {
            var denetim = new SayanDenetim();
            var denetleyici = Denetleyici(dataRoot, denetim);
            var depo = new HtaccessSwitchStore(dataRoot);
            depo.SetSite("ornek.com", HtaccessMode.Enforce, null);
            depo.SetGlobal(HtaccessMode.Enforce, null);

            var cevap = await denetleyici.SetServerModeAsync(
                new HtaccessModeRequest { Mode = "off", Note = "acil kapatma" }, CancellationToken.None);

            Assert.Equal("off", Sonuc(cevap).Mode);
            Assert.False(File.Exists(HtaccessHostingPaths.GlobalSwitchPath(dataRoot)));
            Assert.Equal(HtaccessMode.Off, new HtaccessRuntimeGate(dataRoot, TimeSpan.Zero).Evaluate("ornek.com"));

            // Gerçek bir karar VERİLDİ: kaydı da yazılmalı.
            Assert.Single(denetim.Kayitlar);
        });
    }

    /// <summary>
    /// SİTE BAZLI UÇTA DA KİP SÖYLENMEDİĞİNDE REDDEDİLİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir karar:</b> bu ucun etkisi TEK SİTEYLE sınırlı, yani sunucu geneli
    /// anahtardaki kadar geniş değil. Yine de aynı kararı taşıyor ve iki gerekçesi var.
    /// (1) İki uç AYNI istek tipini (<c>HtaccessModeRequest</c>) kullanıyor: alanın varsayılanı
    /// tek yerde yaşıyor, dolayısıyla "yalnız sunucu geneli ucu düzeltelim" diye bir seçenek
    /// YOK — varsayılan kaldığı sürece sunucu geneli açık da açık kalırdı. (2) Tek site "az" değil:
    /// kip sessizce off'a düşünce sitenin yönlendirmeleri ve erişim kuralları durur, denetim
    /// kaydı ise bunu çağıranın BİLİNÇLİ kararı gibi yazar — kiracı olan biteni o kayıttan okur.
    /// </para>
    /// <para>
    /// MUTASYON (denendi): uçta <c>request?.Mode ?? "off"</c> geri yazıldığında bu test kızarır.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("bos-govde")]
    [InlineData("bos-nesne")]
    [InlineData("null-kip")]
    public async Task SITE_UCUNDA_da_EKSIK_KIP_REDDEDILIR(string govdeTuru)
    {
        await Gecici(async dataRoot =>
        {
            var denetim = new SayanDenetim();
            var denetleyici = Denetleyici(dataRoot, denetim);
            new HtaccessSwitchStore(dataRoot).SetSite("ornek.com", HtaccessMode.Enforce, null);

            var hata = await Assert.ThrowsAsync<AppException>(
                () => denetleyici.SetModeAsync("ornek.com", Govde(govdeTuru), CancellationToken.None));

            Assert.Equal("htaccess.invalid_mode", hata.MessageKey);

            // Sitenin kendi anahtarı DEĞİŞMEDİ ve kayıt yazılmadı.
            Assert.Equal(HtaccessMode.Enforce, new HtaccessSwitchStore(dataRoot).ReadSite("ornek.com").Mode);
            Assert.Empty(denetim.Kayitlar);
        });
    }

    // ------------------------------------------------------------------ ATOMİK YAZIM

    /// <summary>
    /// Yazım sonrası ARTIK BIRAKILMAZ: klasörde yalnız anahtar dosyası kalır.
    /// </summary>
    /// <remarks>
    /// Geçici dosya kalsaydı zararsız görünürdü ama klasör her açma/kapama ile büyür ve yangın
    /// anında yöneticinin "hangisi gerçek anahtar" sorusuna bakması gereken yer kirlenirdi.
    /// </remarks>
    [Fact]
    public void ATOMIK_YAZIM_GECICI_DOSYA_BIRAKMAZ()
    {
        Gecici(dataRoot =>
        {
            var store = new HtaccessSwitchStore(dataRoot);
            var kok = HtaccessHostingPaths.Root(dataRoot);

            for (var i = 0; i < 5; i++)
            {
                store.SetGlobal(HtaccessMode.Observe, "tur " + i);
                store.SetGlobal(HtaccessMode.Enforce, null);
            }

            var dosyalar = Directory.GetFiles(kok).Select(Path.GetFileName).ToArray();
            Assert.Equal([HtaccessHostingPaths.GlobalSwitchFileName], dosyalar);
        });
    }

    /// <summary>
    /// Dosya YERİNDE değiştirilmiyor: yazım başarısız olduğunda bile ESKİ, TAM içerik yerinde kalır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN WINDOWS DAVRANIŞI:</b> hedef dosya başka bir tutamakla açıkken
    /// <c>File.Move(..., overwrite: true)</c> <c>UnauthorizedAccessException</c> verir — okuyanın
    /// <c>FileShare.Delete</c> vermiş olması bunu DEĞİŞTİRMEZ (modülün okuyucusu tam olarak öyle
    /// açar). Bu test o çakışmayı bilerek KALICI hâle getiriyor (tutamağı yazım boyunca açık
    /// tutuyor), yani <c>HtaccessSwitchStore.ReplaceWithRetry</c>'nin bütün denemeleri tükeniyor.
    /// </para>
    /// <para>
    /// <b>Ölçülen asıl şey davranışın SONUCU:</b> yazım gürültülü biçimde başarısız olur ve dosya
    /// ESKİ hâliyle, TAM olarak yerinde kalır. Yani en kötü sonuç "değişiklik olmadı"dır —
    /// "yarım dosya" ya da sessizce açılmış/kapanmış bir sunucu değil.
    /// </para>
    /// <para>
    /// MUTASYON: <c>WriteAtomic</c> içindeki geçici dosya + taşıma yerine doğrudan
    /// <c>File.WriteAllText(path, content)</c> konursa yazım açık tutamağa RAĞMEN BAŞARILI olur
    /// (ölçüldü) — istisna gelmez ve bu iddia kırılır. Yani test gerçekten atomik yolu koruyor.
    /// </para>
    /// </remarks>
    [Fact]
    public void HEDEF_ACIKKEN_YAZIM_GURULTULU_BASARISIZ_OLUR_ve_ESKI_ICERIK_BOZULMAZ()
    {
        Gecici(dataRoot =>
        {
            var store = new HtaccessSwitchStore(dataRoot);
            store.SetGlobal(HtaccessMode.Observe, null);
            var yol = HtaccessHostingPaths.GlobalSwitchPath(dataRoot);
            var eskiIcerik = File.ReadAllText(yol);

            // Modulun kullandigi paylasim bayraklarinin AYNISI (bkz. FileSystemSwitchReader).
            using (new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.ThrowsAny<Exception>(() => store.SetGlobal(HtaccessMode.Enforce, null));

                // ESKI icerik TAM ve gecerli: yarim dosya yok, kip degismedi.
                Assert.Equal(eskiIcerik, File.ReadAllText(yol));
                Assert.Equal(HtaccessMode.Observe, HtaccessSwitchFile.Parse(File.ReadAllText(yol)));
            }

            // Gecici dosya artigi da birakilmamis olmali (WriteAtomic'in finally blogu).
            Assert.Equal(
                [HtaccessHostingPaths.GlobalSwitchFileName],
                Directory.GetFiles(HtaccessHostingPaths.Root(dataRoot)).Select(Path.GetFileName).ToArray());

            // Tutamak kapandiktan sonra ayni cagri CALISIR: ariza gecici, kalici degil.
            store.SetGlobal(HtaccessMode.Enforce, null);
            Assert.Equal(HtaccessMode.Enforce, HtaccessSwitchFile.Parse(File.ReadAllText(yol)));
        });
    }

    /// <summary>
    /// KESİLEN her yazım, istenenden DAHA AÇIK bir kip üretemez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Panelin yazımı atomiktir, ama dosya aynı zamanda bir KURTARMA ARACIDIR ve elle de
    /// düzenlenir: disk dolabilir, süreç ölebilir. Bu yüzden ikinci bir katman var —
    /// <c>HtaccessSwitchFile.Render</c> önce <c>mode</c>, sonra <c>enabled</c> yazar. Ters sırada,
    /// <c>enabled=1</c>'de kesilen bir dosyada mod satırı görülmez ve ayrıştırıcı varsayılan
    /// ENFORCE'a düşerdi: gözleme alınmak istenen sunucu GERÇEK UYGULAMAYA geçerdi.
    /// </para>
    /// <para>
    /// MUTASYON: <c>Render</c> içinde iki satırın sırası geri alınırsa <c>observe</c> için
    /// kesilmiş bir önek <c>Enforce</c> üretir ve bu iddia kırılır.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("observe")]
    [InlineData("enforce")]
    public void KESILEN_YAZIM_ISTENENDEN_DAHA_ACIK_KIP_URETMEZ(string kip)
    {
        // HtaccessMode INTERNAL: xUnit yalniz public metotlari kesfettigi icin kip adi disaridan,
        // enum degeri burada cozuluyor.
        var istenen = kip == "observe" ? HtaccessMode.Observe : HtaccessMode.Enforce;
        var tam = HtaccessSwitchFile.Render(istenen, "yonetici notu");

        for (var uzunluk = 0; uzunluk <= tam.Length; uzunluk++)
        {
            var kesik = tam[..uzunluk];
            var mod = HtaccessSwitchFile.Parse(kesik);

            Assert.True(
                mod == HtaccessMode.Off || mod == istenen,
                $"{uzunluk} karakterde kesilen dosya '{mod}' uretti; istenen '{istenen}' idi.");
        }

        Assert.Equal(istenen, HtaccessSwitchFile.Parse(tam));
    }

    // ------------------------------------------------------------------ ERİŞİM İZİNLERİ

    /// <summary>
    /// Anahtarı yazmak klasörün ERİŞİM İZİNLERİNİ değiştirmez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>htaccess</c> klasörü <c>DataRootHardeningService.SensitiveDirectories</c> listesinde
    /// DEĞİLDİR (liste: data, keys, certs, config, backups, mail-certs); kendi sertleştirmesi
    /// ayrıdır ve kurulumda BİR KEZ uygulanır (<c>HtaccessSwitchStore.HardenAcl</c>).
    /// </para>
    /// <para>
    /// Ölçülen şey: her açma/kapamada ACL'e dokunulmadığı. Dokunulsaydı bu deponun ölçtüğü iki
    /// zarar geri gelirdi — yöneticinin bilinçli izin düzenlemesi ilk açmada silinir, ACL
    /// uygulaması başarısız olduğunda anahtarı yazmak da imkânsızlaşırdı. Yükseltilmiş yetki
    /// GEREKMEZ: karşılaştırılan şey mutlak izinler değil, yazımdan ÖNCEKİ ve SONRAKİ hâlin
    /// aynı olması.
    /// </para>
    /// </remarks>
    [Fact]
    public void ANAHTAR_YAZMAK_KLASOR_IZINLERINI_DEGISTIRMEZ()
    {
        Gecici(dataRoot =>
        {
            var store = new HtaccessSwitchStore(dataRoot);
            store.EnsureDirectories();

            var kok = new DirectoryInfo(HtaccessHostingPaths.Root(dataRoot));
            var once = kok.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            store.SetGlobal(HtaccessMode.Enforce, "izin olcumu");
            var yazdiktanSonra = kok.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            store.SetGlobal(HtaccessMode.Off, null);
            var sildiktenSonra = kok.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            Assert.Equal(once, yazdiktanSonra);
            Assert.Equal(once, sildiktenSonra);
        });
    }

    // ------------------------------------------------------------------ düzenek

    private static MethodInfo Eylem(string ad)
    {
        var eylem = typeof(HtaccessController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(metot => metot.Name == ad);

        Assert.True(eylem is not null, $"HtaccessController.{ad} bulunamadi.");
        return eylem!;
    }

    /// <summary>
    /// Bir ucun kaynak gövdesi: öznitelikten bir sonraki uç özniteliğine (ya da dosya sonuna) kadar.
    /// </summary>
    private static string UcGovdesi(string eylemAdi)
    {
        var kaynak = ClusterAccessGuardTests.KaynakOku("src", "AdaPanel.Api", "Controllers", "HtaccessController.cs");
        var bas = kaynak.IndexOf(" " + eylemAdi + "(", StringComparison.Ordinal);
        Assert.True(bas > 0, $"{eylemAdi} kaynakta bulunamadi (ad degistiyse test de guncellenmeli).");

        var son = kaynak.IndexOf("    [Http", bas, StringComparison.Ordinal);
        return kaynak[bas..(son < 0 ? kaynak.Length : son)];
    }

    private static void Gecici(Action<string> govde)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-server-mode");
        try
        {
            govde(dizin.FullName);
        }
        finally
        {
            try { dizin.Delete(recursive: true); } catch (IOException) { /* silinememesi sonucu degistirmez */ }
        }
    }

    private static async Task Gecici(Func<string, Task> govde)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-server-mode");
        try
        {
            await govde(dizin.FullName);
        }
        finally
        {
            try { dizin.Delete(recursive: true); } catch (IOException) { /* silinememesi sonucu degistirmez */ }
        }
    }

    // ------------------------------------------------------------------ uçtan uca düzenek

    /// <summary>
    /// Kip taşımayan istek gövdelerinin dört hâli — hepsi ayrı bir istemci kazası.
    /// </summary>
    /// <remarks>
    /// Ayrı ayrı sayılıyorlar çünkü uçta AYRI kod yollarından geçiyorlar: <c>bos-govde</c> model
    /// bağlayıcının <c>null</c>'ını (<c>request?.</c>), <c>bos-nesne</c> istek tipinin ALAN
    /// varsayılanını, diğer ikisi de değerin kendisini sınar. Tek bir hâlle yetinilseydi diğer
    /// kapı açık kalır ve test yine yeşil olurdu.
    /// </remarks>
    private static HtaccessModeRequest Govde(string tur) => tur switch
    {
        "bos-govde" => null!,
        "bos-nesne" => new HtaccessModeRequest(),
        "null-kip" => new HtaccessModeRequest { Mode = null, Note = "not var, kip yok" },
        "bos-kip" => new HtaccessModeRequest { Mode = string.Empty },
        _ => throw new ArgumentOutOfRangeException(nameof(tur), tur, "Bilinmeyen govde turu.")
    };

    private static T Sonuc<T>(ActionResult<ApiResponse<T>> cevap)
    {
        var ok = Assert.IsType<OkObjectResult>(cevap.Result);
        var zarf = Assert.IsType<ApiResponse<T>>(ok.Value);
        return zarf.Data!;
    }

    /// <summary>
    /// GERÇEK panel servisiyle kurulmuş GERÇEK denetleyici.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Panel servisi taklit EDİLMİYOR</b> ve bu ölçümün özü: kapatılan açık, servisin
    /// (<c>ModCozumle</c>) doğru davranmasına RAĞMEN denetleyicinin onu atlamasıydı. Taklit bir
    /// servisle "denetleyici hangi dizeyi geçirdi" ölçülürdü — yani düzeltmenin gerçekten
    /// çalıştığı değil, yalnızca kodun şu an nasıl yazıldığı.
    /// </para>
    /// <para>
    /// <b>Kapsam servisi <c>null!</c> ve bu bilerek:</b> çağıran Administrator, dolayısıyla
    /// <c>CustomerAccess.GetEffectiveSitesAsync</c> kapsamsız rollerde servise HİÇ dokunmadan
    /// <c>null</c> döner. Yol bir gün değişip servise dokunulursa test
    /// <c>NullReferenceException</c> ile KIZARIR — yani sessizce yanlış bir şey ölçmez.
    /// </para>
    /// </remarks>
    private static HtaccessController Denetleyici(string dataRoot, IAuditService denetim)
    {
        var site = new SiteDto
        {
            Id = 1,
            Name = "ornek.com",
            State = "Started",
            PhysicalPath = Path.Combine(dataRoot, "site"),
            ApplicationPoolName = "ornek.com"
        };
        Directory.CreateDirectory(site.PhysicalPath);

        var servis = new HtaccessPanelService(
            new HtaccessEngine(),
            new SahteSiteServisi(site),
            new SahteAspServisi(),
            // TAM NITELIKLI: "Options" adi AdaPanel.Infrastructure.Options AD ALANINA cozuluyor.
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }),
            NullLogger<HtaccessPanelService>.Instance);

        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Administrator")
            ],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

        return new HtaccessController(servis, null!, denetim)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = kullanici }
            }
        };
    }

    /// <summary>Yazılan denetim kayıtlarını SAYAR: "olmayan kararın kaydı" da bir bulgudur.</summary>
    private sealed class SayanDenetim : IAuditService
    {
        public List<string> Kayitlar { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add(details);
            return Task.CompletedTask;
        }

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add(details);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(SiteDto site) : IIisSiteService
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

    private sealed class SahteAspServisi : IAspNetService
    {
        public Task<AspSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AspSiteConfigurationDto
            {
                SiteName = siteName,
                ApplicationPoolName = siteName,
                ManagedRuntimeVersion = "v4.0"
            });

        public Task<AspSiteConfigurationDto> SetClassicAspAsync(string siteName, UpdateClassicAspRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AspSiteConfigurationDto> SetDotNetVersionAsync(string siteName, UpdateDotNetRuntimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AspSiteConfigurationDto> EnableMixedHandlersAsync(string siteName, EnableMixedHandlersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
