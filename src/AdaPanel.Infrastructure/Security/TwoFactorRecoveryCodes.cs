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

    /// <summary>
    /// TOTP ZAMAN ADIMINI TÜKETİR: adım daha önce kullanılmışsa (ya da daha eskiyse) <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapatılan açık:</b> TOTP kodu 30 saniyelik bir adımdan üretilir ve panel ±1 adım tolerans
    /// uyguladığı için AYNI KOD 90 saniye boyunca geçerliydi. Doğrulama yalnız "geçerli mi" diye
    /// sorduğundan, bir kez kullanılmış kod o pencere içinde TEKRAR kullanılabiliyordu. Gerçekçi
    /// senaryo: kullanıcı kodu bir kimlik avı sayfasına girer ya da kod omuz üstünden/ekran
    /// görüntüsünden okunur; saldırgan aynı kodla ikinci bir oturum açardı. İkinci faktörün tek
    /// vaadi "kodu gören biri onu kullanamasın" değil, "kod BİR KEZ işe yarasın"dır.
    /// </para>
    /// <para>
    /// <b>Neden <c>&lt;=</c>, neden yalnız eşitlik değil:</b> yalnız son adım reddedilseydi, bir
    /// önceki adımın kodu (tolerans penceresi yüzünden hâlâ geçerli) kullanılabilir kalırdı. Adım
    /// numarası zamanla yalnız BÜYÜDÜĞÜ için "son kullanılandan büyük olmalı" kuralı pencerenin
    /// tamamını kapatır ve tek bir sayı saklamaya yeter.
    /// </para>
    /// <para>
    /// <b>Neden kurtarma kodlarıyla AYNI dosyada:</b> ikisi de kullanıcının iki adımlı doğrulama
    /// durumudur ve ikinci bir dosya, ikinci bir temizleme/silme yolu demekti (2FA kapatıldığında
    /// biri temizlenip diğeri unutulurdu). <c>Codes</c> alanı burada AÇIKÇA korunur; yazımın
    /// tamamı <see cref="JsonFileStore.UpdateAsync"/>'in yol kilidi altında tek parçadır, yani
    /// aynı kodu taşıyan iki eşzamanlı istekten yalnız biri geçer.
    /// </para>
    /// </remarks>
    public static async Task<bool> TryConsumeTotpStepAsync(
        JsonFileStore fileStore,
        Guid userId,
        long step,
        CancellationToken cancellationToken = default)
    {
        var kabul = false;
        await fileStore.UpdateAsync(
            PathFor(userId),
            new Store(),
            mevcut =>
            {
                if (step <= mevcut.LastUsedTotpStep)
                {
                    return mevcut;
                }

                kabul = true;
                return new Store { Codes = mevcut.Codes, LastUsedTotpStep = step };
            },
            cancellationToken);

        return kabul;
    }

    /// <summary>Dosya biçimi: kurtarma kodlarının özetleri ve son tüketilen TOTP zaman adımı.</summary>
    public sealed class Store
    {
        public List<string> Codes { get; set; } = [];

        /// <summary>
        /// En son kabul edilen TOTP zaman adımı. 0 = hiç kullanılmadı. Kurtarma kodları yeniden
        /// üretildiğinde bu değer KORUNMALIDIR (bkz. AuthService); sıfırlanırsa az önce kullanılan
        /// bir kod yeniden geçerli hâle gelirdi.
        /// </summary>
        public long LastUsedTotpStep { get; set; }
    }
}
