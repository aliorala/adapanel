using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Extensions;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Notifications;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BAĞLANTI ANINDA adres denetimi: yönlendirme (302) ve DNS rebinding kapıları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu dosya var:</b> <c>OutboundAddressGuard</c> isteği atmadan ÖNCE adın nereye
/// çözüldüğünü ölçer ve kendi belgesinde bunun bir SINIR olduğu yazılıydı: "dış bir adres
/// <c>302 Location: http://169.254.169.254/</c> ile iç ağa atlarsa bu denetim atlanmış olur".
/// Ölçüldü ki varsayılan <c>HttpClient</c> yönlendirmeyi İZLİYOR, yani sınır teorik değildi.
/// Burada kapatılan tam olarak o sınırdır ve testler kodu okuyarak değil, GERÇEK bir soket
/// üzerinden ölçer.
/// </para>
/// <para>
/// <b>HİÇBİR TEST MAKİNEDEN ÇIKMAZ.</b> Bütün sunucular <c>127.0.0.1</c> üzerinde, testin kendi
/// <see cref="TcpListener"/>'ıyla ayağa kalkar; hiçbir ada gerçek DNS çözümlemesi yapılmaz
/// (adresler düz IP yazılır ya da sahte çözümleyici verilir).
/// </para>
/// <para>
/// <b>KAPININ SİMÜLASYONU NEDEN POLİTİKA DEĞİŞTİRerek yapılıyor:</b> "dış bir sunucu iç ağa
/// yönlendiriyor" senaryosunu gerçek adreslerle kurmak, testin GERÇEKTEN dış bir sunucuya
/// bağlanmasını gerektirirdi (bu makinedeki her adres — 127.x, 192.168.x, 169.254.x — engel
/// listesindedir). Bunun yerine BİRİNCİ bağlantı "temiz dış konak" sayılır (açık kapı politikası),
/// sonraki bağlantılar denetlenir. Denetlenen gövde ÜRETİMDEKİ gövdenin ta kendisidir; değişen tek
/// şey ilk adımın temiz sayılmasıdır — yani ölçülen şey "yönlendirme adımı denetleniyor mu"dur.
/// </para>
/// </remarks>
public sealed class OutboundRedirectGuardTests
{
    // =====================================================================================
    // 1 — GERÇEK YÖNLENDİRME
    // =====================================================================================

    /// <summary>
    /// ASIL İDDİA: doğrulamayı geçmiş bir hedef, <c>302</c> ile iç ağa atlayamaz. Yönlendirme
    /// adımının soketi AÇILMAZ ve iç servise TEK BİR İSTEK BİLE ULAŞMAZ.
    /// </summary>
    /// <remarks>
    /// MUTASYON: <c>OutboundConnectGuard.ConnectAsync</c> içindeki <c>BlockedTarget</c> dalı
    /// silinirse bu test kırmızı yanar (iç sunucu istek sayacı 1 olur).
    /// </remarks>
    [Fact]
    public async Task Yonlendirme_ic_aga_saptiginda_BAGLANTI_KURULMAZ()
    {
        using var icSunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-AGA-ULASILDI"));
        using var disSunucu = SahteHttpSunucu.Baslat(_ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic", string.Empty));

        using var istemci = new HttpClient(IlkBaglantiTemizSayilanIsleyici());

        var hata = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => istemci.GetAsync($"http://127.0.0.1:{disSunucu.Port}/disari"));

        Assert.IsType<OutboundBlockedException>(EnIcteki(hata));

        // ASIL ÖLÇÜM: iç servise HİÇBİR istek ulaşmadı. "İstisna atıldı" tek başına yetmez —
        // istek gidip yanıt okunduktan SONRA da istisna atılabilirdi.
        Assert.Equal(0, icSunucu.IstekSayisi);
        Assert.Equal(1, disSunucu.IstekSayisi);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ + AÇIK KAPI: <c>Panel:AllowInternalOutboundTargets</c> açıkken
    /// yönlendirme zinciri SONUNA KADAR izlenir.
    /// </summary>
    /// <remarks>
    /// İki şeyi birden kanıtlar: (1) düzeneğin kendisi gerçekten yönlendirme izliyor — yani
    /// yukarıdaki testin "0 istek" sonucu düzeneğin çalışmamasından değil, korumadan geliyor;
    /// (2) açık kapı SOKET DÜZEYİNDE de geçerli, yani iç ağdaki NAS'a yedek atan meşru kurulum
    /// kapıyı açtığında gerçekten çalışıyor. Bu ikincisi "bir yerde düzeltildi, öteki yol sessizce
    /// farklı davranıyor" hatasının nöbetçisidir.
    /// </remarks>
    [Fact]
    public async Task Acik_kapi_ACIKKEN_yonlendirme_zinciri_SONUNA_KADAR_izlenir()
    {
        using var icSunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-AGA-ULASILDI"));
        using var disSunucu = SahteHttpSunucu.Baslat(_ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic", string.Empty));

        using var istemci = new HttpClient(OutboundConnectGuard.CreateHandler(
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true)));

        var govde = await istemci.GetStringAsync($"http://127.0.0.1:{disSunucu.Port}/disari");

        Assert.Equal("IC-AGA-ULASILDI", govde);
        Assert.Equal(1, icSunucu.IstekSayisi);
    }

    /// <summary>
    /// AÇIĞIN KENDİSİ — DENETİMSİZ İSTEMCİDE ÖLÇÜLDÜ.
    /// </summary>
    /// <remarks>
    /// Bu test korumayı değil, korumanın olmadığı hâli ölçer: .NET'in varsayılan
    /// <c>HttpClient</c>'ı (yani panelin <c>services.AddHttpClient()</c> ile ürettiği varsayılan
    /// istemci) yönlendirmeyi izler ve iç servisin gövdesini GERÇEKTEN getirir. 0.8.7'de yedek
    /// hedefi, webhook ve göç keşfi bu istemciyi kullanıyordu; "yönlendirme açık" iddiası
    /// buradaki ölçümdür, bir tahmin değil.
    /// </remarks>
    [Fact]
    public async Task DENETIMSIZ_istemci_yonlendirmeyi_izler_ve_ic_servise_ULASIR()
    {
        using var icSunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-AGA-ULASILDI"));
        using var disSunucu = SahteHttpSunucu.Baslat(_ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic", string.Empty));

        using var istemci = new HttpClient();
        var govde = await istemci.GetStringAsync($"http://127.0.0.1:{disSunucu.Port}/disari");

        Assert.Equal("IC-AGA-ULASILDI", govde);
        Assert.Equal(1, icSunucu.IstekSayisi);
    }

    /// <summary>
    /// DNS REBINDING: kayıt anında temiz görünen ad, bağlantı anında iç adrese çözülüyorsa
    /// bağlantı KURULMAZ. Denetlenen adres ile bağlanılan adres AYNI çözümlemeden gelir.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]   // bulut meta verisi
    [InlineData("10.0.0.5")]
    [InlineData("::ffff:169.254.169.254")]
    public async Task Baglanti_aninda_ic_adrese_cozulen_ad_BAGLANMAZ(string cozulen)
    {
        var hata = await Assert.ThrowsAsync<OutboundBlockedException>(async () =>
            await OutboundConnectGuard.ConnectAsync(
                new DnsEndPoint("kotu.example", 80),
                OutboundTargetPolicy.Strict,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse(cozulen) }),
                onBlocked: null,
                CancellationToken.None));

        Assert.Equal("kotu.example", hata.Host);
    }

