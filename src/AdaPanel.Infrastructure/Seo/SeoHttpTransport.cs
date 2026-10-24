using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Tarayıcının TAŞIMA KATMANI: her soketi loopback'e sabitler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asıl fikir:</b> istek adresi sitenin gerçek alan adını taşır (<c>Host</c> başlığı ve TLS SNI
/// doğru olsun diye), ama TCP bağlantısı ad çözümlemesi YAPILMADAN 127.0.0.1'e kurulur. Böylece:
/// </para>
/// <list type="number">
/// <item>Panel, taradığı siteyi dış ağa çıkmadan, kendi IIS'inden okur (DNS henüz yayılmamış bir
/// alan adı bile taranabilir — <c>SiteHealthService</c> ile aynı kanıtlanmış yöntem).</item>
/// <item><see cref="SeoLinkPolicy"/> içinde bir hata olsa BİLE istek makineden çıkamaz; SSRF'in
/// asıl bedeli (bulut meta verisi, iç ağ, başka sunucular) yapısal olarak erişilemez kalır.</item>
/// </list>
/// <para>
/// <b>Neden sertifika doğrulaması kapalı:</b> bağlantı loopback'tedir; arada dinleyebilecek bir ağ
/// yoktur, yani doğrulamanın koruduğu tehdit burada mevcut değildir. Buna karşılık panelde kendi
/// imzalı sertifika çok yaygındır ve doğrulama açık kalsaydı tarama, sitenin SEO'suyla hiç
/// ilgisi olmayan bir sebeple hep başarısız olurdu. Bu içerik taraması sertifikanın güvenilir
/// olduğunu kanıtlamaz; <c>SiteHealthService</c> ayrıca gerçek TLS doğrulaması yapar.
/// </para>
/// </remarks>
public static class SeoHttpTransport
{
    /// <summary>
    /// Tarama için istemci üretir. Yönlendirme İZLENMEZ: her atlama <see cref="SeoLinkPolicy"/>
    /// kapısından yeniden geçmelidir. Otomatik izleme, <c>Location: http://169.254.169.254/</c>
    /// diyen bir sayfanın kapıyı atlamasına izin verirdi.
    /// </summary>
    public static HttpClient CreateClient(SeoCrawlLimits limits)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            // Tarama kısa ömürlüdür; bağlantı havuzunu uzun tutmanın faydası yok.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port),
                    context.DnsEndPoint,
                    cancellationToken)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            // İstek başına süre ayrıca bağlı iptal belirteciyle uygulanır; buradaki değer son emniyet.
            Timeout = limits.RequestTimeout + TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// SABİTLENMİŞ bağlantı: <paramref name="requested"/> yalnızca hangi adın istendiğini söyler,
    /// bağlanılacak adresi BELİRLEMEZ.
    /// </summary>
    /// <remarks>
    /// Ayrı ve public bir metot olmasının sebebi test edilebilirlik: <c>SocketsHttpConnectionContext</c>
    /// dışarıdan üretilemediği için geri çağırma gövdesi buraya alındı. <c>SeoSsrfGuardTests</c> bu
    /// metodu gerçek bir dinleyiciyle çağırır ve istenen adın YOK SAYILDIĞINI kanıtlar.
    /// </remarks>
    public static async ValueTask<Stream> ConnectAsync(
        IPEndPoint pinned,
        DnsEndPoint requested,
        CancellationToken cancellationToken)
    {
        _ = requested; // BİLEREK kullanılmıyor: istenen konak adı bağlantı hedefini etkilemez.

        var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(pinned, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
