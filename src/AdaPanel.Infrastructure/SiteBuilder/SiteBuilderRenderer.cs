using System.Text;
using AdaPanel.Core.Dtos.SiteBuilder;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>Üretilen sitenin tamamı: tek sayfa + tek stil dosyası.</summary>
public sealed record RenderedSite(string Html, string Css);

/// <summary>
/// Tasarımı statik HTML + CSS'e çevirir. SAF: dosya sistemine, ağa, saate bakmaz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden saf:</b> yayının en riskli kısmı diske yazmak, en hataya açık kısmı ise işaretleme
/// üretmek. İkisi aynı sınıfta olsaydı, kaçırmayı (escape) doğrulamak için her testte gerçek bir
/// klasör kurmak gerekirdi; pratikte bu, testlerin azalması demektir. Burada tasarım verilir,
/// dizge alınır — XSS testleri tek satırda yazılabilir.
/// </para>
/// <para>
/// <b>Neden tek sayfa:</b> ilk sürümün dürüst kapsamı bu. Çok sayfalı bir site, sayfa arası
/// gezinme, ad çakışması ve yayında "hangi eski sayfa artık üretilmiyor" sorusunu getirir; o soru
/// SİLME işlemine bakar ve silme bu depoda müşteri sitesini yok edebilecek hata sınıfıdır. Tek
/// sayfa + çapa bağlantıları, hiç silmeden çalışan bir kapsamdır.
/// </para>
/// </remarks>
public static class SiteBuilderRenderer
{
    public static RenderedSite Render(SiteBuilderProjectDto project, SiteBuilderRenderContext? context = null)
    {
        var render = context ?? SiteBuilderRenderContext.Published;
        var template = SiteBuilderCatalog.ResolveTemplate(project.TemplateId);
        var palette = SiteBuilderCatalog.ResolvePalette(project.PaletteId);
        var font = SiteBuilderCatalog.ResolveFont(project.FontId);
        var css = RenderCss(template, palette, font);

        return new RenderedSite(RenderHtml(project, template, render, css), css);
    }

    private static string RenderHtml(
        SiteBuilderProjectDto project,
        SiteBuilderTemplate template,
        SiteBuilderRenderContext render,
        string css)
    {
        var visible = OrderedSections(project).Where(entry => entry.Section.Enabled).ToList();
        var hero = visible.FirstOrDefault(entry => entry.Type == "hero").Section;
        var body = visible.Where(entry => entry.Type != "hero").ToList();

        var language = NormalizeLanguage(project.Language);
        var title = Fallback(project.SiteTitle, language == "en" ? "Website" : "Web sitesi");
        var builder = new StringBuilder();

        builder.Append("<!DOCTYPE html>\n");
        builder.Append("<html lang=\"").Append(language).Append("\">\n");
        builder.Append("<head>\n");
        builder.Append("<meta charset=\"utf-8\" />\n");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        builder.Append("<title>").Append(SiteBuilderHtml.Text(title)).Append("</title>\n");

        if (!string.IsNullOrWhiteSpace(project.MetaDescription))
        {
            builder.Append("<meta name=\"description\" content=\"")
                .Append(SiteBuilderHtml.Text(project.MetaDescription.Trim()))
                .Append("\" />\n");
        }

        if (render.InlineCss)
        {
            // CSS'in tamamı katalogdan üretilir; içinde kullanıcı metni YOKTUR. Bu yüzden
            // "</style>" ile bloktan çıkmak mümkün değildir ve ayrıca kaçırma gerekmez.
            // (Bu iddia testle sabitlendi; bir gün CSS'e kullanıcı değeri girerse test kırılır.)
            builder.Append("<style>\n").Append(css).Append("</style>\n");
        }
        else
        {
            builder.Append("<link rel=\"stylesheet\" href=\"").Append(SiteBuilderLayout.StyleFile).Append("\" />\n");
        }

        builder.Append("</head>\n");
        // Şablon kimliği katalogdan gelir (kullanıcı girdisi DEĞİL), bu yüzden sınıf adına
        // doğrudan yazılabilir.
        builder.Append("<body class=\"tpl-").Append(template.Id).Append("\">\n");

        if (template.ShowNav)
        {
            AppendNav(builder, title, body, language);
        }

        if (hero is not null)
        {
            AppendHero(builder, project, hero, template, title, render);
        }

        builder.Append("<main class=\"wrap\">\n");
        foreach (var entry in body)
        {
            AppendSection(builder, entry.Type, entry.Section, template, language, render);
        }

        builder.Append("</main>\n");

        builder.Append("<footer class=\"site-footer\"><div class=\"wrap\"><p>")
            .Append(SiteBuilderHtml.Text(title))
            .Append("</p></div></footer>\n");

        builder.Append("</body>\n</html>\n");
        return builder.ToString();
    }

