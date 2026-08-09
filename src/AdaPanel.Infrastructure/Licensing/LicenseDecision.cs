using AdaPanel.Core.Dtos.Licensing;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Lisans kuralının TEK kaynağı: saklı duruma bakıp "panel kısıtlı mı" sorusuna cevap verir.
/// </summary>
/// <remarks>
/// <para>
/// Bilerek SAF (yan etkisiz) ve statik: ağ, disk, saat ve DI yok. Kural birden fazla yerde
/// (durum ekranı, kaynak oluşturma kapısı, WHMCS uçları) sorulacağı için tek yerde yaşamalı;
/// aksi hâlde ekran "geçerli" derken kapı "kısıtlı" diyebilirdi. Ayrıca bu sayede kuralın
/// tamamı ağ/saat taklidi olmadan test edilebilir.
/// </para>
/// </remarks>
public static class LicenseDecision
{
    /// <summary>Kararın sonucu.</summary>
    /// <param name="State">Lisansın panel açısından anlamı.</param>
    /// <param name="RestrictsNewResources">YENİ kaynak açma kapalı mı? (mevcutlar HER ZAMAN çalışır)</param>
    /// <param name="OfflineGraceEndsAtUtc">Çevrimdışı toleransın biteceği an; ilgisizse null.</param>
    /// <param name="MessageKey">Arayüzün çevireceği <c>license.*</c> anahtarı.</param>
    public readonly record struct Result(
        LicenseState State,
        bool RestrictsNewResources,
        DateTime? OfflineGraceEndsAtUtc,
        string MessageKey);

    /// <summary>
    /// Kararı üretir.
    /// </summary>
    /// <param name="state">Diskteki lisans durumu (anahtar var mı, son karar, son temas).</param>
    /// <param name="offlineGraceDays">Sunucuya ulaşılamadığında kısıtsız çalışılacak gün sayısı.</param>
    /// <param name="verifyIntervalHours">Başarılı doğrulamanın "taze" sayıldığı süre.</param>
    /// <param name="nowUtc">Şimdi (UTC). Dışarıdan verilir ki test saat taklidi yapmadan yazılabilsin.</param>
    public static Result Evaluate(LicenseRecord state, int offlineGraceDays, int verifyIntervalHours, DateTime nowUtc)
    {
        // 1) YAPILANDIRILMAMIŞ. Panel bugünkü gibi çalışır ve HİÇBİR YERE istek atmaz.
        //    Bu dal en başta durur: aşağıdaki hiçbir kural, lisanslama kapalıyken çalışmamalı.
        if (!state.IsConfigured)
        {
            return new Result(LicenseState.NotConfigured, false, null, "license.not_configured");
        }

        // 2) Lisanslama açık ama anahtar hiç girilmemiş.
        if (string.IsNullOrWhiteSpace(state.ProtectedKey))
        {
            return new Result(LicenseState.Unlicensed, true, null, "license.missing");
        }

        // 3) Sunucu anahtarı REDDETTİ. Tek kısıt: yeni kaynak. Siteler ayakta kalır.
        if (string.Equals(state.LastVerdict, LicenseVerdicts.Invalid, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(LicenseState.Invalid, true, null, "license.invalid");
        }

        // 4) Süre dolmuş: sunucu öyle dediyse VEYA bilinen bitiş tarihi geçtiyse.
        //    İkinci koşul, sunucuya günlerce ulaşılamasa bile süresi biten lisansın
        //    sonsuza kadar geçerli görünmesini engeller.
        var expiredByDate = state.ExpiresAtUtc.HasValue && state.ExpiresAtUtc.Value <= nowUtc;
        if (expiredByDate || string.Equals(state.LastVerdict, LicenseVerdicts.Expired, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(LicenseState.Expired, true, null, "license.expired");
        }

        // 5) Çevrimdışı tolerans. Sayaç, son BAŞARILI temastan başlar; hiç temas olmadıysa
        //    anahtarın panele girildiği andan. Kurulum sırasında ağ yoksa müşteri lisansını
        //    girip çalışmaya devam edebilmeli.
        var anchor = state.LastVerifiedAtUtc ?? state.KeyStoredAtUtc;
        if (anchor is null)
        {
            // Anahtar var ama ne doğrulandı ne de ne zaman girildiği biliniyor (elle bozulmuş
            // durum dosyası). Kısıtlamak yerine "bilinmiyor" deyip toleransı yeniden başlatmak,
            // müşteriyi bizim veri hatamız yüzünden cezalandırmamak demektir.
            return new Result(LicenseState.Grace, false, null, "license.grace");
        }

        var graceEnds = anchor.Value.AddDays(Math.Max(0, offlineGraceDays));
        if (nowUtc > graceEnds)
        {
            return new Result(LicenseState.GraceExpired, true, graceEnds, "license.offline_grace_expired");
        }

        // Tolerans içindeyiz. Son doğrulama TAZE ise "etkin", değilse "tolerans" denir;
        // ikisi de kısıtlamaz — fark yalnız kullanıcıya doğru şeyi söylemek içindir.
        var stale = state.LastVerifiedAtUtc is null
            || nowUtc > state.LastVerifiedAtUtc.Value.AddHours(Math.Max(1, verifyIntervalHours));

        return stale
            ? new Result(LicenseState.Grace, false, graceEnds, "license.grace")
            : new Result(LicenseState.Active, false, graceEnds, "license.active");
    }

    /// <summary>
    /// SKU'nun abonelik limiti aşılıyor mu? (0 = sınırsız)
    /// </summary>
    /// <remarks>
    /// Ayrı bir fonksiyon çünkü bu kısıt <see cref="Evaluate"/>'den BAĞIMSIZDIR: lisans tamamen
    /// geçerliyken de "50 abonelikli paket" limiti dolabilir.
    /// </remarks>
    public static bool ExceedsSubscriptionLimit(int maxSubscriptions, int existingCount)
    {
        return maxSubscriptions > 0 && existingCount >= maxSubscriptions;
    }
}

/// <summary>Doğrulama sunucusunun dönebileceği kararlar. Dizgi sabitleri tek yerde.</summary>
public static class LicenseVerdicts
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Expired = "expired";
}
