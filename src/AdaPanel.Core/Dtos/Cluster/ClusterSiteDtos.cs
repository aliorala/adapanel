using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Cluster;

/// <summary>
/// Siteyi hangi düğüme kuracağımızı belirten istek.
/// </summary>
/// <remarks>
/// Plesk modelinde bir abonelik TEK düğümde yaşar, düğümler arasına bölünmez. Burada da öyle:
/// site ya seçilen düğümde ya da otomatik seçilen düğümde kurulur; "yarısı orada" diye bir hâl yok.
/// </remarks>
public sealed class CreateClusterSiteRequest
{
    /// <summary>
    /// Hedef düğüm. Boş bırakılırsa yerleşim otomatik seçilir (bkz. <c>ClusterPlacement</c>).
    /// </summary>
    public Guid? NodeId { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(255)]
    public string HostName { get; init; } = string.Empty;
}

/// <summary>Uzak düğümde site oluşturma sonucu.</summary>
public sealed class ClusterSiteCreationDto
{
    public Guid NodeId { get; init; }

    public string NodeLabel { get; init; } = string.Empty;

    public string SiteName { get; init; } = string.Empty;

    /// <summary>Yerleşim otomatik mi seçildi (arayüz "X düğümüne kuruldu" diyebilsin).</summary>
    public bool PlacementWasAutomatic { get; init; }

    /// <summary>Uzak panelin döndürdüğü mesaj anahtarı (varsa) — ne olduğu ORADAN gelir.</summary>
    public string RemoteMessage { get; init; } = string.Empty;
}

/// <summary>
/// "Düğüme git" bağlantısı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu bir OTURUM DEVRİ DEĞİLDİR ve olmaması bilinçlidir.</b> Merkez, düğümün gizli giriş yolunu
/// kimliği doğrulanmış bir çağrıyla öğrenir ve yöneticiye doğru adresi verir; yönetici düğümde
/// KENDİ kimliğiyle oturum açar.
/// </para>
/// <para>
/// Gerekçe kodda ölçülü: bu depoda <c>AuthController</c> ve <c>ApiTokensController</c> bilerek
/// ÇEREZ şemasına sabitlenmiştir — bir API anahtarının oturum çerezine ya da yeni bir anahtara
/// dönüşememesi için. Merkezin belirteci düğümde çereze çevirebilseydi, "yıkıcı" yetkili tek bir
/// anahtar düğümde parola değiştirebilen, iki adımlı doğrulamayı kapatabilen, kendine terminal
/// yetkili yeni anahtar basabilen tam bir oturuma yükselirdi. Kolaylık için o kapıyı açmak,
/// panelin kurduğu yetenek modelini sessizce anlamsızlaştırmak olurdu.
/// </para>
/// </remarks>
public sealed class ClusterNodeLinkDto
{
    public Guid NodeId { get; init; }

    /// <summary>Düğümün gerçek panel adresi (gizli giriş yolu dahil).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Bağlantı açıldığında düğümde oturum açmak gerekir mi. Şu an DAİMA true; alan, arayüzün
    /// kullanıcıya doğru beklentiyi vermesi için var.
    /// </summary>
    public bool RequiresSignIn { get; init; } = true;
}
