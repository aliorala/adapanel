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

    /// <summary>Listeye gelen postanın dağıtılacağı alıcı e-posta adresleri.</summary>
    public IReadOnlyCollection<string> Recipients { get; set; } = [];
}

public sealed class SetMailListRecipientsRequest
{
    [Required]
    public string Address { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Recipients { get; set; } = [];
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
