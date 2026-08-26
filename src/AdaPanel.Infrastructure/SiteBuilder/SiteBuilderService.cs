using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Site kurucunun uçtan uca akışı: tasarımı sakla → önizle → yayınla.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tasarım ve görseller panelin veri kökünde durur, müşterinin site klasöründe DEĞİL.</b>
/// Bunun sonucu şu: "Yayınla" düğmesine basılana kadar müşterinin dosyalarına HİÇBİR ŞEY
/// yazılmaz. Görsel yükleme bile yazmaz. Böylece yıkıcı olabilecek tek işlem tek bir düğmeye
/// toplanır ve o düğmenin arkasına plan + onay + kapsam denetimi konabilir. Yükleme sırasında
/// site klasörüne yazsaydık, "hiç yayınlamadım ama sitem değişti" gibi bir durum mümkün olurdu.
/// </para>
/// <para>
/// <b>Hedef klasör IIS'ten okunur, istekten DEĞİL.</b> Kullanıcı yalnız site ADINI verir; yol
/// <see cref="IIisSiteService.GetSiteAsync"/> ile çözülür. Site yoksa istek 404 ile ölür. Yani
/// "hedef klasör gerçekten o siteye ait mi" sorusu, tahminle değil kaynağından cevaplanır.
/// </para>
/// </remarks>
public sealed class SiteBuilderService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    ILogger<SiteBuilderService> logger) : ISiteBuilderService
{
    /// <summary>
    /// Site başına yayın kilidi. Aynı siteye iki eşzamanlı yayın, yarısı eski yarısı yeni bir
    /// sayfa bırakabilirdi. Panelin genel kilidi (<c>IConcurrencyLock</c>) kullanılmadı: o
    /// yeniden girilebilir değil ve sunucudaki ilgisiz işleri de sıraya sokardı.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublishLocks = new(StringComparer.OrdinalIgnoreCase);

    public SiteBuilderCatalogDto GetCatalog() => SiteBuilderCatalog.ToDto();

