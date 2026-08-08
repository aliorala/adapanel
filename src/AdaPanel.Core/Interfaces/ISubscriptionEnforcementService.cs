using AdaPanel.Core.Dtos.Users;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Abonelik kurallarını UYGULAR: trafik kotası dolunca ve süre bitince askıya alır, sebep
/// ortadan kalkınca geri açar.
/// </summary>
/// <remarks>
/// <para>
/// Limitleri <i>tanımlamak</i> tek başına iş modeli değildir; panelde kota vardı ama dolunca
/// hiçbir şey olmuyordu. Bu servis kuralı çalıştıran taraftır.
/// </para>
/// <para>
/// <b>Askı, giriş engelinden AYRIDIR.</b> Askıya alınan müşteri panele girebilmeli — durumunu
/// görsün, kotasını okusun, planını yükseltsin. Yayından kalkan şey SİTELERİDİR. Girişi de
/// kapatmak, müşteriyi sorunu çözemez hâle getirir ve destek yükünü artırır.
/// </para>
/// </remarks>
public interface ISubscriptionEnforcementService
{
    /// <summary>Tek bir aboneliğin durumunu ölçer (askıya almadan).</summary>
    Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bütün abonelikleri denetler; kuralı çiğneyeni askıya alır, sebebi kalmayanı geri açar.
    /// </summary>
    /// <returns>Durumu DEĞİŞEN abonelikler (askıya alınan + geri açılan).</returns>
    Task<IReadOnlyCollection<SubscriptionEnforcementChangeDto>> EnforceAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Aboneliği elle askıya alır ve sitelerini durdurur.</summary>
    Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Askıyı kaldırır ve sitelerini başlatır.</summary>
    Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default);
}
