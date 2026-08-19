using System.Net;
using AdaPanel.Infrastructure.Cluster;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÇOKLU SUNUCU SSRF KAPISI.
/// <para>
/// Yuzey su: dugum adresini KULLANICI yazar ve merkez panel o adrese, ustelik bir YONETICI
/// belirteci tasiyarak, sunucunun kendi agindan istek atar. Denetimsiz birakilsaydi panel,
/// disariya kapali her seye acilan bir vekile donerdi:
/// </para>
/// <list type="bullet">
/// <item><c>http://127.0.0.1:3306</c> — sunucunun kendi veritabani/yonetim arabirimleri.</item>
/// <item><c>http://10.0.0.5</c> — ic agdaki baska bir musteri ya da altyapi.</item>
/// <item><c>http://169.254.169.254</c> — BULUT META VERI servisi; sunucunun bulut kimlik
/// bilgileri burada durur ve SSRF'in en pahali hedefi budur.</item>
/// </list>
/// <para>
/// Bu dosya kapinin UC katmanini da kilitler: bicim, DNS cozumlemesi ve (ayri dosyada) baglanti
/// ani. Bir katmani kaldirmak digerlerinin yakalamadigi bir gedik acar; hangi mutasyonun kac testi
/// kirdigi rapora yazildi.
/// </para>
/// </summary>
public sealed class ClusterAddressGuardTests
{
    // ---------------------------------------------------------------------------------------
    // 1) BICIM KATMANI — dis dunyaya acik gercek adresler kabul edilir
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://dugum.ornek.test", "https://dugum.ornek.test")]
    [InlineData("https://dugum.ornek.test:8888", "https://dugum.ornek.test:8888")]
    [InlineData("http://dugum.ornek.test:8080", "http://dugum.ornek.test:8080")]
    [InlineData("https://dugum.ornek.test/", "https://dugum.ornek.test")]
    [InlineData("  https://DUGUM.ornek.test  ", "https://dugum.ornek.test")]
    [InlineData("http://93.184.216.34", "http://93.184.216.34")]
    public void Gercek_adresler_KABUL_edilir(string girdi, string beklenen)
    {
        Assert.True(ClusterAddressGuard.TryNormalize(girdi, out var normalize, out var kod), $"reddedildi: {kod}");
        Assert.Equal(beklenen, normalize);
    }

    /// <summary>
    /// Sema yazilmazsa HTTPS varsayilir. http varsaymak, yonetici belirtecini duz metin
    /// gonderilebilir hale getirirdi; kullanicinin unutmasi guvenli tarafa dusmeli.
    /// </summary>
    [Fact]
    public void Sema_yazilmazsa_HTTPS_varsayilir()
    {
        Assert.True(ClusterAddressGuard.TryNormalize("dugum.ornek.test:8888", out var normalize, out _));
        Assert.Equal("https://dugum.ornek.test:8888", normalize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://dugum.ornek.test")]
    [InlineData("file:///C:/windows/win.ini")]
    [InlineData("gopher://dugum.ornek.test")]
    public void Http_disi_semalar_REDDEDILIR(string girdi)
    {
        Assert.False(ClusterAddressGuard.TryNormalize(girdi, out _, out var kod));
        Assert.Equal(ClusterMessages.InvalidAddress, kod);
    }

    /// <summary>
    /// Adres bir KOKTUR. Yol/sorgu kabul edilseydi merkez, dugumun API'si yerine dugumdeki
    /// rastgele bir yola cagri yapmaya ikna edilebilirdi.
    /// </summary>
    [Theory]
    [InlineData("https://dugum.ornek.test/api/v1/sites")]
    [InlineData("https://dugum.ornek.test/?x=1")]
    [InlineData("https://dugum.ornek.test/#parca")]
    public void Yol_sorgu_parca_iceren_adres_REDDEDILIR(string girdi)
    {
        Assert.False(ClusterAddressGuard.TryNormalize(girdi, out _, out var kod));
        Assert.Equal(ClusterMessages.InvalidAddress, kod);
    }

    /// <summary>
    /// "https://gercek-sunucu.test@saldirgan.example" adresinde ana makine SALDIRGAN'dir: insan
    /// gozu ilk parcayi okur, ayristirici ikincisini. Bu karisikligi cozmenin tek durust yolu
    /// bicimi hic kabul etmemek.
    /// </summary>
    [Theory]
    [InlineData("https://dugum.ornek.test@saldirgan.example")]
    [InlineData("https://kullanici:parola@dugum.ornek.test")]
    [InlineData("https://127.0.0.1@dugum.ornek.test")]
    public void Kullanici_bilgisi_iceren_adres_REDDEDILIR(string girdi)
    {
        Assert.False(ClusterAddressGuard.TryNormalize(girdi, out _, out var kod));
        Assert.Equal(ClusterMessages.InvalidAddress, kod);
    }

    // ---------------------------------------------------------------------------------------
    // 2) "BURANIN ICI" ADLARI — cozulmeden reddedilir
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://LOCALHOST:8888")]
    [InlineData("https://panel.localhost")]
    [InlineData("https://sunucu.local")]
    [InlineData("https://metadata.google.internal")]
    [InlineData("https://her-hangi-bir-sey.internal")]
    [InlineData("https://yonlendirici.home.arpa")]
    public void Ic_ag_adlari_REDDEDILIR(string girdi)
    {
        Assert.False(ClusterAddressGuard.TryNormalize(girdi, out _, out var kod));
        Assert.Equal(ClusterMessages.AddressForbidden, kod);
    }

    // ---------------------------------------------------------------------------------------
    // 3) DUZ IP — engelli araliklar
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Her satirin yanindaki aralik, o satiri yanlislikla silmenin ne kadarini panele acacagini
    /// soyler.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]          // 127.0.0.0/8 geri dongu
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.5")]           // 10.0.0.0/8 ozel
    [InlineData("172.16.0.1")]         // 172.16.0.0/12 ozel
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]        // 192.168.0.0/16 ozel
    [InlineData("169.254.169.254")]    // BULUT META VERI (AWS/Azure/GCP/DigitalOcean)
    [InlineData("169.254.0.1")]        // link-local
    [InlineData("100.100.100.200")]    // Alibaba meta verisi (100.64.0.0/10 CGNAT)
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]            // "bu ag"
    [InlineData("192.0.0.1")]          // IETF protokol atamalari
    [InlineData("198.18.0.1")]         // kiyaslama
    [InlineData("224.0.0.1")]          // coklu gonderim
    [InlineData("255.255.255.255")]    // yayin
    public void Engelli_IPv4_adresleri_REDDEDILIR(string ip)
    {
        Assert.True(ClusterAddressGuard.IsBlockedAddress(IPAddress.Parse(ip)), $"{ip} gecti — ic aga kapi acildi.");
        Assert.False(ClusterAddressGuard.TryNormalize($"https://{ip}", out _, out var kod));
        Assert.Equal(ClusterMessages.AddressForbidden, kod);
    }