    /// <summary>
    /// Karışık çözümlemede HİÇBİRİNE bağlanılmaz: bir adı hem genel hem iç adrese çözmek,
    /// korumayı yazı-tura oyununa çevirirdi.
    /// </summary>
    [Fact]
    public async Task Karisik_cozumlemede_HICBIRINE_baglanilmaz()
    {
        await Assert.ThrowsAsync<OutboundBlockedException>(async () =>
            await OutboundConnectGuard.ConnectAsync(
                new DnsEndPoint("karisik.example", 80),
                OutboundTargetPolicy.Strict,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("127.0.0.1") }),
                onBlocked: null,
                CancellationToken.None));
    }

    /// <summary>
    /// Kural TEK KAYNAKTA: bağlantı-anı denetimi kendi listesini tutmaz,
    /// <see cref="OutboundAddressGuard.InspectAsync"/>'i çağırır ve o da adresleri BİR KEZ çözer.
    /// </summary>
    /// <remarks>
    /// İki çözümleme yapılsaydı denetlenen adres ile bağlanılan adres farklı olabilirdi — yani
    /// kapatmaya çalıştığımız rebinding'i denetimin İÇİNE koymuş olurduk.
    /// </remarks>
    [Fact]
    public async Task Ad_YALNIZ_BIR_KEZ_cozulur()
    {
        var sayac = 0;
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "TAMAM"));

        var isleyici = OutboundConnectGuard.CreateHandler(
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            (_, _) =>
            {
                Interlocked.Increment(ref sayac);
                return Task.FromResult(new[] { IPAddress.Loopback });
            });

        using var istemci = new HttpClient(isleyici);
        var govde = await istemci.GetStringAsync($"http://ic.ornek.test:{sunucu.Port}/");

        Assert.Equal("TAMAM", govde);
        Assert.Equal(1, sayac);
    }

    // =====================================================================================
    // 2 — FAZLA KAPATMA NÖBETÇİLERİ (panelin KENDİ sabit adresleri denetime GİRMEZ)
    // =====================================================================================

    /// <summary>
    /// Panelin kendi sabit adreslerini kullanan istemciler DENETİME GİRMEZ ve çalışmaya devam eder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapsanan yollar: <b>varsayılan istemci</b> (bileşen indirme/components.json, GitHub sürüm
    /// çözümleme, lisans doğrulama, Stalwart yönetim API'si, hata raporu),
    /// <b>AdaPanel.LocalHealth</b> (sağlık kontrolü, webmail/phpMyAdmin ayakta mı) ve
    /// <b>DbToolsProxy</b> (phpMyAdmin/Adminer vekili). Üçü de <c>127.0.0.1</c>'e konuşur — yani
    /// denetime sokulsalardı BU TEST gibi görünen gerçek bir kurulumda panel kendini
    /// güncelleyemez, phpMyAdmin açılmaz, sağlık kartı hep kırmızı yanardı.
    /// </para>
    /// <para>
    /// Bu testin kırmızı yanması "SSRF düzeltmesi fazla kapattı" demektir ve düzeltmenin kendisi
    /// açığından pahalıya gelmiş olur.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("")]                        // varsayılan istemci
    [InlineData("AdaPanel.LocalHealth")]
    [InlineData("DbToolsProxy")]
    public async Task Panelin_KENDI_istemcileri_loopback_e_ULASABILIR(string istemciAdi)
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "PANEL-KENDI-ADRESI"));
        using var saglayici = Saglayici(icAgaIzin: false);
        var fabrika = saglayici.GetRequiredService<IHttpClientFactory>();

        var istemci = istemciAdi.Length == 0 ? fabrika.CreateClient() : fabrika.CreateClient(istemciAdi);
        var govde = await istemci.GetStringAsync($"http://127.0.0.1:{sunucu.Port}/");

        Assert.Equal("PANEL-KENDI-ADRESI", govde);
    }

    /// <summary>
    /// DENETLENEN istemci aynı adrese ULAŞAMAZ. Ayrımın gerçekten kurulduğunun kanıtı: aynı
    /// süreçte, aynı hedefe, iki farklı istemci iki farklı sonuç verir.
    /// </summary>
    [Fact]
    public async Task Denetlenen_istemci_loopback_e_ULASAMAZ()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "ULASILMAMALI"));
        using var saglayici = Saglayici(icAgaIzin: false);
        var istemci = saglayici.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OutboundConnectGuard.GuardedClientName);

        var hata = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => istemci.GetAsync($"http://127.0.0.1:{sunucu.Port}/"));

        Assert.IsType<OutboundBlockedException>(EnIcteki(hata));
        Assert.Equal(0, sunucu.IstekSayisi);
    }

    /// <summary>
    /// AÇIK KAPI DI ÜZERİNDEN: <c>Panel:AllowInternalOutboundTargets=true</c> yapılandırması
    /// denetlenen istemcide de GEÇERLİDİR.
    /// </summary>
    /// <remarks>
    /// Bu, görevin dördüncü maddesinin doğrudan ölçümü: bayrak yalnız doğrulama kapısında
    /// çalışsaydı, iç ağdaki NAS'a yedek atan kurulum kapıyı AÇTIĞI HÂLDE soket düzeyinde
    /// reddedilirdi.
    /// </remarks>
    [Fact]
    public async Task Acik_kapi_yapilandirmasi_DENETLENEN_istemcide_de_gecerli()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-AG-HEDEFI"));
        using var saglayici = Saglayici(icAgaIzin: true);
        var istemci = saglayici.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OutboundConnectGuard.GuardedClientName);

        var govde = await istemci.GetStringAsync($"http://127.0.0.1:{sunucu.Port}/");

        Assert.Equal("IC-AG-HEDEFI", govde);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ — TLS: denetlenen istemci HTTPS el sıkışmasını TAMAMLAR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu test şart:</b> <c>ConnectCallback</c> soketi ARTIK BİZ açıyoruz ve düz bir
    /// <c>NetworkStream</c> döndürüyoruz; TLS'i .NET'in bunun ÜSTÜNE kurması gerekir. Kurmasaydı
    /// panelin bütün bulut yedek yüklemeleri (S3, Azure, Dropbox, OneDrive, WebDAV — hepsi HTTPS)
    /// sessizce kırılırdı. Yani bu, "SSRF düzeltmesi yedeklemeyi bozdu" senaryosunun nöbetçisidir.
    /// </para>
    /// <para>
    /// Sertifika testin kendi ürettiği kendinden imzalı sertifikadır ve bağlantı loopback'tedir;
    /// hiçbir dış sunucuya gidilmez. Testin verdiği doğrulayıcı bu sertifikayı kabul eder çünkü
    /// ölçülen şey sertifikanın geçerliliği değil, EL SIKIŞMANIN kurulabilmesidir. (Doğrulamayı
    /// kapatan eski <c>verifyTlsCertificate: false</c> boolesi KALDIRILDI; artık bir karar verici
    /// verilmezse .NET'in tam doğrulaması çalışır — gerekçe <c>OutboundConnectGuard</c>'da.)
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Denetlenen_istemci_HTTPS_el_sikismasini_TAMAMLAR()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "TLS-TAMAM"), sertifika);
        using var istemci = new HttpClient(OutboundConnectGuard.CreateHandler(
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            certificateValidator: (_, _, _, _) => true));

        var govde = await istemci.GetStringAsync($"https://127.0.0.1:{sunucu.Port}/");

        Assert.Equal("TLS-TAMAM", govde);
    }

    // =====================================================================================
    // 3 — ÇAĞRI YERLERİ
    // =====================================================================================

    /// <summary>
    /// GÖÇ KEŞFİ (Plesk) DENETİMSİZDİ: <c>request.Host</c> gövdeden geliyordu, kendi
    /// <c>HttpClientHandler</c>'ını kuruyordu ve hiçbir adres kapısından geçmiyordu — üstelik
    /// istek kullanıcının API anahtarını taşır.
    /// </summary>
    [Fact]
    public async Task Plesk_kesfi_ic_adrese_cozulen_konagi_REDDEDER()
    {
        var servis = GocServisi(icAgaIzin: false, ("plesk.example", "169.254.169.254"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DiscoverPleskAsync(
            new PleskDiscoverRequest { Host = "plesk.example", ApiKey = "x" },
            CancellationToken.None));

        Assert.Equal("migration.host_forbidden", hata.Message);
    }

    /// <summary>cPanel/WHM keşfi de aynı kapıdan geçer (iki dal, tek kural).</summary>
    [Fact]
    public async Task Cpanel_kesfi_ic_adrese_cozulen_konagi_REDDEDER()
    {
        var servis = GocServisi(icAgaIzin: false, ("whm.example", "10.0.0.5"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DiscoverCpanelAsync(
            new CpanelDiscoverRequest { Host = "whm.example", ApiToken = "x" },
            CancellationToken.None));

        Assert.Equal("migration.host_forbidden", hata.Message);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: açık kapı açıkken iç ağdaki bir Plesk sunucusundan göç YİNE
    /// mümkün olmalı — reddedilme adres yüzünden DEĞİL, sunucu orada olmadığı için gelmeli.
    /// </summary>
    [Fact]
    public async Task Acik_kapi_ACIKKEN_ic_agdaki_Plesk_adres_yuzunden_reddedilmez()
    {
        var kapaliPort = KapaliPortBul();
        var servis = GocServisi(icAgaIzin: true);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DiscoverPleskAsync(
            new PleskDiscoverRequest { Host = "127.0.0.1", Port = kapaliPort, ApiKey = "x" },
            CancellationToken.None));

        // Adres kapısı DEĞİL, bağlantı hatası: kapı açıkken iç ağ hedefine gerçekten gidiliyor.
        Assert.Equal("migration.plesk.connect_failed", hata.Message);
    }

    /// <summary>
    /// GÖÇ ÖN KONTROLÜ BİR PORT TARAYICISIYDI: <c>FileHost</c>/<c>DbHost</c> + port gövdeden
    /// geliyor, panel TCP açıp sonucu "ulaşıldı/ulaşılamadı" diye DÖNÜYORDU — yani iç ağ ve
    /// loopback portları tek tek ayırt edilebiliyordu. Artık adres kapısından geçiyor.
    /// </summary>
    /// <remarks>
    /// Bu testte hiçbir sokete dokunulmaz: kapı, bağlantı denenmeden önce kararı verir.
    /// MUTASYON: <c>UlasimKontroluAsync</c> içindeki kapı kaldırılırsa sonuç mesajı
    /// <c>*_unreachable</c>'a döner ve test kırmızı yanar.
    /// </remarks>
    [Fact]
    public async Task Goc_on_kontrolu_ic_adresli_konagi_TARAMAZ()
    {
        var servis = GocServisi(icAgaIzin: false);

        var sonuc = await servis.ValidateMigrationAsync(
            new MigrateSiteRequest
            {
                SiteName = "ornek.test",
                PullFiles = true,
                FileHost = "127.0.0.1",
                FilePort = 22,
                TransferDatabase = true,
                DbHost = "169.254.169.254",
                DbPort = 3306
            },
            CancellationToken.None);

        var kaynakKontrolleri = sonuc.Checks.Where(kontrol => kontrol.Name is "file_source" or "db_source").ToArray();
        Assert.Equal(3, sonuc.Checks.Count);
        Assert.Equal(2, kaynakKontrolleri.Length);
        Assert.Equal(new[] { "file_source", "db_source" }, kaynakKontrolleri.Select(kontrol => kontrol.Name));
        Assert.False(sonuc.AllPassed);
        Assert.All(
            kaynakKontrolleri,
            kontrol =>
            {
                Assert.False(kontrol.Ok);
                Assert.Equal("migration.validate.host_forbidden", kontrol.Message);
            });
    }

    /// <summary>
    /// WEBHOOK GÖNDERİMİ: adres kaydedilirken temizdi, gönderme anında iç ağa çözülüyor →
    /// istek ATILMAZ ve kullanıcı "ağ hatası" değil, adres yasağını görür.
    /// </summary>
    [Fact]
    public async Task Webhook_gonderiminde_ic_aga_cozulen_adres_REDDEDILIR()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "ULASILMAMALI"));
        var kok = GeciciKok();
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
        var depo = new JsonFileStore(secenekler);

        // Ayar DOSYAYA doğrudan yazılıyor: kaydetme kapısı zaten ölçülü (OutboundTargetGuardTests),
        // burada ölçülen şey GÖNDERME anındaki bağlantı denetimi.
        await depo.WriteAsync(
            "config/notifications.json",
            new NotificationSettingsDto { WebhookUrl = $"http://127.0.0.1:{sunucu.Port}/kanca" },
            CancellationToken.None);

        var servis = new NotificationService(
            depo,
            new DenetlenenIstemciFabrikasi(),
            new SahteKoruyucu(),
            secenekler,
            (_, _) => throw new InvalidOperationException("DNS beklenmiyordu"));

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.SendWebhookAsync("adapanel.test", "govde", CancellationToken.None));

        Assert.Equal("notifications.webhook_forbidden", hata.Message);
        Assert.Equal(0, sunucu.IstekSayisi);
    }

    // =====================================================================================
    // 4 — ZAMANLANMIŞ GÖREV (url tipi)
    //
    // ÖLÇÜLEN AÇIK VE KAPANIŞI. Görev eskiden şu komutla kuruluyordu:
    //
    //   curl.exe -fsSL --proto "=http,https" --proto-redir "=http,https" --max-redirs 5
    //            --max-time 300 --retry 2 "<adres>"
    //
    // Bu makinede curl 8.19.0 ve gerçek bir loopback sunucusuyla ÖLÇÜLDÜ: yukarıdaki BİREBİR
    // argümanlarla çağrılan curl, "302 Location:" ile gösterilen İÇ adrese gitti ve iç gövdeyi
    // çıktı olarak yazdı — çıkış kodu 0. Yani adres denetimi (görev KURULURKEN yapılıyordu)
    // yalnız ilk adresi görüyor, yönlendirme onun arkasından geçiyordu. Aynı ölçümde:
    //   * "-L" atmak ÇARE DEĞİL: 302 alan curl çıkış kodu 0 + BOŞ çıktı veriyor, yani meşru
    //     http→https / www yönlendirmeli her cron sessizce çalışmaz hâle gelirdi.
    //   * "--proto-redir =http,https" file:// yönlendirmesini GERÇEKTEN kesiyor (ölçüldü) ama
    //     http→http ile iç ağa sapmaya hiçbir etkisi yok.
    // curl'de adres engel listesi YOKTUR; bu yüzden görev artık UrlTaskRunner ile koşuyor.
    // =====================================================================================

    /// <summary>Testlerin sabit bir panel yürütülebiliri kullanması için (üretimde kendi yolunu bulur).</summary>
    private const string SahtePanelYolu = @"C:\Program Files\AdaPanel\AdaPanel.Api.exe";

    /// <summary>
    /// KURULAN KOMUT: artık <c>curl.exe</c> değil, panelin DENETLENEN yürütücüsü.
    /// </summary>
    /// <remarks>
    /// Kaynak taraması değil davranış ölçümü: komut <c>ResolveAction</c> tarafından GERÇEKTEN
    /// kurulur ve burada okunur. Adresin TIRNAK İÇİNDE ve EN SONDA olması sözleşmedir — görev
    /// listesi URL'yi son tırnaklı parçadan çıkarır (bir alttaki nöbetçi bunu ölçer).
    /// </remarks>
    [Fact]
    public void Url_gorevi_artik_curl_DEGIL_panelin_denetlenen_yurutucusuyle_kurulur()
    {
        var (calistir, argumanlar, _) = SchedulerService.ResolveActionForTests(
            new CreateScheduledTaskRequest
            {
                Name = "test",
                TaskType = "url",
                Url = "https://ornek.test/cron"
            },
            SahtePanelYolu);

        Assert.Equal(SahtePanelYolu, calistir);
        Assert.Equal("--cron-url \"https://ornek.test/cron\"", argumanlar);
        Assert.DoesNotContain("curl", calistir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: yeni komut liste/düzenleme ekranını bozmamalı. Panel görev tipini
    /// ve URL'yi KAYITLI EYLEMDEN türetir (<c>GetTasksAsync</c>: argümanlardaki SON tırnaklı parça
    /// URL sayılır); komut biçimi bu çıkarımı bozsaydı URL sütunu boşalır, düzenleme ekranı yanlış
    /// dolar ve kullanıcı kaydettiğinde görev ADRESİNİ KAYBEDERDİ.
    /// </summary>
    [Fact]
    public void Yeni_komut_gorev_listesindeki_URL_cikarimini_BOZMAZ()
    {
        const string adres = "https://ornek.test/cron?a=1";
        var (calistir, argumanlar, _) = SchedulerService.ResolveActionForTests(
            new CreateScheduledTaskRequest { Name = "test", TaskType = "url", Url = adres },
            SahtePanelYolu);

        // Çıktı yakalama sarmalı + geri çözme (kayıtta görev böyle durabiliyor).
        var sarmalanmis = SchedulerService.BuildLoggedArguments(calistir, argumanlar, @"C:\gunluk\test.log");
        Assert.True(SchedulerService.TryUnwrapLoggedAction("cmd.exe", sarmalanmis, out var gercekKomut, out var gercekArgumanlar));
        Assert.Equal(SahtePanelYolu, gercekKomut);

        // Tip türetmesi hâlâ "url" demeli, yoksa görev "command" tipine düşerdi.
        Assert.True(UrlTaskRunner.IsUrlAction(gercekKomut, gercekArgumanlar));

        var tirnakli = System.Text.RegularExpressions.Regex.Matches(gercekArgumanlar, "\"([^\"]+)\"");
        Assert.Equal(adres, tirnakli[^1].Groups[1].Value);
    }

    /// <summary>
    /// GERİYE UYUMLULUK NÖBETÇİSİ: güncellemeden ÖNCE kurulmuş <c>curl.exe</c> görevleri hâlâ
    /// <c>url</c> tipinde tanınır.
    /// </summary>
    /// <remarks>
    /// Tanınmasaydı o görevler listede "command" tipine düşer, URL sütunu boşalır ve kullanıcı
    /// düzenleyip kaydettiğinde görev ADRESİNİ KAYBEDERDİ — yani bir güvenlik düzeltmesi,
    /// kullanıcının verisini yok eden bir hataya dönüşürdü.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Windows\System32\curl.exe", "-fsSL \"https://ornek.test/cron\"", true)]
    [InlineData("curl.exe", "-fsSL \"https://ornek.test/cron\"", true)]
    [InlineData(SahtePanelYolu, "--cron-url \"https://ornek.test/cron\"", true)]
    // Panel yürütülebiliri AMA cron fiili YOK: bu bir url görevi değildir (örn. servis kaydı).
    [InlineData(SahtePanelYolu, "--urls https://127.0.0.1:8890", false)]
    [InlineData(@"C:\site\bot.bat", "", false)]
    public void Eski_ve_yeni_url_gorevleri_TANINIR_digerleri_TANINMAZ(string komut, string argumanlar, bool beklenen)
        => Assert.Equal(beklenen, UrlTaskRunner.IsUrlAction(komut, argumanlar));

    /// <summary>
    /// ASIL İDDİA: <c>url</c> görevi, <c>302</c> ile iç ağa atlayamaz. Yönlendirme adımının soketi
    /// AÇILMAZ ve iç servise TEK BİR İSTEK BİLE ULAŞMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapatılan açık tam olarak budur: curl aynı düzenekte iç gövdeyi getiriyordu (çıkış kodu 0).
    /// Birinci bağlantının "temiz dış konak" sayılması dosya başındaki gerekçeyle aynı — bu
    /// makinede bağlanılabilir HİÇBİR adres engel listesinin dışında değildir. Denetlenen gövde
    /// üretimdeki gövdenin ta kendisidir.
    /// </para>
    /// <para>
    /// MUTASYON: <c>OutboundConnectGuard.ConnectAsync</c> içindeki <c>BlockedTarget</c> dalı ya da
    /// <c>CreateHandler</c>'ın <c>ConnectCallback</c> ataması silinirse bu test kırmızı yanar
    /// (iç sunucu istek sayacı 1 olur ve çıkış kodu 0'a döner).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Url_gorevi_yonlendirmeyle_ic_aga_SAPAMAZ()
    {
        using var icSunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-AGA-ULASILDI"));
        using var disSunucu = SahteHttpSunucu.Baslat(_ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic", string.Empty));

        var baglantiSayisi = 0;
        var cikti = new StringWriter();
        var hata = new StringWriter();

        var kod = await UrlTaskRunner.RunAsync(
            $"http://127.0.0.1:{disSunucu.Port}/disari",
            () => Interlocked.Increment(ref baglantiSayisi) == 1
                ? OutboundTargetPolicy.Connecting(allowInternalTargets: true)
                : OutboundTargetPolicy.Strict,
            cikti,
            hata);

        Assert.Equal(UrlTaskRunner.AdresYasakKod, kod);

        // ASIL ÖLÇÜM: iç servise HİÇBİR istek ulaşmadı ve gövdesi çıktıya YAZILMADI.
        Assert.Equal(0, icSunucu.IstekSayisi);
        Assert.Equal(1, disSunucu.IstekSayisi);
        Assert.DoesNotContain("IC-AGA-ULASILDI", cikti.ToString(), StringComparison.Ordinal);

        // YENİDEN DENENMEDİ: yasak kalıcı bir karardır, üç kez aynı reddi yazmak teşhisi bozardı.
        Assert.Equal(2, baglantiSayisi);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: MEŞRU bir webhook/ping görevi ÇALIŞMAYA DEVAM EDER — yönlendirme
    /// zinciri sonuna kadar izlenir ve gövde çıktıya yazılır.
    /// </summary>
    /// <remarks>
    /// İki şeyi birden kanıtlar: (1) düzenek gerçekten yönlendirme izliyor, yani üstteki testin
    /// "0 istek" sonucu düzeneğin çalışmamasından değil KORUMADAN geliyor; (2) yönlendirmeyi
    /// kapatmadık — <c>http→https</c> ve <c>www</c> yönlendirmesi yapan yaygın servisler (ki
    /// cron adreslerinin çoğu öyledir) kırılmadı. curl'de bunu <c>-L</c> sağlıyordu; onu atmak
    /// bu testi kırardı.
    /// </remarks>
    [Fact]
    public async Task Mesru_url_gorevi_yonlendirmeyi_SONUNA_KADAR_izler_ve_govdeyi_YAZAR()
    {
        using var icSunucu = SahteHttpSunucu.Baslat(_ => (200, null, "GOREV-CALISTI"));
        using var disSunucu = SahteHttpSunucu.Baslat(_ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic", string.Empty));

        var cikti = new StringWriter();
        var hata = new StringWriter();

        var kod = await UrlTaskRunner.RunAsync(
            $"http://127.0.0.1:{disSunucu.Port}/disari",
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            cikti,
            hata);

        Assert.Equal(UrlTaskRunner.BasariliKod, kod);
        Assert.Equal("GOREV-CALISTI", cikti.ToString());
        Assert.Equal(1, icSunucu.IstekSayisi);
    }

    /// <summary>
    /// DNS REBINDING KAPANIŞI: denetim artık ÇALIŞMA ANINDA yapılıyor, yalnız görev kurulurken değil.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eski yolda adres bir kez (kurulumda) denetleniyor, curl ise HER ÇALIŞMADA adı yeniden
    /// çözüyordu; saldırgan görevi meşru bir adresle kurup A kaydını sonradan <c>127.0.0.1</c>
    /// yapabilirdi. Bu test o pencereyi ölçer: kurulumdan geçmiş sayılan bir görev bile, çalışma
    /// anında iç bir adrese bakıyorsa SOKET AÇILMADAN reddedilir.
    /// </para>
    /// <para>
    /// MUTASYON: <c>UrlTaskRunner.RunAsync</c> denetlenen işleyici yerine düz bir
    /// <c>HttpClientHandler</c> kullanırsa bu test kırmızı yanar (sunucu 1 istek alır, kod 0 olur).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Url_gorevi_CALISMA_ANINDA_denetlenir_soket_hic_acilmaz()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (200, null, "IC-SERVIS"));

        var cikti = new StringWriter();
        var hata = new StringWriter();

        var kod = await UrlTaskRunner.RunAsync(
            $"http://127.0.0.1:{sunucu.Port}/ping",
            () => OutboundTargetPolicy.Strict,
            cikti,
            hata);

        Assert.Equal(UrlTaskRunner.AdresYasakKod, kod);
        Assert.Equal(0, sunucu.IstekSayisi);
        Assert.Equal(string.Empty, cikti.ToString());
        Assert.Contains("YASAK", hata.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>curl -f</c> KARŞILIĞI: HTTP 400+ başarısızlıktır (Task Scheduler "son sonuç"a yansır) ve
    /// hata gövdesi çıktıya YAZILMAZ.
    /// </summary>
    /// <remarks>
    /// Nöbetçi olarak da duruyor: kod curl'ün 22'sini korur. Kullanıcıların görev izleme betikleri
    /// bugün curl kodlarına bakıyor; açığı kapatırken izlemeyi sessizce bozmak istemedik.
    /// </remarks>
    [Fact]
    public async Task Url_gorevi_HTTP_hatasinda_curl_ile_AYNI_kodu_verir()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (404, null, "BULUNAMADI-GOVDE"));

        var cikti = new StringWriter();
        var hata = new StringWriter();

        var kod = await UrlTaskRunner.RunAsync(
            $"http://127.0.0.1:{sunucu.Port}/yok",
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            cikti,
            hata);

        Assert.Equal(UrlTaskRunner.HttpHataKod, kod);
        Assert.Equal(string.Empty, cikti.ToString());
        Assert.Contains("404", hata.ToString(), StringComparison.Ordinal);

        // 404 GEÇİCİ DEĞİLDİR: yeniden denenmedi (denenseydi sunucu 3 istek görürdü).
        Assert.Equal(1, sunucu.IstekSayisi);
    }

    /// <summary>
    /// <c>curl --proto "=http,https"</c> KARŞILIĞI: http/https dışı şema hiç denenmez.
    /// </summary>
    /// <remarks>
    /// Klasik SSRF yükseltmeleri (<c>file://</c>, <c>gopher://</c>, <c>dict://</c>) burada
    /// yapı gereği imkânsızdır — <c>HttpClient</c> bu şemaları konuşmaz — ama karar AÇIKÇA
    /// yazılıyor ki bir sonraki okuyucu "acaba açık mı" diye sormasın.
    /// </remarks>
    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("gopher://127.0.0.1:70/x")]
    [InlineData("dict://127.0.0.1:2628/d")]
    [InlineData("bu bir adres degil")]
    public async Task Url_gorevi_http_https_DISINDAKI_semayi_reddeder(string adres)
    {
        var cikti = new StringWriter();
        var hata = new StringWriter();

        var kod = await UrlTaskRunner.RunAsync(
            adres,
            () => OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            cikti,
            hata);

        Assert.Equal(UrlTaskRunner.GecersizAdresKod, kod);
        Assert.Equal(string.Empty, cikti.ToString());
    }

    // =====================================================================================
    // 5 — KARAR GÖVDESİNİN KENDİSİ
    // =====================================================================================

    /// <summary>
    /// <see cref="OutboundAddressGuard.InspectAsync"/> hem kararı hem ADRESLERİ döner; açık kapı
    /// açıkken karar "temiz"dir ama adresler yine gelir (soket açılabilsin diye).
    /// </summary>
    [Fact]
    public async Task Acik_kapi_ACIKKEN_inceleme_adresleri_dondurur_ve_engellemez()
    {
        var inceleme = await OutboundAddressGuard.InspectAsync(
            "nas.ornek.test",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") }),
            OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            CancellationToken.None);

        Assert.Null(inceleme.BlockedTarget);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), Assert.Single(inceleme.Addresses));
    }

    /// <summary>Açık kapı KAPALIYKEN aynı ad engellenir ve bağlanacak adres verilmez.</summary>
    [Fact]
    public async Task Acik_kapi_KAPALIYKEN_ic_adres_engellenir()
    {
        var inceleme = await OutboundAddressGuard.InspectAsync(
            "nas.ornek.test",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") }),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Equal("192.168.1.50", inceleme.BlockedTarget);
    }

    /// <summary>
    /// Çözülemeyen ad: "yasak" ile "çözülemedi" AYRI olaylardır. Bağlantı yolunda ikisi de
    /// bağlanmamakla sonuçlanır ama hata metni farklıdır — teşhis buna bağlı.
    /// </summary>
    [Fact]
    public async Task Cozulemeyen_ad_YASAK_degil_COZUMLEME_hatasi_verir()
    {
        var hata = await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await OutboundConnectGuard.ConnectAsync(
                new DnsEndPoint("yok.example", 80),
                new OutboundTargetPolicy(AllowInternalTargets: false, BlockUnresolvedHost: false),
                (_, _) => throw new SocketException(11001),
                onBlocked: null,
                CancellationToken.None));

        Assert.IsNotType<OutboundBlockedException>(hata);
    }

    // =====================================================================================
    // YARDIMCILAR
    // =====================================================================================

    /// <summary>
    /// İLK bağlantıyı "temiz dış konak" sayan, sonrakileri denetleyen işleyici.
    /// Gerekçesi sınıf açıklamasında yazılı.
    /// </summary>
    private static SocketsHttpHandler IlkBaglantiTemizSayilanIsleyici()
    {
        var baglantiSayisi = 0;
        return OutboundConnectGuard.CreateHandler(() =>
            Interlocked.Increment(ref baglantiSayisi) == 1
                ? OutboundTargetPolicy.Connecting(allowInternalTargets: true)
                : OutboundTargetPolicy.Strict);
    }

    private static Exception EnIcteki(Exception hata)
    {
        var mevcut = hata;
        while (mevcut.InnerException is not null)
        {
            mevcut = mevcut.InnerException;
        }

        return mevcut;
    }

    private static string GeciciKok()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-yonlendirme-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        return kok;
    }

    /// <summary>
    /// Testin KENDİ ürettiği kendinden imzalı sertifika. Makinedeki sertifika deposuna hiçbir şey
    /// yazılmaz; PFX'e çevirme adımı Windows'ta <c>SslStream</c>'in geçici anahtarı kabul etmesi
    /// için gereklidir.
    /// </summary>
    private static X509Certificate2 KendindenImzaliSertifika()
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var adlar = new SubjectAlternativeNameBuilder();
        adlar.AddIpAddress(IPAddress.Loopback);
        istek.CertificateExtensions.Add(adlar.Build());

        using var uretilen = istek.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        return new X509Certificate2(uretilen.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static int KapaliPortBul()
    {
        var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        var port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
        dinleyici.Stop();
        return port;
    }

    private static SiteMigrationService GocServisi(bool icAgaIzin, params (string Ad, string Adres)[] dns)
        => new(
            null!,
            null!,
            null!,
            DispatchProxy.Create<IIisSiteService, MissingMigrationSite>(),
            NullLogger<SiteMigrationService>.Instance,
            userIniScanEntryBudget: null,
            runtimeOptions: Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
            {
                DataRoot = GeciciKok(),
                AllowInternalOutboundTargets = icAgaIzin
            }),
            outboundResolver: (host, _) =>
            {
                var adresler = dns
                    .Where(kayit => string.Equals(kayit.Ad, host, StringComparison.OrdinalIgnoreCase))
                    .Select(kayit => IPAddress.Parse(kayit.Adres))
                    .ToArray();

                return adresler.Length == 0
                    ? throw new SocketException(11001)
                    : Task.FromResult(adresler);
            });

    public class MissingMigrationSite : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IIisSiteService.GetSiteAsync))
                throw new AppException("sites.site_not_found", 404, 404);
            throw new InvalidOperationException("Unexpected migration IIS dependency: " + targetMethod?.Name);
        }
    }

    /// <summary>
    /// Panelin GERÇEK DI kurulumu. Nöbetçi testlerin değeri buradan gelir: istemciler testin
    /// kurduğu bir düzenekten değil, üretimdeki kayıttan geliyor.
    /// </summary>
    private static ServiceProvider Saglayici(bool icAgaIzin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Panel:DataRoot"] = GeciciKok(),
                ["Panel:AllowInternalOutboundTargets"] = icAgaIzin ? "true" : "false",
                ["ConnectionStrings:Default"] = "Data Source=:memory:"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        services.AddDbContext<AdaPanelDbContext>(options => options.UseSqlite(connection));
        services.AddAdaPanelInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>Yalnız DENETLENEN işleyiciyi üreten sahte fabrika (webhook testi için).</summary>
    private sealed class DenetlenenIstemciFabrikasi : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(OutboundConnectGuard.CreateHandler(() => OutboundTargetPolicy.Strict));
    }

    // SahteHttpSunucu ARTIK BU DOSYADA DEĞİL (tests/.../SahteHttpSunucu.cs): aynı sunucu git
    // dağıtımı ve arşiv içe aktarma ölçümlerinde de kullanılıyor; ikinci bir nüsha üretmemek için
    // tek kaynağa çıkarıldı.
}
