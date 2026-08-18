using AdaPanel.Core.Dtos.SiteBuilder;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Site kurucu: teknik olmayan bir kullanıcının hazır şablondan tek sayfalık bir site kurup
/// yayınlamasını sağlar (Plesk'teki "Web Presence Builder" karşılığının dar bir ilk sürümü).
/// </summary>
/// <remarks>
/// Arayüz Core yerine Infrastructure altında duruyor: kurucunun tamamı (katalog, oluşturucu,
/// yayın koruması) tek klasörde toplandı ve Core sözleşmesine yalnız DTO'lar girdi. Modül tek
/// parça hâlinde okunabilir ve taşınabilir kalıyor.
/// </remarks>
public interface ISiteBuilderService
{
    /// <summary>Şablon/palet/yazı tipi seçenekleri.</summary>
    SiteBuilderCatalogDto GetCatalog();

    /// <summary>
    /// Sitenin kayıtlı tasarımı. Hiç kaydedilmemişse doldurulmuş bir BAŞLANGIÇ tasarımı döner —
    /// boş ekran, teknik olmayan kullanıcıyı ilk adımda durdurur.
    /// </summary>
    Task<SiteBuilderProjectDto> GetProjectAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteBuilderProjectDto> SaveProjectAsync(
        string siteName,
        SaveSiteBuilderProjectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Panelde gösterilecek, kendi içinde tam sayfa (stil satır içi, görseller panel API'sinden).</summary>
    Task<string> RenderPreviewAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Yayın öncesi plan: nereye yazılacak, hangi dosyalar ezilecek. DİSKE YAZMAZ.</summary>
    Task<SiteBuilderPublishPlanDto> PlanPublishAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteBuilderPublishResultDto> PublishAsync(
        string siteName,
        PublishSiteBuilderRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SiteBuilderImageDto>> GetImagesAsync(
        string siteName,
        CancellationToken cancellationToken = default);

    /// <summary>Görseli doğrular ve panelin veri kökünde saklar. Site klasörüne DOKUNMAZ.</summary>
    Task<SiteBuilderImageDto> UploadImageAsync(
        string siteName,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default);

    Task DeleteImageAsync(string siteName, string imageName, CancellationToken cancellationToken = default);

    /// <summary>Önizleme için görselin fiziksel yolu ve içerik türü.</summary>
    Task<(string PhysicalPath, string ContentType)> OpenImageAsync(
        string siteName,
        string imageName,
        CancellationToken cancellationToken = default);
}
