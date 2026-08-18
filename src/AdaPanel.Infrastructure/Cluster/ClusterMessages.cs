namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Çoklu sunucu modülünün mesaj anahtarları.
/// </summary>
/// <remarks>
/// Sabit olarak duruyorlar çünkü aynı anahtar hem servis hem denetleyici hem de test tarafından
/// kullanılıyor. Elle yazılan üç kopyadan biri değiştiğinde hata, ekranda ham anahtar olarak
/// görünür — ve o hata sınıfı bu depoda zaten yaşandı (bkz. MessageKeyCoverageTests).
/// </remarks>
public static class ClusterMessages
{
    public const string InvalidAddress = "cluster.invalid_address";

    /// <summary>Adres iç ağa/geri döngüye/bulut meta verisine işaret ediyor (SSRF kapısı).</summary>
    public const string AddressForbidden = "cluster.address_forbidden";

    public const string InvalidLabel = "cluster.invalid_label";

    public const string TokenRequired = "cluster.token_required";

    public const string DuplicateAddress = "cluster.duplicate_address";

    public const string NodeNotFound = "cluster.node_not_found";

    public const string NodeDisabled = "cluster.node_disabled";

    public const string NodeUnreachable = "cluster.node_unreachable";

    /// <summary>Uzak düğüm isteği reddetti (yetki/HTTP hatası); ayrıntı Detail alanında.</summary>
    public const string RemoteRejected = "cluster.remote_rejected";

    /// <summary>Yerleşim için uygun (açık + ulaşılabilir) düğüm yok.</summary>
    public const string NoNodeAvailable = "cluster.no_node_available";

    /// <summary>Otomatik yerleşim gerekçesi: en az siteye sahip düğüm seçildi.</summary>
    public const string PlacementLeastLoaded = "cluster.placement_least_loaded";

    /// <summary>Otomatik yerleşim gerekçesi: yönetici düğümü kendisi seçti.</summary>
    public const string PlacementExplicit = "cluster.placement_explicit";
}
