using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Denetim kaydına yazılmadan ÖNCE metni temizler: sır değerlerini maskeler ve sütun sınırına sığdırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden merkezî:</b> "parolayı kayda yazma" kuralı, her çağrı yerinin hatırlamasına
/// bırakıldığında er ya da geç bir yerde unutulur — ve unutulduğu an parola, silinmesi en zor
/// yerde (denetim kaydında, muhtemelen yedeklerde de) kalıcı olur. Bu yüzden kural çağrı
/// yerlerinde DEĞİL, yazma yolunun tek geçidinde uygulanır. Çağrı yerleri zaten parola
/// göndermez; burası ikinci savunma hattıdır.
/// </para>
/// <para>
/// <b>Neden anahtar=değer kalıbı:</b> serbest metinden "hangi kelime paroladır" diye tahmin
/// etmek imkânsız ve zararlıdır — terminal komutunun kendisini de bozardı. Bunun yerine yalnızca
/// açıkça sır adı taşıyan bir alanın DEĞERİ maskelenir ("password=Abc123" → "password=[gizlendi]").
/// Anahtarın kendisi korunur ki kayıt "neyin değiştiği" bilgisini kaybetmesin.
/// </para>
/// <para>
/// <b>Neden kırpma da burada:</b> Details sütunu 2048, Action/Resource 128 karakterdir. Terminal
/// komutu 8000 karaktere kadar olabilir; kırpılmazsa yazım (SQL Server gibi sıkı bir sağlayıcıda)
/// patlar ve kayıt HİÇ oluşmazdı. Kırpılmış ama var olan bir kayıt, hiç olmayandan iyidir.
/// </para>
/// </remarks>
public static class AuditSecretRedactor
{
    /// <summary>Maskelenen değerin yerine yazılan sabit.</summary>
    public const string Mask = "[gizlendi]";

    /// <summary>Details sütunu 2048; kırpma payı bırakılır (ek "…" ve olası maskeleme için).</summary>
    public const int DetailsMaxLength = 2000;

    /// <summary>Action/Resource sütunları 128.</summary>
    public const int ShortFieldMaxLength = 128;

    // Sır adı taşıyan alanların DEĞERİ maskelenir. Tırnaklı değerler tek parça sayılır ki
    // "password='iki kelime'" içindeki boşluk maskelemeyi yarıda kesmesin.
    private static readonly Regex SecretAssignment = new(
        """(?<key>\b(?:pass|passwd|password|pwd|parola|sifre|şifre|secret|gizli|token|apikey|api[_-]?key|accesskey|connectionstring|credential)s?\b)\s*(?<sep>[:=])\s*(?<value>"[^"]*"|'[^']*'|\S+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Sır değerlerini maskeler ve Details uzunluğuna kırpar.</summary>
    public static string SanitizeDetails(string? value) =>
        Truncate(Redact(value), DetailsMaxLength);

    /// <summary>Action/Resource gibi kısa alanlar için: maskeler ve 128 karaktere kırpar.</summary>
    public static string SanitizeShortField(string? value) =>
        Truncate(Redact(value), ShortFieldMaxLength);

    /// <summary>Yalnız maskeleme (kırpma yok) — test ve yeniden kullanım için ayrı durur.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return SecretAssignment.Replace(value, match =>
                $"{match.Groups["key"].Value}{match.Groups["sep"].Value}{Mask}");
        }
        catch (RegexMatchTimeoutException)
        {
            // ÖLÇEMEDİK durumu: metni maskeleyemediysek olduğu gibi geçirmek, tam da korumak
            // istediğimiz şeyi sızdırmak olurdu. Kaydı tamamen kaybetmemek için içerik atılır,
            // kaydın kendisi (kim/ne zaman/hangi eylem) korunur.
            return Mask;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
