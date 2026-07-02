using AdaPanel.Core.Dtos.System;

namespace AdaPanel.Core.Interfaces;

public interface ISystemMonitoringService
{
    Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Sunucunun ağ arabirimlerine bağlı IP adresleri (site binding'i için seçilebilir).</summary>
    Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default);

    /// <summary>Kalıcı izleme geçmişi örneklerini döner (en eskiden en yeniye). Trend grafikleri için.</summary>
    Task<IReadOnlyList<SystemHistorySampleDto>> GetHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>O anki CPU/RAM/ağ ölçümünü geçmiş halkasına ekler. Arka plan worker'ı periyodik çağırır.</summary>
    Task RecordSampleAsync(CancellationToken cancellationToken = default);
}
