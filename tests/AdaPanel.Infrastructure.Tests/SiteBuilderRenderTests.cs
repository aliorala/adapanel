using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Infrastructure.SiteBuilder;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site kurucunun ürettiği sayfa: KULLANICI METNİ İŞARETLEMEYE KAÇIRILARAK girer.
/// </summary>
/// <remarks>
/// <para>
/// Burada korunan şey bir görünüm tercihi değil, DEPOLANMIŞ XSS'tir. Kurucudaki metin panelde
/// saklanır ve yayınlanan sayfada her ziyaretçide yeniden çalışır: kaçırma bir yerde eksik
/// kalırsa müşteri kendi sitesine kalıcı script gömer (ya da paylaşılan bir sunucuda bir kiracı,
/// başka bir kiracının ziyaretçisine). Bu yüzden kaçırma HER alan için ayrı ayrı sabitlendi —
/// "bir yerde çalışıyorsa her yerde çalışır" varsayımı, tam da unutulan alanı kaçırırdı.
/// </para>
/// </remarks>
public sealed class SiteBuilderRenderTests
{
    private const string Yuk = "<script>alert('xss')</script>";

    /// <summary>Kaçırılmış çıktıda bulunmaması gereken ham parçalar.</summary>
    private static void HamScriptYok(string html)
    {
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script", html, StringComparison.OrdinalIgnoreCase);
    }

    private static SiteBuilderProjectDto Tasarim(params SiteBuilderSectionDto[] bolumler) => new()
    {
        SiteName = "ornek.com",
        TemplateId = "sade",
        PaletteId = "okyanus",
        FontId = "sistem",
        SiteTitle = "Ornek",
        Sections = bolumler.ToList()
    };

    [Fact]
    public void Site_basligi_kacirilir()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
        tasarim.SiteTitle = Yuk;

        var html = SiteBuilderRenderer.Render(tasarim).Html;

        HamScriptYok(html);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Slogan_kacirilir()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
        tasarim.Tagline = Yuk;