    private static void AppendNav(
        StringBuilder builder,
        string title,
        IReadOnlyCollection<(string Type, SiteBuilderSectionDto Section)> body,
        string language)
    {
        builder.Append("<nav class=\"site-nav\"><div class=\"wrap nav-inner\">");
        builder.Append("<span class=\"nav-brand\">").Append(SiteBuilderHtml.Text(title)).Append("</span>");
        builder.Append("<ul>");
        foreach (var (type, section) in body)
        {
            var label = Fallback(section.Heading, DefaultHeading(type, language));
            builder.Append("<li><a href=\"#")
                .Append(SiteBuilderHtml.AnchorId(type))
                .Append("\">")
                .Append(SiteBuilderHtml.Text(label))
                .Append("</a></li>");
        }

        builder.Append("</ul></div></nav>\n");
    }

    private static void AppendHero(
        StringBuilder builder,
        SiteBuilderProjectDto project,
        SiteBuilderSectionDto hero,
        SiteBuilderTemplate template,
        string title,
        SiteBuilderRenderContext render)
    {
        var image = SiteBuilderHtml.ImageSource(hero.ImageName, render.ImageUrlPrefix);
        var heading = Fallback(hero.Heading, title);

        builder.Append("<header class=\"hero hero-").Append(template.HeroStyle).Append("\" id=\"")
            .Append(SiteBuilderHtml.AnchorId("hero")).Append("\">\n");
        builder.Append("<div class=\"wrap hero-inner\">\n");
        builder.Append("<div class=\"hero-text\">\n");
        builder.Append("<h1>").Append(SiteBuilderHtml.Text(heading)).Append("</h1>\n");

        if (!string.IsNullOrWhiteSpace(project.Tagline))
        {
            builder.Append("<p class=\"tagline\">").Append(SiteBuilderHtml.Text(project.Tagline.Trim())).Append("</p>\n");
        }

        var body = SiteBuilderHtml.Paragraphs(hero.Body);
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        builder.Append("</div>\n");

        if (image.Length > 0)
        {
            // alt metni kullanıcının başlığından gelir; boşsa alt="" bırakılır (dekoratif görsel),
            // uydurma bir açıklama yazmak ekran okuyucuya yanlış bilgi vermek olurdu.
            builder.Append("<div class=\"hero-media\"><img src=\"").Append(image)
                .Append("\" alt=\"").Append(SiteBuilderHtml.Text(hero.Heading ?? string.Empty)).Append("\" /></div>\n");
        }

        builder.Append("</div>\n</header>\n");
    }

    private static void AppendSection(
        StringBuilder builder,
        string type,
        SiteBuilderSectionDto section,
        SiteBuilderTemplate template,
        string language,
        SiteBuilderRenderContext render)
    {
        // `type` katalogdan gelir; kullanıcının yazdığı Type alanı DEĞİL. Bu yüzden sınıf ve
        // çapa adına doğrudan yazılabilir (bkz. OrderedSections).
        builder.Append("<section class=\"block block-").Append(type).Append("\" id=\"")
            .Append(SiteBuilderHtml.AnchorId(type)).Append("\">\n");

        var heading = Fallback(section.Heading, DefaultHeading(type, language));
        builder.Append("<h2>").Append(SiteBuilderHtml.Text(heading)).Append("</h2>\n");

        var body = SiteBuilderHtml.Paragraphs(section.Body);

        switch (type)
        {
            case "about":
                AppendAbout(builder, section, body, render);
                break;
            case "services":
                AppendCards(builder, section, body, template, render);
                break;
            case "gallery":
                AppendGallery(builder, section, body, render);
                break;
            case "contact":
                AppendContact(builder, section, body);
                break;
            default:
                if (body.Length > 0)
                {
                    builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
                }

                break;
        }

        builder.Append("</section>\n");
    }