    /// <summary>
    /// GERILEME KORUMASI: korumayi genisletirken gercek genel adresleri kapatmak, ozelligi
    /// kullanilamaz hale getirir. Sinir degerleri (172.15/172.32, 100.63/100.128) bilerek burada.
    /// </summary>
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("185.7.242.225")]
    public void Genel_IPv4_adresleri_KABUL_edilir(string ip)
    {
        Assert.False(ClusterAddressGuard.IsBlockedAddress(IPAddress.Parse(ip)), $"{ip} engellendi — mesru dugum reddedilir.");
        Assert.True(ClusterAddressGuard.TryNormalize($"https://{ip}", out _, out var kod), $"reddedildi: {kod}");
    }

    /// <summary>
    /// IPv6 KACISLARI. "[::ffff:127.0.0.1]" bir IPv6 adresidir ama gittigi yer 127.0.0.1'dir;
    /// eslemeyi acmadan bakan bir kontrol bunu "bilinmeyen IPv6" sayip GECIRIRDI. Ayni sey
    /// IPv4-uyumlu (::7f00:1) ve 6to4 (2002:7f00:1::) bicimleri icin de gecerli.
    /// </summary>
    [Theory]
    [InlineData("::1")]                       // geri dongu
    [InlineData("::")]                        // belirsiz
    [InlineData("fe80::1")]                   // link-local
    [InlineData("fec0::1")]                   // site-local
    [InlineData("fc00::1")]                   // benzersiz yerel
    [InlineData("fd00:ec2::254")]             // AWS IPv6 META VERISI
    [InlineData("ff02::1")]                   // coklu gonderim
    [InlineData("::ffff:127.0.0.1")]          // IPv4-eslemeli geri dongu
    [InlineData("::ffff:169.254.169.254")]    // IPv4-eslemeli META VERI
    [InlineData("::ffff:10.0.0.5")]           // IPv4-eslemeli ic ag
    [InlineData("::7f00:1")]                  // IPv4-uyumlu 127.0.0.1
    [InlineData("2002:7f00:1::")]             // 6to4 127.0.0.1
    [InlineData("2002:a9fe:a9fe::")]          // 6to4 169.254.169.254
    public void Engelli_IPv6_adresleri_REDDEDILIR(string ip)
    {
        Assert.True(ClusterAddressGuard.IsBlockedAddress(IPAddress.Parse(ip)), $"{ip} gecti — IPv6 uzerinden ic aga kapi acildi.");
        Assert.False(ClusterAddressGuard.TryNormalize($"https://[{ip}]", out _, out var kod));
        Assert.Equal(ClusterMessages.AddressForbidden, kod);
    }

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void Genel_IPv6_adresleri_KABUL_edilir(string ip)
    {
        Assert.False(ClusterAddressGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Adres_yoksa_ENGELLIDIR()
    {
        // "Olcemedik" ile "sorun yok" ayni sey degil: null bir adres icin varsayilan GECIS olsaydi,
        // cozumleme hatasi sessizce izin haline gelirdi.
        Assert.True(ClusterAddressGuard.IsBlockedAddress(null));
    }

    // ---------------------------------------------------------------------------------------
    // 4) DNS KATMANI — "ic-sunucu.ornek.com -> 10.0.0.5" yalniz burada yakalanir
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Ic_agi_isaret_eden_alan_adi_REDDEDILIR()
    {
        // Bicim katmani bu adresi temiz gorur: ad da gecerli, sema da. Tek yakalayan katman DNS.
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "ic-sunucu.ornek.com",
            Cozumleyici("10.0.0.5"),
            CancellationToken.None);

        Assert.Equal("10.0.0.5", engellenen);
    }

    [Fact]
    public async Task Bulut_meta_verisine_cozulen_alan_adi_REDDEDILIR()
    {
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "yardimsever.ornek.com",
            Cozumleyici("169.254.169.254"),
            CancellationToken.None);

        Assert.Equal("169.254.169.254", engellenen);
    }

    /// <summary>
    /// TEK engelli adres yeterlidir. "Cogunlugu temiz" diye gecmek, korumayi yazi-tura oyununa
    /// cevirirdi: saldirgan listeye bir genel adres eklemekle kapiyi acardi.
    /// </summary>
    [Fact]
    public async Task Karisik_cozumlemede_TEK_ic_adres_yeter()
    {
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "ikili.ornek.com",
            Cozumleyici("93.184.216.34", "127.0.0.1"),
            CancellationToken.None);

        Assert.Equal("127.0.0.1", engellenen);
    }

    [Fact]
    public async Task Tamamen_genel_cozumleme_KABUL_edilir()
    {
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "dugum.ornek.test",
            Cozumleyici("93.184.216.34", "8.8.8.8"),
            CancellationToken.None);

        Assert.Null(engellenen);
    }

    /// <summary>
    /// DNS COZULEMEZSE REDDEDILIR — panelin baska yerindeki (musteri cron adresi) kuraldan bilerek
    /// FARKLI. Orada istek anonimdir ve gecici bir DNS hatasi mesru bir siteyi engellerdi; burada
    /// istek bir YONETICI BELIRTECI tasir. Nereye gittigini olcemedigimiz bir adrese sir gondermek,
    /// olcebildigimiz bir riski almaktan kotudur.
    /// </summary>
    [Fact]
    public async Task DNS_cozulemezse_REDDEDILIR()
    {
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "cozulmeyen.ornek.test",
            (_, _) => throw new System.Net.Sockets.SocketException(11001),
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    [Fact]
    public async Task Bos_cozumleme_de_REDDEDILIR()
    {
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "bos.ornek.test",
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    /// <summary>
    /// Duz IP yazildiysa DNS'e HIC cikilmaz: cozumleyici cagrilirsa test patlar. Gereksiz DNS
    /// cagrisi, engellenen bir adres icin bile sunucunun disariya sinyal vermesi demektir.
    /// </summary>
    [Fact]
    public async Task Duz_IP_icin_DNS_cagrilmaz()
    {
        var cagrildi = false;
        var engellenen = await ClusterAddressGuard.FindBlockedAddressAsync(
            "10.0.0.5",
            (_, _) =>
            {
                cagrildi = true;
                return Task.FromResult(Array.Empty<IPAddress>());
            },
            CancellationToken.None);

        Assert.Equal("10.0.0.5", engellenen);
        Assert.False(cagrildi);
    }

    [Fact]
    public async Task Iptal_edilen_cozumleme_YUKARI_birakilir()
    {
        // Panel kapaniyorsa bu bir "engellendi" degildir; hata yutulursa kapanis sirasinda sessiz
        // ve yaniltici bir "adres yasak" kaydi olusurdu.
        using var kaynak = new CancellationTokenSource();
        await kaynak.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ClusterAddressGuard.FindBlockedAddressAsync(
                "dugum.ornek.test",
                (_, iptal) => Task.FromException<IPAddress[]>(new OperationCanceledException(iptal)),
                kaynak.Token));
    }

    private static ClusterHostResolver Cozumleyici(params string[] adresler) =>
        (_, _) => Task.FromResult(adresler.Select(IPAddress.Parse).ToArray());
}