        HamScriptYok(SiteBuilderRenderer.Render(tasarim).Html);
    }

    [Fact]
    public void Meta_aciklamasi_ozniteliginden_cikilamaz()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
        // Öznitelikten çıkıp yeni bir öznitelik açma denemesi.
        tasarim.MetaDescription = "\" onload=\"alert(1)";

        var html = SiteBuilderRenderer.Render(tasarim).Html;

        Assert.DoesNotContain("onload=\"alert(1)\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&quot; onload=&quot;alert(1)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bolum_basligi_ve_govdesi_kacirilir()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "about", Enabled = true, Heading = Yuk, Body = Yuk })).Html;

        HamScriptYok(html);
    }

    [Fact]
    public void Hizmet_kartlarinin_metni_kacirilir()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto
            {
                Type = "services",
                Enabled = true,
                Items = [new SiteBuilderItemDto { Title = Yuk, Text = Yuk }]
            })).Html;

        HamScriptYok(html);
    }

    [Fact]
    public void Iletisim_alanlari_kacirilir()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto
            {
                Type = "contact",
                Enabled = true,
                Email = Yuk,
                Phone = Yuk,
                Address = Yuk
            })).Html;

        HamScriptYok(html);
    }

    [Fact]
    public void Gezinme_cubugundaki_bolum_adi_kacirilir()
    {
        // Gezinme yalnız "kurumsal" şablonunda basılır; kaçırma orada da geçerli olmalı.
        var tasarim = Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "about", Enabled = true, Heading = Yuk });
        tasarim.TemplateId = "kurumsal";

        var html = SiteBuilderRenderer.Render(tasarim).Html;

        Assert.Contains("site-nav", html, StringComparison.Ordinal);
        HamScriptYok(html);
    }

    [Fact]
    public void Gorsel_alt_metni_kacirilir()
    {
        var tasarim = Tasarim(
            new SiteBuilderSectionDto
            {
                Type = "hero",
                Enabled = true,
                Heading = "\" onerror=\"alert(1)",
                ImageName = new string('a', 32) + ".jpg"
            });

        var html = SiteBuilderRenderer.Render(tasarim).Html;

        Assert.DoesNotContain("onerror=\"alert(1)\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cok_satirli_govde_paragraflara_bolunur_ama_etiket_uretmez()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto
            {
                Type = "about",
                Enabled = true,
                Body = "birinci satir\nikinci satir\n\nikinci paragraf <b>kalin degil</b>"
            })).Html;

        Assert.Contains("birinci satir<br />ikinci satir", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;kalin degil&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bolum_TIPI_kullanici_girdisinden_ALINMAZ()
    {
        // Tip alanı büyük harfle ve fazladan içerikle gelse bile, işaretlemeye katalogdaki
        // kanonik tip yazılır; sınıf adı/çapa üzerinden öznitelik kaçışı mümkün değildir.
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "ABOUT", Enabled = true, Heading = "Hakkinda" })).Html;

        Assert.Contains("class=\"block block-about\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"bolum-about\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("block-ABOUT", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Katalogda_olmayan_bolum_tipi_sayfaya_HIC_girmez()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "uydurma", Enabled = true, Heading = "GIZLI" })).Html;

        Assert.DoesNotContain("GIZLI", html, StringComparison.Ordinal);
        Assert.DoesNotContain("uydurma", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Kapali_bolum_sayfaya_girmez()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "about", Enabled = false, Heading = "GORUNMEMELI" })).Html;

        Assert.DoesNotContain("GORUNMEMELI", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Gorsel_adresi_yalniz_PANELIN_urettigi_addan_kurulur()
    {
        // javascript: şeması ve yol kaçışı denemeleri: ad kalıba uymadığı için görsel HİÇ basılmaz.
        foreach (var kotuAd in new[] { "javascript:alert(1)", "../../web.config", "x.jpg\" onerror=\"alert(1)", "abc.php" })
        {
            var html = SiteBuilderRenderer.Render(Tasarim(
                new SiteBuilderSectionDto { Type = "hero", Enabled = true, ImageName = kotuAd })).Html;

            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Gecerli_gorsel_adi_dogru_klasorden_basilir()
    {
        var ad = new string('b', 32) + ".png";
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true, ImageName = ad })).Html;

        Assert.Contains($"src=\"assets/adapanel/img/{ad}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Gecersiz_eposta_baglanti_URETMEZ()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "contact", Enabled = true, Email = "javascript:alert(1)" })).Html;

        Assert.DoesNotContain("mailto:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gecerli_eposta_mailto_baglantisi_uretir()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "contact", Enabled = true, Email = "bilgi@ornek.com" })).Html;

        Assert.Contains("href=\"mailto:bilgi@ornek.com\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Tanimsiz_telefon_biciminde_baglanti_kurulmaz_ama_metin_kaybolmaz()
    {
        var html = SiteBuilderRenderer.Render(Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true },
            new SiteBuilderSectionDto { Type = "contact", Enabled = true, Phone = "ara bizi" })).Html;

        Assert.DoesNotContain("tel:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ara bizi", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CSS_kullanici_metni_ICERMEZ()
    {
        // Bu iddia, önizlemede CSS'i <style> içine kaçırmadan gömmeyi meşru kılan şeydir.
        // Bir gün CSS'e kullanıcı değeri girerse burada kırılır ve karar yeniden düşünülür.
        var tasarim = Tasarim(new SiteBuilderSectionDto
        {
            Type = "hero",
            Enabled = true,
            Heading = "</style><script>alert(1)</script>"
        });
        tasarim.SiteTitle = "</style>";

        var css = SiteBuilderRenderer.Render(tasarim).Css;

        Assert.DoesNotContain("</style>", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Onizleme_ve_yayin_AYNI_kacirmayi_uygular()
    {
        var tasarim = Tasarim(
            new SiteBuilderSectionDto { Type = "hero", Enabled = true, Heading = Yuk },
            new SiteBuilderSectionDto { Type = "about", Enabled = true, Body = Yuk });

        var yayin = SiteBuilderRenderer.Render(tasarim, SiteBuilderRenderContext.Published).Html;
        var onizleme = SiteBuilderRenderer.Render(
            tasarim,
            SiteBuilderRenderContext.ForPreview("/api/v1/sitebuilder/x/images/")).Html;

        HamScriptYok(yayin);
        HamScriptYok(onizleme);
    }

    [Fact]
    public void Onizleme_stili_satir_ici_gomer_yayin_ise_baglar()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });

        var yayin = SiteBuilderRenderer.Render(tasarim, SiteBuilderRenderContext.Published).Html;
        var onizleme = SiteBuilderRenderer.Render(
            tasarim,
            SiteBuilderRenderContext.ForPreview("/api/v1/sitebuilder/x/images/")).Html;

        Assert.Contains("<link rel=\"stylesheet\" href=\"assets/adapanel/site.css\"", yayin, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>", yayin, StringComparison.Ordinal);

        Assert.Contains("<style>", onizleme, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", onizleme, StringComparison.Ordinal);
    }

    [Fact]
    public void Onizleme_gorselleri_panel_API_adresinden_okunur()
    {
        var ad = new string('c', 32) + ".webp";
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true, ImageName = ad });

        var onizleme = SiteBuilderRenderer.Render(
            tasarim,
            SiteBuilderRenderContext.ForPreview("/api/v1/sitebuilder/ornek/images/")).Html;

        Assert.Contains($"src=\"/api/v1/sitebuilder/ornek/images/{ad}\"", onizleme, StringComparison.Ordinal);
    }

    [Fact]
    public void Onizleme_oneki_ozniteliksen_cikacak_karakter_ICEREMEZ()
    {
        // Önek panelin ürettiği bir adres; yine de kural burada durur, çağıranın hatırlamasına
        // bırakılmaz.
        foreach (var kotuOnek in new[] { "/x\" onerror=\"alert(1)/", "/x /", "/x'/", "/x<y/" })
        {
            Assert.Throws<AdaPanel.Core.Common.AppException>(() => SiteBuilderRenderContext.ForPreview(kotuOnek));
        }
    }

    [Fact]
    public void Bilinmeyen_sablon_ve_palet_varsayilana_duser()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
        tasarim.TemplateId = "yok-boyle-bir-sey";
        tasarim.PaletteId = "yok";
        tasarim.FontId = "yok";

        var uretilen = SiteBuilderRenderer.Render(tasarim);

        Assert.Contains("tpl-sade", uretilen.Html, StringComparison.Ordinal);
        Assert.Contains(SiteBuilderCatalog.Palettes[0].Accent, uretilen.Css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uretilen_sayfa_duyarli_ve_dil_bilgisi_tasir()
    {
        var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
        tasarim.Language = "en";

        var uretilen = SiteBuilderRenderer.Render(tasarim);

        Assert.Contains("<html lang=\"en\">", uretilen.Html, StringComparison.Ordinal);
        Assert.Contains("name=\"viewport\"", uretilen.Html, StringComparison.Ordinal);
        Assert.Contains("@media", uretilen.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Sablonlar_GERCEKTEN_farkli_ciktilar_uretir()
    {
        // Üç şablon aynı sayfayı üretiyorsa seçim yaptırmak kullanıcıyı kandırmaktır.
        var bolumler = new[]
        {
            new SiteBuilderSectionDto { Type = "hero", Enabled = true, Heading = "Merhaba" },
            new SiteBuilderSectionDto
            {
                Type = "services",
                Enabled = true,
                Items = [new SiteBuilderItemDto { Title = "Hizmet" }]
            }
        };

        var ciktilar = SiteBuilderCatalog.Templates
            .Select(sablon =>
            {
                var tasarim = Tasarim(bolumler);
                tasarim.TemplateId = sablon.Id;
                var uretilen = SiteBuilderRenderer.Render(tasarim);
                return uretilen.Html + uretilen.Css;
            })
            .ToList();

        Assert.Equal(ciktilar.Count, ciktilar.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Paletler_farkli_renk_uretir()
    {
        var vurgular = SiteBuilderCatalog.Palettes
            .Select(palet =>
            {
                var tasarim = Tasarim(new SiteBuilderSectionDto { Type = "hero", Enabled = true });
                tasarim.PaletteId = palet.Id;
                return SiteBuilderRenderer.Render(tasarim).Css;
            })
            .ToList();

        Assert.Equal(vurgular.Count, vurgular.Distinct(StringComparer.Ordinal).Count());
    }
}
