using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Infrastructure.SiteBuilder;
using Xunit;

using TestOrtami = AdaPanel.Infrastructure.Tests.SiteBuilderTestOrtami;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// İLETİŞİM FORMU: kimliksiz bir uç E-POSTA GÖNDERİYOR — yani yanlış yazılırsa bir spam silahı.
/// </summary>
/// <remarks>
/// <para>
/// Form ilk turda bilerek kapsam dışı bırakılmıştı; kayıtlı gerekçe: <i>"sunucu tarafı alıcı +
/// spam koruması ister. Çalışmayan form, ziyaretçinin gönderdiğini sanmasına yol açar; hiç form
/// koymamaktan kötüdür."</i> Bu testler o gerekçenin her maddesini sabitliyor:
/// </para>
/// <list type="number">
/// <item><b>Açık röle değil:</b> alıcı ziyaretçiden değil SİTE SAHİBİNDEN gelir.</item>
/// <item><b>Başlık enjeksiyonu yok:</b> konu satırına <c>\r\n</c> ile ek başlık sokulamaz.</item>
/// <item><b>Spam koruması ölçülebilir:</b> IP/site/gönderen sınırları ve bal küpü.</item>
/// <item><b>Yalan yok:</b> gönderim başarısızsa ziyaretçiye AÇIKÇA söylenir.</item>
/// <item><b>Çalışmayan form basılmaz:</b> koşullar eksikse form HİÇ üretilmez.</item>
/// </list>
/// </remarks>
public sealed class SiteBuilderContactFormTests
{
    // ------------------------------------------------------------------ başlık enjeksiyonu

    [Theory]
    [InlineData("Merhaba\r\nBcc: kurban@ornek.com")]
    [InlineData("Merhaba\nBcc: kurban@ornek.com")]
    [InlineData("Merhaba\rBcc: kurban@ornek.com")]
    [InlineData("Merhaba\u0085Bcc: kurban@ornek.com")]
    [InlineData("Merhaba\u2028Bcc: kurban@ornek.com")]
    [InlineData("Merhaba\u2029Bcc: kurban@ornek.com")]
    [InlineData("Merhaba\vBcc: kurban@ornek.com")]
    [InlineData("Merhaba\fBcc: kurban@ornek.com")]
    [InlineData("Merhaba\0Bcc: kurban@ornek.com")]
    public void Basliktan_SATIR_SONU_ile_cikilamaz(string kotu)
    {
        var temiz = SiteBuilderContactForm.HeaderSafe(kotu);

        // Tek bir satır sonu kaçarsa ileti kendi Bcc satırını kazanır ve uç, istenen adrese posta
        // atan bir SPAM SİLAHINA döner.
        Assert.DoesNotContain('\r', temiz);
        Assert.DoesNotContain('\n', temiz);
        Assert.All(temiz, karakter => Assert.False(char.IsControl(karakter)));
        Assert.DoesNotContain('\u2028', temiz);
        Assert.DoesNotContain('\u2029', temiz);

        // Kelimeler BİRLEŞTİRİLMEZ: silme yerine boşluğa çevirme, metni okunur bırakır.
        Assert.Contains("Merhaba", temiz, StringComparison.Ordinal);
        Assert.Contains("Bcc:", temiz, StringComparison.Ordinal);
        Assert.DoesNotContain("MerhabaBcc", temiz, StringComparison.Ordinal);
    }

    [Fact]
    public void Uretilen_KONU_satiri_da_satir_sonu_tasimaz()
    {
        var dogrulama = SiteBuilderContactForm.Validate(new SiteBuilderContactSubmission(
            Name: "Ziyaretci\r\nX: y",
            Email: "ziyaretci@ornek.com",
            Subject: "Teklif\r\nBcc: kurban@ornek.com",
            Message: "Merhaba",
            Honeypot: null));

        Assert.NotNull(dogrulama.Value);

        var konu = SiteBuilderContactForm.BuildSubject("ornek.com\r\nX: y", dogrulama.Value!);
        Assert.DoesNotContain('\r', konu);
        Assert.DoesNotContain('\n', konu);

        // Ad da başlık güvenli hâle gelir: gövdeye giriyor ama kural TEK yerde uygulanıyor.
        Assert.DoesNotContain('\n', dogrulama.Value!.Name);
    }

