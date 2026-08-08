using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site uygulama kökleri için Windows FSRM tabanlı disk kotası yönetimi.
/// </summary>
public interface IDiskQuotaService
{
    Task<SiteDiskQuotaDto> GetQuotaAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteDiskQuotaDto> SetQuotaAsync(string siteName, SetSiteDiskQuotaRequest request, CancellationToken cancellationToken = default);

    Task<SiteDiskQuotaDto> RemoveQuotaAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disk kotası için gereken Windows rolünü (FSRM) kurar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arayüz "önce FSRM özelliğini ekleyin" diyordu ama panelde ekleme yolu YOKTU — çıkmaz sokak.
    /// Oysa panel diğer Windows rollerini (DNS, FTP, ASP.NET, Containers) zaten kendisi kuruyor;
    /// bunu kurmamak için bir sebep yoktu.
    /// </para>
    /// <para>
    /// FSRM yalnız Windows SERVER sürümlerinde vardır; istemci sürümlerinde kurulamaz ve bu durum
    /// net bir hatayla söylenir ("kurulamadı" diye belirsiz bırakmak yerine).
    /// </para>
    /// </remarks>
    Task<SiteDiskQuotaDto> InstallFsrmAsync(string siteName, CancellationToken cancellationToken = default);
}
