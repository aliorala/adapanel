using System.Net;
using AdaPanel.Infrastructure.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// TASIYICI KATMAN ILKELERI — yonlendirme, vekil, TLS ve baglanti-ani adres denetimi.
/// <para>
/// Bunlarin hepsi GUVENLIK KARARIDIR ve hicbiri "kodda yaziyor" ile dogrulanamaz; bu dosya
/// kararlarin gercekten BAGLI oldugunu olcer.
/// </para>
/// </summary>
public sealed class ClusterTransportPolicyTests
{
    private const string Adres = "https://dugum.ornek.test:8888";

    /// <summary>
    /// İstemci zaman aşımı BİLEREK çok geniş.
    ///
    /// Bu dosyadaki testlerin ölçtüğü şey zamanlama DEĞİL: "iç adrese bağlanmayı reddediyor mu",
    /// "boş çözümlemede bağlanıyor mu", "yönlendirmeyi izliyor mu". Bu kararların hepsi bağlantı
    /// KURULMADAN veriliyor, yani gerçek bir zaman aşımına asla ihtiyaç yok.
    ///
    /// Eskiden 3 saniyeydi ve TAM TAKIM YÜKÜ altında rastgele kırmızı veriyordu: iş parçacığı
    /// havuzu doluyken zaman aşımı, asıl kararı taşıyan istisnadan ÖNCE ateşleniyor ve test
    /// beklediği hata yerine iptal görüyordu. Rastgele kırmızı veren bir test, ilerideki gerçek
    /// bir arızanın "yine o test" diye elenmesine yol açar.
    ///
    /// NOT: aşağıdaki <c>ProbeTimeout</c> sınır testi (üretim değerinin makul aralıkta olduğunu
    /// doğrular) bundan AYRIDIR ve dokunulmadı — o gerçekten zamanlamayı ölçüyor.
    /// </summary>
    private static readonly TimeSpan IstemciZamanAsimi = TimeSpan.FromSeconds(60);

    /// <summary>
    /// YONLENDIRME IZLENMEZ.
    /// <para>
    /// Izlenseydi, dogrulanmis bir adres <c>302 Location: http://169.254.169.254/…</c> ile
    /// denetlenmemis bir adrese donusurdu — ustelik <c>Authorization</c> basligi de cogu istemcide
    /// birlikte tasinir, yani yonetici belirteci bulut meta veri servisine gonderilirdi. Adres
    /// denetimini yonlendirme zincirine yaymak yerine zinciri HIC izlememek hem basit hem olculebilir.
    /// </para>
    /// </summary>
    [Fact]
    public void Yonlendirmeler_KORU_KORUNE_takip_edilmez()
    {
        using var fabrika = Fabrika();
        using var isleyici = fabrika.BuildHandler(Adres, Parmak);

        Assert.False(isleyici.AllowAutoRedirect);
    }

    /// <summary>
    /// VEKIL KULLANILMAZ.
    /// <para>
    /// Sistem vekili devrede olsaydi baglanti VEKILE kurulur, hedef ana makine adi vekile METIN
    /// olarak gonderilirdi — yani asagidaki baglanti-ani adres denetimi HIC CALISMAZDI (soket her
    /// zaman vekile gider, 127.0.0.1'e degil). Vekilin arkasinda ne oldugunu panel olcemez.
    /// </para>
    /// </summary>
    [Fact]
    public void Vekil_KULLANILMAZ()
    {
        using var fabrika = Fabrika();
        using var isleyici = fabrika.BuildHandler(Adres, Parmak);

        Assert.False(isleyici.UseProxy);
    }

    /// <summary>
    /// SERTIFIKA HER ZAMAN SABITLEME GERI CAGRISINDAN GECER (G18): geri cagri hic kurulmasaydi
    /// .NET'in sistem zinciri devreye girer ve parmak izi hic sorulmazdi.
    /// </summary>
    [Fact]
    public void TLS_geri_cagrisi_HER_ZAMAN_kurulu()
    {
        using var fabrika = Fabrika();
        using var isleyici = fabrika.BuildHandler(Adres, Parmak);

        Assert.NotNull(isleyici.SslOptions.RemoteCertificateValidationCallback);
    }

    /// <summary>"Her sertifikayi kabul et" kapisi YOKTUR: parmak izi yoksa ya da uyusmuyorsa RED.</summary>
    [Fact]
    public void Parmak_izi_yoksa_ya_da_uyusmuyorsa_sertifika_REDDEDILIR()
    {
        using var sertifika = Sertifika();
        var dogru = AdaPanel.Infrastructure.Migration.TrustFingerprint.FromCertificate(sertifika);
        using var fabrika = Fabrika();

        bool Karar(string? parmak) => fabrika.BuildHandler(Adres, parmak).SslOptions
            .RemoteCertificateValidationCallback!(this, sertifika, null, System.Net.Security.SslPolicyErrors.None);

        Assert.True(Karar(dogru));
        Assert.False(Karar(null));
        Assert.False(Karar(string.Empty));
        Assert.False(Karar(Parmak));
    }

    /// <summary>
    /// Zincir GECERLI olsa bile (SslPolicyErrors.None) yanlis parmak izi reddedilir: sabitleme
    /// CA'nin ikinci bir sertifika basmasina karsidir.
    /// </summary>
    [Fact]
    public void Gecerli_zincir_yanlis_parmak_izini_KURTARMAZ()
    {
        using var sertifika = Sertifika();
        using var fabrika = Fabrika();

        var kabul = fabrika.BuildHandler(Adres, Parmak).SslOptions
            .RemoteCertificateValidationCallback!(this, sertifika, null, System.Net.Security.SslPolicyErrors.None);

        Assert.False(kabul);
    }

