namespace AdaPanel.Core.Dtos.Cluster;

/// <summary>
/// Toplu görünümün BİR SATIRI: kayıtlı düğüm + o düğümden ölçülebilen kadarı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölü düğüm satırı SİLMEZ.</b> Ulaşılamayan düğüm listeden düşseydi, panel "her şey yolunda"
/// demiş olurdu — oysa bir sunucunun cevap vermemesi tam da görülmesi gereken şeydir. Satır kalır,
/// <see cref="Reachable"/> false olur ve <see cref="StatusKey"/> sebebi söyler.
/// </para>
/// </remarks>
public sealed class ClusterNodeStatusDto
{
    public ClusterNodeDto Node { get; init; } = new();

    /// <summary>Düğüm bu turda yanıt verdi mi.</summary>
    public bool Reachable { get; init; }

    /// <summary>
    /// Durumun makine tarafından okunabilir sebebi ("cluster.node_unreachable",
    /// "cluster.node_disabled", "cluster.token_required"…). Ulaşıldıysa boş.
    /// </summary>
    public string StatusKey { get; init; } = string.Empty;

    /// <summary>Teşhis ayrıntısı (zaman aşımı, TLS hatası, HTTP kodu). Sır İÇERMEZ.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Yoklamanın sürdüğü milisaniye — yavaşlayan düğüm ölmeden önce burada görünür.</summary>
    public long DurationMs { get; init; }

    /// <summary>Ulaşılabildiyse düğümün kendi raporu; ulaşılamadıysa null.</summary>
    public ClusterNodeInfoDto? Info { get; init; }
}

/// <summary>Merkez panosunun tamamı.</summary>
public sealed class ClusterOverviewDto
{
    public IReadOnlyList<ClusterNodeStatusDto> Nodes { get; init; } = [];

    /// <summary>
    /// ULAŞILABİLEN düğümlerdeki toplam site sayısı.
    /// </summary>
    /// <remarks>
    /// Ulaşılamayan düğümün siteleri sayılmaz ve bu KASITLIDIR: bilinmeyeni sıfır saymak, toplamı
    /// "ölçüldü" gibi gösterirdi. <see cref="UnreachableCount"/> sıfır değilken bu sayı EKSİKTİR ve
    /// arayüz bunu açıkça söyler.
    /// </remarks>
    public int TotalSites { get; init; }

    public int ReachableCount { get; init; }

    public int UnreachableCount { get; init; }

    public DateTime GeneratedAtUtc { get; init; }
}

/// <summary>Yeni sitenin hangi düğüme gideceği önerisi.</summary>
public sealed class ClusterPlacementDto
{
    /// <summary>Önerilen düğüm; uygun düğüm yoksa null.</summary>
    public Guid? NodeId { get; init; }

    public string NodeLabel { get; init; } = string.Empty;

    /// <summary>Kararın sebebi ("en az siteli düğüm") — panel neden bu düğümü seçtiğini söyler.</summary>
    public string ReasonKey { get; init; } = string.Empty;

    /// <summary>Aday olarak DEĞERLENDİRİLEN düğüm sayısı (kapalı/ulaşılamayan hariç).</summary>
    public int CandidateCount { get; init; }
}
