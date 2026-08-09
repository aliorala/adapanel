namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Diskte tutulan lisans durumu (<c>config\license\state.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Anahtar burada DÜZ METİN DURMAZ.</b> <see cref="ProtectedKey"/>, projedeki kimlik kasası
/// kalıbıyla (<c>ISecretProtector</c> → ASP.NET DataProtection → <c>ProtectKeysWithDpapi</c>)
/// korunmuş değerdir; <c>DatabaseCredentialStore</c> ile aynı disiplin. Dosyayı kopyalayan biri
/// anahtarı okuyamaz, çünkü çözme anahtarı veri kökündeki <c>keys</c> klasöründe ve makineye
/// (DPAPI, LocalMachine) bağlıdır.
/// </para>
/// <para>
/// <see cref="IsConfigured"/> diske YAZILMAZ; okunduktan sonra yapılandırmadan doldurulur. Aksi
/// hâlde yapılandırmayı kapatmak eski dosyadaki "açık" değeri yüzünden etkisiz kalırdı.
/// </para>
/// </remarks>
public sealed class LicenseRecord
{
    /// <summary>Doğrulama ucu yapılandırıldı mı? (diskten okunmaz, yapılandırmadan gelir)</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Korunmuş (şifreli) lisans anahtarı; yoksa boş.</summary>
    public string ProtectedKey { get; set; } = string.Empty;

    /// <summary>Anahtarın son dört karakteri — gösterim için. Anahtarın kendisi ASLA saklanmaz.</summary>
    public string KeySuffix { get; set; } = string.Empty;

    /// <summary>Anahtarın panele girildiği an (UTC). Hiç doğrulanamazsa tolerans sayacı buradan başlar.</summary>
    public DateTime? KeyStoredAtUtc { get; set; }

    /// <summary>Sunucunun son kararı: "valid" | "invalid" | "expired" | boş (hiç karar alınmadı).</summary>
    public string LastVerdict { get; set; } = string.Empty;

    /// <summary>Sunucuyla son BAŞARILI temas (UTC).</summary>
    public DateTime? LastVerifiedAtUtc { get; set; }

    /// <summary>Son doğrulama DENEMESİ (başarısız olsa da) — aynı hatayı dakikada bir tekrarlamamak için.</summary>
    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>Sunucunun bildirdiği ürün kademesi.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>SKU'nun izin verdiği en fazla abonelik (0 = sınırsız).</summary>
    public int MaxSubscriptions { get; set; }

    /// <summary>Lisans bitiş tarihi (UTC); süresizse null.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Son denemedeki hata metni (teşhis); başarılı denemede temizlenir.</summary>
    public string LastError { get; set; } = string.Empty;
}