    public async Task<SiteBuilderProjectDto> GetProjectAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var stored = await LoadAsync(site.Name, cancellationToken);
        return stored ?? CreateStarterProject(site.Name);
    }

    public async Task<SiteBuilderProjectDto> SaveProjectAsync(
        string siteName,
        SaveSiteBuilderProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var known = await KnownImageNamesAsync(site.Name, cancellationToken);

        return await fileStore.UpdateAsync(
            ProjectPath(site.Name),
            CreateStarterProject(site.Name),
            current =>
            {
                current.SiteName = site.Name;
                // Bilinmeyen kimlik katalog varsayılanına düşer; kayıtlı tasarım hiçbir zaman
                // "açılamaz" hâle gelmez.
                current.TemplateId = SiteBuilderCatalog.ResolveTemplate(request.TemplateId).Id;
                current.PaletteId = SiteBuilderCatalog.ResolvePalette(request.PaletteId).Id;
                current.FontId = SiteBuilderCatalog.ResolveFont(request.FontId).Id;
                current.SiteTitle = Trim(request.SiteTitle, 120);
                current.Tagline = Trim(request.Tagline, 200);
                current.MetaDescription = Trim(request.MetaDescription, 300);
                current.Language = string.Equals(request.Language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
                current.Sections = NormalizeSections(request.Sections, known);
                current.Pages = NormalizePages(request.Pages, known);
                current.UpdatedAtUtc = DateTime.UtcNow;
                return current;
            },
            cancellationToken);
    }

    public async Task<string> RenderPreviewAsync(
        string siteName,
        string? pageSlug = null,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(siteName, cancellationToken);
        var prefix = $"/api/v1/sitebuilder/{Uri.EscapeDataString(project.SiteName)}/images/";

        // Önizlemede de GERÇEK form hedefi kullanılır. Sahte bir hedef koysaydık, "form basılıyor
        // mu" sorusunun cevabı önizlemede ve yayında farklı olurdu — yani önizleme, yayınlanacak
        // sayfayı temsil etmezdi ve kullanıcı formun olmadığını ancak yayından sonra görürdü.
        var action = await BuildContactFormActionAsync(project.SiteName, cancellationToken);
        var rendered = SiteBuilderRenderer.RenderAll(project, SiteBuilderRenderContext.ForPreview(prefix, action));

        var slug = (pageSlug ?? string.Empty).Trim();
        var page = rendered.Pages.FirstOrDefault(item => string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase));

        // İstenen sayfa yoksa (silinmiş bir sayfa hâlâ seçili olabilir) ANA SAYFA gösterilir.
        // Boş bir çerçeve göstermek, kullanıcıya "önizleme bozuldu" dedirtirdi.
        return (page ?? rendered.Pages[0]).Html;
    }

    public async Task<SiteBuilderPublishPlanDto> PlanPublishAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var project = await LoadAsync(site.Name, cancellationToken) ?? CreateStarterProject(site.Name);

        var plan = new SiteBuilderPublishPlanDto { SiteName = site.Name };

        string root;
        try
        {
            root = ResolvePublishRoot(site);
        }
        catch (AppException exception)
        {
            // Plan uçu, engeli İSTİSNA olarak fırlatmak yerine anlatır: kullanıcı ekranda sebebi
            // görüp düzeltebilsin (yayın düğmesi de bu bilgiyle kapatılır).
            plan.BlockedReasonKey = exception.MessageKey;
            return plan;
        }

        plan.TargetPath = root;

        var rendered = SiteBuilderRenderer.RenderAll(project, SiteBuilderRenderContext.Published);
        var writes = BuildWriteList(project, rendered);
        var existing = writes.Where(relative => TargetExists(root, relative)).ToList();
        var decision = SiteBuilderPublishPlanner.Decide(writes, existing, project.ManagedFiles, overwriteConfirmed: false);

        plan.FilesToWrite = decision.FilesToWrite.ToList();
        plan.Conflicts = decision.Conflicts.ToList();
        plan.RequiresConfirmation = decision.RequiresConfirmation;
        plan.PageCount = rendered.Pages.Count;

        // ÖKSÜZLER PLANDA GÖSTERİLİR, SİLİNMEZ. Kullanıcı bir sayfayı kurucudan kaldırdığında
        // eski HTML adresinde durmaya devam eder; bunu yayından ÖNCE görmesi gerekir. Diskte hâlâ
        // duranlar süzülür — daha önce elle silinmiş bir dosyayı "hâlâ orada" diye göstermek,
        // kullanıcıyı olmayan bir dosyayı aramaya gönderirdi.
        plan.OrphanFiles = SiteBuilderPublishPlanner
            .Orphans(project.ManagedFiles, writes)
            .Where(relative => TargetExists(root, relative))
            .ToList();

        plan.ContactFormBlockedReasonKey = await ContactFormBlockedReasonAsync(project, cancellationToken);
        plan.ExistingFileCount = CountExistingFiles(root);
        return plan;
    }

    public async Task<SiteBuilderPublishResultDto> PublishAsync(
        string siteName,
        PublishSiteBuilderRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var root = ResolvePublishRoot(site);

        var gate = PublishLocks.GetOrAdd(site.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var project = await LoadAsync(site.Name, cancellationToken) ?? CreateStarterProject(site.Name);

            // FORM HEDEFİ YAYIN ANINDA ÇÖZÜLÜR. Çözülemezse form BASILMAZ — çalışmayan bir form
            // basmaktansa hiç basmamak. Sebep yayın planında kullanıcıya ADIYLA söylenmiş durumda.
            var action = await BuildContactFormActionAsync(site.Name, cancellationToken);
            var rendered = SiteBuilderRenderer.RenderAll(project, SiteBuilderRenderContext.ForPublish(action));

            var writes = BuildWriteList(project, rendered);
            var existing = writes.Where(relative => TargetExists(root, relative)).ToList();
            var decision = SiteBuilderPublishPlanner.Decide(
                writes,
                existing,
                project.ManagedFiles,
                request.OverwriteExistingFiles);

            if (!decision.Allowed)
            {
                // 409: istek biçimsel olarak doğru, ama mevcut durumla çelişiyor. Kullanıcı planı
                // görüp onay verirse aynı istek başarılı olur.
                throw new AppException("sitebuilder.overwrite_confirmation_required", 409, 409);
            }

            var pagesByPath = rendered.Pages.ToDictionary(
                page => page.RelativePath,
                page => page.Html,
                StringComparer.OrdinalIgnoreCase);

            var written = new List<string>();

            foreach (var relative in decision.FilesToWrite)
            {
                var target = SiteBuilderPathGuard.ResolveWriteTarget(root, relative);
                EnsureParentDirectory(root, target);

                if (pagesByPath.TryGetValue(relative, out var html))
                {
                    await WriteTextAsync(target, html, cancellationToken);
                }
                else if (string.Equals(relative, SiteBuilderLayout.StyleFile, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(target, rendered.Css, cancellationToken);
                }
                else
                {
                    await CopyImageAsync(site.Name, relative, target, cancellationToken);
                }

                written.Add(relative);
            }

            // ÖKSÜZLER: bu yayında artık üretilmeyen ama diskte duran kurucu dosyaları. Silinmez,
            // sayılır ve kullanıcıya döner (gerekçe: SiteBuilderPublishPlanner.Orphans).
            var orphans = SiteBuilderPublishPlanner
                .Orphans(project.ManagedFiles, writes)
                .Where(relative => TargetExists(root, relative))
                .ToList();

            var publishedAt = DateTime.UtcNow;
            await fileStore.UpdateAsync(
                ProjectPath(site.Name),
                project,
                current =>
                {
                    current.Published = true;
                    current.PublishedAtUtc = publishedAt;
                    current.PublishedPath = root;

                    // Bir sonraki yayında "bu dosya bizim mi" sorusunun cevabı. Eski liste
                    // KORUNUR ve birleştirilir: tasarımdan çıkarılmış bir görsel diskte kalır ve
                    // onu yeniden ekleyen kullanıcıya gereksiz "üzerine yazılacak" uyarısı çıkmasın.
                    //
                    // AMA DİSKTE OLMAYAN GİRDİ DÜŞÜRÜLÜR. Aksi hâlde şu sessiz veri kaybı mümkün
                    // olurdu: kullanıcı kurucudan bir sayfayı kaldırır, dosyasını dosya
                    // yöneticisinden siler, sonra AYNI yola kendi elleriyle bir sayfa koyar.
                    // Liste temizlenmeseydi o yol hâlâ "bizim" sayılır ve bir sonraki yayın
                    // kullanıcının kendi dosyasını UYARISIZ ezerdi. Ölçüm TargetExists ile
                    // yapılıyor; o da ölçemediğinde "var" diyor, yani belirsizlikte girdi KALIR.
                    current.ManagedFiles = current.ManagedFiles
                        .Where(item => TargetExists(root, item))
                        .Concat(written)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return current;
                },
                cancellationToken);

            logger.LogInformation(
                "Site kurucu yayini tamamlandi: {Site} -> {Path} ({Count} dosya, {Pages} sayfa, {Orphans} oksuz)",
                site.Name,
                root,
                written.Count,
                rendered.Pages.Count,
                orphans.Count);

            return new SiteBuilderPublishResultDto
            {
                SiteName = site.Name,
                TargetPath = root,
                WrittenFiles = written,
                OverwrittenFiles = decision.Conflicts.ToList(),
                OrphanFiles = orphans,
                PageCount = rendered.Pages.Count,
                PublishedAtUtc = publishedAt,
                PreviewUrl = BuildSiteUrl(site)
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<SiteBuilderImageDto>> GetImagesAsync(
        string siteName,
        CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var folder = fileStore.EnsureDirectory(ImageFolder(site.Name));

        return new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(file => SiteBuilderImageValidator.IsStoredNameValid(file.Name))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new SiteBuilderImageDto
            {
                Name = file.Name,
                SizeBytes = file.Length,
                UploadedAtUtc = file.CreationTimeUtc
            })
            .ToList();
    }

    public async Task<SiteBuilderImageDto> UploadImageAsync(
        string siteName,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);

        // Önce belleğe alınır: karar dosyanın İÇERİĞİNE bakılarak verildiği için baytları görmek
        // şart. Üst sınır (5 MB) küçük olduğundan bu güvenli; sınırı aşan akış zaten okunmadan
        // reddedilir.
        if (declaredLength > SiteBuilderCatalog.MaxImageBytes)
        {
            throw new AppException("sitebuilder.image_too_large", 413, 413);
        }

        using var buffer = new MemoryStream();
        await CopyLimitedAsync(content, buffer, SiteBuilderCatalog.MaxImageBytes, cancellationToken);
        var bytes = buffer.ToArray();

        var validation = SiteBuilderImageValidator.Validate(bytes.LongLength, bytes.AsSpan(0, Math.Min(bytes.Length, 32)));
        if (!validation.Accepted)
        {
            throw new AppException(validation.ErrorKey, 400, 400);
        }

        // Saklanacak ad panelin ürettiği ad; kullanıcının verdiği ad HİÇ kullanılmaz.
        var name = SiteBuilderImageValidator.BuildStoredName(validation.Extension);
        var folder = fileStore.EnsureDirectory(ImageFolder(site.Name));
        var path = Path.Combine(folder, name);

        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        return new SiteBuilderImageDto
        {
            Name = name,
            SizeBytes = bytes.LongLength,
            UploadedAtUtc = DateTime.UtcNow
        };
    }

    public async Task DeleteImageAsync(string siteName, string imageName, CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var path = ResolveImagePath(site.Name, imageName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // Tasarımda o görsele yapılan atıflar da temizlenir; aksi hâlde sayfada kırık görsel kalırdı.
        await fileStore.UpdateAsync(
            ProjectPath(site.Name),
            CreateStarterProject(site.Name),
            current =>
            {
                foreach (var section in current.Sections)
                {
                    if (string.Equals(section.ImageName, imageName, StringComparison.OrdinalIgnoreCase))
                    {
                        section.ImageName = string.Empty;
                    }

                    foreach (var item in section.Items)
                    {
                        if (string.Equals(item.ImageName, imageName, StringComparison.OrdinalIgnoreCase))
                        {
                            item.ImageName = string.Empty;
                        }
                    }
                }

                return current;
            },
            cancellationToken);
    }

    public async Task<(string PhysicalPath, string ContentType)> OpenImageAsync(
        string siteName,
        string imageName,
        CancellationToken cancellationToken = default)
    {
        var site = await ResolveSiteAsync(siteName, cancellationToken);
        var path = ResolveImagePath(site.Name, imageName);
        if (!File.Exists(path))
        {
            throw new AppException("sitebuilder.image_not_found", 404, 404);
        }

        return (path, ContentTypeFor(Path.GetExtension(path)));
    }

    // ---------------------------------------------------------------- yardımcılar

    private async Task<AdaPanel.Core.Dtos.Sites.SiteDto> ResolveSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        // Panelin KENDİ araçları da birer IIS sitesidir (AdaPanel.Webmail, AdaPanel.WebDb.*).
        // Kurucu oraya yayın yaparsa çalışan aracın index'ini ezer ve kullanıcı aracın neden
        // kaybolduğunu anlayamaz. Site oluşturma tarafında aynı koruma zaten var.
        var trimmed = (siteName ?? string.Empty).Trim();
        if (trimmed.StartsWith("AdaPanel.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Default Web Site", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("sitebuilder.site_reserved", 409, 409);
        }

        return await iisSiteService.GetSiteAsync(trimmed, cancellationToken);
    }

    /// <summary>Yayın kökü: uygulama kökü varsa o, yoksa fiziksel yol. Doğrulanmış hâlde döner.</summary>
    private static string ResolvePublishRoot(AdaPanel.Core.Dtos.Sites.SiteDto site)
    {
        var raw = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        return SiteBuilderPathGuard.EnsureUsableRoot(raw);
    }

    /// <summary>
    /// Yayında yazılacak göreli yollar: BÜTÜN sayfalar, stil ve tasarımda GERÇEKTEN kullanılan görseller.
    /// </summary>
    /// <remarks>
    /// Sayfa listesi oluşturucudan gelir, burada yeniden hesaplanmaz. İki yerde hesaplansaydı biri
    /// bir sayfayı düşürebilir (ör. geçersiz slug) ve o sayfa "yazılacak" sayılıp içeriği HİÇ
    /// üretilmemiş olurdu — yani boş bir dosya yazılırdı.
    /// </remarks>
    private static List<string> BuildWriteList(SiteBuilderProjectDto project, RenderedPages rendered)
    {
        var list = rendered.Pages.Select(page => page.RelativePath).ToList();
        list.Add(SiteBuilderLayout.StyleFile);

        foreach (var image in UsedImageNames(project))
        {
            list.Add(SiteBuilderLayout.ImageFolder + "/" + image);
        }

        return list;
    }

    /// <summary>
    /// Tasarımda atıf yapılan görseller (ana sayfa + BÜTÜN ek sayfalar). Kullanılmayan yüklemeler
    /// siteye KOPYALANMAZ — müşterinin sunucusunda gereksiz dosya bırakmamak için.
    /// </summary>
    /// <remarks>
    /// Ek sayfalar unutulsaydı, o sayfaların görselleri siteye hiç kopyalanmaz ve sayfa kırık
    /// görsellerle yayınlanırdı; üstelik bir sonraki yayında o dosyalar ÖKSÜZ sanılırdı.
    /// </remarks>
    private static IEnumerable<string> UsedImageNames(SiteBuilderProjectDto project)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectImages(project.Sections, names);
        foreach (var page in project.Pages ?? [])
        {
            CollectImages(page.Sections, names);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectImages(IReadOnlyList<SiteBuilderSectionDto>? sections, HashSet<string> names)
    {
        foreach (var section in sections ?? [])
        {
            if (!section.Enabled)
            {
                continue;
            }

            if (SiteBuilderImageValidator.IsStoredNameValid(section.ImageName))
            {
                names.Add(section.ImageName.Trim());
            }

            foreach (var item in section.Items ?? [])
            {
                if (SiteBuilderImageValidator.IsStoredNameValid(item.ImageName))
                {
                    names.Add(item.ImageName.Trim());
                }
            }
        }
    }

    /// <summary>
    /// İletişim formunun gönderim hedefi; kurulamıyorsa BOŞ.
    /// </summary>
    /// <remarks>
    /// Ana ad panelin KENDİ erişim ayarından (<c>config/panel-preferences.json</c>) okunur —
    /// isteğin <c>Host</c> başlığından değil. Gerekçe <c>SiteBuilderContactEndpoint.BuildAction</c>
    /// içinde yazılı: yönetici panele <c>localhost</c> üzerinden bağlanmış olabilir ve o adres
    /// müşterinin sitesine gömülseydi form, ziyaretçi için asla çalışmazdı.
    /// </remarks>
    private async Task<string> BuildContactFormActionAsync(string siteName, CancellationToken cancellationToken)
    {
        var preferences = await fileStore.ReadAsync(
            PanelPreferencesPath,
            new AdaPanel.Core.Dtos.Settings.PanelPreferencesDto(),
            cancellationToken);

        return SiteBuilderContactEndpoint.BuildAction(preferences.PanelHostName, StorageKey(siteName));
    }

    /// <summary>
    /// İletişim formu İSTENDİĞİ hâlde basılamıyorsa sebebin çeviri anahtarı; sorun yoksa boş.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu, özelliğin sessizce kaybolmamasının tek yolu. Kullanıcı "form açık" kutusunu işaretler,
    /// yayınlar ve sitesinde form GÖRMEZ — sebebini söylemezsek o kullanıcının elinde kalan tek
    /// bilgi "panel bozuk" olur. Üç sebep de ayrı ayrı adlandırılıyor, çünkü üçünün düzeltmesi
    /// farklı ekranlarda.
    /// </para>
    /// <para>
    /// Sıra bilinçli: önce KULLANICININ düzeltebileceği (adres), sonra YÖNETİCİNİN düzeltebileceği
    /// (panel erişim adresi), en sonda düzeltilemeyen (site adı ucta kullanılamıyor).
    /// </para>
    /// </remarks>
    private async Task<string> ContactFormBlockedReasonAsync(
        SiteBuilderProjectDto project,
        CancellationToken cancellationToken)
    {
        var contact = (project.Sections ?? [])
            .FirstOrDefault(section => string.Equals(section.Type, "contact", StringComparison.OrdinalIgnoreCase));

        if (contact is null || !contact.Enabled || !contact.ContactFormEnabled)
        {
            return string.Empty;
        }

        if (!SiteBuilderContactForm.IsUsableAddress(contact.Email))
        {
            return "sitebuilder.form_recipient_missing";
        }

        var key = StorageKey(project.SiteName);
        if (!SiteBuilderContactEndpoint.IsValidSiteKey(key))
        {
            return "sitebuilder.form_site_unsupported";
        }

        var preferences = await fileStore.ReadAsync(
            PanelPreferencesPath,
            new AdaPanel.Core.Dtos.Settings.PanelPreferencesDto(),
            cancellationToken);

        return SiteBuilderContactEndpoint.BuildAction(preferences.PanelHostName, key).Length == 0
            ? "sitebuilder.form_panel_host_missing"
            : string.Empty;
    }

    /// <summary>
    /// Ek sayfaları düzenler: geçersiz ya da yinelenen slug DÜŞÜRÜLÜR, üst sınır uygulanır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden düşürülüyor, düzeltilmiyor:</b> "Hakkımızda!!" gibi bir slug'ı temizleyip
    /// <c>hakkimizda</c> yapmak, kullanıcının yazmadığı bir adrese dosya yazmak olurdu — üstelik
    /// bir sonraki kaydetmede başka bir adrese düşerse her seferinde diskte yeni bir ÖKSÜZ sayfa
    /// bırakırdı. Arayüz slug'ı kullanıcıya gösterir ve kalıbı orada da uygular; sunucu son sözü
    /// söyler.
    /// </para>
    /// <para>
    /// <b>Neden ilk yazan kazanıyor:</b> aynı slug'lı iki sayfa aynı dosyaya yazılır ve ikincisi
    /// birincisini EZER; kullanıcı yazdığı içeriğin nereye gittiğini anlayamazdı.
    /// </para>
    /// </remarks>
    private static List<SiteBuilderPageDto> NormalizePages(
        IReadOnlyCollection<SiteBuilderPageDto>? pages,
        IReadOnlySet<string> knownImages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SiteBuilderPageDto>();

        foreach (var page in pages ?? [])
        {
            var slug = (page.Slug ?? string.Empty).Trim().ToLowerInvariant();
            if (!SiteBuilderPageSlug.IsValid(slug) || !seen.Add(slug))
            {
                continue;
            }

            result.Add(new SiteBuilderPageDto
            {
                Slug = slug,
                Title = Trim(page.Title, 120),
                MetaDescription = Trim(page.MetaDescription, 300),
                ShowInNav = page.ShowInNav,
                Sections = NormalizeSections(page.Sections, knownImages)
            });

            if (result.Count >= SiteBuilderPageSlug.MaxPages)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Hedef dosya var mı. Yol kaçışı/bağlantı noktası durumunda "var" sayılır: emin olamadığımız
    /// bir hedefi "yok" saymak, çakışma uyarısını sessizce atlamak olurdu.
    /// </summary>
    private static bool TargetExists(string root, string relative)
    {
        try
        {
            var target = SiteBuilderPathGuard.ResolveWriteTarget(root, relative);
            return File.Exists(target) || Directory.Exists(target);
        }
        catch (AppException)
        {
            return true;
        }
    }

    private static int CountExistingFiles(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }

            // Bağlantı noktalarını İZLEMEDEN say; sayım için başka bir diske dalmanın anlamı yok.
            return new DirectoryInfo(root)
                .EnumerateFileSystemInfos()
                .Count(entry => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Hedefin üst klasörlerini oluşturur; her adımda bağlantı noktası denetimiyle.</summary>
    private static void EnsureParentDirectory(string root, string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
        {
            throw new AppException("sitebuilder.target_invalid", 400, 400);
        }

        if (!PathContainment.IsRootOrInside(root, parent))
        {
            throw new AppException("sitebuilder.target_outside_root", 400, 400);
        }

        Directory.CreateDirectory(parent);

        // Oluşturduktan SONRA tekrar bakılır: aradaki bir segment zaten varsa ve bağlantıysa,
        // CreateDirectory hata vermez ve yazma sessizce başka bir yere giderdi.
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(root, parent);
    }

    /// <summary>Atomik yazım: önce geçici dosya, sonra taşı. Yarıda kesilen yayın yarım sayfa bırakmaz.</summary>
    private static async Task WriteTextAsync(string target, string content, CancellationToken cancellationToken)
    {
        var temporary = target + ".adatmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task CopyImageAsync(string siteName, string relative, string target, CancellationToken cancellationToken)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var source = ResolveImagePath(siteName, name);
        if (!File.Exists(source))
        {
            throw new AppException("sitebuilder.image_not_found", 404, 404);
        }

        var temporary = target + ".adatmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // En iyi çaba; geçici dosya kalsa bile yayın bozulmaz.
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                // Sınırı akış SIRASINDA uygular: bildirilen uzunluğa güvenmek, yalan söyleyen bir
                // istemcinin sunucu belleğini doldurmasına izin vermek olurdu.
                throw new AppException("sitebuilder.image_too_large", 413, 413);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task<SiteBuilderProjectDto?> LoadAsync(string siteName, CancellationToken cancellationToken)
    {
        var stored = await fileStore.ReadAsync<SiteBuilderProjectDto?>(ProjectPath(siteName), null, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        stored.SiteName = siteName;
        stored.Sections ??= [];
        stored.Pages ??= [];
        stored.ManagedFiles ??= [];
        return stored;
    }

    private async Task<HashSet<string>> KnownImageNamesAsync(string siteName, CancellationToken cancellationToken)
    {
        var images = await GetImagesAsync(siteName, cancellationToken);
        return new HashSet<string>(images.Select(image => image.Name), StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveImagePath(string siteName, string? imageName)
    {
        // Ad kalıbı doğrulanmadan yol kurulmaz: "../../web.config" gibi bir ad, kalıba uymadığı
        // için buraya hiç gelemez.
        if (!SiteBuilderImageValidator.IsStoredNameValid(imageName))
        {
            throw new AppException("sitebuilder.image_not_found", 404, 404);
        }

        // JsonFileStore.ResolvePath ikinci savunma hattı: veri kökünden çıkan her yolu reddeder.
        return fileStore.ResolvePath(ImageFolder(siteName) + "/" + imageName!.Trim());
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Bölümleri katalog tipine göre düzenler; bilinmeyen tip ATILIR, görsel adı doğrulanır.
    /// </summary>
    /// <remarks>
    /// Görsel adı, o siteye ait YÜKLENMİŞ görseller kümesiyle karşılaştırılır. Yalnız kalıba
    /// bakmak yetmezdi: başka bir sitenin görsel adı da kalıba uyar ve kiracı, komşusunun
    /// görselini kendi sayfasına koyabilirdi.
    /// </remarks>
    private static List<SiteBuilderSectionDto> NormalizeSections(
        IReadOnlyCollection<SiteBuilderSectionDto>? sections,
        IReadOnlySet<string> knownImages)
    {
        var result = new List<SiteBuilderSectionDto>();
        foreach (var type in SiteBuilderCatalog.SectionTypes)
        {
            var incoming = (sections ?? [])
                .FirstOrDefault(section => string.Equals(section.Type, type, StringComparison.OrdinalIgnoreCase));

            var section = new SiteBuilderSectionDto
            {
                Type = type,
                Enabled = incoming?.Enabled ?? true,
                Heading = Trim(incoming?.Heading, 160),
                Body = Trim(incoming?.Body, 4000),
                ImageName = ResolveImageName(incoming?.ImageName, knownImages),
                Email = Trim(incoming?.Email, 160),
                Phone = Trim(incoming?.Phone, 60),
                Address = Trim(incoming?.Address, 300),

                // FORM YALNIZ İLETİŞİM BÖLÜMÜNDE açılabilir. Başka bir bölümde işaretlenmiş bir
                // bayrak, hem sayfada karşılığı olmayan bir ayar üretir hem de kimliksiz uçta
                // "hangi bölüm alıcıyı taşıyor" sorusunu bulanıklaştırırdı; kural tek yerde ve
                // BURADA kesiliyor.
                ContactFormEnabled = string.Equals(type, "contact", StringComparison.Ordinal)
                                     && (incoming?.ContactFormEnabled ?? false),
                Items = (incoming?.Items ?? [])
                    // Üst sınır bilinçli: sınırsız kart, sınırsız büyüyen bir JSON ve sınırsız
                    // büyüyen bir sayfa demektir.
                    .Take(12)
                    .Select(item => new SiteBuilderItemDto
                    {
                        Title = Trim(item.Title, 120),
                        Text = Trim(item.Text, 1000),
                        ImageName = ResolveImageName(item.ImageName, knownImages)
                    })
                    .ToList()
            };

            result.Add(section);
        }

        return result;
    }

    private static string ResolveImageName(string? name, IReadOnlySet<string> knownImages)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return SiteBuilderImageValidator.IsStoredNameValid(trimmed) && knownImages.Contains(trimmed)
            ? trimmed
            : string.Empty;
    }

    private static string Trim(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>Sitenin ilk http bağlamasından adres kurar; yoksa boş (uydurmak yerine boş).</summary>
    private static string BuildSiteUrl(AdaPanel.Core.Dtos.Sites.SiteDto site)
    {
        var binding = site.Bindings.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.HostName));
        return binding is null ? string.Empty : $"http://{binding.HostName}/";
    }

    /// <summary>
    /// Başlangıç tasarımı: boş değil, DOLU. Teknik olmayan kullanıcı boş bir ekranda "şimdi ne
    /// yazacağım" diye kalmasın; her bölümde ne yazılacağını gösteren örnek metin bulunur.
    /// </summary>
    private static SiteBuilderProjectDto CreateStarterProject(string siteName) => new()
    {
        SiteName = siteName,
        TemplateId = SiteBuilderCatalog.Templates[0].Id,
        PaletteId = SiteBuilderCatalog.Palettes[0].Id,
        FontId = SiteBuilderCatalog.Fonts[0].Id,
        SiteTitle = siteName,
        Tagline = string.Empty,
        Language = "tr",
        Sections = SiteBuilderCatalog.SectionTypes
            .Select(type => new SiteBuilderSectionDto
            {
                Type = type,
                Enabled = true,
                Items = type is "services" or "gallery" ? [] : []
            })
            .ToList()
    };

    /// <summary>
    /// Panelin genel erişim ayarları. İletişim formunun gönderim hedefi (ana ad) buradan okunur.
    /// </summary>
    /// <remarks>
    /// Yol <c>PanelPublicAccessService</c> ile AYNI olmak zorunda. İki ayrı dosyaya bakılsaydı,
    /// yönetici "Panel erişimi" ekranından ana adı ayarlar ama form hedefi hiç dolmazdı ve sebebi
    /// görünmezdi. Ayrı bir servise bağlanmak yerine DTO'nun kendisi okunuyor: bu modül panelin
    /// erişim akışını yönetmiyor, yalnız kaydedilmiş sonucu okuyor.
    /// </remarks>
    private const string PanelPreferencesPath = "config/panel-preferences.json";

    private static string ProjectPath(string siteName) => ProjectPathForKey(StorageKey(siteName));

    /// <summary>
    /// Tasarım dosyasının yolu, DOĞRUDAN saklama anahtarından.
    /// </summary>
    /// <remarks>
    /// Kimliksiz iletişim formu ucu site ADINI bilmez, yalnız anahtarı taşır (adresi
    /// <c>SiteBuilderContactEndpoint</c> kuruyor). Bu aşırı yükleme, o ucun IIS'e hiç sormadan
    /// tasarımı okuyabilmesi için var — kimliksiz bir uçta sunucudaki bütün siteleri gezen bir
    /// çözüm, tek satırlık bir istekle iş yaptıran bir kaldıraç olurdu.
    /// </remarks>
    internal static string ProjectPathForKey(string storageKey) => $"config/sitebuilder/{storageKey}.json";

    private static string ImageFolder(string siteName) => $"sitebuilder/{StorageKey(siteName)}/img";

    /// <summary>
    /// Site adından dosya sistemi için güvenli, ÇAKIŞMAYAN bir anahtar üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yalnız geçersiz karakterleri değiştirmek yetmez: <c>".."</c> geçerli karakterlerden oluşur
    /// ve <c>"a b"</c> ile <c>"a_b"</c> aynı dosyaya düşerdi — yani bir kiracının tasarımı
    /// diğerininkini ezerdi. Bu yüzden ada, özgün adın karmasından türeyen bir sonek eklenir.
    /// </para>
    /// <para>
    /// Karma büyük/küçük harf duyarsızdır, çünkü IIS site adları da öyle: "Site" ve "site" aynı
    /// siteyi gösterir ve iki ayrı tasarım dosyasına düşmemelidir.
    /// </para>
    /// </remarks>
    internal static string StorageKey(string siteName)
    {
        var invalid = Path.GetInvalidFileNameChars();

        // KÜÇÜK HARFE İNDİRİLİR. IIS site adları büyük/küçük harf duyarsızdır: "Ornek.com" ile
        // "ornek.COM" AYNI sitedir. Ad olduğu gibi kullanılsaydı aynı site iki ayrı tasarım
        // dosyasına düşerdi — kullanıcı kaydettiği tasarımı bir sonraki açılışta bulamazdı ve
        // "yayınla" da yanlış tasarımı basardı. (Karma zaten küçük harften hesaplanıyordu; asıl
        // fark ön ekteydi ve bu testle yakalandı.)
        var safe = new string((siteName ?? string.Empty)
            .ToLowerInvariant()
            .Select(character => invalid.Contains(character) || character is '.' or ' ' ? '_' : character)
            .ToArray());

        if (safe.Length > 48)
        {
            safe = safe[..48];
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes((siteName ?? string.Empty).ToLowerInvariant())))[..8];

        return safe.Length == 0 ? hash : $"{safe}-{hash}";
    }
}
