using System.Security.Claims;
using System.Text;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Branding;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Özel marka (white-label): yükleme doğrulaması, metin doğrulaması ve KAPSAM izolasyonu.
/// </summary>
/// <remarks>
/// <para>
/// Bu özellik iki ayrı sınıfta hata yapmaya çok müsait: (1) kullanıcıdan gelen bir DOSYA panelin
/// içine giriyor, (2) bir bayinin ayarı BAŞKA bir kiracının ekranını belirliyor. İkisi de sessizce
/// yanlış çalışabilir — logo görünmeye devam ederken yanlış markanın görünmesi ya da "resim" diye
/// bir belge dosyasının kabul edilmesi kimseyi uyarmaz. Bu yüzden her iki sınıf da tek tek ölçülüyor.
/// </para>
/// </remarks>
public sealed class BrandingTests : IDisposable
{
    private static readonly Guid BayiA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid BayiB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid MusteriA = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid MusteriB = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid AltKullaniciA = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");
    private static readonly Guid SahipsizMusteri = Guid.Parse("ffffffff-6666-6666-6666-666666666666");
    private static readonly Guid Yonetici = Guid.Parse("12345678-7777-7777-7777-777777777777");

    private readonly string veriKoku;
    private readonly SahteKapsam kapsam = new();
    private readonly BrandingService servis;

