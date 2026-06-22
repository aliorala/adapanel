using AdaPanel.Core.Dtos.Users;

namespace AdaPanel.Core.Interfaces;

/// <summary>Yeniden kullanılabilir müşteri limit şablonları (Plesk Service Plans).</summary>
public interface IServicePlanService
{
    Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default);

    Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, CancellationToken cancellationToken = default);

    Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default);

    Task DeletePlanAsync(string id, CancellationToken cancellationToken = default);
}
