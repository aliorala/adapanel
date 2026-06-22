using AdaPanel.Core.Dtos.System;

namespace AdaPanel.Core.Interfaces;

public interface ISystemMonitoringService
{
    Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Sunucunun ağ arabirimlerine bağlı IP adresleri (site binding'i için seçilebilir).</summary>
    Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default);
}
