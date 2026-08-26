using System.Text;
using AdaPanel.Core.Dtos.SiteBuilder;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>Üretilen sitenin tamamı: tek sayfa + tek stil dosyası.</summary>
public sealed record RenderedSite(string Html, string Css);

/// <summary>
/// Üretilen TEK bir sayfa: site köküne göreli dosya yolu + içerik.
/// </summary>
/// <remarks>
/// Dosya yolu burada üretilir, yayın tarafında yeniden hesaplanmaz. İki yerde hesaplansaydı biri
/// <c>hakkimizda/index.html</c>, diğeri <c>hakkimizda.html</c> derdi ve sayfa yazıldığı yerden
/// başka bir yerde aranırdı — üstelik "hangi dosya bizim" sorusunun cevabı da bozulurdu.
/// </remarks>
public sealed record RenderedPage(string RelativePath, string Slug, string Html);

/// <summary>Bir sitenin BÜTÜN sayfaları + ortak stil dosyası.</summary>
public sealed record RenderedPages(IReadOnlyList<RenderedPage> Pages, string Css);

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
/// <b>ÇOK SAYFA — ve "artık üretilmeyen sayfa" sorusu:</b> ilk sürüm bilerek tek sayfaydı, çünkü
/// çok sayfa "kullanıcı bir sayfayı kaldırdığında eski HTML ne olacak" sorusunu getiriyor ve o
/// soru SİLME'ye bakıyor — bu depoda müşteri sitesini yok edebilecek hata sınıfı. Soru silmeden
/// çözüldü: yayın hiçbir şeyi silmez, ama artık üretilmeyen kurucu dosyaları ÖKSÜZ olarak
/// listelenir (<see cref="SiteBuilderPublishPlanner.Orphans"/>) ve kullanıcıya gösterilir. Yani
/// "gizli bir sayfa yayında kaldı" durumu görünür, "yanlış dosya silindi" durumu ise mümkün değil.
/// </para>
/// <para>
/// <b>Sayfa arası adresler GÖRELİ:</b> ana sayfa kökte, ek sayfalar <c>{slug}/index.html</c>
/// altında. Bu yüzden her sayfanın kendi derinlik öneki var (<c>""</c> ya da <c>"../"</c>) ve
/// stil/görsel/gezinme adresleri o önekle kurulur. Mutlak adres kullanılsaydı sitenin alan adını
/// bilmek gerekirdi; kurucu ise sitenin bir gün hangi adla yayınlanacağını bilmez.
/// </para>
/// </remarks>
public static class SiteBuilderRenderer
{
    /// <summary>ANA SAYFAYI üretir (geriye dönük uyumlu tek sayfa yolu).</summary>
    public static RenderedSite Render(SiteBuilderProjectDto project, SiteBuilderRenderContext? context = null)
    {
        var all = RenderAll(project, context);
        return new RenderedSite(all.Pages[0].Html, all.Css);
    }

