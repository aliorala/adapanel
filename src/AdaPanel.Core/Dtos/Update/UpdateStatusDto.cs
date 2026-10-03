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

    /// <summary>
    /// SON kurulum denemesinin durumu (çalıştırıcının yazdığı dosyadan). Deneme yoksa null.
    /// <para>
    /// Gerekli: arayüz kurulumdan sonra yalnızca "sürüm değişti mi" diye bakıyordu. Kurulum iptal
    /// olup saniyeler içinde bittiğinde bile ekran "panel yeniden başlatılıyor" diyerek dakikalarca
    /// bekliyor, sonunda da zaman aşımı gösteriyordu — kullanıcı ne sonucu ne sebebini görüyordu.
    /// Bu alanla arayüz denemenin BİTTİĞİNİ anlar ve gerçek hatayı hemen gösterir.
    /// </para>
    /// </summary>
    public UpdateAttemptDto? LastAttempt { get; set; }

    public string LatestVersion { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;

    public string PackagePath { get; set; } = string.Empty;

    /// <summary>Version of the locally staged package, independent of the latest release.</summary>
    public string PreparedVersion { get; set; } = string.Empty;

    /// <summary>Checksum bound to PreparedVersion and used when confirming installation.</summary>
    public string PreparedPackageSha256 { get; set; } = string.Empty;

    public bool IsPackageReady { get; set; }

    public bool IsInstallScheduled { get; set; }

    public DateTime? InstallScheduledAtUtc { get; set; }

    public string Notes { get; set; } = string.Empty;

    /// <summary>GitHub deposu (owner/repo). Panel en son sürümü GitHub Releases'ten otomatik görür. Boş = ayarlanmamış.</summary>
    public string GithubRepo { get; set; } = string.Empty;

    public DateTime CheckedAtUtc { get; set; }
}
