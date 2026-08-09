using AdaPanel.Core.Dtos.Users;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Bayi tavanlarının KALICI KAYDI (config/reseller-quotas.json). Yalnız saklar; ölçüm ve
/// dayatma <c>ResellerQuotaGuard</c> içindedir.
/// </summary>
/// <remarks>
/// Bilerek yalnız depolama: ölçüm <see cref="ICustomerScopeService"/> gerektirir, kapsam servisi
/// ise tavanı okumak için bunu gerektirir. İkisi tek sınıfta toplansaydı DI grafiği DÖNGÜYE
/// girerdi (panel açılışta patlardı). Bağımlılık tek yönlü tutuldu: kapsam → tavan deposu.
/// </remarks>
public interface IResellerQuotaService
{
    /// <summary>Bayinin tavanı; tanımlı değilse <c>null</c> (sınırsız — mevcut davranış).</summary>
    Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tavan atar. Hedef kullanıcının rolü DOĞRULANIR: tavan yalnız bayiye aittir ve müşteriye
    /// yazılması, plandan gelen limitleri sessizce ezerdi.
    /// </summary>
    Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tavanı kaldırır (sınırsıza döner).</summary>
    Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default);
}
