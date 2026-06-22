using System.Security.Cryptography;
using System.Text;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// AWS Signature Version 4 imzalayıcısı (S3). Gerçek S3 ve S3 uyumlu depolara
/// (MinIO, Wasabi, Backblaze B2, Cloudflare R2) erişim anahtarı + gizli anahtar ile
/// imzalı istek üretir. Büyük dosyaları iki kez taramamak için HTTPS üzerinde
/// UNSIGNED-PAYLOAD kullanılır.
/// </summary>
internal static class S3RequestSigner
{
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// İmza üretir; dönen başlıklar isteğe aynen eklenmelidir:
    /// Authorization, x-amz-date ve x-amz-content-sha256.
    /// </summary>
    public static (string Authorization, string AmzDate, string ContentSha256) Sign(
        string httpMethod,
        Uri uri,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset utcNow,
        string payloadSha256,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        const string service = "s3";
        var amzDate = utcNow.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = utcNow.UtcDateTime.ToString("yyyyMMdd");

        // İmzalanacak başlıklar: host + x-amz-* (+ verilen ek başlıklar), küçük harf ve alfabetik.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
            ["x-amz-content-sha256"] = payloadSha256,
            ["x-amz-date"] = amzDate
        };
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                headers[key.ToLowerInvariant()] = value.Trim();
            }
        }

        var canonicalHeaders = string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"));
        var signedHeaders = string.Join(";", headers.Keys);

        var canonicalRequest = string.Join("\n",
            httpMethod.ToUpperInvariant(),
            CanonicalizePath(uri),
            CanonicalizeQuery(uri),
            canonicalHeaders,
            signedHeaders,
            payloadSha256);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = HmacSha256(
            HmacSha256(
                HmacSha256(
                    HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp),
                    region),
                service),
            "aws4_request");
        var signature = ToHex(HmacSha256(signingKey, stringToSign));

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        return (authorization, amzDate, payloadSha256);
    }

    // S3 yol kanonikleştirmesi: segmentler RFC 3986'ya göre kodlanır, '/' korunur.
    private static string CanonicalizePath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return string.Join("/", path.Split('/').Select(segment => UriEncode(Uri.UnescapeDataString(segment))));
    }

    private static string CanonicalizeQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?")
        {
            return string.Empty;
        }

        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var parts = pair.Split('=', 2);
                return (Key: UriEncode(Uri.UnescapeDataString(parts[0])),
                        Value: parts.Length > 1 ? UriEncode(Uri.UnescapeDataString(parts[1])) : string.Empty);
            })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    // RFC 3986 unreserved dışındaki her bayt %XX (büyük harf) olarak kodlanır.
    private static string UriEncode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