    [Fact]
    public void Konu_satiri_asiri_uzayamaz()
    {
        var dogrulama = SiteBuilderContactForm.Validate(new SiteBuilderContactSubmission(
            "Ziyaretci",
            "ziyaretci@ornek.com",
            new string('k', SiteBuilderContactForm.MaxSubjectLength),
            "Merhaba",
            null));

        var konu = SiteBuilderContactForm.BuildSubject(new string('s', 400), dogrulama.Value!);
        Assert.True(konu.Length <= 250, $"Konu satiri {konu.Length} karakter; bazi SMTP sunuculari reddeder.");
    }

    // ------------------------------------------------------------------ doğrulama

    [Theory]
    [InlineData("", "a@b.com", "Merhaba")]                       // ad boş
    [InlineData("Ad", "gecersiz-adres", "Merhaba")]              // adres biçimsiz
    [InlineData("Ad", "a@b.com", "")]                            // mesaj boş
    [InlineData("Ad", "a@b.com", "   ")]                         // yalnız boşluk
    public void Eksik_alan_REDDEDILIR(string ad, string adres, string mesaj)
    {
        var sonuc = SiteBuilderContactForm.Validate(
            new SiteBuilderContactSubmission(ad, adres, "Konu", mesaj, null));

        Assert.Equal(SiteBuilderContactOutcome.Invalid, sonuc.Outcome);
        Assert.Null(sonuc.Value);
    }

    [Fact]
    public void Sinir_asan_mesaj_KIRPILMAZ_REDDEDILIR()
    {
        // Kırpıp "gönderildi" demek, ziyaretçinin yazdığının yarısını sessizce atmak olurdu.
        var sonuc = SiteBuilderContactForm.Validate(new SiteBuilderContactSubmission(
            "Ad",
            "a@b.com",
            "Konu",
            new string('m', SiteBuilderContactForm.MaxMessageLength + 1),
            null));

        Assert.Equal(SiteBuilderContactOutcome.Invalid, sonuc.Outcome);
    }

    [Fact]
    public void Bal_kupu_dolu_ise_TUZAK_donuyor_ve_diger_denetimler_calismiyor()
    {
        // Alanların biçimi kasten bozuk: tuzak KARARI önce verilmeli ki bota "hangi alan
        // yanlıştı" bilgisi de sızmasın.
        var sonuc = SiteBuilderContactForm.Validate(
            new SiteBuilderContactSubmission("", "", "", "", "http://spam.example"));

        Assert.Equal(SiteBuilderContactOutcome.Trapped, sonuc.Outcome);
    }

    // ------------------------------------------------------------------ posta gövdesi

