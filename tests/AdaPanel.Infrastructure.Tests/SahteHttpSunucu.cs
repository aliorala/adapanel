using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// LOOPBACK'te çalışan en küçük HTTP sunucusu.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpListener</c> yerine <see cref="TcpListener"/>: <c>HttpListener</c> Windows'ta URL
/// ayırması (urlacl) isteyebilir ve testin yükseltilmiş yetkiyle koşup koşmadığına göre davranışı
/// DEĞİŞİR — kararsız bir test, ilerideki gerçek arızanın "yine o test" diye elenmesine yol açar.
/// Buradaki sunucu yalnız istek satırını okur ve sabit bir yanıt yazar.
/// </para>
/// <para>
/// <b>Neden kendi dosyasında:</b> aynı sunucu artık ÜÇ ölçüm ailesinde kullanılıyor (giden
/// yönlendirme kapısı, git dağıtımı, arşiv içe aktarma). İkinci bir nüsha üretmek bu deponun
/// defalarca ölçülmüş hata sınıfıdır: biri düzeltilir, öteki sessizce farklı davranır.
/// </para>
/// </remarks>
internal sealed class SahteHttpSunucu : IDisposable
{
    private readonly TcpListener dinleyici;
    private readonly Func<string, (int Kod, string? Konum, string? IcerikTipi, byte[] Govde)> yanitlayici;
    private readonly X509Certificate2? sertifika;
    private readonly CancellationTokenSource iptal = new();
    private int istekSayisi;

    private SahteHttpSunucu(
        TcpListener dinleyici,
        Func<string, (int, string?, string?, byte[])> yanitlayici,
        X509Certificate2? sertifika)
    {
        this.dinleyici = dinleyici;
        this.yanitlayici = yanitlayici;
        this.sertifika = sertifika;
        Port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
        _ = Task.Run(DinleAsync);
    }

    public int Port { get; }

    public int IstekSayisi => Volatile.Read(ref istekSayisi);

    /// <summary>Metin gövdeli yanıt. Sertifika verilirse sunucu TLS konuşur.</summary>
    public static SahteHttpSunucu Baslat(
        Func<string, (int Kod, string? Konum, string Govde)> yanitlayici,
        X509Certificate2? sertifika = null)
        => BaslatHam(
            yol =>
            {
                var (kod, konum, govde) = yanitlayici(yol);
                return (kod, konum, null, Encoding.UTF8.GetBytes(govde));
            },
            sertifika);

    /// <summary>
    /// HAM gövdeli yanıt: içerik tipi ve baytlar çağırana ait.
    /// </summary>
    /// <remarks>
    /// Gerekli çünkü iki ölçüm metin gövdeyle YAPILAMAZ: git'in akıllı HTTP keşfi
    /// <c>application/x-git-upload-pack-advertisement</c> içerik tipi olmadan "aptal protokol"
    /// sanılır, arşiv içe aktarma ise gerçek bir <c>.zip</c>'in baytlarını ister.
    /// </remarks>
    public static SahteHttpSunucu BaslatHam(
        Func<string, (int Kod, string? Konum, string? IcerikTipi, byte[] Govde)> yanitlayici,
        X509Certificate2? sertifika = null)
    {
        var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        return new SahteHttpSunucu(dinleyici, yanitlayici, sertifika);
    }

    private async Task DinleAsync()
    {
        try
        {
            while (!iptal.IsCancellationRequested)
            {
                using var istemci = await dinleyici.AcceptTcpClientAsync(iptal.Token);
                await using Stream akis = sertifika is null
                    ? istemci.GetStream()
                    : await TlsAkisiAsync(istemci, sertifika, iptal.Token);

                var tampon = new byte[8192];
                var okunan = await akis.ReadAsync(tampon, iptal.Token);
                if (okunan <= 0)
                {
                    continue;
                }

                Interlocked.Increment(ref istekSayisi);

                var istek = Encoding.ASCII.GetString(tampon, 0, okunan);
                var yol = istek.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                var (kod, konum, icerikTipi, govdeBaytlari) = yanitlayici(yol);

                var baslik = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(kod).Append(" X\r\n")
                    .Append("Content-Length: ").Append(govdeBaytlari.Length).Append("\r\n")
                    .Append("Connection: close\r\n");

                if (icerikTipi is not null)
                {
                    baslik.Append("Content-Type: ").Append(icerikTipi).Append("\r\n");
                }

                if (konum is not null)
                {
                    baslik.Append("Location: ").Append(konum).Append("\r\n");
                }

                baslik.Append("\r\n");

                var baslikBaytlari = Encoding.ASCII.GetBytes(baslik.ToString());
                await akis.WriteAsync(baslikBaytlari, iptal.Token);
                await akis.WriteAsync(govdeBaytlari, iptal.Token);
                await akis.FlushAsync(iptal.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış: beklenen.
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException or AuthenticationException)
        {
            // Dinleyici kapandı ya da karşı taraf el sıkışmayı yarıda bıraktı; test bitti.
        }
    }

    private static async Task<Stream> TlsAkisiAsync(
        TcpClient istemci,
        X509Certificate2 sertifika,
        CancellationToken iptalBelirteci)
    {
        var tls = new SslStream(istemci.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = sertifika },
            iptalBelirteci);
        return tls;
    }

    public void Dispose()
    {
        iptal.Cancel();
        dinleyici.Stop();
        iptal.Dispose();
    }
}
