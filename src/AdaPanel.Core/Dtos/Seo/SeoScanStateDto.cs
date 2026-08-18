namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// Bir sitenin CANLI tarama durumu (kuyrukta mı, sürüyor mu, kaç sayfa gezildi).
/// </summary>
/// <remarks>
/// Tarama arka planda koştuğu için istek-yanıt tek başına "bitti mi" sorusunu cevaplayamaz;
/// arayüz bu durumu yoklar. Durum SÜREÇ İÇİNDE (bkz. <c>SeoScanQueue</c>) tutulur: panel yeniden
/// başlarsa süren tarama da biter, bu yüzden durum <c>idle</c>'a döner ve son RAPOR gösterilir.
/// </remarks>
public sealed class SeoScanStateDto
{
    public string SiteName { get; init; } = string.Empty;

    /// <summary><c>idle</c> | <c>queued</c> | <c>running</c>.</summary>
    public string State { get; init; } = "idle";

    public int PagesCrawled { get; init; }

    public int PageLimit { get; init; }

    /// <summary>Sıra bekleyen (bu dahil) tarama sayısı — "neden hemen başlamadı" sorusunun cevabı.</summary>
    public int QueueLength { get; init; }

    public DateTime? StartedAtUtc { get; init; }
}