    [Fact]
    public void Ziyaretcinin_adresi_BASLIGA_degil_GOVDEYE_yazilir()
    {
        var dogrulama = SiteBuilderContactForm.Validate(new SiteBuilderContactSubmission(
            "Ziyaretci", "ziyaretci@ornek.com", "Teklif", "Merhaba", null));

        var konu = SiteBuilderContactForm.BuildSubject("ornek.com", dogrulama.Value!);
        var govde = SiteBuilderContactForm.BuildBody("ornek.com", dogrulama.Value!, "10.0.0.1");

        // Adres başlığa girseydi (Reply-To/From) kaçırmaya güvenen ikinci bir yüzey açılırdı.
        Assert.DoesNotContain("ziyaretci@ornek.com", konu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ziyaretci@ornek.com", govde, StringComparison.Ordinal);

        // Alıcı, adresin DOĞRULANMADIĞINI görmeli; yoksa gelen adrese güvenip iş yapar.
        Assert.Contains("DOĞRULANMAMIŞTIR", govde, StringComparison.Ordinal);
        Assert.Contains("NOT verified", govde, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ hız sınırı

    [Fact]
    public void Ayni_IP_pencere_icinde_SINIRLANIR()
    {
        var sinir = new SiteBuilderContactRateLimiter();
        var an = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        // Gönderen bekleme süresini devre dışı bırakmak için her seferinde farklı adres.
        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerIp; i++)
        {
            Assert.True(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", $"g{i}@ornek.com", an));
        }

        Assert.False(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", "yeni@ornek.com", an));

        // Başka bir IP etkilenmez: sınır kaynağa göre, siteye göre değil.
        Assert.True(sinir.TryAcquire("10.0.0.2", "site-AAAAAAAA", "baska@ornek.com", an));
    }

    [Fact]
    public void IP_penceresi_KAYAR()
    {
        var sinir = new SiteBuilderContactRateLimiter();
        var an = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerIp; i++)
        {
            Assert.True(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", $"g{i}@ornek.com", an));
        }

        Assert.False(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", "yeni@ornek.com", an));

        // Pencere dolduktan sonra meşru ziyaretçi yeniden gönderebilmeli — kalıcı bir yasak,
        // saldırganın hız sınırını hizmet engellemeye çevirmesi demek olurdu.
        var sonra = an + SiteBuilderContactRateLimiter.IpWindow + TimeSpan.FromSeconds(1);
        Assert.True(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", "yeni@ornek.com", sonra));
    }

    [Fact]
    public void Ayni_GONDEREN_bekleme_suresine_tabi()
    {
        var sinir = new SiteBuilderContactRateLimiter();
        var an = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(sinir.TryAcquire("10.0.0.1", "site-AAAAAAAA", "ayni@ornek.com", an));

        // Farklı IP'den bile olsa aynı gönderen beklemek zorunda: "Gönder"e iki kez basan
        // ziyaretçi iki kopya göndermez, dağıtık bir bot da adresi tekrar kullanamaz.
        Assert.False(sinir.TryAcquire("10.0.0.9", "site-AAAAAAAA", "ayni@ornek.com", an.AddSeconds(5)));
        Assert.True(sinir.TryAcquire(
            "10.0.0.9",
            "site-AAAAAAAA",
            "ayni@ornek.com",
            an + SiteBuilderContactRateLimiter.SenderCooldown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SITE_basina_sinir_dagitik_saldiriyi_da_keser()
    {
        var sinir = new SiteBuilderContactRateLimiter();
        var an = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        // HER istek FARKLI bir IP'den ve FARKLI bir gönderenden: IP sınırı tek başına bunu
        // durduramaz. Site sahibinin kutusunu koruyan sınır budur.
        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerSite; i++)
        {
            Assert.True(sinir.TryAcquire($"10.1.{i / 250}.{i % 250}", "site-AAAAAAAA", $"g{i}@ornek.com", an));
        }

        Assert.False(sinir.TryAcquire("10.9.9.9", "site-AAAAAAAA", "son@ornek.com", an));

        // KOMŞU SİTE ETKİLENMEZ: bir sitenin bombalanması diğerinin formunu kapatmamalı.
        Assert.True(sinir.TryAcquire("10.9.9.9", "site-BBBBBBBB", "son@ornek.com", an));
    }

    [Fact]
    public void IP_okunamadiginda_sinir_KALKMAZ()
    {
        // "IP yok" durumunu sınırsız saymak, sınırı atlamanın en kolay yolunu açık bırakırdı.
        var sinir = new SiteBuilderContactRateLimiter();
        var an = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerIp; i++)
        {
            Assert.True(sinir.TryAcquire(null, "site-AAAAAAAA", $"g{i}@ornek.com", an));
        }

        Assert.False(sinir.TryAcquire(null, "site-AAAAAAAA", "yeni@ornek.com", an));
    }

    // ------------------------------------------------------------------ uç adresi

    [Theory]
    [InlineData("../../etc")]
    [InlineData("a/b-AAAAAAAA")]
    [InlineData("..-AAAAAAAA")]
    [InlineData("site-zzzzzzzz")]   // özet ONALTILIK olmalı
    [InlineData("site")]
    [InlineData("")]
    public void Gecersiz_site_anahtari_REDDEDILIR(string anahtar)
    {
        Assert.False(SiteBuilderContactEndpoint.IsValidSiteKey(anahtar));
        Assert.Equal(string.Empty, SiteBuilderContactEndpoint.BuildAction("panel.ornek.com", anahtar));
    }

    [Fact]
    public void Gercek_saklama_anahtari_KABUL_edilir()
    {
        var anahtar = SiteBuilderService.StorageKey("ornek.com");
        Assert.True(SiteBuilderContactEndpoint.IsValidSiteKey(anahtar));

        var adres = SiteBuilderContactEndpoint.BuildAction("panel.ornek.com", anahtar);
        Assert.StartsWith("https://panel.ornek.com/api/v1/public/site-forms/", adres, StringComparison.Ordinal);
        Assert.EndsWith("/contact", adres, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]                 // tek etiketli ad: dışarıdan çözülemez
    [InlineData("panel.ornek.com/x")]
    [InlineData("http://panel.ornek.com")]
    public void Panel_ana_adi_yoksa_veya_bozuksa_hedef_KURULMAZ(string host)
    {
        Assert.Equal(
            string.Empty,
            SiteBuilderContactEndpoint.BuildAction(host, SiteBuilderService.StorageKey("ornek.com")));
    }

    [Theory]
    [InlineData("http://panel.ornek.com/x")]
    [InlineData("https://panel.ornek.com/x\" onsubmit=\"alert(1)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://panel.ornek.com/ bosluk")]
    public void Form_hedefi_OZNITELIKTEN_cikamaz(string kotu)
    {
        // Hedef bir action="…" özniteliğine giriyor. Kaçırma, değerin nereden geldiğini
        // hatırlamaya bırakılamaz; kural üretim noktasında duruyor.
        Assert.Throws<AppException>(() => SiteBuilderRenderContext.ForPublish(kotu));
    }

    // ------------------------------------------------------------------ gizli giriş yolu muafiyeti

    /// <summary>
    /// Muafiyet OLMADAN form var görünüp HİÇ çalışmaz — ve bunu yalnız ziyaretçi görür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Panelin <c>/api</c> altındaki her ucu gizli giriş yolu başlığı ister. Müşterinin
    /// sitesindeki bir form gönderimi o başlığı EKLEYEMEZ (tarayıcı gezinmesi), dolayısıyla her
    /// gönderim 404 alırdı. Aynı sınıf bu depoda iki kez canlıda ısırdı (phpMyAdmin düğmesi,
    /// abonelikten çıkma bağlantısı).
    /// </para>
    /// <para>
    /// Test kaynak metnini OKUR: test projesi Api projesine referans vermiyor ve aynı desen
    /// depoda zaten var (<c>SecureEntryPathExemptionTests</c>).
    /// </para>
    /// </remarks>
    [Fact]
    public void Form_ucu_gizli_giris_yolundan_MUAF_ve_muafiyet_DAR()
    {
        var kaynak = AraKatmanKaynagi();

        // Muafiyet listesinde OLMALI.
        Assert.Contains("IsSiteBuilderContactFormPath(path)", kaynak, StringComparison.Ordinal);

        // Yol SABİTTEN okunmalı: elle yazılmış bir dizge, uç bir gün taşındığında sessizce
        // eskir ve form yeniden 404 dönmeye başlar.
        Assert.Contains("SiteBuilderContactEndpoint.RoutePrefix", kaynak, StringComparison.Ordinal);

        // KAPSAM DAR: segment sınırına bakılıyor, yani başka bir /api/v1/public ucu sızamaz.
        Assert.Contains(
            "path.StartsWithSegments(AdaPanel.Infrastructure.SiteBuilder.SiteBuilderContactEndpoint.RoutePrefix, out _)",
            kaynak,
            StringComparison.Ordinal);

        // Muafiyet TÜM /api/v1/public'e verilmemiş olmalı.
        Assert.DoesNotContain("StartsWithSegments(\"/api/v1/public\"", kaynak, StringComparison.Ordinal);

        // Genel kural yerinde duruyor: geri kalan her /api yolu başlığı istemeye devam ediyor.
        Assert.Contains("return path.StartsWithSegments(\"/api\", out _);", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Muaf_yol_ile_denetleyicinin_ROTASI_ayni()
    {
        // İkisi ayrı yazılsaydı biri değiştiğinde form sessizce 404 dönerdi.
        var rota = SiteBuilderContactEndpoint.RoutePrefix.TrimStart('/');
        Assert.Contains(
            $"[Route(\"{rota}\")]",
            DenetleyiciKaynagi(),
            StringComparison.Ordinal);
    }

    private static string AraKatmanKaynagi() =>
        KaynakOku(Path.Combine("src", "AdaPanel.Api", "Middleware", "SecureEntryPathMiddleware.cs"));

    private static string DenetleyiciKaynagi() =>
        KaynakOku(Path.Combine("src", "AdaPanel.Api", "Controllers", "SiteBuilderPublicController.cs"));

    private static string KaynakOku(string goreliYol)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, goreliYol);
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException($"{goreliYol} bulunamadi.");
    }

    // ------------------------------------------------------------------ form basılıyor mu

    [Fact]
    public void Kosullar_tam_ise_form_BASILIR()
    {
        var html = SiteBuilderRenderer
            .Render(Tasarim(formAcik: true, adres: "sahip@ornek.com"), YayinBaglami())
            .Html;

        Assert.Contains("<form class=\"contact-form\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"{SiteBuilderContactForm.HoneypotField}\"", html, StringComparison.Ordinal);
        Assert.Contains("adapanel-cf-trap", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "sahip@ornek.com")]  // kullanıcı formu açmadı
    [InlineData(true, "")]                  // alıcı adresi yok
    [InlineData(true, "gecersiz-adres")]    // alıcı adresi biçimsiz
    public void Kosul_eksikse_form_HIC_basilmaz(bool formAcik, string adres)
    {
        // Çalışmayan bir form, ziyaretçinin gönderdiğini sanması demek — hiç form koymamaktan
        // kötüdür. Bu yüzden "belki çalışır" diye bir hâl bırakılmadı.
        var html = SiteBuilderRenderer.Render(Tasarim(formAcik, adres), YayinBaglami()).Html;
        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_ana_adi_yoksa_form_basilmaz()
    {
        var html = SiteBuilderRenderer
            .Render(Tasarim(formAcik: true, adres: "sahip@ornek.com"), SiteBuilderRenderContext.Published)
            .Html;

        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Formdaki_metinler_KACIRILIR()
    {
        var tasarim = Tasarim(formAcik: true, adres: "sahip@ornek.com");
        tasarim.SiteTitle = "<script>alert(1)</script>";

        var html = SiteBuilderRenderer.Render(tasarim, YayinBaglami()).Html;
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Form_bayragi_YALNIZ_iletisim_bolumunde_gecerli()
    {
        // Başka bir bölümde işaretlenmiş bir form bayrağı, sayfada karşılığı olmayan bir ayar
        // üretir ve kimliksiz uçta "hangi bölüm alıcıyı taşıyor" sorusunu bulanıklaştırırdı.
        // Bayrak, alıcı adresinin bulunduğu bölüme BAĞLI olmak zorunda.
        await using var ortam = await TestOrtami.KurAsync();

        var tasarim = await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Sections =
            [
                new SiteBuilderSectionDto { Type = "hero", Enabled = true, ContactFormEnabled = true },
                new SiteBuilderSectionDto { Type = "about", Enabled = true, ContactFormEnabled = true },
                new SiteBuilderSectionDto
                {
                    Type = "contact",
                    Enabled = true,
                    Email = "sahip@ornek.com",
                    ContactFormEnabled = true
                }
            ]
        });

        Assert.All(
            tasarim.Sections.Where(bolum => bolum.Type != "contact"),
            bolum => Assert.False(bolum.ContactFormEnabled, $"'{bolum.Type}' bolumunde form bayragi acik kaldi."));

        Assert.True(tasarim.Sections.Single(bolum => bolum.Type == "contact").ContactFormEnabled);
    }

    [Fact]
    public async Task Plan_formun_neden_basilamadigini_ADIYLA_soyler()
    {
        await using var ortam = await TestOrtami.KurAsync();

        // 1) Alıcı adresi yok.
        await FormluTasarimKaydetAsync(ortam, adres: string.Empty);
        Assert.Equal(
            "sitebuilder.form_recipient_missing",
            (await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi)).ContactFormBlockedReasonKey);

        // 2) Adres var, panelin genel adresi yok.
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");
        Assert.Equal(
            "sitebuilder.form_panel_host_missing",
            (await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi)).ContactFormBlockedReasonKey);

        // 3) İkisi de tamam: engel yok.
        await ortam.PanelAnaAdiAyarlaAsync("panel.ornek.com");
        Assert.Equal(
            string.Empty,
            (await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi)).ContactFormBlockedReasonKey);
    }

    [Fact]
    public async Task Yayinlanan_sayfadaki_form_GERCEK_ucu_gosterir()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await ortam.PanelAnaAdiAyarlaAsync("panel.ornek.com");
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");

        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        var sayfa = await File.ReadAllTextAsync(Path.Combine(ortam.SiteKoku, "index.html"));
        var beklenen = SiteBuilderContactEndpoint.BuildAction(
            "panel.ornek.com",
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi));

        Assert.Contains($"action=\"{beklenen}\"", sayfa, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ uçtan uca gönderim

    [Fact]
    public async Task Form_GERCEKTEN_gonderir_ve_alici_SITE_SAHIBININ_adresidir()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");

        var sonuc = await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi),
            new SiteBuilderContactSubmission("Ziyaretci", "ziyaretci@baska.com", "Teklif", "Merhaba", null),
            "10.0.0.1");

        Assert.Equal(SiteBuilderContactOutcome.Sent, sonuc.Outcome);
        Assert.Equal(TestOrtami.SiteAdi, sonuc.SiteName);

        var ileti = Assert.Single(ortam.Postaci.Gonderilenler);

        // AÇIK RÖLE DEĞİL: alıcı, ziyaretçinin yazdığı adres DEĞİL, sitenin sahibininki.
        Assert.Equal("sahip@ornek.com", ileti.To);
        Assert.Contains("[ornek.com]", ileti.Subject, StringComparison.Ordinal);
        Assert.Contains("Merhaba", ileti.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bal_kupu_dolu_ise_POSTA_GONDERILMEZ()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");

        var sonuc = await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi),
            new SiteBuilderContactSubmission("Bot", "bot@spam.example", "Reklam", "Spam", "http://spam.example"),
            "10.0.0.1");

        Assert.Equal(SiteBuilderContactOutcome.Trapped, sonuc.Outcome);
        Assert.Empty(ortam.Postaci.Gonderilenler);
    }

    [Fact]
    public async Task Form_KAPALIYSA_uc_posta_gondermez()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com", formAcik: false);

        var sonuc = await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi),
            new SiteBuilderContactSubmission("Ziyaretci", "z@ornek.com", "Konu", "Merhaba", null),
            "10.0.0.1");

