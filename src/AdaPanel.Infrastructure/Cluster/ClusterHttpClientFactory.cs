using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>Adresin sunduğu sertifikayı belirteç göndermeden okur (parmak izi onayı için).</summary>
public interface IClusterCertificateProber
{
    Task<ClusterCertificateProbeDto> ProbeCertificateAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// Uzak düğümle konuşacak <see cref="HttpClient"/>'ı üretir. Güvenlik kararlarının DÖRDÜNÜ de
/// taşıyıcı katman burasıdır.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>
/// <b>Yönlendirme İZLENMEZ</b> (<c>AllowAutoRedirect = false</c>). İzlenseydi doğrulanmış bir adres,
/// <c>302 Location: http://169.254.169.254/…</c> ile denetlenmemiş bir adrese dönüşürdü — üstelik
/// <c>Authorization</c> başlığı da çoğu istemcide birlikte taşınır. Adres denetimini yönlendirme
/// zincirine yaymak yerine zinciri hiç izlememek hem basit hem ölçülebilir.
/// </item>
/// <item>
/// <b>Vekil KULLANILMAZ</b> (<c>UseProxy = false</c>). Sistem vekili devrede olsaydı bağlantı
/// vekile kurulur, hedef ana makine adı vekile METİN olarak gönderilirdi — yani aşağıdaki
/// bağlantı-anı adres denetimi HİÇ ÇALIŞMAZDI (soket her zaman vekile gider). Vekilin arkasında
/// ne olduğunu panel ölçemez.
/// </item>
/// <item>
/// <b>Bağlanılan IP yeniden denetlenir</b> (<c>ConnectCallback</c>). Kayıt anında yapılan DNS
/// denetimi ile isteğin atıldığı an arasında kayıt değişebilir (DNS rebinding). Soketin gerçekten
/// gittiği adresi yalnız burası görür.
/// </item>
/// <item>
/// <b>Sertifika SABİTLENİR, "doğrulamayı kapat" YOKTUR</b> (G18). https düğümde TLS geri çağrısı
/// yalnız yöneticinin onayladığı SHA-256 parmak iziyle eşleşen sertifikayı kabul eder; parmak izi
/// yoksa ya da uyuşmuyorsa el sıkışma REDDEDİLİR (kapalı başarısız) ve yönetici belirteci hiç
/// gönderilmez. Eski <c>verifyTls=false</c> "her sertifikayı kabul et" demekti; kaldırıldı.
/// </item>
/// </list>
/// <para>
/// <b>Neden işleyiciler önbellekte:</b> her istekte yeni <c>SocketsHttpHandler</c> üretmek bağlantı
/// havuzunu her seferinde çöpe atar ve düzenli yoklamada soket tükenmesine (TIME_WAIT birikmesi)
/// yol açar. Önbellek anahtarı adres + parmak izidir; onay değişince yeni işleyici üretilir, yani
/// eski parmak iziyle kurulmuş bir işleyici sessizce yaşamaya devam edemez.
/// </para>
/// </remarks>
public sealed class ClusterHttpClientFactory(ILogger<ClusterHttpClientFactory> logger) : IClusterCertificateProber, IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> handlers = new(StringComparer.Ordinal);

    /// <summary>Test için değiştirilebilir çözümleyici; üretimde gerçek DNS.</summary>
    public ClusterHostResolver Resolver { get; init; } = ClusterAddressGuard.SystemResolver;

