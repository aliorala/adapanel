using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Bayi tavanı tanımsız: kota denetimi geçer (G13'ün zorunlu bağımlılığı için).</summary>
internal sealed class SinirsizBayiTavani : IResellerQuotaService
{
    public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => Task.FromResult<ResellerQuotaDto?>(null);
    public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
