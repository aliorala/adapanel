using System.Reflection;
using System.Threading.RateLimiting;
using System.Text.Json;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ISTEK KATMANI: kurallar KAGIT UZERINDE degil, BORU HATTINA BAGLI mi.
/// </summary>
/// <remarks>
/// Bu depoda tekrar tekrar olculen hata sinifi "kod var ama o yolda calismiyor" oldu (.htaccess
/// modulu uc surum olu yayinlandi, K5 sertlestirmesi ancak ikinci acilista uygulaniyordu).
/// Asagidaki testler degerleri degil BAGLANTIYI olcer.
/// </remarks>
public sealed class PanelRequestSurfaceWiringTests
{
    // -------------------------------------------------------------------------------------
    // 1) SIRALAMA — 429 yanitlari guvenlik basliklarini KAYBETMEMELI
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Guvenlik basliklari ara katmani hiz sinirlayicidan ONCE kayitli olmali.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.8.7'de tam bu duzeltildi: <c>UseRateLimiter</c> daha ONCE kayitliydi ve bir istegi
    /// reddettiginde zinciri KISA DEVRE ediyordu — yani her 429, CSP/nosniff/X-Frame-Options/
    /// Referrer-Policy OLMADAN gidiyordu. Hiz sinirlari GENISLETILDIGI icin (artik cerez yolu da
    /// 429 alabiliyor) bu delik eskisinden cok daha sik acilirdi; siranin testle sabitlenmesi
    /// bu yuzden duzeltmenin parcasi.
    /// </para>
    /// <para>
    /// Olcum METIN SIRASI uzerinden: ara katman kayitlari <c>Program.cs</c>'te ust duzey
    /// deyimlerdir ve yazildiklari SIRAYLA calisirlar; yani dosyadaki konum, calisma sirasinin
    /// ta kendisidir.
    /// </para>
    /// </remarks>
    [Fact]
    public void Guvenlik_basliklari_hiz_sinirlayicidan_ONCE_kayitli()
    {
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        var basliklar = kaynak.IndexOf("PanelSecurityHeaders.Apply(context)", StringComparison.Ordinal);
        var sinirlayici = kaynak.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal);

