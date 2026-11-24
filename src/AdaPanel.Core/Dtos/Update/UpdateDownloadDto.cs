namespace AdaPanel.Core.Dtos.Update;

/// <summary>
/// Paket indirmenin SUNUCU TARAFINDA tutulan durumu. İndirme artık HTTP isteğinin içinde değil
/// arka plan işinde yürür; bu yüzden sayfa yenilense ya da başka sekmeden açılsa da ekran gerçeği
/// buradan okur.
/// </summary>
public sealed class UpdateDownloadDto
{
    public const string Idle = "idle";
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Ready = "ready";
    public const string Failed = "failed";

    /// <summary>idle | downloading | verifying | ready | failed</summary>
    public string State { get; set; } = Idle;

    /// <summary>İndirme yüzdesi (0-100); uzak sunucu boyut vermediyse null.</summary>
    public int? Percent { get; set; }

    /// <summary>State = failed iken çeviri anahtarı; aksi hâlde boş.</summary>
    public string ErrorKey { get; set; } = string.Empty;

    public string TargetVersion { get; set; } = string.Empty;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public bool IsRunning => State is Downloading or Verifying;
}

/// <summary>İndirme işinin bildirdiği ara durum. PartialPath yalnız yarım dosya yolu bilindiğinde dolu.</summary>
public sealed record UpdateDownloadProgress(string Phase, int? Percent, string? PartialPath = null);
