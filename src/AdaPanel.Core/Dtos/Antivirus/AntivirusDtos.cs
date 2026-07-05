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

    /// <summary>clean | infected | failed</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>failed durumunda gerçek hata metni.</summary>
    public string Detail { get; init; } = string.Empty;
}