    private static void AppendAbout(
        StringBuilder builder,
        SiteBuilderSectionDto section,
        string body,
        SiteBuilderRenderContext render)
    {
        var image = SiteBuilderHtml.ImageSource(section.ImageName, render.ImageUrlPrefix);
        builder.Append("<div class=\"split\">\n");
        if (image.Length > 0)
        {
            builder.Append("<div class=\"split-media\"><img src=\"").Append(image)
                .Append("\" alt=\"").Append(SiteBuilderHtml.Text(section.Heading ?? string.Empty)).Append("\" /></div>\n");
        }

        builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        builder.Append("</div>\n");
    }

    private static void AppendCards(
        StringBuilder builder,
        SiteBuilderSectionDto section,
        string body,
        SiteBuilderTemplate template,
        SiteBuilderRenderContext render)
    {
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        var items = (section.Items ?? []).Where(HasCardContent).ToList();
        if (items.Count == 0)
        {
            return;
        }

        builder.Append("<div class=\"cards cards-").Append(template.CardStyle).Append("\">\n");
        foreach (var item in items)
        {
            builder.Append("<article class=\"card\">");

            var image = SiteBuilderHtml.ImageSource(item.ImageName, render.ImageUrlPrefix);
            if (image.Length > 0)
            {
                builder.Append("<img src=\"").Append(image)
                    .Append("\" alt=\"").Append(SiteBuilderHtml.Text(item.Title ?? string.Empty)).Append("\" />");
            }

            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                builder.Append("<h3>").Append(SiteBuilderHtml.Text(item.Title.Trim())).Append("</h3>");
            }

            var text = SiteBuilderHtml.Paragraphs(item.Text);
            if (text.Length > 0)
            {
                builder.Append("<div class=\"prose\">").Append(text).Append("</div>");
            }

            builder.Append("</article>\n");
        }

