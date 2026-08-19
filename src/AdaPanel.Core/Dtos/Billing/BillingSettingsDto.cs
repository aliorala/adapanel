namespace AdaPanel.Core.Dtos.Billing;

/// <summary>
/// Faturalama entegrasyonunun panel tarafındaki ayarı ve SON DURUMU.
/// </summary>
/// <remarks>
/// Ayar ekranının tek veri kaynağı budur: açık mı, en son ne zaman çağrıldı, son çağrılarda ne oldu.
/// "Çalışıyor mu" sorusunu panelden yanıtlayamamak, entegrasyon arızalarını WHMCS günlüklerine
/// bakabilen tek kişiye bağımlı kılardı.
/// </remarks>
public sealed class BillingSettingsDto
{
    /// <summary>
    /// Entegrasyon açık mı. Kapalıyken faturalama uçları <c>billing.disabled</c> ile 403 döner.
    /// </summary>
    /// <remarks>
    /// Anahtar doğrulamasının YERİNE geçmez, ÜSTÜNE biner: geçerli bir anahtar sızsa bile yönetici
    /// entegrasyonu tek düğmeyle kapatıp sağlama uçlarını kesebilmelidir. Anahtarları tek tek iptal
    /// etmek, olay anında aranan hızlı kapatma düğmesi değildir.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Faturalama ucuna en son ne zaman başarıyla ulaşıldı.</summary>
    public DateTime? LastCallAtUtc { get; set; }

    /// <summary>Rapora giren abonelik sayısı (en son üretilen raporda).</summary>
    public int LastAccountCount { get; set; }

    /// <summary>Son çağrılar — en yeni başta. Sorun gidermenin ilk bakılacak yeri.</summary>
    public IReadOnlyList<BillingCallLogEntryDto> RecentCalls { get; set; } = [];
}

/// <summary>Tek bir faturalama çağrısının izi.</summary>
/// <remarks>
/// Anahtarın KENDİSİ asla yazılmaz; hangi anahtarın çağırdığı gerekiyorsa denetim kaydına bakılır.
/// Bu liste veri kökünde düz JSON olarak durur ve yedeklere girer — oraya kimlik bilgisi koymak,
/// panelin geri kalanında özenle kaçınılan şeydir.
/// </remarks>
public sealed class BillingCallLogEntryDto
{
    public DateTime AtUtc { get; set; }

    /// <summary>Sabit anahtar: "usage" | "terminate_check" | "settings".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Sabit anahtar: "ok" | "denied" | "error".</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>İnsan tarafından okunacak kısa açıklama (sır içermez).</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Entegrasyonu açma/kapatma isteği.</summary>
public sealed class UpdateBillingSettingsRequest
{
    public bool Enabled { get; set; }
}