        // Panelde hiç kullanılmayan bir özellik, sunucuda açık bir yüzey bırakmamalı.
        Assert.Equal(SiteBuilderContactOutcome.NotAvailable, sonuc.Outcome);
        Assert.Empty(ortam.Postaci.Gonderilenler);
    }

    [Fact]
    public async Task Olmayan_site_ile_kapali_form_AYNI_cevabi_verir()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com", formAcik: false);

        var servis = ortam.FormServisi();
        var gonderim = new SiteBuilderContactSubmission("Ad", "a@b.com", "K", "M", null);

        var kapali = await servis.SubmitAsync(SiteBuilderService.StorageKey(TestOrtami.SiteAdi), gonderim, "10.0.0.1");
        var yok = await servis.SubmitAsync(SiteBuilderService.StorageKey("hic-olmayan-site"), gonderim, "10.0.0.1");

        // Farklı cevaplar, hangi site anahtarlarının GERÇEK olduğunu taramaya yarayan bir sinyal
        // olurdu.
        Assert.Equal(SiteBuilderContactOutcome.NotAvailable, kapali.Outcome);
        Assert.Equal(SiteBuilderContactOutcome.NotAvailable, yok.Outcome);
        Assert.Equal(
            SiteBuilderContactPages.For(kapali.Outcome),
            SiteBuilderContactPages.For(yok.Outcome));
    }

    [Fact]
    public async Task Gonderim_BASARISIZ_olursa_ziyaretciye_ACIKCA_soylenir()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");
        ortam.Postaci.Ariza = new AppException("notifications.smtp_not_configured", 400, 400);

        var sonuc = await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi),
            new SiteBuilderContactSubmission("Ziyaretci", "z@ornek.com", "Konu", "Merhaba", null),
            "10.0.0.1");

        Assert.Equal(SiteBuilderContactOutcome.SendFailed, sonuc.Outcome);

        // Cevap sayfası "gönderildi" DEMEMELİ. Hata yutulsaydı ziyaretçi gelmeyecek bir yanıtı
        // beklerdi — özelliğin ilk turda ertelenme gerekçesi tam olarak buydu.
        var sayfa = SiteBuilderContactPages.For(sonuc.Outcome);
        Assert.Contains("gönderilemedi", sayfa, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iletilmedi", sayfa, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not send e-mail", sayfa, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mesajınız gönderildi", sayfa, StringComparison.Ordinal);
        Assert.NotEqual(SiteBuilderContactPages.For(SiteBuilderContactOutcome.Sent), sayfa);
    }

    [Fact]
    public async Task Ucta_da_hiz_siniri_UYGULANIR()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");
        var servis = ortam.FormServisi();
        var anahtar = SiteBuilderService.StorageKey(TestOrtami.SiteAdi);

        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerIp; i++)
        {
            var sonuc = await servis.SubmitAsync(
                anahtar,
                new SiteBuilderContactSubmission("Ad", $"g{i}@ornek.com", "K", "M", null),
                "10.0.0.1");
            Assert.Equal(SiteBuilderContactOutcome.Sent, sonuc.Outcome);
        }

        var son = await servis.SubmitAsync(
            anahtar,
            new SiteBuilderContactSubmission("Ad", "son@ornek.com", "K", "M", null),
            "10.0.0.1");

        Assert.Equal(SiteBuilderContactOutcome.RateLimited, son.Outcome);
        Assert.Equal(SiteBuilderContactRateLimiter.MaxPerIp, ortam.Postaci.Gonderilenler.Count);
    }

    [Fact]
    public async Task Biçimsiz_istek_HIZ_SINIRI_sayacini_TUKETMEZ()
    {
        // Reddedilen istekler sayacı harcasaydı, sürekli deneyen bir bot pencereyi hiç
        // boşaltmaz ve GERÇEK ziyaretçileri kalıcı olarak dışarıda tutardı: hız sınırı bir
        // hizmet engelleme aracına dönerdi.
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");
        var servis = ortam.FormServisi();
        var anahtar = SiteBuilderService.StorageKey(TestOrtami.SiteAdi);

        for (var i = 0; i < SiteBuilderContactRateLimiter.MaxPerIp * 3; i++)
        {
            var sonuc = await servis.SubmitAsync(
                anahtar,
                new SiteBuilderContactSubmission("", "bozuk", "", "", null),
                "10.0.0.1");
            Assert.Equal(SiteBuilderContactOutcome.Invalid, sonuc.Outcome);
        }

        var mesru = await servis.SubmitAsync(
            anahtar,
            new SiteBuilderContactSubmission("Ad", "mesru@ornek.com", "K", "M", null),
            "10.0.0.1");

        Assert.Equal(SiteBuilderContactOutcome.Sent, mesru.Outcome);
    }

    [Fact]
    public async Task Baslik_enjeksiyonu_UCTAN_UCA_gonderilen_iletide_de_yok()
    {
        await using var ortam = await TestOrtami.KurAsync();
        await FormluTasarimKaydetAsync(ortam, adres: "sahip@ornek.com");

        await ortam.FormServisi().SubmitAsync(
            SiteBuilderService.StorageKey(TestOrtami.SiteAdi),
            new SiteBuilderContactSubmission(
                "Ziyaretci\r\nBcc: kurban1@ornek.com",
                "ziyaretci@ornek.com",
                "Teklif\r\nBcc: kurban2@ornek.com",
                "Merhaba",
                null),
            "10.0.0.1");

        var ileti = Assert.Single(ortam.Postaci.Gonderilenler);

        // Konu satırında satır sonu KALMAZ; kalırsa ileti kendi Bcc'sini kazanır.
        Assert.DoesNotContain('\r', ileti.Subject);
        Assert.DoesNotContain('\n', ileti.Subject);
        Assert.Equal("sahip@ornek.com", ileti.To);
    }

    // ------------------------------------------------------------------ yardımcılar

    private static SiteBuilderRenderContext YayinBaglami() =>
        SiteBuilderRenderContext.ForPublish("https://panel.ornek.com/api/v1/public/site-forms/x-AAAAAAAA/contact");

    private static SiteBuilderProjectDto Tasarim(bool formAcik, string adres) => new()
    {
        SiteName = "ornek.com",
        SiteTitle = "Ornek",
        TemplateId = "sade",
        PaletteId = "okyanus",
        FontId = "sistem",
        Language = "tr",
        Sections =
        [
            new SiteBuilderSectionDto
            {
                Type = "contact",
                Enabled = true,
                Email = adres,
                ContactFormEnabled = formAcik
            }
        ]
    };

    private static Task FormluTasarimKaydetAsync(TestOrtami ortam, string adres, bool formAcik = true) =>
        ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Sections =
            [
                new SiteBuilderSectionDto
                {
                    Type = "contact",
                    Enabled = true,
                    Email = adres,
                    ContactFormEnabled = formAcik
                }
            ]
        });
}