        Assert.True(basliklar > 0, "Guvenlik basligi ara katmani bulunamadi.");
        Assert.True(sinirlayici > 0, "UseRateLimiter bulunamadi.");
        Assert.True(
            basliklar < sinirlayici,
            "UseRateLimiter, guvenlik basliklarindan ONCE kayitli: 429 yanitlari basliksiz gider.");
    }

    // -------------------------------------------------------------------------------------
    // 2) HIZ SINIRI KABLOLAMASI
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Hiz_sinirlari_TEK_KAYNAKTAN_kuruluyor()
    {
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        Assert.Contains("AddRateLimiter(PanelRateLimits.Configure(", kaynak, StringComparison.Ordinal);
        // Cerez yolu artik "sinirsiz" DEGIL. Bu satir bir zamanlar bir TESTLE "beklenen davranis"
        // diye civilenmisti; kaldirilmasi duzeltmenin cekirdegi.
        Assert.DoesNotContain("GetNoLimiter(", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Sinir_DEGERLERI_Program_cs_e_sizmamis()
    {
        // TEK KAYNAK NOBETCISI: bir deger hem burada hem PanelRateLimits'te dursaydi, biri
        // guncellenip oteki kalirdi. Program.cs artik hicbir sinir sayisi tasimamali.
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        Assert.DoesNotContain("PermitLimit", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedWindowRateLimiterOptions", kaynak, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // 3) KESTREL TAVANLARI
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Kestrel_tavanlari_SERTIFIKADAN_BAGIMSIZ_kuruluyor()
    {
        // OLCULEN TUZAK: ConfigureKestrel yalnizca sertifika VARKEN cagriliyordu. Tavanlar o
        // blogun icine konsaydi, sertifikasiz (HTTP yedegi) kurulumlar — yani en savunmasiz
        // olanlar — sinirsiz kalirdi.
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");

        var tavanlar = kaynak.IndexOf("PanelServerLimits.Apply(options.Limits)", StringComparison.Ordinal);
        var sertifikaKosulu = kaynak.IndexOf("if (panelCertificate is not null)", StringComparison.Ordinal);

        Assert.True(tavanlar > 0, "Kestrel tavanlari hic kurulmuyor.");
        Assert.True(sertifikaKosulu > 0, "Sertifika kosulu bulunamadi (test bayatlamis olabilir).");
        Assert.True(
            tavanlar < sertifikaKosulu,
            "Tavanlar sertifika kosulunun ICINDE: sertifikasiz kurulum sinirsiz kalir.");
    }

    [Fact]
    public void Es_zamanli_baglanti_tavani_GERCEKTEN_uygulaniyor()
    {
        // Kestrel'in varsayilani null = SINIRSIZ. Tek gercek bosluk buydu: baglanti acip hic
        // istek gondermeyen bir istemci kumesi hiz sinirlayicinin GORMEDIGI bir tuketimdir.
        var limits = new KestrelServerLimits();
        Assert.Null(limits.MaxConcurrentConnections);

        PanelServerLimits.Apply(limits);

        Assert.Equal(PanelServerLimits.MaxConcurrentConnections, limits.MaxConcurrentConnections);
        Assert.Equal(PanelServerLimits.MaxConcurrentUpgradedConnections, limits.MaxConcurrentUpgradedConnections);
        Assert.Equal(PanelServerLimits.MaxRequestBodyBytes, limits.MaxRequestBodySize);
        Assert.Equal(PanelServerLimits.KeepAliveTimeout, limits.KeepAliveTimeout);
        Assert.Equal(PanelServerLimits.RequestHeadersTimeout, limits.RequestHeadersTimeout);
    }

    [Fact]
    public void UZUN_isler_kirilmiyor_sure_siniri_YOK()
    {
        // FAZLA KAPATMA NOBETCISI. Yedek indirme, bilesen kurulumu ve 1 GiB'lik SQL ice aktarimi
        // saatlerce surebilir. "Istek 30 dakikayi gecerse dussun" gibi bir kural tam da bu
        // panelin en uzun MESRU islerini keserdi; Kestrel'in olcutu SURE degil HIZ olmali.
        var limits = new KestrelServerLimits();
        PanelServerLimits.Apply(limits);

        Assert.NotNull(limits.MinRequestBodyDataRate);
        Assert.NotNull(limits.MinResponseDataRate);
        // Varsayilan hiz esigi (240 B/sn) korunuyor: yavas bir hat degil, HIC veri gondermeyen
        // baglanti kesilsin.
        Assert.Equal(240, limits.MinRequestBodyDataRate!.BytesPerSecond);
    }

    [Fact]
    public void Form_ayristiricisinin_tavani_YUKLEME_ucuyla_HIZALI()
    {
        // OLCULEN GERCEK KOPUKLUK: FilesController.UploadAsync 200 MiB'a izin veriyor ama
        // yukleme IFormFile ile, yani cok parcali form olarak ayristiriliyor ve o ayristiricinin
        // AYRI tavani vardi (varsayilan 128 MiB). Aradaki her yukleme, uc "izin veriyor"
        // gorundugu halde InvalidDataException ile oluyordu.
        var form = new FormOptions();
        Assert.Equal(134_217_728, form.MultipartBodyLengthLimit); // cerceve varsayilani

        PanelServerLimits.Apply(form);

        Assert.Equal(PanelServerLimits.FileUploadMaxBytes, form.MultipartBodyLengthLimit);
        Assert.True(
            form.MultipartBodyLengthLimit >= UcunGovdeTavani("FilesController", "UploadAsync"),
            "Form ayristiricisi, yukleme ucunun izin verdiginden AZ kabul ediyor.");
    }

    [Fact]
    public void Form_tavani_Program_cs_te_BAGLI()
    {
        var kaynak = Oku("src", "AdaPanel.Api", "Program.cs");
        Assert.Contains("FormOptions>(PanelServerLimits.Apply)", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Genel_govde_tavanini_ASMASI_gereken_uclar_kendi_iznini_TASIYOR()
    {
        // FAZLA KAPATMA NOBETCISI: genel 30 MB tavani yazildi. Bunu asmasi gereken iki yol var
        // ve ikisi de kendi [RequestSizeLimit] ozniteligiyle sunucu tavanini istek basina EZER.
        // Oznitelik dusseydi, 1 GiB'lik phpMyAdmin ice aktarimi ve 200 MiB'lik dosya yuklemesi
        // sessizce 413 alirdi.
        Assert.True(UcunGovdeTavani("FilesController", "UploadAsync") > PanelServerLimits.MaxRequestBodyBytes);
        Assert.True(UcunGovdeTavani("DbToolsProxyController", "ProxyAsync") > PanelServerLimits.MaxRequestBodyBytes);
    }

    [Fact]
    public void Kimliksiz_formlarin_govdesi_DAR()
    {
        // Hiz siniri ISTEK SAYISINI sinirlar, BAYT sayisini degil: izin verilen her istek
        // genel tavan kadar (30 MB) govde tasiyabilirdi ve [FromForm] onu okumak zorundadir.
        foreach (var (tip, eylem) in new[]
                 {
                     ("MailListPublicController", "RequestAsync"),
                     ("MailListPublicController", "ConfirmAsync"),
                     ("SiteBuilderPublicController", "SubmitContactAsync")
                 })
        {
            Assert.Equal(PanelServerLimits.PublicFormMaxBytes, UcunGovdeTavani(tip, eylem));
        }
    }

    [Fact]
    public void Kimliksiz_form_tavani_MESRU_en_uzun_gonderimi_kesmiyor()
    {
        // FAZLA KAPATMA NOBETCISI: en uzun mesru alan iletisim formunun mesajidir.
        // UTF-8'de en kotu hal karakter basina 4 bayt.
        var enKotuHalBayt =
            AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactForm.MaxMessageLength * 4L
            + AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactForm.MaxNameLength * 4L
            + AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactForm.MaxSubjectLength * 4L
            + AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactForm.MaxEmailLength * 4L;

        Assert.True(
            PanelServerLimits.PublicFormMaxBytes > enKotuHalBayt,
            $"Govde tavani ({PanelServerLimits.PublicFormMaxBytes}) mesru en uzun gonderimden "
            + $"({enKotuHalBayt} bayt) dar; gercek bir ziyaretci 413 alir.");
    }

    // -------------------------------------------------------------------------------------
    // 4) UseForwardedHeaders KARARI — sessiz birakilmadi
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>UseForwardedHeaders</c> BILEREK kapali ve gerekcesi kodda YAZILI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bir EKSIGI korumak icin test yazmak ilk bakista tuhaf gorunur; sebebi su ki buradaki eksik
    /// bir unutma DEGIL karardir ve iyi niyetli bir katkici onu "vekil arkasinda IP'ler yanlis"
    /// diye acabilir. Actigi anda, AYNI MAKINEDE kosan her kiracinin PHP'si panele localhost'tan
    /// istek atip <c>X-Forwarded-For</c>'a istedigi adresi yazabilir: hiz sinirini ve IP yasagini
    /// sifirlar, dahasi KURBANIN adresini yazip yoneticiyi kendi panelinden kilitler.
    /// </para>
    /// <para>
    /// Test iki sey olcer: (a) cagri KODDA yok, (b) gerekce KODDA var. Yalniz (a) olsaydi,
    /// gerekce silindiginde kimse kararin varligini bilmezdi.
    /// </para>
    /// </remarks>
    [Fact]
    public void ForwardedHeaders_kapali_ve_gerekcesi_YAZILI()
    {
        var satirlar = Oku("src", "AdaPanel.Api", "Program.cs").Split('\n');

        // (a) YASAK KODUN SEKLINE KONUYOR, dizgeye degil: gerekce yorumu icinde cagrinin adi
        // gecebilmeli (nitekim geciyor). Yorum satiri "//" ile baslar; kod satiri baslamaz.
        var canliCagri = satirlar.Any(satir =>
            satir.TrimStart().StartsWith("app.UseForwardedHeaders(", StringComparison.Ordinal));
        Assert.False(canliCagri, "UseForwardedHeaders acilmis; PanelRateLimits ve IP yasagi artik sahte adreslere guveniyor.");

        var kaynak = string.Join('\n', satirlar);
        // (b) Karar gorunur olmali.
        Assert.Contains("X-Forwarded-For", kaynak, StringComparison.Ordinal);
        Assert.Contains("KnownProxies", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Vekil_karari_BELGEDE_de_yazili()
    {
        var belge = Oku("docs", "hiz-siniri.md");

        Assert.Contains("UseForwardedHeaders", belge, StringComparison.Ordinal);
        Assert.Contains("KnownProxies", belge, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // 5) GIT WEBHOOK JETONU — baslik geldi, sorgu KIRILMADI
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("basliktan", "sorgudan", "basliktan", GitWebhookToken.Transport.Header)]
    [InlineData("basliktan", null, "basliktan", GitWebhookToken.Transport.Header)]
    [InlineData(null, "sorgudan", "sorgudan", GitWebhookToken.Transport.Query)]
    [InlineData("   ", "sorgudan", "sorgudan", GitWebhookToken.Transport.Query)]
    [InlineData(null, null, "", GitWebhookToken.Transport.None)]
    public void Jeton_secimi_BASLIK_oncelikli(
        string? baslik, string? sorgu, string beklenenJeton, GitWebhookToken.Transport beklenenKaynak)
    {
        var (jeton, kaynak) = GitWebhookToken.Select(baslik, sorgu);

        Assert.Equal(beklenenJeton, jeton);
        Assert.Equal(beklenenKaynak, kaynak);
    }

    [Fact]
    public void Webhook_ucu_HER_IKI_tasiyiciyi_da_okuyor()
    {
        // GERIYE DONUK UYUM NOBETCISI: kullanicilarin GitHub/GitLab'a yapistirdigi mevcut
        // adresler sorgu dizesi tasiyor. Sorgu yolu kapatilsaydi, panel guncellendigi anda o
        // kancalarin hepsi SESSIZCE olurdu (saglayici 401 gorur, kullanici hicbir sey gormez).
        var kaynak = Oku("src", "AdaPanel.Api", "Controllers", "GitController.cs");

        Assert.Contains("GitWebhookToken.Select(", kaynak, StringComparison.Ordinal);
        Assert.Contains("Request.Headers[GitWebhookToken.HeaderName]", kaynak, StringComparison.Ordinal);
        Assert.Contains("[FromQuery(Name = GitWebhookToken.QueryName)]", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Sorgu_dizesiyle_gelindiginde_DENETIM_KAYDI_soyluyor()
    {
        // Duzeltmenin gorunur olan yarisi: yonetici jetonun gunluge dustugunu ancak kendi
        // denetim ekraninda gorurse anahtarini yenilemeyi dusunebilir.
        var kaynak = Oku("src", "AdaPanel.Api", "Controllers", "GitController.cs");

        Assert.Contains("QUERY STRING", kaynak, StringComparison.Ordinal);
        Assert.Contains("access logs", kaynak, StringComparison.Ordinal);
        // Jetonun DEGERI asla kayda girmemeli.
        Assert.DoesNotContain("{jeton}", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Sabit_zamanli_karsilastirma_ve_SERI_dagitim_BOZULMADI()
    {
        // Bu iki koruma zaten dogruydu; jeton tasiyicisini degistirirken kaybolmadiklarini
        // sabitlemek isin parcasi.
        var kaynak = Oku("src", "AdaPanel.Infrastructure", "Git", "GitDeploymentService.cs");

        Assert.Contains("FixedTimeEquals(token, record.WebhookSecret)", kaynak, StringComparison.Ordinal);
        Assert.Contains("concurrencyLock.RunExclusiveAsync", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Arayuz_artik_jetonsuz_adres_ve_BASLIK_gosteriyor()
    {
        var kaynak = Oku("frontend", "src", "views", "SiteHostingView.vue");

        // Birincil adres jetonsuz.
        Assert.Contains("return `${window.location.origin}${git.value.webhookPath}`;", kaynak, StringComparison.Ordinal);
        // Baslik satiri gosteriliyor ve adi SUNUCUDAN geliyor (arayuzde sabit yazilmamis).
        Assert.Contains("git.value.webhookHeaderName", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("\"X-AdaPanel-Token\"", kaynak, StringComparison.Ordinal);
        // Eski bicim GIZLENMIYOR: calisan kancalari ariza sanip yeniden kurmasin diye duruyor.
        Assert.Contains("legacyWebhookUrl", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Baslik_adi_SUNUCUDAN_arayuze_tasiniyor()
    {
        // Arayuz adi kendi tarafinda sabitleseydi, sunucu adi degistirdiginde panel artik
        // okunmayan bir basligi TALIMAT olarak gosterirdi: sebebi gorunmeyen bir 401.
        var dto = new AdaPanel.Core.Dtos.Git.GitDeploymentDto();
        Assert.Equal(GitWebhookToken.HeaderName, dto.WebhookHeaderName);

        var servis = Oku("src", "AdaPanel.Infrastructure", "Git", "GitDeploymentService.cs");
        Assert.Contains("WebhookHeaderName = AdaPanel.Core.Common.GitWebhookToken.HeaderName", servis, StringComparison.Ordinal);
    }

    [Fact]
    public void Yeni_arayuz_metinleri_IKI_dilde_de_var()
    {
        var tr = Sozluk(Path.Combine(Kok(), "frontend", "src", "i18n", "tr.json"));
        var en = Sozluk(Path.Combine(Kok(), "frontend", "src", "i18n", "en.json"));

        foreach (var anahtar in new[]
                 {
                     "panel.hosting.gitWebhookHeader",
                     "panel.hosting.gitWebhookHeaderHelp",
                     "panel.hosting.gitWebhookLegacy",
                     "panel.hosting.gitWebhookLegacyShow",
                     "panel.hosting.gitWebhookLegacyHide",
                     "panel.hosting.gitWebhookLegacyHelp",
                     "rate_limit.exceeded"
                 })
        {
            Assert.True(tr.Contains(anahtar), $"tr.json'da eksik: {anahtar}");
            Assert.True(en.Contains(anahtar), $"en.json'da eksik: {anahtar}");
        }
    }

    /// <summary>
    /// Reddedilen istek PANELIN ZARFINI doner (durum kodu + cevrilebilir mesaj anahtari).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BU TESTIN ILK HALI DOGUSTAN OLUYDU ve bunu MUTASYON gosterdi.</b> Kaynak metninde
    /// <c>"rate_limit.exceeded"</c> geciyor mu diye bakiyordu; oysa o dizge ayni dosyada bir
    /// GEREKCE YORUMUNDA da geciyor. Koddaki anahtar <c>"too_many"</c> ile degistirildiginde
    /// test YESIL kaldi — yani kullaniciya ham anahtar donduren bir gerileme testten gecerdi.
    /// </para>
    /// <para>
    /// Simdi olculen sey DAVRANIS: gercek <c>OnRejected</c> geri cagrisi calistiriliyor ve
    /// YAZDIGI govde okunuyor. Metin degil, uretilen yanit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Reddedilen_istek_PANELIN_ZARFINI_doner()
    {
        var secenekler = new Microsoft.AspNetCore.RateLimiting.RateLimiterOptions();
        PanelRateLimits.Configure("AdaPanel.Auth")(secenekler);

        Assert.NotNull(secenekler.OnRejected);
        Assert.Equal(StatusCodes.Status429TooManyRequests, secenekler.RejectionStatusCode);

        var context = new DefaultHttpContext();
        var govde = new MemoryStream();
        context.Response.Body = govde;

        using var kiralama = new RedGecerliKiralama();
        await secenekler.OnRejected!(
            new Microsoft.AspNetCore.RateLimiting.OnRejectedContext { HttpContext = context, Lease = kiralama },
            CancellationToken.None);

        var yazilan = System.Text.Encoding.UTF8.GetString(govde.ToArray());

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        // Arayuzun hata cozucusu (frontend/src/utils/apiError.ts) zarftaki `message` alanini
        // ceviri anahtari olarak okur; govdesiz ya da anahtarsiz bir 429'da ham "HTTP 429"
        // metnine duser ve kullanici ne oldugunu anlamaz.
        Assert.Contains("\"message\":\"rate_limit.exceeded\"", yazilan, StringComparison.Ordinal);
        Assert.Contains("\"success\":false", yazilan, StringComparison.Ordinal);

        // Retry-After: istemcinin NE KADAR bekleyecegini bilmesi icin. Sabit degil, kiralamanin
        // kendi bildirdigi sureden okunur.
        Assert.Equal("42", context.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>Reddedilmis, <c>RetryAfter</c> meta verisi tasiyan sahte kiralama.</summary>
    private sealed class RedGecerliKiralama : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = TimeSpan.FromSeconds(41.2); // yukari yuvarlanmali -> "42"
                return true;
            }

            metadata = null;
            return false;
        }
    }

    // -------------------------------------------------------------------------------------
    // duzenek
    // -------------------------------------------------------------------------------------

    private static long UcunGovdeTavani(string denetleyici, string eylem)
    {
        var tip = typeof(PanelRateLimits).Assembly.GetTypes()
            .Single(t => t.Name == denetleyici);
        var yontem = tip.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == eylem);
        // DEGER YAPICI ARGUMANINDAN OKUNUYOR: RequestSizeLimitAttribute sayiyi hicbir ozellikte
        // yayinlamiyor (ozel alan). CustomAttributeData, kaynakta YAZILANIN aynisini verir.
        var veri = CustomAttributeData.GetCustomAttributes(yontem)
            .SingleOrDefault(a => a.AttributeType == typeof(RequestSizeLimitAttribute));

        Assert.True(veri is not null, $"{denetleyici}.{eylem} [RequestSizeLimit] tasimiyor.");
        return Convert.ToInt64(veri!.ConstructorArguments[0].Value);
    }

    private static HashSet<string> Sozluk(string yol)
    {
        using var belge = JsonDocument.Parse(File.ReadAllText(yol));
        return belge.RootElement.EnumerateObject().Select(alan => alan.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(Kok(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    private static string Kok()
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
