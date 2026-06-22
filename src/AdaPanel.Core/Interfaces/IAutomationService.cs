using AdaPanel.Core.Dtos.Automation;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Panel içi arka plan zamanlayıcısının iş mantığı. Otomatik yedekleme ve SSL
/// yenileme job'larının ayarlarını, durumunu ve elle tetiklenmesini yönetir.
/// Arka plan worker'ı <see cref="RunDueJobsAsync"/>'ı periyodik olarak çağırır.
/// </summary>
public interface IAutomationService
{
    Task<AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<AutomationStatusDto> UpdateSettingsAsync(UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default);

    Task<AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default);

    Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Zamanı gelmiş job'ları çalıştırır (worker tarafından çağrılır).</summary>
    Task RunDueJobsAsync(CancellationToken cancellationToken = default);
}
