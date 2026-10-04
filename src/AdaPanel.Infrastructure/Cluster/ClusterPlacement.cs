using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Yeni sitenin hangi düğüme kurulacağına karar veren SAF fonksiyon.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı ve saf:</b> karar, ölçülmüş satırlardan başka hiçbir şeye bakmaz — ne diske ne
/// ağa. Böylece "on düğümün beşi ölü, üçü dolu" gibi bir durumu tek bir birim testiyle kurup
/// doğrulayabiliyoruz. Servisin içine gömülseydi bu senaryoyu ancak on sahte sunucu ayağa
/// kaldırarak deneyebilirdik.
/// </para>
/// <para>
/// <b>Plesk'ten sapma ve gerekçesi:</b> Plesk Multi Server varsayılan olarak Round Robin kullanır.
/// Round Robin, "en son hangi düğüme verdim" bilgisini KALICI tutmayı gerektirir; o sayaç
/// yeniden başlatmada sıfırlanır ve düğüm listesi değiştiğinde anlamını yitirir. Burada karar,
/// zaten ölçülmüş olan SİTE SAYISINDAN türetiliyor: durum tutmaz, panel yeniden başlasa da aynı
/// cevabı verir ve gerçekten dolu olan düğümü kendiliğinden atlar. Round Robin'in dağıtım hedefini
/// karşılar, sayaç maliyeti yoktur.
/// </para>
/// <para>
/// <b>Ulaşılamayan düğüm ADAY DEĞİLDİR.</b> Site oluşturma çağrısı zaten başarısız olurdu; aday
/// saymak, kullanıcıya "kuruldu" demeye bir adım daha yaklaşmak olurdu.
/// </para>
/// </remarks>
public static class ClusterPlacement
{
    /// <summary>
    /// Ölçülmüş satırlardan en uygun düğümü seçer.
    /// </summary>
    /// <param name="rows">Toplu görünümün satırları (ölçülmüş hâlleriyle).</param>
    public static ClusterPlacementDto Select(IReadOnlyList<ClusterNodeStatusDto>? rows)
    {
        var candidates = (rows ?? [])
            .Where(row => row.Node.Enabled && row.Reachable && row.Info is not null)
            .Where(row => row.Info!.Overview.DisksMeasured && row.Info.Overview.Disks.Count > 0 &&
                row.Info.Overview.Disks.All(SystemMetricValidity.Disk))
            .ToList();

        if (candidates.Count == 0)
        {
            return new ClusterPlacementDto
            {
                NodeId = null,
                ReasonKey = ClusterMessages.NoNodeAvailable,
                CandidateCount = 0
            };
        }

        // Sıralama üç anahtarlı ve SONUNCUSU belirleyici: iki düğüm her ölçümde eşitse karar yine de
        // kararlı olmalı. Aksi hâlde aynı girdi iki farklı cevap verir ve test edilemez hâle gelir.
        var winner = candidates
            .OrderBy(row => row.Info!.SiteCount)
            .ThenBy(row => MaxDiskUsagePercent(row.Info!))
            .ThenBy(row => row.Node.Label, StringComparer.OrdinalIgnoreCase)
            .First();

        return new ClusterPlacementDto
        {
            NodeId = winner.Node.Id,
            NodeLabel = winner.Node.Label,
            ReasonKey = ClusterMessages.PlacementLeastLoaded,
            CandidateCount = candidates.Count
        };
    }

    /// <summary>
    /// Düğümün EN DOLU diskinin doluluk yüzdesi.
    /// </summary>
    /// <remarks>
    /// Ortalama değil en kötü disk alınır: sunucuyu durduran şey disklerin ortalaması değil, dolan
    /// ilk diskidir. Hiç disk raporlanmadıysa yardımcı 0 döner; Select böyle bir düğümü
    /// ölçülmüş kapasite olmadığından adaylara almaz.
    /// </remarks>
    public static double MaxDiskUsagePercent(ClusterNodeInfoDto info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var disks = info.Overview.Disks;
        return disks.Count == 0 ? 0 : disks.Max(disk => disk.UsagePercent);
    }
}
