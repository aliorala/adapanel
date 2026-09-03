using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface ISiteProvisioningService
{
    Task<SiteDto> CreateAndProvisionAsync(
        CreateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitenin belge kökünü (IIS fiziksel yolu) değiştirir — Laravel/Symfony için <c>...\public</c>. IIS'te
    /// kalıcıdır; yeni kökte index.php varsayılan belge yapılır (IIS varsayılanı index.php içermez → "/" = 403).
    /// </summary>
    Task<SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default);

    /// <summary>Laravel/WordPress/Symfony pretty-URL yönlendirmesi (index.php front controller). URL Rewrite modülü gerekir.</summary>
    Task<SiteDto> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitenin yazma iznini (app pool kimliği, kök Modify) yeniden uygular — bu özellikten önce
    /// kurulmuş siteler için — ve SONUCU DİSKTEN OKUYARAK doğrular.
    /// </summary>
    /// <remarks>
    /// Dönüş tipi <see cref="SiteDto"/> DEĞİL: izin uygulaması sessizce başarısız olabilir
    /// (<c>icacls</c> hata 1332'de hiçbir şey yapmaz ve <c>/C</c> ile 0 döner) ve site kaydının
    /// içinde bu cevabı taşıyacak bir alan yok. Alan olmadığı için uç "onarıldı" demeyi
    /// alışkanlık hâline getirmişti; cevabın taşınabilir olması, doğru cevabın ön koşuludur.
    /// </remarks>
    Task<SitePermissionRepairResultDto> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken = default);
}
