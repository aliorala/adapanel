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
    //
    // NEDEN ANAHTAR ADINDA ÖNEK/SONEK SEGMENTİ VAR: eski desen anahtarı `\b...\b` ile sınırlıyordu
    // ve alt çizgi bir KELİME KARAKTERİ olduğu için `refresh_token=...` HİÇ eşleşmiyordu — "token"
    // sınırı alt çizgide açılmıyor. Aynı sebeple `access_token`, `x-amz-signature`, `api.key` de
    // dışarıda kalıyordu; yani sır adı taşıyan en yaygın biçimler tam olarak kaçıyordu. Artık
    // anahtar, nokta/alt çizgi/tire ile ayrılmış önek ve sonek parçaları kabul ediyor. Fazla
    // maskeleme (ör. "password_hint") kabul edilebilir; eksik maskeleme değildir.
    //
    // NEDEN AYRICA `authorization` / `signature` / `sig`: bunlar üstlendikleri işi ADINDAN belli
    // eden alanlar. `sig` özellikle Azure paylaşılan erişim imzasının (SAS) sorgu parametresidir:
    // `?...&sig=<imza>` tek başına depoya TAM erişim verir — yani parolaya eşdeğerdir. Bugün panelde
    // bunları kayda yazan bir çağrı yeri YOK (yedek modülü adresleri kendi temizliyor); burası
    // merkezî geçit olduğu için yarın yazan biri çıkarsa yakalayan yer burası olmalı.
    //
    // NEDEN DEĞER KISMINDA "şema + değer" seçeneği: `authorization: Bearer eyJ...` biçiminde değer
    // İKİ parçadır. Yalnız `\S+` alınsaydı maskelenen parça "Bearer" olur, jetonun kendisi kayda
    // OLDUĞU GİBİ düşerdi — maskeleme var görünüp tam da korunacak şeyi bırakırdı.
    private static readonly Regex SecretAssignment = new(
        """(?<![A-Za-z0-9])(?<key>(?:[A-Za-z0-9]+[_.\-])*(?:pass|passwd|password|pwd|parola|sifre|şifre|secret|gizli|token|jeton|apikey|api[_.\-]?key|accesskey|access[_.\-]?key|connectionstring|connection[_.\-]?string|credential|authorization|bearer|signature|sig)s?(?:[_.\-][A-Za-z0-9]+)*)\s*(?<sep>[:=])\s*(?<value>"[^"]*"|'[^']*'|(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)\s+\S+|\S+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // İKİNCİ DESEN — anahtarsız kimlik şeması: `Bearer eyJhbGciOi...`.
    //
    // NEDEN AYRI: ayraç burada BOŞLUKTUR. Boşluğu genel ayraç listesine eklemek felaket olurdu —
    // "password was changed" gibi İNSAN CÜMLELERİNDE bir sonraki kelimeyi maskeleyip kaydı
    // okunamaz hâle getirirdi (kaydın var olma sebebi okunabilir olması). Bu yüzden boşluk yalnız
    // BİLİNEN kimlik şemalarının ardından, ve yalnız ardından gelen parça GERÇEKTEN bir kimlik
    // bilgisine benziyorsa (jeton alfabesi, en az 16 karakter) sır sayılır. "Bearer token missing"
    // gibi bir cümle bu ölçüye takılmaz ve olduğu gibi kalır.
    private static readonly Regex SchemeCredential = new(
        """(?<![A-Za-z0-9])(?<scheme>Bearer|Basic|Digest|Negotiate)\s+(?<value>[A-Za-z0-9\-._~+/]{16,}={0,2})""",
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
            var maskeli = SecretAssignment.Replace(value, match =>
                $"{match.Groups["key"].Value}{match.Groups["sep"].Value}{Mask}");

            // Anahtarsız "Bearer <jeton>" biçimi ikinci geçişte yakalanır. Şema ADI korunur ki
            // kayıt "burada bir kimlik bilgisi vardı" bilgisini kaybetmesin.
            return SchemeCredential.Replace(maskeli, match =>
                $"{match.Groups["scheme"].Value} {Mask}");
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
