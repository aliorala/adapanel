namespace AdaPanel.Core.Dtos.Waf;

/// <summary>
/// Sitenin güvenlik yapılandırması. Bayrakların hepsi GERÇEKTEN ÖLÇÜLÜR; yanlarındaki
/// <c>*Measured</c> alanı ölçümün yapılıp yapılamadığını söyler.
/// </summary>
/// <remarks>
/// <para>
/// Bu DTO uzun süre <c>config/waf.json</c> kaydını olduğu gibi yansıtıyordu; kayıt ise yalnızca
/// düğmeye basıldığı anda yazılıyordu. Sonuç, ekranın KENDİ İÇİNDE çelişmesiydi: havuz IIS'ten
/// taze okunup <see cref="ApplicationPoolName"/> alanına yazılıyor, hemen yanındaki
/// <see cref="IsIsolated"/> ise aylar önceki kayıttan geliyordu. Havuz <c>DefaultAppPool</c>'a
/// alındığında panel "izole" demeye devam ediyordu.
/// </para>
/// <para>
/// <b>Neden ayrı <c>Measured</c> bayrağı (nullable değil):</b> ölçüm yapılamadığında bayrak
/// panelin son bildiği değeri (kayıt) taşımaya devam eder ve <c>Measured=false</c> bunun
/// ÖLÇÜLMEMİŞ olduğunu söyler. Ölçülemeyeni "kapalı" göstermek, geçici bir arızayı kalıcı yanlış
/// duruma çevirir — aynı hata güvenlik duvarı tarafında bir kez yaşandı ve beş yanlış alarm
/// üretti (bkz. <c>FirewallUnknownStateTests</c>). Bu kalıbın ikinci örneği
/// <c>SubscriptionEnforcementService.TrafikOlcAsync</c>.
/// </para>
/// </remarks>
public sealed class SiteSecurityConfigurationDto
{
    public string SiteName { get; set; } = string.Empty;

    public string ApplicationPoolName { get; set; } = string.Empty;

    public string PhysicalPath { get; set; } = string.Empty;

    /// <summary>Site kendi (AdaPanel'in yönettiği) uygulama havuzunda mı — IIS'ten ölçülür.</summary>
    public bool IsIsolated { get; set; }

    /// <summary>
    /// Klasör ACL'i uygulanmış mı — dosya sisteminden ölçülür (miras kırılmış + kimliğe açık izin).
    /// </summary>
    public bool FolderAclApplied { get; set; }

    /// <summary>Temel güvenlik kuralları/başlıkları sitenin <c>web.config</c>'inde duruyor mu.</summary>
    public bool BasicRulesEnabled { get; set; }

    /// <summary>Dakika başına istek sınırı (0 = sınır yok) — <c>web.config</c>'ten ölçülür.</summary>
    public int RequestsPerMinute { get; set; }

    public string Principal { get; set; } = string.Empty;

    /// <summary><see cref="IsIsolated"/> gerçekten ölçülebildi mi (IIS'ten havuz adı okundu mu).</summary>
    public bool IsIsolatedMeasured { get; set; }

    /// <summary><see cref="FolderAclApplied"/> gerçekten ölçülebildi mi (klasör ACL'i okunabildi mi).</summary>
    public bool FolderAclMeasured { get; set; }

    /// <summary><see cref="BasicRulesEnabled"/> gerçekten ölçülebildi mi (<c>web.config</c> okunabildi mi).</summary>
    public bool BasicRulesMeasured { get; set; }

    /// <summary><see cref="RequestsPerMinute"/> gerçekten ölçülebildi mi (<c>web.config</c> okunabildi mi).</summary>
    public bool RateLimitMeasured { get; set; }
}
