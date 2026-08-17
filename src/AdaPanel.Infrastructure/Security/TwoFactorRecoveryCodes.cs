using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// İki adımlı doğrulama kurtarma kodlarının DEPO BİÇİMİ ve yolu.
/// </summary>
/// <remarks>
/// <para>
/// Yol (<c>config/2fa/&lt;kullanıcı&gt;.json</c>) ve dosya biçimi ayrı bir yere alındı çünkü artık
/// iki farklı yerden yazılıyor: panel içindeki iki adımlı doğrulama akışı (<c>AuthService</c>) ve
/// sunucu konsolundan çalışan parola kurtarma aracı. İki kopya olsaydı birinin yolu değiştiğinde
/// diğeri ARTIK VAR OLMAYAN bir dosyayı temizlediğini sanır; kullanıcı 2FA'yı kapattığını sanarken
/// eski kurtarma kodları yaşamaya devam ederdi.
/// </para>
/// <para>
/// Dosyada kodların KENDİSİ değil SHA-256 özetleri durur; bu sınıf yalnız kabı taşır, özetleme
/// disiplini <c>AuthService</c>'te kalır (kod üretimi orada tek yerdedir).
/// </para>
/// </remarks>
internal static class TwoFactorRecoveryCodes
{
    /// <summary>Kullanıcının kurtarma kodu deposunun veri kökündeki göreli yolu.</summary>
    public static string PathFor(Guid userId) => $"config/2fa/{userId:N}.json";

    /// <summary>
    /// Kullanıcının bütün kurtarma kodlarını geçersiz kılar (dosyayı BOŞ depoya çevirir).
    /// Dosyayı silmek yerine boş yazmak bilinçli: <see cref="JsonFileStore"/> atomik yazım yapar,
    /// silme ise yarıda kalan bir işlemde dosyayı hiç bırakmamak/eskisini bırakmak arasında
    /// belirsizlik doğururdu.
    /// </summary>
    public static Task ClearAsync(JsonFileStore fileStore, Guid userId, CancellationToken cancellationToken = default)
        => fileStore.WriteAsync(PathFor(userId), new Store(), cancellationToken);

    /// <summary>Dosya biçimi: kurtarma kodlarının özetleri.</summary>
    public sealed class Store
    {
        public List<string> Codes { get; set; } = [];
    }
}
