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
                current.UpdatedAtUtc = DateTime.UtcNow;
                return current;
            },
            cancellationToken);
    }

    public async Task<string> RenderPreviewAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(siteName, cancellationToken);
        var prefix = $"/api/v1/sitebuilder/{Uri.EscapeDataString(project.SiteName)}/images/";
        return SiteBuilderRenderer.Render(project, SiteBuilderRenderContext.ForPreview(prefix)).Html;
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

        var writes = BuildWriteList(project);
        var existing = writes.Where(relative => TargetExists(root, relative)).ToList();
        var decision = SiteBuilderPublishPlanner.Decide(writes, existing, project.ManagedFiles, overwriteConfirmed: false);

        plan.FilesToWrite = decision.FilesToWrite.ToList();
        plan.Conflicts = decision.Conflicts.ToList();
        plan.RequiresConfirmation = decision.RequiresConfirmation;
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
            var writes = BuildWriteList(project);
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

            var rendered = SiteBuilderRenderer.Render(project, SiteBuilderRenderContext.Published);
            var written = new List<string>();

            foreach (var relative in decision.FilesToWrite)
            {
                var target = SiteBuilderPathGuard.ResolveWriteTarget(root, relative);
                EnsureParentDirectory(root, target);

                if (string.Equals(relative, SiteBuilderLayout.IndexFile, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(target, rendered.Html, cancellationToken);
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
                    current.ManagedFiles = current.ManagedFiles
                        .Concat(written)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return current;
                },
                cancellationToken);

            logger.LogInformation(
                "Site kurucu yayini tamamlandi: {Site} -> {Path} ({Count} dosya)",
                site.Name,
                root,
                written.Count);

            return new SiteBuilderPublishResultDto
            {
                SiteName = site.Name,
                TargetPath = root,
                WrittenFiles = written,
                OverwrittenFiles = decision.Conflicts.ToList(),
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

    /// <summary>Yayında yazılacak göreli yollar: sayfa, stil ve tasarımda GERÇEKTEN kullanılan görseller.</summary>
    private static List<string> BuildWriteList(SiteBuilderProjectDto project)
    {
        var list = new List<string>(SiteBuilderLayout.CoreFiles);
        foreach (var image in UsedImageNames(project))
        {
            list.Add(SiteBuilderLayout.ImageFolder + "/" + image);
        }

        return list;
    }

    /// <summary>
    /// Tasarımda atıf yapılan görseller. Kullanılmayan yüklemeler siteye KOPYALANMAZ — müşterinin
    /// sunucusunda gereksiz dosya bırakmamak için.
    /// </summary>
    private static IEnumerable<string> UsedImageNames(SiteBuilderProjectDto project)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in project.Sections ?? [])
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

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
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

    private static string ProjectPath(string siteName) => $"config/sitebuilder/{StorageKey(siteName)}.json";

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
