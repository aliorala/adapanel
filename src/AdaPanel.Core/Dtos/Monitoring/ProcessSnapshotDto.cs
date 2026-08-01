namespace AdaPanel.Core.Dtos.Monitoring;

/// <summary>
/// Canlı süreç izleyicisinin tek yanıtı: hem toplam CPU/RAM özeti hem de süreç kırılımı.
/// Özet aynı yanıtta taşınır ki UI saniyede iki ayrı istek atmak zorunda kalmasın.
/// </summary>
public sealed class ProcessSnapshotDto
{
    public DateTime CapturedAtUtc { get; init; }

    /// <summary>Ölçüm penceresi (önceki anlık görüntüden bu yana geçen süre, saniye).</summary>
    public double SampleWindowSeconds { get; init; }

    /// <summary>
    /// false ise bu, servisin gördüğü İLK ölçümdür ve CPU yüzdeleri güvenilir değildir
    /// (CPU% ancak iki ölçüm farkından çıkar). UI ilk turda uyarı/iskelet gösterebilir.
    /// </summary>
    public bool CpuBaselineReady { get; init; }

    /// <summary>Tüm süreçlerin CPU zamanı farklarının toplamından hesaplanan sunucu geneli CPU kullanımı (%).</summary>
    public double TotalCpuPercent { get; init; }

    public int LogicalProcessorCount { get; init; }

    public long TotalMemoryBytes { get; init; }

    public long UsedMemoryBytes { get; init; }

    public long AvailableMemoryBytes { get; init; }

    public double MemoryUsagePercent { get; init; }

    /// <summary>Sunucudaki toplam süreç sayısı (listede yalnız ilk N satır döner).</summary>
    public int TotalProcessCount { get; init; }

    /// <summary>Uygulanan sıralama: "cpu" veya "memory".</summary>
    public string SortBy { get; init; } = "cpu";

    public IReadOnlyCollection<ProcessUsageDto> Processes { get; init; } = [];
}
