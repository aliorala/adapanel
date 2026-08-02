using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Ai;

/// <summary>Listede gösterilen anahtar. Anahtarın kendisi ASLA burada dönmez.</summary>
public sealed class ApiTokenDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Son 4 karakter ("…a9f3"); hangi anahtar olduğunu ayırt etmek için.</summary>
    public string Suffix { get; set; } = string.Empty;

    public bool AllowDestructive { get; set; }

    public bool AllowTerminal { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    public bool IsRevoked { get; set; }

    /// <summary>Süresi dolmuş ya da iptal edilmiş mi — arayüzün "pasif" göstermesi için.</summary>
    public bool IsUsable { get; set; }
}

public sealed class CreateApiTokenRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Silme/üzerine yazma gibi YIKICI araçlara izin. Varsayılan false: bir yapay zeka istemcisine
    /// varsayılan olarak silme yetkisi verilmez, kullanıcı bilerek açar.
    /// </summary>
    public bool AllowDestructive { get; set; }

    /// <summary>
    /// Sunucuda komut çalıştırma izni. AYRI tutuluyor çünkü bu paneli değil SUNUCUYU vermektir.
    /// Yalnızca yönetici açabilir.
    /// </summary>
    public bool AllowTerminal { get; set; }

    /// <summary>Gün cinsinden ömür; 0 ya da boş = süresiz.</summary>
    [Range(0, 3650)]
    public int ExpiresInDays { get; set; }
}

/// <summary>
/// Oluşturma yanıtı. <see cref="Token"/> yalnızca BU YANITTA döner ve bir daha asla gösterilemez;
/// veritabanında yalnız karması saklanır.
/// </summary>
public sealed class CreatedApiTokenDto
{
    public ApiTokenDto Token { get; set; } = new();

    public string Value { get; set; } = string.Empty;
}