    public BrandingTests()
    {
        veriKoku = Path.Combine(Path.GetTempPath(), "adapanel-marka-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(veriKoku);

        kapsam.Sahipler[MusteriA] = BayiA;
        kapsam.Sahipler[MusteriB] = BayiB;
        kapsam.Ebeveynler[AltKullaniciA] = MusteriA;

        servis = new BrandingService(
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })),
            kapsam);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(veriKoku, recursive: true);
        }
        catch
        {
            // Geçici klasör silinemezse test sonucunu etkilemez.
        }
    }

    // ---------------------------------------------------------------------------------------
    // 1) YÜKLENEN DOSYANIN İÇERİK DOĞRULAMASI
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Png_logo_kabul_edilir_ve_tipi_panel_belirler()
    {
        var bicim = BrandingImageRules.Validate(BrandingImageKind.Logo, Png());

        Assert.Equal("image/png", bicim.MediaType);
        Assert.Equal(".png", bicim.FileExtension);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("gif")]
    [InlineData("webp")]
    public void Yaygin_raster_bicimleri_logo_olarak_kabul_edilir(string tur)
    {
        var icerik = tur switch
        {
            "jpeg" => Jpeg(),
            "gif" => Gif(),
            _ => Webp()
        };

        Assert.NotNull(BrandingImageRules.Validate(BrandingImageKind.Logo, icerik));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject/></svg>")]
    [InlineData("   \n\t<svg width=\"10\"/>")]
    public void Svg_REDDEDILIR_ve_sebebi_ayri_anahtarla_soylenir(string svg)
    {
        // SVG bir BELGE biçimidir: script, foreignObject ve olay öznitelikleri taşıyabilir.
        // Kabul edip "temizlemeye" çalışmak bir logo özelliğinin taşıyabileceğinden büyük risktir.
        var hata = Assert.Throws<AppException>(() =>
            BrandingImageRules.Validate(BrandingImageKind.Logo, Encoding.UTF8.GetBytes(svg)));

        Assert.Equal("branding.svg_not_allowed", hata.MessageKey);
    }

    [Fact]
    public void Utf8_BOM_ile_baslayan_svg_de_yakalanir()
    {
        var govde = new List<byte> { 0xEF, 0xBB, 0xBF };
        govde.AddRange(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));

        var hata = Assert.Throws<AppException>(() =>
            BrandingImageRules.Validate(BrandingImageKind.Logo, govde.ToArray()));

        Assert.Equal("branding.svg_not_allowed", hata.MessageKey);
    }

    [Theory]
    [InlineData("<html><body><script>alert(1)</script></body></html>")]
    [InlineData("MZ\u0090\u0000\u0003")]                 // çalıştırılabilir
    [InlineData("%PDF-1.7")]                             // belge
    [InlineData("GIF8xa uydurma")]                       // imzaya BENZEYEN ama geçersiz
    public void Resim_olmayan_icerik_reddedilir(string icerik)
    {
        // KARAR YALNIZ İÇERİKTEN VERİLİR: dosya adı ve istemcinin bildirdiği içerik tipi
        // yükleyenin yazdığı metinlerdir, hiçbir yerde okunmazlar.
        var hata = Assert.Throws<AppException>(() =>
            BrandingImageRules.Validate(BrandingImageKind.Logo, Encoding.UTF8.GetBytes(icerik)));

        Assert.Equal("branding.image_type_not_allowed", hata.MessageKey);
    }

    [Fact]
    public void Bos_dosya_reddedilir()
    {
        var hata = Assert.Throws<AppException>(() => BrandingImageRules.Validate(BrandingImageKind.Logo, []));
        Assert.Equal("branding.image_empty", hata.MessageKey);
    }

    [Fact]
    public void Boyut_siniri_asilirsa_reddedilir()
    {
        var buyukLogo = new byte[BrandingImageRules.MaxLogoBytes + 1];
        Png().CopyTo(buyukLogo.AsSpan());
        var hata = Assert.Throws<AppException>(() => BrandingImageRules.Validate(BrandingImageKind.Logo, buyukLogo));
        Assert.Equal("branding.image_too_large", hata.MessageKey);
        Assert.Equal(413, hata.HttpStatusCode);

        // Favicon sınırı AYRI ve daha dar: logo sınırına sığan bir dosya favicon olarak reddedilir.
        var buyukFavicon = new byte[BrandingImageRules.MaxFaviconBytes + 1];
        Png().CopyTo(buyukFavicon.AsSpan());
        Assert.Equal(
            "branding.image_too_large",
            Assert.Throws<AppException>(() => BrandingImageRules.Validate(BrandingImageKind.Favicon, buyukFavicon)).MessageKey);
    }

    [Fact]
    public void Favicon_yalniz_ico_ve_png_kabul_eder()
    {
        Assert.Equal("image/x-icon", BrandingImageRules.Validate(BrandingImageKind.Favicon, Ico()).MediaType);
        Assert.Equal("image/png", BrandingImageRules.Validate(BrandingImageKind.Favicon, Png()).MediaType);

        // JPEG favicon pratikte yanlışlıkla yüklenmiş bir logodur; sekme ikonu olarak kabul edilmez.
        Assert.Throws<AppException>(() => BrandingImageRules.Validate(BrandingImageKind.Favicon, Jpeg()));
    }

    [Fact]
    public void Ico_imzasini_taklit_eden_dosya_yapisal_denetime_takilir()
    {
        // Ham ICO imzası yalnız 4 bayttır (00 00 01 00) ve ikisi sıfırdır; rastgele bir ikili
        // dosya buna kolayca uyar. Başlıktaki görsel sayısı dosyaya SIĞMIYORSA gerçek ICO değildir.
        var sahte = new byte[22];
        sahte[2] = 0x01;
        sahte[4] = 0xFF; // 255 görsel iddiası → 6 + 255*16 bayt gerekirdi
        sahte[5] = 0x00;

        Assert.Throws<AppException>(() => BrandingImageRules.Validate(BrandingImageKind.Favicon, sahte));
    }

    // ---------------------------------------------------------------------------------------
    // 2) METİN DOĞRULAMASI (CSS / adres / HTML yorumlayıcılarına giden değerler)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Vurgu_rengi_yalniz_alti_haneli_hex_kabul_eder()
    {
        Assert.Equal("#3366ff", BrandingTextRules.NormalizeAccentColor("#3366FF"));
        Assert.Equal(string.Empty, BrandingTextRules.NormalizeAccentColor("  "));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#3366ff; background-image: url(https://saldirgan/iz)")]
    [InlineData("var(--x)")]
    [InlineData("expression(alert(1))")]
    public void Vurgu_renginde_stil_enjeksiyonu_kabul_edilmez(string deger)
    {
        // Bu değer tarayıcıda bir CSS özel değişkenine yazılıyor: serbest metin kabul edilseydi
        // bir bayi, müşterisinin ekranındaki stilleri değiştirebilirdi.
        var hata = Assert.Throws<AppException>(() => BrandingTextRules.NormalizeAccentColor(deger));
        Assert.Equal("branding.invalid_accent_color", hata.MessageKey);
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("/panel/ayarlar")]
    [InlineData("ornek.com")]
    public void Logo_baglantisinda_yalniz_http_ve_https_kabul_edilir(string adres)
    {
        var hata = Assert.Throws<AppException>(() => BrandingTextRules.NormalizeLinkUrl(adres));
        Assert.Equal("branding.invalid_logo_url", hata.MessageKey);
    }

    [Fact]
    public void Mesru_logo_baglantisi_korunur()
    {
        Assert.Equal("https://ornek.com/", BrandingTextRules.NormalizeLinkUrl("https://ornek.com"));
        Assert.Equal("http://ornek.com/yol", BrandingTextRules.NormalizeLinkUrl("  http://ornek.com/yol  "));
        Assert.Equal(string.Empty, BrandingTextRules.NormalizeLinkUrl(null));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Ada<b>Panel")]
    [InlineData("Ada\u0000Panel")]
    public void Panel_adinda_isaretleme_ve_kontrol_karakteri_kabul_edilmez(string ad)
    {
        var hata = Assert.Throws<AppException>(() => BrandingTextRules.NormalizePanelName(ad));
        Assert.Equal("branding.invalid_panel_name", hata.MessageKey);
    }

    [Fact]
    public void Mesru_panel_adi_korunur()
    {
        Assert.Equal("Örnek Hosting", BrandingTextRules.NormalizePanelName("  Örnek Hosting  "));
        Assert.Throws<AppException>(() => BrandingTextRules.NormalizePanelName(new string('a', 65)));
    }

    // ---------------------------------------------------------------------------------------
    // 3) KAPSAM ÇÖZÜMÜ — "bu kullanıcı KİMİN markasını görür"
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Kimliksiz_istek_ve_yonetici_sunucu_markasini_gorur()
    {
        await SunucuMarkasiKur();

        var girisEkrani = await servis.GetEffectiveAsync(null, null);
        var yoneticiGorunumu = await servis.GetEffectiveAsync(Yonetici, UserRole.Administrator);

        Assert.Equal("Sağlayıcı", girisEkrani.PanelName);
        Assert.Equal("server", girisEkrani.Scope);
        Assert.Equal("Sağlayıcı", yoneticiGorunumu.PanelName);
    }

    [Fact]
    public async Task Bayi_kendi_markasini_gorur()
    {
        await SunucuMarkasiKur();
        await BayiMarkasiKur(BayiA, "Bayi A Hosting");

        var gorunum = await servis.GetEffectiveAsync(BayiA, UserRole.Reseller);

        Assert.Equal("Bayi A Hosting", gorunum.PanelName);
        Assert.Equal("reseller", gorunum.Scope);
    }

    [Fact]
    public async Task Musteri_KENDISINI_BARINDIRAN_bayinin_markasini_gorur()
    {
        await SunucuMarkasiKur();
        await BayiMarkasiKur(BayiA, "Bayi A Hosting");
        await BayiMarkasiKur(BayiB, "Bayi B Hosting");

        var a = await servis.GetEffectiveAsync(MusteriA, UserRole.Customer);
        var b = await servis.GetEffectiveAsync(MusteriB, UserRole.Customer);

        Assert.Equal("Bayi A Hosting", a.PanelName);
        Assert.Equal("Bayi B Hosting", b.PanelName);
    }

    [Fact]
    public async Task Alt_kullanici_ebeveyninin_bayisinin_markasini_gorur()
    {
        // Alt kullanıcının kendi sahiplik kaydı yoktur. Ebeveyne sıçranmasaydı, aynı abonelikteki
        // iki kullanıcı FARKLI marka görürdü.
        await SunucuMarkasiKur();
        await BayiMarkasiKur(BayiA, "Bayi A Hosting");

        var gorunum = await servis.GetEffectiveAsync(AltKullaniciA, UserRole.Customer);

        Assert.Equal("Bayi A Hosting", gorunum.PanelName);
    }

    [Fact]
    public async Task Bayisi_olmayan_musteri_sunucu_markasini_gorur()
    {
        await SunucuMarkasiKur();

        var gorunum = await servis.GetEffectiveAsync(SahipsizMusteri, UserRole.Customer);

        Assert.Equal("Sağlayıcı", gorunum.PanelName);
        Assert.Equal("server", gorunum.Scope);
    }

    [Fact]
    public async Task Markalanmamis_bayinin_musterisi_sunucu_markasina_duser()
    {
        await SunucuMarkasiKur();
        // BayiA hiçbir şey ayarlamadı.

        var gorunum = await servis.GetEffectiveAsync(MusteriA, UserRole.Customer);

        Assert.Equal("Sağlayıcı", gorunum.PanelName);
    }

    [Fact]
    public async Task Bayinin_eksik_alani_SUNUCUNUN_logosuyla_TAMAMLANMAZ()
    {
        // BEYAZ ETİKETİN BÜTÜN NOKTASI BU. Bayi adını koymuş ama logo yüklememişse, müşterisine
        // sağlayıcının logosunu göstermek tam da gizlenmesi istenen şeyi göstermek olur.
        await SunucuMarkasiKur();
        await servis.UpdateAsync(new BrandingScope(BayiA), new UpdateBrandingRequest { PanelName = "Bayi A Hosting" });

        var gorunum = await servis.GetEffectiveAsync(MusteriA, UserRole.Customer);

        Assert.Equal("Bayi A Hosting", gorunum.PanelName);
        Assert.Equal(string.Empty, gorunum.LogoDataUrl);
        Assert.Equal(string.Empty, gorunum.AccentColor);
    }

    [Fact]
    public async Task Bir_bayinin_markasi_digerini_ETKILEMEZ()
    {
        await BayiMarkasiKur(BayiA, "Bayi A Hosting");
        await servis.SetImageAsync(new BrandingScope(BayiA), BrandingImageKind.Logo, Png());

        var bGorunumu = await servis.GetEffectiveAsync(MusteriB, UserRole.Customer);
        var bKaydi = await servis.GetScopeAsync(new BrandingScope(BayiB));

        Assert.Equal(string.Empty, bGorunumu.PanelName);
        Assert.Equal(string.Empty, bGorunumu.LogoDataUrl);
        Assert.Equal(string.Empty, bKaydi.PanelName);
    }

    // ---------------------------------------------------------------------------------------
    // 4) DEPOLAMA DAVRANIŞI
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Yuklenen_logo_dogrulanmis_tiple_data_URI_olarak_doner()
    {
        var kayit = await servis.SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Png());

        Assert.StartsWith("data:image/png;base64,", kayit.LogoDataUrl, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Png()), kayit.LogoDataUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logo_degistiginde_ESKI_dosya_diskte_birakilmaz()
    {
        await servis.SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Png());
        await servis.SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Jpeg());

        var klasor = Path.Combine(veriKoku, "branding", "server");
        var dosyalar = Directory.GetFiles(klasor).Select(yol => Path.GetFileName(yol) ?? string.Empty).ToArray();

        Assert.Equal(new[] { "logo.jpg" }, dosyalar);
    }

    [Fact]
    public async Task Marka_kaldirilinca_dosya_ve_kayit_temizlenir()
    {
        await servis.SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Png());
        await servis.RemoveImageAsync(BrandingScope.Server, BrandingImageKind.Logo);

        var kayit = await servis.GetScopeAsync(BrandingScope.Server);
        Assert.Equal(string.Empty, kayit.LogoDataUrl);
        Assert.Empty(Directory.GetFiles(Path.Combine(veriKoku, "branding", "server")));

        // Kayıt tamamen boşaldığı için dosyada da kalmamalı: "kaydı var ama boş" hâli, etkin
        // marka çözümünde bayiyi markalanmış saydırırdı.
        var ham = await File.ReadAllTextAsync(Path.Combine(veriKoku, "config", "branding.json"));
        Assert.DoesNotContain("logo", ham, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Yuklenen_dosya_veri_kokunun_disina_yazilmaz()
    {
        // Yüklenen içerik panelin SUNULAN hiçbir yoluna (wwwroot, site kökleri) düşmemeli.
        await servis.SetImageAsync(new BrandingScope(BayiA), BrandingImageKind.Logo, Png());

        var beklenen = Path.Combine(veriKoku, "branding", BayiA.ToString("N"), "logo.png");
        Assert.True(File.Exists(beklenen), $"Logo beklenen yolda değil: {beklenen}");
    }

    [Fact]
    public async Task Gecersiz_metin_HICBIR_alani_kaydetmez()
    {
        // Doğrulama yazmadan önce ve hep birlikte yapılır; yarı uygulanmış marka olmamalı.
        await Assert.ThrowsAsync<AppException>(() => servis.UpdateAsync(
            BrandingScope.Server,
            new UpdateBrandingRequest { PanelName = "Sağlayıcı", AccentColor = "red" }));

        var kayit = await servis.GetScopeAsync(BrandingScope.Server);
        Assert.Equal(string.Empty, kayit.PanelName);
    }

    // ---------------------------------------------------------------------------------------
    // 5) DENETLEYİCİ KAPSAM KAPISI — izolasyonun tek dayanağı
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Bayi_yalniz_KENDI_kapsamina_yazabilir()
    {
        var kapsamA = BrandingController.ResolveScope(Kimlik(BayiA, UserRole.Reseller));
        var kapsamB = BrandingController.ResolveScope(Kimlik(BayiB, UserRole.Reseller));

        Assert.Equal(BayiA, kapsamA.ResellerId);
        Assert.Equal(BayiB, kapsamB.ResellerId);
        Assert.False(kapsamA.IsServer);
    }

    [Fact]
    public void Yonetici_sunucu_kapsamina_yazar()
    {
        var kapsamSonucu = BrandingController.ResolveScope(Kimlik(Yonetici, UserRole.Administrator));

        Assert.True(kapsamSonucu.IsServer);
        Assert.Equal("server", kapsamSonucu.StorageKey);
    }

    [Fact]
    public async Task Bayi_kimligiyle_yazilan_marka_BASKA_hicbir_kapsama_dokunmaz()
    {
        // UÇTAN UCA İZOLASYON: kimlik → kapsam → depo. Bayi B'nin oturumuyla yazılan marka, ne
        // bayi A'nın ne de SUNUCUNUN kaydını değiştirebilmeli.
        //
        // Kapsam çözümünü tek başına ölçen test bunu göstermez: çözüm bozulup herkes sunucuya
        // yazacak hâle gelse yalnız o test kırmızıya döner ve "bayi, sağlayıcının markasını ezdi"
        // gerçeği ölçülmemiş kalırdı. Beyaz etiketli bir üründe bu, tek bir kiracının bütün
        // panelin görünümünü devralması demektir.
        await SunucuMarkasiKur();
        await BayiMarkasiKur(BayiA, "Bayi A Hosting");

        var bKapsami = BrandingController.ResolveScope(Kimlik(BayiB, UserRole.Reseller));
        await servis.UpdateAsync(bKapsami, new UpdateBrandingRequest { PanelName = "Bayi B Hosting" });
        await servis.SetImageAsync(bKapsami, BrandingImageKind.Logo, Jpeg());

        var sunucu = await servis.GetScopeAsync(BrandingScope.Server);
        var aKaydi = await servis.GetScopeAsync(new BrandingScope(BayiA));

        Assert.Equal("Sağlayıcı", sunucu.PanelName);
        Assert.Equal(string.Empty, sunucu.LogoDataUrl);
        Assert.Equal("Bayi A Hosting", aKaydi.PanelName);
        Assert.Equal(string.Empty, aKaydi.LogoDataUrl);

        // Yazının KENDİ kapsamına düştüğü de doğrulanır: hiç yazmamak da bu testi geçirmemeli.
        var bKaydi = await servis.GetScopeAsync(new BrandingScope(BayiB));
        Assert.Equal("Bayi B Hosting", bKaydi.PanelName);
        Assert.StartsWith("data:image/jpeg;base64,", bKaydi.LogoDataUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Kimliksiz_izleyici_rolsuz_cozulur()
    {
        var (userId, role) = BrandingController.ResolveViewer(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Null(userId);
        Assert.Null(role);
    }

    [Fact]
    public void Izleyici_kimligi_ve_rolu_taleplerden_okunur()
    {
        var (userId, role) = BrandingController.ResolveViewer(Kimlik(MusteriA, UserRole.Customer));

        Assert.Equal(MusteriA, userId);
        Assert.Equal(UserRole.Customer, role);
    }

    // ---------------------------------------------------------------------------------------
    // 6) ARAYÜZ: marka metni HTML olarak YORUMLANMAMALI
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Marka_degerleri_arayuzde_v_html_ile_basilmaz()
    {
        // Marka adı kullanıcı girdisidir. Panelde `v-html` kullanımı VAR (menü ikonları), bu yüzden
        // "hiç kullanılmasın" denemez; ölçülen şey, marka alanlarının o yola HİÇ girmemesi.
        var suclular = new List<string>();

        foreach (var dosya in ArayuzDosyalari())
        {
            foreach (var satir in File.ReadAllLines(dosya))
            {
                if (!satir.Contains("v-html", StringComparison.Ordinal))
                {
                    continue;
                }

                if (satir.Contains("brand", StringComparison.OrdinalIgnoreCase) ||
                    satir.Contains("panelName", StringComparison.Ordinal) ||
                    satir.Contains("logo", StringComparison.OrdinalIgnoreCase))
                {
                    suclular.Add($"{Path.GetFileName(dosya)}: {satir.Trim()}");
                }
            }
        }

        Assert.True(suclular.Count == 0, "Marka değeri v-html ile basılıyor:\n  " + string.Join("\n  ", suclular));
    }

    [Fact]
    public void Taranan_arayuz_dosyalari_gercekten_bulunmali()
    {
        // Dosyalar bulunamazsa yukarıdaki test sessizce yeşil kalır ve hiçbir şeyi korumaz.
        var dosyalar = ArayuzDosyalari();
        Assert.NotEmpty(dosyalar);
        Assert.All(dosyalar, dosya => Assert.True(new FileInfo(dosya).Length > 1000));
    }

    private static IReadOnlyList<string> ArayuzDosyalari()
    {
        var kok = DepoKoku();
        return new[]
        {
            Path.Combine(kok, "frontend", "src", "views", "SetupWizardView.vue"),
            Path.Combine(kok, "frontend", "src", "layouts", "MainLayout.vue")
        }.Where(File.Exists).ToArray();
    }

    private static string DepoKoku()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    // --- yardımcılar ---

    private Task SunucuMarkasiKur() =>
        servis.UpdateAsync(BrandingScope.Server, new UpdateBrandingRequest
        {
            PanelName = "Sağlayıcı",
            AccentColor = "#112233"
        });

    private Task BayiMarkasiKur(Guid bayiId, string ad) =>
        servis.UpdateAsync(new BrandingScope(bayiId), new UpdateBrandingRequest { PanelName = ad });

    private static ClaimsPrincipal Kimlik(Guid userId, UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            "test"));

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static byte[] Gif() => Encoding.ASCII.GetBytes("GIF89a\u0001\u0000\u0001\u0000");

    private static byte[] Webp()
    {
        var govde = new List<byte>();
        govde.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        govde.AddRange([0x1A, 0x00, 0x00, 0x00]);
        govde.AddRange(Encoding.ASCII.GetBytes("WEBP"));
        return govde.ToArray();
    }

    private static byte[] Ico()
    {
        var govde = new byte[22];
        govde[2] = 0x01; // tip = ikon
        govde[4] = 0x01; // 1 görsel → 6 + 16 = 22 bayt, dosyaya sığar
        return govde;
    }

    /// <summary>
    /// Kiracı grafiğinin sahtesi: marka çözümünde YALNIZ iki metot kullanılır. Diğerleri
    /// çağrılırsa test yanlış kurulmuş demektir → sessizce geçmek yerine patlar.
    /// </summary>
    private sealed class SahteKapsam : ICustomerScopeService
    {
        public Dictionary<Guid, Guid> Sahipler { get; } = [];

        public Dictionary<Guid, Guid> Ebeveynler { get; } = [];

        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Sahipler.TryGetValue(userId, out var sahip) ? sahip : (Guid?)null);

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Ebeveynler.TryGetValue(userId, out var ebeveyn) ? ebeveyn : (Guid?)null);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
