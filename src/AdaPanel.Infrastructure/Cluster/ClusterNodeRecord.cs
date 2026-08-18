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

    public bool VerifyTlsCertificate { get; set; } = true;

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
        VerifyTlsCertificate = VerifyTlsCertificate,
        HasToken = !string.IsNullOrWhiteSpace(TokenProtected),
        CreatedAtUtc = CreatedAtUtc,
        LastSeenAtUtc = LastSeenAtUtc,
        LastError = LastError
    };
}

/// <summary>Dosyanın kök nesnesi. Düz dizi yerine sarmalayıcı: şema ileride alan kazanabilsin.</summary>
public sealed class ClusterNodeStoreFile
{
    public List<ClusterNodeRecord> Nodes { get; set; } = [];
}
