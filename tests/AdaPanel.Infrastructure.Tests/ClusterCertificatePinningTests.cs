using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Cluster;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// G18 — küme düğümü sertifika SABİTLEME. Eski davranış: <c>verifyTls=false</c> her sertifikayı
/// sessizce kabul ediyordu. Yeni sözleşme: onaylı SHA-256 parmak izi yoksa ya da uyuşmuyorsa
/// bağlantı KAPALI başarısız olur; eski kayıtlar "yeniden onay gerek"e düşer.
/// </summary>
public sealed class ClusterCertificatePinningTests : IDisposable
{
    private readonly string veriKoku = Path.Combine(Path.GetTempPath(), "adapanel-cluster-pin-" + Guid.NewGuid().ToString("N"));

    // ------------------------------------------------------------------ taşıyıcı (gerçek TLS)

    [Fact]
    public async Task Dogru_parmak_izi_BAGLANIR_yanlis_parmak_izi_TLS_el_sikismasinda_REDDEDILIR()
    {
        using var sertifika = SertifikaUret();
        await using var sunucu = TlsSunucu.Baslat(sertifika);
        var dogru = TrustFingerprint.FromCertificate(sertifika);
        var yanlis = "SHA256:" + string.Join(":", Enumerable.Repeat("AB", 32));
        using var fabrika = new ClusterHttpClientFactory(NullLogger<ClusterHttpClientFactory>.Instance);

        // Üretim işleyicisinin yalnız BAĞLANTI AÇMA adımı yerel sunucuya çevrilir (SSRF kapısı
        // geri döngüyü bilerek reddeder); TLS doğrulama geri çağrısı ÜRETİM kodudur.
        using var iyi = YerelBaglanti(fabrika.BuildHandler(sunucu.Adres, dogru), sunucu.Port);
        using var istemci = new HttpClient(iyi, disposeHandler: false) { BaseAddress = new Uri(sunucu.Adres) };
        using var yanit = await istemci.GetAsync("x");
        Assert.Equal(HttpStatusCode.OK, yanit.StatusCode);

        using var kotu = YerelBaglanti(fabrika.BuildHandler(sunucu.Adres, yanlis), sunucu.Port);
        using var kotuIstemci = new HttpClient(kotu, disposeHandler: false) { BaseAddress = new Uri(sunucu.Adres) };
        var hata = await Assert.ThrowsAsync<HttpRequestException>(() => kotuIstemci.GetAsync("x"));
        Assert.IsType<AuthenticationException>(hata.InnerException);

        using var yok = YerelBaglanti(fabrika.BuildHandler(sunucu.Adres, null), sunucu.Port);
        using var yokIstemci = new HttpClient(yok, disposeHandler: false) { BaseAddress = new Uri(sunucu.Adres) };
        var yokHata = await Assert.ThrowsAsync<HttpRequestException>(() => yokIstemci.GetAsync("x"));
        Assert.IsType<AuthenticationException>(yokHata.InnerException);
    }

    // ------------------------------------------------------------------ istemci (belirteç sızmaz)

    [Fact]
    public async Task Onay_olmayan_https_dugume_BAGLANILMAZ_belirtec_yola_cikmaz()
    {
        using var fabrika = new ClusterHttpClientFactory(NullLogger<ClusterHttpClientFactory>.Instance);
        var istemci = new ClusterNodeClient(fabrika, NullLogger<ClusterNodeClient>.Instance);
        var eski = new ClusterNodeRecord { Label = "eski", Address = "https://eski.ornek.test", CertificateFingerprint = string.Empty };

        var sonuc = await istemci.FetchInfoAsync(eski, "adap_gizli", CancellationToken.None);

        Assert.False(sonuc.Succeeded);
        Assert.Equal(ClusterMessages.CertificateApprovalRequired, sonuc.ErrorKey);
        Assert.Equal(0, sonuc.DurationMs);
    }

