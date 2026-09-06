using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AdaPanel.Infrastructure.Migration;

/// <summary>
/// Göçün UZAK TARAFI kimliğini doğrulama kararı — tek kaynak.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN DURUM (bu dosya bunun için var).</b> Göç yolunda uzak sunucunun kimliği ÜÇ ayrı
/// noktada hiç doğrulanmıyordu ve üçü de KULLANICININ KİMLİK BİLGİSİNİ taşıyordu:
/// </para>
/// <list type="number">
/// <item><b>Plesk/cPanel keşfi</b> — <c>OutboundConnectGuard.CreateHandler(verifyTlsCertificate: false)</c>.
/// İstek <c>X-API-Key</c> / <c>Basic</c> / <c>whm root:TOKEN</c> taşır. Bu anahtarlar kaynak
/// sunucuda genelde TAM YETKİLİDİR.</item>
/// <item><b>FTPS dosya çekme</b> — <c>FtpConfig.ValidateAnyCertificate = true</c>. İstek FTP
/// kullanıcı adı + parolasını taşır.</item>
/// <item><b>SFTP dosya çekme</b> — <c>SftpClient.HostKeyReceived</c>'e HİÇ abone olunmuyordu.
/// ÖLÇÜLDÜ: SSH.NET'te abone yoksa <c>HostKeyEventArgs.CanTrust</c> varsayılanı <c>true</c>'dur,
/// yani panel her host key'i sessizce kabul ediyordu. İstek SSH parolasını taşır.</item>
/// </list>
/// <para>
/// Doğrulama kapalıyken aynı ağdaki bir makine, ele geçmiş bir ağ cihazı ya da zehirlenmiş bir DNS
/// sahte kimlikle bağlantıyı karşılar ve bu bilgileri OKUR.
/// </para>
/// <para>
/// <b>GEVŞETME KASITLIYDI ve gerekçesi GEÇERLİYDİ:</b> göç edilen kaynak sunucular (Plesk 8443,
/// WHM 2087, eski FTPS) neredeyse her zaman kendinden imzalı, süresi geçmiş ya da adı uyuşmayan
/// sertifika taşır. Doğrulamayı kayıtsız şartsız açmak göç özelliğini gerçek dünyada
/// KULLANILAMAZ hâle getirirdi. Bu yüzden buradaki çözüm "aç ve bitir" değil.
/// </para>
/// <para>
/// <b>KOPYALANAN KALIP — <c>ssh</c>'ın host key onayı.</b> Olgun araçlar bu gerilimi aynı şekilde
/// çözer: varsayılan DOĞRULA; doğrulanamayan kimlik REDDEDİLİR ama reddedilirken NE görüldüğü
/// (sebep + parmak izi) yönetciye GÖSTERİLİR; yönetici parmak izini başka bir kanaldan teyit edip
/// AÇIKÇA kabul eder; kabul o parmak izine SABİTLENİR. Plesk Migrator'ın Linux göçünde de yol
/// aynıdır — hedef sunucuda <c>known_hosts</c> önceden doldurulur ve parmak izi sağlayıcı
/// konsolundan teyit edilir (Plesk "Authentication by SSH" belgeleri).
/// </para>
/// <para>
/// <b>NEDEN KÜRESEL "HEP KAPAT" ANAHTARI YOK.</b> En kötü seçenek odur: bir kez açılır, kalır ve
/// unutulur — üstelik açıldığını kimse görmez. Buradaki istisna TEK BİR PARMAK İZİNE ve TEK BİR
/// göç işine bağlıdır; başka bir sunucuya, hatta aynı sunucunun başka bir sertifikasına geçmez.
/// </para>
/// </remarks>
public static class TrustFingerprint
{
    /// <summary>Parmak izlerinin ortak öneki (<c>ssh-keygen -l</c> ve tarayıcılarla aynı okunuş).</summary>
    public const string Prefix = "SHA256:";

    /// <summary>
    /// Sertifikanın SHA-256 parmak izi: <c>SHA256:1A:2B:…</c> (büyük harf onaltılık, iki nokta ayraçlı).
    /// </summary>
    /// <remarks>
    /// Biçim tarayıcıların ve <c>openssl x509 -fingerprint -sha256</c>'nın gösterdiğiyle aynıdır:
    /// yönetici teyidi ancak KARŞILAŞTIRILABİLİR bir biçimde anlamlıdır.
    /// </remarks>
    public static string FromCertificate(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var hash = certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        return Prefix + Convert.ToHexString(hash).Chunk(2).Select(pair => new string(pair)).Aggregate((sol, sag) => sol + ":" + sag);
    }

