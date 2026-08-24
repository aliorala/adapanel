using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Mail;

public sealed class ConfigureMailRequest
{
    /// <summary>hMailServer kurulumunda belirlenen "Administrator" parolası.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string AdminPassword { get; set; } = string.Empty;
}

public sealed class CreateMailDomainRequest
{
    [Required]
    [StringLength(255, MinimumLength = 3)]
    public string DomainName { get; set; } = string.Empty;
}

public sealed class CreateMailAccountRequest
{
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Yerel kısım (örn. "info") ya da tam adres ("info@example.com").</summary>
    [Required]
    public string Address { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;

    [Range(0, 1024 * 1024)]
    public int MaxSizeMB { get; set; } = 1024;
}

public sealed class SetMailAccountPasswordRequest
{
    [Required]
    public string Address { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;
}

public sealed class SetMailDomainQuotaRequest
{
    [Required]
    public string Domain { get; set; } = string.Empty;

    [Range(0, 1024 * 1024)]
    public int MaxSizeMB { get; set; }
}

public sealed class SetMailAccountQuotaRequest
{
    [Required]
    public string Address { get; set; } = string.Empty;

    [Range(0, 1024 * 1024)]
    public int MaxSizeMB { get; set; }
}

public sealed class UpdateMailSpamSettingsRequest
{
    public bool Enabled { get; set; } = true;

    [Range(0, 1000)]
    public double ScoreSpam { get; set; } = 5;

    [Range(0, 1000)]
    public double ScoreReject { get; set; }

    [Range(0, 1000)]
    public double ScoreDiscard { get; set; }

    public bool GreylistEnabled { get; set; }

    [Range(1, 7 * 24 * 60)]
    public int GreylistMinutes { get; set; } = 30;

    public bool TrustContacts { get; set; } = true;

    public bool TrustReplies { get; set; } = true;
}

public sealed class CreateMailAliasRequest
{
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Alias adresi (yerel kısım ya da tam adres).</summary>
    [Required]
    public string Address { get; set; } = string.Empty;

    /// <summary>Yönlendirme hedefi (tam e-posta adresi).</summary>
    [Required]
    [EmailAddress]
    public string Target { get; set; } = string.Empty;
}

public sealed class CreateMailListRequest
{
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Liste adresi (yerel kısım "duyuru" ya da tam adres "duyuru@example.com").</summary>
    [Required]
    public string Address { get; set; } = string.Empty;

    /// <summary>Listenin amacını anlatan serbest metin; Stalwart'ın <c>description</c> alanına yazılır.</summary>
    [StringLength(512)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Listeye gelen postanın dağıtılacağı abone e-posta adresleri.</summary>
    public IReadOnlyCollection<string> Recipients { get; set; } = [];

    /// <summary>Kimler gönderebilir (bkz. <see cref="MailListPostingPolicy"/>). Varsayılan: herkes.</summary>
    public string PostingPolicy { get; set; } = MailListPostingPolicy.Anyone;

    /// <summary><c>allowed</c> kipinde gönderim izni olan adresler.</summary>
    public IReadOnlyCollection<string> AllowedSenders { get; set; } = [];
}

/// <summary>
/// Dağıtım listesinin abonelerini değiştirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden iki ayrı kip (toplu / tek tek):</b> Stalwart'ın <c>update MailingList</c> çağrısı
/// <c>recipients</c> kümesini KOMPLE değiştirir — "bir abone ekle" diye bir işlem yoktur. Panel
/// yalnız toplu değiştirmeyi sunarsa, tek bir aboneyi çıkarmak için yöneticinin bütün listeyi
/// elle yeniden yazması gerekir; bir adresi yanlış kopyalaması, o kişinin listeden SESSİZCE
/// düşmesi demektir. Bu yüzden tek-abone işlemleri sunucudaki güncel küme OKUNUP üzerine fark
/// uygulanarak yapılır ve kullanıcı hiçbir zaman listenin tamamını elle taşımak zorunda kalmaz.
/// </para>
/// <para>
/// <see cref="Recipients"/> <c>null</c> ise toplu değiştirme İSTENMEMİŞTİR ve yalnız
/// <see cref="Add"/>/<see cref="Remove"/> uygulanır. Boş dizi ile <c>null</c> bilinçli olarak
/// AYRI anlamdadır: boş dizi "listeyi tamamen boşalt" demektir ve bu meşru bir isteme.
/// </para>
/// </remarks>
public sealed class SetMailListRecipientsRequest
{
    [Required]
    public string Address { get; set; } = string.Empty;

