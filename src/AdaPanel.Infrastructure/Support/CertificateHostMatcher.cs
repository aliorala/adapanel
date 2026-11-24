using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Sertifikanın, bağlanılan ADIN (IP değil) sertifikası olup olmadığını denetler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var (G19):</b> DNS rebinding'e karşı panel adı BİR KEZ çözer ve doğrulanmış IP'ye
/// bağlanır. TLS ana makine adı olarak IP'yi görür; sertifika adı yazılmışsa .NET
/// <c>RemoteCertificateNameMismatch</c> der — oysa sertifika kullanıcının yazdığı ada AİTTİR.
/// Burada ad uyuşmazlığı, YALNIZ başka hiçbir TLS hatası yokken ve sertifika özgün adı gerçekten
/// kapsıyorsa, kabul edilir. Zincir/süre hatası varsa karar değişmez (reddedilir).
/// </para>
/// <para>
/// Yalnız <c>subjectAltName</c> bakılır (RFC 6125; CN yedeği modern istemcilerde de yoktur).
/// Joker karakter yalnız en soldaki etiket olarak ve tek etiket kapsar.
/// </para>
/// </remarks>
public static class CertificateHostMatcher
{
    /// <summary>
    /// <paramref name="errors"/> yalnız ad uyuşmazlığıysa ve sertifika <paramref name="expectedHost"/>'u
    /// kapsıyorsa <see cref="SslPolicyErrors.None"/> döner; aksi hâlde girdiyi AYNEN döner.
    /// </summary>
    public static SslPolicyErrors ForgiveNameMismatchFor(X509Certificate? certificate, SslPolicyErrors errors, string? expectedHost)
    {
        if (errors != SslPolicyErrors.RemoteCertificateNameMismatch || certificate is null || string.IsNullOrWhiteSpace(expectedHost))
        {
            return errors;
        }

        using var parsed = new X509Certificate2(certificate);
        return Matches(parsed, expectedHost) ? SslPolicyErrors.None : errors;
    }

    public static bool Matches(X509Certificate2 certificate, string host)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var wanted = (host ?? string.Empty).Trim().TrimEnd('.');
        if (wanted.Length == 0)
        {
            return false;
        }

        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null)
        {
            return false;
        }

        var isIp = IPAddress.TryParse(wanted.Trim('[', ']'), out var wantedIp);
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific)
                {
                    sequence.ReadEncodedValue();
                    continue;
                }

                if (tag.TagValue == 2 && !isIp)
                {
                    var dns = sequence.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2));
                    if (DnsMatches(dns, wanted))
                    {
                        return true;
                    }
                }
                else if (tag.TagValue == 7 && isIp)
                {
                    var bytes = sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                    if (new IPAddress(bytes).Equals(wantedIp))
                    {
                        return true;
                    }
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            return false;
        }

        return false;
    }

    private static string ToAscii(string name)
    {
        var wildcard = name.StartsWith("*.", StringComparison.Ordinal);
        var body = wildcard ? name[2..] : name;
        try
        {
            body = new System.Globalization.IdnMapping { AllowUnassigned = true }.GetAscii(body);
        }
        catch (ArgumentException)
        {
            // Çevrilemeyen ad olduğu gibi (küçük harf) karşılaştırılır; eşleşme şansı yok sayılır.
        }

        return ((wildcard ? "*." : string.Empty) + body).ToLowerInvariant();
    }

    private static bool DnsMatches(string pattern, string host)
    {
        // Unicode ad ile punycode SAN AYNI addır: iki taraf da ASCII (punycode) biçime çevrilir.
        pattern = ToAscii(pattern.Trim().TrimEnd('.'));
        host = ToAscii(host);
        if (pattern.Length == 0)
        {
            return false;
        }

        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(pattern, host, StringComparison.Ordinal);
        }

        // Joker: yalnız en soldaki TEK etiket; "*.com" gibi kök joker ve "a.b.example.com" kapsanmaz.
        var suffix = pattern[1..]; // ".example.com"
        if (suffix.Count(c => c == '.') < 2 || !host.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var leftmost = host[..^suffix.Length];
        return leftmost.Length > 0 && !leftmost.Contains('.');
    }
}