    /// <summary>
    /// SSH host key parmak izi: <c>SHA256:base64</c> — <c>ssh-keygen -l</c> ile BİREBİR aynı yazım.
    /// </summary>
    /// <remarks>
    /// SSH.NET zaten dolgusuz base64 üretiyor (<c>HostKeyEventArgs.FingerPrintSHA256</c>); burada
    /// yalnız önek eklenir. Kendi hash'imizi hesaplasaydık yönetici, <c>ssh-keygen</c> çıktısıyla
    /// karşılaştıramadığı bir dizeyi onaylamak zorunda kalırdı — yani teyit imkânsız olurdu.
    /// </remarks>
    public static string FromSshHostKey(string? sha256Base64)
        => string.IsNullOrWhiteSpace(sha256Base64) ? string.Empty : Prefix + sha256Base64.Trim().TrimEnd('=');

    /// <summary>
    /// Karşılaştırma için normalleştirir: boşluk/önek/iki nokta farkları YOK SAYILIR.
    /// </summary>
    /// <remarks>
    /// Yönetici parmak izini tarayıcıdan (<c>1a:2b:…</c>), <c>openssl</c>'den (<c>1A2B…</c>) ya da
    /// <c>ssh-keygen</c>'den (<c>SHA256:base64</c>) kopyalar. Üçü de aynı şeyi söylüyorken birini
    /// "eşleşmedi" saymak, güvenliği artırmaz — yalnız yöneticiyi doğrulamayı büsbütün kapatmaya
    /// iter. Onaltılıkta büyük/küçük harf anlamsızdır; base64'te ANLAMLIDIR ve olduğu gibi kalır.
    /// </remarks>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var deger = new string(fingerprint.Where(karakter => !char.IsWhiteSpace(karakter)).ToArray());
        if (deger.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            deger = deger[Prefix.Length..];
        }

        if (deger.Length == 0)
        {
            return null;
        }

        var onaltilik = deger.Replace(":", string.Empty, StringComparison.Ordinal);
        if (onaltilik.Length == 64 && onaltilik.All(Uri.IsHexDigit))
        {
            return onaltilik.ToUpperInvariant();
        }

        return deger;
    }

    /// <summary>Sunulan parmak izi, yöneticinin sabitlediğiyle AYNI mı.</summary>
    public static bool Matches(string presented, string? pinned)
    {
        var sabit = Normalize(pinned);
        return sabit is not null && string.Equals(Normalize(presented), sabit, StringComparison.Ordinal);
    }
}

/// <summary>
/// Reddedilen uzak kimliğin GÖRÜLEBİLİR açıklaması: ne reddedildi, neden, parmak izi ne.
/// </summary>
/// <remarks>
/// <b>Neden yalnız "doğrulanamadı" demek yetmez:</b> yönetici o mesajla yapabileceği tek şeyin
/// doğrulamayı kapatmak olduğunu düşünür. Sebep ve parmak izi gösterildiğinde ise NE'yi kabul
/// ettiğini bilerek kabul eder — kabul ettiği şey artık "her sertifika" değil, "şu sertifika"dır.
/// </remarks>
public sealed class RemoteTrustFailure
{
    /// <summary>Arayüzün çevireceği hata anahtarı (<c>migration.tls.untrusted</c> / <c>migration.hostkey.untrusted</c>).</summary>
    public required string MessageKey { get; init; }

