using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Günlük analizinin SON raporunu site başına saklar: <c>config/seo/&lt;site&gt;.logs.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN SAKLANIYOR:</b> analiz dakikalar sürebilir ve sonucu yalnız HTTP yanıtında dönseydi,
/// kullanıcı sayfayı yenilediğinde ekran boşalır ve analizi yeniden başlatırdı. Bu, panelde daha
/// önce yaşanan hatanın (virüs taraması yenilemede kayboluyordu, bkz. CODEX_MEMORY 218) aynısı
/// olurdu. Sonuç diske yazılır, ekran onu okur.
/// </para>
/// <para>
/// <b>NEDEN YALNIZ SON RAPOR:</b> tarama geçmişinin aksine burada karşılaştırılacak bir "önceki
/// tur" kavramı yok — pencere zaten kayan bir zaman aralığıdır ve iki komşu analiz büyük ölçüde
/// AYNI günleri kapsar. Beş kopya saklamak, aynı veriyi beş kez diske yazmak olurdu.
/// </para>
/// <para>
/// Dosya adı kuralı <see cref="SeoReportStore.Path(string, string)"/>'ten gelir; ayrılmış aygıt
/// adı tuzağı (<c>CON.json</c>) ikinci kez çözülmez.
/// </para>
/// </remarks>
public sealed class SeoLogReportStore(JsonFileStore fileStore)
{
    /// <summary>Dosya biçimi. Sarmalayıcı sınıf, ileride alan eklenebilsin diye.</summary>
    public sealed class SeoLogHistory
    {
        public SeoLogReportDto? Latest { get; set; }
    }

    private static string Path(string siteName) => SeoReportStore.Path(siteName, "logs.json");

    public async Task<SeoLogReportDto?> ReadAsync(string siteName, CancellationToken cancellationToken)
    {
        var kayit = await fileStore.ReadAsync(Path(siteName), new SeoLogHistory(), cancellationToken);
        return kayit.Latest;
    }

    public Task SaveAsync(string siteName, SeoLogReportDto report, CancellationToken cancellationToken) =>
        fileStore.UpdateAsync(
            Path(siteName),
            new SeoLogHistory(),
            history =>
            {
                history.Latest = report;
                return history;
            },
            cancellationToken);
}