    /// <summary>
    /// Sitenin BÜTÜN sayfalarını üretir; ilk sıradaki daima ana sayfadır.
    /// </summary>
    /// <remarks>
    /// Geçersiz slug'lı ya da yinelenen sayfalar burada DÜŞÜRÜLMEZ — düşürme kaydetme aşamasında,
    /// tek yerde yapılır (<c>SiteBuilderService.NormalizePages</c>). Oluşturucu, kendisine verilen
    /// tasarımın zaten doğrulanmış olduğunu varsayar ama slug'ı yine de kalıptan geçirir: bu
    /// dizge bir klasör adına ve bir <c>href</c>'e girdiği için tek bir doğrulama noktasına
    /// güvenmek, o noktanın bir gün atlanmasına açık kapı bırakırdı.
    /// </remarks>
    public static RenderedPages RenderAll(SiteBuilderProjectDto project, SiteBuilderRenderContext? context = null)
    {
        var render = context ?? SiteBuilderRenderContext.Published;
        var template = SiteBuilderCatalog.ResolveTemplate(project.TemplateId);
        var palette = SiteBuilderCatalog.ResolvePalette(project.PaletteId);
        var font = SiteBuilderCatalog.ResolveFont(project.FontId);
        var css = RenderCss(template, palette, font);

        var extras = ValidPages(project);
        var language = NormalizeLanguage(project.Language);
        var siteTitle = Fallback(project.SiteTitle, language == "en" ? "Website" : "Web sitesi");

        var pages = new List<RenderedPage>
        {
            new(
                SiteBuilderLayout.IndexFile,
                string.Empty,
                RenderHtml(
                    project,
                    template,
                    render,
                    css,
                    new PageView(
                        Slug: string.Empty,
                        Title: siteTitle,
                        MetaDescription: project.MetaDescription,
                        Sections: project.Sections ?? []),
                    extras,
                    siteTitle,
                    language))
        };

        foreach (var page in extras)
        {
            pages.Add(new RenderedPage(
                SiteBuilderPageSlug.ToRelativeFile(page.Slug),
                page.Slug,
                RenderHtml(
                    project,
                    template,
                    render,
                    css,
                    new PageView(
                        page.Slug,
                        Fallback(page.Title, page.Slug),
                        // Sayfaya özel açıklama yoksa sitenin genel açıklaması kullanılır: her
                        // sayfada aynı açıklama, HİÇ açıklama olmamasından iyidir.
                        string.IsNullOrWhiteSpace(page.MetaDescription) ? project.MetaDescription : page.MetaDescription,
                        page.Sections ?? []),
                    extras,
                    siteTitle,
                    language)));
        }

        return new RenderedPages(pages, css);
    }

    /// <summary>Oluşturucunun tek bir sayfa hakkında bilmesi gereken her şey.</summary>
    private sealed record PageView(
        string Slug,
        string Title,
        string MetaDescription,
        IReadOnlyList<SiteBuilderSectionDto> Sections);

    /// <summary>
    /// Slug'ı geçerli ve BENZERSİZ olan ek sayfalar, kaydedilmiş sırayla.
    /// </summary>
    /// <remarks>
    /// Yinelenen slug sessizce iki kez üretilseydi ikinci sayfa birinciyi EZERDİ ve kullanıcı
    /// yazdığı içeriğin nereye gittiğini anlayamazdı. İlk yazan kazanır; ikincisi düşer.
    /// </remarks>
    private static List<SiteBuilderPageDto> ValidPages(SiteBuilderProjectDto project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SiteBuilderPageDto>();

        foreach (var page in project.Pages ?? [])
        {
            var slug = (page.Slug ?? string.Empty).Trim();
            if (!SiteBuilderPageSlug.IsValid(slug) || !seen.Add(slug))
            {
                continue;
            }

            page.Slug = slug;
            result.Add(page);

            if (result.Count >= SiteBuilderPageSlug.MaxPages)
            {
                break;
            }
        }

        return result;
    }

