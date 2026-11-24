using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Cluster;

/// <summary>
/// Merkeze kayıtlı bir hizmet düğümü (Plesk Multi Server'daki "service node").
/// </summary>
/// <remarks>
/// <para>
/// <b>Belirteç BURADA YOK ve olmayacak.</b> Uzak düğümün API anahtarı o düğümde yönetici yetkisi
/// demektir; panele geri dönerse tarayıcı geçmişinde, hata ayıklama günlüğünde ve ekran
/// görüntüsünde yaşamaya başlar. Arayüzün bilmesi gereken tek şey anahtarın VAR OLUP OLMADIĞI —
/// bunu <see cref="HasToken"/> söyler.
/// </para>
/// </remarks>
public sealed class ClusterNodeDto
{
    public Guid Id { get; init; }

    /// <summary>Yöneticinin verdiği ad ("Frankfurt-2"). Adres değişse de bu sabit kalır.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Normalize edilmiş kök adres: <c>https://sunucu.ornek.test:8888</c> (yol/sorgu YOK).</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Kapalı düğüm yoklanmaz ve yeni site yerleşiminde ADAY DEĞİLDİR.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Onaylanmış sertifikanın parmak izi (<c>SHA256:AA:BB…</c>); onay yoksa boş.</summary>
    public string CertificateFingerprint { get; init; } = string.Empty;

    /// <summary><c>pinned</c> / <c>reapproval_required</c> / <c>not_applicable</c> (http).</summary>
    public string CertificateState { get; init; } = "not_applicable";

    public DateTime? CertificateApprovedAtUtc { get; init; }

    /// <summary>Kayıtlı bir belirteç var mı. Değerin KENDİSİ hiçbir yanıtta dönmez.</summary>
    public bool HasToken { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Düğümden en son BAŞARIYLA yanıt alındığı an; hiç alınmadıysa null.</summary>
    public DateTime? LastSeenAtUtc { get; init; }

    /// <summary>Son başarısız yoklamanın makine tarafından okunabilir sebebi; başarılıysa boş.</summary>
    public string LastError { get; init; } = string.Empty;
}

/// <summary>Yeni düğüm kaydı.</summary>
public sealed class RegisterClusterNodeRequest
{
    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string Label { get; init; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Uzak panelde üretilmiş kişisel API anahtarı (<c>adap_…</c>). Merkez bu anahtarla düğümün
    /// REST API'sine bağlanır; anahtarın yetkisi sahibinin rolünden türer.
    /// </summary>
    [Required]
    [StringLength(256, MinimumLength = 8)]
    public string ApiToken { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// https düğüm için ZORUNLU: yöneticinin <c>certificate-probe</c> ile gördüğü ve onayladığı
    /// parmak izi. Sunucu kayıtta sertifikayı yeniden okur; farklıysa kayıt reddedilir.
    /// </summary>
    [StringLength(200)]
    public string CertificateFingerprint { get; init; } = string.Empty;
}

/// <summary>Var olan düğümün güncellenmesi.</summary>
public sealed class UpdateClusterNodeRequest
{
    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string Label { get; init; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// BOŞ bırakılırsa mevcut anahtar korunur. Arayüz mevcut anahtarı okuyamadığı için, her
    /// düzenlemede anahtarı yeniden yazdırmak tek gerçek alternatif olurdu.
    /// </summary>
    [StringLength(256)]
    public string ApiToken { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Adres değiştiyse yeni sertifikanın onaylı parmak izi. Boşsa ve adres aynıysa mevcut onay
    /// korunur; adres değiştiyse onay SİLİNİR (eski sunucunun parmak izi yeni adrese taşınmaz).
    /// </summary>
    [StringLength(200)]
    public string CertificateFingerprint { get; init; } = string.Empty;
}

/// <summary>Adresin sunduğu sertifikayı (belirteç GÖNDERMEDEN) okuma isteği.</summary>
public sealed class ProbeClusterCertificateRequest
{
    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string Address { get; init; } = string.Empty;
}

/// <summary>Var olan düğümün sertifikasını (yeniden) onaylama isteği.</summary>
public sealed class ApproveClusterCertificateRequest
{
    [Required]
    [StringLength(200, MinimumLength = 8)]
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>Uzak adresin sunduğu sertifika. Yönetici parmak izini başka bir kanaldan teyit eder.</summary>
public sealed class ClusterCertificateProbeDto
{
    public string Address { get; init; } = string.Empty;

    /// <summary>false: adres http, sabitlenecek sertifika yok.</summary>
    public bool Https { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public DateTime? NotBeforeUtc { get; init; }

    public DateTime? NotAfterUtc { get; init; }

    /// <summary>Sertifika sistemin güvendiği bir zincirle ve adla doğrulandı mı (bilgi amaçlı).</summary>
    public bool ChainTrusted { get; init; }
}
