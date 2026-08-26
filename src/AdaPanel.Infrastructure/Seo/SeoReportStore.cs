using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Bir sitenin saklanan tarama geçmişi (dosya biçimi).</summary>
public sealed class SeoSiteHistory
{
    public List<SeoScanReportDto> Reports { get; set; } = [];
}

/// <summary>
/// Tarama raporlarını site başına saklar: <c>config/seo/&lt;site&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden dosya, neden veritabanı değil:</b> panelin ayar/durum kayıtları zaten
/// <see cref="JsonFileStore"/> üzerinden dosyada tutuluyor (git dağıtımı, SSL durumu, otomasyon).
/// Yeni bir tablo eklemek göç (migration) gerektirirdi ve bu veri ilişkisel bir veri değil.
/// </para>
/// <para>
/// <b>Neden yalnız son beş rapor:</b> "geçmişle karşılaştırma" için birkaç tur yeter; sınırsız
/// biriktirmek, her sayfanın ölçümünü taşıyan dosyaları büyütür ve okumayı yavaşlatır. Sınır
/// AuditRetentionWorker'daki dersle aynı: silen kimse yoksa dosya sonsuza kadar büyür.
/// </para>
/// </remarks>
public sealed class SeoReportStore(JsonFileStore fileStore)
{
    public const int MaxReports = 5;

    public Task<SeoSiteHistory> ReadAsync(string siteName, CancellationToken cancellationToken)
        => fileStore.ReadAsync(Path(siteName), new SeoSiteHistory(), cancellationToken);

    /// <summary>Yeni raporu başa ekler ve en eskileri düşürür.</summary>
    public async Task<SeoSiteHistory> AppendAsync(string siteName, SeoScanReportDto report, CancellationToken cancellationToken)
    {
        return await fileStore.UpdateAsync(
            Path(siteName),
            new SeoSiteHistory(),
            history =>
            {
                history.Reports.Insert(0, report);
                if (history.Reports.Count > MaxReports)
                {
                    history.Reports.RemoveRange(MaxReports, history.Reports.Count - MaxReports);
                }

                return history;
            },
            cancellationToken);
    }

    /// <summary>
    /// Site adını dosya adına çevirir (kalıp: <c>GitDeploymentService.SanitizeFileName</c>).
    /// <see cref="JsonFileStore.ResolvePath"/> ayrıca kök dışına çıkmayı engeller.
    /// </summary>
    /// <remarks>
    /// AYRILMIŞ AYGIT ADI kontrolü UZANTI EKLENDİKTEN SONRA yapılır: ayrılmışlık ilk noktadan
    /// önceki parçaya bakar, yani "CON" için hedef dosya "CON.json" olur ve o da aygıttır.
    /// Ölçülen sonuç, "CON" adlı bir sitede SEO taramasının raporu yazarken
    /// <c>FileNotFoundException</c> ile düşmesiydi — tarama tamamlanır, sonuç kaybolurdu.
    ///
    /// internal: ürettiği ADIN diskte gerçekten açılabildiği test edilebilsin diye. Tuzak ancak
    /// "dosya oluşturulabiliyor mu" diye ÖLÇÜLDÜĞÜNDE yakalanır; kuralı testte yeniden yazmak,
    /// testin kendi kopyasını doğrulaması olurdu.
    /// </remarks>
    internal static string Path(string siteName) => Path(siteName, "json");

    /// <summary>
    /// Aynı kuralın ikinci kopyasını çıkarmamak için SEO modülünün BÜTÜN dosya adları buradan
    /// üretilir (tarama geçmişi <c>.json</c>, günlük analizi <c>.logs.json</c>). Ayrılmış aygıt
    /// adı tuzağı yalnız burada bir kez karşılanır.
    /// </summary>
    internal static string Path(string siteName, string extension)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(siteName.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return $"config/seo/{WindowsReservedNames.MakeSafe($"{safe}.{extension}")}";
    }
}
