namespace AdaPanel.Core.Dtos.Components;

public sealed class InstalledComponentDto
{
    public Guid Id { get; init; }

    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string LastError { get; init; } = string.Empty;

    public string GeneratedPassword { get; init; } = string.Empty;

    public bool IsSystemDetected { get; init; }

    public string DetectionSource { get; init; } = string.Empty;

    /// <summary>Upstream'de bulunan son sürüm (güncelleme denetimi yapıldıysa).</summary>
    public string AvailableVersion { get; init; } = string.Empty;

    /// <summary>true ise kurulu sürümden daha yeni bir sürüm mevcut.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>
    /// Bu bileşenin üst sürümü canlı olarak sorgulanabiliyor mu. False ise sürüm manifeste sabittir:
    /// "güncelleme yok" bilgisi GÜVENİLİR DEĞİLDİR, yalnızca kontrol edilemediği anlamına gelir.
    /// </summary>
    public bool VersionCheckSupported { get; init; }

    public DateTime InstalledAtUtc { get; init; }
}
