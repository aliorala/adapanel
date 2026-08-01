namespace AdaPanel.Core.Dtos.Monitoring;

/// <summary>
/// Tek bir işletim sistemi sürecinin anlık kaynak kullanımı.
/// Hosting yöneticisi için ham süreç adı ("w3wp") tek başına anlamsızdır; bu yüzden her satır
/// <see cref="Category"/> + <see cref="Label"/> ile "hangi siteye / hangi panel bileşenine ait"
/// sorusunu da yanıtlar (etiket metni UI tarafında i18n ile kurulur, burada ham veri taşınır).
/// </summary>
public sealed class ProcessUsageDto
{
    public int ProcessId { get; init; }

    /// <summary>Süreç adı (uzantısız), ör. "w3wp", "php-cgi", "mysqld".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Sabit kategori anahtarı: site, php, database, cache, mail, panel, web, ftp, dns, docker,
    /// service, windows, other. UI bunu i18n anahtarına çevirir; ASLA çevrilmiş metin dönmez.
    /// </summary>
    public string Category { get; init; } = "other";

    /// <summary>Kategoriye göre okunabilir ad: site adı, "MariaDB", "Stalwart", app pool adı vb.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Ek bağlam: app pool adı, Windows servis adı vb. (boş olabilir).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>İki ölçüm arasındaki CPU zamanı farkından hesaplanan yüzde (tüm çekirdekler üzerinden normalize).</summary>
    public double CpuPercent { get; init; }

    public long WorkingSetBytes { get; init; }

    public long PrivateMemoryBytes { get; init; }

    /// <summary>Çalışma kümesinin toplam fiziksel belleğe oranı (%).</summary>
    public double MemoryPercent { get; init; }

    public int ThreadCount { get; init; }

    /// <summary>Sürecin başlama zamanı; okunamayan (korumalı) süreçlerde null.</summary>
    public DateTime? StartTimeUtc { get; init; }

    /// <summary>Sürecin çalışma süresi (saniye); başlama zamanı okunamadıysa 0.</summary>
    public double UptimeSeconds { get; init; }
}
