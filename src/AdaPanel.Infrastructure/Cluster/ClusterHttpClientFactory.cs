using System.Collections.Concurrent;
using System.Net.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Uzak düğümle konuşacak <see cref="HttpClient"/>'ı üretir. Güvenlik kararlarının ÜÇÜNÜ de
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
/// </list>
/// <para>
/// <b>Neden işleyiciler önbellekte:</b> her istekte yeni <c>SocketsHttpHandler</c> üretmek bağlantı
/// havuzunu her seferinde çöpe atar ve düzenli yoklamada soket tükenmesine (TIME_WAIT birikmesi)
/// yol açar. Önbellek anahtarı adres + TLS kararıdır; ayar değişince yeni işleyici üretilir, yani
/// "doğrulamayı kapat" gibi bir değişiklik eski işleyiciyle sessizce yaşamaya devam edemez.
/// </para>
/// </remarks>
public sealed class ClusterHttpClientFactory(ILogger<ClusterHttpClientFactory> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> handlers = new(StringComparer.Ordinal);

    /// <summary>Test için değiştirilebilir çözümleyici; üretimde gerçek DNS.</summary>
    public ClusterHostResolver Resolver { get; init; } = ClusterAddressGuard.SystemResolver;

    /// <summary>
    /// Düğüm için istemci üretir. İstemci dispose edilebilir; ALTTAKİ işleyici paylaşılır ve
    /// kapanmaz (<c>disposeHandler: false</c>).
    /// </summary>
    public HttpClient Create(string address, bool verifyTlsCertificate, TimeSpan timeout)
    {
        var key = $"{address}|{(verifyTlsCertificate ? "tls" : "notls")}";
        var handler = handlers.GetOrAdd(key, _ => BuildHandler(address, verifyTlsCertificate));

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
    /// "TLS doğrulaması açık" birer GÜVENLİK KARARIDIR ve ölçülmeleri gerekir. Gizli kalsaydı test
    /// ancak kaynakta metin arayabilirdi — yani kararın yazıldığını doğrular, BAĞLANDIĞINI değil.
    /// </remarks>
    public SocketsHttpHandler BuildHandler(string address, bool verifyTlsCertificate)
    {
        var handler = new SocketsHttpHandler
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
                ClusterMessages.AddressForbidden)
        };

        if (!verifyTlsCertificate)
        {
            // BİLEREK AÇILAN DELİK. Varsayılan kapalıdır; buraya yalnız yönetici açıkça istediğinde
            // gelinir. Uyarı her işleyici kurulumunda bir kez günlüğe düşer ki "ne zamandır böyle"
            // sorusunun bir cevabı olsun.
            logger.LogWarning(
                "TLS sertifika doğrulaması KAPALI düğüm: {Address}. Araya giren biri yönetici belirtecini okuyabilir.",
                address);

            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
        }

        return handler;
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
