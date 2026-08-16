using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Dosya taşıyan (dolayısıyla ARTIMSAL alınabilen) yedek isteklerinin ortak alanları:
/// site yedeği ve abonelik yedeği.
/// </summary>
/// <remarks>
/// <para>
/// Veritabanı, posta ve panel yedekleri bilerek DIŞARIDA: bir SQL dökümü ya da RocksDB veri
/// dizini "değişen dosyalar" olarak parçalanamaz; artımsal alınmış gibi davranmak, geri
/// yüklenmeyen bir yedek üretirdi. Artımsal arşivlerde bile veritabanı dökümleri HER HALKADA
/// TAM taşınır ve bu manifestte açıkça yazar.
/// </para>
/// <para>
/// <b>ŞİFRELEME + ARTIMSAL:</b> bir zincirin bütün halkaları aynı politikayla şifrelenir. Parola
/// kaybolursa halkaların TAMAMI (tam yedek dahil) geri getirilemez — zincir, tek bir halkası
/// çözülemediğinde bütün olarak kaybedilir.
/// </para>
/// </remarks>
public abstract class BackupArchiveRequest : BackupTargetRequest
{
    /// <summary>
    /// Artımsal yedek istenir mi. İlk yedek, tam yedek periyodu dolduğunda ve temel indeks
    /// okunamadığında yine TAM alınır — bu bayrak "mümkünse artımsal" demektir.
    /// </summary>
    public bool Incremental { get; set; }

    /// <summary>
    /// Artımsal zincirin kaç günde bir TAM yedekle yenileneceği (Plesk'in "tam yedek periyodu").
    /// </summary>
    [Range(1, 365)]
    public int FullBackupEveryDays { get; set; } = 7;
}