    [Fact]
    public void Eski_kayit_parmak_izi_yokken_YENIDEN_ONAY_GEREK_durumunda()
    {
        // verifyTlsCertificate=false taşıyan eski dosya: alan okunmaz, parmak izi de yok.
        var json = "{\"Nodes\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Label\":\"eski\",\"Address\":\"https://eski.ornek.test\",\"VerifyTlsCertificate\":false}]}";
        var dosya = JsonSerializer.Deserialize<ClusterNodeStoreFile>(json)!;

        var dto = dosya.Nodes.Single().ToDto();

        Assert.Equal(ClusterCertificateStates.ReapprovalRequired, dto.CertificateState);
        Assert.Equal(string.Empty, dto.CertificateFingerprint);
        Assert.Equal(ClusterCertificateStates.NotApplicable, new ClusterNodeRecord { Address = "http://x.test" }.ToDto().CertificateState);
    }

    // ------------------------------------------------------------------ servis

    [Fact]
    public async Task Kayit_sirasinda_sertifika_degisirse_UYUSMAZLIK_reddedilir_ve_yazilmaz()
    {
        var (servis, depo, prober) = Duzenek();
        prober.Presented = "SHA256:" + string.Join(":", Enumerable.Repeat("11", 32)); // araya giren

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RegisterAsync(new RegisterClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://dugum.ornek.test",
            ApiToken = "adap_x12345678",
            CertificateFingerprint = FakeCertificateProber.Fingerprint
        }));

        Assert.Equal(ClusterMessages.CertificateMismatch, hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
        Assert.Empty(await depo.GetAllAsync());
    }

    [Fact]
    public async Task Okunamayan_sertifika_KAYDI_engeller()
    {
        var (servis, depo, prober) = Duzenek();
        prober.Failure = new HttpRequestException("boom");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RegisterAsync(new RegisterClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://dugum.ornek.test",
            ApiToken = "adap_x12345678",
            CertificateFingerprint = FakeCertificateProber.Fingerprint
        }));

        Assert.Equal(ClusterMessages.CertificateUnreadable, hata.MessageKey);
        Assert.Empty(await depo.GetAllAsync());
    }

    [Fact]
    public async Task Adres_degisince_eski_onay_YENI_adrese_TASINMAZ()
    {
        var (servis, depo, _) = Duzenek();
        var dugum = await servis.RegisterAsync(new RegisterClusterNodeRequest
        {
            Label = "Frankfurt",
            Address = "https://a.ornek.test",
            ApiToken = "adap_x12345678",
            CertificateFingerprint = FakeCertificateProber.Fingerprint
        });

        // Aynı adres, parmak izi yok: onay KORUNUR.
        await servis.UpdateAsync(dugum.Id, new UpdateClusterNodeRequest { Label = "Frankfurt-2", Address = "https://a.ornek.test" });
        Assert.Equal(FakeCertificateProber.Fingerprint, (await depo.GetAsync(dugum.Id))!.CertificateFingerprint);

        // Adres değişti, yeni onay yok: onay SİLİNİR -> yeniden onay gerek.
        var tasinan = await servis.UpdateAsync(dugum.Id, new UpdateClusterNodeRequest { Label = "Frankfurt-2", Address = "https://b.ornek.test" });
        Assert.Equal(ClusterCertificateStates.ReapprovalRequired, tasinan.CertificateState);
        Assert.Equal(string.Empty, (await depo.GetAsync(dugum.Id))!.CertificateFingerprint);
    }

    [Fact]
    public async Task Yeniden_onay_yalniz_sunulan_sertifikayla_ESLESIRSE_yazilir()
    {
        var (servis, depo, prober) = Duzenek();
        var kayit = await depo.AddAsync(new ClusterNodeRecord { Label = "eski", Address = "https://eski.ornek.test" }, "adap_x12345678");
        Assert.Equal(ClusterCertificateStates.ReapprovalRequired, kayit.ToDto().CertificateState);

        prober.Presented = "SHA256:" + string.Join(":", Enumerable.Repeat("22", 32));
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ApproveCertificateAsync(kayit.Id, FakeCertificateProber.Fingerprint));
        Assert.Equal(ClusterMessages.CertificateMismatch, hata.MessageKey);
        Assert.Equal(string.Empty, (await depo.GetAsync(kayit.Id))!.CertificateFingerprint);

        prober.Presented = FakeCertificateProber.Fingerprint;
        var onayli = await servis.ApproveCertificateAsync(kayit.Id, FakeCertificateProber.Fingerprint);
        Assert.Equal(ClusterCertificateStates.Pinned, onayli.CertificateState);
        Assert.NotNull(onayli.CertificateApprovedAtUtc);
        Assert.Equal(FakeCertificateProber.Fingerprint, (await depo.GetAsync(kayit.Id))!.CertificateFingerprint);
    }

    [Fact]
    public async Task Sertifika_yoklamasi_ic_adresi_REDDEDER()
    {
        var (servis, _, prober) = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ProbeCertificateAsync("https://169.254.169.254"));

        Assert.Equal(ClusterMessages.AddressForbidden, hata.MessageKey);
        Assert.Equal(0, prober.Calls);
    }

    // ------------------------------------------------------------------ yardımcılar

    private (ClusterService Servis, ClusterNodeStore Depo, FakeCertificateProber Prober) Duzenek()
    {
        var dosyaDeposu = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));
        var depo = new ClusterNodeStore(dosyaDeposu, new Koruyucu(), NullLogger<ClusterNodeStore>.Instance);
        var prober = new FakeCertificateProber();
        var servis = new ClusterService(depo, new AgYok(), prober, NullLogger<ClusterService>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })
        };

        return (servis, depo, prober);
    }

    private static SocketsHttpHandler YerelBaglanti(SocketsHttpHandler isleyici, int port)
    {
        isleyici.ConnectCallback = async (_, token) =>
        {
            var soket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await soket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token);
            return new NetworkStream(soket, ownsSocket: true);
        };
        return isleyici;
    }

    private static X509Certificate2 SertifikaUret()
    {
        using var anahtar = RSA.Create(2048);
        var istek = new CertificateRequest("CN=localhost", anahtar, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var gecici = istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // SChannel için özel anahtar geçici değil kalıcı depoya alınmış olmalı.
        return new X509Certificate2(gecici.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        if (Directory.Exists(veriKoku))
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    private sealed class Koruyucu : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes("k:" + plaintext));

        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue))[2..];
    }

    private sealed class AgYok : IClusterNodeClient
    {
        public Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(ClusterNodeRecord node, string token, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ağ yok");

        public Task<ClusterCallResult<JsonElement>> PostAsync(ClusterNodeRecord node, string token, string relativePath, object payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ağ yok");
    }

    /// <summary>Tek amaçlı yerel TLS sunucusu: her bağlantıya 200 döner.</summary>
    private sealed class TlsSunucu : IAsyncDisposable
    {
        private readonly TcpListener dinleyici;
        private readonly CancellationTokenSource iptal = new();
        private readonly Task dongu;

        public int Port { get; }

        public string Adres => $"https://localhost:{Port}";

        private TlsSunucu(TcpListener dinleyici, X509Certificate2 sertifika)
        {
            this.dinleyici = dinleyici;
            Port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
            dongu = Task.Run(() => Dongu(sertifika));
        }

        public static TlsSunucu Baslat(X509Certificate2 sertifika)
        {
            var dinleyici = new TcpListener(IPAddress.Loopback, 0);
            dinleyici.Start();
            return new TlsSunucu(dinleyici, sertifika);
        }

        private async Task Dongu(X509Certificate2 sertifika)
        {
            while (!iptal.IsCancellationRequested)
            {
                try
                {
                    var istemci = await dinleyici.AcceptTcpClientAsync(iptal.Token);
                    _ = Task.Run(async () =>
                    {
                        using (istemci)
                        {
                            try
                            {
                                await using var tls = new SslStream(istemci.GetStream());
                                await tls.AuthenticateAsServerAsync(sertifika);
                                var tampon = new byte[4096];
                                _ = await tls.ReadAsync(tampon);
                                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                                await tls.FlushAsync();
                            }
                            catch
                            {
                                // Reddeden istemci el sıkışmayı yarıda keser; sunucu için hata değil.
                            }
                        }
                    });
                }
                catch
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            iptal.Cancel();
            dinleyici.Stop();
            await dongu;
        }
    }
}
