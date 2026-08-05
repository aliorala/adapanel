using AdaPanel.Core.Dtos.Scheduler;

namespace AdaPanel.Core.Interfaces;

public interface ISchedulerService
{
    Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default);

    Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Görevi beklemeden hemen tetikler ("Şimdi çalıştır").</summary>
    Task RunTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tek bir görevin anlık durumu — "Şimdi çalıştır"dan sonra sayfanın canlı bilgi verebilmesi için.
    /// </summary>
    Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Görevin SON çalışmasında ürettiği çıktı. Windows Görev Zamanlayıcı çıktıyı saklamaz; panel
    /// görevi bir kabuğa sarıp kendi dosyasına yönlendirir ve buradan okur.
    /// </summary>
    Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Görevlerin çalışacağı ortamı ölçer: sunucuda hangi Python'lar kurulu ve SYSTEM bunları
    /// PATH'inden görebiliyor mu.
    /// </summary>
    Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulunan Python'u makine PATH'ine ekler; kaç klasörün eklendiğini döndürür (0 = zaten vardı).
    /// Yalnızca yönetici çağırmalıdır: sunucu geneli bir değişikliktir.
    /// </summary>
    Task<int> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default);

    /// <summary>Görevi silmeden geçici olarak açar/kapatır.</summary>
    Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sürmekte olan çalışmayı SÜREÇ AĞACIYLA birlikte sonlandırır. Görev silinmez, devre dışı
    /// bırakılmaz; bir sonraki zamanında normal çalışmaya döner.
    /// <para>
    /// Sonuç ölçülerek döner: kaç süreç ağacı sonlandırıldı ve görev hâlâ çalışıyor mu. Arayüz
    /// "durduruldu" sözünü ancak ölçüme dayanarak verebilsin diye.
    /// </para>
    /// </summary>
    Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Bir site silinirken, o sitenin klasörü içinden çalışan zamanlanmış görevleri kaldırır;
    /// kaldırılan görev sayısını döner.
    /// <para>
    /// Site gidince bu görevler Windows Görev Zamanlayıcı'da KALIR ve tetiklenmeye devam eder.
    /// Çalıştıracakları dosya artık yoktur: her turda hata üretir, günlüğü kirletir ve panelin
    /// cron ekranında "başarısız" olarak görünürler — üstelik kullanıcı hangi siteye ait
    /// olduklarını anlayamaz, çünkü site listede yoktur.
    /// </para>
    /// <para>
    /// Eşleştirme YOL ÜZERİNDEN yapılır: görevin komutu ya da çalışma klasörü site kökünün
    /// içindeyse o göreve aittir. Yalnızca ad benzerliğine bakmak yanlış görev silerdi.
    /// </para>
    /// </summary>
    Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default);
}
