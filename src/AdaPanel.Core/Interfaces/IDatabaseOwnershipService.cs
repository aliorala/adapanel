namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Plesk benzeri kaynak↔abonelik sahipliği: hangi veritabanının hangi müşteriye (ve siteye) ait
/// olduğunu kaydeder. MaxDatabases kotası ve müşteri-kapsamlı DB görünürlüğü bunun üstüne kurulur.
/// JSON dosyasında tutulur (config/db-ownership.json) — DB migration gerektirmez.
/// </summary>
public interface IDatabaseOwnershipService
{
    /// <summary>Bir veritabanını bir müşteriye (ve opsiyonel siteye) ait olarak kaydeder. Idempotent.</summary>
    Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default);

    /// <summary>Veritabanı kaydını siler (DB silinince/cascade).</summary>
    Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>Bir siteye bağlı tüm DB kayıtlarını siler (site silinince cascade).</summary>
    Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Müşterinin sahip olduğu veritabanı sayısı (kota dayatması için).</summary>
    Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>Veritabanının sahibi (yoksa null) — kapsam/görünürlük kontrolü için.</summary>
    Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>Müşterinin sahip olduğu veritabanları (engine + ad) — bayi/müşteri self-servis listesi.</summary>
    Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
