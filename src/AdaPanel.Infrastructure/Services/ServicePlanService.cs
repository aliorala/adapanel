using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Services;

/// <summary>
/// Müşteri limit şablonları (Servis Planlari). DB migration'a gerek kalmaması için
/// config/service-plans.json içinde tutulur.
/// </summary>
public sealed class ServicePlanService(JsonFileStore fileStore) : IServicePlanService
{
    private const string StatePath = "config/service-plans.json";

    public async Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await fileStore.ReadAsync(StatePath, Array.Empty<ServicePlanDto>(), cancellationToken);
        return plans.OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default)
    {
        var plans = await fileStore.ReadAsync(StatePath, Array.Empty<ServicePlanDto>(), cancellationToken);
        return plans.FirstOrDefault(plan => string.Equals(plan.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new AppException("plans.name_required", 400, 400);
        }

        var plans = (await fileStore.ReadAsync(StatePath, Array.Empty<ServicePlanDto>(), cancellationToken)).ToList();
        var plan = new ServicePlanDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            MaxSites = Math.Max(0, request.MaxSites),
            MaxDiskMb = Math.Max(0, request.MaxDiskMb),
            MaxDatabases = Math.Max(0, request.MaxDatabases),
            MaxMailboxes = Math.Max(0, request.MaxMailboxes)
        };
        plans.Add(plan);
        await fileStore.WriteAsync(StatePath, plans, cancellationToken);
        return plan;
    }

    public async Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default)
    {
        var plans = (await fileStore.ReadAsync(StatePath, Array.Empty<ServicePlanDto>(), cancellationToken)).ToList();
        var plan = plans.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("plans.not_found", 404, 404);

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            plan.Name = name;
        }

        plan.MaxSites = Math.Max(0, request.MaxSites);
        plan.MaxDiskMb = Math.Max(0, request.MaxDiskMb);
        plan.MaxDatabases = Math.Max(0, request.MaxDatabases);
        plan.MaxMailboxes = Math.Max(0, request.MaxMailboxes);
        await fileStore.WriteAsync(StatePath, plans, cancellationToken);
        return plan;
    }

    public async Task DeletePlanAsync(string id, CancellationToken cancellationToken = default)
    {
        var plans = (await fileStore.ReadAsync(StatePath, Array.Empty<ServicePlanDto>(), cancellationToken)).ToList();
        if (plans.RemoveAll(plan => string.Equals(plan.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await fileStore.WriteAsync(StatePath, plans, cancellationToken);
        }
    }
}
