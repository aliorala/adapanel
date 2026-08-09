namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Lisanslamanın YAPILANDIRMASI. Bölüm: <c>Panel:License</c>
/// (appsettings.json ya da veri kökündeki <c>config\runtime-settings.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Varsayılan hâli lisanslamayı KAPALI bırakır</b> (<see cref="VerificationUrl"/> boş). Bu bilinçli:
/// paneli kendi sunucusuna kuran bir kullanıcı, biz bir lisans sunucusu yayına almadan önce
/// hiçbir kısıtla karşılaşmamalı ve panel dışarıya HİÇBİR istek atmamalı.
/// </para>
/// </remarks>
public sealed class LicenseOptions
{
    public const string SectionName = "Panel:License";

    /// <summary>
    /// Doğrulama ucunun tam adresi (POST). BOŞ = lisanslama kapalı, ağ trafiği yok, kısıt yok.
    /// </summary>
    public string VerificationUrl { get; set; } = string.Empty;

    /// <summary>Ürün kodu; aynı lisans sunucusu birden fazla ürüne hizmet verebilsin diye.</summary>
    public string ProductCode { get; set; } = "adapanel";

    /// <summary>
    /// Sunucuya ulaşılamadığında panelin kısıtsız çalışmayı sürdüreceği gün sayısı.
    /// </summary>
    public int OfflineGraceDays { get; set; } = 14;

    /// <summary>Başarılı doğrulamanın "taze" sayıldığı süre; dolunca yeniden sorulur.</summary>
    public int VerifyIntervalHours { get; set; } = 12;

    /// <summary>
    /// Tek doğrulama isteğinin zaman aşımı (saniye). Kısa tutulur: lisans sunucusu yavaşladığında
    /// panelin kendi istekleri beklememelidir.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}
