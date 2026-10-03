using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// ABONELİK BAŞINA yedek programı (Plesk'te her aboneliğin kendi "Backup Schedule"ı vardır).
/// </summary>
/// <remarks>
/// <para>
/// Panelde önceden yalnızca sunucu geneli TEK bir program vardı: bütün müşteriler aynı sıklıkta
/// ve aynı saklama süresiyle yedekleniyordu. Aboneliği ürün olarak satmak bunu imkânsız kılar —
/// sıklık ve saklama süresi paketin parçasıdır ve plan kotasına (<c>MaxBackupsPerDay</c>) bağlıdır.
/// </para>
/// <para>
/// Sunucu geneli program KALDIRILMADI: panelin kendi durumu, sahipsiz veritabanları ve posta
/// sunucusunun verisi hâlâ oradan yedeklenir. Abonelik programı onun YERİNE değil ÜSTÜNE gelir.
/// </para>
/// </remarks>
public interface ISubscriptionBackupScheduleService
{
    /// <summary>Tanımlı bütün abonelik yedek programları (yönetici/bayi ekranı için).</summary>
    Task<IReadOnlyCollection<SubscriptionBackupScheduleDto>> GetSchedulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tek bir aboneliğin programı. Kayıt yoksa PLANDAN çözülmüş varsayılanlarla (kapalı) döner —
    /// ekran "programı yok" ile "programı kapalı" arasında ayrım yapmak zorunda kalmaz.
    /// </summary>
    Task<SubscriptionBackupScheduleDto> GetScheduleAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Programı kaydeder. İstenen aralık plan kotasını aşıyorsa SESSİZCE kabul edilmez: kotaya
    /// çekilir ve sonuçtaki <c>ThrottledByQuota</c> ile bildirilir.
    /// </summary>
    Task<SubscriptionBackupScheduleDto> UpdateScheduleAsync(
        Guid userId,
        UpdateSubscriptionBackupScheduleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Programı siler (abonelik silinince ya da yönetici kaldırınca).</summary>
    Task RemoveScheduleAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Programı beklemeden ŞİMDİ çalıştırır (elle "yedekle" düğmesi). Günlük kota bu yolu
    /// ENGELLEMEZ: müşteri riskli bir işlemden önce yedek almak istediğinde onu durdurmak,
    /// korumanın kendisini durdurmak olurdu.
    /// </summary>
    Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, CancellationToken cancellationToken = default);

    async Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default)
    {
        await beforeMutation(cancellationToken);
        return await RunNowAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Zamanı gelen bütün abonelik programlarını çalıştırır (arka plan işçisi çağırır).
    /// Bir aboneliğin hatası turu düşürmez; diğerleri işlenmeye devam eder.
    /// </summary>
    Task RunDueAsync(CancellationToken cancellationToken = default);
}
