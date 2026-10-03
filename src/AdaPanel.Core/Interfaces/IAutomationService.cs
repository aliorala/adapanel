using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;

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

    async Task<AutomationStatusDto> RunBackupNowAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await RunBackupNowAsync(cancellationToken);
    }

    Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default);

    async Task<AutomationStatusDto> RunSslRenewNowAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await RunSslRenewNowAsync(cancellationToken);
    }

    /// <summary>Zamanı gelmiş job'ları çalıştırır (worker tarafından çağrılır).</summary>
    Task RunDueJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Off-site yedek hedefini test eder (gerçek yedek almadan küçük dosya yükleme). İstekte parola boşsa
    /// sunucudaki kayıtlı (korumalı) parola kullanılır → kullanıcı kaydedilmiş hedefi tekrar parola girmeden test edebilir.
    /// </summary>
    Task<BackupRemoteTestResultDto> TestRemoteBackupAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default);
}
