using AdaPanel.Core.Dtos.Monitoring;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Veritabanı sorgu geçmişi: canlı listeden düşen sorguları günlük dosyalara yazar ve geriye dönük
/// filtrelenebilir hâle getirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> "şu an kim kullanıyor" ekranı yalnız O ANI gösteriyordu. Sorgular saniyeler
/// içinde gelip gittiği için okumak neredeyse imkânsızdı ve olan biteni sonradan incelemenin hiçbir
/// yolu yoktu — "dün gece sunucuyu ne yordu?" sorusu cevapsızdı.
/// </para>
/// <para>
/// <b>Ne DEĞİL:</b> tam bir sorgu denetim günlüğü değil. Motorun oturum listesi belirli aralıklarla
/// örneklenir; iki örnek arasında başlayıp biten çok kısa sorgular kaydedilmez. Bu bilinçli bir
/// tercih: alternatifi motorun genel sorgu günlüğünü açmaktır ve o, üretim sunucusunda her sorguyu
/// diske yazarak ciddi yavaşlama + disk dolması demektir. Panel ölçtüğünü doğru raporlar, ne
/// ölçmediğini de ekranda söyler.
/// </para>
/// </remarks>
public interface IQueryLogService
{
    /// <summary>
    /// Bir örnekleme turunu işler: verilen anlık liste ile önceki tur karşılaştırılır, listeden
    /// DÜŞMÜŞ sorgular "bitti" sayılıp günlüğe yazılır.
    /// </summary>
    /// <returns>Bu turda günlüğe yazılan kayıt sayısı.</returns>
    Task<int> RecordSampleAsync(ResourceConsumerSnapshotDto snapshot, CancellationToken cancellationToken = default);

    /// <summary>Filtrelenmiş geçmiş + özet.</summary>
    Task<QueryLogPageDto> QueryAsync(QueryLogFilterDto filter, CancellationToken cancellationToken = default);

    /// <summary>Saklama süresini aşan günlük dosyalarını siler.</summary>
    /// <returns>Silinen dosya sayısı.</returns>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
