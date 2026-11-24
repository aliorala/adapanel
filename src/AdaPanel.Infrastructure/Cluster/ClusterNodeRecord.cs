using AdaPanel.Core.Dtos.Cluster;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Düğümün DİSKTEKİ hâli (<c>config/cluster/nodes.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// DTO'dan ayrı bir tip olması kazara sızıntıya karşı yapısal bir engeldir: belirteç alanı yalnız
/// burada var, dışarı çıkan tipte hiç yok. Tek tip kullanılsaydı bir gün birinin
/// <c>Ok(record)</c> yazması yeterdi.
/// </para>
/// </remarks>
public sealed class ClusterNodeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// ISecretProtector (DPAPI) ile korunmuş API anahtarı. Düz metin ASLA yazılmaz — panel
    /// yapılandırma dosyaları yedeklere girer ve yedek panelden çok daha fazla elden ele dolaşır.
    /// </summary>
    public string TokenProtected { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Yöneticinin onayladığı düğüm sertifikasının SHA-256 parmak izi (<c>SHA256:AA:BB…</c>).
    /// Boşsa düğüm "yeniden onay gerek" durumundadır ve https düğüme HİÇ bağlanılmaz (G18).
    /// </summary>
    /// <remarks>
    /// Eski kayıtlardaki <c>verifyTlsCertificate</c> alanı artık OKUNMAZ: "doğrulamayı kapat"
    /// seçeneği kaldırıldı; o alanı <c>false</c> taşıyan kayıtlar da parmak izi olmadığı için
    /// onay isteyen duruma düşer (sessizce güvenilmez).
    /// </remarks>
    public string CertificateFingerprint { get; set; } = string.Empty;

    public DateTime? CertificateApprovedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenAtUtc { get; set; }

    public string LastError { get; set; } = string.Empty;

    /// <summary>Dışarı çıkan görünüm. Belirteç alanı BURADA YOK; olduğu tek yer bu sınıfın kendisi.</summary>
    public ClusterNodeDto ToDto() => new()
    {
        Id = Id,
        Label = Label,
        Address = Address,
        Enabled = Enabled,
        CertificateFingerprint = CertificateFingerprint,
        CertificateState = ClusterCertificateStates.For(Address, CertificateFingerprint),
        CertificateApprovedAtUtc = CertificateApprovedAtUtc,
        HasToken = !string.IsNullOrWhiteSpace(TokenProtected),
        CreatedAtUtc = CreatedAtUtc,
        LastSeenAtUtc = LastSeenAtUtc,
        LastError = LastError
    };
}

/// <summary>Düğümün sertifika durumu (arayüz ve yoklama bunu okur).</summary>
public static class ClusterCertificateStates
{
    public const string Pinned = "pinned";

    public const string ReapprovalRequired = "reapproval_required";

    /// <summary>Adres http: TLS yok, sabitlenecek sertifika da yok.</summary>
    public const string NotApplicable = "not_applicable";

    public static bool IsHttps(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static string For(string? address, string? fingerprint) =>
        !IsHttps(address)
            ? NotApplicable
            : string.IsNullOrWhiteSpace(fingerprint) ? ReapprovalRequired : Pinned;
}

/// <summary>Dosyanın kök nesnesi. Düz dizi yerine sarmalayıcı: şema ileride alan kazanabilsin.</summary>
public sealed class ClusterNodeStoreFile
{
    public List<ClusterNodeRecord> Nodes { get; set; } = [];
}