    /// <summary>Kümenin YENİ hâli (toplu değiştirme). <c>null</c> = dokunma, boş dizi = hepsini sil.</summary>
    public IReadOnlyCollection<string>? Recipients { get; set; }

    /// <summary>Eklenecek aboneler (mevcut kümenin üzerine).</summary>
    public IReadOnlyCollection<string> Add { get; set; } = [];

    /// <summary>Çıkarılacak aboneler — "abonelikten çıkarma" bu yoldan yapılır.</summary>
    public IReadOnlyCollection<string> Remove { get; set; } = [];

    /// <summary>
    /// Yeni gönderim politikası (bkz. <see cref="MailListPostingPolicy"/>). <c>null</c> = değiştirme.
    /// </summary>
    public string? PostingPolicy { get; set; }

    /// <summary><c>allowed</c> kipinin adres listesi. <c>null</c> = değiştirme.</summary>
    public IReadOnlyCollection<string>? AllowedSenders { get; set; }
}

/// <summary>
/// Bir dağıtım listesine EK ADRES (alias) ekler ya da çıkarır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden liste adresi de isteniyor, neden yalnız alias yetmiyor:</b> alias'lar listenin İÇİNDE
/// durur; alias'tan listeye gitmek bütün listeleri taramak demektir. Daha önemlisi bu bir KAPSAM
/// meselesi: yalnız alias gönderilseydi denetleyici sadece ALIAS'ın alan adını doğrulayabilirdi ve
/// kendi alan adında bir alias'ı BAŞKA bir kiracının listesine yazmak/ondan silmek serbest
/// kalırdı. İki adres birlikte gelince iki alan adı da doğrulanır.
/// </para>
/// <para>
/// <b><see cref="Alias"/> yerel kısım olabilir:</b> "info" yazıldığında LİSTENİN alan adıyla
/// tamamlanır — ekranda kullanıcı zaten "@ornek.com" son ekini görüyor.
/// </para>
/// </remarks>
public sealed class MailListAliasRequest
{
    /// <summary>Alias'ın ekleneceği/çıkarılacağı dağıtım listesinin TAM adresi.</summary>
    [Required]
    public string ListAddress { get; set; } = string.Empty;

    /// <summary>Ek adres: yerel kısım ("info") ya da tam adres ("info@ornek.com").</summary>
    [Required]
    public string Alias { get; set; } = string.Empty;
}

/// <summary>
/// Panelin dışarıdan erişilen kök adresi (ör. <c>https://panel.ornek.com</c>).
/// BOŞ = <c>List-Unsubscribe</c> başlığı hiç eklenmez.
/// </summary>
public sealed class SetMailPublicBaseUrlRequest
{
    [StringLength(255)]
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Giden posta rölesi (smarthost) ayarı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parola neden isteğe bağlı:</b> yönetici yalnız portu ya da TLS ayarını değiştirmek
/// istediğinde parolayı yeniden yazmak zorunda kalmamalı. Boş bırakılırsa panelde saklı olan
/// (DPAPI ile şifreli) parola korunur; sunucuya yeniden o yazılır. Bu ayrım olmadan her küçük
/// düzeltme, kullanıcıyı parolayı bir yere not etmeye ve düz metin taşımaya zorlardı.
/// </para>
/// </remarks>
public sealed class SetMailRelayRequest
{
    /// <summary>false ise röle KALDIRILIR: yönlendirme eski hâline döner ve saklı parola silinir.</summary>
    public bool Enabled { get; set; }

    /// <summary>Röle sunucusunun adresi (ad ya da IP). Enabled=true iken zorunlu.</summary>
    [StringLength(255)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary><c>smtp</c> (varsayılan) ya da <c>lmtp</c>.</summary>
    [StringLength(8)]
    public string Protocol { get; set; } = "smtp";

    /// <summary>Bağlantı doğrudan TLS ile açılsın mı (465 gibi)? false = STARTTLS.</summary>
    public bool ImplicitTls { get; set; }

    /// <summary>Röle sertifikası doğrulanmasın mı? Yalnız iç ağdaki röleler için.</summary>
    public bool AllowInvalidCerts { get; set; }

    [StringLength(255)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Yeni parola. BOŞ = panelde saklı parolayı koru (bkz. sınıf açıklaması).</summary>
    [StringLength(512)]
    public string? Password { get; set; }
}
