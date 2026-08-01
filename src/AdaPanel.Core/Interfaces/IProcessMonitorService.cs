using AdaPanel.Core.Dtos.Monitoring;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// htop benzeri canlı süreç/kaynak izleyici. Mevcut <see cref="ISystemMonitoringService"/> yalnız
/// TOPLAM CPU/RAM/disk verir; bu servis kırılımı (hangi süreç hangi siteye/bileşene ait) üretir.
/// UYGULAMA NOTU: CPU yüzdesi iki ölçüm arasındaki farktan çıktığı için gerçekleştirmesi durum tutar
/// ve DI'da Singleton kaydedilmelidir.
/// </summary>
public interface IProcessMonitorService
{
    /// <param name="limit">Döndürülecek satır sayısı (kalanı toplam sayıda görünür).</param>
    /// <param name="sortBy">"cpu" (varsayılan) veya "memory".</param>
    Task<ProcessSnapshotDto> GetSnapshotAsync(int limit = 30, string sortBy = "cpu", CancellationToken cancellationToken = default);
}
