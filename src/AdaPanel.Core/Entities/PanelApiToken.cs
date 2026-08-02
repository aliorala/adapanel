namespace AdaPanel.Core.Entities;

/// <summary>
/// Sunucudan sunucuya erişim için kişisel erişim anahtarı (MCP bağlantıları başta olmak üzere).
/// <para>
/// Neden gerekli: panelin tek kimlik doğrulama yolu ÇEREZDİ. Çerez, tarayıcı dışı bir istemci için
/// uygun değil — 12 saatte bir yeniden giriş, dakikada 5 deneme sınırı, iki adımlı doğrulama açıksa
/// otomatik giriş imkânsız, ve parolanın bir yerde saklanması gerekir. Anahtar bu sorunların
/// hiçbirini taşımaz ve tek tek iptal edilebilir.
/// </para>
/// <para>
/// Anahtarın kendisi ASLA saklanmaz; yalnızca karması tutulur. Kullanıcıya bir kez gösterilir.
/// Kaybedilirse yenisi üretilir — kurtarma yoktur, olmamalıdır.
/// </para>
/// </summary>
public sealed class PanelApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Anahtarın sahibi. Yetkiler bu kullanıcının rolünden ve kapsamından türer.</summary>
    public Guid UserId { get; set; }

    /// <summary>Kullanıcının verdiği ad ("Claude – dizüstü" gibi); iptal ederken hangisi olduğunu bilmek için.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Anahtarın SHA-256 karması (base64). Düz metin hiçbir yerde tutulmaz.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Kullanıcıya listede gösterilecek son 4 karakter ("…a9f3"); hangi anahtar olduğunu ayırt etmek için.</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>
    /// YIKICI araçlara izin var mı (silme, üzerine yazma, servis durdurma).
    /// Varsayılan KAPALI: bir yapay zeka istemcisine varsayılan olarak silme yetkisi verilmez.
    /// </summary>
    public bool AllowDestructive { get; set; }

    /// <summary>
    /// Sunucuda komut çalıştırmaya izin var mı. AYRI bir yetki, çünkü bu paneli değil SUNUCUYU vermektir.
    /// Varsayılan KAPALI.
    /// </summary>
    public bool AllowTerminal { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Boşsa süresiz. Dolduğunda anahtar reddedilir.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Son kullanım anı; kullanılmayan anahtarları fark edip iptal edebilmek için.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>Dolu ise anahtar iptal edilmiştir. Kayıt SİLİNMEZ ki denetim izi kalsın.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}
