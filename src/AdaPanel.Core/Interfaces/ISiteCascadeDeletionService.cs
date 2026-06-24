using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// profesyonel panel benzeri site silme: bir site silindiğinde ona bağlı tüm kaynakları
/// (dosyalar, veritabanları, mail domaini, DNS, FTP, SSL/proxy, kota, git, müşteri
/// atamaları) tek işlemde temizler. Tüm adımlar best-effort'tur; biri başarısız olsa
/// da diğerleri devam eder ve sonuç uyarı olarak raporlanır.
/// </summary>
public interface ISiteCascadeDeletionService
{
    /// <summary>Silmeden önce siteye bağlı kaynakların önizlemesini üretir.</summary>
    Task<SiteDeletionPlanDto> GetPlanAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Siteyi ve bağlı kaynaklarını cascade siler.</summary>
    /// <param name="deleteFiles">Site dosyaları (fiziksel kök) da silinsin mi.</param>
    /// <param name="deleteDatabases">Adı eşleşen veritabanları da silinsin mi.</param>
    Task<SiteDeletionResultDto> DeleteAsync(
        string siteName,
        bool deleteFiles,
        bool deleteDatabases,
        CancellationToken cancellationToken = default);
}
