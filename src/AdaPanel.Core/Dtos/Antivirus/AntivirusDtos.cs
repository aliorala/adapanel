namespace AdaPanel.Core.Dtos.Antivirus;

/// <summary>ClamAV antivirüs durumu (Plesk antivirüs paritesi).</summary>
public sealed class AntivirusStatusDto
{
    public bool IsInstalled { get; init; }

    /// <summary>Kurulu ClamAV bileşen sürümü; kurulu değilse boş.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Virüs tanım veritabanı hazır mı (en az bir .cvd/.cld dosyası var).</summary>
    public bool DatabaseReady { get; init; }

    /// <summary>Tanım dosyalarının en yeni değişiklik zamanı; hiç indirilmediyse null.</summary>
    public DateTime? DatabaseUpdatedAtUtc { get; init; }

    /// <summary>Son taramanın özeti; hiç tarama yapılmadıysa null.</summary>
    public AntivirusScanResultDto? LastScan { get; init; }

    /// <summary>
    /// ŞU ANDA süren tarama; yoksa null. Sayfa yenilendiğinde arayüzün "tarama sürüyor" bilgisini
    /// geri okuduğu tek kaynak budur.
    /// </summary>
    public AntivirusRunningScanDto? RunningScan { get; init; }
}

/// <summary>
/// Süren taramanın SUNUCUDAN okunan durumu.
/// <para>
/// YÜZDE YOK — ve bilerek yok. <c>clamscan</c> ilerleme yüzdesi yayımlamaz; toplam dosya sayısını
/// da tarama bitmeden bildirmez. Yüzde göstermek için tek yol onu UYDURMAK olurdu (geçen süreyi
/// varsayılan bir süreye bölmek gibi) ve o sayı 10 dakikalık bir sitede de, 2 saatlik bir sitede de
/// yanlış olurdu. Bunun yerine ÖLÇÜLEBİLEN iki şey verilir: taramanın hedefi ve ne zaman başladığı.
/// Aynı kural panel güncellemesinde de uygulanıyor (bkz. <c>panel.update.phaseHintReal</c>).
/// </para>
/// </summary>
public sealed class AntivirusRunningScanDto
{
    /// <summary>Taranan hedef (site adı veya dizin yolu).</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Taramanın SUNUCUDA başladığı an; geçen süre buradan hesaplanır.</summary>
    public DateTime StartedAtUtc { get; init; }
}

public sealed class AntivirusScanRequest
{
    /// <summary>Doluysa bu IIS sitesinin kök dizini taranır (öncelikli).</summary>
    public string SiteName { get; init; } = string.Empty;

    /// <summary>SiteName boşsa taranacak mutlak dizin yolu.</summary>
    public string Path { get; init; } = string.Empty;
}

public sealed class AntivirusFindingDto
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Eşleşen imza adı (ör. Win.Test.EICAR_HDB-1).</summary>
    public string Signature { get; init; } = string.Empty;
}

public sealed class AntivirusScanResultDto
{
    /// <summary>Taranan hedef (site adı veya dizin yolu).</summary>
    public string Target { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    public double DurationSeconds { get; init; }

    public int ScannedFiles { get; init; }

    public int InfectedCount { get; init; }

    public IReadOnlyCollection<AntivirusFindingDto> Infected { get; init; } = [];

    /// <summary>
    /// clean | infected | failed | interrupted
    /// <para>
    /// <c>interrupted</c>: tarama başlamıştı ama SONUCU HİÇ YAZILAMADI — panel süreci tarama
    /// sürerken yeniden başladı. "failed" demek yanlış olurdu (clamscan bir hata vermedi),
    /// hiçbir şey dememek daha da yanlış olurdu (kullanıcı 2 saatlik taramanın öldüğünü bilmeli).
    /// </para>
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>failed durumunda gerçek hata metni.</summary>
    public string Detail { get; init; } = string.Empty;
}
