using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Azure Storage <c>SharedKey</c> imzalayıcısı (Blob servisi). Depolama hesabı adı + hesap
/// anahtarıyla imzalı istek üretir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden SAS yetmiyor da bu var:</b> SAS jetonu adresin SORGU dizesinde taşınır ve kayda
/// yazılan adres <see cref="BackupService"/> içinde sorgusundan arındırılır (imza sızmasın diye).
/// Yani SAS ile yüklenen bir yedeğin kaydında jeton YOKTUR — saklama temizliği o nesneyi bir daha
/// silemez. Hesap anahtarıyla imzalama, silmeyi kaydın (temiz) adresinden yeniden mümkün kılar.
/// SAS yolu yine desteklenir; orada silme, S3 <i>presigned</i> hedefinde olduğu gibi ATLANIR ve
/// bu açıkça söylenir.
/// </para>
/// <para>
/// <b>Neden ayrı bir sınıf:</b> <see cref="S3RequestSigner"/> ile aynı gerekçe — imza kuralı
/// kriptografik ve tek karakterlik bir sapmada sunucu <c>403 AuthenticationFailed</c> döner.
/// Yükleme akışından ayrı durması, kuralın ağ olmadan test edilebilmesini sağlar.
/// </para>
/// </remarks>
internal static class AzureBlobRequestSigner
{
    /// <summary>
    /// İstekte bildirilen REST sürümü. SABİT tutulur: sürüm imzaya (canonical headers) girdiği
    /// için "en yenisini kullan" gibi bir davranış, imzayı sunucudan sunucuya oynatırdı.
    /// </summary>
    public const string ApiVersion = "2021-08-06";

    /// <summary>Blob yüklerken ZORUNLU başlık; bloğun türünü söyler.</summary>
    public const string BlockBlobHeader = "BlockBlob";

    /// <summary>
    /// İmza üretir. Dönen <c>Authorization</c> ve <c>x-ms-date</c> başlıkları isteğe AYNEN
    /// eklenmelidir; ayrıca <c>x-ms-version</c> (<see cref="ApiVersion"/>) ve verilen
    /// <paramref name="extraHeaders"/> de eklenmelidir — imza bunların hepsini kapsar.
    /// </summary>
    /// <param name="contentLength">Gövde uzunluğu. 0 ise imzada BOŞ bırakılır (Azure kuralı).</param>
    /// <param name="contentType">
    /// İsteğin <c>Content-Type</c> başlığı; gövde yoksa boş. İstekteki değerle BİREBİR aynı
    /// olmalıdır — imzalanan ile gönderilen ayrılırsa Azure isteği reddeder.
    /// </param>
    public static (string Authorization, string MsDate) Sign(
        string httpMethod,
        Uri uri,
        string accountName,
        string accountKey,
        DateTimeOffset utcNow,
        long contentLength,
        string contentType = "",
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        // RFC 1123 ("R"): Azure yalnızca bu biçimi kabul eder.
        var msDate = utcNow.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-date"] = msDate,
            ["x-ms-version"] = ApiVersion
        };

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                headers[key.ToLowerInvariant()] = value.Trim();
            }
        }

        // Kanonik başlıklar: yalnız x-ms-* , küçük harf, alfabetik, her biri kendi satırında.
        var canonicalHeaders = string.Concat(
            headers
                .Where(header => header.Key.StartsWith("x-ms-", StringComparison.Ordinal))
                .Select(header => $"{header.Key}:{header.Value}\n"));

        // Content-Length 0 iken BOŞ bırakılır. "0" yazmak 2015-02-21+ sürümlerinde imzayı bozar.
        var contentLengthText = contentLength <= 0
            ? string.Empty
            : contentLength.ToString(CultureInfo.InvariantCulture);

        var stringToSign = string.Join("\n",
            httpMethod.ToUpperInvariant(),
            string.Empty,        // Content-Encoding
            string.Empty,        // Content-Language
            contentLengthText,   // Content-Length
            string.Empty,        // Content-MD5
            contentType,         // Content-Type
            string.Empty,        // Date (x-ms-date kullanıldığı için boş)
            string.Empty,        // If-Modified-Since
            string.Empty,        // If-Match
            string.Empty,        // If-None-Match
            string.Empty,        // If-Unmodified-Since
            string.Empty)        // Range
            + "\n" + canonicalHeaders + CanonicalizeResource(uri, accountName);

        // Hesap anahtarı BASE64'tür; ham metin olarak kullanmak sessizce yanlış imza üretirdi.
        var imzaAnahtari = Convert.FromBase64String(accountKey);
        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(imzaAnahtari, Encoding.UTF8.GetBytes(stringToSign)));

        return ($"SharedKey {accountName}:{signature}", msDate);
    }

    /// <summary>
    /// Kanonik kaynak: <c>/&lt;hesap&gt;&lt;yol&gt;</c> + her sorgu parametresi ayrı satırda
    /// (adı küçük harf, alfabetik; aynı adın değerleri virgülle ve sıralı).
    /// </summary>
    /// <remarks>
    /// Sorgu parametreleri İMZAYA GİRER. Bu, SAS ile hesap anahtarını karıştırmanın neden
    /// çalışmadığını da açıklar: SAS parametreleri imzalanacak metni değiştirir.
    /// </remarks>
    private static string CanonicalizeResource(Uri uri, string accountName)
    {
        var builder = new StringBuilder();
        builder.Append('/').Append(accountName).Append(uri.AbsolutePath);

        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?")
        {
            return builder.ToString();
        }

        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var parts = pair.Split('=', 2);
                return (Key: Uri.UnescapeDataString(parts[0]).ToLowerInvariant(),
                        Value: parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
            })
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in pairs)
        {
            var values = group.Select(pair => pair.Value).OrderBy(value => value, StringComparer.Ordinal);
            builder.Append('\n').Append(group.Key).Append(':').Append(string.Join(",", values));
        }

        return builder.ToString();
    }
}