    /// <summary>Sunulan kimliğin parmak izi (<c>SHA256:…</c>). Yönetici bunu kabul eder.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Makine okunur ret sebepleri (<c>self_signed</c>, <c>name_mismatch</c>, <c>expired</c>…).</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Sertifikanın konusu (SSH'ta boş).</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Sertifikayı veren (SSH'ta host key algoritması + uzunluk).</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Geçerlilik aralığı (SSH'ta boş).</summary>
    public string Validity { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="Core.Common.AppException.Details"/> gövdesi. <b>İLK parça HER ZAMAN parmak izidir</b>
    /// — arayüzdeki "kabul et" düğmesi kabul edeceği değeri buradan okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden tek satır:</b> Details arayüzde düz metin olarak basılır; satır sonları kaybolur.
    /// Ayraç <c> · </c>, alan adları ASCII ve küçük harf.
    /// </para>
    /// <para>
    /// <b>Neden alan adları "sebep/konu/veren" değil de anahtarsız değil:</b> biçim
    /// <c>SecretRedactor</c>'dan geçer ve orada <c>anahtar=değer</c> kalıbı SIR sözcükleri için
    /// maskelenir. Buradaki adların hiçbiri o sözcük listesinde yoktur, yani parmak izi maskeye
    /// takılmaz; bu davranış testle sabitlenmiştir.
    /// </para>
    /// </remarks>
    public string ToDetails()
    {
        var parcalar = new List<string> { Fingerprint, "sebep=" + string.Join(",", Reasons) };

        if (!string.IsNullOrWhiteSpace(Subject))
        {
            parcalar.Add("konu=" + Subject);
        }

        if (!string.IsNullOrWhiteSpace(Issuer))
        {
            parcalar.Add("veren=" + Issuer);
        }

        if (!string.IsNullOrWhiteSpace(Validity))
        {
            parcalar.Add("gecerli=" + Validity);
        }

        return string.Join(" · ", parcalar);
    }
}

/// <summary>
/// TLS sertifika kararı: <b>varsayılan DOĞRULA</b>, istisna yalnız yöneticinin sabitlediği parmak izine.
/// </summary>
/// <remarks>
/// <para>
/// Bir örnek TEK bir göç/keşif işine aittir ve o iş bitince ölür. Kalıcı bir <c>known_hosts</c>
/// deposu BİLEREK yoktur: kalıcı kabul, "bir kez tıkla, sonsuza kadar unut" demektir ve tam da
/// kapatmaya çalıştığımız küresel anahtarın yavaş biçimidir.
/// </para>
/// </remarks>
public sealed class RemoteCertificateTrustGate(string? pinnedFingerprint)
{
    /// <summary>Reddedilen kimlik (yoksa <c>null</c>). İLK ret saklanır: sonrakiler teşhisi bulandırır.</summary>
    public RemoteTrustFailure? Failure { get; private set; }

    /// <summary>Sabitlenmiş parmak izi SAYESİNDE kabul edilen kimlik. Denetim kaydına bu yazılır.</summary>
    public string? AcceptedByPin { get; private set; }

    /// <summary>
    /// .NET'in TLS geri çağrısına takılacak biçim. Doğrudan <c>SslOptions</c>'a verilir.
    /// </summary>
    public RemoteCertificateValidationCallback AsCallback()
        => (_, certificate, chain, errors) => Evaluate(certificate, chain, errors);

    /// <summary>
    /// Kimlik kabul edilsin mi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sertifika ZATEN geçerliyse sabitleme aranmaz.</b> Aranaydı, kaynak sunucu sertifikasını
    /// meşru biçimde yenilediğinde göç kırılırdı — sabitlemenin görevi geçerli bir zinciri
    /// daraltmak değil, GEÇERSİZ bir zincire tek seferlik istisna açmaktır.
    /// </para>
    /// </remarks>
    public bool Evaluate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            Kaydet(new RemoteTrustFailure
            {
                MessageKey = "migration.tls.untrusted",
                Fingerprint = string.Empty,
                Reasons = ["no_certificate"]
            });
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var parmakIzi = TrustFingerprint.FromCertificate(certificate);
        if (TrustFingerprint.Matches(parmakIzi, pinnedFingerprint))
        {
            AcceptedByPin = parmakIzi;
            return true;
        }

        Kaydet(new RemoteTrustFailure
        {
            MessageKey = "migration.tls.untrusted",
            Fingerprint = parmakIzi,
            Reasons = Sebepler(certificate, chain, errors),
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            Validity = Gecerlilik(certificate)
        });

        return false;
    }

    private void Kaydet(RemoteTrustFailure failure) => Failure ??= failure;

    /// <summary>
    /// Ret sebebini SINIFLANDIRIR: "kendinden imzalı" ile "süresi geçmiş" yöneticinin vereceği
    /// kararı değiştirir; ikisine de "doğrulanamadı" demek o kararı imkânsız kılardı.
    /// </summary>
    private static IReadOnlyList<string> Sebepler(X509Certificate certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var sebepler = new List<string>();

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            sebepler.Add("no_certificate");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            sebepler.Add("name_mismatch");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            foreach (var durum in chain?.ChainStatus ?? [])
            {
                sebepler.AddRange(ZincirSebebi(durum.Status, certificate));
            }

            if (sebepler.Count == 0 || sebepler.All(sebep => sebep is "no_certificate" or "name_mismatch"))
            {
                // Zincir ayrıntısı okunamadıysa da bir şey söylenmeli: en yaygın hâli adlandır.
                sebepler.Add(KendindenImzali(certificate) ? "self_signed" : "untrusted_root");
            }
        }

