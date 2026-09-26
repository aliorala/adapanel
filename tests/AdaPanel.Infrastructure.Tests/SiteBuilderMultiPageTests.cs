using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Infrastructure.SiteBuilder;
using Xunit;

using TestOrtami = AdaPanel.Infrastructure.Tests.SiteBuilderTestOrtami;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÇOK SAYFA: sayfa adresinin güvenliği, sayfalar arası gezinme ve "artık üretilmeyen sayfa"
/// sorusunun SİLMEDEN çözülmesi.
/// </summary>
/// <remarks>
/// <para>
/// Çok sayfa ilk turda bilerek kapsam dışı bırakılmıştı; kayıtlı gerekçe şuydu: <i>"artık
/// üretilmeyen sayfa" sorusu SİLME'ye bakar ve silme bu depoda müşteri sitesini yok edebilecek
/// hata sınıfıdır.</i> Gerekçe hâlâ geçerli, bu yüzden yok sayılmadı — çözüldü:
/// </para>
/// <list type="number">
/// <item>Yayın hiçbir dosyayı SİLMEZ (bu dosyada ayrıca sabitleniyor).</item>
/// <item>
/// Artık üretilmeyen kurucu dosyaları ÖKSÜZ olarak listelenir ve kullanıcıya gösterilir; karar
/// dosyanın sahibinindir.
/// </item>
/// <item>
/// Sayfa adresi (slug) kapalı bir izin listesinden geçer: klasör adına ve <c>href</c>'e giren
/// tek kullanıcı dizgesi budur.
/// </item>
/// </list>
/// </remarks>
public sealed class SiteBuilderMultiPageTests
{
    [Fact]
    public async Task Gorsel_silme_ana_ve_ek_sayfalardaki_referanslari_temizler()
    {
        await using var ortam = await TestOrtami.KurAsync();
        var image = await ortam.GorselYukleAsync();
        var project = await ortam.Servis.GetProjectAsync(TestOrtami.SiteAdi);
        project.Sections[0].ImageName = image.Name;
        project.Pages = [new SiteBuilderPageDto
        {
            Slug = "galeri", Title = "Galeri",
            Sections = [new SiteBuilderSectionDto
            {
                Type = "gallery", Enabled = true, ImageName = image.Name,
                Items = [new SiteBuilderItemDto { Title = "Görsel", ImageName = image.Name }]
            }]
        }];
        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = project.SiteTitle, Sections = project.Sections, Pages = project.Pages
        });

        await ortam.Servis.DeleteImageAsync(TestOrtami.SiteAdi, image.Name);

        var saved = await ortam.Servis.GetProjectAsync(TestOrtami.SiteAdi);
        Assert.Empty(await ortam.Servis.GetImagesAsync(TestOrtami.SiteAdi));
        Assert.All(saved.Sections.Concat(saved.Pages.SelectMany(page => page.Sections)), section =>
        {
            Assert.Empty(section.ImageName);
            Assert.All(section.Items, item => Assert.Empty(item.ImageName));
        });
        Assert.Equal("Galeri", saved.Pages.Single().Title);
    }

    // ------------------------------------------------------------------ sayfa adresi (slug)

    [Theory]
    [InlineData("../gizli")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e")]
    [InlineData("sayfa.html")]
    [InlineData("web.config")]
    [InlineData("Hakkimizda")]        // büyük harf: aynı dosyaya düşen iki ayrı sayfa üretirdi
    [InlineData("-onde-tire")]
    [InlineData("sonda-tire-")]
    [InlineData("bosluk var")]
    [InlineData("tirnak\"var")]
    [InlineData("")]
    [InlineData("   ")]
    public void Tehlikeli_slug_REDDEDILIR(string slug)
    {
        Assert.False(SiteBuilderPageSlug.IsValid(slug));
    }

    [Theory]
    [InlineData("assets")]      // kurucunun kendi varlık klasörü
    [InlineData("app_data")]    // NeverTouch + IIS'in sunmadığı klasör
    [InlineData("bin")]
    [InlineData("con")]         // Windows ayrılmış aygıt adı
    [InlineData("lpt1")]
    public void Ayrilmis_ad_slug_OLAMAZ(string slug)
    {
        Assert.False(SiteBuilderPageSlug.IsValid(slug));
    }

    [Theory]
    [InlineData("hakkimizda")]
    [InlineData("iletisim-formu")]
    [InlineData("2024")]
    [InlineData("a")]
    public void Gecerli_slug_KABUL_EDILIR(string slug)
    {
        Assert.True(SiteBuilderPageSlug.IsValid(slug));
        Assert.Equal($"{slug}/index.html", SiteBuilderPageSlug.ToRelativeFile(slug));
        Assert.Equal($"{slug}/", SiteBuilderPageSlug.ToHref(slug));
    }

    // ------------------------------------------------------------------ üretim

    [Fact]
    public void Ek_sayfa_KENDI_dosyasina_uretilir()
    {
        var uretilen = SiteBuilderRenderer.RenderAll(Tasarim(Sayfa("hakkimizda", "Hakkımızda")));

        Assert.Equal(2, uretilen.Pages.Count);
        Assert.Equal("index.html", uretilen.Pages[0].RelativePath);
        Assert.Equal("hakkimizda/index.html", uretilen.Pages[1].RelativePath);
    }

    [Fact]
    public void Alt_sayfa_stil_ve_gorseli_BIR_UST_klasorden_ister()
    {
        // Alt sayfa "hakkimizda\index.html" olarak yazılıyor; göreli adres bir üst klasöre
        // çıkmazsa sayfa STİLSİZ açılır ve görselleri kırık görünür. Bu, kullanıcının önizlemede
        // hiç göremeyeceği ama ziyaretçinin ilk saniyede göreceği bir arıza.
        var tasarim = Tasarim(Sayfa("hakkimizda", "Hakkımızda"));
        var uretilen = SiteBuilderRenderer.RenderAll(tasarim, SiteBuilderRenderContext.Published);

        Assert.Contains("href=\"assets/adapanel/site.css\"", uretilen.Pages[0].Html, StringComparison.Ordinal);
        Assert.Contains("href=\"../assets/adapanel/site.css\"", uretilen.Pages[1].Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sayfalar_arasi_gezinme_HER_sayfada_dogru_yonu_gosterir()
    {
        var tasarim = Tasarim(Sayfa("hakkimizda", "Hakkımızda"), Sayfa("iletisim", "İletişim"));
        var uretilen = SiteBuilderRenderer.RenderAll(tasarim, SiteBuilderRenderContext.Published);

        var ana = uretilen.Pages[0].Html;
        Assert.Contains("href=\"hakkimizda/\"", ana, StringComparison.Ordinal);
        Assert.Contains("href=\"iletisim/\"", ana, StringComparison.Ordinal);

        var alt = uretilen.Pages[1].Html;
        Assert.Contains("href=\"../\"", alt, StringComparison.Ordinal);          // ana sayfaya dönüş
        Assert.Contains("href=\"../iletisim/\"", alt, StringComparison.Ordinal); // kardeş sayfaya
        // Bulunulan sayfa kendine BAĞLANMAZ; ekran okuyucuya konumu söylenir.
        Assert.Contains("aria-current=\"page\"", alt, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"../hakkimizda/\"", alt, StringComparison.Ordinal);
    }

    [Fact]
    public void Menusuz_sablonda_bile_cok_sayfa_MENU_basar()
    {
        // "sade" şablonu ShowNav=false. Menü basılmasaydı, kullanıcının oluşturduğu ek sayfalara
        // ulaşmanın tek yolu adresi elle yazmak olurdu — yani ziyaretçi o sayfaları hiç görmez ve
        // kullanıcı bunu fark etmezdi. Ulaşılamayan sayfa üretmek, sayfa üretmemekten kötü.
        var tekSayfa = SiteBuilderRenderer.RenderAll(Tasarim()).Pages[0].Html;
        Assert.DoesNotContain("site-nav", tekSayfa, StringComparison.Ordinal);

        var cokSayfa = SiteBuilderRenderer.RenderAll(Tasarim(Sayfa("hakkimizda", "Hakkımızda"))).Pages[0].Html;
        Assert.Contains("site-nav", cokSayfa, StringComparison.Ordinal);
    }

    [Fact]
    public void Menude_gizli_sayfa_MENUDE_gorunmez_ama_URETILIR()
    {
        var gizli = Sayfa("gizli", "Gizli");
        gizli.ShowInNav = false;

        var uretilen = SiteBuilderRenderer.RenderAll(Tasarim(gizli, Sayfa("acik", "Açık")));

        Assert.Equal(3, uretilen.Pages.Count);
        Assert.DoesNotContain("href=\"gizli/\"", uretilen.Pages[0].Html, StringComparison.Ordinal);
        Assert.Contains("href=\"acik/\"", uretilen.Pages[0].Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sayfa_basligi_KACIRILIR()
    {
        var uretilen = SiteBuilderRenderer.RenderAll(
            Tasarim(Sayfa("hakkimizda", "<script>alert(1)</script>")));

        foreach (var sayfa in uretilen.Pages)
        {
            Assert.DoesNotContain("<script>", sayfa.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Yinelenen_slug_IKINCI_sayfayi_dusurur()
    {
        // Aynı slug'lı iki sayfa aynı dosyaya yazılır; ikincisi birincisini EZER ve kullanıcı
        // yazdığı içeriğin nereye gittiğini anlayamaz.
        var uretilen = SiteBuilderRenderer.RenderAll(
            Tasarim(Sayfa("hakkimizda", "Birinci"), Sayfa("hakkimizda", "Ikinci")));

        Assert.Equal(2, uretilen.Pages.Count);
        Assert.Contains("Birinci", uretilen.Pages[1].Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ikinci", uretilen.Pages[1].Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Gecersiz_sluglu_sayfa_URETILMEZ()
    {
        var uretilen = SiteBuilderRenderer.RenderAll(Tasarim(Sayfa("../kacis", "Kotu")));
        Assert.Single(uretilen.Pages);
    }

    // ------------------------------------------------------------------ kaydetme

    [Fact]
    public async Task Kaydetme_gecersiz_ve_yinelenen_sayfalari_DUSURUR()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var tasarim = await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages =
            [
                new SiteBuilderPageDto { Slug = "HAKKIMIZDA", Title = "Hakkımızda" },
                new SiteBuilderPageDto { Slug = "hakkimizda", Title = "Kopya" },
                new SiteBuilderPageDto { Slug = "../kacis", Title = "Kotu" },
                new SiteBuilderPageDto { Slug = "assets", Title = "Cakisan" },
                new SiteBuilderPageDto { Slug = "iletisim", Title = "İletişim" }
            ]
        });

        // "HAKKIMIZDA" küçük harfe indirilir; ikinci kopya, kaçış ve ayrılmış ad düşer.
        Assert.Equal(["hakkimizda", "iletisim"], tasarim.Pages.Select(sayfa => sayfa.Slug));
    }

    [Fact]
    public async Task Sayfa_sayisi_UST_SINIRA_uyar()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var tasarim = await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = Enumerable.Range(1, SiteBuilderPageSlug.MaxPages + 5)
                .Select(index => new SiteBuilderPageDto { Slug = $"sayfa-{index}", Title = $"Sayfa {index}" })
                .ToList()
        });

        Assert.Equal(SiteBuilderPageSlug.MaxPages, tasarim.Pages.Count);
    }

    // ------------------------------------------------------------------ öksüz dosyalar

    [Fact]
    public void Oksuz_listesi_YALNIZ_kendi_yazdiklarimizi_sayar()
    {
        // Müşterinin kendi dosyası ASLA öksüz sayılmaz: sayılsaydı, liste bir gün bir "temizle"
        // düğmesine bağlandığında siteyi silen şey tam olarak o olurdu.
        var oksuzler = SiteBuilderPublishPlanner.Orphans(
            managedFiles: ["index.html", "eski-sayfa/index.html", "assets/adapanel/img/a.jpg"],
            filesToWrite: ["index.html", "assets/adapanel/site.css"]);

        Assert.Equal(["assets/adapanel/img/a.jpg", "eski-sayfa/index.html"], oksuzler);
    }

    [Fact]
    public void Oksuz_hesabi_yol_ayirici_farkindan_ETKILENMEZ()
    {
        var oksuzler = SiteBuilderPublishPlanner.Orphans(
            managedFiles: ["assets\\adapanel\\site.css"],
            filesToWrite: ["assets/adapanel/site.css"]);

        Assert.Empty(oksuzler);
    }

    [Fact]
    public async Task Kaldirilan_sayfa_SILINMEZ_ama_OKSUZ_olarak_bildirilir()
    {
        await using var ortam = await TestOrtami.KurAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = [new SiteBuilderPageDto { Slug = "hakkimizda", Title = "Hakkımızda" }]
        });

        var ilk = await ortam.Servis.PublishAsync(
            TestOrtami.SiteAdi,
            new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        var sayfaDosyasi = Path.Combine(ortam.SiteKoku, "hakkimizda", "index.html");
        Assert.Equal(2, ilk.PageCount);
        Assert.True(File.Exists(sayfaDosyasi));
        Assert.Empty(ilk.OrphanFiles);

        // Kullanıcı sayfayı kurucudan KALDIRIYOR.
        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = []
        });

        var plan = await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi);
        Assert.Contains("hakkimizda/index.html", plan.OrphanFiles);
        Assert.DoesNotContain("hakkimizda/index.html", plan.FilesToWrite);

        var ikinci = await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest());

        // ÇEKİRDEK İDDİA: dosya HÂLÂ DURUYOR (silme yok) ama kullanıcıya ADIYLA bildiriliyor.
        Assert.True(File.Exists(sayfaDosyasi));
        Assert.Contains("hakkimizda/index.html", ikinci.OrphanFiles);
        Assert.Equal(1, ikinci.PageCount);
    }

    [Fact]
    public async Task Elle_silinen_oksuz_ARTIK_bildirilmez_ve_yolu_yeniden_KORUNUR()
    {
        await using var ortam = await TestOrtami.KurAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = [new SiteBuilderPageDto { Slug = "hakkimizda", Title = "Hakkımızda" }]
        });
        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = []
        });

        // Kullanıcı dosyayı dosya yöneticisinden siliyor ve AYNI yola kendi sayfasını koyuyor.
        var sayfaDosyasi = Path.Combine(ortam.SiteKoku, "hakkimizda", "index.html");
        File.Delete(sayfaDosyasi);
        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest());
        await File.WriteAllTextAsync(sayfaDosyasi, "MUSTERININ ELLE YAZDIGI SAYFA");

        // Sayfayı kurucuya geri ekliyor: artık o yol BİZİM sayılmamalı, yoksa müşterinin
        // dosyası UYARISIZ ezilirdi.
        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages = [new SiteBuilderPageDto { Slug = "hakkimizda", Title = "Hakkımızda" }]
        });

        var plan = await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi);
        Assert.Contains("hakkimizda/index.html", plan.Conflicts);
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal("MUSTERININ ELLE YAZDIGI SAYFA", await File.ReadAllTextAsync(sayfaDosyasi));
    }

    [Fact]
    public async Task Cok_sayfali_yayin_HER_sayfayi_diske_yazar()
    {
        await using var ortam = await TestOrtami.KurAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Sections = [new SiteBuilderSectionDto { Type = "hero", Enabled = true, Heading = "ANA-SAYFA-METNI" }],
            Pages =
            [
                new SiteBuilderPageDto
                {
                    Slug = "hakkimizda",
                    Title = "Hakkımızda",
                    Sections = [new SiteBuilderSectionDto { Type = "about", Enabled = true, Body = "Biz kimiz" }]
                }
            ]
        });

        var sonuc = await ortam.Servis.PublishAsync(
            TestOrtami.SiteAdi,
            new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        // Bildirilen HER dosya gerçekten diskte: "yayınlandı" demek yetmez.
        foreach (var göreli in sonuc.WrittenFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(ortam.SiteKoku, göreli.Replace('/', Path.DirectorySeparatorChar))),
                $"Bildirilen dosya diskte yok: {göreli}");
        }

        var altSayfa = await File.ReadAllTextAsync(Path.Combine(ortam.SiteKoku, "hakkimizda", "index.html"));
        Assert.Contains("Biz kimiz", altSayfa, StringComparison.Ordinal);

        // Alt sayfa ana sayfanın İÇERİĞİNİ taşımaz. Taşısaydı çok sayfa özelliği görünüşte
        // çalışır, gerçekte aynı sayfanın kopyalarını üretirdi.
        Assert.DoesNotContain("ANA-SAYFA-METNI", altSayfa, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ek_sayfanin_gorseli_de_KOPYALANIR()
    {
        await using var ortam = await TestOrtami.KurAsync();
        var gorsel = await ortam.GorselYukleAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Pages =
            [
                new SiteBuilderPageDto
                {
                    Slug = "galeri",
                    Title = "Galeri",
                    Sections =
                    [
                        new SiteBuilderSectionDto { Type = "about", Enabled = true, ImageName = gorsel.Name }
                    ]
                }
            ]
        });

        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        // Ek sayfaların görselleri unutulsaydı sayfa KIRIK görsellerle yayınlanır ve dosya bir
        // sonraki yayında ÖKSÜZ sanılırdı.
        Assert.True(File.Exists(Path.Combine(ortam.SiteKoku, "assets", "adapanel", "img", gorsel.Name)));
    }

    [Fact]
    public async Task Onizleme_istenen_SAYFAYI_gosterir_bilinmeyende_ana_sayfaya_duser()
    {
        await using var ortam = await TestOrtami.KurAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Sections = [new SiteBuilderSectionDto { Type = "hero", Enabled = true, Heading = "ANA-SAYFA-METNI" }],
            Pages =
            [
                new SiteBuilderPageDto
                {
                    Slug = "hakkimizda",
                    Title = "Hakkımızda",
                    Sections = [new SiteBuilderSectionDto { Type = "about", Enabled = true, Body = "ALT-SAYFA-METNI" }]
                }
            ]
        });

        Assert.Contains(
            "ALT-SAYFA-METNI",
            await ortam.Servis.RenderPreviewAsync(TestOrtami.SiteAdi, "hakkimizda"),
            StringComparison.Ordinal);

        // Silinmiş bir sayfa arayüzde hâlâ seçili olabilir: önizleme hata vermek yerine çalışır.
        Assert.Contains(
            "ANA-SAYFA-METNI",
            await ortam.Servis.RenderPreviewAsync(TestOrtami.SiteAdi, "boyle-bir-sayfa-yok"),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ yardımcılar

    private static SiteBuilderProjectDto Tasarim(params SiteBuilderPageDto[] sayfalar) => new()
    {
        SiteName = "ornek.com",
        SiteTitle = "Ornek",
        TemplateId = "sade",
        PaletteId = "okyanus",
        FontId = "sistem",
        Language = "tr",
        Sections = [new SiteBuilderSectionDto { Type = "hero", Enabled = true, Heading = "Merhaba" }],
        Pages = sayfalar.ToList()
    };

    private static SiteBuilderPageDto Sayfa(string slug, string baslik) => new()
    {
        Slug = slug,
        Title = baslik,
        ShowInNav = true,
        Sections = [new SiteBuilderSectionDto { Type = "about", Enabled = true, Body = "Metin" }]
    };
}