    private static string RenderHtml(
        SiteBuilderProjectDto project,
        SiteBuilderTemplate template,
        SiteBuilderRenderContext render,
        string css,
        PageView page,
        IReadOnlyList<SiteBuilderPageDto> extras,
        string siteTitle,
        string language)
    {
        // DERİNLİK ÖNEKİ: ana sayfa kökte (""), ek sayfalar bir klasör altında ("../"). Bütün
        // göreli adresler (stil, görsel, gezinme) bu önekten kurulur; tek yerde hesaplanır ki
        // sayfalardan biri stilsiz açılmasın.
        var depth = page.Slug.Length == 0 ? string.Empty : "../";

        var visible = OrderedSections(page.Sections).Where(entry => entry.Section.Enabled).ToList();
        var hero = visible.FirstOrDefault(entry => entry.Type == "hero").Section;
        var body = visible.Where(entry => entry.Type != "hero").ToList();

        var builder = new StringBuilder();

        builder.Append("<!DOCTYPE html>\n");
        builder.Append("<html lang=\"").Append(language).Append("\">\n");
        builder.Append("<head>\n");
        builder.Append("<meta charset=\"utf-8\" />\n");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        builder.Append("<title>").Append(SiteBuilderHtml.Text(PageTitle(page, siteTitle))).Append("</title>\n");

        if (!string.IsNullOrWhiteSpace(page.MetaDescription))
        {
            builder.Append("<meta name=\"description\" content=\"")
                .Append(SiteBuilderHtml.Text(page.MetaDescription.Trim()))
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
            builder.Append("<link rel=\"stylesheet\" href=\"").Append(depth).Append(SiteBuilderLayout.StyleFile).Append("\" />\n");
        }

        builder.Append("</head>\n");
        // Şablon kimliği katalogdan gelir (kullanıcı girdisi DEĞİL), bu yüzden sınıf adına
        // doğrudan yazılabilir.
        builder.Append("<body class=\"tpl-").Append(template.Id).Append("\">\n");

        // GEZİNME ÇUBUĞU İKİ SEBEPTEN BASILIR:
        //   1) şablon öyle istiyorsa (tek sayfada bölüm çapaları),
        //   2) BİRDEN FAZLA SAYFA varsa — şablon istemese bile.
        // İkincisi pazarlık dışı: menüsüz bir çok sayfalı sitede ek sayfalara ulaşmanın tek yolu
        // adresi elle yazmaktır, yani kullanıcı oluşturduğu sayfaların ziyaretçi tarafından hiç
        // görülmediğini fark etmez. "Ulaşılamayan sayfa üretmek", sayfayı hiç üretmemekten kötü.
        if (extras.Count > 0)
        {
            AppendPageNav(builder, siteTitle, page.Slug, extras, depth, language);
        }
        else if (template.ShowNav)
        {
            AppendSectionNav(builder, siteTitle, body, language);
        }

        if (hero is not null)
        {
            AppendHero(builder, project, hero, template, PageTitle(page, siteTitle), render, depth);
        }

        builder.Append("<main class=\"wrap\">\n");
        foreach (var entry in body)
        {
            AppendSection(builder, entry.Type, entry.Section, template, language, render, depth);
        }

        builder.Append("</main>\n");

        builder.Append("<footer class=\"site-footer\"><div class=\"wrap\"><p>")
            .Append(SiteBuilderHtml.Text(siteTitle))
            .Append("</p></div></footer>\n");

        builder.Append("</body>\n</html>\n");
        return builder.ToString();
    }

    /// <summary>
    /// Sekme başlığı: ek sayfalarda "Sayfa · Site", ana sayfada yalnız site adı.
    /// </summary>
    /// <remarks>
    /// Bütün sayfalar aynı başlığı taşısaydı ziyaretçi sekmelerini ayırt edemez, arama sonuçlarında
    /// da beş özdeş satır görürdü.
    /// </remarks>
    private static string PageTitle(PageView page, string siteTitle) =>
        page.Slug.Length == 0 || string.Equals(page.Title, siteTitle, StringComparison.Ordinal)
            ? siteTitle
            : $"{page.Title} · {siteTitle}";

