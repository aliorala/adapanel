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

    public async Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new AppException("plans.name_required", 400, 400);
        }

        var plan = new ServicePlanDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            // Sahip yalnız OLUŞTURMADA yazılır; güncelleme sahibi DEĞİŞTİRMEZ (aşağıya bkz.).
            OwnerId = ownerId?.ToString("N") ?? string.Empty,
            MaxSites = Math.Max(0, request.MaxSites),
            MaxDiskMb = Math.Max(0, request.MaxDiskMb),
            MaxDatabases = Math.Max(0, request.MaxDatabases),
            MaxMailboxes = Math.Max(0, request.MaxMailboxes),
            MaxScheduledTasks = Math.Max(0, request.MaxScheduledTasks),
            // Bu üç alan eklendiğinde eşleştirme GÜNCELLENMEMİŞTİ: istek doluyken kayda 0 yazılıyor,
            // API "başarılı" dönüyor ve kullanıcı limiti koyduğunu sanıyordu. Trafik kotası
            // özelliği fiilen ölüydü. Uçtan uca test yakaladı; birim testleri yakalayamamıştı.
            MaxFtpUsers = Math.Max(0, request.MaxFtpUsers),
            MaxTrafficGbPerMonth = Math.Max(0, request.MaxTrafficGbPerMonth),
            ValidityDays = Math.Max(0, request.ValidityDays),
            // Eşleştirme HEM burada HEM UpdatePlanAsync'te olmalı: yukarıdaki yorumda anlatılan
            // hata (istek dolu, kayda 0) tam olarak eşleştirmenin bir yerde unutulmasıydı.
            MaxBackupsPerDay = Math.Max(0, request.MaxBackupsPerDay)
        };
        await fileStore.UpdateAsync(StatePath, Array.Empty<ServicePlanDto>(),
            plans => [.. plans, plan], cancellationToken);
        return plan;
    }

    public async Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default)
    {
        ServicePlanDto plan = null!;
        await fileStore.UpdateAsync(StatePath, Array.Empty<ServicePlanDto>(), plans =>
        {
            plan = plans.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new AppException("plans.not_found", 404, 404);

            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length > 0)
            {
                plan.Name = name;
            }

            // OwnerId BİLEREK güncellenmiyor: sahiplik plan oluşturulurken belirlenir ve düzenleme
            // isteğiyle devredilemez. Aksi hâlde bir bayi, düzenleme gövdesine sahiplik yazarak planı
            // kendi üstüne alabilir ya da sistem planına çevirebilirdi.
            plan.MaxSites = Math.Max(0, request.MaxSites);
            plan.MaxDiskMb = Math.Max(0, request.MaxDiskMb);
            plan.MaxDatabases = Math.Max(0, request.MaxDatabases);
            plan.MaxMailboxes = Math.Max(0, request.MaxMailboxes);
            plan.MaxScheduledTasks = Math.Max(0, request.MaxScheduledTasks);
            plan.MaxFtpUsers = Math.Max(0, request.MaxFtpUsers);
            plan.MaxTrafficGbPerMonth = Math.Max(0, request.MaxTrafficGbPerMonth);
            plan.ValidityDays = Math.Max(0, request.ValidityDays);
            plan.MaxBackupsPerDay = Math.Max(0, request.MaxBackupsPerDay);
            return plans;
        }, cancellationToken);
        return plan;
    }

    public async Task DeletePlanAsync(string id, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<ServicePlanDto>(),
            plans => plans.Where(plan => !string.Equals(plan.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray(),
            cancellationToken);
    }
}
