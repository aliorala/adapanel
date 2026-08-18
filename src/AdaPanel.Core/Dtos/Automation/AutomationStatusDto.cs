namespace AdaPanel.Core.Dtos.Automation;

/// <summary>
/// Arka plan zamanlayıcısının çalışma durumu (son/sonraki çalışma, sonuç özeti).
/// </summary>
public sealed class AutomationStatusDto
{
    public AutomationSettingsDto Settings { get; set; } = new();

    public DateTime? LastBackupRunUtc { get; set; }

    public string LastBackupStatus { get; set; } = string.Empty;

    public string LastBackupDetails { get; set; } = string.Empty;

    public DateTime? NextBackupRunUtc { get; set; }

    public DateTime? LastSslRunUtc { get; set; }

    public string LastSslStatus { get; set; } = string.Empty;

    public string LastSslDetails { get; set; } = string.Empty;

    public DateTime? NextSslRunUtc { get; set; }

    public DateTime? LastLogCleanupRunUtc { get; set; }

    public string LastLogCleanupStatus { get; set; } = string.Empty;

    public string LastLogCleanupDetails { get; set; } = string.Empty;

    public DateTime? LastUpdateCheckRunUtc { get; set; }

    /// <summary>ok / applied / failed + kısa özet.</summary>
    public string LastUpdateCheckStatus { get; set; } = string.Empty;

    /// <summary>Güncellemesi çıkan bileşenlerin adı ve sürümleri.</summary>
    public string LastUpdateCheckDetails { get; set; } = string.Empty;

    /// <summary>Son kontrolde güncellemesi bulunan bileşen sayısı.</summary>
    public int PendingComponentUpdates { get; set; }

    /// <summary>
    /// Sunucunun saat dilimi kimliği (ör. <c>Turkey Standard Time</c>).
    /// </summary>
    /// <remarks>
    /// <b>Neden döndürülüyor:</b> yedek saati SUNUCU yerel saatidir, tarayıcının saati değil.
    /// Panele başka bir saat diliminden bağlanan yönetici "03:00" yazdığında hangi saatin
    /// kastedildiğini bilmek zorunda; ekran bunu söyleyebilsin diye dilim ve ANLIK sunucu saati
    /// yanıtta taşınır. Tahmin ettirmek, yedeği yanlış saate kurdurmak demekti.
    /// </remarks>
    public string ServerTimeZoneId { get; set; } = string.Empty;

    /// <summary>Saat diliminin okunabilir adı.</summary>
    public string ServerTimeZoneDisplayName { get; set; } = string.Empty;

    /// <summary>Sunucunun ŞU ANDAKİ UTC farkı (dakika); yaz saati uygulanmış hâliyle.</summary>
    public int ServerUtcOffsetMinutes { get; set; }

    /// <summary>Sunucunun şu anki yerel saati (ISO, ofsetsiz) — ekranda "sunucuda şu an ..." satırı için.</summary>
    public string ServerLocalTime { get; set; } = string.Empty;
}