    /// <summary>Sayfalar arası gezinme. Bulunulan sayfa bağlantı DEĞİL, işaretli bir metindir.</summary>
    /// <remarks>
    /// Bulunulan sayfayı kendine bağlamak ziyaretçiye hiçbir şey kazandırmaz; <c>aria-current</c>
    /// ise ekran okuyucuya nerede olduğunu söyler. Menü sırası kullanıcının kaydettiği sıradır —
    /// alfabetik sıralamak, kullanıcının bilerek verdiği düzeni sessizce bozmak olurdu.
    /// </remarks>
    private static void AppendPageNav(
        StringBuilder builder,
        string siteTitle,
        string currentSlug,
        IReadOnlyList<SiteBuilderPageDto> extras,
        string depth,
        string language)
    {
        var homeLabel = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "Home" : "Ana sayfa";
        var homeHref = depth.Length == 0 ? "./" : depth;

        builder.Append("<nav class=\"site-nav\"><div class=\"wrap nav-inner\">");
        builder.Append("<span class=\"nav-brand\">").Append(SiteBuilderHtml.Text(siteTitle)).Append("</span>");
        builder.Append("<ul>");

        AppendNavEntry(builder, homeHref, homeLabel, currentSlug.Length == 0);

        foreach (var page in extras)
        {
            if (!page.ShowInNav && !string.Equals(page.Slug, currentSlug, StringComparison.OrdinalIgnoreCase))
            {
                // Menüde gizlenmiş sayfa: adresi bilen ziyaretçi ulaşır ama menüde durmaz.
                // İSTİSNA — o sayfadayken menüde GÖSTERİLİR: aksi hâlde ziyaretçi hangi sayfada
                // olduğunu menüden okuyamaz ve menü, bulunduğu yeri göstermeyen bir menü olur.
                continue;
            }

            // Slug kalıbı [a-z0-9-] ile sınırlı; yine de href'e kaçırılarak giriyor, çünkü
            // "kalıp doğrulandı" varsayımı kaçırmayı atlamanın en yaygın gerekçesidir.
            AppendNavEntry(
                builder,
                depth + SiteBuilderPageSlug.ToHref(page.Slug),
                Fallback(page.Title, page.Slug),
                string.Equals(page.Slug, currentSlug, StringComparison.OrdinalIgnoreCase));
        }

        builder.Append("</ul></div></nav>\n");
    }

    private static void AppendNavEntry(StringBuilder builder, string href, string label, bool current)
    {
        builder.Append("<li>");
        if (current)
        {
            builder.Append("<span class=\"nav-current\" aria-current=\"page\">")
                .Append(SiteBuilderHtml.Text(label))
                .Append("</span>");
        }
        else
        {
            builder.Append("<a href=\"").Append(SiteBuilderHtml.Text(href)).Append("\">")
                .Append(SiteBuilderHtml.Text(label))
                .Append("</a>");
        }

        builder.Append("</li>");
    }

