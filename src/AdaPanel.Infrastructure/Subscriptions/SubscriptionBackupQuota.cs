namespace AdaPanel.Infrastructure.Subscriptions;

/// <summary>
/// Abonelik yedek programının PLAN KOTASINA çekilmesi.
/// </summary>
/// <remarks>
/// <para>
/// "Günde 2 kez yedek" satılabilir bir hizmet farkıdır: her tur disk, CPU ve off-site hedefte
/// bant genişliği harcar. Sınır olmadan tek bir müşteri saatlik program kurup sunucunun bütün
/// disk alanını tüketebilir.
/// </para>
/// <para>
/// <b>Kural iki yerde birden uygulanır ve sebebi ölçülmüştür:</b> yalnız kaydetme anında
/// uygulansaydı, plan sonradan daraltıldığında eski kayıtlı aralık kotayı sonsuza kadar delerdi
/// (kimse programı yeniden kaydetmez). Bu yüzden aralık HER TURDA yeniden çözülür.
/// </para>
/// </remarks>
internal static class SubscriptionBackupQuota
{
    /// <summary>Panelin izin verdiği en sık aralık (saat). Daha sıkı bir yedek programı ürün kararı değildir.</summary>
    public const int MinimumIntervalHours = 1;

    /// <summary>Kotaya çekilmiş aralık ve kotanın devreye girip girmediği.</summary>
    /// <param name="IntervalHours">Uygulanacak gerçek aralık.</param>
    /// <param name="Throttled">Kullanıcının istediği aralık kotaya takıldı mı.</param>
    public sealed record Result(int IntervalHours, bool Throttled);

    /// <summary>
    /// İstenen aralığı plan kotasına göre çözer.
    /// </summary>
    /// <param name="requestedIntervalHours">Kullanıcının/kaydın istediği aralık (saat).</param>
    /// <param name="maxBackupsPerDay">Planın günlük yedek sayısı sınırı (0 = sınırsız).</param>
    public static Result Resolve(int requestedIntervalHours, int maxBackupsPerDay)
    {
        var istenen = Math.Clamp(requestedIntervalHours, MinimumIntervalHours, 24 * 30);

        if (maxBackupsPerDay <= 0)
        {
            return new Result(istenen, false);
        }

        // Günde en fazla N tur → turlar arası EN AZ 24/N saat olmalı. Yukarı yuvarlama şart:
        // N=7'de 3,43 saati 3'e yuvarlamak günde 8 tura izin verirdi (kota delinirdi).
        var enAzAralik = (int)Math.Ceiling(24.0 / maxBackupsPerDay);
        enAzAralik = Math.Max(MinimumIntervalHours, enAzAralik);

        return istenen >= enAzAralik
            ? new Result(istenen, false)
            : new Result(enAzAralik, true);
    }
}
