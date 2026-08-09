using AdaPanel.Core.Dtos.Users;

namespace AdaPanel.Core.Interfaces;

/// <summary>Yeniden kullanılabilir müşteri limit şablonları (Servis Planlari).</summary>
public interface IServicePlanService
{
    Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plan oluşturur. <paramref name="ownerId"/> doluysa plan o bayinin ÖZEL planıdır; null ise
    /// sistem planı (yönetici).
    /// </summary>
    /// <remarks>
    /// Sahiplik ayrı bir çağrıyla değil BURADA yazılır: iki adım olsaydı ikinci adım hata
    /// verdiğinde ortada sahipsiz — yani sistem planı görünen — bir plan kalırdı ve onu oluşturan
    /// bayi kendi planını bir daha düzenleyemezdi.
    /// </remarks>
    Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default);

    Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default);

    Task DeletePlanAsync(string id, CancellationToken cancellationToken = default);
}