    private static void AppendSectionNav(
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
        SiteBuilderRenderContext render,
        string depth)
    {
        var image = SiteBuilderHtml.ImageSource(hero.ImageName, AssetPrefix(render, depth));
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
        SiteBuilderRenderContext render,
        string depth)
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
                AppendAbout(builder, section, body, render, depth);
                break;
            case "services":
                AppendCards(builder, section, body, template, render, depth);
                break;
            case "gallery":
                AppendGallery(builder, section, body, render, depth);
                break;
            case "contact":
                AppendContact(builder, section, body, render, language);
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
        SiteBuilderRenderContext render,
        string depth)
    {
        var image = SiteBuilderHtml.ImageSource(section.ImageName, AssetPrefix(render, depth));
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
        SiteBuilderRenderContext render,
        string depth)
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

            var image = SiteBuilderHtml.ImageSource(item.ImageName, AssetPrefix(render, depth));
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
        SiteBuilderRenderContext render,
        string depth)
    {
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        var prefix = AssetPrefix(render, depth);
        var images = (section.Items ?? [])
            .Select(item => (Source: SiteBuilderHtml.ImageSource(item.ImageName, prefix), Alt: item.Title ?? string.Empty))
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

    private static void AppendContact(
        StringBuilder builder,
        SiteBuilderSectionDto section,
        string body,
        SiteBuilderRenderContext render,
        string language)
    {
        if (body.Length > 0)
        {
            builder.Append("<div class=\"prose\">").Append(body).Append("</div>\n");
        }

        builder.Append("<ul class=\"contact-list\">\n");

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

        AppendContactForm(builder, section, render, language);
    }

    /// <summary>
    /// İletişim FORMU — yalnız GERÇEKTEN çalışacaksa basılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Üç koşul birden:</b> kullanıcı formu açmış olmalı, alıcı adres geçerli olmalı ve
    /// gönderim hedefi (panelin genel adresi) çözülmüş olmalı. Koşullardan biri eksikse form
    /// basılmaz; "belki çalışır" diye basmak, ziyaretçiye gönderdiğini sandırmak olurdu ve bu
    /// hiç form koymamaktan kötüdür. Eksik koşul kullanıcıya yayın planında ADIYLA söylenir.
    /// </para>
    /// <para>
    /// <b>Neden düz form POST, JavaScript değil:</b> gönderim panelin ana adına, yani BAŞKA bir
    /// kökene gider. Tarayıcı, form gezinmesi (navigation) için CORS izni aramaz; <c>fetch</c>
    /// ise arar ve panelde bu uç için köken listesi tutmak, müşterinin alan adını her
    /// değiştirdiğinde sessizce kırılan bir yapılandırma demek olurdu. Ayrıca JavaScript'siz
    /// tarayıcıda da çalışır ve yanıt sayfası, gönderimin gerçekten başarılı olup olmadığını
    /// SÖYLER.
    /// </para>
    /// <para>
    /// <b>Bal küpü (honeypot):</b> ekranda görünmeyen ama işaretlemede duran bir alan. Botların
    /// büyük kısmı bütün alanları doldurur; insan doldurmaz. Alan <c>tabindex="-1"</c> ile klavye
    /// sırasından çıkarılır, etiketi "boş bırakın" der ve <c>autocomplete="off"</c> ile
    /// tarayıcının otomatik doldurması engellenir — yani ekran okuyucu kullanan bir ziyaretçi de
    /// yanlışlıkla dolduramaz. Dış bir servise (CAPTCHA) bağımlılık YOK: ziyaretçiyi üçüncü bir
    /// sunucuya bağlamak, panelin hiçbir yerinde kabul edilmeyen bir bedel.
    /// </para>
    /// </remarks>
    private static void AppendContactForm(
        StringBuilder builder,
        SiteBuilderSectionDto section,
        SiteBuilderRenderContext render,
        string language)
    {
        if (!section.ContactFormEnabled
            || render.ContactFormAction.Length == 0
            || SiteBuilderHtml.MailtoHref(section.Email).Length == 0)
        {
            return;
        }

        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

        builder.Append("<form class=\"contact-form\" method=\"post\" accept-charset=\"utf-8\" action=\"")
            .Append(SiteBuilderHtml.Text(render.ContactFormAction))
            .Append("\">\n");

        AppendFormField(builder, "adapanel-cf-name", "name", english ? "Your name" : "Adınız", 120, required: true);
        AppendFormField(builder, "adapanel-cf-email", "email", english ? "Your e-mail" : "E-posta adresiniz", 160, required: true, type: "email");
        AppendFormField(builder, "adapanel-cf-subject", "subject", english ? "Subject" : "Konu", 160, required: false);

        builder.Append("<p class=\"field\"><label for=\"adapanel-cf-message\">")
            .Append(SiteBuilderHtml.Text(english ? "Message" : "Mesajınız"))
            .Append("</label><textarea id=\"adapanel-cf-message\" name=\"message\" rows=\"6\" maxlength=\"")
            .Append(SiteBuilderContactForm.MaxMessageLength)
            .Append("\" required></textarea></p>\n");

        // BAL KÜPÜ. Etiketi açıkça "boş bırakın" der; gizleme yalnız GÖRSEL (CSS), yani ekran
        // okuyucu etiketi okur ve ziyaretçi ne yapacağını bilir.
        builder.Append("<p class=\"adapanel-cf-trap\"><label for=\"adapanel-cf-website\">")
            .Append(SiteBuilderHtml.Text(english ? "Leave this field empty" : "Bu alanı boş bırakın"))
            .Append("</label><input id=\"adapanel-cf-website\" type=\"text\" name=\"")
            .Append(SiteBuilderContactForm.HoneypotField)
            .Append("\" tabindex=\"-1\" autocomplete=\"off\" value=\"\" /></p>\n");

        builder.Append("<p class=\"field\"><button type=\"submit\">")
            .Append(SiteBuilderHtml.Text(english ? "Send" : "Gönder"))
            .Append("</button></p>\n");

        builder.Append("</form>\n");

        // Ziyaretçiye adresinin ne için kullanılacağı SÖYLENİR. Formun arkasında bir e-posta
        // gönderimi var; bunu gizlemek, ziyaretçiden veri toplamanın sessiz biçimi olurdu.
        builder.Append("<p class=\"form-note\">")
            .Append(SiteBuilderHtml.Text(english
                ? "Your message is delivered to the site owner by e-mail. You will see whether it was sent."
                : "Mesajınız site sahibine e-posta ile iletilir. Gönderilip gönderilmediğini ekranda görürsünüz."))
            .Append("</p>\n");
    }

    private static void AppendFormField(
        StringBuilder builder,
        string id,
        string name,
        string label,
        int maxLength,
        bool required,
        string type = "text")
    {
        builder.Append("<p class=\"field\"><label for=\"").Append(id).Append("\">")
            .Append(SiteBuilderHtml.Text(label))
            .Append("</label><input id=\"").Append(id)
            .Append("\" type=\"").Append(type)
            .Append("\" name=\"").Append(name)
            .Append("\" maxlength=\"").Append(maxLength).Append('"');

        if (required)
        {
            builder.Append(" required");
        }

        builder.Append(" /></p>\n");
    }

    /// <summary>
    /// Görsel adreslerinin öneki, sayfanın derinliğine göre.
    /// </summary>
    /// <remarks>
    /// Önizlemede önek panelin MUTLAK API yoludur (<c>/api/v1/sitebuilder/…</c>) ve ona derinlik
    /// eklemek adresi bozardı. Kural "önizleme mi" diye SORMUYOR — önekin kendisine bakıyor:
    /// eğik çizgiyle başlayan bir adres zaten kökten çözülür. Bağlamın adını sormak yerine
    /// değerin şeklini ölçmek, yarın üçüncü bir bağlam eklendiğinde de doğru kalır.
    /// </remarks>
    private static string AssetPrefix(SiteBuilderRenderContext render, string depth) =>
        render.ImageUrlPrefix.StartsWith('/') ? render.ImageUrlPrefix : depth + render.ImageUrlPrefix;

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
.site-nav .nav-current{color:var(--accent);font-weight:600;}
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
.contact-form{display:grid;gap:.9rem;margin-top:1.5rem;max-width:36rem;text-align:left;}
.contact-form .field{margin:0;display:grid;gap:.35rem;}
.contact-form label{font-size:.92rem;color:var(--muted);}
.contact-form input,.contact-form textarea{font:inherit;color:var(--text);background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:.6rem .7rem;width:100%;}
.contact-form textarea{resize:vertical;min-height:8rem;}
.contact-form button{font:inherit;cursor:pointer;background:var(--accent);color:var(--on-accent);border:0;border-radius:8px;padding:.7rem 1.4rem;}
.contact-form button:hover{background:var(--accent-dark);}
.adapanel-cf-trap{position:absolute;left:-9999px;top:auto;width:1px;height:1px;overflow:hidden;}
.form-note{font-size:.85rem;color:var(--muted);max-width:36rem;}
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
.contact-form,.form-note{margin-inline:auto;}
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
    private static List<(string Type, SiteBuilderSectionDto Section)> OrderedSections(
        IReadOnlyList<SiteBuilderSectionDto>? sections)
    {
        var byType = new List<(string, SiteBuilderSectionDto)>();
        foreach (var type in SiteBuilderCatalog.SectionTypes)
        {
            var match = (sections ?? []).FirstOrDefault(section =>
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
