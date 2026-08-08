using AdaPanel.Core.Dtos.Components;

namespace AdaPanel.Core.Interfaces;

public interface IComponentBootstrapper
{
    Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Kurulu bileşenlerin upstream'deki son sürümlerini canlı çözer, sonucu önbelleğe
    /// alır ve güncelleme bilgisini içeren güncel katalogu döndürür.
    /// </summary>
    Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Güncellemesi bekleyen kurulu bileşen sayısı. UCUZ: yalnız kurulu bileşenleri ve son kontrolün
    /// önbelleğini okur — ağa çıkmaz, sistem taraması yapmaz. Rozet/uyarı sayaçları bunu kullanır ki
    /// sayı kurulumdan hemen sonra doğru olsun (otomasyon durumu günde bir güncellenir, geç kalır).
    /// </summary>
    Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default);

    /// <param name="force">
    /// true ise bileşen kurulu ve sağlıklı olsa bile kurulum YENİDEN çalıştırılır. Sürüm
    /// yükseltmesinin tek yolu budur; aksi halde kurulum "zaten kurulu" deyip hiçbir şey yapmaz.
    /// </param>
    Task<ComponentInstallResultDto> InstallAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bileşeni kaldırır ve TEMİZLENEMEYENLERİ döndürür (boş liste = tam temizlik).
    /// </summary>
    /// <remarks>
    /// Uyarılar önceden yalnızca denetim günlüğüne yazılıyordu; API her hâlükârda "kaldırıldı"
    /// diyordu. Yani servis durdurulamamış, dosyalar kilitli kalmış olsa bile kullanıcı BAŞARI
    /// görüyordu — bileşen listeden kayboluyor ama disk ve servis sunucuda duruyordu, üstelik
    /// panel onu artık bilmediği için yeniden kurmak da bozuk duruma düşüyordu.
    /// </remarks>
    Task<IReadOnlyCollection<string>> RemoveAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}
