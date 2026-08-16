namespace AdaPanel.Core.Common;

/// <summary>
/// Panel parolalarının TEK kuralı. Uzunluk sınırı burada tanımlanır ve parolayı yazan HER yol
/// buradan geçer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekliydi — ölçülen asimetri:</b> kural üç ayrı yerde, üç ayrı biçimde duruyordu.
/// <c>CreateUserRequest</c> ve <c>ResetPasswordRequest</c> üzerinde
/// <c>[StringLength(128, MinimumLength = 12)]</c> vardı; <c>UpdateSubUserAsync</c> aynı sayıyı
/// gövdesine ELLE yazmıştı; <c>CreateSubUserAsync</c> ise HİÇ denetlemiyordu — yani abonelik altı
/// ek kullanıcı 1 karakterlik parolayla açılabiliyordu. Aynı kuralın üç kopyası, biri unutulduğu
/// anda sessizce delik açar ve tam olarak bu oldu.
/// </para>
/// <para>
/// <b>Neden veri açıklaması (DataAnnotations) yetmez:</b> öznitelikler yalnız MVC model bağlama
/// yolunda çalışır. Aynı servisler MCP araçlarından ve panelin kendi iç akışlarından da çağrılıyor;
/// oralarda öznitelik HİÇ değerlendirilmez. Kural bu yüzden servis katmanında, veriyi yazan
/// noktanın hemen yanında da uygulanır.
/// </para>
/// <para>
/// Anahtar (<c>users.password_too_short</c>) YENİDEN ÜRETİLMİYOR: panelde zaten var ve iki dilde
/// çevrili. Yeni bir anahtar açmak, kullanıcının ekranda ham anahtar görme riskini bedava üretmek
/// olurdu.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>En kısa kabul edilen parola uzunluğu.</summary>
    public const int MinimumLength = 12;

    /// <summary>
    /// En uzun kabul edilen parola uzunluğu. Yalnız veri açıklamalarında kullanılır (karma
    /// maliyetini sınırlar); <see cref="Ensure"/> üst sınırı denetlemez çünkü elimizdeki tek
    /// çevrilmiş anahtar "çok kısa" diyor — uzun bir parolayı "kısa" diye reddetmek yalan olurdu.
    /// </summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Parolayı kurala göre doğrular; uymuyorsa <c>users.password_too_short</c> ile reddeder.
    /// </summary>
    /// <remarks>
    /// <see cref="InvalidOperationException"/> kullanılıyor çünkü panelin iş kuralı ihlali için
    /// yerleşik biçimi bu: <c>ApiExceptionHandlingMiddleware</c> onu mesaj anahtarıyla 400'e çevirir
    /// (aynı anahtarı bugün <c>UpdateSubUserAsync</c> da bu şekilde fırlatıyor).
    /// </remarks>
    public static void Ensure(string? password)
    {
        if ((password ?? string.Empty).Length < MinimumLength)
        {
            throw new InvalidOperationException("users.password_too_short");
        }
    }
}