    /// <summary>Onay degisince AYRI isleyici uretilir: eski parmak iziyle yasayan isleyici kalmaz.</summary>
    [Fact]
    public void Parmak_izi_degisince_AYRI_isleyici_uretilir()
    {
        using var fabrika = Fabrika();

        var eski = fabrika.Create(Adres, Parmak, IstemciZamanAsimi);
        var yeni = fabrika.Create(Adres, Parmak.Replace("AA:BB", "BB:AA"), IstemciZamanAsimi);

        Assert.NotSame(eski, yeni);
        eski.Dispose();
        yeni.Dispose();
    }

    private const string Parmak = "SHA256:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 Sertifika()
    {
        using var anahtar = System.Security.Cryptography.RSA.Create(2048);
        var istek = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=dugum.ornek.test", anahtar, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void Baglanti_ani_adres_denetimi_KURULU()
    {
        using var fabrika = Fabrika();
        using var isleyici = fabrika.BuildHandler(Adres, Parmak);

        // Geri cagri yoksa ucuncu katman HIC yok demektir; DNS rebinding'i yakalayan tek yer burasi.
        Assert.NotNull(isleyici.ConnectCallback);
    }

    /// <summary>
    /// UCUNCU KATMAN — DAVRANIS OLCUMU. Ad kayit aninda genel bir adrese cozulmus olsa bile,
    /// istek atildigi an ic bir adrese cozuluyorsa BAGLANTI KURULMAZ (DNS rebinding).
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    public async Task Baglanti_aninda_ic_adrese_cozulen_ad_BAGLANMAZ(string ip)
    {
        using var fabrika = new ClusterHttpClientFactory(NullLogger<ClusterHttpClientFactory>.Instance)
        {
            // Kayit aninda temiz gorunen ad, istek aninda IC ADRESE cozuluyor.
            Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse(ip) })
        };

        using var istemci = fabrika.Create(Adres, Parmak, IstemciZamanAsimi);

        var hata = await Assert.ThrowsAsync<HttpRequestException>(
            () => istemci.GetAsync("api/v1/cluster/node-info"));

        // Sebep "baglanamadim" degil "adres yasak" olmali: ikisi farkli olaylardir ve tesihs
        // metni buna gore ayrilir.
        Assert.Equal(ClusterMessages.AddressForbidden, Kok(hata).Message);
    }

    /// <summary>
    /// Cozumleme BOS donerse de baglanilmaz. "Adres yok" ile "adres temiz" ayni sey degildir.
    /// </summary>
    [Fact]
    public async Task Cozumleme_bos_donerse_BAGLANMAZ()
    {
        using var fabrika = new ClusterHttpClientFactory(NullLogger<ClusterHttpClientFactory>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(Array.Empty<IPAddress>())
        };

        using var istemci = fabrika.Create(Adres, Parmak, IstemciZamanAsimi);

        var hata = await Assert.ThrowsAsync<HttpRequestException>(
            () => istemci.GetAsync("api/v1/cluster/node-info"));

        Assert.Equal(ClusterMessages.AddressForbidden, Kok(hata).Message);
    }

    /// <summary>
    /// KARISIK cozumlemede hicbirine baglanilmaz. "Temiz olana baglan" deseydik, saldirganin
    /// listeye bir genel adres eklemesi korumayi tamamen asmaya yeterdi.
    /// </summary>
    [Fact]
    public async Task Karisik_cozumlemede_HICBIRINE_baglanilmaz()
    {
        using var fabrika = new ClusterHttpClientFactory(NullLogger<ClusterHttpClientFactory>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("127.0.0.1")
            })
        };

        using var istemci = fabrika.Create(Adres, Parmak, IstemciZamanAsimi);

        var hata = await Assert.ThrowsAsync<HttpRequestException>(
            () => istemci.GetAsync("api/v1/cluster/node-info"));

        Assert.Equal(ClusterMessages.AddressForbidden, Kok(hata).Message);
    }

    /// <summary>
    /// Zaman asimi ACIKCA verilir. Varsayilan <c>HttpClient</c> 100 saniye bekler; merkez panoyu
    /// bir dugumun olumu yuzunden bir bucuk dakika kilitlemek, panelin kendisini cokmus gostermek
    /// olurdu.
    /// </summary>
    [Fact]
    public void Yoklama_zaman_asimi_kisa_ve_ACIK()
    {
        Assert.True(ClusterNodeClient.ProbeTimeout <= TimeSpan.FromSeconds(15));
        Assert.True(ClusterNodeClient.ProbeTimeout >= TimeSpan.FromSeconds(3));

        using var fabrika = Fabrika();
        using var istemci = fabrika.Create(Adres, Parmak, ClusterNodeClient.ProbeTimeout);

        Assert.Equal(ClusterNodeClient.ProbeTimeout, istemci.Timeout);
    }

    private static ClusterHttpClientFactory Fabrika() =>
        new(NullLogger<ClusterHttpClientFactory>.Instance)
        {
            Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })
        };

    /// <summary>En ictekki istisna: HttpClient sarmalayarak firlatir.</summary>
    private static Exception Kok(Exception hata)
    {
        var mevcut = hata;
        while (mevcut.InnerException is not null)
        {
            mevcut = mevcut.InnerException;
        }

        return mevcut;
    }
}
