using System.Reflection;
using System.Security.AccessControl;
using AdaPanel.Api.Controllers;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
}
