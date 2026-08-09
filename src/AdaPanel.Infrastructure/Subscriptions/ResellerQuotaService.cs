using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <inheritdoc />
/// <remarks>
/// Abonelik modelinin geri kalanıyla AYNI depoda tutulur (veri kökündeki JSON dosyaları):
/// servis planları <c>config/service-plans.json</c>, abonelikler <c>config/customer-scopes.json</c>.
/// Yalnız bayi tavanı için EF göçü açmak, aynı iş modelini iki ayrı depoya bölmek olurdu; yedekleme,
/// taşıma ve elle kurtarma yolları da ikiye ayrılırdı.
/// </remarks>
public sealed class ResellerQuotaService(JsonFileStore fileStore, AdaPanelDbContext dbContext) : IResellerQuotaService
{
    private const string StatePath = "config/reseller-quotas.json";

    /// <inheritdoc />
    public async Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        if (!state.TryGetValue(resellerId.ToString("N"), out var entry))
        {
            return null;
        }

        return new ResellerQuotaDto
        {
            ResellerId = resellerId,
            MaxSites = entry.MaxSites,
            MaxDiskMb = entry.MaxDiskMb
        };
    }

    /// <inheritdoc />
    public async Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default)
    {
        // ROL DOĞRULANIR. Tavan, kapsam okumasında (CustomerScopeService.GetScopeAsync) plandan
        // gelen limitleri EZER; müşteriye yazılabilseydi, plan bağı duruyorken limitler sessizce
        // başka bir yerden gelmeye başlardı ve "planı düzenlemek müşteriyi etkiler" kuralı
        // görünmez biçimde bozulurdu.
        var rol = await dbContext.Users
            .Where(user => user.Id == resellerId)
            .Select(user => (UserRole?)user.Role)
            .FirstOrDefaultAsync(cancellationToken);

        if (rol is null)
        {
            throw new AppException("users.not_found", 404, 404);
        }

        if (rol != UserRole.Reseller)
        {
            throw new AppException("users.forbidden", 403, 403);
        }

        var maxSites = Math.Max(0, request.MaxSites);
        var maxDiskMb = Math.Max(0, request.MaxDiskMb);

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = resellerId.ToString("N");

            // İkisi de 0 (sınırsız) ise kayıt TUTULMAZ: "tavan yok" durumunun tek bir temsili
            // olsun, yoksa kimi bayide null kimi bayide sıfırlı kayıt olur ve okuyan taraf iki
            // ayrı yolu doğru saymak zorunda kalırdı.
            if (maxSites == 0 && maxDiskMb == 0)
            {
                state.Remove(key);
                return state;
            }

            state[key] = new QuotaEntry { MaxSites = maxSites, MaxDiskMb = maxDiskMb };
            return state;
        }, cancellationToken);

        return new ResellerQuotaDto { ResellerId = resellerId, MaxSites = maxSites, MaxDiskMb = maxDiskMb };
    }

    /// <inheritdoc />
    public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
        => fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            state.Remove(resellerId.ToString("N"));
            return state;
        }, cancellationToken);

    private async Task<Dictionary<string, QuotaEntry>> LoadAsync(CancellationToken cancellationToken)
        => Wrap(await fileStore.ReadAsync<Dictionary<string, QuotaEntry>>(StatePath, [], cancellationToken));

    private static Dictionary<string, QuotaEntry> NewState() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, QuotaEntry> Wrap(Dictionary<string, QuotaEntry> state) =>
        new(state, StringComparer.OrdinalIgnoreCase);

    private sealed class QuotaEntry
    {
        public int MaxSites { get; set; }

        public int MaxDiskMb { get; set; }
    }
}