        builder.Append("</div>\n");
    }

    private static void AppendGallery(
        StringBuilder builder,
        SiteBuilderSectionDto section,
        string body,
        SiteBuilderRenderContext render)
    {
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        var images = (section.Items ?? [])
            .Select(item => (Source: SiteBuilderHtml.ImageSource(item.ImageName, render.ImageUrlPrefix), Alt: item.Title ?? string.Empty))
            .Where(item => item.Source.Length > 0)
            .ToList();

        if (images.Count == 0)
        {
            return;
        }

        builder.Append("<div class=\"gallery\">\n");
        foreach (var image in images)
        {
            builder.Append("<figure><img src=\"").Append(image.Source)
                .Append("\" alt=\"").Append(SiteBuilderHtml.Text(image.Alt)).Append("\" />");
            if (!string.IsNullOrWhiteSpace(image.Alt))
            {
                builder.Append("<figcaption>").Append(SiteBuilderHtml.Text(image.Alt.Trim())).Append("</figcaption>");
            }

            builder.Append("</figure>\n");
        }

        builder.Append("</div>\n");
    }

    private static void AppendContact(StringBuilder builder, SiteBuilderSectionDto section, string body)
    {
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        builder.Append("<ul class=\"contact-list\">\n");

        // İLETİŞİM FORMU YOK, BİLEREK. Form, sunucu tarafında bir alıcı (posta gönderimi) ve
        // istenmeyen ileti koruması ister; ikisi de bu sürümün kapsamı dışında. Çalışmayan bir
        // form koymak, ziyaretçinin gönderdiğini sanıp yanıt beklemesi demek olurdu — bu, hiç
        // form koymamaktan kötüdür. Yerine doğrudan iletişim adresleri basılır.
        var mail = SiteBuilderHtml.MailtoHref(section.Email);
        if (mail.Length > 0)
        {
            builder.Append("<li><a href=\"").Append(mail).Append("\">")
                .Append(SiteBuilderHtml.Text(section.Email.Trim())).Append("</a></li>\n");
        }

        var tel = SiteBuilderHtml.TelHref(section.Phone);
        if (!string.IsNullOrWhiteSpace(section.Phone))
        {
            builder.Append("<li>");
            if (tel.Length > 0)
            {
                builder.Append("<a href=\"").Append(tel).Append("\">")
                    .Append(SiteBuilderHtml.Text(section.Phone.Trim())).Append("</a>");
            }
            else
            {
                // Bağlantı kurulamadı ama numarayı GİZLEMİYORUZ: kullanıcı yazdığını sayfada görsün.
                builder.Append(SiteBuilderHtml.Text(section.Phone.Trim()));
            }

            builder.Append("</li>\n");
        }

        if (!string.IsNullOrWhiteSpace(section.Address))
        {
            builder.Append("<li>").Append(SiteBuilderHtml.Text(section.Address.Trim()).Replace("\n", "<br />")).Append("</li>\n");
        }

        builder.Append("</ul>\n");
    }

    private static string RenderCss(SiteBuilderTemplate template, SiteBuilderPalette palette, SiteBuilderFont font)
    {
        // Bu dizgenin İÇİNE kullanıcı girdisi GİRMEZ; her değer katalogdan gelir. Gerekçe:
        // SiteBuilderCatalog.
        var builder = new StringBuilder();

        builder.Append("/* AdaPanel site kurucu - otomatik uretildi */\n");
        builder.Append(":root{")
            .Append("--accent:").Append(palette.Accent).Append(';')
            .Append("--accent-dark:").Append(palette.AccentDark).Append(';')
            .Append("--surface:").Append(palette.Surface).Append(';')
            .Append("--surface-alt:").Append(palette.SurfaceAlt).Append(';')
            .Append("--text:").Append(palette.Text).Append(';')
            .Append("--muted:").Append(palette.TextMuted).Append(';')
            .Append("--on-accent:").Append(palette.OnAccent).Append(';')
            .Append("--border:").Append(palette.Border).Append(';')
            .Append("--font:").Append(font.Stack).Append(';')
            .Append("}\n");

        builder.Append("""
*,*::before,*::after{box-sizing:border-box;}
body{margin:0;font-family:var(--font);color:var(--text);background:var(--surface);line-height:1.65;-webkit-text-size-adjust:100%;}
img{max-width:100%;height:auto;display:block;}
a{color:var(--accent);}
h1,h2,h3{line-height:1.25;margin:0 0 .5em;}
h1{font-size:clamp(1.9rem,5vw,3rem);}
h2{font-size:clamp(1.4rem,3.5vw,2rem);}
p{margin:0 0 1em;}
.wrap{width:min(1080px,100% - 2.5rem);margin-inline:auto;}
.prose{color:var(--muted);}
.prose p:last-child{margin-bottom:0;}
.site-nav{background:var(--surface);border-bottom:1px solid var(--border);position:sticky;top:0;z-index:10;}
.nav-inner{display:flex;flex-wrap:wrap;gap:.75rem 1.25rem;align-items:center;justify-content:space-between;padding:.85rem 0;}
.nav-brand{font-weight:700;}
.site-nav ul{display:flex;flex-wrap:wrap;gap:1rem;list-style:none;margin:0;padding:0;}
.site-nav a{text-decoration:none;color:var(--muted);}
.site-nav a:hover,.site-nav a:focus{color:var(--accent);}
.hero{padding:clamp(2.5rem,8vw,5.5rem) 0;background:var(--surface-alt);}
.hero-inner{display:grid;gap:2rem;align-items:center;}
.tagline{font-size:1.15rem;color:var(--muted);margin-bottom:1rem;}
.block{padding:clamp(2rem,6vw,4rem) 0;}
.block+.block{border-top:1px solid var(--border);}
.split{display:grid;gap:1.75rem;}
.cards{display:grid;gap:1.25rem;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));}
.card{padding:1.25rem;border-radius:14px;background:var(--surface-alt);}
.card h3{margin-top:.65rem;}
.card img{border-radius:10px;}
.gallery{display:grid;gap:1rem;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));}
.gallery figure{margin:0;}
.gallery img{border-radius:12px;}
.gallery figcaption{padding-top:.45rem;font-size:.9rem;color:var(--muted);}
.contact-list{list-style:none;margin:0;padding:0;display:grid;gap:.6rem;}
.contact-list a{text-decoration:none;}
.site-footer{padding:1.75rem 0;border-top:1px solid var(--border);color:var(--muted);font-size:.9rem;}
.site-footer p{margin:0;}
@media (min-width:760px){.split{grid-template-columns:minmax(0,1fr) minmax(0,1.2fr);}}
:focus-visible{outline:3px solid var(--accent);outline-offset:2px;}

""");

        // Şablona özgü düzen farkları. Şablonlar yalnız "renk teması" değil; gerçekten farklı
        // yerleşimler üretirler, aksi hâlde seçim yapmak kullanıcıyı kandırmak olurdu.
        builder.Append(template.Id switch
        {
            "vitrin" => """
.hero-vitrin{padding:0;}
.hero-banner .hero-inner{grid-template-columns:1fr;gap:0;padding:0;width:100%;}
.hero-banner .hero-media{order:-1;}
.hero-banner .hero-media img{width:100%;max-height:min(60vh,460px);object-fit:cover;}
.hero-banner .hero-text{width:min(1080px,100% - 2.5rem);margin-inline:auto;padding:clamp(2rem,6vw,3.5rem) 0;}
.cards-raised .card{background:var(--surface);box-shadow:0 10px 30px rgba(15,23,42,.10);border:1px solid var(--border);}

""",
            "kurumsal" => """
.hero-split .hero-inner{grid-template-columns:1fr;text-align:left;}
@media (min-width:860px){.hero-split .hero-inner{grid-template-columns:1.15fr .85fr;}}
.hero-split .hero-media img{border-radius:16px;}
.cards-bordered .card{background:transparent;border:1px solid var(--border);border-radius:10px;}
.block h2{position:relative;padding-bottom:.4rem;}
.block h2::after{content:"";display:block;width:56px;height:3px;margin-top:.5rem;background:var(--accent);}

""",
            _ => """
.hero-plain .hero-inner{grid-template-columns:1fr;text-align:center;justify-items:center;}
.hero-plain .hero-text{max-width:44rem;}
.hero-plain .hero-media{max-width:36rem;}
.block{text-align:center;}
.block .prose,.block .contact-list{margin-inline:auto;max-width:44rem;}
.cards-flat .card{background:var(--surface-alt);}
.split{justify-items:center;}

"""
        });

        return builder.ToString();
    }

    /// <summary>
    /// Katalog sırasına göre bölümler; bilinmeyen tipler ATILIR.
    /// </summary>
    /// <remarks>
    /// Döndürülen tip dizgesi KATALOGDAN alınır, bölümün kendi <c>Type</c> alanından değil.
    /// Bu, işaretlemeye (sınıf adı, çapa kimliği) giren tek "kaçırılmamış" dizgenin kullanıcı
    /// denetiminde olmadığını YAPISAL olarak garanti eder — eşleştirme büyük/küçük harf duyarsız
    /// olduğu için aksi hâlde <c>"HERO"</c> gibi bir değer de sınıf adına sızabilirdi.
    /// </remarks>
    private static List<(string Type, SiteBuilderSectionDto Section)> OrderedSections(SiteBuilderProjectDto project)
    {
        var byType = new List<(string, SiteBuilderSectionDto)>();
        foreach (var type in SiteBuilderCatalog.SectionTypes)
        {
            var match = (project.Sections ?? []).FirstOrDefault(section =>
                string.Equals(section.Type, type, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                byType.Add((type, match));
            }
        }

        return byType;
    }

    private static bool HasCardContent(SiteBuilderItemDto item) =>
        !string.IsNullOrWhiteSpace(item.Title)
        || !string.IsNullOrWhiteSpace(item.Text)
        || !string.IsNullOrWhiteSpace(item.ImageName);

    /// <summary>
    /// Başlık boş bırakılmışsa sayfada başlıksız bir blok kalmasın. Yedek metin, sayfanın DİLİNDE
    /// yazılır — İngilizce bir sitede Türkçe "Hakkımızda" başlığı, kullanıcının fark etmeden
    /// yayınlayacağı bir hata olurdu.
    /// </summary>
    private static string DefaultHeading(string type, string language)
    {
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        return type switch
        {
            "about" => english ? "About us" : "Hakkımızda",
            "services" => english ? "Services" : "Hizmetler",
            "gallery" => english ? "Gallery" : "Galeri",
            "contact" => english ? "Contact" : "İletişim",
            _ => english ? "Welcome" : "Hoş geldiniz"
        };
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>Yalnız panelin desteklediği iki dil; başka değer <c>tr</c>'ye düşer.</summary>
    private static string NormalizeLanguage(string? language) =>
        string.Equals((language ?? string.Empty).Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
}
