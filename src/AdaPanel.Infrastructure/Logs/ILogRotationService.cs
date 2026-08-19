using AdaPanel.Core.Dtos.Logs;

namespace AdaPanel.Infrastructure.Logs;

/// <summary>
/// Günlük döndürme politikası ve çalıştırması.
/// </summary>
/// <remarks>
/// Arayüz <c>AdaPanel.Core.Interfaces</c> yerine burada duruyor çünkü sözleşme altyapıya özgüdür
/// (IIS klasör düzeni, ZSTD arşiv biçimi) ve API katmanı zaten Infrastructure'a başvuruyor.
/// </remarks>
public interface ILogRotationService
{
    /// <summary>Politika + bütün hedeflerin son durumu.</summary>
    Task<LogRotationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Politikayı kaydeder. Kaydetmek TEK BAŞINA hiçbir dosyaya dokunmaz.</summary>
    Task<LogRotationStatusDto> UpdateSettingsAsync(UpdateLogRotationSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turu ŞİMDİ çalıştırır. <paramref name="siteName"/> verilirse yalnız o sitenin klasörü işlenir
    /// ve zamanlama saati İLERLETİLMEZ (tek site, sunucu genelindeki takvimi kaydırmamalı).
    /// </summary>
    Task<LogRotationStatusDto> RunAsync(string? siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zamanı geldiyse çalıştırır (arka plan işçisi bunu çağırır). Çalıştıysa true.
    /// </summary>
    Task<bool> RunDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Tek bir sitenin döndürme durumu (site ekranındaki kart).</summary>
    Task<LogRotationTargetDto> GetSiteStatusAsync(string siteName, CancellationToken cancellationToken = default);
}