        return sebepler.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> ZincirSebebi(X509ChainStatusFlags status, X509Certificate certificate)
    {
        if (status.HasFlag(X509ChainStatusFlags.NotTimeValid))
        {
            // "Süresi geçmiş" ile "henüz geçerli değil" farklı arızalardır: ilki unutulmuş bir
            // yenileme, ikincisi çoğu zaman sunucu saatinin yanlış olması demektir.
            yield return DateTime.TryParse(
                certificate.GetExpirationDateString(),
                CultureInfo.CurrentCulture,
                DateTimeStyles.None,
                out var bitis) && bitis < DateTime.Now
                ? "expired"
                : "not_yet_valid";
        }

        if (status.HasFlag(X509ChainStatusFlags.UntrustedRoot))
        {
            yield return KendindenImzali(certificate) ? "self_signed" : "untrusted_root";
        }

        if (status.HasFlag(X509ChainStatusFlags.PartialChain))
        {
            yield return "incomplete_chain";
        }

        if (status.HasFlag(X509ChainStatusFlags.Revoked))
        {
            yield return "revoked";
        }

        if (status.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) ||
            status.HasFlag(X509ChainStatusFlags.OfflineRevocation))
        {
            yield return "revocation_unknown";
        }
    }

    private static bool KendindenImzali(X509Certificate certificate)
        => string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal);

    private static string Gecerlilik(X509Certificate certificate)
        => $"{certificate.GetEffectiveDateString()}..{certificate.GetExpirationDateString()}";
}

/// <summary>
/// SSH host key kararı: <b>her zaman</b> yöneticinin onayını ister (SSH'ta sertifika otoritesi yoktur).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden burada "geçerli sertifika" dalı yok:</b> SSH'ta bir host key'i tek başına geçerli kılan
/// bir zincir yoktur — <c>ssh</c> da ilk bağlantıda sorar. Bu yüzden SFTP göçü artık bir kez
/// parmak izi onayı ister; o onay olmadan bağlantı KURULMAZ.
/// </para>
/// <para>
/// <b>ÖLÇÜLDÜ:</b> önceki hâlde <c>HostKeyReceived</c>'e hiç abone olunmuyordu ve SSH.NET'in
/// <c>HostKeyEventArgs.CanTrust</c> varsayılanı <c>true</c>. Yani panel yalnız "gevşek" değil,
/// TAMAMEN doğrulamasızdı.
/// </para>
/// </remarks>
public sealed class RemoteHostKeyTrustGate(string? pinnedFingerprint)
{
    /// <summary>Reddedilen host key (yoksa <c>null</c>).</summary>
    public RemoteTrustFailure? Failure { get; private set; }

    /// <summary>Sabitlenmiş parmak izi sayesinde kabul edilen host key.</summary>
    public string? AcceptedByPin { get; private set; }

    /// <param name="hostKeyName">Algoritma adı (ör. <c>ssh-ed25519</c>).</param>
    /// <param name="sha256FingerprintBase64">SSH.NET'in ürettiği dolgusuz base64 SHA-256.</param>
    /// <param name="keyLength">Anahtar uzunluğu (bit) — yöneticinin teyidinde ikinci işaret.</param>
    public bool Evaluate(string? hostKeyName, string? sha256FingerprintBase64, int keyLength)
    {
        var parmakIzi = TrustFingerprint.FromSshHostKey(sha256FingerprintBase64);
        if (parmakIzi.Length > TrustFingerprint.Prefix.Length && TrustFingerprint.Matches(parmakIzi, pinnedFingerprint))
        {
            AcceptedByPin = parmakIzi;
            return true;
        }

        Failure ??= new RemoteTrustFailure
        {
            MessageKey = "migration.hostkey.untrusted",
            Fingerprint = parmakIzi,
            Reasons = ["unknown_host_key"],
            Issuer = keyLength > 0 ? $"{hostKeyName} {keyLength} bit" : hostKeyName ?? string.Empty
        };

        return false;
    }
}
