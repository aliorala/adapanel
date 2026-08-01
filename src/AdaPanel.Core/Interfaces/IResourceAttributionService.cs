using AdaPanel.Core.Dtos.Monitoring;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// "Bu paylaşılan süreci KİM kullanıyor?" sorusunu yanıtlar.
///
/// <see cref="IProcessMonitorService"/> işletim sistemi düzeyinde kalır ve paylaşılan bir motorda
/// (MySQL/MariaDB/PostgreSQL tek süreçtir) daha ileri GİDEMEZ. Bu servis bir adım İÇERİ girer:
/// motorun kendi oturum listesini okuyup her aktif işlemi -mümkünse- bir SİTEYE bağlar.
/// Salt-okunur ve HIZLIDIR (uç 2-3 saniyede bir çağrılabilir); ağır sorgu çalıştırmaz.
/// </summary>
public interface IResourceAttributionService
{
    /// <param name="source">"mysql" | "mariadb" | "postgresql" | "iis".</param>
    /// <param name="limit">Döndürülecek satır sayısı (toplam sayı ayrıca raporlanır).</param>
    Task<ResourceConsumerSnapshotDto> GetConsumersAsync(
        string source,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
