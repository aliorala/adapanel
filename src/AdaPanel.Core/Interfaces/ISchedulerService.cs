using AdaPanel.Core.Dtos.Scheduler;

namespace AdaPanel.Core.Interfaces;

public interface ISchedulerService
{
    Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default);

    Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Görevi beklemeden hemen tetikler ("Şimdi çalıştır").</summary>
    Task RunTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Görevi silmeden geçici olarak açar/kapatır.</summary>
    Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sürmekte olan çalışmayı sonlandırır. Görev silinmez, devre dışı bırakılmaz; bir sonraki
    /// zamanında normal çalışmaya döner.
    /// </summary>
    Task StopTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Daha önce kaydedilmiş görevlerin ayarlarını sunucuya uygun hâle getirir; kaç görevin
    /// düzeltildiğini döndürür.
    /// <para>
    /// Yalnızca yeni görevleri doğru kaydetmek YETMEZ: panelde zaten kayıtlı olan görevler
    /// Windows'un dizüstü bilgisayar varsayılanlarıyla oluşturulmuştu ve kendiliğinden
    /// düzelmezler. Çalıştırmayı reddedilmiş bir görev, kullanıcı onu silip yeniden kurana
    /// kadar reddedilmeye devam ederdi.
    /// </para>
    /// </summary>
    Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default);
}