    /// <summary>
    /// Düğüm için istemci üretir. İstemci dispose edilebilir; ALTTAKİ işleyici paylaşılır ve
    /// kapanmaz (<c>disposeHandler: false</c>).
    /// </summary>
    public HttpClient Create(string address, string? pinnedFingerprint, TimeSpan timeout)
    {
        var pin = TrustFingerprint.Normalize(pinnedFingerprint) ?? string.Empty;
        var key = $"{address}|{pin}";
        var handler = handlers.GetOrAdd(key, _ => BuildHandler(address, pinnedFingerprint));

        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(address.TrimEnd('/') + "/"),
            Timeout = timeout
        };
    }

    /// <summary>
    /// Taşıyıcı katmanın ilkelerini kuran tek yer.
    /// </summary>
    /// <remarks>
    /// <b><c>public</c> olması bilinçli:</b> "yönlendirme izlenmiyor", "vekil kullanılmıyor" ve
    /// "sertifika sabitli" birer GÜVENLİK KARARIDIR ve ölçülmeleri gerekir. Gizli kalsaydı test
    /// ancak kaynakta metin arayabilirdi — yani kararın yazıldığını doğrular, BAĞLANDIĞINI değil.
    /// </remarks>
    public SocketsHttpHandler BuildHandler(string address, string? pinnedFingerprint)
        => BuildHandlerCore(CreatePinValidator(pinnedFingerprint));

    private SocketsHttpHandler BuildHandlerCore(RemoteCertificateValidationCallback validator)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            // DNS değişiklikleri makul sürede yakalansın; sonsuz ömürlü bağlantı, taşınmış bir
            // düğüme saatlerce eski adresten bağlanmak demek olurdu.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // GÖVDE ARTIK BURADA DEĞİL. Aynı "çöz → denetle → yalnız denetlenen adrese bağlan"
            // kalıbı panelin kullanıcıdan adres alan bütün istemcilerinde gerekiyordu; ikinci bir
            // nüsha yazmak yerine küme de TEK KAYNAĞI (OutboundConnectGuard) çağırıyor.
            //
            // Kümeye ÖZGÜ kalan iki şey: (1) politika STRICT — çözülemeyen ad reddedilir, çünkü bu
            // istek bir YÖNETİCİ BELİRTECİ taşır; (2) istisna metni ClusterMessages.AddressForbidden
            // — merkez pano "bağlanamadım" ile "adres yasak"ı bu metinle ayırıyor.
            ConnectCallback = (context, cancellationToken) => OutboundConnectGuard.ConnectAsync(
                context.DnsEndPoint,
                OutboundTargetPolicy.Strict,
                (host, iptal) => Resolver(host, iptal),
                onBlocked: (host, blocked) => logger.LogWarning(
                    "Düğüm bağlantısı REDDEDİLDİ: {Host} adı {Address} adresine çözülüyor (iç ağ/geri döngü/meta veri).",
                    host,
                    blocked),
                cancellationToken,
                ClusterMessages.AddressForbidden),
            // Doğrulayıcı HER ZAMAN kurulur: geri çağrı hiç kurulmasaydı .NET'in sistem zinciri
            // devreye girer ve parmak izi hiç sorulmazdı. Parmak izi yoksa geri çağrı `false`
            // döner — kapalı başarısız. (http adreste TLS yoktur; geri çağrı hiç çalışmaz.)
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validator }
        };
    }

    /// <summary>
    /// Parmak izi doğrulayıcı. Zincir geçerli olsa bile parmak izi ARANIR: CA'nın başka bir
    /// sertifika basması (ya da ele geçirilmesi) sabitlemenin engellemeye çalıştığı şeydir.
    /// </summary>
    private RemoteCertificateValidationCallback CreatePinValidator(string? pinnedFingerprint)
    {
        var pin = TrustFingerprint.Normalize(pinnedFingerprint);
        return (_, certificate, _, _) =>
        {
            if (pin is null || certificate is null)
            {
                logger.LogWarning("Düğüm TLS bağlantısı REDDEDİLDİ: onaylı sertifika parmak izi yok.");
                return false;
            }

            var sunulan = TrustFingerprint.FromCertificate(certificate);
            if (TrustFingerprint.Matches(sunulan, pin))
            {
                return true;
            }

            logger.LogWarning("Düğüm TLS bağlantısı REDDEDİLDİ: sunulan sertifika {Sunulan}, onaylı değil.", sunulan);
            return false;
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Yalnız TLS el sıkışması + kimliksiz bir GET: yönetici belirteci GÖNDERİLMEZ. Sertifikayı
    /// kabul eden geri çağrı yalnız bu önbelleksiz, tek kullanımlık işleyicide yaşar; yoklamanın
    /// döndürdüğü şey yönetici onaylayana kadar hiçbir güven kararına dönüşmez. Bağlantı yine
    /// <c>OutboundConnectGuard</c>'dan geçer (iç ağ adresine sertifika yoklaması da yapılmaz).
    /// </remarks>
    public async Task<ClusterCertificateProbeDto> ProbeCertificateAsync(string address, CancellationToken cancellationToken)
    {
        if (!ClusterCertificateStates.IsHttps(address))
        {
            return new ClusterCertificateProbeDto { Address = address, Https = false };
        }

        X509Certificate2? yakalanan = null;
        var zincirGuvenilir = false;
        using var handler = BuildHandlerCore((_, certificate, _, errors) =>
        {
            if (certificate is not null)
            {
                yakalanan = new X509Certificate2(certificate);
                zincirGuvenilir = errors == SslPolicyErrors.None;
            }

            // Kabul: yalnız sertifikayı OKUMAK için. Gövdeye hiçbir şey gönderilmiyor.
            return certificate is not null;
        });
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(address.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        try
        {
            using var response = await client.GetAsync("", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (yakalanan is not null && exception is HttpRequestException or TaskCanceledException)
        {
            // Sertifika alındı; uygulama yanıtı önemsiz.
        }

        if (yakalanan is null)
        {
            throw new HttpRequestException(ClusterMessages.NodeUnreachable);
        }

        using (yakalanan)
        {
            return new ClusterCertificateProbeDto
            {
                Address = address,
                Https = true,
                Fingerprint = TrustFingerprint.FromCertificate(yakalanan),
                Subject = yakalanan.Subject,
                Issuer = yakalanan.Issuer,
                NotBeforeUtc = yakalanan.NotBefore.ToUniversalTime(),
                NotAfterUtc = yakalanan.NotAfter.ToUniversalTime(),
                ChainTrusted = zincirGuvenilir
            };
        }
    }

    public void Dispose()
    {
        foreach (var handler in handlers.Values)
        {
            handler.Dispose();
        }

        handlers.Clear();
    }
}
