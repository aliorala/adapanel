namespace AdaPanel.Core.Dtos.Update;

public sealed class UpdateStatusDto
{
    public string CurrentVersion { get; set; } = string.Empty;

    public bool IsUpdateAvailable { get; set; }

    /// <summary>
    /// Sürüm bilgisi GitHub'dan ALINAMADI; aşağıdaki sürüm önbellekten geliyor (ya da hiç yok).
    /// <para>
    /// Ayrı bir alan olması şart: kontrol başarısız olduğunda <see cref="IsUpdateAvailable"/> de
    /// false çıkar ve ekran "✓ Güncelsiniz" gösterirdi. Yani panel, yeni sürümü GÖREMEDİĞİ hâlde
    /// güncel olduğunuzu söylüyordu. GitHub'ın kimliksiz saatlik sınırına takılmak bunun en sık
    /// sebebi ve tam olarak bu yaşandı.
    /// </para>
    /// </summary>
    public bool IsCheckFailed { get; set; }

    public string LatestVersion { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;

    public string PackagePath { get; set; } = string.Empty;

    public bool IsPackageReady { get; set; }

    public bool IsInstallScheduled { get; set; }

    public DateTime? InstallScheduledAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    /// <summary>GitHub deposu (owner/repo). Panel en son sürümü GitHub Releases'ten otomatik görür. Boş = ayarlanmamış.</summary>
    public string GithubRepo { get; set; } = string.Empty;

    public DateTime CheckedAtUtc { get; set; }
}
